using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 24 chunk 268 (partial) — Arity-Prolog explicit control
/// (ifthen/2, ifthenelse/3) and atom-as-string utilities
/// (string_term/2, string_termq/2, string_search/3).
/// </summary>
public class IfThenStringSearchTests
{
    // ----- string_term / string_termq -----

    [Fact]
    public void StringTerm_AtomToTerm_Parses()
    {
        var e = new PrologEngine();
        var sol = e.Query("string_term('foo(a, 1)', T).");
        Assert.True(sol.Success);
        // T is a compound foo(a, 1).
        var c = (CompoundTerm)sol["T"]!;
        Assert.Equal("foo", c.Functor);
        Assert.Equal(2, c.Args.Length);
    }

    [Fact]
    public void StringTerm_TermToAtom_RendersWriteStyle()
    {
        // write-style does NOT quote `foo bar` (special chars). Use a
        // plain term to keep the test deterministic across renderer
        // quirks.
        var e = new PrologEngine();
        var sol = e.Query("string_term(S, hello(world)).");
        Assert.Equal("hello(world)", ((AtomTerm)sol["S"]!).Name);
    }

    [Fact]
    public void StringTermq_TermToAtom_QuotesWhenNeeded()
    {
        // writeq-style quotes an atom that contains a space.
        var e = new PrologEngine();
        var sol = e.Query("string_termq(S, 'hello world').");
        Assert.Equal("'hello world'", ((AtomTerm)sol["S"]!).Name);
    }

    [Fact]
    public void StringTerm_Roundtrips_SimpleTerm()
    {
        var e = new PrologEngine();
        var sol = e.Query("string_term(S, foo(a, b)), string_term(S, T2).");
        Assert.True(sol.Success);
        var t = (CompoundTerm)sol["T2"]!;
        Assert.Equal("foo", t.Functor);
    }

    // ----- string_search -----

    [Fact]
    public void StringSearch_FindsSingleMatch()
    {
        var e = new PrologEngine();
        var sol = e.Query("string_search('lo', 'hello world', L).");
        Assert.Equal(3L, ((IntTerm)sol["L"]!).Value);
    }

    [Fact]
    public void StringSearch_BacktracksOverEveryOccurrence()
    {
        var e = new PrologEngine();
        var sol = e.Query("findall(L, string_search('a', 'banana', L), Ls).");
        Assert.True(sol.Success);
        Assert.Equal("[1, 3, 5]", AstTermRenderer.Render(sol["Ls"]!));
    }

    [Fact]
    public void StringSearch_NoMatch_Fails()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("string_search('xyz', 'hello', _).").Success);
    }

    [Fact]
    public void StringSearch_OverlappingMatches_AllReported()
    {
        // 'aa' in 'aaaa' overlaps at positions 0, 1, 2.
        var e = new PrologEngine();
        var sol = e.Query("findall(L, string_search('aa', 'aaaa', L), Ls).");
        Assert.Equal("[0, 1, 2]", AstTermRenderer.Render(sol["Ls"]!));
    }

    [Fact]
    public void StringSearch_EmptySub_ReturnsZero()
    {
        var e = new PrologEngine();
        var sol = e.Query("string_search('', 'hello', L).");
        Assert.Equal(0L, ((IntTerm)sol["L"]!).Value);
    }

    [Fact]
    public void StringSearch_LocationCanBeBound_VerifiesMatch()
    {
        // Calling with a ground Location should verify it.
        var e = new PrologEngine();
        Assert.True(e.Query("string_search('lo', 'hello', 3).").Success);
        Assert.False(e.Query("string_search('lo', 'hello', 0).").Success);
    }
}
