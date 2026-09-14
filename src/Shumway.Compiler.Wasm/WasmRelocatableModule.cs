using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Shumway.Core;

namespace Shumway.Compiler.Wasm;

/// <summary>A member of a relocatable module: which predicate, and its
/// re-entry cursors by bytecode offset (offset 0 is the fresh entry).
/// </summary>
public sealed record WasmRelocatableMember(
    string Name, int Arity, IReadOnlyList<(int Offset, int Cursor)> CursorByOffset);

/// <summary>A group module compiled once and installable into ANY process:
/// every immediate that names process state is a sentinel with a
/// relocation record (<see cref="RelocatingCompileEnv"/>), patched in
/// place on install against the live intern tables and link. The wasm
/// travels beside the bytecode it was compiled from, so the install trusts
/// the bytecode and re-checks only what it cannot see: that every member
/// and builtin exists, and that the builtin-form decisions still hold.
/// </summary>
public sealed class WasmRelocatableModule
{
    private const uint Magic = 0x4D52_5753;   // "SWRM"
    private const byte I32Const = 0x41, I64Const = 0x42, CodeSectionId = 10;
    private const int IntWidth = 5, CellWidth = 10;

    public byte[] Bytes { get; private set; } = Array.Empty<byte>();
    public int RegisterDemand { get; private set; }
    public IReadOnlyList<WasmRelocatableMember> Members { get; private set; }
        = Array.Empty<WasmRelocatableMember>();
    public IReadOnlyList<WasmRelocation> Relocations { get; private set; }
        = Array.Empty<WasmRelocation>();
    public IReadOnlyList<WasmBuiltinEvidence> Builtins { get; private set; }
        = Array.Empty<WasmBuiltinEvidence>();
    /// <summary>(caller, callee) edges, as member/functor names.</summary>
    public IReadOnlyList<(string CallerName, int CallerArity, string CalleeName, int CalleeArity)>
        CallSites { get; private set; }
        = Array.Empty<(string, int, string, int)>();

    private WasmRelocatableModule() { }

    /// <summary>Compiles the members with sentinels and locates every
    /// sentinel in the code section. <paramref name="liveEnv"/> supplies
    /// the builtin-form decisions; nothing of its encodings is baked.
    /// </summary>
    public static WasmRelocatableModule Bake(IReadOnlyList<WasmGroupMember> members,
                                            IWasmCompileEnv liveEnv)
    {
        var env = new RelocatingCompileEnv(members, liveEnv);
        var entry = WasmPredicateCompiler.CompileGroup(members, env);
        var relocs = new List<WasmRelocation>(env.Relocations);
        Scan(entry.Module, relocs);

        var mems = new List<WasmRelocatableMember>(members.Count);
        var byIndex = new Dictionary<int, int>();
        var cursors = new List<(int, int)>[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            byIndex[members[i].Predicate.FunctorId] = i;
            cursors[i] = new List<(int, int)>();
        }
        foreach (var (fid, cursor) in entry.EntryCursorByFid)
            cursors[byIndex[fid]].Add((0, cursor));
        foreach (var (addr, cursor) in entry.CursorByAddress)
        {
            int owner = OwnerOf(members, addr);
            if (owner < 0) throw new WasmCompileException($"cursor address {addr} lies in no member");
            int off = addr - members[owner].Bias;
            if (off != 0) cursors[owner].Add((off, cursor));
        }
        for (int i = 0; i < members.Count; i++)
        {
            var (name, arity) = RelocatingCompileEnv.NameOf(members[i].Predicate.FunctorId);
            mems.Add(new WasmRelocatableMember(name, arity, cursors[i]));
        }
        var sites = new List<(string, int, string, int)>(entry.CallSites.Count);
        foreach (var (caller, callee) in entry.CallSites.Keys)
        {
            var (cn, ca) = RelocatingCompileEnv.NameOf(caller);
            var (en, ea) = RelocatingCompileEnv.NameOf(callee);
            sites.Add((cn, ca, en, ea));
        }
        return new WasmRelocatableModule
        {
            Bytes = entry.Module, RegisterDemand = entry.RegisterDemand, Members = mems,
            Relocations = relocs, Builtins = new List<WasmBuiltinEvidence>(env.Evidence),
            CallSites = sites,
        };
    }

    // ---- the byte scan ----

    /// <summary>Finds every sentinel site in the code section. Sentinels
    /// are matched by their exact padded form after an i32.const/i64.const
    /// opcode byte; a miss advances one byte, so an immediate that happens
    /// to hold 0x41 cannot desynchronise the walk (a genuine sentinel is
    /// always preceded by its opcode, and no opcode byte is a continuation
    /// byte). Every relocation must be found at least once.</summary>
    private static void Scan(byte[] bytes, List<WasmRelocation> relocs)
    {
        var sites = new List<int>[relocs.Count];
        for (int i = 0; i < sites.Length; i++) sites[i] = new List<int>();
        var (start, end) = CodeSection(bytes);
        int p = start;
        while (p < end)
        {
            byte op = bytes[p];
            if (op != I32Const && op != I64Const) { p++; continue; }
            int len = DecodeLeb(bytes, p + 1, end, out long value);
            int slot = -1, width = 0;
            if (len == IntWidth && value >= RelocatingCompileEnv.IntSentinelBase
                && value <= RelocatingCompileEnv.IntSentinelBase + RelocatingCompileEnv.SlotMask)
            {
                slot = (int)(value - RelocatingCompileEnv.IntSentinelBase);
                width = IntWidth;
            }
            else if (op == I64Const && len == CellWidth
                     && (value & ~(long)RelocatingCompileEnv.SlotMask)
                        == RelocatingCompileEnv.CellSentinelBase)
            {
                slot = (int)(value & RelocatingCompileEnv.SlotMask);
                width = CellWidth;
            }
            if (slot < 0) { p++; continue; }
            if (slot >= relocs.Count)
                throw new WasmCompileException($"sentinel of unknown slot {slot} at byte {p}");
            if (relocs[slot].Wide != (width == CellWidth))
                throw new WasmCompileException($"slot {slot} emitted at the wrong width at byte {p}");
            sites[slot].Add(p + 1);
            p += 1 + len;
        }
        for (int i = 0; i < relocs.Count; i++)
        {
            if (sites[i].Count == 0)
                throw new WasmCompileException($"relocation {i} ({relocs[i].Kind} {relocs[i].Name}/{relocs[i].Arity}) was never emitted");
            relocs[i].Sites = sites[i].ToArray();
        }
    }

    private static (int Start, int End) CodeSection(byte[] bytes)
    {
        int p = 8;                                   // magic + version
        while (p < bytes.Length)
        {
            byte id = bytes[p++];
            int size = (int)ReadVarUInt32(bytes, ref p);
            if (id == CodeSectionId) return (p, p + size);
            p += size;
        }
        throw new WasmCompileException("the module has no code section");
    }

    private static uint ReadVarUInt32(byte[] bytes, ref int p)
    {
        uint result = 0; int shift = 0; byte b;
        do
        {
            b = bytes[p++];
            result |= (uint)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return result;
    }

    /// <summary>Signed LEB128 at <paramref name="p"/>; returns the byte
    /// length, or 0 when it runs past <paramref name="end"/> or 10 bytes.
    /// </summary>
    private static int DecodeLeb(byte[] bytes, int p, int end, out long value)
    {
        value = 0; int shift = 0; int n = 0; byte b;
        do
        {
            if (p + n >= end || n == CellWidth) return 0;
            b = bytes[p + n++];
            value |= (long)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        if (shift < 64 && (b & 0x40) != 0) value |= -1L << shift;
        return n;
    }

    private static void WritePaddedLeb(byte[] bytes, int at, long value, int width)
    {
        for (int i = 0; i < width; i++)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (i < width - 1) b |= 0x80;
            bytes[at + i] = b;
        }
        // The padded form is exact only when the leftover bits are the sign.
        bool neg = (bytes[at + width - 1] & 0x40) != 0;
        if (value != (neg ? -1 : 0))
            throw new WasmCompileException($"relocated value does not fit {width} bytes");
    }

    // ---- install ----

    /// <summary>Patches a copy of the bytes against the live process and
    /// returns what <see cref="WasmGroupInstall.InstallInto"/> needs. False,
    /// with the reason, when a member is not linked, a builtin is missing
    /// or its form decision changed.</summary>
    /// <param name="biasOf">The linked base of a member functor, or -1.</param>
    public bool TryResolve(IWasmCompileEnv env, Func<int, int> biasOf, int moduleId,
        out WasmGroupEntry entry, out Dictionary<int, int> entryAddressByFid, out string reason)
    {
        entry = null!;
        entryAddressByFid = new Dictionary<int, int>(Members.Count);
        var biasByName = new Dictionary<(string, int), int>(Members.Count);
        var fidByName = new Dictionary<(string, int), int>(Members.Count);
        foreach (var m in Members)
        {
            int fid = Functor(m.Name, m.Arity);
            int bias = biasOf(fid);
            if (bias < 0) { reason = $"member {m.Name}/{m.Arity} is not linked"; return false; }
            biasByName[(m.Name, m.Arity)] = bias;
            fidByName[(m.Name, m.Arity)] = fid;
            entryAddressByFid[fid] = bias;
        }
        foreach (var ev in Builtins)
        {
            int fid = Functor(ev.Name, ev.Arity);
            bool found = env.TryGetBuiltin(fid, out int id);
            bool same = found == ev.Found;
            if (same && found)
                same = env.IsDirectBuiltin(id) == ev.Direct
                    && env.IsInlineUnify(id) == ev.InlineUnify
                    && env.IsInlineCompare(id, out bool neg) == ev.InlineCompare
                    && neg == ev.Negated;
            if (!same) { reason = $"builtin {ev.Name}/{ev.Arity} changed form"; return false; }
        }

        var bytes = (byte[])Bytes.Clone();
        foreach (var r in Relocations)
        {
            long value;
            switch (r.Kind)
            {
                case WasmRelocKind.Marker:
                    value = env.EncodeBp(fidByName[(r.Name, r.Arity)],
                                         biasByName[(r.Name, r.Arity)] + r.Offset);
                    break;
                case WasmRelocKind.CallTarget:
                    value = env.EncodeCallTarget(Functor(r.Name, r.Arity));
                    break;
                case WasmRelocKind.Address:
                    value = env.EncodeAddress(biasByName[(r.Name, r.Arity)] + r.Offset);
                    break;
                case WasmRelocKind.Builtin:
                    if (!env.TryGetBuiltin(Functor(r.Name, r.Arity), out int id))
                    { reason = $"builtin {r.Name}/{r.Arity} is missing"; return false; }
                    value = env.EncodeBuiltinId(id, r.Offset);
                    break;
                case WasmRelocKind.Atom:
                    value = env.AtomCell(AtomTable.Intern(r.Name).Id);
                    break;
                case WasmRelocKind.Functor:
                    value = env.FunctorCell(Functor(r.Name, r.Arity));
                    break;
                case WasmRelocKind.ModuleId:
                    value = env.EncodeModuleId(moduleId);
                    break;
                default:
                    reason = $"unknown relocation kind {r.Kind}"; return false;
            }
            int width = r.Wide ? CellWidth : IntWidth;
            foreach (int at in r.Sites) WritePaddedLeb(bytes, at, value, width);
        }

        var entryCursors = new Dictionary<int, int>(Members.Count);
        var cursorByAddress = new Dictionary<int, int>();
        foreach (var m in Members)
        {
            int fid = fidByName[(m.Name, m.Arity)];
            int bias = biasByName[(m.Name, m.Arity)];
            foreach (var (off, cursor) in m.CursorByOffset)
            {
                cursorByAddress[bias + off] = cursor;
                if (off == 0) entryCursors[fid] = cursor;
            }
        }
        var callSites = new Dictionary<(int, int), int>(CallSites.Count);
        foreach (var (cn, ca, en, ea) in CallSites)
            callSites[(Functor(cn, ca), Functor(en, ea))] = 1;
        entry = new WasmGroupEntry(bytes, entryCursors, cursorByAddress, RegisterDemand,
                                   callSites, moduleId);
        reason = $"{Members.Count} predicates";
        return true;
    }

    private static int Functor(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name).Id, arity);

    // Members are contiguous: the end of one is the start of the next, and
    // an address at a member's bias is THAT member's entry, not the
    // predecessor's end cursor. The end of the last member is still legal
    // (the cursor after a trailing call).
    internal static int OwnerOf(IReadOnlyList<WasmGroupMember> members, int address)
    {
        for (int i = 0; i < members.Count; i++)
            if (members[i].Bias == address) return i;
        for (int i = 0; i < members.Count; i++)
        {
            int bias = members[i].Bias;
            if (address > bias && address <= bias + members[i].Predicate.Bytecode.Length)
                return i;
        }
        return -1;
    }

    // ---- serialization ----

    public void Write(Stream stream) => Write(stream, Relocations, Builtins);

    // With substituted tables: how a test forges a tampered file.
    internal void Write(Stream stream, IReadOnlyList<WasmRelocation> relocations,
                        IReadOnlyList<WasmBuiltinEvidence> builtins)
    {
        var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(RegisterDemand);
        w.Write(Bytes.Length); w.Write(Bytes);
        w.Write(Members.Count);
        foreach (var m in Members)
        {
            w.Write(m.Name); w.Write(m.Arity);
            w.Write(m.CursorByOffset.Count);
            foreach (var (off, cursor) in m.CursorByOffset) { w.Write(off); w.Write(cursor); }
        }
        w.Write(relocations.Count);
        foreach (var r in relocations)
        {
            w.Write((byte)r.Kind); w.Write(r.Name); w.Write(r.Arity); w.Write(r.Offset);
            w.Write(r.Wide);
            w.Write(r.Sites.Length);
            foreach (int s in r.Sites) w.Write(s);
        }
        w.Write(builtins.Count);
        foreach (var b in builtins)
        {
            w.Write(b.Name); w.Write(b.Arity); w.Write(b.Found); w.Write(b.Direct);
            w.Write(b.InlineUnify); w.Write(b.InlineCompare); w.Write(b.Negated);
        }
        w.Write(CallSites.Count);
        foreach (var (cn, ca, en, ea) in CallSites)
        { w.Write(cn); w.Write(ca); w.Write(en); w.Write(ea); }
        w.Flush();
    }

    public byte[] ToBytes()
    {
        var ms = new MemoryStream();
        Write(ms);
        return ms.ToArray();
    }

    public static WasmRelocatableModule Read(Stream stream)
    {
        var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("not a relocatable wasm module");
        var m = new WasmRelocatableModule { RegisterDemand = r.ReadInt32() };
        m.Bytes = r.ReadBytes(r.ReadInt32());
        int n = r.ReadInt32();
        var members = new List<WasmRelocatableMember>(n);
        for (int i = 0; i < n; i++)
        {
            string name = r.ReadString(); int arity = r.ReadInt32();
            int k = r.ReadInt32();
            var cursors = new List<(int, int)>(k);
            for (int j = 0; j < k; j++) cursors.Add((r.ReadInt32(), r.ReadInt32()));
            members.Add(new WasmRelocatableMember(name, arity, cursors));
        }
        m.Members = members;
        n = r.ReadInt32();
        var relocs = new List<WasmRelocation>(n);
        for (int i = 0; i < n; i++)
        {
            var kind = (WasmRelocKind)r.ReadByte();
            string name = r.ReadString(); int arity = r.ReadInt32(); int off = r.ReadInt32();
            bool wide = r.ReadBoolean();
            var sites = new int[r.ReadInt32()];
            for (int j = 0; j < sites.Length; j++) sites[j] = r.ReadInt32();
            relocs.Add(new WasmRelocation(kind, name, arity, off, wide) { Sites = sites });
        }
        m.Relocations = relocs;
        n = r.ReadInt32();
        var builtins = new List<WasmBuiltinEvidence>(n);
        for (int i = 0; i < n; i++)
            builtins.Add(new WasmBuiltinEvidence(r.ReadString(), r.ReadInt32(), r.ReadBoolean(),
                r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean()));
        m.Builtins = builtins;
        n = r.ReadInt32();
        var sites2 = new List<(string, int, string, int)>(n);
        for (int i = 0; i < n; i++)
            sites2.Add((r.ReadString(), r.ReadInt32(), r.ReadString(), r.ReadInt32()));
        m.CallSites = sites2;
        return m;
    }
}
