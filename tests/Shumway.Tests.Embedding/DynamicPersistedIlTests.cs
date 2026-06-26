using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-023 build-time persist — a `:- dynamic` / `:- visible` predicate that ships
/// WITH clauses in a `--with-compiled-il` / `--exe` bundle gets its static-style
/// SNAPSHOT baked into the persisted IL. At load the snapshot delegate is registered
/// into IlPromotion._delegates[fid], so the predicate runs as IL from the FIRST call
/// with NO runtime promotion (Threshold stays 0 — the AOT / --exe win), and the first
/// assert/retract evicts it (back to the live dynamic chain). A snapshot that would
/// reference a string/float/bigint literal not in the bundle's pools is NOT baked
/// (those are index-addressed) — it stays Tier-0 but still runs correctly.
/// </summary>
public class DynamicPersistedIlTests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static PrologEngine LoadWithPersistedIl(string src)
    {
        var bundle = new Bundle(new[] { new BundleEntry("dyn", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var rt = BundleReader.FromBytes(bytes);
        var e = new PrologEngine();   // Threshold 0 — NO runtime promotion
        e.LoadBundle(rt);
        return e;
    }

    [Fact]
    public void DynamicWithClauses_BakedToIl_RunsWithoutPromotion_AndEvicts()
    {
        var e = LoadWithPersistedIl(":- dynamic d/1.\nd(1).\nd(2).\nd(3).\n");
        int fid = Fid("d", 1);

        // Installed from the BUNDLE — promoted at load, no warm-up, Threshold 0.
        Assert.True(e.IlPromotion.IsPromoted(fid));
        Assert.True(e.Query("d(2).").Success);
        Assert.True(e.Query("findall(X, d(X), L), L == [1, 2, 3].").Success);

        // Still ISO-mutable: the first mutation evicts the baked delegate and the
        // live dynamic chain reflects the new state.
        Assert.True(e.Query("assertz(d(4)).").Success);
        Assert.False(e.IlPromotion.IsPromoted(fid));               // evicted
        Assert.True(e.Query("d(4).").Success);
        Assert.True(e.Query("findall(X, d(X), L), L == [1, 2, 3, 4].").Success);
    }

    [Fact]
    public void VisibleWithClauses_BakedToIl_RunsWithoutPromotion()
    {
        // `:- visible` is dynamic; with clauses it bakes the same way.
        var e = LoadWithPersistedIl(":- visible v/2.\nv(a, 1).\nv(b, 2).\n");
        Assert.True(e.IlPromotion.IsPromoted(Fid("v", 2)));
        Assert.True(e.Query("v(b, X), X == 2.").Success);
    }

    [Fact]
    public void FloatLiteralDynamic_BakedToPersistedIl()
    {
        // Float literals are now value-baked into the IL (ldc.r8), so a float-bearing
        // dynamic snapshot bakes too (the old index-addressed limitation is gone).
        var e = LoadWithPersistedIl(":- dynamic f/1.\nf(1.5).\n");
        int fid = Fid("f", 1);
        Assert.True(e.IlPromotion.IsPromoted(fid));
        Assert.True(e.Query("f(1.5).").Success);
        Assert.True(e.Query("f(X), X =:= 1.5.").Success);
    }

    [Fact]
    public void RuntimeOnlyDynamic_NotBaked()
    {
        // A `:- dynamic` predicate with no source clauses has no snapshot to bake.
        var e = LoadWithPersistedIl(":- dynamic t/1.\n");
        Assert.False(e.IlPromotion.IsPromoted(Fid("t", 1)));
        Assert.True(e.Query("assertz(t(9)), t(9).").Success);
    }
}
