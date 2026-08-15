using System;
using System.Collections.Generic;
using System.IO;
using Shumway.Embedding;
using Xunit;

/// <summary>Every artifact records the Shumway that produced it: a
/// <c>.shmo</c> and a <c>.shum</c> both carry the generator version as the
/// first field of their body.
///
/// <para>The format version and the generator version answer different
/// questions — "can this reader read the file?" versus "which build wrote
/// it?". Only the first is enforced; the second is recorded so that when the
/// format does evolve, an old file can be identified and diagnosed instead of
/// merely rejected.</para></summary>
namespace Shumway.Tests.Embedding;

public sealed class GeneratorVersionStampTests
{
    private static ShmoObject MinimalObject() =>
        new(
            moduleName: "m",
            source: "p.",
            bytecode: Array.Empty<byte>(),
            defined: Array.Empty<ShmoDefinedPredicate>(),
            ensureLinked: Array.Empty<PredicateRef>(),
            callGraph: new Dictionary<PredicateRef, IReadOnlyList<ShmoCallEdge>>(),
            qualifiedRefs: Array.Empty<QualifiedPredicateRef>());

    [Fact]
    public void ShmoRoundTrip_CarriesTheGeneratorVersion()
    {
        byte[] bytes = ShmoWriter.ToBytes(MinimalObject());
        ShmoObject read = ShmoReader.FromBytes(bytes);
        Assert.Equal(ShumwayVersion.Current, read.GeneratorVersion);
        Assert.Equal(PrologEngine.VersionString, read.GeneratorVersion.ToString());
    }

    [Fact]
    public void BundleRoundTrip_CarriesTheGeneratorVersion()
    {
        var bundle = new Bundle(new[]
        {
            new BundleEntry("m", "p."),
        });
        byte[] bytes = BundleWriter.ToBytes(bundle);
        Bundle read = BundleReader.FromBytes(bytes);
        Assert.Equal(ShumwayVersion.Current, read.GeneratorVersion);
    }

    [Fact]
    public void TheTwoBundleWriters_StillAgreeByteForByte()
    {
        // The .shum has TWO producers (BundleWriter.ToBytes and
        // ShmoLinker.SerialiseBundle). A field added to one and not the other
        // is the classic way this format breaks; link a real program and
        // compare the linker's own image against the writer's.
        var result = ShmoLinker.LinkFromSources(
            new[] { ("app", ":- public main/0.\nmain :- true.\n") },
            new[] { new PredicateRef("main", 0) });
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.NotNull(result.Bytes);

        // The linker's own image must read back with the stamp...
        Bundle fromLinker = BundleReader.FromBytes(result.Bytes!);
        Assert.Equal(ShumwayVersion.Current, fromLinker.GeneratorVersion);

        // ...and re-serialising that same bundle through the OTHER writer
        // must produce the identical image.
        byte[] rewritten = BundleWriter.ToBytes(fromLinker);
        Assert.Equal(result.Bytes!.Length, rewritten.Length);
        Assert.Equal(result.Bytes!, rewritten);
    }

    [Fact]
    public void VersionOrdering_ComparesByMajorMinorPatch()
    {
        Assert.True(new ShumwayVersion(1, 0, 0) > new ShumwayVersion(0, 9, 9));
        Assert.True(new ShumwayVersion(1, 0, 1) > new ShumwayVersion(1, 0, 0));
        Assert.Equal(new ShumwayVersion(1, 2, 3), new ShumwayVersion(1, 2, 3));
        Assert.True(ShumwayVersion.None.IsNone);
        Assert.Equal("1.2.3", new ShumwayVersion(1, 2, 3).ToString());
    }
}
