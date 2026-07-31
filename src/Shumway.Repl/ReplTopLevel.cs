using System.Linq;
using System.Threading;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;

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
        string[] ownArgs = helpSep < 0 ? args : args[..helpSep];
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
        Console.WriteLine(
            $"Shumway Prolog top-level.  End a query with '.'  —  'halt.' or {eofKey} exits.");

        // Split args at "--": everything before is a file to consult,
        // everything after is exposed to the program as the argv
        // Prolog flag (current_prolog_flag(argv, Argv)). Matches
        // SWI / GNU / SICStus convention.
        int sep = Array.IndexOf(args, "--");
        string[] consultFiles = sep < 0 ? args : args[..sep];
        string[] programArgs = sep < 0 ? Array.Empty<string>() : args[(sep + 1)..];

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
        // Stash the engine reference so the line editor's
        // Tab completer (constructed lazily on first ReadLine) can
        // query for predicate names.
        _replEngine = engine;
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
            string goalText = startupGoalArg.Trim();
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
        switch (ex)
        {
            case ShumwayPrologException pex:
                Console.WriteLine($"% error: {pex.Term}");
                break;
            case Shumway.Core.PrologRuntimeException re:
                Console.WriteLine($"% error: {ErrorRendering.FormatRuntimeError(re)}");
                break;
            default:
                Console.WriteLine($"% {ex.GetType().Name}: {ex.Message}");
                break;
        }

        // Stack trace from the engine — non-empty only for Prolog
        // exception kinds. Skip synthetic launcher frames and the
        // innermost wrapper that just re-throws.
        var trace = engine.LastErrorStackTraceWithPositions;
        if (trace is not null && trace.Count > 0)
        {
            foreach (var f in trace)
            {
                if (f.Name.StartsWith("$", StringComparison.Ordinal)) continue;
                if (f.Position.Line <= 1 && f.Position.Column <= 1 && f.Position.Offset == 0)
                    Console.WriteLine($"%   at {f.Name}/{f.Arity}");
                else
                    Console.WriteLine($"%   at {f.Name}/{f.Arity} ({f.Position})");
            }
        }

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
    private static PrologEngine? _replEngine;
    private static ColumnTrackingWriter? _outputTracker;
    private static LineEditor LineEd => _lineEditor ??=
        new LineEditor(
            new HistoryStore(HistoryStore.DefaultPath()),
            completer: BuildCompleter());

    private static Func<string, IReadOnlyList<string>> BuildCompleter() =>
        prefix => CompletePredicateName(_replEngine, prefix);

    /// <summary>Returns the sorted, deduplicated set of
    /// predicate names that start with <paramref name="prefix"/>.
    /// Sources: every registered builtin (process-wide
    /// <see cref="Shumway.Builtins.BuiltinsRegistry"/>), every user
    /// predicate the engine knows about (each module's clauses +
    /// dynamic functors + precompiled-bundle predicates). Capped to
    /// keep the UI usable when the user hits Tab on an empty / very
    /// short prefix.</summary>
    private static IReadOnlyList<string> CompletePredicateName(
        PrologEngine? engine, string prefix)
    {
        const int Cap = 200;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<string>();
        void Offer(string name)
        {
            if (results.Count >= Cap) return;
            if (string.IsNullOrEmpty(name)) return;
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return;
            if (seen.Add(name)) results.Add(name);
        }

        // Builtins.
        foreach (var b in Shumway.Builtins.BuiltinsRegistry.AllEntries())
            Offer(b.Name);

        // User predicates per module — module-local + module-public +
        // dynamic.
        if (engine is not null)
        {
            foreach (var (_, manifest) in engine.Modules)
            {
                foreach (var clause in manifest.Clauses)
                {
                    string? n = ClauseHeadName(clause);
                    if (n is not null) Offer(n);
                }
                foreach (int fid in manifest.PublicFunctors)
                    Offer(NameOfFunctor(fid));
                foreach (int fid in manifest.DynamicFunctors)
                    Offer(NameOfFunctor(fid));
            }
            foreach (var (fid, _) in engine.PrecompiledStaticPredicates)
                Offer(NameOfFunctor(fid));
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static string? ClauseHeadName(Shumway.Compiler.Ast.Clause clause)
    {
        Shumway.Compiler.Ast.Term head = clause.Term;
        if ((clause.Kind == Shumway.Compiler.Ast.ClauseKind.Rule
             || clause.Kind == Shumway.Compiler.Ast.ClauseKind.DcgRule)
            && head is Shumway.Compiler.Ast.CompoundTerm wrap && wrap.Args.Length == 2)
            head = wrap.Args[0];
        return head switch
        {
            Shumway.Compiler.Ast.AtomTerm a => a.Name,
            Shumway.Compiler.Ast.CompoundTerm c => c.Functor,
            _ => null,
        };
    }

    private static string NameOfFunctor(int fid)
    {
        var (atomId, _) = Shumway.Core.FunctorTable.Lookup(fid);
        return Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
    }

    /// <summary>Reads one query from standard input, joining lines until a
    /// line ends with the <c>.</c> clause terminator. Returns the empty
    /// string for a blank entry and <c>null</c> at end of input.</summary>
    private static string? ReadQuery()
    {
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            string prompt = buffer.Length == 0 ? "?- " : "   ";
            string? line = LineEd.ReadLine(prompt);
            if (line is null)
                return buffer.Length == 0 ? null : buffer.ToString().Trim();
            buffer.Append(line).Append('\n');
            string accumulated = buffer.ToString().Trim();
            if (accumulated.Length == 0) return "";
            if (accumulated.EndsWith('.')) return accumulated;
        }
    }

    // Hidden var names used to smuggle residual constraints out of
    // the wrapped query — long unique strings unlikely to collide
    // with anything a user types. Parsed-input vars must start with
    // an uppercase letter or `_`; these meet that rule.
    private const string ResidualVarName = "_ReplResiduals_8a7b3c";
    private const string CopiesVarName = "_ReplCopies_8a7b3c";

    /// <summary>Runs a query and prints its solutions one at a time.
    /// The query is wrapped with <c>copy_term/3</c> over its named
    /// variables, which collects residual attribute goals (e.g. CLP(FD)
    /// domain constraints) so an unground answer like
    /// <c>?- A #&gt; 5, A #&lt; 10.</c> can print as <c>A in 6..9.</c>
    /// rather than leaving the user with a bare unbound variable.</summary>
    private static void RunQuery(PrologEngine engine, string query)
    {
        // Parse and wrap. If the parse fails, fall through to the
        // string-form QueryAll so the engine produces the same error
        // it always did.
        Term wrapped;
        IReadOnlyList<string> userVars;
        try
        {
            var (goal, vars) = engine.ParseGoal(query);
            userVars = vars;
            if (vars.Count == 0)
            {
                wrapped = goal;
            }
            else
            {
                var varsList = MakeList(vars.Select(n => (Term)new VarTerm(n)).ToArray());
                Term copyTerm = new CompoundTerm("copy_term", new Term[]
                {
                    varsList,
                    new VarTerm(CopiesVarName),
                    new VarTerm(ResidualVarName),
                });
                wrapped = new CompoundTerm(",", new[] { goal, copyTerm });
            }
        }
        catch
        {
            // Parser error — let the engine produce its usual diagnostic.
            using var fallback = engine.QueryAll(query).GetEnumerator();
            if (!fallback.MoveNext())
            {
                if (engine.LastHaltExitCode is null)
                    Console.WriteLine("false.");
            }
            return;
        }

        // ADR-035 — the goal the engine is about to run is not the goal the user typed: it
        // is theirs wrapped in a copy_term/3 so residual constraints can be shown. A
        // debugger names the query frame after what it was handed, so tell it what was
        // actually typed, or the user reads their query back with the top level's plumbing
        // stapled to it.
        engine.QueryLabel = query.TrimEnd().TrimEnd('.');

        // The search runs on this thread; an ESC keypress (watched on a
        // background thread) aborts a long-running query at the next engine
        // safe point. Not instantaneous, but responsive.
        using var cts = new CancellationTokenSource();
        using var solutions = engine.QueryAll(wrapped, cts.Token).GetEnumerator();
        if (!MoveNextWatched(solutions, cts, out bool aborted))
        {
            if (aborted) Console.WriteLine("% Execution aborted.");
            else if (engine.LastHaltExitCode is null) { EnsureLineStart(); Console.WriteLine("false."); }
            return;
        }
        while (true)
        {
            Solution solution = solutions.Current;
            int width;
            try { width = Console.WindowWidth; }
            catch { width = 80; }
            if (width < 20) width = 80;
            // SWI-style: if the goal left the cursor mid-line (e.g. a bare
            // writeq/1 with no trailing nl), start the answer on its own line
            // so the `true`/bindings don't run into the goal's output.
            EnsureLineStart();
            Console.Write(FormatSolutionWithResiduals(engine, solution, userVars, width));
            if (solution.IsLast)
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
            if (!MoveNextWatched(solutions, cts, out aborted))
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
    private static bool MoveNextWatched(
        IEnumerator<Solution> solutions, CancellationTokenSource cts, out bool aborted)
    {
        aborted = false;
        if (Console.IsInputRedirected)
            return solutions.MoveNext();

        // Keep Ctrl+C on the SIGNAL route while a query runs (a debugger attach/detach
        // can leave the console delivering it as a keystroke — see LineEditor.ReadLine).
        try { Console.TreatControlCAsInput = false; }
        catch { /* no interactive console */ }

        using var stop = new ManualResetEventSlim(false);
        var watcher = new Thread(() => WatchForEsc(cts, stop))
        {
            IsBackground = true,
            Name = "repl-esc-watch",
        };
        watcher.Start();
        try
        {
            return solutions.MoveNext();
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
    private static void WatchForEsc(CancellationTokenSource cts, ManualResetEventSlim stop)
    {
        while (!stop.IsSet)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo k = Console.ReadKey(intercept: true);
                    if (k.Key == ConsoleKey.Escape)
                    {
                        cts.Cancel();
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

    /// <summary>Builds a Prolog list AST from a sequence of terms.</summary>
    private static Term MakeList(IList<Term> elements)
    {
        Term tail = new AtomTerm("[]");
        for (int i = elements.Count - 1; i >= 0; i--)
            tail = new CompoundTerm(".", new[] { elements[i], tail });
        return tail;
    }

    /// <summary>Formats a solution: bindings for vars that got values,
    /// residual goals (substituted to mention the original variable
    /// names) for vars that are still attvar-constrained.</summary>
    private static string FormatSolutionWithResiduals(
        PrologEngine engine, Solution solution, IReadOnlyList<string> userVars, int width)
    {
        var ops = engine.Operators;
        if (userVars.Count == 0)
            return solution.Bindings.Count == 0 ? "true" : solution.ToString(width);

        // Build copy-name -> original-name map from the _ReplCopies_ binding
        // (a list `[Copy1, Copy2, ...]` aligned with userVars).
        var copyToOriginal = new Dictionary<string, string>();
        Term? copiesTerm = solution[CopiesVarName];
        int idx = 0;
        Term cursor = copiesTerm ?? new AtomTerm("[]");
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c
               && idx < userVars.Count)
        {
            if (c.Args[0] is VarTerm v)
                copyToOriginal[v.Name] = userVars[idx];
            cursor = c.Args[1];
            idx++;
        }

        // Collect residual goals and substitute copy-vars back to originals.
        var residuals = new List<Term>();
        Term? resTerm = solution[ResidualVarName];
        Term resCursor = resTerm ?? new AtomTerm("[]");
        while (resCursor is CompoundTerm { Functor: ".", Args.Length: 2 } rc)
        {
            residuals.Add(SubstituteVarNames(rc.Args[0], copyToOriginal));
            resCursor = rc.Args[1];
        }

        // For each user var: if it has residuals mentioning it, those
        // replace the binding line; otherwise show the binding (unless
        // the binding is an unbound var with no residuals, in which case
        // skip it — that's what SWI does).
        var residualsByVar = new Dictionary<string, List<Term>>();
        var unattachedResiduals = new List<Term>();
        foreach (Term g in residuals)
        {
            string? owner = FindMentionedUserVar(g, userVars);
            if (owner is null) unattachedResiduals.Add(g);
            else
            {
                if (!residualsByVar.TryGetValue(owner, out var list))
                    residualsByVar[owner] = list = new List<Term>();
                list.Add(g);
            }
        }

        // Cyclic-term display: a cycle that re-enters at the root of a user
        // variable's value renders as that variable — `A = [a, b | A]` — by
        // mapping the materializer's `_C{addr}` marker back to the variable
        // whose value is rooted at that address.
        Dictionary<string, string>? cycleNames = null;
        if (solution.ValueRootAddresses is { } rootAddrs)
            foreach (string name in userVars)
                if (rootAddrs.TryGetValue(name, out int addr))
                    (cycleNames ??= new Dictionary<string, string>())
                        .TryAdd($"_C{addr}", name);

        // SWI-style binding display: user vars whose values are identical are
        // CHAINED — `A = B, B = algo` instead of `A = algo, B = algo` — and
        // two vars sharing one still-unbound variable show their aliasing
        // (`A = B.`) instead of nothing. A lone unbound var stays omitted.
        var renderedValue = new Dictionary<string, string>();
        var groups = new Dictionary<string, List<string>>();
        foreach (string name in userVars)
        {
            Term? val = solution[name];
            if (val is null || residualsByVar.ContainsKey(name)) continue;
            if (cycleNames is not null) val = SubstituteVarNames(val, cycleNames);
            string key = AstTermRenderer.Render(val, 1200, ops);
            renderedValue[name] = key;
            if (!groups.TryGetValue(key, out var members))
                groups[key] = members = new List<string>();
            members.Add(name);
        }

        var lines = new List<string>();
        var groupEmitted = new HashSet<string>();
        foreach (string name in userVars)
        {
            if (residualsByVar.TryGetValue(name, out var rs))
            {
                foreach (Term g in rs) lines.Add(AstTermRenderer.Render(g, 1200, ops));
                continue;
            }
            if (!renderedValue.TryGetValue(name, out string? key)
                || !groupEmitted.Add(key))
                continue;   // no value, or its group was already emitted
            var members = groups[key];
            for (int i = 0; i + 1 < members.Count; i++)
                lines.Add($"{members[i]} = {members[i + 1]}");
            // The last member carries the value — unless the shared value is
            // itself an unbound variable (the chain alone says it all).
            if (solution[members[^1]] is not VarTerm)
                lines.Add($"{members[^1]} = {key}");
        }
        foreach (Term g in unattachedResiduals)
            lines.Add(AstTermRenderer.Render(g, 1200, ops));

        if (lines.Count == 0) return "true";
        return string.Join(",\n", lines);
    }

    /// <summary>Returns the first userVars name that appears in
    /// <paramref name="term"/>, or <c>null</c> if none does.</summary>
    private static string? FindMentionedUserVar(Term term, IReadOnlyList<string> userVars)
    {
        switch (term)
        {
            case VarTerm v:
                return userVars.Contains(v.Name) ? v.Name : null;
            case CompoundTerm c:
                foreach (Term a in c.Args)
                {
                    string? r = FindMentionedUserVar(a, userVars);
                    if (r is not null) return r;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>Returns <paramref name="term"/> with every <see cref="VarTerm"/>
    /// whose name is a key in <paramref name="renames"/> replaced by a
    /// <see cref="VarTerm"/> carrying the mapped name. Other terms are
    /// returned as-is (or rebuilt structurally for compounds).</summary>
    private static Term SubstituteVarNames(Term term, IReadOnlyDictionary<string, string> renames)
    {
        switch (term)
        {
            case VarTerm v when renames.TryGetValue(v.Name, out string? newName):
                return new VarTerm(newName);
            case CompoundTerm c:
                var newArgs = new Term[c.Args.Length];
                bool changed = false;
                for (int i = 0; i < c.Args.Length; i++)
                {
                    newArgs[i] = SubstituteVarNames(c.Args[i], renames);
                    if (!ReferenceEquals(newArgs[i], c.Args[i])) changed = true;
                }
                return changed ? new CompoundTerm(c.Functor, newArgs) : term;
            default:
                return term;
        }
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
