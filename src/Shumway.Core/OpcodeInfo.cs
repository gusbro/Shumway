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

        // Control
        Set(Opcode.Allocate, 5, "allocate", OperandKind.Count);
        Set(Opcode.Deallocate, 1, "deallocate");
        Set(Opcode.Call, 9, "call", OperandKind.Address, OperandKind.Count);
        Set(Opcode.Execute, 5, "execute", OperandKind.Address);
        Set(Opcode.Proceed, 1, "proceed");
        Set(Opcode.Halt, 1, "halt");

        // Choice points
        Set(Opcode.TryMeElse, 9, "try_me_else", OperandKind.Address, OperandKind.Count);
        Set(Opcode.RetryMeElse, 5, "retry_me_else", OperandKind.Address);
        Set(Opcode.TrustMe, 1, "trust_me");
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

        // PSTR
        Set(Opcode.GetPstr, 9, "get_pstr", OperandKind.LiteralId, OperandKind.Reg);
        Set(Opcode.PutPstr, 9, "put_pstr", OperandKind.LiteralId, OperandKind.Reg);
        Set(Opcode.UnifyPstrHead, 5, "unify_pstr_head", OperandKind.Reg);

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
