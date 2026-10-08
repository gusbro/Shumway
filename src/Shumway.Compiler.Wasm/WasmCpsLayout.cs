using WebAssembly;

namespace Shumway.Compiler.Wasm;

/// <summary>ADR-061: what one continuation function of a module holds.</summary>
public enum WasmCpsGrain
{
    /// <summary>One entry point: a member's entry or the return of a
    /// predicate call, with the alternatives that follow it.</summary>
    EntryPoint,
    /// <summary>Whole members up to the partition budget, as the partitions
    /// of the other form.</summary>
    Budget,
}

/// <summary>ADR-061: where a module of continuation functions keeps its
/// layout, so the world that installs it can write each resume row with the
/// function holding the cursor. It travels inside the module, as a custom
/// section, so a module baked at build time carries it as one compiled now
/// does.
///
/// <para>Content: the module's cursor count, then the first cursor of each
/// function in order (function f, from 1, holds the cursors from its first
/// up to the next one's), all unsigned LEB128.</para></summary>
public static class WasmCpsLayout
{
    public const string SectionName = "shumway.cps";

    public static CustomSection Section(int cursorCount, IEnumerable<int> firstCursors)
    {
        var payload = new List<byte>();
        Leb(payload, (uint)cursorCount);
        var firsts = firstCursors.ToList();
        Leb(payload, (uint)firsts.Count);
        foreach (int c in firsts) Leb(payload, (uint)c);
        return new CustomSection { Name = SectionName, Content = payload };
    }

    /// <summary>Cursor to function (from 1), or null for a module without the
    /// section: one entered through run at every cursor.</summary>
    public static int[]? FunctionOfCursor(byte[] module)
    {
        int at = 8;                                         // magic + version
        while (at < module.Length)
        {
            int id = module[at++];
            int size = (int)ReadLeb(module, ref at);
            int end = at + size;
            if (id == 0)
            {
                int p = at;
                int nameLength = (int)ReadLeb(module, ref p);
                if (nameLength == SectionName.Length
                    && System.Text.Encoding.UTF8.GetString(module, p, nameLength) == SectionName)
                {
                    p += nameLength;
                    int cursors = (int)ReadLeb(module, ref p);
                    int functions = (int)ReadLeb(module, ref p);
                    var map = new int[cursors];
                    int previous = 0, f = 0;
                    for (int k = 0; k < functions; k++)
                    {
                        int first = (int)ReadLeb(module, ref p);
                        for (int c = previous; c < first; c++) map[c] = f;
                        previous = first;
                        f = k + 1;
                    }
                    for (int c = previous; c < cursors; c++) map[c] = f;
                    return map;
                }
            }
            at = end;
        }
        return null;
    }

    private static void Leb(List<byte> o, uint v)
    {
        do { byte b = (byte)(v & 0x7f); v >>= 7; if (v != 0) b |= 0x80; o.Add(b); } while (v != 0);
    }

    private static long ReadLeb(byte[] b, ref int at)
    {
        long v = 0; int shift = 0;
        while (true)
        {
            byte x = b[at++];
            v |= (long)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return v;
            shift += 7;
        }
    }
}
