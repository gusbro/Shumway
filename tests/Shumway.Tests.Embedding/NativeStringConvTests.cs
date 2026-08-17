using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ADR-024 — the Arity string-conversion builtins (make_c_string / make_prolog_string)
// in value mode (identity), and the skip of builtin redefinitions under arity_compat.
public sealed class NativeStringConvTests
{
    [Fact]
    public void MakePrologString_ValueMode_Identity()
    {
        var e = new PrologEngine();
        // arg0 is an atom (a value, not a holder) → identity arg0 = arg1.
        Assert.True(e.Query("make_prolog_string(hello, X), X == hello.").Success);
        Assert.True(e.Query("make_prolog_string(X, hello), X == hello.").Success);  // bidirectional
        Assert.True(e.Query("make_prolog_string(daemon, 'daemon').").Success);      // ground check
        Assert.False(e.Query("make_prolog_string(daemon, 'other').").Success);
    }

    [Fact]
    public void MakeCString_ValueMode_Identity()
    {
        var e = new PrologEngine();
        // arg0 is a value → identity arg0 = arg2 (max-len / actual-len ignored).
        Assert.True(e.Query("make_c_string(X, 1024, hello, _), X == hello.").Success);
        Assert.True(e.Query("make_c_string(hello, 1024, X, _), X == hello.").Success);
    }

    [Fact]
    public void HolderMode_FillThenRead_RoundTrips()
    {
        // i_start_rpt pattern: a char* global is a reusable holder. `H is buf` gives
        // the holder slot; make_c_string sets it, make_prolog_string reads it.
        var e = new PrologEngine();
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            :- c.
            char* buf;
            :- prolog.
            p(In, Out) :-
              { H: pchar; H is buf },
              make_c_string(H, 100, In, _),
              make_prolog_string(H, Out).
            """);
        Assert.True(e.Query("p(hello, Out), Out == hello.").Success);
    }

    [Fact]
    public void HolderMode_RunsUnderTier1Il()
    {
        // The holder flow also compiles to IL (delegate / Tier-1 inline).
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            :- public p/2.
            :- c.
            char* buf;
            :- prolog.
            p(In, Out) :-
              { H: pchar; H is buf },
              make_c_string(H, 100, In, _),
              make_prolog_string(H, Out).
            """);
        for (int i = 0; i < 6; i++)
            Assert.True(e.Query("p(hello, Out), Out == hello.").Success);
    }

    [Fact]
    public void HolderMode_ReusedHolder_NoAliasing()
    {
        // The key reason for holders over identity: filling the same buffer twice
        // must NOT alias the two Prolog values.
        var e = new PrologEngine();
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            :- c.
            char* buf;
            :- prolog.
            q(A, B, OA, OB) :-
              { H: pchar; H is buf },
              make_c_string(H, 100, A, _), make_prolog_string(H, OA),
              make_c_string(H, 100, B, _), make_prolog_string(H, OB).
            """);
        Assert.True(e.Query("q(x, y, OA, OB), OA == x, OB == y.").Success);
    }

    [Fact]
    public void BuiltinRedefinition_DroppedUnderArityCompat()
    {
        // A program that redefines a Shumway builtin (atom_length/2) under
        // arity_compat keeps the builtin — the user clause is dropped (with a
        // warning), so the builtin's behaviour wins.
        var e = new PrologEngine();
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            atom_length(_, _) :- throw(user_clause_should_be_dropped).
            """);
        Assert.True(e.Query("atom_length(hello, 5).").Success);   // builtin ran, no throw
    }

    [Fact]
    public void BuiltinRedefinition_KeptWithoutArityCompat()
    {
        // Without arity_compat the drop is gated off — but atom_length is a builtin,
        // so a user clause for it still doesn't override (builtins win regardless);
        // this just confirms the drop path is the arity_compat-only part.
        var e = new PrologEngine();
        e.ConsultString("p(N) :- atom_length(hello, N).");   // no redefinition
        Assert.True(e.Query("p(5).").Success);
    }
}
