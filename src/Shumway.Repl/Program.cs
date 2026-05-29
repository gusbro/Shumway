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
        engine.Flags.Argv = programArgs;
        // SHUMWAY_IL_PROMOTE=N sets the Tier-1 promotion threshold.
        // Without it, Tier-0 (interpreter) handles every dispatch.
        // With N, predicates that hit N invocations get IL-compiled.
        string? promoteEnv = Environment.GetEnvironmentVariable("SHUMWAY_IL_PROMOTE");
        if (int.TryParse(promoteEnv, out int promoteN) && promoteN > 0)
            engine.IlPromotion.Threshold = promoteN;
        foreach (string path in consultFiles)
            ConsultFile(engine, path);

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
            catch (Exception ex) { Console.WriteLine($"% {ex.GetType().Name}: {ex.Message}"); }
            if (Shumway.Core.Profiler.Enabled)
            {
                long allocAfter = GC.GetTotalAllocatedBytes();
                Console.Error.WriteLine($"[mem] total allocated during query: {(allocAfter - allocBefore) / (1024.0 * 1024):N1} MB");
            }
            Shumway.Core.Profiler.StopRun();
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
                // A parse failure, an uncaught throw/1, or a runtime error.
                Console.WriteLine($"% {ex.GetType().Name}: {ex.Message}");
                if (Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_TRACE") == "1")
                    Console.WriteLine(ex.StackTrace);
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

    /// <summary>Reads one query from standard input, joining lines until a
    /// line ends with the <c>.</c> clause terminator. Returns the empty
    /// string for a blank entry and <c>null</c> at end of input.</summary>
    private static string? ReadQuery()
    {
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            Console.Write(buffer.Length == 0 ? "?- " : "   ");
            string? line = Console.ReadLine();
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
            Console.Write(solution.Bindings.Count == 0 ? "true" : solution.ToString());
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
