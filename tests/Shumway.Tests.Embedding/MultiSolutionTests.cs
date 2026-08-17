using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

public class MultiSolutionTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    [Fact]
    public void QueryAll_NoSolutions_YieldsEmpty()
    {
        var engine = new PrologEngine();
        engine.ConsultString("colour(red).");
        var solutions = engine.QueryAll("colour(blue).").ToList();
        Assert.Empty(solutions);
    }

    [Fact]
    public void QueryAll_SingleSolution_YieldsExactlyOne()
    {
        var engine = new PrologEngine();
        engine.ConsultString("colour(red).");
        var solutions = engine.QueryAll("colour(X).").ToList();
        Assert.Single(solutions);
        Assert.Equal(Atom("red"), solutions[0]["X"]);
    }

    [Fact]
    public void QueryAll_MultiClause_YieldsEachInSourceOrder()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a).
            p(b).
            p(c).
            """);
        var bindings = engine.QueryAll("p(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { Atom("a"), Atom("b"), Atom("c") }, bindings);
    }

    [Fact]
    public void QueryAll_CutCommitsAndYieldsOnlyOne()
    {
        // p(a) :- !.   p(b).   ?- p(X).
        // With the cut, only the first clause's solution is reachable.
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a) :- !.
            p(b).
            """);
        var solutions = engine.QueryAll("p(X).").ToList();
        Assert.Single(solutions);
        Assert.Equal(Atom("a"), solutions[0]["X"]);
    }

    [Fact]
    public void QueryAll_DeepCut_YieldsCommittedBranchOnly()
    {
        // p(X) :- q(X), !.    q(a).   q(b).
        // ?- p(X). → X = a only.
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(X) :- q(X), !.
            q(a).
            q(b).
            """);
        var solutions = engine.QueryAll("p(X).").ToList();
        Assert.Single(solutions);
        Assert.Equal(Atom("a"), solutions[0]["X"]);
    }

    [Fact]
    public void QueryAll_RuleWithMultiClauseSubgoal_EnumeratesProduct()
    {
        // pair(X, Y) :- one(X), two(Y).
        // one(a). one(b).
        // two(1). two(2).
        // Solutions: (a,1), (a,2), (b,1), (b,2).
        var engine = new PrologEngine();
        engine.ConsultString("""
            pair(X, Y) :- one(X), two(Y).
            one(a).
            one(b).
            two(1).
            two(2).
            """);

        var solutions = engine.QueryAll("pair(X, Y).")
            .Select(s => (s["X"], s["Y"]))
            .ToList();

        Assert.Equal(4, solutions.Count);
        Assert.Equal((Atom("a"), Int(1)), solutions[0]);
        Assert.Equal((Atom("a"), Int(2)), solutions[1]);
        Assert.Equal((Atom("b"), Int(1)), solutions[2]);
        Assert.Equal((Atom("b"), Int(2)), solutions[3]);
    }

    [Fact]
    public void QueryAll_Member_Pattern()
    {
        // member(X, [X|_]).
        // member(X, [_|T]) :- member(X, T).
        // ?- member(X, [a, b, c]).
        var engine = new PrologEngine();
        engine.ConsultString("""
            member(X, [X|_]).
            member(X, [_|T]) :- member(X, T).
            """);

        var solutions = engine.QueryAll("member(X, [a, b, c]).")
            .Select(s => s["X"])
            .ToList();
        Assert.Equal(new Term[] { Atom("a"), Atom("b"), Atom("c") }, solutions);
    }

    [Fact]
    public void Query_FirstOnly_StillWorksAfterRefactor()
    {
        // The single-solution Query method should still return the first.
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
    public void QueryAll_Count_GivesNumberOfSolutions()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a).
            p(b).
            p(c).
            p(d).
            """);
        Assert.Equal(4, engine.QueryAll("p(X).").Count());
    }

    [Fact]
    public void QueryAll_LaziltyEnumerates_FirstShortCircuits()
    {
        // Backed by IEnumerable, .First() should stop after the first solution.
        // This isn't directly observable from C# but verifies the contract.
        var engine = new PrologEngine();
        engine.ConsultString("""
            p(a).
            p(b).
            p(c).
            """);
        var first = engine.QueryAll("p(X).").First();
        Assert.Equal(Atom("a"), first["X"]);
    }

    [Fact]
    public void QueryAll_NestedBacktracking_EnumeratesCorrectly()
    {
        // path(a, b). path(a, c). path(b, d). path(c, d).
        // reachable(X, Y) :- path(X, Y).
        // reachable(X, Y) :- path(X, Z), reachable(Z, Y).
        //
        // ?- reachable(a, Y) — should enumerate b, c, d, d (since two paths
        // lead to d).
        var engine = new PrologEngine();
        engine.ConsultString("""
            path(a, b).
            path(a, c).
            path(b, d).
            path(c, d).
            reachable(X, Y) :- path(X, Y).
            reachable(X, Y) :- path(X, Z), reachable(Z, Y).
            """);

        var targets = engine.QueryAll("reachable(a, Y).")
            .Select(s => s["Y"])
            .ToList();
        Assert.Equal(
            new Term[] { Atom("b"), Atom("c"), Atom("d"), Atom("d") },
            targets);
    }

    [Fact]
    public void QueryAll_ArithmeticGenerator_Pattern()
    {
        // between(L, H, L) :- L =< H.
        // between(L, H, X) :- L < H, L1 is L + 1, between(L1, H, X).
        //
        // ?- between(1, 4, X) — enumerates 1, 2, 3, 4.
        var engine = new PrologEngine();
        engine.ConsultString("""
            between(L, H, L) :- L =< H.
            between(L, H, X) :- L < H, L1 is L + 1, between(L1, H, X).
            """);

        var nums = engine.QueryAll("between(1, 4, X).")
            .Select(s => s["X"])
            .ToList();
        Assert.Equal(new Term[] { Int(1), Int(2), Int(3), Int(4) }, nums);
    }
}
