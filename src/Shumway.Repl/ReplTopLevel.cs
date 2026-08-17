using System.Linq;
using System.Threading;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Shumway.TopLevel;

namespace Shumway.Repl;

/// <summary>
/// A minimal interactive top-level (REPL) for Shumway. It
/// consults any files named on the command line, then reads queries from
/// standard input, runs each, and prints its solutions; pressing ';'
/// (or space / Tab) after a solution searches for the next. The session ends at
/// <c>halt.</c> or end of input.
///
/// <para>This is a thin client over the <see cref="PrologEngine"/>
/// embedding API — its purpose is interactive exercising of Shumway, not
/// to be a full-featured development environment.</para>
/// </summary>
internal static class ReplTopLevel
{
    private static int Main(string[] args)
    {
        try { return MainCore(args); }
        finally { Shumway.Embedding.PrologEngine.PrintLoadProfile(); }
    }

    private static int MainCore(string[] args)
    {
        // -h / --help — only among the REPL's OWN arguments: anything after the `--`
        // separator belongs to the consulted program (the argv Prolog flag), including
        // a --help of its own.
        int helpSep = Array.IndexOf(args, "--");
        string[] ownArgs = helpSep < 0 ? args : args.Take(helpSep).ToArray();   // not args[..n]: net48 lacks GetSubArray
        if (Array.IndexOf(ownArgs, "--help") >= 0 || Array.IndexOf(ownArgs, "-h") >= 0)
        {
            PrintUsage();
            return 0;
        }

        // Route all console output through a column tracker so the top-level can
        // start an answer on a fresh line when a goal left the cursor mid-line
        // (see EnsureLineStart). Must precede `new PrologEngine()` so the engine's
        // default Out (= Console.Out) is the tracked writer, and precede any write.
        _outputTracker = new ColumnTrackingWriter(Console.Out);
        Console.SetOut(_outputTracker);

        // SHUMWAY_TIMING=1 prints to stderr a per-phase breakdown of
        // wall time spent in (1) process startup + bundle/consult load
        // versus (2) the actual SHUMWAY_GOAL execution. Lets benchmarks
        // separate fixed startup cost (JIT, AssemblyLoad, bundle parse)
        // from the workload itself.
        bool timing =
            Environment.GetEnvironmentVariable("SHUMWAY_TIMING") == "1";
        var stopwatch = timing
            ? System.Diagnostics.Stopwatch.StartNew()
            : null;
        long setupMsAtConsultStart = 0;
        long setupMsAtGoalStart = 0;
        // EOF on the console is Ctrl+Z (then Enter) on Windows and
        // Ctrl+D on Unix/macOS — Console.ReadLine() returns null for
        // both, but the key combo differs, so name the right one.
        string eofKey = OperatingSystem.IsWindows() ? "Ctrl-Z" : "Ctrl-D";
        // Name and version first, as every Prolog top level does — the
        // version reported here is the same one `current_prolog_flag(
        // version_data, V)` gives, both from PrologEngine's constants.
        Console.WriteLine(PrologEngine.VersionBanner);
        Console.WriteLine(
            $"End a query with '.'  —  'halt.' or {eofKey} exits.");

        // Split args at "--": everything before is a file to consult,
        // everything after is exposed to the program as the argv
        // Prolog flag (current_prolog_flag(argv, Argv)). Matches
        // SWI / GNU / SICStus convention.
        int sep = Array.IndexOf(args, "--");
        string[] consultFiles = sep < 0 ? args : args.Take(sep).ToArray();   // not args[..n]: net48 lacks GetSubArray
        string[] programArgs = sep < 0 ? Array.Empty<string>() : args.Skip(sep + 1).ToArray();

        // --foreign-dll <path> / --native-dll <path>, repeatable. The linker records these in
        // a bundle and LoadBundle honours them; consulting SOURCE had no way to say them at
        // all, which meant a program with interop could only be run compiled — and therefore
        // could not be debugged, since debugging is a property of source. Same flag names as
        // shumway-link, on purpose.
        var foreignDlls = new List<string>();
        var nativeDlls = new List<string>();
        var libraryDirs = new List<string>();   // -L / --library-dir (ADR-038)
        string? startupGoalArg = null;   // --goal / -g: run after consulting, then stay
        int? dapPortArg = null;          // --dap <port>: ADR-036 VS Code endpoint
        bool dapWait = false;            // --dap-wait: hold the start until configured
        var flagArgs = new HashSet<int>();
        for (int i = 0; i < consultFiles.Length; i++)
        {
            string flag = consultFiles[i];
            if (flag is not ("--foreign-dll" or "--native-dll" or "--goal" or "-g"
                or "--dap" or "--dap-wait" or "--library-dir" or "-L"))
                continue;
            flagArgs.Add(i);
            if (i + 1 >= consultFiles.Length)
            {
                Console.Error.WriteLine($"% {flag} needs a value");
                continue;
            }
            flagArgs.Add(i + 1);
            switch (flag)
            {
                case "--foreign-dll": foreignDlls.Add(consultFiles[i + 1]); break;
                case "--native-dll": nativeDlls.Add(consultFiles[i + 1]); break;
                case "--library-dir":
                case "-L": libraryDirs.Add(consultFiles[i + 1]); break;
                case "--dap":
                case "--dap-wait":
                    if (int.TryParse(consultFiles[i + 1], out int dapPort) && dapPort >= 0)
                    {
                        dapPortArg = dapPort;
                        dapWait = flag == "--dap-wait";
                    }
                    else
                        Console.Error.WriteLine($"% {flag} needs a port number (0 = pick one)");
                    break;
                default: startupGoalArg = consultFiles[i + 1]; break;
            }
            i++;
        }

        // The files to consult: everything that is not a flag, and not a flag's value.
        string[] sourceFiles = consultFiles
            .Where((a, i) => !flagArgs.Contains(i)
                && a is not ("--clpfd" or "--clpr" or "--debug" or "--debug-wait"))
            .ToArray();

        var engine = new PrologEngine();
        // user_input reads the REPL's shared type-ahead buffer: what the typed
        // line left unconsumed feeds the next query — or an in-query read/1
        // (`?- read(X). write(b).` binds X = write(b)). Must be set before the
        // first query: the stream registry snapshots it at query setup.
        _pendingInput = new ReplPendingReader(AcquireEngineInputLine);
        engine.In = _pendingInput;
        // ADR-038 — the shipped lib/ (beside the executable) is on the library
        // search path by default, so use_module(library(X)) finds a bundled
        // library with no configuration. SHUMWAY_LIBRARY_PATH and
        // file_search_path/library_directory facts add to it.
        engine.AddDefaultLibraryDirectories();
        // -L / --library-dir: extra directories searched by use_module(library(X)).
        foreach (string dir in libraryDirs)
            engine.AddLibraryDirectorySpec(dir);   // dir or dir:dialect (ADR-040)
        // Point the engine's Out directly at the column tracker (Console.Out is the
        // synchronized wrapper SetOut installed, which hides ILineStartAware). Set
        // before the stream registry is built so user_output writes and time/1's
        // report share the tracker and can query the column. Must precede any query.
        engine.Out = _outputTracker!;
        // Stash the session so the line editor's Tab completer (constructed
        // lazily on first ReadLine) can query for predicate names, and so
        // RunQuery has the shared top-level logic to drive.
        _session = new TopLevelSession(engine);
        engine.Flags.Argv = programArgs;
        // Default: promote a predicate to Tier-1 IL once it has been invoked 32
        // times, so interactive / --goal runs get compiled code for hot predicates
        // without any flag. SHUMWAY_IL_PROMOTE=N overrides the threshold; N <= 0
        // disables promotion (Threshold <= 0 is the "off" sentinel), keeping every
        // dispatch on the Tier-0 interpreter.
        engine.IlPromotion.Threshold = 32;
        string? promoteEnv = Environment.GetEnvironmentVariable("SHUMWAY_IL_PROMOTE");
        if (int.TryParse(promoteEnv, out int promoteN))
            engine.IlPromotion.Threshold = promoteN;
        if (stopwatch is not null)
            setupMsAtConsultStart = stopwatch.ElapsedMilliseconds;
        // --clpfd / --clpr: enable the constraint library BEFORE consulting, so
        // its operators (#=, in, .., {}/1, ...) are in the operator table when
        // the named files are parsed (a `:- use_module(library(clpfd))` directive
        // inside a file is too late — the file is parsed before directives run).
        foreach (string path in consultFiles)
        {
            if (path == "--clpfd") engine.UseClpfd();
            else if (path == "--clpr") engine.UseClpr();
        }

        // Interop, before the consult: a `:- native fn/N` directive resolves against a
        // library that must already be registered, and a foreign predicate must be a known
        // predicate by the time a clause that calls it is linked.
        foreach (string dll in nativeDlls)
        {
            try { engine.UseNativeLibrary(System.IO.Path.GetFullPath(dll)); }
            catch (Exception ex) { Console.Error.WriteLine($"% could not load native library {dll}: {ex.Message}"); }
        }
        foreach (string dll in foreignDlls)
        {
            try { engine.RegisterForeignAssembly(System.IO.Path.GetFullPath(dll)); }
            catch (Exception ex) { Console.Error.WriteLine($"% could not load foreign assembly {dll}: {ex.Message}"); }
        }

        // ADR-035 — --debug opens a debug session before anything is consulted, because
        // debuggability is a property of the CODE, decided when it is compiled: an engine
        // told to debug afterwards would already have thrown away the variable names, the
        // frames and the source positions the debugger exists to show. --debug-wait also
        // holds the process at the door until a debugger is actually attached, which is
        // what a launcher (D4's F5) needs and what an attach-by-hand does not.
        bool debug = Array.IndexOf(consultFiles, "--debug") >= 0
            || Array.IndexOf(consultFiles, "--debug-wait") >= 0
            || dapPortArg is not null;
        if (debug)
        {
            // The whole of --debug is now one embedding call: it sets the debug flags, turns
            // LCO off (honouring the SHUMWAY_DEBUG_LCO pin), arms the SHUMWAY_DEBUG_DIAG
            // exception log, announces the files we are about to consult, and opens the
            // channel session. It is the SAME entry point a .NET host uses to debug an
            // embedded engine — the REPL is just the first caller. We keep the wait here so
            // the console can narrate it (the API's own WaitForAttach is the silent variant
            // for a host that has no console).
            var options = new Shumway.Embedding.Debugging.DebugOptions
            {
                SourceFiles = sourceFiles,
            };
            if (dapPortArg is int p) options.DapPort = p;   // --dap wins over the env default
            var session = engine.EnableDebugging(options);
            Console.WriteLine($"% debug session open (pid {Environment.ProcessId}) — attach a debugger.");
            if (session.DapPort is int boundDap)
                Console.WriteLine($"% DAP endpoint listening on 127.0.0.1:{boundDap} (VS Code).");

            // --dap-wait: hold the door until the client's breakpoints are ARMED
            // (configurationDone), so the very first goal typed at the prompt cannot
            // run past them — the launch race the plain prompt otherwise loses. The
            // DAP twin of --debug-wait, with the same no-deadline honesty: a program
            // launched to be debugged that runs undebugged is useless (Ctrl+C exits).
            if (dapWait)
            {
                Console.WriteLine("% waiting for the debugger to finish configuring...");
                while (!session.WaitForDapConfigured(TimeSpan.FromMilliseconds(500)))
                { /* no deadline — the client is coming */ }
                Console.WriteLine("% debugger configured.");
            }
            if (Array.IndexOf(consultFiles, "--debug-wait") >= 0)
            {
                Console.WriteLine("% waiting for a debugger to attach...");
                while (!System.Diagnostics.Debugger.IsAttached)
                    System.Threading.Thread.Sleep(100);

                // Attached is not ready: the debugger still has to find the channel and arm
                // the breakpoints the user set before pressing the button. Consulting now
                // would run the program straight past them. Wait for it to say something.
                bool ready = session.WaitForDebuggerCommands(10_000);
                Console.WriteLine(ready
                    ? "% attached."
                    : "% attached (the debugger said nothing; running anyway).");
            }
        }

        foreach (string path in sourceFiles)
        {
            try
            {
                ConsultFile(engine, path);
            }
            catch (Shumway.Core.PrologHaltException halted)
            {
                // A file whose `:- initialization` goal halted has already done its work.
                // This is the whole shape of a program launched by the IDE (D4's F5): a .pl
                // that runs and ends, with no top-level in sight.
                MaybeDumpIlStats(engine);
                MaybeDumpProfile(engine);
                return halted.ExitCode;
            }
        }
        if (stopwatch is not null)
            setupMsAtGoalStart = stopwatch.ElapsedMilliseconds;

        // SHUMWAY_GOAL=<term>. — run one goal at startup, then exit.
        // Lets a CPU profiler (dotnet-trace) drive a fixed workload via
        // `-- shumway.exe bundle.shum` without needing to forward stdin
        // (which dotnet-trace's `--` launcher does not do). Also handy
        // for scripted benchmarking.
        string? startupGoal = Environment.GetEnvironmentVariable("SHUMWAY_GOAL");
        if (!string.IsNullOrWhiteSpace(startupGoal))
        {
            Shumway.Core.Profiler.Reset();
            long allocBefore = Shumway.Core.Profiler.Enabled ? GC.GetTotalAllocatedBytes() : 0;
            try { RunQuery(engine, startupGoal.Trim()); }
            catch (Exception ex) { PrintError(engine, ex); }
            if (Shumway.Core.Profiler.Enabled)
            {
                long allocAfter = GC.GetTotalAllocatedBytes();
                Console.Error.WriteLine($"[mem] total allocated during query: {(allocAfter - allocBefore) / (1024.0 * 1024):N1} MB");
            }
            Shumway.Core.Profiler.StopRun();
            if (stopwatch is not null)
            {
                long total = stopwatch.ElapsedMilliseconds;
                long preConsult = setupMsAtConsultStart;
                long consult = setupMsAtGoalStart - setupMsAtConsultStart;
                long exec = total - setupMsAtGoalStart;
                Console.Error.WriteLine(
                    $"[timing] startup={preConsult}ms consult+link={consult}ms exec={exec}ms total={total}ms");
            }
            MaybeDumpIlStats(engine);
            MaybeDumpProfile(engine);
            return engine.LastHaltExitCode ?? 0;
        }

        // --goal / -g: run one goal exactly as if it were the first query typed at the
        // prompt — solutions print, a non-deterministic answer offers ';' — and then STAY
        // in the top level (end the goal with halt to exit instead; SHUMWAY_GOAL above is
        // the run-and-exit variant). Runs after every file is consulted, and under
        // --debug-wait after the debugger armed its breakpoints, so a program can be
        // launched and run under the debugger in one command line.
        if (!string.IsNullOrWhiteSpace(startupGoalArg))
        {
            string goalText = startupGoalArg!.Trim();
            if (!goalText.EndsWith(".", StringComparison.Ordinal)) goalText += " .";
            Shumway.Core.Profiler.Reset();
            try { RunQuery(engine, goalText); }
            catch (Exception ex) { PrintError(engine, ex); }
            Shumway.Core.Profiler.StopRun();
            if (engine.LastHaltExitCode is int goalHalt)
            {
                MaybeDumpIlStats(engine);
                MaybeDumpProfile(engine);
                return goalHalt;
            }
        }

        while (true)
        {
            string? query = ReadQuery();
            if (query is null) break;            // end of input
            if (query.Length == 0) continue;     // blank entry

            Shumway.Core.Profiler.Reset();
            try
            {
                RunQuery(engine, query);
            }
            catch (Exception ex)
            {
                PrintError(engine, ex);
            }
            Shumway.Core.Profiler.StopRun();

            // halt/0,1 is caught inside the engine's solution iterator; it
            // surfaces as LastHaltExitCode rather than a .NET exception.
            if (engine.LastHaltExitCode is int exitCode)
            {
                MaybeDumpIlStats(engine);
                MaybeDumpProfile(engine);
                return exitCode;
            }

            MaybeDumpProfile(engine);
        }

        Console.WriteLine();
        MaybeDumpIlStats(engine);
        return 0;
    }

    /// <summary>REPL-side error renderer. Distinguishes
    /// the three exception families a query can surface:
    ///
    /// <list type="bullet">
    /// <item><see cref="ShumwayPrologException"/> — user-thrown via
    ///   <c>throw/1</c>. The carried <c>Term</c> renders with the
    ///   operator-aware printer the bindings use.</item>
    /// <item><see cref="Shumway.Core.PrologRuntimeException"/> —
    ///   ISO-shaped error from a builtin. <c>Kind</c> +
    ///   <c>Detail</c> compose into the standard error/2 shape.</item>
    /// <item>Any other .NET exception — parse failure, embedding
    ///   misuse, internal bug. Type name + message.</item>
    /// </list>
    ///
    /// <para>Both Prolog families surface the engine's captured
    /// stack trace with source positions when the bytecode carried
    /// debug info. <c>SHUMWAY_DEBUG_TRACE=1</c>
    /// adds the .NET stack on top — useful when an engine bug
    /// surfaces as an InvalidOperationException somewhere in the
    /// interpreter.</para></summary>
    private static void PrintError(PrologEngine engine, Exception ex)
    {
        // The diagnostic itself is shared with the web front-end (ErrorRendering);
        // the console only adds its own `%` prefix.
        foreach (string line in ErrorRendering.Describe(engine, ex))
            Console.WriteLine("% " + line);

        if (Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_TRACE") == "1")
        {
            Console.WriteLine("% .NET stack:");
            Console.WriteLine(ex.StackTrace);
        }
    }

    /// <summary>Prints the execution profile to stderr after a
    /// query, in a profiling build. A normal build's
    /// <see cref="Shumway.Core.Profiler.Enabled"/> is a compile-time
    /// <c>false</c>, so this is a no-op (and the report is never built).</summary>
    private static void MaybeDumpProfile(PrologEngine engine)
    {
        if (!Shumway.Core.Profiler.Enabled) return;
        Console.Error.WriteLine(engine.ProfileReport());
    }

    private static void MaybeDumpIlStats(PrologEngine engine)
    {
        // ADR-032 sizing — SHUMWAY_CPFREE_STATS=1 prints the CP-free guard
        // recogniser's accept/reject counters accumulated over this process's
        // Tier-1 promotions: the per-program impact estimate for each
        // static-widening candidate (caps / callee cuts / true-G3 nesting).
        if (Environment.GetEnvironmentVariable("SHUMWAY_CPFREE_STATS") == "1")
            Console.Error.WriteLine(
                Shumway.Compiler.Il.IlPredicateCompiler.CpFreeGuardStats.Summary());
        if (Environment.GetEnvironmentVariable("SHUMWAY_IL_STATS") != "1") return;
        Console.Error.WriteLine(
            $"[il-stats] promoted={engine.IlPromotion.PromotedCount} "
            + $"unpromotable={engine.IlPromotion.UnpromotableCount} "
            + $"tracked={engine.IlPromotion.TrackedCount} "
            + $"threshold={engine.IlPromotion.Threshold}");

        static string NameOf(int fid)
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
            string n = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? $"#{atomId}";
            return $"{n}/{arity}";
        }

        // Unpromotable predicates grouped by reason, so the Tier-1
        // coverage analysis can see what's architectural (dynamic) vs
        // fixable (size / compiler-subset gaps).
        var byReason = new Dictionary<string, List<string>>();
        foreach (var (fid, reason) in engine.IlPromotion.UnpromotableEntries())
            (byReason.TryGetValue(reason, out var l) ? l : (byReason[reason] = new List<string>()))
                .Add(NameOf(fid));
        foreach (var (reason, names) in byReason)
        {
            names.Sort(StringComparer.Ordinal);
            Console.Error.WriteLine($"[il-unpromotable:{reason}] count={names.Count}");
            foreach (var n in names) Console.Error.WriteLine($"    {n}");
        }

        var promoted = new List<string>();
        foreach (int fid in engine.IlPromotion.PromotedFunctorIds()) promoted.Add(NameOf(fid));
        promoted.Sort(StringComparer.Ordinal);
        Console.Error.WriteLine($"[il-promoted] count={promoted.Count}");
        foreach (var n in promoted) Console.Error.WriteLine($"    {n}");
    }

    private static void ConsultFile(PrologEngine engine, string path)
    {
        try
        {
            // .shum bundle (binary) → LoadBundle; everything else →
            // ConsultString on the file's text. Useful for measuring
            // persisted-IL load + run times without going through
            // Sigil at runtime.
            if (path.EndsWith(".shum", StringComparison.OrdinalIgnoreCase))
            {
                engine.LoadBundle(path);
                Console.WriteLine($"% loaded bundle {path}");
                // REPL usability: standing in `user`, the bundle's module-local
                // predicates would be invisible — unlike consulting the source.
                // Alias each bare (non-library) module's locals into `user` so
                // `?- pred(...)` works, and say which modules were promoted.
                var outcome = engine.PromoteBareBundleModulesToUser();
                static string Indicators(
                    System.Collections.Generic.List<(string Name, int Arity)> ps)
                {
                    const int cap = 8;
                    string s = string.Join(", ",
                        ps.Take(cap).Select(p => $"{p.Name}/{p.Arity}"));
                    return ps.Count > cap ? $"{s} (+{ps.Count - cap} more)" : s;
                }
                foreach (var pm in outcome.Promoted)
                {
                    // Hide the compiler's internal `$`-helpers (aliased too, but
                    // noise); report only the module's user-facing predicates.
                    var preds = pm.Predicates.Where(p => !p.Name.StartsWith('$')).ToList();
                    if (preds.Count > 0)
                        Console.WriteLine(
                            $"%   promoted '{pm.Module}' to user: {Indicators(preds)}");
                }
                foreach (var sk in outcome.SkippedForCollision)
                {
                    var names = sk.Predicates.Where(p => !p.Name.StartsWith('$')).ToList();
                    Console.WriteLine(
                        $"%   NOT promoted '{sk.Module}' — name clash on "
                        + $"{Indicators(names.Count > 0 ? names : sk.Predicates)}; "
                        + $"call as {sk.Module}:Pred");
                }
            }
            else
            {
                // ConsultFile (not ConsultString-of-text) so the engine
                // records the file's directory (for `:- include`) and its
                // path (so a later `:- use_module` of the same file is a
                // no-op instead of a clause-doubling re-consult).
                engine.ConsultFile(path);
                Console.WriteLine($"% consulted {path}");
            }
        }
        catch (Shumway.Core.PrologHaltException)
        {
            // `:- initialization(main)` where main halts: the program HAS run, and it asked
            // to end. Falling through to the top-level prompt here would leave a program
            // that said halt sitting at a `?- ` nobody typed at — which is what happened
            // until the engine learned to re-raise a halt out of a load.
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"% could not consult {path}: {ex.Message}");
        }
    }

    /// <summary>Shared line editor, lazily created on
    /// first use so the SHUMWAY_GOAL / scripted entry paths that
    /// never read interactive lines don't even touch disk to load
    /// history. The engine reference is captured so the
    /// editor's Tab handler can query for completion candidates.</summary>
    private static LineEditor? _lineEditor;
    private static TopLevelSession? _session;
    private static ColumnTrackingWriter? _outputTracker;
    private static LineEditor LineEd => _lineEditor ??=
        new LineEditor(
            new HistoryStore(HistoryStore.DefaultPath()),
            completer: BuildCompleter());

    private static Func<string, IReadOnlyList<string>> BuildCompleter() =>
        prefix => PredicateCompletion.Matching(_session?.Engine, prefix);

    /// <summary>The shared type-ahead buffer <c>user_input</c> also reads —
    /// see <see cref="ReplPendingReader"/>.</summary>
    private static ReplPendingReader? _pendingInput;

    /// <summary>Set while a goal is blocked reading a fresh console line for
    /// <c>user_input</c>; parks the ESC watcher, which otherwise intercepts
    /// (and drops) the keys the user types for <c>read/1</c>.</summary>
    private static volatile bool _engineReadingInput;

    /// <summary>More input for a goal reading <c>user_input</c> once the
    /// type-ahead is dry: prompt SWI-style <c>|: </c> and read a fresh
    /// console line.</summary>
    private static string? AcquireEngineInputLine()
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine();
        _engineReadingInput = true;
        try
        {
            Console.Write("|: ");
            return Console.ReadLine();
        }
        finally { _engineReadingInput = false; }
    }

    /// <summary>Reads one query — ONE SENTENCE — from the type-ahead buffer,
    /// prompting for (and joining) more lines until the buffer holds a
    /// complete sentence. Text beyond the sentence stays buffered: it is the
    /// next query, or an in-query <c>read/1</c>'s input. Returns the empty
    /// string for a blank entry and <c>null</c> at end of input.</summary>
    private static string? ReadQuery()
    {
        var pending = _pendingInput!;
        while (true)
        {
            string buffered = pending.Buffered;
            if (buffered.AsSpan().Trim().Length == 0)
            {
                pending.Clear();
            }
            else
            {
                string? s = Shumway.Embedding.SentenceScanner.ReadSentenceText(
                    new System.IO.StringReader(buffered), out bool complete);
                if (s is not null && complete)
                {
                    pending.Consume(s.Length);
                    return s.Trim();
                }
                // Incomplete sentence buffered: prompt for its continuation.
            }

            string prompt = pending.Buffered.Length == 0 ? "?- " : "   ";
            string? line = LineEd.ReadLine(prompt);
            if (line is null)
            {
                string rest = pending.Buffered.Trim();
                pending.Clear();
                return rest.Length == 0 ? null : rest;
            }
            pending.Push(line + "\n");
            if (pending.Buffered.AsSpan().Trim().Length == 0)
            {
                pending.Clear();
                return "";   // blank entry
            }
        }
    }

    /// <summary>Runs a query and prints its solutions one at a time. The search
    /// itself, the <c>copy_term/3</c> wrapping that surfaces residual constraints,
    /// and the answer formatting all live in <see cref="TopLevelSession"/>; what
    /// stays here is the console's half — when to ask for another solution, and
    /// how to print.</summary>
    private static void RunQuery(PrologEngine engine, string query)
    {
        using QueryRun run = _session!.StartQuery(query);
        if (!run.Parsed)
        {
            // Parser error — let the engine produce its usual diagnostic.
            if (!run.MoveNext() && engine.LastHaltExitCode is null)
                Console.WriteLine("false.");
            return;
        }

        // The search runs on this thread; an ESC keypress (watched on a
        // background thread) aborts a long-running query at the next engine
        // safe point. Not instantaneous, but responsive.
        if (!MoveNextWatched(run, out bool aborted))
        {
            if (aborted) Console.WriteLine("% Execution aborted.");
            else if (engine.LastHaltExitCode is null) { EnsureLineStart(); Console.WriteLine("false."); }
            return;
        }
        while (true)
        {
            int width;
            try { width = Console.WindowWidth; }
            catch { width = 80; }
            if (width < 20) width = 80;
            // SWI-style: if the goal left the cursor mid-line (e.g. a bare
            // writeq/1 with no trailing nl), start the answer on its own line
            // so the `true`/bindings don't run into the goal's output.
            EnsureLineStart();
            Console.Write(run.Format(width));
            if (run.IsLast)
            {
                Console.WriteLine(".");
                return;
            }
            if (!WantsAnotherSolution())
            {
                Console.WriteLine(".");
                return;
            }
            Console.WriteLine(" ;");
            if (!MoveNextWatched(run, out aborted))
            {
                if (aborted) Console.WriteLine("% Execution aborted.");
                else if (engine.LastHaltExitCode is null) { EnsureLineStart(); Console.WriteLine("false."); }
                return;
            }
        }
    }

    /// <summary>SWI-style: when the just-run goal left the cursor somewhere other
    /// than column 0 (a bare <c>writeq/1</c>, <c>write/1</c>, etc. with no trailing
    /// newline), emit a newline so the top-level's <c>true</c> / <c>false</c> /
    /// bindings begin on a fresh line instead of running into the goal's output.
    /// The column is tracked on the output we write (see
    /// <see cref="ColumnTrackingWriter"/>), so this is correct whether stdout is a
    /// terminal or is redirected / captured — matching SWI, which tracks the stream
    /// column rather than querying a (possibly absent) hardware cursor.</summary>
    private static void EnsureLineStart()
    {
        if (_outputTracker is { AtLineStart: false })
            Console.WriteLine();
    }

    /// <summary>Advances <paramref name="solutions"/> by one solution while a
    /// background thread watches for <c>ESC</c>; pressing it fires
    /// <paramref name="cts"/>, which the engine observes at its next safe point
    /// and throws <see cref="OperationCanceledException"/>. Returns the
    /// <c>MoveNext</c> result; sets <paramref name="aborted"/> when ESC stopped
    /// the search. With redirected input there is no key to watch, so it just
    /// advances.</summary>
    private static bool MoveNextWatched(QueryRun run, out bool aborted)
    {
        aborted = false;
        if (Console.IsInputRedirected)
            return run.MoveNext();

        // Keep Ctrl+C on the SIGNAL route while a query runs (a debugger attach/detach
        // can leave the console delivering it as a keystroke — see LineEditor.ReadLine).
        try { Console.TreatControlCAsInput = false; }
        catch { /* no interactive console */ }

        using var stop = new ManualResetEventSlim(false);
        var watcher = new Thread(() => WatchForEsc(run, stop))
        {
            IsBackground = true,
            Name = "repl-esc-watch",
        };
        watcher.Start();
        try
        {
            return run.MoveNext();
        }
        catch (OperationCanceledException)
        {
            aborted = true;
            return false;
        }
        finally
        {
            // Stop the watcher and let it release the console before the main
            // thread reads keys again (e.g. the ';' prompt).
            stop.Set();
            watcher.Join(500);
        }
    }

    /// <summary>Polls the console for keystrokes while a query runs. ESC fires
    /// the cancellation source; any other key typed mid-search is dropped (the
    /// REPL doesn't queue type-ahead). Exits when <paramref name="stop"/> is
    /// signalled or the console becomes unavailable.</summary>
    private static void WatchForEsc(QueryRun run, ManualResetEventSlim stop)
    {
        while (!stop.IsSet)
        {
            try
            {
                // A goal is reading user_input from the console: the keys
                // belong to it, not to us.
                if (_engineReadingInput)
                {
                    stop.Wait(25);
                    continue;
                }
                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo k = Console.ReadKey(intercept: true);
                    if (k.Key == ConsoleKey.Escape)
                    {
                        run.Cancel();
                        return;
                    }
                    // Ctrl+C arriving as a KEYSTROKE (post-debugger console-mode skew —
                    // see LineEditor.ReadLine): behave as the signal — terminate.
                    if (k.Key == ConsoleKey.C
                        && (k.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        Console.WriteLine();
                        Environment.Exit(0);
                    }
                    // Any other key during execution → ignore.
                }
                else
                {
                    // Sleep until the next poll or until told to stop — no busy-spin.
                    stop.Wait(25);
                }
            }
            catch
            {
                return; // console not interactive / disposed
            }
        }
    }

    /// <summary>The <c>-h / --help</c> text — same conventions as the rest of the tool
    /// family (shumway-compile, shumway-link, shumway-lib): usage line, a paragraph of
    /// orientation, then the options. The environment variables are part of the
    /// interface (benchmark harnesses and the debugger depend on them), so they are
    /// documented here too.</summary>
    private static void PrintUsage()
    {
        string eof = OperatingSystem.IsWindows() ? "Ctrl-Z" : "Ctrl-D";
        Console.WriteLine(
            "Usage: shumway [options] [file.pl | bundle.shum ...] [-- arg ...]\n"
            + "\n"
            + "Interactive Shumway Prolog top-level. Consults each file named on the\n"
            + "command line (a .pl consults source; a .shum loads a linked bundle), then\n"
            + "reads queries from the console. End a query with '.'; ';' or space for more\n"
            + $"solutions; ESC cancels a running query; 'halt.' or {eof} exits.\n"
            + "\n"
            + "Arguments after `--` are not consulted: they reach the program as the argv\n"
            + "Prolog flag (current_prolog_flag(argv, Argv)) — SWI/GNU convention.\n"
            + "\n"
            + "Options:\n"
            + "  --clpfd               Enable the CLP(FD) library before consulting, so its\n"
            + "                        operators (#=, in, ..) exist when the files parse.\n"
            + "  --clpr                Enable the CLP(R) library before consulting.\n"
            + "                        (clpfd and clpr cannot share one engine.)\n"
            + "  --foreign-dll <path>  Register a .NET assembly whose [PrologPredicate]\n"
            + "                        methods become foreign predicates. Repeatable.\n"
            + "  --native-dll <path>   Load a native C library for `:- native` functions.\n"
            + "                        Repeatable. Same flag names as shumway-link.\n"
            + "  -L, --library-dir <[dialect:]dir>\n"
            + "                        Add a directory searched by use_module(library(X)).\n"
            + "                        An optional leading dialect: prefix (scryer: / swi:)\n"
            + "                        loads that dir's libraries in that dialect — name\n"
            + "                        resolution + double_quotes (ADR-040), e.g.\n"
            + "                        -L scryer:C:/Scryer/lib. Repeatable; also read from\n"
            + "                        SHUMWAY_LIBRARY_PATH (per entry).\n"
            + "  --debug               Compile debuggable and open a debug session; prints\n"
            + "                        the pid so a debugger (VS + the Shumway extension)\n"
            + "                        can attach.\n"
            + "  --debug-wait          --debug, plus hold at startup until a debugger has\n"
            + "                        attached and armed its breakpoints (what an IDE\n"
            + "                        launcher uses).\n"
            + "  --dap <port>          --debug, plus a DAP endpoint on 127.0.0.1:<port>\n"
            + "                        for VS Code (0 picks a free port and prints it).\n"
            + "  --dap-wait <port>     --dap, plus hold at startup until the client has\n"
            + "                        finished configuring its breakpoints (what the\n"
            + "                        VS Code launch uses — no goal can outrun them).\n"
            + "  -g, --goal <goal>     Run a goal after consulting, as if typed at the\n"
            + "                        prompt, then stay in the top level (end the goal\n"
            + "                        with halt to exit instead). With --debug-wait it\n"
            + "                        runs after the debugger has armed its breakpoints.\n"
            + "  -h, --help            Show this message.\n"
            + "\n"
            + "Environment:\n"
            + "  SHUMWAY_GOAL=<goal>.     Run one goal at startup, then exit (scripted runs,\n"
            + "                           profilers that cannot forward stdin).\n"
            + "  SHUMWAY_IL_PROMOTE=<N>   Promote predicates to Tier-1 IL after N calls\n"
            + "                           (default: interpreter only).\n"
            + "  SHUMWAY_TIMING=1         Print a startup-vs-goal wall-clock breakdown to\n"
            + "                           stderr.\n"
            + "  SHUMWAY_DEBUG_LCO=on|off Pin last-call optimisation under --debug.\n"
            + "  SHUMWAY_DEBUG_ACTIVATION=attach\n"
            + "                           Lazy full debug: under --debug the runtime\n"
            + "                           machinery (ports, trail-everything, LCO off)\n"
            + "                           stays OFF — near-release speed — until a\n"
            + "                           debugger actually attaches.\n"
            + "  SHUMWAY_DAP_PORT=<N>     Open the --dap endpoint whenever a debug session\n"
            + "                           opens (any deployment shape, incl. linked exes).\n"
            + "  SHUMWAY_DEBUG_DIAG=1     Verbose debug-session diagnostics on stderr.");
    }

    /// <summary>After a solution, asks whether to search for the next:
    /// ';', space or Tab mean yes (a single keypress when interactive,
    /// SWI-style); anything else stops.
    ///
    /// <para>With redirected/piped input the top-level takes only the FIRST
    /// solution and does not consume any input — every <c>.</c>-terminated
    /// line in the script is an independent query (SWI/GProlog <c>-g</c>
    /// batch behaviour). Reading a line here to check for <c>;</c> used to
    /// eat the query that followed a non-deterministic one (e.g. the line
    /// after <c>member(X,[a,b,c]).</c>), silently dropping it. A script that
    /// wants every solution uses <c>findall/3</c> / <c>forall/2</c>.</para>
    /// </summary>
    private static bool WantsAnotherSolution()
    {
        if (Console.IsInputRedirected)
            return false;
        ConsoleKeyInfo k = Console.ReadKey(intercept: true);
        return k.KeyChar is ';' or ' ' || k.Key == ConsoleKey.Tab;
    }
}
