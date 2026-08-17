using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

public class PrologEngineTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Cmp(string f, params Term[] args) => new CompoundTerm(f, args);

    // ---------- Simplest queries ----------

    [Fact]
    public void Query_AgainstEmptyEngine_AtomFact_Halts()
    {
        var engine = new PrologEngine();
        engine.ConsultString("foo.");
        var sol = engine.Query("foo.");
        Assert.True(sol.Success);
        Assert.Empty(sol.Bindings);
    }

    [Fact]
    public void Query_AgainstMissingPredicate_RaisesExistenceError()
    {
        var engine = new PrologEngine();
        engine.ConsultString("foo.");
        // 'bar/0' is undefined. ISO requires the call to raise
        // existence_error(procedure, bar/0) when reached — not a link
        // failure, and not a silent `false`.
        var ex = Assert.Throws<PrologRuntimeException>(() => engine.Query("bar."));
        Assert.Equal("existence_error", ex.Kind);
        Assert.Equal("bar/0", ex.Detail);
    }

    [Fact]
    public void Query_VariableBindsToAtom()
    {
        var engine = new PrologEngine();
        engine.ConsultString("colour(red).");
        var sol = engine.Query("colour(X).");

        Assert.True(sol.Success);
        Assert.Equal(Atom("red"), sol["X"]);
    }

    [Fact]
    public void Query_VariableBindsToInteger()
    {
        var engine = new PrologEngine();
        engine.ConsultString("answer(42).");
        var sol = engine.Query("answer(N).");

        Assert.True(sol.Success);
        Assert.Equal(Int(42), sol["N"]);
    }

    [Fact]
    public void Query_AtomicMismatch_Fails()
    {
        var engine = new PrologEngine();
        engine.ConsultString("colour(red).");
        var sol = engine.Query("colour(blue).");

        Assert.False(sol.Success);
        Assert.Empty(sol.Bindings);
    }

    // ---------- Compound terms ----------

    [Fact]
    public void Query_CompoundResult_IsMaterialized()
    {
        var engine = new PrologEngine();
        engine.ConsultString("pair(foo(a, b)).");
        var sol = engine.Query("pair(X).");

        Assert.True(sol.Success);
        Assert.Equal(Cmp("foo", Atom("a"), Atom("b")), sol["X"]);
    }

    [Fact]
    public void Query_NestedCompound_IsMaterialized()
    {
        var engine = new PrologEngine();
        engine.ConsultString("nested(outer(inner(x))).");
        var sol = engine.Query("nested(X).");

        Assert.True(sol.Success);
        Assert.Equal(Cmp("outer", Cmp("inner", Atom("x"))), sol["X"]);
    }

    [Fact]
    public void Query_ListResult_IsMaterializedAsConsChain()
    {
        var engine = new PrologEngine();
        engine.ConsultString("items([a, b, c]).");
        var sol = engine.Query("items(L).");

        Assert.True(sol.Success);
        // [a, b, c] is '.'(a, '.'(b, '.'(c, [])))
        Term expected = Cmp(".", Atom("a"),
            Cmp(".", Atom("b"),
                Cmp(".", Atom("c"), Atom("[]"))));
        Assert.Equal(expected, sol["L"]);
    }

    // ---------- Multi-clause backtracking ----------

    [Fact]
    public void Query_MultiClausePredicate_BacktracksToMatch()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a).
            p(b).
            p(c).
            """);
        var sol = engine.Query("p(b).");

        Assert.True(sol.Success);
    }

    [Fact]
    public void Query_MultiClausePredicate_FirstSolutionIsClause1()
    {
        // Query with a variable against a multi-clause predicate: the first
        // solution should bind X to the first clause's value.
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a).
            p(b).
            p(c).
            """);
        var sol = engine.Query("p(X).");

        Assert.True(sol.Success);
        Assert.Equal(Atom("a"), sol["X"]);
    }

    [Fact]
    public void Query_NoMatchingClause_Fails()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a).
            p(b).
            """);
        var sol = engine.Query("p(z).");
        Assert.False(sol.Success);
    }

    // ---------- Rules and conjunctions ----------

    [Fact]
    public void Query_Rule_Succeeds()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            parent(tom, bob).
            parent(bob, alice).
            grandparent(X, Y) :- parent(X, Z), parent(Z, Y).
            """);
        var sol = engine.Query("grandparent(tom, alice).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Query_Rule_BindsCorrectly()
    {
        // grandparent(tom, X)? — should bind X = alice.
        var engine = new PrologEngine();
        engine.ConsultString("""
            parent(tom, bob).
            parent(bob, alice).
            grandparent(X, Y) :- parent(X, Z), parent(Z, Y).
            """);
        var sol = engine.Query("grandparent(tom, Y).");

        Assert.True(sol.Success);
        Assert.Equal(Atom("alice"), sol["Y"]);
    }

    // ---------- Cut ----------

    [Fact]
    public void Query_CutCommitsAndDoesNotBacktrack()
    {
        // Without cut, the second clause would succeed via backtracking after
        // q(b) fails. With cut, the alternative is unreachable.
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a) :- !, q(b).
            p(a).
            q(a).
            """);
        var sol = engine.Query("p(a).");

        Assert.False(sol.Success);
    }

    [Fact]
    public void Query_WithoutCut_DoesBacktrack()
    {
        // Control test for the above: same shape minus the cut.
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a) :- q(b).
            p(a).
            q(a).
            """);
        var sol = engine.Query("p(a).");

        Assert.True(sol.Success);
    }

    // ---------- Solution.ToString ----------

    [Fact]
    public void Solution_ToString_HandlesTrueAndFalse()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            p.
            colour(red).
            """);
        Assert.Equal("true", engine.Query("p.").ToString());
        // colour(blue) is a defined predicate but no clause matches — succeeds
        // at link time, fails at runtime, prints as 'false'.
        Assert.Equal("false", engine.Query("colour(blue).").ToString());
    }

    [Fact]
    public void Solution_ToString_RendersBindings()
    {
        var engine = new PrologEngine();
        engine.ConsultString("colour(red).");
        var sol = engine.Query("colour(X).");
        Assert.Equal("X = red", sol.ToString());
    }

    [Fact]
    public void Solution_ToString_RendersListNicely()
    {
        var engine = new PrologEngine();
        engine.ConsultString("items([a, b, c]).");
        var sol = engine.Query("items(L).");
        Assert.Equal("L = [a, b, c]", sol.ToString());
    }

    // ---------- Operator directives carry over ----------

    [Fact]
    public void OpDirectiveInConsult_AffectsLaterQueries()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- op(700, xfx, ===>).
            a ===> b.
            """);
        var sol = engine.Query("a ===> X.");
        Assert.True(sol.Success);
        Assert.Equal(Atom("b"), sol["X"]);
    }
}
