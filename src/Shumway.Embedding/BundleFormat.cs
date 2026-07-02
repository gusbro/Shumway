namespace Shumway.Embedding;

/// <summary>
/// Shared layout constants for the Shumway bundle (<c>.shum</c>) file
/// format. A bundle is a binary container for one or more modules' Prolog
/// source plus an optional pre-compiled bytecode / IL payload per entry —
/// produced by <see cref="CompiledModuleCodec"/> and
/// <c>Shumway.Compiler.Il.PersistedIlBuilder</c> respectively, intended
/// for runtime paths that want to skip the parser / compiler at consult
/// time.
///
/// <para>The on-disk format is little-endian throughout.</para>
///
/// <para>Layout (the single supported one):</para>
/// <code>
///   [0..3]    Magic 'S','H','U','M'
///   [4..7]    Format version (uint32, = CurrentVersion)
///   [8]       Compression flag (Phase 33 T2): 0 = raw body, 1 = the whole
///             body below is ONE Brotli stream (bodies ≥ 4 KB compress;
///             typical ratio ~4-6× on real corpora)
///   [9..]     Body (raw or decompressed):
///                 Module count (uint32)
///             then for each module:
///                 nameLength       : uint32
///                 nameBytes        : utf-8 bytes
///                 sourceLength     : uint32
///                 sourceBytes      : utf-8 bytes
///                 compiledLength   : uint32   (0 = no compiled blob)
///                 compiledBytes    : encoded CompiledModuleCodec output
///                 compiledIlLength : uint32   (0 = no compiled IL .dll)
///                 compiledIlBytes  : PersistedAssemblyBuilder output
///                 definedCount     : uint32   (chunk 178 — enables the
///                       source-less LoadBundle path)
///                   definedEntries : { name:string, arity:uint32, vis:byte }*
///                 ilPatchLength    : uint32   (Phase 17 — sentinel patch
///                       table for the persisted IL; 0 when no IL)
///                 ilPatchBytes     : bytes
///                 ilEntriesLength  : uint32   (per-method name/arity/slot)
///                 ilEntriesBytes   : bytes
///                 dynamicSeedCount : uint32   (chunk 209 — TermCodec-encoded
///                       clauses of `:- dynamic foo/N.` predicates)
///                   each seed      : { name:string, arity:uint32,
///                                      clauseCount:uint32,
///                                      each clause: byteCount:uint32 + bytes }
///   then the bundle-level trailers:
///                 foreignAsmCount  : uint32   (chunk 247 — filename-only;
///                       LoadBundle resolves next to the .shum / the exe)
///                   each entry     : { nameLen:uint32, nameBytes:utf-8 }
///                 snapshotPresent  : byte     (chunk 264 — 0 from the
///                       linker/compiler; 1 from PrologEngine.SaveState)
///                 if 1:
///                     dynamicOnly  : byte
///                     consultCount : uint32 + { len:uint32, utf-8 }*
///                     dynamicCount : uint32 + seeds (same shape as above)
///                 archiveCount     : uint32   (shumway-lib librarian — 0
///                       from the linker / compiler / SaveState)
///                   each member    : { fileNameLen:uint32, fileNameBytes:utf-8,
///                                      shmoByteCount:uint32, shmoBytes (verbatim
///                                      .shmo image) }
/// </code>
///
/// <para>PRE-RELEASE FORMAT POLICY (same as <see cref="ShmoFormat"/>): there
/// is exactly ONE supported layout — this one — and the version number is
/// FROZEN. No Shumway artifact has shipped publicly, so backward
/// compatibility and version bumps are deliberately not maintained (rebuild
/// stale bundles by re-linking); the number starts meaning something at the
/// first public release. Do not add <c>version &gt;=</c> conditionals to the
/// reader.</para>
/// </summary>
public static class BundleFormat
{
    public static readonly byte[] Magic = new byte[] { (byte)'S', (byte)'H', (byte)'U', (byte)'M' };

    /// <summary>The single supported format version — frozen pre-release
    /// (see the format-policy note above). Writer and reader both require
    /// exactly this value.</summary>
    public const int CurrentVersion = 6;

    // ---- Phase 33 T2 — whole-body compression ------------------------------
    // Layout addition: ONE flag byte follows the version; the REST of the
    // stream (the "body": module count + entries + trailers) is stored raw
    // (flag 0) or as one Brotli stream (flag 1). Whole-body rather than
    // per-entry on purpose: the big redundancy is CROSS-entry (shared atom
    // names, repeated opcode patterns across modules), and the reader is
    // sequential anyway. Decompression happens ONCE at LoadBundle; runtime
    // pays nothing.

    /// <summary>Compression flag values (the byte after the version).</summary>
    public const byte CompressionNone = 0;
    public const byte CompressionBrotli = 1;

    /// <summary>Bodies below this size are stored raw — Brotli's overhead
    /// isn't worth it and tiny bundles stay trivially inspectable.</summary>
    public const int CompressionThresholdBytes = 4096;

    /// <summary>Phase 33 T2 — turns a writer's RAW image
    /// (<c>[magic 4][version 4][body…]</c>) into the on-disk form:
    /// <c>[magic][version][flag][raw-or-brotli body]</c>. Shared by
    /// <see cref="BundleWriter"/> and the linker's in-line serialiser so both
    /// emit identical framing.</summary>
    internal static byte[] FinalizeImage(byte[] raw)
    {
        const int headerBytes = 8;   // magic + version
        int bodyLen = raw.Length - headerBytes;
        if (bodyLen < CompressionThresholdBytes)
        {
            var plain = new byte[raw.Length + 1];
            Array.Copy(raw, 0, plain, 0, headerBytes);
            plain[headerBytes] = CompressionNone;
            Array.Copy(raw, headerBytes, plain, headerBytes + 1, bodyLen);
            return plain;
        }
        using var ms = new MemoryStream(headerBytes + 1 + bodyLen / 3);
        ms.Write(raw, 0, headerBytes);
        ms.WriteByte(CompressionBrotli);
        using (var brotli = new System.IO.Compression.BrotliStream(
                   ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(raw, headerBytes, bodyLen);
        return ms.ToArray();
    }

    /// <summary>Phase 33 T2 — reader counterpart: given the flag byte and the
    /// stream positioned right after it, returns a <see cref="BinaryReader"/>
    /// over the (decompressed when needed) body.</summary>
    internal static BinaryReader OpenBody(byte flag, MemoryStream stream)
    {
        switch (flag)
        {
            case CompressionNone:
                return new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            case CompressionBrotli:
            {
                var body = new MemoryStream();
                using (var brotli = new System.IO.Compression.BrotliStream(
                           stream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true))
                    brotli.CopyTo(body);
                body.Position = 0;
                return new BinaryReader(body, System.Text.Encoding.UTF8, leaveOpen: false);
            }
            default:
                throw new InvalidDataException(
                    $"Bundle: unknown compression flag {flag} (supported: 0 raw, 1 brotli).");
        }
    }
}
