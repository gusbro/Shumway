using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shumway.Tests.Embedding;

/// <summary>Work a test waits for with a time limit. Not on the pool: its threads can all be
/// waiting on something, and it may not add one while the machine is busy, so the limit would
/// run out before the work began.</summary>
internal static class OwnThread
{
    // On .NET 5 and later what a socket completes reaches the program through the pool, so with
    // its threads taken a connect runs out of time. A test process holds many of them at once.
    static OwnThread()
    {
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));
    }

    public static Task<T> Run<T>(Func<T> work) => Task.Factory.StartNew(work,
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static Task Run(Action work) => Task.Factory.StartNew(work,
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
