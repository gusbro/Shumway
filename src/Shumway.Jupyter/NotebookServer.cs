using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Shumway.Embedding;
using Shumway.WebHost;

namespace Shumway.Jupyter;

/// <summary>The notebook server of one session (ADR-067): the server's
/// Prolog loop running on the session's engine for as long as the kernel
/// lives, and the two pipes a kernel reaches it through.
///
/// <para>A kernel sends one request line with <see cref="Request"/> and gets
/// the task of its response line. Requests are strictly one after another.
/// The loop holds the engine thread, so nothing else can be queued to the
/// session while the server runs.</para></summary>
public sealed class NotebookServer
{
    private static readonly ConditionalWeakTable<PrologEngine, NotebookServer> Servers = new();

    // A plain object: System.Threading.Lock does not exclude on browser-wasm
    // with threads.
    private readonly object _gate = new();
    private TaskCompletionSource<string?>? _pending;
    private bool _stopped;

    private NotebookServer()
    {
        Responses = new ResponsePipe(Deliver);
        Ended = Task.CompletedTask;
    }

    internal RequestPipe Requests { get; } = new();

    internal ResponsePipe Responses { get; }

    internal OutputCapture Output { get; } = new();

    /// <summary>Completes when the server's loop has ended: a cell called
    /// <c>halt</c>, <see cref="Close"/> was called, the session was cancelled,
    /// or the loop raised.</summary>
    public Task Ended { get; private set; }

    /// <summary>The diagnostic of the error that ended the loop, or null
    /// when it ended by itself.</summary>
    public string? Failure { get; private set; }

    internal static NotebookServer? Of(PrologEngine engine)
        => Servers.TryGetValue(engine, out NotebookServer? server) ? server : null;

    /// <summary>Gives an engine the server's code. For a session's boot
    /// function, before the engine runs anything.</summary>
    public static void Install(PrologEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ServerPredicates.Register();
        // In the order they import each other.
        engine.ConsultString(Source("jupyter_json.pl"));
        engine.ConsultString(Source("jupyter.pl"));
        engine.ConsultString(Source("jupyter_server.pl"));
    }

    /// <summary>Starts the server on a session whose engine was prepared
    /// with <see cref="Install"/>. What the session writes from now on goes
    /// to the responses, not to the page.</summary>
    public static NotebookServer Start(HostSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var server = new NotebookServer();
        server.Ended = session.OnEngine(() => server.Run(session));
        return server;
    }

    /// <summary>Sends one request and returns its response line, or null
    /// when the server has stopped and will answer nothing more.</summary>
    public Task<string?> Request(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.AsSpan().IndexOfAny('\n', '\r') >= 0)
            throw new ArgumentException("A request is one line.", nameof(line));
        // Not on the engine thread: a continuation that sends the next
        // request would run inside the write of this response.
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopped) return Task.FromResult<string?>(null);
            if (_pending is not null)
                throw new InvalidOperationException("The previous request has not been answered.");
            _pending = answer;
        }
        Requests.Add(line);
        return answer.Task;
    }

    /// <summary>Ends the requests: the server stops once it has answered the
    /// one it is working on.</summary>
    public void Close() => Requests.Complete();

    private bool Run(HostSession session)
    {
        PrologEngine engine = session.Engine;
        Servers.AddOrUpdate(engine, this);
        engine.Out = Output;
        engine.Error = Output;
        engine.Warnings = Output;
        QueryStep step = new(QueryStepKind.Error, "The server did not start.");
        try
        {
            session.StartQuery("jupyter_server:jupyter_serve.");
            step = session.Next(80);
            session.EndRun();
        }
        finally
        {
            Stop(step);
        }
        return true;
    }

    private void Stop(QueryStep step)
    {
        TaskCompletionSource<string?>? pending;
        lock (_gate)
        {
            _stopped = true;
            pending = _pending;
            _pending = null;
            if (step.Kind == QueryStepKind.Error) Failure = step.Text;
        }
        // A request the loop died on still gets an answer.
        pending?.TrySetResult(Failure is { } text ? Unhandled(text) : null);
    }

    private void Deliver(string line)
    {
        TaskCompletionSource<string?>? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }
        pending?.TrySetResult(line);
    }

    private static string Unhandled(string text)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WriteNull("id");
            json.WriteStartObject("error");
            json.WriteNumber("code", -4715);
            json.WriteString("message", "Unhandled exception");
            json.WriteStartObject("data");
            json.WriteString("prolog_message", text);
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Source(string name)
    {
        Assembly assembly = typeof(NotebookServer).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The server's source '{name}' is not in the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
