using System.Diagnostics;
using System.Globalization;
using System.Text;
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

VanRoyMultiEngine.Run(args);

// ============================================================================
// Van Roy multi-engine comparison
// ============================================================================

public static class VanRoyMultiEngine
{
    private const string GprologPath  = @"C:\GProlog\bin\gprolog.exe";
    private const string GplcPath     = @"C:\GProlog\bin\gplc.exe";
    private const string SwiplPath    = @"C:\Program Files (x86)\swipl\bin\swipl.exe";

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
        string repoRoot = FindRepoRoot();
        string vanroyDir = Path.Combine(repoRoot, "benchmarks", "vanroy");
        string resultsDir = Path.Combine(repoRoot, "benchmarks", "results");
        string gprologBinDir = Path.Combine(resultsDir, "bin-gprolog");
        Directory.CreateDirectory(resultsDir);
        Directory.CreateDirectory(gprologBinDir);

        // Resolve engines. Each may be absent (skipped); env-var overrides
        // (GPROLOG_PATH, GPLC_PATH, SWIPL_PATH) take precedence.
        string? gprolog = Resolve("GPROLOG_PATH", GprologPath);
        string? gplc    = Resolve("GPLC_PATH",    GplcPath);
        string? swipl   = Resolve("SWIPL_PATH",   SwiplPath);

        Console.WriteLine();
        Console.WriteLine("Engines:");
        Console.WriteLine("  shumway : in-process");
        Console.WriteLine($"  gprolog : {(gprolog ?? "not found")} (gplc={(gplc ?? "not found")})");
        Console.WriteLine($"  swipl   : {(swipl ?? "not found")}");
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

        Console.WriteLine();
        Console.WriteLine($"{"benchmark",-12} {"engine",-10} {"iters",8} {"startup_ms",12} {"total_ms",12} {"per_iter_us",14}");
        Console.WriteLine(new string('-', 76));
        var results = new List<Result>();
        foreach (var (name, iters) in Benchmarks)
        {
            string pl = Path.Combine(vanroyDir, $"{name}.pl");
            if (!File.Exists(pl))
            {
                Console.Error.WriteLine($"missing: {pl}");
                continue;
            }

            // Shumway in-process.
            var sStart = TimeShumway(pl, 0);
            var sTotal = TimeShumway(pl, iters);
            Record(results, name, "shumway", iters, sStart, sTotal);

            // Compiled GProlog (native exe).
            if (gprologExes.TryGetValue(name, out string? exe))
            {
                var gStart = TimeProcess(exe, new[] { "0" });
                var gTotal = TimeProcess(exe, new[] { iters.ToString(CultureInfo.InvariantCulture) });
                Record(results, name, "gprolog", iters, gStart, gTotal);
            }

            // SWI interpreted.
            if (swipl is not null)
            {
                string[] swiArgs0 = { "-q", "-g", "bench(0), halt", pl };
                string[] swiArgsN = { "-q", "-g", $"bench({iters}), halt", pl };
                var wStart = TimeProcess(swipl, swiArgs0);
                var wTotal = TimeProcess(swipl, swiArgsN);
                Record(results, name, "swipl", iters, wStart, wTotal);
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
    }

    private static void Record(List<Result> results, string name, string engine, int iters, double startup, double total)
    {
        var r = new Result(name, engine, iters, startup, total);
        results.Add(r);
        Console.WriteLine(
            $"{name,-12} {engine,-10} {iters,8} {startup,12:F2} {total,12:F2} {FormatPerIter(r),14}");
    }

    private static string FormatPerIter(Result r)
    {
        if (r.Iterations <= 0) return "";
        double work = r.TotalMs - r.StartupMs;
        if (work <= 0.5) return "<noise>";
        return (work * 1000.0 / r.Iterations).ToString("F3", CultureInfo.InvariantCulture);
    }

    private static double? PerIterUs(Result r)
    {
        if (r.Iterations <= 0) return null;
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
                          double StartupMs, double TotalMs);

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

    private static double TimeShumway(string plPath, int iterations)
    {
        // Fresh engine each invocation — matches external engines which
        // pay fresh-process startup. Tiny "true." warmup so the first
        // run doesn't pay the very first JIT lift.
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
        sb.AppendLine("benchmark,engine,iterations,startup_ms,total_ms,per_iter_us");
        foreach (var r in results)
        {
            double? per = PerIterUs(r);
            string perStr = per is null ? "" : per.Value.ToString("F4", CultureInfo.InvariantCulture);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3:F3},{4:F3},{5}",
                r.Benchmark, r.Engine, r.Iterations, r.StartupMs, r.TotalMs, perStr));
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
