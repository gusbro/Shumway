using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-054: a dynamic predicate with source clauses runs in the
/// wasm tier as a snapshot of its clauses, linked into the code space as a
/// shadow region so a deopt has bytecode to continue in. The first mutation
/// retires it: new calls reach the predicate as it is then, and a call
/// already running finishes on the clauses it began with (the logical
/// update view). Every answer here is checked against Tier-0.</summary>
public sealed class DynamicShadowTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(lists)).
        :- use_module(library(coroutining)).
        :- dynamic(d/1).
        d(1).
        d(2).
        d(3).
        run(0) :- !.
        run(N) :- d(2), N1 is N - 1, run(N1).
        all(L) :- findall(X, d(X), L).
        iter(L) :- findall(X, (d(X), (X =:= 2 -> assertz(d(99)) ; true)), L).
        :- dynamic(c/1).
        c(1).
        c(2) :- assertz(c(3)), c(3).
        :- dynamic(eqp/2).
        eqp(X, X).
        :- dynamic(w/1).
        w(0).
        churn(0) :- !.
        churn(N) :- w(_), assertz(w(N)), retract(w(N)), N1 is N - 1, churn(N1).
        :- dynamic(rd/3).
        rd(0, _, _) :- !, rd(extra, _, _).
        rd(N, X, X) :- integer(N), N > 0, assertz(rd(extra, _, _)), N1 is N - 1,
            freeze(B, true), rd(N1, B, 7).
        """;

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static (PrologEngine Plain, PrologEngine Tiered) Both()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _) = TieredEngine.Build(Corpus);
        return (plain, tiered);
    }

    private static void Agree(PrologEngine plain, PrologEngine tiered, string goal)
    {
        Assert.True(plain.Query(goal).Success, goal + " on Tier-0");
        Assert.True(tiered.Query(goal).Success, goal + " on the tier");
    }

    [Fact]
    public void ADynamicPredicateRunsAsASnapshot()
    {
        var (plain, tiered) = Both();
        Agree(plain, tiered, "run(50).");
        Agree(plain, tiered, "all(L), L == [1, 2, 3].");
        int fid = Fid("d", 1);
        Assert.True(tiered.IlPromotion.Wasm!.HasShadow(fid), "d/1 was not promoted as a snapshot");
        Assert.True(tiered.IlPromotion.IsPromoted(fid));
    }

    [Fact]
    public void AMutationReachesTheNextCall()
    {
        var (plain, tiered) = Both();
        Agree(plain, tiered, "run(5).");
        Assert.True(tiered.IlPromotion.Wasm!.HasShadow(Fid("d", 1)));
        Agree(plain, tiered, "assertz(d(4)).");
        Agree(plain, tiered, "all(L), L == [1, 2, 3, 4].");
        Agree(plain, tiered, "retract(d(1)).");
        Agree(plain, tiered, "all(L), L == [2, 3, 4].");
        Agree(plain, tiered, "d(4), \\+ d(1).");
        Assert.True(tiered.IlPromotion.Wasm!.ShadowRetirements >= 2,
            $"{tiered.IlPromotion.Wasm.ShadowRetirements} retirements: the mutations never met a snapshot");
    }

    [Fact]
    public void TheCallInFlightFinishesOnTheClausesItBeganWith()
    {
        // d/1 iterates with a choice point inside its snapshot; the assert in
        // the middle retires the snapshot, and the iteration still sees only
        // the clauses that were there when it began.
        var (plain, tiered) = Both();
        Agree(plain, tiered, "run(5).");
        Assert.True(tiered.IlPromotion.Wasm!.HasShadow(Fid("d", 1)));
        Agree(plain, tiered, "iter(L), L == [1, 2, 3].");
        Agree(plain, tiered, "d(99).");
    }

    [Fact]
    public void ACallThatStartsAfterTheMutationSeesIt()
    {
        // c(2) asserts c(3) and then calls c(3): a new call, which must find
        // the new clause, from inside the snapshot's own recursion.
        var (plain, tiered) = Both();
        Agree(plain, tiered, "c(1).");
        Agree(plain, tiered, "c(1).");
        Assert.True(tiered.IlPromotion.Wasm!.HasShadow(Fid("c", 1)));
        Agree(plain, tiered, "c(2).");
    }

    [Fact]
    public void ADeoptInsideTheSnapshotContinuesInItsBytecode()
    {
        // An attributed variable against a value in a head unification is
        // the host's: the instruction goes back to the interpreter, which
        // runs the rest from the shadow region.
        var (plain, tiered) = Both();
        Agree(plain, tiered, "eqp(a, a).");
        Agree(plain, tiered, "eqp(b, b).");
        Assert.True(tiered.IlPromotion.Wasm!.HasShadow(Fid("eqp", 2)));
        Agree(plain, tiered, "freeze(A, B = woken), eqp(A, 5), A == 5, B == woken.");
        Agree(plain, tiered, "freeze(A, true), \\+ eqp(A, f(A)) ; true.");
    }

    [Fact]
    public void AfterADeoptARecursiveCallReachesThePredicateAsItIsNow()
    {
        // The head's attributed variable against a value sends rd/3 back to
        // the interpreter, which runs on in the shadow region: it asserts a
        // clause, and the recursive call after that must find it. The shadow
        // calls the predicate's live chain, never itself.
        var (plain, tiered) = Both();
        Agree(plain, tiered, "(rd(0, a, a) ; true).");
        Assert.True(tiered.IlPromotion.Wasm!.HasShadow(Fid("rd", 3)));
        Agree(plain, tiered, "freeze(A, true), rd(1, A, 5).");
    }

    // A dynamic predicate declared in a MODULE keeps its source clauses in
    // the module, not in the dynamic store: clp(Z)'s clpz_neq/2 is one.
    private const string ModuleCorpus = """
        :- module(mdyn, [md/1, mrun/1, mall/1]).
        :- dynamic(md/1).
        md(1).
        md(2).
        md(3).
        mrun(0) :- !.
        mrun(N) :- md(2), N1 is N - 1, mrun(N1).
        mall(L) :- findall(X, md(X), L).
        """;

    [Fact]
    public void AModuleDeclaredDynamicRunsAsASnapshotAndSeesItsMutations()
    {
        var plain = new PrologEngine();
        plain.ConsultString(ModuleCorpus);
        var (tiered, _) = TieredEngine.Build(ModuleCorpus);
        Agree(plain, tiered, "mrun(20).");
        Assert.True(tiered.IlPromotion.Wasm!.HasShadow(Fid("md", 1)),
            "a module's dynamic predicate was not promoted as a snapshot");
        Agree(plain, tiered, "mall(L), L == [1, 2, 3].");
        // A source clause retracted, and a call in the SAME query after it:
        // no snapshot may be taken from sources that predate the retract.
        Agree(plain, tiered, "retract(md(1)), mrun(3), mall(L), L == [2, 3].");
        Agree(plain, tiered, "mall(L), L == [2, 3].");
        Agree(plain, tiered, "assertz(md(4)), mall(L), L == [2, 3, 4].");
        Agree(plain, tiered, "mrun(5), mall(L), L == [2, 3, 4].");
        Assert.True(tiered.IlPromotion.Wasm!.HasShadow(Fid("md", 1)), "no re-promotion after the mutations");
    }

    [Fact]
    public void AMutationHeavyPredicateIsPinned()
    {
        var (plain, tiered) = Both();
        Agree(plain, tiered, "churn(200).");
        Agree(plain, tiered, "findall(X, w(X), L), L == [0].");
        var wasm = tiered.IlPromotion.Wasm!;
        o.WriteLine($"promotions={wasm.ShadowPromotions} retirements={wasm.ShadowRetirements}");
        Assert.True(wasm.ShadowPromotions <= tiered.IlPromotion.EvictionChurnLimit + 1,
            $"{wasm.ShadowPromotions} promotions in 200 mutating rounds: the churn pin did not hold");
        Assert.True(wasm.ShadowPromotions >= 1, "w/1 never promoted: the corpus stopped exercising it");
    }

    [Fact]
    public void ARebuiltCodeSpaceEvictsTheSnapshot()
    {
        var (plain, tiered) = Both();
        Agree(plain, tiered, "run(5).");
        int fid = Fid("d", 1);
        var wasm = tiered.IlPromotion.Wasm!;
        Assert.True(wasm.HasShadow(fid));
        int promoted = wasm.ShadowPromotions;
        plain.ConsultString("other(1).");
        tiered.ConsultString("other(1).");
        Agree(plain, tiered, "other(1).");
        Assert.False(wasm.HasShadow(fid), "the snapshot outlived the code space it was linked into");
        Agree(plain, tiered, "run(5), all(L), L == [1, 2, 3].");
        Assert.True(wasm.HasShadow(fid));
        Assert.True(wasm.ShadowPromotions > promoted);
    }

    private static long ForeignExitsTo(int fid)
    {
        foreach (var (f, hits) in WasmTierDelegate.ForeignRanking())
            if (f == fid) return hits;
        return 0;
    }

    /// <summary>The counters: a promoted caller reaches the snapshot inside
    /// wasm, with no foreign exit; the same corpus on a host that cannot
    /// retire a snapshot leaves on every call (the anti-vacuity).</summary>
    [DiagFact]
    public void TheCallerReachesTheSnapshotInsideWasm()
    {
        int fid = Fid("d", 1);
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("run(3).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query("run(300).").Success);
        o.WriteLine($"with snapshots: foreign exits to d/1 = {ForeignExitsTo(fid)}");
        Assert.Equal(0, ForeignExitsTo(fid));

        var (bare, _) = TieredEngine.Build(Corpus);
        bare.IlPromotion.Wasm!.ShadowRetired = null;
        Assert.True(bare.Query("run(3).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(bare.Query("run(300).").Success);
        o.WriteLine($"without: foreign exits to d/1 = {ForeignExitsTo(fid)}");
        Assert.True(ForeignExitsTo(fid) >= 300,
            $"{ForeignExitsTo(fid)} foreign exits without snapshots: the corpus stopped reaching d/1 from the tier");
    }
}
