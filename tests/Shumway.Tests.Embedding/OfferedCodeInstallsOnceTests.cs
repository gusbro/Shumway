using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A predicate gets one delegate. A bundle's offer counts the
/// predicate's dispatches, and no other compile of it starts while the offer
/// stands; a compile that drains after a delegate is installed is dropped,
/// since the choice points the installed one pushed resume through the
/// functor's current delegate at cursors of their own code.</summary>
public sealed class OfferedCodeInstallsOnceTests
{
    private static int Fid(string n, int a) => FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    // caller/2 runs on its bytecode and calls leaf/2, which runs compiled: the
    // call credits caller/2 with a dispatch.
    private const string Program = """
        :- public leaf/2, caller/2.
        leaf(0, 1) :- !.
        leaf(X, Y) :- Y is X + 1.
        caller(N, S) :- leaf(N, S0), S is S0 * 2.
        """;

    [Fact]
    public void ACallerWithAnOffer_IsNotCompiledByTheTier()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(Program);
        e.IlPromotion.PersistedThreshold = 1_000_000;
        int caller = Fid("caller", 2);
        e.IlPromotion.OfferPersisted(caller, wakes: true, isDynamic: false,
            () => throw new InvalidOperationException("caller/2 never earns its offer here"));
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal("4", $"{e.Query("caller(1, S).")["S"]}");
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        // ANTI-VACUITY: leaf/2 runs compiled, so its calls from caller/2 credited it.
        Assert.True(e.IlPromotion.IsPromoted(Fid("leaf", 2)), "leaf/2 not promoted");
        Assert.True(e.IlPromotion.HasOffer(caller), "caller/2 lost its offer");
        Assert.False(e.IlPromotion.IsPromoted(caller), "caller/2 compiled while its offer stands");
    }

    [Fact]
    public void ACompileThatDrainsAfterADelegateIsInstalled_DoesNotReplaceIt()
    {
        var store = new PrologEngine().IlPromotion;
        store.PersistedThreshold = 1;
        store.PersistedFreeBytes = 1_000_000;
        int fid = Fid("installed_once", 0);
        PredicateDelegate offered = (_, _) => true;
        PredicateDelegate first = (_, _) => true;
        store.OfferPersisted(fid, wakes: true, isDynamic: false, () => (offered, null), cost: 1);
        // Earned at its first call: the worker binds the offer's code.
        Assert.False(store.Tick(store.TryGetOffer(fid)!));
        store.RegisterBoundDelegate(fid, first, wakes: true);
        Assert.True(store.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.Same(first, store.TryGet(fid));
        Assert.False(store.HasOffer(fid), "the offer stands after its compile drained");
    }
}
