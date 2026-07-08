namespace Shumway.Core;

/// <summary>
/// WAM bytecode opcodes. The encoding framework (fixed-size instructions, operand
/// layout) is defined by docs/design/wam-instruction-set.md and ADR-006.
///
/// <para>Chunk 429: values are assigned CONTIGUOUSLY (0x00..0x65, no gaps) so the
/// interpreter's dispatch <c>switch</c> compiles to ONE dense jump table. The old
/// per-category reserved ranges (get 0x01.., put 0x20.., unify 0x40.., …) left the
/// ~90 cases scattered across 11 disjoint clusters, which made Roslyn emit a chain
/// of cluster-selection compares in front of several smaller tables — measurable on
/// the ~28M dispatches of a Blint run. Pre-release, the numeric values carry no
/// compatibility obligation (no released bundles exist), so they may be renumbered
/// freely — but keep the block dense: add new opcodes at the end of the dispatched
/// block (before <see cref="ReservedExtension"/>), renumbering the tail.</para>
/// <list type="bullet">
///   <item>0x00: <see cref="ReservedInvalid"/> — catches PC corruption when dispatched.
///     Must stay 0x00 (zeroed memory dispatches as corruption).</item>
///   <item>0x01..0x5E: every opcode the interpreter dispatches, grouped by category
///     (get, put, unify, control, choice points, indexing, cut, builtin call,
///     consolidated/fused, PSTR, arithmetic), ending with <see cref="Meta"/> — the
///     dense jump-table block.</item>
///   <item>0x5F: <see cref="ReservedExtension"/> — escape mechanism for a
///     hypothetical extended encoding (no dispatch case).</item>
///   <item>0x60..0x69: reserved specialised-builtin opcodes (never emitted, no
///     dispatch case).</item>
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
    PutVariableX = 0x0D,
    PutVariableY = 0x0E,
    PutValueX = 0x0F,
    PutValueY = 0x10,
    PutConstant = 0x11,
    PutInteger = 0x12,
    PutAtom = 0x13,
    PutNil = 0x14,
    PutStructure = 0x15,
    PutList = 0x16,
    PutFloat = 0x17,
    PutBigInt = 0x18,
    // ADR-020: reserve-upfront write-mode roots for a term tree that contains a
    // non-last nested compound. The reserve size is baked at compile time (no
    // runtime FunctorTable lookup). put_structure_r carries <functorId:4>
    // <packed:4> where packed = regIdx (low 24 bits) | argCount (high byte) —
    // 9 bytes, same width as put_structure. put_list_r carries <regIdx:4> and
    // reserves 2 (5 bytes).
    PutStructureR = 0x19,
    PutListR = 0x1A,

    // Unify instructions (read/write-mode-sensitive)
    UnifyVariableX = 0x1B,
    UnifyVariableY = 0x1C,
    UnifyValueX = 0x1D,
    UnifyValueY = 0x1E,
    UnifyConstant = 0x1F,
    UnifyInteger = 0x20,
    UnifyAtom = 0x21,
    UnifyNil = 0x22,
    UnifyVoid = 0x23,
    UnifyFloat = 0x24,
    UnifyBigInt = 0x25,
    // ADR-019: inline nested compound build (write mode) / match (read mode).
    UnifyStructure = 0x26,   // opcode + 4-byte functor id = 5 bytes
    UnifyList = 0x27,        // 1 byte

    // Control
    Allocate = 0x28,
    Deallocate = 0x29,
    Call = 0x2A,
    Execute = 0x2B,
    Proceed = 0x2C,
    Halt = 0x2D,
    // ADR-015 chunk C step 4: a 1-byte no-op. Used as padding when
    // asserta converts the chain's old head from try_me_else (9 bytes)
    // to retry_me_else (5 bytes) — the trailing 4 arity-operand bytes
    // are overwritten with 4 nops so the in-place patch keeps the
    // rest of the clause layout unchanged.
    Nop = 0x2E,

    // Chunk 225 Stage B.1 — fast-path Call for predicates with bundle-IL
    // already registered at link time. Same byte width (9) and operand
    // layout as Call ([functorId:4][numLivePerms:4]) so PrologEngine's
    // post-link rewrite is an in-place byte swap of the opcode + a
    // 4-byte operand patch (address → functor id). Skips the
    // Tier1Dispatcher?.OnDispatch interface call + dictionary probe
    // every Call does today; the interpreter looks the delegate up
    // directly in its IlByFunctorId array.
    CallIl = 0x2F,

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
    CallBytecode = 0x30,

    // Chunk 227 Stage B.3 — tail-call counterparts of CallIl /
    // CallBytecode. Same 5-byte width as Execute ([op:1][operand:4]);
    // the linker rewrites each Execute site to the right variant the
    // same way it rewrites Call sites. ExecuteIl's operand is the
    // callee functor id (looked up in IlByFunctorId);
    // ExecuteBytecode's operand is the absolute target address
    // (just SetPc).
    ExecuteIl = 0x31,
    ExecuteBytecode = 0x32,

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
    ExecuteBuiltin = 0x33,

    // Choice points
    TryMeElse = 0x34,
    RetryMeElse = 0x35,
    TrustMe = 0x36,
    Try = 0x37,
    Retry = 0x38,
    Trust = 0x39,

    // ADR-015 chunk C, bytecode-level dispatch — generation-filtered
    // dynamic predicates (logical update view at the bytecode level, no
    // builtin indirection).
    EnterDynamic = 0x3A,    // sample DbGeneration -> CurrentViewGen
    CheckVisible = 0x3B,    // <born:8> <died:8> — skip clause if not visible

    // Indexing
    SwitchOnTerm = 0x3C,
    SwitchOnAtom = 0x3D,
    SwitchOnInteger = 0x3E,
    SwitchOnStructure = 0x3F,
    // Multi-arg indexing (Phase 2): same semantics as the four above, but
    // dispatch on an arbitrary argument register A[k] (encoded as the first
    // operand) instead of A1 (X[0]).
    SwitchOnArg = 0x40,
    SwitchOnAtomArg = 0x41,
    SwitchOnIntegerArg = 0x42,
    SwitchOnStructureArg = 0x43,

    // Cut
    NeckCut = 0x44,
    GetLevel = 0x45,
    Cut = 0x46,

    // Builtin call (the reserved specialised-builtin opcodes — never
    // emitted, no interpreter dispatch case — live at the END of the
    // enum, after ReservedExtension, so they don't punch holes in the
    // chunk-429 dense jump-table block).
    CallBuiltin = 0x47,

    // Consolidated patterns
    GetConstantA1 = 0x48,
    GetConstantA2 = 0x49,
    PutConstantA1 = 0x4A,
    PutConstantA2 = 0x4B,
    GetListA1 = 0x4C,
    GetListA2 = 0x4D,

    // Chunk 220 — opcode fusion. Profiled pairs in Blint workload:
    //   Allocate (5) + GetLevel (5)  : 14.0M pairs / run — clause prologue with deep cut.
    //   Deallocate (1) + Proceed (1) :  6.7M pairs / run — clause epilogue.
    // Fused opcodes use the SAME total byte width as the two they replace,
    // with the second opcode's byte slot overwritten with Nop so no
    // operand-address shifts cascade through try_me_else / switch tables.
    AllocateGetLevel = 0x4E,   // 10 bytes: op + count(4) + Nop + slot(4)
    DeallocateProceed = 0x4F,  // 2 bytes:  op + Nop

    // PSTR
    GetPstr = 0x50,
    PutPstr = 0x51,
    UnifyPstrHead = 0x52,

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
    AEvalPush = 0x53,   // 9 bytes
    AEvalBin = 0x54,    // 5 bytes
    AEvalUn = 0x55,     // 5 bytes
    AEvalIs = 0x56,     // 9 bytes
    AEvalCmp = 0x57,    // 5 bytes

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
    AIntBin = 0x58,     // 17 bytes (compact encoding, chunk 311)
    AIntCmp = 0x59,     // 13 bytes (compact encoding, chunk 311)

    // ADR-025 — unconditional intra-predicate branch: Pc = <target>. Emitted by
    // the inline if-then-else / disjunction lowering (jump over the else branch
    // after the then branch completes). The operand is an Address, so the
    // linker's dispatch-site shift makes it program-absolute like a
    // try_me_else target.
    //   Jump <target:int32>
    Jump = 0x5A,        // 5 bytes

    // ADR-025 — Y[slot] := RawInt(B): capture the CURRENT choice-point top as
    // the inline-ITE commit barrier. Distinct from get_level, which captures
    // B0 (the procedure-entry snapshot a pre-ITE body call resets — using it
    // over-cut a preceding generator's choice points; the helper form never
    // saw this because the helper CALL re-established B0 at its own entry).
    //   GetLevelB <slot:int32>
    GetLevelB = 0x5B,   // 5 bytes

    // ADR-027 — second-level (sub-argument) indexing. Dispatch on a sub-term
    // reached by a bounded path from an argument register, instead of on the
    // argument itself. Covers list-head ("car") discrimination, compound
    // sub-argument dispatch, and the Arity token-stream idiom
    // `p([t(Sym,Code)|Tail], ...)` in one generic form.
    //   switch_on_{atom,integer}_sub <argIdx:4> <sub0:4> <sub1:4> <tableId:4>
    //     Walk from X[argIdx]: hop sub0, then (if sub1 >= 0) hop sub1; each hop
    //     indexes into whatever compound sits there — a list cell (idx 0 = head,
    //     1 = tail) or a struct (idx = arg position). Deref the final cell; an
    //     atom/integer keys the table, anything else (incl. a missed hop) takes
    //     the default. sub1 = -1 is the depth-1 sentinel. 17 bytes.
    SwitchOnAtomSub = 0x5C,
    SwitchOnIntegerSub = 0x5D,

    // ADR-028 — structure-keyed sub-argument indexing. Same bounded 2-hop walk
    // as the atom/integer subs, but the terminal is keyed by FUNCTOR id (the
    // switch_on_structure table format): a Str terminal (a list keys as './2')
    // indexes the table, anything else / a missed hop takes the default.
    //   switch_on_structure_sub <argIdx:4> <sub0:4> <sub1:4> <tableId:4>  (17 bytes)
    SwitchOnStructureSub = 0x5E,

    // ADR-029 — clause-epilogue peephole fusion. Each fused opcode keeps the
    // SAME total byte width as the two straight-line opcodes it replaces (the
    // spare byte slot(s) become Nop), so no operand-address shifts cascade
    // through try_me_else / switch tables. Each carries its single operand at
    // offset +1 (natural sequential read); Nop padding at the tail. Tier-0
    // dispatch-count win only — the IL describer un-fuses each to the exact IL
    // of its two components (promotion preserved).
    //   deallocate_execute <target:4>  (6 = deallocate 1 + execute 5) — the LCO
    //       epilogue (the missing sibling of deallocate_proceed).
    //   cut_deallocate_proceed <slot:4> (7 = cut 5 + deallocate_proceed 2) — the
    //       frame-allocated deterministic-clause epilogue `Head :- Body, !.`.
    //   cut_proceed <slot:4>           (6 = cut 5 + proceed 1) — frameless variant.
    DeallocateExecute = 0x5F,
    CutDeallocateProceed = 0x60,
    CutProceed = 0x61,

    // Meta — last member of the dense dispatched block (chunk 429).
    Meta = 0x62,

    // Extension escape — reserved, never dispatched.
    ReservedExtension = 0x63,

    // Reserved specialised-builtin opcodes. Defined in OpcodeTable but
    // never emitted by the compiler and never dispatched by the
    // interpreter; parked after ReservedExtension so the dispatched
    // block stays hole-free (chunk 429).
    UnifyEq = 0x64,
    IsOp = 0x65,
    LessThan = 0x66,
    GreaterThan = 0x67,
    LessEq = 0x68,
    GreaterEq = 0x69,
    ArithEq = 0x6A,
    ArithNotEq = 0x6B,
    StructEq = 0x6C,
    StructNotEq = 0x6D,
}

/// <summary>Sub-opcodes for <see cref="Opcode.Meta"/>. Only <see cref="DbgInfo"/> exists in v1.</summary>
public enum MetaSubOpcode : byte
{
    DbgInfo = 0x00,
}
