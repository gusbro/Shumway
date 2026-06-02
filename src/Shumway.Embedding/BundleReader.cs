using System.Text;

namespace Shumway.Embedding;

/// <summary>
/// Reads a Shumway bundle file into an in-memory <see cref="Bundle"/>.
/// Format errors (bad magic, unsupported version, truncated data) throw
/// <see cref="InvalidDataException"/> with a descriptive message.
/// </summary>
public static class BundleReader
{
    public static Bundle ReadFromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return FromBytes(File.ReadAllBytes(path));
    }

    public static Bundle FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        byte[] magic = br.ReadBytes(4);
        if (magic.Length != 4 || !magic.AsSpan().SequenceEqual(BundleFormat.Magic))
            throw new InvalidDataException(
                "Bundle: magic bytes don't match 'SHUM' — not a Shumway bundle.");

        uint version = br.ReadUInt32();
        if (version < 1 || version > BundleFormat.CurrentVersion)
            throw new InvalidDataException(
                $"Bundle: format version {version} is not supported by this runtime "
                + $"(expected 1..{BundleFormat.CurrentVersion}).");

        uint moduleCount = br.ReadUInt32();
        var entries = new BundleEntry[moduleCount];
        for (uint i = 0; i < moduleCount; i++)
        {
            string name = ReadLengthPrefixedUtf8(br);
            string source = ReadLengthPrefixedUtf8(br);
            uint compiledLength = br.ReadUInt32();
            byte[]? compiled = null;
            if (compiledLength > 0)
            {
                compiled = br.ReadBytes((int)compiledLength);
                if (compiled.Length != compiledLength)
                    throw new InvalidDataException(
                        $"Bundle: truncated compiled-bytecode section (expected "
                        + $"{compiledLength} bytes, got {compiled.Length}).");
            }
            uint compiledIlLength = br.ReadUInt32();
            byte[]? compiledIl = null;
            if (compiledIlLength > 0)
            {
                compiledIl = br.ReadBytes((int)compiledIlLength);
                if (compiledIl.Length != compiledIlLength)
                    throw new InvalidDataException(
                        $"Bundle: truncated compiled-IL section (expected "
                        + $"{compiledIlLength} bytes, got {compiledIl.Length}).");
            }
            // V2+: per-predicate visibility metadata. Empty list for V1.
            List<ShmoDefinedPredicate>? defined = null;
            if (version >= 2)
            {
                uint definedCount = br.ReadUInt32();
                defined = new List<ShmoDefinedPredicate>((int)definedCount);
                for (uint j = 0; j < definedCount; j++)
                {
                    string predName = ReadLengthPrefixedUtf8(br);
                    uint arity = br.ReadUInt32();
                    byte vis = br.ReadByte();
                    defined.Add(new ShmoDefinedPredicate(
                        new PredicateRef(predName, (int)arity),
                        (PredicateVisibility)vis));
                }
            }
            // V3+ (Phase 17): IL patch table + per-method entries table.
            byte[]? compiledIlPatches = null;
            byte[]? compiledIlEntries = null;
            if (version >= 3)
            {
                uint patchLength = br.ReadUInt32();
                if (patchLength > 0)
                {
                    compiledIlPatches = br.ReadBytes((int)patchLength);
                    if (compiledIlPatches.Length != patchLength)
                        throw new InvalidDataException(
                            $"Bundle: truncated IL patch table (expected "
                            + $"{patchLength} bytes, got {compiledIlPatches.Length}).");
                }
                uint entriesLength = br.ReadUInt32();
                if (entriesLength > 0)
                {
                    compiledIlEntries = br.ReadBytes((int)entriesLength);
                    if (compiledIlEntries.Length != entriesLength)
                        throw new InvalidDataException(
                            $"Bundle: truncated IL entries table (expected "
                            + $"{entriesLength} bytes, got {compiledIlEntries.Length}).");
                }
            }
            // V4+ (chunk 209): dynamic seeds trailer.
            List<ShmoDynamicSeed>? dynamicSeeds = null;
            if (version >= 4)
            {
                uint seedCount = br.ReadUInt32();
                dynamicSeeds = new List<ShmoDynamicSeed>((int)seedCount);
                for (uint j = 0; j < seedCount; j++)
                {
                    string seedName = ReadLengthPrefixedUtf8(br);
                    int seedArity = (int)br.ReadUInt32();
                    uint clauseCount = br.ReadUInt32();
                    var encoded = new byte[clauseCount][];
                    for (uint k = 0; k < clauseCount; k++)
                    {
                        uint byteCount = br.ReadUInt32();
                        byte[] bytes = br.ReadBytes((int)byteCount);
                        if (bytes.Length != byteCount)
                            throw new InvalidDataException(
                                $"Bundle: truncated dynamic-seed clause for "
                                + $"{seedName}/{seedArity} (expected {byteCount}, got {bytes.Length}).");
                        encoded[k] = bytes;
                    }
                    dynamicSeeds.Add(new ShmoDynamicSeed(
                        new PredicateRef(seedName, seedArity), encoded));
                }
            }
            entries[i] = new BundleEntry(name, source, compiled, compiledIl, defined,
                compiledIlPatches, compiledIlEntries, dynamicSeeds);
        }
        // V5+ (chunk 247): foreign-assemblies trailer.
        List<string>? foreignAssemblies = null;
        if (version >= 5)
        {
            uint asmCount = br.ReadUInt32();
            foreignAssemblies = new List<string>((int)asmCount);
            for (uint i = 0; i < asmCount; i++)
                foreignAssemblies.Add(ReadLengthPrefixedUtf8(br));
        }
        // V6+ (chunk 264): optional save-state snapshot trailer.
        BundleSnapshot? snapshot = null;
        if (version >= 6 && br.BaseStream.Position < br.BaseStream.Length)
        {
            byte snapshotPresent = br.ReadByte();
            if (snapshotPresent == 1)
            {
                bool dynamicOnly = br.ReadByte() != 0;
                uint consultCount = br.ReadUInt32();
                var consultHistory = new List<string>((int)consultCount);
                for (uint i = 0; i < consultCount; i++)
                    consultHistory.Add(ReadLengthPrefixedUtf8(br));
                uint dynamicCount = br.ReadUInt32();
                var dynamicClauses = new List<ShmoDynamicSeed>((int)dynamicCount);
                for (uint i = 0; i < dynamicCount; i++)
                {
                    string seedName = ReadLengthPrefixedUtf8(br);
                    int seedArity = (int)br.ReadUInt32();
                    uint clauseCount = br.ReadUInt32();
                    var encoded = new byte[clauseCount][];
                    for (uint k = 0; k < clauseCount; k++)
                    {
                        uint byteCount = br.ReadUInt32();
                        byte[] bytes = br.ReadBytes((int)byteCount);
                        if (bytes.Length != byteCount)
                            throw new InvalidDataException(
                                $"Bundle: truncated snapshot dynamic clause for "
                                + $"{seedName}/{seedArity} (expected {byteCount}, got {bytes.Length}).");
                        encoded[k] = bytes;
                    }
                    dynamicClauses.Add(new ShmoDynamicSeed(
                        new PredicateRef(seedName, seedArity), encoded));
                }
                snapshot = new BundleSnapshot(dynamicOnly, consultHistory, dynamicClauses);
            }
        }
        return new Bundle(entries, foreignAssemblies, snapshot);
    }

    private static string ReadLengthPrefixedUtf8(BinaryReader br)
    {
        uint length = br.ReadUInt32();
        byte[] bytes = br.ReadBytes((int)length);
        if (bytes.Length != length)
            throw new InvalidDataException(
                "Bundle: truncated string section (expected "
                + $"{length} bytes, got {bytes.Length}).");
        return Encoding.UTF8.GetString(bytes);
    }
}
