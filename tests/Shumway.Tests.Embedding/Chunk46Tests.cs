using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 46: DCG enhancements — negation-as-failure (<c>\+ NT</c>)
/// inside a DCG body, and meta-call <c>call(G)</c> for variable
/// non-terminals. The ISO conformance suite that's the other half
/// of this chunk lives in <c>Shumway.Tests.IsoConformance</c>.
/// </summary>
public class Chunk46Tests
{
    private static Term Atom(string n) => new AtomTerm(n);

    [Fact]
    public void Dcg_NegationAsFailure_AcceptsNonMatchingPrefix()
    {
        // `not_x --> \+ [x].` succeeds when the next char isn't 'x',
        // consuming nothing.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public not_x/2.\n" +
            "not_x --> \\+ [x].");
        // Input doesn't start with x → succeeds, no input consumed.
        Assert.True(engine.Query("not_x([a, b], R), R == [a, b].").Success);
        // Input starts with x → fails.
        Assert.False(engine.Query("not_x([x, b], _).").Success);
    }

    [Fact]
    public void Dcg_NegationDoesNotConsume()
    {
        // After `\+ NT`, the diff-list state is unchanged regardless of
        // what NT would have done.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public after_neg/2.\n" +
            "after_neg --> \\+ [no], [yes].");
        Assert.True(engine.Query("after_neg([yes], []).").Success);
        Assert.False(engine.Query("after_neg([no, yes], _).").Success);
    }

    [Fact]
    public void Dcg_DisjunctionAndIfThenElseStillWork()
    {
        // The existing branches still pass through unchanged.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public ab/2.\n" +
            "ab --> [a] ; [b].");
        Assert.True(engine.Query("ab([a], []).").Success);
        Assert.True(engine.Query("ab([b], []).").Success);
        Assert.False(engine.Query("ab([c], _).").Success);
    }

    [Fact]
    public void Dcg_EmbeddedActions()
    {
        // `{ G }` runs G as a plain Prolog goal without touching the
        // diff-list state. Combine with a terminal to check both halves.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public any_atom/3.\n" +
            "any_atom(X) --> [X], { atom(X) }.");
        Assert.True(engine.Query("any_atom(foo, [foo], []).").Success);
        Assert.False(engine.Query("any_atom(42, [42], _).").Success);
    }

    [Fact]
    public void Dcg_RecursiveNonTerminal_StillWorks()
    {
        // Sanity check that recursive DCG productions remain intact
        // after the chunk-46 transform changes.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public as/2.\n" +
            "as --> [].\n" +
            "as --> [a], as.");
        Assert.True(engine.Query("as([a, a, a], []).").Success);
        // [a, b] can't be fully parsed because b stops the as recursion;
        // the residual list must contain b.
        Assert.False(engine.Query("as([a, b], []).").Success);
    }
}
