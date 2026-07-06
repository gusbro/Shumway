using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-027 — second-level (sub-argument) indexing, end-to-end. Verifies the
/// runtime <c>switch_on_atom_sub</c> / <c>switch_on_integer_sub</c> dispatch is
/// sound across the list-head, token-stream and struct-sub-arg shapes in BOTH
/// tiers, and that a distinct-key call is deterministic. Tier-1 is exercised
/// deterministically through an IL bundle (baked delegates registered at load) —
/// both with the WAM present (the bytecode-walking resolver) and stripped (the
/// WAM-independent <c>IlIndexGraph</c> persisted via <c>IndexGraphCodec</c>).
/// </summary>
public class SubArgIndexingTests
{
    private const string Program =
        ":- public tok/2, pc/2, eo/2, w/2.\n"
        // depth-1 list-head atoms
        + "tok([a|T],T).\ntok([b|T],T).\ntok([c|T],T).\ntok([d|T],T).\n"
        // depth-2 token stream: list head t/4, integer code at sub-arg 1
        + "pc([t(_,104,_,_)|R], one).\npc([t(_,105,_,_)|R], two).\npc([t(_,106,_,_)|R], three).\n"
        // struct sub-arg: e/2, integer OpCode at sub-arg 0
        + "eo(e(1,_), wa).\neo(e(29,_), wb).\neo(e(31,_), wc).\neo(e(49,_), wd).\n"
        // wildcard fallback: a whole-var-arg clause must merge into every bucket
        + "w([a|T], T).\nw([b|T], T).\nw(X, wild) :- X = [_|_].\n";

    public enum Mode { Tier0, Tier1Wam, Tier1StripWam }

    private static int Fid(string n, int a) =>
        FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    private static PrologEngine Engine(Mode mode)
    {
        if (mode == Mode.Tier0)
        {
            var e0 = new PrologEngine();
            e0.ConsultString(Program);
            return e0;
        }

        // Tier-1: build an IL bundle (baked delegates) and load it. Loading
        // registers the baked IL, so calls dispatch through Tier-1 immediately.
        var bundle = new Bundle(new[] { new BundleEntry("subidx", Program) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: mode == Mode.Tier1Wam,
            includeCompiledIl: true,
            stripWam: mode == Mode.Tier1StripWam);
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(bytes));
        // Guarantee the IL path is actually what runs (otherwise a silent Tier-0
        // fallback would mask a Tier-1 bug).
        Assert.True(e.IlPromotion.IsPromoted(Fid("w", 2)), "w/2 must be Tier-1 IL");
        Assert.True(e.IlPromotion.IsPromoted(Fid("pc", 2)), "pc/2 must be Tier-1 IL");
        Assert.True(e.IlPromotion.IsPromoted(Fid("eo", 2)), "eo/2 must be Tier-1 IL");
        return e;
    }

    public static TheoryData<Mode> Modes => new() { Mode.Tier0, Mode.Tier1Wam, Mode.Tier1StripWam };

    [Theory]
    [MemberData(nameof(Modes))]
    public void ListHeadAtom_DistinctKey_IsCorrect(Mode mode)
    {
        var e = Engine(mode);
        Assert.Single(e.QueryAll("tok([c,z], R)."));
        Assert.True(e.Query("tok([c,z], R), R == [z].").Success);
        Assert.Equal(4, e.QueryAll("tok(X, Y).").Count());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void TokenStream_DepthTwo_IntegerCode(Mode mode)
    {
        var e = Engine(mode);
        Assert.True(e.Query("pc([t(sym,105,x,y), rest], W), W == two.").Success);
        Assert.Single(e.QueryAll("pc([t(sym,106,_,_)], _)."));
        Assert.False(e.Query("pc([t(sym,999,_,_)], _).").Success);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void StructSubArg_OpCode(Mode mode)
    {
        var e = Engine(mode);
        Assert.True(e.Query("eo(e(31,foo), S), S == wc.").Success);
        Assert.False(e.Query("eo(e(99,_), _).").Success);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Wildcard_ClauseMergesIntoEveryBucketAndDefault(Mode mode)
    {
        var e = Engine(mode);
        // w([a,z], V): clause 1 (V=[z]) then the var-arg wildcard (V=wild).
        Assert.Equal(2, e.QueryAll("w([a,z], V).").Count());
        // A key with no specific clause still matches the wildcard alone.
        Assert.Single(e.QueryAll("w([z], V)."));
        Assert.True(e.Query("w([z], V), V == wild.").Success);
    }

    [Fact]
    public void DistinctKeyHit_AllocatesFewerCellsThanFullScan()
    {
        // Determinism proxy: a distinct-key lookup jumps straight to the one
        // clause body, so it allocates strictly fewer cells than enumerating
        // every clause of the same predicate via a var-arg traversal.
        var e = Engine(Mode.Tier0);
        e.QueryAll("tok([c,z], R).").ToList();
        long hit = e.LastQueryCellsAllocated;
        e.QueryAll("tok(X, Y).").ToList();
        long scan = e.LastQueryCellsAllocated;
        Assert.True(hit < scan, $"distinct-key hit ({hit}) should cost fewer cells than full scan ({scan})");
    }
}
