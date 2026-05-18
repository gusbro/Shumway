namespace Shumway.Embedding;

/// <summary>
/// Shared layout constants for the Shumway bundle (<c>.shum</c>) file
/// format. A bundle is a binary container for one or more modules' Prolog
/// source plus an optional pre-compiled bytecode payload per entry —
/// produced by <see cref="CompiledModuleCodec"/> and intended for future
/// runtime paths that want to skip the parser / compiler at consult time.
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
/// </code>
/// </summary>
public static class BundleFormat
{
    public static readonly byte[] Magic = new byte[] { (byte)'S', (byte)'H', (byte)'U', (byte)'M' };
    public const int CurrentVersion = 1;
}
