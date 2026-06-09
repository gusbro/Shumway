using System;
using System.Collections.Generic;
using Shumway.Compiler.Wam;

namespace Shumway.Compiler.Il;

/// <summary>
/// Stage 9 (module-level dead-region elimination) reachability analysis —
/// <c>docs/design/il-region-compilation.md</c> §9. For a module compiled in REGION
/// mode, computes which predicates still need a STANDALONE (trampoline-callable) form,
/// and conversely which are reached ONLY as absorbed <c>br</c>-members of some region
/// and can therefore be PRUNED from the bundle.
///
/// <para>The model: each region root is one IL method that absorbs a set of member
/// predicates (its local closure, see <see cref="IlPredicateCompiler.RegionMemberFids"/>).
/// A call from inside a region to an absorbed member is an intra-region <c>br</c> — it
/// does NOT reach the member's standalone form. A call to a NON-absorbed predicate
/// trampolines out, so that predicate needs a standalone (region-root) form. A
/// predicate's standalone form is dead iff EVERY call that reaches it is an intra-region
/// <c>br</c> (every caller absorbs it) AND it is not externally reachable
/// (entry-point / public / dynamic).</para>
///
/// <para>This is a forward reachability <b>fixpoint</b> over region roots: seed with the
/// externally-reachable predicates; each is a live region root; follow only its region's
/// CROSS-region (trampoline) edges to discover more live roots; iterate. Everything not
/// discovered is prunable. Pure analysis — no IL, no mutation; the caller (the linker)
/// supplies the region-membership function and the external-root set, and applies the
/// result to the bundle.</para>
/// </summary>
public static class RegionReachability
{
    /// <summary>The set of predicates that need a standalone (trampoline-callable) form
    /// — the live region roots. Its complement within <paramref name="predicates"/> is
    /// <see cref="Prunable"/>.</summary>
    /// <param name="predicates">The module: functor id → its compiled WAM (for the call
    /// graph via <see cref="CompiledPredicate.CallSites"/>). A callee not in this map is
    /// a builtin / external / dynamic reference and is ignored (it is not a prunable
    /// member of this module).</param>
    /// <param name="externallyReachable">Functor ids that MUST keep a standalone form
    /// regardless of absorption — entry points, public predicates, dynamic predicates.
    /// The reachability seeds.</param>
    /// <param name="regionMembers">root fid → the functor ids its region absorbs as
    /// <c>br</c>-members (including the root). Typically
    /// <see cref="IlPredicateCompiler.RegionMemberFids"/>. Must return <c>{root}</c> for
    /// a predicate that is not region-compiled (so all its callees trampoline out).</param>
    public static HashSet<int> TrampolineReachable(
        IReadOnlyDictionary<int, CompiledPredicate> predicates,
        IEnumerable<int> externallyReachable,
        Func<int, IReadOnlyCollection<int>> regionMembers)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(externallyReachable);
        ArgumentNullException.ThrowIfNull(regionMembers);

        var reachable = new HashSet<int>();
        var work = new Queue<int>();
        foreach (int fid in externallyReachable)
            if (predicates.ContainsKey(fid) && reachable.Add(fid))
                work.Enqueue(fid);

        while (work.Count > 0)
        {
            int root = work.Dequeue();
            var absorbed = regionMembers(root);
            var absorbedSet = absorbed as ISet<int> ?? new HashSet<int>(absorbed);
            // Every absorbed member's body is emitted inside this region method; its
            // calls to predicates NOT in the absorbed set trampoline out and so reach
            // those predicates' standalone forms (making them live roots in turn).
            foreach (int member in absorbed)
            {
                if (!predicates.TryGetValue(member, out var mp)) continue;
                foreach (var cs in mp.CallSites)
                {
                    int callee = cs.CalleeFunctorId;
                    if (!predicates.ContainsKey(callee)) continue;   // builtin / external
                    if (absorbedSet.Contains(callee)) continue;       // intra-region br
                    if (reachable.Add(callee)) work.Enqueue(callee);  // trampoline out → live root
                }
            }
        }
        return reachable;
    }

    /// <summary>The predicates whose standalone form is DEAD under region compilation —
    /// reached only as absorbed <c>br</c>-members and not externally reachable — so the
    /// linker can drop them from the bundle. The complement of
    /// <see cref="TrampolineReachable"/>.</summary>
    public static HashSet<int> Prunable(
        IReadOnlyDictionary<int, CompiledPredicate> predicates,
        IEnumerable<int> externallyReachable,
        Func<int, IReadOnlyCollection<int>> regionMembers)
    {
        var reachable = TrampolineReachable(predicates, externallyReachable, regionMembers);
        var prunable = new HashSet<int>();
        foreach (int fid in predicates.Keys)
            if (!reachable.Contains(fid))
                prunable.Add(fid);
        return prunable;
    }
}
