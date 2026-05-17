namespace Shumway.Embedding;

/// <summary>
/// Shared layout constants for the Shumway bundle (<c>.shum</c>) file
/// format. The Phase-1 bundle is intentionally simple: a binary container
/// for one or more modules' Prolog source, validated at bundle time so
/// loading time is guaranteed to produce well-formed clauses.
///
/// <para>The on-disk format is little-endian throughout. A Phase-2 chunk
/// will extend it with pre-compiled bytecode so loading bypasses the parser
/// and compiler entirely.</para>
///
/// <para>Layout (Phase 1):</para>
/// <code>
///   [0..3]    Magic 'S','H','U','M'
///   [4..7]    Format version (uint32, currently 1)
///   [8..11]   Module count (uint32)
///   [12..]    For each module:
///                 nameLength    : uint32
///                 nameBytes     : utf-8 bytes
///                 sourceLength  : uint32
///                 sourceBytes   : utf-8 bytes
/// </code>
/// </summary>
public static class BundleFormat
{
    public static readonly byte[] Magic = new byte[] { (byte)'S', (byte)'H', (byte)'U', (byte)'M' };
    public const int CurrentVersion = 1;
}
