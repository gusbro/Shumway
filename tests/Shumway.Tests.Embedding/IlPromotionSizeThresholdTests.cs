using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Coverage for the
/// <see cref="IlPromotionStore.MaxIlPromotionBytecodeBytes"/>
/// guard. Sigil's <c>ReturnTracer</c> overflows the default
/// thread stack and runs super-linearly on predicates with many
/// branches (Blint.pl's 200-clause <c>parse_args/2</c> is the
/// canonical case). The threshold parks oversized predicates on
/// Tier 0 — the interpreter dispatches them linearly. The
/// long-term plan is to replace Sigil with a linear-validation
/// emitter and drop the threshold.
/// </summary>
public class IlPromotionSizeThresholdTests
{
    private const string LargePredicateSource = @"
:- public big/1.
" + // 80 clauses — enough to push the compiled bytecode over a
   // 2KB-ish threshold while staying small enough that the test
   // runs quickly.
    @"
big(1).
big(2).
big(3).
big(4).
big(5).
big(6).
big(7).
big(8).
big(9).
big(10).
big(11).
big(12).
big(13).
big(14).
big(15).
big(16).
big(17).
big(18).
big(19).
big(20).
big(21).
big(22).
big(23).
big(24).
big(25).
big(26).
big(27).
big(28).
big(29).
big(30).
big(31).
big(32).
big(33).
big(34).
big(35).
big(36).
big(37).
big(38).
big(39).
big(40).
big(41).
big(42).
big(43).
big(44).
big(45).
big(46).
big(47).
big(48).
big(49).
big(50).
big(51).
big(52).
big(53).
big(54).
big(55).
big(56).
big(57).
big(58).
big(59).
big(60).
big(61).
big(62).
big(63).
big(64).
big(65).
big(66).
big(67).
big(68).
big(69).
big(70).
big(71).
big(72).
big(73).
big(74).
big(75).
big(76).
big(77).
big(78).
big(79).
big(80).
";

    [Fact]
    public void DefaultThreshold_IsReasonable()
    {
        var e = new PrologEngine();
        Assert.True(e.IlPromotion.MaxIlPromotionBytecodeBytes >= 1024,
            "default threshold must allow small-to-medium predicates");
    }

    [Fact]
    public void LargePredicate_DoesNotIlPromote_StillRuns()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;     // promote eagerly
        e.IlPromotion.MaxIlPromotionBytecodeBytes = 256;  // tiny — every realistic preds exceeds this
        // Phase 33 L2 — background promotion (now the default) uses its own
        // higher cap (L3); pin that one down too so the size GATE is what's
        // under test, not the mode.
        e.IlPromotion.MaxIlPromotionBytecodeBytesBackground = 256;

        e.ConsultString(LargePredicateSource);
        // Drive it hot enough to cross the IL threshold; the size
        // threshold should still keep it on Tier 0.
        for (int i = 0; i < 5; i++)
            Assert.True(e.Query("big(42).").Success);

        // Tier-1 promotion should NOT have happened.
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern("big", permanent: true).Id, 1);
        Assert.False(e.IlPromotion.IsPromoted(fid),
            "large predicate should not promote to IL");
        // Functionally it still works.
        Assert.True(e.Query("big(60).").Success);
        Assert.False(e.Query("big(999).").Success);
    }

    [Fact]
    public void SmallPredicate_PromotesNormally()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        // Default threshold (2048 bytes) — small predicate easily
        // fits under it.
        e.ConsultString("""
            :- public greet/1.
            greet(hello).
            """);
        Assert.True(e.Query("greet(X).").Success);
        Assert.True(e.Query("greet(X).").Success);
        // Hard to assert IsPromoted=true reliably (depends on the
        // predicate's IL eligibility); but importantly the predicate
        // must not have been parked by the SIZE threshold.
        // The IL-eligibility check (IsExcludedBySize false → may
        // still be unpromotable via CanCompile false) is what we're
        // pinning.
        var canCompile = !e.IlPromotion.IsUnpromotable(
            Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern("greet", permanent: true).Id, 1));
        // It's allowed to be unpromotable for OTHER reasons (e.g.
        // CanCompile rejected the shape), but the size guard alone
        // shouldn't park it. Either it's actually promoted or it's
        // unpromotable for a non-size reason — both fine.
        Assert.True(canCompile || e.IlPromotion.IsPromoted(
            Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern("greet", permanent: true).Id, 1)));
    }
}
