namespace Shumway.Compiler.Wasm;

/// <summary>Marks a module's imported memory SHARED, after the fact.
///
/// <para>The browser's runtime memory is shared once threads are on, and an
/// import has to say so or instantiation is refused. The emitter this backend
/// builds on has no flag for it, so the bit is set where it lives: the limits
/// byte of the memory entry in the import section. The value goes from
/// <c>0x01</c> (a minimum and a maximum) to <c>0x03</c> (the same, shared),
/// one byte for one byte, so no section length moves and nothing else in the
/// module needs touching. This is the plan's D5, and doing it here rather than
/// in the browser keeps the risk off the critical path.</para></summary>
public static class WasmSharedMemory
{
    private const byte ImportSectionId = 2;
    private const byte MemoryImportKind = 2;
    private const byte LimitsMinMax = 0x01;
    private const byte LimitsMinMaxShared = 0x03;

    /// <summary>Returns the module with its imported memory marked shared.
    /// The input is not modified.</summary>
    public static byte[] Patch(byte[] module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var bytes = (byte[])module.Clone();
        int at = FindMemoryLimitsByte(bytes);
        if (bytes[at] != LimitsMinMax)
            throw new InvalidOperationException(
                $"The memory import's limits byte is 0x{bytes[at]:X2}; only a "
                + "minimum-and-maximum import (0x01) can be marked shared without "
                + "moving anything.");
        bytes[at] = LimitsMinMaxShared;
        return bytes;
    }

    /// <summary>True when the module's imported memory is already shared.</summary>
    public static bool IsShared(byte[] module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return module[FindMemoryLimitsByte(module)] == LimitsMinMaxShared;
    }

    private static int FindMemoryLimitsByte(byte[] bytes)
    {
        int p = 8;                                   // magic + version
        while (p < bytes.Length)
        {
            byte id = bytes[p++];
            uint size = ReadVarUInt32(bytes, ref p);
            int end = p + (int)size;
            if (id != ImportSectionId) { p = end; continue; }

            uint count = ReadVarUInt32(bytes, ref p);
            for (uint i = 0; i < count; i++)
            {
                SkipName(bytes, ref p);              // module
                SkipName(bytes, ref p);              // field
                byte kind = bytes[p++];
                switch (kind)
                {
                    case MemoryImportKind:
                        return p;                    // the limits byte itself
                    case 0:                          // function: a type index
                    case 3:                          // global: a value type...
                        ReadVarUInt32(bytes, ref p);
                        if (kind == 3) p++;          // ...plus its mutability
                        break;
                    case 1:                          // table: element type, limits
                        p++;
                        SkipLimits(bytes, ref p);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown import kind 0x{kind:X2} at offset {p - 1}.");
                }
            }
            throw new InvalidOperationException("The module imports no memory.");
        }
        throw new InvalidOperationException("The module has no import section.");
    }

    private static void SkipName(byte[] bytes, ref int p)
    {
        // In two steps on purpose: `p += Read(ref p)` reads the left side
        // before the call advances it, so the length's own bytes go missing
        // and the walk drifts into the next section.
        uint length = ReadVarUInt32(bytes, ref p);
        p += (int)length;
    }

    private static void SkipLimits(byte[] bytes, ref int p)
    {
        byte flags = bytes[p++];
        ReadVarUInt32(bytes, ref p);                 // minimum
        if ((flags & 0x01) != 0) ReadVarUInt32(bytes, ref p);
    }

    private static uint ReadVarUInt32(byte[] bytes, ref int p)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = bytes[p++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 28)
                throw new InvalidOperationException("Malformed LEB128 in the module.");
        }
    }
}
