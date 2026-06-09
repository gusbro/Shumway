using System;
using System.Collections.Generic;
using Shumway.Compiler.Wam;

namespace Shumway.Compiler.Il;

/// <summary>
/// Stage 9c (cost-based root selection / minimal-root-set) — picks which predicates to
/// FORCE as region roots (exclude from absorption) to cut the inter-root duplication
/// that all-as-roots region compilation produces. See
/// <c>docs/design/il-region-compilation.md</c> §9c.
///
/// <para>The model: absorbing a member <c>M</c> into a region root bakes in M AND its
/// whole absorbed sub-closure (the BFS continues through M), so the code DUPLICATED per
/// absorbing region is <c>size(region(M))</c> — the size of M's OWN region closure, not
/// M's bytecode. If M is absorbed by <c>dup(M)</c> regions, promoting M to its own root
/// (one copy + dup(M) cross-region trampolines) saves <c>(dup(M) − 1) × size(region(M))</c>.
/// </para>
///
/// <para>Because regions overlap/nest, promoting one predicate changes every other
/// predicate's region size and duplication, so this is a GLOBAL optimisation solved by
/// an iterative greedy fixpoint: build all regions with the current promotion set
/// excluded, score every still-absorbed shared predicate, promote the single best,
/// recompute, repeat until no promotion beats <paramref name="minSaving"/>. Promoting
/// deep, highly-shared leaf utilities first (they have the biggest score) shrinks all
/// their callers' regions at once.</para>
/// </summary>
public static class RegionRootSelector
{
    /// <summary>Computes the forced-root (promotion) set. Pure (does not touch
    /// <see cref="IlPredicateCompiler.RegionForcedRootFids"/> — the caller installs the
    /// result). Decoupled from the IL compiler for testability: region membership and
    /// per-predicate size are supplied as functions.</summary>
    /// <param name="fids">The candidate predicate functor ids.</param>
    /// <param name="regionMembersOf">(root fid, currently-promoted set) → the functor ids
    /// the region rooted at <c>root</c> absorbs as members (including the root), with the
    /// promoted set excluded from absorption. Typically
    /// <c>(f, ex) => ic.RegionMemberFids(predicates[f], predicates, ex)</c>.</param>
    /// <param name="predicateSize">fid → its own bytecode size (a region's size is the
    /// sum over its members).</param>
    /// <param name="minSaving">Minimum byte saving <c>(dup−1)×regionSize</c> for a
    /// promotion to be worth its trampolines. Higher = fewer, bigger-win promotions.</param>
    /// <param name="onPromote">Optional per-promotion trace (fid, dup, regionSize).</param>
    public static HashSet<int> ComputeForcedRoots(
        IReadOnlyCollection<int> fids,
        Func<int, IReadOnlySet<int>, IReadOnlyCollection<int>> regionMembersOf,
        Func<int, long> predicateSize,
        long minSaving,
        Action<int, int, long>? onPromote = null)
    {
        ArgumentNullException.ThrowIfNull(fids);
        ArgumentNullException.ThrowIfNull(regionMembersOf);
        ArgumentNullException.ThrowIfNull(predicateSize);
        var promoted = new HashSet<int>();
        var regionOf = new Dictionary<int, IReadOnlyCollection<int>>(fids.Count);
        var sizeOf = new Dictionary<int, long>(fids.Count);

        while (true)
        {
            // 1. Build every predicate's region with the current promotion set excluded.
            regionOf.Clear();
            sizeOf.Clear();
            foreach (int fid in fids)
            {
                var members = regionMembersOf(fid, promoted);
                regionOf[fid] = members;
                long s = 0;
                foreach (int m in members) s += predicateSize(m);
                sizeOf[fid] = s;
            }

            // 2. Duplication: how many OTHER regions absorb each predicate as a member.
            var dup = new Dictionary<int, int>(fids.Count);
            foreach (var (root, members) in regionOf)
                foreach (int m in members)
                    if (m != root)
                        dup[m] = dup.TryGetValue(m, out int c) ? c + 1 : 1;

            // 3. Score the still-absorbed shared predicates; promote the single best.
            int best = -1;
            long bestScore = minSaving;   // must strictly exceed to promote
            foreach (var (m, d) in dup)
            {
                if (d < 2 || promoted.Contains(m)) continue;
                long score = (long)(d - 1) * sizeOf[m];   // size = M's OWN region closure
                if (score > bestScore) { bestScore = score; best = m; }
            }
            if (best < 0) break;
            promoted.Add(best);
            onPromote?.Invoke(best, dup[best], sizeOf[best]);
        }
        return promoted;
    }
}
