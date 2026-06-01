using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 11 chunk 157: <c>compact_dynamic_buffer/0</c> — invalidates
/// the persistent dynamic-code buffer so the next query setup
/// rebuilds it from current <c>_dynamicClauses</c>. Reclaims memory
/// consumed by in-place chain entries and clause bodies that are no
/// longer reachable from any current clause after a long run of
/// assertz / retract / asserta cycles.
///
/// <para>Correctness: after compaction every query must return the
/// same answers as before. Trade-off: the next query pays one re-
/// link cost; subsequent queries start fresh with append-only growth
/// (the chunk-155b-f in-place paths).</para>
/// </summary>
public class Chunk157Tests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    [Fact]
    public void CompactDynamicBuffer_LeavesDispatchCorrect()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        e.Query("assertz(d(c)).");
        e.Query("d(a).");
        e.Query("d(b).");
        e.Query("retract(d(b)).");
        // Compact.
        Assert.True(e.Query("compact_dynamic_buffer.").Success);
        // All correct queries still work.
        Assert.True(e.Query("d(a).").Success);
        Assert.False(e.Query("d(b).").Success);
        Assert.True(e.Query("d(c).").Success);
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { Atom("a"), Atom("c") }, xs);
    }

    [Fact]
    public void CompactDynamicBuffer_AfterManyMutations_DispatchStillCorrect()
    {
        // Heavy churn — assertz then retract many clauses, then
        // compact, then assertz some more.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        for (int i = 0; i < 50; i++)
            e.Query($"assertz(d({i})).");
        e.Query("d(0).");
        e.Query("d(0).");
        for (int i = 0; i < 25; i++)
            e.Query($"retract(d({i * 2})).");  // retract evens 0,2,4...48
        Assert.True(e.Query("compact_dynamic_buffer.").Success);
        // Survivors: odd numbers 1,3,5,...,49 (25 entries).
        var xs = e.QueryAll("d(X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        Assert.Equal(25, xs.Count);
        Assert.All(xs, v => Assert.True(v % 2 == 1));
        Assert.True(e.Query("d(1).").Success);
        Assert.False(e.Query("d(2).").Success);
    }

    [Fact]
    public void CompactDynamicBuffer_AllowsFurtherInPlaceMutations()
    {
        // After compaction, the next query rebuilds. Subsequent
        // mutations should still use the in-place path (chunks
        // 155b-f), not get stuck in rebuild mode.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        e.Query("d(a).");
        e.Query("d(b).");
        e.Query("compact_dynamic_buffer.");
        // Trigger another query to materialize the rebuild.
        e.Query("d(a).");
        // Now mutate again — should go through chunk-155b in place
        // (correctness alone is observable here).
        e.Query("assertz(d(c)).");
        e.Query("retract(d(a)).");
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new Term[] { Atom("b"), Atom("c") }, xs);
    }

    [Fact]
    public void CompactDynamicBuffer_NoOpOnFreshEngine_Succeeds()
    {
        // Compacting an engine with nothing dynamic should just
        // succeed without error.
        var e = new PrologEngine();
        Assert.True(e.Query("compact_dynamic_buffer.").Success);
        // And subsequent normal usage still works.
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        Assert.True(e.Query("d(1).").Success);
    }

    [Fact]
    public void CompactDynamicBuffer_WithMultiArgIndexed_StillCorrect()
    {
        // Multi-arg dynamic predicates went through chunk 156 in-
        // place. Compaction must work for them too.
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.ConsultString(":- dynamic shape/2.");
        e.Query("assertz(shape(circle, area)).");
        e.Query("assertz(shape(square, area)).");
        e.Query("assertz(shape(circle, perimeter)).");
        e.Query("shape(circle, area).");
        e.Query("shape(circle, area).");
        e.Query("assertz(shape(triangle, area)).");
        e.Query("retract(shape(square, area)).");
        e.Query("compact_dynamic_buffer.");
        Assert.Equal(2, e.QueryAll("shape(circle, _).").Count());
        Assert.Single(e.QueryAll("shape(triangle, _)."));
        Assert.False(e.Query("shape(square, _).").Success);
    }
}
