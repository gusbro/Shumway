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
/// </code>
///
/// <para>The version stays at 1 throughout Phase 2: there is no released
/// runtime to maintain compatibility with, and bumping the version on
/// every additive field churn would force downstream tooling to track
/// changes that don't actually matter to anyone yet.</para>
/// </summary>
public static class BundleFormat
{
    public static readonly byte[] Magic = new byte[] { (byte)'S', (byte)'H', (byte)'U', (byte)'M' };
    public const int CurrentVersion = 1;
}
