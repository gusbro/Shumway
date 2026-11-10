using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-049 in a baked library module: a head that binds a frozen
/// variable, then a builtin. The goal the binding woke runs before the
/// builtin, as on Tier-0, so atom/1 sees what it bound. A baked module is
/// compiled from the library's bytecode, where a builtin can be a call to
/// its functor rather than a call_builtin, and both must wake.</summary>
public sealed class BakedWakeTests
{
    private const string Library = """
        :- module(wlib, [wp/2]).
        wp(a, W) :- atom(W), !.
        wp(_, no).
        """;

    private const string Goal = "findall(W, (freeze(X, W = yes), wlib:wp(X, W)), Ws).";

    private static Bundle Bake()
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Library, "wlib") },
            Library = true,
            WasmBaker = b => WasmBundleTier.Bake(b, stdlib: false),
        });
        Assert.True(link.Success, string.Join(", ", link.Diagnostics.Select(d => d.Message)));
        return BundleReader.FromBytes(link.Bytes!);
    }

    private static string Answer(PrologEngine engine)
    {
        var r = engine.Query(Goal);
        Assert.True(r.Success);
        return r.Bindings["Ws"].ToString()!;
    }

    [DiagFact]
    public void TheWokenGoalRunsBeforeTheBuiltinInABakedModule()
    {
        var bundle = Bake();
        Assert.Single(bundle.WasmModules);

        var tier0 = new PrologEngine();
        tier0.IlPromotion.Threshold = 0;
        tier0.ConsultString(":- use_module(library(coroutining)).");
        tier0.LoadBundle(bundle);
        string expected = Answer(tier0);

        var world = new DesktopWasmWorld();
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = int.MaxValue,
            BundleInstaller = (eng, bytes) => WasmBundleTier.Install(eng, world, bytes),
        };
        engine.ConsultString(":- use_module(library(coroutining)).");
        engine.LoadBundle(bundle);
        _ = Answer(engine);                         // installs the baked module
        WasmTierDelegate.ResetDiag();
        string actual = Answer(engine);

        Assert.Equal(".(yes, [])", expected);
        Assert.Equal(expected, actual);
        Assert.True(WasmTierDelegate.DiagEntries > 0, "wp/2 did not run from the baked module");
    }
}
