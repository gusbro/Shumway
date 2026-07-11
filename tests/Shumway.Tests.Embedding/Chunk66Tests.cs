using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 66: IL non-leaf callee support via meta-CP. Each non-tail
/// IL Call site captures <c>engine.B</c> as <c>preCallB</c>, invokes
/// the sub-call helper, then on success pushes a per-site IL choice
/// point that saves preCallB as <c>Cell.Int(preCallB)</c> in an
/// arity-1 frame slot. On backtrack the resume path reads preCallB
/// back via <see cref="Shumway.Compiler.Il.IlRuntimeHelpers.ReadPreCallB"/>,
/// drives <see cref="Activation.BacktrackRunner"/> to fetch the callee's
/// next solution, re-pushes a fresh meta-CP for the iteration after
/// that, and rejoins the body at the post-call label.
///
/// <para>The investigation rounds in chunks 63 / 65 / 66 traced the
/// failure mode (callee try_me_else CPs capture sentinel-Cp inside
/// RunSubroutine, short-circuiting the IL caller's continuation on
/// backtrack) and identified the meta-CP design as the fix. The
/// deep-dive landing here closes the gap.</para>
/// </summary>
public class Chunk66Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    // ============================================================================
    // Tier-0 baselines for the cross-product shape
    // ============================================================================

    [Fact]
    public void Tier0_NonLeafCrossProduct_FullCorrect()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public pair/2.\n" +
            ":- public left/1.\n" +
            ":- public right/1.\n" +
            "left(a). left(b). left(c).\n" +
            "right(1). right(2).\n" +
            "pair(X, Y) :- left(X), right(Y).\n");
        Assert.Equal(6, engine.QueryAll("pair(_, _).").Count());
    }

    [Fact]
    public void Tier0_NonLeafTripleProduct_FullCorrect()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public triple/3.\n" +
            ":- public dim_a/1.\n" +
            ":- public dim_b/1.\n" +
            ":- public dim_c/1.\n" +
            "dim_a(x). dim_a(y).\n" +
            "dim_b(p). dim_b(q). dim_b(r).\n" +
            "dim_c(7).\n" +
            "triple(A, B, C) :- dim_a(A), dim_b(B), dim_c(C).\n");
        Assert.Equal(6, engine.QueryAll("triple(_, _, _).").Count());
    }

    // ============================================================================
    // Tier-1 meta-CP under IL promotion
    // ============================================================================

    [Fact]
    public void MetaCp_PromotesAcrossNonLeafCalls()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public pair/2.\n" +
            ":- public left/1.\n" +
            ":- public right/1.\n" +
            "left(a). left(b). left(c).\n" +
            "right(1). right(2).\n" +
            "pair(X, Y) :- left(X), right(Y).\n");
        engine.Query("pair(a, 1).");
        Assert.True(engine.IlPromotion.IsPromoted(Fid("pair", 2)));
    }

    [Fact]
    public void MetaCp_NonLeafCallee_FullCrossProductUnderIl()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public pair/2.\n" +
            ":- public left/1.\n" +
            ":- public right/1.\n" +
            "left(a). left(b). left(c).\n" +
            "right(1). right(2).\n" +
            "pair(X, Y) :- left(X), right(Y).\n");
        engine.Query("pair(a, 1).");   // warm
        Assert.Equal(6, engine.QueryAll("pair(_, _).").Count());
    }

    [Fact]
    public void MetaCp_MatchesTier0ExactlyForChooseShape()
    {
        var src =
            ":- public choose/2.\n" +
            ":- public color/1.\n" +
            ":- public size/1.\n" +
            "color(red). color(green). color(blue).\n" +
            "size(small). size(large).\n" +
            "choose(X, Y) :- color(X), size(Y).\n";

        var tier0 = new PrologEngine();
        tier0.ConsultString(src);
        int sol0 = tier0.QueryAll("choose(_, _).").Count();

        var tier1 = new PrologEngine();
        tier1.IlPromotion.Threshold = 1;
        tier1.ConsultString(src);
        tier1.Query("choose(red, small).");   // warm
        int sol1 = tier1.QueryAll("choose(_, _).").Count();

        Assert.Equal(sol0, sol1);
        Assert.Equal(6, sol1);
    }

    [Fact]
    public void MetaCp_DeterministicCallee_NoSpuriousCps()
    {
        // When the non-leaf callee leaves no CPs on the engine stack
        // (engine.B doesn't grow across the call), the meta-CP push
        // is suppressed by the engine.B > preCallB guard. The
        // predicate runs end-to-end without any spurious choice
        // points.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public wrapped/1.\n" +
            ":- public inner/1.\n" +
            "inner(X) :- atom(X).\n" +
            "wrapped(X) :- inner(X), atom(X).\n");
        engine.Query("wrapped(foo).");
        Assert.True(engine.Query("wrapped(bar).").Success);
        Assert.False(engine.Query("wrapped(7).").Success);
    }

    [Fact]
    public void MetaCp_NestedNonLeafCalls_FullCrossProduct()
    {
        // Two non-tail Calls in the same body, each to a multi-clause
        // callee. The IL emits two meta-CPs (one per Call site) and
        // their resume paths chain together via the post-call cursors.
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public triple/3.\n" +
            ":- public a/1.\n" +
            ":- public b/1.\n" +
            ":- public c/1.\n" +
            "a(1). a(2).\n" +
            "b(p). b(q).\n" +
            "c(x). c(y).\n" +
            "triple(A, B, C) :- a(A), b(B), c(C).\n");
        engine.Query("triple(1, p, x).");   // warm
        // 2 * 2 * 2 = 8 cross-product solutions.
        Assert.Equal(8, engine.QueryAll("triple(_, _, _).").Count());
    }
}
