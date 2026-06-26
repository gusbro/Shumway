using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Float literals (get_float / put_float) in Tier-1 IL. The value is resolved
/// from the predicate's float pool at emit time and baked as an ldc.r8 constant —
/// process-independent, so it works for runtime promotion, the dump, AND persisted
/// (--with-compiled-il / --exe) bundles with no patch. Covers static + dynamic
/// (snapshot) predicates, in head matching (get_float) and body build (put_float).
/// </summary>
public class FloatLiteralIlTests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static PrologEngine LoadWithPersistedIl(string src)
    {
        var bundle = new Bundle(new[] { new BundleEntry("flt", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var rt = BundleReader.FromBytes(bytes);
        var e = new PrologEngine();   // Threshold 0 — runs the persisted IL, no promotion
        e.LoadBundle(rt);
        return e;
    }

    [Fact]
    public void StaticFloatFacts_PromoteToIl_AndUnify()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;   // promote on first call
        e.ConsultString(":- public temp/2.\ntemp(mon, 1.5).\ntemp(tue, 2.5).\ntemp(wed, 3.5).\n");
        int fid = Fid("temp", 2);
        for (int i = 0; i < 3; i++) Assert.True(e.Query("temp(tue, 2.5).").Success);
        Assert.True(e.IlPromotion.IsPromoted(fid));                 // floats no longer block IL
        Assert.True(e.Query("temp(wed, X), X =:= 3.5.").Success);   // get_float into an unbound
        Assert.True(e.Query("temp(D, 1.5), D == mon.").Success);    // get_float match
        Assert.False(e.Query("temp(mon, 9.9).").Success);           // get_float mismatch
    }

    [Fact]
    public void PutFloat_BuildsFloatArg_InIl()
    {
        // make/1 builds a compound carrying a float literal (put_float in the body).
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(":- public make/1.\nmake(p(1.25)).\n");
        for (int i = 0; i < 3; i++) Assert.True(e.Query("make(_).").Success);
        Assert.True(e.IlPromotion.IsPromoted(Fid("make", 1)));
        Assert.True(e.Query("make(p(X)), X =:= 1.25.").Success);
    }

    [Fact]
    public void StaticFloatPredicate_BakedToPersistedIl()
    {
        var e = LoadWithPersistedIl(":- public k/2.\nk(a, 1.5).\nk(b, 2.5).\n");
        Assert.True(e.IlPromotion.IsPromoted(Fid("k", 2)));   // baked from the bundle, no warm-up
        Assert.True(e.Query("k(b, X), X =:= 2.5.").Success);
        Assert.True(e.Query("k(a, 1.5).").Success);
    }

    [Fact]
    public void DynamicFloatSnapshot_BakedToPersistedIl_AndEvicts()
    {
        // The guard that used to skip float-bearing snapshots is relaxed (floats are
        // value-baked), so a dynamic predicate with floats also bakes.
        var e = LoadWithPersistedIl(":- dynamic measure/1.\nmeasure(1.5).\nmeasure(2.5).\n");
        int fid = Fid("measure", 1);
        Assert.True(e.IlPromotion.IsPromoted(fid));
        Assert.True(e.Query("measure(X), X =:= 1.5.").Success);
        Assert.True(e.Query("findall(X, measure(X), L), length(L, N), N == 2.").Success);
        // still mutable — eviction on assert.
        Assert.True(e.Query("assertz(measure(9.5)).").Success);
        Assert.False(e.IlPromotion.IsPromoted(fid));
        Assert.True(e.Query("measure(9.5).").Success);
    }
}
