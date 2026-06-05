using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Theme 2 — the async / cancellable query API: QueryAsync (off-thread
/// IAsyncEnumerable) and QueryAll(string, CancellationToken) (synchronous,
/// cancellable). Cancellation is cooperative: the engine aborts at its next
/// goal-boundary safe point with OperationCanceledException (not a Prolog ball).</summary>
public class QueryAsyncTests
{
    private static PrologEngine WithCounter()
    {
        var e = new PrologEngine();
        // No base case → a long-running FAILING search; each recursive call is a
        // goal boundary (safe point), so it is promptly cancellable.
        e.ConsultString("count(N) :- N > 0, N1 is N - 1, count(N1).");
        return e;
    }

    [Fact]
    public async Task QueryAsync_YieldsAllSolutions()
    {
        var e = new PrologEngine();
        e.ConsultString("color(red). color(green). color(blue).");
        var got = new List<string>();
        await foreach (var s in e.QueryAsync("color(C)."))
            got.Add(s["C"]!.ToString()!);
        Assert.Equal(new[] { "red", "green", "blue" }, got);
    }

    [Fact]
    public async Task QueryAsync_MatchesSyncQueryAll()
    {
        var e = new PrologEngine();
        e.ConsultString("n(1). n(2). n(3). n(4). n(5).");
        var sync = e.QueryAll("n(X).").Select(s => s["X"]!.ToString()!).ToList();
        var asyncGot = new List<string>();
        await foreach (var s in e.QueryAsync("n(X)."))
            asyncGot.Add(s["X"]!.ToString()!);
        Assert.Equal(sync, asyncGot);
    }

    [Fact]
    public async Task QueryAsync_Cancellation_AbortsLongSearch()
    {
        var e = WithCounter();
        using var cts = new CancellationTokenSource(80);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in e.QueryAsync("count(2000000000).", cts.Token))
            {
                // count/1 has no solution; this body never runs. The token
                // fires first and the engine aborts at a safe point.
            }
        });
    }

    [Fact]
    public void QueryAll_WithToken_Cancellation_AbortsLongSearch()
    {
        var e = WithCounter();
        using var cts = new CancellationTokenSource(80);
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            foreach (var _ in e.QueryAll("count(2000000000).", cts.Token))
            {
            }
        });
    }

    [Fact]
    public void QueryAll_WithToken_UncancelledRunsNormally()
    {
        var e = new PrologEngine();
        e.ConsultString("ok(1). ok(2).");
        var xs = e.QueryAll("ok(X).", CancellationToken.None)
            .Select(s => s.Get<long>("X")).ToList();
        Assert.Equal(new[] { 1L, 2L }, xs);
    }

    [Fact]
    public async Task QueryAsync_AlreadyCancelledToken_DoesNotEnumerate()
    {
        var e = WithCounter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in e.QueryAsync("count(10).", cts.Token))
            {
            }
        });
    }
}
