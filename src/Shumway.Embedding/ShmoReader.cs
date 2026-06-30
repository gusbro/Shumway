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
        // Pre-release format policy (see ShmoFormat): exactly ONE supported
        // layout, frozen version number, no backward compatibility — a stale
        // .shmo (older layout under the same number) fails on a truncated
        // section below; regenerate it by recompiling.
        if (version != ShmoFormat.CurrentVersion)
            throw new InvalidDataException(
                $".shmo: format version {version} is not supported by this linker "
                + $"(requires {ShmoFormat.CurrentVersion}; pre-release formats are "
                + "not backward compatible — recompile the source).");

        string moduleName = ReadLengthPrefixedUtf8(br);
        string source = ReadLengthPrefixedUtf8(br);
        uint bytecodeLength = br.ReadUInt32();
        byte[] bytecode = br.ReadBytes((int)bytecodeLength);
        if (bytecode.Length != bytecodeLength)
            throw new InvalidDataException(
                $".shmo: truncated bytecode section (expected "
                + $"{bytecodeLength} bytes, got {bytecode.Length}).");

        byte mode = br.ReadByte();
        if (mode > (byte)ShmoBuildMode.Debug)
            throw new InvalidDataException(
                $".shmo: unknown build-mode code {mode}.");
        ShmoBuildMode buildMode = (ShmoBuildMode)mode;

        // Chunk 441 — Arity-compat compile mode (pre-release layout
        // change, version frozen; see the ShmoFormat policy note).
        byte arityByte = br.ReadByte();
        if (arityByte > 1)
            throw new InvalidDataException(
                $".shmo: unknown arity-compat code {arityByte}.");
        bool arityCompat = arityByte == 1;

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
        var callGraph = new Dictionary<PredicateRef, IReadOnlyList<ShmoCallEdge>>();
        for (uint i = 0; i < callerCount; i++)
        {
            string callerName = ReadLengthPrefixedUtf8(br);
            int callerArity = (int)br.ReadUInt32();
            uint edgeCount = br.ReadUInt32();
            var targets = new ShmoCallEdge[edgeCount];
            for (uint j = 0; j < edgeCount; j++)
            {
                string tName = ReadLengthPrefixedUtf8(br);
                int tArity = (int)br.ReadUInt32();
                byte metaByte = br.ReadByte();   // chunk 441
                if (metaByte > 1)
                    throw new InvalidDataException(
                        $".shmo: unknown call-edge meta marker {metaByte} for "
                        + $"{tName}/{tArity}.");
                targets[j] = new ShmoCallEdge(
                    new PredicateRef(tName, tArity), metaByte == 1);
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

        // dynamicSeeds trailer.
        uint seedCount = br.ReadUInt32();
        var dynamicSeeds = new ShmoDynamicSeed[seedCount];
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

        // clauseTerms trailer (the LTO channel).
        uint termCount = br.ReadUInt32();
        var clauseTerms = new byte[termCount][];
        for (uint i = 0; i < termCount; i++)
        {
            uint byteCount = br.ReadUInt32();
            byte[] bytes = br.ReadBytes((int)byteCount);
            if (bytes.Length != byteCount)
                throw new InvalidDataException(
                    $".shmo: truncated clause-terms entry {i} "
                    + $"(expected {byteCount}, got {bytes.Length}).");
            clauseTerms[i] = bytes;
        }

        // nativeBlocks trailer (ADR-022 — embedded native-block marshalling).
        uint nbCount = br.ReadUInt32();
        var nativeBlocks = new ShmoNativeBlock[nbCount];
        for (uint i = 0; i < nbCount; i++)
        {
            string nbName = ReadLengthPrefixedUtf8(br);
            string rawText = ReadLengthPrefixedUtf8(br);
            uint varCount = br.ReadUInt32();
            var vars = new Shumway.Compiler.NativeC.NativeVar[varCount];
            for (uint j = 0; j < varCount; j++)
            {
                string vName = ReadLengthPrefixedUtf8(br);
                var vKind = (Shumway.Compiler.NativeC.NativeKind)br.ReadByte();
                var vMode = (Shumway.Compiler.NativeC.NativeMode)br.ReadByte();
                vars[j] = new Shumway.Compiler.NativeC.NativeVar(vName, vKind, vMode);
            }
            uint sgCount = br.ReadUInt32();
            var scalarGlobals = new Shumway.Compiler.NativeC.NativeScalarGlobal[sgCount];
            for (uint j = 0; j < sgCount; j++)
            {
                string gName = ReadLengthPrefixedUtf8(br);
                bool isFloat = br.ReadBoolean();
                scalarGlobals[j] = new Shumway.Compiler.NativeC.NativeScalarGlobal(gName, isFloat);
            }
            nativeBlocks[i] = new ShmoNativeBlock(nbName, rawText, vars, scalarGlobals);
        }

        // ADR-024 — native-interop trailer: :- native indicators + :- c decls.
        uint nfCount = br.ReadUInt32();
        var nativeFunctions = new PredicateRef[nfCount];
        for (uint i = 0; i < nfCount; i++)
        {
            string nfName = ReadLengthPrefixedUtf8(br);
            int nfArity = (int)br.ReadUInt32();
            nativeFunctions[i] = new PredicateRef(nfName, nfArity);
        }
        string nd = ReadLengthPrefixedUtf8(br);
        string? nativeDecls = nd.Length == 0 ? null : nd;

        return new ShmoObject(moduleName, source, bytecode,
            defined, ensureLinked, callGraph, qrefs, buildMode, dynamicSeeds,
            clauseTerms, arityCompat, nativeBlocks, nativeFunctions, nativeDecls);
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
