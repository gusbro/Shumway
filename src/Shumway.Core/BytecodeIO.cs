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

    public static void WriteInt32(byte[] code, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(offset), value);

    public static void WriteInt32(Span<byte> code, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(code[offset..], value);
}
