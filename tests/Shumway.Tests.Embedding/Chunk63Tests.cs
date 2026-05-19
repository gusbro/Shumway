using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 63: investigation report on lifting the IL Call's "leaf
/// callee only" restriction, plus a small dispatch-robustness fix
/// that came out of it.
///
/// <para>The investigation found a semantic gap: when a non-leaf
/// callee pushes a try_me_else CP inside <c>RunSubroutine</c>, the
/// CP captures the engine's current <c>Cp</c> — which is the
/// <c>SubroutineSentinelCp</c>. A later backtrack pops the CP, runs
/// the alternative clause, and the alternative's <c>proceed</c>
/// reads Cp = sentinel and halts, dropping out of the IL caller's
/// body without running the goals that followed the original Call.
/// Observable as a multi-clause callee producing fewer cross-product
/// solutions than Tier 0 — e.g. <c>choose(X) :- color(X), size(_)</c>
/// with three colors and two sizes yielding 4 instead of 6. The fix
/// wants the IL caller to push a meta-CP that re-runs the goals
/// after the Call on each callee alternative, which is a deeper IL
/// emission change deferred to Phase-2 work.</para>
///
/// <para>The dispatch loop did get a small robustness fix though:
/// <c>Pc &lt; 0</c> now halts cleanly instead of throwing, mirroring
/// what <c>proceed</c>'s <c>Cp &lt; 0</c> check already does. This
/// shows up in the IL Call helper's edge cases and is useful even
/// with the leaf restriction back in place.</para>
/// </summary>
public class Chunk63Tests
{
    [Fact]
    public void LeafOnly_Restriction_StillEnforced()
    {
        // Sanity check that the IL Call's leaf-callee restriction
        // is still active — a non-leaf callee keeps the parent on
        // Tier 0. (The same shape is tested at Chunk50Tests; we
        // re-pin it here so chunk 63's investigation context stays
        // legible.)
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(
            ":- public outer/0.\n" +
            ":- public inner/0.\n" +
            ":- public a/0.\n" +
            "a.\n" +
            "inner :- a, a.\n" +
            "outer :- inner, a.\n");
        engine.Query("outer.");
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern("outer", permanent: true).Id, 0);
        Assert.True(engine.IlPromotion.IsUnpromotable(fid));
    }

    [Fact]
    public void Tier0_NonLeafCallees_ProduceFullCrossProduct()
    {
        // The Tier-0 baseline yields the full cartesian product even
        // though Tier-1 doesn't handle this shape yet. Documents the
        // target behaviour for the Phase-2 IL lift.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public choose/1.\n" +
            ":- public color/1.\n" +
            ":- public size/1.\n" +
            "color(red). color(green). color(blue).\n" +
            "size(small). size(large).\n" +
            "choose(X) :- color(X), size(_).\n");
        Assert.Equal(6, engine.QueryAll("choose(_).").Count());
    }
}
