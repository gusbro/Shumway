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
    // A session's own: jit_compile/1 changes them.
    private static readonly Dictionary<string, WasmTierSettings> NotebookTiers = new();
    // What the last session's start was spent on, for the #notebook hook.
    private static string _lastSessionStart = "";

    /// <summary>Asks the page for a line for the kernel's running cell.</summary>
    [JSImport("ui.sessionAskForInput", "main.js")]
    internal static partial void SessionAskForInput(string kernelId);

    /// <summary>Asks the page to import a collection for the kernel's running
    /// cell: a JSON object with its dialect and location. The page answers
    /// through <see cref="SessionSupplyImport"/>.</summary>
    [JSImport("ui.sessionImportLibrary", "main.js")]
    internal static partial void SessionImportLibrary(string kernelId, string request);

    // The imports a session is waiting for, one per kernel at most: a cell's
    // query runs on one thread.
    private static readonly Dictionary<string, ImportWait> ImportWaits = new();

    private sealed class ImportWait
    {
        private readonly object _gate = new();
        private string? _answer;

        public void Answer(string answer)
        {
            lock (_gate) { _answer ??= answer; Monitor.PulseAll(_gate); }
        }

        public string Take()
        {
            lock (_gate)
            {
                // CA1416: blocking is unsupported on the browser's main thread;
                // this is a session's thread, which a cell's query holds anyway.
#pragma warning disable CA1416
                while (_answer is null) Monitor.Wait(_gate);
#pragma warning restore CA1416
                return _answer;
            }
        }
    }

    /// <summary>The page's collections for one kernel's session (ADR-070, O3).</summary>
    private sealed class PageLibraries(string kernelId) : INotebookLibraries
    {
        public void Refresh(Shumway.Embedding.PrologEngine engine)
        {
            // A collection that cannot be registered is one a cell cannot
            // load; that is no reason to refuse the cell.
            try { RegisterLibraries(engine); } catch (IOException) { }
        }

        public string Import(Shumway.Embedding.PrologEngine engine, string dialect, string location)
        {
            var wait = new ImportWait();
            lock (ImportWaits) ImportWaits[kernelId] = wait;
            string request = JsonObject(("dialect", dialect), ("location", location));
            // JavaScript is thread-affine: the page is asked on its thread.
            _jsThread!.Post(static state =>
            {
                var (id, line) = ((string, string))state!;
                SessionImportLibrary(id, line);
            }, (kernelId, request));
            string answer;
            try { answer = wait.Take(); }
            finally { lock (ImportWaits) ImportWaits.Remove(kernelId); }

            using var json = System.Text.Json.JsonDocument.Parse(answer);
            if (json.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.GetString());
            RegisterLibraries(engine);
            return json.RootElement.TryGetProperty("note", out var note) ? note.GetString() ?? "" : "";
        }
    }

    private static string JsonObject(params (string Key, string Value)[] pairs)
    {
        using var buffer = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            foreach ((string key, string value) in pairs) json.WriteString(key, value);
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The page's answer to <see cref="SessionImportLibrary"/>: a JSON
    /// object with <c>note</c>, what to tell the notebook, or <c>error</c>.</summary>
    [JSExport]
    internal static Task SessionSupplyImport(string kernelId, string answer)
    {
        ReleaseImport(kernelId, answer);
        return Answered();
    }

    // In WebKit, .NET's threads stop taking the page's calls after some
    // thousands of them, and sooner when an export hands back a Task that is
    // already complete. The exports a notebook calls give their result a
    // turn later, and a cell makes one call (SessionCall).
    private static async Task<T> Answered<T>(T value)
    {
        await Task.Yield();
        return value;
    }

    private static async Task Answered() => await Task.Yield();

    // An interrupted or disposed session is not left waiting for the page.
    private static void ReleaseImport(string kernelId, string answer)
    {
        ImportWait? wait;
        lock (ImportWaits) wait = ImportWaits.GetValueOrDefault(kernelId);
        wait?.Answer(answer);
    }

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
        return Answered(_notebooks?.MaxSessions ?? _notebookLimit);
    }

    /// <summary>Answers, a turn later: what the kernel asks while a step waits
    /// for the runtime, to know that the runtime still takes calls.</summary>
    [JSExport]
    internal static Task<bool> SessionAlive() => Answered(true);

    /// <summary>Starts a kernel's session: an engine with the server
    /// <paramref name="bundle"/> names, in <paramref name="directory"/>.
    /// Answers the empty string, or why there is no session.</summary>
    [JSExport]
    internal static async Task<string> SessionCreate(string kernelId, string bundle, string directory)
    {
        if (bundle != "prolog") return $"There is no notebook server '{bundle}'.";
        _notebooks ??= new SessionHost(_jsThread, _notebookLimit);
        HostSession session;
        var tier = new WasmTierSettings();
        try
        {
            session = _notebooks.Create(kernelId, new NotebookPage(kernelId), _ =>
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Shumway.Embedding.PrologEngine engine = BootEngine(tier);
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
                RegisterLibraries(session.Engine);
                return true;
            }).ConfigureAwait(false);
            NotebookServer server = NotebookServer.Start(session, new PageLibraries(kernelId));
            lock (NotebookServers)
            {
                NotebookServers[kernelId] = server;
                NotebookTiers[kernelId] = tier;
            }
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
        await Task.Yield();
        if (ServerOf(kernelId) is not { } server) return "";
        return await server.Request(jsonLine).ConfigureAwait(false) ?? "";
    }

    /// <summary>A cell's request and what the page copies after it, in one
    /// call. Answers a JSON object: <c>answer</c>, as
    /// <see cref="SessionRequest"/> answers; <c>files</c>, what
    /// <see cref="FilesList"/> answers for <paramref name="filesRoot"/>; and
    /// <c>compiled</c>, what <see cref="LibraryTakeCompiledOnDemand"/>
    /// answers.</summary>
    [JSExport]
    internal static async Task<string> SessionCall(string kernelId, string jsonLine, string filesRoot)
    {
        await Task.Yield();
        string answer = ServerOf(kernelId) is { } server
            // Back on this thread, where FilesList runs when it is called.
            ? await server.Request(jsonLine).ConfigureAwait(true) ?? ""
            : "";
        using var buffer = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("answer", answer);
            json.WritePropertyName("files");
            json.WriteRawValue(FilesJson(filesRoot));
            json.WriteString("compiled", TakeCompiledOnDemand());
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Interrupts what a kernel's session is running. The server is
    /// one query, so it ends with it (ADR-067 D5): the kernel starts a new
    /// session.</summary>
    [JSExport]
    internal static Task<bool> SessionCancel(string kernelId)
    {
        ReleaseImport(kernelId, JsonObject(("error", "the import was interrupted")));
        return Answered(_notebooks?.Find(kernelId)?.Cancel() ?? false);
    }

    /// <summary>Hands a line to a session's <c>read/1</c>.</summary>
    [JSExport]
    internal static Task SessionSupplyInput(string kernelId, string line)
    {
        _notebooks?.Find(kernelId)?.Input.Supply(line);
        return Answered();
    }

    /// <summary>Ends a session's input: <c>read/1</c> answers
    /// <c>end_of_file</c>.</summary>
    [JSExport]
    internal static Task SessionSupplyEndOfFile(string kernelId)
    {
        _notebooks?.Find(kernelId)?.Input.SupplyEndOfFile();
        return Answered();
    }

    /// <summary>Ends a kernel's session and frees its place.</summary>
    [JSExport]
    internal static async Task SessionDispose(string kernelId)
    {
        NotebookServer? server;
        lock (NotebookServers)
        {
            NotebookServers.Remove(kernelId, out server);
            NotebookTiers.Remove(kernelId);
        }
        server?.Close();
        ReleaseImport(kernelId, JsonObject(("error", "the session ended")));
        if (_notebooks is { } host) await host.Close(kernelId).ConfigureAwait(false);
    }

    /// <summary>What runs a session's goals, the wasm tier or the
    /// interpreter, for the kernel's banner. Empty when there is no such
    /// session.</summary>
    [JSExport]
    internal static Task<string> SessionTier(string kernelId)
    {
        WasmTierSettings? tier;
        lock (NotebookServers) tier = NotebookTiers.GetValueOrDefault(kernelId);
        // Read from here, not on the session's thread: the server's loop
        // holds that thread for as long as the session lives (ADR-067 D5).
        return Answered(tier is null || _notebooks?.Find(kernelId) is not { } session
            ? "" : TierOf(session.TopLevel?.Engine, tier));
    }

    /// <summary>What the last session's start was spent on: booting its
    /// engine, and loading the server into it.</summary>
    [JSExport]
    internal static Task<string> SessionStartCost() => Answered(_lastSessionStart);

    /// <summary>The managed heap after a full collection, for the page's
    /// measurements of what a session costs.</summary>
    [JSExport]
    internal static Task<double> ManagedHeapBytes()
        => Answered((double)GC.GetTotalMemory(forceFullCollection: true));
}
