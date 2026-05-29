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

    // Per-collection map: environment-frame base -> number of live Y
    // slots to scan. Computed precisely from each frame's continuation
    // (the saved return address), recovering the compiler's
    // num_live_perms — see ComputeFrameLiveCounts / EnvFrameLiveCount.
    private readonly System.Collections.Generic.Dictionary<int, int> _gcFrameLive = new();
    private long[]? _gcStackSnap;   // diagnostic: pre-phase stack snapshot
    private bool _gcDryRun;         // diagnostic: mark but don't relocate

    /// <summary>ADR-016 (correct env liveness): for every reachable
    /// environment frame, records the exact number of live Y slots,
    /// recovered from the frame's continuation point. A frame reachable
    /// both from the current continuation and from a choice point's
    /// backtrack continuation is recorded with the MAX of the two counts,
    /// so the scan covers what either resume path will read. This is
    /// precise — the count is the compiler's <c>num_live_perms</c>, a
    /// prefix ending exactly where any choice point was pushed — so the
    /// Y-scan never overlaps a CP's or another frame's control words.</summary>
    private void ComputeFrameLiveCounts()
    {
        _gcFrameLive.Clear();

        void Chain(int e, int retAddr)
        {
            while (e >= 0 && e + EnvY1Offset <= _stackTop)
            {
                int cnt = EnvFrameLiveCount(retAddr, e);
                int maxSlots = _stackTop - e - EnvY1Offset;
                if (cnt > maxSlots) cnt = maxSlots;
                if (cnt < 0) cnt = 0;
                if (_gcFrameLive.TryGetValue(e, out int prev))
                {
                    if (cnt > prev) _gcFrameLive[e] = cnt;
                    return;   // downstream chain already recorded
                }
                _gcFrameLive[e] = cnt;
                retAddr = (int)_stack[e + EnvCpOffset].Data;   // parent's resume point
                e = (int)_stack[e + EnvCeOffset].Data;
            }
        }

        Chain(_e, _cp);
        int b = _b;
        while (b >= 0 && b < _stackTop)
        {
            int arity = (int)_stack[b + CpArityOffset].Data;
            if (arity < 0 || arity > 4096 || b + CpSize(arity) > _stackTop) break;
            Chain((int)_stack[b + CpCeOffset(arity)].Data, (int)_stack[b + CpCpOffset(arity)].Data);
            int prevB = (int)_stack[b + CpBOffset(arity)].Data;
            if (prevB == b) break;
            b = prevB;
        }
        for (int i = 0; i < _catchFrames.Count; i++)
        {
            CatchFrame cf = _catchFrames[i];
            Chain(cf.SnapE, -1);                 // sentinel -> stored-count fallback
            Chain(cf.RecoveryE, cf.RecoveryCp);
        }
    }

    /// <summary>The number of live Y slots in the frame whose continuation
    /// is <paramref name="retAddr"/>. When the return address sits right
    /// after a 9-byte <c>Call</c> / <c>CallBuiltin</c> (the only ops that
    /// save a continuation), its <c>num_live_perms</c> operand is the
    /// compiler's exact live count. Otherwise (a sentinel continuation —
    /// the outermost query frame) fall back to the stored count.</summary>
    private int EnvFrameLiveCount(int retAddr, int e)
    {
        byte[]? prog = CurrentProgram;
        if (prog is not null && retAddr >= 9 && retAddr <= ProgramLength)
        {
            byte op = prog[retAddr - 9];
            if (op == (byte)Opcode.Call || op == (byte)Opcode.CallBuiltin)
            {
                int nlp = BytecodeIO.ReadInt32(prog, retAddr - 4);
                if (nlp >= 0) return nlp;
            }
        }
        int stored = (int)_stack[e + EnvNOffset].Data;
        return stored < 0 ? 0 : stored;
    }

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
    /// index → new) and <c>relocCell(Cell)</c> (rewrites a value cell's
    /// heap-index payload). Implementations must write the relocated
    /// indices / cells back into their own storage.</summary>
    public System.Action<System.Func<int, int>, System.Func<Cell, Cell>>? OnGcRelocate { get; set; }

    /// <summary>When true, <see cref="MaybeCollectHeap"/> collects at
    /// every safe point — the ADR-016 fuzz mode used to validate
    /// relocation against every query shape in the test suite.</summary>
    public bool GcStressMode { get => _gcStressMode; set => _gcStressMode = value; }

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
    private int _gcDumpAt = -1;
    private int _gcSafePointCount;

    /// <summary>Total safe points seen — diagnostic for GC bisection.</summary>
    public int GcSafePointCount => _gcSafePointCount;

    public void MaybeCollectHeap()
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

        // A frame's stored Y-count can be stale-large: after a trim or a
        // backtrack reused the stack, a choice point or a nested frame may
        // sit inside the range the count implies. Scanning that far would
        // treat a CP's / frame's control words as Y cells and relocate
        // them (corrupting saved Cp / prevB). Collect every live stack
        // item base so each frame's Y-scan can be clamped to the next one
        // above it.
        ComputeFrameLiveCounts();

        MarkRoots(MarkReferents, MarkCell, oldTop);
        OnGcMark?.Invoke(MarkCell, MarkReferents);

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

        // Diagnostic dry-run: mark + forward computed, but skip the actual
        // move/relocate so the heap and roots are left untouched. If a
        // workload still misbehaves with dry-run stress, the fault is not
        // in relocation.
        if (_gcDryRun) return 0;

        // Bisection dump: SHUMWAY_GC_DUMP=N dumps collection N's heap +
        // forwarding so a corrupting relocation can be eyeballed.
        bool dump = _gcDumpAt == 0 || (_gcDumpAt > 0 && _gcSafePointCount == _gcDumpAt);
        // Whole-stack before/after diff: snapshot every stack slot now, and
        // after all phases report any that changed but is NOT a legitimate
        // GC write target (a live frame Y-slot, a CP arg, or a CP HeapTop/
        // Hb boundary). Such a change is an unmodelled corrupting write.
        long[]? stackSnap = dump ? new long[_stackTop] : null;
        if (stackSnap is not null)
            for (int i = 0; i < _stackTop; i++) stackSnap[i] = _stack[i].Data;
        if (dump)
        {
            System.Console.Error.WriteLine($"[gc] sp={_gcSafePointCount} oldTop={oldTop} live={live} stackTop={_stackTop} _e={_e} _b={_b}");
            // A dangling ref (heap-ref to a DEAD cell) inside a scanned
            // region — a frame's precise live Y-range or a CP's saved args
            // — is a real missing root. Others are dead-stack noise.
            static bool IsRef(Cell c) => c.Tag is Tag.Ref or Tag.Str or Tag.Lis or Tag.AttVar;
            bool InCpArgs(int slot)
            {
                int b2 = _b, g2 = 0;
                while (b2 >= 0 && b2 < _stackTop && g2++ < 100000)
                {
                    int ar = (int)_stack[b2 + CpArityOffset].Data;
                    if (ar < 0 || ar > 4096 || b2 + CpSize(ar) > _stackTop) break;
                    if (slot >= b2 + CpArg1Offset && slot < b2 + CpArg1Offset + ar) return true;
                    int pv = (int)_stack[b2 + CpBOffset(ar)].Data; if (pv == b2) break; b2 = pv;
                }
                return false;
            }
            bool InFrame(int slot)
            {
                foreach (var (fb, cnt) in _gcFrameLive)
                    if (slot >= fb + EnvY1Offset && slot < fb + EnvY1Offset + cnt) return true;
                return false;
            }
            for (int i = 0; i < _stackTop; i++)
            {
                Cell c = _stack[i];
                if (IsRef(c) && (uint)c.AsHeapIndex < (uint)oldTop && !marked[c.AsHeapIndex]
                    && (InFrame(i) || InCpArgs(i)))
                    System.Console.Error.WriteLine($"  MISSING-ROOT stack[{i}]->{c.AsHeapIndex} (DEAD)");
            }
            // CP-chain completeness: does the _b walk terminate cleanly
            // (prevB == self or -1) or break on an insane arity (=> a
            // corrupted prevB, so we miss CPs/frames below)?
            int cb = _b, cg = 0;
            while (cb >= 0 && cb < _stackTop && cg++ < 100000)
            {
                int ar = (int)_stack[cb + CpArityOffset].Data;
                if (ar < 0 || ar > 4096 || cb + CpSize(ar) > _stackTop)
                { System.Console.Error.WriteLine($"  CP-CHAIN-BREAK at b={cb} arity={ar}"); break; }
                int pv = (int)_stack[cb + CpBOffset(ar)].Data;
                if (pv == cb || pv < 0) break;       // clean terminator
                cb = pv;
            }
        }
        _gcStackSnap = stackSnap;

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
        // Bisection (step b): snapshot every frame's saved-Cp and every
        // CP's saved-Cp before the stack relocation, so we can flag any
        // that the relocation changes — a saved Cp is a code address the
        // GC must never touch, so a change pinpoints the corrupting write.
        System.Collections.Generic.List<(int Addr, long Val, string Kind)>? cpSnap = null;
        if (dump)
        {
            cpSnap = new System.Collections.Generic.List<(int, long, string)>();
            SnapshotSavedCps(cpSnap);
        }
        RelocateRoots(forward, oldTop);
        OnGcRelocate?.Invoke(idx => RelocIndex(idx, forward), c => RelocateCell(c, forward));
        if (cpSnap is not null)
            foreach (var (addr, val, kind) in cpSnap)
                if (_stack[addr].Data != val)
                    System.Console.Error.WriteLine(
                        $"  CP-CORRUPT {kind} stack[{addr}] {val} -> {_stack[addr].Data}");

        _heapTop = live;
        _hb = RelocBoundary(_hb, forward);

        if (dump)
        {
            // Post-compaction: any stack slot still holding a heap-ref
            // that points at or beyond the new heap top was NOT relocated
            // (an unscanned holder). If the engine later reads it, that's
            // the missing root.
            string Classify(int slot)
            {
                // In a known frame? distinguish within-count vs beyond-count.
                foreach (var (fb, cnt) in _gcFrameLive)
                {
                    int storedN = (int)_stack[fb + EnvNOffset].Data;
                    if (slot >= fb + EnvY1Offset && slot < fb + EnvY1Offset + storedN)
                        return slot < fb + EnvY1Offset + cnt
                            ? $"FRAME e={fb} WITHIN-COUNT(scanned={cnt})"
                            : $"FRAME e={fb} BEYOND-COUNT(scanned={cnt},storedN={storedN})";
                }
                // In an active CP frame region?
                int b2 = _b, g2 = 0;
                while (b2 >= 0 && b2 < _stackTop && g2++ < 100000)
                {
                    int ar = (int)_stack[b2 + CpArityOffset].Data;
                    if (ar < 0 || ar > 4096 || b2 + CpSize(ar) > _stackTop) break;
                    if (slot >= b2 && slot < b2 + CpSize(ar)) return $"CP b={b2} (off {slot - b2})";
                    int pv = (int)_stack[b2 + CpBOffset(ar)].Data; if (pv == b2) break; b2 = pv;
                }
                return "ORPHAN";
            }
            for (int i = 0; i < _stackTop; i++)
            {
                Cell c = _stack[i];
                if ((c.Tag is Tag.Ref or Tag.Str or Tag.Lis or Tag.AttVar)
                    && c.AsHeapIndex >= live && c.AsHeapIndex < oldTop)
                    System.Console.Error.WriteLine($"  STALE-AFTER stack[{i}]->{c.AsHeapIndex} {Classify(i)}");
            }
            // Heap integrity: every surviving cell's heap-ref must point
            // inside the new live region. A dangling intra-heap pointer is
            // a phase-3/4 relocation bug.
            for (int i = 0; i < live; i++)
            {
                Cell c = _heap[i];
                int idx = c.AsHeapIndex;
                if ((c.Tag is Tag.Ref or Tag.Str or Tag.Lis or Tag.AttVar) && (uint)idx >= (uint)live)
                    System.Console.Error.WriteLine($"  HEAP-BAD [{i}] {c.Tag}->{idx} (newTop {live})");
                if (c.Tag is Tag.Float && (uint)c.FloatPairedIndex >= (uint)live)
                    System.Console.Error.WriteLine($"  HEAP-BAD [{i}] Float paired->{c.FloatPairedIndex}");
            }
            // Whole-stack diff: any changed slot NOT a legitimate write
            // target (frame Y / CP arg / CP boundary) is an unmodelled GC
            // write — the corruption.
            if (_gcStackSnap is { } snap)
            {
                bool InFrameY(int slot)
                {
                    foreach (var (fb, cnt) in _gcFrameLive)
                        if (slot >= fb + EnvY1Offset && slot < fb + EnvY1Offset + cnt) return true;
                    return false;
                }
                bool InCpWrite(int slot)
                {
                    int b2 = _b, g2 = 0;
                    while (b2 >= 0 && b2 < _stackTop && g2++ < 100000)
                    {
                        int ar = (int)_stack[b2 + CpArityOffset].Data;
                        if (ar < 0 || ar > 4096 || b2 + CpSize(ar) > _stackTop) break;
                        if (slot >= b2 + CpArg1Offset && slot < b2 + CpArg1Offset + ar) return true;
                        if (slot == b2 + CpHeapTopOffset(ar) || slot == b2 + CpHbOffset(ar)) return true;
                        int pv = (int)_stack[b2 + CpBOffset(ar)].Data; if (pv == b2) break; b2 = pv;
                    }
                    return false;
                }
                for (int i = 0; i < snap.Length; i++)
                    if (_stack[i].Data != snap[i] && !InFrameY(i) && !InCpWrite(i))
                        System.Console.Error.WriteLine($"  UNMODELLED-WRITE stack[{i}] {snap[i]} -> {_stack[i].Data}");
            }
        }

        return oldTop - live;
    }

    // Bisection (step b): record the address + value of every saved-Cp
    // slot (a frame's EnvCpOffset, a choice point's CpCpOffset). These are
    // code addresses the collector must never write, so any post-relocate
    // change is the corrupting write.
    private void SnapshotSavedCps(System.Collections.Generic.List<(int, long, string)> snap)
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        void Frames(int e)
        {
            while (e >= 0 && e + EnvY1Offset <= _stackTop && seen.Add(e))
            {
                snap.Add((e + EnvCpOffset, _stack[e + EnvCpOffset].Data, $"frameCp e={e}"));
                e = (int)_stack[e + EnvCeOffset].Data;
            }
        }
        Frames(_e);
        int b = _b;
        while (b >= 0 && b < _stackTop)
        {
            int ar = (int)_stack[b + CpArityOffset].Data;
            if (ar < 0 || ar > 4096 || b + CpSize(ar) > _stackTop) break;
            snap.Add((b + CpArityOffset, _stack[b + CpArityOffset].Data, $"cpArity b={b}"));
            snap.Add((b + CpCpOffset(ar), _stack[b + CpCpOffset(ar)].Data, $"cpCp b={b}"));
            snap.Add((b + CpBOffset(ar), _stack[b + CpBOffset(ar)].Data, $"cpPrevB b={b}"));
            snap.Add((b + CpCeOffset(ar), _stack[b + CpCeOffset(ar)].Data, $"cpCE b={b}"));
            Frames((int)_stack[b + CpCeOffset(ar)].Data);
            int pv = (int)_stack[b + CpBOffset(ar)].Data;
            if (pv == b) break;
            b = pv;
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
                    ? Cell.Pstr(c.AsPstrLength, forward[c.AsPstrBufferIndex], c.AsPstrOffset)
                    : c;
            default:
                return c;   // atomic / leaf
        }
    }

    private static bool InBounds(int idx, int oldTop) => (uint)idx < (uint)oldTop;

    // Relocate a bare heap-address root, leaving an out-of-range index
    // (a transient/dead slot, never a live heap cell) untouched.
    private static int RelocIndex(int idx, int[] forward)
        => (uint)idx < (uint)(forward.Length - 1) ? forward[idx] : idx;

    // Relocate a heap-TOP boundary (valid range [0, oldTop], inclusive —
    // forward[oldTop] is the new live count). A stale/garbage boundary
    // outside that range is left untouched rather than crashing.
    private static int RelocBoundary(int p, int[] forward)
        => (uint)p < (uint)forward.Length ? forward[p] : p;

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

        // Environment frames: scan each frame's precise live Y-slot count
        // (ComputeFrameLiveCounts, called before MarkRoots).
        foreach (var (frameBase, liveCount) in _gcFrameLive)
            for (int i = 0; i < liveCount; i++)
                markReferents(_stack[frameBase + EnvY1Offset + i]);

        // Choice points: walk the _b chain. Saved argument slots are
        // roots; the saved HeapTop / Hb are boundaries (relocated later,
        // not roots). Env frames are handled above.
        int b = _b;
        while (b >= 0 && b < _stackTop)
        {
            int arity = (int)_stack[b + CpArityOffset].Data;
            // A base whose arity isn't a sane frame size is not a real CP
            // — the chain walked off into a frame / reclaimed slot. Stop.
            if (arity < 0 || arity > 4096 || b + CpSize(arity) > _stackTop) break;
            for (int i = 0; i < arity; i++)
                markReferents(_stack[b + CpArg1Offset + i]);
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

        // Catch frames: the catcher and recovery terms are heap roots. The
        // SnapE / RecoveryE environment chains are covered by
        // ComputeFrameLiveCounts (scanned above), so only the heap terms
        // are marked here.
        for (int i = 0; i < _catchFrames.Count; i++)
        {
            CatchFrame cf = _catchFrames[i];
            markCell(cf.CatcherHeapIdx);
            markCell(cf.RecoveryHeapIdx);
        }
    }

    private void RelocateRoots(int[] forward, int oldTop)
    {
        for (int i = 0; i < _registers.Length; i++)
            _registers[i] = RelocateCell(_registers[i], forward);

        // Environment frames: precise live Y-slot counts (computed at the
        // start of the collection, before any relocation).
        foreach (var (frameBase, liveCount) in _gcFrameLive)
            for (int i = 0; i < liveCount; i++)
            {
                int slot = frameBase + EnvY1Offset + i;
                _stack[slot] = RelocateCell(_stack[slot], forward);
            }

        int b = _b;
        while (b >= 0 && b < _stackTop)
        {
            int arity = (int)_stack[b + CpArityOffset].Data;
            if (arity < 0 || arity > 4096 || b + CpSize(arity) > _stackTop) break;
            for (int i = 0; i < arity; i++)
                _stack[b + CpArg1Offset + i] = RelocateCell(_stack[b + CpArg1Offset + i], forward);
            // Saved HeapTop / Hb are boundaries.
            int htOff = b + CpHeapTopOffset(arity);
            _stack[htOff] = new Cell(RelocBoundary((int)_stack[htOff].Data, forward));
            int hbOff = b + CpHbOffset(arity);
            _stack[hbOff] = new Cell(RelocBoundary((int)_stack[hbOff].Data, forward));
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
