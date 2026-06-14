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
        // Pre-release format policy (see BundleFormat): exactly ONE supported
        // layout, frozen version number, no backward compatibility — a stale
        // bundle fails here (or on a truncated section); rebuild it by
        // re-linking.
        if (version != BundleFormat.CurrentVersion)
            throw new InvalidDataException(
                $"Bundle: format version {version} is not supported by this runtime "
                + $"(requires {BundleFormat.CurrentVersion}; pre-release formats are "
                + "not backward compatible — rebuild the bundle).");

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
            // Per-predicate visibility metadata.
            uint definedCount = br.ReadUInt32();
            var defined = new List<ShmoDefinedPredicate>((int)definedCount);
            for (uint j = 0; j < definedCount; j++)
            {
                string predName = ReadLengthPrefixedUtf8(br);
                uint arity = br.ReadUInt32();
                byte vis = br.ReadByte();
                defined.Add(new ShmoDefinedPredicate(
                    new PredicateRef(predName, (int)arity),
                    (PredicateVisibility)vis));
            }
            // IL patch table + per-method entries table (Phase 17).
            byte[]? compiledIlPatches = null;
            byte[]? compiledIlEntries = null;
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
            // Dynamic seeds trailer (chunk 209).
            uint seedCount = br.ReadUInt32();
            var dynamicSeeds = new List<ShmoDynamicSeed>((int)seedCount);
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
            entries[i] = new BundleEntry(name, source, compiled, compiledIl, defined,
                compiledIlPatches, compiledIlEntries, dynamicSeeds);
        }
        // Foreign-assemblies trailer (chunk 247).
        uint asmCount = br.ReadUInt32();
        var foreignAssemblies = new List<string>((int)asmCount);
        for (uint i = 0; i < asmCount; i++)
            foreignAssemblies.Add(ReadLengthPrefixedUtf8(br));
        // Save-state snapshot trailer (chunk 264): one presence byte, then the
        // payload when a PrologEngine.SaveState bundle carries a snapshot.
        BundleSnapshot? snapshot = null;
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
        // Librarian archive trailer (shumway-lib): the verbatim .shmo objects
        // the bundle was assembled from. 0 for linker / compiler / SaveState
        // bundles; non-zero only for a librarian archive (whose Entries are
        // then empty — LoadBundle derives a runnable entry per member).
        uint archiveCount = br.ReadUInt32();
        var archiveMembers = new List<BundleArchiveMember>((int)archiveCount);
        for (uint i = 0; i < archiveCount; i++)
        {
            string fileName = ReadLengthPrefixedUtf8(br);
            uint shmoLength = br.ReadUInt32();
            byte[] shmoBytes = br.ReadBytes((int)shmoLength);
            if (shmoBytes.Length != shmoLength)
                throw new InvalidDataException(
                    $"Bundle: truncated archive member '{fileName}' (expected "
                    + $"{shmoLength} bytes, got {shmoBytes.Length}).");
            archiveMembers.Add(new BundleArchiveMember(fileName, shmoBytes));
        }
        return new Bundle(entries, foreignAssemblies, snapshot, archiveMembers);
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
