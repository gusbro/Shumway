using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 81 — residual-constraint projection: <c>copy_term/3</c> and the
/// <c>attribute_goals/4</c> hook. <c>copy_term(Term, Copy, Goals)</c>
/// copies <c>Term</c> with fresh plain variables and, for every
/// attributed variable in it, collects the goals each module's
/// <c>attribute_goals(Module, AttrValue, Var, Goals)</c> hook produces —
/// already expressed over <c>Copy</c>'s variables. It is what lets a
/// constrained variable's state be turned back into goals (for the
/// top-level to print, or to round-trip a constraint).
///
/// <para><c>attribute_goals/4</c> is pre-declared <c>:- dynamic</c> by
/// the prelude, so a user program simply writes its clauses (no
/// declaration needed) and a program with no hook still links and
/// yields empty residual goals.</para>
/// </summary>
public class Chunk81Tests
{
    [Fact]
    public void CopyTerm3_WithoutAttributedVariables_YieldsNoGoals()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "copy_term(foo(A, B), Copy, Goals), Copy = foo(_, _), Goals == [].").Success);
    }

    [Fact]
    public void CopyTerm3_ProjectsTheAttributeGoals()
    {
        var engine = new PrologEngine();
        engine.ConsultString("attribute_goals(dom, D, V, [in(V, D)]).");
        Assert.True(engine.Query(
            "put_attr(X, dom, [1,2,3]), copy_term(X, _, Goals), " +
            "Goals = [in(_, [1,2,3])].").Success);
    }

    [Fact]
    public void CopyTerm3_ResidualGoalsReferenceTheCopyVariable()
    {
        // The variable in the projected goal IS the copy variable —
        // checked with ==, which holds only for the *same* variable.
        var engine = new PrologEngine();
        engine.ConsultString("attribute_goals(dom, D, V, [in(V, D)]).");
        Assert.True(engine.Query(
            "put_attr(X, dom, [1,2,3]), copy_term(X, Copy, Goals), " +
            "Goals == [in(Copy, [1,2,3])].").Success);
    }

    [Fact]
    public void CopyTerm3_TheCopyIsAPlainVariable_NotAttributed()
    {
        var engine = new PrologEngine();
        engine.ConsultString("attribute_goals(dom, D, V, [in(V, D)]).");
        Assert.True(engine.Query(
            "put_attr(X, dom, [1,2,3]), copy_term(X, Copy, _), " +
            "var(Copy), \\+ attvar(Copy).").Success);
    }

    [Fact]
    public void CopyTerm3_HooklessAttribute_ContributesNoGoals()
    {
        // The attribute's module defines no attribute_goals/4 clause —
        // it contributes nothing, and copy_term/3 still succeeds.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "put_attr(X, m, 1), copy_term(X, _, Goals), Goals == [].").Success);
    }

    [Fact]
    public void CopyTerm3_CollectsGoalsFromEveryModule()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            "attribute_goals(a, Av, V, [ga(V, Av)]).\n" +
            "attribute_goals(b, Bv, V, [gb(V, Bv)]).");
        Assert.True(engine.Query(
            "put_attr(X, a, 1), put_attr(X, b, 2), copy_term(X, _, Goals), " +
            "member(ga(_, 1), Goals), member(gb(_, 2), Goals).").Success);
    }

    [Fact]
    public void CopyTerm3_FindsAttributedVariablesInsideCompounds_PreservingSharing()
    {
        // X appears twice in pair(X, X); the copy keeps the sharing and
        // the single attributed variable projects exactly one goal.
        var engine = new PrologEngine();
        engine.ConsultString("attribute_goals(dom, D, V, [in(V, D)]).");
        Assert.True(engine.Query(
            "put_attr(X, dom, [1,2]), copy_term(pair(X, X), Copy, Goals), " +
            "Copy = pair(C1, C2), C1 == C2, Goals == [in(C1, [1,2])].").Success);
    }

    [Fact]
    public void CopyTerm3_TheProjectedGoalIsRunnable()
    {
        // The residual goal is a real goal: bind the copy and call it,
        // and it checks the value against the captured domain.
        var engine = new PrologEngine();
        engine.ConsultString(
            "attribute_goals(dom, D, V, [in(V, D)]).\n" +
            "in(V, D) :- member(V, D).");
        Assert.True(engine.Query(
            "put_attr(X, dom, [1,2,3]), copy_term(X, Copy, [G]), Copy = 2, call(G).").Success);
        Assert.False(engine.Query(
            "put_attr(X, dom, [1,2,3]), copy_term(X, Copy, [G]), Copy = 9, call(G).").Success);
    }
}
