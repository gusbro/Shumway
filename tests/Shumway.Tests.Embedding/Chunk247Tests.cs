using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ---- A static foreign predicate the linker can resolve. Lives at
// namespace level so the source generator handles it cleanly. ----
public partial class C247Math
{
    [PrologPredicate("c247_double/2")]
    public static int Double(int n) => n * 2;
}

/// <summary>
/// Chunk 247: linker --foreign-dll support. The linker accepts
/// DLLs containing <c>[PrologPredicate]</c>-decorated static
/// methods, treats those name/arity pairs as resolved during
/// reachability, records the assembly filenames in the bundle,
/// and the runtime <see cref="PrologEngine.LoadBundle"/> path
/// auto-registers them.
/// </summary>
public class Chunk247Tests
{
    [Fact]
    public void Linker_WithoutForeignDll_FlagsCallAsMissing()
    {
        // Sanity baseline: a Prolog file calling a predicate that
        // doesn't exist anywhere should produce a missing_predicate
        // error. Use a name guaranteed not to be registered by any
        // other test's [PrologPredicate] (BuiltinsRegistry is
        // process-wide).
        var shmo = ShmoCompiler.CompileSource(
            ":- public main/0.\n"
            + "main :- c247_phantom_no_dll(5, X), X = 10.\n");

        var config = new LinkConfig
        {
            Objects = new[] { shmo },
            EntryPoints = new[] { new PredicateRef("main", 0) },
        };
        var result = ShmoLinker.Link(config);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "missing_predicate"
            && d.Message.Contains("c247_phantom_no_dll/2"));
    }

    [Fact]
    public void Linker_WithForeignDll_ResolvesCall()
    {
        // Same source, this time with --foreign-dll pointing at the
        // test assembly itself (which carries C247Math.Double). The
        // foreign predicate must be in BuiltinsRegistry at COMPILE
        // time for ShmoCompiler to emit CallBuiltin (the
        // process-wide registry is what ClauseCompiler consults).
        string testDll = typeof(C247Math).Assembly.Location;
        new PrologEngine().RegisterForeignAssembly(testDll);

        var shmo = ShmoCompiler.CompileSource(
            ":- public main/0.\n"
            + "main :- c247_double(5, X), X = 10.\n");

        var config = new LinkConfig
        {
            Objects = new[] { shmo },
            EntryPoints = new[] { new PredicateRef("main", 0) },
            ForeignAssemblies = new[] { testDll },
        };
        var result = ShmoLinker.Link(config);
        Assert.True(result.Success, string.Join(", ", result.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(result.Bundle);
        Assert.Contains(System.IO.Path.GetFileName(testDll), result.Bundle!.ForeignAssemblies);
    }

    [Fact]
    public void LoadBundle_AutoRegistersForeignAssembly()
    {
        // End-to-end: link with --foreign-dll, write the .shum to
        // disk next to the test DLL (so the runtime's adjacent-to-
        // bundle probe finds it), load into a fresh engine, run
        // main/0. The auto-registration must make the foreign call
        // succeed without any explicit RegisterPredicates from C#.
        // Pre-register so ShmoCompiler emits CallBuiltin for
        // c247_double (it consults BuiltinsRegistry at compile time).
        string testDll = typeof(C247Math).Assembly.Location;
        string testDir = Path.GetDirectoryName(testDll)!;
        new PrologEngine().RegisterForeignAssembly(testDll);

        var shmo = ShmoCompiler.CompileSource(
            ":- public chunk247_check/0.\n"
            + "chunk247_check :- c247_double(7, X), X =:= 14.\n");
        var config = new LinkConfig
        {
            Objects = new[] { shmo },
            EntryPoints = new[] { new PredicateRef("chunk247_check", 0) },
            ForeignAssemblies = new[] { testDll },
        };
        var result = ShmoLinker.Link(config);
        Assert.True(result.Success);

        string bundlePath = Path.Combine(testDir,
            $"chunk247-bundle-{System.Guid.NewGuid():N}.shum");
        File.WriteAllBytes(bundlePath, result.Bytes!);
        try
        {
            var engine = new PrologEngine();
            engine.LoadBundle(bundlePath);
            // chunk247_check succeeds iff Double(7) = 14.
            Assert.True(engine.Query("chunk247_check.").Success);
        }
        finally { File.Delete(bundlePath); }
    }

    [Fact]
    public void BundleFormat_V5_RoundTrip_PreservesForeignAssemblies()
    {
        // Hand-build a bundle with a ForeignAssemblies list and
        // verify writer → reader round-trip preserves it.
        var bundle = new Bundle(
            new[] { new BundleEntry("user", ":- public p/0.\np.\n") },
            foreignAssemblies: new[] { "Foreign1.dll", "Foreign2.dll" });
        byte[] bytes = BundleWriter.ToBytes(bundle);
        var roundTripped = BundleReader.FromBytes(bytes);
        Assert.Equal(new[] { "Foreign1.dll", "Foreign2.dll" },
            roundTripped.ForeignAssemblies);
    }

    [Fact]
    public void Linker_EmptyForeignDll_WarnsAndSkips()
    {
        // A DLL with no [PrologPredicate] should still link, but the
        // linker warns and the bundle's ForeignAssemblies list
        // doesn't include it (no point auto-loading at runtime).
        var shmo = ShmoCompiler.CompileSource(
            ":- public bare/0.\nbare.\n");
        // Use Shumway.Core.dll — it doesn't carry [PrologPredicate].
        string emptyDll = typeof(Shumway.Core.Engine).Assembly.Location;
        var config = new LinkConfig
        {
            Objects = new[] { shmo },
            EntryPoints = new[] { new PredicateRef("bare", 0) },
            ForeignAssemblies = new[] { emptyDll },
        };
        var result = ShmoLinker.Link(config);
        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "foreign_assembly_empty");
        Assert.Empty(result.Bundle!.ForeignAssemblies);
    }
}
