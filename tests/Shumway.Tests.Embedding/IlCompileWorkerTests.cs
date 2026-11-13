using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The process's IL compile workers. A caller that waits for its
/// compile (a synchronous promotion, a warm-up) is not queued behind the
/// background compiles every engine has asked for: the next free worker takes
/// it first. How many workers the process may run follows the processors it may
/// use.</summary>
// Exclusive: the test holds the process's workers while it queues.
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class IlCompileWorkerTests
{
    [Fact]
    public void AWaitingCallerGoesBeforeTheBackgroundQueue()
    {
        var order = new ConcurrentQueue<string>();
        var held = IlCompileWorker.Hold();
        Thread caller;
        var clock = Stopwatch.StartNew();
        const int background = 20;
        try
        {
            for (int i = 0; i < background; i++)
            {
                string name = $"background {i}";
                IlCompileWorker.RunAsync(() => { order.Enqueue(name); return null; }, (_, _) => { });
            }
            caller = new Thread(() => IlCompileWorker.RunSync(() => { order.Enqueue("waiting"); return 0; }));
            caller.Start();
            while (IlCompileWorker.WaitingCallers == 0)
            {
                Assert.True(clock.ElapsedMilliseconds < 60_000, "the waiting caller never queued");
                Thread.Sleep(1);
            }
            Assert.Empty(order);
            // One worker free: what it takes first is the waiting caller's item.
            held.ReleaseOne();
            Assert.True(caller.Join(60_000), "the waiting caller did not return");
        }
        finally { held.Dispose(); }

        while (order.Count < background + 1)
        {
            Assert.True(clock.ElapsedMilliseconds < 120_000, "the background queue did not drain");
            Thread.Sleep(1);
        }
        var ran = order.ToArray();
        Assert.Equal("waiting", ran[0]);
        var expected = new List<string>();
        for (int i = 0; i < background; i++) expected.Add($"background {i}");
        Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal),
            ran.Skip(1).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(1, null, 1)]
    [InlineData(2, null, 1)]
    [InlineData(3, null, 1)]
    [InlineData(4, null, 2)]
    [InlineData(6, null, 3)]
    [InlineData(8, null, 4)]
    [InlineData(16, null, 4)]
    [InlineData(64, null, 4)]
    [InlineData(8, "3", 3)]
    [InlineData(2, "6", 6)]
    [InlineData(8, "0", 4)]
    [InlineData(8, "x", 4)]
    public void TheWorkersFollowTheProcessorsOrTheSetting(int processors, string? setting, int workers)
        => Assert.Equal(workers, IlCompileWorker.WorkersFor(processors, setting));
}
