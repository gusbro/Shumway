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
/// <para>Layout:</para>
/// <code>
///   [0..3]    Magic 'S','H','U','M'
///   [4..7]    Format version (uint32, = 1)
///   [8..11]   Module count (uint32)
///   [12..]    For each module:
///                 nameLength      : uint32
///                 nameBytes       : utf-8 bytes
///                 sourceLength    : uint32
///                 sourceBytes     : utf-8 bytes
///                 compiledLength  : uint32   (0 = no compiled blob)
///                 compiledBytes   : encoded CompiledModuleCodec output
///                 compiledIlLength: uint32   (0 = no compiled IL .dll)
///                 compiledIlBytes : PersistedAssemblyBuilder output
///                 V2+: definedCount: uint32
///                       definedEntries: { name:string, arity:uint32, vis:byte }*
/// </code>
///
/// <para>Versions:</para>
/// <list type="bullet">
/// <item><b>V1</b>: original layout, no per-predicate visibility metadata.
/// The engine's LoadBundle has to re-consult the embedded source.</item>
/// <item><b>V2 (chunk 178)</b>: each entry additionally carries a
/// <c>Defined</c> list mirroring <see cref="ShmoObject.Defined"/> —
/// every predicate the module defines with its
/// <see cref="PredicateVisibility"/>. Enables the source-less
/// LoadBundle path: when an entry's <c>Source</c> has been stripped
/// (chunk 172 <c>--strip</c> or chunk 177 Release <c>shumway-compile</c>),
/// the engine populates its <c>ModuleManifest</c> straight from
/// <c>Defined</c> and plugs the precompiled bytecode into the static
/// link region without re-consulting source.</item>
/// </list>
/// </summary>
public static class BundleFormat
{
    public static readonly byte[] Magic = new byte[] { (byte)'S', (byte)'H', (byte)'U', (byte)'M' };
    public const int CurrentVersion = 6;
    // V6 (chunk 264) — optional save-state snapshot trailer after the
    // V5 foreign-assemblies trailer:
    //     snapshotPresent: byte (0 = none, 1 = snapshot follows)
    //     if 1:
    //         dynamicOnly: byte
    //         consultCount: uint32
    //         each entry: { sourceLen:uint32, sourceBytes:utf-8 }
    //         dynamicCount: uint32
    //         each entry: { nameLen:uint32, nameBytes:utf-8,
    //                       arity:uint32, clauseCount:uint32,
    //                       each clause: byteCount:uint32 + bytes }
    // Bundles built by shumway-link / shumway-compile always emit
    // snapshotPresent=0. PrologEngine.SaveState emits snapshotPresent=1
    // with 0 module entries.
    // V5 (chunk 247) — bundle gains a foreign-assemblies trailer
    // after the per-entry payloads:
    //     foreignAsmCount: uint32
    //     each entry: { nameLen:uint32, nameBytes:utf-8 }
    // Names are filename-only (Path.GetFileName); the runtime
    // LoadBundle resolves them next to the .shum file (or the
    // executable's AppContext.BaseDirectory) and registers every
    // [PrologPredicate]-decorated static method via
    // RegisterPredicates(type, staticOnly: true).
    // Pre-V5 readers stop at the end of entries and never see the
    // trailer; pre-V5 bundles read by a V5 runtime have an empty
    // ForeignAssemblies list (the writer never emitted one).
    // V3 (Phase 17) — each entry additionally carries an IL patch table
    // (uint32 length + bytes) immediately after the compiledIl blob.
    // Used by the LoadBundle path to overwrite build-time atom/functor
    // id sentinels in the persisted IL with runtime-process values
    // before Assembly.Load. Bundles built without IL carry a 0-length
    // patch table.
    //
    // V4 (chunk 209) — each entry additionally carries a DynamicSeeds
    // trailer (mirrors ShmoObject.DynamicSeeds) at the very end of the
    // entry record. Carries TermCodec-encoded clauses for any
    // `:- dynamic foo/N.` predicate, so LoadBundle can seed the engine's
    // _dynamicClauses store without re-consulting source.
}
