using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>The internals of <c>library(process)</c>. The streams come from the bytes under
/// .NET's own readers and writers: those decode by the console's code page, differently on
/// each runtime, and on .NET Framework their Peek says end of file when data is still on
/// its way.</summary>
public static partial class MetaBuiltins
{
    private const string ProcessFunctor = "$process";

    /// <summary>A child process and the last signal sent to it. .NET gives only an exit
    /// code, 128 plus the signal on Unix, so that signal is what tells a kill from a
    /// program that exits with the same number.</summary>
    internal sealed class ProcessEntry
    {
        public ProcessEntry(Process process) => Process = process;
        public Process Process { get; }
        public int? SentSignal { get; set; }
        public int KilledExitCodeOnWindows { get; set; } = -1;

        /// <summary>The copies of its output still on their way to a file or a stream.</summary>
        public List<Task> OutputCopies { get; } = new();

        public Term Status()
        {
            int code = Process.ExitCode;
            bool killed = SentSignal is int sent
                && code == (OperatingSystem.IsWindows() ? KilledExitCodeOnWindows : 128 + sent);
            return new CompoundTerm(killed ? "killed" : "exit",
                new Term[] { new IntTerm(killed ? SentSignal!.Value : code) });
        }
    }

    private sealed class StdioSpec
    {
        public string Kind = "std";
        public string? File;
        public StreamHandle? Stream;
        public bool Binary;
        public string? Encoding;
        public string? Alias;
        public string? EofAction;
    }

    private static string ProcessText(Term term, Term whole)
    {
        switch (term)
        {
            case VarTerm: throw new ShumwayPrologException(IsoError.InstantiationError());
            case AtomTerm a: return a.Name;
            case IntTerm i: return i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case FloatTerm f: return f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case CompoundTerm { Functor: "file", Args: [var file] }:
                return ProcessText(file, whole).Replace('/', Path.DirectorySeparatorChar);
        }
        if (term.TryAsText(out string text)) return text;
        // A list that is not text concatenates its parts into one argument.
        if (term is CompoundTerm { Functor: "." })
            return string.Concat(ListItems(term, whole).Select(part => ProcessText(part, whole)));
        throw new ShumwayPrologException(IsoError.DomainError("process_argument", term));
    }

    /// <summary>The file a name stands for by the search path: on Windows with the
    /// extensions it may leave out. Null when there is none.</summary>
    private static string? FindOnPath(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        string[] extensions = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';')
            : new[] { "" };
        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            foreach (string extension in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(directory, name + extension); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>The file an executable names: <c>path(Name)</c> or a name with no directory
    /// is looked up on the search path; any other is a file, relative to the working
    /// directory. Null when it does not exist.</summary>
    private static string? ResolveExecutable(Term exe, out Term culprit)
    {
        culprit = exe;
        if (exe is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        if (exe is CompoundTerm { Functor: "path", Args: [var inner] })
        {
            if (inner is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
            if (!inner.TryAsText(out string name))
                throw new ShumwayPrologException(IsoError.DomainError("process_executable", exe));
            return FindOnPath(name);
        }
        if (!exe.TryAsText(out string file) || file.Length == 0)
            throw new ShumwayPrologException(IsoError.DomainError("process_executable", exe));
        if (file.IndexOfAny(new[] { '/', '\\' }) < 0) return FindOnPath(file);
        string full = Path.GetFullPath(file);
        if (File.Exists(full)) return full;
        return OperatingSystem.IsWindows() && !Path.HasExtension(full) && File.Exists(full + ".exe")
            ? full + ".exe" : null;
    }

    /// <summary>Whether a stream has bytes under it for a child to share, the only kind
    /// that means anything to a program outside. A stream only inside this process, such
    /// as a host's own writer, has none.</summary>
    private static Stream? BytesToWrite(Activation engine, StreamHandle h)
    {
        StreamRegistry registry = engine.Streams!;
        if (ReferenceEquals(h, registry.UserError)) return Console.OpenStandardError();
        if (ReferenceEquals(h, registry.UserOutput))
            return ReferenceEquals(RequireHost(engine, "library(process)").Out, Console.Out)
                ? Console.OpenStandardOutput() : null;
        if (h.BinaryStream is not null) return h.BinaryStream;
        return h.Writer is StreamWriter writer ? writer.BaseStream : null;
    }

    private static Stream? BytesToRead(Activation engine, StreamHandle h)
    {
        if (ReferenceEquals(h, engine.Streams!.UserInput)) return Console.OpenStandardInput();
        if (h.BinaryStream is not null) return h.BinaryStream;
        return h.Reader?.Inner is StrictTextReader reader ? reader.BaseStream : null;
    }

    private static StdioSpec ParseStreamSpec(Activation engine, Term stream, bool childReads)
    {
        if (stream is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        StreamHandle h = StreamBuiltins.ResolveStream(engine, Materializer.MaterializeAsCell(engine, stream));
        if (childReads ? !h.IsReader : !h.IsWriter)
            throw new ShumwayPrologException(IsoError.PermissionError(
                childReads ? "input" : "output", "stream", stream, engine));
        if ((childReads ? BytesToRead(engine, h) : BytesToWrite(engine, h)) is null)
            throw new ShumwayPrologException(IsoError.DomainError("file_stream", stream));
        return new StdioSpec { Kind = "stream", Stream = h };
    }

    private static StdioSpec ParseStdio(Activation engine, Term spec, Term option, bool childReads)
    {
        switch (spec)
        {
            case CompoundTerm { Functor: "stream", Args: [var stream] }:
                return ParseStreamSpec(engine, stream, childReads);
            case VarTerm: throw new ShumwayPrologException(IsoError.InstantiationError());
            case AtomTerm { Name: "std" }: return new StdioSpec();
            case AtomTerm { Name: "null" }: return new StdioSpec { Kind = "null" };
            case CompoundTerm { Functor: "file", Args: [var file] }:
                return new StdioSpec { Kind = "file", File = ProcessText(file, option) };
            case CompoundTerm { Functor: "pipe", Args: [VarTerm] }:
                return new StdioSpec { Kind = "pipe" };
            case CompoundTerm { Functor: "pipe", Args: [VarTerm, var streamOptions] }:
                return ParsePipeOptions(streamOptions, option);
            case CompoundTerm { Functor: "pipe" }:
                throw new ShumwayPrologException(IsoError.UninstantiationError(spec));
            default:
                throw new ShumwayPrologException(IsoError.DomainError("process_stream", spec));
        }
    }

    private static StdioSpec ParsePipeOptions(Term list, Term whole)
    {
        var s = new StdioSpec { Kind = "pipe" };
        foreach (Term option in ListItems(list, whole))
        {
            switch (option)
            {
                case VarTerm: throw new ShumwayPrologException(IsoError.InstantiationError());
                case CompoundTerm { Functor: "type", Args: [AtomTerm { Name: "binary" or "text" } t] }:
                    s.Binary = t.Name == "binary";
                    break;
                case CompoundTerm { Functor: "encoding", Args: [AtomTerm e] }
                        when TextEncodings.NameToEngineName(e.Name) is string name:
                    s.Encoding = name;
                    break;
                case CompoundTerm { Functor: "alias", Args: [AtomTerm a] }:
                    s.Alias = a.Name;
                    break;
                case CompoundTerm { Functor: "eof_action", Args: [AtomTerm { Name: "error" or "eof_code" or "reset" } e] }:
                    s.EofAction = e.Name;
                    break;
                default:
                    throw new ShumwayPrologException(IsoError.DomainError("process_stream_option", option));
            }
        }
        return s;
    }

    private static bool ProcessBoolOption(Term value, Term option)
    {
        if (value is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        return value is AtomTerm { Name: "true" } || (value is AtomTerm { Name: "false" }
            ? false
            : throw new ShumwayPrologException(IsoError.DomainError("process_create_option", option)));
    }

    private static void ApplyEnvironment(ProcessStartInfo psi, Term list, Term option, bool replace)
    {
        if (replace) psi.Environment.Clear();
        foreach (Term pair in ListItems(list, option))
        {
            if (pair is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
            if (pair is not CompoundTerm { Functor: "=", Args: [var name, var value] })
                throw new ShumwayPrologException(IsoError.DomainError("process_create_option", option));
            psi.Environment[ProcessText(name, option)] = ProcessText(value, option);
        }
    }

    private static string QuotedForWindows(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;
        var b = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { backslashes++; continue; }
            // Backslashes count only before a quote: there they double, and it gets its own.
            if (c == '"') b.Append('\\', 2 * backslashes + 1);
            else b.Append('\\', backslashes);
            backslashes = 0;
            b.Append(c);
        }
        b.Append('\\', 2 * backslashes);
        return b.Append('"').ToString();
    }

    private static ProcessPriorityClass PriorityClassOf(long nice) => nice switch
    {
        <= -10 => ProcessPriorityClass.High,
        < 0 => ProcessPriorityClass.AboveNormal,
        0 => ProcessPriorityClass.Normal,
        < 10 => ProcessPriorityClass.BelowNormal,
        _ => ProcessPriorityClass.Idle,
    };

    /// <summary><c>'$process_start'(+Exe, +Args, +Options, -In, -Out, -Err, -Process)</c>. The
    /// library binds the variables the options hold, which a copy of them could not.</summary>
    public static bool ProcessStart(Activation engine) => NetCall(engine, StartProcess);

    private static bool StartProcess(Activation engine)
    {
        Term exeTerm = MaterializeRegister(engine, 0);
        string? exe = ResolveExecutable(exeTerm, out Term culprit)
            ?? throw new ShumwayPrologException(IsoError.ExistenceError("source_sink", culprit));
        Term args = MaterializeRegister(engine, 1);
        var argv = ListItems(args, args).Select(a => ProcessText(a, args)).ToList();

        var stdin = new StdioSpec();
        var stdout = new StdioSpec();
        var stderr = new StdioSpec();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        bool window = false;
        long? priority = null;
        Term options = MaterializeRegister(engine, 2);
        foreach (Term option in ListItems(options, options))
        {
            if (option is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
            if (option is not CompoundTerm { Args: [var v] } c)
                throw new ShumwayPrologException(IsoError.DomainError("process_create_option", option));
            switch (c.Functor)
            {
                case "stdin": stdin = ParseStdio(engine, v, option, childReads: true); break;
                case "stdout": stdout = ParseStdio(engine, v, option, childReads: false); break;
                case "stderr": stderr = ParseStdio(engine, v, option, childReads: false); break;
                // The library binds these.
                case "process": break;
                case "wait": break;
                case "cwd":
                    string cwd = Path.GetFullPath(ProcessText(v, option));
                    if (!Directory.Exists(cwd))
                        throw new ShumwayPrologException(IsoError.ExistenceError("directory", v));
                    psi.WorkingDirectory = cwd;
                    break;
                case "env": ApplyEnvironment(psi, v, option, replace: true); break;
                case "environment": ApplyEnvironment(psi, v, option, replace: false); break;
                case "detached":
                    bool detached = ProcessBoolOption(v, option);
#if !NETFRAMEWORK
                    // Keeps a Ctrl+C at the console from reaching it. A child outlives its
                    // parent anyway, and Unix has no such choice in .NET.
                    if (detached && OperatingSystem.IsWindows()) psi.CreateNewProcessGroup = true;
#else
                    _ = detached;
#endif
                    break;
                case "window": window = ProcessBoolOption(v, option); break;
                case "priority":
                    priority = v is IntTerm { Value: >= -20 and <= 19 } n ? n.Value
                        : throw new ShumwayPrologException(IsoError.DomainError("process_create_option", option));
                    break;
                default:
                    throw new ShumwayPrologException(IsoError.DomainError("process_create_option", option));
            }
        }
        StdioSpec[] all = { stdin, stdout, stderr };
        StreamRegistry registry = engine.Streams!;
        foreach (StdioSpec s in all)
            CheckStreamAlias(engine, registry, s.Alias);

        psi.RedirectStandardInput = stdin.Kind != "std";
        psi.RedirectStandardOutput = stdout.Kind != "std";
        psi.RedirectStandardError = stderr.Kind != "std";
        if (window)
        {
            if (all.Any(s => s.Kind != "std"))
                throw new ShumwayPrologException(IsoError.DomainError("process_create_option", options));
            psi.UseShellExecute = true;
        }
        else if (all.All(s => s.Kind != "std"))
        {
            // Nothing shares the console: a host with none would flash one up.
            psi.CreateNoWindow = true;
        }
#if NETFRAMEWORK
        psi.Arguments = string.Join(" ", argv.Select(QuotedForWindows));
#else
        foreach (string argument in argv) psi.ArgumentList.Add(argument);
#endif
        Process process;
        try { process = Process.Start(psi) ?? throw new PrologRuntimeException("system_error", "no process"); }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            throw new ShumwayPrologException(IsoError.ExistenceError("source_sink", culprit));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 13)
        {
            throw new ShumwayPrologException(IsoError.PermissionError("execute", "source_sink", culprit, engine));
        }
        catch (Win32Exception ex)
        {
            throw new PrologRuntimeException("system_error", ex.Message);
        }
        if (priority is long nice)
        {
            try { process.PriorityClass = PriorityClassOf(nice); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                throw new PrologRuntimeException("system_error", ex.Message);
            }
        }
        var entry = new ProcessEntry(process);
        int id = RequireHost(engine, "library(process)").AddNetObject(entry);

        Cell input = OpenChildStream(engine, entry, stdin, psi.RedirectStandardInput ? process.StandardInput.BaseStream : null, writes: true);
        Cell output = OpenChildStream(engine, entry, stdout, psi.RedirectStandardOutput ? process.StandardOutput.BaseStream : null, writes: false);
        Cell error = OpenChildStream(engine, entry, stderr, psi.RedirectStandardError ? process.StandardError.BaseStream : null, writes: false);
        return engine.UnifyRegisterWithCell(3, input)
            && engine.UnifyRegisterWithCell(4, output)
            && engine.UnifyRegisterWithCell(5, error)
            && engine.UnifyRegisterWithCell(6, Materializer.MaterializeAsCell(engine,
                new CompoundTerm(ProcessFunctor, new Term[] { new IntTerm(id) })));
    }

    /// <summary>What the library sees for one standard stream of the child: its Prolog
    /// stream for a pipe, otherwise <c>none</c>. Null and file ends are served in the
    /// background: the child must not stall on a pipe nobody reads.</summary>
    private static Cell OpenChildStream(Activation engine, ProcessEntry entry, StdioSpec spec, Stream? pipe, bool writes)
    {
        Cell none = Cell.Atom(AtomTable.Intern("none", permanent: true).Id);
        if (pipe is null) return none;
        switch (spec.Kind)
        {
            case "null":
                if (writes) pipe.Dispose();
                else _ = RunCopy(() => DrainQuietly(pipe, Stream.Null));
                return none;
            case "file":
                Task fileCopy = StartFilePump(spec.File!, pipe, writes);
                if (!writes) entry.OutputCopies.Add(fileCopy);
                return none;
            case "stream":
                Task streamCopy = writes
                    ? CopyStreamToChild(engine, spec.Stream!, pipe)
                    : CopyChildToStream(engine, spec.Stream!, pipe);
                if (!writes) entry.OutputCopies.Add(streamCopy);
                return none;
            default:
                StreamHandle handle = StreamBuiltins.OpenLiveStream(engine.Streams!, pipe,
                    writes ? "write" : "read", spec.Binary, spec.Encoding, spec.Alias, spec.EofAction,
                    PositionTrackingReader.TranslateNewlinesByDefault);
                return StreamBuiltins.MakeStreamTerm(engine, handle);
        }
    }

    private const int CopyBufferBytes = 4096;

    /// <summary>A copy blocks for as long as the child lives, so it gets its own thread: on
    /// the pool, a few of them keep the next one from starting and its output from arriving.</summary>
    private static Task RunCopy(Action copy) => Task.Factory.StartNew(copy,
        System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>A child's output into a stream of this program. The child gets a pipe,
    /// because .NET has no way to hand it the stream's own handle. Written and flushed
    /// chunk by chunk, so what the program writes meanwhile falls between them.</summary>
    private static Task CopyChildToStream(Activation engine, StreamHandle h, Stream pipe)
    {
        // What the program wrote before starting the child goes first.
        if (h.Writer is not null) h.Writer.Flush();
        else h.BinaryStream?.Flush();
        Stream sink = BytesToWrite(engine, h)!;
        h.CopyStarted();
        return RunCopy(() =>
        {
            var buffer = new byte[CopyBufferBytes];
            try
            {
                int n;
                while ((n = pipe.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (sink is LiveStream live) live.WriteFromCopy(buffer, n);
                    else
                    {
                        sink.Write(buffer, 0, n);
                        sink.Flush();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or PrologRuntimeException) { }
            finally
            {
                pipe.Dispose();
                h.CopyEnded();
            }
        });
    }

    /// <summary>A stream of this program into a child's input, starting with what the
    /// program read ahead of its own reads. The child's input ends with the stream.</summary>
    private static Task CopyStreamToChild(Activation engine, StreamHandle h, Stream pipe)
    {
        byte[] first = h.TakeInputBuffered();
        Stream source = BytesToRead(engine, h)!;
        h.CopyStarted();
        return RunCopy(() =>
        {
            var buffer = new byte[CopyBufferBytes];
            try
            {
                if (first.Length > 0) pipe.Write(first, 0, first.Length);
                int n;
                while ((n = source is LiveStream live ? live.ReadForCopy(buffer) : source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    pipe.Write(buffer, 0, n);
                    pipe.Flush();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or PrologRuntimeException) { }
            finally
            {
                pipe.Dispose();
                h.CopyEnded();
            }
        });
    }

    private static Task StartFilePump(string file, Stream pipe, bool childReads)
    {
        Stream target;
        try
        {
            target = childReads
                ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                : new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PrologRuntimeException("system_error", ex.Message);
        }
        return RunCopy(() =>
        {
            using (target)
            using (pipe)
            {
                if (childReads) DrainQuietly(target, pipe);
                else DrainQuietly(pipe, target);
            }
        });
    }

    private static void DrainQuietly(Stream from, Stream to)
    {
        // A child that closes its end early is not an error of anybody's.
        try { from.CopyTo(to); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static ProcessEntry RequireProcess(Activation engine, Term term)
    {
        if (term is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        if (term is not CompoundTerm { Functor: ProcessFunctor, Args: [IntTerm id] })
            throw new ShumwayPrologException(IsoError.DomainError("process", term));
        return RequireHost(engine, "library(process)").NetObject((int)id.Value) as ProcessEntry
            ?? throw new ShumwayPrologException(IsoError.ExistenceError("process", term));
    }

    private static void ReleaseProcess(Activation engine, Term term, ProcessEntry entry)
    {
        entry.Process.Dispose();
        RequireHost(engine, "library(process)").RemoveNetObject((int)((IntTerm)((CompoundTerm)term).Args[0]).Value);
    }

    /// <summary><c>'$process_wait'(+Process, -Status, +Options)</c></summary>
    public static bool ProcessWait(Activation engine) => NetCall(engine, WaitForProcess);

    private static bool WaitForProcess(Activation engine)
    {
        Term term = MaterializeRegister(engine, 0);
        ProcessEntry entry = RequireProcess(engine, term);
        Term options = MaterializeRegister(engine, 2);
        int? timeoutMs = null;
        bool release = true;
        foreach (Term option in ListItems(options, options))
        {
            switch (option)
            {
                case VarTerm: throw new ShumwayPrologException(IsoError.InstantiationError());
                case CompoundTerm { Functor: "timeout", Args: [AtomTerm { Name: "infinite" }] }: timeoutMs = null; break;
                case CompoundTerm { Functor: "timeout", Args: [var seconds] }: timeoutMs = SecondsToProcessMs(seconds, option); break;
                case CompoundTerm { Functor: "release", Args: [var value] }: release = ProcessWaitBool(value, option); break;
                default: throw new ShumwayPrologException(IsoError.DomainError("process_wait_option", option));
            }
        }
        const int step = 50;
        long deadline = timeoutMs is int ms ? Environment.TickCount64 + ms : long.MaxValue;
        Process process = entry.Process;
        while (!process.WaitForExit(timeoutMs == 0 ? 0 : step))
        {
            engine.WaitSafePoint();
            if (Environment.TickCount64 >= deadline)
                return engine.UnifyRegisterWithCell(1, Cell.Atom(AtomTable.Intern("timeout", permanent: true).Id));
        }
        WaitForOutputCopies(engine, entry);
        Term status = entry.Status();
        if (release) ReleaseProcess(engine, term, entry);
        return engine.UnifyRegisterWithCell(1, Materializer.MaterializeAsCell(engine, status));
    }

    private const int CopiesGraceMs = 2000;

    /// <summary>What the child wrote should be in its file or stream when the wait returns,
    /// for the program to go on from there. Not for ever: something the child left running
    /// can hold its output open long after it is gone, and that goes on in the background.</summary>
    private static void WaitForOutputCopies(Activation engine, ProcessEntry entry)
    {
        if (entry.OutputCopies.Count == 0) return;
        try { engine.Streams!.Wait(Task.WhenAll(entry.OutputCopies), CopiesGraceMs, "process_wait"); }
        catch (PrologRuntimeException ex) when (ex.Kind == "timeout_error") { }
    }

    private static int SecondsToProcessMs(Term value, Term option) => value switch
    {
        VarTerm => throw new ShumwayPrologException(IsoError.InstantiationError()),
        IntTerm { Value: >= 0 and <= int.MaxValue / 1000 } i => (int)i.Value * 1000,
        FloatTerm { Value: >= 0 and <= int.MaxValue / 1000 } f => (int)Math.Ceiling(f.Value * 1000),
        _ => throw new ShumwayPrologException(IsoError.DomainError("process_wait_option", option)),
    };

    private static bool ProcessWaitBool(Term value, Term option)
    {
        if (value is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        return value is AtomTerm { Name: "true" } || (value is AtomTerm { Name: "false" }
            ? false
            : throw new ShumwayPrologException(IsoError.DomainError("process_wait_option", option)));
    }

    private static readonly Dictionary<string, int> SignalNumbers = new()
    {
        ["SIGHUP"] = 1, ["SIGINT"] = 2, ["SIGQUIT"] = 3, ["SIGILL"] = 4, ["SIGABRT"] = 6,
        ["SIGFPE"] = 8, ["SIGKILL"] = 9, ["SIGUSR1"] = 10, ["SIGSEGV"] = 11, ["SIGUSR2"] = 12,
        ["SIGPIPE"] = 13, ["SIGALRM"] = 14, ["SIGTERM"] = 15, ["SIGCHLD"] = 17, ["SIGCONT"] = 18,
        ["SIGSTOP"] = 19, ["SIGTSTP"] = 20,
    };

    private static int SignalNumber(Term signal)
    {
        switch (signal)
        {
            case VarTerm: throw new ShumwayPrologException(IsoError.InstantiationError());
            case IntTerm { Value: >= 0 and <= 64 } i: return (int)i.Value;
            case AtomTerm a when SignalNumbers.TryGetValue(a.Name, out int n): return n;
            // The other spelling: the name without SIG, in lower case.
            case AtomTerm a when SignalNumbers.TryGetValue("SIG" + a.Name.ToUpperInvariant(), out int n)
                                 && a.Name == a.Name.ToLowerInvariant():
                return n;
            default: throw new ShumwayPrologException(IsoError.DomainError("signal", signal));
        }
    }

#if !NETFRAMEWORK
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendUnixSignal(int pid, int signal);
#endif

    /// <summary><c>'$process_kill'(+Process, +Signal)</c>. .NET can only kill, so Unix gets
    /// its own call for the other signals. Windows has no signals: any of them terminates.</summary>
    public static bool ProcessKill(Activation engine) => NetCall(engine, KillProcess);

    private static bool KillProcess(Activation engine)
    {
        ProcessEntry entry = RequireProcess(engine, MaterializeRegister(engine, 0));
        int signal = SignalNumber(MaterializeRegister(engine, 1));
        Process process = entry.Process;
        // Gone already: the end the caller wanted. Two runtimes differ on saying so.
        if (process.HasExited) return true;
        entry.SentSignal = signal;
#if !NETFRAMEWORK
        if (!OperatingSystem.IsWindows() && signal != 9)
        {
            if (SendUnixSignal(process.Id, signal) != 0)
                throw new PrologRuntimeException("system_error", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return true;
        }
#endif
        KillQuietly(() => process.Kill());
        return true;
    }

    private static void KillQuietly(Action kill)
    {
        // It may end by itself between the check and the kill.
        try { kill(); }
        catch (InvalidOperationException) { }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            throw new PrologRuntimeException("system_error", ex.Message);
        }
    }

    /// <summary><c>'$process_group_kill'(+Process)</c>: the process and what it started.</summary>
    public static bool ProcessGroupKill(Activation engine) => NetCall(engine, KillProcessTree);

    private static bool KillProcessTree(Activation engine)
    {
        ProcessEntry entry = RequireProcess(engine, MaterializeRegister(engine, 0));
        Process process = entry.Process;
        if (process.HasExited) return true;
        entry.SentSignal = 9;
#if NETFRAMEWORK
        // No tree in the Framework's API: taskkill knows it, and ends a process with code 1.
        entry.KilledExitCodeOnWindows = 1;
        using Process? killer = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {process.Id}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        killer?.WaitForExit();
#else
        KillQuietly(() => process.Kill(entireProcessTree: true));
#endif
        return true;
    }

    /// <summary><c>'$process_release'(+Process)</c></summary>
    public static bool ProcessRelease(Activation engine) => NetCall(engine, ReleaseProcessBuiltin);

    private static bool ReleaseProcessBuiltin(Activation engine)
    {
        Term term = MaterializeRegister(engine, 0);
        ReleaseProcess(engine, term, RequireProcess(engine, term));
        return true;
    }

    /// <summary><c>'$process_id'(+Process, -Pid)</c></summary>
    public static bool ProcessId2(Activation engine) => NetCall(engine, static e =>
        e.UnifyRegisterWithCell(1, Cell.Int(RequireProcess(e, MaterializeRegister(e, 0)).Process.Id)));

    /// <summary><c>'$process_id'(-Pid)</c>: this process.</summary>
    public static bool ProcessId1(Activation engine)
    {
        using Process current = Process.GetCurrentProcess();
        return engine.UnifyRegisterWithCell(0, Cell.Int(current.Id));
    }

    /// <summary><c>'$is_process'(@Term)</c></summary>
    public static bool IsProcess(Activation engine)
        => MaterializeRegister(engine, 0) is CompoundTerm { Functor: ProcessFunctor, Args: [IntTerm id] }
            && RequireHost(engine, "library(process)").NetObject((int)id.Value) is ProcessEntry;

    /// <summary><c>'$process_which'(+Exe, -File)</c></summary>
    public static bool ProcessWhich(Activation engine)
    {
        string? file = ResolveExecutable(MaterializeRegister(engine, 0), out _);
        return file is not null
            && engine.UnifyRegisterWithCell(1, Cell.Atom(AtomTable.Intern(PrologPath.ToCanonical(file)).Id));
    }
}
