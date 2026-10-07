using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-035 on the wasm tier: code compiled debuggable is neither in
/// the batch nor promoted by hotness. A fact, or a rule ending in a builtin
/// with no inline goal, carries no debug opcode for the compiler to refuse,
/// and promoted, a breakpoint in it never fired. Compiled release, the same
/// predicates promote, so a pass is not a shape the tier refuses anyway.
/// </summary>
public sealed class DebuggableCodeStaysOffTheTierTests
{
    private const string Program = """
        :- public fact/1, tail/1.
        fact(a).
        fact(b).
        tail(X) :- atom_length(X, _).
        """;

    private const string Goal = "fact(_), tail(abc).";

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name).Id, arity);

    /// <summary>A world whose promoters compile each candidate in a module of
    /// its own, as the browser's lazy grain does.</summary>
    private static (PrologEngine Engine, WasmPromotionStore Wasm) Tiered(int threshold, bool debug)
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        PredicateDelegate? Install(CompiledPredicate pred, int addr)
        {
            var m = new WasmGroupMember(pred, addr, store.FloatPoolProvider?.Invoke(pred.FunctorId));
            try { WasmPredicateCompiler.CompileGroup(new[] { m }, env); }
            catch (WasmCompileException) { return null; }
            TieredEngine.Install(world, new List<WasmGroupMember> { m }, env, store);
            store.Wasm?.NoteInstalled(pred.FunctorId, addr, pred);
            return new WasmTierDelegate(pred.FunctorId, world).Invoke;
        }
        var wasm = new WasmPromotionStore(store)
        {
            Threshold = threshold,
            CompileAllOnConsult = false,
            Promoter = Install,
            BatchPromoter = candidates =>
            {
                int n = 0;
                foreach (var (pred, addr) in candidates)
                    if (Install(pred, addr) is { } del)
                    {
                        store.RegisterBoundDelegate(pred.FunctorId, del);
                        n++;
                    }
                return n;
            },
        };
        wasm.LiveRefreshed = world.RefreshLiveAddresses;
        wasm.StaleEvicted = world.Evict;
        store.Wasm = wasm;
        engine.ConsultString($":- set_prolog_flag(compile_mode, {(debug ? "debug" : "release")}).");
        engine.ConsultString(Program);
        return (engine, wasm);
    }

    private static void AssertTier(PrologEngine engine, bool debug)
    {
        foreach (var (name, what) in new[] { ("fact", "fact"), ("tail", "rule ending in a builtin") })
            Assert.True(engine.IlPromotion.IsPromoted(Fid(name, 1)) != debug,
                debug ? $"a debuggable {what} promoted"
                      : $"the {what} did not promote compiled release: nothing here is measured");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheBatchLeavesDebuggableCodeOut(bool debug)
    {
        var (engine, wasm) = Tiered(threshold: 1_000_000, debug);
        Assert.True(engine.Query(Goal).Success);
        Assert.True(wasm.PromoteAllStatics(engine) > 0);
        AssertTier(engine, debug);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HotnessLeavesDebuggableCodeOut(bool debug)
    {
        var (engine, _) = Tiered(threshold: 2, debug);
        for (int i = 0; i < 6; i++)
            Assert.True(engine.Query(Goal).Success);
        AssertTier(engine, debug);
    }
}
