namespace Shumway.Compiler.Il;

/// <summary>
/// What kind of build-time identifier a patch site references.
/// Determines how the runtime value is computed from the (name, arity)
/// pair at LoadBundle time.
/// </summary>
public enum IlPatchKind : byte
{
    /// <summary>Atom id — emitted by <c>PutAtom</c>, <c>GetAtom</c>,
    /// <c>UnifyAtom</c>, and the chunk-189 indexed-atom dispatch
    /// comparand. Resolved as <c>AtomTable.Intern(Name).Id</c>.</summary>
    Atom = 1,

    /// <summary>Functor id — emitted by <c>PutStructure</c>,
    /// <c>GetStructure</c>, and the <c>Call</c> / <c>Execute</c> resolve
    /// helpers. Resolved as <c>FunctorTable.Intern(AtomTable.Intern(Name).Id, Arity)</c>.</summary>
    Functor = 2,

    /// <summary>Phase-16 resume marker — the int the IL caller writes
    /// into <c>engine.Cp</c> before tail-calling a non-tail callee. The
    /// marker encodes <c>(owner-functor-id, cursor)</c>; only the functor
    /// id needs runtime remapping, the cursor is owner-local.</summary>
    ResumeMarker = 3,
}

/// <summary>
/// One IL constant the persisted .dll baked at build time but needs to
/// hold a runtime-process value at load time. The emit pipeline writes a
/// unique sentinel int (in a reserved range) at the IL site; the post-
/// Save PE scan locates the four-byte int operand of the corresponding
/// <c>ldc.i4</c> instruction and records its absolute byte offset within
/// the .dll bytes. <see cref="PrologEngine.LoadBundle"/> walks the patch
/// list, looks up the runtime id for each <c>(Name, Arity)</c>, and
/// overwrites the four bytes at <see cref="AbsoluteByteOffset"/> before
/// calling <c>Assembly.Load</c>.
/// </summary>
public sealed class IlPatchSite
{
    /// <summary>The unique sentinel emitted into the IL as the
    /// <c>ldc.i4</c> operand. Used to locate the patch site by scanning
    /// the saved PE bytes — each sentinel value occurs exactly once.</summary>
    public required int Sentinel { get; init; }

    public required IlPatchKind Kind { get; init; }

    /// <summary>The atom name (for <see cref="IlPatchKind.Atom"/>) or
    /// the functor name (for <see cref="IlPatchKind.Functor"/> and
    /// <see cref="IlPatchKind.ResumeMarker"/>).</summary>
    public required string Name { get; init; }

    /// <summary>The functor arity. Unused for <see cref="IlPatchKind.Atom"/>.</summary>
    public required int Arity { get; init; }

    /// <summary>The forward-resume cursor — only meaningful for
    /// <see cref="IlPatchKind.ResumeMarker"/>. Combined with the runtime
    /// functor id via <c>Engine.EncodeResumeMarker</c> to compute the
    /// runtime marker value.</summary>
    public int Cursor { get; init; }

    /// <summary>The absolute byte offset within the saved PE bytes
    /// where the four-byte int operand lives. Filled by the post-Save
    /// scan in <see cref="PersistedIlBuilder.Build"/>.</summary>
    public int AbsoluteByteOffset { get; set; }
}

/// <summary>
/// Per-persisted-method runtime-binding info. Each emitted method
/// represents one Prolog predicate; <see cref="PrologEngine.LoadBundle"/>
/// reads this list, interns each <c>(Name, Arity)</c> in the current
/// process, and registers the resolved delegate under the
/// <em>runtime</em> functor id. Without this the delegate would be
/// keyed off the build-time functor id baked into the method name —
/// the cross-process functor-id drift Phase 17 set out to fix.
/// </summary>
public sealed class IlPersistedEntry
{
    public required int Slot { get; init; }
    public required string Name { get; init; }
    public required int Arity { get; init; }
    public required string MethodName { get; init; }

    /// <summary>Serialised WAM-independent dispatch graph for an indexed
    /// predicate (<see cref="IndexGraphCodec"/>); empty/absent for a
    /// self-contained shape. Registered at LoadBundle so a stripped indexed
    /// predicate dispatches without a WAM body.</summary>
    public byte[]? IndexGraph { get; init; }
}

public static class IlPersistedEntryCodec
{
    public const uint Magic = 0x534C4950u; // "PILS"

    public static byte[] Encode(IReadOnlyList<IlPersistedEntry> entries)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write(Magic);
        bw.Write((uint)entries.Count);
        foreach (var e in entries)
        {
            bw.Write(e.Slot);
            bw.Write(e.Arity);
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(e.Name);
            bw.Write((uint)nameBytes.Length);
            bw.Write(nameBytes);
            byte[] methodBytes = System.Text.Encoding.UTF8.GetBytes(e.MethodName);
            bw.Write((uint)methodBytes.Length);
            bw.Write(methodBytes);
            byte[] graph = e.IndexGraph ?? System.Array.Empty<byte>();
            bw.Write((uint)graph.Length);
            bw.Write(graph);
        }
        bw.Flush();
        return ms.ToArray();
    }

    public static List<IlPersistedEntry> Decode(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return new List<IlPersistedEntry>();
        using var ms = new MemoryStream(bytes, writable: false);
        using var br = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        uint magic = br.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException(
                $"IL persisted-entry table magic mismatch (got 0x{magic:X8}, expected 0x{Magic:X8}).");
        uint count = br.ReadUInt32();
        var result = new List<IlPersistedEntry>((int)count);
        for (uint i = 0; i < count; i++)
        {
            int slot = br.ReadInt32();
            int arity = br.ReadInt32();
            uint nameLen = br.ReadUInt32();
            string name = System.Text.Encoding.UTF8.GetString(br.ReadBytes((int)nameLen));
            uint methodLen = br.ReadUInt32();
            string methodName = System.Text.Encoding.UTF8.GetString(br.ReadBytes((int)methodLen));
            uint graphLen = br.ReadUInt32();
            byte[]? graph = graphLen == 0 ? null : br.ReadBytes((int)graphLen);
            result.Add(new IlPersistedEntry
            {
                Slot = slot,
                Name = name,
                Arity = arity,
                MethodName = methodName,
                IndexGraph = graph,
            });
        }
        return result;
    }
}

/// <summary>
/// Wire format for the per-bundle-entry patch table. Layout (all little-
/// endian):
/// <code>
///   uint32 count
///   for each:
///     int32  AbsoluteByteOffset
///     int32  Sentinel (debug / verification only)
///     byte   Kind
///     int32  Arity
///     int32  Cursor
///     int32  NameLength
///     bytes  Name (UTF-8)
/// </code>
/// </summary>
public static class IlPatchSiteCodec
{
    /// <summary>Magic prefix so a stale reader that finds a V2 (no patch
    /// table) bundle followed by garbage doesn't misinterpret it as a
    /// patch table.</summary>
    public const uint Magic = 0x53494C50u; // "PLIS" little-endian = bytes P L I S

    /// <summary>Sentinel range used by the emit pipeline. Sentinels are
    /// assigned sequentially starting at <see cref="SentinelBase"/>; any
    /// value &gt;= base and &lt; base+0x10_0000 may be a patch sentinel.
    /// The range is chosen to be a large positive int (so Sigil emits the
    /// 5-byte long form of <c>ldc.i4</c>) and well outside the typical
    /// atom-id / functor-id range an unpatched bundle would naturally use.</summary>
    public const int SentinelBase = 0x7E000000;
    public const int SentinelLimit = SentinelBase + 0x10_0000; // 1M sites

    public static byte[] Encode(IReadOnlyList<IlPatchSite> sites)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write(Magic);
        bw.Write((uint)sites.Count);
        foreach (var s in sites)
        {
            bw.Write(s.AbsoluteByteOffset);
            bw.Write(s.Sentinel);
            bw.Write((byte)s.Kind);
            bw.Write(s.Arity);
            bw.Write(s.Cursor);
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(s.Name);
            bw.Write((uint)nameBytes.Length);
            bw.Write(nameBytes);
        }
        bw.Flush();
        return ms.ToArray();
    }

    public static List<IlPatchSite> Decode(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return new List<IlPatchSite>();
        using var ms = new MemoryStream(bytes, writable: false);
        using var br = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        uint magic = br.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException(
                $"IL patch table magic mismatch (got 0x{magic:X8}, expected 0x{Magic:X8}).");
        uint count = br.ReadUInt32();
        var result = new List<IlPatchSite>((int)count);
        for (uint i = 0; i < count; i++)
        {
            int offset = br.ReadInt32();
            int sentinel = br.ReadInt32();
            var kind = (IlPatchKind)br.ReadByte();
            int arity = br.ReadInt32();
            int cursor = br.ReadInt32();
            uint nameLength = br.ReadUInt32();
            byte[] nameBytes = br.ReadBytes((int)nameLength);
            string name = System.Text.Encoding.UTF8.GetString(nameBytes);
            result.Add(new IlPatchSite
            {
                AbsoluteByteOffset = offset,
                Sentinel = sentinel,
                Kind = kind,
                Name = name,
                Arity = arity,
                Cursor = cursor,
            });
        }
        return result;
    }
}
