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

    // Choice points
    TryMeElse = 0x60,
    RetryMeElse = 0x61,
    TrustMe = 0x62,
    Try = 0x63,
    Retry = 0x64,
    Trust = 0x65,

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

    // PSTR
    GetPstr = 0xC0,
    PutPstr = 0xC1,
    UnifyPstrHead = 0xC2,

    // Meta and extension
    Meta = 0xFE,
    ReservedExtension = 0xFF,
}

/// <summary>Sub-opcodes for <see cref="Opcode.Meta"/>. Only <see cref="DbgInfo"/> exists in v1.</summary>
public enum MetaSubOpcode : byte
{
    DbgInfo = 0x00,
}
