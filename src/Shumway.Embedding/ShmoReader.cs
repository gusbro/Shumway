using System.Text;

namespace Shumway.Embedding;

/// <summary>
/// Reads a <c>.shmo</c> file into an in-memory <see cref="ShmoObject"/>.
/// Format errors (bad magic, unsupported version, truncated payload) throw
/// <see cref="InvalidDataException"/> with a descriptive message — the
/// linker / CLI surfaces these as the user-visible diagnostic.
/// </summary>
public static class ShmoReader
{
    public static ShmoObject ReadFromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return FromBytes(File.ReadAllBytes(path));
    }

    public static ShmoObject FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        byte[] magic = br.ReadBytes(4);
        if (magic.Length != 4 || !magic.AsSpan().SequenceEqual(ShmoFormat.Magic))
            throw new InvalidDataException(
                ".shmo: magic bytes don't match 'SHMO' — not a Shumway object file.");

        uint version = br.ReadUInt32();
        if (version < ShmoFormat.MinSupportedVersion || version > ShmoFormat.CurrentVersion)
            throw new InvalidDataException(
                $".shmo: format version {version} is not supported by this linker "
                + $"(supports {ShmoFormat.MinSupportedVersion}..{ShmoFormat.CurrentVersion}).");

        string moduleName = ReadLengthPrefixedUtf8(br);
        string source = ReadLengthPrefixedUtf8(br);
        uint bytecodeLength = br.ReadUInt32();
        byte[] bytecode = br.ReadBytes((int)bytecodeLength);
        if (bytecode.Length != bytecodeLength)
            throw new InvalidDataException(
                $".shmo: truncated bytecode section (expected "
                + $"{bytecodeLength} bytes, got {bytecode.Length}).");

        // V2+ adds the build-mode byte after the bytecode payload.
        // V1 objects default to Release (the byte didn't exist).
        ShmoBuildMode buildMode = ShmoBuildMode.Release;
        if (version >= 2)
        {
            byte mode = br.ReadByte();
            if (mode > (byte)ShmoBuildMode.Debug)
                throw new InvalidDataException(
                    $".shmo: unknown build-mode code {mode}.");
            buildMode = (ShmoBuildMode)mode;
        }

        uint definedCount = br.ReadUInt32();
        var defined = new ShmoDefinedPredicate[definedCount];
        for (uint i = 0; i < definedCount; i++)
        {
            string name = ReadLengthPrefixedUtf8(br);
            int arity = (int)br.ReadUInt32();
            byte vis = br.ReadByte();
            if (vis > (byte)PredicateVisibility.Dynamic)
                throw new InvalidDataException(
                    $".shmo: unknown predicate visibility code {vis} for {name}/{arity}.");
            defined[i] = new ShmoDefinedPredicate(
                new PredicateRef(name, arity), (PredicateVisibility)vis);
        }

        uint ensureLinkedCount = br.ReadUInt32();
        var ensureLinked = new PredicateRef[ensureLinkedCount];
        for (uint i = 0; i < ensureLinkedCount; i++)
        {
            string name = ReadLengthPrefixedUtf8(br);
            int arity = (int)br.ReadUInt32();
            ensureLinked[i] = new PredicateRef(name, arity);
        }

        uint callerCount = br.ReadUInt32();
        var callGraph = new Dictionary<PredicateRef, IReadOnlyList<PredicateRef>>();
        for (uint i = 0; i < callerCount; i++)
        {
            string callerName = ReadLengthPrefixedUtf8(br);
            int callerArity = (int)br.ReadUInt32();
            uint edgeCount = br.ReadUInt32();
            var targets = new PredicateRef[edgeCount];
            for (uint j = 0; j < edgeCount; j++)
            {
                string tName = ReadLengthPrefixedUtf8(br);
                int tArity = (int)br.ReadUInt32();
                targets[j] = new PredicateRef(tName, tArity);
            }
            callGraph[new PredicateRef(callerName, callerArity)] = targets;
        }

        uint qrefCount = br.ReadUInt32();
        var qrefs = new QualifiedPredicateRef[qrefCount];
        for (uint i = 0; i < qrefCount; i++)
        {
            string module = ReadLengthPrefixedUtf8(br);
            string name = ReadLengthPrefixedUtf8(br);
            int arity = (int)br.ReadUInt32();
            qrefs[i] = new QualifiedPredicateRef(module, name, arity);
        }

        // V3+ adds dynamicSeeds trailer.
        ShmoDynamicSeed[] dynamicSeeds = Array.Empty<ShmoDynamicSeed>();
        if (version >= 3)
        {
            uint seedCount = br.ReadUInt32();
            dynamicSeeds = new ShmoDynamicSeed[seedCount];
            for (uint i = 0; i < seedCount; i++)
            {
                string name = ReadLengthPrefixedUtf8(br);
                int arity = (int)br.ReadUInt32();
                uint clauseCount = br.ReadUInt32();
                var encoded = new byte[clauseCount][];
                for (uint j = 0; j < clauseCount; j++)
                {
                    uint byteCount = br.ReadUInt32();
                    byte[] bytes = br.ReadBytes((int)byteCount);
                    if (bytes.Length != byteCount)
                        throw new InvalidDataException(
                            $".shmo: truncated dynamic-seed clause for "
                            + $"{name}/{arity} (expected {byteCount}, got {bytes.Length}).");
                    encoded[j] = bytes;
                }
                dynamicSeeds[i] = new ShmoDynamicSeed(
                    new PredicateRef(name, arity), encoded);
            }
        }

        // V4+ adds the clauseTerms trailer (the LTO channel).
        byte[][] clauseTerms = Array.Empty<byte[]>();
        if (version >= 4)
        {
            uint clauseCount = br.ReadUInt32();
            clauseTerms = new byte[clauseCount][];
            for (uint i = 0; i < clauseCount; i++)
            {
                uint byteCount = br.ReadUInt32();
                byte[] bytes = br.ReadBytes((int)byteCount);
                if (bytes.Length != byteCount)
                    throw new InvalidDataException(
                        $".shmo: truncated clause-terms entry {i} "
                        + $"(expected {byteCount}, got {bytes.Length}).");
                clauseTerms[i] = bytes;
            }
        }

        return new ShmoObject(moduleName, source, bytecode,
            defined, ensureLinked, callGraph, qrefs, buildMode, dynamicSeeds,
            clauseTerms);
    }

    private static string ReadLengthPrefixedUtf8(BinaryReader br)
    {
        uint length = br.ReadUInt32();
        byte[] bytes = br.ReadBytes((int)length);
        if (bytes.Length != length)
            throw new InvalidDataException(
                ".shmo: truncated string section (expected "
                + $"{length} bytes, got {bytes.Length}).");
        return Encoding.UTF8.GetString(bytes);
    }
}
