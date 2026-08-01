using System;
using System.Collections.Generic;
using System.IO;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.DialectInterop;

/// <summary>End-to-end validation of real Scryer libraries: load each (under the
/// scryer dialect) and EXERCISE a representative predicate — not just load.
/// Records load + smoke outcomes and writes a report to SHUMWAY_TRIAGE_OUT.
/// Opt-in (SHUMWAY_SCRYER_LIB, e.g. C:/Scryer/lib). Mirrors
/// <see cref="SwiEndToEndValidation"/>.</summary>
public sealed class ScryerEndToEndValidation
{
    private readonly ITestOutputHelper _out;
    public ScryerEndToEndValidation(ITestOutputHelper output) => _out = output;

    // (library, smoke query). A null query = load-only. Queries build char
    // lists explicitly (atom_chars) — the REPL/query default is PSTR, while
    // Scryer sources expect chars.
    private static readonly (string Lib, string? Query)[] Cases =
    {
        ("lists",       "member(2, [1,2]), append([1], [2], [1,2]), length(L, 2), L = [_,_]."),
        ("assoc",       "empty_assoc(A0), put_assoc(k, A0, 1, A1), get_assoc(k, A1, 1)."),
        ("between",     "between(1, 3, 2), numlist(1, 3, [1,2,3])."),
        ("clpb",        "taut(X + ~X, T), T == 1."),
        ("clpz",        "X #= 3 + 4, X == 7."),
        ("csv",         "atom_chars('a,b\\n', Cs), phrase(parse_csv(Rows), Cs), Rows = frame(_, _)."),
        ("dcgs",        "atom_chars(ab, Cs), phrase(seq(Cs), Cs)."),
        ("debug",       "* fail."),   // `* Goal` = goal generalized away: always succeeds
        ("dif",         "dif(a, b), \\+ dif(a, a)."),
        ("error",       "catch(must_be(integer, a), error(type_error(integer, a), _), true)."),
        ("freeze",      "freeze(X, Y = 1), X = a, Y == 1."),
        ("gensym",      "gensym(zork, G), atom(G)."),
        ("iso_ext",     "bb_put(k, 41), bb_get(k, 41)."),
        ("lambda",      "maplist(\\X^Y^(Y is X + 1), [1,2], [2,3])."),
        ("ordsets",     "ord_union([1,3], [2], [1,2,3])."),
        ("pairs",       "pairs_keys_values(Ps, [a], [1]), Ps == [a-1]."),
        ("queues",      "list_queue([1,2], Q), queue_length(Q, 2)."),
        ("reif",        "if_(1 = 1, X = yes, X = no), X == yes, tfilter(=(a), [a,b,a], [a,a])."),
        ("si",          "atom_si(a), integer_si(3)."),
        ("simplex",     "gen_state(S0), constraint([x] =< 1, S0, _)."),
        ("terms",       "numbervars(f(X), 0, N), N == 1."),
        ("ugraphs",     "add_vertices([], [a,b], G), G == [a-[], b-[]]."),
        ("xpath",       "xpath(element(a, [], [element(b, [], [])]), //b, _)."),
        ("crypto",      "atom_chars(ff, HC), hex_bytes(HC, [255])."),   // pure part; hashes are Rust-native
        ("atts",        null),
        ("builtins",    null),
        ("dcg/high-order-check", null),   // placeholder row: subdir libs not swept
        // Known runtime gaps (round-2 candidates) — load-only here:
        ("arithmetic",  null),   // needs builtins.pl's must_be_number/2
        ("charsio",     null),   // char_type wraps the Rust-native $char_type
        ("format",      null),   // format_//2 needs builtins:parse_write_options
        ("files",       null),   // wraps $file_exists etc.
        ("os",          null),
        ("random",      null),   // wraps $maybe / $random_integer
        ("time",        null),   // wraps $cpu_now
        ("uuid",        null),   // wraps $crypto_random_byte
        ("when",        null),   // loads; posting fails silently — needs diagnosis
        ("cont",        null),   // delimited continuations — VM feature
        ("tabling",     null),   // Scryer's uses cont; Shumway's native :- table serves
        ("ffi",         null),
        ("sockets",     null),
        ("tls",         null),
        ("wasm",        null),
        ("sgml",        null),
        ("process",     null),
        ("diag",        null),
        ("pio",         null),
        ("ops_and_meta_predicates", null),
    };

    [Fact]
    public void Validate()
    {
        string? dir = Environment.GetEnvironmentVariable("SHUMWAY_SCRYER_LIB");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _out.WriteLine("SKIPPED: SHUMWAY_SCRYER_LIB not set / missing.");
            return;
        }

        var rows = new List<(string Lib, string Load, string Smoke)>();
        foreach (var (lib, query) in Cases)
        {
            if (lib.Contains("-check")) continue;   // placeholder rows
            string load, smoke;
            var errCapture = new StringWriter();
            var prevErr = Console.Error;
            Console.SetError(errCapture);
            PrologEngine? e = null;
            try
            {
                e = new PrologEngine();
                e.AddLibraryDirectory(dir, "scryer");
                e.ConsultString($":- use_module(library({lib})).");
            }
            catch (Exception ex) { errCapture.Write("\nEXC:" + ex.Message); }
            finally { Console.SetError(prevErr); }

            string warn = errCapture.ToString();
            bool topLevelOk = e is not null && !warn.Contains("EXC:");
            bool depWarn = warn.IndexOf("failed:", StringComparison.Ordinal) >= 0;
            load = !topLevelOk ? "LOADFAIL" : (depWarn ? "load(dep!)" : "load");

            if (!topLevelOk || query is null || e is null)
            {
                smoke = query is null ? "(load-only)" : "-";
            }
            else
            {
                try
                {
                    smoke = e.Query(query).Success ? "SMOKE-OK" : "SMOKE-FAIL";
                }
                catch (Exception ex)
                {
                    smoke = "SMOKE-EXC: " + FirstLine(ex.Message);
                }
            }
            rows.Add((lib, load, smoke));
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Scryer library end-to-end validation ===");
        sb.AppendLine($"{"library",-16} {"load",-10} smoke");
        int okCount = 0, loadFail = 0;
        foreach (var (lib, load, smoke) in rows)
        {
            sb.AppendLine($"{lib,-16} {load,-10} {smoke}");
            if (smoke == "SMOKE-OK") okCount++;
            if (load == "LOADFAIL") loadFail++;
        }
        sb.AppendLine($"=== {okCount} smoke-ok, {loadFail} load failures, {rows.Count} libraries ===");
        string report = sb.ToString();
        _out.WriteLine(report);
        string? outFile = Environment.GetEnvironmentVariable("SHUMWAY_TRIAGE_OUT");
        if (!string.IsNullOrWhiteSpace(outFile)) File.WriteAllText(outFile, report);

        // The load sweep is the hard assertion: every Scryer top-level library
        // must at least LOAD on Shumway (46/46 at the time of writing).
        Assert.Equal(0, loadFail);
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        s = nl >= 0 ? s.Substring(0, nl) : s;
        return s.Length > 80 ? s.Substring(0, 80) : s;
    }
}
