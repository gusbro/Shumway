using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A <c>!</c> inside a runtime conjunction reaches <c>'$call'/2</c>
/// through the prelude's <c>'$call_conj'/3</c> and cuts to the enclosing
/// call's barrier. When that cut discards a <c>call_cleanup/2</c> scope, its
/// cleanup runs before the goal after the cut, as for any other cut. The
/// compiled dispatcher cut without running the cleanups it queued, so the goal
/// after the cut saw the state from before them: once <c>'$call_conj'/3</c>
/// was compiled, a cut inside a cleanup body (which runs as a runtime goal)
/// did not run the inner scope's cleanup in time. Both tiers are pinned to
/// the same answers here.</summary>
public class MetaCallCutCleanupTests
{
    private static readonly int CallConjFid =
        FunctorTable.Intern(AtomTable.Intern("$call_conj", permanent: true).Id, 3);

    private const string Program = """
        :- dynamic(log/1).
        % A cut inside a cleanup: the cleanup body runs as a runtime goal.
        nested(V) :-
            call_cleanup(member(_, [1,2]),
                ( b_setval(k, before),
                  call_cleanup(member(_, [1,2]), b_setval(k, cleaned)), !,
                  b_getval(k, V0), assertz(log(V0)) )),
            !, log(V).
        nested_run(V) :- retractall(log(_)), nested(V).
        """;

    // Built at run time so the conjunction reaches call/1 whole.
    private const string CutDiscardsAScope =
        "G =.. [',', b_setval(k, before), "
        + "(call_cleanup(member(_, [1,2]), b_setval(k, cleaned)), !, b_getval(k, V))], call(G)";

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
        for (int i = 0; i < 8; i++) Assert.True(e.Query("G =.. [',', true, true], call(G).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(e.IlPromotion.IsPromoted(CallConjFid),
            "'$call_conj'/3 did not reach the IL tier: the test would run the interpreter's path");
        return e;
    }

    // Three runs: the first cut resolves the goal `!`, the next ones take the
    // dispatcher's cached route.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACutInARuntimeConjunctionRunsTheCleanupOfTheScopeItDiscards(bool compiled)
    {
        var e = Engine(compiled);
        for (int i = 0; i < 3; i++)
        {
            var s = e.Query(CutDiscardsAScope + ".");
            Assert.True(s.Success, $"run {i}");
            Assert.Equal("cleaned", s["V"]!.ToString());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACutInsideACleanupRunsTheCleanupsItQueued(bool compiled)
    {
        var e = Engine(compiled);
        for (int i = 0; i < 3; i++)
        {
            var s = e.Query("nested_run(V).");
            Assert.True(s.Success, $"run {i}");
            Assert.Equal("cleaned", s["V"]!.ToString());
        }
    }
}
