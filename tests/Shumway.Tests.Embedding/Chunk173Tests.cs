using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 14 chunk 173: <see cref="ShmoBundleMap"/> emits a
/// human-readable text report describing what landed in a linked
/// bundle. The <c>shumway-link --map FILE</c> CLI wires the file
/// output; the API surface
/// (<see cref="ShmoBundleMap.GenerateText(IReadOnlyList{ShmoObject},
/// IReadOnlyList{PredicateRef}, LinkResult)"/>) is the in-process
/// equivalent.
/// </summary>
public class Chunk173Tests
{
    private static ShmoObject Compile(string src, string fallback)
        => ShmoCompiler.CompileSource(src, fallback);

    [Fact]
    public void Map_ListsReachedModulesWithSizesAndPredicates()
    {
        var lib = Compile(
            ":- module(lib).\n:- public greet/1.\n:- dynamic state/1.\ngreet(hi).\n", "lib");
        var app = Compile(
            ":- module(app).\n:- public main/1.\nmain(X) :- greet(X).\n", "app");
        var entries = new[] { new PredicateRef("main", 1) };
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { lib, app },
            EntryPoints = entries,
        });
        string map = ShmoBundleMap.GenerateText(new[] { lib, app }, entries, result);

        Assert.Contains("lib", map);
        Assert.Contains("app", map);
        Assert.Contains("greet/1", map);
        Assert.Contains("state/1", map);    // dynamic
        Assert.Contains("main/1", map);
        Assert.Contains("Entry points", map);
        Assert.Contains("Totals", map);
    }

    [Fact]
    public void Map_ReportsUnreachableModules()
    {
        var used = Compile(":- module(used).\n:- public foo/0.\nfoo.\n", "used");
        var dead = Compile(":- module(dead).\n:- public bar/0.\nbar.\n", "dead");
        var entries = new[] { new PredicateRef("foo", 0) };
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { used, dead },
            EntryPoints = entries,
        });
        string map = ShmoBundleMap.GenerateText(new[] { used, dead }, entries, result);
        Assert.Contains("Modules dropped", map);
        Assert.Contains("dead", map);
    }

    [Fact]
    public void Map_RecordsBuildMode()
    {
        var release = Compile(":- module(r).\n:- public p/0.\np.\n", "r");
        var debug = ShmoCompiler.CompileSource(
            ":- module(d).\n:- public q/0.\nq.\n", "d", ShmoBuildMode.Debug);
        var entries = new[] { new PredicateRef("p", 0), new PredicateRef("q", 0) };
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { release, debug },
            EntryPoints = entries,
        });
        string map = ShmoBundleMap.GenerateText(new[] { release, debug }, entries, result);
        Assert.Contains("release", map);
        Assert.Contains("debug", map);
    }

    [Fact]
    public void Map_WriteToFile_RoundTrips()
    {
        var obj = Compile(":- module(m).\n:- public p/0.\np.\n", "m");
        var entries = new[] { new PredicateRef("p", 0) };
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = entries,
        });
        string path = Path.Combine(Path.GetTempPath(),
            $"map-{Guid.NewGuid():N}.map");
        try
        {
            ShmoBundleMap.WriteToFile(new[] { obj }, entries, result, path);
            Assert.True(File.Exists(path));
            string text = File.ReadAllText(path);
            Assert.Contains("Shumway link map", text);
            Assert.Contains("p/0", text);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Map_IncludesEnsureLinkedHints()
    {
        var disp = Compile(
            ":- module(disp).\n:- public d/0.\n:- ensure_linked target/1.\nd :- call(target(_)).\n",
            "disp");
        var target = Compile(":- module(t).\n:- public target/1.\ntarget(_).\n", "t");
        var entries = new[] { new PredicateRef("d", 0) };
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { disp, target },
            EntryPoints = entries,
        });
        string map = ShmoBundleMap.GenerateText(new[] { disp, target }, entries, result);
        Assert.Contains("ensure_linked", map);
        Assert.Contains("target/1", map);
    }
}
