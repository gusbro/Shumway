using System.Collections.Concurrent;

namespace Shumway.Embedding;

/// <summary>One persistent large-stack IL-compile worker for the
/// whole process, replacing the previous thread-create + <c>Join</c> per compile
/// (a fresh 16 MB-stack thread per promotion, spawned on the query thread).
/// A compile runs on a big stack (see <see cref="StackBytes"/>): a predicate's
/// size is unbounded and a StackOverflowException cannot be caught. The worker
/// pays that stack once.
///
/// <para>Two entry points: <see cref="RunSync{T}"/> is for a caller that waits
/// for the result (synchronous promotion, warm-up, a bundle's offers installed
/// now); its item goes before everything queued in the background, so the wait
/// is the compile in flight and its own. <see cref="RunAsync"/> is the background
/// mode (<c>IlPromotionStore.BackgroundCompilation</c>): the completion callback
/// runs on the worker and must only hand the result to a thread-safe queue.</para></summary>
internal static class IlCompileWorker
{
    private const int StackBytes = 16 * 1024 * 1024;

    private sealed class Item
    {
        public required Func<object?> Work;
        public ManualResetEventSlim? Done;                 // sync mode
        public Action<object?, Exception?>? OnCompleted;   // async mode (runs on worker)
        public object? Result;
        public Exception? Error;
    }

    // Callers blocked on their result. Not behind _queue: the background compiles
    // of every engine in the process, dead ones included, can take seconds there.
    private static readonly ConcurrentQueue<Item> _syncQueue = new();
    private static readonly ConcurrentQueue<Item> _queue = new();
    // Work no caller waits on to run its code (ADR-061 continuation methods):
    // taken only when _queue is empty, so a delegate is never compiled behind it.
    private static readonly ConcurrentQueue<Item> _lowQueue = new();
    private static readonly SemaphoreSlim _signal = new(0);
    private static Thread? _thread;
    private static readonly object _startLock = new();
    private static long _processed;
    private static long _callbackFaults;

    /// <summary>One line of worker liveness for promotion diagnostics: a dead
    /// or never-started worker with a non-empty queue is exactly the silent
    /// zero-promotions state the net48/x86 smoke flake shows.</summary>
    internal static string Describe() =>
        $"worker thread={( _thread is null ? "never-started"
            : _thread.IsAlive ? "alive" : "DEAD")} "
        + $"queued={WaitingCallers}+{_queue.Count}+{_lowQueue.Count} processed={Interlocked.Read(ref _processed)} "
        + $"callbackFaults={Interlocked.Read(ref _callbackFaults)}";

    /// <summary>Callers of <see cref="RunSync{T}"/> whose work has not started.</summary>
    internal static int WaitingCallers => _syncQueue.Count;

    /// <summary>Runs <paramref name="work"/> on the shared large-stack worker and
    /// waits for the result; exceptions propagate to the caller. Work submitted
    /// from the worker itself runs inline (a nested sync compile would otherwise
    /// deadlock the single worker).</summary>
    public static T RunSync<T>(Func<T> work)
    {
        if (ReferenceEquals(Thread.CurrentThread, _thread)) return work();
        var item = new Item { Work = () => work(), Done = new ManualResetEventSlim(false) };
        Enqueue(_syncQueue, item);
        item.Done.Wait();
        item.Done.Dispose();
        if (item.Error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(item.Error).Throw();
        return (T)item.Result!;
    }

    /// <summary>Queues <paramref name="work"/>; <paramref name="onCompleted"/>
    /// fires on the worker thread with (result, error).</summary>
    public static void RunAsync(Func<object?> work, Action<object?, Exception?> onCompleted,
        bool lowPriority = false)
        => Enqueue(lowPriority ? _lowQueue : _queue, new Item { Work = work, OnCompleted = onCompleted });

    private static void Enqueue(ConcurrentQueue<Item> queue, Item item)
    {
        EnsureStarted();
        queue.Enqueue(item);
        _signal.Release();
    }

    private static void EnsureStarted()
    {
        if (_thread is not null) return;
        lock (_startLock)
        {
            if (_thread is not null) return;
            var t = new Thread(Loop, StackBytes)
            {
                IsBackground = true,
                Name = "shumway-il-compile",
            };
            _thread = t;   // publish before Start so the re-entrancy check holds
            try { t.Start(); }
            catch
            {
                // A failed Start (e.g. the 16 MB stack reservation on a
                // fragmented 32-bit address space) must not leave the dead
                // thread published: every later enqueue would feed a queue
                // nobody drains — the silent zero-promotions state.
                _thread = null;
                Console.Error.WriteLine("[il-worker] worker thread failed to start");
                throw;
            }
        }
    }

    private static void Loop()
    {
        while (true)
        {
            _signal.Wait();
            if (!_syncQueue.TryDequeue(out var item) && !_queue.TryDequeue(out item)
                && !_lowQueue.TryDequeue(out item))
                continue;
            try { item.Result = item.Work(); }
            catch (Exception ex) { item.Error = ex; }
            Interlocked.Increment(ref _processed);
            if (item.Done is not null) item.Done.Set();
            else
            {
                // A throwing completion callback must not kill the only
                // worker — that would strand every later compile as
                // pending-forever. Count and report instead.
                try { item.OnCompleted?.Invoke(item.Result, item.Error); }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _callbackFaults);
                    Console.Error.WriteLine(
                        $"[il-worker] completion callback threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
