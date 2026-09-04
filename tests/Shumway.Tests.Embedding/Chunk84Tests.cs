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
/// <c>T</c> and not bound by a <c>^/2</c> wrapper — and then enumerates the
/// witness groups on backtracking. The witness groups come out in
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

    [Fact]
    public void CompiledSetof_GroupsAliasingWitnesses_AtScale()
    {
        // permutation(L, L) over N fresh variables solves N! times, and the
        // free variable L partitions those solutions into one witness group
        // per variable-ALIASING pattern — a Bell number (52 for N=5, 203
        // for N=6, 877 for N=7). This pins two things at once: the witness
        // semantics over aliased unbound variables, and the collector's
        // linear grouping — a per-pair scan of the groups made the same
        // query quadratic in the Bell number (an 8-element setof took
        // twenty seconds; N=7 here finishes in well under one).
        var e = new PrologEngine();
        e.ConsultString("""
            groups(N, G) :-
                length(L, N),
                findall(x, setof(t, permutation(L, L), _), Xs),
                length(Xs, G).
            """);
        var sol = e.Query("groups(5, G5), groups(6, G6), groups(7, G7).");
        Assert.True(sol.Success);
        Assert.Equal(52L, ((Shumway.Compiler.Ast.IntTerm)sol["G5"]!).Value);
        Assert.Equal(203L, ((Shumway.Compiler.Ast.IntTerm)sol["G6"]!).Value);
        Assert.Equal(877L, ((Shumway.Compiler.Ast.IntTerm)sol["G7"]!).Value);
    }

    [Fact]
    public void RuntimeSetof_GroupsIdenticallyToTheCompiledRewrite()
    {
        // setof/3 reached through a meta-call runs the prelude driver, not
        // the compile-time rewrite. Both record and group through the same
        // builtins, and this pins that they agree where it is easiest to
        // drift: witness groups over aliased unbound variables.
        var e = new PrologEngine();
        e.ConsultString("""
            groups(N, G) :-
                length(L, N),
                findall(x, call(setof(t, permutation(L, L), _)), Xs),
                length(Xs, G).
            """);
        var sol = e.Query("groups(5, G5), groups(6, G6), groups(7, G7).");
        Assert.True(sol.Success);
        Assert.Equal(52L, ((Shumway.Compiler.Ast.IntTerm)sol["G5"]!).Value);
        Assert.Equal(203L, ((Shumway.Compiler.Ast.IntTerm)sol["G6"]!).Value);
        Assert.Equal(877L, ((Shumway.Compiler.Ast.IntTerm)sol["G7"]!).Value);
    }

    [Fact]
    public void Setof_CommittingToTheFirstGroup_DoesNotPayForTheRest()
    {
        // The enumerator is lazy: it groups the recorded solutions once and
        // materialises a group's Witness-Bag only when backtracking demands
        // it. Eight elements is 40,320 solutions in 4,140 witness groups,
        // and a caller that commits to the first one used to wait for all
        // 4,140.
        //
        // Measured in HEAP CELLS, not seconds. The cell counter is a pure
        // function of the program — byte-identical across runs, machines and
        // builds — where a wall-clock ratio is not: this test failed once in
        // CI on a commit that had passed minutes earlier, which says
        // something about the machine's load and nothing about the engine.
        // The three paths separate by a wide margin (measured, x8):
        //
        //   findall alone                628,666 cells
        //   setof, first group only      825,719 cells   1.3x
        //   setof, every group           1,984,663 cells 3.2x
        //
        // so a 2x bound sits between "one group" and "all of them".
        var (e, output) = TimedEngine();
        e.ConsultString("""
            base :- length(L, 8), findall(x, permutation(L, L), _).
            first :- length(L, 8), setof(t, permutation(L, L), [t]), !.
            """);
        Assert.True(e.Query("base, first.").Success);   // warm both paths
        long bare = HeapCellsOf(e, output, "base");
        long grouped = HeapCellsOf(e, output, "first");
        Assert.True(grouped < bare * 2,
            $"first witness group allocated {grouped} cells, bare enumeration {bare}");
    }

    private static (PrologEngine Engine, System.IO.StringWriter Out) TimedEngine()
    {
        var w = new System.IO.StringWriter();
        return (new PrologEngine { Out = w }, w);
    }

    /// <summary>The heap cells one call of <paramref name="goal"/> allocates,
    /// read off time/1's own report — the counter <c>--alloc</c> benchmarking
    /// uses, deterministic by construction.</summary>
    private static long HeapCellsOf(
        PrologEngine engine, System.IO.StringWriter output, string goal)
    {
        var before = output.GetStringBuilder().Length;
        Assert.True(engine.Query($"time({goal}).").Success);
        string report = output.ToString().Substring(before);
        var m = System.Text.RegularExpressions.Regex.Match(
            report, @"([\d,]+) heap cells");
        Assert.True(m.Success, $"time/1 printed no cell count for {goal}: {report}");
        return long.Parse(m.Groups[1].Value.Replace(",", ""),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
