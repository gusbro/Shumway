namespace Shumway.Core;

/// <summary>
/// WAM bytecode opcodes. Values are fixed by docs/design/wam-instruction-set.md and ADR-006.
/// Ranges are reserved by category so future opcodes can be added without renumbering:
/// <list type="bullet">
///   <item>0x00: <see cref="ReservedInvalid"/> — catches PC corruption when dispatched.</item>
///   <item>0x01..0x1F: get instructions.</item>
///   <item>0x20..0x3F: put instructions.</item>
///   <item>0x40..0x4F: unify instructions (read/write-mode-sensitive).</item>
///   <item>0x50..0x5F: control flow.</item>
///   <item>0x60..0x6F: choice points.</item>
///   <item>0x70..0x7F: indexing.</item>
///   <item>0x80..0x8F: cut.</item>
///   <item>0x90..0x9F: builtin call and specialised builtin opcodes.</item>
///   <item>0xA0..0xBF: consolidated patterns (A1/A2 specialisations).</item>
///   <item>0xC0..0xCF: PSTR-specific.</item>
///   <item>0xD0..0xFD: reserved for future use.</item>
///   <item>0xFE: <see cref="Meta"/> opcode with a sub-byte for kind (only DbgInfo in v1).</item>
///   <item>0xFF: <see cref="ReservedExtension"/> — escape mechanism for a hypothetical extended encoding.</item>
/// </list>
/// </summary>
public enum Opcode : byte
{
    ReservedInvalid = 0x00,

    // Get instructions
    GetVariableX = 0x01,
    GetVariableY = 0x02,
    GetValueX = 0x03,
    GetValueY = 0x04,
    GetConstant = 0x05,
    GetInteger = 0x06,
    GetAtom = 0x07,
    GetNil = 0x08,
    GetStructure = 0x09,
    GetList = 0x0A,
    GetFloat = 0x0B,
    GetBigInt = 0x0C,

    // Put instructions
    PutVariableX = 0x20,
    PutVariableY = 0x21,
    PutValueX = 0x22,
    PutValueY = 0x23,
    PutConstant = 0x24,
    PutInteger = 0x25,
    PutAtom = 0x26,
    PutNil = 0x27,
    PutStructure = 0x28,
    PutList = 0x29,
    PutFloat = 0x2A,
    PutBigInt = 0x2B,

    // Unify instructions (read/write-mode-sensitive)
    UnifyVariableX = 0x40,
    UnifyVariableY = 0x41,
    UnifyValueX = 0x42,
    UnifyValueY = 0x43,
    UnifyConstant = 0x44,
    UnifyInteger = 0x45,
    UnifyAtom = 0x46,
    UnifyNil = 0x47,
    UnifyVoid = 0x48,
    UnifyFloat = 0x49,
    UnifyBigInt = 0x4A,

    // Control
    Allocate = 0x50,
    Deallocate = 0x51,
    Call = 0x52,
    Execute = 0x53,
    Proceed = 0x54,
    Halt = 0x55,
    // ADR-015 chunk C step 4: a 1-byte no-op. Used as padding when
    // asserta converts the chain's old head from try_me_else (9 bytes)
    // to retry_me_else (5 bytes) — the trailing 4 arity-operand bytes
    // are overwritten with 4 nops so the in-place patch keeps the
    // rest of the clause layout unchanged.
    Nop = 0x56,

    // Chunk 225 Stage B.1 — fast-path Call for predicates with bundle-IL
    // already registered at link time. Same byte width (9) and operand
    // layout as Call ([functorId:4][numLivePerms:4]) so PrologEngine's
    // post-link rewrite is an in-place byte swap of the opcode + a
    // 4-byte operand patch (address → functor id). Skips the
    // Tier1Dispatcher?.OnDispatch interface call + dictionary probe
    // every Call does today; the interpreter looks the delegate up
    // directly in its IlByFunctorId array.
    CallIl = 0x57,

    // Chunk 226 Stage B.2 — fast-path Call for predicates known to be
    // permanently bytecode-only (dynamic predicates per chunk 159,
    // layout-excluded statics, OR any callee when the engine's IL
    // promotion is disabled — Threshold==0 — so no functor will ever
    // earn an IL delegate). Same byte width (9) and operand layout as
    // Call ([target:4][numLivePerms:4]). The linker's post-link
    // rewrite swaps the opcode byte and leaves the target operand
    // unchanged. Skips the Tier1Dispatcher?.OnDispatch interface call
    // + dictionary probe; the interpreter does MaybeCollectHeap +
    // SetPc(target) directly.
    CallBytecode = 0x58,

    // Chunk 227 Stage B.3 — tail-call counterparts of CallIl /
    // CallBytecode. Same 5-byte width as Execute ([op:1][operand:4]);
    // the linker rewrites each Execute site to the right variant the
    // same way it rewrites Call sites. ExecuteIl's operand is the
    // callee functor id (looked up in IlByFunctorId);
    // ExecuteBytecode's operand is the absolute target address
    // (just SetPc).
    ExecuteIl = 0x59,
    ExecuteBytecode = 0x5A,

    // Chunk 248 — tail-call counterpart of CallBuiltin. Same 5-byte
    // width as Execute / ExecuteIl ([op:1][builtinId:4]); the
    // linker rewrites an Execute site to ExecuteBuiltin when its
    // resolved callee is a builtin (foreign predicate from
    // --foreign-dll, or a standard builtin the compiler missed
    // because it was registered after compile time). The runtime
    // dispatches the builtin (without TrimEnv — we're returning),
    // then jumps Pc = Cp to return to the caller. Drops the
    // numLivePerms field that CallBuiltin carries: a tail call
    // doesn't trim env (Deallocate has already restored the
    // parent frame, or there was never one).
    ExecuteBuiltin = 0x5B,

    // Choice points
    TryMeElse = 0x60,
    RetryMeElse = 0x61,
    TrustMe = 0x62,
    Try = 0x63,
    Retry = 0x64,
    Trust = 0x65,

    // ADR-015 chunk C, bytecode-level dispatch — generation-filtered
    // dynamic predicates (logical update view at the bytecode level, no
    // builtin indirection).
    EnterDynamic = 0x66,    // sample DbGeneration -> CurrentViewGen
    CheckVisible = 0x67,    // <born:8> <died:8> — skip clause if not visible

    // Indexing
    SwitchOnTerm = 0x70,
    SwitchOnAtom = 0x71,
    SwitchOnInteger = 0x72,
    SwitchOnStructure = 0x73,
    // Multi-arg indexing (Phase 2): same semantics as the four above, but
    // dispatch on an arbitrary argument register A[k] (encoded as the first
    // operand) instead of A1 (X[0]).
    SwitchOnArg = 0x74,
    SwitchOnAtomArg = 0x75,
    SwitchOnIntegerArg = 0x76,
    SwitchOnStructureArg = 0x77,

    // Cut
    NeckCut = 0x80,
    GetLevel = 0x81,
    Cut = 0x82,

    // Builtin call and specialised builtin opcodes
    CallBuiltin = 0x90,
    UnifyEq = 0x91,
    IsOp = 0x92,
    LessThan = 0x93,
    GreaterThan = 0x94,
    LessEq = 0x95,
    GreaterEq = 0x96,
    ArithEq = 0x97,
    ArithNotEq = 0x98,
    StructEq = 0x99,
    StructNotEq = 0x9A,

    // Consolidated patterns
    GetConstantA1 = 0xA0,
    GetConstantA2 = 0xA1,
    PutConstantA1 = 0xA2,
    PutConstantA2 = 0xA3,
    GetListA1 = 0xA4,
    GetListA2 = 0xA5,

    // Chunk 220 — opcode fusion. Profiled pairs in Blint workload:
    //   Allocate (5) + GetLevel (5)  : 14.0M pairs / run — clause prologue with deep cut.
    //   Deallocate (1) + Proceed (1) :  6.7M pairs / run — clause epilogue.
    // Fused opcodes use the SAME total byte width as the two they replace,
    // with the second opcode's byte slot overwritten with Nop so no
    // operand-address shifts cascade through try_me_else / switch tables.
    AllocateGetLevel = 0xA6,   // 10 bytes: op + count(4) + Nop + slot(4)
    DeallocateProceed = 0xA7,  // 2 bytes:  op + Nop

    // PSTR
    GetPstr = 0xC0,
    PutPstr = 0xC1,
    UnifyPstrHead = 0xC2,

    // ADR-018 — arithmetic instruction set. `X is Expr` and the six
    // comparisons compile to a postfix (RPN) sequence over a per-engine
    // Number eval stack; no heap term, no synthetic variables. Operands are
    // 4-byte ints (the small kind/op codes are widened) so the existing
    // BytecodeIO / disassembler / emitter framework reads them uniformly.
    //   AEvalPush <kind:4> <operand:4> — push a leaf. kind ∈ {0 int (operand =
    //       value), 1 bigint-lit, 2 float-lit, 3 X-reg, 4 Y-slot}. For X/Y the
    //       cell is deref'd and arithmetically evaluated before the push.
    //   AEvalBin  <op:4>               — pop b, pop a, push (a op b).
    //   AEvalUn   <op:4>               — pop a, push op(a).
    //   AEvalIs   <kind:4> <target:4>  — pop result, unify with X/Y[target].
    //   AEvalCmp  <rel:4>              — pop b, pop a, compare; fail = backtrack.
    AEvalPush = 0xD0,   // 9 bytes
    AEvalBin = 0xD1,    // 5 bytes
    AEvalUn = 0xD2,     // 5 bytes
    AEvalIs = 0xD3,     // 9 bytes
    AEvalCmp = 0xD4,    // 5 bytes

    //   Fused flat-arithmetic ops (ADR-018). Collapse the common single-operator
    //   `T is A op B` and `A cmp B` over simple leaf operands into one dispatch
    //   instead of the push/push/op/is RPN sequence. Each operand is encoded
    //   <kind:4><val:4> with kind ∈ {0 int-literal, 3 X-reg, 4 Y-slot}; the
    //   handler runs the int fast lane inline and escalates to Number for
    //   float/bigint/overflow, identically to the a_eval_* path.
    //   AIntBin <op:4> <aKind:4><aVal:4> <bKind:4><bVal:4> <tKind:4><tVal:4>
    //           — T is A op B; tKind ∈ {3 unify-reg,4 unify-Y,5 set-reg,6 set-Y}.
    //   AIntCmp <rel:4> <aKind:4><aVal:4> <bKind:4><bVal:4>
    //           — A cmp B; fail = backtrack.
    AIntBin = 0xD5,     // 29 bytes
    AIntCmp = 0xD6,     // 21 bytes

    // Meta and extension
    Meta = 0xFE,
    ReservedExtension = 0xFF,
}

/// <summary>Sub-opcodes for <see cref="Opcode.Meta"/>. Only <see cref="DbgInfo"/> exists in v1.</summary>
public enum MetaSubOpcode : byte
{
    DbgInfo = 0x00,
}
