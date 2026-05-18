using System.Diagnostics;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Shumway.Embedding;

// Run all benchmarks in this assembly. The classes below describe
// canonical Prolog workloads — naive reverse, member traversal, N-queens
// — exercising lists, choice points, and indexed dispatch. Output is a
// BenchmarkDotNet table plus, when GNU Prolog is on the path, a parallel
// gprolog timing for the same source.

if (args.Length > 0 && args[0] == "--gnuprolog-compare")
{
    GnuPrologCompare.Run();
    return;
}

BenchmarkRunner.Run(typeof(Program).Assembly);

// ============================================================================
// Benchmarks
// ============================================================================

[MemoryDiagnoser]
public class ShumwayBenchmarks
{
    private PrologEngine _engine = null!;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new PrologEngine();
        _engine.ConsultString("""
            :- public list50/1.
            list50([1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,
                   21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,
                   41,42,43,44,45,46,47,48,49,50]).

            :- public nrev/2.
            nrev([], []).
            nrev([H|T], R) :- nrev(T, RT), append(RT, [H], R).

            :- public nrev_test/1.
            nrev_test(R) :- list50(L), nrev(L, R).

            :- public count_member/2.
            count_member([], 0).
            count_member([_|T], N) :- count_member(T, M), N is M + 1.

            :- public ackermann/3.
            ackermann(0, N, R) :- !, R is N + 1.
            ackermann(M, 0, R) :- !, M1 is M - 1, ackermann(M1, 1, R).
            ackermann(M, N, R) :-
                M1 is M - 1, N1 is N - 1,
                ackermann(M, N1, X),
                ackermann(M1, X, R).
            """);
    }

    /// <summary>Naive list reversal — the canonical Prolog benchmark.
    /// Reverses a 50-element list, which is O(n^2) via append.</summary>
    [Benchmark]
    public void NaiveReverse_50()
    {
        var sol = _engine.Query("nrev_test(_).");
        if (!sol.Success) throw new InvalidOperationException("nrev_test failed");
    }

    /// <summary>Find each element of a 50-element list via member/2 and
    /// count them — exercises the prelude-defined member with its
    /// choice-point machinery.</summary>
    [Benchmark]
    public void Member_50ElementList_Findall()
    {
        var sol = _engine.Query("list50(L), findall(X, member(X, L), All), length(All, _).");
        if (!sol.Success) throw new InvalidOperationException("member findall failed");
    }

    /// <summary>Ackermann(2, 4) — small but recursion-heavy; stresses the
    /// call/return path and cut handling.</summary>
    [Benchmark]
    public void Ackermann_2_4()
    {
        var sol = _engine.Query("ackermann(2, 4, _).");
        if (!sol.Success) throw new InvalidOperationException("ackermann failed");
    }
}

// ============================================================================
// GNU Prolog comparison (manual invocation: `--gnuprolog-compare`)
// ============================================================================

/// <summary>Runs the same workloads as <see cref="ShumwayBenchmarks"/>
/// against the local GNU Prolog (<c>gprolog</c>) installation and
/// prints a side-by-side comparison. Skips automatically if
/// <c>gprolog</c> isn't on the PATH — useful for CI environments
/// where it isn't installed.</summary>
public static class GnuPrologCompare
{
    private const string SourceProgram = """
        list50([1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,
               21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,
               41,42,43,44,45,46,47,48,49,50]).
        nrev([], []).
        nrev([H|T], R) :- nrev(T, RT), append(RT, [H], R).
        nrev_test(R) :- list50(L), nrev(L, R).
        ackermann(0, N, R) :- !, R is N + 1.
        ackermann(M, 0, R) :- !, M1 is M - 1, ackermann(M1, 1, R).
        ackermann(M, N, R) :-
            M1 is M - 1, N1 is N - 1,
            ackermann(M, N1, X),
            ackermann(M1, X, R).
        """;

    public static void Run()
    {
        bool hasGprolog = FindGprolog(out string? gprologPath);
        if (!hasGprolog)
        {
            Console.WriteLine("gprolog not found on PATH — skipping the comparison.");
            Console.WriteLine("Install GNU Prolog and re-run to see Shumway vs. gprolog side-by-side.");
            return;
        }

        Console.WriteLine($"Using gprolog at: {gprologPath}");
        Console.WriteLine();

        // Build temp .pl with the shared source.
        string tmpPath = Path.GetTempFileName();
        File.Move(tmpPath, tmpPath + ".pl");
        tmpPath += ".pl";
        File.WriteAllText(tmpPath, SourceProgram);

        var workloads = new (string Name, string Query, int Iters)[]
        {
            ("naive_reverse_50",   "nrev_test(_)",        10000),
            ("ackermann_2_4",      "ackermann(2, 4, _)",  1000),
        };

        Console.WriteLine($"{"Workload",-22} {"Shumway (ms)",15} {"gprolog (ms)",15} {"ratio",10}");
        Console.WriteLine(new string('-', 65));
        var engine = new PrologEngine();
        engine.ConsultString(SourceProgram);

        foreach (var (name, query, iters) in workloads)
        {
            double shumwayMs = TimeShumway(engine, query, iters);
            double gprologMs = TimeGprolog(gprologPath!, tmpPath, query, iters);
            double ratio = gprologMs / shumwayMs;
            Console.WriteLine(
                $"{name,-22} {shumwayMs,15:F2} {gprologMs,15:F2} {ratio,10:F2}x");
        }

        File.Delete(tmpPath);
    }

    private static bool FindGprolog(out string? path)
    {
        path = null;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "gprolog.exe", @"C:\GnuPrologwin\bin\gprolog.exe" }
            : new[] { "gprolog", "/usr/local/bin/gprolog", "/usr/bin/gprolog" };
        foreach (var c in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(c, "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p is null) continue;
                if (!p.WaitForExit(2000)) { p.Kill(); continue; }
                if (p.ExitCode == 0) { path = c; return true; }
            }
            catch { /* swallow and try next candidate */ }
        }
        return false;
    }

    private static double TimeShumway(PrologEngine engine, string query, int iters)
    {
        // Warm-up.
        for (int i = 0; i < 10; i++) engine.Query(query + ".");
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++) engine.Query(query + ".");
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static double TimeGprolog(string gprologPath, string sourcePath, string query, int iters)
    {
        // Build a driver that consults the program and runs the query N times.
        // gprolog measures internal time via statistics/2; we use wall clock
        // around the whole process for an apples-to-apples comparison.
        string driver =
            $":- consult('{sourcePath.Replace("\\", "/")}').\n" +
            $":- (between(1, {iters}, _), ({query}), fail ; true).\n" +
            ":- halt.\n";
        string driverPath = Path.GetTempFileName();
        File.Move(driverPath, driverPath + ".pl");
        driverPath += ".pl";
        File.WriteAllText(driverPath, driver);

        try
        {
            var psi = new ProcessStartInfo(gprologPath, $"--consult-file {driverPath} --quiet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start gprolog.");
            p.WaitForExit();
            sw.Stop();
            if (p.ExitCode != 0)
                Console.Error.WriteLine($"gprolog exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
            return sw.Elapsed.TotalMilliseconds;
        }
        finally
        {
            File.Delete(driverPath);
        }
    }
}
