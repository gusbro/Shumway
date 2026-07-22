using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// One-slot recycler for activation heap buffers, owned by a
/// <c>PrologEngine</c>. A big query grows the activation heap by doubling
/// (alloc + copy per step: a 300M-cell peak from the 64K initial = 13
/// reallocations copying ~2 GB), and the fully-grown buffer died with the
/// activation — so REPEATING the query re-paid the whole ladder, and the dead
/// 4 GB array lingered as GC-retained LOH. The pool recycles the buffer
/// across activations: at most ONE pooled buffer (overlapping activations —
/// a suspended QueryAll plus a nested query — allocate fresh as before),
/// handed to the next activation at setup, taken back when an activation's
/// solution enumeration dies.
///
/// <para>Retention policy: a decayed usage peak.
/// <c>recentUse = max(thisActivationUse, recentUse / 2)</c> per death. The
/// buffer is kept only while its capacity ≤ max(4 × recentUse, floor):
/// repeating big queries keeps it hot; a big spike followed by small queries
/// halves the peak each query and the oversized buffer is dropped (geometric
/// descent — a returning big query pays one ladder, once). Trimming at the
/// boundary is free: the heap is logically empty, so "trim" is just not
/// keeping the reference.</para>
///
/// <para>Not thread-safe on its own — access is serialized by the owning
/// engine's single-driver contract, like the rest of the per-engine
/// state.</para>
/// </summary>
internal sealed class HeapBufferPool
{
    private Cell[]? _pooledHeap;
    private long _recentHeapUseCells;
    private const long FloorCells = 1L << 20;   // 8 MB — always OK to keep

    /// <summary>Hands the pooled heap buffer (if any) to a fresh activation.
    /// Must run before query setup materializes anything onto the heap.</summary>
    public void Adopt(Activation activation)
    {
        var buffer = _pooledHeap;
        if (buffer is null) return;
        _pooledHeap = null;
        activation.AdoptHeapBuffer(buffer);
    }

    /// <summary>Takes a dead activation's heap buffer back into the pool,
    /// applying the decayed-peak retention policy. CellsAllocated (cumulative
    /// allocations, capped at capacity) proxies the activation's usage.</summary>
    public void Return(Activation activation)
    {
        var buffer = activation.DetachHeapBuffer();
        long used = Math.Min(activation.CellsAllocated, buffer.LongLength);
        _recentHeapUseCells = Math.Max(used, _recentHeapUseCells / 2);
        long keepCap = Math.Max(4 * _recentHeapUseCells, FloorCells);
        if (buffer.LongLength > keepCap) return;   // decayed workload no longer justifies it
        if (_pooledHeap is null || _pooledHeap.Length < buffer.Length)
            _pooledHeap = buffer;
    }

    /// <summary>The pooled buffer's capacity in cells (0 = empty).</summary>
    public long CapacityCells => _pooledHeap?.LongLength ?? 0;
}
