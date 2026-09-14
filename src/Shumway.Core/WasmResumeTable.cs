namespace Shumway.Core;

/// <summary>The rows a wasm module reads to resolve a resume marker: one i64
/// per marker, indexed by <c>marker - Activation.ResumeMarkerBase</c>.
///
/// <para>The index needs no computing. <see cref="Activation.EncodeResumeMarker"/>
/// interns each (functor, address) pair and returns <c>Base + denseId</c>, so a
/// marker IS a dense id and resolving one is a subscript. What that replaces is
/// a chain of baked comparisons — <c>if (bp == c1) ... if (bp == c2) ...</c>,
/// one per choice-point site — walked linearly on every failure.</para>
///
/// <para>It also collapses two encodings of one fact into one. The host
/// resolves a marker through a dictionary and the module through its baked
/// chain; those are derived separately today, and a disagreement between them
/// is a bug with nowhere to show. Reading the same rows makes that class of bug
/// impossible rather than unlikely.</para>
///
/// <para>An instance belongs to ONE ENGINE, and every module of that engine
/// shares it. That is the whole point: a module resolving a marker has to be
/// able to discover that it belongs to a DIFFERENT module and where that one
/// is, which it cannot do from a table only it can see.</para>
///
/// <para>Engine, though, and never process-wide. Functor ids and the marker
/// pool are global, but bytecode addresses belong to an engine's code space,
/// so two engines running the same program mint the same markers for different
/// code. Sharing across engines would resolve one engine's marker into
/// another's module — and the desktop tests, which build dozens of engines in
/// a process, would be the first to find out.</para></summary>
public sealed class WasmResumeTable
{
    // Row: ((moduleId + 1) << 32) | cursor. Zero is "not here", which has to
    // be distinguishable from a valid cursor 0 (the first leader of a module).
    private long[] _rows;

    public WasmResumeTable(int initialRows = 4096)
        => _rows = new long[initialRows];

    /// <summary>Hands out the next module id for this engine. Ids are dense
    /// and start at 0, so they index the moduleId -&gt; table-index array the
    /// in-wasm hop reads.</summary>
    public int NextModuleId() => _nextModuleId++;

    private int _nextModuleId;

    /// <summary>How many modules this engine has handed ids to.</summary>
    public int ModuleCount => _nextModuleId;

    /// <summary>The rows, for the host to copy into linear memory. Grown by
    /// <see cref="Set"/>; the reference changes, so read it each time.</summary>
    public long[] Rows => _rows;

    public int Length => _rows.Length;

    /// <summary>Records where a marker resolves. <paramref name="cursor"/> is
    /// the owning module's own dispatch cursor.</summary>
    public void Set(int marker, int moduleId, int cursor)
    {
        int i = marker - Activation.ResumeMarkerBase;
        if (i < 0) throw new ArgumentOutOfRangeException(nameof(marker),
            $"0x{marker:X} is not a resume marker.");
        if (i >= _rows.Length)
        {
            int grown = _rows.Length;
            while (grown <= i) grown *= 2;
            Array.Resize(ref _rows, grown);
        }
        _rows[i] = ((long)(moduleId + 1) << 32) | (uint)cursor;
    }

    /// <summary>Forgets every row of one module: what an eviction does. Linear
    /// in the table, and eviction is rare — the alternative is a per-module
    /// index that would have to be kept correct for a case that almost never
    /// runs.</summary>
    public void ClearModule(int moduleId)
    {
        long tag = (long)(moduleId + 1) << 32;
        for (int i = 0; i < _rows.Length; i++)
            if ((_rows[i] & ~0xFFFFFFFFL) == tag) _rows[i] = 0;
    }

    /// <summary>Forgets every row of one functor: what an eviction, or a
    /// takeover by a newer module, does. Linear in the table; both are
    /// rare.</summary>
    public void ClearFunctor(int functorId)
    {
        for (int i = 0; i < _rows.Length; i++)
        {
            if (_rows[i] == 0) continue;
            if (Activation.DecodeResumeMarker(Activation.ResumeMarkerBase + i).FunctorId
                == functorId)
                _rows[i] = 0;
        }
    }

    /// <summary>Reads a row back. False when the marker does not resolve here,
    /// which is what the host and the module both treat as "not mine".</summary>
    public bool TryGet(int marker, out int moduleId, out int cursor)
    {
        moduleId = 0;
        cursor = 0;
        int i = marker - Activation.ResumeMarkerBase;
        if ((uint)i >= (uint)_rows.Length) return false;
        long row = _rows[i];
        if (row == 0) return false;
        moduleId = (int)(row >> 32) - 1;
        cursor = (int)row;
        return true;
    }
}
