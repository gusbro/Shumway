namespace Shumway.Core;

/// <summary>
/// A single decoded instruction, suitable for inspection and printing. <see cref="Address"/>
/// is the offset of the opcode byte in the original bytecode; <see cref="Operands"/>
/// holds the 32-bit operands in source order (empty for zero-operand instructions);
/// <see cref="MetaSubOpcode"/> is non-null only for <see cref="Opcode.Meta"/> instructions.
/// </summary>
public readonly record struct DisassembledInstruction(
    int Address,
    Opcode Op,
    string Mnemonic,
    int[] Operands,
    MetaSubOpcode? MetaSubOpcode)
{
    public override string ToString()
    {
        if (Operands.Length == 0)
            return $"0x{Address:X4}: {Mnemonic}";
        return $"0x{Address:X4}: {Mnemonic} {string.Join(", ", Operands)}";
    }
}

/// <summary>
/// Walks a bytecode region and yields one <see cref="DisassembledInstruction"/> per
/// opcode. The walker consults <see cref="OpcodeTable"/> for the instruction size and
/// reads 32-bit operands as little-endian via <see cref="BytecodeIO"/>. The Meta opcode
/// (<see cref="Opcode.Meta"/>) is handled inline: its sub-byte is decoded into
/// <see cref="MetaSubOpcode"/> and the rest of the operands are read accordingly.
///
/// <para>Unknown opcodes (gaps in the table) and bytecode that runs off <paramref name="end"/>
/// mid-instruction both throw — they signal corruption or a mismatched offset and there
/// is no useful recovery here.</para>
/// </summary>
public static class Disassembler
{
    public static IEnumerable<DisassembledInstruction> Iterate(byte[] code, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (start < 0 || end > code.Length || start > end)
            throw new ArgumentOutOfRangeException(nameof(start),
                $"Range [{start}, {end}) is not within [0, {code.Length}].");

        int p = start;
        while (p < end)
        {
            byte opByte = code[p];

            if (opByte == (byte)Opcode.Meta)
            {
                yield return DecodeMeta(code, p, end);
                p += MetaSize(code, p);
                continue;
            }

            // a_int_bin / a_int_cmp use a packed kind/op word (Phase 26 compact
            // encoding); unpack it so the operands read [op, aKind, aVal, ...].
            if (opByte == (byte)Opcode.AIntBin)
            {
                int packed = BytecodeIO.ReadInt32(code, p + 1);
                yield return new DisassembledInstruction(p, Opcode.AIntBin, "a_int_bin",
                    new[]
                    {
                        (packed >> 24) & 0xFF, packed & 0xFF, BytecodeIO.ReadInt32(code, p + 5),
                        (packed >> 8) & 0xFF, BytecodeIO.ReadInt32(code, p + 9),
                        (packed >> 16) & 0xFF, BytecodeIO.ReadInt32(code, p + 13),
                    }, null);
                p += 17;
                continue;
            }
            if (opByte == (byte)Opcode.AIntCmp)
            {
                int packed = BytecodeIO.ReadInt32(code, p + 1);
                yield return new DisassembledInstruction(p, Opcode.AIntCmp, "a_int_cmp",
                    new[]
                    {
                        (packed >> 16) & 0xFF, packed & 0xFF, BytecodeIO.ReadInt32(code, p + 5),
                        (packed >> 8) & 0xFF, BytecodeIO.ReadInt32(code, p + 9),
                    }, null);
                p += 13;
                continue;
            }

            var info = OpcodeTable.Get(opByte);
            if (!info.IsDefined)
                throw new InvalidOperationException(
                    $"Unknown opcode 0x{opByte:X2} at offset 0x{p:X4}.");

            if (p + info.Size > end)
                throw new InvalidOperationException(
                    $"Bytecode truncated: instruction 0x{opByte:X2} at offset 0x{p:X4} " +
                    $"needs {info.Size} bytes but only {end - p} are available.");

            int[] operands = info.NumOperands == 0 ? Array.Empty<int>() : new int[info.NumOperands];
            for (int i = 0; i < info.NumOperands; i++)
                operands[i] = BytecodeIO.ReadInt32(code, p + 1 + i * 4);

            yield return new DisassembledInstruction(
                Address: p,
                Op: info.Op,
                Mnemonic: info.Mnemonic!,
                Operands: operands,
                MetaSubOpcode: null);

            p += info.Size;
        }
    }

    private static DisassembledInstruction DecodeMeta(byte[] code, int offset, int end)
    {
        if (offset + 2 > end)
            throw new InvalidOperationException(
                $"Bytecode truncated: meta opcode at offset 0x{offset:X4} is missing its sub-byte.");

        var sub = (MetaSubOpcode)code[offset + 1];
        return sub switch
        {
            Shumway.Core.MetaSubOpcode.DbgInfo => DecodeMetaDbgInfo(code, offset, end),
            _ => throw new InvalidOperationException(
                $"Unknown meta sub-opcode 0x{(byte)sub:X2} at offset 0x{offset:X4}."),
        };
    }

    private static DisassembledInstruction DecodeMetaDbgInfo(byte[] code, int offset, int end)
    {
        const int size = 6;
        if (offset + size > end)
            throw new InvalidOperationException(
                $"Bytecode truncated: meta dbg_info at offset 0x{offset:X4} needs {size} bytes.");

        int entryId = BytecodeIO.ReadInt32(code, offset + 2);
        return new DisassembledInstruction(
            Address: offset,
            Op: Opcode.Meta,
            Mnemonic: "meta dbg_info",
            Operands: new[] { entryId },
            MetaSubOpcode: Shumway.Core.MetaSubOpcode.DbgInfo);
    }

    private static int MetaSize(byte[] code, int offset)
    {
        var sub = (MetaSubOpcode)code[offset + 1];
        return sub switch
        {
            Shumway.Core.MetaSubOpcode.DbgInfo => 6,
            _ => throw new InvalidOperationException(
                $"Unknown meta sub-opcode 0x{(byte)sub:X2} at offset 0x{offset:X4}."),
        };
    }
}
