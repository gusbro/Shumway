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
    Foreign = 0x8,
    AttVar = 0x9,
    Pstr = 0xA,
    PstrBuffer = 0xB,

    /// <summary>A raw machine word stored on the stack — an environment /
    /// choice-point control slot (CE, CP, B, BP, trail tops, HeapTop, Hb,
    /// ViewGen, B0, arity, permanent count). Tagged distinctly so the heap
    /// garbage collector (ADR-016) never mistakes a small control value
    /// for a <see cref="Ref"/> and relocates it. The stored value occupies
    /// the 60-bit payload; integer slots read it back with a plain
    /// <c>(int)Data</c> cast (the tag lives above bit 31, so the cast is
    /// unaffected), and the one 60-bit slot (ViewGen) reads via
    /// <see cref="Cell.Payload"/>.</summary>
    RawInt = 0xC,

    /// <summary>An exact rational <c>Num/Den</c> (ADR-039). Like
    /// <see cref="BigInt"/>, the payload is an id into a per-activation side
    /// table (<c>_rationalTable</c>); the value never lives in the cell. Every
    /// rational cell is a genuine fraction (denominator &gt; 1) — an integral
    /// value collapses to <see cref="Int"/> / <see cref="BigInt"/>.</summary>
    Rational = 0xD,

    // 0xE and 0xF are the two free slots. The tag space is 4 bits and cannot
    // grow without changing the cell layout, so a NEW tag is a major decision
    // (decision-policy.md). Values are contiguous by construction (compacted
    // pre-v1 when ADR-047 removed the string tag) — keep them that way.
}
