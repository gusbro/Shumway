using Shumway.Embedding;

namespace Shumway.Web;

internal static partial class WebShumwayApp
{
    private const string StdlibResourceName = "stdlib.shum";

    /// <summary>True when this build cannot emit IL and therefore runs the Tier-0
    /// interpreter — always so in a browser (see <c>Shumway.Core.RuntimeCaps</c>).</summary>
    internal static bool Tier0Only => !Shumway.Core.RuntimeCaps.SupportsRuntimeCodegen;

    /// <summary>Boots an engine from the stdlib bundle embedded at build time, so
    /// startup skips compiling the ~780-line prelude — measured ~500 ms to ~345 ms
    /// cold under browser-wasm.
    ///
    /// <para>No filesystem is involved on either path: the bundle is a manifest
    /// resource and the fallback prelude is compiled-in source. That matters here
    /// beyond speed — the page must boot before any workspace is mounted.</para></summary>
    internal static PrologEngine BootEngine()
    {
        using Stream? rs = typeof(WebShumwayApp).Assembly
            .GetManifestResourceStream(StdlibResourceName);
        PrologEngine engine;
        if (rs is null)
        {
            // The bake target did not run. Correct, just slower — surfaced rather
            // than silent, or a build regression reads as a sluggish engine.
            WriteToPage($"% no {StdlibResourceName} embedded — compiling the prelude\n");
            engine = new PrologEngine();
        }
        else
        {
            var ms = new MemoryStream();
            rs.CopyTo(ms);
            engine = PrologEngine.FromBundle(BundleReader.FromBytes(ms.ToArray()));
        }
        // The wasm Tier-1 (plan phase 2): promotion through the ordinary
        // dispatch machinery, execution as native wasm. No-op unless the
        // Shumway.WasmCodegen switch is on.
        BrowserWasmTier.Attach(engine);
        InstallBakedPrelude(engine);
        return engine;
    }

    private const string WasmGroupResourceName = "prelude.wasmgroup";

    /// <summary>The build-time-baked prelude wasm group: installed here,
    /// FIRST — the validation replays the bake's marker interns against a
    /// pool nothing else has touched yet. A rejected asset just means the
    /// tier compiles lazily, as it would without the bake.</summary>
    private static void InstallBakedPrelude(PrologEngine engine)
    {
        if (!Shumway.Core.RuntimeCaps.SupportsWasmCodegen) return;
        // wasm_compile(off) asked for no wasm: installing 530 predicates of
        // it at boot would answer a different question.
        if (BrowserWasmTier.Disabled)
        {
            BrowserWasmTier.BakedInstallNote = "not installed (wasm_compile off)";
            return;
        }
        using Stream? rs = typeof(WebShumwayApp).Assembly
            .GetManifestResourceStream(WasmGroupResourceName);
        if (rs is null) return;
        var ms = new MemoryStream();
        rs.CopyTo(ms);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (BrowserWasmTier.TryInstallBaked(engine, ms.ToArray(), out string reason))
            {
                double msTaken = (System.Diagnostics.Stopwatch.GetTimestamp() - t0)
                    * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                BrowserWasmTier.BakedInstallNote = $"installed ({reason}, {msTaken:F0} ms)";
            }
            else
                BrowserWasmTier.BakedInstallNote = $"rejected: {reason}";
        }
        catch (Exception e)
        {
            BrowserWasmTier.BakedInstallNote = $"failed: {e.GetType().Name}: {e.Message}";
        }
        // Deliberately NOT written to the page: a successful boot has
        // nothing to say, and this greeted every restart with a line about
        // an asset nobody asked for. wasm_compile(status) reports the note
        // on demand, which is where someone looking for it would look.
    }
}
