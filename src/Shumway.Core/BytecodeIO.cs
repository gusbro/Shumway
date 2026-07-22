using System.Buffers.Binary;

namespace Shumway.Core;

/// <summary>
/// Little-endian read/write helpers for bytecode operands. Using
/// <see cref="BinaryPrimitives"/> guarantees little-endian on every platform regardless
/// of CPU endianness, which keeps serialized bytecode (notably bundles) portable across
/// x86, x64, and ARM (ADR-006).
/// </summary>
public static class BytecodeIO
{
    public static int ReadInt32(byte[] code, int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(offset));

    public static int ReadInt32(ReadOnlySpan<byte> code, int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(code[offset..]);

    /// <summary>read a 4-byte int across a split
    /// <see cref="ProgramView"/>. Routes to the underlying array;
    /// for the single-buffer common case this is the same as the
    /// byte[] overload (the implicit conversion picks Primary).
    /// Multi-byte reads that straddle the split boundary are
    /// problem — bytecode emission always writes a
    /// chain instruction's operand entirely within one region.</summary>
    public static int ReadInt32(in ProgramView code, int offset)
    {
        if (code.Overflow is null || offset + 4 <= code.Split)
            return ReadInt32(code.Primary, offset);
        return ReadInt32(code.Overflow, offset - code.Split);
    }

    public static void WriteInt32(byte[] code, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(offset), value);

    public static void WriteInt32(Span<byte> code, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(code[offset..], value);

    public static long ReadInt64(byte[] code, int offset)
        => BinaryPrimitives.ReadInt64LittleEndian(code.AsSpan(offset));

    public static long ReadInt64(ReadOnlySpan<byte> code, int offset)
        => BinaryPrimitives.ReadInt64LittleEndian(code[offset..]);

    /// <summary><see cref="ProgramView"/> overload —
    /// see <see cref="ReadInt32(in ProgramView, int)"/> for the
    /// routing rationale.</summary>
    public static long ReadInt64(in ProgramView code, int offset)
    {
        if (code.Overflow is null || offset + 8 <= code.Split)
            return ReadInt64(code.Primary, offset);
        return ReadInt64(code.Overflow, offset - code.Split);
    }

    public static void WriteInt64(byte[] code, int offset, long value)
        => BinaryPrimitives.WriteInt64LittleEndian(code.AsSpan(offset), value);

    public static void WriteInt64(Span<byte> code, int offset, long value)
        => BinaryPrimitives.WriteInt64LittleEndian(code[offset..], value);
}
