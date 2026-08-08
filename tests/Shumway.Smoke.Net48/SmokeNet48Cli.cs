using System.Runtime.InteropServices;
using Shumway.Embedding;

namespace Shumway.Smoke.Net48;

/// <summary>
/// Milestone-3 smoke for the netfx-target branch: Tier-0 running on
/// .NET Framework 4.8, meant to be executed BOTH as x86 (32-bit — the
/// point of the branch) and as x64 (parity). Exit code = number of
/// failed checks. Optional args: a .shum path plus a query, to prove a
/// bundle produced by the .NET 10 toolchain loads and runs here.
/// </summary>
internal static class SmokeNet48Cli
{
    private static int _failures;

    private static int Main(string[] args)
    {
        Console.WriteLine($"runtime : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"process : {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} (IntPtr={IntPtr.Size})");
        Console.WriteLine();

        var engine = new PrologEngine();

        Check("tier-0 default (no IL promotion armed)",
            () => engine.IlPromotion.Threshold == 0);

        Check("arithmetic", () =>
            engine.QueryFirst<long>("X is 6*7.", "X") == 42);

        Check("backtracking + findall (+ List<string> conversion)", () =>
        {
            var l = engine.QueryFirst<List<string>>("findall(X, member(X,[a,b,c]), L).", "L");
            return l != null && string.Join(",", l) == "a,b,c";
        });

        Check("cut commits", () =>
        {
            int n = 0;
            foreach (var s in engine.QueryAll("member(X,[1,2,3]), !.")) n++;
            return n == 1;
        });

        Check("dynamic assertz/retract + logical update view", () =>
        {
            if (!Succeeds(engine, "assertz(d(1)), d(1).")) return false;
            if (!Succeeds(engine, "assertz(d(2)), retract(d(1)), \\+ d(1), d(2).")) return false;
            return true;
        });

        Check("bigint (2^100)", () =>
            engine.QueryFirst<string>("X is 2^100, number_codes(X, Cs), atom_codes(A, Cs).", "A")
                == "1267650600228229401496703205376");

        Check("atom builtins + backtrackable sub_atom", () =>
        {
            if (engine.QueryFirst<string>("atom_concat(foo, bar, X).", "X") != "foobar")
                return false;
            int n = 0;
            foreach (var s in engine.QueryAll("sub_atom(abc, _, 2, _, S).")) n++;
            return n == 2; // ab, bc
        });

        Check("ISO exception round-trip", () =>
            Succeeds(engine, "catch(_ is foo+1, error(type_error(_,_), _), true)."));

        Check("clpfd (attvars + propagation + labeling)", () =>
        {
            engine.UseClpfd();
            var seen = new List<string>();
            foreach (var s in engine.QueryAll("X in 1..10, X #> 5, X #< 8, label([X])."))
                seen.Add(s["X"]!.ToString());
            return seen.Count == 2 && seen[0] == "6" && seen[1] == "7";
        });

        Check("tabling (left-recursive transitive closure)", () =>
        {
            engine.ConsultString("""
                :- table path/2.
                path(X, Y) :- path(X, Z), edge(Z, Y).
                path(X, Y) :- edge(X, Y).
                edge(a, b).
                edge(b, c).
                edge(c, d).
                """);
            return engine.QueryFirst<string>(
                "findall(Y, path(a, Y), Ys0), msort(Ys0, Ys), atomic_list_concat(Ys, R).", "R")
                == "bcd";
        });

        Check("heap growth + GC (1M-element list, 32-bit friendly)", () =>
        {
            engine.ConsultString("""
                deepsum(S) :- numlist(1, 1000000, L), sum_list(L, S0),
                              garbage_collect, S = S0.
                """);
            return engine.QueryFirst<long>("deepsum(S).", "S") == 500000500000L;
        });

        if (args.Length >= 2)
        {
            Check($"bundle from the .NET 10 toolchain ({Path.GetFileName(args[0])})", () =>
            {
                var e2 = new PrologEngine();
                e2.LoadBundle(args[0]);
                return Succeeds(e2, args[1]);
            });
        }
        else
        {
            Console.WriteLine("skip  : bundle check (pass <bundle.shum> <query.> to enable)");
        }

        // Informational, never counted as failure: how big a single query's
        // heap can get before the 32-bit address space (contiguous Cell[]
        // growth needs old+new alive during the doubling copy) says no.
        if (Array.IndexOf(args, "--stress") >= 0)
        {
            Console.WriteLine();
            foreach (long n in new[] { 1_000_000L, 2_000_000, 3_000_000, 4_000_000, 5_000_000, 10_000_000, 20_000_000 })
            {
                try
                {
                    var e = new PrologEngine();
                    long expected = n * (n + 1) / 2;
                    // The list variable must stay INSIDE a consulted predicate:
                    // a named query variable materializes its value to a C# Term
                    // AST in the bindings, and a multimillion-node AST — not the
                    // engine heap — is then what exhausts a 32-bit process.
                    e.ConsultString("stress(N, S) :- numlist(1, N, L), sum_list(L, S).");
                    bool ok = e.QueryFirst<long>($"stress({n}, S).", "S") == expected;
                    Console.WriteLine($"stress: {n:N0}-element list {(ok ? "OK" : "WRONG RESULT")}");
                    if (!ok) _failures++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"stress: {n:N0}-element list — {ex.GetType().Name}: {FirstLine(ex.Message)}");
                    if (Environment.GetEnvironmentVariable("SMOKE_TRACE") == "1") Console.WriteLine(ex.ToString());
                    break;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "SMOKE OK" : $"SMOKE FAILED ({_failures})");
        return _failures;
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        return i < 0 ? s : s.Substring(0, i).TrimEnd('\r');
    }

    private static bool Succeeds(PrologEngine engine, string query)
    {
        foreach (var s in engine.QueryAll(query)) return s.Success;
        return false;
    }

    private static void Check(string name, Func<bool> body)
    {
        try
        {
            bool ok = body();
            if (!ok) _failures++;
            Console.WriteLine($"{(ok ? "ok    " : "FAIL  ")}: {name}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"FAIL  : {name} — {ex.GetType().Name}: {ex.Message}");
        }
    }
}
