namespace Shumway.Core;

/// <summary>
/// ADR-016 — order-preserving sliding mark-compact collector for the
/// <see cref="_heap"/> <see cref="Cell"/> array. Reclaims unreachable
/// cells by reachability, independently of the choice-point stack: open
/// choice points no longer pin garbage, only genuinely reachable state
/// survives. Called at engine safe points (see the watermark hook and
/// <c>garbage_collect/0</c>).
///
/// <para>First cut: attributed variables are out of scope —
/// the collector bails (no-op) when any attvar state is present, which
/// is always safe. Attvar relocation (the attr table keyed by home
/// index, the attr-modify side log, pending wakeups) is a follow-up.</para>
/// </summary>
public sealed partial class Activation
{
    // Scratch buffers reused across collections to avoid per-GC churn.
    private bool[]? _gcMarked;
    private int[]? _gcForward;
    private int[]? _gcWork;
    // mark-phase state for the de-closured GcMarkCell /
    // GcMarkReferents (they were closure-capturing locals invoked through
    // Action<int> / Action<Cell> — a delegate call per register / stack
    // slot / trail entry per collection). Valid only inside CollectHeap.
    private int _gcWorkTop;
    private int _gcOldTop;

    // Adaptive auto-collection threshold (cells). Starts at the config
    // value; after each collection it is raised to twice the surviving
    // live size so a genuinely large live set does not re-trigger every
    // goal. 0 disables automatic collection.
    private int _gcThreshold;
    private bool _gcStressMode;

    /// <summary>Mark-phase hook for roots the engine itself cannot see —
    /// heap references held by higher layers (the embedding's
    /// query-variable index table, the global-variable store, …). The
    /// engine invokes it with two primitives: <c>markCell(int)</c> keeps
    /// the cell at a heap index alive; <c>markReferents(Cell)</c> marks
    /// what a value cell points at. Implementations must enumerate every
    /// heap index / cell they hold so it is neither collected nor left
    /// dangling after compaction.</summary>
    public System.Action<System.Action<int>, System.Action<Cell>>? OnGcMark { get; set; }

    /// <summary>Relocate-phase counterpart of <see cref="OnGcMark"/>,
    /// invoked after compaction with <c>relocIndex(int)</c> (old heap
    /// index → new), <c>relocCell(Cell)</c> (rewrites a value cell's
    /// heap-index payload) and <c>relocBoundary(int)</c> (old heap-TOP
    /// boundary → new; boundaries range over [0, oldTop] inclusive, one
    /// past the last cell — a debugger's saved allocation points need this
    /// form, ADR-035 D5+). Implementations must write the relocated
    /// indices / cells back into their own storage.</summary>
    public System.Action<System.Func<int, int>, System.Func<Cell, Cell>,
        System.Func<int, int>>? OnGcRelocate { get; set; }

    /// <summary>When true, <see cref="MaybeCollectHeap"/> collects at
    /// every safe point — the ADR-016 fuzz mode used to validate
    /// relocation against every query shape in the test suite.</summary>
    public bool GcStressMode
    {
        get => _gcStressMode;
        set { _gcStressMode = value; UpdateGcDiagActive(); }
    }

    // single "any diag knob set" flag, maintained at the
    // places _gcStressMode / _gcOnlyAt / _gcUpTo are written (this setter
    // + DiagReadGcOverrides), so the steady-state MaybeCollectHeap check
    // is one volatile read + one fused compare instead of five sequential
    // field tests per safe point.
    private bool _gcDiagActive;

    private void UpdateGcDiagActive()
        => _gcDiagActive = _gcStressMode || _gcOnlyAt >= 0 || _gcUpTo >= 0;

    /// <summary>Called at engine safe points (goal boundaries — see the
    /// interpreter's dispatch / resume-marker sites) to run a collection
    /// when heap occupancy has crossed the adaptive watermark. A no-op
    /// when auto-collection is disabled and stress mode is off. Safe for
    /// both tiers: at a goal boundary all live heap references are in the
    /// engine (registers / Y slots / choice points / trails); Tier-1 IL
    /// keeps the same WAM state in the engine arrays (its CLR locals are
    /// intra-instruction temporaries holding nothing across these points).</summary>
    // Bisection: when SHUMWAY_GC_AT=N is set, collect at exactly the Nth
    // safe point (single shot) so a corrupting collection can be pinned
    // down by binary search over N. -1 disables.
    private int _gcOnlyAt = -1;
    private int _gcUpTo = -1;
    private int _gcSafePointCount;

    /// <summary>Total safe points seen — diagnostic for GC bisection.</summary>
    public int GcSafePointCount => _gcSafePointCount;

    // Cooperative cancellation (theme 2): the embedding layer sets this from a
    // CancellationToken; the interpreter observes it the next time the heap GC
    // watermark is crossed inside MaybeCollectHeap (NOT every goal — see the
    // note there) and aborts the query by throwing OperationCanceledException,
    // which propagates to the host (it is NOT a Prolog ball, so catch/3 never
    // intercepts it). Checking only at the already-paid-for watermark keeps the
    // common per-goal path free; the trade-off is that a heap-bounded loop is
    // not cancellable. volatile so a request from another thread is seen
    // promptly.
    private volatile bool _cancelRequested;

    /// <summary>Requests that the running query abort at the next heap GC
    /// watermark crossing (a query that allocates no heap is not cancellable).
    /// Thread-safe; the engine that observes it throws
    /// <see cref="OperationCanceledException"/>.</summary>
    public void RequestCancellation() => _cancelRequested = true;

    /// <summary>Clears a pending cancellation request (e.g. before reusing an
    /// engine).</summary>
    public void ClearCancellation() => _cancelRequested = false;

    /// <summary>True once <see cref="RequestCancellation"/> has been called and
    /// not yet cleared.</summary>
    public bool IsCancellationRequested => _cancelRequested;

    // Backtrack-path cancellation throttle. A backtrackable-BUILTIN loop
    // (`between(_,_,_), fail` / `repeat, fail`) re-satisfies through a builtin
    // choice point and never crosses a call-boundary MaybeCollectHeap, so it
    // would be uncancellable. TryBacktrack calls BacktrackSafePoint when popping
    // such a choice point — but it only reads the (volatile) cancel flag once
    // every BacktrackCancelInterval pops. The per-pop cost is a single
    // non-volatile decrement + a predicted-not-taken branch, so it is negligible;
    // cancellation latency is bounded to a few thousand pops (microseconds).
    // Clause-backtracking loops re-satisfy via Call and are already cancellable
    // there, so they never reach this and pay nothing.
    private const int BacktrackCancelInterval = 4096;
    private int _backtrackCancelCountdown = BacktrackCancelInterval;

    /// <summary>Cheap cancellation poll for the backtrackable-builtin resume
    /// path — see <see cref="RequestCancellation"/>. Throttled by a counter so
    /// the volatile flag is read only periodically; throws
    /// <see cref="OperationCanceledException"/> when a cancellation is pending.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void BacktrackSafePoint()
    {
        if (--_backtrackCancelCountdown > 0) return;
        _backtrackCancelCountdown = BacktrackCancelInterval;
        if (_cancelRequested) ThrowQueryCancelled();
    }

    // hot/cold split. The guard is AggressiveInlining so each
    // safe-point call site inlines to: a volatile _cancelRequested read +
    // one compare (the diag flag and the watermark, fused into a single
    // early return on the steady-state path). Everything that can actually
    // do work — cancellation throw, diag modes, the collection + re-arm —
    // lives in the NoInlining slow body. NB: when auto-collection is
    // disabled (_gcThreshold <= 0) the `_heapTop < _gcThreshold` test is
    // never true, so those (test-only) configurations pay the cold call;
    // the slow body's threshold check keeps the no-collect behavior.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void MaybeCollectHeap()
    {
        // Cooperative cancellation: checked at EVERY safe point (not only at the
        // GC watermark). Lazy Y-slot allocation made many loops heap-light, so a
        // watermark-only check left them uncancellable — and the watermark may
        // now never be crossed. `_cancelRequested` is volatile and the branch is
        // predicted not-taken, so the steady-state cost is ~one read per safe
        // point.
        if (_cancelRequested)
            ThrowQueryCancelled();
        // ADR-035 D5+ — a lazily-opened debug session arming itself mid-run (a
        // debugger just attached): applied HERE, on the engine's own thread at a
        // goal boundary, because the watcher thread that noticed the attach must
        // never mutate a running activation. Same cost class as the cancel flag.
        if (_debugArmPending)
            ApplyDebugArm();
        if (!_gcDiagActive && _heapTop < _gcThreshold) return;
        MaybeCollectHeapSlow();
    }

    // ADR-035 D5+ — the pending arm. Volatile: set from the session's watcher
    // thread, read here every safe point.
    private volatile bool _debugArmPending;
    private System.Action<Activation>? _debugArmAction;

    /// <summary>Asks this activation to run <paramref name="arm"/> on its OWN thread at
    /// the next safe point — the one sound way a watcher thread turns full debug on for
    /// a machine that is mid-run. Harmless on an activation that already finished: the
    /// flag is simply never consumed.</summary>
    public void RequestDebugArm(System.Action<Activation> arm)
    {
        _debugArmAction = arm;
        _debugArmPending = true;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ApplyDebugArm()
    {
        _debugArmPending = false;
        var arm = _debugArmAction;
        _debugArmAction = null;
        arm?.Invoke(this);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowQueryCancelled()
        => throw new OperationCanceledException("Prolog query cancelled at a safe point.");

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void MaybeCollectHeapSlow()
    {
        if (_gcOnlyAt >= 0)
        {
            _gcSafePointCount++;
            if (_gcSafePointCount == _gcOnlyAt) CollectHeap();
            return;
        }
        if (_gcUpTo >= 0)
        {
            _gcSafePointCount++;
            if (_gcSafePointCount <= _gcUpTo) CollectHeap();
            return;
        }
        if (_gcStressMode)
        {
            _gcSafePointCount++;
            CollectHeap();
            return;
        }
        if (_gcThreshold <= 0 || _heapTop < _gcThreshold) return;
        // Watermark crossed (~once per GcThreshold of heap allocation).
        CollectHeap();
        // Re-arm: collect again only once the heap doubles past what
        // survived (bounded below by the configured floor). If the
        // collection freed nothing (e.g. attvars in use, or a genuinely
        // large live set), this still pushes the threshold up so we do
        // not retry on the very next goal.
        long next = (long)_heapTop * 2;
        int floor = _config.GcThreshold > 0 ? _config.GcThreshold : (1 << 18);
        _gcThreshold = next > int.MaxValue ? int.MaxValue
            : (int)System.Math.Max(next, floor);
    }

    /// <summary>ADR-035 D5+ — how many collections have actually run (compacting the heap
    /// and rewriting the trail). The debugger's rewind history records this per mark: a
    /// mark taken before a collection indexes a heap and a trail that no longer exist,
    /// and matching the count is how such marks are recognised and refused.</summary>
    public int HeapGcCount { get; private set; }

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
        // also carry heap indices. Relocating all of that correctly is
        // future work; until then a no-op is the safe choice.
        if (_attrTable.Count > 0 || _pendingWakeups.Count > 0) return 0;

        int oldTop = _heapTop;
        if (oldTop == 0) return 0;

        // ---- mark every cell reachable from the roots. ----
        bool[] marked = _gcMarked is { } m && m.Length >= oldTop ? m : (_gcMarked = new bool[oldTop]);
        System.Array.Clear(marked, 0, oldTop);
        if (_gcWork is null || _gcWork.Length < 1024) _gcWork = new int[1024];
        // the mark primitives are private methods over these
        // fields (direct calls from MarkRoots / the trace loop); delegates
        // remain only for the external OnGcMark hook.
        _gcWorkTop = 0;
        _gcOldTop = oldTop;

        MarkRoots(oldTop);
        // Pending b_setval restores hold old-value cells (possibly compounds).
        MarkExternalTrailRoots(GcMarkReferents);
        OnGcMark?.Invoke(GcMarkCell, GcMarkReferents);

        // Trace to fixpoint. (_gcWork can be resized by GcMarkCell, so
        // re-read the field each iteration.)
        while (_gcWorkTop > 0)
            GcMarkReferents(_heap[_gcWork[--_gcWorkTop]]);

        // ---- forwarding addresses (order-preserving slide). ----
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

        if (live == oldTop) return 0;   // nothing to reclaim (and nothing moved:
                                        // debugger marks etc. stay valid as they are,
                                        // which is why HeapGcCount bumps only below)
        HeapGcCount++;

        // ---- rewrite payloads in place (still at old positions). ----
        for (int i = 0; i < oldTop; i++)
            if (marked[i])
                _heap[i] = RelocateCell(_heap[i], forward);

        // ---- slide live cells left. forward[i] <= i and i is
        // increasing, so the destination is always already vacated. ----
        for (int i = 0; i < oldTop; i++)
            if (marked[i])
                _heap[forward[i]] = _heap[i];

        // ---- relocate every external holder of a heap index. ----
        RelocateRoots(forward, oldTop);
        RelocateExternalTrail(c => RelocateCell(c, forward));
        OnGcRelocate?.Invoke(
            idx => RelocIndex(idx, forward),
            c => RelocateCell(c, forward),
            p => RelocBoundary(p, forward));

        _heapTop = live;
        _hb = RelocBoundary(_hb, forward);

        return oldTop - live;
    }

    /// <summary>Returns <paramref name="c"/> with its heap-index payload
    /// (if any) mapped through <paramref name="forward"/>. Atomic cells
    /// are returned unchanged.</summary>
    private static Cell RelocateCell(Cell c, int[] forward)
    {
        // forward has length oldTop+1; a valid heap index is in [0, oldTop).
        int oldTop = forward.Length - 1;
        switch (c.Tag)
        {
            case Tag.Ref:
                return InBounds(c.AsHeapIndex, oldTop) ? Cell.Ref(forward[c.AsHeapIndex]) : c;
            case Tag.Str:
                return InBounds(c.AsHeapIndex, oldTop) ? Cell.Str(forward[c.AsHeapIndex]) : c;
            case Tag.Lis:
                return InBounds(c.AsHeapIndex, oldTop) ? Cell.Lis(forward[c.AsHeapIndex]) : c;
            case Tag.AttVar:
                return InBounds(c.AsHeapIndex, oldTop) ? Cell.AttVar(forward[c.AsHeapIndex]) : c;
            case Tag.Float:
                // Preserve tag + the 4 high mantissa/exponent bits (payload
                // bits 56..59); rewrite only the low-32-bit paired index.
                return InBounds(c.FloatPairedIndex, oldTop)
                    ? new Cell((c.Data & unchecked((long)0xFFFFFFFF00000000L))
                               | (uint)forward[c.FloatPairedIndex])
                    : c;
            case Tag.Pstr:
                return InBounds(c.AsPstrBufferIndex, oldTop)
                    ? Cell.Pstr(c.AsPstrLength, forward[c.AsPstrBufferIndex], c.AsPstrOffset)
                    : c;
            default:
                return c;   // atomic / leaf
        }
    }

    private static bool InBounds(int idx, int oldTop) => (uint)idx < (uint)oldTop;

    // de-closured mark/enqueue (was a closure local invoked
    // through Action<int>). Guards bounds defensively — a root should
    // never reference outside [0, _gcOldTop), but a stray value must not
    // crash the collector.
    private void GcMarkCell(int addr)
    {
        bool[] marked = _gcMarked!;
        if ((uint)addr >= (uint)_gcOldTop || marked[addr]) return;
        marked[addr] = true;
        int[] work = _gcWork!;
        if (_gcWorkTop == work.Length)
        {
            System.Array.Resize(ref work, work.Length * 2);
            _gcWork = work;
        }
        work[_gcWorkTop++] = addr;
    }

    // de-closured referent enqueue (was a closure local invoked
    // through Action<Cell>). Enqueues the cells a value cell references
    // (without marking the value cell itself — used both for heap cells
    // during the trace and for root cells that live off-heap in registers /
    // Y slots). The whole stack is scanned conservatively (see MarkRoots),
    // so a value cell can be a genuine live reference OR a stale leftover in
    // a dead slot. Every payload is bounds-guarded, and a Str is only
    // followed when its target really is a Functor cell — a stale Str
    // pointing at a non-functor (or out of range) is left as a leaf
    // rather than chasing garbage into FunctorTable. Control words are
    // never seen here: they are Tag.RawInt, which falls through to the
    // leaf default. The worst a stale-but-plausible ref can do is
    // over-retain (keep a still-addressable cell alive) — never crash
    // and never corrupt.
    private void GcMarkReferents(Cell c)
    {
        int oldTop = _gcOldTop;
        switch (c.Tag)
        {
            case Tag.Ref:
            case Tag.AttVar:
                GcMarkCell(c.AsHeapIndex);
                break;
            case Tag.Str:
            {
                int f = c.AsHeapIndex;
                if ((uint)f >= (uint)oldTop || _heap[f].Tag != Tag.Functor) break;
                GcMarkCell(f);
                var (_, arity) = FunctorTable.Lookup(_heap[f].AsFunctorId);
                for (int i = 1; i <= arity && f + i < oldTop; i++) GcMarkCell(f + i);
                break;
            }
            case Tag.Lis:
            {
                int h = c.AsHeapIndex;
                if ((uint)(h + 1) >= (uint)oldTop) break;
                GcMarkCell(h);
                GcMarkCell(h + 1);
                break;
            }
            case Tag.Float:
                GcMarkCell(c.FloatPairedIndex);
                break;
            case Tag.Pstr:
            {
                int bufStart = c.AsPstrBufferIndex;
                int bufCount = (c.AsPstrOffset + c.AsPstrLength
                                + Cell.PstrCodeUnitsPerBuffer - 1)
                               / Cell.PstrCodeUnitsPerBuffer;
                if ((uint)(bufStart + bufCount) >= (uint)oldTop) break;
                for (int i = 0; i < bufCount; i++) GcMarkCell(bufStart + i);
                GcMarkCell(bufStart + bufCount);   // logical tail cell
                break;
            }
            // Atom, Int, Functor, BigInt, Rational, String, Foreign,
            // PstrBuffer, RawInt (control words): leaves.
        }
    }

    // Relocate a bare heap-address root, leaving an out-of-range index
    // (a transient/dead slot, never a live heap cell) untouched.
    private static int RelocIndex(int idx, int[] forward)
        => (uint)idx < (uint)(forward.Length - 1) ? forward[idx] : idx;

    // Relocate a heap-TOP boundary (valid range [0, oldTop], inclusive —
    // forward[oldTop] is the new live count). A stale/garbage boundary
    // outside that range is left untouched rather than crashing.
    private static int RelocBoundary(int p, int[] forward)
        => (uint)p < (uint)forward.Length ? forward[p] : p;

    /// <summary>Marks the cells reachable from every root. The X register
    /// bank and the entire control stack are scanned <i>conservatively</i>:
    /// every slot in [0, _stackTop) is fed to <see cref="MarkReferents"/>.
    /// This is both safe and complete because control words (CE, CP, B, BP,
    /// arity, trail tops, HeapTop, Hb, ViewGen, B0, perm-count) are stored
    /// as <see cref="Tag.RawInt"/> cells — never <see cref="Tag.Ref"/> — so
    /// MarkReferents treats them as leaves, while every genuine heap
    /// reference (in a register, a frame Y-slot, or a CP saved argument) is
    /// a real tagged cell that gets marked regardless of which frame owns
    /// it. A stale reference in a dead slot can only over-retain a still-
    /// valid cell, never corrupt — and never crashes, thanks to the
    /// bounds + Str-functor guards in GcMarkReferents. This avoids the
    /// fragile precise frame-liveness walk, which under-counted roots in
    /// the tabling fixpoint's reused stack. Trails and catch frames carry
    /// bare heap indices, so they are marked explicitly. calls
    /// the de-closured <see cref="GcMarkReferents"/> /
    /// <see cref="GcMarkCell"/> directly — no per-slot delegate invoke.</summary>
    private void MarkRoots(int oldTop)
    {
        // X registers — conservative: scan the whole bank.
        for (int i = 0; i < _registers.Length; i++)
            GcMarkReferents(_registers[i]);

        // Entire control stack — conservative. Control words are RawInt
        // (leaves); every real ref is marked no matter which frame it is in.
        for (int i = 0; i < _stackTop; i++)
            GcMarkReferents(_stack[i]);

        // Binding trail: each entry is the heap address of a bound
        // variable that a backtrack will reset, so the cell must stay
        // valid (root) and the entry is relocated later.
        for (int k = 0; k < _bindingTrailTop; k++)
            GcMarkCell(_bindingTrail[k]);

        // Extra trail: ValueChange entries carry a heap address and an old
        // cell value that a backtrack restores; both are roots. The other
        // live types here (BigIntAlloc, CatchFrame) carry table/frame
        // indices, not heap addresses.
        for (int k = 0; k < _extraTrailTop; k++)
        {
            var e = _extraTrail[k];
            if (e.Type == TrailType.ValueChange)
            {
                GcMarkCell(e.HeapIdx);
                GcMarkReferents(e.OldValue);
            }
        }

        // Catch frames: the catcher and recovery terms are heap roots. The
        // SnapE / RecoveryE environment chains are covered by
        // ComputeFrameLiveCounts (scanned above), so only the heap terms
        // are marked here.
        for (int i = 0; i < _catchFrames.Count; i++)
        {
            CatchFrame cf = _catchFrames[i];
            GcMarkCell(cf.CatcherHeapIdx);
            GcMarkCell(cf.RecoveryHeapIdx);
        }
    }

    /// <summary>Rewrites every external holder of a heap index through the
    /// forwarding map. The register bank and the whole control stack are
    /// relocated conservatively (matching <see cref="MarkRoots"/>):
    /// <see cref="RelocateCell"/> rewrites genuine reference cells and
    /// returns control words (<see cref="Tag.RawInt"/>) and other leaves
    /// unchanged. The one exception is each choice point's saved HeapTop /
    /// Hb, which are RawInt-tagged heap-top <i>boundaries</i> (not cell
    /// references) that must be mapped through <see cref="RelocBoundary"/>;
    /// the CP chain is walked separately to fix exactly those two slots per
    /// frame. Trails and catch frames hold bare indices, relocated
    /// explicitly.</summary>
    private void RelocateRoots(int[] forward, int oldTop)
    {
        for (int i = 0; i < _registers.Length; i++)
            _registers[i] = RelocateCell(_registers[i], forward);

        // Whole control stack — conservative. RawInt control words pass
        // through RelocateCell's leaf default untouched; every real
        // reference cell (frame Y / CP arg) is relocated in place.
        for (int i = 0; i < _stackTop; i++)
            _stack[i] = RelocateCell(_stack[i], forward);

        // CP chain: the only stack slots needing boundary (not cell)
        // relocation are each frame's saved HeapTop / Hb. Re-write them as
        // RawInt so they retain the control-word tag.
        int b = _b;
        while (b >= 0 && b < _stackTop)
        {
            int arity = (int)_stack[b + CpArityOffset].Data;
            if (arity < 0 || arity > 4096 || b + CpSize(arity) > _stackTop) break;
            int htOff = b + CpHeapTopOffset(arity);
            _stack[htOff] = Cell.RawInt(RelocBoundary((int)_stack[htOff].Data, forward));
            int hbOff = b + CpHbOffset(arity);
            _stack[hbOff] = Cell.RawInt(RelocBoundary((int)_stack[hbOff].Data, forward));
            int prevB = (int)_stack[b + CpBOffset(arity)].Data;
            if (prevB == b) break;
            b = prevB;
        }

        for (int k = 0; k < _bindingTrailTop; k++)
            _bindingTrail[k] = RelocIndex(_bindingTrail[k], forward);

        for (int k = 0; k < _extraTrailTop; k++)
        {
            var e = _extraTrail[k];
            if (e.Type == TrailType.ValueChange)
            {
                e.HeapIdx = RelocIndex(e.HeapIdx, forward);
                e.OldValue = RelocateCell(e.OldValue, forward);
                _extraTrail[k] = e;
            }
        }

        for (int i = 0; i < _catchFrames.Count; i++)
        {
            CatchFrame cf = _catchFrames[i];
            cf.CatcherHeapIdx = RelocIndex(cf.CatcherHeapIdx, forward);
            cf.RecoveryHeapIdx = RelocIndex(cf.RecoveryHeapIdx, forward);
            cf.SnapHeapTop = RelocBoundary(cf.SnapHeapTop, forward);
            cf.SnapHb = RelocBoundary(cf.SnapHb, forward);
            _catchFrames[i] = cf;
        }
    }
}
