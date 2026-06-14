using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 349 (Phase 28): self-tail-recursion compiles to an in-method IL loop
/// (a <c>br</c> to the predicate's cursor-0 entry) instead of the
/// marker / dispatch-loop round trip — the GProlog-style jump for the common
/// hot case. Wired into the indexed-dispatch path. These run self-recursive
/// indexed predicates through a <b>persisted IL bundle</b> (which uses the same
/// EmitIndexedDispatchBody) and check: the loop computes correctly, stays in
/// constant C# stack at depth, and — the subtle case — backtracking through a
/// self-recursive predicate that left a choice point still restores the right
/// (pre-loop) arguments from the choice point's own register snapshot.
/// </summary>
public class Chunk349Tests
{
    private static PrologEngine LoadIl(string src)
    {
        var bundle = new Bundle(new[] { new BundleEntry("c349", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));
        return engine;
    }

    [Fact]
    public void IntegerSelfRecursion_ComputesCorrectly()
    {
        // sum(N, Acc, R): integer-indexed (0 vs N), self-tail-recursive.
        var e = LoadIl(
            ":- public sum/3.\n" +
            "sum(0, A, A) :- !.\n" +
            "sum(N, A, R) :- N > 0, A1 is A + N, N1 is N - 1, sum(N1, A1, R).\n");
        var s = e.Query("sum(100, 0, R).");
        Assert.True(s.Success);
        Assert.Equal(5050L, ((IntTerm)s["R"]!).Value);   // 1+..+100
    }

    [Fact]
    public void DeepSelfRecursion_StaysInConstantStack()
    {
        // 200k-deep self-recursion: the in-method loop must not grow the C#
        // stack (it branches, never nests a frame).
        var e = LoadIl(
            ":- public down/1.\n" +
            "down(0) :- !.\n" +
            "down(N) :- N > 0, N1 is N - 1, down(N1).\n");
        Assert.True(e.Query("down(200000).").Success);
    }

    [Fact]
    public void SelfRecursion_WithChoicePoints_BacktracksCorrectly()
    {
        // gen/2 enumerates every sub-sequence: the cons clauses both match, so
        // a choice point is left at each step, and the recursive call is a self
        // tail call (the in-method loop). Backtracking must restore each choice
        // point's own saved arguments, not the loop's overwritten registers.
        var e = LoadIl(
            ":- public gen/2.\n" +
            "gen([], []).\n" +
            "gen([H|T], [H|R]) :- gen(T, R).\n" +
            "gen([_|T], R) :- gen(T, R).\n");
        int count = e.QueryAll("gen([a,b,c,d], R).").Count();
        Assert.Equal(16, count);                         // 2^4 sub-sequences
        // And the actual sub-sequences are right (spot-check the full one and []).
        Assert.True(e.Query("gen([a,b,c,d], [a,b,c,d]).").Success);
        Assert.True(e.Query("gen([a,b,c,d], []).").Success);
        Assert.False(e.Query("gen([a,b,c,d], [d,a]).").Success);   // order preserved
    }

    [Fact]
    public void SelfRecursion_CutInBaseCaseCommits()
    {
        // The base-case cut must commit correctly even though earlier iterations
        // ran through the in-method loop (which sets B0 = B at each back-edge).
        var e = LoadIl(
            ":- public last/2.\n" +
            "last([X], X) :- !.\n" +
            "last([_|T], X) :- last(T, X).\n");
        var s = e.Query("last([1,2,3,4,5], X).");
        Assert.True(s.Success);
        Assert.Equal(5L, ((IntTerm)s["X"]!).Value);
        Assert.Single(e.QueryAll("last([1,2,3], X)."));   // deterministic
    }
}
