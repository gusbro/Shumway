using System.Numerics;

namespace Shumway.Core;

public sealed partial class Activation
{
    // ----- Activation registers (per ADR-005) -----
    // -1 means "none yet" for E, B, and B0. P and CP track the program counter and
    // continuation point; they are set when the interpreter is hooked up. B0 is
    // the value of B at the most recent procedure entry — neck_cut uses it as the
    // implicit cut barrier so the call protocol can distinguish CPs created inside
    // the current procedure from CPs that pre-existed it. _writeMode and
    // _unifyPointer track the read/write state set up by get_structure/get_list/
    // put_structure/put_list and stepped through by the unify_* family.
    private int _e = -1;
    private int _b = -1;
    private int _b0 = -1;
    private int _p = -1;
    private int _cp = -1;
    private bool _writeMode;
    private int _unifyPointer;

    // ADR-020 reserve-upfront write mode. When _reservedWrite is true the cells
    // at _unifyPointer are pre-allocated (by put_structure_r / put_list_r), so a
    // scalar unify_* writes in place (no AllocateHeap) and an auto-popping
    // write-pointer stack restores _unifyPointer to the parent after a nested
    // compound completes. Set only by the _r roots; cleared by the on-demand /
    // read entries and when the base frame pops. Ephemeral within one structure
    // build (no choice point spans it, so it is never trailed).
    private bool _reservedWrite;
    // Each frame: the parent-resume unify pointer (high 32) and the remaining
    // arg count (low 32), packed so the stack is one long[]. Depth = nesting
    // depth of the term being built; 32 is far beyond any real clause.
    private int[] _writeResume = new int[64];
    private int[] _writeRemaining = new int[64];
    private int _writeSp;

    public bool ReservedWrite => _reservedWrite;

    public Activation() : this(new ActivationConfig()) { }

    public Activation(ActivationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Validate(config);

        _config = config;
        _heap = new Cell[config.InitialHeapSize];
        _stack = new Cell[config.InitialStackSize];
        _registers = new Cell[config.InitialRegisterCount];
        _bindingTrail = new int[config.InitialBindingTrailSize];
        _extraTrail = new ExtraTrailEntry[config.InitialExtraTrailSize];
        _gcThreshold = config.GcThreshold;
        // the GC fuzz/bisect env overrides (SHUMWAY_GC_THRESHOLD /
        // GC_STRESS / GC_AT / GC_UPTO) only exist in -p:ShumwayDiag=true
        // builds; a normal build never reads the environment here. The fuzz
        // FIELDS and their (branch-predicted) checks in MaybeCollectHeap stay,
        // because the test suite also drives them programmatically
        // (GcStressMode / ActivationConfig).
        DiagReadGcOverrides();
    }

    /// <summary>diag-build-only env overrides for the ADR-016 GC:
    /// watermark replacement, every-safe-point stress mode (fuzz), and the
    /// collect-at-exactly-N / collect-up-to-N bisection knobs. Stripped from
    /// normal builds via <c>[Conditional("SHUMWAY_DIAG")]</c>.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private void DiagReadGcOverrides()
    {
        if (int.TryParse(System.Environment.GetEnvironmentVariable("SHUMWAY_GC_THRESHOLD"), out int gcThr))
            _gcThreshold = gcThr;
        _gcStressMode = System.Environment.GetEnvironmentVariable("SHUMWAY_GC_STRESS") == "1";
        if (int.TryParse(System.Environment.GetEnvironmentVariable("SHUMWAY_GC_AT"), out int gcAt))
            _gcOnlyAt = gcAt;
        if (int.TryParse(System.Environment.GetEnvironmentVariable("SHUMWAY_GC_UPTO"), out int gcUpTo))
            _gcUpTo = gcUpTo;
        // fold the knobs into the single steady-state flag
        // MaybeCollectHeap's inlined guard tests.
        UpdateGcDiagActive();
    }

    /// <summary>Drops the attribute record of a variable whose ATTVAR-restoring
    /// trail entry the cut just discarded.
    ///
    /// <para>The record outlives the binding because a backtrack restores the
    /// ATTVAR cell and would find its constraints gone otherwise — so it may be
    /// dropped exactly when the entry that would do that restoring is dropped,
    /// and not before. The cut drops it when the cell is YOUNGER than the
    /// surviving choice point, which means an outer backtrack truncates the
    /// cell away entirely and there is nothing left to restore.</para>
    ///
    /// <para>Without this the table grows one dead record per constrained
    /// variable for the life of the query, and because the record is a GC root
    /// it holds the variable's whole term with it — which is what made a lazy
    /// DCG retain every window it had already consumed.</para></summary>
    private void DropDeadAttrRecord(int home)
    {
        // Still an attributed variable — the binding was undone, or this entry
        // was about something else at the same address. Nothing to drop.
        if ((uint)home < (uint)_heapTop && _heap[home].Tag == Tag.AttVar) return;
        _attrTable.Remove(home);
    }

    private static void Validate(ActivationConfig c)
    {
        if (c.InitialHeapSize <= 0) throw new ArgumentException("InitialHeapSize must be > 0", nameof(c));
        if (c.InitialStackSize <= 0) throw new ArgumentException("InitialStackSize must be > 0", nameof(c));
        if (c.InitialRegisterCount <= 0) throw new ArgumentException("InitialRegisterCount must be > 0", nameof(c));
        if (c.InitialBindingTrailSize <= 0) throw new ArgumentException("InitialBindingTrailSize must be > 0", nameof(c));
        if (c.InitialExtraTrailSize <= 0) throw new ArgumentException("InitialExtraTrailSize must be > 0", nameof(c));
    }

    public ActivationConfig Config => _config;

    // ----- Heap accessors -----

    public int HeapTop => _heapTop;
    public int HeapCapacity => _heap.Length;
    public int Hb => _hb;

    /// <summary>ADR-035 D5+ (Set Next Statement) — trail EVERY binding, not only those the
    /// HB check requires for backtracking. The HB optimisation skips trailing a binding to
    /// a variable younger than the newest choice point (backtracking discards that heap
    /// wholesale, so undoing is pointless) — which also makes such bindings UNRECOVERABLE
    /// for anyone else. A debugger that rewinds execution to an earlier goal restores state
    /// by unwinding the trail to a recorded mark, and that only restores everything if
    /// everything was trailed. Implemented by PINNING <see cref="Hb"/> at
    /// <see cref="int.MaxValue"/> (every write to it goes through <see cref="AssignHb"/>):
    /// the per-bind hot path keeps its single <c>addr &lt; _hb</c> compare — zero new
    /// branches — and only the cold per-choice-point assignments pay a predicted branch.
    /// Set by the debug session for the queries it watches; extra trail growth is a
    /// debug-only cost.</summary>
    public bool TrailEverything
    {
        get => _trailEverything;
        set
        {
            _trailEverything = value;
            if (value) _hb = int.MaxValue;
        }
    }
    private bool _trailEverything;

    private void AssignHb(int value) => _hb = _trailEverything ? int.MaxValue : value;

    /// <summary>Monotonic count of WAM heap cells reserved over this engine's
    /// lifetime (never decremented by backtracking). A deterministic,
    /// wall-clock-independent metric for allocation-affecting changes.</summary>
    public long CellsAllocated => _cellsAllocated;

    public Cell GetHeap(int idx) => _heap[idx];

    /// <summary>Writes a cell directly without trailing. Use for setting up state, not
    /// for binding variables — for that, call <see cref="Bind"/>.</summary>
    public void SetHeap(int idx, Cell value) => _heap[idx] = value;

    /// <summary>Reserves <paramref name="count"/> uninitialised cells on the heap and returns
    /// the index of the first one.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int AllocateHeap(int count)
    {
        if (count <= 0) ThrowBadAlloc();
        int newTop = _heapTop + count;
        if (newTop > _heap.Length) EnsureHeapCapacity(count);
        int start = _heapTop;
        _heapTop = newTop;
        _cellsAllocated += count;
        return start;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowBadAlloc()
        => throw new ArgumentOutOfRangeException("count");

    /// <summary>Allocates a fresh unbound variable on the heap (a self-pointing REF) and
    /// returns its index.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int AllocateHeapUnbound()
    {
        int idx = _heapTop;
        if (idx + 1 > _heap.Length) EnsureHeapCapacity(1);
        _heap[idx] = Cell.UnboundVar(idx);
        _heapTop = idx + 1;
        _cellsAllocated++;
        return idx;
    }

    // ----- Stack & registers -----

    public int StackTop => _stackTop;
    public int StackCapacity => _stack.Length;
    public int RegisterCount => _registers.Length;

    /// <summary>Reads a raw cell from the stack. The interpretation depends on context —
    /// frame control slots (CE / CP / B / BP / trail tops) hold raw <c>long</c> values
    /// rather than tagged cells; argument slots inside choice points hold A-register
    /// snapshots; Y-slots hold permanent variables.</summary>
    public Cell GetStack(int idx) => _stack[idx];

    // ----- Environment frame layout (ADR-005) -----

    /// <summary>Offset of <c>CE</c> (previous environment) within a frame.</summary>
    public const int EnvCeOffset = 0;

    /// <summary>Offset of <c>CP</c> (saved continuation point) within a frame.</summary>
    public const int EnvCpOffset = 1;

    /// <summary>Offset of the live-permanent count within a frame (ADR-016).
    /// Holds the number of Y slots the heap GC should treat as roots for
    /// this frame — written by <see cref="Allocate"/> (the full count) and
    /// lowered by <see cref="TrimEnv"/> to the compiler's live-Y count, so
    /// a stop-the-world collector can scan exactly the live permanents of
    /// every frame on the environment chain without decoding bytecode.
    /// The count is a prefix (Y1..Yn) because the compiler numbers
    /// longer-lived permanents lower.</summary>
    public const int EnvNOffset = 2;

    /// <summary>Offset of <c>Y1</c> within a frame. <c>Yk</c> is at <c>EnvY1Offset + (k-1)</c>.</summary>
    public const int EnvY1Offset = 3;

    /// <summary>Size in cells of an environment frame with <paramref name="numPermanents"/> Y slots.</summary>
    public static int EnvSize(int numPermanents) => 3 + numPermanents;

    /// <summary>Env trimming. Shrinks the
    /// current environment frame to keep only
    /// <paramref name="numLivePerms"/> Y slots, reclaiming the stack
    /// space the dead slots occupied so subsequent pushes pack
    /// tighter. The compile-time analysis side
    /// (<c>ClauseCompiler.ComputeLivePermsAfterEachGoal</c>) emits
    /// the right <c>num_live_perms</c> operand on every Call /
    /// CallBuiltin; the interpreter's handlers call here to apply it.
    ///
    /// <para><b>CP-frame protection:</b> the trim must
    /// never push <c>_stackTop</c> below the top of the most recent
    /// choice-point frame. Doing so would let the next stack push
    /// (a sub-call's CP, a sub-call's env, etc.) overwrite the
    /// active CP's saved slots — and on backtrack the CP restore
    /// would read back corrupted state. The check raises
    /// <c>desired</c> to <c>_b + CpSize(arity_at_b)</c> when the
    /// computed shrink would intrude on the active CP frame, so the
    /// trim degrades to a no-op for that call without losing
    /// correctness. (This protects both bytecode try_me_else CPs
    /// and IL choice points uniformly — they share the same _b
    /// chain.)</para></summary>
    public void TrimEnv(int numLivePerms)
    {
        // A negative count is the compiler's no-trim sentinel, emitted for a
        // clause's last goal: there the environment is the caller's (the
        // clause has no frame) or is about to be deallocated, so trimming it
        // would corrupt the caller's frame.
        if (numLivePerms < 0) return;
        if (_e < 0) return;
        int desired = _e + EnvSize(numLivePerms);
        bool clamped = false;
        if (_b >= 0)
        {
            int cpArity = (int)_stack[_b + CpArityOffset].Data;
            int cpTop = _b + CpSize(cpArity);
            if (cpTop > desired) { desired = cpTop; clamped = true; }
        }
        if (_stackTop > desired) _stackTop = desired;
        // ADR-016: only lower the recorded Y-slot count when the trim
        // actually reclaimed slots. When the CP-protection clamp prevents
        // the trim, the frame KEEPS all its slots — and they are live (an
        // in-progress choice point can still backtrack into them), so the
        // heap GC must continue to scan them as roots. Lowering the count
        // to numLivePerms in that case would let the collector reclaim
        // heap a later backtrack still needs.
        if (!clamped)
            _stack[_e + EnvNOffset] = Cell.RawInt(numLivePerms);
    }

    // ----- catch/3 frame stack -----

    private const long CatchTrailPush = 0;
    private const long CatchTrailDeactivate = 1;

    /// <summary>Number of catch frames on the stack (active or not).</summary>
    public int CatchFrameCount => _catchFrames.Count;

    internal static readonly bool CatchDiag =
        System.Environment.GetEnvironmentVariable("SHUMWAY_CATCH_DIAG") == "1";

    /// <summary>The nested-driver FAILURE path: a still-active frame the
    /// failed goal opened (its '$catch_end' only fires on success) must stop
    /// catching — a later ball must not route into the dead goal. It cannot
    /// be REMOVED, though: the goal's push/deactivate records are still on
    /// the extra trail (the driver does not rewind it on failure), and the
    /// outer unwind replays them against this stack — a physically shorter
    /// stack then underflows the replay (their clpb's hook-failure inside
    /// \+ was the finder). Deactivate trailed instead; each frame dies when
    /// its own push entry unwinds.</summary>
    public void DeactivateCatchFramesAbove(int count)
    {
        for (int i = _catchFrames.Count - 1; i >= count; i--)
        {
            if (!_catchFrames[i].Active) continue;
            CatchFrame f = _catchFrames[i];
            f.Active = false;
            _catchFrames[i] = f;
            if (CatchDiag)
                System.Console.Error.WriteLine($"[catch] deact-above idx={i} xTop={_extraTrailTop}");
            EnsureExtraTrailCapacity(1);
            _extraTrail[_extraTrailTop++] = new ExtraTrailEntry
            {
                Type = TrailType.CatchFrame,
                HeapIdx = i,
                OldValue = new Cell(CatchTrailDeactivate),
                BindingTrailMarker = _bindingTrailTop,
            };
        }
    }

    /// <summary>Reads the catch frame at <paramref name="index"/>.</summary>
    public CatchFrame GetCatchFrame(int index) => _catchFrames[index];

    /// <summary>Pushes a catch frame for a <c>catch/3</c> entry, snapshotting
    /// the live machine. The push is recorded on the extra trail, so
    /// backtracking past the catch removes the frame again.
    /// <paramref name="catcherHeapIdx"/> and <paramref name="recoveryHeapIdx"/>
    /// name heap slots that must already be allocated (so the snapshot's
    /// heap top covers them and they survive a caught throw's truncation).
    ///
    /// <para>The recovery continuation — where the enclosing clause resumes
    /// after recovery — is the current environment's CE / CP header: this
    /// method must be called as the first goal of the catch goal-helper, so
    /// the live environment is that helper's frame and its header points at
    /// the clause that contained the original <c>catch/3</c>.</para></summary>
    public void PushCatchFrame(int catcherHeapIdx, int recoveryHeapIdx)
    {
        int index = _catchFrames.Count;
        _catchFrames.Add(new CatchFrame
        {
            CatcherHeapIdx = catcherHeapIdx,
            RecoveryHeapIdx = recoveryHeapIdx,
            Active = true,
            SnapB = _b,
            SnapE = _e,
            SnapHeapTop = _heapTop,
            SnapHb = _hb,
            SnapBindingTrailTop = _bindingTrailTop,
            SnapExtraTrailTop = _extraTrailTop,
            SnapGuardContTop = _guardContTop,
            SnapPendingWakeups = _pendingWakeups.Count,
            RecoveryE = (int)_stack[_e + EnvCeOffset].Data,
            RecoveryCp = (int)_stack[_e + EnvCpOffset].Data,
        });
        if (CatchDiag)
            System.Console.Error.WriteLine($"[catch] push idx={index} xTop={_extraTrailTop}");
        EnsureExtraTrailCapacity(1);
        _extraTrail[_extraTrailTop++] = new ExtraTrailEntry
        {
            Type = TrailType.CatchFrame,
            HeapIdx = index,
            OldValue = new Cell(CatchTrailPush),
            BindingTrailMarker = _bindingTrailTop,
        };
        // Lower the heap boundary like a choice point would: a caught throw
        // rolls the heap back to here, so every binding the guarded goal
        // makes to an older cell must be trailed to be reversible. The
        // pre-catch _hb is kept in the frame's snapshot and restored on a
        // catch (UnwindToCatchFrame).
        AssignHb(_heapTop);
    }

    /// <summary>Deactivates the top-most still-active catch frame: control
    /// has left its guarded goal, so a later throw must not be caught
    /// there. The change is trailed, so backtracking into the guarded goal
    /// re-activates it. A no-op when there is no active frame.</summary>
    public void DeactivateTopCatchFrame()
    {
        for (int i = _catchFrames.Count - 1; i >= 0; i--)
        {
            if (!_catchFrames[i].Active) continue;
            CatchFrame f = _catchFrames[i];
            f.Active = false;
            _catchFrames[i] = f;
            if (CatchDiag)
                System.Console.Error.WriteLine($"[catch] deact idx={i} xTop={_extraTrailTop}");
            EnsureExtraTrailCapacity(1);
            _extraTrail[_extraTrailTop++] = new ExtraTrailEntry
            {
                Type = TrailType.CatchFrame,
                HeapIdx = i,
                OldValue = new Cell(CatchTrailDeactivate),
                BindingTrailMarker = _bindingTrailTop,
            };
            return;
        }
    }

    /// <summary>Rolls the machine back to the state captured when catch
    /// frame <paramref name="index"/> was pushed — undoing everything the
    /// guarded goal did — and resumes at that catch's recovery
    /// continuation. The extra-trail unwind also discards that frame and
    /// every frame above it. Used by the throw handler after a catcher has
    /// matched; the caller then runs the recovery goal.</summary>
    /// <summary>SHUMWAY_ATTR_VERIFY=1 debug sweep: reports any live attvar
    /// whose stored attribute index dangles above the current heap top —
    /// i.e. a truncation that outran the attr trail. The first site that
    /// logs is the truncation that lost the restore.</summary>
    public void DebugSweepAttrTable(string site)
    {
        foreach (var kv in _attrTable)
        {
            int home = kv.Key;
            if (home >= _heapTop || _heap[home].Tag != Tag.AttVar) continue;
            foreach (var rec in kv.Value)
                if (rec.Value >= _heapTop)
                    System.Console.Error.WriteLine(
                        $"[ATTR-SWEEP] {site}: var@{home} module={AtomTable.GetById(rec.Key)?.Name}"
                        + $" attr->heap[{rec.Value}] >= heapTop={_heapTop}");
        }
    }

    public static readonly bool AttrSweepEnabled =
        System.Environment.GetEnvironmentVariable("SHUMWAY_ATTR_VERIFY") == "1";

    public void UnwindToCatchFrame(int index)
    {
        if (CatchDiag)
            System.Console.Error.WriteLine($"[catch] unwindTo idx={index} count={_catchFrames.Count} xTop={_extraTrailTop}");
        CatchFrame f = _catchFrames[index];
        // setup_call_cleanup/3: an exception unwinding to this frame discards
        // every choice-point scope above it — fire the cleanup of any registered
        // scope that is being abandoned (its Goal's CPs sit above the frame's
        // snapshot level). The drain runs at the recovery goal's first safe point.
        if (HasCleanupHandlers) FireCleanupsAbove(f.SnapB, heapIntact: false);
        if (AttrSweepEnabled)
        {
            int attrEntries = 0, oldHomeEntries = 0;
            for (int i = f.SnapExtraTrailTop; i < _extraTrailTop; i++)
                if (_extraTrail[i].Type == TrailType.AttrModify)
                {
                    attrEntries++;
                    if (_attrTrailLog[_extraTrail[i].HeapIdx].Home < 100) oldHomeEntries++;
                }
            System.Console.Error.WriteLine(
                $"[ATTR-SWEEP] pre_catch_unwind frame={index}/{_catchFrames.Count}"
                + $" snapX={f.SnapExtraTrailTop} xTop={_extraTrailTop}"
                + $" snapH={f.SnapHeapTop} hTop={_heapTop}"
                + $" attrEntriesAbove={attrEntries} queryVarHomes={oldHomeEntries}"
                + $" attrLogCount={_attrTrailLog.Count}");
        }
        UnwindTrails(f.SnapBindingTrailTop, f.SnapExtraTrailTop);
        _heapTop = f.SnapHeapTop;
        AssignHb(f.SnapHb);
        if (AttrSweepEnabled) DebugSweepAttrTable("catch_unwind");
        // Wakeups queued by the guarded goal reference heap cells the
        // truncation above just discarded — drop them with it.
        if (_pendingWakeups.Count > f.SnapPendingWakeups)
            _pendingWakeups.RemoveRange(
                f.SnapPendingWakeups, _pendingWakeups.Count - f.SnapPendingWakeups);
        _b = f.SnapB;
        // ADR-033 — drop guard-continuation entries the guarded goal pushed.
        _guardContTop = f.SnapGuardContTop;
        // The recovery goal runs as a fresh predicate activation, so its cut
        // barrier is the restored choice-point level.
        _b0 = f.SnapB;
        _e = f.RecoveryE;
        _cp = f.RecoveryCp;
        _stackTop = f.SnapE;
    }

    /// <summary>
    /// Pushes an environment frame with <paramref name="numPermanents"/> Y slots onto the
    /// stack, saving the current <see cref="E"/> as CE and <see cref="Cp"/> as CP. The Y
    /// slots are initialised to a self-pointing REF as an "uninitialised" marker; the
    /// compiler guarantees each Y is written by an instruction (e.g. <c>get_variable</c>)
    /// before any read, so this marker is never actually dereferenced. After the call
    /// <see cref="E"/> points at the new frame.
    /// </summary>
    public void Allocate(int numPermanents)
    {
        if (numPermanents < 0)
            throw new ArgumentOutOfRangeException(nameof(numPermanents));

        int frameSize = EnvSize(numPermanents);
        EnsureStackCapacity(frameSize);

        if (TraceCpStack)
            System.Console.Error.WriteLine($"[cp-stack] alloc(n={numPermanents}) _b={_b} _e={_e} _stackTop={_stackTop} -> newE={_stackTop}");
        int newE = _stackTop;
        // CE / CP / N are control words: tag them RawInt (ADR-016) so the
        // heap GC never mistakes one for a heap Ref. N is the per-frame
        // live-permanent count the GC reads (via (int)Data) to scan Y-slots.
        _stack[newE + EnvCeOffset] = Cell.RawInt(_e);
        _stack[newE + EnvCpOffset] = Cell.RawInt(_cp);
        _stack[newE + EnvNOffset] = Cell.RawInt(numPermanents);
        // Y slots are left UNINITIALISED — tagged Cell.RawInt(0), which the heap
        // GC's conservative stack scan skips (it is not a Ref), and which is
        // never read: standard WAM codegen writes a permanent at its first
        // occurrence (get_variable_y / put_variable_y / unify_variable_y, all of
        // which overwrite the slot — put_variable_y allocates its own heap var)
        // before any later occurrence reads it. Eagerly allocating a fresh
        // heap-unbound var per permanent here (an earlier design, for Deref/Unify
        // uniformity) was pure garbage in permanent-heavy loops — the slot is
        // overwritten by the very next instruction, so the cell is immediately
        // dead and drives the heap GC. Lazy allocation (this) matches a textbook
        // WAM, where `allocate` does not touch the Y slots.
        for (int i = 0; i < numPermanents; i++)
            _stack[newE + EnvY1Offset + i] = Cell.RawInt(0);
        _stackTop = newE + frameSize;
        _e = newE;
    }

    /// <summary>
    /// Restores <see cref="Cp"/> and <see cref="E"/> from the current frame, and
    /// reclaims the popped frame's stack space when no choice point protects it
    /// (the standard WAM environment trimming on deallocate). Without the
    /// reclamation a deterministic tail-recursive loop — which runs
    /// <c>deallocate; execute</c> every iteration and never backtracks — would
    /// leave each frame in place and grow the stack by one frame per iteration,
    /// forcing repeated stack-array reallocation (visible as a large
    /// <c>Buffer.Memmove</c> share in a profile).
    /// </summary>
    public void Deallocate()
    {
        if (_e < 0)
            throw new InvalidOperationException("Deallocate called without an active environment frame.");
        int oldE = _e;
        _cp = (int)_stack[oldE + EnvCpOffset].Data;
        _e = (int)_stack[oldE + EnvCeOffset].Data;
        // _b is the most recent choice point's stack index. _b < oldE means
        // every live CP sits below the just-popped frame, so nothing at or above
        // oldE is live (the frame was the topmost region) and its space is free.
        // When a CP is at or above oldE (the clause body left an open choice
        // point) the frame must stay — a backtrack could reactivate it — so the
        // reclamation degrades to the original no-op. Only ever lowers _stackTop.
        //
        // The floor is NOT oldE: a cut-discarded choice point may sit DEAD
        // directly below the frame (try_me_else pushed per call, killed by the
        // clause's cut), and stopping at oldE leaks its slots FOREVER on LCO
        // loops — a lazy DCG grew ~15 dead slots per parsed line. Everything
        // live on the control stack is reachable from the E-chain or the
        // B-chain, and both grow upward (a CP's saved CE frame predates the
        // CP; a frame's CE parent predates it), so the true top of live stack
        // is max(E's frame end, B's frame end) — reclaim down to it.
        if (_b < oldE && _stackTop > oldE)
        {
            int eTop = 0;
            if (_e >= 0)
            {
                int n = (int)_stack[_e + EnvNOffset].Data;
                eTop = _e + EnvSize(n < 0 ? 0 : n);
            }
            int bTop = 0;
            if (_b >= 0)
            {
                int ar = (int)_stack[_b + CpArityOffset].Data;
                bTop = _b + CpSize(ar);
            }
            int live = eTop > bTop ? eTop : bTop;
            _stackTop = live < oldE ? live : oldE;
        }
    }

    /// <summary>Reads the <c>Y(k+1)</c> slot of the current environment frame.</summary>
    // the inline throw blocked JIT inlining of these two,
    // which every Y-slot opcode in BOTH tiers calls. Hoisted to the cold
    // ThrowNoEnv helper (the ThrowBadAlloc pattern) + AggressiveInlining.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public Cell GetY(int slot)
    {
        if (_e < 0) ThrowNoEnv();
        return _stack[_e + EnvY1Offset + slot];
    }

    /// <summary>ADR-035 — a Y slot of a NAMED environment, rather than the current one:
    /// a debugger reads the variables of every frame on the stack, not just the
    /// innermost.</summary>
    public Cell GetY(int env, int slot)
    {
        if (env < 0) ThrowNoEnv();
        return _stack[env + EnvY1Offset + slot];
    }

    /// <summary>ADR-035 — the environment chain from <paramref name="e"/> outwards,
    /// innermost first. These are exactly the clauses that have a frame, in call order,
    /// which is what lets a debugger line each stack frame up with the environment its
    /// variables live in.</summary>
    public IEnumerable<int> EnumerateEnvChain(int e)
    {
        while (e >= 0)
        {
            yield return e;
            int prevE = (int)_stack[e + EnvCeOffset].Data;
            if (prevE == e || prevE < 0) yield break;
            e = prevE;
        }
    }

    /// <summary>ADR-035 — the return address an environment frame saved at allocate: the
    /// caller's continuation, authoritative for the frame walk (unlike the Cp REGISTER,
    /// which between two calls of a clause body still holds the completed previous call's
    /// return — dead state).</summary>
    public int EnvSavedCp(int e) => e >= 0 ? (int)_stack[e + EnvCpOffset].Data : -1;

    // ADR-035 D5+ — Set Next Statement onto a SIBLING clause's head: after rewinding to
    // the caller's goal and re-running its argument setup, the next dispatch of that call
    // enters the CHOSEN clause instead of the predicate's entry. The entry FUNCTION (built
    // by the debug service, which knows the predicate's clause table) receives the
    // activation at the dispatch point — arguments loaded, Cp/B0 set — pushes the
    // clause-alternative choice point for the clauses AFTER the chosen one (standard
    // Prolog: if the chosen clause fails and did not cut, the following clauses are
    // tried), and returns the clause's code address. One-shot, consumed at the first
    // dispatch whatever its target.
    private int _debugClauseEntryPred = -1;
    private Func<Activation, int>? _debugClauseEntryEnter;

    public void ArmDebugClauseEntry(int predicateAddress, Func<Activation, int> enter)
    {
        _debugClauseEntryPred = predicateAddress;
        _debugClauseEntryEnter = enter;
    }

    public bool DebugClauseEntryArmed => _debugClauseEntryPred >= 0;

    /// <summary>The predicate a pending re-enter targets (-1 when none): while a stop
    /// holds one, its clause heads remain Set Next Statement targets — the user may
    /// change which clause to enter any number of times before resuming.</summary>
    public int DebugClauseEntryPredicate => _debugClauseEntryPred;

    /// <summary>Cancels a pending re-enter: any OTHER successful Set Next Statement while
    /// one is armed means the user changed their mind away from it.</summary>
    public void DisarmDebugClauseEntry()
    {
        _debugClauseEntryPred = -1;
        _debugClauseEntryEnter = null;
    }

    /// <summary>ADR-035 D5+ — the debugger's DESTRUCTIVE variable edit: resets the cell at
    /// <paramref name="addr"/> to an unbound variable, trailing the old value so
    /// backtracking (and a Set Next Statement rewind) restores the binding the program
    /// had made. The Watch-window edit builds on this: un-instantiate, or clear-then-bind
    /// to a new term. Only meaningful while the activation is stopped in a debugger.</summary>
    public void DebugUnbindCell(int addr)
    {
        TrailValueChange(addr, _heap[addr]);
        _heap[addr] = Cell.Ref(addr);
    }

    /// <summary>Consumes the armed re-enter. True (with the entry function) when the
    /// dispatch target IS the armed predicate; the arm is cleared either way — it was
    /// meant for the very next call, and a different target means the world moved on.</summary>
    public bool TryTakeDebugClauseEntry(int target, out Func<Activation, int> enter)
    {
        enter = _debugClauseEntryEnter!;
        bool match = target == _debugClauseEntryPred && enter is not null;
        _debugClauseEntryPred = -1;
        _debugClauseEntryEnter = null;
        return match;
    }

    /// <summary>Writes the <c>Y(k+1)</c> slot of the current environment frame.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void SetY(int slot, Cell value)
    {
        if (_e < 0) ThrowNoEnv();
        _stack[_e + EnvY1Offset + slot] = value;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowNoEnv()
        => throw new InvalidOperationException("No environment frame is active.");

    // ----- Choice-point frame layout (ADR-005) -----
    //
    // Layout: [arity | A1 .. Aarity | CE | CP | B | BP | BindingTrailTop |
    //          ExtraTrailTop | HeapTop | Hb | ViewGen | B0]
    // Total size = 11 + arity cells (CpSize).
    //
    // ViewGen (ADR-015, bytecode-level dispatch): the live
    // CurrentViewGen register at push time — the "logical update view"
    // timestamp sampled by the innermost enter_dynamic. Restored on every
    // CP restore so a CheckVisible in a dynamic chain, re-entered by
    // backtracking, reads the same view-gen its activation started with.
    // The slot is uniform across CPs even though only dynamic-chain CPs
    // semantically need it (CheckVisible is CurrentViewGen's ONLY reader);
    // ADR-026 analyzed splitting the frame into narrow (static) / wide
    // (dynamic-chain) widths and REJECTED it — the measured ceiling on the
    // most CP-intensive synthetic is 0.3-1% (below noise), against ~13
    // frame-walker mask sites each a silent-corruption hazard. The tiny
    // per-CP cost buys a single uniform save/restore path.

    public const int CpArityOffset = 0;
    public const int CpArg1Offset = 1;

    public static int CpCeOffset(int arity) => 1 + arity;
    public static int CpCpOffset(int arity) => 1 + arity + 1;
    public static int CpBOffset(int arity) => 1 + arity + 2;
    public static int CpBpOffset(int arity) => 1 + arity + 3;
    public static int CpBindingTrailOffset(int arity) => 1 + arity + 4;
    public static int CpExtraTrailOffset(int arity) => 1 + arity + 5;
    public static int CpHeapTopOffset(int arity) => 1 + arity + 6;
    public static int CpHbOffset(int arity) => 1 + arity + 7;
    public static int CpViewGenOffset(int arity) => 1 + arity + 8;
    /// <summary>The cut barrier (<c>_b0</c>) in effect when this choice
    /// point was pushed. Saved/restored so a clause's deep cut
    /// (<c>get_level</c> + <c>cut</c>) reads the predicate-entry barrier
    /// even after an earlier clause's nested <c>Call</c> clobbered the
    /// global <c>_b0</c> register and a backtrack re-entered a later
    /// clause.</summary>
    public static int CpB0Offset(int arity) => 1 + arity + 9;

    /// <summary>Size in cells of a choice-point frame with <paramref name="arity"/> saved args.</summary>
    public static int CpSize(int arity) => 11 + arity;

    /// <summary>
    /// Pushes a choice point onto the stack, snapshotting the first <paramref name="arity"/>
    /// argument registers and the engine's continuation/trail/heap state. After the call
    /// <see cref="B"/> points at the new CP and <see cref="Hb"/> is bumped to the current
    /// <see cref="HeapTop"/> so that subsequent bindings of pre-CP heap cells get trailed.
    /// </summary>
    // SHUMWAY_PC_RING=1 forensics: every CP push / BP update records
    // (current P, bp) so a backtrack landing on garbage can be traced to the
    // exact push. Null (and JIT-eliminated at the record sites) by default.
    public const int CpPushRingSize = 1 << 20;
    public static readonly long[]? CpPushRing =
        System.Environment.GetEnvironmentVariable("SHUMWAY_PC_RING") == "1"
            ? new long[CpPushRingSize] : null;
    public static int CpPushRingPos;

    public void PushChoicePoint(int arity, int nextClauseAddr)
    {
        if (arity < 0)
            throw new ArgumentOutOfRangeException(nameof(arity));
        if (CpPushRing is { } cpRing)
            cpRing[CpPushRingPos++ & (CpPushRingSize - 1)] =
                ((long)P << 32) | (uint)nextClauseAddr;
        if (arity > _registers.Length) EnsureRegisterCapacity(arity);

        Profiler.ChoicePoint();
        int size = CpSize(arity);
        EnsureStackCapacity(size);

        if (TraceCpStack)
            System.Console.Error.WriteLine($"[cp-stack] push  _b={_b} _e={_e} _stackTop={_stackTop} bp=0x{nextClauseAddr:X} arity={arity} -> newB={_stackTop}");
        int newB = _stackTop;
        // Control words are tagged RawInt (ADR-016) so the heap GC never
        // mistakes a small control value for a heap Ref; only the saved
        // A-register args below are genuine cells the collector relocates.
        _stack[newB + CpArityOffset] = Cell.RawInt(arity);
        // Args: a tiny per-element loop. Span.CopyTo's Memmove machinery
        // is slower than a plain loop for typical arities (0-3).
        for (int i = 0; i < arity; i++)
            _stack[newB + CpArg1Offset + i] = _registers[i];

        // hoist a Span<Cell> over the contiguous control
        // word block so the 10 writes share a single bounds check.
        // EnsureStackCapacity just guaranteed _stack[newB..newB+size)
        // is in range; writing back through `_stack[newB + offset] = …`
        // would re-bounds-check each one.
        int ctlBase = newB + 1 + arity;
        Span<Cell> ctl = _stack.AsSpan(ctlBase, 10);
        ctl[0] = Cell.RawInt(_e);                  // CpCeOffset
        ctl[1] = Cell.RawInt(_cp);                 // CpCpOffset
        ctl[2] = Cell.RawInt(_b);                  // CpBOffset
        ctl[3] = Cell.RawInt(nextClauseAddr);      // CpBpOffset
        ctl[4] = Cell.RawInt(_bindingTrailTop);    // CpBindingTrailOffset
        ctl[5] = Cell.RawInt(_extraTrailTop);      // CpExtraTrailOffset
        ctl[6] = Cell.RawInt(_heapTop);            // CpHeapTopOffset
        ctl[7] = Cell.RawInt(_hb);                 // CpHbOffset
        ctl[8] = Cell.RawInt(CurrentViewGen);      // CpViewGenOffset
        ctl[9] = Cell.RawInt(_b0);                 // CpB0Offset

        _stackTop = newB + size;
        _b = newB;
        AssignHb(_heapTop);
    }

    /// <summary>
    /// Restores engine state from the current choice point and updates its BP slot to
    /// <paramref name="nextClauseAddr"/>. The CP itself is preserved so subsequent failures
    /// will retry against the new <c>nextClauseAddr</c>. <see cref="Hb"/> is reset to the
    /// just-restored <see cref="HeapTop"/> — the CP is still active so its heap boundary
    /// remains in effect.
    /// </summary>
    public void RetryMeElse(int nextClauseAddr)
    {
        if (_b < 0)
            throw new InvalidOperationException("RetryMeElse called without an active choice point.");
        int arity = RestoreCommonFromCurrentCp();
        AssignHb(_heapTop);
        if (CpPushRing is { } cpRing)
            cpRing[CpPushRingPos++ & (CpPushRingSize - 1)] =
                ((long)P << 32) | (uint)nextClauseAddr;
        _stack[_b + CpBpOffset(arity)] = Cell.RawInt(nextClauseAddr);
    }

    /// <summary>
    /// Restores engine state from the current choice point and discards it: <see cref="B"/>
    /// reverts to the previous CP and the stack slots occupied by the discarded CP (plus
    /// any frames above it) are reclaimed. <see cref="Hb"/> is reset to the value saved
    /// when this CP was pushed — i.e., the boundary of the now-current (previous) CP.
    /// </summary>
    public void TrustMe()
    {
        if (_b < 0)
            throw new InvalidOperationException("TrustMe called without an active choice point.");
        int arity = RestoreCommonFromCurrentCp();
        AssignHb((int)_stack[_b + CpHbOffset(arity)].Data);
        int oldB = _b;
        _b = (int)_stack[_b + CpBOffset(arity)].Data;
        _stackTop = oldB;
    }

    /// <summary>Restores registers, E, CP, trails, and HeapTop from the current CP. Returns
    /// the saved arity. Does NOT set <see cref="Hb"/> — that differs between Retry and Trust
    /// and is the caller's responsibility.</summary>
    private int RestoreCommonFromCurrentCp()
    {
        int b = _b;
        int arity = (int)_stack[b + CpArityOffset].Data;
        // Args back to register bank: tiny loop, no Span.CopyTo (whose
        // Memmove machinery is slower than the loop for typical arity).
        for (int i = 0; i < arity; i++)
            _registers[i] = _stack[b + CpArg1Offset + i];

        // single Span over the contiguous control-word
        // block so the JIT can elide per-field bounds checks. Mirrors
        // the PushChoicePoint layout.
        int ctlBase = b + 1 + arity;
        Span<Cell> ctl = _stack.AsSpan(ctlBase, 10);
        _e = (int)ctl[0].Data;                     // CpCeOffset
        _cp = (int)ctl[1].Data;                    // CpCpOffset
        int bindingTarget = (int)ctl[4].Data;      // CpBindingTrailOffset
        int extraTarget = (int)ctl[5].Data;        // CpExtraTrailOffset
        UnwindTrails(bindingTarget, extraTarget);

        _heapTop = (int)ctl[6].Data;               // CpHeapTopOffset
        if (AttrSweepEnabled) DebugSweepAttrTable("cp_restore");
        // ViewGen is a 60-bit value; read via Payload to strip the RawInt tag.
        CurrentViewGen = ctl[8].Payload;           // CpViewGenOffset
        // Restore the cut barrier in effect when this CP was pushed
        // (i.e. the enclosing predicate's entry barrier). Without this a
        // deep cut in a later clause, reached by backtracking, would
        // read a _b0 left clobbered by an earlier clause's nested Call.
        _b0 = (int)ctl[9].Data;                    // CpB0Offset
        return arity;
    }

    // ----- Cut -----

    /// <summary>
    /// Discards every choice point above <paramref name="barrier"/>, then compacts the
    /// binding and extra trails: entries above the parent CP's saved trail tops that bind
    /// or reference heap cells created after the parent's heap top are dropped (those
    /// cells will be truncated on any outer backtrack, so the trail entries serve no
    /// purpose). For each surviving extra-trail entry the <c>BindingTrailMarker</c> is
    /// adjusted to the compacted binding-trail position so the interleaved unwind from
    /// <see cref="UnwindTrails"/> still processes mutations in the right order.
    ///
    /// <para>If <paramref name="barrier"/> is <c>-1</c>, every remaining CP is discarded
    /// and the trails are fully compacted with parent-heap-top = 0 — i.e. emptied,
    /// since nothing can backtrack past a cut to "no choice point at clause entry".</para>
    /// </summary>
    public void Cut(int barrier)
    {
        if (barrier < -1)
            throw new ArgumentOutOfRangeException(nameof(barrier));
        // a stale barrier (above current B) means the
        // choice point the cut wanted to commit to has already been
        // popped — typically by a surrounding catch/3 unwinding past
        // the clause-entry snapshot. ISO semantics: cut commits to
        // the most recent *active* CP at clause entry; if that CP is
        // gone, the cut is a no-op. Don't throw.
        if (barrier > _b)
            return;
        if (_b == barrier)
            return;

        // setup_call_cleanup/3: a cut past a registered scope discards it without
        // a backtrack into its cleanup — enqueue the cleanup for the next safe
        // point. Cheap no-op when nothing is registered.
        if (HasCleanupHandlers) FireCleanupsAbove(barrier);

        // Drop IL CP stack entries above the
        // barrier BEFORE _b moves. Each entry's Key is its frame's
        // stack-B position; the entries are pushed in monotonic _b
        // order so any stale ones sit at the top of _ilCpStack —
        // pop them down to the first <= barrier. Replaces the
        // foreach-over-dict-Keys loop (which was 5.31%
        // self-time on Activation.Cut in Blint with user IL active,
        // plus a List<int> allocation per cut that hit any IL CP).
        while (_ilCpTop > 0 && _ilCpStack[_ilCpTop - 1].Key > barrier)
        {
            // fire the optional cleanup hook before
            // dropping the entry. Non-det foreign predicates
            // register iter.Dispose here so a generator's
            // try / finally / using runs deterministically when
            // Prolog `!` cuts past the CP.
            var onPrune = _ilCpStack[_ilCpTop - 1].OnPrune;
            if (onPrune is not null) onPrune();
            _ilCpStack[_ilCpTop - 1].Del = null!;     // release delegate
            _ilCpStack[_ilCpTop - 1].OnPrune = null;  // release callback
            _ilCpTop--;
        }

        _b = barrier;

        int parentBindingTop, parentExtraTop, parentHeapTop;
        if (_b < 0)
        {
            parentBindingTop = 0;
            parentExtraTop = 0;
            parentHeapTop = 0;
        }
        else
        {
            int arity = (int)_stack[_b + CpArityOffset].Data;
            parentBindingTop = (int)_stack[_b + CpBindingTrailOffset(arity)].Data;
            parentExtraTop = (int)_stack[_b + CpExtraTrailOffset(arity)].Data;
            parentHeapTop = (int)_stack[_b + CpHeapTopOffset(arity)].Data;
        }

        CompactTrails(parentBindingTop, parentExtraTop, parentHeapTop);
    }

    /// <summary>Copies the current <see cref="B0"/> into <c>Y[slot]</c> of the current
    /// environment frame. Used by the WAM <c>get_level</c> instruction to capture the
    /// cut barrier in a permanent variable.
    ///
    /// <para><c>B0</c> is the procedure-entry value of <c>B</c> recorded by every
    /// <c>call</c> / <c>execute</c>. Capturing it at the start of the body (in a Y
    /// slot, so it survives sub-goal calls that overwrite the <c>B0</c> register)
    /// gives the compiler exactly the barrier ISO Prolog's <c>!</c> commits to:
    /// every choice point above the predicate's entry point, including the
    /// predicate's own <c>try_me_else</c> CP and any CPs pushed by sub-goals.</para></summary>
    // The captured cut barrier is a choice-point stack index, not a heap
    // reference — store it as a RawInt control word so the heap GC
    // (ADR-016, conservative stack scan) never mistakes it for a Ref and
    // relocates it. The Cut opcode reads it back with a tag-agnostic
    // (int)Data cast.
    public void GetLevel(int slot) => SetY(slot, Cell.RawInt(_b0));

    /// <summary>ADR-025 — <c>Y[slot] := RawInt(B)</c>: capture the CURRENT
    /// choice-point top as the inline-ITE commit barrier. <see cref="GetLevel"/>
    /// captures <c>B0</c>, which any pre-ITE body call resets — cutting to it
    /// pruned a preceding generator's choice points (the helper form never saw
    /// this because the helper CALL re-established B0 at its own entry).</summary>
    public void GetLevelB(int slot) => SetY(slot, Cell.RawInt(_b));

    /// <summary>
    /// Cut back to <see cref="B0"/> — the value of <c>B</c> recorded at the most recent
    /// procedure entry. This is the implicit barrier used by the WAM <c>neck_cut</c>
    /// instruction. The interpreter's <c>call</c> and <c>execute</c> opcodes maintain
    /// <c>B0</c> by writing <c>_b</c> into it before transferring control to the callee.
    /// </summary>
    public void NeckCut() => Cut(_b0);

    /// <summary>Cut to the barrier captured earlier by <see cref="GetLevel"/>
    /// in <c>Y[slot]</c> — the WAM <c>cut</c> (deep cut) instruction. The
    /// stored barrier is a <see cref="Tag.RawInt"/> control word; read it
    /// back tag-agnostically with <c>(int)Data</c> (the cast ignores the
    /// tag bits above bit 31). Mirrors the bytecode interpreter's
    /// <c>cut</c> opcode so Tier-1 IL can emit deep cut as a single engine
    /// call.</summary>
    public void CutToLevel(int slot) => Cut((int)GetY(slot).Data);

    /// <summary>The <c>BP</c> (next-alternative) value written by
    /// <see cref="SoftCut"/> to mark an ELSE choice point neutralised. Distinct
    /// from the <c>-1</c> sentinel an IL choice-point frame carries; a real code
    /// address is a non-negative offset, so <c>-2</c> can never collide.</summary>
    public const int SoftCutDeadBp = -2;

    /// <summary>ADR-037 — soft cut. <paramref name="barrier"/> names the ELSE
    /// choice point of an inline <c>( Cond *-> Then ; Else )</c> (captured into a
    /// Y slot by a <c>get_level_b</c> emitted AFTER the <c>try_me_else</c>). Once
    /// <c>Cond</c> succeeds this commits away the <c>Else</c> alternative:
    /// <list type="bullet">
    /// <item>If the ELSE CP is the current top (<c>Cond</c> left no choice point),
    /// it is DISCARDED — cut to its parent, keeping all current bindings — so the
    /// whole <c>*-></c> is deterministic and the top level sees no remaining
    /// alternative (this is what makes <c>time(true)</c> determinate).</item>
    /// <item>If <c>Cond</c> left choice points ABOVE the ELSE CP, that frame is a
    /// middle frame and cannot be popped, so it is NEUTRALISED — its <c>BP</c>
    /// slot is set to <see cref="SoftCutDeadBp"/>. Backtracking that later reaches
    /// it (after the condition's CPs are exhausted) pops it and keeps
    /// backtracking instead of running <c>Else</c>, so the condition's
    /// non-determinism survives while <c>Else</c> never runs.</item>
    /// </list>
    /// A stale barrier (already popped, e.g. by a surrounding <c>catch/3</c>
    /// unwind) is a no-op, exactly as <see cref="Cut"/> treats one.</summary>
    public void SoftCut(int barrier)
    {
        if (barrier < 0 || barrier > _b)
            return;
        int arity = (int)_stack[barrier + CpArityOffset].Data;
        if (barrier == _b)
        {
            // ELSE CP is the top → the condition was deterministic → discard it
            // (cut to its parent) so no neutralised-but-present frame lingers.
            // Cut also drops the matching _ilCpStack entry when it is an IL CP.
            Cut((int)_stack[barrier + CpBOffset(arity)].Data);
            return;
        }
        // Middle frame. A Tier-1 inline-ITE ELSE CP carries the IL sentinel BP and
        // is resumed by its delegate (the dead-BP sentinel would be ignored), so
        // neutralise its _ilCpStack entry instead; a Tier-0 ELSE CP is resumed via
        // its BP, so patch that to the dead sentinel.
        if ((int)_stack[barrier + CpBpOffset(arity)].Data == IlChoicePointSentinelBp)
            NeutralizeIlChoicePoint(barrier);
        else
            _stack[barrier + CpBpOffset(arity)] = Cell.RawInt(SoftCutDeadBp);
    }

    /// <summary>ADR-037 — the Tier-1 IL entry point for <c>soft_cut</c>: reads the
    /// barrier stashed in <c>Y[slot]</c> by <c>get_level_b</c> and applies
    /// <see cref="SoftCut(int)"/>. Mirrors <see cref="CutToLevel"/>.</summary>
    public void SoftCutToLevel(int slot) => SoftCut((int)GetY(slot).Data);

    /// <summary>
    /// Single-pass interleaved trail compaction (Warren's algorithm extended to the extra
    /// trail). Both trails are walked in temporal order: for each surviving extra entry,
    /// its <see cref="ExtraTrailEntry.BindingTrailMarker"/> is rewritten to the index it
    /// would occupy in the compacted binding trail, preserving the relative ordering that
    /// <see cref="UnwindTrails"/> relies on.
    /// </summary>
    private void CompactTrails(int parentBindingTop, int parentExtraTop, int parentHeapTop)
    {
        // ADR-035 D5+ — under a debug session the trail IS the debugger's history: Set
        // Next Statement's rewind marks index positions in it, and TrailEverything grew it
        // precisely so every binding since any mark can be undone. Cut-time compaction is
        // a pure optimisation (it drops entries no future BACKTRACK could need) and it
        // destroyed that history wholesale — a real program cuts constantly (every Blint
        // clause ends in !), the trail collapsed to a handful of entries, and the marks'
        // saved tops read as "backtracked past" and were purged. Same trade as the pinned
        // Hb: the optimisation stands down while a debugger needs the past.
        if (_trailEverything) return;

        // I5 — fast no-op cut: when nothing was trailed since the
        // parent CP both tops already equal the parent's, so both compaction
        // walks are empty and — since the trail only grows between CPs — no
        // catch-frame snapshot can sit above the (unchanged) top. The whole
        // body is a no-op, INCLUDING the O(_catchFrames) snapshot-clip loop
        // that otherwise runs on every cut (deep catch nesting made every
        // deterministic cut pay O(catch frames) for nothing).
        if (parentBindingTop == _bindingTrailTop && parentExtraTop == _extraTrailTop)
            return;

        // A cut's "young entry" drop reasons about BACKTRACKING: anything
        // above the parent CP's heap top is truncated by any outer
        // backtrack, so restoring it is moot. But an ACTIVE CATCH FRAME is
        // a second unwind consumer: its throw truncates the heap only to
        // ITS SnapHeapTop, and every mutation of an OLDER cell / attvar
        // made inside the guarded goal must still be restored then. With
        // no parent CP at all (barrier -1 → parentHeapTop 0) the old rule
        // emptied the trails wholesale while a catch frame was live —
        // clpz's with_local_attributes (mutate attrs → throw to undo) then
        // found nothing to undo, leaving every fd variable's attribute
        // pointing at heap the throw had truncated (the send_more_money
        // phantom-functor crash). Raise the survival floor to the highest
        // active frame's snapshot: entries whose target is below it must
        // survive for that frame's unwind.
        int effectiveFloor = parentHeapTop;
        for (int fi = 0; fi < _catchFrames.Count; fi++)
        {
            CatchFrame cf = _catchFrames[fi];
            if (cf.Active && cf.SnapHeapTop > effectiveFloor)
                effectiveFloor = cf.SnapHeapTop;
        }

        int bindingRead = parentBindingTop;
        int bindingWrite = parentBindingTop;
        int extraRead = parentExtraTop;
        int extraWrite = parentExtraTop;

        while (extraRead < _extraTrailTop)
        {
            var entry = _extraTrail[extraRead];
            int marker = entry.BindingTrailMarker;

            // Compact all binding entries that came before this extra entry.
            int stop = marker < _bindingTrailTop ? marker : _bindingTrailTop;
            while (bindingRead < stop)
            {
                int idx = _bindingTrail[bindingRead];
                if (idx < effectiveFloor)
                    _bindingTrail[bindingWrite++] = idx;
                bindingRead++;
            }

            // Decide whether this entry must survive the cut's compaction.
            // The "young heap cell" drop rule keeps an entry only when it
            // references a cell that pre-dates the parent CP's heap top
            // (younger cells are truncated on any outer backtrack, so the
            // entry would serve no purpose). But `entry.HeapIdx` only IS a
            // heap address for ValueChange; the other kinds overload it:
            //   - AttrModify: HeapIdx indexes _attrTrailLog, NOT the heap.
            //     The drop rule must test the attribute variable's HOME —
            //     a young attvar is reclaimed on backtrack (its record
            //     restore is moot), but an OLD attvar's record restore is
            //     still required. Testing HeapIdx (a monotonic log counter)
            //     against parentHeapTop is meaningless and, once the log
            //     outgrows the heap top, wrongly drops live old-attvar
            //     entries — breaking the restore chain so the record ends
            //     up pointing at a reclaimed term (donald: fd(Dom, _)).
            //   - BigIntAlloc: HeapIdx is the bigint TABLE size before the
            //     allocation, also NOT a heap address. The big-integer table
            //     is a side table reclaimed only by these entries (nothing
            //     else trims it), so — like CatchFrame — the entry must always
            //     survive: a plain backtrack to an ancestor trims every bigint
            //     allocated since, and the cut must keep that behaviour intact
            //     (the committed bigints are dead once that ancestor backtrack
            //     reclaims the heap cells referencing them). Dropping it on the
            //     bogus `tableSize < parentHeapTop` test leaks table slots and
            //     can reuse an id under a surviving reference.
            //   - CatchFrame: control state, not a heap cell — always keep.
            bool survives = entry.Type switch
            {
                TrailType.CatchFrame => true,
                TrailType.BigIntAlloc => true,
                TrailType.RationalAlloc => true,   // side table, same contract as BigIntAlloc
                // External-state restore (b_setval): HeapIdx indexes the
                // external trail log, not the heap — a backtrack to an ancestor
                // above the cut must still restore the host-level value.
                TrailType.MutableSet => true,
                TrailType.AttrModify => _attrTrailLog[entry.HeapIdx].Home < effectiveFloor,
                _ => entry.HeapIdx < effectiveFloor,
            };
            if (survives)
            {
                entry.BindingTrailMarker = bindingWrite;
                _extraTrail[extraWrite++] = entry;
            }
            else if (_attrTable.Count > 0 && entry.Type == TrailType.ValueChange)
            {
                DropDeadAttrRecord(entry.HeapIdx);
            }
            else if (entry.Type == TrailType.AttrModify)
            {
                // The dropped entry was the ONLY reference into its side-log
                // record; without this the record is orphaned — positional
                // unwind truncation never reaches it in cut-only (CP-free)
                // runs, and the GC roots its Home/OldValue forever. A lazy
                // phrase_from_file retained its ENTIRE consumed input through
                // exactly these orphans (one per chunk, via the frozen tail's
                // old attribute). Dead records are skipped by mark/relocate
                // and physically reclaimed when an unwind truncates past them.
                if ((uint)entry.HeapIdx < (uint)_attrTrailLog.Count)
                    _attrTrailLog[entry.HeapIdx] = (int.MinValue, 0, 0);
            }
            extraRead++;
        }

        // Any binding entries left after the last extra entry.
        while (bindingRead < _bindingTrailTop)
        {
            int idx = _bindingTrail[bindingRead];
            if (idx < effectiveFloor)
                _bindingTrail[bindingWrite++] = idx;
            bindingRead++;
        }

        _bindingTrailTop = bindingWrite;
        _extraTrailTop = extraWrite;

        // catch frames captured snapshots of the trail
        // tops at push time. The compaction above just dropped some
        // entries from above each snapshot — those entries no longer
        // exist, so the snapshot's position is stale. Clip any
        // snapshot that points past the new top back down to it; on
        // throw, UnwindToCatchFrame's UnwindTrails(snap) won't ask
        // to roll back to a non-existent trail position.
        for (int i = 0; i < _catchFrames.Count; i++)
        {
            CatchFrame f = _catchFrames[i];
            bool changed = false;
            if (f.SnapBindingTrailTop > _bindingTrailTop)
            {
                f.SnapBindingTrailTop = _bindingTrailTop;
                changed = true;
            }
            if (f.SnapExtraTrailTop > _extraTrailTop)
            {
                f.SnapExtraTrailTop = _extraTrailTop;
                changed = true;
            }
            if (changed) _catchFrames[i] = f;
        }
    }

}
