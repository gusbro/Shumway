using System.Linq;
using Shumway.Builtins;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Persisted IL carries no builtin registry id: ids follow
/// registration order, which differs from one process to another. An engine
/// builtin is a direct call to its method; any other builtin is a name-relative
/// <see cref="IlPatchKind.Builtin"/> patch site, resolved against the loading
/// process's registry like atoms and functors.</summary>
public sealed class PersistedIlBuiltinPatchTests
{
    private static int _probeCalls;

    [Fact]
    public void AnEngineBuiltinIsCalledDirectlyAndAHostBuiltinByName()
    {
        _ = new PrologEngine();   // registers the engine's builtins
        int probeId = BuiltinsRegistry.Register("$persisted_builtin_probe", 1,
            engine => engine.UnifyRegisterWithCell(0, Cell.Int(++_probeCalls)));
        Assert.NotNull(BuiltinsRegistry.GetById(probeId).Impl.Target);

        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(
                    "measure(A, N, K) :- atom_length(A, N), '$persisted_builtin_probe'(K).\n", "app"),
            },
            EntryPoints = new[] { new PredicateRef("measure", 3) },
            IncludeCompiledIl = true,
            // No bytecode to fall back on: the bundle runs its IL.
            StripWam = true,
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));

        var bundle = BundleReader.FromBytes(result.Bytes!);
        var entry = bundle.Entries.Single(e => e.ModuleName == "app");
        Assert.Contains(("measure", 3),
            IlPersistedEntryCodec.Decode(entry.CompiledIlEntries!).Select(pe => (pe.Name, pe.Arity)));
        var named = IlPatchSiteCodec.Decode(entry.CompiledIlPatches!)
            .Where(s => s.Kind == IlPatchKind.Builtin).Select(s => s.Name + "/" + s.Arity).ToList();
        Assert.Contains("$persisted_builtin_probe/1", named);
        Assert.DoesNotContain("atom_length/2", named);

        var engine = PrologEngine.FromBundle(BundleReader.FromBytes(result.Bytes!));
        int before = _probeCalls;
        Assert.True(engine.Query("measure(abcde, N, K), N == 5, integer(K).").Success);
        Assert.False(engine.Query("measure(abc, 4, _).").Success);
        Assert.True(_probeCalls > before, "the host builtin did not run");
    }
}
