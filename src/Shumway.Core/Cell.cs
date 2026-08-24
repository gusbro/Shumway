using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Shumway.Core;

/// <summary>
/// 64-bit blittable cell: 4-bit <see cref="Tag"/> in bits 63..60 and 60-bit payload in bits 59..0.
/// Cells are the unit of heap, stack, and register storage. The .NET GC never scans cell
/// memory for references; all managed-reference data (BigInteger, string, foreign object)
/// lives in per-engine auxiliary tables and is reached via an integer id stored in the cell.
/// See ADR-002 and docs/design/cell-layout-detail.md.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct Cell : IEquatable<Cell>
{
    public readonly long Data;

    public const int TagShift = 60;
    public const long PayloadMask = (1L << 60) - 1;
    public const long MinInt60 = -(1L << 59);
    public const long MaxInt60 = (1L << 59) - 1;

    public Cell(long data) => Data = data;

    public Tag Tag => (Tag)((int)(Data >> TagShift) & 0xF);

    /// <summary>The tag as a raw <see cref="int"/>. Lets the Tier-1 IL emit
    /// the index-dispatch tag test as a clean int32 comparison (the
    /// <see cref="Tag"/> enum on the IL stack is awkward to compare against an
    /// int constant), using a member that lives in this always-loaded core
    /// assembly — a persisted-bundle .dll can call it (a helper in the
    /// compiler assembly would not be visible to it). The JIT inlines it to
    /// the same shift+mask.</summary>
    public int TagId => (int)(Data >> TagShift) & 0xF;

    public long Payload => Data & PayloadMask;

    // The "AsX" accessors decode the low-32-bit id encoded in the payload. They do NOT
    // verify the tag; the caller is responsible for dispatching on Tag first.
    public int AsHeapIndex => (int)Data;
    public int AsAtomId => (int)Data;
    public int AsFunctorId => (int)Data;
    public int AsBigIntId => (int)Data;
    public int AsRationalId => (int)Data;
    public int AsForeignId => (int)Data;

    /// <summary>
    /// Decodes the inline 60-bit signed integer, sign-extending into the upper 4 bits.
    /// Only meaningful for cells with <see cref="Tag.Int"/>.
    /// </summary>
    public long AsInt
    {
        get
        {
            long p = Payload;
            if ((p & (1L << 59)) != 0)
                p |= unchecked((long)0xF000_0000_0000_0000UL);
            return p;
        }
    }

    // ---------- Factories ----------

    // AggressiveInlining on the hot Cell factories. The
    // bodies are 1-2 cycles of bit manipulation, but C# expression-
    // bodied static methods are not always inlined by the JIT
    // automatically. Cell.RawInt alone showed 0.73% exclusive on Blint
    // with user IL (called 10x per PushChoicePoint × millions of
    // pushes); Cell.Ref / Atom / Lis / Str hit every WAM get/put
    // instruction. Inlining collapses each call to a couple of x86
    // instructions in the caller.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell Ref(int heapIdx)
        => new(((long)Tag.Ref << TagShift) | (uint)heapIdx);

    /// <summary>An unbound variable: a REF cell whose payload points to its own heap index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell UnboundVar(int selfHeapIdx) => Ref(selfHeapIdx);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell Str(int functorHeapIdx)
        => new(((long)Tag.Str << TagShift) | (uint)functorHeapIdx);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell Lis(int headHeapIdx)
        => new(((long)Tag.Lis << TagShift) | (uint)headHeapIdx);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell Functor(int functorId)
        => new(((long)Tag.Functor << TagShift) | (uint)functorId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell Atom(int atomId)
        => new(((long)Tag.Atom << TagShift) | (uint)atomId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell Int(long value)
    {
        if (value < MinInt60 || value > MaxInt60)
            ThrowIntOutOfRange(value);
        return new Cell(((long)Tag.Int << TagShift) | (value & PayloadMask));
    }

    // Out-of-line so the JIT inlines Cell.Int's fast path; the throw
    // call site is small and stays after the inline.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIntOutOfRange(long value) =>
        throw new ArgumentOutOfRangeException(nameof(value),
            $"Integer {value} is outside the 60-bit signed inline range [{MinInt60}, {MaxInt60}]. Use BigInt for larger values.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell BigInt(int tableId)
        => new(((long)Tag.BigInt << TagShift) | (uint)tableId);

    /// <summary>An exact rational (ADR-039). Payload is an id into the
    /// activation's rational side table — the <see cref="BigInt"/> pattern.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell Rational(int tableId)
        => new(((long)Tag.Rational << TagShift) | (uint)tableId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell Foreign(int tableId)
        => new(((long)Tag.Foreign << TagShift) | (uint)tableId);

    /// <summary>An attributed variable: payload =
    /// the heap index of the variable's own home cell — exactly like a
    /// self-referencing <see cref="Ref"/>, but tagged ATTVAR so
    /// <see cref="Deref"/> stops at it instead of following it. The
    /// home index is also the key into the engine's attribute table,
    /// so a bare ATTVAR cell is fully self-describing (its identity
    /// and its attributes are both reachable from the payload alone).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell AttVar(int homeHeapIdx)
        => new(((long)Tag.AttVar << TagShift) | (uint)homeHeapIdx);

    /// <summary>A raw machine word (an environment / choice-point control
    /// slot — see <see cref="Tag.RawInt"/>). The value occupies the 60-bit
    /// payload; integer slots round-trip through a plain <c>(int)Data</c>
    /// cast (including negatives such as the -1 sentinel), and 60-bit
    /// slots through <see cref="Payload"/>. The distinct tag keeps the
    /// heap GC from relocating a control value as a <see cref="Ref"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell RawInt(long value)
        => new(((long)Tag.RawInt << TagShift) | (value & PayloadMask));

    // ---------- Float (spans two cells) ----------

    /// <summary>
    /// Encodes a double across two cells: a FLOAT header carrying the 4 high bits + the heap
    /// index of the paired cell, and an INT-tagged paired cell carrying the 60 low bits.
    /// The paired cell is structurally a valid INT but its numeric int value is meaningless
    /// — only <see cref="DecodeFloat"/> can reconstruct the original double.
    /// </summary>
    public static (Cell Header, Cell Paired) MakeFloat(double value, int pairedHeapIdx)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        long highBits = (bits >> 60) & 0xFL;
        long lowBits = bits & PayloadMask;

        var paired = new Cell(((long)Tag.Int << TagShift) | lowBits);
        long headerPayload = (highBits << 56) | (uint)pairedHeapIdx;
        var header = new Cell(((long)Tag.Float << TagShift) | headerPayload);
        return (header, paired);
    }

    public static double DecodeFloat(Cell header, Cell paired)
    {
        long payload = header.Payload;
        long highBits = (payload >> 56) & 0xFL;
        long lowBits = paired.Payload;
        long bits = (highBits << 60) | lowBits;
        return BitConverter.Int64BitsToDouble(bits);
    }

    /// <summary>Heap index of the paired INT cell. Only meaningful when <see cref="Tag"/> is <see cref="Tag.Float"/>.</summary>
    public int FloatPairedIndex => (int)Data;

    // ---------- PSTR (partial string) ----------
    //
    // PSTR header payload (60 bits): kind(1) | length(27) | bufferIdx(30) | offset(2)
    //   kind:      TextKind - bit 59, 1 = Chars, 0 = Codes (ADR-047)
    //   length:    UTF-16 code units in the string slice, 0..2^27-1
    //   bufferIdx: heap index of the first buffer cell, 0..2^30-1
    //   offset:    starting code-unit position within the first buffer cell, 0..2
    //
    // The kind bit sits at 59 — and the astral bit at 58 — not below the
    // length, so that bufferIdx and offset keep the bit positions they had
    // before either existed: the GC rebuilds a relocated header
    // (Engine.HeapGc.cs) and dropping a bit there would turn a list of chars
    // into a list of codes (or re-split astral characters) mid-collection.
    //
    // PSTR buffer payload (60 bits): reserved(12) | cu0(16) | cu1(16) | cu2(16)
    //   Three UTF-16 code units packed per buffer cell. The reserved high 12 bits
    //   are zero; cell tag is PstrBuffer.

    /// <summary>Code units packed per PSTR buffer cell.</summary>
    public const int PstrCodeUnitsPerBuffer = 3;

    /// <summary>Maximum representable PSTR length in code units. Bit 58 of
    /// the payload is the astral flag, so length spends 26 bits — 67M code
    /// units per PSTR, still far beyond any real text.</summary>
    public const int MaxPstrLength = (1 << 26) - 1;

    /// <summary>Maximum representable heap index for a PSTR buffer cell.</summary>
    public const int MaxPstrBufferIndex = (1 << 30) - 1;

    /// <summary><paramref name="kind"/> has no default: a slice must carry the
    /// kind of the PSTR it came from, and a producer must state what it is
    /// building. Defaulting it would make "forgot to pass it" indistinguishable
    /// from "meant Codes".</summary>
    public static Cell Pstr(int length, int bufferIdx, int offset, TextKind kind,
        bool astral)
    {
        if ((uint)length > (uint)MaxPstrLength)
            throw new ArgumentOutOfRangeException(nameof(length),
                $"PSTR length must be in [0, {MaxPstrLength}].");
        if ((uint)bufferIdx > (uint)MaxPstrBufferIndex)
            throw new ArgumentOutOfRangeException(nameof(bufferIdx),
                $"PSTR buffer index must be in [0, {MaxPstrBufferIndex}].");
        if ((uint)offset > 2)
            throw new ArgumentOutOfRangeException(nameof(offset),
                "PSTR offset must be 0, 1, or 2.");

        long payload = ((long)kind << 59)
                     | (astral ? 1L << 58 : 0)
                     | ((long)length << 32) | ((long)bufferIdx << 2) | (uint)offset;
        return new Cell(((long)Tag.Pstr << TagShift) | payload);
    }

    public static Cell PstrBuffer(int cu0, int cu1, int cu2)
    {
        long payload = ((long)(cu0 & 0xFFFF) << 32)
                     | ((long)(cu1 & 0xFFFF) << 16)
                     | (long)(cu2 & 0xFFFF);
        return new Cell(((long)Tag.PstrBuffer << TagShift) | payload);
    }

    /// <summary>Length in UTF-16 code units. Only meaningful for <see cref="Tag.Pstr"/> cells.</summary>
    public int AsPstrLength => (int)((Data >> 32) & 0x03FF_FFFFL);

    /// <summary>True when the packed text may contain a surrogate (an astral
    /// character or a lone half). BMP slices keep the exact one-unit-per-char
    /// fast paths; a set bit routes uncons through the pair-joining walk. The
    /// bit is conservative on slices — a slice of astral-flagged text stays
    /// flagged even if its own units are all BMP.</summary>
    public bool AsPstrIsAstral => (Data & (1L << 58)) != 0;

    /// <summary>Whether the packed list's elements are chars or codes (ADR-047).
    /// Only meaningful for <see cref="Tag.Pstr"/> cells.</summary>
    public TextKind AsPstrKind => (TextKind)((Data >> 59) & 1L);

    /// <summary>Heap index of the first buffer cell. Only meaningful for <see cref="Tag.Pstr"/> cells.</summary>
    public int AsPstrBufferIndex => (int)((Data >> 2) & 0x3FFF_FFFFL);

    /// <summary>Starting position (0..2) within the first buffer cell. Only meaningful for <see cref="Tag.Pstr"/> cells.</summary>
    public int AsPstrOffset => (int)(Data & 0x3L);

    /// <summary>Extracts code unit at <paramref name="pos"/> (0..2). Only meaningful for <see cref="Tag.PstrBuffer"/> cells.</summary>
    public int AsPstrCodeUnit(int pos)
    {
        int shift = 32 - pos * 16;
        return (int)((Data >> shift) & 0xFFFFL);
    }

    // ---------- Equality ----------

    public bool Equals(Cell other) => Data == other.Data;
    public override bool Equals(object? obj) => obj is Cell c && Equals(c);
    public override int GetHashCode() => Data.GetHashCode();
    public static bool operator ==(Cell a, Cell b) => a.Data == b.Data;
    public static bool operator !=(Cell a, Cell b) => a.Data != b.Data;

    public override string ToString()
        => $"Cell(tag={Tag}, payload=0x{Payload:X15}, data=0x{Data:X16})";
}
