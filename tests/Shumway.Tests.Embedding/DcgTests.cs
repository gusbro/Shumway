using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

public class DcgTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term List(params Term[] elements)
    {
        Term result = Atom("[]");
        for (int i = elements.Length - 1; i >= 0; i--)
            result = new CompoundTerm(".", new[] { elements[i], result });
        return result;
    }

    // ---------- Terminal-only rules ----------

    [Fact]
    public void Dcg_EmptyTerminal_ConsumesNothing()
    {
        // nothing --> [].
        // ?- nothing([], []).         → succeed
        // ?- nothing([a], [a]).       → succeed (no consumption)
        // ?- nothing([a], []).        → fail (remaining is [a], not [])
        var engine = new PrologEngine();
        engine.ConsultString("nothing --> [].");
        Assert.True(engine.Query("nothing([], []).").Success);
        Assert.True(engine.Query("nothing([a], [a]).").Success);
        Assert.False(engine.Query("nothing([a], []).").Success);
    }

    [Fact]
    public void Dcg_SingleTokenTerminal()
    {
        var engine = new PrologEngine();
        engine.ConsultString("noun --> [dog].");
        // ?- noun([dog], []). → succeed
        Assert.True(engine.Query("noun([dog], []).").Success);
        // ?- noun([cat], []). → fail
        Assert.False(engine.Query("noun([cat], []).").Success);
    }

    [Fact]
    public void Dcg_MultiTokenTerminal()
    {
        var engine = new PrologEngine();
        engine.ConsultString("greeting --> [hello, world].");
        Assert.True(engine.Query("greeting([hello, world], []).").Success);
        Assert.False(engine.Query("greeting([hello], []).").Success);
    }

    [Fact]
    public void Dcg_TerminalLeavesRemaining()
    {
        var engine = new PrologEngine();
        engine.ConsultString("noun --> [dog].");
        var sol = engine.Query("noun([dog, runs], Rest).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("runs")), sol["Rest"]);
    }

    // ---------- Conjunctions of non-terminals ----------

    [Fact]
    public void Dcg_NonTerminalChain()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            "noun --> [dog].\n" +
            "verb --> [runs].\n" +
            "sentence --> noun, verb.\n");
        Assert.True(engine.Query("sentence([dog, runs], []).").Success);
        Assert.False(engine.Query("sentence([dog, walks], []).").Success);
    }

    [Fact]
    public void Dcg_MixedTerminalAndNonTerminal()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            "verb --> [runs].\n" +
            "phrase --> [the, dog], verb.\n");
        Assert.True(engine.Query("phrase([the, dog, runs], []).").Success);
    }

    // ---------- Recursive grammars ----------

    [Fact]
    public void Dcg_NonEmptyList()
    {
        // num_list --> [N], { integer(N) }.
        // num_list --> [N], { integer(N) }, num_list.
        // ?- num_list([1, 2, 3], []). → succeed
        // ?- num_list([1, two], []).  → fail
        var engine = new PrologEngine();
        engine.ConsultString(
            "num_list --> [N], { integer(N) }.\n" +
            "num_list --> [N], { integer(N) }, num_list.\n");
        Assert.True(engine.Query("num_list([1, 2, 3], []).").Success);
        Assert.False(engine.Query("num_list([1, two], []).").Success);
    }

    [Fact]
    public void Dcg_GrammarWithEscapedGoal()
    {
        // The "{ G }" escape lets DCG body call regular Prolog goals.
        var engine = new PrologEngine();
        engine.ConsultString(
            "positive --> [N], { integer(N), N > 0 }.\n");
        Assert.True(engine.Query("positive([5], []).").Success);
        Assert.False(engine.Query("positive([-3], []).").Success);
        Assert.False(engine.Query("positive([foo], []).").Success);
    }

    // ---------- Parameterised DCG rules ----------

    [Fact]
    public void Dcg_PassesUserArgsThrough()
    {
        // A DCG rule with a user arg gets it before the diff-list pair.
        // greet(X) --> [hello, X].
        // ?- greet(world, [hello, world], []). → succeed
        // ?- greet(W, [hello, friend], []).     → succeed, W = friend
        var engine = new PrologEngine();
        engine.ConsultString("greet(X) --> [hello, X].");
        Assert.True(engine.Query("greet(world, [hello, world], []).").Success);
        var sol = engine.Query("greet(W, [hello, friend], []).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("friend"), sol["W"]);
    }

    // ---------- Enumeration through DCG ----------

    [Fact]
    public void Dcg_MultiClause_Enumerates()
    {
        // colour --> [red].
        // colour --> [green].
        // colour --> [blue].
        var engine = new PrologEngine();
        engine.ConsultString(
            "colour --> [red].\n" +
            "colour --> [green].\n" +
            "colour --> [blue].\n");

        // Each clause produces a different consumed token.
        var solutions = engine.QueryAll("colour([X], []).")
            .Select(s => s["X"]).ToList();
        Assert.Equal(
            new Term[] { Atom("red"), Atom("green"), Atom("blue") },
            solutions);
    }

    // ---------- Classic arithmetic expression grammar ----------

    [Fact]
    public void Dcg_DigitListGrammar()
    {
        // digit --> [D], { integer(D), D >= 0, D =< 9 }.
        // digits --> digit.
        // digits --> digit, digits.
        var engine = new PrologEngine();
        engine.ConsultString(
            "digit --> [D], { integer(D), D >= 0, D =< 9 }.\n" +
            "digits --> digit.\n" +
            "digits --> digit, digits.\n");

        Assert.True(engine.Query("digits([1, 2, 3], []).").Success);
        Assert.True(engine.Query("digits([5], []).").Success);
        Assert.False(engine.Query("digits([], []).").Success);
        Assert.False(engine.Query("digits([1, a], []).").Success);
    }

    // ---------- Diff-list output capturing ----------

    [Fact]
    public void Dcg_CaptureRemainingInput()
    {
        // word --> [W].
        // ?- word([hello, world], Rest). → Rest = [world]
        var engine = new PrologEngine();
        engine.ConsultString("word --> [_].");
        var sol = engine.Query("word([hello, world], Rest).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("world")), sol["Rest"]);
    }

    // ---------- Cut in DCG body ----------

    [Fact]
    public void Dcg_CutInBody_CommitsToBranch()
    {
        // first_match --> [a], !, [b].
        // first_match --> [a], [c].
        // ?- first_match([a, b], []). → succeed
        // ?- first_match([a, c], []). → fail (cut prevented alternative)
        var engine = new PrologEngine();
        engine.ConsultString(
            "first_match --> [a], !, [b].\n" +
            "first_match --> [a], [c].\n");
        Assert.True(engine.Query("first_match([a, b], []).").Success);
        Assert.False(engine.Query("first_match([a, c], []).").Success);
    }
}
