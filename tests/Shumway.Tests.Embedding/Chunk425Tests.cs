using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 425 (Phase 30) — the <c>arity_compat</c> flag: Arity/Prolog32
/// <c>$...$</c> quoted atoms (a <c>$</c> doubles; no backslash escapes),
/// C-preprocessor <c>#line</c> markers (skipped AND honoured for positions),
/// and annotated directive indicators (<c>foo/8:far</c>,
/// <c>f/2:system(...)</c> — annotation ignored). Off by default; enabled by
/// <c>set_prolog_flag(arity_compat, true)</c> (runtime or as an in-file
/// directive, which flips the live lexer) or <c>shumway-compile --arity</c>.
/// </summary>
public class Chunk425Tests
{
    private const string On = ":- set_prolog_flag(arity_compat, true).\n";

    [Fact]
    public void DollarAtoms_BasicEmptyAndEscapes()
    {
        var e = new PrologEngine();
        e.ConsultString(On +
            "p($JOIN_ALL_CASES$).\n" +
            "q($$).\n" +                  // empty atom, like ''
            "r($ho'l$$a$).\n" +           // ' literal, $$ -> $
            "s($a\\b$).\n");              // backslash is LITERAL inside $...$
        Assert.True(e.Query("p(X), X == 'JOIN_ALL_CASES'.").Success);
        Assert.True(e.Query("q(X), X == ''.").Success);
        Assert.True(e.Query("r(X), X == 'ho''l$a'.").Success);
        Assert.True(e.Query("s(X), atom_length(X, 3).").Success);
    }

    [Fact]
    public void DollarAtoms_OffByDefault()
    {
        var e = new PrologEngine();
        Assert.ThrowsAny<System.Exception>(() =>
            e.ConsultString("p($oops$).\n"));
    }

    [Fact]
    public void HashLine_SkippedAndPositionsHonoured()
    {
        // The marker is consumed; the NEXT physical line reports as line 500.
        var r = ShmoCompiler.TryCompileSource(
            "#line 500 \"orig.pl\"\nfoo(X) :- bar(X.\n",
            arityCompat: true);
        Assert.False(r.Success);
        Assert.Equal(500, r.Errors[0].Line);
        // And a clean file with markers compiles.
        var ok = ShmoCompiler.TryCompileSource(
            "#line 1 \"a.pl\"\np(1).\n#line 7 \"b.pl\"\nq(2).\n",
            arityCompat: true);
        Assert.True(ok.Success);
    }

    [Fact]
    public void AnnotatedDirectives_AcceptedAndIgnored()
    {
        var r = ShmoCompiler.TryCompileSource(
            ":- public o_join_nf/2:far.\n" +
            ":- public f/1:system(det='Func'(int)).\n" +
            ":- visible v/1:far.\n" +
            "o_join_nf(_, _).\nf(_).\n",
            arityCompat: true);
        Assert.True(r.Success);
        var publics = r.Object!.Defined
            .Where(d => d.Visibility == PredicateVisibility.Public)
            .Select(d => d.Indicator.ToString()).ToList();
        Assert.Contains("o_join_nf/2", publics);
        Assert.Contains("f/1", publics);
    }

    [Fact]
    public void Flag_ReadableAndSettableAtRuntime()
    {
        var e = new PrologEngine();
        var s = e.Query("current_prolog_flag(arity_compat, V).");
        Assert.Equal("false", s["V"]!.ToString());
        Assert.True(e.Query("set_prolog_flag(arity_compat, true).").Success);
        // Applies to a SUBSEQUENT consult.
        e.ConsultString("t($dollar atom$).\n");
        Assert.True(e.Query("t(X), X == 'dollar atom'.").Success);
    }

    [Fact]
    public void MidFileFlip_AffectsLexingOfTheRestOfTheFile()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "a(plain).\n" + On + "b($after$).\n");
        Assert.True(e.Query("a(plain), b(X), X == after.").Success);
    }
}
