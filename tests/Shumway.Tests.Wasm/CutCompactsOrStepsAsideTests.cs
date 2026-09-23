using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A cut owes the trails a compaction, and the module pays what it
/// can.
///
/// <para>The interpreter's Cut lowers B and then COMPACTS, dropping trail
/// entries the cut has made unreachable. The module does the same walk in
/// place. What it cannot do is WRITE the managed state a dropped entry
/// sometimes owes -- an orphaned attribute record cleared, a dead record
/// dropped from the store, a catch frame's snapshot clipped -- so a first
/// pass decides whether any of that is coming and the walk only runs when
/// the answer is no.</para>
///
/// <para>This file holds the clp(FD) end of it, where the entries are real
/// domain narrowings rather than a synthetic attribute. <see
/// cref="CutCompactsInPlaceTests"/> holds the halves in isolation.</para>
///
/// <para>Why the guard existed at all, which is worth keeping: before the
/// module weighed anything, an AttrModify entry the interpreter drops
/// survived, sat below a later choice point, and was unwound at the end of
/// the goal -- putting twenty-five cells back to ATTVAR and leaving six
/// attributed variables in an answer that should have had none. On
/// SEND+MORE that was the difference between not finishing in 180 seconds
/// and 868 ms.</para></summary>
public sealed class CutCompactsOrStepsAsideTests(ITestOutputHelper o)
{
    /// <summary>Every reason the cut's compaction declines: no image, a
    /// dropped attribute entry, a dropped binding whose record must go, a
    /// snapshot to clip, an index past the image.</summary>
    private static readonly int[] DeclineGuards = [28, 30, 31, 32, 33];

    private const string Corpus = """
        :- use_module(library(clpfd)).
        % Narrowing a domain writes an AttrModify entry; the cut then has
        % something on the extra trail to weigh.
        narrow(X) :- X #< 5, !.
        trailed(R) :- X in 1..9, narrow(X), ( X #= 3 -> R = yes ; R = no ).
        % Nothing trailed since the barrier, so the cut has nothing to owe
        % and must NOT decline: the anti-vacuity half, without which a guard
        % that fired on every cut would pass the test above.
        plain(R) :- p(R).
        p(R) :- q(R), !.
        q(yes).
        """;

    /// <summary>A narrowing's entries are DROPPED by the cut, and a dropped
    /// attribute entry orphans its record. So this one declines, and the
    /// reasons are named: guard 30 for the orphan, 31 for a dead record the
    /// store has to lose. Both are writes into managed state, which is the
    /// line the module does not cross.</summary>
    [DiagFact]
    public void ACutWhoseDropsOweAWriteStillDeclines()
    {
        Assert.True(DeclinesOf("trailed(R), R == yes.") > 0,
            "a dropped attribute entry has to leave its record to the engine");
    }

    [DiagFact]
    public void ACutWithNothingTrailedDoesNotDeclineEither()
    {
        Assert.Equal(0L, DeclinesOf("plain(R), R == yes."));
    }

    /// <summary>The answer itself, against the interpreter's. A compaction
    /// that drops an entry it should have kept shows up here and nowhere
    /// else: the goal still succeeds, with the wrong residue.</summary>
    [Theory]
    [InlineData("trailed(R), R == yes.")]
    [InlineData("plain(R), R == yes.")]
    // The narrowing really happened and really stuck: 3 is still in
    // the domain, 7 is not.
    [InlineData("X in 1..9, narrow(X), X #= 3.")]
    [InlineData(@"X in 1..9, narrow(X), \+ X #= 7.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    private long DeclinesOf(string goal)
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(goal).Success, "the answer moved");
        long total = 0;
        foreach (int g in DeclineGuards)
        {
            long h = WasmTierDelegate.DiagMetaGuardHist[g];
            if (h > 0) o.WriteLine($"{goal} -> guard {g} = {h}");
            total += h;
        }
        o.WriteLine($"{goal} -> declines={total}");
        return total;
    }
}
