using System.Collections.Concurrent;
using Shumway.Embedding;
using Shumway.TopLevel;

namespace Shumway.WebHost;

/// <summary>How a step of a query ended.</summary>
public enum QueryStepKind
{
    /// <summary>A solution; more may follow.</summary>
    Solution,
    /// <summary>The last solution.</summary>
    Last,
    /// <summary>No (more) solutions, or the query was cancelled.</summary>
    Failed,
    /// <summary>The query raised; the text is the diagnostic.</summary>
    Error,
    /// <summary><c>abort/0</c> ended the query.</summary>
    Aborted,
}

/// <summary>One step of a query: what happened and the text to show.</summary>
public readonly record struct QueryStep(QueryStepKind Kind, string Text);

/// <summary>One engine and everything that belongs to it alone (ADR-068):
/// its thread, the query it has open, its input, its output and its working
/// directory. A page holds one per notebook; WebShumway holds exactly one.
///
/// <para><b>Threads.</b> An engine is single-threaded inside, so its work is
/// queued to one dedicated thread (<see cref="OnEngine{T}"/>). The same
/// thread every time, not merely one at a time: a compiled wasm module is
/// registered in the function table of the thread that calls it, and a new
/// thread would pay each registration again. The members marked "engine
/// thread" must be called from inside <see cref="OnEngine{T}"/>.
/// <see cref="Cancel"/>, <see cref="Input"/> and <see cref="Shutdown"/> are
/// for any thread: they have to reach a search that is holding the engine
/// thread.</para></summary>
public sealed class HostSession : IDisposable
{
    private readonly Func<HostSession, PrologEngine> _boot;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly object _threadLock = new();
    private Thread? _thread;
    private int _threadId;
    private QueryRun? _run;
    private string? _directory;
    private int _closed;

    internal HostSession(string id, SynchronizationContext? pageThread,
        ISessionPage page, Func<HostSession, PrologEngine> boot)
    {
        Id = id;
        _boot = boot;
        _pageThread = pageThread;
        _page = page;
        Output = new PageOutput(pageThread, page);
        Input = new PageInput(pageThread, page);
    }

    private readonly SynchronizationContext? _pageThread;
    private readonly ISessionPage _page;

    // The page hears it on its own thread, as it hears output.
    private void InitializationRunning(bool running)
    {
        if (_pageThread is null) _page.InitializationRunning(running);
        else _pageThread.Post(static o =>
        {
            var (page, on) = ((ISessionPage, bool))o!;
            page.InitializationRunning(on);
        }, (_page, running));
    }

    /// <summary>The id the host knows this session by.</summary>
    public string Id { get; }

    public PageOutput Output { get; }

    public PageInput Input { get; }

    /// <summary>The top level over the current engine. Null until
    /// <see cref="StartEngine"/> has run.</summary>
    public TopLevelSession? TopLevel { get; private set; }

    /// <summary>The current engine. Throws when none has been started.</summary>
    public PrologEngine Engine
        => TopLevel?.Engine
           ?? throw new InvalidOperationException($"Session '{Id}' has no engine yet.");

    /// <summary>True while a query is open.</summary>
    public bool HasRun => _run is not null;

    /// <summary>Raised on the engine thread when the open query ends, however
    /// it ends.</summary>
    public event Action? RunEnded;

    /// <summary>The managed id of the engine thread; 0 before it starts.</summary>
    public int EngineThreadId => _threadId;

    /// <summary>Runs <paramref name="work"/> on the session's thread, after
    /// whatever is already queued.</summary>
    public Task<T> OnEngine<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // RunContinuationsAsynchronously: otherwise the awaiting continuation
        // runs on the engine thread, and one that queues more engine work
        // would wait on the queue it is standing in.
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            EnsureThread();
            _queue.Add(() =>
            {
                try { done.SetResult(work()); }
                catch (Exception e) { done.SetException(e); }
            });
        }
        catch (InvalidOperationException)
        {
            done.SetException(new ObjectDisposedException(
                nameof(HostSession), $"Session '{Id}' is closed."));
        }
        return done.Task;
    }

    private void EnsureThread()
    {
        if (_thread is not null) return;
        lock (_threadLock)
        {
            if (_thread is not null) return;
            var thread = new Thread(() =>
            {
                _threadId = Environment.CurrentManagedThreadId;
                foreach (Action item in _queue.GetConsumingEnumerable()) item();
            })
            { IsBackground = true, Name = "shumway-engine" };
            thread.Start();
            _thread = thread;
        }
    }

    /// <summary>Starts an engine, replacing the one there was. Engine thread.
    /// The engine writes to this session's output, reads this session's input
    /// and has a working directory of its own (ADR-071), so nothing it does
    /// reaches another session.</summary>
    public void StartEngine()
    {
        // What was open belonged to the engine being discarded, a goal blocked
        // on input included.
        EndRun();
        Input.EndInput();
        PrologEngine engine = _boot(this);
        // Before the first query: query setup builds the stream registry.
        engine.Out = Output.Writer;
        // Load-time warnings default to standard error, which a page does not
        // show: a use_module that found nothing would load silently.
        engine.Warnings = Output.Writer;
        engine.Error = Output.ErrorWriter;
        engine.In = Input;
        engine.InitializationRunning = InitializationRunning;
        Input.Reset();
        engine.SetCwdScope(CwdScope.Engine, _directory);
        TopLevel = new TopLevelSession(engine);
    }

    /// <summary>The directory the session's engine resolves relative paths
    /// against. Engine thread. Kept across <see cref="StartEngine"/>: a new
    /// engine starts where the host last put the session, not where a program
    /// left the old one.</summary>
    public void SetDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        if (TopLevel is { } top) top.Engine.WorkingDirectory = directory;
    }

    /// <summary>Begins a query. Engine thread. Throws what the reader throws
    /// for text that is not a goal.</summary>
    public void StartQuery(string queryText)
    {
        // A fresh query gets a fresh input stream: an end of file answered to
        // the last one must not be the first thing this one reads.
        Input.Reset();
        _run = (TopLevel ?? throw new InvalidOperationException(
            $"Session '{Id}' has no engine yet.")).StartQuery(queryText);
    }

    /// <summary>Before a consult the host starts. Engine thread. The open
    /// query is over, and the consult's initialization goals read a fresh
    /// input stream, as a query does.</summary>
    public void StartConsult()
    {
        EndRun();
        Input.Reset();
    }

    /// <summary>Takes the next solution of the open query. Engine thread.</summary>
    public QueryStep Next(int width)
    {
        QueryRun? run = _run;
        if (run is null) return new QueryStep(QueryStepKind.Failed, "");
        try
        {
            if (!run.MoveNext()) { EndRun(); return new QueryStep(QueryStepKind.Failed, ""); }
            string text = run.Format(width);
            if (run.IsLast) { EndRun(); return new QueryStep(QueryStepKind.Last, text); }
            return new QueryStep(QueryStepKind.Solution, text);
        }
        catch (OperationCanceledException)
        {
            EndRun();
            return new QueryStep(QueryStepKind.Failed, "");
        }
        catch (Shumway.Core.PrologAbortException)
        {
            EndRun();
            return new QueryStep(QueryStepKind.Aborted, "");
        }
        catch (Exception ex)
        {
            EndRun();
            return new QueryStep(QueryStepKind.Error, Describe(ex));
        }
    }

    /// <summary>Ends the open query, if any. Engine thread.</summary>
    public void EndRun()
    {
        QueryRun? run = _run;
        _run = null;
        run?.Dispose();
        RunEnded?.Invoke();
    }

    /// <summary>Asks the running query to stop. Any thread. It only sets the
    /// token, which the engine observes at its next safe point; whoever
    /// finishes the run disposes it. Returns whether a query was open.</summary>
    public bool CancelRun()
    {
        QueryRun? run = _run;
        // The engine thread may be disposing this very run.
        try { run?.Cancel(); }
        catch (ObjectDisposedException) { }
        return run is not null;
    }

    /// <summary><see cref="CancelRun"/>, and the abandoned query's unpainted
    /// output goes with it: what Stop means. Any thread.</summary>
    public bool Cancel()
    {
        bool wasOpen = CancelRun();
        Output.Drop();
        // A goal waiting for input is not at a safe point: it is let go, and
        // reads nothing more.
        Input.EndInput();
        return wasOpen;
    }

    /// <summary>The error with the engine's call stack and source positions,
    /// one line per entry.</summary>
    public string Describe(Exception ex)
        => TopLevel is { } top
            ? string.Join('\n', ErrorRendering.Describe(top.Engine, ex))
            : ex.Message;

    /// <summary>Stops the session: cancels what is running, lets go of a goal
    /// blocked on input, and ends the thread once what is queued has run. Any
    /// thread. The task completes when the engine thread has let go of the
    /// engine.</summary>
    public Task Shutdown()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return Task.CompletedTask;
        Cancel();
        Task<bool> last = OnEngine(() =>
        {
            EndRun();
            TopLevel = null;
            return true;
        });
        _queue.CompleteAdding();
        return last;
    }

    public void Dispose() => _ = Shutdown();
}
