using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 12 chunk 158: auto-compaction watermark and per-predicate
/// <c>compact_dynamic_buffer/1</c>. The watermark on
/// <see cref="PrologEngine.CompactWatermark"/> triggers a buffer
/// rebuild at the next query setup once accumulated mutations
/// cross the threshold; <c>compact_dynamic_buffer/1</c> validates
/// a predicate indicator and then delegates to the same full
/// rebuild (the persistent buffer holds every dynamic predicate
/// interleaved, so independent per-predicate compaction isn't
/// feasible without partial-relink support).
/// </summary>
public class Chunk158Tests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    [Fact]
    public void Watermark_BumpsOnMutation()
    {
        var e = new PrologEngine();
        e.CompactWatermark = long.MaxValue;  // disable auto-compact for this probe.
        e.ConsultString(":- dynamic d/1.");
        Assert.Equal(0, e.PersistentMutationsSinceCompact);
        e.Query("assertz(d(1)).");
        Assert.Equal(1, e.PersistentMutationsSinceCompact);
        e.Query("assertz(d(2)).");
        e.Query("asserta(d(0)).");
        e.Query("retract(d(2)).");
        Assert.Equal(4, e.PersistentMutationsSinceCompact);
    }

    [Fact]
    public void Watermark_AutoCompacts_AtNextQuery()
    {
        var e = new PrologEngine();
        e.CompactWatermark = 3;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        e.Query("assertz(d(2)).");
        e.Query("assertz(d(3)).");
        // Counter now at 3, threshold met. The NEXT query's setup
        // triggers the auto-compaction → counter resets.
        Assert.Equal(3, e.PersistentMutationsSinceCompact);
        // d(_) query is the trigger; its setup compacts.
        Assert.True(e.Query("d(1).").Success);
        Assert.Equal(0, e.PersistentMutationsSinceCompact);
        // Dispatch correctness across the auto-compaction.
        var xs = e.QueryAll("d(X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        Assert.Equal(new long[] { 1, 2, 3 }, xs);
    }

    [Fact]
    public void Watermark_HighThreshold_NeverFiresImplicitly()
    {
        var e = new PrologEngine();
        e.CompactWatermark = long.MaxValue;
        e.ConsultString(":- dynamic d/1.");
        for (int i = 0; i < 100; i++) e.Query($"assertz(d({i})).");
        // Counter at 100, watermark never reached → no auto-compact.
        e.Query("d(0).");
        Assert.Equal(100, e.PersistentMutationsSinceCompact);
    }

    [Fact]
    public void ExplicitCompact0_ResetsCounter()
    {
        var e = new PrologEngine();
        e.CompactWatermark = long.MaxValue;
        e.ConsultString(":- dynamic d/1.");
        for (int i = 0; i < 5; i++) e.Query($"assertz(d({i})).");
        Assert.Equal(5, e.PersistentMutationsSinceCompact);
        e.Query("compact_dynamic_buffer.");
        Assert.Equal(0, e.PersistentMutationsSinceCompact);
    }

    [Fact]
    public void Compact1_DynamicPredicate_Works()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(1)).");
        e.Query("assertz(d(2)).");
        Assert.True(e.Query("compact_dynamic_buffer(d/1).").Success);
        // Dispatch still correct.
        var xs = e.QueryAll("d(X).").Select(s => ((IntTerm)s["X"]!).Value).ToList();
        Assert.Equal(new long[] { 1, 2 }, xs);
    }

    [Fact]
    public void Compact1_StaticPredicate_RaisesPermissionError()
    {
        var e = new PrologEngine();
        e.ConsultString("p(1). p(2).");
        var sol = e.Query(
            "catch(compact_dynamic_buffer(p/1), error(permission_error(_, _, _), _), true).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Compact1_UnboundArg_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(compact_dynamic_buffer(_), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void Compact1_NonIndicator_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(compact_dynamic_buffer(foo), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("predicate_indicator"), sol["T"]);
    }

    [Fact]
    public void Watermark_AfterAutoCompact_InPlaceMutationsResume()
    {
        var e = new PrologEngine();
        e.JitIndexing.Threshold = 1;
        e.CompactWatermark = 2;
        e.ConsultString(":- dynamic d/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        // Crosses watermark (count=2). Next query triggers
        // auto-compaction.
        e.Query("d(a).");
        Assert.Equal(0, e.PersistentMutationsSinceCompact);
        // Mutate further — in-place paths resume.
        e.Query("assertz(d(c)).");
        e.Query("retract(d(a)).");
        Assert.True(e.Query("d(c).").Success);
        Assert.False(e.Query("d(a).").Success);
    }
}
