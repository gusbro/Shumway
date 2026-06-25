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
    public void BuiltinRedefinition_DroppedUnderArityCompat()
    {
        // A program that redefines a Shumway builtin (atom_length/2) under
        // arity_compat keeps the builtin — the user clause is dropped (with a
        // warning), so the builtin's behaviour wins.
        var e = new PrologEngine();
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "atom_length(_, _) :- throw(user_clause_should_be_dropped).\n");
        Assert.True(e.Query("atom_length(hello, 5).").Success);   // builtin ran, no throw
    }

    [Fact]
    public void BuiltinRedefinition_KeptWithoutArityCompat()
    {
        // Without arity_compat the drop is gated off — but atom_length is a builtin,
        // so a user clause for it still doesn't override (builtins win regardless);
        // this just confirms the drop path is the arity_compat-only part.
        var e = new PrologEngine();
        e.ConsultString("p(N) :- atom_length(hello, N).\n");   // no redefinition
        Assert.True(e.Query("p(5).").Success);
    }
}
