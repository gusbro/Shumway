using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 389 (Phase 29, Stage 9b — linker side): the externally-reachable SEED set the
/// linker computes for the dead-region prune (<see cref="ShmoLinker.ComputeExternallyReachableSeeds"/>).
/// The seeds are the reached predicates that must keep a standalone form because they
/// are callable BY NAME from outside a region's br-absorption: entry / ensure_linked
/// roots + every reached public + every reached dynamic (which includes <c>:- visible</c>,
/// recorded as Dynamic). A purely-local, internally-called predicate is NOT a seed.
/// </summary>
public class Chunk389Tests
{
    private static PredicateRef P(string n, int a) => new(n, a);

    private static IReadOnlyDictionary<string, Dictionary<PredicateRef, PredicateVisibility>>
        Defined(string module, params (PredicateRef Pred, PredicateVisibility Vis)[] defs)
        => new Dictionary<string, Dictionary<PredicateRef, PredicateVisibility>>
        {
            [module] = defs.ToDictionary(d => d.Pred, d => d.Vis),
        };

    [Fact]
    public void PublicAndDynamicReached_AreSeeds_LocalIsNot()
    {
        var defined = Defined("m",
            (P("main", 0), PredicateVisibility.Local),     // entry (local, promoted)
            (P("pub", 1), PredicateVisibility.Public),
            (P("dyn", 0), PredicateVisibility.Dynamic),    // also the :- visible case
            (P("loc", 2), PredicateVisibility.Local));     // internal helper
        var reached = new[]
        {
            ("m", P("main", 0)), ("m", P("pub", 1)), ("m", P("dyn", 0)), ("m", P("loc", 2)),
        };
        var roots = new[] { ("m", P("main", 0)) };

        var seeds = ShmoLinker.ComputeExternallyReachableSeeds(roots, reached, defined);

        Assert.Contains(("m", P("main", 0)), seeds);   // entry root (even though local)
        Assert.Contains(("m", P("pub", 1)), seeds);    // public
        Assert.Contains(("m", P("dyn", 0)), seeds);    // dynamic / visible
        Assert.DoesNotContain(("m", P("loc", 2)), seeds);  // local internal → prunable
        Assert.Equal(3, seeds.Count);
    }

    [Fact]
    public void EnsureLinkedRoot_IsSeed_EvenWhenLocal()
    {
        // A :- ensure_linked target resolved to a local predicate is a root → seed
        // (it is invoked by name via a runtime meta-call).
        var defined = Defined("m", (P("hook", 1), PredicateVisibility.Local));
        var reached = new[] { ("m", P("hook", 1)) };
        var roots = new[] { ("m", P("hook", 1)) };   // came from ensure_linked

        var seeds = ShmoLinker.ComputeExternallyReachableSeeds(roots, reached, defined);
        Assert.Contains(("m", P("hook", 1)), seeds);
    }

    [Fact]
    public void VisibleRecordedAsDynamic_IsSeed()
    {
        // `:- visible foo/N` is recorded as PredicateVisibility.Dynamic (chunk 265),
        // so it lands in the seed set via the dynamic rule — the user-flagged case.
        var defined = Defined("m", (P("vis", 2), PredicateVisibility.Dynamic));
        var reached = new[] { ("m", P("vis", 2)) };
        var seeds = ShmoLinker.ComputeExternallyReachableSeeds(
            System.Array.Empty<(string, PredicateRef)>(), reached, defined);
        Assert.Contains(("m", P("vis", 2)), seeds);
    }

    [Fact]
    public void UnreachablePublic_NotASeed()
    {
        // A public predicate that was NOT reached is not in `reached`, so not a seed —
        // it is dropped by the existing module/predicate reachability, not Stage 9.
        var defined = Defined("m",
            (P("used", 0), PredicateVisibility.Public),
            (P("unused", 0), PredicateVisibility.Public));
        var reached = new[] { ("m", P("used", 0)) };   // unused never reached
        var seeds = ShmoLinker.ComputeExternallyReachableSeeds(
            System.Array.Empty<(string, PredicateRef)>(), reached, defined);
        Assert.Contains(("m", P("used", 0)), seeds);
        Assert.DoesNotContain(("m", P("unused", 0)), seeds);
    }
}
