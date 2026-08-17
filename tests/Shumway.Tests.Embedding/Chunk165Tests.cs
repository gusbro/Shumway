using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 13 chunk 165: ergonomic .NET surface around
/// <see cref="ShmoLinker"/>. Parallel to the chunk-72 Bundler API:
/// adds <see cref="ShmoLinker.LinkAsync"/>,
/// <see cref="ShmoLinker.LinkFromFiles"/>, and
/// <see cref="ShmoLinker.LinkFromSources"/> so callers don't have to
/// shell out to the CLI for in-process use.
/// </summary>
public class Chunk165Tests
{
    [Fact]
    public async Task LinkAsync_ReturnsSameResultAsSync()
    {
        var obj = ShmoCompiler.CompileSource("""
            :- module(m).
            :- public f/1.
            f(X) :- g(X).
            g(_).
            """);
        var config = new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = new[] { new PredicateRef("f", 1) },
        };
        var sync = ShmoLinker.Link(config);
        var async = await ShmoLinker.LinkAsync(config);
        Assert.Equal(sync.Success, async.Success);
        Assert.Equal(sync.ReachedModules.Count, async.ReachedModules.Count);
    }

    [Fact]
    public async Task LinkAsync_HonoursCancellationBeforeStart()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => ShmoLinker.LinkAsync(
                new LinkConfig { EntryPoints = new[] { new PredicateRef("p", 0) } },
                cts.Token));
    }

    [Fact]
    public void LinkFromFiles_ReadsShmosAndLinks()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            $"shmo-from-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var libObj = ShmoCompiler.CompileSource("""
                :- module(lib).
                :- public lib_main/1.
                lib_main(X) :- X = ok.
                """);
            var appObj = ShmoCompiler.CompileSource("""
                :- module(app).
                :- public main/1.
                main(X) :- lib_main(X).
                """);
            string libPath = Path.Combine(dir, "lib.shmo");
            string appPath = Path.Combine(dir, "app.shmo");
            ShmoWriter.WriteToFile(libObj, libPath);
            ShmoWriter.WriteToFile(appObj, appPath);

            var result = ShmoLinker.LinkFromFiles(
                shmoPaths: new[] { libPath, appPath },
                entryPoints: new[] { new PredicateRef("main", 1) });
            Assert.True(result.Success);
            Assert.Contains("lib", result.ReachedModules);
            Assert.Contains("app", result.ReachedModules);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LinkFromSources_CompilesAndLinks_NoDiskTouch()
    {
        var result = ShmoLinker.LinkFromSources(
            sources: new[]
            {
                ("util", ":- module(util).\n:- public double/2.\ndouble(X, Y) :- Y is X * 2.\n"),
                ("app", ":- module(app).\n:- public main/2.\nmain(X, Y) :- double(X, Y).\n"),
            },
            entryPoints: new[] { new PredicateRef("main", 2) });
        Assert.True(result.Success);
        Assert.NotNull(result.Bundle);
        Assert.NotNull(result.Bytes);
    }

    [Fact]
    public void LinkFromSources_LoadsAndExecutes()
    {
        var result = ShmoLinker.LinkFromSources(
            sources: new[]
            {
                ("lib", ":- module(lib).\n:- public msg/1.\nmsg(hello).\n"),
                ("app", ":- module(app).\n:- public say/1.\nsay(X) :- msg(X).\n"),
            },
            entryPoints: new[] { new PredicateRef("say", 1) });
        Assert.True(result.Success);

        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        var sol = engine.Query("say(X).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void LinkFromSources_MissingPredicate_StillReturnsBundleUnderAllowUndefined()
    {
        var result = ShmoLinker.LinkFromSources(
            sources: new[]
            {
                ("app", ":- module(app).\n:- public main/0.\nmain :- maybe_late.\n"),
            },
            entryPoints: new[] { new PredicateRef("main", 0) },
            allowUndefined: true);
        Assert.True(result.Success);
        Assert.NotNull(result.Bundle);
        Assert.Contains(result.Diagnostics,
            d => d.Code == "missing_predicate" && d.Severity == LinkSeverity.Warning);
    }
}
