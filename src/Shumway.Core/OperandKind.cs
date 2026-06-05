namespace Shumway.Core;

/// <summary>
/// Semantic role of a bytecode operand. Used by the disassembler (and any future
/// validator) to interpret raw 32-bit operand values consistently with the opcode that
/// owns them. Every WAM operand is encoded as a 32-bit signed little-endian integer; the
/// kind tells the reader whether to print it as a register number, a code offset, an
/// atom id, etc.
/// </summary>
public enum OperandKind : byte
{
    /// <summary>X register index (X1=0, X2=1, ...).</summary>
    Reg,

    /// <summary>Y (permanent) register index (Y1=0, Y2=1, ...).</summary>
    Perm,

    /// <summary>Atom id from the global atom table.</summary>
    Atom,

    /// <summary>Functor id from the global functor table.</summary>
    Functor,

    /// <summary>Code offset into <c>CodeArea.Bytes</c> (label target).</summary>
    Address,

    /// <summary>Inline 32-bit signed integer literal.</summary>
    IntValue,

    /// <summary>Generic count: number of permanents (allocate), live perms (call), or anonymous vars (unify_void).</summary>
    Count,

    /// <summary>Index into <c>CodeArea.SwitchTables</c>.</summary>
    TableId,

    /// <summary>Index into one of the auxiliary literal tables (bigint / string / float / pstr).</summary>
    LiteralId,

    /// <summary>Builtin id from the global builtin table.</summary>
    BuiltinId,

    /// <summary>Inline 64-bit signed integer (8 bytes). Used by ADR-015's
    /// <c>CheckVisible</c> opcode to carry a clause's <c>born</c> /
    /// <c>died</c> generations — patched in place by <c>retract</c>.</summary>
    LongValue,

    /// <summary>ADR-020 packed word for <c>put_structure_r</c>: a register
    /// index in the low 24 bits and an argument count (reserve size) in the
    /// high byte. Disassembled as <c>X{reg}/{argCount}</c>.</summary>
    PackedRegCount,
}
