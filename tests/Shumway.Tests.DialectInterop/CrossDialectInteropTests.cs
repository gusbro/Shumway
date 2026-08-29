using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.DialectInterop;

/// <summary>ADR-040 — cross-dialect interop against REAL third-party libraries.
/// Parameterised by environment variables naming each engine's library dir; a
/// test whose dir is unset/missing is a logged no-op (so a clone without the
/// libraries does not fail). Run explicitly with the dirs you have, e.g.
/// <c>SHUMWAY_SCRYER_LIB=C:/Scryer/lib SHUMWAY_SWI_LIB=C:/swipl/library dotnet
/// test tests/Shumway.Tests.DialectInterop/</c>.</summary>
public sealed class CrossDialectInteropTests
{
    private readonly ITestOutputHelper _out;
    public CrossDialectInteropTests(ITestOutputHelper output) => _out = output;

    private const string ScryerEnv = "SHUMWAY_SCRYER_LIB";
    private const string SwiEnv = "SHUMWAY_SWI_LIB";

    // The configured, existing directory for an engine. When there is none the
    // test SKIPS rather than passing: a run with nothing configured verifies
    // nothing, and reporting that as a pass made it indistinguishable from a
    // run that loaded the real libraries (2 seconds against 43).
    private string Dir(string env)
    {
        string? d = System.Environment.GetEnvironmentVariable(env);
        Skip.If(string.IsNullOrWhiteSpace(d), $"{env} is not set.");
        Skip.If(!System.IO.Directory.Exists(d), $"{env}='{d}' does not exist.");
        return d!;
    }

    [SkippableFact]
    public void Scryer_Clpz_Loads_And_Solves()
    {
        string scryer = Dir(ScryerEnv);

        var e = new PrologEngine();
        e.AddLibraryDirectory(scryer, "scryer");
        e.ConsultString(":- use_module(library(clpz)).");
        // clpz attribute-variable constraint + labeling.
        Assert.True(e.Query("X in 1..3, indomain(X), X == 1.").Success);
        Assert.Equal(3, e.QueryAll("Y in 1..3, indomain(Y).").Count());
        Assert.False(e.Query("Z in 1..3, Z #> 5, indomain(Z).").Success);
    }

    [SkippableFact]
    public void Swi_Gensym_Runs_Via_AtomConcat_Coercion()
    {
        // SWI's own library(gensym) calls atom_concat(Base, Integer, Atom) — an
        // ISO type_error, but SWI coerces the number. Because gensym's module is
        // loaded as the swi dialect, the dialect-sensitive atom_concat/3 applies
        // the coercion, so SWI's unmodified gensym.pl runs. (ADR-040.)
        string swi = Dir(SwiEnv);

        var e = new PrologEngine();
        e.AddLibraryDirectory(swi, "swi");
        e.ConsultString(":- use_module(library(gensym)).");
        Assert.Equal("foo1", e.Query("gensym(foo, X).").Get<string>("X"));
        Assert.Equal("foo2", e.Query("gensym(foo, X).").Get<string>("X"));
    }

    [SkippableFact]
    public void Swi_Heaps_Standalone_Loads_And_Works()
    {
        // A real SWI library (priority queues), loaded on its own.
        string swi = Dir(SwiEnv);

        var e = new PrologEngine();
        e.AddLibraryDirectory(swi, "swi");
        e.ConsultString(":- use_module(library(heaps)).");
        // Min-heap: the smallest key comes out first.
        Assert.True(e.Query(
            "list_to_heap([3-c, 1-a, 2-b], H), get_from_heap(H, 1, a, _).").Success);
    }

    [SkippableFact]
    public void Swi_Assoc_Standalone_Loads_And_Works()
    {
        // AVL-tree library — loads standalone, exercising the full chain of
        // engine features it needs: :- meta_predicate, :- autoload, `=>` (SSU),
        // and :- if/else/endif conditional compilation.
        string swi = Dir(SwiEnv);

        var e = new PrologEngine();
        e.AddLibraryDirectory(swi, "swi");
        e.ConsultString(":- use_module(library(assoc)).");
        Assert.True(e.Query(
            "list_to_assoc([a-1, b-2, c-3], A), get_assoc(b, A, 2).").Success);
        Assert.True(e.Query(
            "list_to_assoc([x-1], A0), put_assoc(y, A0, 2, A), get_assoc(y, A, 2).").Success);
    }

    [SkippableFact]
    public void UniteWorlds_ScryerClpz_And_SwiAssoc_InOneEngine()
    {
        // The headline ADR-040 property: a Scryer library and an SWI library,
        // each from its own system's checkout, loaded and working side by side
        // in ONE engine — attribute-variable constraints (clpz) next to AVL trees
        // (SWI assoc, which needed meta_predicate + autoload + => + if/else/endif
        // to load), each parsed in its own dialect.
        string scryer = Dir(ScryerEnv);
        string swi = Dir(SwiEnv);

        var e = new PrologEngine();
        e.AddLibraryDirectory(scryer, "scryer");
        e.AddLibraryDirectory(swi, "swi");
        e.ConsultString(":- use_module(library(clpz)).");
        e.ConsultString(":- use_module(library(assoc)).");

        Assert.True(e.Query("X in 5..7, indomain(X), X == 5.").Success);         // clpz
        Assert.True(e.Query(
            "list_to_assoc([k-9], A), get_assoc(k, A, 9).").Success);            // SWI
        // Both in a single conjunction, one engine, one query.
        Assert.True(e.Query(
            "V in 1..9, indomain(V), V == 1, "
            + "list_to_assoc([v-V], A), get_assoc(v, A, 1).").Success);
    }

    /// <summary>A load-sweep over every SWI library: a fresh engine per library,
    /// record whether <c>use_module(library(X))</c> loads cleanly, and if not,
    /// the error class. Prints a triage summary — feeds docs/library-triage-swi.md.
    /// Not an assertion sweep (it never fails); run with SHUMWAY_SWI_LIB set and
    /// read the test output.</summary>
    [SkippableFact]
    public void Swi_Triage_Sweep()
    {
        string swi = Dir(SwiEnv);
        SweepLibraries(swi, "swi");
    }

    [SkippableFact]
    public void Scryer_Triage_Sweep()
    {
        string scryer = Dir(ScryerEnv);
        SweepLibraries(scryer, "scryer");
    }

    private void SweepLibraries(string dir, string dialect)
    {
        var files = System.IO.Directory.GetFiles(dir, "*.pl");
        System.Array.Sort(files);
        int ok = 0, attempted = 0;
        var buckets = new System.Collections.Generic.SortedDictionary<
            string, System.Collections.Generic.List<string>>();
        foreach (string f in files)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(f);
            if (name == "INDEX") continue;
            attempted++;
            string outcome;
            // A library that fails to load is NOT thrown — the directive handler
            // catches it and reports `warning: use_module(...) failed: <msg>`,
            // then use_module returns null. Captured through the engine's own
            // per-engine Warnings writer (not a Console.Error swap — see the
            // note in ScryerEndToEndValidation) and any such warning is a
            // failure.
            var errCapture = new System.IO.StringWriter();
            var e = new PrologEngine { Warnings = errCapture };
            e.AddLibraryDirectory(dir, dialect);
            try
            {
                e.ConsultString($":- use_module(library({name})).");
            }
            catch (System.Exception ex)
            {
                errCapture.Write("\nEXC: " + ex.Message);
            }

            string err = errCapture.ToString();
            int fi = err.IndexOf("failed:", System.StringComparison.Ordinal);
            if (fi < 0 && !err.Contains("EXC:"))
            {
                outcome = "OK";
                ok++;
            }
            else
            {
                string msg = fi >= 0 ? err.Substring(fi + "failed:".Length).Trim() : err.Trim();
                if (msg.Contains("existence_error")) outcome = "MISSING: " + ExtractPI(msg);
                else if (System.Text.RegularExpressions.Regex.IsMatch(msg, @"\d+:\d+"))
                    outcome = "PARSE: " + FirstLine(msg);
                else outcome = "OTHER: " + FirstLine(msg);
            }
            string key = outcome == "OK" ? "OK" : outcome;
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = new System.Collections.Generic.List<string>();
            list.Add(name);
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== {dialect} triage: {ok}/{attempted} load cleanly ===");
        if (buckets.TryGetValue("OK", out var okList))
            sb.AppendLine("OK: " + string.Join(", ", okList));
        foreach (var kv in buckets)
        {
            if (kv.Key == "OK") continue;
            sb.AppendLine($"[{kv.Value.Count}] {kv.Key}");
            sb.AppendLine("     " + string.Join(", ", kv.Value));
        }
        string report = sb.ToString();
        _out.WriteLine(report);
        string? outFile = System.Environment.GetEnvironmentVariable("SHUMWAY_TRIAGE_OUT");
        if (!string.IsNullOrWhiteSpace(outFile))
            System.IO.File.WriteAllText(outFile, report);
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        s = nl >= 0 ? s.Substring(0, nl) : s;
        return s.Length > 120 ? s.Substring(0, 120) : s;
    }

    // Pull the predicate indicator out of an existence_error message so the
    // MISSING bucket groups by the actual missing predicate.
    private static string ExtractPI(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            s, @"existence_error\(\s*procedure\s*,\s*([^\)]+/\d+)");
        if (m.Success) return m.Groups[1].Value.Trim();
        var m2 = System.Text.RegularExpressions.Regex.Match(s, @"[\w$]+/\d+");
        return m2.Success ? m2.Value : FirstLine(s);
    }
}
