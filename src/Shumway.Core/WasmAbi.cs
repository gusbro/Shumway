namespace Shumway.Core;

/// <summary>What a compiled predicate hands back when it returns. The wasm
/// never calls managed code: when it needs something only the engine can do,
/// it writes what it needs into the mailbox and returns one of these, and the
/// C# wrapper does it and re-enters at a continuation cursor.</summary>
public enum WasmVerdict
{
    /// <summary>The predicate failed. The wrapper backtracks.</summary>
    Fail = 0,

    /// <summary>It succeeded and there is nothing left to do.</summary>
    Success = 1,

    /// <summary>It succeeded into a tail call: <see cref="WasmAbi.Pc"/> holds
    /// the target, which the wrapper turns into a pending tail call the way
    /// the IL tier's resume markers already do.</summary>
    SuccessTailCall = 2,

    /// <summary>A builtin has to run: <see cref="WasmAbi.BuiltinId"/> says
    /// which, its arguments are in the registers, and
    /// <see cref="WasmAbi.Cursor"/> says where to come back.</summary>
    BuiltinRequest = 3,

    /// <summary>A choice point has to be pushed (ADR-031's delayed forms push
    /// mid-body, so the push is a boundary anyway).</summary>
    PushChoicePoint = 4,

    /// <summary>A safe point was reached with work pending: the heap crossed
    /// its watermark, or the flags word says a wakeup or an interrupt is
    /// waiting. The wrapper collects / drains and re-enters at the cursor.
    /// </summary>
    Safepoint = 5,

    /// <summary>The compiled code met something it does not handle -- an
    /// attributed variable, an operand past the small-integer lane, a full
    /// trail -- and stepped aside: every scalar is synced and
    /// <see cref="WasmAbi.Pc"/> holds the BYTECODE address of the very
    /// instruction that stepped, so the interpreter continues there as if the
    /// predicate had never been compiled. The state is the engine's own
    /// arrays, which is what makes deoptimising this cheap.</summary>
    Deopt = 6,
}

/// <summary>The one contract between a compiled wasm predicate and the engine:
/// a MAILBOX of 64-bit slots at a known address, and an exported function
/// <c>(mailbox: i32, cursor: i32) -&gt; i32</c> whose result is a
/// <see cref="WasmVerdict"/>.
///
/// <para>The module sees two things and no more: the memory it imports, which
/// in the browser IS the runtime's own linear memory, and this mailbox inside
/// it. On every entry the wrapper writes fresh BASES (the addresses of the
/// engine's pinned arrays) and the WAM scalars into the mailbox, calls, and
/// copies the scalars back. The arrays can only be replaced by managed code,
/// and managed code only runs once the wasm has returned, so a base cannot go
/// stale while the wasm holds it. That is the plan's D2, and it is what
/// answers the open question ADR-042 left about the heap being a managed
/// array.</para>
///
/// <para>Slots are 8 bytes each so a base, a scalar and a flags word all read
/// with one <c>i64.load</c>. A base is an address inside the imported memory:
/// 32 bits of it are meaningful today, and the slot is 64 wide so a future
/// memory64 needs no layout change.</para></summary>
public static class WasmAbi
{
    // ---- bases: where the engine's arrays start, this entry ----
    public const int HeapBase = 0;
    public const int StackBase = 1;
    public const int RegistersBase = 2;
    public const int BindingTrailBase = 3;
    public const int ExtraTrailBase = 4;

    // ---- the WAM scalars, in and out ----
    /// <summary>H — the first free heap cell.</summary>
    public const int HeapTop = 5;
    /// <summary>The heap index at which the wasm must stop and let the engine
    /// collect. Compared on every back edge.</summary>
    public const int HeapWatermark = 6;
    /// <summary>E — the environment (stack) top.</summary>
    public const int StackTop = 7;
    /// <summary>B — the choice-point top.</summary>
    public const int ChoiceTop = 8;
    /// <summary>HB — the heap top as of the youngest choice point.</summary>
    public const int HeapBacktrack = 9;
    /// <summary>TR — the binding trail top.</summary>
    public const int TrailTop = 10;

    // ---- the channel for a bail ----
    /// <summary>Wakeups, interrupts, a cancellation request: anything the
    /// engine wants the wasm to notice without a call. Non-zero means "come
    /// out at the next back edge" (ADR-049's concern: a long loop must not
    /// swallow them).</summary>
    public const int Flags = 11;
    /// <summary>The tail-call target for <see cref="WasmVerdict.SuccessTailCall"/>.
    /// </summary>
    public const int Pc = 12;
    /// <summary>Which builtin, for <see cref="WasmVerdict.BuiltinRequest"/>.</summary>
    public const int BuiltinId = 13;
    /// <summary>Where to re-enter after the wrapper has done what the verdict
    /// asked for. Zero is a fresh entry.</summary>
    public const int Cursor = 14;

    // ---- the rest of the WAM scalars (the backend's working set) ----
    /// <summary>E -- the current environment frame.</summary>
    public const int EnvTop = 15;
    /// <summary>CP -- the continuation (a bytecode address or a resume
    /// marker).</summary>
    public const int ContinuationPc = 16;
    /// <summary>First stack index that does NOT fit: a frame or choice point
    /// that would cross it makes the code step aside instead.</summary>
    public const int StackLimit = 17;
    /// <summary>First binding-trail index that does not fit.</summary>
    public const int TrailLimit = 18;
    /// <summary>The extra trail's top: saved into every choice point, and a
    /// restore that would have to unwind it steps aside instead (nothing this
    /// backend compiles pushes extra entries).</summary>
    public const int ExtraTrailTop = 19;
    /// <summary>The logical-update view generation, saved into choice points.</summary>
    public const int ViewGen = 20;
    /// <summary>B0 -- the cut barrier, saved into choice points.</summary>
    public const int CutBarrier = 21;
    /// <summary>The unify machine's mode: non-zero while building (write),
    /// zero while matching (read). Synced so a step-aside mid-sequence
    /// resumes exactly.</summary>
    public const int WriteMode = 22;
    /// <summary>S -- the unify pointer.</summary>
    public const int UnifyPointer = 23;
    /// <summary>Base of the functor table's mirror in linear memory: one
    /// i64 per functor id, packed <c>(atomId &lt;&lt; 32) | arity</c> exactly
    /// as FunctorTable keeps it. The table is managed state the module
    /// cannot reach, and the mirror is deliberately an EXACT copy rather
    /// than the arity alone: a wasm-side value that diverges can then be
    /// named (this functor, that atom) instead of only counted.
    ///
    /// <para>An id the host has not mirrored reads as zero, whose arity is
    /// zero: a structure walk enqueues nothing and a sub-index hop misses.
    /// Both are the safe direction.</para></summary>
    public const int FunctorTableBase = 24;

    /// <summary>Diagnostic scratch: a step-aside site may leave the two
    /// values it compared here, so the host can see WHY it stepped aside
    /// rather than only where. Never read by compiled code.</summary>
    public const int DiagA = 25;
    public const int DiagB = 26;

    /// <summary>Goals dispatched and heap cells claimed INSIDE the module,
    /// added to the engine's own tallies when a chain closes. Without these
    /// the module is invisible to time/1: every call it makes and every cell
    /// it claims never reaches a managed counter, and a run that stays in
    /// wasm reports a handful of inferences for millions of goals. The heap
    /// count is allocations, not the top, so backtracking does not undo it.</summary>
    public const int GoalsRun = 27;
    public const int CellsClaimed = 28;

    /// <summary>Base of the RESUME TABLE: one i64 per resume marker, indexed
    /// by <c>marker - Activation.ResumeMarkerBase</c>.
    ///
    /// <para>A marker is already a dense id — <c>EncodeResumeMarker</c> interns
    /// the (functor, address) pair and hands back <c>Base + denseId</c> — so
    /// resolving one is a subscript, not a search. That replaces a linear chain
    /// of baked <c>if (bp == const)</c> comparisons, one per choice-point site
    /// in the module.</para>
    ///
    /// <para>Row layout: <c>((moduleId + 1) &lt;&lt; 32) | cursor</c>. Zero means
    /// the marker does not resolve HERE, which is the safe direction: the
    /// module returns the verdict and the host takes over, exactly as it did
    /// before there was a table.</para>
    ///
    /// <para>PER WORLD, never global. Functor ids and the marker pool are
    /// process-wide, but bytecode addresses belong to each engine's code space:
    /// two engines running the same program mint the SAME markers for DIFFERENT
    /// code. Today that is safe only because resolution is per world, and a
    /// shared table would quietly lose it.</para></summary>
    public const int ResumeTableBase = 29;
    /// <summary>Rows in the resume table. A marker at or past this is newer
    /// than the table and resolves to the host.</summary>
    public const int ResumeTableLength = 30;
    /// <summary>Base of the ATTRIBUTE TABLE's image: two i64 per slot, key
    /// <c>((home + 1) &lt;&lt; 32) | module</c> then the attribute value's heap
    /// index. Key 0 is an empty slot and ends a probe; -1 is a tombstone and
    /// does not.
    ///
    /// <para>Zero here means there is no image and get_attr/3 exits to the
    /// host, which is what it did before there was one -- the same safe
    /// direction an unwritten slot gives every other base.</para>
    ///
    /// <para>The host is the only writer. The module reading a row the store
    /// no longer holds would be unsound, so every mutation goes through the
    /// funnel in Activation.Attrs.cs and the image is rebuilt, never patched,
    /// when the heap collector moves every index at once.</para></summary>
    public const int AttrTableBase = 31;
    /// <summary>Slots minus one: the image is a power of two, so a probe
    /// wraps with an AND. Read only when the base is non-zero.</summary>
    public const int AttrTableMask = 33;

    /// <summary>Base of the moduleId -&gt; function-table index array the
    /// in-wasm hop reads: -1 for a module this thread has not registered.
    /// </summary>
    public const int ModuleIndexBase = 32;
    /// <summary>The module that produced the last verdict, written by every
    /// exit to the host and by nothing else. After in-wasm hops it is the only
    /// way the host can tell whose build space a pc is in.</summary>
    public const int CurrentModuleId = 34;

    /// <summary>Scratch for the emitter's DebugLoopGuard (off by default): a
    /// dispatch counter, the last cursor, and a limit the host may set. Kept
    /// here rather than at hand-picked indexes, which is how they came to
    /// overlap the diagnostic tallies once already.</summary>
    public const int DebugGuardLimit = 35;
    public const int DebugGuardCount = 36;
    public const int DebugGuardCursor = 37;
    /// <summary>Hops taken inside wasm during the chain (a module tail-calling
    /// another through the table). The only witness that a crossing stayed in
    /// wasm: a hop that silently went out to the host instead is correct and
    /// slow, and no answer-comparing test can tell.</summary>
    public const int HopCount = 38;

    /// <summary>Base of the CALL MARKER table: one i32 per functor id, the
    /// resume marker of that functor's fresh entry, 0 for a functor no module
    /// covers. Zero base means no table, and a meta-call steps aside exactly
    /// as it did before there was one.
    ///
    /// <para>What it buys is the only thing a module could not do: call a
    /// goal whose functor it learns at RUN time. A marker is interned by the
    /// host, so it cannot be computed from a functor; with the marker in hand
    /// the module takes the ordinary resume probe.</para></summary>
    public const int CallMarkerBase = 39;
    /// <summary>Entries in the call-marker table. A functor id at or past
    /// this is newer than the table and steps aside.</summary>
    public const int CallMarkerLength = 40;

    /// <summary>Base of the META-CALL INLINE CACHE: two i64 per slot, key
    /// <c>((moduleAtom + 1) &lt;&lt; 32) | goalFunctor</c> then the RESOLVED
    /// functor. Zero base means no cache and a module-tagged meta-call steps
    /// aside, as it always did.
    ///
    /// <para>The resolved functor, not a marker: the module reads the marker
    /// from <see cref="CallMarkerBase"/> afterwards, so an eviction that
    /// zeroes that row invalidates this cache for free.</para></summary>
    public const int MetaCacheBase = 41;
    /// <summary>Slots minus one. Read only when the base is non-zero.</summary>
    public const int MetaCacheMask = 42;

    /// <summary>Base of the ATOM marker table: one i32 per atom id, the
    /// fresh-entry marker of the zero-arity predicate of that name. Zero
    /// base means none is staged and an atom goal steps aside.
    ///
    /// <para>It exists because a module can index but not search: a bare
    /// atom goal gives it an atom id, and (atom, 0) -&gt; functor is a
    /// lookup only the host can do.</para></summary>
    public const int AtomMarkerBase = 43;
    /// <summary>Entries in the atom marker table.</summary>
    public const int AtomMarkerLength = 44;

    /// <summary>Non-zero while setup_call_cleanup/3 has live handlers.
    ///
    /// <para>A cut may FIRE them, and running a cleanup is meta-calling a
    /// goal from inside the cut -- host work. So the module's inline cut
    /// declines whenever any is live, which is the common case being
    /// nothing.</para>
    ///
    /// <para>A slot of its own rather than a Flags bit: Flags makes the code
    /// bail at the next safe point, and this must stop ONE emitted form, not
    /// the whole chain.</para></summary>
    public const int CleanupsPending = 45;

    /// <summary>The thread's function table, as emscripten names it. Every
    /// module a thread registers lands in this one, which is what lets a
    /// module reach another without going out to the host.</summary>
    public const string TableModule = "env";
    public const string TableField = "__indirect_function_table";

    public const int SlotCount = 46;
    public const int SlotSize = 8;
    public const int ByteSize = SlotCount * SlotSize;

    /// <summary>The byte offset of a slot from the mailbox address.</summary>
    public static uint ByteOffset(int slot) => (uint)(slot * SlotSize);

    // ---- flag bits ----
    public const long FlagWakeupPending = 1L << 0;
    public const long FlagInterrupt = 1L << 1;

    // ---- names on the wire ----
    /// <summary>The import module name for the memory the runtime owns.</summary>
    public const string MemoryModule = "env";
    public const string MemoryField = "memory";
    /// <summary>The exported entry point of every compiled predicate.</summary>
    public const string EntryExport = "run";
}
