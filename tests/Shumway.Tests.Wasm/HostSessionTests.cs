using System.Text;
using Shumway.Embedding;
using Shumway.WebHost;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-068: the sessions of one page. Each has an engine, a thread,
/// an input, an output and a working directory of its own, and nothing one
/// does reaches another.</summary>
public sealed class HostSessionTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly SessionHost _host = new(pageThread: null, maxSessions: 4);
    private readonly string _processDirectory = Directory.GetCurrentDirectory();
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "shumway-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Assert.True(_host.CloseAll().Wait(Patience));
        // No session ever moves the process.
        Assert.Equal(_processDirectory, Directory.GetCurrentDirectory());
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Page : ISessionPage
    {
        private readonly StringBuilder _out = new(), _err = new();
        public int Asked;
        public Action? OnAsk;

        public string Out { get { lock (_out) return _out.ToString(); } }
        public string Err { get { lock (_err) return _err.ToString(); } }

        public void Write(string text) { lock (_out) _out.Append(text); }
        public void WriteError(string text) { lock (_err) _err.Append(text); }
        public void AskForInput() { Interlocked.Increment(ref Asked); OnAsk?.Invoke(); }

        private readonly List<bool> _initializations = new();
        public bool[] Initializations { get { lock (_initializations) return _initializations.ToArray(); } }
        public void InitializationRunning(bool running) { lock (_initializations) _initializations.Add(running); }
    }

    private (HostSession Session, Page Page) Open(string id, string? directory = null)
    {
        var page = new Page();
        HostSession session = _host.Create(id, page, _ => new PrologEngine());
        On(session, () =>
        {
            if (directory is not null) session.SetDirectory(directory);
            session.StartEngine();
            return true;
        });
        return (session, page);
    }

    private static T On<T>(HostSession session, Func<T> work)
    {
        Task<T> task = session.OnEngine(work);
        Assert.True(task.Wait(Patience), "the session's thread did not answer");
        return task.Result;
    }

    /// <summary>The first answer of a query, with the query closed after it.</summary>
    private static QueryStep First(HostSession session, string query)
        => On(session, () =>
        {
            try { session.StartQuery(query); }
            catch (Exception ex) { return new QueryStep(QueryStepKind.Error, session.Describe(ex)); }
            QueryStep step = session.Next(80);
            session.EndRun();
            return step;
        });

    private static bool Holds(HostSession session, string query)
        => First(session, query).Kind is QueryStepKind.Solution or QueryStepKind.Last;

    private string Dir(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // The page hears when an initialization goal of what it consults runs,
    // and nothing for the directives around it.
    [Fact]
    public void ThePage_IsToldWhileAnInitializationGoalRuns()
    {
        var (session, page) = Open("init");
        On(session, () =>
        {
            session.TopLevel!.Consult(@"
:- initialization(write(i)).
:- write(d).
");
            return true;
        });
        Assert.Equal("di", page.Out);
        Assert.Equal(new[] { true, false }, page.Initializations);
    }

    // The page's asks answered in turn: null is an end of file, as Esc gives.
    private static void Answer(HostSession session, Page page, params string?[] lines)
    {
        var next = new Queue<string?>(lines);
        page.OnAsk = () =>
        {
            string? line = next.Count > 0 ? next.Dequeue() : null;
            if (line is null) session.Input.SupplyEndOfFile();
            else session.Input.Supply(line);
        };
    }

    // Esc is a terminal's Ctrl-D: it ends one read, and the next one asks.
    [Fact]
    public void AReadAfterAnEndOfFile_AsksThePageAgain()
    {
        var (session, page) = Open("eof-twice");
        Answer(session, page, null, "bob.");
        string text = First(session, @"read(A), read(B).").Text;
        Assert.Contains("A = end_of_file", text);
        Assert.Contains("B = bob", text);
        Assert.Equal(2, page.Asked);
    }

    // Stop lets go of a goal that waits for input, and it reads nothing more.
    [Fact]
    public void Stop_EndsTheInputOfAGoalThatReads()
    {
        var (session, page) = Open("stop-reading");
        page.OnAsk = () => session.Cancel();
        First(session, @"read(A), read(B).");
        Assert.Equal(1, page.Asked);
    }

    // A consult the page starts after a Stop reads again: its initialization
    // goal asks the page.
    [Fact]
    public void AConsultsInitializationGoal_ReadsAfterAStop()
    {
        var (session, page) = Open("eof");
        page.OnAsk = () => session.Cancel();
        First(session, @"read(X).");
        int asked = page.Asked;
        page.OnAsk = () => session.Input.Supply("bob.\n");
        On(session, () =>
        {
            session.StartConsult();
            session.TopLevel!.Consult(@"
:- initialization((read(T), write(got(T)))).
");
            return true;
        });
        Assert.Equal(asked + 1, page.Asked);
        Assert.Equal("got(bob)", page.Out);
    }

    [Fact]
    public void EachSession_HasItsOwnDatabase()
    {
        var (a, _) = Open("a");
        var (b, _) = Open("b");

        Assert.True(Holds(a, @"assertz(owner(a))."));
        Assert.True(Holds(b, @"assertz(owner(b))."));

        Assert.Contains("X = a", First(a, @"owner(X).").Text);
        Assert.Contains("X = b", First(b, @"owner(X).").Text);
        Assert.False(Holds(a, @"owner(b)."));
        Assert.False(Holds(b, @"owner(a)."));
    }

    [Fact]
    public void EachSession_HasItsOwnFlags()
    {
        var (a, _) = Open("a");
        var (b, _) = Open("b");

        Assert.True(Holds(a, @"set_prolog_flag(double_quotes, atom)."));

        Assert.True(Holds(a, @"X = ""ab"", atom(X)."));
        Assert.False(Holds(b, @"X = ""ab"", atom(X)."));
    }

    [Fact]
    public void EachSession_HasItsOwnOperators()
    {
        var (a, _) = Open("a");
        var (b, _) = Open("b");

        Assert.True(Holds(a, @"op(700, xfx, ===>)."));

        Assert.True(Holds(a, @"X = (p ===> q), X =.. [===>, p, q]."));
        Assert.Equal(QueryStepKind.Error, First(b, @"X = (p ===> q).").Kind);
    }

    [Fact]
    public async Task CancellingOneSession_LeavesTheOtherUntouched()
    {
        var (a, pageA) = Open("a");
        var (b, _) = Open("b");
        Assert.True(Holds(a, @"assertz(kept(a))."));

        // b has a query open, with solutions still to come.
        QueryStep b1 = On(b, () => { b.StartQuery(@"member(X, [1, 2, 3])."); return b.Next(80); });
        Assert.Equal(QueryStepKind.Solution, b1.Kind);
        Assert.Contains("X = 1", b1.Text);

        // a searches without end, on its own thread.
        Task<QueryStep> endless = a.OnEngine(() =>
        {
            a.StartQuery(@"write(started), repeat, fail.");
            return a.Next(80);
        });
        Assert.True(SpinWait.SpinUntil(() => pageA.Out.Contains("started"), Patience));
        // Anti-vacuity: the search really is still running.
        await Task.Delay(200);
        Assert.False(endless.IsCompleted);

        // b answers while a is searching.
        QueryStep b2 = On(b, () => b.Next(80));
        Assert.Contains("X = 2", b2.Text);

        Assert.True(a.Cancel());
        Assert.Equal(QueryStepKind.Failed, (await endless.WaitAsync(Patience)).Kind);

        // b's query is where it was, and a's engine is whole.
        Assert.True(b.HasRun);
        Assert.Contains("X = 3", On(b, () => b.Next(80)).Text);
        Assert.Contains("X = a", First(a, @"kept(X).").Text);
        Assert.False(b.Cancel());
    }

    [Fact]
    public async Task TwoSessions_ConsultTheSameProgramAtOnce()
    {
        const string template = @"
            loop_K(0).
            loop_K(N) :- N > 0, N1 is N - 1, loop_K(N1).
            app_K([], L, L).
            app_K([H|T], L, [H|R]) :- app_K(T, L, R).
            nrev_K([], []).
            nrev_K([H|T], R) :- nrev_K(T, RT), app_K(RT, [H], R).
            range_K(N, N, [N]) :- !.
            range_K(I, N, [I|T]) :- I < N, I1 is I + 1, range_K(I1, N, T).
            go_K(F, W) :- range_K(1, 20, L), nrev_K(L, [F|_]), loop_K(50), who_K(W).
            ";
        var (a, _) = Open("a");
        var (b, _) = Open("b");
        string run = Guid.NewGuid().ToString("N")[..8];

        for (int round = 0; round < 150; round++)
        {
            // Names nobody has interned yet: both sessions meet them at once.
            string k = run + "_" + round;
            string program = template.Replace("_K", "_" + k);
            Task<bool> ca = a.OnEngine(() =>
            {
                a.StartEngine();
                a.TopLevel!.Consult(program + $"who_{k}(a).");
                return true;
            });
            Task<bool> cb = b.OnEngine(() =>
            {
                b.StartEngine();
                b.TopLevel!.Consult(program + $"who_{k}(b).");
                return true;
            });
            await Task.WhenAll(ca, cb).WaitAsync(Patience);

            Task<QueryStep> qa = a.OnEngine(() => { a.StartQuery($"go_{k}(F, W)."); return a.Next(200); });
            Task<QueryStep> qb = b.OnEngine(() => { b.StartQuery($"go_{k}(F, W)."); return b.Next(200); });
            QueryStep[] both = await Task.WhenAll(qa, qb).WaitAsync(Patience);
            Assert.Contains("F = 20", both[0].Text);
            Assert.Contains("W = a", both[0].Text);
            Assert.Contains("F = 20", both[1].Text);
            Assert.Contains("W = b", both[1].Text);
        }
    }

    [Fact]
    public void Output_GoesToTheSessionsOwnPage()
    {
        var (a, pageA) = Open("a");
        var (b, pageB) = Open("b");

        Assert.True(Holds(a, @"write(from_a), write(user_error, oops_a)."));
        Assert.True(Holds(b, @"write(from_b)."));

        Assert.Contains("from_a", pageA.Out);
        Assert.DoesNotContain("from_b", pageA.Out);
        Assert.Contains("from_b", pageB.Out);
        Assert.DoesNotContain("from_a", pageB.Out);

        // user_error is the session's too, and it is marked as a diagnostic.
        Assert.Contains("oops_a", pageA.Err);
        Assert.DoesNotContain("oops_a", pageA.Out);
        Assert.Equal("", pageB.Err);
    }

    [Fact]
    public void Input_IsAskedOfTheSessionsOwnPage()
    {
        var (a, pageA) = Open("a");
        var (b, pageB) = Open("b");
        pageA.OnAsk = () => Task.Run(() => a.Input.Supply("answer(42)."));

        Assert.Contains("X = answer(42)", First(a, @"read(X).").Text);

        Assert.True(pageA.Asked > 0);
        Assert.Equal(0, pageB.Asked);
        Assert.True(Holds(b, @"true."));
    }

    [Fact]
    public void Input_IsAsked_WhenTheBootRanAQuery()
    {
        // The page's boot queries before the session wires the input:
        // installing the standard library's wasm module links the program.
        var page = new Page();
        HostSession a = _host.Create("a", page, _ =>
        {
            var engine = new PrologEngine();
            Assert.True(engine.Query("true.").Success);
            return engine;
        });
        On(a, () => { a.StartEngine(); return true; });
        page.OnAsk = () => Task.Run(() => a.Input.Supply("answer(7)."));

        Assert.Contains("X = answer(7)", First(a, @"read(X).").Text);
        Assert.True(page.Asked > 0);
    }

    [Fact]
    public void EachSession_HasItsOwnWorkingDirectory()
    {
        string dirA = Dir("alpha"), dirB = Dir("beta");
        Directory.CreateDirectory(Path.Combine(dirA, "inner"));
        var (a, _) = Open("a", dirA);
        var (b, _) = Open("b", dirB);

        Assert.Contains("alpha", First(a, @"working_directory(D, D).").Text);
        Assert.Contains("beta", First(b, @"working_directory(D, D).").Text);

        // A relative name is the session's own.
        Assert.True(Holds(a, @"open('note.txt', write, S), write(S, hello), close(S)."));
        Assert.True(File.Exists(Path.Combine(dirA, "note.txt")));
        Assert.False(File.Exists(Path.Combine(dirB, "note.txt")));

        // One session moving leaves the other where it was.
        Assert.True(Holds(a, @"working_directory(_, inner)."));
        Assert.Contains("inner", First(a, @"working_directory(D, D).").Text);
        string b2 = First(b, @"working_directory(D, D).").Text;
        Assert.Contains("beta", b2);
        Assert.DoesNotContain("inner", b2);
    }

    [Fact]
    public void ANewEngine_KeepsTheThreadAndTheDirectory_AndForgetsTheProgram()
    {
        string dir = Dir("gamma");
        Directory.CreateDirectory(Path.Combine(dir, "inner"));
        var (a, _) = Open("a", dir);
        Assert.True(Holds(a, @"assertz(seen(1)), working_directory(_, inner)."));
        int thread = On(a, () => Environment.CurrentManagedThreadId);

        On(a, () => { a.StartEngine(); return true; });

        Assert.False(Holds(a, @"catch(seen(_), _, fail)."));
        Assert.Equal(thread, On(a, () => Environment.CurrentManagedThreadId));
        // Where the host put the session, not where the old program left it.
        string where = First(a, @"working_directory(D, D).").Text;
        Assert.Contains("gamma", where);
        Assert.DoesNotContain("inner", where);
    }

    [Fact]
    public void EachSession_RunsOnOneThreadOfItsOwn()
    {
        var (a, _) = Open("a");
        var (b, _) = Open("b");

        int a1 = On(a, () => Environment.CurrentManagedThreadId);
        int a2 = On(a, () => Environment.CurrentManagedThreadId);
        int b1 = On(b, () => Environment.CurrentManagedThreadId);

        Assert.Equal(a1, a2);
        Assert.Equal(a1, a.EngineThreadId);
        Assert.NotEqual(a1, b1);
        Assert.NotEqual(Environment.CurrentManagedThreadId, a1);
    }

    [Fact]
    public async Task TheLimit_RefusesWithAMessage_AndClosingMakesRoom()
    {
        var host = new SessionHost(pageThread: null, maxSessions: 2);
        try
        {
            host.Create("a", new Page(), _ => new PrologEngine());
            host.Create("b", new Page(), _ => new PrologEngine());

            var full = Assert.Throws<InvalidOperationException>(
                () => host.Create("c", new Page(), _ => new PrologEngine()));
            Assert.Contains("limit (2)", full.Message);
            Assert.Throws<InvalidOperationException>(
                () => host.Create("a", new Page(), _ => new PrologEngine()));
            Assert.Equal(2, host.Count);

            await host.Close("a").WaitAsync(Patience);
            Assert.Null(host.Find("a"));
            host.Create("c", new Page(), _ => new PrologEngine());
            Assert.Equal(2, host.Count);
        }
        finally
        {
            await host.CloseAll().WaitAsync(Patience);
        }
    }

    [Fact]
    public async Task Closing_LetsGoOfAGoalWaitingForInput()
    {
        var (a, pageA) = Open("a");
        // The page never answers.
        Task<QueryStep> waiting = a.OnEngine(() =>
        {
            a.StartQuery(@"read(X).");
            return a.Next(80);
        });
        Assert.True(SpinWait.SpinUntil(() => pageA.Asked > 0, Patience));
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);

        await _host.Close("a").WaitAsync(Patience);

        Assert.True(waiting.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => a.OnEngine(() => true).WaitAsync(Patience));
    }
}
