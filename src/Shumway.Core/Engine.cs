using System.Numerics;

namespace Shumway.Core;

/// <summary>
/// The execution context for a single Prolog computation: heap, stack, registers,
/// trails, and the engine bookkeeping (E/B/P/CP/HB) defined by the WAM.
///
/// Engines are single-threaded internally (no locks on hot paths) and thread-agile
/// (no <c>[ThreadStatic]</c> state) — see ADR-001. Multiple engines coexist in a
/// process and share the global <see cref="AtomTable"/> and <see cref="FunctorTable"/>.
///
/// This file implements the storage substrate (ADR-001/004/005), <see cref="Deref"/>,
/// <see cref="Bind"/> with the HB check and young-to-old rule (ADR-004), and
/// <see cref="Unify"/> for atomic operands. Compound, list, PSTR, float, and the
/// auxiliary-table value tags throw <see cref="NotImplementedException"/> from
/// <see cref="Unify"/> for now; they land with the subsystems that produce them.
/// </summary>
public sealed partial class Engine
{
    private readonly EngineConfig _config;

    // ----- Heap -----
    private Cell[] _heap;
    private int _heapTop;
    private int _hb;

    // Monotonic count of cells reserved on the WAM heap over the engine's
    // lifetime. Backtracking rewinds _heapTop but never this counter — it
    // is a cumulative "cells ever allocated" tally, not the current high
    // watermark. Bumped once per allocation primitive (AllocateHeap /
    // AllocateHeapUnbound). Purpose: a *deterministic* benchmark metric
    // (independent of wall-clock noise) for changes that add or remove
    // heap allocations — e.g. the read-mode atomic-literal fast path in
    // UnifyHeapWithCell, whose whole effect is skipping one cell per
    // matched literal. Present in every build, so it cancels exactly when
    // comparing two builds. See the harness --alloc mode.
    private long _cellsAllocated;

    // ----- Stack (storage only in this phase; no frame operations yet) -----
    private Cell[] _stack;

    // ----- Registers -----
    private Cell[] _registers;

    // ----- Trails -----
    private int[] _bindingTrail;
    private int _bindingTrailTop;

    private ExtraTrailEntry[] _extraTrail;

    // ----- Per-engine auxiliary value tables (ADR-002) -----
    private readonly List<BigInteger> _bigIntTable = new();
    private readonly List<string> _stringTable = new();
    private readonly List<object?> _foreignTable = new();

    // ----- Attributed-variable storage (chunk 77, Phase 4) -----
    // Maps the heap home index of an attributed variable to its
    // attribute record — itself a map from a module's atom id to the
    // heap index of that module's attribute value. An ATTVAR cell's
    // payload is its home index (like a self-REF), which is also its
    // key here, so a bare ATTVAR cell is fully self-describing.
    // Backtracking reverts the ATTVAR cell to a plain REF (via the
    // ValueChange trail); the orphaned record is left in place and is
    // overwritten outright if the heap slot is later reused.
    private readonly Dictionary<int, Dictionary<int, int>> _attrTable = new();
    // Side log for AttrModify trail entries: each records (attvar home
    // index, module id, previous value heap index — or -1 when the
    // module was absent). ExtraTrailEntry.HeapIdx indexes into this list.
    private readonly List<(int Home, int Module, int OldValue)> _attrTrailLog = new();

    // ----- attributed-variable unify-hook wakeups (chunk 78, 79) -----
    // When an attributed variable is bound, one wakeup per attribute
    // module is queued here: (module atom id, heap index of that
    // module's attribute value, heap index of the term the variable
    // was bound to). The interpreter drains the queue at the next goal
    // boundary and runs verify_attributes/4 for each entry; a hook
    // failure fails the triggering unification. The queue is transient
    // — not trailed — because it is consumed before the next goal and
    // cleared outright on backtracking.
    private readonly List<(int Module, int AttrValueIdx, int OtherIdx)> _pendingWakeups = new();

    // catch/3 scopes, innermost last. Pushed by '$catch_begin', deactivated
    // by '$catch_end'; both operations are recorded on the extra trail
    // (TrailType.CatchFrame) so backtracking restores the stack. The throw
    // handler walks it from the top to find a matching catcher.
    private readonly List<CatchFrame> _catchFrames = new();

    private int _stackTop;
    private int _extraTrailTop;

    /// <summary>Output sink the I/O builtins (<c>write/1</c>, <c>nl/0</c>,
    /// <c>writeln/1</c>) write into. Defaults to <see cref="Console.Out"/>;
    /// embedding callers can swap in a <see cref="System.IO.StringWriter"/>
    /// or another sink for testing or for capturing program output.</summary>
    public System.IO.TextWriter Out { get; set; } = Console.Out;

    /// <summary>Opaque back-reference to the embedding-layer object that owns
    /// this engine (typically a <c>PrologEngine</c>). Engine itself doesn't
    /// touch the value — it's read by meta-builtins like <c>findall/3</c>
    /// that need to spawn a peer engine to run a sub-query. The Core layer
    /// stays free of any embedding-layer types by keeping this typed as
    /// <see cref="object"/>; callers downcast at the use site.</summary>
    public object? Host { get; set; }

    /// <summary>Operator-lookup view used by the renderer to decide whether
    /// a compound should print in operator form (<c>a + b</c>) or
    /// canonical form (<c>+(a, b)</c>). Set by the embedding layer; left
    /// <c>null</c> means "no operator-form rendering, always canonical".</summary>
    public IOperatorLookup? Operators { get; set; }

    // ----- Engine registers (per ADR-005) -----
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

    public Engine() : this(new EngineConfig()) { }

    public Engine(EngineConfig config)
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
        // Chunk 414 — the GC fuzz/bisect env overrides (SHUMWAY_GC_THRESHOLD /
        // GC_STRESS / GC_AT / GC_UPTO) only exist in -p:ShumwayDiag=true
        // builds; a normal build never reads the environment here. The fuzz
        // FIELDS and their (branch-predicted) checks in MaybeCollectHeap stay,
        // because the test suite also drives them programmatically
        // (GcStressMode / EngineConfig).
        DiagReadGcOverrides();
    }

    /// <summary>Chunk 414 — diag-build-only env overrides for the ADR-016 GC:
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
    }

    private static void Validate(EngineConfig c)
    {
        if (c.InitialHeapSize <= 0) throw new ArgumentException("InitialHeapSize must be > 0", nameof(c));
        if (c.InitialStackSize <= 0) throw new ArgumentException("InitialStackSize must be > 0", nameof(c));
        if (c.InitialRegisterCount <= 0) throw new ArgumentException("InitialRegisterCount must be > 0", nameof(c));
        if (c.InitialBindingTrailSize <= 0) throw new ArgumentException("InitialBindingTrailSize must be > 0", nameof(c));
        if (c.InitialExtraTrailSize <= 0) throw new ArgumentException("InitialExtraTrailSize must be > 0", nameof(c));
    }

    public EngineConfig Config => _config;

    // ----- Heap accessors -----

    public int HeapTop => _heapTop;
    public int HeapCapacity => _heap.Length;
    public int Hb => _hb;

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

    /// <summary>Env trimming (chunks 57 / 61 / 64). Shrinks the
    /// current environment frame to keep only
    /// <paramref name="numLivePerms"/> Y slots, reclaiming the stack
    /// space the dead slots occupied so subsequent pushes pack
    /// tighter. The compile-time analysis side
    /// (<c>ClauseCompiler.ComputeLivePermsAfterEachGoal</c>) emits
    /// the right <c>num_live_perms</c> operand on every Call /
    /// CallBuiltin; the interpreter's handlers call here to apply it.
    ///
    /// <para><b>CP-frame protection (chunk 64):</b> the trim must
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
            RecoveryE = (int)_stack[_e + EnvCeOffset].Data,
            RecoveryCp = (int)_stack[_e + EnvCpOffset].Data,
        });
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
        _hb = _heapTop;
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
    public void UnwindToCatchFrame(int index)
    {
        CatchFrame f = _catchFrames[index];
        UnwindTrails(f.SnapBindingTrailTop, f.SnapExtraTrailTop);
        _heapTop = f.SnapHeapTop;
        _hb = f.SnapHb;
        _b = f.SnapB;
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
        if (_b < oldE && _stackTop > oldE)
            _stackTop = oldE;
    }

    /// <summary>Reads the <c>Y(k+1)</c> slot of the current environment frame.</summary>
    public Cell GetY(int slot)
    {
        if (_e < 0)
            throw new InvalidOperationException("No environment frame is active.");
        return _stack[_e + EnvY1Offset + slot];
    }

    /// <summary>Writes the <c>Y(k+1)</c> slot of the current environment frame.</summary>
    public void SetY(int slot, Cell value)
    {
        if (_e < 0)
            throw new InvalidOperationException("No environment frame is active.");
        _stack[_e + EnvY1Offset + slot] = value;
    }

    // ----- Choice-point frame layout (ADR-005) -----
    //
    // Layout: [arity | A1 .. Aarity | CE | CP | B | BP | BindingTrailTop |
    //          ExtraTrailTop | HeapTop | Hb | ViewGen]
    // Total size = 10 + arity cells.
    //
    // ViewGen (ADR-015 chunk C, bytecode-level dispatch): the
    // dynamic-database generation the calling query had captured when it
    // entered this predicate — its "logical update view" timestamp. Pushed
    // by try_me_else along with the rest of the engine state; restored on
    // retry_me_else so a CheckVisible instruction in any of the
    // backtrackable clauses reads the same view-gen the call started with.
    // The field is uniform across CPs (zero for static predicates); the
    // tiny per-CP cost buys a single uniform save/restore path.

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
    public void PushChoicePoint(int arity, int nextClauseAddr)
    {
        if (arity < 0)
            throw new ArgumentOutOfRangeException(nameof(arity));
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

        // Chunk 234 — hoist a Span<Cell> over the contiguous control
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
        _hb = _heapTop;
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
        _hb = _heapTop;
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
        _hb = (int)_stack[_b + CpHbOffset(arity)].Data;
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

        // Chunk 234 — single Span over the contiguous control-word
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
        // Chunk 146: a stale barrier (above current B) means the
        // choice point the cut wanted to commit to has already been
        // popped — typically by a surrounding catch/3 unwinding past
        // the clause-entry snapshot. ISO semantics: cut commits to
        // the most recent *active* CP at clause entry; if that CP is
        // gone, the cut is a no-op. Don't throw.
        if (barrier > _b)
            return;
        if (_b == barrier)
            return;

        // Chunk 164 / chunk 231: drop IL CP stack entries above the
        // barrier BEFORE _b moves. Each entry's Key is its frame's
        // stack-B position; the entries are pushed in monotonic _b
        // order so any stale ones sit at the top of _ilCpStack —
        // pop them down to the first <= barrier. Replaces the
        // chunk-164 foreach-over-dict-Keys loop (which was 5.31%
        // self-time on Engine.Cut in Blint with user IL active,
        // plus a List<int> allocation per cut that hit any IL CP).
        while (_ilCpTop > 0 && _ilCpStack[_ilCpTop - 1].Key > barrier)
        {
            // Chunk 245 — fire the optional cleanup hook before
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
    /// call (chunk 215).</summary>
    public void CutToLevel(int slot) => Cut((int)GetY(slot).Data);

    /// <summary>
    /// Single-pass interleaved trail compaction (Warren's algorithm extended to the extra
    /// trail). Both trails are walked in temporal order: for each surviving extra entry,
    /// its <see cref="ExtraTrailEntry.BindingTrailMarker"/> is rewritten to the index it
    /// would occupy in the compacted binding trail, preserving the relative ordering that
    /// <see cref="UnwindTrails"/> relies on.
    /// </summary>
    private void CompactTrails(int parentBindingTop, int parentExtraTop, int parentHeapTop)
    {
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
                if (idx < parentHeapTop)
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
                TrailType.AttrModify => _attrTrailLog[entry.HeapIdx].Home < parentHeapTop,
                _ => entry.HeapIdx < parentHeapTop,
            };
            if (survives)
            {
                entry.BindingTrailMarker = bindingWrite;
                _extraTrail[extraWrite++] = entry;
            }
            extraRead++;
        }

        // Any binding entries left after the last extra entry.
        while (bindingRead < _bindingTrailTop)
        {
            int idx = _bindingTrail[bindingRead];
            if (idx < parentHeapTop)
                _bindingTrail[bindingWrite++] = idx;
            bindingRead++;
        }

        _bindingTrailTop = bindingWrite;
        _extraTrailTop = extraWrite;

        // Chunk 147: catch frames captured snapshots of the trail
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

    // ----- Registers -----

    public Cell GetRegister(int idx) => _registers[idx];
    public void SetRegister(int idx, Cell value)
    {
        if (idx >= _registers.Length) EnsureRegisterCapacity(idx + 1);
        _registers[idx] = value;
    }

    /// <summary>Ensures the X-register bank can hold at least
    /// <paramref name="required"/> registers, doubling the backing
    /// array if needed. The initial register count
    /// (<see cref="EngineConfig.InitialRegisterCount"/>) covers the
    /// vast majority of predicates; this growth path catches the tail
    /// where a single complex clause has more live temporaries than
    /// the initial bank holds. Existing register values are preserved.</summary>
    private void EnsureRegisterCapacity(int required)
    {
        int newSize = _registers.Length;
        while (newSize < required) newSize *= 2;
        var grown = new Cell[newSize];
        Array.Copy(_registers, grown, _registers.Length);
        _registers = grown;
    }

    // ----- Unify against registers / permanents -----
    //
    // The base Unify(int, int) operates on heap addresses. Register and permanent
    // (Y-slot) cells live outside the heap, so the helpers below "materialise" them:
    // if the cell already holds a REF we reuse its heap target directly; otherwise we
    // copy the atomic value into a freshly-allocated heap cell. This costs at most one
    // extra heap cell per atomic operand and keeps the unify implementation single-form.

    /// <summary>Unifies the cells held in <c>X[<paramref name="aRegIdx"/>]</c> and
    /// <c>X[<paramref name="bRegIdx"/>]</c>.</summary>
    public bool UnifyRegisters(int aRegIdx, int bRegIdx)
        => UnifyCells(_registers[aRegIdx], _registers[bRegIdx]);

    /// <summary>Occurs-check variant of <see cref="UnifyRegisters"/> —
    /// drives ISO <c>unify_with_occurs_check/2</c>.</summary>
    public bool UnifyRegistersWithOccursCheck(int aRegIdx, int bRegIdx)
        => UnifyCellsWithOccursCheck(_registers[aRegIdx], _registers[bRegIdx]);

    /// <summary>Unifies the cell held in <c>X[<paramref name="regIdx"/>]</c> with an
    /// immediate <paramref name="value"/> (typically a bytecode-literal cell such as
    /// <see cref="Cell.Atom"/> or <see cref="Cell.Int"/>).</summary>
    public bool UnifyRegisterWithCell(int regIdx, Cell value)
    {
        // Chunk 170: fast paths for the two common shapes — the
        // register is unbound, or the register already holds the
        // exact same value. Both occur on every char_code /
        // peek_char dispatch (the output register is fresh; the
        // input register is bound and we compare). Either path
        // avoids the heap slot allocation + general Unify call.
        Cell rc = _registers[regIdx];
        if (rc.Tag == Tag.Ref)
        {
            int home = rc.AsHeapIndex;
            int deref = Deref(home);
            Cell d = _heap[deref];
            if (d.Tag == Tag.Ref && d.AsHeapIndex == deref)
            {
                // Truly unbound — bind it to the immediate value,
                // trailing the binding if it sits below HB. Same
                // young-to-old discipline the full Unify path uses.
                _heap[deref] = value;
                if (deref < _hb) TrailBinding(deref);
                return true;
            }
            // Bound register: same-value early-out. Saves the alloc
            // for the (very common) recheck pattern in char_code /
            // tokenizer guards.
            if (d.Tag == value.Tag && d.Data == value.Data) return true;
        }
        else if (rc.Tag == value.Tag && rc.Data == value.Data)
        {
            return true;
        }
        // General case: cell-based, no materialise (ADR-017). value is a
        // bytecode literal, so it has no heap home of its own.
        return UnifyCells(rc, value);
    }

    /// <summary>Unifies the cell held in <c>Y[<paramref name="permSlot"/>]</c> of the
    /// current environment frame with <c>X[<paramref name="regIdx"/>]</c>.</summary>
    public bool UnifyPermanentWithRegister(int permSlot, int regIdx)
    {
        if (_e < 0)
            throw new InvalidOperationException("No environment frame is active.");
        return UnifyCells(_stack[_e + EnvY1Offset + permSlot], _registers[regIdx]);
    }

    /// <summary>Unifies <c>X[<paramref name="regIdx"/>]</c> with the heap cell at
    /// <paramref name="heapIdx"/>. Used by <c>unify_value_x</c> in read mode.</summary>
    public bool UnifyRegisterWithHeapAt(int regIdx, int heapIdx)
        => UnifyCells(_registers[regIdx], Cell.Ref(heapIdx));

    /// <summary>Unifies <c>Y[<paramref name="permSlot"/>]</c> with an immediate
    /// <paramref name="value"/> cell. Used by the ADR-018 <c>a_eval_is</c> opcode
    /// when the <c>is/2</c> target is a permanent variable.</summary>
    public bool UnifyPermanentWithCell(int permSlot, Cell value)
    {
        if (_e < 0)
            throw new InvalidOperationException("No environment frame is active.");
        return UnifyCells(_stack[_e + EnvY1Offset + permSlot], value);
    }

    /// <summary>Unifies <c>Y[<paramref name="permSlot"/>]</c> with the heap cell at
    /// <paramref name="heapIdx"/>. Used by <c>unify_value_y</c> in read mode.</summary>
    public bool UnifyPermanentWithHeapAt(int permSlot, int heapIdx)
    {
        if (_e < 0)
            throw new InvalidOperationException("No environment frame is active.");
        return UnifyCells(_stack[_e + EnvY1Offset + permSlot], Cell.Ref(heapIdx));
    }

    /// <summary>Unifies the heap cell at <paramref name="heapIdx"/> with the immediate
    /// <paramref name="value"/>. Used by <c>unify_constant/atom/integer/nil</c> in read
    /// mode (the value is a literal from the bytecode).</summary>
    public bool UnifyHeapWithCell(int heapIdx, Cell value)
    {
        // Fast path for read-mode unification of a compound argument against a
        // ground atomic literal (atom / inline int / nil) from a bytecode
        // constant — the unify_atom / unify_integer / unify_constant opcodes.
        // Deref the target once and:
        //   - unbound plain var  → bind directly to the literal (skips the
        //     throwaway heap slot the general path would allocate and bind);
        //   - bound atom or int  → Data equality is exact semantic equality
        //     (atoms compare by id, ints are inline, the tag bits are part of
        //     Data so a type mismatch also compares unequal).
        // Everything else — attributed vars (the attr_unify_hook must fire),
        // BigInt / String / Pstr (table-id ≠ value), Float (two cells), or a
        // compound — takes the general alloc-then-Unify path unchanged.
        //
        // NOTE (Phase 25 --alloc finding): on the Van Roy suite this saves
        // zero allocations — head-level literal args go through the already-
        // optimised UnifyRegisterWithCell (chunk 170, get_atom/get_nil/
        // get_integer), and these benchmarks have no literals nested inside
        // compound head args (the only shape that reaches here). Kept because
        // it is correct, harmless, and a real win for programs that DO match
        // nested literals (e.g. DCG / parser heads like foo([a|T], ...)).
        //
        // The fast path applies ONLY when `value` is a genuine atomic literal
        // (Atom / inline Int). It must NOT trigger for a `value` that is a
        // REF — unify_float passes Cell.Ref(pairIdx) here (chunk-287 bug):
        // a Ref value against an unbound target needs Unify's young-to-old
        // BindVarToVar discipline, and against a bound value needs the full
        // recursive unify; binding the target straight to the Ref breaks
        // backtracking and float matching. Guard on the value's tag.
        if (value.Tag is Tag.Atom or Tag.Int)
        {
            int addr = Deref(heapIdx);
            Cell c = _heap[addr];
            Tag t = c.Tag;
            if (t == Tag.Ref)
            {
                Bind(addr, value);
                return true;
            }
            if (t == Tag.Atom || t == Tag.Int)
                return c.Data == value.Data;
        }

        int valueSlot = AllocateHeap(1);
        _heap[valueSlot] = value;
        return Unify(heapIdx, valueSlot);
    }

    // ----- Compound / list construction (write-mode entry points) -----

    /// <summary>
    /// Implements <c>put_structure</c>: allocates a FUNCTOR cell on the heap and stores an
    /// inline STR cell pointing at it in <c>X[<paramref name="regIdx"/>]</c> (ADR-017: no
    /// separate on-heap STR header), then enters write mode with <see cref="UnifyPointer"/>
    /// at the position where the first argument will be written.
    /// </summary>
    public void PutStructure(int functorId, int regIdx)
    {
        if (regIdx >= _registers.Length) EnsureRegisterCapacity(regIdx + 1);
        // ADR-017 phase 2: the STR tag rides inline in the register, pointing
        // straight at the FUNCTOR cell; the args follow. A structure is
        // functor + n args, not STR-header + functor + n args. Whole-structure
        // unification no longer pays a materialise copy thanks to the
        // cell-based UnifyCells path.
        int f = AllocateHeap(1);
        _heap[f] = Cell.Functor(functorId);
        _registers[regIdx] = Cell.Str(f);
        _writeMode = true;
        _reservedWrite = false;
        _unifyPointer = f + 1;
    }

    /// <summary>ADR-020 <c>put_structure_r</c>: reserve <paramref name="argCount"/>
    /// + 1 contiguous cells (functor + args) upfront and enter reserved write
    /// mode with a fresh write-pointer stack, so a non-last nested compound arg
    /// can write its ref into a pre-reserved slot and resume the parent. The
    /// reserve size is baked by the compiler — no functor-table lookup.</summary>
    public void PutStructureReserved(int functorId, int regIdx, int argCount)
    {
        if (regIdx >= _registers.Length) EnsureRegisterCapacity(regIdx + 1);
        int f = AllocateHeap(argCount + 1);
        _heap[f] = Cell.Functor(functorId);
        _registers[regIdx] = Cell.Str(f);
        _writeMode = true;
        _reservedWrite = true;
        _unifyPointer = f + 1;
        _writeSp = 0;
        PushWriteFrame(0, argCount);
    }

    /// <summary>ADR-020 <c>put_list_r</c>: reserve the 2-cell cons upfront and
    /// enter reserved write mode (the cons head may be a non-last nested
    /// compound).</summary>
    public void PutListReserved(int regIdx)
    {
        if (regIdx >= _registers.Length) EnsureRegisterCapacity(regIdx + 1);
        int pair = AllocateHeap(2);
        _registers[regIdx] = Cell.Lis(pair);
        _writeMode = true;
        _reservedWrite = true;
        _unifyPointer = pair;
        _writeSp = 0;
        PushWriteFrame(0, 2);
    }

    private void PushWriteFrame(int resume, int remaining)
    {
        if (_writeSp >= _writeResume.Length)
        {
            System.Array.Resize(ref _writeResume, _writeResume.Length * 2);
            System.Array.Resize(ref _writeRemaining, _writeRemaining.Length * 2);
        }
        _writeResume[_writeSp] = resume;
        _writeRemaining[_writeSp] = remaining;
        _writeSp++;
    }

    /// <summary>ADR-020: one arg of the current (top) reserved frame was just
    /// written and <see cref="_unifyPointer"/> already advanced. Decrement the
    /// top frame and cascade-pop every frame that reaches zero, restoring
    /// <see cref="_unifyPointer"/> to the popped frame's parent-resume slot.
    /// When the base frame pops the build is complete and reserved mode ends.</summary>
    private void OnReservedArgWritten()
    {
        _writeRemaining[_writeSp - 1]--;
        while (_writeSp > 0 && _writeRemaining[_writeSp - 1] == 0)
        {
            int resume = _writeResume[_writeSp - 1];
            _writeSp--;
            if (_writeSp > 0) _unifyPointer = resume;
            else _reservedWrite = false;
        }
    }

    /// <summary>
    /// Implements <c>get_structure</c>: derefs <c>X[<paramref name="regIdx"/>]</c> and
    /// either enters write mode (allocating a fresh compound and binding the unbound
    /// variable to it) or read mode (when the dereferenced cell is a matching STR) or
    /// fails. The <see cref="UnifyPointer"/> is positioned at the first argument cell.
    /// </summary>
    public bool GetStructure(int functorId, int regIdx)
    {
        _reservedWrite = false;   // ADR-020: head matching is never reserved
        Cell regCell = _registers[regIdx];
        int finalAddr = -1;
        Cell finalCell = regCell;
        // A register may hold a REF or — once chunk 77's attvars exist —
        // a bare ATTVAR cell (its payload is its own home index, so
        // Deref of it is the identity). Both name a heap home.
        if (regCell.Tag is Tag.Ref or Tag.AttVar)
        {
            finalAddr = Deref(regCell.AsHeapIndex);
            finalCell = _heap[finalAddr];
        }

        if (finalCell.Tag == Tag.AttVar)
        {
            // Attributed var — write mode. Keep the on-heap STR header:
            // BindAttVarToValue stores a Ref to a heap home for compound
            // values, and the attr machinery expects it (the heap GC bails
            // while any attvar is live, so the extra cell is immaterial).
            // chunk 78 fires its unify hook from the queued wakeup.
            int h = AllocateHeap(2);
            _heap[h] = Cell.Str(h + 1);
            _heap[h + 1] = Cell.Functor(functorId);
            BindAttVarToValue(finalAddr, h, _heap[h]);
            _writeMode = true;
            _unifyPointer = h + 2;
            return true;
        }
        if (finalCell.Tag == Tag.Ref)
        {
            // ADR-017 phase 2: bind the plain var directly to an inline STR
            // cell pointing at the FUNCTOR cell; the args follow. No separate
            // on-heap STR header (functor + n args, not STR + functor + n).
            int f = AllocateHeap(1);
            _heap[f] = Cell.Functor(functorId);
            Bind(finalAddr, Cell.Str(f));
            _writeMode = true;
            _unifyPointer = f + 1;
            return true;
        }
        if (finalCell.Tag == Tag.Str)
        {
            int functorIdx = finalCell.AsHeapIndex;
            if (_heap[functorIdx].AsFunctorId != functorId)
                return false;
            _writeMode = false;
            _unifyPointer = functorIdx + 1;
            return true;
        }
        return false;
    }

    // ----- Unify-mode-aware dispatch helpers (chunk 48) -----
    //
    // Each of these matches the bytecode interpreter's <c>unify_*</c>
    // opcode behaviour: in write mode allocate one heap slot and write
    // a cell; in read mode unify the cell at <c>UnifyPointer</c> against
    // the supplied value. The unify pointer advances by one in either
    // case. The Tier-1 IL compiler emits a call into the matching
    // helper instead of inlining the dispatch — saves IL volume at the
    // cost of one extra method call per unify operand.

    /// <summary>Mode-aware <c>unify_*</c> for ground value cells
    /// (atom / int / nil / float-via-ref / etc.).</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool UnifyArgCell(Cell value)
    {
        int ptr = _unifyPointer;
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                // ADR-020: the cell is pre-reserved — write in place, then
                // decrement / cascade-pop the write-pointer frame stack.
                _heap[ptr] = value;
                _unifyPointer = ptr + 1;
                OnReservedArgWritten();
                return true;
            }
            int idx = AllocateHeap(1);
            _heap[idx] = value;
        }
        else
        {
            if (!UnifyHeapWithCell(ptr, value)) return false;
        }
        _unifyPointer = ptr + 1;
        return true;
    }

    /// <summary><c>unify_variable_x</c>: first occurrence of a temp
    /// variable inside a compound. In write mode allocate an unbound
    /// var on the heap and stash a REF to it in X[slot]; in read mode
    /// copy the cell at <c>UnifyPointer</c> into X[slot].</summary>
    // Chunk 353: split into a small read-mode fast path (AggressiveInlining, so
    // the JIT inlines it into the Tier-1 IL delegate and the interpreter — the
    // common head-matching case: capture the cell at the unify pointer into a
    // temp register, no heap allocation, no register grow) and a cold slow path
    // (write mode, or a slot beyond the register bank). Surfaced as ~13% of a
    // list-processing tight loop in a dotnet-trace profile.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void UnifyVariableX(int slot)
    {
        if (!_writeMode && slot < _registers.Length)
        {
            int ptr = _unifyPointer;
            // A bare ATTVAR at the unify pointer is a variable at its home;
            // capture it as a REF to that home (a copied ATTVAR's payload would
            // no longer name its own slot). A plain REF / value copies fine.
            Cell src = _heap[ptr];
            _registers[slot] = src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src;
            _unifyPointer = ptr + 1;
            return;
        }
        UnifyVariableXSlow(slot);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void UnifyVariableXSlow(int slot)
    {
        // The IL emit calls UnifyVariableX directly; unlike the bytecode
        // interpreter's opcode handler (which routes through SetRegister) we
        // don't get the auto-grow for free, so grow the bank when a
        // structure-unification slot exceeds it (Blint had unify_variable_x
        // slot=256 with a 256-register default bank).
        if (slot >= _registers.Length) EnsureRegisterCapacity(slot + 1);
        int ptr = _unifyPointer;
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                // ADR-020: fresh unbound var in the pre-reserved cell at ptr.
                _heap[ptr] = Cell.UnboundVar(ptr);
                _registers[slot] = Cell.Ref(ptr);
                _unifyPointer = ptr + 1;
                OnReservedArgWritten();
                return;
            }
            int idx = AllocateHeap(1);
            _heap[idx] = Cell.UnboundVar(idx);
            _registers[slot] = Cell.Ref(idx);
        }
        else
        {
            Cell src = _heap[ptr];
            _registers[slot] = src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src;
        }
        _unifyPointer = ptr + 1;
    }

    /// <summary><c>unify_value_x</c>: subsequent occurrence of a temp
    /// variable inside a compound. Same as <see cref="UnifyArgCell"/>
    /// with the cell drawn from <c>X[slot]</c>.</summary>
    public bool UnifyValueX(int slot)
    {
        Cell regVal = _registers[slot];
        return UnifyArgCell(regVal);
    }

    /// <summary><c>unify_variable_y</c>: first occurrence of a
    /// permanent variable inside a compound.</summary>
    public void UnifyVariableY(int slot)
    {
        int ptr = _unifyPointer;
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                _heap[ptr] = Cell.UnboundVar(ptr);
                SetY(slot, Cell.Ref(ptr));
                _unifyPointer = ptr + 1;
                OnReservedArgWritten();
                return;
            }
            int idx = AllocateHeap(1);
            _heap[idx] = Cell.UnboundVar(idx);
            SetY(slot, Cell.Ref(idx));
        }
        else
        {
            // See UnifyVariableX: a bare ATTVAR is captured as a REF to
            // its home so its identity survives the copy. (chunk 77)
            Cell src = _heap[ptr];
            SetY(slot, src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src);
        }
        _unifyPointer = ptr + 1;
    }

    /// <summary><c>unify_value_y</c>: subsequent occurrence of a
    /// permanent variable.</summary>
    public bool UnifyValueY(int slot)
    {
        Cell yVal = GetY(slot);
        return UnifyArgCell(yVal);
    }

    /// <summary><c>unify_void</c>: skips <paramref name="count"/>
    /// anonymous variable slots. In write mode allocates fresh unbound
    /// cells; in read mode just advances the pointer.</summary>
    public void UnifyVoid(int count)
    {
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                // ADR-020: the cells are pre-reserved — initialise each as a
                // fresh unbound var in place and decrement / cascade-pop per arg.
                for (int i = 0; i < count; i++)
                {
                    int p = _unifyPointer;
                    _heap[p] = Cell.UnboundVar(p);
                    _unifyPointer = p + 1;
                    OnReservedArgWritten();
                }
                return;
            }
            for (int i = 0; i < count; i++)
            {
                int idx = AllocateHeap(1);
                _heap[idx] = Cell.UnboundVar(idx);
            }
        }
        _unifyPointer += count;
    }

    /// <summary>
    /// Implements <c>put_list</c>: allocates a LIS cell pointing to the head position,
    /// stores a REF in <c>X[<paramref name="regIdx"/>]</c>, and enters write mode with
    /// <see cref="UnifyPointer"/> at the head position.
    /// </summary>
    public void PutList(int regIdx)
    {
        if (regIdx >= _registers.Length) EnsureRegisterCapacity(regIdx + 1);
        // ADR-017: store the LIS tag inline in the register, pointing
        // directly at the 2-cell [head, tail] pair the following two
        // unify_* opcodes will write (in write mode they AllocateHeap
        // sequentially starting at the current top). No separate on-heap
        // LIS header cell — a cons is 2 cells, not 3.
        int pair = _heapTop;
        _registers[regIdx] = Cell.Lis(pair);
        _writeMode = true;
        _reservedWrite = false;
        _unifyPointer = pair;
    }

    /// <summary>
    /// Implements <c>get_list</c>: enters write mode against an unbound argument, read
    /// mode against a LIS, or fails. The <see cref="UnifyPointer"/> is positioned at
    /// the head cell.
    /// </summary>
    // Chunk 353: split into a small read-mode fast path (AggressiveInlining: the
    // register directly holds an inline LIS cell — ADR-017 — so no deref, no
    // allocation; the common case when consuming an existing list) and a cold
    // slow path (deref, var-binding write mode, attvar, fail). ~10% of a
    // list-processing tight loop in a dotnet-trace profile.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool GetList(int regIdx)
    {
        _reservedWrite = false;   // ADR-020: head matching is never reserved
        Cell regCell = _registers[regIdx];
        if (regCell.Tag == Tag.Lis)
        {
            _writeMode = false;
            _unifyPointer = regCell.AsHeapIndex;
            return true;
        }
        return GetListSlow(regCell);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool GetListSlow(Cell regCell)
    {
        int finalAddr = -1;
        Cell finalCell = regCell;
        // REF or a bare ATTVAR cell (chunk 77) — both name a heap home;
        // Deref of an ATTVAR is the identity since it isn't a REF.
        if (regCell.Tag is Tag.Ref or Tag.AttVar)
        {
            finalAddr = Deref(regCell.AsHeapIndex);
            finalCell = _heap[finalAddr];
        }

        if (finalCell.Tag == Tag.AttVar)
        {
            // Attributed var: keep the on-heap LIS header. BindAttVarToValue
            // stores a Ref to a heap home for compound values, and the attr
            // machinery expects that home. Rare path (and the heap GC bails
            // while any attvar is live), so the extra cell is immaterial.
            int h = AllocateHeap(1);
            _heap[h] = Cell.Lis(h + 1);
            BindAttVarToValue(finalAddr, h, _heap[h]);
            _writeMode = true;
            _unifyPointer = h + 1;
            return true;
        }
        if (finalCell.Tag == Tag.Ref)
        {
            // ADR-017: bind the plain var directly to an inline LIS cell
            // pointing at the [head, tail] pair the following unify_* will
            // write — no separate header cell (2-cell cons).
            int pair = _heapTop;
            Bind(finalAddr, Cell.Lis(pair));
            _writeMode = true;
            _unifyPointer = pair;
            return true;
        }
        if (finalCell.Tag == Tag.Lis)
        {
            _writeMode = false;
            _unifyPointer = finalCell.AsHeapIndex;
            return true;
        }
        return false;
    }

    /// <summary>ADR-019 <c>unify_structure</c>: build (write) or match (read) a
    /// nested compound at the parent's current argument and descend into its
    /// args. Write mode allocates the nested FUNCTOR cell, writes an inline STR
    /// ref to it into the parent's current arg slot, and moves
    /// <see cref="UnifyPointer"/> to the nested's first arg. Read mode derefs the
    /// parent's current arg and binds it (var → fresh structure, switch to write)
    /// or matches it (STR with the same functor, stay read), failing otherwise.
    /// Emitted only for a nested compound in the LAST argument position, so the
    /// parent is never resumed — the build stays linear, no write-pointer stack.</summary>
    public bool UnifyStructure(int functorId)
    {
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                // ADR-020: nested compound inside a reserved build. The parent's
                // slot at _unifyPointer is pre-reserved; write the STR ref there,
                // reserve the nested (functor + args) at the heap top, push a
                // frame so the cascade resumes the parent when the nested fills.
                // arity is looked up here (the minority reserved path), not in the
                // hot on-demand path below.
                var (_, arity) = FunctorTable.Lookup(functorId);
                int nested = AllocateHeap(arity + 1);
                _heap[nested] = Cell.Functor(functorId);
                _heap[_unifyPointer] = Cell.Str(nested);
                int parentResume = _unifyPointer + 1;
                _writeRemaining[_writeSp - 1]--;   // parent slot filled (no cascade yet)
                PushWriteFrame(parentResume, arity);
                _unifyPointer = nested + 1;
                return true;
            }
            int slot = AllocateHeap(1);          // parent's current arg slot
            int f = AllocateHeap(1);             // nested FUNCTOR cell (contiguous)
            _heap[f] = Cell.Functor(functorId);
            _heap[slot] = Cell.Str(f);
            _unifyPointer = f + 1;
            return true;
        }
        int addr = Deref(_unifyPointer);
        Cell cell = _heap[addr];
        if (cell.Tag == Tag.AttVar)
        {
            int h = AllocateHeap(2);
            _heap[h] = Cell.Str(h + 1);
            _heap[h + 1] = Cell.Functor(functorId);
            BindAttVarToValue(addr, h, _heap[h]);
            _writeMode = true;
            _unifyPointer = h + 2;
            return true;
        }
        if (cell.Tag == Tag.Ref)
        {
            int f = AllocateHeap(1);
            _heap[f] = Cell.Functor(functorId);
            Bind(addr, Cell.Str(f));
            _writeMode = true;
            _unifyPointer = f + 1;
            return true;
        }
        if (cell.Tag == Tag.Str)
        {
            int functorIdx = cell.AsHeapIndex;
            if (_heap[functorIdx].AsFunctorId != functorId) return false;
            _unifyPointer = functorIdx + 1;      // stays read mode
            return true;
        }
        return false;
    }

    /// <summary>ADR-019 <c>unify_list</c>: build (write) or match (read) a nested
    /// list cell at the parent's current argument, descending to its head. The
    /// list-tail counterpart of <see cref="UnifyStructure"/>; uses the ADR-017
    /// inline 2-cell cons (no header). Last-argument position only.</summary>
    public bool UnifyList()
    {
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                // ADR-020: nested cons inside a reserved build. Write the LIS ref
                // into the pre-reserved parent slot, reserve the 2-cell cons at
                // the heap top, descend to its head.
                int pair = AllocateHeap(2);
                _heap[_unifyPointer] = Cell.Lis(pair);
                int parentResume = _unifyPointer + 1;
                _writeRemaining[_writeSp - 1]--;
                PushWriteFrame(parentResume, 2);
                _unifyPointer = pair;
                return true;
            }
            int slot = AllocateHeap(1);          // parent's current arg slot
            _heap[slot] = Cell.Lis(_heapTop);    // points at the cons (next 2 cells)
            _unifyPointer = _heapTop;
            return true;
        }
        int addr = Deref(_unifyPointer);
        Cell cell = _heap[addr];
        if (cell.Tag == Tag.AttVar)
        {
            int h = AllocateHeap(1);
            _heap[h] = Cell.Lis(h + 1);
            BindAttVarToValue(addr, h, _heap[h]);
            _writeMode = true;
            _unifyPointer = h + 1;
            return true;
        }
        if (cell.Tag == Tag.Ref)
        {
            int pair = _heapTop;
            Bind(addr, Cell.Lis(pair));
            _writeMode = true;
            _unifyPointer = pair;
            return true;
        }
        if (cell.Tag == Tag.Lis)
        {
            _unifyPointer = cell.AsHeapIndex;    // stays read mode
            return true;
        }
        return false;
    }

    private int MaterializeRegister(int regIdx)
    {
        Cell c = _registers[regIdx];
        // REF and ATTVAR (chunk 77) both carry a heap home index in
        // their payload, so either already names a heap cell.
        if (c.Tag is Tag.Ref or Tag.AttVar) return c.AsHeapIndex;
        int slot = AllocateHeap(1);
        _heap[slot] = c;
        return slot;
    }

    /// <summary>Public wrapper around <see cref="MaterializeRegister"/>
    /// for diagnostic instrumentation (e.g. <c>RetractTrace</c>) that
    /// needs to inspect the same heap address the unify path would
    /// produce. Allocates a fresh heap cell for non-REF/AttVar
    /// register values exactly like the private overload —
    /// idempotent if called twice in a row only when the register
    /// already names a heap home.</summary>
    public int MaterializeRegisterForTrace(int regIdx)
        => MaterializeRegister(regIdx);

    // ----- Current-query functor address map (Tier-1, chunk 47) -----
    //
    // Set by the embedding-layer query setup once per query, this map
    // gives the bytecode address of every functor in the linked program.
    // IL-emitted Execute opcodes resolve their tail-call target by
    // looking up the *functor id* (stable across queries) here, instead
    // of embedding the address as a constant (which would only be valid
    // for one query's linked layout).
    public IReadOnlyDictionary<int, int>? CurrentFunctorAddresses { get; set; }

    /// <summary>Functor id of the user hook <c>verify_attributes/4</c>.
    /// Interned once; used by <see cref="MergeAttributes"/> to detect
    /// whether the program defines an attribute-unification hook (its
    /// presence in <see cref="CurrentFunctorAddresses"/> means it does).</summary>
    private static readonly int VerifyAttributesFunctorId =
        FunctorTable.Intern(AtomTable.Intern("verify_attributes", permanent: true).Id, 4);

    /// <summary>True when the linked program defines a
    /// <c>verify_attributes/4</c> hook. When set, that hook owns the
    /// merge of a shared module's attribute values on an attvar+attvar
    /// unification — the engine no longer applies the chunk-77 hookless
    /// "values must unify" rule, which would fail before the hook could
    /// run (fatal for constraint libraries like CLP(FD), whose two
    /// variables carry deliberately different domains).</summary>
    private bool HasVerifyAttributesHook =>
        CurrentFunctorAddresses?.ContainsKey(VerifyAttributesFunctorId) ?? false;

    /// <summary>Per-query string literal pool. Set by the embedding
    /// layer at query setup so IL-emitted <c>get_pstr</c> / <c>put_pstr</c>
    /// opcodes (chunk 50) can resolve a literal id to its string at
    /// runtime — same lookup the bytecode interpreter does, but
    /// accessible from the Engine surface so Tier-1 IL doesn't need
    /// to carry its own pool reference.</summary>
    public IReadOnlyList<string>? CurrentStringLiterals { get; set; }

    /// <summary>Per-query bytecode program, set alongside
    /// <see cref="CurrentFunctorAddresses"/>. IL-emitted <c>Call</c>
    /// opcodes (chunk 50) re-enter the bytecode interpreter on this
    /// program to run sub-predicates synchronously. ADR-015 chunk C: the
    /// program grows — a dynamic predicate modified mid-query is
    /// recompiled and appended via <see cref="AppendCode"/>.</summary>
    public byte[]? CurrentProgram { get; set; }

    private int _programLength = -1;

    /// <summary>Logical length of the program (ADR-015 chunk E).
    /// <see cref="CurrentProgram"/> is over-allocated — capacity grows by
    /// doubling — so <see cref="AppendCode"/> is amortised O(1) instead of
    /// copying the whole buffer each call. The slack tail is zero (the
    /// Invalid opcode), so a stray PC into it still fails loudly.</summary>
    public int ProgramLength =>
        _programLength >= 0 ? _programLength : (CurrentProgram?.Length ?? 0);

    /// <summary>Chunk 151b — the persistent dynamic-code buffer is
    /// over-allocated up front (so mid-query assertz extends without
    /// re-copy), so the engine needs to know its live length explicitly.
    /// Sets <see cref="ProgramLength"/> on a fresh engine before any
    /// <see cref="AppendCode"/> call. </summary>
    public void SetInitialProgramLength(int length) => _programLength = length;

    /// <summary>Chunk 151b — the per-query overlay buffer holding the
    /// synthetic <c>__query__</c> clause and its auxiliaries. Lives at
    /// logical addresses ≥ <see cref="CurrentQuerySplit"/>; addresses
    /// below the split index into <see cref="CurrentProgram"/>. Null
    /// when there's no overlay (e.g. a sub-engine, IL re-entry path
    /// driving over the persistent buffer alone).</summary>
    public byte[]? CurrentQueryOverlay { get; set; }

    /// <summary>The logical address at which <see cref="CurrentQueryOverlay"/>
    /// starts. Addresses in <c>[0, Split)</c> live in
    /// <see cref="CurrentProgram"/>; addresses in
    /// <c>[Split, Split + Overlay.Length)</c> live in the overlay.</summary>
    public int CurrentQuerySplit { get; set; }

    /// <summary>The two-buffer logical view used by the interpreter
    /// dispatch loop's hot-path refresh after a mid-query
    /// <see cref="AppendCode"/>. Reads stay correct across the
    /// realloc-and-grow path.</summary>
    public Shumway.Core.ProgramView GetProgramView()
    {
        var prog = CurrentProgram ?? Array.Empty<byte>();
        if (CurrentQueryOverlay is null) return new Shumway.Core.ProgramView(prog);
        return new Shumway.Core.ProgramView(prog, CurrentQueryOverlay, CurrentQuerySplit);
    }

    /// <summary>Chunk 155b — per-query switch tables, wired by the
    /// embedding layer at query setup as a mutable list so the
    /// chunk-155c new-key assertz path can add bucket keys in place
    /// by swapping the entry at a given table id.</summary>
    public System.Collections.Generic.List<Shumway.Core.SwitchTable>? SwitchTables { get; set; }

    /// <summary>Helper: returns the switch table at the given index
    /// or <c>null</c> when out of range / not wired. Used by
    /// PrologEngine's chunk-155b/c in-place assertz path to look up
    /// the bucket chain head for a new clause's key.</summary>
    public Shumway.Core.SwitchTable? GetSwitchTable(int id)
    {
        var tables = SwitchTables;
        if (tables is null || id < 0 || id >= tables.Count) return null;
        return tables[id];
    }

    /// <summary>Chunk 155c — replaces the switch table at
    /// <paramref name="id"/>, used by the new-key assertz path to
    /// extend a switch table with an additional <c>(key →
    /// bucket-chain-head)</c> entry. The interpreter reads through
    /// the list reference each dispatch, so the replacement takes
    /// effect immediately.</summary>
    public void ReplaceSwitchTable(int id, Shumway.Core.SwitchTable table)
    {
        var tables = SwitchTables;
        if (tables is null || id < 0 || id >= tables.Count) return;
        tables[id] = table;
    }

    /// <summary>Appends a linked bytecode chunk to <see cref="CurrentProgram"/>
    /// and returns its start offset. Existing offsets stay valid — the
    /// content is only ever appended, never moved. Capacity doubling keeps
    /// a long-running query's repeated dynamic recompiles from re-copying
    /// the whole (growing) buffer each time.</summary>
    public int AppendCode(byte[] chunk)
    {
        byte[] program = CurrentProgram ?? Array.Empty<byte>();
        int offset = ProgramLength;
        int needed = offset + chunk.Length;
        if (needed > program.Length)
        {
            var grown = new byte[Math.Max(needed, program.Length * 2)];
            Array.Copy(program, grown, offset);
            CurrentProgram = program = grown;
            // Chunk 169: bumping the generation tells the interpreter's
            // dispatch loop to refresh its cached ProgramView. Plain
            // (in-place) byte writes don't change the array reference,
            // so they don't need a bump; only a reallocation does.
            _programGeneration++;
        }
        Array.Copy(chunk, 0, program, offset, chunk.Length);
        _programLength = needed;
        return offset;
    }

    /// <summary>Monotonic counter bumped whenever the bytecode
    /// program's underlying array reference changes (a reallocation
    /// inside <see cref="AppendCode"/>, an embedding-layer rewire of
    /// <see cref="CurrentProgram"/> / <see cref="CurrentQueryOverlay"/>
    /// / <see cref="CurrentQuerySplit"/>). The bytecode interpreter
    /// caches its <see cref="ProgramView"/> across dispatch iterations
    /// and only refreshes when this generation has changed — the
    /// per-iteration <c>GetProgramView()</c> call was measurable on
    /// Blint.pl's hot loop (chunk 169).</summary>
    public int ProgramGeneration => _programGeneration;
    private int _programGeneration;

    /// <summary>Bump after the embedding layer rewires program /
    /// overlay / split fields directly (e.g., chunk 151b's per-
    /// query reset of <see cref="CurrentQueryOverlay"/>). The
    /// interpreter then picks up the new view on its next dispatch
    /// iteration.</summary>
    public void BumpProgramGeneration() => _programGeneration++;

    /// <summary>The dynamic-database generation the currently-running
    /// goal saw when it entered (ADR-015 chunk C, bytecode-level
    /// dispatch). Sampled by the upcoming <c>EnterDynamic</c> opcode at
    /// every dynamic-predicate entry, captured into each choice point's
    /// <c>ViewGen</c> slot by <c>try_me_else</c>, restored on
    /// <c>retry_me_else</c>. The upcoming <c>CheckVisible</c> instruction
    /// reads this against a clause's <c>born</c> / <c>died</c> to honour
    /// the ISO logical update view. Zero outside dynamic dispatch.</summary>
    public long CurrentViewGen { get; set; }

    /// <summary>Name of the builtin currently executing, set by the
    /// <c>CallBuiltin</c> dispatch right before invoking the impl. Read
    /// by <c>IsoError</c> when constructing an <c>error/2</c> term so the
    /// Context slot reports the offending builtin as <c>Name/Arity</c>
    /// rather than a fresh anonymous variable (the impl-defined identity
    /// ISO §7.12.2 calls for). Never reset on impl return — the next
    /// builtin dispatch overwrites it, and on an exception unwind the
    /// last-set value is exactly the one we want. <c>null</c> outside
    /// any builtin (during interpreter-emitted opcodes, IL-compiled
    /// bodies, or the embedding-layer query plumbing).</summary>
    public string? CurrentBuiltinName { get; set; }

    /// <summary>Arity companion to <see cref="CurrentBuiltinName"/>.</summary>
    public int CurrentBuiltinArity { get; set; }

    /// <summary>Per-engine stream registry (chunk 140). Wired by the
    /// embedding layer at query setup; <c>StreamBuiltins</c> uses it
    /// for every <c>open/close/read/write</c> dispatch so handles
    /// outlive any one query.</summary>
    public StreamRegistry? Streams { get; set; }

    /// <summary>Chunk-150 free-list of dead-clause bytecode regions
    /// available for reuse by within-query incremental
    /// <c>assertz</c> / <c>asserta</c>. Lives on <see cref="Engine"/>
    /// purely as legacy ABI — chunk 151b migrated the live free-list
    /// to <c>PrologEngine</c>'s persistent buffer so chunks freed in
    /// one query are reusable by the next. Not consulted by any
    /// current code path; kept as a no-op holder for any external
    /// caller that still references it.</summary>
    public readonly List<(int Addr, int Length)> FreeChunks = new();

    /// <summary>Wired by the embedding layer at query setup —
    /// materialises a <see cref="Cell"/> into the AST <c>Term</c>
    /// type (held as an opaque <c>object</c> here because Core can't
    /// reference the AST namespace). Used by
    /// <see cref="PrologRuntimeException"/>'s value-carrying
    /// constructor (chunk 144) so a throwing builtin can snapshot
    /// the offending term into the error's value slot; eager
    /// materialisation lets the value survive sub-engine teardown.
    /// </summary>
    public Func<Cell, object?>? MaterializeCellToTerm { get; set; }

    /// <summary>Embedding-supplied resolver from an absolute bytecode
    /// address to a human-readable <c>"name/arity@offset"</c> string,
    /// used by the opt-in <c>SHUMWAY_CP_TRACE</c> dump
    /// (<c>ChoicePointTrace.DumpAtSite</c>) to label each live
    /// choice-point's saved BP. Returns <c>null</c> when the address
    /// falls outside any known predicate range. Wired by
    /// <c>PrologEngine</c> at query setup against the same
    /// <c>_currentPredicatesByAddress</c> the stack-trace resolver
    /// uses.</summary>
    public Func<int, string?>? ResolveAddressToLabel { get; set; }

    /// <summary>Absolute byte position of the per-query fail-stub
    /// (ADR-015 chunk C step 4) — a tiny <c>call_builtin fail/0</c>
    /// emitted in the prefix. Dynamic predicates' last-clause chain
    /// instructions point here as their "no more clauses" target; an
    /// empty dynamic predicate's trampoline jumps here directly. Set by
    /// the embedding layer at query setup; zero outside dynamic
    /// dispatch.</summary>
    public int DynamicFailStubAddr { get; set; }

    /// <summary>Reads the host's current dynamic-database generation.
    /// Wired by the embedding layer at query setup so the
    /// <c>enter_dynamic</c> opcode can sample it without the interpreter
    /// having to depend on the embedding layer's types.</summary>
    public Func<long>? DbGenerationProvider { get; set; }

    /// <summary>ADR-015 chunk C step 4: refreshes the interpreter's
    /// literal pools after an <c>assertz</c> / <c>asserta</c> may have
    /// interned a new string / float / bigint literal. Wired at query
    /// setup; the incremental assert paths invoke it.</summary>
    public Action<IReadOnlyList<string>, IReadOnlyList<double>,
        IReadOnlyList<System.Numerics.BigInteger>>? RefreshLiteralPoolsCallback { get; set; }

    /// <summary>Snapshot of <see cref="CurrentViewGen"/> from a given CP
    /// — exposed so the choice-point save/restore stays inside
    /// <c>PushChoicePoint</c> / <c>RestoreCommonFromCurrentCp</c>.</summary>
    public long ViewGenOf(int cpBase, int arity) =>
        _stack[cpBase + CpViewGenOffset(arity)].Payload;   // strip RawInt tag

    // Phase 16 chunk 183: chunk-50 IlSubroutineRunner, chunk-66
    // BacktrackRunner and chunk-174 SetBacktrackFloor callbacks were
    // deleted when IL non-tail Call dispatch switched to threaded
    // continuation. The threaded design uses resume markers
    // (chunk 181) and the natural CP cascade — no recursive
    // sub-engine, no separate backtrack driver, no floor pin.

    /// <summary>Walks the environment-frame chain starting at the
    /// current frame, yielding each frame's saved return address
    /// (<c>CP</c>) — the bytecode location the caller will resume at
    /// when the current procedure proceeds. The embedding layer
    /// translates these to predicate names via the per-query address
    /// map to assemble a stack trace at error reporting time
    /// (chunk 51).</summary>
    /// <summary>Walks the active choice-point chain from the current
    /// CP toward the root. Each yielded triple is
    /// <c>(stackB, savedBp, arity)</c> where <c>savedBp</c> is the
    /// next-clause address recorded at CP push time
    /// (<see cref="IlChoicePointSentinelBp"/> for IL-side CPs and
    /// builtin CPs that route through the IL pop path). Used by the
    /// opt-in <c>SHUMWAY_CP_TRACE</c> diagnostic to dump the live
    /// CP stack at suspicious error sites (chunk 162).</summary>
    public IEnumerable<(int StackB, int SavedBp, int Arity)> EnumerateChoicePoints()
    {
        int b = _b;
        // The CP chain is anchored at _b == -1 (no CPs left). Each frame
        // stores the previous B at CpBOffset(arity). Walk until we hit
        // the sentinel.
        while (b >= 0)
        {
            int arity = (int)_stack[b + CpArityOffset].Data;
            int bp = (int)_stack[b + CpBpOffset(arity)].Data;
            int prevB = (int)_stack[b + CpBOffset(arity)].Data;
            yield return (b, bp, arity);
            if (prevB == b) yield break;
            b = prevB;
        }
    }

    public IEnumerable<int> EnumerateCallReturnAddresses()
    {
        // The first frame to surface is the IMMEDIATE return target —
        // _cp is the caller's "next instruction after Call". After that
        // we walk env frames; each frame stores the *caller's* CP at
        // EnvCpOffset, and EnvCeOffset chains back to the next frame
        // up the call tree.
        if (_cp >= 0) yield return _cp;
        int e = _e;
        while (e >= 0)
        {
            int cp = (int)_stack[e + EnvCpOffset].Data;
            if (cp >= 0 && cp != _cp) yield return cp;
            int prevE = (int)_stack[e + EnvCeOffset].Data;
            if (prevE == e || prevE < 0) yield break;
            e = prevE;
        }
    }

    // ----- IL tail-call signal (Tier-1, chunk 47) -----
    //
    // When an IL delegate emits an Execute opcode, it sets _pc to the
    // tail-call target and raises this flag. The interpreter's Call /
    // Execute handlers consult the flag after the IL returns: when set,
    // they leave _pc alone instead of overriding it with _cp, so the
    // dispatch picks up at the target rather than returning to the
    // caller's continuation immediately. Cleared by the handler that
    // observes it.
    public bool IlTailCallPending { get; set; }

    // ----- IL choice points (Tier-1, chunk 41) -----
    //
    // A side table mapping a choice-point frame's stack index to the IL
    // delegate + cursor that should run when backtracking pops that frame.
    // The CP frame itself uses a sentinel BP (-1) so the bytecode
    // interpreter's standard PC-based backtrack path doesn't accidentally
    // jump into bytecode 0xFFFFFFFF.
    public const int IlChoicePointSentinelBp = -1;
    // Chunk 231 removed the Dictionary form of _ilCpInfo in favour of
    // the stack-array _ilCpStack/_ilCpTop declared just above (with
    // the IlChoicePointEntry struct).

    /// <summary>Chunk 233 — per-engine slot for the IL indexed-dispatch
    /// cache (the typed dictionary lives in Compiler.Il and Core can't
    /// name its type). Previously a
    /// <c>ConditionalWeakTable&lt;Engine, ConcurrentDictionary&gt;</c>
    /// in <c>IlIndexedDispatch._perEngineCache</c> — every IL Call to
    /// an indexed predicate paid an internal ConditionalWeakTable
    /// lock + a ConcurrentDictionary bucket lock. Engine is single-
    /// threaded and the cache lives exactly as long as the engine, so
    /// a plain instance field is both safe and free of those internal
    /// locks. Compiler.Il accesses it via an <c>is</c> pattern check
    /// to the typed Dictionary, which the JIT compiles to a single
    /// type-token compare (no Dictionary boxing / cast).</summary>
    public object? IlIndexedDispatchCache;

    // Phase 16 — threaded Tier-1 dispatch. An IL non-tail Call site sets
    // engine.Cp to a *resume marker* address instead of recursing into
    // RunSubroutine. When the callee Proceeds (Pc = Cp), the bytecode
    // interpreter's main loop sees the marker, decodes it back to
    // (functorId, cursor), looks up the IL delegate via the active
    // Tier1Dispatcher, and invokes it at the right cursor. The marker
    // encoding lives entirely in the int address — no side table — so
    // saving / restoring Cp around frames just works.
    //
    // Encoding:
    //   marker = ResumeMarkerBase + functorId * ResumeMarkerCursorStride + cursor
    //
    // ResumeMarkerBase is set high enough that no plausible bytecode
    // address collides (the per-query overlay lives at
    // PersistentToQueryGap which is ~64 MB — markers start at 1 GB).
    // ResumeMarkerCursorStride caps a single predicate at 4096 forward-
    // resume cursors, which is *vastly* more than the number of Call
    // sites a real predicate has (Blint's parse_args is the busiest at
    // ~60).
    public const int ResumeMarkerBase = 0x4000_0000;
    public const int ResumeMarkerCursorStride = 4096;

    public static bool IsResumeMarker(int address) => address >= ResumeMarkerBase;

    public static int EncodeResumeMarker(int functorId, int cursor)
    {
        if (cursor < 0 || cursor >= ResumeMarkerCursorStride)
            throw new ArgumentOutOfRangeException(nameof(cursor),
                $"cursor must be in [0, {ResumeMarkerCursorStride}); got {cursor}.");
        return ResumeMarkerBase + functorId * ResumeMarkerCursorStride + cursor;
    }

    public static (int FunctorId, int Cursor) DecodeResumeMarker(int address)
    {
        int slot = address - ResumeMarkerBase;
        return (slot / ResumeMarkerCursorStride, slot % ResumeMarkerCursorStride);
    }

    /// <summary>Phase 29 region compilation — at a region member's proceed, decode
    /// the continuation (<see cref="Cp"/>): if it is a resume marker INTO this
    /// region (functor id == <paramref name="regionRootFunctorId"/>) the member's
    /// proceed continues inside the region's IL method at the returned cursor (the
    /// emit does an intra-method <c>br</c>); otherwise (a different functor, or not
    /// a marker at all — the region's own caller-continuation) it returns −1 and the
    /// member returns to the dispatch loop, which runs <c>Cp</c>.</summary>
    public int RegionReturnCursor(int regionRootFunctorId)
    {
        int cp = _cp;
        if (!IsResumeMarker(cp)) return -1;
        var (fid, cursor) = DecodeResumeMarker(cp);
        return fid == regionRootFunctorId ? cursor : -1;
    }

    private struct IlChoicePointEntry
    {
        public Func<Engine, int, bool> Del;
        public int Cursor;
        // Chunk 231 — the _b value at PushIlChoicePoint time. Lets
        // Cut(barrier) compare against the IL CP stack without going
        // through the Dictionary's KeyCollection (which was the
        // PopIlChoicePointAndRestore + Engine.Cut hot path: ~5.31%
        // self-time on Engine.Cut from the foreach over Keys, plus
        // ~1.55% from FindValue/MoveNext on dict ops in profiling
        // Blint with bundled user IL).
        public int Key;
        // Chunk 245 — optional cleanup callback invoked when this
        // CP is discarded without ever being backtracked into (cut
        // pruning, or — eventually — engine teardown). Non-det
        // foreign predicates supply iter.Dispose here so a
        // generator that holds non-managed resources gets
        // deterministic cleanup when Prolog `!` commits past its
        // choice point. Null for the (vast majority of) IL CPs
        // that have no extra-engine state to release.
        public Action? OnPrune;
    }

    // Chunk 231 — stack-array replacement for the previous
    // Dictionary<int, IlChoicePointEntry> _ilCpInfo. IL CPs are
    // always pushed in monotonic _b order (each CP push grows _b)
    // and popped (or cut) from the top — same shape as a plain
    // stack. Direct array index + a parallel _ilCpTop pointer
    // replaces dict hash/probe per op. Grown copy-on-resize when
    // _ilCpTop reaches capacity.
    private IlChoicePointEntry[] _ilCpStack = new IlChoicePointEntry[64];
    private int _ilCpTop;

    /// <summary>Pushes a choice point that, on backtrack, re-enters an IL
    /// delegate at <paramref name="nextCursor"/> instead of jumping to a
    /// bytecode address. State preservation matches the bytecode CP
    /// machinery exactly — the only difference is what happens at retry
    /// time.</summary>
    public void PushIlChoicePoint(Func<Engine, int, bool> del, int nextCursor, int arity)
        => PushIlChoicePoint(del, nextCursor, arity, onPrune: null);

    /// <summary>Chunk 245 overload — additionally registers an
    /// <paramref name="onPrune"/> callback invoked exactly once if
    /// this CP is discarded without ever being backtracked into
    /// (cut pruning). The callback fires before the entry's
    /// delegate reference is released; if it throws, the
    /// exception propagates and the remaining CPs above the
    /// barrier are <em>not</em> pruned — callers should keep the
    /// callback small and safe (a single Dispose, no
    /// arbitrary user code).</summary>
    public void PushIlChoicePoint(
        Func<Engine, int, bool> del, int nextCursor, int arity, Action? onPrune)
    {
        ArgumentNullException.ThrowIfNull(del);
        PushChoicePoint(arity, IlChoicePointSentinelBp);
        if (_ilCpTop == _ilCpStack.Length)
            System.Array.Resize(ref _ilCpStack, _ilCpStack.Length * 2);
        _ilCpStack[_ilCpTop++] = new IlChoicePointEntry
        {
            Del = del, Cursor = nextCursor, Key = _b, OnPrune = onPrune,
        };
    }

    /// <summary>Wrapper around <see cref="PushIlChoicePoint"/> for
    /// builtins that need runtime choice-point semantics (chunk 56's
    /// multi-solution <c>call/N</c>, the non-deterministic split modes
    /// of <c>append/3</c> and <c>atom_concat/3</c>). The push itself is
    /// identical to <see cref="PushIlChoicePoint"/>; the wrapper exists
    /// so the resume mechanism's "post-call PC" convention is named
    /// consistently across builtin sites.
    ///
    /// <para>On a successful retry the resume delegate is expected to
    /// call <see cref="ResumeAtReturnPc(int)"/> with the address of the
    /// instruction immediately after the <c>call_builtin</c> opcode
    /// that originally invoked the builtin. That sets the engine's PC
    /// and IL-tail-call flag so the interpreter resumes execution at
    /// the next goal instead of falling back on the saved <c>Cp</c>
    /// (which points at the parent procedure's continuation, not the
    /// next instruction in the current clause).</para></summary>
    public void PushBuiltinChoicePoint(
        Func<Engine, int, bool> del, int arity)
    {
        PushIlChoicePoint(del, nextCursor: 0, arity: arity);
    }

    /// <summary>Chunk 245 — builtin-CP overload that registers an
    /// <paramref name="onPrune"/> cleanup callback. Used by the
    /// non-deterministic [PrologPredicate] bridge to Dispose the
    /// iterator when Prolog `!` cuts past the CP without the
    /// engine backtracking through it (in which case
    /// MoveNext-returns-false already handles Dispose).</summary>
    public void PushBuiltinChoicePoint(
        Func<Engine, int, bool> del, int arity, Action? onPrune)
    {
        PushIlChoicePoint(del, nextCursor: 0, arity: arity, onPrune: onPrune);
    }

    /// <summary>Sets the engine's PC to <paramref name="returnPc"/> and
    /// flags an IL-style tail call so the interpreter, on this
    /// retry-success, leaves PC alone instead of overriding it with
    /// <see cref="Cp"/>. Used by builtin choice-point resume delegates
    /// (chunk 56) to land execution on the instruction immediately
    /// after the <c>call_builtin</c> that pushed the CP.</summary>
    public void ResumeAtReturnPc(int returnPc)
    {
        _p = returnPc;
        IlTailCallPending = true;
    }

    /// <summary>True when the topmost choice point is an IL CP — the
    /// bytecode interpreter consults this on backtrack to choose between
    /// the standard PC-jump path and the IL re-dispatch path.</summary>
    public bool TopChoicePointIsIl =>
        _b >= 0 && _ilCpTop > 0 && _ilCpStack[_ilCpTop - 1].Key == _b;

    /// <summary>Pops the topmost IL choice point, restoring engine state
    /// (heap top, trails, registers, …) the same way <c>TrustMe</c> would
    /// for a bytecode CP, and returns the delegate + cursor that should
    /// be re-invoked. The caller (usually the interpreter's
    /// <c>TryBacktrack</c>) is responsible for actually calling the
    /// delegate.</summary>
    /// <summary>Diagnostic flag — when on, PushChoicePoint and the
    /// IL CP pop log <c>_b</c> / <c>_e</c> / <c>_stackTop</c> for
    /// every event. Used by the chunk 173 debug session to track
    /// whether a meta-CP's saved <c>_e</c> still names a valid
    /// frame at pop time.</summary>
    public static bool TraceCpStack { get; set; }

    public (Func<Engine, int, bool> Del, int Cursor) PopIlChoicePointAndRestore()
    {
        if (_b < 0)
            throw new InvalidOperationException("PopIlChoicePointAndRestore: no active choice point.");
        if (_ilCpTop == 0 || _ilCpStack[_ilCpTop - 1].Key != _b)
            throw new InvalidOperationException(
                "PopIlChoicePointAndRestore: the topmost choice point isn't an IL CP.");
        var info = _ilCpStack[_ilCpTop - 1];

        Diagnostics.PopRestoreTrace.PrePop(this, _b);
        if (TraceCpStack)
            System.Console.Error.WriteLine($"[cp-stack] pop-il _b={_b} _e_before_restore={_e} _stackTop_before={_stackTop} saved_e={_stack[_b + CpCeOffset((int)_stack[_b + CpArityOffset].Data)].Data}");
        int arity = RestoreCommonFromCurrentCp();
        Diagnostics.PopRestoreTrace.PostRestore(this, arity);
        _hb = (int)_stack[_b + CpHbOffset(arity)].Data;
        int oldB = _b;
        _b = (int)_stack[_b + CpBOffset(arity)].Data;
        _stackTop = oldB;
        if (TraceCpStack)
            System.Console.Error.WriteLine($"[cp-stack] pop-il-done _b={_b} _e={_e} _stackTop={_stackTop}");
        // Clear the delegate reference so the array doesn't pin it
        // for GC after pop. The OnPrune (chunk 245) is NOT
        // invoked here — backtracking *into* the CP means the
        // delegate runs and handles its own cleanup (the non-det
        // bridge's MoveNext-returns-false path Disposes the
        // iterator). OnPrune is only for cut-pruned discards.
        _ilCpStack[_ilCpTop - 1].Del = null!;
        _ilCpStack[_ilCpTop - 1].OnPrune = null;
        _ilCpTop--;
        return (info.Del, info.Cursor);
    }

    // ----- Auxiliary value tables -----

    /// <summary>
    /// Stores <paramref name="value"/> in the engine's BigInteger table and returns a
    /// BIGINT cell whose payload is its id. The cell is meaningful only for this engine
    /// (auxiliary tables are not shared, unlike atoms and functors).
    ///
    /// <para>Values that fit in the 60-bit inline range collapse to <see cref="Cell.Int"/>
    /// instead of consuming a side-table slot. The runtime invariant is that a value
    /// in 60-bit range is always represented as <c>Tag.Int</c>, never <c>Tag.BigInt</c>;
    /// keeping the canonical form unique lets unification compare values by raw cell
    /// equality without crossing tag boundaries.</para>
    /// </summary>
    public Cell MakeBigInt(BigInteger value)
    {
        if (value >= Cell.MinInt60 && value <= Cell.MaxInt60)
            return Cell.Int((long)value);
        int id = _bigIntTable.Count;
        _bigIntTable.Add(value);
        // Record the allocation on the extra trail so a later backtrack
        // truncates the table back to its pre-allocation size and frees
        // the slot (chunk 41 — BigInt trail-aware allocation).
        TrailBigIntAlloc(id);
        return Cell.BigInt(id);
    }

    private void TrailBigIntAlloc(int oldCount)
    {
        EnsureExtraTrailCapacity(1);
        _extraTrail[_extraTrailTop++] = new ExtraTrailEntry
        {
            Type = TrailType.BigIntAlloc,
            HeapIdx = oldCount,
            OldValue = default,
            BindingTrailMarker = _bindingTrailTop,
        };
    }

    /// <summary>Returns the <see cref="BigInteger"/> referenced by a BIGINT cell.</summary>
    public BigInteger AsBigInt(Cell cell)
    {
        if (cell.Tag != Tag.BigInt)
            throw new InvalidOperationException($"Cell tag is {cell.Tag}, expected BigInt.");
        return _bigIntTable[cell.AsBigIntId];
    }

    /// <summary>Stores <paramref name="value"/> in the engine's string table and returns
    /// a STRING cell whose payload is its id.</summary>
    public Cell MakeString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int id = _stringTable.Count;
        _stringTable.Add(value);
        return Cell.String(id);
    }

    /// <summary>Returns the string referenced by a STRING cell.</summary>
    public string AsString(Cell cell)
    {
        if (cell.Tag != Tag.String)
            throw new InvalidOperationException($"Cell tag is {cell.Tag}, expected String.");
        return _stringTable[cell.AsStringId];
    }

    /// <summary>Stores <paramref name="value"/> in the engine's foreign-object table and
    /// returns a FOREIGN cell whose payload is its id. The value may be <c>null</c>.</summary>
    public Cell MakeForeign(object? value)
    {
        int id = _foreignTable.Count;
        _foreignTable.Add(value);
        return Cell.Foreign(id);
    }

    /// <summary>Returns the object referenced by a FOREIGN cell (possibly <c>null</c>).</summary>
    public object? AsForeign(Cell cell)
    {
        if (cell.Tag != Tag.Foreign)
            throw new InvalidOperationException($"Cell tag is {cell.Tag}, expected Foreign.");
        return _foreignTable[cell.AsForeignId];
    }

    /// <summary>Typed accessor over <see cref="AsForeign(Cell)"/>. Casts to <typeparamref name="T"/>;
    /// returns <c>default</c> if the stored value is <c>null</c>.</summary>
    public T? AsForeign<T>(Cell cell) where T : class
    {
        object? value = AsForeign(cell);
        if (value is null) return null;
        if (value is T typed) return typed;
        throw new InvalidCastException($"Foreign value of type {value.GetType()} is not assignable to {typeof(T)}.");
    }

    /// <summary>
    /// Allocates a FLOAT header cell and its paired INT cell contiguously on the heap
    /// and returns the heap index of the header. Together they encode the 64-bit double
    /// per the two-cell layout in <see cref="Cell.MakeFloat(double, int)"/>.
    /// </summary>
    public int MakeFloat(double value)
    {
        int headerIdx = AllocateHeap(2);
        var (header, paired) = Cell.MakeFloat(value, headerIdx + 1);
        _heap[headerIdx] = header;
        _heap[headerIdx + 1] = paired;
        return headerIdx;
    }

    /// <summary>
    /// Allocates a PSTR header, the necessary buffer cells (each holding three UTF-16
    /// code units), and a tail cell initialised to <c>[]</c>, contiguously on the heap.
    /// Returns the heap index of the header. Total cells used: <c>1 + ceil(len/3) + 1</c>.
    /// </summary>
    public int MakePstr(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int codeUnits = value.Length;
        int bufferCellCount = (codeUnits + Cell.PstrCodeUnitsPerBuffer - 1) / Cell.PstrCodeUnitsPerBuffer;
        int totalCells = 1 + bufferCellCount + 1;

        int headerIdx = AllocateHeap(totalCells);
        int bufferStart = headerIdx + 1;
        int tailIdx = bufferStart + bufferCellCount;

        for (int i = 0; i < bufferCellCount; i++)
        {
            int basePos = i * Cell.PstrCodeUnitsPerBuffer;
            int cu0 = basePos < codeUnits ? value[basePos] : 0;
            int cu1 = (basePos + 1) < codeUnits ? value[basePos + 1] : 0;
            int cu2 = (basePos + 2) < codeUnits ? value[basePos + 2] : 0;
            _heap[bufferStart + i] = Cell.PstrBuffer(cu0, cu1, cu2);
        }

        _heap[tailIdx] = Cell.Atom(AtomTable.EmptyListId);
        _heap[headerIdx] = Cell.Pstr(codeUnits, bufferStart, 0);
        return headerIdx;
    }

    /// <summary>
    /// Reconstructs the .NET string represented by the PSTR header at <paramref name="headerIdx"/>.
    /// Reads each segment's code units then follows the tail cell — chunk 70's lazy concat
    /// chains multiple PSTR pieces together by storing a <see cref="Tag.Pstr"/> cell in the
    /// tail position, and this walker treats such tails as continuation segments.
    /// </summary>
    public string AsPstrString(int headerIdx)
    {
        Cell header = _heap[headerIdx];
        if (header.Tag != Tag.Pstr)
            throw new InvalidOperationException($"Cell tag is {header.Tag}, expected Pstr.");
        var sb = new System.Text.StringBuilder(header.AsPstrLength);
        AppendPstrChain(sb, header);
        return sb.ToString();
    }

    private void AppendPstrChain(System.Text.StringBuilder sb, Cell header)
    {
        while (header.Tag == Tag.Pstr)
        {
            int length = header.AsPstrLength;
            for (int i = 0; i < length; i++)
                sb.Append((char)GetPstrCodeUnit(header, i));
            int tailIdx = ComputePstrTailIndex(header);
            Cell tail = _heap[tailIdx];
            if (tail.Tag == Tag.Ref)
            {
                int derefIdx = Deref(tail.AsHeapIndex);
                tail = _heap[derefIdx];
            }
            header = tail;
        }
    }

    /// <summary>Total logical length of a PSTR chain in UTF-16 code units —
    /// follows tail cells when they are <see cref="Tag.Pstr"/> (chunk 70's
    /// lazy concat representation). Returns the immediate segment's length
    /// when the tail is anything else.</summary>
    public int GetPstrChainLength(int headerIdx)
    {
        int total = 0;
        Cell header = _heap[headerIdx];
        while (header.Tag == Tag.Pstr)
        {
            total += header.AsPstrLength;
            int tailIdx = ComputePstrTailIndex(header);
            Cell tail = _heap[tailIdx];
            if (tail.Tag == Tag.Ref)
            {
                int derefIdx = Deref(tail.AsHeapIndex);
                tail = _heap[derefIdx];
            }
            header = tail;
        }
        return total;
    }

    /// <summary>Lazy <c>pstr_concat</c> (chunk 70): builds a new PSTR whose
    /// buffer holds <paramref name="aIdx"/>'s logical content (flattening
    /// any pre-existing tail chain) and whose tail cell stores a
    /// <see cref="Tag.Pstr"/> reference to <paramref name="bIdx"/>'s
    /// header. The result's right side (B) is shared without copying;
    /// only the left side is materialised into a fresh buffer. For
    /// concat-then-decompose grammar pipelines this avoids the
    /// <c>O(N_b)</c> cell allocation per join that the eager
    /// <c>MakePstr(aStr + bStr)</c> path used to pay.
    ///
    /// <para>Special cases: when A or B is logically empty, the result
    /// is just the non-empty side (no allocation). When B is empty but
    /// non-PSTR (e.g. the <c>[]</c> atom — caller's responsibility to
    /// recognise that case), the caller should fall back to the eager
    /// path.</para></summary>
    public int MakePstrConcat(int aIdx, int bIdx)
    {
        Cell aHdr = _heap[aIdx];
        Cell bHdr = _heap[bIdx];
        if (aHdr.Tag != Tag.Pstr)
            throw new InvalidOperationException(
                $"MakePstrConcat: A's cell tag is {aHdr.Tag}, expected Pstr.");
        if (bHdr.Tag != Tag.Pstr)
            throw new InvalidOperationException(
                $"MakePstrConcat: B's cell tag is {bHdr.Tag}, expected Pstr.");

        int totalALength = GetPstrChainLength(aIdx);
        int bChainLength = GetPstrChainLength(bIdx);
        if (totalALength == 0) return bIdx;
        if (bChainLength == 0) return aIdx;

        int bufferCellCount =
            (totalALength + Cell.PstrCodeUnitsPerBuffer - 1) / Cell.PstrCodeUnitsPerBuffer;
        int totalCells = 1 + bufferCellCount + 1;
        int headerIdx = AllocateHeap(totalCells);
        int bufferStart = headerIdx + 1;
        int tailIdx = bufferStart + bufferCellCount;

        // Copy A's full content (following any existing chain) into the
        // new buffer in 3-code-unit groups.
        var aChars = new char[totalALength];
        FillCharsFromPstrChain(_heap[aIdx], aChars);
        for (int i = 0; i < bufferCellCount; i++)
        {
            int basePos = i * Cell.PstrCodeUnitsPerBuffer;
            int cu0 = basePos < totalALength ? aChars[basePos] : 0;
            int cu1 = (basePos + 1) < totalALength ? aChars[basePos + 1] : 0;
            int cu2 = (basePos + 2) < totalALength ? aChars[basePos + 2] : 0;
            _heap[bufferStart + i] = Cell.PstrBuffer(cu0, cu1, cu2);
        }

        // Tail of the new PSTR is B's header value — by storing a Pstr
        // cell here we extend the chain without copying B's buffer. The
        // unification path's recursive Unify on tail indices follows the
        // chain transparently (UnifyPstrPstr dispatches on Pstr-Pstr).
        _heap[tailIdx] = _heap[bIdx];
        _heap[headerIdx] = Cell.Pstr(totalALength, bufferStart, 0);
        return headerIdx;
    }

    private void FillCharsFromPstrChain(Cell header, char[] dst)
    {
        int writeIdx = 0;
        while (header.Tag == Tag.Pstr)
        {
            int length = header.AsPstrLength;
            for (int i = 0; i < length; i++)
                dst[writeIdx++] = (char)GetPstrCodeUnit(header, i);
            int tailIdx = ComputePstrTailIndex(header);
            Cell tail = _heap[tailIdx];
            if (tail.Tag == Tag.Ref)
            {
                int derefIdx = Deref(tail.AsHeapIndex);
                tail = _heap[derefIdx];
            }
            header = tail;
        }
    }

    /// <summary>Heap index of the cell that immediately follows a PSTR's buffer cells.
    /// That cell is the tail value (typically <c>[]</c>, a variable, another PSTR, or
    /// a LIS in the "fallback to cons" case).</summary>
    public int GetPstrTailIndex(int headerIdx)
    {
        Cell header = _heap[headerIdx];
        if (header.Tag != Tag.Pstr)
            throw new InvalidOperationException($"Cell tag is {header.Tag}, expected Pstr.");
        return ComputePstrTailIndex(header);
    }

    /// <summary>
    /// Reads the <paramref name="i"/>-th code unit (0-indexed within the PSTR's logical
    /// content) from the PSTR header at <paramref name="headerIdx"/>. Public surface for
    /// the interpreter's PSTR-aware opcodes.
    /// </summary>
    public int GetPstrCodeUnitAt(int headerIdx, int i)
    {
        Cell header = _heap[headerIdx];
        if (header.Tag != Tag.Pstr)
            throw new InvalidOperationException($"Cell tag is {header.Tag}, expected Pstr.");
        return GetPstrCodeUnit(header, i);
    }

    /// <summary>
    /// Decomposes the PSTR header at heap[<paramref name="headerIdx"/>] into its first
    /// code unit and a tail header, updating the cell at <paramref name="headerIdx"/>
    /// in place. When the PSTR had one remaining code unit, the cell is replaced with
    /// the PSTR's own tail value (typically <c>Atom([])</c>); otherwise it's replaced
    /// with an advanced PSTR header sharing the original buffer.
    ///
    /// <para>The extracted head is returned as <c>Int(code_unit)</c> — Phase 1 only
    /// supports <c>codes</c> mode for <c>double_quotes</c>; the <c>chars</c> path is
    /// deferred until the flags subsystem lands.</para>
    /// </summary>
    /// <returns>true if a head was extracted; false if the cell was not a PSTR or had
    /// length zero (in which case no state is changed).</returns>
    public bool AdvancePstrHead(int headerIdx, out Cell head)
    {
        Cell hdr = _heap[headerIdx];
        if (hdr.Tag != Tag.Pstr || hdr.AsPstrLength == 0)
        {
            head = default;
            return false;
        }

        int firstUnit = GetPstrCodeUnit(hdr, 0);
        head = Cell.Int(firstUnit);

        int newLength = hdr.AsPstrLength - 1;
        if (newLength == 0)
        {
            int tailIdx = ComputePstrTailIndex(hdr);
            _heap[headerIdx] = _heap[tailIdx];
        }
        else
        {
            int absoluteStart = hdr.AsPstrOffset + 1;
            int newBufferIdx = hdr.AsPstrBufferIndex + absoluteStart / Cell.PstrCodeUnitsPerBuffer;
            int newOffset = absoluteStart % Cell.PstrCodeUnitsPerBuffer;
            _heap[headerIdx] = Cell.Pstr(newLength, newBufferIdx, newOffset);
        }
        return true;
    }

    private int GetPstrCodeUnit(Cell header, int i)
    {
        int absolute = header.AsPstrOffset + i;
        int cellIdx = header.AsPstrBufferIndex + absolute / Cell.PstrCodeUnitsPerBuffer;
        int posInCell = absolute % Cell.PstrCodeUnitsPerBuffer;
        return _heap[cellIdx].AsPstrCodeUnit(posInCell);
    }

    private static int ComputePstrTailIndex(Cell header)
    {
        int length = header.AsPstrLength;
        int offset = header.AsPstrOffset;
        int bufferIdx = header.AsPstrBufferIndex;
        int bufferCellCount = (offset + length + Cell.PstrCodeUnitsPerBuffer - 1) / Cell.PstrCodeUnitsPerBuffer;
        return bufferIdx + bufferCellCount;
    }

    internal int BigIntTableCount => _bigIntTable.Count;
    internal int StringTableCount => _stringTable.Count;
    internal int ForeignTableCount => _foreignTable.Count;

    // ----- Trail accessors -----

    public int BindingTrailTop => _bindingTrailTop;
    public int BindingTrailCapacity => _bindingTrail.Length;
    public int ExtraTrailTop => _extraTrailTop;
    public int ExtraTrailCapacity => _extraTrail.Length;

    // ----- Engine register accessors (read-only for now; set by the interpreter later) -----

    public int E => _e;
    public int B => _b;
    public int B0 => _b0;
    public int P => _p;
    public int Cp => _cp;

    /// <summary>True after a get_structure/get_list against an unbound argument, or
    /// after a put_structure/put_list — the interpreter is building a fresh compound
    /// and subsequent <c>unify_*</c> opcodes write cells. False when an existing
    /// compound on the heap is being matched against.</summary>
    public bool WriteMode => _writeMode;

    /// <summary>Heap index of the next cell <c>unify_*</c> will read (in read mode)
    /// or write (in write mode). Advances by one per <c>unify_*</c> instruction
    /// (or by <c>count</c> for <c>unify_void count</c>).</summary>
    public int UnifyPointer => _unifyPointer;

    // ----- Deref -----

    /// <summary>
    /// Walks REF cells starting at <paramref name="heapIdx"/> until reaching a non-REF cell
    /// or a self-pointing REF (unbound variable). Returns the final heap index. Reading
    /// <see cref="GetHeap"/> at the returned index yields the dereferenced cell.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int Deref(int heapIdx)
    {
        while (true)
        {
            Cell c = _heap[heapIdx];
            if (c.Tag != Tag.Ref) return heapIdx;
            int target = c.AsHeapIndex;
            if (target == heapIdx) return heapIdx; // unbound: self-pointer
            heapIdx = target;
        }
    }

    // ----- Bind / Trail -----

    /// <summary>
    /// Writes <paramref name="value"/> into the cell at <paramref name="varAddr"/>, trailing
    /// the previous value if the cell pre-dates the most recent choice point
    /// (<c>varAddr &lt; HB</c>). Caller is responsible for ensuring this is a valid binding
    /// (typically the cell is an unbound REF).
    /// </summary>
    public void Bind(int varAddr, Cell value)
    {
        _heap[varAddr] = value;
        if (varAddr < _hb)
            TrailBinding(varAddr);
    }

    private void TrailBinding(int heapIdx)
    {
        EnsureBindingTrailCapacity(1);
        _bindingTrail[_bindingTrailTop++] = heapIdx;
    }

    // ============================================================================
    // Attributed variables (chunk 77, Phase 4)
    // ============================================================================

    /// <summary>True iff the deref'd cell at <paramref name="heapAddr"/>
    /// is an attributed variable.</summary>
    public bool IsAttVar(int heapAddr) => _heap[Deref(heapAddr)].Tag == Tag.AttVar;

    /// <summary>Number of attribute records allocated — diagnostic surface.</summary>
    internal int AttrTableCount => _attrTable.Count;

    /// <summary>Attaches (or replaces) the attribute for
    /// <paramref name="moduleId"/> on the variable at
    /// <paramref name="varAddr"/>, whose value lives at
    /// <paramref name="valueHeapIdx"/>. A plain unbound variable is
    /// promoted to an attributed variable; an existing attributed
    /// variable's record is updated. Every mutation is trailed so it
    /// reverts on backtracking. Throws when the target isn't a
    /// variable.</summary>
    public void PutAttr(int varAddr, int moduleId, int valueHeapIdx)
    {
        int addr = Deref(varAddr);
        Cell cell = _heap[addr];
        if (cell.Tag == Tag.Ref && cell.AsHeapIndex == addr)
        {
            // Plain unbound variable → promote to an attributed
            // variable. The cell change is trailed as a ValueChange so
            // backtracking restores the plain REF. A fresh record is
            // installed under the home index, overwriting any orphan
            // left by a backtracked-then-reused heap slot.
            TrailValueChange(addr, cell);
            _heap[addr] = Cell.AttVar(addr);
            _attrTable[addr] = new Dictionary<int, int>();
        }
        else if (cell.Tag != Tag.AttVar)
        {
            // ISO shape: the embedding layer renders Detail as the
            // expected-type atom, so error(type_error(var, _), _).
            throw new PrologRuntimeException("type_error", "var");
        }

        var record = _attrTable[addr];
        int oldValue = record.TryGetValue(moduleId, out int prev) ? prev : -1;
        TrailAttrChange(addr, moduleId, oldValue);
        record[moduleId] = valueHeapIdx;
    }

    /// <summary>Reads the attribute for <paramref name="moduleId"/> on
    /// the variable at <paramref name="varAddr"/>. Returns the heap
    /// index of the stored attribute value, or <c>-1</c> when the
    /// variable carries no such attribute (or isn't an attributed
    /// variable at all).</summary>
    public int GetAttr(int varAddr, int moduleId)
    {
        int addr = Deref(varAddr);
        if (_heap[addr].Tag != Tag.AttVar) return -1;
        var record = _attrTable[addr];
        return record.TryGetValue(moduleId, out int value) ? value : -1;
    }

    /// <summary>Removes the attribute for <paramref name="moduleId"/>
    /// from the variable at <paramref name="varAddr"/>. A no-op (still
    /// succeeds) when the variable isn't attributed or carries no such
    /// attribute. The removal is trailed.</summary>
    public void DelAttr(int varAddr, int moduleId)
    {
        int addr = Deref(varAddr);
        if (_heap[addr].Tag != Tag.AttVar) return;
        var record = _attrTable[addr];
        if (!record.TryGetValue(moduleId, out int oldValue)) return;
        TrailAttrChange(addr, moduleId, oldValue);
        record.Remove(moduleId);
    }

    /// <summary>The module ids that carry an attribute on the variable
    /// at <paramref name="varAddr"/> — empty when it isn't attributed.
    /// Used by the attvar-unification merge and by <c>copy_term/3</c>'s
    /// residual-goal projection (chunk 81).</summary>
    public IReadOnlyCollection<int> AttrModules(int varAddr)
    {
        int addr = Deref(varAddr);
        return _heap[addr].Tag == Tag.AttVar
            ? _attrTable[addr].Keys
            : Array.Empty<int>();
    }

    private void TrailAttrChange(int homeAddr, int moduleId, int oldValue)
    {
        int logIndex = _attrTrailLog.Count;
        _attrTrailLog.Add((homeAddr, moduleId, oldValue));
        EnsureExtraTrailCapacity(1);
        _extraTrail[_extraTrailTop++] = new ExtraTrailEntry
        {
            Type = TrailType.AttrModify,
            HeapIdx = logIndex,
            BindingTrailMarker = _bindingTrailTop,
        };
    }

    /// <summary>
    /// Restores all bindings recorded after <paramref name="targetTop"/>, leaving the binding
    /// trail at exactly that depth. Each rolled-back cell becomes a self-pointing REF again.
    /// Use this only when no <see cref="ExtraTrailEntry"/> entries are involved — otherwise
    /// call <see cref="UnwindTrails"/> for the interleaved version required by ADR-004.
    /// </summary>
    public void UnwindBindingTrail(int targetTop)
    {
        if (targetTop < 0 || targetTop > _bindingTrailTop)
            throw new ArgumentOutOfRangeException(nameof(targetTop));
        while (_bindingTrailTop > targetTop)
        {
            int idx = _bindingTrail[--_bindingTrailTop];
            _heap[idx] = Cell.UnboundVar(idx);
        }
    }

    /// <summary>
    /// Records a value-change entry on the extra trail. Use this when overwriting an
    /// already-bound cell whose previous value must survive backtracking. The
    /// <see cref="ExtraTrailEntry.BindingTrailMarker"/> captures the current binding-trail
    /// depth so <see cref="UnwindTrails"/> can interleave correctly with binding unwind.
    /// </summary>
    public void TrailValueChange(int heapIdx, Cell oldValue)
    {
        EnsureExtraTrailCapacity(1);
        _extraTrail[_extraTrailTop++] = new ExtraTrailEntry
        {
            Type = TrailType.ValueChange,
            HeapIdx = heapIdx,
            OldValue = oldValue,
            BindingTrailMarker = _bindingTrailTop,
        };
    }

    /// <summary>
    /// Interleaved unwind of both trails back to the given target depths. Walks the extra
    /// trail in reverse; for each entry, first rolls back binding-trail entries down to
    /// the entry's <see cref="ExtraTrailEntry.BindingTrailMarker"/>, then applies the
    /// extra entry itself. Once the extra trail reaches <paramref name="extraTarget"/>,
    /// any remaining binding-trail entries above <paramref name="bindingTarget"/> are
    /// rolled back in a final pass.
    /// </summary>
    public void UnwindTrails(int bindingTarget, int extraTarget)
    {
        if (bindingTarget < 0 || bindingTarget > _bindingTrailTop)
            throw new ArgumentOutOfRangeException(nameof(bindingTarget));
        if (extraTarget < 0 || extraTarget > _extraTrailTop)
            throw new ArgumentOutOfRangeException(nameof(extraTarget));

        while (_extraTrailTop > extraTarget)
        {
            ref var entry = ref _extraTrail[_extraTrailTop - 1];
            while (_bindingTrailTop > entry.BindingTrailMarker)
            {
                int idx = _bindingTrail[--_bindingTrailTop];
                _heap[idx] = Cell.UnboundVar(idx);
            }
            ProcessExtraUnwind(entry);
            _extraTrailTop--;
        }
        while (_bindingTrailTop > bindingTarget)
        {
            int idx = _bindingTrail[--_bindingTrailTop];
            _heap[idx] = Cell.UnboundVar(idx);
        }
    }

    private void ProcessExtraUnwind(in ExtraTrailEntry entry)
    {
        switch (entry.Type)
        {
            case TrailType.ValueChange:
                _heap[entry.HeapIdx] = entry.OldValue;
                break;
            case TrailType.BigIntAlloc:
                // entry.HeapIdx holds the table size *before* the allocation
                // that this trail entry records. Drop everything appended
                // since (in this case, exactly the one slot) so the table
                // is back to its pre-allocation shape.
                if (_bigIntTable.Count > entry.HeapIdx)
                    _bigIntTable.RemoveRange(entry.HeapIdx, _bigIntTable.Count - entry.HeapIdx);
                break;
            case TrailType.AttrModify:
                // entry.HeapIdx indexes _attrTrailLog, which records the
                // (attvar home, module, previous value) of one attribute
                // mutation. Restore the previous value, or remove the
                // module entirely when it was absent before (-1).
                {
                    var (home, mod, oldValue) = _attrTrailLog[entry.HeapIdx];
                    var record = _attrTable[home];
                    if (oldValue < 0) record.Remove(mod);
                    else record[mod] = oldValue;
                }
                break;
            case TrailType.CatchFrame:
                // Reverse a catch-frame stack operation. A push (recorded by
                // '$catch_begin') is undone by removing the top frame — by
                // the time this fires, every frame above it has already
                // been removed by its own entry. A deactivate (recorded by
                // '$catch_end') is undone by re-activating that frame, so
                // backtracking into a guarded goal restores its catcher.
                if (entry.OldValue.Data == CatchTrailPush)
                {
                    _catchFrames.RemoveAt(_catchFrames.Count - 1);
                }
                else
                {
                    CatchFrame f = _catchFrames[entry.HeapIdx];
                    f.Active = true;
                    _catchFrames[entry.HeapIdx] = f;
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"Unwind not yet implemented for TrailType.{entry.Type}.");
        }
    }

    // ----- Unify -----

    /// <summary>
    /// Unifies the cells at <paramref name="aIdx"/> and <paramref name="bIdx"/>, dereferencing
    /// each first. Returns <c>true</c> on success, <c>false</c> on failure. On failure the
    /// caller is responsible for unwinding the trail back to the pre-unify state.
    /// </summary>
    public bool Unify(int aIdx, int bIdx)
    {
        Profiler.Unify();
        int aAddr = Deref(aIdx);
        int bAddr = Deref(bIdx);
        if (aAddr == bAddr) return true;

        Cell aCell = _heap[aAddr];
        Cell bCell = _heap[bAddr];

        // Attributed variables (chunk 77) participate in cross-tag
        // unification, so dispatch them before the plain-REF handling.
        if (aCell.Tag == Tag.AttVar || bCell.Tag == Tag.AttVar)
            return UnifyAttVar(aAddr, aCell, bAddr, bCell);

        if (aCell.Tag == Tag.Ref)
        {
            if (bCell.Tag == Tag.Ref)
                BindVarToVar(aAddr, bAddr);
            else
                BindVarToValue(aAddr, bAddr, bCell);
            return true;
        }
        if (bCell.Tag == Tag.Ref)
        {
            BindVarToValue(bAddr, aAddr, aCell);
            return true;
        }

        // PSTR participates in cross-tag unification (with LIS and the [] atom in particular),
        // so it must be dispatched before the strict tag-equality check.
        if (aCell.Tag == Tag.Pstr) return UnifyPstr(aAddr, bAddr);
        if (bCell.Tag == Tag.Pstr) return UnifyPstr(bAddr, aAddr);

        // Both bound, neither REF, neither PSTR.
        if (aCell.Tag != bCell.Tag) return false;
        return aCell.Tag switch
        {
            Tag.Atom => aCell.AsAtomId == bCell.AsAtomId,
            Tag.Int => aCell.AsInt == bCell.AsInt,
            Tag.Str => UnifyStr(aCell.AsHeapIndex, bCell.AsHeapIndex),
            Tag.Lis => UnifyLis(aCell.AsHeapIndex, bCell.AsHeapIndex),
            Tag.BigInt => _bigIntTable[aCell.AsBigIntId].Equals(_bigIntTable[bCell.AsBigIntId]),
            Tag.String => string.Equals(_stringTable[aCell.AsStringId], _stringTable[bCell.AsStringId]),
            Tag.Foreign => ReferenceEquals(_foreignTable[aCell.AsForeignId], _foreignTable[bCell.AsForeignId]),
            Tag.Float => UnifyFloat(aCell, bCell),
            _ => throw new InvalidOperationException($"Unify reached cell with unexpected tag {aCell.Tag}."),
        };
    }

    /// <summary>
    /// Cell-based unification (ADR-017). Unifies two operand cells taken
    /// directly from registers / Y-slots / bytecode literals, WITHOUT first
    /// copying an inline compound (Str/Lis) to a heap address — which is what
    /// the materialise-then-<see cref="Unify(int,int)"/> entry points used to
    /// do, re-copying a register-held structure on every <c>get_value</c> and
    /// quadratically under backtracking (the zebra regression that blocked
    /// ADR-017 phase 2). The structure's functor/args already live on the
    /// heap; only its tag rides in the operand cell, so binding a variable to
    /// it copies just the one tag cell into the variable's existing home
    /// (zero allocation), and unifying two compounds recurses on their heap
    /// args via the address-based path. Attributed variables and partial
    /// strings — which genuinely need both operands at heap addresses — fall
    /// back to materialise-then-Unify (rare; an inline operand there is
    /// copied once).
    /// </summary>
    private bool UnifyCells(Cell ca, Cell cb)
    {
        var (a, aAddr) = ResolveOperand(ca);
        var (b, bAddr) = ResolveOperand(cb);
        if (aAddr >= 0 && aAddr == bAddr) return true;

        // AttVar / PSTR need heap addresses for both sides — defer to the
        // address-based path, materialising an inline operand if necessary.
        if (a.Tag is Tag.AttVar or Tag.Pstr || b.Tag is Tag.AttVar or Tag.Pstr)
        {
            int ha = aAddr >= 0 ? aAddr : MaterializeCell(a);
            int hb = bAddr >= 0 ? bAddr : MaterializeCell(b);
            return Unify(ha, hb);
        }

        if (a.Tag == Tag.Ref)            // a is an unbound variable at aAddr
        {
            if (b.Tag == Tag.Ref) BindVarToVar(aAddr, bAddr);
            else BindVarToCellValue(aAddr, b, bAddr);
            return true;
        }
        if (b.Tag == Tag.Ref)            // b is an unbound variable at bAddr
        {
            BindVarToCellValue(bAddr, a, aAddr);
            return true;
        }

        // Both bound values.
        if (a.Tag != b.Tag) return false;
        return a.Tag switch
        {
            Tag.Atom => a.AsAtomId == b.AsAtomId,
            Tag.Int => a.AsInt == b.AsInt,
            Tag.Str => UnifyStr(a.AsHeapIndex, b.AsHeapIndex),
            Tag.Lis => UnifyLis(a.AsHeapIndex, b.AsHeapIndex),
            Tag.BigInt => _bigIntTable[a.AsBigIntId].Equals(_bigIntTable[b.AsBigIntId]),
            Tag.String => string.Equals(_stringTable[a.AsStringId], _stringTable[b.AsStringId]),
            Tag.Foreign => ReferenceEquals(_foreignTable[a.AsForeignId], _foreignTable[b.AsForeignId]),
            Tag.Float => UnifyFloat(a, b),
            _ => throw new InvalidOperationException($"UnifyCells reached unexpected tag {a.Tag}."),
        };
    }

    /// <summary>Resolves an operand cell to its effective (dereferenced) cell
    /// and heap home. A REF / ATTVAR is followed to its heap home (returning
    /// that address); any other cell is an inline value returned as-is with
    /// address -1 (its Str/Lis referents are reachable via its payload, so it
    /// needs no home of its own).</summary>
    private (Cell cell, int addr) ResolveOperand(Cell c)
    {
        if (c.Tag is Tag.Ref or Tag.AttVar)
        {
            int addr = Deref(c.AsHeapIndex);
            return (_heap[addr], addr);
        }
        return (c, -1);
    }

    /// <summary>Binds the unbound variable at <paramref name="varAddr"/> to a
    /// value cell. For a compound (Str/Lis) the variable receives a REF to the
    /// value's heap home when it has one, or the inline compound cell copied
    /// directly into its own home when it does not (zero allocation — the
    /// ADR-017 win); atomic values are copied in place.</summary>
    private void BindVarToCellValue(int varAddr, Cell value, int valueAddr)
    {
        if (value.Tag is Tag.Str or Tag.Lis)
            Bind(varAddr, valueAddr >= 0 ? Cell.Ref(valueAddr) : value);
        else
            Bind(varAddr, value);
    }

    /// <summary>Copies an inline value cell to a fresh heap slot and returns
    /// its address. Only used by <see cref="UnifyCells"/>'s AttVar/PSTR
    /// fallback for the rare case of an inline operand of that kind.</summary>
    private int MaterializeCell(Cell c)
    {
        int slot = AllocateHeap(1);
        _heap[slot] = c;
        return slot;
    }

    /// <summary>
    /// Unifies two compound (STR) terms. <paramref name="fA"/> and <paramref name="fB"/>
    /// are heap indices of FUNCTOR cells (the payloads of their containing STR cells).
    /// Fails fast if the functor ids differ; otherwise recurses on each argument cell.
    ///
    /// <para>The recursion uses the C# stack, which is sufficient for the typical
    /// (shallow) depths produced by Prolog parsing. Pathologically deep terms — most
    /// notably long cons-cell lists, which is precisely why PSTR exists for strings —
    /// would warrant a future switch to an explicit push-down list.</para>
    /// </summary>
    private bool UnifyStr(int fA, int fB)
    {
        int functorIdA = _heap[fA].AsFunctorId;
        int functorIdB = _heap[fB].AsFunctorId;
        if (functorIdA != functorIdB) return false;
        var (_, arity) = FunctorTable.Lookup(functorIdA);
        for (int i = 1; i <= arity; i++)
        {
            if (!Unify(fA + i, fB + i)) return false;
        }
        return true;
    }

    /// <summary>
    /// Unifies two cons cells. <paramref name="hA"/> and <paramref name="hB"/> are heap
    /// indices of head cells (the payloads of their containing LIS cells); the matching
    /// tail cells live immediately after.
    /// </summary>
    private bool UnifyLis(int hA, int hB)
    {
        if (!Unify(hA, hB)) return false;
        return Unify(hA + 1, hB + 1);
    }

    /// <summary>Cell-based occurs-check unification (ADR-017) — the
    /// occurs-check counterpart of <see cref="UnifyCells"/>. Var-to-var and
    /// both-bound cases unify the operand cells directly without
    /// materialising; only binding a variable to an inline compound needs the
    /// value at a heap address (for <see cref="OccursIn"/>), so it
    /// materialises that one operand — rare, since occurs-check unification is
    /// itself rare (<c>unify_with_occurs_check/2</c>).</summary>
    private bool UnifyCellsWithOccursCheck(Cell ca, Cell cb)
    {
        var (a, aAddr) = ResolveOperand(ca);
        var (b, bAddr) = ResolveOperand(cb);
        if (aAddr >= 0 && aAddr == bAddr) return true;

        if (a.Tag is Tag.AttVar or Tag.Pstr || b.Tag is Tag.AttVar or Tag.Pstr)
        {
            int ha = aAddr >= 0 ? aAddr : MaterializeCell(a);
            int hb = bAddr >= 0 ? bAddr : MaterializeCell(b);
            return UnifyWithOccursCheck(ha, hb);
        }

        if (a.Tag == Tag.Ref)            // a is an unbound variable at aAddr
        {
            if (b.Tag == Tag.Ref) { BindVarToVar(aAddr, bAddr); return true; }
            int hb = bAddr >= 0 ? bAddr : MaterializeCell(b);
            if (OccursIn(aAddr, hb)) return false;
            BindVarToValue(aAddr, hb, _heap[hb]);
            return true;
        }
        if (b.Tag == Tag.Ref)            // b is an unbound variable at bAddr
        {
            int ha = aAddr >= 0 ? aAddr : MaterializeCell(a);
            if (OccursIn(bAddr, ha)) return false;
            BindVarToValue(bAddr, ha, _heap[ha]);
            return true;
        }

        if (a.Tag != b.Tag) return false;
        return a.Tag switch
        {
            Tag.Atom => a.AsAtomId == b.AsAtomId,
            Tag.Int => a.AsInt == b.AsInt,
            Tag.Str => UnifyStrWithOccursCheck(a.AsHeapIndex, b.AsHeapIndex),
            Tag.Lis => UnifyLisWithOccursCheck(a.AsHeapIndex, b.AsHeapIndex),
            Tag.BigInt => _bigIntTable[a.AsBigIntId].Equals(_bigIntTable[b.AsBigIntId]),
            Tag.String => string.Equals(_stringTable[a.AsStringId], _stringTable[b.AsStringId]),
            Tag.Foreign => ReferenceEquals(_foreignTable[a.AsForeignId], _foreignTable[b.AsForeignId]),
            Tag.Float => UnifyFloat(a, b),
            _ => throw new InvalidOperationException($"UnifyCellsWithOccursCheck reached unexpected tag {a.Tag}."),
        };
    }

    /// <summary>
    /// The occurs-check variant of <see cref="Unify"/> — ISO §8.2.2's
    /// <c>unify_with_occurs_check/2</c>. Identical to <see cref="Unify"/>
    /// except that before binding a variable to a compound term, it
    /// verifies that the variable's heap cell does not occur anywhere
    /// inside that term; if it does, the unification fails. This rules
    /// out the cyclic terms plain <c>=/2</c> would build (e.g.
    /// <c>X = f(X)</c>), at the cost of one structural walk per bind.
    /// </summary>
    public bool UnifyWithOccursCheck(int aIdx, int bIdx)
    {
        int aAddr = Deref(aIdx);
        int bAddr = Deref(bIdx);
        if (aAddr == bAddr) return true;

        Cell aCell = _heap[aAddr];
        Cell bCell = _heap[bAddr];

        // Attributed variables follow the same hook path as the
        // standard unify — the occurs check is enforced before the
        // hook fires, identical to SWI's semantics.
        if (aCell.Tag == Tag.AttVar || bCell.Tag == Tag.AttVar)
        {
            // For attvars we fall back to the regular unify path; the
            // occurs-check still applies to any plain Ref binding the
            // hook hasn't intercepted. (Strict ISO occurs-check over
            // attvar hooks is not in the standard.)
            return UnifyAttVar(aAddr, aCell, bAddr, bCell);
        }

        if (aCell.Tag == Tag.Ref)
        {
            if (bCell.Tag == Tag.Ref)
            {
                // Var-to-var binding is safe — no cycle possible.
                BindVarToVar(aAddr, bAddr);
                return true;
            }
            if (OccursIn(aAddr, bAddr)) return false;
            BindVarToValue(aAddr, bAddr, bCell);
            return true;
        }
        if (bCell.Tag == Tag.Ref)
        {
            if (OccursIn(bAddr, aAddr)) return false;
            BindVarToValue(bAddr, aAddr, aCell);
            return true;
        }

        if (aCell.Tag == Tag.Pstr) return UnifyPstr(aAddr, bAddr);
        if (bCell.Tag == Tag.Pstr) return UnifyPstr(bAddr, aAddr);

        if (aCell.Tag != bCell.Tag) return false;
        return aCell.Tag switch
        {
            Tag.Atom => aCell.AsAtomId == bCell.AsAtomId,
            Tag.Int => aCell.AsInt == bCell.AsInt,
            Tag.Str => UnifyStrWithOccursCheck(aCell.AsHeapIndex, bCell.AsHeapIndex),
            Tag.Lis => UnifyLisWithOccursCheck(aCell.AsHeapIndex, bCell.AsHeapIndex),
            Tag.BigInt => _bigIntTable[aCell.AsBigIntId].Equals(_bigIntTable[bCell.AsBigIntId]),
            Tag.String => string.Equals(_stringTable[aCell.AsStringId], _stringTable[bCell.AsStringId]),
            Tag.Foreign => ReferenceEquals(_foreignTable[aCell.AsForeignId], _foreignTable[bCell.AsForeignId]),
            Tag.Float => UnifyFloat(aCell, bCell),
            _ => throw new InvalidOperationException($"UnifyWithOccursCheck reached cell with unexpected tag {aCell.Tag}."),
        };
    }

    private bool UnifyStrWithOccursCheck(int fA, int fB)
    {
        int functorIdA = _heap[fA].AsFunctorId;
        int functorIdB = _heap[fB].AsFunctorId;
        if (functorIdA != functorIdB) return false;
        var (_, arity) = FunctorTable.Lookup(functorIdA);
        for (int i = 1; i <= arity; i++)
            if (!UnifyWithOccursCheck(fA + i, fB + i)) return false;
        return true;
    }

    private bool UnifyLisWithOccursCheck(int hA, int hB)
    {
        if (!UnifyWithOccursCheck(hA, hB)) return false;
        return UnifyWithOccursCheck(hA + 1, hB + 1);
    }

    /// <summary>True iff the variable cell at <paramref name="targetAddr"/>
    /// is structurally reachable from the (dereferenced) value at
    /// <paramref name="sourceAddr"/>. Walks the source term iteratively
    /// over an explicit stack so deep / long-list structures do not
    /// overflow C# recursion.</summary>
    private bool OccursIn(int targetAddr, int sourceAddr)
    {
        var stack = new Stack<int>();
        stack.Push(sourceAddr);
        while (stack.Count > 0)
        {
            int addr = Deref(stack.Pop());
            if (addr == targetAddr) return true;
            Cell c = _heap[addr];
            switch (c.Tag)
            {
                case Tag.Str:
                {
                    int fIdx = c.AsHeapIndex;
                    int functorId = _heap[fIdx].AsFunctorId;
                    var (_, arity) = FunctorTable.Lookup(functorId);
                    for (int i = 1; i <= arity; i++) stack.Push(fIdx + i);
                    break;
                }
                case Tag.Lis:
                {
                    int hIdx = c.AsHeapIndex;
                    stack.Push(hIdx);
                    stack.Push(hIdx + 1);
                    break;
                }
                case Tag.Pstr:
                {
                    // PSTR characters are immediate ints — only the
                    // logical tail can carry a variable.
                    int tailIdx = ComputePstrTailIndex(c);
                    stack.Push(tailIdx);
                    break;
                }
                case Tag.AttVar:
                    // An attvar is a kind of variable; the comparison
                    // by address above covers the identity case.
                    // Attribute pairs live in a side table, not the
                    // heap, so we don't traverse them here — matching
                    // the standard occurs-check semantics.
                    break;
                // Atoms, Ints, Floats, BigInts, Strings, Foreigns: leaves.
            }
        }
        return false;
    }

    /// <summary>
    /// Unifies two FLOAT-tagged cells by reconstructing the double from each header and
    /// its paired INT cell and comparing the raw bit patterns. Bit-exact comparison gives
    /// NaN unifying with NaN (consistent with SWI-Prolog's <c>=/2</c>) and keeps +0.0 and
    /// -0.0 distinct (their bits differ even though <c>==</c> returns true).
    /// </summary>
    private bool UnifyFloat(Cell aHeader, Cell bHeader)
    {
        double a = Cell.DecodeFloat(aHeader, _heap[aHeader.FloatPairedIndex]);
        double b = Cell.DecodeFloat(bHeader, _heap[bHeader.FloatPairedIndex]);
        return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    }

    /// <summary>
    /// Unifies a PSTR (at <paramref name="pstrAddr"/>) with any other dereferenced cell.
    /// An empty PSTR is logically just its tail value; non-empty PSTRs can only match a
    /// LIS (character-by-character decomposition) or another PSTR. Anything else fails —
    /// including the empty-list atom against a non-empty PSTR.
    /// </summary>
    private bool UnifyPstr(int pstrAddr, int otherAddr)
    {
        Cell pstrHdr = _heap[pstrAddr];
        int length = pstrHdr.AsPstrLength;

        if (length == 0)
        {
            int tailIdx = ComputePstrTailIndex(pstrHdr);
            return Unify(tailIdx, otherAddr);
        }

        Cell otherCell = _heap[otherAddr];
        return otherCell.Tag switch
        {
            Tag.Pstr => UnifyPstrPstr(pstrAddr, otherAddr),
            Tag.Lis => UnifyPstrLis(pstrAddr, otherAddr),
            _ => false,
        };
    }

    /// <summary>
    /// Unifies two PSTRs. Compares the first <c>min(aLen, bLen)</c> code units pairwise;
    /// on equal length, the stored tail values are unified directly. On a length mismatch,
    /// the longer PSTR is sliced from the shorter's length onward (a virtual header is
    /// materialised on the heap pointing at the same buffer) and that slice is unified
    /// with the shorter PSTR's tail.
    /// </summary>
    private bool UnifyPstrPstr(int aAddr, int bAddr)
    {
        Cell aHdr = _heap[aAddr];
        Cell bHdr = _heap[bAddr];
        int aLen = aHdr.AsPstrLength;
        int bLen = bHdr.AsPstrLength;

        if (aLen > bLen)
        {
            (aAddr, bAddr) = (bAddr, aAddr);
            (aHdr, bHdr) = (bHdr, aHdr);
            (aLen, bLen) = (bLen, aLen);
        }

        for (int i = 0; i < aLen; i++)
        {
            if (GetPstrCodeUnit(aHdr, i) != GetPstrCodeUnit(bHdr, i))
                return false;
        }

        int aTailIdx = ComputePstrTailIndex(aHdr);

        if (aLen == bLen)
        {
            int bTailIdx = ComputePstrTailIndex(bHdr);
            return Unify(aTailIdx, bTailIdx);
        }

        // Build a virtual slice of B starting at position aLen. The slice points at the
        // same buffer (no copy) and shares B's tail position by layout.
        int absoluteStart = bHdr.AsPstrOffset + aLen;
        int sliceBufferIdx = bHdr.AsPstrBufferIndex + absoluteStart / Cell.PstrCodeUnitsPerBuffer;
        int sliceOffset = absoluteStart % Cell.PstrCodeUnitsPerBuffer;
        int sliceLength = bLen - aLen;

        int sliceSlot = AllocateHeap(1);
        _heap[sliceSlot] = Cell.Pstr(sliceLength, sliceBufferIdx, sliceOffset);
        return Unify(aTailIdx, sliceSlot);
    }

    /// <summary>
    /// Unifies a non-empty PSTR with a cons cell. Decomposes the first code unit as
    /// <c>Int(cu)</c>, then recurses on the LIS tail with either the PSTR's stored tail
    /// (length 1) or a virtual one-shorter PSTR slice.
    ///
    /// <para>Heads are emitted as 16-bit UTF-16 code units; supplementary codepoints
    /// (above U+FFFF) appear as two separate surrogate values rather than one combined
    /// codepoint. This is enough for the BMP-only grammar workloads that motivate Phase 1;
    /// surrogate-pair fusion is a future refinement.</para>
    /// </summary>
    private bool UnifyPstrLis(int pstrAddr, int lisAddr)
    {
        Cell pstrHdr = _heap[pstrAddr];
        int length = pstrHdr.AsPstrLength;
        int firstUnit = GetPstrCodeUnit(pstrHdr, 0);

        int lisHeadIdx = _heap[lisAddr].AsHeapIndex;
        int lisTailIdx = lisHeadIdx + 1;

        // Unify the head: a fresh Int cell against the LIS's head slot.
        int headSlot = AllocateHeap(1);
        _heap[headSlot] = Cell.Int(firstUnit);
        if (!Unify(headSlot, lisHeadIdx)) return false;

        if (length == 1)
        {
            int origTailIdx = ComputePstrTailIndex(pstrHdr);
            return Unify(origTailIdx, lisTailIdx);
        }

        int absoluteStart = pstrHdr.AsPstrOffset + 1;
        int newBufferIdx = pstrHdr.AsPstrBufferIndex + absoluteStart / Cell.PstrCodeUnitsPerBuffer;
        int newOffset = absoluteStart % Cell.PstrCodeUnitsPerBuffer;

        int sliceSlot = AllocateHeap(1);
        _heap[sliceSlot] = Cell.Pstr(length - 1, newBufferIdx, newOffset);
        return Unify(sliceSlot, lisTailIdx);
    }

    /// <summary>Young-to-old binding of two unbound variables (ADR-004). The variable with the
    /// higher heap index is bound to a REF pointing at the older one, so the bound cell will
    /// be discarded automatically if the heap is later truncated past the older variable.</summary>
    private void BindVarToVar(int aAddr, int bAddr)
    {
        if (aAddr == bAddr) return;
        if (aAddr < bAddr)
            Bind(bAddr, Cell.Ref(aAddr));
        else
            Bind(aAddr, Cell.Ref(bAddr));
    }

    /// <summary>Unifies when at least one side is an attributed
    /// variable (chunk 77). Three cases:
    /// <list type="bullet">
    /// <item>attvar + plain unbound REF — the plain variable binds to
    /// the attvar, which survives carrying its attributes.</item>
    /// <item>attvar + attvar — the younger binds to the older and the
    /// younger's attributes merge onto the older's record; a module
    /// present on both must have unifiable values.</item>
    /// <item>attvar + bound value — the attvar binds to the value, and
    /// a wakeup is queued so the next goal boundary runs the module's
    /// <c>verify_attributes/4</c> hook (chunk 79).</item>
    /// </list></summary>
    private bool UnifyAttVar(int aAddr, Cell aCell, int bAddr, Cell bCell)
    {
        bool aAtt = aCell.Tag == Tag.AttVar;
        bool bAtt = bCell.Tag == Tag.AttVar;

        if (aAtt && bAtt)
        {
            int olderAddr = aAddr < bAddr ? aAddr : bAddr;
            int youngerAddr = aAddr < bAddr ? bAddr : aAddr;
            if (!MergeAttributes(youngerAddr, olderAddr)) return false;
            // The younger variable is the one being bound (to the
            // older); queue its modules' hooks with the older variable
            // as the "other" term. (chunk 78)
            QueueAttrWakeups(youngerAddr, olderAddr);
            BindAttVarToValue(youngerAddr, olderAddr, Cell.Ref(olderAddr));
            return true;
        }

        int attAddr = aAtt ? aAddr : bAddr;
        int otherAddr = aAtt ? bAddr : aAddr;
        Cell otherCell = aAtt ? bCell : aCell;

        if (otherCell.Tag == Tag.Ref)
        {
            // Plain unbound variable binds to the attvar — the attvar
            // (with its attributes) survives. Nothing is bound to a
            // value, so no hook fires; a normal trailed Bind is right.
            Bind(otherAddr, Cell.Ref(attAddr));
            return true;
        }

        // attvar + bound value: queue the attvar's modules' hooks (with
        // the value as the "other" term), then bind. The interpreter
        // runs the queued hooks at the next goal boundary.
        QueueAttrWakeups(attAddr, otherAddr);
        BindAttVarToValue(attAddr, otherAddr, otherCell);
        return true;
    }

    /// <summary>Queues one unify-hook wakeup per attribute module
    /// carried by the attributed variable at
    /// <paramref name="attvarHome"/>, recording the term it was bound
    /// to (<paramref name="otherIdx"/>). A no-op when the variable
    /// carries no attributes. (chunk 79)</summary>
    private void QueueAttrWakeups(int attvarHome, int otherIdx)
    {
        if (!_attrTable.TryGetValue(attvarHome, out var record)) return;
        foreach (var (moduleId, attrValueIdx) in record)
            _pendingWakeups.Add((moduleId, attrValueIdx, otherIdx));
    }

    /// <summary>True when attribute hooks are queued and waiting to run.
    /// The interpreter checks this at every goal boundary.</summary>
    public bool HasPendingWakeups => _pendingWakeups.Count > 0;

    /// <summary>Set by the bytecode interpreter so Tier-1 IL code can run
    /// pending <c>verify_attributes</c> wakeups through the interpreter's
    /// goal-running machinery (which the IL delegate, holding only an
    /// <see cref="Engine"/>, cannot reach directly). Returns false when a
    /// hook failed.</summary>
    internal Func<bool>? Tier1WakeupFlusher { get; set; }

    /// <summary>Tier-1 IL cut support (Phase 28). A cut is a goal boundary:
    /// any wakeup queued by the IL clause body (e.g. binding a clpfd attvar
    /// in the head, then a neck cut) must run BEFORE the cut commits, or a
    /// failing constraint has no surviving choice point to backtrack into —
    /// the same unsoundness fixed for the bytecode interpreter in chunk 335.
    /// The IL emit calls this immediately before <see cref="NeckCut"/> /
    /// <see cref="CutToLevel"/>; a false result means a wakeup failed and the
    /// caller must branch to its fail label instead of cutting. The
    /// <c>_pendingWakeups.Count</c> fast path keeps the overwhelmingly common
    /// no-wakeup case (every non-attvar program) to a single field read.
    ///
    /// <para>FUTURE (deferred, kept on purpose): this runtime guard costs
    /// ~1-2.5 ns per IL cut even when no attribute hook exists. It could be
    /// elided entirely by gating the IL EMISSION on
    /// <see cref="HasVerifyAttributesHook"/> at promotion time (zero cost for
    /// non-attvar IL programs). That was NOT done because it needs a
    /// soundness-critical invariant — the IL promotion cache must be
    /// invalidated whenever ANY consult first defines
    /// <c>verify_attributes/4</c> (not just UseClpfd/UseClpr), or a predicate
    /// promoted before a custom hook loaded would silently skip the flush. The
    /// ~ns-per-cut cost is below the wall-clock noise floor and only applies to
    /// opt-in Tier-1 IL, so the simple always-on runtime guard wins for now.</para>
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool FlushWakeupsForIlCut()
    {
        if (_pendingWakeups.Count == 0) return true;
        return Tier1WakeupFlusher is null || Tier1WakeupFlusher();
    }

    /// <summary>Discards every queued wakeup. Called by the interpreter
    /// on backtracking — wakeups belong to the abandoned computation.</summary>
    public void ClearPendingWakeups() => _pendingWakeups.Clear();

    /// <summary>Returns the queued wakeups and empties the queue. The
    /// interpreter drains them at a goal boundary (chunk 80), building a
    /// <c>verify_attributes/4</c> goal per entry and meta-calling it in
    /// this (live) engine so the hooks see the real attributed
    /// variables.</summary>
    public IReadOnlyList<(int Module, int AttrValueIdx, int OtherIdx)> TakePendingWakeups()
    {
        var taken = _pendingWakeups.ToArray();
        _pendingWakeups.Clear();
        return taken;
    }

    /// <summary>Binds the attributed variable at <paramref name="attAddr"/>
    /// to <paramref name="valueCell"/>. Unlike a plain bind this trails
    /// a <see cref="TrailType.ValueChange"/> carrying the original
    /// ATTVAR cell, so backtracking restores the attributed variable
    /// (not a bare unbound REF).</summary>
    private void BindAttVarToValue(int attAddr, int valueAddr, Cell valueCell)
    {
        Cell newCell = valueCell.Tag is Tag.Str or Tag.Lis or Tag.Pstr
            ? Cell.Ref(valueAddr)
            : valueCell;
        TrailValueChange(attAddr, _heap[attAddr]);
        _heap[attAddr] = newCell;
    }

    /// <summary>Copies every attribute of the attributed variable at
    /// <paramref name="fromAddr"/> onto the one at <paramref name="toAddr"/>.
    /// A module the destination lacks is added outright. A module both
    /// carry is resolved differently depending on whether the program
    /// defines a <c>verify_attributes/4</c> hook:
    /// <list type="bullet">
    /// <item>No hook — chunk 77's hookless merge rule: the two values
    /// must unify, or the whole unification fails.</item>
    /// <item>Hook present — the destination keeps its own value and the
    /// hook (run from the queued wakeup) owns the merge. Pre-unifying
    /// here would fail before the hook could run, which is wrong for a
    /// constraint library whose variables carry different domains.</item>
    /// </list></summary>
    private bool MergeAttributes(int fromAddr, int toAddr)
    {
        int fromHome = Deref(fromAddr);
        int toHome = Deref(toAddr);
        if (_heap[fromHome].Tag != Tag.AttVar || _heap[toHome].Tag != Tag.AttVar)
            return true;
        var fromRecord = _attrTable[fromHome];
        var toRecord = _attrTable[toHome];
        bool hasHook = HasVerifyAttributesHook;
        // Snapshot the source modules: the Unify below can't mutate
        // fromRecord, but iterating a dictionary we may also be reading
        // is fragile — copy the pairs first.
        foreach (var (moduleId, fromValueIdx) in fromRecord.ToArray())
        {
            int toValueIdx = toRecord.TryGetValue(moduleId, out int v) ? v : -1;
            if (toValueIdx < 0)
            {
                TrailAttrChange(toHome, moduleId, -1);
                toRecord[moduleId] = fromValueIdx;
            }
            else if (hasHook)
            {
                // The hook owns this shared module's merge — leave the
                // destination value untouched; the queued wakeup runs
                // verify_attributes/4, which reads both sides.
            }
            else if (!Unify(toValueIdx, fromValueIdx))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Binds an unbound variable to a bound value. For compound-valued cells
    /// (STR / LIS / PSTR) the variable receives a REF pointing at the value's heap
    /// position; for atomic values the cell is copied in place (ADR-002 binding policy).</summary>
    private void BindVarToValue(int varAddr, int valueAddr, Cell valueCell)
    {
        if (valueCell.Tag is Tag.Str or Tag.Lis or Tag.Pstr)
            Bind(varAddr, Cell.Ref(valueAddr));
        else
            Bind(varAddr, valueCell);
    }

    // ----- Capacity management -----

    private void EnsureHeapCapacity(int extra)
    {
        if (Profiler.Enabled)
        {
            int before = _heap.Length;
            GrowIfNeeded(ref _heap, _heapTop, extra, _config.MaxHeapSize, "heap");
            if (_heap.Length != before)
            {
                var (cps, floor) = DiagnoseCpFloor();
                System.Console.Error.WriteLine(
                    $"[heapgrow] cap={_heap.Length:N0} heapTop={_heapTop:N0} "
                    + $"cps={cps} bottomCpHeapTop={floor:N0} trappedAboveFloor={_heapTop - floor:N0}");
            }
            return;
        }
        GrowIfNeeded(ref _heap, _heapTop, extra, _config.MaxHeapSize, "heap");
    }

    /// <summary>Diagnostic — walks the choice-point chain and returns the
    /// count of live CPs and the saved <c>HeapTop</c> of the oldest
    /// (bottom-most) one. Backtracking can never reclaim heap below that
    /// floor without failing the whole query, so a low, stable floor while
    /// <see cref="HeapTop"/> balloons signals heap garbage pinned by a
    /// long-lived choice point.</summary>
    private (int Count, int BottomHeapTop) DiagnoseCpFloor()
    {
        int b = _b;
        int count = 0;
        int floor = _heapTop;
        while (b >= 0)
        {
            int arity = (int)_stack[b + CpArityOffset].Data;
            floor = (int)_stack[b + CpHeapTopOffset(arity)].Data;
            count++;
            int prevB = (int)_stack[b + CpBOffset(arity)].Data;
            if (prevB == b) break;
            b = prevB;
        }
        return (count, floor);
    }

    private void EnsureStackCapacity(int extra)
        => GrowIfNeeded(ref _stack, _stackTop, extra, _config.MaxStackSize, "stack");

    private void EnsureBindingTrailCapacity(int extra)
        => GrowIfNeeded(ref _bindingTrail, _bindingTrailTop, extra, _config.MaxBindingTrailSize, "binding trail");

    private void EnsureExtraTrailCapacity(int extra)
        => GrowIfNeeded(ref _extraTrail, _extraTrailTop, extra, _config.MaxExtraTrailSize, "extra trail");

    private static void GrowIfNeeded<T>(ref T[] buffer, int top, int extra, int maxSize, string name)
    {
        long required = (long)top + extra;
        if (required <= buffer.Length) return;

        long newSize = buffer.Length;
        while (newSize < required) newSize *= 2;
        if (maxSize > 0 && newSize > maxSize)
        {
            if (required > maxSize)
                throw new InvalidOperationException($"Engine {name} overflow: would need {required} cells, max is {maxSize}.");
            newSize = maxSize;
        }
        if (newSize > int.MaxValue)
            throw new InvalidOperationException($"Engine {name} overflow: would exceed int.MaxValue.");
        Profiler.Realloc(name, (long)newSize * System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        Array.Resize(ref buffer, (int)newSize);
    }

    // ----- Internal / test hooks -----

    /// <summary>Sets <see cref="Hb"/>, the heap-top boundary used by the
    /// young-to-old binding rule. Setting <c>Hb</c> equal to the current
    /// <see cref="HeapTop"/> makes every existing heap cell look "old", so any
    /// subsequent binding to an existing variable will be trailed — useful
    /// when a builtin performs a trial unification and needs the bindings to
    /// be reversible via <see cref="UnwindTrails"/>.</summary>
    public void SetHb(int hb)
    {
        if (hb < 0 || hb > _heapTop) throw new ArgumentOutOfRangeException(nameof(hb));
        _hb = hb;
    }

    /// <summary>Backwards-compatible alias for <see cref="SetHb"/>, retained
    /// for the test code that referenced it before <c>SetHb</c> became
    /// public.</summary>
    internal void SetHbForTesting(int hb) => SetHb(hb);

    /// <summary>Shrinks (or grows back) the heap-top to <paramref name="newTop"/>.
    /// Builtins that perform a trial allocation and want to release the
    /// heap range on rollback use this together with <see cref="UnwindTrails"/>.
    /// Growing past the current top is rejected — cells beyond the top are
    /// not initialised, and growing here would expose them.</summary>
    public void SetHeapTop(int newTop)
    {
        if (newTop < 0 || newTop > _heapTop)
            throw new ArgumentOutOfRangeException(nameof(newTop),
                $"newTop {newTop} must be in [0, {_heapTop}].");
        _heapTop = newTop;
    }

    /// <summary>Returns true if the two cells are structurally identical — same
    /// shape, same atom/integer values, same variable identities (an unbound
    /// REF is equal to another unbound REF only when they point at the same
    /// heap cell). Used by <c>==/2</c> and <c>\==/2</c>: unlike unification,
    /// this never binds anything.</summary>
    public bool AreStructurallyEqual(Cell a, Cell b)
    {
        // Resolve each cell: follow REFs to their dereference target, and
        // keep the dereferenced REF (a Cell.Ref pointing at the final heap
        // address) when the chain terminates at an unbound variable. This
        // lets two unbound vars compare equal iff they're the same heap cell.
        a = ResolveForStructuralCompare(a);
        b = ResolveForStructuralCompare(b);

        if (a.Tag != b.Tag) return false;
        return a.Tag switch
        {
            Tag.Ref => a.AsHeapIndex == b.AsHeapIndex,
            Tag.Atom => a.AsAtomId == b.AsAtomId,
            Tag.Int => a.AsInt == b.AsInt,
            Tag.Float => Cell.DecodeFloat(a, _heap[a.FloatPairedIndex])
                      == Cell.DecodeFloat(b, _heap[b.FloatPairedIndex]),
            Tag.Functor => a.AsFunctorId == b.AsFunctorId,
            Tag.Str => AreStrStructurallyEqual(a.AsHeapIndex, b.AsHeapIndex),
            Tag.Lis => AreLisStructurallyEqual(a.AsHeapIndex, b.AsHeapIndex),
            // Foreign cells (chunk 140): identity via the underlying
            // .NET reference. Two foreign cells are == iff their
            // boxed payloads are reference-equal.
            Tag.Foreign => ReferenceEquals(
                _foreignTable[a.AsForeignId], _foreignTable[b.AsForeignId]),
            // BigInt: value equality.
            Tag.BigInt => _bigIntTable[a.AsBigIntId].Equals(_bigIntTable[b.AsBigIntId]),
            // String literal: value equality.
            Tag.String => string.Equals(
                _stringTable[a.AsStringId], _stringTable[b.AsStringId]),
            // PSTR: delegate to the unification-style comparator; for
            // structural equality two PSTRs match iff their character
            // sequences and tails do.
            Tag.Pstr => AreStrStructurallyEqual(a.AsHeapIndex, b.AsHeapIndex),
            _ => throw new NotSupportedException(
                $"AreStructurallyEqual: tag {a.Tag} is not yet supported."),
        };
    }

    private Cell ResolveForStructuralCompare(Cell c)
    {
        // An attributed variable (chunk 77) is still a variable: it
        // normalizes to a REF at its home address — its payload already
        // *is* that address — so == compares it by identity, like any
        // unbound variable. This also handles a bare ATTVAR cell read
        // straight out of a structure-argument slot.
        if (c.Tag == Tag.AttVar) return Cell.Ref(c.AsHeapIndex);
        if (c.Tag != Tag.Ref) return c;
        int addr = Deref(c.AsHeapIndex);
        Cell target = _heap[addr];
        return target.Tag is Tag.Ref or Tag.AttVar ? Cell.Ref(addr) : target;
    }

    private bool AreStrStructurallyEqual(int aFunctorIdx, int bFunctorIdx)
    {
        int aFunctorId = _heap[aFunctorIdx].AsFunctorId;
        int bFunctorId = _heap[bFunctorIdx].AsFunctorId;
        if (aFunctorId != bFunctorId) return false;
        var (_, arity) = FunctorTable.Lookup(aFunctorId);
        for (int i = 1; i <= arity; i++)
            if (!AreStructurallyEqual(_heap[aFunctorIdx + i], _heap[bFunctorIdx + i]))
                return false;
        return true;
    }

    private bool AreLisStructurallyEqual(int aHeadIdx, int bHeadIdx) =>
        AreStructurallyEqual(_heap[aHeadIdx], _heap[bHeadIdx])
        && AreStructurallyEqual(_heap[aHeadIdx + 1], _heap[bHeadIdx + 1]);

    /// <summary>Sets <c>CP</c> directly. The interpreter uses this from the <c>call</c>
    /// instruction; tests use it to seed the engine state before running a fragment.
    /// Chunk 192: public so chunk-71 persisted-IL assemblies (loaded into the
    /// process without InternalsVisibleTo) can call it from emitted IL.</summary>
    public void SetCp(int cp) => _cp = cp;

    /// <summary>Sets <c>PC</c> directly. Used by the interpreter for jumps
    /// (<c>execute</c>, <c>proceed</c>) and by Run for the initial entry point.
    /// Chunk 192: public so chunk-71 persisted-IL assemblies (loaded into the
    /// process without InternalsVisibleTo) can call it from emitted IL.</summary>
    public void SetPc(int pc) => _p = pc;

    /// <summary>Advances <c>PC</c> by <paramref name="delta"/> bytes. Used by the
    /// interpreter to step past straight-line instructions.</summary>
    internal void AdvancePc(int delta) => _p += delta;

    /// <summary>Chunk 218 — the address a backtrackable builtin's CP
    /// resume should jump to after a successful retry. Set by the caller
    /// just before invoking <c>entry.Impl</c>:
    /// <list type="bullet">
    /// <item>Tier-0 sets it to the post-<c>call_builtin</c> address
    ///   (<c>pc + 9</c>) — the next bytecode instruction.</item>
    /// <item>Tier-1 IL sets it to a resume marker that the dispatcher
    ///   decodes back to the IL caller (chunk 218).</item>
    /// </list>
    /// Builtins that call <see cref="ResumeAtReturnPc"/> from inside a
    /// CP-resume delegate must capture this value at push time (was
    /// previously <c>engine.P + 9</c>, which only worked under Tier-0
    /// because Pc happened to be the <c>call_builtin</c> opcode addr;
    /// under Tier-1 Pc was stale and the resume landed mid-instruction).
    /// Public so persisted IL (loaded without InternalsVisibleTo) can
    /// set it from emitted code.</summary>
    public int BuiltinReturnPc { get; set; }

    /// <summary>Sets <c>B0</c> directly. The interpreter writes <c>_b</c> into this
    /// before any <c>call</c> or <c>execute</c> so the callee's <c>neck_cut</c> sees
    /// the right barrier. Chunk 192: public so chunk-71 persisted-IL assemblies
    /// (loaded into the process without InternalsVisibleTo) can call it from
    /// emitted IL.</summary>
    public void SetB0(int b0) => _b0 = b0;

    /// <summary>Sets the write/read mode flag directly. The interpreter writes this
    /// from get_structure/put_structure/get_list/put_list. Exposed for tests that
    /// exercise <c>unify_*</c> opcodes without first running an open instruction.</summary>
    internal void SetWriteMode(bool writeMode) => _writeMode = writeMode;

    /// <summary>Sets the unify pointer directly. Same usage pattern as
    /// <see cref="SetWriteMode"/>.</summary>
    internal void SetUnifyPointer(int idx) => _unifyPointer = idx;

    internal ReadOnlySpan<int> BindingTrailSpan => _bindingTrail.AsSpan(0, _bindingTrailTop);
}
