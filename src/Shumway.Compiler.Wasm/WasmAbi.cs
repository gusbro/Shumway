namespace Shumway.Compiler.Wasm;

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
    /// <summary>Base of an int32 array mapping functor id to arity, mirrored
    /// into the linear memory by the host. The general unifier needs an
    /// arity to walk a structure's arguments, and the functor table is
    /// managed state.</summary>
    public const int FunctorArityBase = 24;

    public const int SlotCount = 32;
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
