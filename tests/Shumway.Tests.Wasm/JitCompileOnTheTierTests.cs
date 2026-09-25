using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>jit_compile/1 against a wasm world: here Tier-1 IS the wasm
/// backend, which is the arrangement WebShumway ships and the one the
/// conformance corpus asks for when it runs in a browser. The desktop
/// runner builds three engines instead; this is the other half, where one
/// engine is told to change mode.</summary>
public sealed class JitCompileOnTheTierTests
{
    private const string Corpus = """
        count(0, []) :- !.
        count(N, [_|T]) :- count(M, T), N is M + 1.
        len(L, N) :- count(N, L).
        """;

    private const string Work = "numlist(1, 60, L), len(L, N), N =:= 60.";

    [Fact]
    public void OffStopsPromotingToWasmAndReturnsWhatDid()
    {
        var (e, members, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        Assert.True(e.Query(Work).Success);
        Assert.NotEmpty(members);
        Assert.NotEmpty(e.IlPromotion.PromotedFunctorIds());

        Assert.True(e.Query("jit_compile(off).").Success);
        // The eviction is queued for the next query setup, so it is the goal
        // AFTER the switch that finds a Tier-0 engine.
        Assert.True(e.Query("true.").Success);
        Assert.Empty(e.IlPromotion.PromotedFunctorIds());

        // And it stays off: running more work promotes nothing back.
        Assert.True(e.Query(Work).Success);
        Assert.Empty(e.IlPromotion.PromotedFunctorIds());
    }

    /// <summary>Back on again, in the same engine. Without this, "off" could
    /// be a one-way door and the test above would not notice.</summary>
    [Fact]
    public void AndOnAgain()
    {
        var (e, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        Assert.True(e.Query("jit_compile(off).").Success);
        Assert.True(e.Query(Work).Success);
        Assert.Empty(e.IlPromotion.PromotedFunctorIds());

        Assert.True(e.Query("jit_compile(all).").Success);
        Assert.True(e.Query(Work).Success);
        Assert.NotEmpty(e.IlPromotion.PromotedFunctorIds());
    }

    /// <summary>The answers are the same in every mode, switched mid-session.
    /// This is the property the corpus checks across three engines, asserted
    /// here across one engine's mode changes.</summary>
    [Fact]
    public void TheAnswersDoNotDependOnTheMode()
    {
        var (e, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        foreach (string mode in new[] { "all", "off", "3", "all", "off" })
        {
            Assert.True(e.Query($"jit_compile({mode}).").Success, mode);
            Assert.True(e.Query(Work).Success, mode);
            Assert.True(e.Query("numlist(1, 9, L), len(L, N), N =:= 9.").Success, mode);
        }
    }

    /// <summary>The store's own policy hook takes precedence, which is how
    /// WebShumway attaches its world lazily and compiles the whole program
    /// for "all". Desktop has no such need, so this checks the seam rather
    /// than the behaviour behind it.</summary>
    [Fact]
    public void AHostPolicyOverridesTheDefault()
    {
        var (e, _, _) = TieredEngine.BuildWithWorld(Corpus, wasmThreshold: 1);
        var asked = new List<int>();
        e.IlPromotion.JitPolicy = t => { asked.Add(t); return t != 7; };

        Assert.True(e.Query("jit_compile(all).").Success);
        Assert.True(e.Query("jit_compile(off).").Success);
        Assert.False(e.Query("jit_compile(7).").Success);
        Assert.Equal(new[] { 1, 0, 7 }, asked);
    }
}
