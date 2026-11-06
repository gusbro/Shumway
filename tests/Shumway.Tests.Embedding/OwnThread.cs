using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shumway.Tests.Embedding;

/// <summary>Work a test waits for with a time limit. Not on the pool: its threads can all be
/// waiting on something, and it may not add one while the machine is busy, so the limit would
/// run out before the work began.</summary>
internal static class OwnThread
{
    public static Task<T> Run<T>(Func<T> work) => Task.Factory.StartNew(work,
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static Task Run(Action work) => Task.Factory.StartNew(work,
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
