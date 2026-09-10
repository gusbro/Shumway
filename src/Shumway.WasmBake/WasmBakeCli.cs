using Shumway.Compiler.Wasm;
using Shumway.Embedding;

namespace Shumway.WasmBake;

/// <summary>shumway-wasmbake: compiles a stdlib bundle's whole static program
/// into one wasm group module ahead of time, for the web build to embed. The
/// asset is process-state-sensitive (interned markers, linked addresses,
/// id-bearing operands), so this tool reproduces the WEB boot exactly — same
/// bundle bytes, same Tier-0-only caps, same link-materialising query — and
/// records the evidence <see cref="WasmBakedGroup.Validate"/> replays at
/// install; a boot that diverges rejects the asset and compiles instead.</summary>
internal static class WasmBakeCli
{
    internal static int Main(string[] args)
    {
        string? input = null, output = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length) output = args[++i];
            else if (input is null) input = args[i];
            else { Console.Error.WriteLine($"unexpected argument: {args[i]}"); return 2; }
        }
        if (input is null || output is null)
        {
            Console.Error.WriteLine("usage: shumway-wasmbake <stdlib.shum> -o <prelude.wasmgroup>");
            return 2;
        }

        // Mirror the browser: no IL tier (its promotions would intern markers
        // the web boot never sees), wasm codegen on, and the builtin block
        // interned first (the web Main does it before exports exist, to keep
        // early ids free of concurrent-intern shuffle).
        AppContext.SetSwitch("Shumway.RuntimeCodegen", false);
        AppContext.SetSwitch("Shumway.WasmCodegen", true);
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();

        var engine = PrologEngine.FromBundle(BundleReader.FromBytes(File.ReadAllBytes(input)));
        var store = engine.IlPromotion;
        WasmBakedGroup? baked = null;
        store.Wasm = new WasmPromotionStore(store)
        {
            BatchPromoter = candidates =>
            {
                var members = new List<WasmGroupMember>(candidates.Count);
                foreach (var (pred, addr) in candidates)
                    members.Add(new WasmGroupMember(pred, addr,
                        store.FloatPoolProvider?.Invoke(pred.FunctorId)));
                baked = WasmBakedGroup.BakeCompilable(members,
                    new EngineWasmCompileEnv(),
                    (m, why) => Console.WriteLine("shumway-wasmbake: skipping "
                        + $"functor {m.Predicate.FunctorId}: {why}"));
                return baked.Members.Count;
            },
        };
        // The same throwaway goal the installer runs to materialise the
        // static link — intern parity between bake and install.
        engine.Query("true.");
        int count = store.Wasm.PromoteAllStatics(engine);
        if (baked is null || count <= 0)
        {
            Console.Error.WriteLine("shumway-wasmbake: nothing to bake");
            return 1;
        }
        using (var fs = File.Create(output))
            baked.Write(fs);
        Console.WriteLine($"shumway-wasmbake: {count} predicates, "
            + $"{baked.Module.Length} module bytes, {baked.Markers.Count} markers "
            + $"-> {output}");
        return 0;
    }

}
