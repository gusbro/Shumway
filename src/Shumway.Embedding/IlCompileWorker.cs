using System.Collections.Concurrent;

namespace Shumway.Embedding;

/// <summary>The process's IL-compile workers: persistent large-stack threads
/// shared by every engine. A compile runs on a big stack (see
/// <see cref="StackBytes"/>): a predicate's size is unbounded and a
/// StackOverflowException cannot be caught. Each worker pays that stack once.
/// A worker emits a predicate's IL and has .NET compile it to native code, so
/// the .NET JIT runs on these threads, several methods at once.
///
/// <para>There are at most <see cref="MaxWorkers"/>. One starts with the first
/// item; another only when items wait and no started worker is free, so a
/// program that never queues two compiles at once runs one.</para>
///
/// <para>Two entry points: <see cref="RunSync{T}"/> is for a caller that waits
/// for the result (synchronous promotion, warm-up, a bundle's offers installed
/// now); its item goes before everything queued in the background, so the wait
/// is the compiles in flight and its own. <see cref="RunAsync"/> is the background
/// mode (<c>IlPromotionStore.BackgroundCompilation</c>): the completion callback
/// runs on a worker and must only hand the result to a thread-safe queue.
/// Background items complete in any order.</para></summary>
internal static class IlCompileWorker
{
    private const int StackBytes = 16 * 1024 * 1024;

    /// <summary>The most workers the process runs: <c>SHUMWAY_IL_WORKERS</c>
    /// when set, else <see cref="WorkersFor"/> of the processors the process may
    /// use. Raising it takes effect at the next queued item; lowering it stops
    /// no worker already started.</summary>
    internal static int MaxWorkers { get; set; } = WorkersFor(
        Environment.ProcessorCount, Environment.GetEnvironmentVariable("SHUMWAY_IL_WORKERS"));

    /// <summary>A quarter of the logical processors, between 1 and 4: two
    /// logical processors share a core under SMT, and the rest of the machine
    /// belongs to the threads that run Prolog. A positive
    /// <paramref name="setting"/> overrides it.</summary>
    internal static int WorkersFor(int processors, string? setting)
        => int.TryParse(setting, out int n) && n > 0 ? n : Math.Max(1, Math.Min(4, processors / 4));

    private sealed class Item
    {
        public required Func<object?> Work;
        public ManualResetEventSlim? Done;                 // sync mode
        public Action<object?, Exception?>? OnCompleted;   // async mode (runs on worker)
        public bool Holds;                                 // a test's hold, not work
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
    // One permit per queued item.
    private static readonly SemaphoreSlim _signal = new(0);
    private static readonly List<Thread> _threads = new();
    private static readonly object _startLock = new();
    // _free: started workers not running an item (a new one counts from its start).
    private static int _started, _free, _busy, _peakBusy;
    private static long _processed;
    private static long _callbackFaults;

    // Set on each worker: work a worker submits runs inline.
    [ThreadStatic] private static bool _onWorker;

    /// <summary>One line of worker liveness for promotion diagnostics: no live
    /// worker with a non-empty queue is exactly the silent zero-promotions state
    /// the net48/x86 smoke flake shows.</summary>
    internal static string Describe()
    {
        int started, alive = 0;
        lock (_startLock)
        {
            started = _started;
            foreach (var t in _threads) if (t.IsAlive) alive++;
        }
        string state = started == 0 ? "never-started" : alive == 0 ? "DEAD" : $"alive={alive}";
        return $"workers={started}/{MaxWorkers} {state} "
            + $"queued={WaitingCallers}+{_queue.Count}+{_lowQueue.Count} processed={Interlocked.Read(ref _processed)} "
            + $"callbackFaults={Interlocked.Read(ref _callbackFaults)}";
    }

    /// <summary>Callers of <see cref="RunSync{T}"/> whose work has not started.</summary>
    internal static int WaitingCallers => _syncQueue.Count;

    /// <summary>The most items that ran at the same time since the process
    /// started or <see cref="ResetPeakBusy"/>, a test's hold not counted.</summary>
    internal static int PeakBusy => Volatile.Read(ref _peakBusy);

    internal static void ResetPeakBusy() => Volatile.Write(ref _peakBusy, 0);

    /// <summary>Runs <paramref name="work"/> on a large-stack worker and waits
    /// for the result; exceptions propagate to the caller. Work submitted from a
    /// worker runs inline (a nested sync compile would otherwise wait on a pool
    /// it may be holding).</summary>
    public static T RunSync<T>(Func<T> work)
    {
        if (_onWorker) return work();
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

    /// <summary>For tests: every worker the pool may run, started and held on an
    /// item of its own, so what is queued meanwhile waits until the result lets
    /// workers go (<see cref="Held.ReleaseOne"/>, or all at
    /// <see cref="Held.Dispose"/>).</summary>
    internal static Held Hold()
    {
        int n;
        lock (_startLock)
        {
            for (int i = _started; i < MaxWorkers; i++) StartWorker();
            n = _started;
        }
        var held = new Held(n);
        for (int i = 0; i < n; i++)
            Enqueue(_queue, new Item
            {
                Work = () => { held.Holding.Signal(); held.Gate.Wait(); return null; },
                OnCompleted = (_, _) => { },
                Holds = true,
            });
        if (!held.Holding.Wait(60_000))
        {
            held.Dispose();
            throw new TimeoutException($"the compile workers were not all held: {Describe()}");
        }
        return held;
    }

    internal sealed class Held : IDisposable
    {
        internal readonly SemaphoreSlim Gate = new(0);
        internal readonly CountdownEvent Holding;
        private int _held;

        internal Held(int n)
        {
            Holding = new CountdownEvent(n);
            _held = n;
        }

        /// <summary>How many workers are held.</summary>
        internal int Count => Volatile.Read(ref _held);

        /// <summary>Lets one held worker go on to the queue.</summary>
        internal void ReleaseOne()
        {
            if (Interlocked.Decrement(ref _held) >= 0) Gate.Release();
        }

        public void Dispose()
        {
            int n = Interlocked.Exchange(ref _held, 0);
            if (n > 0) Gate.Release(n);
        }
    }

    private static void Enqueue(ConcurrentQueue<Item> queue, Item item)
    {
        if (Volatile.Read(ref _started) == 0)
            lock (_startLock)
                if (_started == 0) StartWorker();   // throws when no worker can start
        queue.Enqueue(item);
        _signal.Release();
        // Another worker when items wait and no started worker is free.
        if (_signal.CurrentCount > Volatile.Read(ref _free) && Volatile.Read(ref _started) < MaxWorkers)
            lock (_startLock)
                if (_signal.CurrentCount > Volatile.Read(ref _free) && _started < MaxWorkers)
                    StartWorker();
    }

    // Under _startLock.
    private static void StartWorker()
    {
        var t = new Thread(Loop, StackBytes)
        {
            IsBackground = true,
            Name = "shumway-il-compile",
        };
        Interlocked.Increment(ref _free);
        try { t.Start(); }
        catch
        {
            // A failed Start (e.g. the 16 MB stack reservation on a fragmented
            // 32-bit address space). With no worker at all every later enqueue
            // would feed a queue nobody drains, the silent zero-promotions state.
            Interlocked.Decrement(ref _free);
            Console.Error.WriteLine("[il-worker] worker thread failed to start");
            if (_started == 0) throw;
            return;
        }
        _threads.Add(t);
        Volatile.Write(ref _started, _started + 1);
    }

    private static void Loop()
    {
        _onWorker = true;
        while (true)
        {
            _signal.Wait();
            if (!_syncQueue.TryDequeue(out var item) && !_queue.TryDequeue(out item)
                && !_lowQueue.TryDequeue(out item))
                continue;
            Interlocked.Decrement(ref _free);
            if (!item.Holds)
            {
                int busy = Interlocked.Increment(ref _busy);
                for (int peak = Volatile.Read(ref _peakBusy); busy > peak; peak = Volatile.Read(ref _peakBusy))
                    if (Interlocked.CompareExchange(ref _peakBusy, busy, peak) == peak) break;
            }
            try { item.Result = item.Work(); }
            catch (Exception ex) { item.Error = ex; }
            if (!item.Holds) Interlocked.Decrement(ref _busy);
            Interlocked.Increment(ref _processed);
            if (item.Done is not null) item.Done.Set();
            else
            {
                // A throwing completion callback must not kill a worker: with
                // the last one gone every later compile is stranded as
                // pending-forever. Count and report instead.
                try { item.OnCompleted?.Invoke(item.Result, item.Error); }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _callbackFaults);
                    Console.Error.WriteLine(
                        $"[il-worker] completion callback threw {ex.GetType().Name}: {ex.Message}");
                }
            }
            Interlocked.Increment(ref _free);
        }
    }
}
