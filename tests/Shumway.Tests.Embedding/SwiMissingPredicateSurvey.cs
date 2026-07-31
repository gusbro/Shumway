using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>Opt-in survey (gated on <c>SHUMWAY_SWI_LIB</c>): for EACH SWI library,
/// compile it (+ its use_module deps) through the consult pipeline in the swi
/// dialect and LINK it, harvesting the linker's <c>missing_predicate</c>
/// diagnostics — the predicates it REFERENCES but nothing (a builtin, the prelude,
/// or a linked dependency) defines. This is the signal the load-sweep cannot give:
/// a referenced-but-undefined predicate only errors when CALLED, so a library that
/// merely mentions it still "loads cleanly". Per-library isolation (a fresh engine
/// each) — loading all 129 into one engine cascades (125/129 fail on shared state).
///
/// <para>Real engine/shim gaps = (union of all libraries' missing) MINUS (union of
/// all libraries' DEFINED) — a predicate one SWI library defines is not a gap even
/// if another references it without importing it. Ranked by how many libraries
/// reference each gap: the shim priority list. Writes to <c>SHUMWAY_TRIAGE_OUT</c>
/// when set. Never fails (a clone without the libraries is a logged no-op).</para></summary>
public sealed class SwiMissingPredicateSurvey
{
    private readonly ITestOutputHelper _out;
    public SwiMissingPredicateSurvey(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Survey()
    {
        string? dir = Environment.GetEnvironmentVariable("SHUMWAY_SWI_LIB");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _out.WriteLine("SKIPPED: SHUMWAY_SWI_LIB not set / missing.");
            return;
        }

        var libs = Directory.GetFiles(dir, "*.pl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && n != "INDEX")
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        string tmp = Path.Combine(Path.GetTempPath(), "swi-missing-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            var definedByAny = new HashSet<string>(StringComparer.Ordinal);
            // missing PI → set of libraries that reference it.
            var missingRefs = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var parseBlocked = new List<string>();   // library did not compile at all

            foreach (string lib in libs)
            {
                string root = Path.Combine(tmp, "r.pl");
                File.WriteAllText(root, $":- use_module(library({lib})).\n");

                List<(string ModuleName, ShmoObject Object)> objects;
                var compileErrors = new List<ShmoCompileError>();
                var errCapture = new StringWriter();
                var prevErr = Console.Error;
                Console.SetError(errCapture);
                try
                {
                    objects = ShmoViaConsult.Compile(
                        root, new[] { dir }, ShmoBuildMode.Debuggable, compileErrors, dialect: "swi");
                }
                catch (Exception ex)
                {
                    Console.SetError(prevErr);
                    parseBlocked.Add($"{lib}: EXC {FirstLine(ex.Message)}");
                    continue;
                }
                finally { Console.SetError(prevErr); }

                // The lib's own module never became an object → it failed to load
                // (parse error or a load-time missing predicate). Record + skip.
                var shmos = objects.Select(o => o.Object).ToList();
                bool libLoaded = shmos.Any(o =>
                    o.ModuleName == lib || o.ModuleName == "r");
                if (!shmos.Any())
                {
                    string warn = errCapture.ToString();
                    int fi = warn.IndexOf("failed:", StringComparison.Ordinal);
                    parseBlocked.Add($"{lib}: {(fi >= 0 ? FirstLine(warn.Substring(fi + 7).Trim()) : "no objects")}");
                    continue;
                }

                foreach (var o in shmos)
                    foreach (var d in o.Defined)
                        definedByAny.Add($"{d.Indicator.Name}/{d.Indicator.Arity}");

                var entries = new HashSet<PredicateRef>();
                foreach (var o in shmos)
                    foreach (var d in o.Defined)
                        entries.Add(d.Indicator);

                LinkResult result;
                try
                {
                    result = ShmoLinker.Link(new LinkConfig
                    {
                        Objects = shmos,
                        EntryPoints = entries.ToList(),
                        AllowUndefined = true,
                    });
                }
                catch (Exception ex)
                {
                    parseBlocked.Add($"{lib}: LINK-EXC {FirstLine(ex.Message)}");
                    continue;
                }

                foreach (var d in result.Diagnostics)
                {
                    if (d.Code != "missing_predicate") continue;
                    var m = System.Text.RegularExpressions.Regex.Match(d.Message, @"[\w$']+/\d+");
                    if (!m.Success) continue;
                    string pi = m.Value.Trim('\'');
                    if (!missingRefs.TryGetValue(pi, out var set))
                        missingRefs[pi] = set = new SortedSet<string>(StringComparer.Ordinal);
                    set.Add(lib);
                }
            }

            // Real gaps: referenced-but-undefined AND not defined by any library.
            var gaps = missingRefs
                .Where(kv => !definedByAny.Contains(kv.Key))
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            var providedElsewhere = missingRefs
                .Where(kv => definedByAny.Contains(kv.Key))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key)
                .ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== SWI missing-predicate survey (per-library) ===");
            sb.AppendLine($"libraries: {libs.Count}   parse/load-blocked: {parseBlocked.Count}");
            sb.AppendLine($"distinct predicates defined across all libraries: {definedByAny.Count}");
            sb.AppendLine($"REAL GAPS (referenced, undefined, not provided by any library): {gaps.Count}");
            sb.AppendLine();
            sb.AppendLine("gap predicate                #libs  referencing libraries");
            foreach (var kv in gaps)
                sb.AppendLine($"  {kv.Key,-26} {kv.Value.Count,4}   {string.Join(", ", kv.Value)}");
            sb.AppendLine();
            sb.AppendLine($"referenced-but-provided-by-another-library (not a gap): {providedElsewhere.Count}");
            sb.AppendLine("  " + string.Join(", ", providedElsewhere));
            sb.AppendLine();
            sb.AppendLine($"parse/load-blocked libraries (their gaps are hidden): {parseBlocked.Count}");
            foreach (var p in parseBlocked) sb.AppendLine("  " + p);

            string report = sb.ToString();
            _out.WriteLine(report);
            string? outFile = Environment.GetEnvironmentVariable("SHUMWAY_TRIAGE_OUT");
            if (!string.IsNullOrWhiteSpace(outFile))
                File.WriteAllText(outFile, report);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        s = nl >= 0 ? s.Substring(0, nl) : s;
        return s.Length > 100 ? s.Substring(0, 100) : s;
    }
}
