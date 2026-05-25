using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 12 chunk 159: Tier-1 IL promotion now explicitly excludes
/// dynamic predicates (those whose bytecode opens with
/// <c>enter_dynamic</c>) via the new
/// <see cref="IlPromotionStore.IsUnpromotable"/>-visible early
/// rejection. Static predicates stay eligible — the existing IL
/// paths (single-clause, indexed atom, try_me_else chain) are
/// unchanged.
///
/// <para>Rationale: a cached IL delegate doesn't observe a mid-
/// life <c>retract</c> patching a clause's died slot or an in-
/// place <c>assertz</c> appending a chain entry. Tier 0's dispatch
/// honours both via per-clause <c>check_visible</c> and the
/// chunk-155/156 chain-modification hooks; Tier 1 would silently
/// run a stale cached compilation.</para>
/// </summary>
public class Chunk159Tests
{
    private static AtomTerm Atom(string n) => new(n);
    private static int Fid(string n, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, arity);

    [Fact]
    public void DynamicPredicate_HotInvocation_NotIlPromoted()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;        // make it hot fast.
        e.IlPromotion.Threshold = 2;        // and try to IL-promote it.
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        // Several calls — enough to cross IL threshold if eligible.
        for (int i = 0; i < 10; i++) e.Query("d(a).");
        // Dynamic predicates are unpromotable post chunk 159.
        Assert.True(e.IlPromotion.IsUnpromotable(Fid("d", 1)));
        Assert.False(e.IlPromotion.IsPromoted(Fid("d", 1)));
    }

    [Fact]
    public void StaticPredicate_HotInvocation_StillIlPromoted()
    {
        // Static predicates have no enter_dynamic prefix and stay
        // IL-eligible (chunk 159 only excludes dynamics).
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString("p(a). p(b). p(c).");
        // First call promotes.
        e.Query("p(a).");
        e.Query("p(b).");
        // After the first call crosses threshold, the second
        // observes the promoted delegate. Either way, static
        // predicate is NOT marked unpromotable.
        Assert.False(e.IlPromotion.IsUnpromotable(Fid("p", 1)));
    }

    [Fact]
    public void DynamicMutation_AfterExclusion_StillCorrect()
    {
        // The point of excluding dynamic predicates from IL: even
        // when the IL threshold is crossed by many calls, the live
        // dispatch stays on Tier 0 — so subsequent retract /
        // assertz mutate dispatch through the chunk-155/156 hooks
        // and queries see the right answers.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.IlPromotion.Threshold = 2;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        e.Query("assertz(d(2)).");
        for (int i = 0; i < 10; i++) e.Query("d(1).");
        // Now mutate.
        e.Query("retract(d(1)).");
        Assert.False(e.Query("d(1).").Success);
        Assert.True(e.Query("d(2).").Success);
        e.Query("assertz(d(3)).");
        Assert.True(e.Query("d(3).").Success);
    }

    [Fact]
    public void QueryPredicate_AlwaysExcluded()
    {
        // Existing behaviour: __query__/N is always excluded
        // (chunk-159 doesn't regress this — pre-existing test).
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        for (int i = 0; i < 5; i++) e.Query("true.");
        // The synthetic __query__/0 the engine wraps queries in.
        Assert.True(e.IlPromotion.IsUnpromotable(Fid("__query__", 0)));
    }
}
