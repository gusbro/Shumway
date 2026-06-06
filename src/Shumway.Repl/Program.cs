using Shumway.Compiler.Ast;
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
        // --clpfd / --clpr: enable the constraint library BEFORE consulting, so
        // its operators (#=, in, .., {}/1, ...) are in the operator table when
        // the named files are parsed (a `:- use_module(library(clpfd))` directive
        // inside a file is too late — the file is parsed before directives run).
        foreach (string path in consultFiles)
        {
            if (path == "--clpfd") engine.UseClpfd();
            else if (path == "--clpr") engine.UseClpr();
        }
        foreach (string path in consultFiles)
        {
            if (path is "--clpfd" or "--clpr") continue;
            ConsultFile(engine, path);
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

        using var solutions = engine.QueryAll(wrapped).GetEnumerator();
        if (!solutions.MoveNext())
        {
            if (engine.LastHaltExitCode is null)
                Console.WriteLine("false.");
            return;
        }
        while (true)
        {
            Solution solution = solutions.Current;
            int width;
            try { width = Console.WindowWidth; }
            catch { width = 80; }
            if (width < 20) width = 80;
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
            if (!solutions.MoveNext())
            {
                if (engine.LastHaltExitCode is null)
                    Console.WriteLine("false.");
                return;
            }
        }
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

        var lines = new List<string>();
        foreach (string name in userVars)
        {
            Term? val = solution[name];
            if (val is null) continue;
            bool isUnbound = val is VarTerm;
            if (residualsByVar.TryGetValue(name, out var rs))
                foreach (Term g in rs) lines.Add(AstTermRenderer.Render(g, 1200, ops));
            else if (!isUnbound)
                lines.Add($"{name} = {AstTermRenderer.Render(val, 1200, ops)}");
            // Else: unbound var with no residuals — omit (SWI behaviour).
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
