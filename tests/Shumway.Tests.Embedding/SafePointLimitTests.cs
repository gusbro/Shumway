using System;
using System.Threading;
using System.Threading.Tasks;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A safe point compares the heap top with one limit, which a
/// cancellation from another thread, a deadline or a collection due lowers.
/// A compiled loop that allocates nothing never reaches the collection
/// threshold, so only the limit can stop it: these run one in IL and stop it
/// both ways.</summary>
public sealed class SafePointLimitTests
{
    private const string Program = """
        :- public spin/2, endless/0, timed/0.
        % A last call with its arithmetic in registers: no heap, no frame.
        spin(M, M) :- !.
        spin(N, M) :- N1 is N + 1, spin(N1, M).
        far(M) :- M is 10 ^ 15.
        endless :- far(M), spin(0, M).
        timed :- far(M), catch(call_with_timeout(spin(0, M), 0.2), _, true).
        """;

    // Nothing is promoted under a deadline: the loop is warmed without one.
    private static PrologEngine Promoted()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(Program);
        Assert.True(e.Query("spin(0, 1000).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(e.Query("spin(0, 1000).").Success);
        // ANTI-VACUITY: the loop under test is the compiled one.
        Assert.True(e.IlPromotion.IsPromoted(
            FunctorTable.Intern(AtomTable.Intern("spin").Id, 2)), "spin/2 did not promote");
        return e;
    }

    [Fact]
    public async Task ACompiledLoopThatAllocatesNothingIsCancelled()
    {
        var e = Promoted();
        using var cts = new CancellationTokenSource(100);
        Exception? caught = null;
        var run = OwnThread.Run(() =>
        {
            try { foreach (var _ in e.QueryAll("endless.", cts.Token)) { } }
            catch (Exception ex) { caught = ex; }
        });
        // A loop that missed the cancellation would run on: the wait times out.
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsAssignableFrom<OperationCanceledException>(caught);
    }

    [Fact]
    public async Task ACompiledLoopThatAllocatesNothingRunsOutOfTime()
    {
        var e = Promoted();
        bool timedOut = await OwnThread.Run(() => e.Query("timed.").Success)
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(timedOut);
    }
}
