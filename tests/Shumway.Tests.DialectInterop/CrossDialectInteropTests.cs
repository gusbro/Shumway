using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.DialectInterop;

/// <summary>ADR-040 — cross-dialect interop against REAL third-party libraries.
/// Parameterised by environment variables naming each engine's library dir; a
/// test whose dir is unset/missing is a logged no-op (so a clone without the
/// libraries does not fail). Run explicitly with the dirs you have, e.g.
/// <c>SHUMWAY_SCRYER_LIB=C:/Scryer/lib SHUMWAY_SWI_LIB=C:/swipl/library dotnet
/// test tests/Shumway.Tests.DialectInterop/</c>.</summary>
public sealed class CrossDialectInteropTests
{
    private readonly ITestOutputHelper _out;
    public CrossDialectInteropTests(ITestOutputHelper output) => _out = output;

    private const string ScryerEnv = "SHUMWAY_SCRYER_LIB";
    private const string SwiEnv = "SHUMWAY_SWI_LIB";

    // Returns the configured, existing directory for an engine, or null (with a
    // logged reason) when the test should be skipped.
    private string? Dir(string env)
    {
        string? d = System.Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(d))
        {
            _out.WriteLine($"SKIPPED: {env} not set.");
            return null;
        }
        if (!System.IO.Directory.Exists(d))
        {
            _out.WriteLine($"SKIPPED: {env}='{d}' does not exist.");
            return null;
        }
        return d;
    }

    [Fact]
    public void Scryer_Clpz_Loads_And_Solves()
    {
        string? scryer = Dir(ScryerEnv);
        if (scryer is null) return;

        var e = new PrologEngine();
        e.AddLibraryDirectory(scryer, "scryer");
        e.ConsultString(":- use_module(library(clpz)).");
        // clpz attribute-variable constraint + labeling.
        Assert.True(e.Query("X in 1..3, indomain(X), X == 1.").Success);
        Assert.Equal(3, e.QueryAll("Y in 1..3, indomain(Y).").Count());
        Assert.False(e.Query("Z in 1..3, Z #> 5, indomain(Z).").Success);
    }

    [Fact]
    public void Swi_Heaps_Standalone_Loads_And_Works()
    {
        // A real SWI library (priority queues), loaded on its own — parses past
        // SWI's :- meta_predicate (now a known prefix operator) and works.
        string? swi = Dir(SwiEnv);
        if (swi is null) return;

        var e = new PrologEngine();
        e.AddLibraryDirectory(swi, "swi");
        e.ConsultString(":- use_module(library(heaps)).");
        // Min-heap: the smallest key comes out first.
        Assert.True(e.Query(
            "list_to_heap([3-c, 1-a, 2-b], H), get_from_heap(H, 1, a, _).").Success);
    }

    [Fact]
    public void UniteWorlds_ScryerClpz_And_SwiHeaps_InOneEngine()
    {
        // The headline ADR-040 property: a Scryer library and an SWI library,
        // each from its own system's checkout, loaded and working side by side
        // in ONE engine — attribute-variable constraints (clpz) next to a
        // priority queue (SWI heaps), each parsed in its own dialect.
        string? scryer = Dir(ScryerEnv);
        string? swi = Dir(SwiEnv);
        if (scryer is null || swi is null) return;

        var e = new PrologEngine();
        e.AddLibraryDirectory(scryer, "scryer");
        e.AddLibraryDirectory(swi, "swi");
        e.ConsultString(":- use_module(library(clpz)).");
        e.ConsultString(":- use_module(library(heaps)).");

        Assert.True(e.Query("X in 5..7, indomain(X), X == 5.").Success);        // clpz
        Assert.True(e.Query(
            "list_to_heap([2-b, 1-a], H), get_from_heap(H, 1, a, _).").Success); // SWI
        // Both in a single conjunction, one engine, one query.
        Assert.True(e.Query(
            "V in 1..9, indomain(V), V == 1, "
            + "list_to_heap([V-a], H), get_from_heap(H, V, a, _).").Success);
    }
}
