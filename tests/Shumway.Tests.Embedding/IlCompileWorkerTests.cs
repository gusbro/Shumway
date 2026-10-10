using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The process has one IL compile worker. A caller that waits for its
/// compile (a synchronous promotion, a warm-up) is not queued behind the
/// background compiles every engine has asked for: its wait is the compile in
/// flight and its own.</summary>
// Exclusive: the test holds the process's worker while it queues.
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class IlCompileWorkerTests
{
    [Fact]
    public void AWaitingCallerGoesBeforeTheBackgroundQueue()
    {
        var order = new ConcurrentQueue<string>();
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IlCompileWorker.RunAsync(() => { running.Set(); release.Wait(); return null; }, (_, _) => { });
        Assert.True(running.Wait(60_000), "the worker did not reach the item that holds it");

        const int background = 20;
        for (int i = 0; i < background; i++)
        {
            string name = $"background {i}";
            IlCompileWorker.RunAsync(() => { order.Enqueue(name); return null; }, (_, _) => { });
        }
        var caller = new Thread(() => IlCompileWorker.RunSync(() => { order.Enqueue("waiting"); return 0; }));
        caller.Start();
        var clock = Stopwatch.StartNew();
        while (IlCompileWorker.WaitingCallers == 0)
        {
            Assert.True(clock.ElapsedMilliseconds < 60_000, "the waiting caller never queued");
            Thread.Sleep(1);
        }

        release.Set();
        Assert.True(caller.Join(60_000), "the waiting caller did not return");
        while (order.Count < background + 1)
        {
            Assert.True(clock.ElapsedMilliseconds < 120_000, "the background queue did not drain");
            Thread.Sleep(1);
        }
        var expected = new List<string> { "waiting" };
        for (int i = 0; i < background; i++) expected.Add($"background {i}");
        Assert.Equal(expected, order.ToArray());
    }
}
