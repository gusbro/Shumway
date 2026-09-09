using Shumway.Embedding;

namespace Shumway.Web;

internal static partial class WasmCoupling
{
    /// <summary>Call sites between modules, heaviest edge first. The group is
    /// ONE wasm module, so a call inside it is a branch; a call that leaves it
    /// closes the chain, goes back through the interpreter and opens another.
    /// Splitting the group along a module boundary therefore turns every edge
    /// crossing that boundary into a host round trip, which is what this
    /// report is for: it names the boundaries and how tightly each is woven,
    /// so the question is settled with evidence rather than intuition.
    ///
    /// <para>STATIC by construction, and the report says so: it counts call
    /// SITES in the compiled code, not how often they are taken. A single site
    /// inside a loop outweighs fifty that run once. Measuring frequency would
    /// cost something on the hot path; this costs nothing.</para></summary>
    public static string Report(PrologEngine engine,
        IReadOnlyDictionary<(int Caller, int Callee), int> callSites,
        int topN = 8)
    {
        if (callSites.Count == 0) return "";
        var attribution = new WasmModuleAttribution(engine);
        var byPair = new Dictionary<(string, string), int>();
        int internalSites = 0;
        foreach (var ((caller, callee), sites) in callSites)
        {
            string a = attribution.ModuleOf(caller), b = attribution.ModuleOf(callee);
            if (a == b) { internalSites += sites; continue; }
            var key = (a, b);
            byPair.TryGetValue(key, out int seen);
            byPair[key] = seen + sites;
        }
        if (byPair.Count == 0)
            return $"%   module coupling: none ({internalSites} call sites, all "
                 + "within one module)\n";

        var ranked = new List<((string From, string To) Pair, int Sites)>();
        foreach (var (pair, sites) in byPair) ranked.Add((pair, sites));
        ranked.Sort((x, y) => y.Sites.CompareTo(x.Sites));

        int crossing = 0;
        foreach (var r in ranked) crossing += r.Sites;
        var sb = new System.Text.StringBuilder();
        sb.Append($"%   module coupling: {crossing} call sites cross a module "
                + $"boundary, {internalSites} do not\n");
        sb.Append("%     (sites in the code, NOT how often they run)\n");
        for (int i = 0; i < ranked.Count && i < topN; i++)
            sb.Append($"%     {ranked[i].Sites} {ranked[i].Pair.From} -> "
                    + $"{ranked[i].Pair.To}\n");
        if (ranked.Count > topN)
            sb.Append($"%     ... and {ranked.Count - topN} more edges\n");
        return sb.ToString();
    }
}
