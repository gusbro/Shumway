namespace Shumway.Core;

/// <summary>
/// 4-bit type tag occupying the high bits of a <see cref="Cell"/>.
/// See ADR-002 and docs/design/cell-layout-detail.md for the bit layout.
/// </summary>
public enum Tag : byte
{
    Ref = 0x0,
    Str = 0x1,
    Lis = 0x2,
    Functor = 0x3,
    Atom = 0x4,
    Int = 0x5,
    Float = 0x6,
    BigInt = 0x7,
    String = 0x8,
    Foreign = 0x9,
    AttVar = 0xA,
    Pstr = 0xB,
    PstrBuffer = 0xC,
}
