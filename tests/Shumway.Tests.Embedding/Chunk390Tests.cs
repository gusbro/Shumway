using System.Collections.Generic;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 390 (Phase 29, Stage 9b-2 — the fid bridge): mapping the linker's
/// <c>(module, PredicateRef)</c> seeds to the functor ids the compiled bytecode uses
/// (<see cref="ShmoLinker.ResolveSeedFids"/>). A predicate's functor name is MANGLED
/// (<c>module$name</c>, local) or BARE (<c>name</c>, public / dynamic / promoted entry);
/// resolving BOTH forms that exist is a sound over-keep (a seed's true fid is always one
/// of them). A seed that names no decoded predicate (e.g. a dynamic predicate, whose
/// clauses live in the DynamicSeeds trailer, not the static bytecode) resolves to nothing
/// — correctly, since it is never region-compiled and so never prunable.
/// </summary>
public class Chunk390Tests
{
    private static PredicateRef P(string n, int a) => new(n, a);

    [Fact]
    public void LocalSeed_ResolvesViaMangledName()
    {
        var byName = new Dictionary<(string, int), int> { [("m$foo", 2)] = 42 };
        var fids = ShmoLinker.ResolveSeedFids(new[] { ("m", P("foo", 2)) }, byName);
        Assert.Equal(new HashSet<int> { 42 }, fids);
    }

    [Fact]
    public void PublicSeed_ResolvesViaBareName()
    {
        var byName = new Dictionary<(string, int), int> { [("pub", 1)] = 7 };
        var fids = ShmoLinker.ResolveSeedFids(new[] { ("m", P("pub", 1)) }, byName);
        Assert.Equal(new HashSet<int> { 7 }, fids);
    }

    [Fact]
    public void BothFormsPresent_AddsBoth_SoundOverKeep()
    {
        // A local m$foo AND an unrelated bare foo both exist; we keep both (the seed's
        // true fid is included whichever form it is — over-keep is sound for a seed).
        var byName = new Dictionary<(string, int), int>
        {
            [("m$foo", 0)] = 10,
            [("foo", 0)] = 20,
        };
        var fids = ShmoLinker.ResolveSeedFids(new[] { ("m", P("foo", 0)) }, byName);
        Assert.Equal(new HashSet<int> { 10, 20 }, fids);
    }

    [Fact]
    public void DynamicSeedNotInStaticSet_ResolvesToNothing()
    {
        // A dynamic predicate is not in the decoded static predicates → neither form
        // matches → no fid. Correct: it is never region-compiled, never prunable.
        var byName = new Dictionary<(string, int), int> { [("m$other", 0)] = 1 };
        var fids = ShmoLinker.ResolveSeedFids(new[] { ("m", P("dyn_state", 1)) }, byName);
        Assert.Empty(fids);
    }

    [Fact]
    public void MultipleSeeds_Accumulate()
    {
        var byName = new Dictionary<(string, int), int>
        {
            [("m$a", 0)] = 1,
            [("pub", 2)] = 2,
        };
        var fids = ShmoLinker.ResolveSeedFids(
            new[] { ("m", P("a", 0)), ("m", P("pub", 2)) }, byName);
        Assert.Equal(new HashSet<int> { 1, 2 }, fids);
    }
}
