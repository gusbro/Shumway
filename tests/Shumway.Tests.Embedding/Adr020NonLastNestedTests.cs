using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-020 — non-last nested compound build (body args) via the
/// reserve-upfront roots <c>put_structure_r</c> / <c>put_list_r</c> and the
/// runtime write-pointer frame stack produces the same term as the BFS would,
/// end-to-end. Covers single / multiple / deep non-last nesting, list literals
/// whose head is a compound, variables flowing into nested args, the mixed
/// last+non-last case, and the same shapes under Tier-1 IL promotion.</summary>
public class Adr020NonLastNestedTests
{
    private static PrologEngine Load(string program, int ilThreshold = 0)
    {
        var engine = new PrologEngine();
        if (ilThreshold > 0) engine.IlPromotion.Threshold = ilThreshold;
        engine.ConsultString(program);
        return engine;
    }

    [Fact]
    public void SingleNonLastNested_BuildsCorrectly()
    {
        // g(a) is the non-last arg of f/2 → reserve-upfront build.
        var engine = Load("p(X) :- X = f(g(a), b).");
        var sol = engine.Query("p(R).");
        Assert.True(sol.Success);
        Assert.Equal("f(g(a), b)", sol["R"]!.ToString());
    }

    [Fact]
    public void MultipleNonLastNested_BuildsCorrectly()
    {
        var engine = Load("p(X) :- X = f(g(a), h(b), c).");
        var sol = engine.Query("p(R).");
        Assert.True(sol.Success);
        Assert.Equal("f(g(a), h(b), c)", sol["R"]!.ToString());
    }

    [Fact]
    public void DeepNonLastNested_BuildsCorrectly()
    {
        // g(h(a), b) is non-last in f, and h(a) is non-last in g — two levels of
        // non-last nesting, exercising the frame-stack cascade.
        var engine = Load("p(X) :- X = f(g(h(a), b), c).");
        var sol = engine.Query("p(R).");
        Assert.True(sol.Success);
        Assert.Equal("f(g(h(a), b), c)", sol["R"]!.ToString());
    }

    [Fact]
    public void ListOfCompounds_HeadIsNonLast_BuildsCorrectly()
    {
        // [point(1,2), point(3,4)] = '.'(point(1,2), '.'(point(3,4), []));
        // each cons head is a non-last compound.
        var engine = Load("p(X) :- X = [point(1, 2), point(3, 4)].");
        var sol = engine.Query("p([A, B]), A = point(1, 2), B = point(3, 4).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void VariablesFlowIntoNested()
    {
        // Bound input vars must reach the nested args.
        var engine = Load("mk(A, B, R) :- R = wrap(pair(A, B), tag).");
        var sol = engine.Query("mk(1, 2, R).");
        Assert.True(sol.Success);
        Assert.Equal("wrap(pair(1, 2), tag)", sol["R"]!.ToString());
    }

    [Fact]
    public void MixedLastAndNonLastNested()
    {
        // g(a) non-last, h(b) last — both inline in the reserved build.
        var engine = Load("p(X) :- X = f(g(a), h(b)).");
        var sol = engine.Query("p(R).");
        Assert.True(sol.Success);
        Assert.Equal("f(g(a), h(b))", sol["R"]!.ToString());
    }

    [Fact]
    public void UnifyAgainstExistingTerm_MatchAndMismatch()
    {
        var engine = Load("p(X) :- X = f(g(a), b).");
        Assert.True(engine.Query("p(f(g(a), b)).").Success);
        Assert.False(engine.Query("p(f(g(c), b)).").Success);
        Assert.False(engine.Query("p(f(g(a), z)).").Success);
    }

    [Fact]
    public void SharedVariableAcrossNonLastNested()
    {
        // The same X appears in a non-last nested arg and the last arg — both
        // must resolve to the same value.
        var engine = Load("p(X, R) :- R = pair(box(X), X).");
        var sol = engine.Query("p(7, R), R = pair(box(V1), V2), V1 == 7, V2 == 7.");
        Assert.True(sol.Success);
    }

    // ----- Same shapes under Tier-1 IL promotion -----

    [Fact]
    public void DeepNonLastNested_UnderIl()
    {
        var engine = Load("p(X) :- X = f(g(h(a), b), c).", ilThreshold: 1);
        Assert.Equal("f(g(h(a), b), c)", engine.Query("p(R).")["R"]!.ToString());
        // Re-run so the predicate is promoted and the IL path runs too.
        Assert.Equal("f(g(h(a), b), c)", engine.Query("p(R).")["R"]!.ToString());
    }

    [Fact]
    public void ListOfCompounds_UnderIl()
    {
        var engine = Load("p(X) :- X = [point(1, 2), point(3, 4)].", ilThreshold: 1);
        for (int i = 0; i < 3; i++)
            Assert.True(engine.Query("p([point(1, 2), point(3, 4)]).").Success);
    }
}
