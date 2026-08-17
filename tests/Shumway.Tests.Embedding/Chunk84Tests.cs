using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 84 — <c>bagof/3</c>, <c>setof/3</c> and <c>forall/2</c> run in the
/// live engine. They used to spawn an isolated peer sub-engine, and
/// <c>bagof</c>/<c>setof</c> did no witness grouping at all ("findall +
/// fail-on-empty", a documented Phase-1 gap).
///
/// <para>Chunk 84 makes them compile transforms. <c>bagof(T, Goal, B)</c>
/// becomes a fail-driven collect loop that pairs each solution with a
/// <em>witness</em> term — the variables free in <c>Goal</c> but not in
/// <c>T</c> and not bound by a <c>^/2</c> wrapper — and then backtracks the
/// grouped result over <c>member/2</c>. The witness groups come out in
/// standard order of the witness; each bag keeps its solutions in generation
/// order (<c>bagof</c>) or sorted and de-duplicated (<c>setof</c>).
/// <c>forall(C, A)</c> becomes <c>\+ (C, \+ A)</c>. Because the goals are
/// spliced as ordinary body goals, they run in the live engine — side
/// effects persist.</para>
///
/// <para>Several tests capture <c>bagof</c>/<c>setof</c>'s whole backtracking
/// sequence with an enclosing <c>findall</c>, so the exact solution order is
/// pinned, not just the set of results.</para>
/// </summary>
public class Chunk84Tests
{
    [Fact]
    public void Bagof_GroupsSolutionsByTheWitness()
    {
        // Y is free in the goal, not in the template — a witness. bagof
        // succeeds once per distinct Y, with the X values for that Y.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(Y-Xs, bagof(X, member(X-Y, [1-a,2-b,3-a]), Xs), R), " +
            "R == [a-[1,3], b-[2]].").Success);
    }

    [Fact]
    public void Bagof_EnumeratesWitnessGroupsInStandardOrder()
    {
        // The goal generates witness b before a; bagof keysorts, so the
        // a-group still comes out first. This is the order every modern
        // Prolog produces.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(Y-Xs, bagof(X, member(X-Y, [1-b,2-a]), Xs), R), " +
            "R == [a-[2], b-[1]].").Success);
    }

    [Fact]
    public void Bagof_KeepsTheBagInGenerationOrder()
    {
        // One witness group; the bag preserves the goal's solution order
        // (it is not sorted — that is setof's job).
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "bagof(X, member(X-Y, [3-a,1-a,2-a]), Xs), Xs == [3,1,2].").Success);
    }

    [Fact]
    public void Bagof_KeepsDuplicates()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "bagof(X, member(X, [1,1,2]), Xs), Xs == [1,1,2].").Success);
    }

    [Fact]
    public void Bagof_FailsWhenTheGoalHasNoSolutions()
    {
        // The defining bagof/findall difference: [] vs failure.
        var engine = new PrologEngine();
        Assert.False(engine.Query("bagof(X, fail, _).").Success);
    }

    [Fact]
    public void Bagof_ExistentialQuantifierSuppressesAWitness()
    {
        // Y^Goal marks Y as existential, so it is not a witness — every
        // solution lands in a single group.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "bagof(X, Y^member(X-Y, [1-a,2-b]), Xs), Xs == [1,2].").Success);
    }

    [Fact]
    public void Bagof_AnonymousVariableInTheGoalIsAWitness()
    {
        // An anonymous variable in the goal, absent from the template, is a
        // witness just like a named one — so this groups by it.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(Xs, bagof(X, member(X-_, [1-a,2-b,3-a]), Xs), R), " +
            "R == [[1,3],[2]].").Success);
    }

    [Fact]
    public void Bagof_WithNoWitness_IsFindallThatFailsWhenEmpty()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "bagof(X, member(X, [3,1,2]), Xs), Xs == [3,1,2].").Success);
    }

    [Fact]
    public void Bagof_GoalSideEffects_PersistInTheLiveEngine()
    {
        // The in-engine proof: assertz inside a bagof goal survives the call.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic logged/1.");
        Assert.True(engine.Query(
            "bagof(X, (member(X, [a,b,c]), assertz(logged(X))), [a,b,c]).").Success);
        Assert.True(engine.Query("logged(a).").Success);
        Assert.True(engine.Query("logged(b).").Success);
        Assert.True(engine.Query("logged(c).").Success);
    }

    [Fact]
    public void Bagof_OverAUserPredicate()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public color/1.
            color(red).
            color(green).
            color(blue).
            """);
        Assert.True(engine.Query(
            "bagof(C, color(C), L), L == [red, green, blue].").Success);
    }

    [Fact]
    public void Setof_SortsAndDeduplicatesTheBag()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "setof(X, member(X, [3,1,2,1,3]), Xs), Xs == [1,2,3].").Success);
    }

    [Fact]
    public void Setof_GroupsByWitness_WithEachBagSorted()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(Y-Xs, setof(X, member(X-Y, [3-a,1-a,2-b,1-a]), Xs), R), " +
            "R == [a-[1,3], b-[2]].").Success);
    }

    [Fact]
    public void Setof_FailsWhenTheGoalHasNoSolutions()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("setof(X, fail, _).").Success);
    }

    [Fact]
    public void Forall_SucceedsWhenEveryConditionSatisfiesTheAction()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("forall(member(X, [1,2,3]), X > 0).").Success);
    }

    [Fact]
    public void Forall_FailsOnACounterExample_EvenWhenItIsLast()
    {
        // The counter-example (1, which is not > 1) is the last solution of
        // the condition — forall must enumerate every one, not just the first.
        var engine = new PrologEngine();
        Assert.False(engine.Query("forall(member(X, [2,3,1]), X > 1).").Success);
    }

    [Fact]
    public void Forall_GoalSideEffects_PersistInTheLiveEngine()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic visited/1.");
        Assert.True(engine.Query(
            "forall(member(X, [p,q,r]), assertz(visited(X))).").Success);
        Assert.True(engine.Query("visited(p).").Success);
        Assert.True(engine.Query("visited(q).").Success);
        Assert.True(engine.Query("visited(r).").Success);
    }

    [Fact]
    public void Bagof_NestedInsideBagof()
    {
        // The inner bagof opens and closes its own solution frame for each
        // solution the outer goal produces.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "bagof(X-Ys, bagof(Y, member(X-Y, [1-a,1-b,2-c]), Ys), R), " +
            "R == [1-[a,b], 2-[c]].").Success);
    }
}
