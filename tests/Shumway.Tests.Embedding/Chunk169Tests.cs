using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 14 chunk 169: <c>shumway-compile</c> accepts
/// <c>--debug</c> / <c>--release</c> flags. Default is release.
/// The chosen mode is recorded into the V2
/// <c>.shmo</c> via <see cref="ShmoObject.BuildMode"/>; the linker
/// surfaces it in the chunk-173 map file and the chunk-172
/// <c>--strip</c> path can branch on it.
/// </summary>
public class Chunk169Tests
{
    [Fact]
    public void DefaultMode_IsRelease()
    {
        var obj = ShmoCompiler.CompileSource(":- module(m).\np(1).\n");
        Assert.Equal(ShmoBuildMode.Release, obj.BuildMode);
    }

    [Fact]
    public void DebugMode_PropagatesIntoShmoObject()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- module(m).\np(1).\n",
            moduleNameFallback: "m",
            buildMode: ShmoBuildMode.Debug);
        Assert.Equal(ShmoBuildMode.Debug, obj.BuildMode);
    }

    [Fact]
    public void BuildMode_RoundTripsThroughShmoIo()
    {
        var release = ShmoCompiler.CompileSource(":- module(m).\np(1).\n",
            buildMode: ShmoBuildMode.Release);
        var debug = ShmoCompiler.CompileSource(":- module(m).\np(1).\n",
            buildMode: ShmoBuildMode.Debug);

        var restoredRelease = ShmoReader.FromBytes(ShmoWriter.ToBytes(release));
        var restoredDebug = ShmoReader.FromBytes(ShmoWriter.ToBytes(debug));

        Assert.Equal(ShmoBuildMode.Release, restoredRelease.BuildMode);
        Assert.Equal(ShmoBuildMode.Debug, restoredDebug.BuildMode);
    }

    [Fact]
    public void ShmoFormat_CurrentVersion_IsThree()
    {
        // V3 (chunk 209) adds the dynamic-seeds trailer. V1/V2 still
        // readable (MinSupportedVersion stays 1).
        Assert.Equal(3, ShmoFormat.CurrentVersion);
        Assert.Equal(1, ShmoFormat.MinSupportedVersion);
    }

    [Fact]
    public void ReadingV1Object_DefaultsToRelease()
    {
        // Build a hand-crafted V1 payload (no buildMode byte).
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write(ShmoFormat.Magic);
        bw.Write((uint)1);                      // version 1
        WriteString(bw, "legacy");              // module name
        WriteString(bw, "p(1).");               // source
        bw.Write((uint)0);                      // bytecodeLength
        // No buildMode byte (V1).
        bw.Write((uint)0);                      // definedCount
        bw.Write((uint)0);                      // ensureLinkedCount
        bw.Write((uint)0);                      // callGraphSize
        bw.Write((uint)0);                      // qualifiedRefsCount
        bw.Flush();

        var restored = ShmoReader.FromBytes(ms.ToArray());
        Assert.Equal("legacy", restored.ModuleName);
        Assert.Equal(ShmoBuildMode.Release, restored.BuildMode);
    }

    [Fact]
    public void UnsupportedFutureVersion_Throws()
    {
        var obj = ShmoCompiler.CompileSource("p.\n");
        byte[] bytes = ShmoWriter.ToBytes(obj);
        // Bump version to 999.
        bytes[4] = 0xE7;
        bytes[5] = 0x03;
        bytes[6] = 0x00;
        bytes[7] = 0x00;
        var ex = Assert.Throws<InvalidDataException>(() => ShmoReader.FromBytes(bytes));
        Assert.Contains("999", ex.Message);
    }

    private static void WriteString(BinaryWriter bw, string s)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
        bw.Write((uint)bytes.Length);
        bw.Write(bytes);
    }
}
