using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-028 — sibling-argument + structure-keyed indexing inside a value bucket,
/// end-to-end in all three tiers. Covers: the atom sibling nested in a ground
/// arg0 bucket; the structure-keyed sub on a list head; and — critically — the
/// ADR-027 soundness fix, where an UNBOUND discriminator must still enumerate the
/// whole bucket (a var-headed clause present) rather than only the wildcards.
/// </summary>
public class BucketIndexingTests
{
    private const string Program =
        ":- public p/2, h/3, rr/2.\n"
        // ADR-027/028 soundness: struct sub-arg (e/... vs a var-headed clause).
        // p(f(Y), R) with Y unbound must yield f(a)->1, f(b)->2 AND the var clause.
        + "p(f(a),1).\np(f(b),2).\np(X,3).\n"
        // Atom sibling inside the ground arg0='a' bucket (3 clauses, arg1 x/y/z).
        + "h(a,x,1).\nh(a,y,2).\nh(a,z,3).\nh(b,w,9).\n"
        // Structure-keyed sub on the list head functor + a catch-all clause.
        + "rr([parse(_)|_], parsed).\nrr([amp(_)|_], amped).\n"
        + "rr([lit(_)|_], litted).\nrr([_|_], other).\n";

    public enum Mode { Tier0, Tier1Wam, Tier1StripWam }

    private static int Fid(string n, int a) =>
        FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    private static PrologEngine Activation(Mode mode)
    {
        if (mode == Mode.Tier0)
        {
            var e0 = new PrologEngine();
            e0.ConsultString(Program);
            return e0;
        }
        var bundle = new Bundle(new[] { new BundleEntry("bucketidx", Program) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: mode == Mode.Tier1Wam,
            includeCompiledIl: true,
            stripWam: mode == Mode.Tier1StripWam);
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.True(e.IlPromotion.IsPromoted(Fid("p", 2)), "p/2 must be Tier-1 IL");
        Assert.True(e.IlPromotion.IsPromoted(Fid("h", 3)), "h/3 must be Tier-1 IL");
        Assert.True(e.IlPromotion.IsPromoted(Fid("rr", 2)), "rr/2 must be Tier-1 IL");
        return e;
    }

    public static TheoryData<Mode> Modes => new() { Mode.Tier0, Mode.Tier1Wam, Mode.Tier1StripWam };

    [Theory]
    [MemberData(nameof(Modes))]
    public void UnboundSubArg_EnumeratesWholeBucket_SoundnessFix(Mode mode)
    {
        // The regression: p(f(Y),R) once returned only R=3 (the sub-switch default
        // routed the unbound discriminator to the wildcards, dropping f(a)/f(b)).
        var e = Activation(mode);
        Assert.Equal(3, e.QueryAll("p(f(Y), R).").Count());
        Assert.True(e.Query("p(f(a), R), R == 1.").Success);
        Assert.True(e.Query("p(f(b), R), R == 2.").Success);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AtomSibling_BoundKey_IsCorrect(Mode mode)
    {
        var e = Activation(mode);
        Assert.True(e.Query("h(a, y, V), V == 2.").Success);   // nested arg1 index picks (a,y)
        Assert.Single(e.QueryAll("h(a, y, V)."));               // deterministic
        Assert.Equal(4, e.QueryAll("h(A, B, C).").Count());     // full enumeration intact
        // Unbound sibling still enumerates the whole 'a' bucket.
        Assert.Equal(3, e.QueryAll("h(a, B, C).").Count());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void StructureKeyedSub_RoutesByHeadFunctor(Mode mode)
    {
        var e = Activation(mode);
        // Each functor routes to its clause plus the catch-all.
        Assert.True(e.Query("rr([amp(9)], R), R == amped.").Success);
        Assert.True(e.Query("rr([lit(8)], R), R == litted.").Success);
        Assert.True(e.Query("rr([parse(1)], R), R == parsed.").Success);
        // An unknown functor matches only the catch-all.
        Assert.Single(e.QueryAll("rr([zzz(0)], R)."));
        Assert.True(e.Query("rr([zzz(0)], R), R == other.").Success);
    }

    [Fact]
    public void AtomSiblingHit_AllocatesNoMoreThanFullScan()
    {
        var e = Activation(Mode.Tier0);
        e.QueryAll("h(a, y, V).").ToList();
        long hit = e.LastQueryCellsAllocated;
        e.QueryAll("h(A, B, C).").ToList();
        long scan = e.LastQueryCellsAllocated;
        Assert.True(hit <= scan, $"keyed hit ({hit}) should not cost more than full scan ({scan})");
    }
}
