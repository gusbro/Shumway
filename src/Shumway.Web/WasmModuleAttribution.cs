using Shumway.Embedding;

namespace Shumway.Web;

/// <summary>Which Prolog module a compiled predicate came from. Nothing on
/// <c>CompiledPredicate</c> records it, so this recovers it the only way
/// available: from the functor's name and the engine's module manifests.
///
/// <para>Used both to fold library internals out of the promoted list and to
/// attribute call sites to module pairs, which is why it lives here rather
/// than inline in the status report.</para></summary>
internal sealed class WasmModuleAttribution
{
    private readonly Dictionary<int, string> _exporter = new();

    public WasmModuleAttribution(PrologEngine engine)
    {
        // A library's EXPORTS carry no module prefix (clpfd's in/2, #=/2,
        // label/1 look exactly like user predicates), so the qualified-name
        // rule alone leaves dozens of them unattributed. The manifests name
        // them.
        foreach (var (modName, manifest) in engine.Modules)
            foreach (int pf in manifest.PublicFunctors)
                _exporter[pf] = modName;
    }

    /// <summary>A name's module is its prefix up to the scope '$' — one more
    /// '$' along when the name itself starts with one ($q$..., $prelude$$...).
    /// </summary>
    public static string PrefixModuleOf(string name)
    {
        int at = name.IndexOf('$', name.StartsWith('$') ? 1 : 0);
        if (at <= 0) return "";
        int scope = name.StartsWith('$') ? name.IndexOf('$', at + 1) : at;
        return scope > 0 ? name[..at] : "";
    }

    /// <summary>The owning module of <paramref name="functorId"/>: its name
    /// prefix, else the manifest that exports it, else "user" for source
    /// predicates and "generated" for compiler helpers ($disj_N and friends,
    /// which belong to whatever clause spawned them).</summary>
    public string ModuleOf(int functorId)
    {
        var (aid, _) = Shumway.Core.FunctorTable.Lookup(functorId);
        string name = Shumway.Core.AtomTable.GetById(aid)?.Name ?? "";
        string mod = PrefixModuleOf(name);
        if (mod is "" && _exporter.TryGetValue(functorId, out string? owner))
            mod = owner;
        if (mod is "" or "user" && name.StartsWith('$')) return "generated";
        return mod is "" ? "user" : mod;
    }
}
