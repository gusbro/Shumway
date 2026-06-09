using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Il;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 394 (Phase 29, Stage 9c — cost-based root selection): the iterative
/// minimal-root-set fixpoint (<see cref="RegionRootSelector.ComputeForcedRoots"/>).
/// Promote a shared member to its own root when (dup−1)×regionSize beats the threshold,
/// recompute, repeat. Tested on a synthetic diamond call graph (no real bytecode — the
/// selector takes region-membership + size as functions); the end-to-end size win is
/// validated on Blint via shumway-link (1463 KB → ~914 KB).
/// </summary>
public class Chunk394Tests
{
    // Diamond: a(1) → b(2), c(3); b → d(4); c → d. d is the shared leaf utility.
    private static readonly Dictionary<int, int[]> Calls = new()
    {
        [1] = new[] { 2, 3 },
        [2] = new[] { 4 },
        [3] = new[] { 4 },
        [4] = System.Array.Empty<int>(),
    };

    // Region of `root` = BFS over call edges, NOT absorbing anything in `excluded`
    // (those stay cross-region roots) — mirrors IlRegionBuilder + the 9c exclusion.
    private static IReadOnlyCollection<int> Region(int root, IReadOnlySet<int> excluded)
    {
        var members = new HashSet<int> { root };
        var q = new Queue<int>();
        q.Enqueue(root);
        while (q.Count > 0)
            foreach (int c in Calls[q.Dequeue()])
                if (!excluded.Contains(c) && members.Add(c))
                    q.Enqueue(c);
        return members;
    }

    [Fact]
    public void SharedLeaf_IsPromoted_WhenSavingBeatsThreshold()
    {
        // d is absorbed by a, b, c (dup 3); each predicate is 100 B. score(d) =
        // (3−1)×100 = 200 > minSaving 100 → promote d. After: nothing has dup ≥ 2.
        var roots = RegionRootSelector.ComputeForcedRoots(
            Calls.Keys, Region, _ => 100, minSaving: 100);
        Assert.Equal(new[] { 4 }, roots.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Threshold_AboveSaving_PromotesNothing()
    {
        // score(d) = 200 < minSaving 300 → no promotion (the trampolines aren't worth it).
        var roots = RegionRootSelector.ComputeForcedRoots(
            Calls.Keys, Region, _ => 100, minSaving: 300);
        Assert.Empty(roots);
    }

    private static IReadOnlyCollection<int> RegionOf(
        Dictionary<int, int[]> g, int root, IReadOnlySet<int> excluded)
    {
        var m = new HashSet<int> { root };
        var q = new Queue<int>(); q.Enqueue(root);
        while (q.Count > 0)
            foreach (int c in g[q.Dequeue()])
                if (!excluded.Contains(c) && m.Add(c)) q.Enqueue(c);
        return m;
    }

    [Fact]
    public void Star_NoSharing_PromotesNothing()
    {
        // a → b, a → c with b, c leaves: each leaf is absorbed by exactly ONE region
        // (a's). No predicate has dup ≥ 2, so nothing promotes even at minSaving 0.
        var star = new Dictionary<int, int[]>
        {
            [1] = new[] { 2, 3 }, [2] = System.Array.Empty<int>(), [3] = System.Array.Empty<int>(),
        };
        var roots = RegionRootSelector.ComputeForcedRoots(
            star.Keys, (r, ex) => RegionOf(star, r, ex), _ => 100, minSaving: 0);
        Assert.Empty(roots);
    }

    [Fact]
    public void Chain_OneMidPromotion_DedupesTheTail()
    {
        // A chain a→b→c→d duplicates in all-as-roots (region(a) absorbs b,c,d; region(b)
        // absorbs c,d; region(c) absorbs d). The iterative fixpoint promotes the MIDDLE
        // node c(3) first (tie-broken; score 200) — and recomputing shows that now leaves
        // d(4) in only c's region (one copy), so d is no longer shared and is NOT
        // promoted. One promotion de-dups the whole tail: result {3}, not {3,4}.
        var chain = new Dictionary<int, int[]>
        {
            [1] = new[] { 2 }, [2] = new[] { 3 }, [3] = new[] { 4 }, [4] = System.Array.Empty<int>(),
        };
        var roots = RegionRootSelector.ComputeForcedRoots(
            chain.Keys, (r, ex) => RegionOf(chain, r, ex), _ => 100, minSaving: 0);
        Assert.Equal(new[] { 3 }, roots.OrderBy(x => x).ToArray());
    }
}
