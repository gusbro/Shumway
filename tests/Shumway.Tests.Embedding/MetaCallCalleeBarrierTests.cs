using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A runtime goal's conjunction runs through the prelude's
/// <c>'$call_conj'/3</c>, which hands each piece to <c>'$call'/2</c> with the
/// enclosing call's cut barrier. The compiled dispatcher set B0 to that barrier
/// instead of the choice-point level at the callee's entry, so a neck cut in the
/// callee (the commit of a runtime <c>( C -> T ; E )</c>, a user predicate's own
/// cut) pruned the choice points of the pieces before it: a <c>clause/2</c>
/// enumeration kept only its first solution, which looked like a lost logical
/// update view. The interpreter's twin always set B0 to B; both tiers are pinned
/// to the same answers here.</summary>
public class MetaCallCalleeBarrierTests
{
    private static readonly int CallConjFid =
        FunctorTable.Intern(AtomTable.Intern("$call_conj", permanent: true).Id, 3);

    private const string Program = """
        :- dynamic(d/2).
        d(1, a). d(2, b). d(k, c). d([x], d). d(f(1), e). d(_, anything).
        pick(_) :- !.
        pick(_) :- fail.
        """;

    // Built at run time so the conjunction reaches call/1 whole, not the
    // compile-time rewrite of a findall over a literal one.
    private const string IteAfterMember =
        "G =.. [',', member(X, [1,2,3]), (X == 1 -> true ; true)], findall(X, G, L)";
    private const string NeckCutAfterMember =
        "G =.. [',', member(Y, [a,b,c]), pick(Y)], findall(Y, G, L)";
    private const string RetractWhileEnumerating =
        "G =.. [',', clause(d(K, _), true), (K == 1 -> retract(d(2, b)) ; true)], findall(K, G, L)";

    /// <summary>Runs <paramref name="goal"/> and checks its L against
    /// <paramref name="expected"/> in Prolog, naming what it got instead.</summary>
    private static void AssertCollects(PrologEngine e, string goal, string expected)
    {
        // One run: the goal may change the database, so the check rides on it.
        var sol = e.Query(goal + ", ( " + expected + " -> Ok = yes ; Ok = no ).");
        Assert.True(sol.Success, "the goal failed");
        Assert.True(sol.Bindings["Ok"].ToString() == "yes",
            $"collected {sol.Bindings["L"]} where {expected} was expected");
    }

    private static PrologEngine Engine(bool compiled)
    {
        var e = new PrologEngine();
        if (!compiled)
        {
            e.IlPromotion.Threshold = 0;
            e.ConsultString(Program);
            return e;
        }
        // The prelude's compiled '$call_conj'/3 is bound on the compile worker
        // once the predicate is hot; the goals below run only against it.
        e.IlPromotion.PersistedThreshold = 1;
        e.IlPromotion.PersistedCallsPerByte = 0;
        e.ConsultString(Program);
        for (int i = 0; i < 8; i++) Assert.True(e.Query(IteAfterMember + ".").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(e.IlPromotion.IsPromoted(CallConjFid),
            "'$call_conj'/3 did not reach the IL tier: the test would run the interpreter's path");
        return e;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARuntimeIfThenElse_KeepsTheChoicePointsOfTheGoalBeforeIt(bool compiled)
    {
        AssertCollects(Engine(compiled), IteAfterMember, "L == [1,2,3]");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACalleesNeckCut_CommitsItsOwnClausesOnly(bool compiled)
    {
        AssertCollects(Engine(compiled), NeckCutAfterMember, "L == [a,b,c]");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARetractUnderARuntimeIfThenElse_LeavesTheClauseEnumerationItsView(bool compiled)
    {
        // Two runs: the second finds d(2, b) gone, so the retract at K = 1
        // fails and drops that solution, and the rest of the view is whole.
        var e = Engine(compiled);
        AssertCollects(e, RetractWhileEnumerating, "L = [1,2,k,[x],f(1),_]");
        AssertCollects(e, RetractWhileEnumerating, "L = [k,[x],f(1),_]");
    }
}
