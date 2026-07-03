using System;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ADR-022 follow-ups from real Arity sources (WINDOWS.pl's string_buf_long/1):
// (1) the MakeCString length argument types its Prolog variable as an integer, so
// a variable that is also used elsewhere in the block no longer fails inference;
// (2) a faulty native block's error names the predicate and the line.
public sealed class NativeErrorAndLengthTests
{
    private static class LenInterop
    {
        public static int strlen(string s) => s.Length;
        public static int getcode(int x) => x * 10;
    }

    [Fact]
    public void VariableDeclaredInOneBlock_UsedInAnother_KeepsType()
    {
        // i_pfglrp.pl's i_start_rpt pattern: a variable declared `Par1: pchar` in
        // the first block is used in a later block (passed to a C function). The
        // declaration must carry across blocks of the same clause.
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(LenInterop));
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\ntypedef char *pchar;\nint strlen(const char*);\n:- prolog.\n" +
            "seen(_).\n" +
            "p(S, R) :- atom(S),\n" +
            "  { P: pchar; P is S },\n" +        // P declared here
            "  seen(P),\n" +                     // P in a Prolog goal (a clause var, no type guard)
            "  { R is 'strlen'(P) },\n" +        // P used here with no local decl → cross-block type
            "  integer(R).\n");
        Assert.Equal(5L, e.Query("p(hello, R).").Get<long>("R"));
    }

    [Fact]
    public void VariableAssignedFromTypedVariable_InheritsType()
    {
        // i_mdlinf.pl's i_product_revision pattern: Ret is typed from a prototype's
        // return type ('getcode' → int), then `RetCode is Ret` must inherit that
        // type — a variable assigned from another, already-typed, Prolog variable.
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(LenInterop));
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\nint getcode(int);\n:- prolog.\n" +
            "p(In, RetCode) :- integer(In), " +
            "{ Ret is 'getcode'(In); RetCode is Ret }, integer(RetCode).\n");
        Assert.Equal(50L, e.Query("p(5, R).").Get<long>("R"));
    }

    [Fact]
    public void MakePrologStringGuard_TypesItsArgsAsString()
    {
        // daemon.pl's prolog_call_fact pattern: Fact's type is known because a
        // clause goal make_prolog_string(Fact, _) implies it is a string — even
        // without an explicit atom/1 guard on Fact.
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(LenInterop));
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\nint strlen(const char*);\n:- prolog.\n" +
            "make_prolog_string(A, A) :- atom(A), !.\n" +
            "g(Fact, L) :- make_prolog_string(Fact, _), { L is 'strlen'(Fact) }, integer(L).\n");
        Assert.Equal(5L, e.Query("g(hello, L).").Get<long>("L"));
    }

    [Fact]
    public void MakeCStringLengthArg_TypesItsVariableAsInteger()
    {
        // string_buf_long pattern: Len is MakeCString's length arg AND used in
        // arithmetic. Its type is known (integer); inference must not fail.
        var e = new PrologEngine();
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\nchar buf[10];\n:- prolog.\n" +
            "sbl(S, In, L) :- atom(S), integer(In), Len = In, " +
            "{ 'MakeCString'(buf, Len, &S); L is Len + 1 }, integer(L).\n");
        Assert.Equal(6L, e.Query("sbl(hello, 5, L).").Get<long>("L"));
    }

    [Fact]
    public void InferenceError_NamesPredicate()
    {
        // X and Y have no guards / type source — a genuine inference failure. The
        // message must name the predicate (foo/1) so the author can find it.
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(() => e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "foo(X) :- { Y is X + 1 }.\n"));
        Assert.Contains("foo/1", ex.Message);
    }

    [Fact]
    public void InferenceError_ReportsLine_NotZero_ViaCompile()
    {
        // Through the .shmo compiler the error must carry the block's line, not 0.
        const string src =
            ":- set_prolog_flag(arity_compat, true).\n" +     // line 1
            "a(1).\n" +                                       // line 2
            "foo(X) :- { Y is X + 1 }.\n";                    // line 3 — the block
        var result = ShmoCompiler.TryCompileSource(src, "prog", ShmoBuildMode.Release,
            arityCompat: true);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, err => err.Message.Contains("foo/1") && err.Line == 3);
    }

    // ---- Phase 33 D6 — the '$native_run' variable ceiling. 40 variables (past
    //      the old 32 cap) work; past 64 the consult fails with a clear message,
    //      never a runtime existence_error($native_run/N). ----

    private static string BigBlockProgram(int n)
    {
        // f(X1..Xn) :- integer(X1), ..., { S is X1 + X2 + ... + Xn }, integer(S)... via
        // head vars all used in one block. Guards give every var an integer type.
        var head = new System.Text.StringBuilder("f(");
        for (int i = 1; i <= n; i++) head.Append(i > 1 ? ", X" : "X").Append(i);
        head.Append(", S)");
        var guards = new System.Text.StringBuilder();
        for (int i = 1; i <= n; i++) guards.Append("integer(X").Append(i).Append("), ");
        var sum = new System.Text.StringBuilder("{ S is X1");
        for (int i = 2; i <= n; i++) sum.Append(" + X").Append(i);
        sum.Append(" }");
        return ":- set_prolog_flag(arity_compat, true).\n"
            + head + " :- " + guards + sum + ".\n";
    }

    private static string CallWithOnes(int n)
    {
        var q = new System.Text.StringBuilder("f(");
        for (int i = 0; i < n; i++) q.Append("1, ");
        q.Append("S).");
        return q.ToString();
    }

    [Fact]
    public void NativeBlock_40Variables_RunsPastTheOld32Cap()
    {
        var e = new PrologEngine();
        e.ConsultString(BigBlockProgram(40));
        Assert.Equal(40L, e.Query(CallWithOnes(40)).Get<long>("S"));
    }

    [Fact]
    public void NativeBlock_Past64Variables_FailsLoudlyAtConsult()
    {
        var e = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(() => e.ConsultString(BigBlockProgram(70)));
        Assert.Contains("at most 64", ex.Message);
    }
}
