using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 438 (Phase 30) — two Arity-corpus lexer/parser pieces, both
/// arity_compat only:
/// <list type="number">
/// <item><c>$</c> terminates symbol-atom runs: <c>X=$texto$</c> lexes as
/// <c>=</c> + the $-quoted atom <c>texto</c> instead of the ISO
/// maximal-munch atom <c>=$</c>. Flag off: unchanged (<c>=$</c> is one
/// symbolic atom).</item>
/// <item>Embedded native goals: in a NON-DCG clause body, <c>{ raw C }</c>
/// is skipped raw (naive brace counting) and the goal <c>true</c> is
/// substituted. DCG rules (<c>--&gt;</c> before any body <c>{</c>) keep
/// the ISO {}/1 meaning. Flag off: braces keep their ISO meaning
/// everywhere.</item>
/// </list>
/// </summary>
public class Chunk438Tests
{
    // ------------------------------------------------------------------
    // 1. `$` terminates symbol-atom runs (arity_compat only)
    // ------------------------------------------------------------------

    [Fact]
    public void EqualsDollarAtom_NoSpaces_UnifiesAtom_FlagOn()
    {
        var e = new PrologEngine();
        // The corpus shape: X=$texto$ with no whitespace. Without the
        // chunk-438 fix the lexer munched `=$` into one symbolic atom.
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "eq(X) :- X=$texto$.\n");
        Assert.True(e.Query("eq(texto).").Success);
        Assert.False(e.Query("eq(otro).").Success);
    }

    [Fact]
    public void EqualsDollarDollar_EmptyAtom_FlagOn()
    {
        var e = new PrologEngine();
        // X=$$ — `=` followed by the EMPTY $-quoted atom (like '').
        // Pre-fix this munched `=$$` into one symbolic atom.
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "emp(X) :- X=$$.\n");
        Assert.True(e.Query("emp(X), atom_length(X, 0).").Success);
    }

    [Fact]
    public void DollarAtomInArgumentPosition_FlagOn()
    {
        var e = new PrologEngine();
        // Mid-run $ after a longer symbol prefix: `==$a$` must lex as
        // `==` + atom a.
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "chk :- a==$a$.\n");
        Assert.True(e.Query("chk.").Success);
    }

    [Fact]
    public void EqDollar_StillOneSymbolAtom_FlagOff()
    {
        var e = new PrologEngine();
        // ISO maximal munch: with the flag OFF, `=$` is a single
        // symbolic atom — usable as an operator once declared.
        e.ConsultString(
            ":- op(700, xfx, =$).\n" +
            "f(a =$ b).\n");
        Assert.True(e.Query("f(X), X = '=$'(a, b).").Success);
    }

    // ------------------------------------------------------------------
    // 2. Embedded native goals { ... } (arity_compat only, non-DCG)
    // ------------------------------------------------------------------

    // A native block the compiler cannot handle — a call to a function the interop
    // class does not provide, C control flow, or un-parseable text — is a CONSULT
    // ERROR, never a silent no-op (a no-op'd block would make the program misbehave
    // without the author noticing). The raw span is still captured
    // (NativeBlockCaptureTests); a handled block running end-to-end is in
    // NativeWiringTests.

    [Fact]
    public void NativeGoal_MissingInteropFunction_IsAConsultError()
    {
        var e = new PrologEngine();
        // ThrowsAny: the consult error is a NativeBlockCompileException (an
        // InvalidOperationException subclass carrying the block's line).
        Assert.ThrowsAny<System.InvalidOperationException>(() => e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "p(X, Y) :- X = 1, { call_some_c_function(x, 1); }, Y is X + 1.\n"));
    }

    [Fact]
    public void NativeGoal_ControlFlow_IsAConsultError()
    {
        var e = new PrologEngine();
        // Nested braces still balance under the raw skip (capture), but C control
        // flow is not compilable → a consult error, not a silent no-op.
        Assert.ThrowsAny<System.InvalidOperationException>(() => e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "q(ok) :- { if (x) { y(); } else { z(); } }.\n"));
    }


    [Fact]
    public void NativeGoal_Unterminated_ErrorDiagnosticNotCrash()
    {
        var r = ShmoCompiler.TryCompileSource(
            "p :- { never closed.\n",
            arityCompat: true);
        Assert.False(r.Success);
        Assert.Contains(r.Errors,
            err => err.Message.Contains("Unterminated native code goal"));
    }

    // ------------------------------------------------------------------
    // DCG rules keep the ISO {}/1 meaning under the flag
    // ------------------------------------------------------------------

    [Fact]
    public void DcgRule_BraceGoalStillExecutes_FlagOn()
    {
        var e = new PrologEngine();
        // `-->` appears before the body `{`, so the brace is a real
        // Prolog goal that must EXECUTE (Y is X * 2), not be skipped.
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "double(X) --> [X], { Y is X * 2 }, [Y].\n");
        Assert.True(e.Query("phrase(double(3), [3, 6]).").Success);
        Assert.False(e.Query("phrase(double(3), [3, 5]).").Success);
        var s = e.Query("phrase(double(4), [4, Y]).");
        Assert.True(s.Success);
        Assert.Equal(8L, ((IntTerm)s["Y"]!).Value);
    }

    [Fact]
    public void DcgFollowedByNormalClause_FlagResetPerClause()
    {
        var e = new PrologEngine();
        // The saw-arrow state is per clause: the DCG rule's braces are Prolog
        // (Y is X+1 as an ordinary goal); the NEXT (non-DCG) clause's braces are
        // native — here a compilable native-arithmetic block (R is 1+1).
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "inc(X) --> [X], { Y is X + 1 }, [Y].\n" +
            "plain(R) :- { R is 1 + 1 }, integer(R).\n");
        Assert.True(e.Query("phrase(inc(1), [1, 2]).").Success);
        Assert.True(e.Query("plain(2).").Success);
    }

    // ------------------------------------------------------------------
    // Flag off: braces keep the ISO meaning everywhere
    // ------------------------------------------------------------------

    [Fact]
    public void Braces_IsoTermMeaning_FlagOff()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "iso(X) :- X = {a, b}.\n" +
            "n({}).\n");
        Assert.True(e.Query("iso({a, b}).").Success);
        Assert.True(e.Query("iso(Y), Y = '{}'(','(a, b)).").Success);
        Assert.True(e.Query("n({}).").Success);
    }

    [Fact]
    public void Braces_IsoDcgMeaning_FlagOff()
    {
        var e = new PrologEngine();
        e.ConsultString("twice(X) --> [X], { Y is X * 2 }, [Y].\n");
        Assert.True(e.Query("phrase(twice(5), [5, 10]).").Success);
    }
}
