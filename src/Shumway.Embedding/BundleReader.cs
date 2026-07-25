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
        using var headerReader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        byte[] magic = headerReader.ReadBytes(4);
        if (magic.Length != 4 || !magic.AsSpan().SequenceEqual(BundleFormat.Magic))
            throw new InvalidDataException(
                "Bundle: magic bytes don't match 'SHUM' — not a Shumway bundle.");

        uint version = headerReader.ReadUInt32();
        // Pre-release format policy (see BundleFormat): exactly ONE supported
        // layout, frozen version number, no backward compatibility — a stale
        // bundle fails here (or on a truncated section); rebuild it by
        // re-linking.
        if (version != BundleFormat.CurrentVersion)
            throw new InvalidDataException(
                $"Bundle: format version {version} is not supported by this runtime "
                + $"(requires {BundleFormat.CurrentVersion}; pre-release formats are "
                + "not backward compatible — rebuild the bundle).");

        // the byte after the version selects the body encoding
        // (raw / Brotli); everything below reads from the decoded body.
        byte compression = headerReader.ReadByte();
        using var br = BundleFormat.OpenBody(compression, ms);

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
            // IL patch table + per-method entries table.
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
            // Dynamic seeds trailer.
            uint seedCount = br.ReadUInt32();
            var dynamicSeeds = new List<ShmoDynamicSeed>((int)seedCount);
            for (uint j = 0; j < seedCount; j++)
            {
                string seedName = ReadLengthPrefixedUtf8(br);
                int seedArity = (int)br.ReadUInt32();
                bool seedMultifile = br.ReadBoolean();
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
                    new PredicateRef(seedName, seedArity), encoded, seedMultifile));
            }
            // Native-blocks trailer (ADR-022).
            var nativeBlocks = ReadNativeBlocks(br);
            // Native-interop trailer (ADR-024): :- native indicators + :- c decls.
            uint nfCount = br.ReadUInt32();
            var nativeFunctions = new List<PredicateRef>((int)nfCount);
            for (uint j = 0; j < nfCount; j++)
            {
                string nfName = ReadLengthPrefixedUtf8(br);
                int nfArity = (int)br.ReadUInt32();
                nativeFunctions.Add(new PredicateRef(nfName, nfArity));
            }
            string nd = ReadLengthPrefixedUtf8(br);
            string? nativeDecls = nd.Length == 0 ? null : nd;
            // Operator trailer.
            uint opCount = br.ReadUInt32();
            var operators = new List<ShmoOperatorDef>((int)opCount);
            for (uint j = 0; j < opCount; j++)
            {
                int opPrio = br.ReadInt32();
                string opType = ReadLengthPrefixedUtf8(br);
                string opName = ReadLengthPrefixedUtf8(br);
                operators.Add(new ShmoOperatorDef(opPrio, opType, opName));
            }
            // ADR-038 — export-qualification + import table per entry.
            bool isExportQualified = br.ReadBoolean();
            uint exCount = br.ReadUInt32();
            var exports = new List<PredicateRef>((int)exCount);
            for (uint j = 0; j < exCount; j++)
            {
                string exName = ReadLengthPrefixedUtf8(br);
                int exArity = (int)br.ReadUInt32();
                exports.Add(new PredicateRef(exName, exArity));
            }
            uint impCount = br.ReadUInt32();
            var imports = new List<ShmoImportEntry>((int)impCount);
            for (uint j = 0; j < impCount; j++)
            {
                string impName = ReadLengthPrefixedUtf8(br);
                int impArity = (int)br.ReadUInt32();
                string impSrc = ReadLengthPrefixedUtf8(br);
                imports.Add(new ShmoImportEntry(new PredicateRef(impName, impArity), impSrc));
            }
            entries[i] = new BundleEntry(name, source, compiled, compiledIl, defined,
                compiledIlPatches, compiledIlEntries, dynamicSeeds, nativeBlocks,
                nativeFunctions, nativeDecls, operators,
                isExportQualified: isExportQualified, exports: exports, imports: imports);
        }
        // Foreign-assemblies trailer.
        uint asmCount = br.ReadUInt32();
        var foreignAssemblies = new List<string>((int)asmCount);
        for (uint i = 0; i < asmCount; i++)
            foreignAssemblies.Add(ReadLengthPrefixedUtf8(br));
        // ADR-024 native-libraries trailer (--native-dll).
        uint nativeCount = br.ReadUInt32();
        var nativeLibraries = new List<string>((int)nativeCount);
        for (uint i = 0; i < nativeCount; i++)
            nativeLibraries.Add(ReadLengthPrefixedUtf8(br));
        // Save-state snapshot trailer: one presence byte, then the
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
        return new Bundle(entries, foreignAssemblies, snapshot, archiveMembers, nativeLibraries);
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

    /// <summary>ADR-022 — deserialise one entry's native blocks (mirrors
    /// <see cref="BundleWriter.WriteNativeBlocks"/>).</summary>
    private static List<ShmoNativeBlock> ReadNativeBlocks(BinaryReader br)
    {
        uint nbCount = br.ReadUInt32();
        var blocks = new List<ShmoNativeBlock>((int)nbCount);
        for (uint j = 0; j < nbCount; j++)
        {
            string nbName = ReadLengthPrefixedUtf8(br);
            string rawText = ReadLengthPrefixedUtf8(br);
            uint varCount = br.ReadUInt32();
            var vars = new Shumway.Compiler.NativeC.NativeVar[varCount];
            for (uint k = 0; k < varCount; k++)
            {
                string vName = ReadLengthPrefixedUtf8(br);
                var kind = (Shumway.Compiler.NativeC.NativeKind)br.ReadByte();
                var mode = (Shumway.Compiler.NativeC.NativeMode)br.ReadByte();
                vars[k] = new Shumway.Compiler.NativeC.NativeVar(vName, kind, mode);
            }
            uint sgCount = br.ReadUInt32();
            var scalarGlobals = new Shumway.Compiler.NativeC.NativeScalarGlobal[sgCount];
            for (uint k = 0; k < sgCount; k++)
            {
                string gName = ReadLengthPrefixedUtf8(br);
                bool isFloat = br.ReadBoolean();
                scalarGlobals[k] = new Shumway.Compiler.NativeC.NativeScalarGlobal(gName, isFloat);
            }
            blocks.Add(new ShmoNativeBlock(nbName, rawText, vars, scalarGlobals));
        }
        return blocks;
    }
}
