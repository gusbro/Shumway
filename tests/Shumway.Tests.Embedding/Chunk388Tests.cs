using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 388 (Phase 29, Stage 9 foundation): the dead-region reachability analysis
/// (<see cref="RegionReachability"/>). Pure fixpoint over region roots — seed with the
/// externally-reachable predicates, follow each region's CROSS-region (trampoline)
/// edges to discover more live roots, and everything not discovered is prunable (reached
/// only as an absorbed br-member). These tests pin the analysis on synthetic call
/// graphs + explicit region-membership maps; wiring it onto a real bundle is a later
/// step.
/// </summary>
public class Chunk388Tests
{
    // A predicate with the given call edges (callee fid, isExecute=false → non-tail).
    private static CompiledPredicate Pred(int fid, params int[] callees)
    {
        var body = new byte[1];
        var sites = callees.Select((c, i) => new CallSite(i + 1, c, false)).ToList();
        return new CompiledPredicate(body, fid, /*arity*/ 1, /*clauses*/ 1, sites, Array.Empty<int>());
    }

    private static Dictionary<int, CompiledPredicate> Map(params CompiledPredicate[] ps)
        => ps.ToDictionary(p => p.FunctorId);

    // regionMembers built from an explicit map (root -> absorbed incl. root); a root not
    // listed absorbs only itself (not region-compiled / not emittable).
    private static Func<int, IReadOnlyCollection<int>> Regions(Dictionary<int, int[]> m)
        => root => m.TryGetValue(root, out var s) ? s : new[] { root };

    [Fact]
    public void ChainFullyAbsorbed_OnlyRootKept()
    {
        // a→b→c, a external; a's region absorbs {a,b,c}. b,c reached only as br members.
        var preds = Map(Pred(1, 2), Pred(2, 3), Pred(3));
        var regions = Regions(new() { [1] = new[] { 1, 2, 3 } });
        var prunable = RegionReachability.Prunable(preds, new[] { 1 }, regions);
        Assert.Equal(new[] { 2, 3 }, prunable.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void CrossRegionCallee_IsKept()
    {
        // a→b but a does NOT absorb b (e.g. b has a backtrackable builtin → Stage 6d).
        // b trampolines out → live root; b absorbs c → c prunable.
        var preds = Map(Pred(1, 2), Pred(2, 3), Pred(3));
        var regions = Regions(new() { [1] = new[] { 1 }, [2] = new[] { 2, 3 } });
        var reachable = RegionReachability.TrampolineReachable(preds, new[] { 1 }, regions);
        Assert.Equal(new[] { 1, 2 }, reachable.OrderBy(x => x).ToArray());
        var prunable = RegionReachability.Prunable(preds, new[] { 1 }, regions);
        Assert.Equal(new[] { 3 }, prunable.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void AbsorbedButAlsoPublic_IsKept()
    {
        // a absorbs {a,b}; but b is ALSO externally reachable (public) → keep its
        // standalone form even though a calls it via br.
        var preds = Map(Pred(1, 2), Pred(2));
        var regions = Regions(new() { [1] = new[] { 1, 2 } });
        var prunable = RegionReachability.Prunable(preds, new[] { 1, 2 }, regions);
        Assert.Empty(prunable);
    }

    [Fact]
    public void Diamond_AllInteriorAbsorbed_Pruned()
    {
        // a→b, a→c, b→d, c→d; a's region absorbs everything.
        var preds = Map(Pred(1, 2, 3), Pred(2, 4), Pred(3, 4), Pred(4));
        var regions = Regions(new() { [1] = new[] { 1, 2, 3, 4 } });
        var prunable = RegionReachability.Prunable(preds, new[] { 1 }, regions);
        Assert.Equal(new[] { 2, 3, 4 }, prunable.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void SplitCaller_OneAbsorbsOneDoesnt_Kept()
    {
        // b is called by a (absorbs b) AND by c (does NOT). The trampoline edge from c
        // keeps b's standalone form alive.
        var preds = Map(Pred(1, 2), Pred(3, 2), Pred(2));
        var regions = Regions(new() { [1] = new[] { 1, 2 }, [3] = new[] { 3 } });
        var prunable = RegionReachability.Prunable(preds, new[] { 1, 3 }, regions);
        Assert.Empty(prunable);   // b kept via c's trampoline edge
    }

    [Fact]
    public void BuiltinOrExternalCallee_Ignored()
    {
        // a→99 where 99 is not a module predicate (a builtin). Not prunable (not ours),
        // not a crash.
        var preds = Map(Pred(1, 99));
        var regions = Regions(new() { [1] = new[] { 1 } });
        var prunable = RegionReachability.Prunable(preds, new[] { 1 }, regions);
        Assert.Empty(prunable);
    }

    [Fact]
    public void Cycle_AmongAbsorbed_Terminates()
    {
        // a↔b mutually recursive, both absorbed into a's region.
        var preds = Map(Pred(1, 2), Pred(2, 1));
        var regions = Regions(new() { [1] = new[] { 1, 2 } });
        var prunable = RegionReachability.Prunable(preds, new[] { 1 }, regions);
        Assert.Equal(new[] { 2 }, prunable.OrderBy(x => x).ToArray());
    }
}
