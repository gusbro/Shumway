using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A cut compacts the trails IN PLACE, without leaving the module.
///
/// <para>Two passes, and the first one is the reason this is sound. A walk
/// can turn out to need a write into managed state the module cannot reach:
/// an orphaned attribute record cleared, a dead record dropped from the
/// store, a catch frame's snapshot clipped. Finding that out halfway through
/// is not an option, because a half-done compaction that leaves a record
/// orphaned is a leak the engine already documents. So the first pass
/// decides without touching anything.</para>
///
/// <para>The half that matters most here is the one no answer can show: a
/// surviving entry's binding-trail MARKER has to be rewritten to where the
/// binding side now stands, or a later unwind interleaves the two trails in
/// the wrong order. Every test below therefore backtracks THROUGH the cut's
/// work and checks what was restored, not just what was answered.</para>
/// </summary>
public sealed class CutCompactsInPlaceTests(ITestOutputHelper o)
{
    private const int CutNeedsCompaction = 28;

    private const string Corpus = """
        % The attributed variable is OLDER than the choice point the cut
        % commits to -- which takes a choice point BETWEEN its creation and
        % the mutation, because the barrier is the callee's entry state and
        % not the caller's. Its AttrModify entry then SURVIVES and nothing
        % has to be written back to the log: the shape the module finishes
        % on its own.
        mutate(V) :- member(_, [c, d]), put_attr(V, m, 1), !.
        old_attr(R) :- put_attr(V, m, 0), member(_, [a, b]), mutate(V),
                       get_attr(V, m, X), X == 1, R = yes.

        % Younger than the choice point: the entry is DROPPED, which orphans
        % its record and has to be written back. The module declines.
        young(R) :- member(_, [a, b]), put_attr(W, m, 1),
                    put_attr(W, m, 2), !, get_attr(W, m, X), X == 2, R = yes.

        % Bindings only: no extra trail at all, so the compaction needs no
        % image and runs whatever else is staged.
        bind_only(R) :- b1(R).
        b1(R) :- member(R, [yes, no]), R == yes, !.

        % Backtracking THROUGH a compacted cut. The outer choice point is
        % older than everything the cut dropped, so what it restores is
        % exactly what the compaction was allowed to keep.
        restores(Before, After) :-
            put_attr(V, m, 0),
            get_attr(V, m, Before),
            ( mutate(V), get_attr(V, m, A), A == 1, fail
            ; get_attr(V, m, After) ).

        % Two levels, so the compaction of the inner cut runs with the outer
        % one's entries still below it on the same trail.
        nested(R) :- put_attr(V, m, 0), outer(V), get_attr(V, m, R).
        outer(V) :- member(_, [a, b]), put_attr(V, m, 7), mutate(V), !.

        % A catch frame is live across the cut, which RAISES the survival
        % floor: the mutation the cut would otherwise drop has to survive so
        % the throw can undo it. The answer IS the proof -- 0 means the entry
        % was still there to restore, 1 means the compaction ate it.
        guarded(R) :- put_attr(V, m, 0),
                      catch(( mutate(V), throw(done) ), done, true),
                      get_attr(V, m, R).
        """;

    [Theory]
    [InlineData("old_attr(R), R == yes.")]
    [InlineData("young(R), R == yes.")]
    [InlineData("bind_only(R), R == yes.")]
    [InlineData("restores(B, A), B == 0, A == 0.")]
    [InlineData("nested(R), R == 1.")]
    [InlineData("guarded(R), R == 0.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>The compaction really happens in the module: a cut with a
    /// SURVIVING attribute entry on the extra trail does not step aside.
    /// </summary>
    [DiagFact]
    public void ASurvivingEntryIsCompactedInTheModule()
        => Assert.Equal(0L, GuardHitsOf("old_attr(R), R == yes."));

    /// <summary>And so does a cut that trailed only bindings, which needs no
    /// image at all.</summary>
    [DiagFact]
    public void ABindingOnlyCutIsCompactedInTheModule()
        => Assert.Equal(0L, GuardHitsOf("bind_only(R), R == yes."));

    /// <summary>A DROPPED attribute entry orphans its record, and clearing
    /// that record is a write the module cannot make. It does not decline
    /// for it: the index is PARKED and the host clears it when the chain
    /// comes out, because the clearing is hygiene and not semantics.
    ///
    /// <para>The counterproof for this one is not here but in the answers
    /// above -- restores/2, nested/1 and guarded/1 all backtrack or throw
    /// THROUGH a compaction that parked something -- and in <see
    /// cref="CutCompactsOrStepsAsideTests"/>, where the write that cannot be
    /// deferred still declines.</para></summary>
    [DiagFact]
    public void ADroppedEntryIsParkedRatherThanDeclined()
        => Assert.Equal(0L, GuardHitsOf("young(R), R == yes."));

    /// <summary>And the parked record really IS cleared. Deferring a write
    /// is only sound if the write happens, and a leak that never fires an
    /// assertion is exactly what this would otherwise become: the engine's
    /// own comment says an orphaned record roots its home and its old value
    /// against the heap GC forever.</summary>
    [DiagFact]
    public void AParkedRecordIsClearedOnTheWayOut()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("young(R), R == yes.").Success);
        Shumway.Core.Diagnostics.CompactCensus.Reset();
        Assert.True(tiered.Query("young(R), R == yes.").Success);
        o.WriteLine("orphans cleared = "
            + Shumway.Core.Diagnostics.CompactCensus.OrphansCleared);
        Assert.True(Shumway.Core.Diagnostics.CompactCensus.OrphansCleared > 0,
            "the module parked a record the host never cleared");
    }

    private long GuardHitsOf(string goal)
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(goal).Success, "the answer moved");
        long g = 0;
        for (int r = 28; r <= 33; r++)
        {
            long h = WasmTierDelegate.DiagMetaGuardHist[r];
            if (h > 0) o.WriteLine($"{goal} -> guard {r} = {h}");
            if (r == 28 || r >= 30) g += h;
        }
        o.WriteLine($"{goal} -> declines={g} deopts={WasmTierDelegate.DiagDeopts}");
        return g;
    }
}
