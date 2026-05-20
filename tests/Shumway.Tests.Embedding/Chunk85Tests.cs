using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 85 — <c>catch/3</c> runs in the live engine. It used to spawn an
/// isolated peer sub-engine for the guarded goal (re-parsing the prelude,
/// copying every module) and took only the goal's first solution; a side
/// effect the goal performed was lost with the sub-engine.
///
/// <para>Chunk 85 makes <c>catch/3</c> a compile transform. The guarded
/// goal is spliced into a synthesised goal helper, so it compiles inline
/// and runs with full backtracking in the live engine; <c>'$catch_begin'</c>
/// pushes a catch frame snapshotting the machine, and a thrown ball that
/// unifies with the catcher rolls the engine back to that frame and runs
/// the recovery goal. The catch-frame stack is reversible through the
/// extra trail, so backtracking — into the guarded goal, or past the whole
/// catch — restores it exactly.</para>
/// </summary>
public class Chunk85Tests
{
    [Fact]
    public void Catch_RecoversFromAMatchingThrow()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("catch(throw(boom), boom, true).").Success);
    }

    [Fact]
    public void Catch_GoalSucceeds_RecoveryNotConsulted()
    {
        // No throw: the goal's solution flows through and the recovery
        // (here a failing goal) is never run.
        var engine = new PrologEngine();
        Assert.True(engine.Query("catch(X = 7, _, fail), X == 7.").Success);
    }

    [Fact]
    public void Catch_GoalFails_CatchFails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("catch(fail, _, true).").Success);
    }

    [Fact]
    public void Catch_MismatchedCatcher_Propagates()
    {
        // The ball doesn't unify with the catcher, so catch/3 does not
        // intercept it — the throw propagates out of the query.
        var engine = new PrologEngine();
        Assert.Throws<ShumwayPrologException>(
            () => engine.Query("catch(throw(boom), other, true)."));
    }

    [Fact]
    public void Catch_CatcherUnifiesWithTheBall()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("catch(throw(err(42)), err(N), true), N == 42.").Success);
    }

    [Fact]
    public void Catch_RecoveryRuns_ContinuationSeesItsBindings()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("catch(throw(x), x, Y = recovered), Y == recovered.").Success);
    }

    [Fact]
    public void Catch_IsTransparentToTheGoalsSolutions()
    {
        // catch/3 backtracks into the guarded goal — every solution of
        // member/2 reaches the caller, not just the first.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(X, catch(member(X, [1,2,3]), _, fail), L), L == [1,2,3].").Success);
    }

    [Fact]
    public void Catch_NestedInnerMismatch_OuterCatches()
    {
        // The inner catcher (shallow) doesn't match, so the ball escapes
        // the inner catch and is caught by the outer one.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "catch(catch(throw(deep), shallow, true), deep, R = outer), R == outer.").Success);
    }

    [Fact]
    public void Catch_ThrowFromRecovery_CaughtByOuter()
    {
        // The inner recovery itself throws; the outer catch handles it.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "catch(catch(throw(a), a, throw(b)), b, true).").Success);
    }

    [Fact]
    public void Catch_GoalSideEffects_PersistAcrossACaughtThrow()
    {
        // The defining in-engine proof: assertz/1 runs in the live engine
        // and is not backtrackable, so it survives even the rollback a
        // caught throw performs.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic logged/1.");
        Assert.True(engine.Query(
            "catch((assertz(logged(a)), throw(stop)), stop, true).").Success);
        Assert.True(engine.Query("logged(a).").Success);
    }

    [Fact]
    public void Catch_CaughtThrow_UndoesTheGoalsBindings()
    {
        // X is bound inside the guarded goal, but a caught throw rolls the
        // machine back — so X is unbound again when the recovery runs.
        var engine = new PrologEngine();
        Assert.True(engine.Query("catch((X = 1, throw(x)), x, true), var(X).").Success);
    }

    [Fact]
    public void Catch_CatchesATranslatedRuntimeError()
    {
        // A Core runtime error (division by zero) is promoted to its ISO
        // error/2 term, which the catcher matches.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "catch(_ is 1 / 0, error(evaluation_error(zero_divisor), _), true).").Success);
    }

    [Fact]
    public void Catch_RecoveryIsBacktrackable()
    {
        // The recovery goal keeps its choice points: catch/3 yields each
        // of member/2's solutions.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(Y, catch(throw(x), x, member(Y, [a,b])), L), L == [a,b].").Success);
    }

    [Fact]
    public void Catch_WithAVariableGoal_StillWorks()
    {
        // A goal that is a variable at compile time isn't rewritten — it
        // falls through to the runtime catch/3 builtin.
        var engine = new PrologEngine();
        Assert.True(engine.Query("G = throw(v), catch(G, v, true).").Success);
    }

    [Fact]
    public void Catch_BacktrackingPastTheCatch_RestoresTheFrameStack()
    {
        // After the first solution the catch frame is deactivated; on
        // backtracking into the guarded goal it re-activates, so a later
        // throw is still caught. member then X > 1 forces that re-entry.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(X, catch((member(X, [1,2,3]), (X =:= 2 -> throw(hit) ; true)), " +
            "hit, X = caught), L), L == [1, caught].").Success);
    }
}
