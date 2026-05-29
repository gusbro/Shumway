namespace Shumway.Core;

/// <summary>
/// ADR-016 — order-preserving sliding mark-compact collector for the
/// <see cref="_heap"/> <see cref="Cell"/> array. Reclaims unreachable
/// cells by reachability, independently of the choice-point stack: open
/// choice points no longer pin garbage, only genuinely reachable state
/// survives. Called at engine safe points (see the watermark hook and
/// <c>garbage_collect/0</c>).
///
/// <para>First cut (chunk 211): attributed variables are out of scope —
/// the collector bails (no-op) when any attvar state is present, which
/// is always safe. Attvar relocation (the attr table keyed by home
/// index, the attr-modify side log, pending wakeups) is a follow-up.</para>
/// </summary>
public sealed partial class Engine
{
    // Scratch buffers reused across collections to avoid per-GC churn.
    private bool[]? _gcMarked;
    private int[]? _gcForward;
    private int[]? _gcWork;

    /// <summary>Runs a mark-compact collection of the heap. Returns the
    /// number of cells reclaimed (0 if the collector bailed). Safe to
    /// call only at a safe point — between WAM instructions — where the
    /// machine state is consistent (no half-built structure).</summary>
    public int CollectHeap()
    {
        // Bail when attributed variables are in use: their attribute
        // terms are roots reachable only via the attr table (keyed by
        // the attvar's home heap index, which itself must be relocated),
        // plus an attr-modify side log and a transient wakeup queue that
        // also carry heap indices. Relocating all of that correctly is a
        // separate chunk; until then a no-op is the safe choice.
        if (_attrTable.Count > 0 || _pendingWakeups.Count > 0) return 0;

        int oldTop = _heapTop;
        if (oldTop == 0) return 0;

        // ---- Phase 1: mark every cell reachable from the roots. ----
        bool[] marked = _gcMarked is { } m && m.Length >= oldTop ? m : (_gcMarked = new bool[oldTop]);
        System.Array.Clear(marked, 0, oldTop);
        int[] work = _gcWork is { } w && w.Length >= 1024 ? w : (_gcWork = new int[1024]);
        int workTop = 0;

        // Local mark/enqueue. Guards bounds defensively — a root should
        // never reference outside [0, oldTop), but a stray value must not
        // crash the collector.
        void MarkCell(int addr)
        {
            if ((uint)addr >= (uint)oldTop || marked[addr]) return;
            marked[addr] = true;
            if (workTop == work.Length)
            {
                System.Array.Resize(ref work, work.Length * 2);
                _gcWork = work;
            }
            work[workTop++] = addr;
        }

        // Enqueue the cells a value cell references (without marking the
        // value cell itself — used both for heap cells during the trace
        // and for root cells that live off-heap in registers / Y slots).
        void MarkReferents(Cell c)
        {
            switch (c.Tag)
            {
                case Tag.Ref:
                case Tag.AttVar:
                    MarkCell(c.AsHeapIndex);
                    break;
                case Tag.Str:
                {
                    int f = c.AsHeapIndex;
                    MarkCell(f);
                    var (_, arity) = FunctorTable.Lookup(_heap[f].AsFunctorId);
                    for (int i = 1; i <= arity; i++) MarkCell(f + i);
                    break;
                }
                case Tag.Lis:
                    MarkCell(c.AsHeapIndex);
                    MarkCell(c.AsHeapIndex + 1);
                    break;
                case Tag.Float:
                    MarkCell(c.FloatPairedIndex);
                    break;
                case Tag.Pstr:
                {
                    int bufStart = c.AsPstrBufferIndex;
                    int bufCount = (c.AsPstrOffset + c.AsPstrLength
                                    + Cell.PstrCodeUnitsPerBuffer - 1)
                                   / Cell.PstrCodeUnitsPerBuffer;
                    for (int i = 0; i < bufCount; i++) MarkCell(bufStart + i);
                    MarkCell(bufStart + bufCount);   // logical tail cell
                    break;
                }
                // Atom, Int, Functor, BigInt, String, Foreign, PstrBuffer: leaves.
            }
        }

        MarkRoots(MarkReferents, MarkCell, oldTop);

        // Trace to fixpoint.
        while (workTop > 0)
            MarkReferents(_heap[work[--workTop]]);

        // ---- Phase 2: forwarding addresses (order-preserving slide). ----
        // forward[i] = number of marked cells in [0, i). New address of a
        // live cell at i is forward[i]; a heap-top boundary p maps to
        // forward[p]; the new heap top is forward[oldTop].
        int[] forward = _gcForward is { } f2 && f2.Length >= oldTop + 1
            ? f2 : (_gcForward = new int[oldTop + 1]);
        int live = 0;
        for (int i = 0; i < oldTop; i++)
        {
            forward[i] = live;
            if (marked[i]) live++;
        }
        forward[oldTop] = live;

        if (live == oldTop) return 0;   // nothing to reclaim

        // ---- Phase 3: rewrite payloads in place (still at old positions). ----
        for (int i = 0; i < oldTop; i++)
            if (marked[i])
                _heap[i] = RelocateCell(_heap[i], forward);

        // ---- Phase 4: slide live cells left. forward[i] <= i and i is
        // increasing, so the destination is always already vacated. ----
        for (int i = 0; i < oldTop; i++)
            if (marked[i])
                _heap[forward[i]] = _heap[i];

        // ---- Phase 5: relocate every external holder of a heap index. ----
        RelocateRoots(forward, oldTop);

        _heapTop = live;
        _hb = forward[_hb];

        return oldTop - live;
    }

    /// <summary>Returns <paramref name="c"/> with its heap-index payload
    /// (if any) mapped through <paramref name="forward"/>. Atomic cells
    /// are returned unchanged.</summary>
    private static Cell RelocateCell(Cell c, int[] forward)
    {
        switch (c.Tag)
        {
            case Tag.Ref: return Cell.Ref(forward[c.AsHeapIndex]);
            case Tag.Str: return Cell.Str(forward[c.AsHeapIndex]);
            case Tag.Lis: return Cell.Lis(forward[c.AsHeapIndex]);
            case Tag.AttVar: return Cell.AttVar(forward[c.AsHeapIndex]);
            case Tag.Float:
                // Preserve tag + the 4 high mantissa/exponent bits (payload
                // bits 56..59); rewrite only the low-32-bit paired index.
                return new Cell((c.Data & unchecked((long)0xFFFFFFFF00000000L))
                                | (uint)forward[c.FloatPairedIndex]);
            case Tag.Pstr:
                return Cell.Pstr(c.AsPstrLength, forward[c.AsPstrBufferIndex], c.AsPstrOffset);
            default:
                return c;   // atomic / leaf
        }
    }

    /// <summary>Marks the cells reachable from every root: X registers
    /// (conservative — every slot is a valid Cell), the live permanents
    /// of every environment frame, every choice point's saved arguments,
    /// both trails, and the catch-frame heap slots.</summary>
    private void MarkRoots(System.Action<Cell> markReferents, System.Action<int> markCell, int oldTop)
    {
        // X registers — conservative: scan the whole bank. A stale slot
        // can only over-retain (keep a still-valid cell alive), never
        // corrupt, because every register holds a tagged Cell.
        for (int i = 0; i < _registers.Length; i++)
            markReferents(_registers[i]);

        // Environment frames reachable from the current continuation and
        // from every choice point's saved CE. Each frame's live-permanent
        // count sits at EnvNOffset (chunk 210).
        var seenFrames = new System.Collections.Generic.HashSet<int>();
        MarkEnvChain(_e, seenFrames, markReferents);

        // Choice points: walk the _b chain. Saved argument slots are
        // roots; the saved HeapTop / Hb are boundaries (relocated later,
        // not roots).
        int b = _b;
        while (b >= 0)
        {
            int arity = (int)_stack[b + CpArityOffset].Data;
            for (int i = 0; i < arity; i++)
                markReferents(_stack[b + CpArg1Offset + i]);
            MarkEnvChain((int)_stack[b + CpCeOffset(arity)].Data, seenFrames, markReferents);
            int prevB = (int)_stack[b + CpBOffset(arity)].Data;
            if (prevB == b) break;
            b = prevB;
        }

        // Binding trail: each entry is the heap address of a bound
        // variable that a backtrack will reset, so the cell must stay
        // valid (root) and the entry is relocated later.
        for (int k = 0; k < _bindingTrailTop; k++)
            markCell(_bindingTrail[k]);

        // Extra trail: ValueChange entries carry a heap address and an old
        // cell value that a backtrack restores; both are roots. The other
        // live types here (BigIntAlloc, CatchFrame) carry table/frame
        // indices, not heap addresses.
        for (int k = 0; k < _extraTrailTop; k++)
        {
            var e = _extraTrail[k];
            if (e.Type == TrailType.ValueChange)
            {
                markCell(e.HeapIdx);
                markReferents(e.OldValue);
            }
        }

        // Catch frames: the catcher and recovery terms are heap roots.
        for (int i = 0; i < _catchFrames.Count; i++)
        {
            CatchFrame cf = _catchFrames[i];
            markCell(cf.CatcherHeapIdx);
            markCell(cf.RecoveryHeapIdx);
        }
    }

    private void MarkEnvChain(int e, System.Collections.Generic.HashSet<int> seen,
        System.Action<Cell> markReferents)
    {
        while (e >= 0 && seen.Add(e))
        {
            int n = (int)_stack[e + EnvNOffset].Data;
            for (int i = 0; i < n; i++)
                markReferents(_stack[e + EnvY1Offset + i]);
            e = (int)_stack[e + EnvCeOffset].Data;
        }
    }

    private void RelocateRoots(int[] forward, int oldTop)
    {
        for (int i = 0; i < _registers.Length; i++)
            _registers[i] = RelocateCell(_registers[i], forward);

        var seenFrames = new System.Collections.Generic.HashSet<int>();
        RelocateEnvChain(_e, seenFrames, forward);

        int b = _b;
        while (b >= 0)
        {
            int arity = (int)_stack[b + CpArityOffset].Data;
            for (int i = 0; i < arity; i++)
                _stack[b + CpArg1Offset + i] = RelocateCell(_stack[b + CpArg1Offset + i], forward);
            // Saved HeapTop / Hb are boundaries.
            int htOff = b + CpHeapTopOffset(arity);
            _stack[htOff] = new Cell(forward[(int)_stack[htOff].Data]);
            int hbOff = b + CpHbOffset(arity);
            _stack[hbOff] = new Cell(forward[(int)_stack[hbOff].Data]);
            RelocateEnvChain((int)_stack[b + CpCeOffset(arity)].Data, seenFrames, forward);
            int prevB = (int)_stack[b + CpBOffset(arity)].Data;
            if (prevB == b) break;
            b = prevB;
        }

        for (int k = 0; k < _bindingTrailTop; k++)
            _bindingTrail[k] = forward[_bindingTrail[k]];

        for (int k = 0; k < _extraTrailTop; k++)
        {
            var e = _extraTrail[k];
            if (e.Type == TrailType.ValueChange)
            {
                e.HeapIdx = forward[e.HeapIdx];
                e.OldValue = RelocateCell(e.OldValue, forward);
                _extraTrail[k] = e;
            }
        }

        for (int i = 0; i < _catchFrames.Count; i++)
        {
            CatchFrame cf = _catchFrames[i];
            cf.CatcherHeapIdx = forward[cf.CatcherHeapIdx];
            cf.RecoveryHeapIdx = forward[cf.RecoveryHeapIdx];
            cf.SnapHeapTop = forward[cf.SnapHeapTop];
            cf.SnapHb = forward[cf.SnapHb];
            _catchFrames[i] = cf;
        }
    }

    private void RelocateEnvChain(int e, System.Collections.Generic.HashSet<int> seen, int[] forward)
    {
        while (e >= 0 && seen.Add(e))
        {
            int n = (int)_stack[e + EnvNOffset].Data;
            for (int i = 0; i < n; i++)
            {
                int slot = e + EnvY1Offset + i;
                _stack[slot] = RelocateCell(_stack[slot], forward);
            }
            e = (int)_stack[e + EnvCeOffset].Data;
        }
    }
}
