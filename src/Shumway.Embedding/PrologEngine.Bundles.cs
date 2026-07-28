using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>The bundle loader (extracted component) — see
    /// <see cref="BundleLoader"/>. Lazy so construction needs no ctor edit;
    /// the engine forwards the surface below.</summary>
    private BundleLoader? _bundleLoader;
    private BundleLoader Bundles => _bundleLoader ??= new BundleLoader(this);

    /// <summary>Loads a linked <c>.shum</c> bundle from disk into this engine.</summary>
    public void LoadBundle(string path) => Bundles.LoadBundle(path);

    /// <summary>Loads an in-memory <see cref="Bundle"/> into this engine.</summary>
    public void LoadBundle(Bundle bundle) => Bundles.LoadBundle(bundle);

    internal void LoadBundleCore(Bundle bundle, string? bundleDir)
        => Bundles.LoadBundleCore(bundle, bundleDir);

    /// <summary>Arity <c>save/0,1</c> — snapshots consult history + dynamic
    /// clauses to a bundle file.</summary>
    public void SaveState(string path, bool dynamicOnly = false)
        => Bundles.SaveState(path, dynamicOnly);

    public byte[] SaveStateToBytes(bool dynamicOnly = false)
        => Bundles.SaveStateToBytes(dynamicOnly);

    public void RestoreState(string path) => Bundles.RestoreState(path);

    public void RestoreStateFromBytes(byte[] data) => Bundles.RestoreStateFromBytes(data);

    /// <summary>Test hook — true when the last static link came from the
    /// process-wide shared cache instead of a fresh link.</summary>
    internal bool LastStaticLinkWasSharedHit
    {
        get => Bundles.LastStaticLinkWasSharedHit;
        set => Bundles.LastStaticLinkWasSharedHit = value;
    }

    /// <summary>Test hook — process-wide count of real persisted-IL assembly
    /// loads (cache hits do not count).</summary>
    internal static int PersistedIlLoadCount => BundleLoader.PersistedIlLoadCount;

    internal static bool IsPersistedIlCached(BundleEntry entry)
        => BundleLoader.IsPersistedIlCached(entry);

    internal Shumway.Compiler.Wam.Linker.LinkResult GetOrLinkStatic(
        List<Shumway.Compiler.Wam.CompiledPredicate> staticPreds, int loadOffset)
        => Bundles.GetOrLinkStatic(staticPreds, loadOffset);

    internal void InstallCallIlRewrites(
        Shumway.Interpreter.BytecodeInterpreter interp,
        Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> predicatesByAddress,
        IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate> queryPredicatesByAddress,
        byte[] queryBytes)
        => Bundles.InstallCallIlRewrites(
            interp, predicatesByAddress, queryPredicatesByAddress, queryBytes);
}
