using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Coverage for the bundle format and the engine's LoadBundle path
/// (chunk 22). Tests focus on round-trip correctness and on the
/// validation that bundle writes catch at bundle time.
/// </summary>
public class BundleTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    [Fact]
    public void Bundle_RoundTripsThroughBytes()
    {
        var bundle = new Bundle(new[]
        {
            new BundleEntry("user", "p(a).\np(b).\n"),
        });
        byte[] bytes = BundleWriter.ToBytes(bundle);
        Bundle loaded = BundleReader.FromBytes(bytes);

        Assert.Single(loaded.Entries);
        Assert.Equal("user", loaded.Entries[0].ModuleName);
        Assert.Equal("p(a).\np(b).\n", loaded.Entries[0].Source);
    }

    [Fact]
    public void Bundle_MagicBytes_StartWithSHUM()
    {
        var bundle = new Bundle(new[] { new BundleEntry("user", "ok.") });
        byte[] bytes = BundleWriter.ToBytes(bundle);
        Assert.Equal((byte)'S', bytes[0]);
        Assert.Equal((byte)'H', bytes[1]);
        Assert.Equal((byte)'U', bytes[2]);
        Assert.Equal((byte)'M', bytes[3]);
    }

    [Fact]
    public void Bundle_BadMagic_Throws()
    {
        // Crafted bytes that look like a bundle but aren't.
        byte[] notBundle = { (byte)'X', (byte)'X', (byte)'X', (byte)'X', 1, 0, 0, 0 };
        Assert.Throws<InvalidDataException>(() => BundleReader.FromBytes(notBundle));
    }

    [Fact]
    public void Bundle_UnsupportedVersion_Throws()
    {
        byte[] futureVersion =
        {
            (byte)'S', (byte)'H', (byte)'U', (byte)'M',  // magic
            99, 0, 0, 0,                                  // version 99
            0, 0, 0, 0,                                   // 0 modules
        };
        Assert.Throws<InvalidDataException>(() => BundleReader.FromBytes(futureVersion));
    }

    // ---------- LoadBundle ----------

    [Fact]
    public void LoadBundle_PopulatesEngineAndQueriesSucceed()
    {
        var bundle = new Bundle(new[]
        {
            new BundleEntry("user", "colour(red).\ncolour(green).\ncolour(blue).\n"),
        });

        var engine = new PrologEngine();
        engine.LoadBundle(bundle);

        var solutions = engine.QueryAll("colour(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Atom("red"), Atom("green"), Atom("blue") }, solutions);
    }

    [Fact]
    public void LoadBundle_MultiModuleWithCrossCall_Works()
    {
        var bundle = new Bundle(new[]
        {
            new BundleEntry("lib",
                ":- module(lib).\n" +
                ":- public square/2.\n" +
                "square(X, Y) :- Y is X * X.\n"),
            new BundleEntry("client",
                ":- module(client).\n" +
                ":- public report/2.\n" +
                "report(X, R) :- square(X, R).\n"),
        });

        var engine = new PrologEngine();
        engine.LoadBundle(bundle);

        Assert.Equal(Int(49), engine.Query("report(7, R).")["R"]);
    }

    [Fact]
    public void LoadBundle_FromDisk_RoundTrip()
    {
        var bundle = new Bundle(new[]
        {
            new BundleEntry("user", "fact(1).\nfact(2).\nfact(3).\n"),
        });

        string path = Path.Combine(Path.GetTempPath(),
            $"shumway-test-{Guid.NewGuid():N}.shum");
        try
        {
            BundleWriter.WriteToFile(bundle, path);
            Assert.True(File.Exists(path));

            var engine = new PrologEngine();
            engine.LoadBundle(path);
            var solutions = engine.QueryAll("fact(X).").Select(s => s["X"]).ToList();
            Assert.Equal(new[] { Int(1), Int(2), Int(3) }, solutions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---------- Bundle-time validation ----------

    [Fact]
    public void Write_PublicCollision_Throws()
    {
        // Two modules both declare 'shared/1' public — the bundle writer's
        // validation pass should catch it before we hit disk.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("a",
                ":- module(a).\n:- public shared/1.\nshared(from_a).\n"),
            new BundleEntry("b",
                ":- module(b).\n:- public shared/1.\nshared(from_b).\n"),
        });
        Assert.Throws<InvalidOperationException>(() => BundleWriter.ToBytes(bundle));
    }

    [Fact]
    public void Write_SyntaxError_Throws()
    {
        // Missing terminal '.' — the source can't be parsed.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("user", "p(a) p(b)"),
        });
        Assert.ThrowsAny<Exception>(() => BundleWriter.ToBytes(bundle));
    }

    // ---------- Combination ----------

    [Fact]
    public void Bundle_PreservesArithmeticAndDcg()
    {
        // A bundle exercising both arithmetic and DCG rules — exactly the
        // kinds of features a real application would carry.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("user",
                "factorial(0, 1).\n" +
                "factorial(N, F) :- N > 0, M is N - 1, factorial(M, MF), F is N * MF.\n" +
                "noun --> [dog].\n" +
                "noun --> [cat].\n"),
        });

        var engine = new PrologEngine();
        engine.LoadBundle(bundle);

        Assert.Equal(Int(120), engine.Query("factorial(5, F).")["F"]);
        Assert.True(engine.Query("phrase(noun, [dog]).").Success);
        Assert.False(engine.Query("phrase(noun, [bird]).").Success);
    }
}
