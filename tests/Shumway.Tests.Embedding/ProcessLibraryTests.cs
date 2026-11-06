using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>library(process), running real programs of the system: the shell on either one,
/// and Prolog is told which through sys_shell/2 and sleeper/1.</summary>
public sealed class ProcessLibraryTests : IDisposable
{
    private static readonly bool Windows = Environment.OSVersion.Platform == PlatformID.Win32NT;

    private static string Helpers => @"
:- use_module(library(process)).
:- use_module(library(sockets)).

caught(Goal, Error) :- catch((Goal, fail), error(Error, _), true).

" + (Windows
        ? @"
sys_shell(Command, [path('cmd.exe'), ['/d', '/c', Command]]).
sleeper([path('ping.exe'), ['-n', '30', '127.0.0.1']]).
"
        : @"
sys_shell(Command, [path(sh), ['-c', Command]]).
sleeper([path(sleep), ['30']]).
") + @"
run(Command, Options) :- sys_shell(Command, [Exe, Args]), process_create(Exe, Args, Options).

all_codes(S, Cs) :-
    get_code(S, C),
    ( C =:= -1 -> Cs = [] ; Cs = [C|T], all_codes(S, T) ).

text_of(S, Atom) :- all_codes(S, Cs), atom_codes(Atom, Cs).
";

    private readonly string _dir = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "shumway-process-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        for (int i = 0; i < 20; i++)
        {
            try { Directory.Delete(_dir, recursive: true); return; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    private string P(string name) => Path.Combine(_dir, name).Replace('\\', '/');

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(Helpers);
        return e;
    }

    private static T WithinSeconds<T>(int seconds, Func<T> work)
    {
        var task = OwnThread.Run(work);
        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), "it hung");
        return task.Result;
    }

    private static Solution Q(PrologEngine e, string goal) => WithinSeconds(60, () => e.Query(goal));

    private static string CatRaw => Windows ? "type raw.bin" : "cat raw.bin";

    private static string Print(string text) => "echo " + text;

    // ---------- the common case

    [Fact]
    public void AProgram_RunsAndItsOutputIsRead()
    {
        var sol = Q(Engine(), $@"
            run('{Print("hello.")}', [stdout(pipe(Out)), process(P)]),
            read_term(Out, T, []),
            close(Out),
            process_wait(P, Status).");
        Assert.True(sol.Success);
        Assert.Equal("hello", ((AtomTerm)sol["T"]!).Name);
        Assert.Equal("exit(0)", sol["Status"]!.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(137)]
    public void AnExitCode_ComesBack(int code)
    {
        var sol = Q(Engine(), $"run('exit {code}', [process(P)]), process_wait(P, exit(C)).");
        Assert.True(sol.Success);
        Assert.Equal((long)code, ((IntTerm)sol["C"]!).Value);
    }

    [Fact]
    public void WaitOption_WaitsAndGivesTheStatus()
    {
        var sol = Q(Engine(), "run('exit 4', [wait(S)]).");
        Assert.Equal("exit(4)", sol["S"]!.ToString());
    }

    [Fact]
    public void WithoutAReference_ItGoesOnAndAsksNothing()
    {
        Assert.True(Q(Engine(), "run('exit 3', [stdout(null)]).").Success);
    }

    [Fact]
    public void ProcessCreate2_RunsAProgram()
    {
        string marker = P("marker.txt");
        var e = Engine();
        Assert.True(Q(e, Windows
            ? $"sys_shell('echo x> {marker}', [Exe, Args]), process_create(Exe, Args)."
            : $"sys_shell('echo x > {marker}', [Exe, Args]), process_create(Exe, Args).").Success);
        Assert.True(SpinUntil(() => File.Exists(marker)));
    }

    private static bool SpinUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    // ---------- the three streams

    [Fact]
    public void Stdin_ReachesTheProgram_AndClosingEndsItsInput()
    {
        var e = Engine();
        string cat = Windows ? "sort" : "cat";
        var sol = Q(e, $@"
            run('{cat}', [stdin(pipe(In)), stdout(pipe(Out)), process(P)]),
            write(In, 'ok.'), nl(In), close(In),
            read_term(Out, T, []),
            close(Out),
            process_wait(P, exit(0)).");
        Assert.True(sol.Success);
        Assert.Equal("ok", ((AtomTerm)sol["T"]!).Name);
    }

    [Fact]
    public void Stderr_IsItsOwnStream()
    {
        var sol = Q(Engine(), $@"
            run('{Print("to_err.")} 1>&2', [stdout(pipe(Out)), stderr(pipe(Err)), process(P)]),
            read_term(Err, T, []),
            text_of(Out, ''),
            close(Out), close(Err),
            process_wait(P, _).");
        Assert.True(sol.Success, "stderr");
        Assert.Equal("to_err", ((AtomTerm)sol["T"]!).Name);
    }

    [Fact]
    public void AStream_ReportsItsDirection()
    {
        Assert.True(Q(Engine(), @"
            run('exit 0', [stdin(pipe(In)), stdout(pipe(Out)), process(P)]),
            stream_property(In, output), \+ stream_property(In, input),
            stream_property(Out, input), \+ stream_property(Out, output),
            stream_property(In, mode(write)), stream_property(Out, mode(read)),
            stream_property(Out, reposition(false)),
            stream_property(Out, end_of_stream(not)),
            close(In), close(Out),
            process_wait(P, _).").Success);
    }

    [Fact]
    public void Null_DiscardsAndReadsAsEmpty()
    {
        string cat = Windows ? "sort" : "cat";
        Assert.True(Q(Engine(), $@"
            run('{Print("x")}', [stdout(null), stderr(null), process(P)]),
            process_wait(P, exit(0)),
            run('{cat}', [stdin(null), stdout(pipe(Out)), process(P2)]),
            text_of(Out, ''),
            close(Out),
            process_wait(P2, _).").Success);
    }

    [Fact]
    public void APriority_ForAChildAlreadyEnded_IsNoError()
    {
        var start = new ProcessStartInfo(Windows ? "cmd.exe" : "sh", Windows ? "/c exit 0" : "-c \"exit 0\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process child = Process.Start(start)!;
        Assert.True(child.WaitForExit(30000));
        MetaBuiltins.SetPriority(child, 5);
    }

    [Fact]
    public void Null_DoesNotStallAProgramThatWritesALot()
    {
        // More than a pipe holds, on each system: cmd writes a line at a time, and on a
        // busy machine can take a minute over a few thousand.
        string loop = Windows ? "for /L %i in (1,1,500) do @echo 0123456789012345678901234567890123456789" : "i=0; while [ $i -lt 3000 ]; do echo 0123456789012345678901234567890123456789; i=$((i+1)); done";
        var e = Engine();
        var started = Q(e, $"run('{loop}', [stdin(null), stdout(null), stderr(null), process(P)]), process_id(P, Pid).");
        long process = ((IntTerm)((CompoundTerm)started["P"]!).Args[0]).Value;
        var clock = Stopwatch.StartNew();
        string status = Q(e, $"process_wait('$process'({process}), Status, [timeout(45)]).")["Status"]!.ToString()!;
        if (status == "timeout")
        {
            string cpu = CpuOverTwoSeconds((int)((IntTerm)started["Pid"]!).Value);
            Q(e, $"process_group_kill('$process'({process})), process_wait('$process'({process}), _).");
            Assert.Fail($"the child had not ended after {clock.Elapsed.TotalSeconds:N0} s, and used {cpu}");
        }
        Assert.Equal("exit(0)", status);
    }

    /// <summary>Tells a child that is slow from one that is blocked.</summary>
    private static string CpuOverTwoSeconds(int pid)
    {
        using Process child = Process.GetProcessById(pid);
        TimeSpan before = child.TotalProcessorTime;
        Thread.Sleep(2000);
        child.Refresh();
        return $"{(child.TotalProcessorTime - before).TotalMilliseconds:N0} ms of CPU in its last 2 s";
    }

    [Fact]
    public void File_TakesTheOutput_AndGivesTheInput()
    {
        string cat = Windows ? "sort" : "cat";
        File.WriteAllText(P("in.txt"), "line.\n");
        var e = Engine();
        Assert.True(Q(e, $@"
            run('{cat}', [stdin(file('{P("in.txt")}')), stdout(file('{P("out.txt")}')), process(P)]),
            process_wait(P, exit(0)).").Success);
        Assert.True(SpinUntil(() => File.Exists(P("out.txt")) && new FileInfo(P("out.txt")).Length > 0));
        Assert.StartsWith("line.", File.ReadAllText(P("out.txt")));
    }

    // ---------- stream(S)

    [Fact]
    public void TwoPrograms_AreJoined()
    {
        string cat = Windows ? "sort" : "cat";
        var sol = Q(Engine(), $@"
            run('{Print("joined.")}', [stdout(pipe(A)), process(P1)]),
            run('{cat}', [stdin(stream(A)), stdout(pipe(B)), process(P2)]),
            close(A),
            read_term(B, T, []),
            close(B),
            process_wait(P1, exit(0)), process_wait(P2, exit(0)).");
        Assert.True(sol.Success);
        Assert.Equal("joined", ((AtomTerm)sol["T"]!).Name);
    }

    [Fact]
    public void TheOutput_GoesToAFileTheProgramAlsoWrites_InOrder()
    {
        var e = Engine();
        Assert.True(Q(e, $@"
            open('{P("log.txt")}', write, S),
            write(S, before), nl(S),
            run('{Print("child")}', [stdout(stream(S)), process(C)]),
            process_wait(C, exit(0)),
            write(S, after), nl(S),
            close(S).").Success);
        string[] lines = File.ReadAllLines(P("log.txt")).Select(l => l.Trim()).ToArray();
        Assert.Equal(new[] { "before", "child", "after" }, lines);
    }

    [Fact]
    public void BothOutputs_GoToOneFile()
    {
        Assert.True(Q(Engine(), $@"
            open('{P("log.txt")}', write, S),
            run('{Print("out")} & {Print("err")} 1>&2', [stdout(stream(S)), stderr(stream(S)), process(C)]),
            process_wait(C, _),
            close(S).").Success);
        string text = File.ReadAllText(P("log.txt"));
        Assert.Contains("out", text);
        Assert.Contains("err", text);
    }

    [Fact]
    public void AFileAlreadyOpen_IsTheInput()
    {
        File.WriteAllText(P("in.txt"), "line.\n");
        string cat = Windows ? "sort" : "cat";
        var sol = Q(Engine(), $@"
            open('{P("in.txt")}', read, I),
            run('{cat}', [stdin(stream(I)), stdout(pipe(O)), process(C)]),
            read_term(O, T, []),
            close(O), close(I),
            process_wait(C, _).");
        Assert.True(sol.Success);
        Assert.Equal("line", ((AtomTerm)sol["T"]!).Name);
    }

    [Fact]
    public void TheInput_StartsWithWhatWasReadAhead_AfterThePartAlreadyRead()
    {
        File.WriteAllText(P("in.txt"), "xrest.\n");
        string cat = Windows ? "sort" : "cat";
        var sol = Q(Engine(), $@"
            open('{P("in.txt")}', read, I),
            get_char(I, x),
            run('{cat}', [stdin(stream(I)), stdout(pipe(O)), process(C)]),
            read_term(O, T, []),
            close(O), close(I),
            process_wait(C, _).");
        Assert.Equal("rest", ((AtomTerm)sol["T"]!).Name);
    }

    [Fact]
    public void AChild_GetsTheByteAPeekLeft()
    {
        string cat = Windows ? "sort" : "cat";
        var sol = Q(Engine(), $@"
            run('{Print("abc.")}', [stdout(pipe(O1, [type(binary)])), process(P1)]),
            peek_byte(O1, 97),
            run('{cat}', [stdin(stream(O1)), stdout(pipe(O2)), process(P2)]),
            read_term(O2, T, []),
            close(O2), close(O1),
            process_wait(P1, _), process_wait(P2, _).");
        Assert.Equal("abc", ((AtomTerm)sol["T"]!).Name);
    }

    [Fact]
    public void AChild_GetsWhatASelectLeftWaiting()
    {
        string cat = Windows ? "sort" : "cat";
        var sol = Q(Engine(), $@"
            run('{Print("abc.")}', [stdout(pipe(O1, [type(binary)])), process(P1)]),
            socket_select([], [], [O1], [O1], [], [], off),
            run('{cat}', [stdin(stream(O1)), stdout(pipe(O2)), process(P2)]),
            read_term(O2, T, []),
            close(O2), close(O1),
            process_wait(P1, _), process_wait(P2, _).");
        Assert.Equal("abc", ((AtomTerm)sol["T"]!).Name);
    }

    [Fact]
    public void AFileOutput_DoesNotDependOnThePool()
    {
        var sol = WithThePoolBusy(Engine(), $@"
            run('{Print("hello.")}', [stdout(file('{P("out.txt")}')), process(C)]),
            process_wait(C, exit(0)).");
        Assert.True(sol.Success);
        Assert.StartsWith("hello.", File.ReadAllText(P("out.txt")));
    }

    [Fact]
    public void APipe_DoesNotDependOnThePool()
    {
        string cat = Windows ? "sort" : "cat";
        var sol = WithThePoolBusy(Engine(), $@"
            run('{cat}', [stdin(pipe(I)), stdout(pipe(O)), process(C)]),
            write(I, 'hello.'), nl(I),
            close(I),
            read_term(O, T, []),
            close(O),
            process_wait(C, exit(0)).");
        Assert.True(sol.Success);
        Assert.Equal("hello", ((AtomTerm)sol["T"]!).Name);
    }

    [Theory]
    [InlineData("[type(binary), buffer_size(0)]", 0, "put_byte(I, 97), put_byte(I, 46), put_byte(I, 10)")]
    [InlineData("[buffer_size(2)]", 2, "put_char(I, a), put_char(I, '.'), nl(I)")]
    public void APipe_TakesABufferSize(string options, int size, string put)
    {
        string cat = Windows ? "sort" : "cat";
        var sol = Q(Engine(), $@"
            run('{cat}', [stdin(pipe(I, {options})), stdout(pipe(O)), process(C)]),
            stream_property(I, buffer_size({size})),
            {put},
            close(I),
            read_term(O, T, []),
            close(O),
            process_wait(C, exit(0)).");
        Assert.Equal("a", ((AtomTerm)sol["T"]!).Name);
    }

    /// <summary>Runs a query with every thread the pool may have taken, on a thread of its own.</summary>
    private static Solution WithThePoolBusy(PrologEngine e, string goal)
    {
        Solution? sol = null;
        var query = new Thread(() => sol = e.Query(goal));
        ThreadPool.GetMinThreads(out int workers, out _);
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        // Not disposed: what is still queued when the test ends waits on it later.
        var release = new ManualResetEventSlim();
        Assert.True(ThreadPool.SetMaxThreads(workers, maxIo));
        try
        {
            for (int i = 0; i < workers; i++) ThreadPool.QueueUserWorkItem(_ => release.Wait());
            query.Start();
            Assert.True(query.Join(TimeSpan.FromSeconds(30)), "it hung");
            return sol!;
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
        }
    }

    // A long limit: on a busy runner cmd can take a long time to get to what it writes.
    private const string SocketPair = @"
            socket_server_open('127.0.0.1':Port, Srv),
            socket_client_open('127.0.0.1':Port, C, [type(binary), timeout(45)]),
            socket_server_accept(Srv, _, A, [type(binary), timeout(45)]),
            socket_server_close(Srv),";

    private static string Pause => Windows ? "ping -n 2 127.0.0.1 >nul" : "sleep 1";

    [Fact]
    public void WhatTheProgramHeld_GoesBeforeWhatAChildSends()
    {
        string then = Windows ? "& echo x" : "; echo x";
        Assert.True(Q(Engine(), $@"
            {SocketPair}
            run('{Pause} {then}', [stdin(null), stdout(stream(C)), stderr(null), process(P)]),
            put_byte(C, 97),
            get_byte(A, 97),
            get_byte(A, 120),
            process_wait(P, exit(0)),
            close(C), close(A).").Success);
    }

    [Fact]
    public void ClosingAByteStream_ThatAChildStillWrites_SendsWhatItHeld()
    {
        // Read while the child lives: the stream closes for real only when it ends.
        string longer = Windows ? "ping -n 30 127.0.0.1 >nul" : "sleep 30";
        Assert.True(Q(Engine(), $@"
            {SocketPair}
            run('{longer}', [stdin(null), stdout(stream(C)), stderr(null), process(P)]),
            put_byte(C, 97),
            close(C),
            get_byte(A, 97),
            process_group_kill(P),
            process_wait(P, _),
            close(A).").Success);
    }

    private sealed class SlowSink : Stream
    {
        public volatile int Received;
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c)
        {
            Thread.Sleep(500);
            Received += c;
        }
    }

    [Fact]
    public void AWait_WaitsForTheOutputOnItsWayToAStream()
    {
        var e = Engine();
        var sink = new SlowSink();
        StreamHandle h = StreamBuiltins.OpenLiveStream(e.Streams, sink, "write", binary: true,
            null, alias: null, null, translateNewlines: false);
        Assert.True(Q(e, $@"
            run('{Print("hello")}', [stdout(stream('$stream'({h.Id}))), process(P)]),
            process_wait(P, exit(0)).").Success);
        Assert.True(sink.Received > 0);
    }

    [Fact]
    public void AnOutput_MayGoToABinaryFile()
    {
        Assert.True(Q(Engine(), $@"
            open('{P("log.bin")}', write, S, [type(binary)]),
            put_byte(S, 0x41),
            run('{Print("x")}', [stdout(stream(S)), process(C)]),
            process_wait(C, exit(0)),
            close(S).").Success);
        byte[] bytes = File.ReadAllBytes(P("log.bin"));
        Assert.Equal(0x41, bytes[0]);
        Assert.Equal((byte)'x', bytes[1]);
    }

    [Fact]
    public void Closing_TheStreamAfterStarting_LosesNothing()
    {
        Assert.True(Q(Engine(), $@"
            open('{P("log.txt")}', write, S),
            write(S, before), nl(S),
            run('{Print("child")}', [stdout(stream(S)), process(C)]),
            close(S),
            \+ is_stream(S),
            process_wait(C, exit(0)).").Success);
        Assert.True(SpinUntil(() => File.ReadAllText(P("log.txt")).Contains("child")));
        Assert.StartsWith("before", File.ReadAllText(P("log.txt")));
    }

    [Fact]
    public void TheOutput_GoesToASocket()
    {
        Assert.True(Q(Engine(), $@"
            socket_server_open('127.0.0.1':Port, Srv),
            socket_client_open('127.0.0.1':Port, C, []),
            socket_server_accept(Srv, _, A, [type(text)]),
            run('{Print("sent.")}', [stdout(stream(C)), process(P)]),
            process_wait(P, exit(0)),
            read_term(A, sent, []),
            close(C), close(A), socket_server_close(Srv).").Success);
    }

    [Fact]
    public void TheInput_ComesFromASocket()
    {
        string cat = Windows ? "sort" : "cat";
        var sol = Q(Engine(), $@"
            socket_server_open('127.0.0.1':Port, Srv),
            socket_client_open('127.0.0.1':Port, C, []),
            socket_server_accept(Srv, _, A, []),
            run('{cat}', [stdin(stream(A)), stdout(pipe(O)), process(P)]),
            put_byte(C, 0x6F), put_byte(C, 0x6B), put_byte(C, 0x2E), put_byte(C, 0x0A),
            close(C),
            read_term(O, T, []),
            close(O), close(A), socket_server_close(Srv),
            process_wait(P, _).");
        Assert.True(sol.Success);
        Assert.Equal("ok", ((AtomTerm)sol["T"]!).Name);
    }

    [Fact]
    public void UserError_TakesAnOutput()
    {
        Assert.True(Q(Engine(), @"
            run('exit 0', [stdout(stream(user_error)), process(P)]),
            process_wait(P, exit(0)).").Success);
    }

    [Fact]
    public void AHostWriter_HasNoBytes_ForAChild()
    {
        var e = Engine();
        e.Out = new StringWriter();
        Assert.True(Q(e, @"
            sys_shell(x, [E, A]),
            caught(process_create(E, A, [stdout(stream(user_output))]), domain_error(file_stream, user_output)).").Success);
    }

    [Theory]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [stdin(pipe(_, [buffer_size(-1)]))]), domain_error(process_stream_option, buffer_size(-1)))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [stdin(pipe(_, [buffer_size(a)]))]), domain_error(process_stream_option, buffer_size(a)))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [stdout(stream(_))]), instantiation_error)")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [stdout(stream(user_input))]), permission_error(output, stream, user_input))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [stdin(stream(user_output))]), permission_error(input, stream, user_output))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [stdout(stream(no_such_alias))]), existence_error(stream, no_such_alias))")]
    public void ABadStream_IsAnError(string goal)
    {
        Assert.True(Q(Engine(), goal + ".").Success, goal);
    }

    [Fact]
    public void AFileOutput_IsThereWhenTheWaitReturns()
    {
        Assert.True(Q(Engine(), $@"
            run('{Print("now")}', [stdout(file('{P("out.txt")}')), process(C)]),
            process_wait(C, exit(0)).").Success);
        Assert.StartsWith("now", File.ReadAllText(P("out.txt")));
    }

    // ---------- what a pipe's text is

    [Fact]
    public void Text_NotInTheEncoding_NeverRaises()
    {
        var bytes = new byte[] { 0x61, 0x82, 0x62, 0x0A };
        File.WriteAllBytes(P("raw.bin"), bytes);
        Assert.True(Q(Engine(), $@"
            run('{CatRaw}', [cwd('{P("")}'), stdout(pipe(Out)), process(P)]),
            all_codes(Out, Cs),
            close(Out),
            process_wait(P, _),
            Cs = [97, 0xFFFD, 98|_].").Success);
    }

    [Fact]
    public void Text_InANamedEncoding_RaisesWhatDoesNotFit()
    {
        File.WriteAllBytes(P("raw.bin"), new byte[] { 0x82, 0x62 });
        Assert.True(Q(Engine(), $@"
            run('{CatRaw}', [cwd('{P("")}'), stdout(pipe(Out, [encoding('UTF-8')])), process(P)]),
            stream_property(Out, encoding('UTF-8')),
            caught(get_code(Out, _), representation_error(character)),
            get_code(Out, 0x62),
            close(Out),
            process_wait(P, _).").Success);
    }

    [Fact]
    public void ABinaryPipe_GivesTheBytes()
    {
        File.WriteAllBytes(P("raw.bin"), new byte[] { 0x82, 0x0D, 0x0A });
        Assert.True(Q(Engine(), $@"
            run('{CatRaw}', [cwd('{P("")}'), stdout(pipe(Out, [type(binary)])), process(P)]),
            stream_property(Out, type(binary)),
            peek_byte(Out, 0x82), get_byte(Out, 0x82), get_byte(Out, 0x0D), get_byte(Out, 0x0A),
            at_end_of_stream(Out),
            close(Out),
            process_wait(P, _).").Success);
    }

    [Fact]
    public void APipe_TakesAnAliasAndAnEofAction()
    {
        Assert.True(Q(Engine(), @"
            run('exit 0', [stdout(pipe(Out, [alias(kid), eof_action(eof_code)])), process(P)]),
            stream_property(Out, alias(kid)),
            get_code(kid, -1), get_code(kid, -1),
            close(kid),
            process_wait(P, _).").Success);
    }

    [Fact]
    public void ReadingAPipe_WaitsAndCanBeCancelled()
    {
        var e = Engine();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var ex = Assert.ThrowsAny<AggregateException>(() => WithinSeconds(30, () => e.QueryAll(@"
            sleeper([Exe, Args]),
            process_create(Exe, Args, [stdin(pipe(_)), stdout(null), stderr(pipe(Err))]),
            get_char(Err, _).", cts.Token).ToList()));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public void Select_TakesAPipe()
    {
        Assert.True(Q(Engine(), $@"
            run('{Print("x")}', [stdout(pipe(Out)), process(P)]),
            socket_select([], [], [Out], [Out], [], [], off),
            get_char(Out, x),
            text_of(Out, _),
            close(Out),
            process_wait(P, _).").Success);
    }

    // ---------- arguments and options

    [Theory]
    [InlineData(@"a b", @"'a b'")]
    [InlineData(@"quote""d", @"'quote""d'")]
    [InlineData(@"é", @"'é'")]
    [InlineData(@"12", @"12")]
    [InlineData(@"abc", @"[a, b, c]")]
    [InlineData(@"abc", @"[0x61, 0x62, 0x63]")]
    [InlineData(@"a1", @"[a, 1]")]
    public void AnArgument_ArrivesWhole(string expected, string argument)
    {
        // A child that reports its first argument, without any shell in the way.
        string script = P("args.py");
        File.WriteAllText(script, "import sys\nsys.stdout.buffer.write(sys.argv[1].encode('utf-8'))\n");
        string python = Windows ? "python" : "python3";
        var sol = Q(Engine(), $@"
            process_create(path({python}), ['{script}', {argument}], [stdout(pipe(Out, [encoding('UTF-8')])), process(P)]),
            text_of(Out, Got),
            close(Out),
            process_wait(P, exit(0)).");
        Assert.True(sol.Success, argument);
        Assert.Equal(expected, ((AtomTerm)sol["Got"]!).Name);
    }

    [Fact]
    public void AFileArgument_IsInTheSystemsForm()
    {
        string script = P("args.py");
        File.WriteAllText(script, "import sys\nsys.stdout.buffer.write(sys.argv[1].encode('utf-8'))\n");
        string python = Windows ? "python" : "python3";
        var sol = Q(Engine(), $@"
            process_create(path({python}), ['{script}', file('a/b')], [stdout(pipe(Out)), process(P)]),
            text_of(Out, Got),
            close(Out),
            process_wait(P, _).");
        Assert.Equal("a" + Path.DirectorySeparatorChar + "b", ((AtomTerm)sol["Got"]!).Name);
    }

    [Fact]
    public void Cwd_IsWhereItRuns()
    {
        string pwd = Windows ? "cd" : "pwd";
        var sol = Q(Engine(), $@"
            run({pwd}, [cwd('{P("")}'), stdout(pipe(Out)), process(P)]),
            text_of(Out, Got),
            close(Out),
            process_wait(P, _).");
        string got = ((AtomTerm)sol["Got"]!).Name.Trim().Replace('\\', '/').TrimEnd('/');
        Assert.EndsWith(Path.GetFileName(_dir), got);
    }

    [Theory]
    [InlineData(@"environment(['SHUM_X'=bar])", "bar")]
    public void Environment_AddsAVariable(string option, string expected)
    {
        string echo = Windows ? "echo %SHUM_X%" : "echo $SHUM_X";
        var sol = Q(Engine(), $@"
            run('{echo}', [{option}, stdout(pipe(Out)), process(P)]),
            text_of(Out, Got),
            close(Out),
            process_wait(P, _).");
        Assert.Equal(expected, ((AtomTerm)sol["Got"]!).Name.Trim());
    }

    [Fact]
    public void Env_ReplacesTheEnvironment()
    {
        string echo = Windows ? "if defined PATH (echo has) else echo none" : "if [ -n \"$HOME\" ]; then echo has; else echo none; fi";
        string shell = Windows ? "C:/Windows/System32/cmd.exe" : "/bin/sh";
        string flag = Windows ? "/c" : "-c";
        var sol = Q(Engine(), $@"
            process_create('{shell}', ['{flag}', '{echo}'], [env(['SHUM_X'=1]), stdout(pipe(Out)), process(P)]),
            text_of(Out, Got),
            close(Out),
            process_wait(P, _).");
        Assert.Equal("none", ((AtomTerm)sol["Got"]!).Name.Trim());
    }

    [Theory]
    [InlineData(@"detached(true)")]
    [InlineData(@"detached(false)")]
    [InlineData(@"window(false)")]
    [InlineData(@"priority(5)")]
    [InlineData(@"priority(0)")]
    public void AnOption_IsTaken(string option)
    {
        Assert.True(Q(Engine(), $"run('exit 0', [{option}, process(P)]), process_wait(P, exit(0)).").Success, option);
    }

    // ---------- finding the program

    [Fact]
    public void AnAbsoluteFile_Runs()
    {
        string file = Windows ? "C:/Windows/System32/cmd.exe" : "/bin/sh";
        string flag = Windows ? "/c" : "-c";
        Assert.True(Q(Engine(), $"process_create('{file}', ['{flag}', 'exit 0'], [wait(exit(0))]).").Success);
    }

    [Fact]
    public void ABareName_IsLookedUpOnThePath()
    {
        string name = Windows ? "cmd.exe" : "sh";
        string flag = Windows ? "/c" : "-c";
        Assert.True(Q(Engine(), $"process_create('{name}', ['{flag}', 'exit 0'], [wait(exit(0))]).").Success);
    }

    [Fact]
    public void Which_TriesTheExtensionsOfThePath()
    {
        if (!Windows) return;
        Assert.True(Q(Engine(), "process_which(more, _).").Success);
    }

    [Fact]
    public void Which_FindsAProgram_AndFailsForNone()
    {
        string name = Windows ? "cmd" : "sh";
        var e = Engine();
        var sol = Q(e, $"process_which(path({name}), File).");
        Assert.True(sol.Success);
        Assert.True(File.Exists(((AtomTerm)sol["File"]!).Name));
        Assert.False(Q(e, "process_which(path(no_such_program_x), _).").Success);
    }

    [Theory]
    [InlineData(@"path(no_such_program_x)")]
    [InlineData(@"no_such_program_x")]
    [InlineData(@"'C:/no/such/program.exe'")]
    [InlineData(@"'/no/such/program'")]
    public void AMissingProgram_IsAnExistenceError(string exe)
    {
        Assert.True(Q(Engine(), $"caught(process_create({exe}, [], []), existence_error(source_sink, {exe})).").Success, exe);
    }

    // ---------- waiting and ending

    [Fact]
    public void ATimeout_GivesTimeout_AndTheProcessIsStillThere()
    {
        Assert.True(Q(Engine(), @"
            sleeper([Exe, Args]),
            process_create(Exe, Args, [stdout(null), process(P)]),
            process_wait(P, timeout, [timeout(0)]),
            process_wait(P, timeout, [timeout(0.1)]),
            is_process(P),
            process_group_kill(P),
            process_wait(P, _),
            \+ is_process(P).").Success);
    }

    [Fact]
    public void AWaitThatWaits_IsCancelled()
    {
        var e = Engine();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var ex = Assert.ThrowsAny<AggregateException>(() => WithinSeconds(30, () => e.QueryAll(@"
            sleeper([Exe, Args]),
            process_create(Exe, Args, [stdout(null), process(P)]),
            process_wait(P, _).", cts.Token).ToList()));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public void AWaitThatRunsOutOfTime_GivesTimeOut()
    {
        var sol = Q(Engine(), @"
            sleeper([Exe, Args]),
            process_create(Exe, Args, [stdout(null), process(P)]),
            time_out(process_wait(P, _), 200, R),
            process_group_kill(P),
            process_wait(P, _).");
        Assert.Equal("time_out", ((AtomTerm)sol["R"]!).Name);
    }

    [Theory]
    [InlineData(@"process_kill(P)", 9)]
    [InlineData(@"process_kill(P, 'SIGKILL')", 9)]
    [InlineData(@"process_kill(P, 9)", 9)]
    [InlineData(@"process_kill(P, kill)", 9)]
    [InlineData(@"process_kill(P, 'SIGTERM')", 15)]
    [InlineData(@"process_kill(P, 15)", 15)]
    [InlineData(@"process_kill(P, term)", 15)]
    public void AKill_IsReportedAsKilled(string kill, int signal)
    {
        var sol = Q(Engine(), $@"
            sleeper([Exe, Args]),
            process_create(Exe, Args, [stdout(null), process(P)]),
            {kill},
            process_wait(P, Status).");
        Assert.True(sol.Success, kill);
        Assert.Equal($"killed({signal})", sol["Status"]!.ToString());
    }

    [Fact]
    public void AProgramThatExitsWith137_IsNotKilled()
    {
        Assert.True(Q(Engine(), "run('exit 137', [process(P)]), process_wait(P, exit(137)).").Success);
    }

    [Fact]
    public void KillingAProcessThatEnded_IsNoError()
    {
        Assert.True(Q(Engine(), @"
            run('exit 5', [process(P)]),
            process_wait(P, exit(5), [release(false)]),
            process_kill(P), process_kill(P, 'SIGTERM'),
            process_wait(P, exit(5)).").Success);
    }

    [Fact]
    public void ATreeKill_EndsWhatItStarted()
    {
        var e = Engine();
        // What it started holds the pipe, so the read below ends only with that gone too.
        string command = Windows ? "ping -n 30 127.0.0.1" : "sleep 30; echo x";
        string started = Windows ? "peek_code(Out, _)," : "";
        var clock = Stopwatch.StartNew();
        var sol = Q(e, $@"
            run('{command}', [stdin(null), stdout(pipe(Out)), process(P)]),
            {started}
            process_group_kill(P),
            process_wait(P, Status),
            text_of(Out, _),
            close(Out).");
        Assert.True(sol.Success);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "what it started kept running");
        Assert.Equal("killed(9)", sol["Status"]!.ToString());
    }

    [Theory]
    [InlineData(@"process_group_kill(P, 'SIGKILL')")]
    [InlineData(@"process_group_kill(P, 9)")]
    public void ATreeKill_TakesSigkill(string kill)
    {
        Assert.True(Q(Engine(), $@"
            sleeper([Exe, Args]),
            process_create(Exe, Args, [stdout(null), process(P)]),
            {kill},
            process_wait(P, killed(9)).").Success);
    }

    [Fact]
    public void AReleasedProcess_IsAnError_AndGoesOn()
    {
        Assert.True(Q(Engine(), @"
            run('exit 0', [process(P)]),
            process_release(P),
            \+ is_process(P),
            caught(process_wait(P, _), existence_error(process, _)),
            caught(process_kill(P), existence_error(process, _)),
            caught(process_release(P), existence_error(process, _)),
            caught(process_id(P, _), existence_error(process, _)).").Success);
    }

    [Fact]
    public void AWaitedProcess_IsReleased()
    {
        Assert.True(Q(Engine(), @"
            run('exit 0', [process(P)]),
            process_wait(P, _),
            caught(process_wait(P, _), existence_error(process, _)).").Success);
    }

    [Fact]
    public void ReleaseFalse_KeepsIt()
    {
        Assert.True(Q(Engine(), @"
            run('exit 2', [process(P)]),
            process_wait(P, exit(2), [release(false)]),
            is_process(P),
            process_wait(P, exit(2)).").Success);
    }

    [Fact]
    public void Ids_AreTheSystems()
    {
        var sol = Q(Engine(), @"
            process_id(Me),
            run('exit 0', [process(P)]),
            process_id(P, Child),
            process_wait(P, _).");
        using var current = Process.GetCurrentProcess();
        Assert.Equal((long)current.Id, ((IntTerm)sol["Me"]!).Value);
        Assert.True(((IntTerm)sol["Child"]!).Value > 0);
    }

    [Fact]
    public void AReference_IsAGroundTerm_NotAPid()
    {
        Assert.True(Q(Engine(), @"
            run('exit 0', [process(P)]),
            ground(P), compound(P),
            process_wait(P, _).").Success);
    }

    // ---------- bad arguments

    [Theory]
    [InlineData(@"caught(process_create(_, [], []), instantiation_error)")]
    [InlineData(@"caught(process_create(path(_), [], []), instantiation_error)")]
    [InlineData(@"caught(process_create(f(x), [], []), domain_error(process_executable, f(x)))")]
    [InlineData(@"sys_shell(x, [E, _]), caught(process_create(E, _, []), instantiation_error)")]
    [InlineData(@"sys_shell(x, [E, _]), caught(process_create(E, foo, []), type_error(list, foo))")]
    [InlineData(@"sys_shell(x, [E, _]), caught(process_create(E, [_], []), instantiation_error)")]
    [InlineData(@"sys_shell(x, [E, _]), caught(process_create(E, [f(x)], []), domain_error(process_argument, f(x)))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, foo), type_error(list, foo))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [_]), instantiation_error)")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [nosuch(1)]), domain_error(process_create_option, nosuch(1)))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [stdout(other)]), domain_error(process_stream, other))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [stdout(_)]), instantiation_error)")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [stdout(pipe(foo))]), uninstantiation_error(pipe(foo)))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [stdout(pipe(_, [nosuch]))]), domain_error(process_stream_option, nosuch))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [cwd('/no/such/dir')]), existence_error(directory, '/no/such/dir'))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [env(foo)]), type_error(list, env(foo)))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [environment([foo])]), domain_error(process_create_option, environment([foo])))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [detached(maybe)]), domain_error(process_create_option, detached(maybe)))")]
    [InlineData(@"sys_shell(x, [E, A]), caught(process_create(E, A, [priority(99)]), domain_error(process_create_option, priority(99)))")]
    [InlineData(@"caught(process_wait(_, _), instantiation_error)")]
    [InlineData(@"caught(process_wait(foo, _), domain_error(process, foo))")]
    [InlineData(@"caught(process_wait(123, _), domain_error(process, 123))")]
    [InlineData(@"caught(process_wait('$process'(999), _), existence_error(process, _))")]
    [InlineData(@"run('exit 0', [process(P)]), caught(process_wait(P, _, [nosuch]), domain_error(process_wait_option, nosuch)), process_wait(P, _)")]
    [InlineData(@"run('exit 0', [process(P)]), caught(process_wait(P, _, [timeout(-1)]), domain_error(process_wait_option, timeout(-1))), process_wait(P, _)")]
    [InlineData(@"run('exit 0', [process(P)]), caught(process_kill(P, _), instantiation_error), process_wait(P, _)")]
    [InlineData(@"run('exit 0', [process(P)]), caught(process_kill(P, nosuch), domain_error(signal, nosuch)), process_wait(P, _)")]
    [InlineData(@"run('exit 0', [process(P)]), caught(process_kill(P, 'Term'), domain_error(signal, 'Term')), process_wait(P, _)")]
    [InlineData(@"run('exit 0', [process(P)]), caught(process_kill(P, 'sigterm_x'), domain_error(signal, sigterm_x)), process_wait(P, _)")]
    [InlineData(@"run('exit 0', [process(P)]), caught(process_group_kill(P, 'SIGTERM'), domain_error(process_group_signal, 'SIGTERM')), process_wait(P, _)")]
    [InlineData(@"\+ is_process(foo)")]
    [InlineData(@"\+ is_process(_)")]
    public void BadArguments_AreErrors(string goal)
    {
        Assert.True(Q(Engine(), goal + ".").Success, goal);
    }

    [Fact]
    public void ATakenAlias_IsAnError()
    {
        Assert.True(Q(Engine(), @"
            run('exit 0', [stdout(pipe(_, [alias(kid)])), process(P)]),
            sys_shell(x, [E, A]),
            caught(process_create(E, A, [stdout(pipe(_, [alias(kid)]))]), permission_error(open, source_sink, alias(kid))),
            close(kid),
            process_wait(P, _).").Success);
    }

    [Fact]
    public void AnError_NamesThePredicateOfTheLibrary()
    {
        Assert.True(Q(Engine(), @"
            catch(process_wait(foo, _), error(_, Context), true),
            Context == process_wait/3.").Success);
    }

    [Fact]
    public void TheLibrary_FillsNoGlobalName()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("catch(process_id(_), error(existence_error(procedure, _), _), true).").Success);
        e.ConsultString(@"
:- module(m, [p/1]).
:- use_module(library(process)).
p(Pid) :- process_id(Pid).
");
        Assert.True(e.Query("m:p(_).").Success);
        Assert.True(e.Query("catch(process_id(_), error(existence_error(procedure, _), _), true).").Success);
    }
}
