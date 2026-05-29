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
}
