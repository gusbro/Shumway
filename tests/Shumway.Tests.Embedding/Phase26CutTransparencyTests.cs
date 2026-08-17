using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 26 — a NECK cut (<c>head :- !, ...</c>) is chunk-transparent, so the
/// chunk-0 call's head-var arguments stay temporary (no environment frame),
/// matching GProlog. The Warren argument scheduler is extended to target that
/// post-neck-cut call, so reordered arguments are read before their home
/// registers are reused — no clobber. These run the pattern end-to-end so the
/// codegen change is validated at runtime, not just in the disassembly.
/// </summary>
public class Phase26CutTransparencyTests
{
    private static PrologEngine Load(string program)
    {
        var engine = new PrologEngine();
        engine.ConsultString(program);
        return engine;
    }

    [Fact]
    public void NeckCut_SwappedArgs_NotClobbered()
    {
        // route's call reuses argument-register homes: B(arg1) flows to combine's
        // arg0 while A(arg0) flows to arg1. With the neck cut transparent both
        // are temporaries; the scheduler must order the puts so neither value is
        // lost. A clobber would bind R to pair(a,a) or pair(b,b) instead of
        // pair(b,a).
        var engine = Load("""
            route(A, B, R) :- !, combine(B, A, R).
            combine(X, Y, pair(X, Y)).
            """);
        var sol = engine.Query("route(a, b, R).");
        Assert.True(sol.Success);
        Assert.Equal("pair(b, a)", sol["R"]!.ToString());
    }

    [Fact]
    public void NeckCut_FourArgReorder_FromHeadStructure()
    {
        // Mirrors the prelude $call_disj shape: C/T come from a head structure,
        // E (arg1) flows to the last call position while arg1 is reused. The
        // arg whose home is clobbered must be read first.
        var engine = Load("""
            disp((C -> T), E, K, R) :- !, build(C, T, K, E, R).
            build(C, T, K, E, c(C, T, K, E)).
            """);
        var sol = engine.Query("disp((1 -> 2), 3, 4, R).");
        Assert.True(sol.Success);
        Assert.Equal("c(1, 2, 4, 3)", sol["R"]!.ToString());
    }
}
