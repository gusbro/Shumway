namespace Shumway.Core;

/// <summary>Translates a group build's baked addresses into the live code
/// space after a relink. A consult relinks the whole static program and
/// moves every predicate (measured: two plain facts shifted all ~530 prelude
/// addresses); the bytecode itself only MOVES, so a build stays runnable —
/// what must not happen is a build-space pc reaching the interpreter's
/// SetPc. The owning member is found by base (members' address ranges are
/// disjoint and their leaders lie inside them), and the pc moves by that
/// member's own displacement.</summary>
public sealed class WasmBuildAddressIndex
{
    private readonly int[] _bases;
    private readonly int[] _fids;

    public WasmBuildAddressIndex(
        System.Collections.Generic.IReadOnlyDictionary<int, int> entryAddressByFid)
    {
        int n = entryAddressByFid.Count;
        _bases = new int[n];
        _fids = new int[n];
        int i = 0;
        foreach (var (fid, addr) in entryAddressByFid)
        { _bases[i] = addr; _fids[i] = fid; i++; }
        System.Array.Sort(_bases, _fids);
    }

    /// <summary>The member owning a build-space pc: the greatest base at or
    /// below it. -1 when the pc precedes every member.</summary>
    private int OwnerOf(long pc)
    {
        int lo = 0, hi = _bases.Length - 1, at = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_bases[mid] <= pc) { at = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return at;
    }

    /// <summary>Build-space pc to live-space pc. Identity when no live map
    /// is set (no relink yet), when the pc precedes the build, or when the
    /// owner has no live address (evicted; its build code no longer runs by
    /// the time that can matter — the reconcile drops it at a boundary).</summary>
    public long Translate(long buildPc,
        System.Collections.Generic.IReadOnlyDictionary<int, int>? liveByFid)
    {
        if (liveByFid is null) return buildPc;
        int at = OwnerOf(buildPc);
        if (at < 0) return buildPc;
        return liveByFid.TryGetValue(_fids[at], out int live)
            ? buildPc - _bases[at] + live
            : buildPc;
    }
}
