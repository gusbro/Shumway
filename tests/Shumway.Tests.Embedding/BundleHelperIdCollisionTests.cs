using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A bundle's MetaTransform helpers (<c>$disj_N</c> / <c>$neg_N</c> / …) are
/// numbered by ShmoCompiler's per-module 0-based counter. The engine's runtime
/// <c>NextMetaHelperId</c> — which re-numbers a bundled module's DYNAMIC clause
/// helpers at query setup — starts low too, so without intervention a dynamic
/// clause's helper can mint the SAME mangled functor id as a compiled static
/// helper (e.g. <c>clpz$$disj_253</c>) and shadow it with the wrong body. The
/// symptom in the field: clpz constraint narrowing (label/1, queens, every
/// non-singleton domain update) silently failed or gave a wrong answer when
/// clpz was compiled to a bundle, while running correctly live.
///
/// <para><see cref="PrologEngine.ObserveBundleHelperId"/> keeps the runtime
/// counter above every bundled helper id so the two number ranges stay
/// disjoint — the guarantee the live/JIT path gets for free by numbering every
/// helper (static and dynamic) from one counter.</para>
/// </summary>
public class BundleHelperIdCollisionTests
{
    private static int HelperFid(string mangledName, int arity)
        => FunctorTable.Intern(AtomTable.Intern(mangledName, permanent: true).Id, arity);

    [Fact]
    public void ObserveBundleHelperId_AdvancesCounterPastHelperNumber()
    {
        var e = new PrologEngine();
        e.ObserveBundleHelperId(HelperFid("clpz$$disj_253", 3));
        // The next runtime helper id must land ABOVE the bundled one, so a
        // query-setup re-transform can never reproduce clpz$$disj_253.
        Assert.True(e.NextMetaHelperId() > 253);
    }

    [Fact]
    public void ObserveBundleHelperId_TakesMaximumAcrossManyHelpers()
    {
        var e = new PrologEngine();
        e.ObserveBundleHelperId(HelperFid("a$$disj_10", 2));
        e.ObserveBundleHelperId(HelperFid("b$$neg_500", 1));   // highest
        e.ObserveBundleHelperId(HelperFid("c$$once_7", 1));
        Assert.True(e.NextMetaHelperId() > 500);
    }

    [Theory]
    [InlineData("$disj_42")]        // bare (unmangled) helper
    [InlineData("mod$$neg_42")]     // mangled negation helper
    [InlineData("mod$$catchgoal_42")]
    public void ObserveBundleHelperId_RecognisesHelperShapes(string name)
    {
        var e = new PrologEngine();
        e.ObserveBundleHelperId(HelperFid(name, 1));
        Assert.True(e.NextMetaHelperId() > 42);
    }

    [Fact]
    public void ObserveBundleHelperId_IgnoresNonHelperNames()
    {
        var e = new PrologEngine();
        // A plain predicate name with no `$<kind>_` marker must NOT be parsed
        // as a helper: no `$` before the trailing digits.
        int before = e.NextMetaHelperId();      // consumes one id
        e.ObserveBundleHelperId(HelperFid("append", 3));     // no underscore
        e.ObserveBundleHelperId(HelperFid("list_42", 2));    // digits, but no `$` marker
        // The counter only moved by the natural NextMetaHelperId increments,
        // not by a bogus parse of "append"/"some_pred".
        Assert.Equal(before + 1, e.NextMetaHelperId());
    }
}
