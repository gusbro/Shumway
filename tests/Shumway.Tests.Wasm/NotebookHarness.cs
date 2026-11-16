using Shumway.Embedding;
using Shumway.Jupyter;
using Shumway.WebHost;

namespace Shumway.Tests.Wasm;

/// <summary>Notebook servers on the desktop, driven as a kernel drives them
/// in a page: one request line in, one response line out.</summary>
internal sealed class NotebookHarness : IDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly SessionHost _host = new(pageThread: null, maxSessions: 4);
    private readonly List<NotebookServer> _servers = new();

    public sealed class Page : ISessionPage
    {
        public int Asked;
        public Action? OnAsk;

        public void Write(string text) { }
        public void WriteError(string text) { }
        public void AskForInput() { Interlocked.Increment(ref Asked); OnAsk?.Invoke(); }
    }

    public (NotebookServer Server, HostSession Session) Open(string id, Page? page = null)
    {
        HostSession session = _host.Create(id, page ?? new Page(), _ =>
        {
            var engine = new PrologEngine();
            NotebookServer.Install(engine);
            return engine;
        });
        Task<bool> started = session.OnEngine(() => { session.StartEngine(); return true; });
        Assert.True(started.Wait(Patience), "the session did not start");
        NotebookServer server = NotebookServer.Start(session);
        _servers.Add(server);
        return (server, session);
    }

    /// <summary>The response to a request line, or null when the server has
    /// stopped.</summary>
    public static string? Ask(NotebookServer server, string request)
        => Result(server.Request(request));

    public static bool Finished(Task task) => task.Wait(Patience);

    public static T Result<T>(Task<T> task)
    {
        Assert.True(task.Wait(Patience), "the server did not answer");
        return task.Result;
    }

    public void Dispose()
    {
        // A server waiting for a request holds its session's thread.
        foreach (NotebookServer server in _servers) server.Close();
        Assert.True(_host.CloseAll().Wait(Patience));
    }
}
