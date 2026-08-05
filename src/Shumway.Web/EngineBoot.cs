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
        if (rs is null)
        {
            // The bake target did not run. Correct, just slower — surfaced rather
            // than silent, or a build regression reads as a sluggish engine.
            WriteToPage($"% no {StdlibResourceName} embedded — compiling the prelude\n");
            return new PrologEngine();
        }
        var ms = new MemoryStream();
        rs.CopyTo(ms);
        return PrologEngine.FromBundle(BundleReader.FromBytes(ms.ToArray()));
    }
}
