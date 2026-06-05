using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A call to an undefined predicate raises the ISO
/// <c>existence_error(procedure, Name/Arity)</c> when (and only when) the
/// call is reached — rather than failing the link up front. This makes
/// the error catchable, keeps it consistent however the predicate is
/// reached, and means an undefined predicate in one part of a program no
/// longer breaks unrelated queries.
/// </summary>
public class UndefinedPredicateTests
{
    [Fact]
    public void DirectQuery_RaisesExistenceError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<PrologRuntimeException>(
            () => engine.Query("no_such_pred(1)."));
        Assert.Equal("existence_error", ex.Kind);
        Assert.Equal("no_such_pred/1", ex.Detail);
    }

    [Fact]
    public void UndefinedGoal_IsCatchable()
    {
        // The whole point of deferring the error to call time: catch/3
        // can now intercept it as an ordinary ISO error/2 ball.
        var engine = new PrologEngine();
        var sol = engine.Query("catch(no_such_pred(1), E, true).");
        Assert.True(sol.Success);
        var err = Assert.IsType<CompoundTerm>(sol["E"]);
        Assert.Equal("error", err.Functor);
        var inner = Assert.IsType<CompoundTerm>(err.Args[0]);
        Assert.Equal("existence_error", inner.Functor);
        Assert.Equal(new AtomTerm("procedure"), inner.Args[0]);
    }

    [Fact]
    public void ProcedureIndicator_IsCompoundNotAtom()
    {
        // ISO §7.12.2: the culprit is the COMPOUND '/'(Name, Arity), not an
        // atom literally named "Name/Arity". A specific catcher must unify.
        var engine = new PrologEngine();
        var sol = engine.Query("catch(no_such_pred(1, 2), error(existence_error(procedure, PI), _), true).");
        Assert.True(sol.Success);
        var pi = Assert.IsType<CompoundTerm>(sol["PI"]);   // compound, not AtomTerm
        Assert.Equal("/", pi.Functor);
        Assert.Equal(2, pi.Args.Length);
        Assert.Equal(new AtomTerm("no_such_pred"), pi.Args[0]);
        Assert.Equal(new IntTerm(2), pi.Args[1]);
    }

    [Fact]
    public void ProcedureIndicator_SpecificCatcherUnifies()
    {
        // The whole reason the compound matters: a catcher naming the exact
        // indicator now intercepts the error.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "catch(no_such_pred(1, 2, 3), error(existence_error(procedure, no_such_pred/3), _), true).").Success);
        // And a DIFFERENT indicator does NOT match: the inner catch rethrows
        // the ball, which the outer var catcher then intercepts.
        Assert.True(engine.Query(
            "catch( catch(no_such_pred(1, 2, 3), error(existence_error(procedure, other/9), _), fail), _, true ).").Success);
    }

    [Fact]
    public void UndefinedInClauseBody_RaisesWhenClauseRuns()
    {
        var engine = new PrologEngine();
        engine.ConsultString("run :- helper(1).\n");
        var ex = Assert.Throws<PrologRuntimeException>(() => engine.Query("run."));
        Assert.Equal("existence_error", ex.Kind);
        Assert.EndsWith("helper/1", ex.Detail);
    }

    [Fact]
    public void UndefinedPredicateElsewhere_DoesNotBreakUnrelatedQueries()
    {
        // 'broken' references an undefined predicate, but querying the
        // well-formed 'ok' must still succeed — an undefined predicate no
        // longer fails the whole program's link.
        var engine = new PrologEngine();
        engine.ConsultString("broken :- gone(1).\nok.\n");
        Assert.True(engine.Query("ok.").Success);
    }

    [Fact]
    public void UndefinedGoal_NotReached_NoError()
    {
        // The undefined goal sits in an unreached disjunction branch — the
        // first branch yields the solution, so the error never fires.
        var engine = new PrologEngine();
        var sol = engine.Query("( true ; never_defined(1) ).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void DeclaredEmptyDynamicPredicate_FailsNotErrors()
    {
        // A declared-but-empty dynamic predicate is *defined* — it just
        // has no clauses — so a call to it fails rather than raising
        // existence_error.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic maybe/1.\n");
        Assert.False(engine.Query("maybe(x).").Success);
    }
}
