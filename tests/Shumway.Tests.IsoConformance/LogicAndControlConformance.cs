using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §7.8 / §8.15 — control constructs and exception
/// handling. Covers <c>\\+/1</c>, <c>once/1</c>, <c>repeat/0</c>,
/// <c>(;)/2</c>, <c>(-&gt;)/2</c>, <c>(-&gt; ; ; )/2</c>,
/// <c>!/0</c> (cut), <c>call/1..7</c>, <c>catch/3</c> and
/// <c>throw/1</c>. The cut + conjunction + disjunction families
/// share <see cref="ControlAndListsConformance"/> (Phase 1) for
/// happy-path coverage; this file focuses on cases the standard
/// pins explicitly: cut semantics inside <c>\\+</c> / <c>once</c>,
/// the <c>catch</c>/<c>throw</c> match-or-rethrow rule, and the
/// ISO errors each construct's contract requires.
/// </summary>
public class LogicAndControlConformance
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ---------- \+/1 (negation as failure) ----------

    [Fact]
    public void NotProvable_FailsForProvable()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("\\+ true.").Success);
    }

    [Fact]
    public void NotProvable_SucceedsForUnprovable()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("\\+ fail.").Success);
    }

    [Fact]
    public void NotProvable_DoesNotKeepBindings()
    {
        // ISO §7.8.5: bindings made inside the negated goal do not
        // survive. bind_and_fail/1 binds its arg then fails; \+ over
        // it succeeds, the binding is unwound, the outer X=2 takes.
        var e = new PrologEngine();
        e.ConsultString("bind_and_fail(X) :- X = 1, fail.");
        var sol = e.Query("\\+ bind_and_fail(X), X = 2.");
        Assert.True(sol.Success);
        Assert.Equal(Int(2), sol["X"]);
    }

    [Fact]
    public void NotProvable_SpacedParenConjunction_ParsesAsOperatorArg()
    {
        // ISO §6.3.3 / §6.4.7 function-call disambiguation (chunk 149):
        // `\+ (G1, G2)` with whitespace before `(` is the prefix operator
        // `\+` applied to the parenthesised conjunction `(G1, G2)` — NOT
        // the function-call `\+/2`. So `\+ (fail, true)` negates a goal
        // that fails and therefore succeeds, while `\+ (true, true)`
        // negates a goal that succeeds and therefore fails.
        var e = new PrologEngine();
        Assert.True(e.Query("\\+ (fail, true).").Success);
        Assert.False(e.Query("\\+ (true, true).").Success);
    }

    [Fact]
    public void NotProvable_AdjacentParen_IsFunctionCallShape()
    {
        // The flip side: `\+(fail, true)` with NO whitespace is the
        // function-call notation `\+/2`, which is undefined → a catchable
        // existence_error(procedure, _). (The indicator IS `\+/2`, but the
        // literal `\+/2` can't be written in source — `\+/` is one graphic
        // token under maximal munch — so the catcher leaves the PI a var.)
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(\\+(fail, true), error(existence_error(procedure, _), _), true).").Success);
    }

    [Fact]
    public void NotProvable_VarGoal_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(\\+ _G, error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    // ---------- once/1 ----------

    [Fact]
    public void Once_TakesFirstSolution()
    {
        // once(G) commits to the first solution — no backtracking
        // into G even if more solutions exist.
        var e = new PrologEngine();
        var sols = e.QueryAll("once(member(X, [1,2,3])).").ToList();
        Assert.Single(sols);
        Assert.Equal(Int(1), sols[0]["X"]);
    }

    [Fact]
    public void Once_FailsIfGoalFails()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("once(fail).").Success);
    }

    // ---------- repeat/0 ----------

    [Fact]
    public void Repeat_SuccessThenFailure_Loops()
    {
        // The classic failure-driven loop idiom: repeat, body, !.
        // Each iteration succeeds until body's cut commits.
        var e = new PrologEngine();
        // Use an asserted counter to keep state across re-runs.
        e.ConsultString(":- dynamic seen/1.");
        Assert.True(e.Query(
            "assertz(seen(0)), "
            + "( repeat, "
            + "  retract(seen(N0)), N is N0 + 1, assertz(seen(N)), "
            + "  N >= 3, ! ), "
            + "seen(F), F == 3.").Success);
    }

    // ---------- (-> ; ) if-then-else ----------

    [Fact]
    public void IfThenElse_ConditionTrue_RunsThen()
    {
        var e = new PrologEngine();
        var sol = e.Query("( true -> X = then ; X = else ).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("then"), sol["X"]);
    }

    [Fact]
    public void IfThenElse_ConditionFalse_RunsElse()
    {
        var e = new PrologEngine();
        var sol = e.Query("( fail -> X = then ; X = else ).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("else"), sol["X"]);
    }

    [Fact]
    public void IfThenElse_CommitsToFirstConditionSolution()
    {
        // If the condition has multiple solutions, -> commits to the
        // first one. Backtracking past the if-then-else does not
        // explore further condition solutions.
        var e = new PrologEngine();
        var sols = e.QueryAll(
            "( member(X, [1, 2, 3]) -> Y = first ; Y = none ).").ToList();
        Assert.Single(sols);
        Assert.Equal(Int(1), sols[0]["X"]);
        Assert.Equal(Atom("first"), sols[0]["Y"]);
    }

    // ---------- !/0 (cut) ----------

    [Fact]
    public void Cut_RemovesChoicePoints()
    {
        var e = new PrologEngine();
        e.ConsultString("p(X) :- member(X, [1, 2, 3]), !.");
        var sols = e.QueryAll("p(X).").ToList();
        Assert.Single(sols);
        Assert.Equal(Int(1), sols[0]["X"]);
    }

    [Fact]
    public void Cut_DoesNotEscapeNotProvable()
    {
        // ISO §7.8.5: a cut inside \+ G has scope only over G, not
        // the surrounding context. With chunk 149 the parser's
        // adjacency rule lets us write the conjunction inline —
        // '\+ (member(_, [1]), !)' is now read as unary \+ applied
        // to the parenthesised conjunction, not as binary '\+'/2.
        var e = new PrologEngine();
        var sols = e.QueryAll(
            "(\\+ (member(_, [1]), !) ; X = b).").ToList();
        // \+ (member(_, [1]), !) fails (the inner conjunction
        // succeeds — there is a member of [1]), so the first
        // disjunct dies; the second yields X=b.
        Assert.Single(sols);
        Assert.Equal(Atom("b"), sols[0]["X"]);
    }

    // ---------- call/N ----------

    [Fact]
    public void Call1_RunsAtom()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("call(true).").Success);
        Assert.False(e.Query("call(fail).").Success);
    }

    [Fact]
    public void Call1_RunsCompound()
    {
        var e = new PrologEngine();
        var sol = e.Query("call(=(X, hello)).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["X"]);
    }

    [Fact]
    public void CallN_AppendsArgs()
    {
        // call(member, X, L) ≡ member(X, L).
        var e = new PrologEngine();
        var sol = e.Query("call(member, X, [a, b, c]).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("a"), sol["X"]);
    }

    [Fact]
    public void Call_VarGoal_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(call(_G), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void Call_NonCallableGoal_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(call(123), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("callable"), sol["T"]);
    }

    // ---------- catch/3 and throw/1 ----------

    [Fact]
    public void Catch_CatchesMatching()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(throw(my_error(42)), my_error(N), Caught = N).");
        Assert.True(sol.Success);
        Assert.Equal(Int(42), sol["Caught"]);
    }

    [Fact]
    public void Catch_NonMatching_Rethrows()
    {
        // If the ball doesn't unify with the catcher, the throw
        // propagates outward.
        var e = new PrologEngine();
        var sol = e.Query(
            "catch( "
            + "  catch(throw(inner), outer, X = wrong), "
            + "  inner, "
            + "  X = caught_outer).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("caught_outer"), sol["X"]);
    }

    [Fact]
    public void Catch_SuccessfulGoal_PassesThrough()
    {
        // No exception → catch is transparent, the goal's bindings
        // flow out.
        var e = new PrologEngine();
        var sol = e.Query("catch(X = 1, _, X = 99).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["X"]);
    }

    [Fact]
    public void Throw_VarBall_RaisesInstantiationError()
    {
        // ISO §7.8.10.3.a: a var ball is instantiation_error.
        var e = new PrologEngine();
        var sol = e.Query("catch(throw(_X), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void Catch_RecoveryBindingsPersist()
    {
        // The recovery goal's bindings leak out of catch/3.
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(throw(oops), oops, R = recovered).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("recovered"), sol["R"]);
    }

    [Fact]
    public void Catch_GoalSideEffects_Persist()
    {
        // assertz inside catch's Goal must survive even if a later
        // throw fires the recovery.
        var e = new PrologEngine();
        e.ConsultString(":- dynamic logged/1.");
        Assert.True(e.Query(
            "catch( ( assertz(logged(1)), throw(oops) ), "
            + "      oops, true), "
            + "logged(1).").Success);
    }
}
