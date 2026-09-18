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
        var engine = EngineFromStdlib(announce: true);
        // The wasm Tier-1 (plan phase 2): promotion through the ordinary
        // dispatch machinery, execution as native wasm. No-op unless the
        // Shumway.WasmCodegen switch is on.
        BrowserWasmTier.WireJitControl(engine);
        BrowserWasmTier.Attach(engine);
        InstallBundleWasm(engine);
        return engine;
    }

    /// <summary>The engine a page boots with, before the tier is attached.
    /// Separate so a measurement can start from the same place the page
    /// does: with the stdlib already linked and its wasm module waiting to
    /// install, rather than from a bare engine that has to compile the
    /// prelude first.</summary>
    internal static PrologEngine EngineFromStdlib(bool announce)
    {
        using Stream? rs = typeof(WebShumwayApp).Assembly
            .GetManifestResourceStream(StdlibResourceName);
        if (rs is null)
        {
            // The bake target did not run. Correct, just slower — surfaced rather
            // than silent, or a build regression reads as a sluggish engine.
            if (announce)
                WriteToPage($"% no {StdlibResourceName} embedded — compiling the prelude\n");
            return new PrologEngine();
        }
        var ms = new MemoryStream();
        rs.CopyTo(ms);
        return PrologEngine.FromBundle(BundleReader.FromBytes(ms.ToArray()));
    }

    /// <summary>The stdlib bundle's wasm module (linked with --wasm when the
    /// tier is built in): installed now rather than at the first query, so
    /// the boot pays for it and not the user's first goal. Nothing is
    /// written to the page: wasm_compile(status) reports the note.</summary>
    internal static void InstallBundleWasm(PrologEngine engine)
    {
        if (engine.IlPromotion.Wasm is not { } wasm) return;
        // wasm_compile(off) asked for no wasm: installing 530 predicates of
        // it at boot would answer a different question.
        if (BrowserWasmTier.Disabled)
        {
            engine.IlPromotion.PendingWasmModules.Clear();
            BrowserWasmTier.BundleInstallNote = "not installed (wasm_compile off)";
            return;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            wasm.InstallPendingBundles(engine);
            double msTaken = (System.Diagnostics.Stopwatch.GetTimestamp() - t0)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            BrowserWasmTier.BundleInstallNote = $"{wasm.BundleInstallNote} ({msTaken:F0} ms)";
        }
        catch (Exception e)
        {
            BrowserWasmTier.BundleInstallNote = $"failed: {e.GetType().Name}: {e.Message}";
        }
    }
}
