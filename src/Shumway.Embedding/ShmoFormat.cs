namespace Shumway.Embedding;

/// <summary>
/// Shared layout constants for the Shumway compiled-object
/// (<c>.shmo</c>) file format — the per-module artifact produced by
/// <c>shumway-compile</c> and consumed by <c>shumway-link</c>.
///
/// <para>A <c>.shmo</c> carries one module's WAM bytecode plus the
/// link-time metadata the linker needs: the predicates this module
/// defines (with visibility), its <c>:- ensure_linked/1</c> roots, the
/// per-predicate call graph (so the linker can do reachability + missing-
/// predicate analysis), and any module-qualified references (the rare
/// case — most call sites resolve through the flat public namespace).</para>
///
/// <para>The on-disk format is little-endian throughout.</para>
///
/// <para>Layout (V2):</para>
/// <code>
///   [0..3]    Magic 'S','H','M','O'
///   [4..7]    Format version (uint32, = 2)
///   [8..]     V2 payload:
///                 moduleName       : len-prefixed UTF-8
///                 source           : len-prefixed UTF-8 (may be empty)
///                 bytecodeLength   : uint32
///                 bytecodeBytes    : bytes (CompiledModuleCodec output)
///                 buildMode        : byte (0=release, 1=debug)        [V2+]
///                 definedCount     : uint32
///                   for each defined predicate:
///                     name         : len-prefixed UTF-8
///                     arity        : uint32
///                     visibility   : byte (0=local, 1=public, 2=dynamic)
///                 ensureLinkedCount: uint32
///                   for each:
///                     name         : len-prefixed UTF-8
///                     arity        : uint32
///                 callGraphSize    : uint32 (caller-entry count)
///                   for each caller:
///                     callerName   : len-prefixed UTF-8
///                     callerArity  : uint32
///                     edgeCount    : uint32
///                       for each edge target:
///                         name     : len-prefixed UTF-8
///                         arity    : uint32
///                 qualifiedRefsCount: uint32
///                   for each:
///                     module       : len-prefixed UTF-8
///                     name         : len-prefixed UTF-8
///                     arity        : uint32
/// </code>
///
/// <para>V3 (chunk 209): adds a <c>dynamicSeeds</c> trailer carrying the
/// source clauses of <c>:- dynamic foo/N.</c> predicates as
/// <see cref="TermCodec"/>-encoded blobs. The engine needs these to mutate
/// the predicates at runtime; the static bytecode can't hold them. Layout
/// after <c>qualifiedRefs</c>:</para>
/// <code>
///                 dynamicSeedsCount : uint32
///                   for each:
///                     name          : len-prefixed UTF-8
///                     arity         : uint32
///                     clauseCount   : uint32
///                     for each clause:
///                       byteCount   : uint32
///                       bytes       : TermCodec-encoded clause term
/// </code>
///
/// <para>V4 (chunk 411): adds a <c>clauseTerms</c> trailer — the module's RAW
/// static clauses (post-parse, pre-transform; dynamic heads excluded, they
/// travel in <c>dynamicSeeds</c>) as <see cref="TermCodec"/>-encoded blobs.
/// The LTO channel: present in Release too (the <c>.shmo</c> is an
/// intermediate artifact, like a fat object file with embedded IR; IP
/// stripping applies to the shipped <c>.shum</c>/exe, not here). Consumed by
/// the linker's cross-module meta-wrapper unfold, which recompiles affected
/// caller modules from these clauses. Layout after <c>dynamicSeeds</c>:</para>
/// <code>
///                 clauseTermsCount  : uint32
///                   for each clause:
///                     byteCount     : uint32
///                     bytes         : TermCodec-encoded clause term
/// </code>
///
/// <para>V1 (legacy): identical to V2 minus the <c>buildMode</c> byte.
/// The reader accepts V1..V4; older versions default to empty
/// <c>DynamicSeeds</c> / <c>ClauseTerms</c>.</para>
/// </summary>
public static class ShmoFormat
{
    public static readonly byte[] Magic = new byte[] { (byte)'S', (byte)'H', (byte)'M', (byte)'O' };

    /// <summary>The version this build of the writer produces.</summary>
    public const int CurrentVersion = 4;

    /// <summary>The minimum version the reader will accept. V1 objects
    /// are still readable but their build mode defaults to
    /// <see cref="ShmoBuildMode.Release"/>.</summary>
    public const int MinSupportedVersion = 1;
}

/// <summary>Compilation mode the <c>.shmo</c> was built in.
/// Currently a metadata-only flag: the linker surfaces it in the
/// chunk-173 map file and the chunk-172 <c>--strip</c> option may
/// use it to decide which entries are safe to strip. Future
/// extensions (per-instruction line info, source-position metadata,
/// etc.) ride on this flag.</summary>
public enum ShmoBuildMode : byte
{
    Release = 0,
    Debug = 1,
}
