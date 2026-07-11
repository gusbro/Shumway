using System.Collections.Concurrent;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Theme 2 — <see cref="EnginePool"/>: bounded reuse of thread-agile
/// engines for concurrent embedding.</summary>
public class EnginePoolTests
{
    private const string Program =
        "double(X, Y) :- Y is X * 2. fact(0, 1) :- !. fact(N, F) :- N > 0, N1 is N - 1, fact(N1, F1), F is N * F1.";

    [Fact]
    public void Rent_RunsQuery_AndReturns()
    {
        using var pool = EnginePool.FromSource(Program, maxSize: 4);
        using var lease = pool.Rent();
        var sol = lease.Activation.Query("double(21, R).");
        Assert.True(sol.Success);
        Assert.Equal(42L, sol.Get<long>("R"));
    }

    [Fact]
    public void SerialUse_ReusesOneEngine()
    {
        using var pool = EnginePool.FromSource(Program, maxSize: 4);
        for (int i = 0; i < 10; i++)
            using (var lease = pool.Rent())
                Assert.True(lease.Activation.Query("double(1, _).").Success);
        // No contention → a single engine was created and reused.
        Assert.Equal(1, pool.Created);
    }

    [Fact]
    public void ConcurrentQueries_AreIsolatedAndCorrect()
    {
        using var pool = EnginePool.FromSource(Program, maxSize: 8);
        var results = new ConcurrentDictionary<int, long>();
        Parallel.For(0, 200, i =>
        {
            using var lease = pool.Rent();
            var sol = lease.Activation.Query($"fact({i % 10}, F).");
            results[i] = sol.Get<long>("F");
        });
        long[] expected = { 1, 1, 2, 6, 24, 120, 720, 5040, 40320, 362880 };
        for (int i = 0; i < 200; i++)
            Assert.Equal(expected[i % 10], results[i]);
        Assert.True(pool.Created <= 8, $"created {pool.Created} > maxSize 8");
    }

    [Fact]
    public async Task RentAsync_Works()
    {
        using var pool = EnginePool.FromSource(Program, maxSize: 2);
        using var lease = await pool.RentAsync();
        Assert.True(lease.Activation.Query("double(5, R), R == 10.").Success);
    }

    [Fact]
    public void Rent_BlocksUntilReturned_WhenPoolExhausted()
    {
        using var pool = EnginePool.FromSource(Program, maxSize: 1);
        var lease1 = pool.Rent();
        // A second rent must wait; cancel it to prove it was blocking.
        using var cts = new CancellationTokenSource(150);
        Assert.Throws<OperationCanceledException>(() => pool.Rent(cts.Token));
        lease1.Dispose();
        // Now a rent succeeds immediately and reuses the returned engine.
        using var lease2 = pool.Rent();
        Assert.True(lease2.Activation.Query("double(2, _).").Success);
        Assert.Equal(1, pool.Created);
    }

    [Fact]
    public void DisposedPool_Throws()
    {
        var pool = EnginePool.FromSource(Program, maxSize: 2);
        pool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pool.Rent());
    }

    [Fact]
    public void FactoryThrow_DoesNotLeakPermit()
    {
        int calls = 0;
        var pool = new EnginePool(() =>
        {
            if (Interlocked.Increment(ref calls) <= 1) throw new InvalidOperationException("boom");
            var e = new PrologEngine();
            e.ConsultString(Program);
            return e;
        }, maxSize: 1);
        // First rent's factory throws — the permit must be released so a retry
        // can still acquire (otherwise the size-1 pool would deadlock).
        Assert.Throws<InvalidOperationException>(() => pool.Rent());
        using var lease = pool.Rent();   // would block forever if the permit leaked
        Assert.True(lease.Activation.Query("double(3, _).").Success);
    }
}
