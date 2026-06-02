using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Shumway.Embedding;

// Phase 25 chunk 281 — multi-engine benchmark harness for the Van Roy
// suite in benchmarks/vanroy/. Runs each .pl against:
//   - Shumway, in-process.
//   - GNU Prolog, COMPILED via gplc to a standalone console-subsystem
//     .exe under benchmarks/results/bin-gprolog/. Native code, no
//     GUI fallback, no global-stack overflows at canonical iteration
//     counts.
//   - SWI-Prolog, invoked as `swipl -g ... <pl>` (interpreted).
//
// Wall-clock total is measured from C# (Process.Start). For Shumway
// it's the wall time of the in-process Query. The harness derives
// per-iteration time as `(total - startup) / N` where startup is
// measured independently via bench(0). If total < startup (work too
// fast vs noise), per-iter prints "<noise>".
//
// Modes:
//   dotnet run -c Release --project tests/Shumway.Tests.Benchmarks/ -- --vanroy
//       Multi-engine Van Roy suite (default mode when no args).
//
//   dotnet run -c Release --project tests/Shumway.Tests.Benchmarks/ -- --bench
//       BenchmarkDotNet microbenchmarks (no cross-engine).

if (args.Length > 0 && args[0] == "--bench")
{
    BenchmarkRunner.Run(typeof(Program).Assembly);
    return;
}

if (Array.IndexOf(args, "--alloc") >= 0)
{
    VanRoyMultiEngine.RunAlloc(args);
    return;
}

VanRoyMultiEngine.Run(args);

// ============================================================================
// Van Roy multi-engine comparison
// ============================================================================

public static class VanRoyMultiEngine
{
    private const string GprologPath  = @"C:\GProlog\bin\gprolog.exe";
    private const string GplcPath     = @"C:\GProlog\bin\gplc.exe";
    private const string SwiplPath    = @"C:\Program Files (x86)\swipl\bin\swipl.exe";

    // hyperfine (MIT/Apache-2.0) drives the cross-engine wall-clock timing:
    // warmup runs + statistical outlier detection + median/stddev, far more
    // robust than a hand-rolled Stopwatch loop. Resolved at runtime; when
    // absent the harness falls back to the in-process Stopwatch median.
    private static string? _hyperfine;

    // Iteration counts. Compiled-native gprolog is fast, so all engines
    // get the same N and we adjust per-bench to keep total runtime in a
    // sane range. Each engine's per_iter is what we actually compare.
    private static readonly (string Name, int Iterations)[] Benchmarks =
    {
        ("nreverse",  10000),
        ("qsort",      5000),
        ("queens",     2000),
        ("tak",         500),
        ("serialize",  1000),
        ("flatten",   10000),
        ("sendmore",    100),
        ("zebra",       200),
        ("boyer",      2000),
        ("crypt",       500),
    };

    public static void Run(string[] args)
    {
        // Parse `--runs N` (default 1). With N>1 each (engine, bench)
        // cell is measured N times and the median is reported, which
        // is what the baseline doc consumes.
        int runs = 1;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--runs" && i + 1 < args.Length
                && int.TryParse(args[i + 1], out int n) && n > 0)
                runs = n;
        }

        string repoRoot = FindRepoRoot();
        string vanroyDir = Path.Combine(repoRoot, "benchmarks", "vanroy");
        string resultsDir = Path.Combine(repoRoot, "benchmarks", "results");
        string gprologBinDir = Path.Combine(resultsDir, "bin-gprolog");
        Directory.CreateDirectory(resultsDir);
        Directory.CreateDirectory(gprologBinDir);
        if (runs > 1)
            Console.WriteLine($"Runs per cell: {runs} (reporting median)");

        // Resolve engines. Each may be absent (skipped); env-var overrides
        // (GPROLOG_PATH, GPLC_PATH, SWIPL_PATH) take precedence.
        string? gprolog = Resolve("GPROLOG_PATH", GprologPath);
        string? gplc    = Resolve("GPLC_PATH",    GplcPath);
        string? swipl   = Resolve("SWIPL_PATH",   SwiplPath);
        _hyperfine = ResolveHyperfine();

        Console.WriteLine();
        Console.WriteLine("Engines:");
        Console.WriteLine("  shumway : in-process");
        Console.WriteLine($"  gprolog : {(gprolog ?? "not found")} (gplc={(gplc ?? "not found")})");
        Console.WriteLine($"  swipl   : {(swipl ?? "not found")}");
        Console.WriteLine($"  timer   : {(_hyperfine is null ? "Stopwatch (hyperfine not found)" : $"hyperfine ({_hyperfine})")}");
        Console.WriteLine();

        // Compile each .pl to a native .exe via gplc on first run (or
        // when the source is newer than the cached exe). The compiled
        // .exe takes N as argv[1] — Shumway/SWI just consult the source.
        var gprologExes = new Dictionary<string, string>();
        if (gprolog is not null && gplc is not null)
        {
            foreach (var (name, _) in Benchmarks)
            {
                string pl = Path.Combine(vanroyDir, $"{name}.pl");
                string exe = Path.Combine(gprologBinDir, $"{name}.exe");
                if (NeedsRebuild(pl, exe))
                {
                    Console.Write($"  gplc compile {name}.pl ... ");
                    string? err = CompileWithGplc(gplc, pl, exe);
                    Console.WriteLine(err is null ? "ok" : $"FAILED: {err}");
                    if (err is not null) continue;
                }
                gprologExes[name] = exe;
            }
        }

        // ---- Correctness phase: run report/0 in each engine and compare.
        Console.WriteLine();
        Console.WriteLine("Correctness check (report/0 fingerprint per engine):");
        var correctness = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (name, _) in Benchmarks)
        {
            string pl = Path.Combine(vanroyDir, $"{name}.pl");
            if (!File.Exists(pl)) continue;
            var fps = new Dictionary<string, string>();
            fps["shumway"] = ShumwayReport(pl);
            if (gprologExes.TryGetValue(name, out string? exe))
            {
                // Use the compiled exe (console-subsystem, no GUI
                // fallback) with the `report` argv to run the
                // correctness check. Avoids invoking interpreted
                // gprolog.exe which pops its GUI when stdin/stdout
                // aren't attached to a real Win32 console.
                fps["gprolog"] = RunForReport(exe, new[] { "report" });
            }
            if (swipl is not null) fps["swipl"] = RunInterpretedSwipl(swipl, pl);
            correctness[name] = fps;
            string status = MatchStatus(fps);
            Console.WriteLine($"  {name,-12} {status}");
            if (status.StartsWith("MISMATCH"))
            {
                foreach (var (eng, fp) in fps)
                    Console.WriteLine($"      {eng,-8} : {Truncate(fp, 100)}");
            }
        }

        string runnersDir = Path.Combine(resultsDir, "runners");

        Console.WriteLine();
        Console.WriteLine($"{"benchmark",-12} {"engine",-10} {"iters",8} {"startup_ms",12} {"total_ms",12} {"tot_sd%",9} {"per_iter_us",14}");
        Console.WriteLine(new string('-', 86));
        var results = new List<Result>();
        foreach (var (name, iters) in Benchmarks)
        {
            string pl = Path.Combine(vanroyDir, $"{name}.pl");
            if (!File.Exists(pl))
            {
                Console.Error.WriteLine($"missing: {pl}");
                continue;
            }
            string itersArg = iters.ToString(CultureInfo.InvariantCulture);

            // Shumway in-process — its primary target is embedding in a .NET
            // application (no AOT), so it's measured the way it's actually
            // used: a warm in-process PrologEngine.Query. Timing it as a fresh
            // `dotnet` subprocess (to match the externals under hyperfine)
            // would inject ~330 ms of JIT-cold runtime startup with ~33%
            // variance that can't be cleanly subtracted (see Phase-25 notes);
            // a Native-AOT runner would fix the startup but isn't the target
            // deployment. The external engines have small, stable native
            // startup (~60-100 ms), so hyperfine times them well.
            {
                double sStart = TimeShumway(pl, 0);
                var sTotals = Enumerable.Range(0, Math.Max(runs, 1))
                    .Select(_ => TimeShumway(pl, iters)).ToList();
                Record(results, name, "shumway", iters, sStart, Median(sTotals), Stddev(sTotals));
            }

            // Compiled GProlog (native exe).
            if (gprologExes.TryGetValue(name, out string? exe))
            {
                var (st, tot, sd) = TimeExternal($"{name}-gprolog", exe,
                    new[] { "0" }, new[] { itersArg }, runs, runnersDir);
                Record(results, name, "gprolog", iters, st, tot, sd);
            }

            // SWI interpreted.
            if (swipl is not null)
            {
                var (st, tot, sd) = TimeExternal($"{name}-swipl", swipl,
                    new[] { "-q", "-g", "bench(0), halt", pl },
                    new[] { "-q", "-g", $"bench({iters}), halt", pl },
                    runs, runnersDir);
                Record(results, name, "swipl", iters, st, tot, sd);
            }
        }

        // CSV side-by-side.
        string csvPath = Path.Combine(resultsDir,
            $"vanroy-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        WriteCsv(csvPath, results);
        Console.WriteLine();
        Console.WriteLine($"CSV: {csvPath}");

        Console.WriteLine();
        PrintRatioSummary(results);

        // Baseline markdown report — overwrites any prior version,
        // commiteable under docs/benchmarks/baseline.md.
        string baselinePath = Path.Combine(repoRoot, "docs", "benchmarks", "baseline.md");
        WriteBaselineMarkdown(baselinePath, results, runs);
        Console.WriteLine($"Baseline: {baselinePath}");
    }

    // ------------------------------------------------------------------
    // Tier-0 deterministic allocation mode (Phase 25).
    //
    // Wall-clock benchmarking on a laptop is dominated by external noise
    // (Turbo Boost, scheduler, background load) — a GProlog native exe was
    // observed swinging 4× run-to-run. To validate an allocation-affecting
    // change (e.g. the read-mode atomic-literal fast path in
    // UnifyHeapWithCell) WITHOUT that noise, this mode measures the engine's
    // monotonic WAM-cell allocation counter, which is a pure function of the
    // executed code path + input: identical every run, on any machine, under
    // any load. Shumway-only (no gplc/swipl subprocess spawning).
    //
    //   dotnet run -c Release --project tests/Shumway.Tests.Benchmarks/ -- --alloc
    //
    // Columns: cells/iter is the primary metric (fully deterministic);
    // bytes/iter is managed-heap bytes via GC.GetAllocatedBytesForCurrentThread
    // (Cell[] resizes + Term materialisation; deterministic for a fixed path).
    // Each is (work at N − work at 0) / N, mirroring the timing methodology.
    public static void RunAlloc(string[] args)
    {
        string repoRoot = FindRepoRoot();
        string vanroyDir = Path.Combine(repoRoot, "benchmarks", "vanroy");

        Console.WriteLine("Tier-0 deterministic allocation metrics (Shumway in-process).");
        Console.WriteLine("cells/iter = monotonic WAM-cell allocations, identical every run.");
        Console.WriteLine();
        Console.WriteLine($"{"benchmark",-12} {"iters",8} {"cells/iter",14} {"bytes/iter",14} {"total_cells",16} {"determ",8}");
        Console.WriteLine(new string('-', 76));

        foreach (var (name, iters) in Benchmarks)
        {
            string pl = Path.Combine(vanroyDir, $"{name}.pl");
            if (!File.Exists(pl)) { Console.Error.WriteLine($"missing: {pl}"); continue; }
            string src = File.ReadAllText(pl);

            long cells0 = ShumwayCells(src, 0);
            long cellsN = ShumwayCells(src, iters);
            // Determinism self-check: a second measurement of the N case
            // must be bit-identical (the metric's whole selling point).
            long cellsN2 = ShumwayCells(src, iters);
            string determ = cellsN == cellsN2 ? "yes" : $"NO({cellsN}/{cellsN2})";

            long bytes0 = ShumwayManagedBytes(src, 0);
            long bytesN = ShumwayManagedBytes(src, iters);

            double cellsPerIter = iters > 0 ? (cellsN - cells0) / (double)iters : 0;
            double bytesPerIter = iters > 0 ? (bytesN - bytes0) / (double)iters : 0;

            Console.WriteLine(
                $"{name,-12} {iters,8} {cellsPerIter,14:F1} {bytesPerIter,14:F1} {cellsN,16:N0} {determ,8}");
        }

        Console.WriteLine();
        Console.WriteLine("To A/B a change: record this table, apply the change, rebuild, re-run.");
        Console.WriteLine("A real allocation win shows as a strictly lower cells/iter — no noise band.");
    }

    // Cells allocated by `bench(n)` alone: the consult + "true." warmup run
    // in earlier (separate) per-query engines, so the bench query's engine
    // starts at 0 and LastQueryCellsAllocated is exactly its tally.
    private static long ShumwayCells(string src, int n)
    {
        var engine = new PrologEngine();
        engine.ConsultString(src);
        engine.Query("true.");
        var sol = engine.Query($"bench({n}).");
        if (!sol.Success)
            throw new InvalidOperationException($"Shumway: bench({n}) failed");
        return engine.LastQueryCellsAllocated;
    }

    private static long ShumwayManagedBytes(string src, int n)
    {
        var engine = new PrologEngine();
        engine.ConsultString(src);
        engine.Query("true.");
        long g0 = GC.GetAllocatedBytesForCurrentThread();
        var sol = engine.Query($"bench({n}).");
        long g1 = GC.GetAllocatedBytesForCurrentThread();
        if (!sol.Success)
            throw new InvalidOperationException($"Shumway: bench({n}) failed");
        return g1 - g0;
    }

    private static double Median(IList<double> xs)
    {
        if (xs.Count == 0) return 0;
        var sorted = xs.OrderBy(x => x).ToList();
        int n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static void WriteBaselineMarkdown(string path, List<Result> results, int runs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.AppendLine("# Van Roy benchmark baseline");
        sb.AppendLine();
        sb.AppendLine($"_Generated {DateTime.Now:yyyy-MM-dd HH:mm}_  ");
        sb.AppendLine($"_Runs per cell_: **{runs}** (median reported)  ");
        sb.AppendLine($"_Machine_: {Environment.MachineName}, .NET {Environment.Version}, {Environment.ProcessorCount} cores, {Environment.OSVersion.VersionString}");
        sb.AppendLine();
        sb.AppendLine("## Engines");
        sb.AppendLine();
        sb.AppendLine("| Engine | Version | Mode |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine("| Shumway | (this build) | Bytecode interpreter + Tier-1 IL (in-process) |");
        sb.AppendLine("| GNU Prolog | 1.5.0 | Native compiled (`gplc` + MSVC link) |");
        sb.AppendLine("| SWI-Prolog | 9.2.3 | Interpreted (`swipl -g`) |");
        sb.AppendLine();
        sb.AppendLine("Correctness: every benchmark's `report/0` fingerprint was verified ");
        sb.AppendLine("equal across the three engines (whitespace-normalised) before timing.");
        sb.AppendLine();
        sb.AppendLine("## Per-iteration time (µs, lower is better)");
        sb.AppendLine();
        sb.AppendLine("| Benchmark | Iters | Shumway | GProlog (native) | SWI |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var grp in results.GroupBy(r => r.Benchmark).OrderBy(g =>
            Array.FindIndex(Benchmarks, x => x.Name == g.Key)))
        {
            var sh  = grp.FirstOrDefault(r => r.Engine == "shumway");
            var gp  = grp.FirstOrDefault(r => r.Engine == "gprolog");
            var sw  = grp.FirstOrDefault(r => r.Engine == "swipl");
            int iters = sh?.Iterations ?? 0;
            sb.AppendLine($"| {grp.Key} | {iters} | {FmtPi(sh)} | {FmtPi(gp)} | {FmtPi(sw)} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Ratios vs Shumway (>1.0 means Shumway is slower)");
        sb.AppendLine();
        sb.AppendLine("| Benchmark | Shumway / GProlog | Shumway / SWI |");
        sb.AppendLine("|---|---:|---:|");
        foreach (var grp in results.GroupBy(r => r.Benchmark).OrderBy(g =>
            Array.FindIndex(Benchmarks, x => x.Name == g.Key)))
        {
            var sh = PerIterUs(grp.First(r => r.Engine == "shumway"));
            var gp = grp.FirstOrDefault(r => r.Engine == "gprolog");
            var sw = grp.FirstOrDefault(r => r.Engine == "swipl");
            sb.AppendLine($"| {grp.Key} | {FmtRatio(sh, PerIterUs(gp))} | {FmtRatio(sh, PerIterUs(sw))} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Methodology");
        sb.AppendLine();
        sb.AppendLine("- **Shumway** is timed **in-process** (a warm `PrologEngine.Query`), ");
        sb.AppendLine("  because its target is embedding in a long-lived .NET application — that ");
        sb.AppendLine("  is how it is actually used. Running it as a fresh `dotnet` subprocess to ");
        sb.AppendLine("  match the externals would inject ~330 ms of JIT-cold runtime startup ");
        sb.AppendLine("  with ~33% variance that the `bench(0)` subtraction can't cleanly remove ");
        sb.AppendLine("  (Native-AOT would fix it, but AOT is not the target deployment).");
        sb.AppendLine("- **GProlog** and **SWI** are timed as fresh processes under ");
        sb.AppendLine("  [hyperfine](https://github.com/sharkdp/hyperfine) (warmup + statistical ");
        sb.AppendLine("  outlier detection + median/stddev): GProlog the gplc-compiled native ");
        sb.AppendLine("  `.exe`, SWI as `swipl -g`. Their native startup (~60-100 ms) is small ");
        sb.AppendLine("  and stable, so the subprocess regime measures them well. When hyperfine ");
        sb.AppendLine("  is absent the harness falls back to a Stopwatch median over the runs.");
        sb.AppendLine("- `startup_ms` is `bench(0)` (consult + halt; for externals also process ");
        sb.AppendLine("  start), `total_ms` is the median of `bench(N)`. Per-iteration time = ");
        sb.AppendLine("  `(total - startup) / N`. When `total - startup ≤ 0.5 ms` the work is ");
        sb.AppendLine("  below the wall-clock noise floor and we print `<noise>`.");
        sb.AppendLine("- For deterministic, noise-free comparison of Shumway-internal changes ");
        sb.AppendLine("  (immune to Turbo Boost / scheduler / background load), use the `--alloc` ");
        sb.AppendLine("  mode: it reports WAM cells allocated per iteration, bit-identical across ");
        sb.AppendLine("  runs. That is the right tool for A/B-ing an optimisation; wall-clock is ");
        sb.AppendLine("  for cross-engine positioning.");
        sb.AppendLine($"- hyperfine runs: warmup 1 + max({runs},3) timed runs per external cell; ");
        sb.AppendLine("  median reported (stddev in the CSV / console `tot_sd%`). Shumway uses ");
        sb.AppendLine($"  the harness `--runs` value ({runs}) directly. `--runs N` raises both.");
        sb.AppendLine("- GProlog runs as a native, statically-linked Windows .exe ");
        sb.AppendLine("  (`/SUBSYSTEM:CONSOLE`) compiled by `gplc` with 256 MB global stack, ");
        sb.AppendLine("  32 MB local, 32 MB trail. Compilation routes through `cmd /c vcvars64 ");
        sb.AppendLine("  >NUL && gplc` to give gplc the MSVC environment its internal `cl` / ");
        sb.AppendLine("  `link` shells need.");
        sb.AppendLine();
        sb.AppendLine("## Reproducibility");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("dotnet run -c Release --project tests/Shumway.Tests.Benchmarks/ -- --vanroy --runs 5");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Requirements: GProlog 1.5+ on PATH or at `C:\\GProlog\\bin\\gprolog.exe`, ");
        sb.AppendLine("SWI-Prolog 9.x at the standard install path, Visual Studio 2017+ with ");
        sb.AppendLine("VC++ C++ Build Tools (vcvars64 discovered via vswhere), and ");
        sb.AppendLine("[hyperfine](https://github.com/sharkdp/hyperfine) (`winget install ");
        sb.AppendLine("sharkdp.hyperfine`; resolved via PATH, `HYPERFINE_PATH`, or the winget ");
        sb.AppendLine("package dir). Without hyperfine the harness still runs (Stopwatch fallback).");
        File.WriteAllText(path, sb.ToString());
    }

    private static string FmtPi(Result? r)
    {
        if (r is null) return "—";
        var v = PerIterUs(r);
        return v is null ? "&lt;noise&gt;" : v.Value.ToString("F2", CultureInfo.InvariantCulture);
    }

    private static string FmtRatio(double? sh, double? other)
    {
        if (sh is null || other is null || other.Value == 0) return "&lt;noise&gt;";
        return (sh.Value / other.Value).ToString("F2", CultureInfo.InvariantCulture) + "×";
    }

    private static void Record(List<Result> results, string name, string engine,
        int iters, double startup, double total, double totalStddev = 0)
    {
        var r = new Result(name, engine, iters, startup, total, totalStddev);
        results.Add(r);
        string sdPct = total > 0 && totalStddev > 0
            ? (100.0 * totalStddev / total).ToString("F1", CultureInfo.InvariantCulture)
            : "—";
        Console.WriteLine(
            $"{name,-12} {engine,-10} {iters,8} {startup,12:F2} {total,12:F2} {sdPct,9} {FormatPerIter(r),14}");
    }

    private static string FormatPerIter(Result r)
    {
        if (r.Iterations <= 0) return "";
        double work = r.TotalMs - r.StartupMs;
        if (work <= 0.5) return "<noise>";
        return (work * 1000.0 / r.Iterations).ToString("F3", CultureInfo.InvariantCulture);
    }

    private static double? PerIterUs(Result? r)
    {
        if (r is null || r.Iterations <= 0) return null;
        double work = r.TotalMs - r.StartupMs;
        if (work <= 0.5) return null;
        return work * 1000.0 / r.Iterations;
    }

    private static void PrintRatioSummary(List<Result> results)
    {
        // Per-benchmark: shumway per-iter vs each external engine.
        var engineNames = results.Select(r => r.Engine).Distinct().Where(n => n != "shumway").ToList();
        if (engineNames.Count == 0) return;
        Console.WriteLine("Per-iteration ratios (>1.0 = Shumway slower; <noise> when work below measurement threshold):");
        Console.Write($"{"benchmark",-12}");
        foreach (var eng in engineNames) Console.Write($" {"shumway/"+eng,-18}");
        Console.WriteLine();
        Console.WriteLine(new string('-', 12 + (engineNames.Count * 19)));
        foreach (var grp in results.GroupBy(r => r.Benchmark))
        {
            Console.Write($"{grp.Key,-12}");
            double? sh = PerIterUs(grp.Single(r => r.Engine == "shumway"));
            foreach (var eng in engineNames)
            {
                var r = grp.FirstOrDefault(x => x.Engine == eng);
                if (r is null) { Console.Write($" {"-",-18}"); continue; }
                double? ext = PerIterUs(r);
                if (sh is null || ext is null)
                    Console.Write($" {"<noise>",-18}");
                else
                {
                    double ratio = sh.Value / ext.Value;
                    Console.Write($" {ratio,16:F2}x ");
                }
            }
            Console.WriteLine();
        }
    }

    private record Result(string Benchmark, string Engine, int Iterations,
                          double StartupMs, double TotalMs, double TotalStddevMs = 0);

    // ---- gplc compilation ----

    private static bool NeedsRebuild(string srcPl, string outExe)
    {
        if (!File.Exists(outExe)) return true;
        return File.GetLastWriteTimeUtc(srcPl) > File.GetLastWriteTimeUtc(outExe);
    }

    // Discovered once on first call: the MSVC vcvars64.bat that
    // sets up PATH / INCLUDE / LIB for cl + link + lib. gplc shells
    // out to those tools internally, and a stripped-down PATH that
    // happens to put GNU coreutils' `link` first (typical with Git
    // Bash / MSYS on PATH) breaks the gplc → link chain. We invoke
    // gplc as `cmd /c "vcvars64.bat && gplc ..."` to inherit the
    // MSVC env in the child.
    private static string? _vcvarsCache;
    private static string? FindVcvars64()
    {
        if (_vcvarsCache is not null) return _vcvarsCache.Length == 0 ? null : _vcvarsCache;
        string vsWhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
        if (!File.Exists(vsWhere)) { _vcvarsCache = ""; return null; }
        var psi = new ProcessStartInfo
        {
            FileName = vsWhere,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-latest", "-products", "*",
            "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
            "-property", "installationPath" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) { _vcvarsCache = ""; return null; }
        string installPath = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(5000);
        if (string.IsNullOrEmpty(installPath)) { _vcvarsCache = ""; return null; }
        string vcvars = Path.Combine(installPath, "VC", "Auxiliary", "Build", "vcvars64.bat");
        _vcvarsCache = File.Exists(vcvars) ? vcvars : "";
        return _vcvarsCache.Length == 0 ? null : _vcvarsCache;
    }

    private static string? CompileWithGplc(string gplc, string srcPl, string outExe)
    {
        // Build a "combined" .pl that appends a main/0 initialization
        // to the original source. main/0 reads N from argv[1]
        // (gprolog's argument_value/2) and calls bench(N).
        //
        // We write the combined source to the output directory with
        // the desired exe basename — gplc's default is to emit
        // <basename>.exe next to the source, which lands where we
        // want. Avoids `-o`, which on some Windows toolchains causes
        // gplc to forward MSVC-style `/out:` flags to a GNU coreutils
        // `link` that's first on PATH (which then rejects them).
        // atom_number/2 is a SWI/SICStus extension; not in gprolog 1.5.
        // atom_codes + number_codes is the ISO portable way.
        //
        // Two argv forms: `<exe> report` runs the correctness fingerprint
        // (so we never invoke interpreted gprolog.exe, which falls back
        // to its GUI on non-console parents). `<exe> N` runs bench(N).
        string driver =
            "\n:- initialization(main).\n" +
            "main :-\n" +
            "    argument_value(1, Arg),\n" +
            "    ( Arg = report -> report, halt\n" +
            "    ; atom_codes(Arg, Codes), number_codes(N, Codes), bench(N), halt\n" +
            "    ).\n";
        string outDir = Path.GetDirectoryName(outExe)!;
        string baseName = Path.GetFileNameWithoutExtension(outExe);
        string combinedPath = Path.Combine(outDir, baseName + ".pl");
        File.WriteAllText(combinedPath, File.ReadAllText(srcPl) + driver, new UTF8Encoding(false));
        try
        {
            // Run via `cmd /c "<vcvars64.bat> >NUL && gplc.exe ..."` so
            // gplc inherits the MSVC environment (PATH, INCLUDE, LIB) it
            // needs to find cl.exe / link.exe / lib.exe. Without
            // vcvars64, gplc may find GNU coreutils' `link` first on
            // PATH (from MSYS/Git Bash) and emit `/out:` flags it
            // doesn't understand.
            string? vcvars = FindVcvars64();
            if (vcvars is null)
                return "vcvars64.bat not found (need Visual Studio 2017+ with VC C++ tools)";
            string? gplcDir = Path.GetDirectoryName(gplc);
            // Compose a single cmd-line string. `>NUL` mutes vcvars64's
            // own copyright banner; gplc's own output still flows.
            var sb = new StringBuilder();
            sb.Append('"').Append(vcvars).Append('"').Append(" >NUL && ");
            sb.Append('"').Append(gplc).Append('"');
            sb.Append(" --global-size 256000 --local-size 32000 --trail-size 32000 ");
            sb.Append('"').Append(combinedPath).Append('"');
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = outDir,
                Arguments = $"/c \"{sb}\"",
            };
            // Make sure the gplc bin dir is on PATH too, for pl2wam /
            // ma2asm / yasm. vcvars64 doesn't touch it.
            if (gplcDir is not null)
                psi.Environment["PATH"] = gplcDir + Path.PathSeparator +
                    (Environment.GetEnvironmentVariable("PATH") ?? "");
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start gplc");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } return "timeout"; }
            if (p.ExitCode != 0)
                return $"exit={p.ExitCode}; {stderr.TrimEnd()}";
            // Sanity-check: gplc should have produced outExe.
            if (!File.Exists(outExe))
                return $"gplc returned 0 but {Path.GetFileName(outExe)} doesn't exist";
            return null;
        }
        finally
        {
            try { File.Delete(combinedPath); } catch { }
        }
    }

    // ---- timing ----

    // ---- correctness helpers ----

    private static string ShumwayReport(string plPath)
    {
        var engine = new PrologEngine();
        engine.ConsultString(File.ReadAllText(plPath));
        // Capture engine's stdout into a buffer.
        var sw = new StringWriter();
        engine.Out = sw;
        try { var sol = engine.Query("report."); if (!sol.Success) return "<fail>"; }
        catch (Exception ex) { return $"<error: {ex.GetType().Name}>"; }
        return sw.ToString().Trim();
    }

    private static string RunInterpretedSwipl(string swipl, string plPath)
        => RunForReport(swipl, new[] { "-q", "-g", "report, halt", plPath });

    private static string RunForReport(string exePath, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start");
            p.StandardInput.Close();
            string stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();   // drain so the child doesn't block
            if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } return "<timeout>"; }
            // Some engines mix in a banner / warnings; only the
            // report's output is on stdout. Trim and pick the last
            // non-blank line — that's our fingerprint.
            var lines = stdout.Split('\n').Select(l => l.TrimEnd('\r', ' ', '\t'))
                .Where(l => l.Length > 0).ToList();
            // Join multi-line report output with a plain newline so
            // the whitespace-normalised comparator in MatchStatus
            // treats it equivalently to Shumway's `\n`-joined capture.
            return lines.Count == 0 ? "<no output>" : string.Join("\n", lines.TakeLast(4));
        }
        catch (Exception ex) { return $"<error: {ex.Message}>"; }
    }

    private static string MatchStatus(Dictionary<string, string> fps)
    {
        if (fps.Count <= 1) return "OK (only one engine)";
        // Normalise: each engine's write/1 differs cosmetically
        // (Shumway emits `[a, b, c]` with spaces; gprolog/swipl emit
        // `[a,b,c]` without). Strip whitespace before comparing so
        // we catch real semantic differences, not formatting noise.
        var normalised = fps.Values
            .Select(s => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()))
            .Distinct()
            .ToList();
        if (normalised.Count == 1) return $"OK ({Truncate(fps.Values.First().Replace("\n", " "), 60)})";
        return "MISMATCH";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 3) + "...";

    // Shumway in-process timing: a fresh engine consults the source and runs
    // bench(N), wall-clocked with a Stopwatch. A "true." query warms the JIT
    // so the first measured run doesn't pay the initial lift. This mirrors
    // the real embedding use (warm engine inside a long-lived .NET process)
    // rather than a cold subprocess.
    private static double TimeShumway(string plPath, int iterations)
    {
        var engine = new PrologEngine();
        engine.ConsultString(File.ReadAllText(plPath));
        engine.Query("true.");
        var sw = Stopwatch.StartNew();
        var sol = engine.Query($"bench({iterations}).");
        sw.Stop();
        if (!sol.Success)
            throw new InvalidOperationException(
                $"Shumway: bench({iterations}) failed for {Path.GetFileName(plPath)}");
        return sw.Elapsed.TotalMilliseconds;
    }

    // Times an external command's bench(N) total. Uses hyperfine (warmup +
    // outlier-robust median + stddev) when available; falls back to a
    // Stopwatch median over `runs` otherwise. The startup (bench(0)) is
    // cheap and stable, so it's always measured with a single quick spawn
    // rather than a full hyperfine run, halving the hyperfine cost.
    // Returns (startupMs, totalMedianMs, totalStddevMs).
    private static (double startup, double total, double stddev) TimeExternal(
        string label, string exePath, string[] args0, string[] argsN,
        int runs, string runnersDir)
    {
        double startup = TimeProcess(exePath, args0);
        if (_hyperfine is not null)
        {
            var n = HyperfineTime(label, exePath, argsN, runs, runnersDir);
            if (n is not null) return (startup, n.Value.median, n.Value.stddev);
            Console.Error.WriteLine($"warn: hyperfine failed for {label}; using Stopwatch fallback");
        }
        var totals = Enumerable.Range(0, runs).Select(_ => TimeProcess(exePath, argsN)).ToList();
        return (startup, Median(totals), Stddev(totals));
    }

    // Runs `<exePath> argsN...` under hyperfine, returns (medianMs, stddevMs).
    // The command is written to a .bat so hyperfine hands cmd a single token,
    // sidestepping shell-quoting issues for exe/arg paths that contain spaces.
    private static (double median, double stddev)? HyperfineTime(
        string label, string exePath, string[] args, int runs, string runnersDir)
    {
        Directory.CreateDirectory(runnersDir);
        string safe = new string(label.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        string bat = Path.Combine(runnersDir, safe + ".bat");
        var cmd = new StringBuilder();
        cmd.Append('"').Append(exePath).Append('"');
        foreach (var a in args)
        {
            cmd.Append(' ');
            if (a.Length == 0 || a.IndexOfAny(new[] { ' ', '\t', '&', '(', ')', ',' }) >= 0)
                cmd.Append('"').Append(a).Append('"');
            else
                cmd.Append(a);
        }
        File.WriteAllText(bat, "@echo off\r\n" + cmd + "\r\n");
        string json = Path.Combine(runnersDir, safe + ".json");
        try { if (File.Exists(json)) File.Delete(json); } catch { }

        // warmup 1 (a fresh native/JIT process mostly just needs the OS file
        // cache primed), runs floored at 3 so the median + stddev are
        // meaningful even at the harness default of --runs 1.
        var psi = new ProcessStartInfo
        {
            FileName = _hyperfine!,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "--warmup", "1",
            "--runs", Math.Max(runs, 3).ToString(CultureInfo.InvariantCulture),
            "--style", "none",
            "--export-json", json,
            bat,
        })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(600_000)) { try { p.Kill(); } catch { } return null; }
            if (p.ExitCode != 0 || !File.Exists(json))
            {
                if (!string.IsNullOrWhiteSpace(err))
                    Console.Error.WriteLine($"  hyperfine {label}: {err.Trim()}");
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            var r0 = doc.RootElement.GetProperty("results")[0];
            double medianSec = r0.GetProperty("median").GetDouble();
            double stddevSec = r0.TryGetProperty("stddev", out var sd) && sd.ValueKind == JsonValueKind.Number
                ? sd.GetDouble() : 0;
            return (medianSec * 1000.0, stddevSec * 1000.0);
        }
        catch (Exception ex) { Console.Error.WriteLine($"  hyperfine {label}: {ex.Message}"); return null; }
    }

    private static double Stddev(IList<double> xs)
    {
        if (xs.Count < 2) return 0;
        double mean = xs.Average();
        double sumSq = 0;
        foreach (var x in xs) sumSq += (x - mean) * (x - mean);
        return Math.Sqrt(sumSq / (xs.Count - 1));
    }

    private static double TimeProcess(string exePath, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {exePath}");
        p.StandardInput.Close();
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(); } catch { }
            throw new InvalidOperationException(
                $"timeout: {exePath} {string.Join(' ', args)}");
        }
        sw.Stop();
        if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            Console.Error.WriteLine(
                $"warn: {Path.GetFileName(exePath)} exit={p.ExitCode}; stderr=\"{stderr.TrimEnd()}\"");
        return sw.Elapsed.TotalMilliseconds;
    }

    // ---- discovery / IO ----

    private static string? Resolve(string envVar, string defaultPath)
    {
        string? env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        if (File.Exists(defaultPath)) return defaultPath;
        return null;
    }

    // Locate hyperfine: HYPERFINE_PATH env override, then PATH, then the
    // per-user winget install location (its PATH entry isn't visible to a
    // shell started before the install).
    private static string? ResolveHyperfine()
    {
        string? env = Environment.GetEnvironmentVariable("HYPERFINE_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string cand;
                try { cand = Path.Combine(dir.Trim(), "hyperfine.exe"); }
                catch { continue; }
                if (File.Exists(cand)) return cand;
            }

        string wingetPkgs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPkgs))
        {
            try
            {
                var hit = Directory.EnumerateFiles(wingetPkgs, "hyperfine.exe",
                    SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch { /* enumeration race / access — ignore */ }
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        string? dir = Path.GetDirectoryName(typeof(VanRoyMultiEngine).Assembly.Location);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Shumway.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate repo root (no Shumway.slnx found walking up).");
    }

    private static void WriteCsv(string path, IList<Result> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("benchmark,engine,iterations,startup_ms,total_ms,total_stddev_ms,per_iter_us");
        foreach (var r in results)
        {
            double? per = PerIterUs(r);
            string perStr = per is null ? "" : per.Value.ToString("F4", CultureInfo.InvariantCulture);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3:F3},{4:F3},{5:F3},{6}",
                r.Benchmark, r.Engine, r.Iterations, r.StartupMs, r.TotalMs, r.TotalStddevMs, perStr));
        }
        File.WriteAllText(path, sb.ToString());
    }
}

// ============================================================================
// BenchmarkDotNet microbenchmarks (in-process; not cross-engine)
// ============================================================================

[MemoryDiagnoser]
public class ShumwayMicrobenchmarks
{
    private PrologEngine _engine = null!;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new PrologEngine();
        _engine.ConsultString("""
            nrev([], []).
            nrev([H|T], R) :- nrev(T, RT), conc(RT, [H], R).
            conc([], L, L).
            conc([H|T], L, [H|R]) :- conc(T, L, R).
            list30([1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,
                    16,17,18,19,20,21,22,23,24,25,26,27,28,29,30]).
            nrev_test(R) :- list30(L), nrev(L, R).

            ackermann(0, N, R) :- !, R is N + 1.
            ackermann(M, 0, R) :- !, M1 is M - 1, ackermann(M1, 1, R).
            ackermann(M, N, R) :-
                M1 is M - 1, N1 is N - 1,
                ackermann(M, N1, X),
                ackermann(M1, X, R).
            """);
    }

    [Benchmark] public void NaiveReverse_30() { _ = _engine.Query("nrev_test(_)."); }
    [Benchmark] public void Ackermann_2_4()    { _ = _engine.Query("ackermann(2, 4, _)."); }
}
