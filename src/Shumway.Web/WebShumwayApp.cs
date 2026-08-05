using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using Shumway.Embedding;

namespace Shumway.Web;

/// <summary>
/// Phase 38 chunk 1 — the feasibility spike. Boots the engine under
/// <c>browser-wasm</c> and reports what the plan needs decided before the rest of
/// WebShumway is designed:
/// <list type="number">
///   <item>does the engine run at all in the browser (Tier-0, with Tier-1 cleanly
///     off — the same gate Native AOT uses)?</item>
///   <item>does <c>System.IO</c> work over Emscripten's MEMFS? The whole filesystem
///     story (workspace, OPFS sync, <c>consult/1</c>, <c>open/4</c>) rides on it.</item>
///   <item>what does it cost — boot time and payload?</item>
/// </list>
/// Every probe is independently guarded: one failure must not hide the others.
/// </summary>
internal static partial class WebShumwayApp
{
    [JSImport("ui.line", "main.js")]
    internal static partial void Line(string text);

    private static void Main()
    {
        // Both flags, because they disagree here and that disagreement is the
        // finding: Mono-wasm reports dynamic code as supported, yet Reflection.Emit
        // and MethodBody.GetILAsByteArray both throw. RuntimeCaps is the real gate.
        Line($"IsDynamicCodeSupported   : {RuntimeFeature.IsDynamicCodeSupported}");
        Line($"RuntimeCaps codegen      : {Shumway.Core.RuntimeCaps.SupportsRuntimeCodegen}  (false ⇒ Tier-0, as intended)");
        Line("");

        PrologEngine? engine = Probe("engine boot", () =>
        {
            var sw = Stopwatch.StartNew();
            var e = new PrologEngine();
            sw.Stop();
            Line($"  prelude consulted in {sw.ElapsedMilliseconds} ms");
            return e;
        });
        if (engine is null) return;

        Probe("arithmetic", () =>
        {
            foreach (var s in engine.QueryAll("X is 6*7."))
                return $"X = {s.Get<long>("X")}";
            return "NO SOLUTION";
        });

        Probe("backtracking", () =>
        {
            var acc = new List<string>();
            foreach (var s in engine.QueryAll("member(X, [a,b,c])."))
                acc.Add(s.Get<string>("X"));
            return string.Join(", ", acc);
        });

        Probe("consult + solve", () =>
        {
            engine.ConsultString(
                "anc(X,Y) :- par(X,Y).  anc(X,Z) :- par(X,Y), anc(Y,Z).  par(a,b).  par(b,c).");
            var acc = new List<string>();
            foreach (var s in engine.QueryAll("anc(a, X)."))
                acc.Add(s.Get<string>("X"));
            return string.Join(", ", acc);
        });

        Probe("output redirect", () =>
        {
            // How the web UI will capture Prolog output. Out must be set BEFORE the
            // engine's first query: query setup builds the StreamRegistry, and
            // user_output keeps whatever writer it was handed then.
            var sink = new StringWriter();
            var fresh = new PrologEngine { Out = sink };
            foreach (var _ in fresh.QueryAll("write(hello_from_wasm), nl.")) break;
            return $"captured '{sink.ToString().Trim()}'";
        });

        // ---- the filesystem question ----
        Line("");
        Line("filesystem (Emscripten MEMFS):");
        Probe("  temp path", () => Path.GetTempPath());
        Probe("  cwd", () => Directory.GetCurrentDirectory());

        const string File1 = "/shumway-spike.pl";
        Probe("  File.WriteAllText", () =>
        {
            File.WriteAllText(File1, "spike_fact(from_memfs).\n");
            return $"wrote {new FileInfo(File1).Length} bytes to {File1}";
        });
        Probe("  File.ReadAllText", () => File.ReadAllText(File1).Trim());
        Probe("  ConsultFile + query", () =>
        {
            engine.ConsultFile(File1);
            foreach (var s in engine.QueryAll("spike_fact(X)."))
                return $"spike_fact({s.Get<string>("X")})";
            return "NO SOLUTION";
        });
        Probe("  Directory ops", () =>
        {
            Directory.CreateDirectory("/ws");
            File.WriteAllText("/ws/a.pl", "a.\n");
            File.WriteAllText("/ws/b.pl", "b.\n");
            return string.Join(", ", Directory.GetFiles("/ws").OrderBy(p => p));
        });
        Probe("  Prolog open/4", () =>
        {
            foreach (var _ in engine.QueryAll(
                "open('/via_prolog.txt', write, S), write(S, hi), nl(S), close(S)."))
                break;
            return $"read back '{File.ReadAllText("/via_prolog.txt").Trim()}'";
        });

        Line("");
        Line("spike complete.");
    }

    /// <summary>Runs one probe, reporting its value or the exception it died of.
    /// Returns default on failure so the remaining probes still run.</summary>
    private static T? Probe<T>(string name, Func<T> body)
    {
        try
        {
            T value = body();
            Line($"{name,-24} : {value}");
            return value;
        }
        catch (Exception ex)
        {
            Line($"{name,-24} : FAILED {ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }
}
