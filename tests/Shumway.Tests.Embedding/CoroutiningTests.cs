using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The coroutining library: freeze/2, frozen/2, dif/2, plus the core
/// term_attvars/2 builtin and the trial-unification wakeup hygiene it
/// depends on. The library rides the multifile verify_attributes/4 hook,
/// so it must coexist with CLP(FD) on one engine.
/// </summary>
public class CoroutiningTests
{
    private static PrologEngine Co()
    {
        var e = new PrologEngine();
        e.UseCoroutining();
        return e;
    }

    // ===== freeze/2 =====

    [Fact]
    public void Freeze_OnBoundVar_RunsImmediately()
    {
        var sol = Co().Query("X = 1, freeze(X, Y = ran).");
        Assert.True(sol.Success);
        Assert.Equal("ran", sol["Y"]!.ToString());
    }

    [Fact]
    public void Freeze_WakesWhenVarIsBound()
    {
        var sol = Co().Query("freeze(X, Y = woke), X = 1.");
        Assert.True(sol.Success);
        Assert.Equal("woke", sol["Y"]!.ToString());
    }

    [Fact]
    public void Freeze_UnboundVar_StaysSuspended()
    {
        // The goal would fail if run — but the variable is never bound.
        Assert.True(Co().Query("freeze(_X, fail).").Success);
    }

    [Fact]
    public void Freeze_FailingGoal_FailsTheBinding()
    {
        Assert.False(Co().Query("freeze(X, fail), X = 1.").Success);
    }

    [Fact]
    public void Freeze_MultipleGoals_RunInFreezeOrder()
    {
        var sol = Co().Query(
            "freeze(X, assertz(co_log(a))), freeze(X, assertz(co_log(b))), "
            + "X = 1, findall(K, co_log(K), Ks), Ks == [a, b].");
        Assert.True(sol.Success);
    }

    // ===== control constructs in a woken goal (issue #47) =====
    // The wake runner dispatches predicates; a frozen (A ; B) reached it as a
    // call of the nonexistent ;/2. Any goal call/1 takes, freeze/2 takes.

    [Fact]
    public void Freeze_DisjunctionWithCut_TheIssueQuery()
    {
        var sol = Co().Query("freeze(X, ((integer(X), !) ; X = Y)), X = a.");
        Assert.True(sol.Success);
        Assert.Equal("a", sol["Y"]!.ToString());
    }

    [Fact]
    public void Freeze_EachControlConstructWakes()
    {
        Assert.True(Co().Query(
            "freeze(A, (integer(A) ; atom(A))), A = foo.").Success);
        Assert.False(Co().Query(
            "freeze(A, (integer(A) ; A = b)), A = a.").Success);
        var ite = Co().Query(
            "freeze(A, (integer(A) -> R = int ; R = other)), A = foo, atom(R).");
        Assert.True(ite.Success);
        Assert.Equal("other", ite["R"]!.ToString());
        Assert.True(Co().Query(
            "freeze(A, (integer(A) *-> R = int ; R = other)), A = foo, R == other.").Success);
        Assert.True(Co().Query("freeze(A, \\+ integer(A)), A = foo.").Success);
        Assert.False(Co().Query("freeze(A, \\+ atom(A)), A = foo.").Success);
        Assert.True(Co().Query("freeze(A, not(integer(A))), A = foo.").Success);
        Assert.True(Co().Query("freeze(A, !), A = foo.").Success);
    }

    [Fact]
    public void Freeze_CutInWokenGoal_StaysLocalToIt()
    {
        // call semantics: the cut commits within the goal, no further.
        // ((!, fail) ; true) under call/1 fails; the CALLER's alternatives
        // survive a cut fired inside a woken goal.
        Assert.False(Co().Query("freeze(X, ((!, fail) ; true)), X = a.").Success);
        Assert.True(Co().Query(
            "( freeze(X, !), X = a, fail ; true ).").Success);
    }

    [Fact]
    public void Freeze_ControlConstructsInTheImmediateCaseToo()
    {
        // A bound variable runs the goal at once, through the same wrapper.
        Assert.True(Co().Query(
            "X = foo, freeze(X, (integer(X) ; atom(X))).").Success);
    }

    [Fact]
    public void When_DisjunctionGoalWakes()
    {
        Assert.True(Co().Query(
            "when(nonvar(X), (integer(X) ; atom(X))), X = foo.").Success);
    }

    [Fact]
    public void Freeze_VarVarAliasing_MigratesTheGoal()
    {
        var sol = Co().Query("freeze(X, Y = woke), X = Z, Z = 1.");
        Assert.True(sol.Success);
        Assert.Equal("woke", sol["Y"]!.ToString());
    }

    [Fact]
    public void Freeze_BacktrackingRestoresTheSuspension()
    {
        // The frozen fail kills both alternatives — the suspension must be
        // re-armed after the first binding is undone.
        var sols = Co().QueryAll("freeze(X, fail), ( X = 1 ; X = 2 ).").ToList();
        Assert.Empty(sols);
    }

    // ===== frozen/2 =====

    [Fact]
    public void Frozen_ReadsBackTheDelayedGoal()
    {
        var sol = Co().Query("freeze(X, foo(1)), frozen(X, G).");
        Assert.True(sol.Success);
        Assert.Equal("foo(1)", sol["G"]!.ToString());
    }

    [Fact]
    public void Frozen_PlainVar_IsTrue()
    {
        var sol = Co().Query("frozen(_X, G).");
        Assert.True(sol.Success);
        Assert.Equal("true", sol["G"]!.ToString());
    }

    // ===== dif/2 =====

    [Fact]
    public void Dif_GroundDifferent_Succeeds() =>
        Assert.True(Co().Query("dif(a, b).").Success);

    [Fact]
    public void Dif_GroundEqual_Fails() =>
        Assert.False(Co().Query("dif(a, a).").Success);

    [Fact]
    public void Dif_SameVariable_Fails() =>
        Assert.False(Co().Query("dif(X, X).").Success);

    [Fact]
    public void Dif_SuspendsAndFailsOnEqualBinding()
    {
        Assert.False(Co().Query("dif(X, a), X = a.").Success);
        Assert.True(Co().Query("dif(X, a), X = b.").Success);
    }

    [Fact]
    public void Dif_CompoundArgs_ResolveArgByArg()
    {
        // X = a leaves the pair unifiable only via Y = b; Y = c settles it.
        Assert.True(Co().Query("dif(f(X, Y), f(a, b)), X = a, Y = c.").Success);
        Assert.False(Co().Query("dif(f(X, Y), f(a, b)), X = a, Y = b.").Success);
    }

    [Fact]
    public void Dif_AliasingChain_FailsWhenIdentical()
    {
        Assert.False(Co().Query("dif(X, Y), X = Z, Y = Z.").Success);
    }

    [Fact]
    public void Dif_PrunesTheEqualAlternative()
    {
        var sols = Co().QueryAll("dif(X, 1), ( X = 1 ; X = 2 ).").ToList();
        var sol = Assert.Single(sols);
        Assert.Equal("2", sol["X"]!.ToString());
    }

    [Fact]
    public void Dif_RationalTree_ResolvesOnBinding()
    {
        // X = a makes the pair (a, f(a)) — not unifiable, dif holds.
        Assert.True(Co().Query("dif(X, f(X)), X = a.").Success);
    }

    // ===== term_attvars/2 (core builtin, no library needed) =====

    [Fact]
    public void TermAttvars_CollectsTheRealAttributedVariables()
    {
        var sol = new PrologEngine().Query(
            "put_attr(X, m, v), term_attvars(s(a, X, [X|_Y]), Vs), Vs = [W], W == X.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void TermAttvars_NoAttvars_GivesEmptyList()
    {
        var sol = new PrologEngine().Query("term_attvars(f(_X, g(_Y), 3), Vs).");
        Assert.True(sol.Success);
        Assert.Equal("[]", sol["Vs"]!.ToString());
    }

    // ===== trial-unification wakeup hygiene =====

    [Fact]
    public void NotUnifiable_DiscardsWakeupsFromTheFailedTrial()
    {
        // The trial binds X to 2 (queueing X's clpfd hook) before failing on
        // a \= b. The queued wakeup must die with the trial: were it run at
        // the next goal boundary, verify_attributes(clpfd, fd(5..9), 2)
        // would fail the query even though X was never really bound.
        var e = new PrologEngine();
        e.UseClpfd();
        Assert.True(e.Query(@"X in 5..9, f(X, a) \= f(2, b), X = 7.").Success);
    }

    // ===== coexistence with CLP(FD) =====

    [Fact]
    public void Coroutining_AndClpfd_ShareOneEngine()
    {
        var e = Co();
        e.UseClpfd();
        var sol = e.Query(
            "X in 1..5, freeze(Y, Z = woke), X #> 3, Y = go, label([X]).");
        Assert.True(sol.Success);
        Assert.Equal("4", sol["X"]!.ToString());
        Assert.Equal("woke", sol["Z"]!.ToString());
    }

    [Fact]
    public void Freeze_OnAClpfdVariable_BothHooksFire()
    {
        var e = Co();
        e.UseClpfd();
        var sol = e.Query("X in 1..3, freeze(X, Y = woke), X = 2.");
        Assert.True(sol.Success);
        Assert.Equal("woke", sol["Y"]!.ToString());
        // And the domain check still guards: a value outside fails.
        Assert.False(e.Query("A in 1..3, freeze(A, true), A = 9.").Success);
    }

    // ===== residual projection =====

    [Fact]
    public void Freeze_ProjectsAsResidualGoal()
    {
        var sol = Co().Query("freeze(X, foo(X)), copy_term(X, _C, Gs), Gs = [G].");
        Assert.True(sol.Success);
        Assert.StartsWith("freeze(", sol["G"]!.ToString());
    }

    [Fact]
    public void Dif_ProjectsAsDifGoal()
    {
        var sol = Co().Query("dif(X, a), copy_term(X, _C, Gs), Gs = [G].");
        Assert.True(sol.Success);
        Assert.StartsWith("dif(", sol["G"]!.ToString());
    }

    [Fact]
    public void Dif_ReSuspension_DoesNotAccumulateDuplicateConstraints()
    {
        // Regression: dif re-suspended on every partial binding without
        // retiring the previous incarnation, so the residual repeated the
        // same constraint many times. Here A's two bindings drive two
        // re-suspensions; the projection must still show exactly ONE dif.
        var sol = Co().Query(
            "dif(A, [C|B]), A = [[]|_], A = [B], "
            + "copy_term(C, _Cc, Gs), Gs = [G], length(Gs, 1).");
        Assert.True(sol.Success);
        Assert.StartsWith("dif(", sol["G"]!.ToString());
    }

    [Fact]
    public void Dif_MultiVariableConstraint_ProjectsOnce()
    {
        // A dif watching two variables must be shown once (owner-variable
        // rule), not once per watched variable.
        var sol = Co().Query(
            "dif(f(X, Y), f(a, b)), copy_term(f(X, Y), _C, Gs), length(Gs, 1).");
        Assert.True(sol.Success);
    }

    // ===== call_residue_vars/2 =====

    [Fact]
    public void CallResidueVars_CapturesTheSuspendedVariable()
    {
        // dif(X, a) leaves X constrained — it is the residue.
        var sol = Co().Query("call_residue_vars(dif(X, a), Vs), Vs = [V], V == X.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void CallResidueVars_NoConstraint_GivesEmptyList()
    {
        var sol = Co().Query("call_residue_vars(X = 1, Vs).");
        Assert.True(sol.Success);
        Assert.Equal("[]", sol["Vs"]!.ToString());
    }

    [Fact]
    public void CallResidueVars_ResolvedConstraint_LeavesNoResidue()
    {
        // dif holds and then X is bound to a different value — no residue left.
        var sol = Co().Query("call_residue_vars((dif(X, a), X = b), Vs).");
        Assert.True(sol.Success);
        Assert.Equal("[]", sol["Vs"]!.ToString());
    }

    [Fact]
    public void CallResidueVars_IgnoresPreExistingConstraints()
    {
        // The X constraint predates the call — only Y's residue is reported.
        var sol = Co().Query(
            "dif(X, a), call_residue_vars(dif(Y, b), Vs), Vs = [V], V == Y.");
        Assert.True(sol.Success);
    }

    // ===== ground/1 on attributed variables (core correctness) =====

    [Fact]
    public void Ground_TreatsAnAttributedVariableAsNonGround()
    {
        // put_attr makes X an attvar (unbound); a term holding it is not
        // ground. (Regression: ground/1 used to treat an attvar cell as a
        // bound value, so a frozen/clpfd variable read as ground.)
        Assert.False(new PrologEngine().Query("put_attr(X, m, v), ground(f(1, X)).").Success);
        Assert.True(new PrologEngine().Query("put_attr(X, m, v), \\+ ground(g(X)).").Success);
    }

    // ===== ?=/2 (core builtin, no library) =====

    [Fact]
    public void DecidedUnify_GroundDifferent_Decided() =>
        Assert.True(new PrologEngine().Query("a ?= b.").Success);

    [Fact]
    public void DecidedUnify_Identical_Decided() =>
        Assert.True(new PrologEngine().Query("f(X, b) ?= f(X, b).").Success);

    [Fact]
    public void DecidedUnify_TwoUnbound_Undecided() =>
        Assert.False(new PrologEngine().Query("X ?= Y.").Success);

    [Fact]
    public void DecidedUnify_PartiallyUnifiable_Undecided() =>
        Assert.False(new PrologEngine().Query("f(X) ?= f(a).").Success);

    [Fact]
    public void DecidedUnify_LeavesNoBinding()
    {
        var sol = new PrologEngine().Query("( a ?= b ; true ), X = kept.");
        Assert.True(sol.Success);
        Assert.Equal("kept", sol["X"]!.ToString());
    }

    // ===== unifiable/3 (core builtin, no library) =====

    [Fact]
    public void Unifiable_TwoStructures_ReportsTheBindings()
    {
        var sol = new PrologEngine().Query(
            "unifiable(f(X, b), f(a, Y), U), U = [X=a, Y=b].");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Unifiable_TwoVars_BindsOneToTheOther()
    {
        var sol = new PrologEngine().Query("unifiable(X, Y, U), U = [X=Y].");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Unifiable_Identical_EmptyUnifier()
    {
        var sol = new PrologEngine().Query("unifiable(a, a, U).");
        Assert.True(sol.Success);
        Assert.Equal("[]", sol["U"]!.ToString());
    }

    [Fact]
    public void Unifiable_CannotUnify_Fails() =>
        Assert.False(new PrologEngine().Query("unifiable(a, b, _U).").Success);

    [Fact]
    public void Unifiable_LeavesNoBinding()
    {
        // X must remain unbound after unifiable/3 reports X=a.
        var sol = new PrologEngine().Query("unifiable(X, a, _U), var(X).");
        Assert.True(sol.Success);
    }

    // ===== when/2 =====

    [Fact]
    public void When_Nonvar_FiresOnBinding()
    {
        var sol = Co().Query("when(nonvar(X), Y = woke), X = 1.");
        Assert.True(sol.Success);
        Assert.Equal("woke", sol["Y"]!.ToString());
    }

    [Fact]
    public void When_Nonvar_AlreadyBound_RunsImmediately()
    {
        var sol = Co().Query("X = 1, when(nonvar(X), Y = ran).");
        Assert.True(sol.Success);
        Assert.Equal("ran", sol["Y"]!.ToString());
    }

    [Fact]
    public void When_Ground_WaitsForEverySubterm()
    {
        // Must NOT fire until both X and Y are bound (the re-attach path).
        var sol = Co().Query(
            "when(ground(f(X, Y)), Z = g), X = 1, ( var(Z) -> Y = 2 ; throw(too_early) ).");
        Assert.True(sol.Success);
        Assert.Equal("g", sol["Z"]!.ToString());
    }

    [Fact]
    public void When_DecidedEquality_FiresWhenDecided()
    {
        var sol = Co().Query("when(?=(X, a), Y = dec), X = b.");
        Assert.True(sol.Success);
        Assert.Equal("dec", sol["Y"]!.ToString());
    }

    [Fact]
    public void When_Disjunction_FiresWhenEitherHolds()
    {
        var sol = Co().Query("when((nonvar(X) ; nonvar(Y)), Z = fired), Y = 1.");
        Assert.True(sol.Success);
        Assert.Equal("fired", sol["Z"]!.ToString());
    }

    [Fact]
    public void When_Conjunction_FiresWhenBothHold()
    {
        var sol = Co().Query(
            "when((nonvar(X), nonvar(Y)), Z = both), "
            + "X = 1, ( var(Z) -> Y = 2 ; throw(too_early) ).");
        Assert.True(sol.Success);
        Assert.Equal("both", sol["Z"]!.ToString());
    }

    [Fact]
    public void When_RunsGoalAtMostOnce()
    {
        var sol = Co().Query(
            "when((nonvar(X) ; nonvar(Y)), assertz(w_log(hit))), "
            + "X = 1, Y = 2, findall(H, w_log(H), Hs), Hs == [hit].");
        Assert.True(sol.Success);
    }

    [Fact]
    public void When_MalformedCondition_Throws()
    {
        var e = Co();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => e.Query("when(silly(X), true)."));
        Assert.Contains("domain_error", ex.Message);
    }

    // ===== when/2 and ?=, across an ALIASING =====
    // Binding one variable to another is not what releases a frozen goal, so
    // a condition over two variables needed its own look at the moment they
    // became one.

    [Fact]
    public void WhenDecidedFiresWhenTwoVariablesAreAliased()
    {
        var e = Co();
        e.ConsultString("""
            :- dynamic ran/1.
            probe(X, Y) :- when(?=(X, Y), assertz(ran(decided))).
            """);
        Assert.True(e.Query("probe(X, Y), X = Y, ran(decided).").Success);
    }

    [Fact]
    public void WhenDecidedStillFiresOnValuesEitherWay()
    {
        var e = Co();
        e.ConsultString("""
            :- dynamic ran/1.
            probe(X, Y) :- when(?=(X, Y), assertz(ran(decided))).
            """);
        // Equal, and unequal: both are decided.
        Assert.True(e.Query("probe(X, Y), X = a, Y = a, ran(decided).").Success);
        Assert.True(e.Query("probe(P, Q), P = a, Q = b, ran(decided).").Success);
    }

    [Fact]
    public void WhenDecidedStaysQuietWhileTheAnswerIsOpen()
    {
        // f(_) against f(_) unifies but is not identical: still genuinely
        // undecided, and claiming either way would be a guess.
        var e = Co();
        e.ConsultString("""
            :- dynamic ran/1.
            probe(X, Y) :- when(?=(X, Y), assertz(ran(decided))).
            """);
        Assert.False(e.Query("probe(X, Y), X = f(_), Y = f(_), ran(decided).").Success);
    }

    [Fact]
    public void AnAliasingDoesNotDuplicateTheResidual()
    {
        // The constraint watches both variables, so it is in both goal lists;
        // merging them on an aliasing used to leave the survivor carrying it
        // twice, and the top level printed it once per copy.
        var e = Co();
        var sol = e.Query("when(?=(X, Y), true), X = f(_), Y = f(_), "
                          + "copy_term(X, _, Attrs), length(Attrs, N), N =:= 1.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void AliasingStillFailsAnImpossibleDif()
    {
        // The alias check earns its keep for dif/2 as well: the merge must not
        // lose the suspension that has to fail right now.
        Assert.False(Co().Query("dif(A, B), A = B.").Success);
        Assert.True(Co().Query("dif(A, B), A = 1, B = 2.").Success);
    }

    // ===== dif/2 keeps ONE copy of a constraint it already has =====
    // The unifier of the two terms is what a dif really constrains, so terms
    // that unify the same way ARE the same constraint. Storing that form makes
    // equivalent posts recognisable, and a redundant one is then not posted at
    // all: the store stays small (less to re-check on every later binding) and
    // the top level shows the constraint once.

    /// <summary>An engine that can COUNT the live dif suspensions a variable
    /// carries: the store measured rather than described.</summary>
    private static PrologEngine CoCounting()
    {
        var e = Co();
        e.ConsultString("""
            tally((A, B), N0, N) :- !, tally(A, N0, N1), tally(B, N1, N).
            tally('$dif_wake'(dif_c(_, _, Alive)), N0, N) :-
                !, ( Alive == dead -> N = N0 ; N is N0 + 1 ).
            tally(_, N, N).
            live(V, N) :- frozen(V, G), tally(G, 0, N).
            """);
        return e;
    }

    [Fact]
    public void EquivalentDifsCollapseToOne()
    {
        // The reported case: five posts of one constraint, one residual.
        var e = CoCounting();

        var five = e.Query(
            "dif(X, Y), dif(Y, X), dif(-X, -Y), dif(-Y, -X), dif(X, Y), live(X, N).");
        Assert.True(five.Success);
        Assert.Equal(1L, ((Shumway.Compiler.Ast.IntTerm)five["N"]!).Value);

        // Symmetry alone is enough to recognise a duplicate.
        var two = e.Query("dif(X, Y), dif(Y, X), live(X, N).");
        Assert.True(two.Success);
        Assert.Equal(1L, ((Shumway.Compiler.Ast.IntTerm)two["N"]!).Value);
    }

    [Fact]
    public void DifferentConstraintsAreAllKept()
    {
        // The pruning must not reach anything that is not the same constraint:
        // X \= f(Y) and X \= g(Y) share their variables and nothing else.
        var e = CoCounting();
        var sol = e.Query("dif(X, f(Y)), dif(X, g(Y)), live(X, N).");
        Assert.True(sol.Success);
        Assert.Equal(2L, ((Shumway.Compiler.Ast.IntTerm)sol["N"]!).Value);
    }

    [Fact]
    public void ADifOverOneArgumentReducesToThatArgument()
    {
        // dif(-X, -Y) unifies by {X = Y} alone, so it IS dif(X, Y): one pair in
        // the unifier means the disjunction has a single disequality.
        // ONE residual, and its arguments are the plain variables — before
        // this it read back as dif(-X, -Y), the shape as posted.
        Assert.True(Co().Query(
            "dif(-X, -Y), copy_term(X-Y, _, Gs), "
          + @"Gs = [dif(A, B)], var(A), var(B), A \== B.").Success);
    }

    [Fact]
    public void ADifOverSwappedArgumentsReducesToo()
    {
        // f(X,Y) vs f(Y,X) decomposes to (X\=Y ; Y\=X) — the same disequality
        // twice, so it collapses.
        Assert.True(Co().Query(
            "dif(f(X,Y), f(Y,X)), copy_term(X-Y, _, Gs), "
          + @"Gs = [dif(A, B)], var(A), var(B), A \== B.").Success);
    }

    [Fact]
    public void ADifOverTwoArgumentsDoesNotReduce()
    {
        // f(X,Y) vs f(A,B) is X\=A OR Y\=B — a real disjunction, and not any
        // one dif. It must be kept whole, or the constraint would be wrong.
        var e = Co();
        Assert.True(e.Query("dif(f(X,Y), f(A,B)), X = A, Y = 1, B = 2.").Success);
        Assert.False(e.Query("dif(f(X,Y), f(A,B)), X = A, Y = B.").Success);
    }

    [Fact]
    public void OneTrialAnswersBothQuestionsAboutADif()
    {
        // Whether to suspend, and what the constraint reduces to, come from the
        // SAME trial unification: posting a dif never unifies twice. The pair
        // names the caller's own variables — it is the cells, not a copy.
        // Which of the two the trial happened to bind decides the order here;
        // '$dif_canon' orients it afterwards. What matters is that both members
        // ARE the caller's variables, not copies of them.
        var e = Co();
        Assert.True(e.Query(
            @"'$dif_check'(-X, -Y, Out, Canon), Out \== none, "
          + "Canon = (A - B), ( A == X, B == Y -> true ; A == Y, B == X ).").Success);

        // Two bindings are a disjunction, so there is no single pair to give.
        Assert.True(e.Query(
            "'$dif_check'(f(X,Y), f(A,B), _, Canon), Canon == no.").Success);

        // Terms that cannot unify decide the disequality outright.
        Assert.True(e.Query("'$dif_check'(a, b, Out, Canon), Out == none, Canon == no.").Success);

        // Identical terms make it fail, which is how dif/2 fails.
        Assert.False(e.Query("'$dif_check'(a, a, _, _).").Success);
    }

    [Fact]
    public void ACutInOneFrozenGoalLeavesTheOthersAlone()
    {
        // Two goals frozen on the same variable are two goals. A cut in the
        // second used to prune the first one's alternatives as well, so an
        // answer the program had was never reported: an incompleteness, not a
        // cosmetic difference.
        var e = Co();
        Assert.True(e.Query(
            "findall(X-Y, (freeze(X, (Y = 1 ; Y = 2)), freeze(X, !), X = c ; X = end), L), "
            + "L = [c-1, c-2, end-_].").Success);
    }

    [Fact]
    public void ACutStillCutsInsideItsOwnFrozenGoal()
    {
        // The other half of the same rule: within ONE frozen goal the cut is
        // that goal's own, so it does commit to the first alternative.
        var e = Co();
        Assert.True(e.Query(
            "findall(Y, (freeze(X, ((Y = 1 ; Y = 2), !)), X = c), L), L == [1].").Success);
        // Without the cut, both alternatives survive.
        Assert.True(e.Query(
            "findall(Y, (freeze(X, (Y = 1 ; Y = 2)), X = c), L), L == [1, 2].").Success);
    }

    [Fact]
    public void HowTheGoalsAreStoredIsNotVisible()
    {
        // The store keeps each frozen goal apart from the next. Nothing that
        // reads the goals back may show how.
        var e = Co();
        Assert.True(e.Query("freeze(X, a), freeze(X, b), frozen(X, G), G == (a, b).").Success);
        Assert.True(e.Query("freeze(Y, (p, q)), frozen(Y, G), G == (p, q).").Success);
        Assert.True(e.Query("freeze(X, G0), frozen(X, G), G == G0, var(G).").Success);
        // Two frozen goals project as two freeze/2 constraints, as before.
        Assert.True(e.Query(
            "freeze(X, a), freeze(X, b), copy_term(X, _, As), "
            + "As = [freeze(_, a), freeze(_, b)].").Success);
    }

    [Fact]
    public void CollapsingDoesNotWeakenTheConstraint()
    {
        // The point of the exercise is fewer copies, not a weaker dif: the one
        // that survives still fails the moment the terms become identical, and
        // still lets a genuinely different pair through.
        var e = Co();
        Assert.False(e.Query("dif(X, Y), dif(-X, -Y), X = Y.").Success);
        Assert.False(e.Query("dif(f(X,Y), f(Y,X)), X = Y.").Success);
        Assert.True(e.Query("dif(X, Y), dif(-X, -Y), X = 1, Y = 2.").Success);
    }
}
