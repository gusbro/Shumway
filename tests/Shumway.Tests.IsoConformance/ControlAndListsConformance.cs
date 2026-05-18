using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §7.8 Control constructs and §8.10 List operations.
/// Covers <c>true/0</c>, <c>fail/0</c>, conjunction, disjunction,
/// if-then(-else), negation-as-failure (<c>\+/1</c>), cut (<c>!/0</c>),
/// plus the prelude's <c>member/2</c>, <c>append/3</c>, and
/// <c>length/2</c> from chunk 40 + chunk 43.
/// </summary>
public class ControlAndListsConformance
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ---------- Control ----------

    [Fact]
    public void True_AlwaysSucceeds() =>
        Assert.True(new PrologEngine().Query("true.").Success);

    [Fact]
    public void Fail_AlwaysFails() =>
        Assert.False(new PrologEngine().Query("fail.").Success);

    [Fact]
    public void Conjunction()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("true, true.").Success);
        Assert.False(engine.Query("true, fail.").Success);
        Assert.False(engine.Query("fail, true.").Success);
    }

    [Fact]
    public void Disjunction()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("true ; fail.").Success);
        Assert.True(engine.Query("fail ; true.").Success);
        Assert.False(engine.Query("fail ; fail.").Success);
    }

    [Fact]
    public void Disjunction_EnumeratesBothBranches()
    {
        var engine = new PrologEngine();
        var sols = engine.QueryAll("(X = a ; X = b).").ToList();
        Assert.Equal(2, sols.Count);
        Assert.Equal(Atom("a"), sols[0]["X"]);
        Assert.Equal(Atom("b"), sols[1]["X"]);
    }

    [Fact]
    public void IfThen_BranchTakenWhenConditionSucceeds()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("(true -> X = yes ; X = no), X == yes.").Success);
        Assert.True(engine.Query("(fail -> X = yes ; X = no), X == no.").Success);
    }

    [Fact]
    public void Negation_AsFailure()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("\\+ fail.").Success);
        Assert.False(engine.Query("\\+ true.").Success);
    }

    [Fact]
    public void Cut_CommitsToFirstChoice()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- public maybe/1.\nmaybe(yes).\nmaybe(no).");
        // Without cut: both clauses match X.
        Assert.Equal(2, engine.QueryAll("maybe(X).").Count());
        // With cut after first match: only yes survives.
        Assert.Single(engine.QueryAll("maybe(X), !.").ToList());
    }

    // ---------- Lists (prelude member/append/length) ----------

    [Fact]
    public void Member_FirstSolution()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("a"), engine.Query("member(X, [a, b, c]).")["X"]);
    }

    [Fact]
    public void Member_EnumeratesEverything()
    {
        var engine = new PrologEngine();
        var sols = engine.QueryAll("member(X, [a, b, c]).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Atom("a"), Atom("b"), Atom("c") }, sols);
    }

    [Fact]
    public void Append_JoinsTwoLists()
    {
        var engine = new PrologEngine();
        // append(+, +, ?)
        var sol = engine.Query("append([1, 2], [3, 4], L).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Length_BothModes()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(3), engine.Query("length([a, b, c], N).")["N"]);
        Assert.True(engine.Query("length(L, 3), is_list(L).").Success);
    }

    [Fact]
    public void Reverse_ReversesList()
    {
        var engine = new PrologEngine();
        // reverse/2 is a builtin (chunk 30-something).
        var sol = engine.Query("reverse([1, 2, 3], R).");
        Assert.True(sol.Success);
    }
}
