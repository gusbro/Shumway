using System.Runtime.InteropServices.JavaScript;
using Shumway.Jupyter;
using Shumway.WebHost;

namespace Shumway.Web;

/// <summary>The sessions of notebook kernels (ADR-068): an engine for each
/// kernel, running the notebook server (ADR-067). A kernel sends a request
/// line and gets the response line. These sessions are beside the page's own,
/// which the demo uses, and have their own limit.</summary>
internal static partial class WebShumwayApp
{
    // ADR-068 D7: ten sessions with the server loaded take the runtime's
    // memory from 138 MB to 344 MB, in the three browser engines.
    private static int _notebookLimit = 10;
    private static SessionHost? _notebooks;
    private static readonly Dictionary<string, NotebookServer> NotebookServers = new();
    // What the last session's start was spent on, for the #notebook hook.
    private static string _lastSessionStart = "";

    /// <summary>Asks the page for a line for the kernel's running cell.</summary>
    [JSImport("ui.sessionAskForInput", "main.js")]
    internal static partial void SessionAskForInput(string kernelId);

    private sealed class NotebookPage(string kernelId) : ISessionPage
    {
        // What the session writes is the server's to capture: it goes in the
        // response to the cell that wrote it.
        public void Write(string text) { }
        public void WriteError(string text) { }
        public void AskForInput() => SessionAskForInput(kernelId);
    }

    /// <summary>How many notebook sessions the page may hold. Takes effect
    /// before the first one is created; answers the limit in force.</summary>
    [JSExport]
    internal static Task<int> SessionLimit(int limit)
    {
        if (_notebooks is null && limit > 0) _notebookLimit = limit;
        return Task.FromResult(_notebooks?.MaxSessions ?? _notebookLimit);
    }

    /// <summary>Starts a kernel's session: an engine with the server
    /// <paramref name="bundle"/> names, in <paramref name="directory"/>.
    /// Answers the empty string, or why there is no session.</summary>
    [JSExport]
    internal static async Task<string> SessionCreate(string kernelId, string bundle, string directory)
    {
        if (bundle != "prolog") return $"There is no notebook server '{bundle}'.";
        _notebooks ??= new SessionHost(_jsThread, _notebookLimit);
        HostSession session;
        try
        {
            session = _notebooks.Create(kernelId, new NotebookPage(kernelId), _ =>
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Shumway.Embedding.PrologEngine engine = BootEngine(new WasmTierSettings());
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                NotebookServer.Install(engine);
                _lastSessionStart =
                    $"engine {System.Diagnostics.Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds:F0} ms, "
                    + $"server {System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds:F0} ms";
                return engine;
            });
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;      // the limit, or the id is taken
        }
        try
        {
            await session.OnEngine(() =>
            {
                if (directory.Length > 0)
                {
                    Directory.CreateDirectory(directory);
                    session.SetDirectory(directory);
                }
                session.StartEngine();
                return true;
            }).ConfigureAwait(false);
            NotebookServer server = NotebookServer.Start(session);
            lock (NotebookServers) NotebookServers[kernelId] = server;
            return "";
        }
        catch (Exception ex)
        {
            await _notebooks.Close(kernelId).ConfigureAwait(false);
            return session.Describe(ex);
        }
    }

    private static NotebookServer? ServerOf(string kernelId)
    {
        lock (NotebookServers) return NotebookServers.GetValueOrDefault(kernelId);
    }

    /// <summary>Sends a request line to a kernel's server and answers its
    /// response line. The empty string when there is no such session or its
    /// server has stopped: after <c>halt</c>, or after an interrupt.</summary>
    [JSExport]
    internal static async Task<string> SessionRequest(string kernelId, string jsonLine)
    {
        if (ServerOf(kernelId) is not { } server) return "";
        return await server.Request(jsonLine).ConfigureAwait(false) ?? "";
    }

    /// <summary>Interrupts what a kernel's session is running. The server is
    /// one query, so it ends with it (ADR-067 D5): the kernel starts a new
    /// session.</summary>
    [JSExport]
    internal static Task<bool> SessionCancel(string kernelId)
        => Task.FromResult(_notebooks?.Find(kernelId)?.Cancel() ?? false);

    /// <summary>Hands a line to a session's <c>read/1</c>.</summary>
    [JSExport]
    internal static Task SessionSupplyInput(string kernelId, string line)
    {
        _notebooks?.Find(kernelId)?.Input.Supply(line);
        return Task.CompletedTask;
    }

    /// <summary>Ends a session's input: <c>read/1</c> answers
    /// <c>end_of_file</c>.</summary>
    [JSExport]
    internal static Task SessionSupplyEndOfFile(string kernelId)
    {
        _notebooks?.Find(kernelId)?.Input.SupplyEndOfFile();
        return Task.CompletedTask;
    }

    /// <summary>Ends a kernel's session and frees its place.</summary>
    [JSExport]
    internal static async Task SessionDispose(string kernelId)
    {
        NotebookServer? server;
        lock (NotebookServers)
        {
            NotebookServers.Remove(kernelId, out server);
        }
        server?.Close();
        if (_notebooks is { } host) await host.Close(kernelId).ConfigureAwait(false);
    }

    /// <summary>What the last session's start was spent on: booting its
    /// engine, and loading the server into it.</summary>
    [JSExport]
    internal static Task<string> SessionStartCost() => Task.FromResult(_lastSessionStart);

    /// <summary>The managed heap after a full collection, for the page's
    /// measurements of what a session costs.</summary>
    [JSExport]
    internal static Task<double> ManagedHeapBytes()
        => Task.FromResult((double)GC.GetTotalMemory(forceFullCollection: true));
}
