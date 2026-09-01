using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The cross-process bundle flake's root cause, pinned: persisted
/// IL used to bake builtin REGISTRY IDS as immediates, and registry ids are
/// assigned in registration order — which concurrent engine construction in
/// the building process can shuffle. A child process then dispatched a
/// DIFFERENT builtin (the type_error(evaluable) / fd_bound / '$native_run'
/// error zoo). Builtin references now travel as name-relative
/// <see cref="Shumway.Compiler.Il.IlPatchKind.Builtin"/> patch sites,
/// resolved against the LOADING process's registry like atoms and functors
/// always were.</summary>
public sealed class PersistedIlBuiltinPatchTests
{
    [Fact]
    public void BuiltinDispatchIsNameRelativeInPersistedIl()
    {
        byte[] bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(
                    "measure(A, N) :- atom_length(A, N).\n"
                    + "scenario :- measure(abcde, N), N =:= 5.\n", "app"),
            },
            EntryPoints = new[] { new PredicateRef("scenario", 0) },
            IncludeCompiledIl = true,
        }).Bytes!;

        var bundle = BundleReader.FromBytes(bytes);
        var entry = bundle.Entries.Single(e => e.ModuleName == "app");
        Assert.NotNull(entry.CompiledIlPatches);
        var sites = Shumway.Compiler.Il.IlPatchSiteCodec.Decode(entry.CompiledIlPatches!);
        Assert.Contains(sites,
            s => s.Kind == Shumway.Compiler.Il.IlPatchKind.Builtin);

        // And the patched bundle still runs in a fresh engine.
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.True(engine.Query("scenario.").Success);
    }
}
