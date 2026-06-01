using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.Repl;

/// <summary>
/// A minimal interactive top-level (REPL) for Shumway — Phase 5. It
/// consults any files named on the command line, then reads queries from
/// standard input, runs each, and prints its solutions; pressing ';'
/// after a solution searches for the next. The session ends at
/// <c>halt.</c> or end of input.
///
/// <para>This is a thin client over the <see cref="PrologEngine"/>
/// embedding API — its purpose is interactive exercising of Shumway, not
/// to be a full-featured development environment.</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
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

        var engine = new PrologEngine();
        // Chunk 250: stash the engine reference so the line editor's
        // Tab completer (constructed lazily on first ReadLine) can
        // query for predicate names.
        _replEngine = engine;
        engine.Flags.Argv = programArgs;
        // SHUMWAY_IL_PROMOTE=N sets the Tier-1 promotion threshold.
        // Without it, Tier-0 (interpreter) handles every dispatch.
        // With N, predicates that hit N invocations get IL-compiled.
        string? promoteEnv = Environment.GetEnvironmentVariable("SHUMWAY_IL_PROMOTE");
        if (int.TryParse(promoteEnv, out int promoteN) && promoteN > 0)
            engine.IlPromotion.Threshold = promoteN;
        if (stopwatch is not null)
            setupMsAtConsultStart = stopwatch.ElapsedMilliseconds;
        foreach (string path in consultFiles)
            ConsultFile(engine, path);
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

    /// <summary>Chunk 251 — REPL-side error renderer. Distinguishes
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
    /// debug info (chunks 144+). <c>SHUMWAY_DEBUG_TRACE=1</c>
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

    /// <summary>Phase 20: prints the execution profile to stderr after a
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
            }
            else
            {
                engine.ConsultString(File.ReadAllText(path));
                Console.WriteLine($"% consulted {path}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"% could not consult {path}: {ex.Message}");
        }
    }

    /// <summary>Chunk 249 — shared line editor, lazily created on
    /// first use so the SHUMWAY_GOAL / scripted entry paths that
    /// never read interactive lines don't even touch disk to load
    /// history. Chunk 250 — the engine reference is captured so the
    /// editor's Tab handler can query for completion candidates.</summary>
    private static LineEditor? _lineEditor;
    private static PrologEngine? _replEngine;
    private static LineEditor LineEd => _lineEditor ??=
        new LineEditor(
            new HistoryStore(HistoryStore.DefaultPath()),
            completer: BuildCompleter());

    private static Func<string, IReadOnlyList<string>> BuildCompleter() =>
        prefix => CompletePredicateName(_replEngine, prefix);

    /// <summary>Chunk 250 — returns the sorted, deduplicated set of
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

    /// <summary>Runs a query and prints its solutions one at a time.</summary>
    private static void RunQuery(PrologEngine engine, string query)
    {
        using var solutions = engine.QueryAll(query).GetEnumerator();
        if (!solutions.MoveNext())
        {
            // No solutions — print false, unless the goal was `halt`
            // (which Main detects via LastHaltExitCode).
            if (engine.LastHaltExitCode is null)
                Console.WriteLine("false.");
            return;
        }
        while (true)
        {
            Solution solution = solutions.Current;
            // Chunk 252 — pretty-print the bindings using the
            // terminal width so long terms break across lines.
            int width;
            try { width = Console.WindowWidth; }
            catch { width = 80; }
            if (width < 20) width = 80;
            Console.Write(solution.Bindings.Count == 0 ? "true" : solution.ToString(width));
            // The engine reports when no choice point remains: this is
            // the last solution, so finish with '.' and no ';' prompt —
            // matching SWI / GNU / SICStus. member(A,[x,y]) stops at
            // `A = y.`, and `A = x, !` finishes at once.
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
            if (!solutions.MoveNext())
            {
                if (engine.LastHaltExitCode is null)
                    Console.WriteLine("false.");
                return;
            }
        }
    }

    /// <summary>After a solution, asks whether to search for the next:
    /// ';' means yes. A single keypress when interactive, a whole line
    /// when input is redirected — so the top-level stays scriptable.</summary>
    private static bool WantsAnotherSolution()
    {
        if (Console.IsInputRedirected)
        {
            string? line = Console.ReadLine();
            return line is not null && line.TrimStart().StartsWith(';');
        }
        return Console.ReadKey(intercept: true).KeyChar == ';';
    }
}
