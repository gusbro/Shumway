using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 83 — <c>findall/3</c> runs in the live engine. It used to spawn
/// an isolated peer sub-engine per call (re-parsing the prelude, copying
/// every module, recompiling and relinking the whole program), and a
/// side effect the goal performed — notably <c>assertz/1</c> — was lost
/// when the sub-engine was discarded.
///
/// <para>Chunk 83 makes <c>findall/3</c> with a callable goal a compile
/// transform instead: <c>findall(T, G, L)</c> becomes
/// <c>('$findall_push', G, '$findall_record'(T), fail ; '$findall_collect'(L))</c>.
/// G is spliced in as an ordinary body goal, so it compiles inline with
/// real choice points and runs in the live engine — the <c>fail</c>
/// enumerates its solutions, a per-engine frame stack collects the
/// templates off-heap, and side effects persist. A bare-variable goal
/// still falls through to the runtime builtin.</para>
/// </summary>
public class Chunk83Tests
{
    [Fact]
    public void Findall_CollectsEverySolution()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("findall(X, member(X, [1,2,3]), L), L == [1,2,3].").Success);
    }

    [Fact]
    public void Findall_NoSolutions_YieldsEmptyList()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("findall(X, fail, L), L == [].").Success);
    }

    [Fact]
    public void Findall_GoalSideEffects_PersistInTheLiveEngine()
    {
        // The defining proof of chunk 83: an assertz inside the goal runs
        // in the live engine, so it is still visible after findall/3
        // returns. Under the old sub-engine runner it was lost.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic collected/1.");
        Assert.True(engine.Query(
            "findall(X, (member(X, [a,b,c]), assertz(collected(X))), _).").Success);
        Assert.True(engine.Query("collected(a).").Success);
        Assert.True(engine.Query("collected(b).").Success);
        Assert.True(engine.Query("collected(c).").Success);
    }

    [Fact]
    public void Findall_WithAConjunctionGoal_BacktracksAcrossTheComma()
    {
        // (member(X,...), X > 2) needs real backtracking across the comma
        // — when X > 2 fails it must retry member.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(X, (member(X, [1,2,3,4]), X > 2), L), L == [3,4].").Success);
    }

    [Fact]
    public void Findall_DoesNotLeakTheGoalsBindings()
    {
        // X is the template and is bound by the goal on every solution,
        // but findall must leave it unbound for the caller.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(X, member(X, [1,2,3]), _), var(X).").Success);
    }

    [Fact]
    public void Findall_OverAUserPredicate()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public color/1.
            color(red).
            color(green).
            color(blue).
            """);
        Assert.True(engine.Query(
            "findall(C, color(C), L), L == [red, green, blue].").Success);
    }

    [Fact]
    public void Findall_NestsCorrectly()
    {
        // The inner findall pushes and pops its own frame for each
        // solution of the outer goal.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(P, (member(X, [1,2]), findall(X, member(_, [a,b]), P)), R), " +
            "R == [[1,1],[2,2]].").Success);
    }

    [Fact]
    public void Findall_WithAVariableGoal_StillWorks()
    {
        // A bare-variable goal isn't rewritten — it falls through to the
        // runtime findall/3 builtin.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "G = member(Y, [1,2]), findall(Y, G, L), L == [1,2].").Success);
    }

    [Fact]
    public void Findall_RepeatedCalls_StayCorrect()
    {
        var engine = new PrologEngine();
        for (int i = 0; i < 5; i++)
            Assert.True(engine.Query("findall(N, member(N, [1,2,3]), [1,2,3]).").Success);
    }
}
