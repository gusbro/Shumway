using System;
using System.Collections.Generic;
using System.IO;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.DialectInterop;

/// <summary>End-to-end validation of real Trealla libraries: load each (under
/// the trealla dialect) and EXERCISE a representative predicate — not just
/// load. Records load + smoke outcomes and writes a report to
/// SHUMWAY_TRIAGE_OUT. Opt-in (SHUMWAY_TREALLA_LIB, e.g.
/// C:/Prolog/Trealla/library). Mirrors <see cref="ScryerEndToEndValidation"/>.</summary>
public sealed class TreallaEndToEndValidation
{
    private readonly ITestOutputHelper _out;
    public TreallaEndToEndValidation(ITestOutputHelper output) => _out = output;

    // (library, smoke query). A null query = load-only: the library's
    // operation needs a host capability (C FFI via use_foreign_module /
    // foreign_struct, network sockets, their VM's task engine) or is
    // certified elsewhere (atts under clpz/clpb, tabling by the native
    // `:- table`).
    private static readonly (string Lib, string? Query)[] Cases =
    {
        ("abnf",        "phrase(abnf_digit(D), \"5\"), D == '5'."),
        ("aggregate",   "aggregate_all(count, member(_, [a,b]), 2)."),
        ("arithmetic",  "popcount(7, 3)."),
        ("assoc",       "empty_assoc(A0), put_assoc(k, A0, 1, A1), get_assoc(k, A1, 1)."),
        ("atts",        null),      // certified end-to-end by clpz/clpb
        ("builtins",    null),      // their bootstrap layer
        ("charsio",     "char_type(a, alpha), char_type('A', upper(a))."),
        ("clpb",        "taut(X + ~X, T), T == 1."),
        ("clpz",        "X #= 3 + 4, X == 7."),
        ("concurrent",  null),      // their VM's task engine
        ("curl",        null),      // C FFI (use_foreign_module)
        ("debug",       "* fail."), // `* Goal` = goal generalized away: always succeeds
        ("dif",         "dif(a, b), \\+ dif(a, a)."),
        ("error",       "catch(must_be(integer, a), error(type_error(integer, a), _), true)."),
        ("format",      "format(\"~w-~w~n\", [a, b])."),
        ("freeze",      "freeze(X, Y = 1), X = a, Y == 1."),
        ("gensym",      "gensym(zork, G), atom(G)."),
        ("gsl",         null),      // GNU Scientific Library FFI
        ("http",        null),      // pure Prolog, but over network sockets
        ("iso_ext",     "bb_put(k, 41), bb_get(k, 41)."),
        ("json",        "phrase(json_chars(J), \"{\\\"a\\\":[1,true]}\"), ground(J)."),
        ("lambda",      "maplist(\\X^Y^(Y is X + 1), [1,2], [2,3])."),
        ("lists",       "member(2, [1,2]), append([1], [2], [1,2]), length(L, 2), L = [_,_]."),
        ("ordsets",     "ord_union([1,3], [2], [1,2,3])."),
        ("pairs",       "pairs_keys_values(Ps, [a], [1]), Ps == [a-1]."),
        ("pio",         null),      // the native phrase_from_file/2,3 serves
        ("quads",       null),      // their quad-store test framework
        ("random",      "random_integer(1, 10, R), integer(R), maybe(1)."),
        ("raylib",      null),      // C FFI
        ("rbtrees",     null),      // YAP-heritage `:- hmtype ... ---> ...`
                                    // directives + a library('dialect/commons')
                                    // dependency not present in the Trealla tree
        ("reif",        "if_(1 = 1, X = yes, X = no), X == yes."),
        ("si",          "atom_si(a), integer_si(3)."),
        ("sockets",     null),      // network sockets
        ("sqlite3",     null),      // C FFI
        ("tabling",     null),      // the native `:- table` serves
        ("time",        "sleep(0)."),
        ("ugraphs",     "add_vertices([], [a,b], G), G == [a-[], b-[]]."),
        ("uuid",        "uuidv4_string(U), length(U, 36)."),
        ("when",        "when(ground(X), Y = 1), X = a, Y == 1."),
        ("yall",        "maplist([X,Y]>>(Y is X + 1), [1], [2])."),
    };

    /// <summary>Libraries whose LOAD is known to fail (see the per-case
    /// notes) — tolerated by the hard assertion below.</summary>
    private static readonly HashSet<string> ExpectedLoadFail = new() { "rbtrees" };

    [SkippableFact]
    public void Validate()
    {
        string? dir = Environment.GetEnvironmentVariable("SHUMWAY_TREALLA_LIB");
        // Skipped, not passed: with no directory there is nothing to load,
        // and a pass here would look exactly like a real run.
        Skip.If(string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir),
                "SHUMWAY_TREALLA_LIB is not set, or names a directory that is not there.");

        var rows = new List<(string Lib, string Load, string Smoke)>();
        foreach (var (lib, query) in Cases)
        {
            string load, smoke;
            // Per-engine Warnings capture — not a Console.Error swap; see the
            // note in ScryerEndToEndValidation. This class was the visible
            // victim: it fails beside the other validations and passes alone,
            // because a neighbour's "failed:" warning landing in this capture
            // (or this one's landing elsewhere) flips the load verdict.
            var errCapture = new StringWriter();
            PrologEngine? e = null;
            try
            {
                e = new PrologEngine { Warnings = errCapture };
                e.AddLibraryDirectory(dir, "trealla");
                e.ConsultString($":- use_module(library({lib})).");
            }
            catch (Exception ex) { errCapture.Write("\nEXC:" + ex.Message); }

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
        sb.AppendLine("=== Trealla library end-to-end validation ===");
        sb.AppendLine($"{"library",-16} {"load",-10} smoke");
        int okCount = 0, loadFail = 0;
        foreach (var (lib, load, smoke) in rows)
        {
            sb.AppendLine($"{lib,-16} {load,-10} {smoke}");
            if (smoke == "SMOKE-OK") okCount++;
            if ((load == "LOADFAIL" || load == "load(dep!)") && !ExpectedLoadFail.Contains(lib))
                loadFail++;
        }
        sb.AppendLine($"=== {okCount} smoke-ok, {loadFail} unexpected load failures, {rows.Count} libraries ===");
        string report = sb.ToString();
        _out.WriteLine(report);
        string? outFile = Environment.GetEnvironmentVariable("SHUMWAY_TRIAGE_OUT");
        if (!string.IsNullOrWhiteSpace(outFile)) File.WriteAllText(outFile, report);

        // The load sweep is the hard assertion: every Trealla library except
        // the documented exception must at least LOAD on Shumway.
        Assert.Equal(0, loadFail);
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        s = nl >= 0 ? s.Substring(0, nl) : s;
        return s.Length > 80 ? s.Substring(0, 80) : s;
    }
}
