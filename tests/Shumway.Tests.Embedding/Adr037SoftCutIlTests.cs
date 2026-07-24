using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-037 — the inline <c>( Cond *-> Then ; Else )</c> soft cut compiled to
/// Tier-1 IL. The <c>soft_cut</c> opcode emits <c>engine.SoftCutToLevel</c>; the
/// ELSE choice point is an IL choice point, so the deterministic case discards it
/// (via <c>Cut</c>, which also drops the <c>_ilCpStack</c> entry) and the
/// non-deterministic case neutralises the IL CP's resume delegate (so backtracking
/// pops it and fails through instead of running Else). Every test runs PROMOTED
/// (threshold 1) and asserts promotion happened — a Tier-0 fallback would hide an
/// emit failure.
/// </summary>
public class Adr037SoftCutIlTests
{
    private static (PrologEngine Engine, int Fid) Promoted(
        string program, string name, int arity, string warmQuery)
    {
        var e = new PrologEngine { EnableInlineIte = true };
        e.IlPromotion.Threshold = 1;
        e.ConsultString(program);
        Assert.True(e.Query(warmQuery).Success);
        Assert.True(e.Query(warmQuery).Success);
        int fid = FunctorTable.Intern(AtomTable.Intern(name).Id, arity);
        Assert.True(e.IlPromotion.IsPromoted(fid),
            $"{name}/{arity} must promote to IL (ADR-037 soft_cut emit)");
        return (e, fid);
    }

    [Fact]
    public void SoftCut_Promoted_TakesThen_AndIsDeterministic()
    {
        var (e, _) = Promoted(
            ":- public d/2.\n" +
            "d(X, R) :- ( member(X, [1,2,3]) *-> R = got(X) ; R = none ).\n",
            "d", 2, "d(X, R), X == 1, R == got(1).");
        for (int i = 0; i < 3; i++)
        {
            Assert.True(e.Query("d(X, R), X == 1, R == got(1).").Success);
            // Cond succeeded: Else pruned even under full backtracking.
            Assert.True(e.Query("findall(X, d(X, _), L), L == [1,2,3].").Success);
        }
    }

    [Fact]
    public void SoftCut_Promoted_PreservesNondeterminism_ThenPerSolution()
    {
        // Exercises the IL-CP neutralisation path: member leaves choice points
        // ABOVE the ELSE IL CP, so soft_cut must mark that middle IL CP's resume
        // to fail (not run Else) — Then runs once per condition solution.
        var (e, _) = Promoted(
            ":- public g/2.\n" +
            "g(X, R) :- ( member(X, [1,2,3]) *-> R = t(X) ; R = none ).\n",
            "g", 2, "g(_, _).");
        for (int i = 0; i < 3; i++)
            Assert.True(e.Query("findall(R, g(_, R), L), L == [t(1),t(2),t(3)].").Success);
    }

    [Fact]
    public void SoftCut_Promoted_RunsElse_WhenCondFails()
    {
        var (e, _) = Promoted(
            ":- public f/1.\n" +
            "f(R) :- ( member(_, []) *-> R = cond ; R = els ).\n",
            "f", 1, "f(_).");
        for (int i = 0; i < 3; i++)
            Assert.True(e.Query("f(R), R == els.").Success);
    }

    [Fact]
    public void SoftCut_Promoted_CallCondition_DeterministicLeavesNoChoicePoint()
    {
        // The time/1 shape (call/1 condition) under IL, with the choice-level
        // probe the top-level determinism check reads.
        var (e, _) = Promoted(
            ":- public r/2.\n" +
            "r(G, R) :- ( call(G) *-> R = ok ; R = no ).\n",
            "r", 2, "r(true, _).");
        for (int i = 0; i < 3; i++)
        {
            Assert.True(e.Query("r(true, R), R == ok.").Success);
            Assert.True(e.Query("r(fail, R), R == no.").Success);
            Assert.True(e.Query(
                "'$choice_level'(B0), r(true, _), '$choice_level'(B1), B1 =:= B0.").Success);
        }
    }
}
