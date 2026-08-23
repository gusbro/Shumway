using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Consult-time routing of clauses into the dynamic store must see the same
/// pipeline stages the static side gets: a grammar rule's REAL head is the
/// DCG-translated one, and in-file goal_expansion applies to the stored body.
/// Before the fix, `f(b) --> [x]` under `:- dynamic f/3.` compiled into an
/// invisible static twin, and a dynamic clause ran its body UNexpanded while
/// an identical static clause ran it expanded (their clpz's listing test0084
/// was the finder).
/// </summary>
public class DynamicClauseRoutingTests
{
    [Fact]
    public void DcgRule_ForDynamicPredicate_LandsInTheDynamicStore()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic f/3.\nf(a, X, X).\nf(b) --> [x].");
        Assert.True(e.Query(
            "clause(f(b, S0, S), true), S0 == [x|S].").Success);
        Assert.True(e.Query("f(b, [x, y], R), R == [y].").Success);
        // and it stays mutable like any dynamic clause
        Assert.True(e.Query(
            "retract((f(b, S0, S) :- true)), \\+ f(b, [x], _).").Success);
    }

    [Fact]
    public void GoalExpansion_AppliesToDynamicClauses_LikeStaticOnes()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "goal_expansion(foo, bar).\n"
            + "bar.\n"
            + ":- dynamic t/0.\n"
            + "t :- foo.\n"
            + "s :- foo.");
        // both run the EXPANDED body (foo/0 does not exist)
        Assert.True(e.Query("t.").Success);
        Assert.True(e.Query("s.").Success);
        // and the stored form is the expanded one (SWI/Trealla load semantics)
        Assert.True(e.Query("clause(t, B), B == bar.").Success);
    }

    [Fact]
    public void GoalExpansion_HookAfterTheDynamicClause_DoesNotRetroApply()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- dynamic u/0.\n"
            + "u :- foo.\n"
            + "goal_expansion(foo, bar).\n"
            + "bar.");
        Assert.True(e.Query("clause(u, B), B == foo.").Success);
    }
}
