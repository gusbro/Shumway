using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 13 chunk 160: the <c>.shmo</c> compiled-object file format
/// — magic + version + body, plus <see cref="ShmoWriter"/> and
/// <see cref="ShmoReader"/>. Pure I/O. The compile-from-source path
/// (chunk 161) and the linker (chunk 163) sit on top of this layer.
/// </summary>
public class Chunk160Tests
{
    private static ShmoObject MakeMinimal()
    {
        var defined = new[]
        {
            new ShmoDefinedPredicate(new PredicateRef("foo", 1), PredicateVisibility.Public),
            new ShmoDefinedPredicate(new PredicateRef("helper", 2), PredicateVisibility.Local),
            new ShmoDefinedPredicate(new PredicateRef("counter", 1), PredicateVisibility.Dynamic),
        };
        var ensureLinked = new[]
        {
            new PredicateRef("indirect_target", 0),
            new PredicateRef("via_meta_call", 3),
        };
        var callGraph = new Dictionary<PredicateRef, IReadOnlyList<ShmoCallEdge>>
        {
            [new PredicateRef("foo", 1)] = new[]
            {
                new ShmoCallEdge(new PredicateRef("helper", 2), IsMeta: false),
                new ShmoCallEdge(new PredicateRef("write", 1), IsMeta: false),
            },
            [new PredicateRef("helper", 2)] = new[]
            {
                // chunk 441 — the per-edge META marker round-trips too.
                new ShmoCallEdge(new PredicateRef("counter", 1), IsMeta: true),
            },
        };
        var qrefs = new[]
        {
            new QualifiedPredicateRef("lists", "append", 3),
        };
        return new ShmoObject(
            moduleName: "demo",
            source: "foo(X) :- helper(X, _), write(X).\nhelper(X, Y) :- counter(X), Y = ok.\n",
            bytecode: new byte[] { 0x01, 0x02, 0x03, 0xAA, 0xBB },
            defined: defined,
            ensureLinked: ensureLinked,
            callGraph: callGraph,
            qualifiedRefs: qrefs);
    }

    [Fact]
    public void Magic_StartsWithSHMO()
    {
        var obj = MakeMinimal();
        byte[] bytes = ShmoWriter.ToBytes(obj);
        Assert.Equal((byte)'S', bytes[0]);
        Assert.Equal((byte)'H', bytes[1]);
        Assert.Equal((byte)'M', bytes[2]);
        Assert.Equal((byte)'O', bytes[3]);
    }

    [Fact]
    public void Version_IsCurrentVersion()
    {
        var obj = MakeMinimal();
        byte[] bytes = ShmoWriter.ToBytes(obj);
        uint version = BitConverter.ToUInt32(bytes, 4);
        Assert.Equal((uint)ShmoFormat.CurrentVersion, version);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = MakeMinimal();
        byte[] bytes = ShmoWriter.ToBytes(original);
        var restored = ShmoReader.FromBytes(bytes);

        Assert.Equal(original.ModuleName, restored.ModuleName);
        Assert.Equal(original.Source, restored.Source);
        Assert.Equal(original.Bytecode, restored.Bytecode);

        Assert.Equal(original.Defined.Count, restored.Defined.Count);
        for (int i = 0; i < original.Defined.Count; i++)
        {
            Assert.Equal(original.Defined[i].Indicator, restored.Defined[i].Indicator);
            Assert.Equal(original.Defined[i].Visibility, restored.Defined[i].Visibility);
        }

        Assert.Equal(original.EnsureLinked, restored.EnsureLinked);

        Assert.Equal(original.CallGraph.Count, restored.CallGraph.Count);
        foreach (var (k, v) in original.CallGraph)
        {
            Assert.True(restored.CallGraph.TryGetValue(k, out var rv));
            Assert.Equal(v, rv);
        }

        Assert.Equal(original.QualifiedRefs, restored.QualifiedRefs);
    }

    [Fact]
    public void Empty_AllSectionsZero_RoundTrips()
    {
        var obj = new ShmoObject(
            moduleName: "",
            source: "",
            bytecode: Array.Empty<byte>(),
            defined: Array.Empty<ShmoDefinedPredicate>(),
            ensureLinked: Array.Empty<PredicateRef>(),
            callGraph: new Dictionary<PredicateRef, IReadOnlyList<ShmoCallEdge>>(),
            qualifiedRefs: Array.Empty<QualifiedPredicateRef>());
        byte[] bytes = ShmoWriter.ToBytes(obj);
        var restored = ShmoReader.FromBytes(bytes);
        Assert.Equal("", restored.ModuleName);
        Assert.Equal("", restored.Source);
        Assert.Empty(restored.Bytecode);
        Assert.Empty(restored.Defined);
        Assert.Empty(restored.EnsureLinked);
        Assert.Empty(restored.CallGraph);
        Assert.Empty(restored.QualifiedRefs);
    }

    [Fact]
    public void BadMagic_Throws()
    {
        var obj = MakeMinimal();
        byte[] bytes = ShmoWriter.ToBytes(obj);
        bytes[0] = (byte)'X';
        var ex = Assert.Throws<InvalidDataException>(() => ShmoReader.FromBytes(bytes));
        Assert.Contains("SHMO", ex.Message);
    }

    [Fact]
    public void TooShortMagic_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => ShmoReader.FromBytes(new byte[] { (byte)'S', (byte)'H' }));
        Assert.Contains("SHMO", ex.Message);
    }

    [Fact]
    public void UnsupportedVersion_Throws()
    {
        var obj = MakeMinimal();
        byte[] bytes = ShmoWriter.ToBytes(obj);
        // Bump version to 999.
        bytes[4] = 0xE7;
        bytes[5] = 0x03;
        bytes[6] = 0x00;
        bytes[7] = 0x00;
        var ex = Assert.Throws<InvalidDataException>(() => ShmoReader.FromBytes(bytes));
        Assert.Contains("999", ex.Message);
        Assert.Contains("requires", ex.Message);
    }

    [Fact]
    public void UnknownVisibilityByte_Throws()
    {
        // Build a hand-crafted minimal payload with a bad visibility byte.
        var obj = new ShmoObject(
            moduleName: "m",
            source: "",
            bytecode: Array.Empty<byte>(),
            defined: new[]
            {
                new ShmoDefinedPredicate(new PredicateRef("p", 0), PredicateVisibility.Public),
            },
            ensureLinked: Array.Empty<PredicateRef>(),
            callGraph: new Dictionary<PredicateRef, IReadOnlyList<ShmoCallEdge>>(),
            qualifiedRefs: Array.Empty<QualifiedPredicateRef>());
        byte[] bytes = ShmoWriter.ToBytes(obj);
        // Find and corrupt the visibility byte. V2 layout:
        //  4 magic + 4 version
        //  + 1 compression flag (Phase 33 T6; 0 = raw for this tiny body)
        //  + 4 moduleNameLen + 1 ('m')
        //  + 4 sourceLen
        //  + 4 bytecodeLen
        //  + 1 buildMode                                 ← V2 addition
        //  + 1 arityCompat                               ← chunk 441
        //  + 4 definedCount + 4 nameLen + 1 ('p') + 4 arity + 1 visibility
        // Plus 12 bytes of generator version (3 × uint32), the FIRST body
        // field — see ShmoWriter.
        // visibility byte is at 4+4+1 + 12 + 4+1 + 4 + 4 + 1 + 1 + 4 + 4+1 + 4 = 49.
        bytes[49] = 99;
        var ex = Assert.Throws<InvalidDataException>(() => ShmoReader.FromBytes(bytes));
        Assert.Contains("visibility", ex.Message);
    }

    [Fact]
    public void TruncatedPayload_Throws()
    {
        var obj = MakeMinimal();
        byte[] bytes = ShmoWriter.ToBytes(obj);
        byte[] truncated = bytes.AsSpan(0, bytes.Length - 5).ToArray();
        Assert.ThrowsAny<Exception>(() => ShmoReader.FromBytes(truncated));
    }

    [Fact]
    public void WriteToFile_ReadFromFile_RoundTrips()
    {
        var original = MakeMinimal();
        string path = Path.Combine(Path.GetTempPath(),
            $"shmo-test-{Guid.NewGuid():N}.shmo");
        try
        {
            ShmoWriter.WriteToFile(original, path);
            var restored = ShmoReader.ReadFromFile(path);
            Assert.Equal(original.ModuleName, restored.ModuleName);
            Assert.Equal(original.Bytecode, restored.Bytecode);
            Assert.Equal(original.Defined.Count, restored.Defined.Count);
            Assert.Equal(original.EnsureLinked.Count, restored.EnsureLinked.Count);
            Assert.Equal(original.CallGraph.Count, restored.CallGraph.Count);
            Assert.Equal(original.QualifiedRefs.Count, restored.QualifiedRefs.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PredicateRef_ToString()
    {
        Assert.Equal("foo/2", new PredicateRef("foo", 2).ToString());
        Assert.Equal("lists:append/3",
            new QualifiedPredicateRef("lists", "append", 3).ToString());
    }
}
