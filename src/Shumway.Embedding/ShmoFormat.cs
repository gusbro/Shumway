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
/// <para>Layout (V1):</para>
/// <code>
///   [0..3]    Magic 'S','H','M','O'
///   [4..7]    Format version (uint32, = 1)
///   [8..]     V1 payload:
///                 moduleName       : len-prefixed UTF-8
///                 source           : len-prefixed UTF-8 (may be empty)
///                 bytecodeLength   : uint32
///                 bytecodeBytes    : bytes (CompiledModuleCodec output)
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
/// <para>The version stays at <c>1</c> until the separate-compilation
/// flow stabilises; any later breaking change to the body bumps it and
/// the linker rejects older artifacts with a descriptive error.</para>
/// </summary>
public static class ShmoFormat
{
    public static readonly byte[] Magic = new byte[] { (byte)'S', (byte)'H', (byte)'M', (byte)'O' };
    public const int CurrentVersion = 1;
}
