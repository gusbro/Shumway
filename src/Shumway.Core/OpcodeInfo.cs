namespace Shumway.Core;

/// <summary>
/// Per-opcode metadata: the total byte size of the instruction (including the opcode
/// byte), the count and semantic kind of operands, and the symbolic mnemonic used by
/// the disassembler. Default-constructed entries (<see cref="Mnemonic"/> = <c>null</c>)
/// indicate an unassigned opcode value.
/// </summary>
public readonly struct OpcodeInfo
{
    public Opcode Op { get; }
    public byte Size { get; }
    public byte NumOperands { get; }
    public string? Mnemonic { get; }
    public OperandKind[]? OperandKinds { get; }

    public OpcodeInfo(Opcode op, byte size, byte numOperands, string mnemonic, OperandKind[]? operandKinds)
    {
        Op = op;
        Size = size;
        NumOperands = numOperands;
        Mnemonic = mnemonic;
        OperandKinds = operandKinds;
    }

    public bool IsDefined => Mnemonic is not null;
}

/// <summary>
/// Static catalog of every defined opcode. Indexed by byte value, so dispatch and
/// disassembly both pay only an array load.
/// </summary>
public static class OpcodeTable
{
    /// <summary>ADR-025 — the arity operand value marking a BODY
    /// <c>try_me_else</c> (an inline if-then-else / disjunction choice point)
    /// as opposed to a clause-dispatch one. Dispatch chains always carry the
    /// real predicate arity (&gt;= 0), so this sentinel distinguishes the two
    /// forms everywhere a whole-bytecode scan needs to (the IL cursor-budget
    /// counter, the legacy-recogniser guards). The interpreter pushes the CP
    /// with arity 0 — the ITE variable discipline keeps branch state in Y
    /// slots, so no argument registers are saved.</summary>
    public const int InlineIteCpArity = -1;

    private static readonly OpcodeInfo[] _entries = new OpcodeInfo[256];

    static OpcodeTable()
    {
        // 0x00 — invalid. Size 1 so a disassembler can advance past it; encountering this
        // at runtime is a corruption signal.
        Set(Opcode.ReservedInvalid, 1, "reserved_invalid");

        // Get instructions
        Set(Opcode.GetVariableX, 9, "get_variable_x", OperandKind.Reg, OperandKind.Reg);
        Set(Opcode.GetVariableY, 9, "get_variable_y", OperandKind.Perm, OperandKind.Reg);
        Set(Opcode.GetValueX, 9, "get_value_x", OperandKind.Reg, OperandKind.Reg);
        Set(Opcode.GetValueY, 9, "get_value_y", OperandKind.Perm, OperandKind.Reg);
        Set(Opcode.GetConstant, 9, "get_constant", OperandKind.Atom, OperandKind.Reg);
        Set(Opcode.GetInteger, 9, "get_integer", OperandKind.IntValue, OperandKind.Reg);
        Set(Opcode.GetAtom, 9, "get_atom", OperandKind.Atom, OperandKind.Reg);
        Set(Opcode.GetNil, 5, "get_nil", OperandKind.Reg);
        Set(Opcode.GetStructure, 9, "get_structure", OperandKind.Functor, OperandKind.Reg);
        Set(Opcode.GetList, 5, "get_list", OperandKind.Reg);
        Set(Opcode.GetFloat, 9, "get_float", OperandKind.LiteralId, OperandKind.Reg);
        Set(Opcode.GetBigInt, 9, "get_bigint", OperandKind.LiteralId, OperandKind.Reg);

        // Put instructions
        Set(Opcode.PutVariableX, 9, "put_variable_x", OperandKind.Reg, OperandKind.Reg);
        Set(Opcode.PutVariableY, 9, "put_variable_y", OperandKind.Perm, OperandKind.Reg);
        Set(Opcode.PutValueX, 9, "put_value_x", OperandKind.Reg, OperandKind.Reg);
        Set(Opcode.PutValueY, 9, "put_value_y", OperandKind.Perm, OperandKind.Reg);
        Set(Opcode.PutConstant, 9, "put_constant", OperandKind.Atom, OperandKind.Reg);
        Set(Opcode.PutInteger, 9, "put_integer", OperandKind.IntValue, OperandKind.Reg);
        Set(Opcode.PutAtom, 9, "put_atom", OperandKind.Atom, OperandKind.Reg);
        Set(Opcode.PutNil, 5, "put_nil", OperandKind.Reg);
        Set(Opcode.PutStructure, 9, "put_structure", OperandKind.Functor, OperandKind.Reg);
        Set(Opcode.PutList, 5, "put_list", OperandKind.Reg);
        // ADR-020: reserve-upfront roots. put_structure_r packs reg+argCount
        // into one word after the functor id.
        Set(Opcode.PutStructureR, 9, "put_structure_r", OperandKind.Functor, OperandKind.PackedRegCount);
        Set(Opcode.PutListR, 5, "put_list_r", OperandKind.Reg);
        Set(Opcode.PutFloat, 9, "put_float", OperandKind.LiteralId, OperandKind.Reg);
        Set(Opcode.PutBigInt, 9, "put_bigint", OperandKind.LiteralId, OperandKind.Reg);

        // Unify instructions
        Set(Opcode.UnifyVariableX, 5, "unify_variable_x", OperandKind.Reg);
        Set(Opcode.UnifyVariableY, 5, "unify_variable_y", OperandKind.Perm);
        Set(Opcode.UnifyValueX, 5, "unify_value_x", OperandKind.Reg);
        Set(Opcode.UnifyValueY, 5, "unify_value_y", OperandKind.Perm);
        Set(Opcode.UnifyConstant, 5, "unify_constant", OperandKind.Atom);
        Set(Opcode.UnifyInteger, 5, "unify_integer", OperandKind.IntValue);
        Set(Opcode.UnifyAtom, 5, "unify_atom", OperandKind.Atom);
        Set(Opcode.UnifyNil, 1, "unify_nil");
        Set(Opcode.UnifyVoid, 5, "unify_void", OperandKind.Count);
        Set(Opcode.UnifyFloat, 5, "unify_float", OperandKind.LiteralId);
        Set(Opcode.UnifyBigInt, 5, "unify_bigint", OperandKind.LiteralId);
        // ADR-019: inline nested compound build/match.
        Set(Opcode.UnifyStructure, 5, "unify_structure", OperandKind.Functor);
        Set(Opcode.UnifyList, 1, "unify_list");

        // Control
        Set(Opcode.Allocate, 5, "allocate", OperandKind.Count);
        Set(Opcode.Deallocate, 1, "deallocate");
        Set(Opcode.Call, 9, "call", OperandKind.Address, OperandKind.Count);
        Set(Opcode.Execute, 5, "execute", OperandKind.Address);
        Set(Opcode.Proceed, 1, "proceed");
        Set(Opcode.Halt, 1, "halt");
        // Chunk 225 Stage B.1 — Call → CallIl rewrite (in-place at
        // link time). Same width and operand layout as Call so the
        // patch is a single opcode-byte swap + 4-byte operand
        // overwrite (target address → callee functor id).
        Set(Opcode.CallIl, 9, "call_il", OperandKind.Functor, OperandKind.Count);
        // Chunk 226 Stage B.2 — Call → CallBytecode rewrite for
        // bytecode-only targets. Same width / operand layout as Call;
        // the rewrite is a single opcode-byte swap with the target
        // operand left alone.
        Set(Opcode.CallBytecode, 9, "call_bytecode",
            OperandKind.Address, OperandKind.Count);
        // Chunk 227 Stage B.3 — Execute → ExecuteIl / ExecuteBytecode
        // rewrites at link time. Same 5-byte width as Execute; rewrite
        // is an opcode-byte swap (+4-byte operand patch for ExecuteIl
        // where address becomes functor id).
        Set(Opcode.ExecuteIl, 5, "execute_il", OperandKind.Functor);
        Set(Opcode.ExecuteBytecode, 5, "execute_bytecode", OperandKind.Address);
        // Chunk 248 — ExecuteBuiltin: tail-call to a builtin. Same
        // 5-byte width as Execute so the linker can do an opcode-byte
        // swap (plus a 4-byte operand patch from address to
        // BuiltinId) when a tail Execute resolves to a builtin —
        // typically a foreign predicate the linker discovered via
        // --foreign-dll that wasn't in BuiltinsRegistry at compile
        // time.
        Set(Opcode.ExecuteBuiltin, 5, "execute_builtin", OperandKind.BuiltinId);

        // Choice points
        Set(Opcode.Nop, 1, "nop");
        Set(Opcode.TryMeElse, 9, "try_me_else", OperandKind.Address, OperandKind.Count);
        Set(Opcode.RetryMeElse, 5, "retry_me_else", OperandKind.Address);
        Set(Opcode.TrustMe, 1, "trust_me");
        // ADR-025 — unconditional intra-predicate branch (inline if-then-else).
        Set(Opcode.Jump, 5, "jump", OperandKind.Address);
        // ADR-025 — capture CURRENT B (not B0) as the inline-ITE commit
        // barrier (get_level's B0 is reset by any pre-ITE body call, which
        // made the ITE cut prune a preceding generator's choice points).
        Set(Opcode.GetLevelB, 5, "get_level_b", OperandKind.Perm);
        // ADR-015 chunk C — generation-filtered dynamic dispatch.
        Set(Opcode.EnterDynamic, 1, "enter_dynamic");
        Set(Opcode.CheckVisible, 17, "check_visible",
            OperandKind.LongValue, OperandKind.LongValue);
        // try carries an arity operand so the indexing entry point can build
        // a choice point without help from the predicate context. retry/trust
        // reuse the arity already captured in the active CP.
        Set(Opcode.Try, 9, "try", OperandKind.Address, OperandKind.Count);
        Set(Opcode.Retry, 5, "retry", OperandKind.Address);
        Set(Opcode.Trust, 5, "trust", OperandKind.Address);

        // Indexing
        Set(Opcode.SwitchOnTerm, 17, "switch_on_term",
            OperandKind.Address, OperandKind.Address, OperandKind.Address, OperandKind.Address);
        Set(Opcode.SwitchOnAtom, 5, "switch_on_atom", OperandKind.TableId);
        Set(Opcode.SwitchOnInteger, 5, "switch_on_integer", OperandKind.TableId);
        Set(Opcode.SwitchOnStructure, 5, "switch_on_structure", OperandKind.TableId);
        // Multi-arg indexing: opcode + arg_idx + (same operands as the arg-0
        // variant above). Reads X[arg_idx] instead of A1.
        Set(Opcode.SwitchOnArg, 21, "switch_on_arg",
            OperandKind.Reg,
            OperandKind.Address, OperandKind.Address, OperandKind.Address, OperandKind.Address);
        Set(Opcode.SwitchOnAtomArg, 9, "switch_on_atom_arg",
            OperandKind.Reg, OperandKind.TableId);
        Set(Opcode.SwitchOnIntegerArg, 9, "switch_on_integer_arg",
            OperandKind.Reg, OperandKind.TableId);
        Set(Opcode.SwitchOnStructureArg, 9, "switch_on_structure_arg",
            OperandKind.Reg, OperandKind.TableId);
        // ADR-027 second-level (sub-argument) indexing: opcode + arg_idx +
        // two path indices (sub0, sub1; sub1 = -1 = depth-1 sentinel) + table.
        Set(Opcode.SwitchOnAtomSub, 17, "switch_on_atom_sub",
            OperandKind.Reg, OperandKind.IntValue, OperandKind.IntValue, OperandKind.TableId);
        Set(Opcode.SwitchOnIntegerSub, 17, "switch_on_integer_sub",
            OperandKind.Reg, OperandKind.IntValue, OperandKind.IntValue, OperandKind.TableId);

        // Cut
        Set(Opcode.NeckCut, 1, "neck_cut");
        Set(Opcode.GetLevel, 5, "get_level", OperandKind.Perm);
        Set(Opcode.Cut, 5, "cut", OperandKind.Perm);

        // Builtin call and specialised builtin opcodes
        Set(Opcode.CallBuiltin, 9, "call_builtin", OperandKind.BuiltinId, OperandKind.Count);
        Set(Opcode.UnifyEq, 1, "unify_eq");
        Set(Opcode.IsOp, 1, "is_op");
        Set(Opcode.LessThan, 1, "less_than");
        Set(Opcode.GreaterThan, 1, "greater_than");
        Set(Opcode.LessEq, 1, "less_eq");
        Set(Opcode.GreaterEq, 1, "greater_eq");
        Set(Opcode.ArithEq, 1, "arith_eq");
        Set(Opcode.ArithNotEq, 1, "arith_not_eq");
        Set(Opcode.StructEq, 1, "struct_eq");
        Set(Opcode.StructNotEq, 1, "struct_not_eq");

        // Consolidated patterns
        Set(Opcode.GetConstantA1, 5, "get_constant_a1", OperandKind.Atom);
        Set(Opcode.GetConstantA2, 5, "get_constant_a2", OperandKind.Atom);
        Set(Opcode.PutConstantA1, 5, "put_constant_a1", OperandKind.Atom);
        Set(Opcode.PutConstantA2, 5, "put_constant_a2", OperandKind.Atom);
        Set(Opcode.GetListA1, 1, "get_list_a1");
        Set(Opcode.GetListA2, 1, "get_list_a2");

        // Chunk 220 — fused opcodes (same total size as the two they
        // replace; the second opcode's byte slot is overwritten with Nop
        // so addresses don't shift).
        // Layout: [op:1] [count:4] [Nop:1] [slot:4] — count + slot are
        // read at fixed offsets by the handler. Reusing the original
        // operand positions keeps OpcodeInfo's NumOperands meaningful.
        Set(Opcode.AllocateGetLevel, 10, "allocate_get_level",
            OperandKind.Count, OperandKind.Perm);
        Set(Opcode.DeallocateProceed, 2, "deallocate_proceed");

        // PSTR
        Set(Opcode.GetPstr, 9, "get_pstr", OperandKind.LiteralId, OperandKind.Reg);
        Set(Opcode.PutPstr, 9, "put_pstr", OperandKind.LiteralId, OperandKind.Reg);
        Set(Opcode.UnifyPstrHead, 5, "unify_pstr_head", OperandKind.Reg);

        // ADR-018 — arithmetic instruction set (4-byte operands).
        Set(Opcode.AEvalPush, 9, "a_eval_push", OperandKind.Count, OperandKind.IntValue);
        Set(Opcode.AEvalBin, 5, "a_eval_bin", OperandKind.Count);
        Set(Opcode.AEvalUn, 5, "a_eval_un", OperandKind.Count);
        Set(Opcode.AEvalIs, 9, "a_eval_is", OperandKind.Count, OperandKind.Reg);
        Set(Opcode.AEvalCmp, 5, "a_eval_cmp", OperandKind.Count);
        // Compact encoding (Phase 26): a packed kind/op word + the three values.
        // a_int_bin = 1 + 4*4 = 17; a_int_cmp = 1 + 3*4 = 13. The disassembler
        // special-cases both (DecodeAIntBin / DecodeAIntCmp) to unpack the word
        // into readable [op, aKind, aVal, bKind, bVal, tKind, tVal] operands.
        Set(Opcode.AIntBin, 17, "a_int_bin",
            OperandKind.Count, OperandKind.IntValue, OperandKind.IntValue, OperandKind.Reg);
        Set(Opcode.AIntCmp, 13, "a_int_cmp",
            OperandKind.Count, OperandKind.IntValue, OperandKind.IntValue);

        // Meta and extension
        // The Meta opcode size is 6 by default for the DbgInfo sub-opcode (1 opcode + 1
        // sub-byte + 4-byte entry id). Disassembler dispatches on the sub-byte to recover
        // the actual structure when future sub-opcodes are added.
        Set(Opcode.Meta, 6, "meta");
        Set(Opcode.ReservedExtension, 1, "reserved_extension");
    }

    private static void Set(Opcode op, byte size, string mnemonic, params OperandKind[] operandKinds)
    {
        byte n = (byte)operandKinds.Length;
        var kinds = n == 0 ? null : operandKinds;
        _entries[(byte)op] = new OpcodeInfo(op, size, n, mnemonic, kinds);
    }

    public static OpcodeInfo Get(byte opcode) => _entries[opcode];
    public static OpcodeInfo Get(Opcode opcode) => _entries[(byte)opcode];

    public static bool IsDefined(byte opcode) => _entries[opcode].IsDefined;
}
