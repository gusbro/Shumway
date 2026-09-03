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
    // Cells newly marked since the current mark began — the root-attribution
    // probe reads deltas of this to charge retained cells to specific roots.
    private int _gcMarkCount;
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

    // call_with_timeout/2,3 deadlines, checked at the same safe points as
    // cancellation — which is what lets `(repeat, fail)` time out: it grows no
    // heap and never crosses a call boundary, so only the backtrack poll sees
    // it. A stack, because the calls nest; the EFFECTIVE deadline is the
    // EARLIEST, so an inner call can tighten the budget but never extend past
    // a promise an outer one already made.
    private long[] _deadlines = new long[4];
    private int _deadlineCount;
    private long _deadlineAt;   // 0 = none active; the earliest of the stack

    /// <summary>Starts a deadline <paramref name="seconds"/> from now.</summary>
    public void PushDeadline(double seconds)
    {
        if (_deadlineCount == _deadlines.Length)
            System.Array.Resize(ref _deadlines, _deadlineCount * 2);
        long at = System.Environment.TickCount64 + (long)(seconds * 1000.0);
        _deadlines[_deadlineCount++] = at;
        if (_deadlineAt == 0 || at < _deadlineAt) _deadlineAt = at;
    }

    /// <summary>Ends the innermost deadline. Idempotent on an empty stack so an
    /// unwinding path can pop without first proving it pushed.</summary>
    public void PopDeadline()
    {
        if (_deadlineCount == 0) return;
        _deadlineCount--;
        // Recompute rather than restore: the popped one may not have been the
        // earliest (an outer, tighter deadline still governs).
        _deadlineAt = 0;
        for (int i = 0; i < _deadlineCount; i++)
            if (_deadlineAt == 0 || _deadlines[i] < _deadlineAt)
                _deadlineAt = _deadlines[i];
    }

    /// <summary>True while any <c>call_with_timeout</c> is in progress.</summary>
    public bool HasDeadline => _deadlineAt != 0;

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void CheckDeadline()
    {
        if (System.Environment.TickCount64 < _deadlineAt) return;
        // A bare ball, not an error/2: call_with_timeout/2 in the prelude
        // catches it and rethrows it as timeout(Goal), which is the only
        // shape a program should ever see.
        throw new PrologRuntimeException("$timeout_expired");
    }

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
        if (_deadlineAt != 0) CheckDeadline();
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
        // call_with_timeout/2,3 — same cost class as the cancel flag (one field
        // read, predicted not-taken) whenever no deadline is active.
        if (_deadlineAt != 0)
            CheckDeadline();
        // ADR-035 D5+ — a lazily-opened debug session arming itself mid-run (a
        // debugger just attached): applied HERE, on the engine's own thread at a
        // goal boundary, because the watcher thread that noticed the attach must
        // never mutate a running activation. Same cost class as the cancel flag.
        if (_debugArmPending)
            ApplyDebugArm();
        if (!_gcDiagActive && _heapTop < _gcThreshold) return;
        MaybeCollectHeapSlow();
    }

    // WAM X registers are caller-saved: at a call boundary the live ones are
    // exactly the callee's arguments, and after a return none are (the caller
    // reloads from Y slots). A stale high register would otherwise root a
    // dead structure for the rest of the query — the classic "one register
    // pins 400k cells" retention. -1 = unknown provenance, scan the whole
    // bank (the safe default every legacy caller keeps).
    private int _gcLiveRegisterBound = -1;

    /// <summary>Safe point at a call boundary where the callee's FUNCTOR is
    /// in hand: only its arguments are live registers. Same steady-state cost
    /// as <see cref="MaybeCollectHeap"/> — the arity lookup happens on the
    /// collection path only.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void MaybeCollectHeapAtCall(int functorId)
    {
        if (_cancelRequested)
            ThrowQueryCancelled();
        if (_deadlineAt != 0)
            CheckDeadline();
        if (_debugArmPending)
            ApplyDebugArm();
        if (!_gcDiagActive && _heapTop < _gcThreshold) return;
        MaybeCollectHeapSlowAtCall(functorId);
    }

    /// <summary>Safe point at a dispatch whose target may be a resume marker
    /// (functor recoverable — precise) or a raw code address (not — the
    /// conservative full-bank scan stands in).</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void MaybeCollectHeapAtDispatch(int target)
    {
        if (_cancelRequested)
            ThrowQueryCancelled();
        if (_deadlineAt != 0)
            CheckDeadline();
        if (_debugArmPending)
            ApplyDebugArm();
        if (!_gcDiagActive && _heapTop < _gcThreshold) return;
        if (IsResumeMarker(target))
            MaybeCollectHeapSlowAtCall(DecodeResumeMarker(target).FunctorId);
        else
            MaybeCollectHeapSlow();
    }

    /// <summary>Safe point where a callee has just Proceeded back: no X
    /// register is live — the resuming caller reloads from its frame.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void MaybeCollectHeapAtReturn()
    {
        if (_cancelRequested)
            ThrowQueryCancelled();
        if (_deadlineAt != 0)
            CheckDeadline();
        if (_debugArmPending)
            ApplyDebugArm();
        if (!_gcDiagActive && _heapTop < _gcThreshold) return;
        MaybeCollectHeapSlowBounded(0);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void MaybeCollectHeapSlowAtCall(int functorId)
        => MaybeCollectHeapSlowBounded(FunctorTable.Lookup(functorId).Arity);

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void MaybeCollectHeapSlowBounded(int liveRegisters)
    {
        _gcLiveRegisterBound = liveRegisters;
        try { MaybeCollectHeapSlow(); }
        finally { _gcLiveRegisterBound = -1; }
    }

    /// <summary>Explicit collection with a known live-register bound —
    /// <c>garbage_collect/0</c> (arity 0: nothing is live in the bank).</summary>
    public int CollectHeapBounded(int liveRegisters)
    {
        _gcLiveRegisterBound = liveRegisters;
        try { return CollectHeap(); }
        finally { _gcLiveRegisterBound = -1; }
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
    // Non-zero while a region is running that holds heap indices outside the
    // root set — the attribute wakeup drain. Raised/lowered in pairs.
    private int _gcInhibit;

    /// <summary>Suppresses collection while a half-built structure is held in
    /// C# locals. Nests; every raise must be paired with a lower.</summary>
    public void EnterGcInhibit() => _gcInhibit++;
    public void ExitGcInhibit() => _gcInhibit--;

    public int HeapGcCount { get; private set; }

    /// <summary>How many times the collector RAN, whether or not it found
    /// anything to move. The two numbers differ by the collections that marked
    /// the whole heap live and returned without compacting, and telling them
    /// apart is the difference between "the collector never engaged" and "it
    /// engaged and there was nothing to give back" — which
    /// <c>statistics/0</c> reported as the same thing, `0 collections`, on a
    /// query whose memory came back through BACKTRACKING rather than through
    /// the collector.</summary>
    public int HeapGcRuns { get; private set; }

    /// <summary>Cells reclaimed across every collection of this activation —
    /// the companion of <see cref="HeapGcCount"/> for statistics/0.</summary>
    public long HeapGcReclaimedCells { get; private set; }

    /// <summary>Runs a mark-compact collection of the heap. Returns the
    /// number of cells reclaimed (0 if the collector bailed). Safe to
    /// call only at a safe point — between WAM instructions — where the
    /// machine state is consistent (no half-built structure).</summary>
    public int CollectHeap()
    {
        // The collector used to bail here whenever an attributed variable or a
        // setup_call_cleanup handler was live, which meant a single dif/2
        // turned it off for the rest of the query. Those structures hold heap
        // indices the heap walk does not reach, so they are marked and
        // relocated explicitly — see MarkExternalHolders /
        // RelocateExternalHolders.
        //
        // What that bail ALSO did, without saying so, was make the wakeup drain
        // atomic with respect to collection: it builds a verify_attributes goal
        // from heap indices held in C# locals and then meta-calls it, and a
        // collection in the middle leaves those locals naming moved cells. That
        // is what GcInhibit protects now — a bounded region rather than the
        // rest of the query.
        if (_gcInhibit > 0) return 0;

        int oldTop = _heapTop;
        if (oldTop == 0) return 0;
        HeapGcRuns++;

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
        // Attributed variables: the attr table is keyed by the variable's home
        // heap index and its entries hold attribute-term indices, so both are
        // roots. Same for the transient wakeup queue. Marking them is the half
        // of "collect with attvars live" that is purely additive; relocating
        // them is the other half.
        MarkExternalHolders(oldTop);
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
        HeapGcReclaimedCells += oldTop - live;

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
        RelocateExternalHolders(forward);
        RelocateExternalTrail(c => RelocateCell(c, forward));
        OnGcRelocate?.Invoke(
            idx => RelocIndex(idx, forward),
            c => RelocateCell(c, forward),
            p => RelocBoundary(p, forward));

        _heapTop = live;
        _hb = RelocBoundary(_hb, forward);

        return oldTop - live;
    }

    /// <summary>Marks the roots that live OUTSIDE the heap-root set. Each of
    /// these is a structure the collector does not otherwise walk but which
    /// holds heap indices, so a cell reachable only from one of them would be
    /// freed under a live reference.
    ///
    /// <para>The attribute trail log is the subtle one: it holds an attribute's
    /// PREVIOUS value so backtracking can restore it. Nothing else need
    /// reference that term — the current value replaced it — so without this it
    /// is collected and backtracking restores a dangling index.</para></summary>
    private void MarkExternalHolders(int oldTop)
    {
        foreach (var kv in _attrTable)
        {
            if ((uint)kv.Key < (uint)oldTop) GcMarkCell(kv.Key);
            foreach (var (_, attrValueIdx) in kv.Value)
                if ((uint)attrValueIdx < (uint)oldTop) GcMarkCell(attrValueIdx);
        }
        foreach (var (home, _, oldValue) in _attrTrailLog)
        {
            if (home == int.MinValue) continue;   // dead record (cut-dropped entry)
            if ((uint)home < (uint)oldTop) GcMarkCell(home);
            if ((uint)oldValue < (uint)oldTop) GcMarkCell(oldValue);
        }
        foreach (var (_, attrValueIdx, otherIdx, attvarHome) in _pendingWakeups)
        {
            if ((uint)attrValueIdx < (uint)oldTop) GcMarkCell(attrValueIdx);
            if ((uint)otherIdx < (uint)oldTop) GcMarkCell(otherIdx);
            if ((uint)attvarHome < (uint)oldTop) GcMarkCell(attvarHome);
        }
        MarkCleanupRoots();
    }

    /// <summary>Relocates every external holder's heap indices. Runs in the
    /// same pass as the rest of the relocation: compaction reuses addresses, so
    /// an index left unmapped can come to name an unrelated cell.</summary>
    private void RelocateExternalHolders(int[] forward)
    {
        // The attribute table is rebuilt: its KEYS are heap indices, so this is
        // a re-key, not an in-place edit.
        if (_attrTable.Count > 0)
        {
            var moved = new System.Collections.Generic.List<(int Home,
                System.Collections.Generic.Dictionary<int, int> Record)>(_attrTable.Count);
            foreach (var kv in _attrTable)
            {
                var record = kv.Value;
                foreach (int module in
                    new System.Collections.Generic.List<int>(record.Keys))
                    record[module] = RelocIndex(record[module], forward);
                moved.Add((RelocIndex(kv.Key, forward), record));
            }
            _attrTable.Clear();
            foreach (var (home, record) in moved) _attrTable[home] = record;
        }

        for (int i = 0; i < _attrTrailLog.Count; i++)
        {
            var (home, module, oldValue) = _attrTrailLog[i];
            _attrTrailLog[i] = (RelocIndex(home, forward), module,
                                oldValue < 0 ? oldValue : RelocIndex(oldValue, forward));
        }

        for (int i = 0; i < _pendingWakeups.Count; i++)
        {
            var (module, attrValueIdx, otherIdx, attvarHome) = _pendingWakeups[i];
            _pendingWakeups[i] = (module,
                RelocIndex(attrValueIdx, forward),
                RelocIndex(otherIdx, forward),
                RelocIndex(attvarHome, forward));
        }

        RelocateCleanupRoots(c => RelocateCell(c, forward));

        // call_residue_vars snapshots observe by raw address and deliberately
        // do NOT retain, so they are relocated but never marked.
        foreach (object? o in _foreignTable)
            if (o is AttrSnapshot snap)
            {
                var mapped = new System.Collections.Generic.HashSet<int>(snap.Homes.Count);
                foreach (int home in snap.Homes)
                    mapped.Add(RelocIndex(home, forward));
                snap.Homes.Clear();
                foreach (int home in mapped) snap.Homes.Add(home);
            }
    }


    /// <summary>TEMPORARY PROBE — how many cells are reachable right now.
    /// Runs the mark phase and reports; moves nothing.</summary>
    public (int Live, int Total) HeapLiveProbe()
    {
        int oldTop = _heapTop;
        if (oldTop == 0) return (0, 0);
        bool[] marked = _gcMarked is { } m && m.Length >= oldTop ? m : (_gcMarked = new bool[oldTop]);
        System.Array.Clear(marked, 0, oldTop);
        if (_gcWork is null || _gcWork.Length < 1024) _gcWork = new int[1024];
        _gcWorkTop = 0;
        _gcOldTop = oldTop;
        MarkRoots(oldTop);
        MarkExternalHolders(oldTop);
        MarkExternalTrailRoots(GcMarkReferents);
        OnGcMark?.Invoke(GcMarkCell, GcMarkReferents);
        while (_gcWorkTop > 0)
            GcMarkReferents(_heap[_gcWork[--_gcWorkTop]]);
        int live = 0;
        for (int i = 0; i < oldTop; i++) if (marked[i]) live++;
        return (live, oldTop);
    }

    /// <summary>DIAGNOSTIC (the stack-roots GC arc): attributes retained heap
    /// cells to individual roots. Marks from every root EXCEPT the register
    /// bank and the control stack first (the baseline), then feeds registers
    /// and stack slots one at a time, charging each the cells only IT newly
    /// reaches (order-dependent: an earlier slot absorbs shared structure —
    /// good enough to name the offenders). Stack slots are classified by
    /// walking the live frame/CP chains: Y-slot i of the frame at base b
    /// (with its recorded live count), CP argument, control word, or
    /// unattributed. Prints the top offenders to stderr; moves nothing.</summary>
    public void HeapRootAttributionProbe(int top = 25)
    {
        int oldTop = _heapTop;
        if (oldTop == 0) return;
        bool[] marked = _gcMarked is { } m && m.Length >= oldTop ? m : (_gcMarked = new bool[oldTop]);
        System.Array.Clear(marked, 0, oldTop);
        if (_gcWork is null || _gcWork.Length < 1024) _gcWork = new int[1024];
        _gcWorkTop = 0;
        _gcOldTop = oldTop;
        _gcMarkCount = 0;

        void Drain()
        {
            while (_gcWorkTop > 0)
                GcMarkReferents(_heap[_gcWork[--_gcWorkTop]]);
        }

        // Baseline: everything EXCEPT registers + control stack, each
        // category charged separately.
        int c0 = _gcMarkCount;
        for (int k = 0; k < _bindingTrailTop; k++) GcMarkCell(_bindingTrail[k]);
        Drain();
        int cBind = _gcMarkCount - c0; c0 = _gcMarkCount;
        for (int k = 0; k < _extraTrailTop; k++)
        {
            var e = _extraTrail[k];
            if (e.Type == TrailType.ValueChange)
            {
                GcMarkCell(e.HeapIdx);
                GcMarkReferents(e.OldValue);
            }
        }
        Drain();
        int cExtra = _gcMarkCount - c0; c0 = _gcMarkCount;
        for (int i = 0; i < _catchFrames.Count; i++)
        {
            GcMarkCell(_catchFrames[i].CatcherHeapIdx);
            GcMarkCell(_catchFrames[i].RecoveryHeapIdx);
        }
        Drain();
        int cCatch = _gcMarkCount - c0; c0 = _gcMarkCount;
        int h0 = _gcMarkCount;
        foreach (var kv in _attrTable)
        {
            if ((uint)kv.Key < (uint)oldTop) GcMarkCell(kv.Key);
            foreach (var (_, attrValueIdx) in kv.Value)
                if ((uint)attrValueIdx < (uint)oldTop) GcMarkCell(attrValueIdx);
        }
        Drain();
        int hTable = _gcMarkCount - h0; h0 = _gcMarkCount;
        foreach (var (home, _, oldValue) in _attrTrailLog)
        {
            if (home == int.MinValue) continue;   // dead record
            if ((uint)home < (uint)oldTop) GcMarkCell(home);
            if ((uint)oldValue < (uint)oldTop) GcMarkCell(oldValue);
        }
        Drain();
        int hLog = _gcMarkCount - h0; h0 = _gcMarkCount;
        foreach (var (_, attrValueIdx, otherIdx, attvarHome) in _pendingWakeups)
        {
            if ((uint)attrValueIdx < (uint)oldTop) GcMarkCell(attrValueIdx);
            if ((uint)otherIdx < (uint)oldTop) GcMarkCell(otherIdx);
            if ((uint)attvarHome < (uint)oldTop) GcMarkCell(attvarHome);
        }
        Drain();
        int hWake = _gcMarkCount - h0; h0 = _gcMarkCount;
        MarkCleanupRoots();
        Drain();
        int hClean = _gcMarkCount - h0;
        int cHolders = _gcMarkCount - c0; c0 = _gcMarkCount;
        System.Console.Error.WriteLine(
            $"[gc-roots] holders breakdown: attrTable={hTable} (n={_attrTable.Count})"
            + $" attrTrailLog={hLog} (n={_attrTrailLog.Count}) wakeups={hWake} (n={_pendingWakeups.Count})"
            + $" cleanups={hClean}");
        MarkExternalTrailRoots(GcMarkReferents);
        Drain();
        int cExtTrail = _gcMarkCount - c0; c0 = _gcMarkCount;
        OnGcMark?.Invoke(GcMarkCell, GcMarkReferents);
        Drain();
        int cHook = _gcMarkCount - c0;
        int baseline = _gcMarkCount;
        System.Console.Error.WriteLine(
            $"[gc-roots] baseline breakdown: bindingTrail={cBind} (entries={_bindingTrailTop})"
            + $" extraTrail={cExtra} (entries={_extraTrailTop}) catchFrames={cCatch} (n={_catchFrames.Count})"
            + $" externalHolders={cHolders} externalTrail={cExtTrail} markHook={cHook}");

        var offenders = new List<(string Kind, int Index, Cell Cell, int Retained)>();
        for (int i = 0; i < _registers.Length; i++)
        {
            int before = _gcMarkCount;
            GcMarkReferents(_registers[i]);
            Drain();
            if (_gcMarkCount > before)
                offenders.Add(("X", i, _registers[i], _gcMarkCount - before));
        }
        for (int i = 0; i < _stackTop; i++)
        {
            int before = _gcMarkCount;
            GcMarkReferents(_stack[i]);
            Drain();
            if (_gcMarkCount > before)
                offenders.Add(("stack", i, _stack[i], _gcMarkCount - before));
        }

        // Frame/CP attribution maps for the stack offenders.
        var frameOfSlot = new Dictionary<int, string>();
        void MapFrame(int e)
        {
            while (e >= 0 && e + EnvY1Offset <= _stackTop)
            {
                int n = (int)_stack[e + EnvNOffset].Data;
                frameOfSlot.TryAdd(e + EnvCeOffset, $"frame@{e}.CE");
                frameOfSlot.TryAdd(e + EnvCpOffset, $"frame@{e}.CP");
                frameOfSlot.TryAdd(e + EnvNOffset, $"frame@{e}.N");
                int maxY = _stackTop - e - EnvY1Offset;
                for (int y = 0; y < System.Math.Min(n < 0 ? 0 : n, maxY); y++)
                    frameOfSlot.TryAdd(e + EnvY1Offset + y, $"frame@{e}.Y{y}(live,N={n})");
                int parent = (int)_stack[e + EnvCeOffset].Data;
                if (parent == e) break;
                e = parent;
            }
        }
        int chainLen = 0, lowestE = _e;
        for (int e2 = _e; e2 >= 0 && e2 + EnvY1Offset <= _stackTop && chainLen < 1_000_000; )
        {
            chainLen++; lowestE = e2;
            int p2 = (int)_stack[e2 + EnvCeOffset].Data;
            if (p2 == e2) break;
            e2 = p2;
        }
        System.Console.Error.WriteLine(
            $"[gc-roots] E-chain: frames={chainLen} lowest={lowestE}");
        MapFrame(_e);
        int b = _b, guard = 0;
        while (b >= 0 && b < _stackTop && guard++ < 100000)
        {
            int ar = (int)_stack[b + CpArityOffset].Data;
            if (ar < 0 || ar > 4096 || b + CpSize(ar) > _stackTop) break;
            for (int i = 0; i < ar; i++)
                frameOfSlot.TryAdd(b + CpArg1Offset + i, $"cp@{b}.A{i}");
            MapFrame((int)_stack[b + CpCeOffset(ar)].Data);
            int pv = (int)_stack[b + CpBOffset(ar)].Data;
            if (pv == b) break;
            b = pv;
        }

        offenders.Sort((x, y) => y.Retained.CompareTo(x.Retained));
        System.Console.Error.WriteLine(
            $"[gc-roots] heapTop={oldTop} baseline(non-stack)={baseline} "
            + $"total-live={_gcMarkCount} stackTop={_stackTop} E={_e} B={_b}");
        int shown = 0;
        foreach (var (kind, idx, cell, retained) in offenders)
        {
            if (shown++ >= top) break;
            string attr = kind == "stack" && frameOfSlot.TryGetValue(idx, out string? f)
                ? f : (kind == "stack" ? "UNATTRIBUTED" : "");
            System.Console.Error.WriteLine(
                $"[gc-roots]   {kind}[{idx}] {cell.Tag}->{(cell.Tag is Tag.Ref or Tag.Str or Tag.Lis or Tag.AttVar or Tag.Pstr ? cell.AsHeapIndex : 0)} retains={retained} {attr}");
        }
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
                    ? Cell.Pstr(c.AsPstrLength, forward[c.AsPstrBufferIndex], c.AsPstrOffset, c.AsPstrKind, c.AsPstrIsAstral)
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
        _gcMarkCount++;
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
        // X registers. With a live bound (a call boundary whose callee arity
        // is known) only the arguments are roots, and the DEAD registers are
        // cleared to a harmless leaf — a stale one would re-root its dead
        // structure at the next conservative collection, or dangle after
        // this one slides the heap. Without a bound: the whole bank,
        // conservatively.
        int liveRegs = _gcLiveRegisterBound;
        if (liveRegs >= 0)
        {
            for (int i = 0; i < liveRegs && i < _registers.Length; i++)
                GcMarkReferents(_registers[i]);
            for (int i = liveRegs; i < _registers.Length; i++)
                _registers[i] = Cell.Int(0);
        }
        else
        {
            for (int i = 0; i < _registers.Length; i++)
                GcMarkReferents(_registers[i]);
        }

        // Entire control stack — conservative. Control words are RawInt
        // (leaves); every real ref is marked no matter which frame it is in.
        //
        // The cost of that is retention: a slot a frame no longer reads still
        // roots whatever it holds, and last-call optimisation reuses frames
        // without clearing the slots above the live count. It is why a lazy
        // DCG holds every window it has consumed — the windows are dead but a
        // stale environment slot still names them. Making this precise needs a
        // per-frame live-slot count at the current continuation, which the
        // engine does not compute today.
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

        // Catch frames: the catcher and recovery terms are heap roots. Their
        // SnapE / RecoveryE environment chains need nothing here — the whole
        // control stack is scanned above — so only the heap terms are marked.
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
