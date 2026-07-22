using Shumway.Compiler.Wam;

namespace Shumway.Compiler.Il;

/// <summary>
/// Memoized result of one Tier-1 IL shape analysis
/// (<c>TryDescribeIndexed</c> / <c>TryDescribeTryMeElseChain</c> /
/// <c>TryDescribeSwitchedChain</c> / <c>TryDescribeIndexedAtomPredicate</c>)
/// for one immutable <see cref="CompiledPredicate"/>, stored on the
/// predicate's cache slots (same pattern as <c>PoolFreeMemo</c>).
///
/// <para>The describers were recomputed up to ~8× per predicate across
/// promotion (CanCompile then Compile), the region pipeline
/// (IsRegionMemberEligible / RegionMemberOk / the planner's indexNodeCount /
/// RegionBuiltinResumePcs / EmitRegionInto) and the persisted build
/// (CanPersist / EmitPersistedMethod / BuildPersistableIndexGraph /
/// UsesWamBackedIndexedDispatch) — each a full bytecode walk with ~10+
/// collection allocations.</para>
///
/// <para>The analyses take a <c>calleeMap</c>, but its ONLY influence on the
/// result is conjunctive: <c>IsClauseBodyOpcode</c> rejects a <c>Call</c>
/// opcode whose callee fid is missing from the map (or has no call-site
/// metadata, or when the map is null). So the memo stores the
/// <em>structural</em> describe result — computed with every <c>Call</c>
/// accepted and its callee fid recorded — and <see cref="Resolve{T}"/>
/// re-applies the map-dependent part per call: the memoized info is returned
/// iff every recorded fid resolves in the caller's map. This is exactly
/// equivalent to the unmemoized check, because the describers only ever use
/// the body-opcode predicate as an accept/reject filter (never to branch
/// between result shapes).</para>
/// </summary>
internal sealed class IlShapeMemo
{
    /// <summary>The structural describe result, or null when the bytecode
    /// does not match the shape under ANY calleeMap.</summary>
    private readonly object? _info;

    /// <summary>The callee fid of every <c>Call</c> opcode the structural
    /// walk validated, in walk order. Empty → the result is
    /// calleeMap-independent. An entry of <c>-1</c> (a Call with no
    /// call-site metadata) can never resolve.</summary>
    private readonly int[] _callFids;

    internal IlShapeMemo(object? info, List<int> callFids)
    {
        _info = info;
        _callFids = callFids.ToArray();
    }

    /// <summary>Re-applies the calleeMap-dependent half of the analysis:
    /// returns the memoized describe result iff the shape matched
    /// structurally AND every recorded <c>Call</c> callee resolves in
    /// <paramref name="calleeMap"/>.</summary>
    internal bool Resolve<T>(
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, out T? info)
        where T : class
    {
        info = null;
        if (_info is null) return false;
        if (_callFids.Length > 0)
        {
            if (calleeMap is null) return false;
            foreach (int fid in _callFids)
                if (fid < 0 || !calleeMap.ContainsKey(fid)) return false;
        }
        info = (T)_info;
        return true;
    }
}
