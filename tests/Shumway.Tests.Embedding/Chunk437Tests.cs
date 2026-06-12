using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 437 (Phase 30) — four Arity-corpus compiler pieces:
/// <list type="number">
/// <item><c>extrn</c> seeded into
/// <see cref="ShmoCompiler.SilentlyIgnoredDirectives"/> (covered in
/// <see cref="Chunk436Tests"/>, which adapts to the seed).</item>
/// <item>Backquote char-code literals (<c>`x</c>) under arity_compat
/// only — same INTEGER token as <c>0'x</c>. Flag off: still an
/// unlexable character (error diagnostic). (Chunk 439 revised the
/// escape rule: the character after the backquote is taken LITERALLY,
/// no escape processing — see <see cref="Chunk439Tests"/>.)</item>
/// <item>Literal backslash inside <c>'...'</c> quoted atoms under
/// arity_compat only — no escape processing; <c>''</c> doubling still
/// escapes the quote. Flag off: ISO escapes unchanged.</item>
/// <item><c>:- define(TermA = TermB)</c> — ALWAYS active (no flag):
/// consumed by the ClauseReader; every subsequent subterm value-equal
/// to TermA becomes TermB. Single pass, no re-expansion, functor names
/// untouched, malformed define is an error diagnostic.</item>
/// </list>
/// </summary>
public class Chunk437Tests
{
    // ------------------------------------------------------------------
    // 2. Backquote char-code literals (arity_compat only)
    // ------------------------------------------------------------------

    [Fact]
    public void Backquote_InList_FlagOn()
    {
        var e = new PrologEngine();
        // Prolog source: codes([`a, `b, `\]).  — expect [97, 98, 92].
        // Chunk 439: the char after the backquote is taken literally
        // (Arity has no escape processing here), so `\ is 92 directly.
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "codes([`a, `b, `\\]).\n");
        Assert.True(e.Query("codes([97, 98, 92]).").Success);
    }

    [Fact]
    public void Backquote_MatchesZeroQuoteForm_FlagOn()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "pair(`x, 0'x).\n");
        Assert.True(e.Query("pair(C, C).").Success);
    }

    [Fact]
    public void Backquote_FlagOff_IsErrorDiagnosticNotCrash()
    {
        var r = ShmoCompiler.TryCompileSource(
            "codes([`a, `b]).\nq(1).\n",
            arityCompat: false);
        Assert.False(r.Success);
        Assert.Contains(r.Errors, err => err.Message.Contains("Unexpected character '`'"));
    }

    // ------------------------------------------------------------------
    // 3. Literal backslash in '...' quoted atoms (arity_compat only)
    // ------------------------------------------------------------------

    [Fact]
    public void QuotedAtom_BackslashLiteral_FlagOn()
    {
        var e = new PrologEngine();
        // Prolog source: path('c:\tmp\new').  — under arity_compat every
        // backslash is literal: 10 characters, no tab/newline escapes.
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "path('c:\\tmp\\new').\n" +
            "bs('\\').\n");
        Assert.True(e.Query("path(X), atom_length(X, 10).").Success);
        Assert.True(e.Query("path(X), atom_codes(X, [0'c, 0':, 92, 0't, 0'm, 0'p, 92, 0'n, 0'e, 0'w]).").Success);
        // bs('\') — the one-character backslash atom.
        Assert.True(e.Query("bs(X), atom_codes(X, [92]).").Success);
    }

    [Fact]
    public void QuotedAtom_DoubledQuoteStillEscapes_FlagOn()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "w('it''s').\n");
        Assert.True(e.Query("w(X), atom_length(X, 4).").Success);
    }

    [Fact]
    public void QuotedAtom_EscapeSemanticsUnchanged_FlagOff()
    {
        var e = new PrologEngine();
        // Prolog source: a('\\'). b('\n').  — ISO escapes: single
        // backslash and a newline character respectively.
        e.ConsultString("a('\\\\').\nb('\\n').\n");
        Assert.True(e.Query("a(X), atom_codes(X, [92]).").Success);
        Assert.True(e.Query("b(X), atom_codes(X, [10]).").Success);
    }

    [Fact]
    public void QuotedAtom_BackslashN_IsTwoCharsUnderFlag()
    {
        var e = new PrologEngine();
        // The same '\n' source text that is ONE char (newline) without
        // the flag is TWO literal chars (backslash, n) with it.
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "b('\\n').\n");
        Assert.True(e.Query("b(X), atom_codes(X, [92, 110]).").Success);
    }

    // ------------------------------------------------------------------
    // 4. :- define(TermA = TermB) — always active
    // ------------------------------------------------------------------

    [Fact]
    public void Define_AtomToNumber()
    {
        var e = new PrologEngine();
        e.ConsultString(":- define(maxlen = 100).\nlimit(maxlen).\n");
        Assert.True(e.Query("limit(100).").Success);
        Assert.False(e.Query("limit(maxlen).").Success);
    }

    [Fact]
    public void Define_AtomToAtom()
    {
        var e = new PrologEngine();
        e.ConsultString(":- define(red = rojo).\ncolor(red).\n");
        Assert.True(e.Query("color(rojo).").Success);
        Assert.False(e.Query("color(red).").Success);
    }

    [Fact]
    public void Define_Cumulative_AllActiveInOneWalk()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- define(a = 1).\n" +
            ":- define(b = 2).\n" +
            "pair(a, b).\n");
        Assert.True(e.Query("pair(1, 2).").Success);
    }

    [Fact]
    public void Define_SinglePass_NoReExpansion()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- define(a = b).\n" +
            ":- define(b = c).\n" +
            "v(a).\n" +
            "w(b).\n");
        // a -> b (NOT chained on to c); b -> c.
        Assert.True(e.Query("v(b).").Success);
        Assert.False(e.Query("v(c).").Success);
        Assert.True(e.Query("w(c).").Success);
    }

    [Fact]
    public void Define_OnlyAppliesFromDirectiveOnward()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "before(k).\n" +
            ":- define(k = 9).\n" +
            "after(k).\n");
        Assert.True(e.Query("before(k).").Success);
        Assert.True(e.Query("after(9).").Success);
    }

    [Fact]
    public void Define_FunctorNamesNotRenamed()
    {
        var e = new PrologEngine();
        // define(f = g) rewrites the ATOM f only — f(1) keeps its
        // functor; only the atom argument position changes.
        e.ConsultString(":- define(f = g).\nq(f(1), f).\n");
        Assert.True(e.Query("q(f(1), g).").Success);
    }

    [Fact]
    public void Define_CompoundLhs_SubtermReplaced()
    {
        var e = new PrologEngine();
        // Non-atom LHS exercises the linear (non-dictionary) store.
        e.ConsultString(":- define(f(1) = one).\nr(f(1), f(2)).\n");
        Assert.True(e.Query("r(one, f(2)).").Success);
    }

    [Fact]
    public void Define_WorksWithoutArityFlag_AndWithIt()
    {
        // Always active — no flag required (TryCompileSource path).
        var off = ShmoCompiler.TryCompileSource(
            ":- define(m = 5).\nlim(m).\n", arityCompat: false);
        Assert.True(off.Success);
        Assert.Empty(off.Errors);

        // And with the flag: consumed by the reader, so it is never
        // reported as an unknown directive either.
        var on = ShmoCompiler.TryCompileSource(
            ":- define(m = 5).\nlim(m).\n", arityCompat: true);
        Assert.True(on.Success);
        Assert.Empty(on.Warnings);
    }

    [Fact]
    public void Define_Malformed_ErrorDiagnosticNotCrash()
    {
        // No `=` inside define/1.
        var r1 = ShmoCompiler.TryCompileSource(":- define(x).\np(1).\n");
        Assert.False(r1.Success);
        Assert.Contains(r1.Errors,
            err => err.Message.Contains("define(TermA = TermB)"));

        // Wrong arity (define/2).
        var r2 = ShmoCompiler.TryCompileSource(":- define(a, b).\np(1).\n");
        Assert.False(r2.Success);
        Assert.Contains(r2.Errors,
            err => err.Message.Contains("define(TermA = TermB)"));

        // Recovery continues: the clause after the bad define still
        // compiles in both cases.
        Assert.Single(r1.Errors);
        Assert.Single(r2.Errors);
    }

    [Fact]
    public void Define_RedefinitionOverwrites()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- define(n = 1).\n" +
            "first(n).\n" +
            ":- define(n = 2).\n" +
            "second(n).\n");
        Assert.True(e.Query("first(1).").Success);
        Assert.True(e.Query("second(2).").Success);
    }
}
