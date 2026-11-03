using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 12 chunk 158: auto-compaction and per-predicate
/// <c>compact_dynamic_buffer/1</c>. Every <see cref="PrologEngine.CompactWatermark"/>
/// mutations a query's setup checks the buffer's growth, and rebuilds it
/// once it has grown to twice its length at the last link: proportional,
/// so a churn the layouts recycle never pays a rebuild, and a buffer
/// that grows pays one per doubling. <c>compact_dynamic_buffer/1</c>
/// validates a predicate indicator and then delegates to the same full
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

    // Grows the buffer past twice its length at the last link, a query of
    // twenty facts of the cold predicate at a time, and returns how many went in.
    private static int GrowToTheDouble(PrologEngine e, string pred = "d")
    {
        int linked = e._persistentLengthAtLink;
        Assert.True(linked > 0, "the buffer was never linked");
        int facts = 0;
        while (e._persistentLength < 2 * linked)
        {
            Assert.True(e.Query($"between(1, 20, I), assertz({pred}(f(I, a, b, c, d, e, f, g, h, i, j, k, l, m, n, o))), fail ; true.").Success);
            facts += 20;
            Assert.True(facts < 200_000, "the buffer never doubled");
        }
        return facts;
    }

    [Fact]
    public void ABufferThatDoubledSinceItsLinkCompactsAtTheNextCheck()
    {
        var e = new PrologEngine();
        e.CompactWatermark = 3;
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query("d(_) ; true.").Success);
        long drops = e.PersistentInvalidations;
        int facts = GrowToTheDouble(e);
        // Checked at every query's setup on the way (twenty mutations each):
        // below the double, no compaction.
        Assert.Equal(drops, e.PersistentInvalidations);
        // The next setup's check compacts: the buffer is linked again, its
        // length at the link the new measure.
        Assert.True(e.Query("d(_) ; true.").Success);
        Assert.Equal(drops + 1, e.PersistentInvalidations);
        Assert.Equal(0, e.PersistentMutationsSinceCompact);
        Assert.True(e._persistentLengthAtLink > 0 && e._persistentLength < 2 * e._persistentLengthAtLink);
        // Dispatch correctness across the compaction: every fact is there.
        Assert.Equal(facts, e.QueryAll("d(_).").Count());
    }

    [Fact]
    public void ABufferThatDidNotDoubleIsNotCompactedAtTheChecks()
    {
        var e = new PrologEngine();
        e.CompactWatermark = 3;
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query("d(_) ; true.").Success);
        long drops = e.PersistentInvalidations;
        for (int i = 0; i < 10; i++)
            Assert.True(e.Query($"assertz(d({i})), assertz(d({i})), retract(d({i})).").Success);
        Assert.True(e.Query("d(_) ; true.").Success);
        Assert.Equal(drops, e.PersistentInvalidations);
        // The checks ran: the count is back to zero after each.
        Assert.Equal(0, e.PersistentMutationsSinceCompact);
        Assert.Equal(10, e.QueryAll("d(_).").Count());
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
        e.ConsultString(":- dynamic d/1, g/1.");
        e.Query("assertz(d(a)).");
        e.Query("assertz(d(b)).");
        Assert.True(e.Query("d(a).").Success);   // hot
        Assert.True(e.Query("d(a).").Success);   // laid out indexed at this setup
        long drops = e.PersistentInvalidations;
        // The growth goes to a cold predicate of its own: a key of a kind the
        // hot one's layout has no switch for would lay it out again instead.
        GrowToTheDouble(e, "g");
        // The next check compacts.
        Assert.True(e.Query("d(a).").Success);
        Assert.Equal(drops + 1, e.PersistentInvalidations);
        Assert.Equal(0, e.PersistentMutationsSinceCompact);
        // Mutate further — in-place paths resume.
        e.Query("assertz(d(c)).");
        e.Query("retract(d(a)).");
        Assert.True(e.Query("d(c).").Success);
        Assert.False(e.Query("d(a).").Success);
    }
}
