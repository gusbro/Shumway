using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-016 end-to-end: a <c>garbage_collect</c> goal placed mid-query must
/// preserve every live structure (relocating it) so the goals after it see
/// correct data. A root-handling or relocation bug would surface as a wrong
/// answer or a failure here.
/// </summary>
public class GarbageCollectBuiltinTests
{
    [Fact]
    public void Gc_PreservesListUsedAfter()
    {
        var e = new PrologEngine();
        var sol = e.Query("numlist(1, 1000, L), garbage_collect, sum_list(L, S).");
        Assert.True(sol.Success);
        Assert.Equal("500500", sol["S"]!.ToString());
    }

    [Fact]
    public void Gc_PreservesNestedTerm()
    {
        var e = new PrologEngine();
        // Build a nested ground term, GC, then re-unify with the exact same
        // term: succeeds iff the structure survived the collection intact.
        var sol = e.Query(
            "T = foo(bar(1, 2), baz([a, b, c])), garbage_collect, "
            + "T == foo(bar(1, 2), baz([a, b, c])).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Gc_PreservesBoundVariableChain()
    {
        var e = new PrologEngine();
        // A=B=C=hello before GC; all must still be hello after.
        var sol = e.Query("A = B, B = C, C = hello, garbage_collect, A == hello, B == hello.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Gc_AcrossChoicePoint_KeepsBacktrackingCorrect()
    {
        var e = new PrologEngine();
        // member leaves a choice point; GC runs while it is open. All
        // solutions must still enumerate correctly.
        var sols = e.QueryAll("member(X, [a, b, c]), garbage_collect.").ToList();
        Assert.Equal(3, sols.Count);
        Assert.Equal("a", sols[0]["X"]!.ToString());
        Assert.Equal("b", sols[1]["X"]!.ToString());
        Assert.Equal("c", sols[2]["X"]!.ToString());
    }

    [Fact]
    public void Gc_ReclaimsGarbage_RepeatedRuns()
    {
        // Build and discard large lists in a failure-driven loop with a GC
        // each round; without reclamation the heap would balloon, with it
        // the run completes and the count is right.
        var e = new PrologEngine();
        var sol = e.Query(
            "( between(1, 200, _), numlist(1, 500, L), last(L, _), "
            + "garbage_collect, fail ; true ).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Gc_PreservesFloatsAndBigInts()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "X is 1.5 + 2.5, Y is 2 ^ 100, garbage_collect, "
            + "X =:= 4.0, Y =:= 1267650600228229401496703205376.");
        Assert.True(sol.Success);
    }

    // ----- chunk-213 regressions: control words survive relocation -----
    //
    // The cut barrier captured by get_level is a choice-point stack index,
    // not a heap reference. It used to be stored as a plain Cell (Tag.Ref);
    // a GC that ran between the capture (clause entry) and the cut relocated
    // it as if it were a heap index, so the cut committed to a garbage
    // barrier (crash / wrong commit). Tag.RawInt control words + the
    // conservative stack scan fix it. These build real garbage with numlist
    // so the collection actually slides cells, then exercise a cut whose
    // barrier was captured before the GC.

    [Fact]
    public void Gc_PreservesCutBarrier_DeepCut()
    {
        var e = new PrologEngine();
        // q/1: get_level fires at clause entry; numlist makes ~5000 cells of
        // garbage; garbage_collect slides the heap; the later '!' must still
        // commit to the barrier captured before the GC. Exactly one solution.
        e.ConsultString(
            ":- public q/1.\n"
            + "q(X) :- numlist(1, 5000, _), garbage_collect, "
            + "member(X, [a, b, c]), X == b, !.");
        var sols = e.QueryAll("q(X).").ToList();
        Assert.Single(sols);
        Assert.Equal("b", sols[0]["X"]!.ToString());
    }

    [Fact]
    public void Gc_PreservesCutBarrier_IfThenElse()
    {
        var e = new PrologEngine();
        // The '->' compiles to a get_level + cut around the condition; GC
        // runs inside the condition, between capture and commit.
        e.ConsultString(
            ":- public r/1.\n"
            + "r(R) :- numlist(1, 5000, _), "
            + "( garbage_collect, member(X, [a, b, c]), X == b "
            + "  -> R = X ; R = none ).");
        var sol = e.Query("r(R).");
        Assert.True(sol.Success);
        Assert.Equal("b", sol["R"]!.ToString());
    }

    [Fact]
    public void Gc_Findall_PreservesPairTerms()
    {
        var e = new PrologEngine();
        // The downstream symptom of the cut-barrier bug was a goal aliased
        // to a '-'/2 answer-table pair under tabling/meta-call. findall over
        // '-'/2 pairs with a GC mid-query must return them intact.
        e.ConsultString(":- public p/2.\np(a, 1).\np(b, 2).\np(c, 3).");
        var sol = e.Query(
            "numlist(1, 5000, _), garbage_collect, "
            + "findall(K-V, p(K, V), L), garbage_collect, "
            + "L == [a-1, b-2, c-3].");
        Assert.True(sol.Success);
    }
}
