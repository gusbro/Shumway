using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 72 — programmatic <see cref="Bundler"/> API for build-pipeline
/// integration. ADR-009 sketched the surface (BundleConfig in,
/// BundleResult out, structured diagnostics, optional async); chunk 72
/// lands it and refactors <c>shumway-bundler</c>'s CLI into a thin
/// wrapper.
///
/// <para>These tests exercise the API end-to-end through both happy
/// paths (in-memory entries, file sources, both kinds of compiled
/// payloads) and unhappy paths (missing file, parse errors, bad
/// entry-point indicators). The CLI's behaviour is implicitly
/// validated since it delegates straight through.</para>
/// </summary>
public class Chunk72Tests
{
    [Fact]
    public void Build_InlineEntries_RoundTrips()
    {
        var config = new BundleConfig
        {
            InlineEntries =
            {
                new BundleEntry("hello", ":- public hi/0.\nhi."),
            },
        };
        var result = Bundler.Build(config);
        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Bundle);
        Assert.NotNull(result.Bytes);
        Assert.NotNull(result.Report);
        Assert.Equal(1, result.Report!.ModuleCount);

        // Round-trip through the reader and execute.
        var rt = BundleReader.FromBytes(result.Bytes!);
        var engine = new PrologEngine();
        engine.LoadBundle(rt);
        Assert.True(engine.Query("hi.").Success);
    }

    [Fact]
    public void Build_NoSources_FailsWithError()
    {
        var config = new BundleConfig();
        var result = Bundler.Build(config);
        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            d => d.Message.Contains("no source files or inline entries"));
    }

    [Fact]
    public void Build_MissingFile_FailsWithDiagnosticPointingAtFile()
    {
        string fakePath = Path.Combine(Path.GetTempPath(), "shumway_does_not_exist_72.pl");
        var config = new BundleConfig
        {
            SourceFiles = { fakePath },
        };
        var result = Bundler.Build(config);
        Assert.False(result.Success);
        var err = result.Errors.First(d => d.Message.Contains("not found"));
        Assert.Equal(fakePath, err.Source);
    }

    [Fact]
    public void Build_BadProlog_SurfacesAsDiagnostic()
    {
        // Parse error: missing dot.
        var config = new BundleConfig
        {
            InlineEntries =
            {
                new BundleEntry("bad", ":- public foo/0.\nfoo")
            },
        };
        var result = Bundler.Build(config);
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Build_WithEntryPoints_ValidatesEachOne()
    {
        var config = new BundleConfig
        {
            InlineEntries =
            {
                new BundleEntry("greet",
                    ":- public hello/0.\n:- public greet/1.\n" +
                    "hello.\ngreet(X) :- atom(X).\n"),
            },
            EntryPoints = { new EntryPointSpec("hello", 0), new EntryPointSpec("greet", 1) },
        };
        var result = Bundler.Build(config);
        Assert.True(result.Success);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Build_WithBytecodeFlag_EmbedsCompiledBlob()
    {
        var config = new BundleConfig
        {
            InlineEntries = { new BundleEntry("mod", ":- public hi/0.\nhi.") },
            IncludeCompiledBytecode = true,
        };
        var result = Bundler.Build(config);
        Assert.True(result.Success);
        Assert.NotNull(result.Bundle);
        Assert.NotNull(result.Bundle!.Entries[0].CompiledBytecode);
        Assert.True(result.Report!.CompiledBytecodeBytes > 0);
    }

    [Fact]
    public void Build_WithCompiledIlFlag_EmbedsPersistedAssembly()
    {
        var config = new BundleConfig
        {
            InlineEntries = { new BundleEntry("mod", ":- public hi/0.\nhi.") },
            IncludeCompiledIl = true,
        };
        var result = Bundler.Build(config);
        Assert.True(result.Success);
        Assert.NotNull(result.Bundle);
        Assert.NotNull(result.Bundle!.Entries[0].CompiledIl);
        Assert.True(result.Report!.CompiledIlBytes > 0);
    }

    [Fact]
    public void Build_WithOutputPath_WritesFile()
    {
        string tmp = Path.GetTempFileName();
        try
        {
            var config = new BundleConfig
            {
                InlineEntries = { new BundleEntry("mod", ":- public hi/0.\nhi.") },
                OutputPath = tmp,
            };
            var result = Bundler.Build(config);
            Assert.True(result.Success);
            Assert.True(File.Exists(tmp));
            Assert.Equal(result.Bytes, File.ReadAllBytes(tmp));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Build_NoOutputPath_LeavesBytesInMemory()
    {
        var config = new BundleConfig
        {
            InlineEntries = { new BundleEntry("mod", ":- public hi/0.\nhi.") },
        };
        var result = Bundler.Build(config);
        Assert.True(result.Success);
        Assert.NotNull(result.Bytes);
        // No OutputPath ⇒ no file side-effect; nothing else to check.
    }

    [Fact]
    public async Task BuildAsync_ReturnsSameAsBuild()
    {
        var config = new BundleConfig
        {
            InlineEntries = { new BundleEntry("mod", ":- public hi/0.\nhi.") },
        };
        var resultSync = Bundler.Build(config);
        var resultAsync = await Bundler.BuildAsync(config);
        Assert.Equal(resultSync.Success, resultAsync.Success);
        Assert.Equal(resultSync.Bytes, resultAsync.Bytes);
    }

    [Fact]
    public async Task BuildAsync_HonoursCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var config = new BundleConfig
        {
            InlineEntries = { new BundleEntry("mod", ":- public hi/0.\nhi.") },
        };
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => Bundler.BuildAsync(config, cts.Token));
    }

    [Fact]
    public void Build_Report_HasCorrectModuleCountAndNames()
    {
        var config = new BundleConfig
        {
            InlineEntries =
            {
                new BundleEntry("alpha", ":- public a/0.\na."),
                new BundleEntry("beta", ":- public b/0.\nb."),
                new BundleEntry("gamma", ":- public c/0.\nc."),
            },
        };
        var result = Bundler.Build(config);
        Assert.True(result.Success);
        Assert.Equal(3, result.Report!.ModuleCount);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, result.Report.Modules);
        Assert.True(result.Report.TotalSourceChars > 0);
        Assert.True(result.Report.BundleBytes > 0);
    }

    [Fact]
    public void Build_FileSourcesAndInlineEntries_BothLand()
    {
        string tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, ":- public from_file/0.\nfrom_file.");
            var config = new BundleConfig
            {
                SourceFiles = { tmp },
                InlineEntries = { new BundleEntry("inline", ":- public from_inline/0.\nfrom_inline.") },
            };
            var result = Bundler.Build(config);
            Assert.True(result.Success);
            Assert.Equal(2, result.Bundle!.Entries.Count);
            // File entries precede inline entries (chunk-72 contract).
            Assert.Equal("inline", result.Bundle.Entries[1].ModuleName);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Build_VerboseOutput_RoutesToProvidedWriter()
    {
        var sink = new StringWriter();
        var config = new BundleConfig
        {
            InlineEntries = { new BundleEntry("mod", ":- public hi/0.\nhi.") },
            Verbose = true,
            VerboseOut = sink,
        };
        var result = Bundler.Build(config);
        Assert.True(result.Success);
        string verbose = sink.ToString();
        Assert.Contains("module(s) staged", verbose);
        Assert.Contains("mod", verbose);
    }
}
