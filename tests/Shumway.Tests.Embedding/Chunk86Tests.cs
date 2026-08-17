using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 86 — <c>call/1..7</c> runs in the live engine and is fully
/// backtrackable. It used to spawn an isolated peer sub-engine for the
/// goal and take only its first solution — a Phase-1 limitation that is
/// not ISO and silently truncates any backtracking through a call.
///
/// <para>Chunk 86 makes the interpreter dispatch the runtime goal
/// directly: <c>call(G)</c> decodes <c>G</c> — appending <c>call/N</c>'s
/// extra arguments — and tail-jumps to the goal's predicate, so it runs
/// with real choice points and the call's continuation flows on success.
/// A control construct in a runtime goal (<c>,/2</c>, <c>;/2</c>,
/// <c>-&gt;/2</c>, <c>\+/1</c>) routes to a plainly-named prelude helper.
/// Side effects persist; there is no sub-engine.</para>
/// </summary>
public class Chunk86Tests
{
    private static string Neg(string inner) => "call(" + (char)92 + "+ " + inner + ")";

    [Fact]
    public void Call_RunsAGoal()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("call(true).").Success);
    }

    [Fact]
    public void Call_FailingGoal_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("call(fail).").Success);
    }

    [Fact]
    public void Call_OfAVariableGoal()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("G = true, call(G).").Success);
    }

    [Fact]
    public void Call_IsTransparentToBacktracking()
    {
        // The defining fix: call/1 yields every solution of its goal, not
        // just the first.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(X, call(member(X, [1,2,3])), L), L == [1,2,3].").Success);
    }

    [Fact]
    public void Call_OfAVariableGoal_IsBacktrackable()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "G = member(X, [1,2,3]), findall(X, call(G), L), L == [1,2,3].").Success);
    }

    [Fact]
    public void Call2_AppendsAnArgument()
    {
        // call(member(X), [1,2,3]) is member(X, [1,2,3]).
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(X, call(member(X), [1,2,3]), L), L == [1,2,3].").Success);
    }

    [Fact]
    public void Call3_AppendsTwoArguments()
    {
        // call(=, hi, hi) is =(hi, hi).
        var engine = new PrologEngine();
        Assert.True(engine.Query("call(=, hi, hi).").Success);
    }

    [Fact]
    public void Call_ConjunctionGoal()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("call((X = 1, Y = 2)), X == 1, Y == 2.").Success);
    }

    [Fact]
    public void Call_DisjunctionGoal_IsBacktrackable()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "findall(X, call((X = 1 ; X = 2)), L), L == [1,2].").Success);
    }

    [Fact]
    public void Call_IfThenElse_TakesTheThenBranch()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "call((1 < 2 -> R = yes ; R = no)), R == yes.").Success);
    }

    [Fact]
    public void Call_IfThenElse_TakesTheElseBranch()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "call((1 > 2 -> R = yes ; R = no)), R == no.").Success);
    }

    [Fact]
    public void Call_NegationOfAFailingGoal_Succeeds()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(Neg("fail") + ".").Success);
    }

    [Fact]
    public void Call_NegationOfASucceedingGoal_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query(Neg("true") + ".").Success);
    }

    [Fact]
    public void Call_GoalSideEffects_PersistInTheLiveEngine()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic logged/1.");
        Assert.True(engine.Query("call((assertz(logged(here)), true)).").Success);
        Assert.True(engine.Query("logged(here).").Success);
    }

    [Fact]
    public void Call_OfALocalUserPredicate_IsBacktrackable()
    {
        // A module-local predicate is linked under a mangled functor; the
        // runtime call resolves it by its bare name.
        var engine = new PrologEngine();
        engine.ConsultString("""
            colour(red).
            colour(green).
            colour(blue).
            """);
        Assert.True(engine.Query(
            "findall(C, call(colour(C)), L), L == [red, green, blue].").Success);
    }

    [Fact]
    public void Call_NestedCall()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("call(call(call(true))).").Success);
    }

    [Fact]
    public void Call_BuiltinGoal()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("call(atom(foo)).").Success);
        Assert.False(engine.Query("call(atom(123)).").Success);
    }
}
