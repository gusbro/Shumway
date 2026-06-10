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
///   [8..11]   Module count (uint32)
///   [12..]    For each module:
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
}
