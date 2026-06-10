using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 408 (Phase 29) — ISO 7.8.8 cut transparency in lowered control
/// constructs. A <c>!</c> inside a <c>;</c> branch or an if-then-else
/// then/else must commit the HOST clause; MetaTransform lowers those branches
/// to synthesised <c>$disj</c> helpers, and the <c>!</c> used to cut only the
/// HELPER's clause dispatch — leaving the host's later clauses reachable
/// (extra backtracking = a soundness bug: their side effects ran). Found
/// because the chunk-407 meta-wrapper unfold made Blint's <c>ifthen/2</c>
/// sites ISO-correct while the non-unfolded (wrapper meta-call) path kept the
/// old behaviour — the two paths diverged, and the minimal repro showed the
/// engine bug was the pre-existing one.
///
/// The fix: when a clause body has a branch cut, MetaTransform captures the
/// host's barrier (<c>'$get_cut_barrier'(K)</c>, first body goal — CallBuiltin
/// leaves B0 untouched, so it still holds the caller's Call-site value) and
/// threads K through the cut-transparent positions; the branch <c>!</c>
/// becomes <c>'$call'(!, K)</c>, the chunk-88 barrier cut. Cut-opaque
/// positions (a condition, <c>\+</c>, meta-goal args) are unchanged.
/// </summary>
public class Chunk408Tests
{
    private static PrologEngine Make(string source)
    {
        var engine = new PrologEngine();
        engine.ConsultString(source);
        return engine;
    }

    [Fact]
    public void CutInThenBranch_CommitsHostClause()
    {
        // The minimal repro: after d(true) succeeds via clause 1, the branch !
        // must have cut clause 2 away — `d(true), fail` fails into t's second
        // clause, never reaching the throw.
        var e = Make(
            "d(X) :- X -> !, true.\n"
            + "d(_) :- throw(should_not_reach).\n"
            + "t :- d(true), fail.\n"
            + "t.\n");
        Assert.True(e.Query("t.").Success);
    }

    [Fact]
    public void CutInDisjunctionBranch_CommitsHostClause()
    {
        var e = Make(
            "g(X) :- ( X = 1, ! ; true ).\n"
            + "g(_) :- throw(disj_branch_cut_broken).\n"
            + "t :- g(1), fail.\n"
            + "t.\n");
        Assert.True(e.Query("t.").Success);
    }

    [Fact]
    public void CutInElseBranch_CommitsHostClause()
    {
        var e = Make(
            "h(X) :- ( X = 2 -> true ; !, true ).\n"
            + "h(_) :- throw(else_branch_cut_broken).\n"
            + "t :- h(1), fail.\n"
            + "t.\n");
        Assert.True(e.Query("t.").Success);
    }

    [Fact]
    public void BranchCut_AlsoCutsGoalsToTheLeft()
    {
        // The host barrier commits the WHOLE clause: a generator to the LEFT
        // of the branch is cut too — p(X) yields only its first solution.
        var e = Make(
            "pick(1).\npick(2).\npick(3).\n"
            + "p(X) :- pick(X), ( X > 0 -> ! ; true ).\n");
        var all = e.QueryAll("p(X).").ToList();
        Assert.Single(all);
        Assert.Equal(1, all[0].Get<int>("X"));
    }

    [Fact]
    public void CutInCondition_StaysLocalToHelper()
    {
        // Cut-OPAQUE position: a ! in the if-then-else CONDITION must NOT
        // commit the host clause — h's second clause stays reachable when the
        // condition path ultimately fails the first clause.
        var e = Make(
            "q(X) :- ( ( X = 1, ! ) -> fail ; true ), fail.\n"
            + "q(_).\n");
        Assert.True(e.Query("q(1).").Success);
    }

    [Fact]
    public void MetaCalledWrapper_NoLongerResatisfiable()
    {
        // The Blint shape that exposed the bug: ifthen/2 meta-called through
        // findall — the wrapper must be deterministic per call (one solution
        // each), not [1,2,2,3,3].
        var e = Make(
            "ifthen(X,Y) :- X -> !, Y.\n"
            + "ifthen(_,_) :- !.\n"
            + "pick(1).\npick(2).\npick(3).\n"
            + "t(L) :- findall(P, (pick(P), ifthen(P > 1, true)), L).\n");
        var s = e.Query("t(L), L == [1, 2, 3].");
        Assert.True(s.Success);
    }

    [Fact]
    public void NestedBranchCut_ThreadsThroughInnerHelper()
    {
        // The cut sits in a branch of a DISJUNCTION nested inside the then of
        // an outer if-then-else — the barrier threads through both helper
        // levels and still commits the host.
        var e = Make(
            "n(X) :- ( X > 0 -> ( X > 10, ! ; true ) ; true ).\n"
            + "n(_) :- throw(nested_cut_broken).\n"
            + "t :- n(11), fail.\n"
            + "t.\n");
        Assert.True(e.Query("t.").Success);
    }

    [Fact]
    public void PlainBranches_NoCut_Unchanged()
    {
        // No branch cut → no barrier capture; ordinary disjunction semantics
        // (both branches enumerable) are untouched.
        var e = Make("r(X) :- ( X = a ; X = b ).\n");
        var all = e.QueryAll("r(X).").ToList();
        Assert.Equal(2, all.Count);
    }
}
