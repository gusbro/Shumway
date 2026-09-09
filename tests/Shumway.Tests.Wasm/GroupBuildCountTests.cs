using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A group is ONE wasm module: dispatcher, fail/proceed resolver,
/// cursor table and br_table are shared, so adding a member renumbers every
/// cursor and the whole thing is re-emitted. Promoting n predicates one
/// dispatch at a time therefore costs n(n+1)/2 predicate compiles, not n --
/// measured on boards.pl as 136 group builds against 3 in batch. The batch
/// path must be what a consult uses.</summary>
public sealed class GroupBuildCountTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- public a/1.
        :- public b/1.
        :- public c/1.
        :- public d/1.
        :- public e/1.
        :- public run/1.
        a(1). a(2). a(3).
        b(X) :- a(X).
        c(X) :- b(X), X > 1.
        d(X) :- c(X), X > 2.
        e(X) :- d(X).
        run(L) :- findall(X, e(X), L).
        """;

    /// <summary>Builds the tier the way BrowserWasmTier.Attach does, with the
    /// batch promoter wired, and counts group builds.</summary>
    private static (PrologEngine Engine, Func<int> Builds, WasmPromotionStore Store)
        Tier(bool batch, int threshold)
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        var members = new List<WasmGroupMember>();
        int builds = 0;

        void Install()
        {
            var entry = WasmPredicateCompiler.CompileGroup(members, env);
            builds++;
            var map = new Dictionary<int, int>(members.Count);
            foreach (var mm in members) map[mm.Predicate.FunctorId] = mm.Bias;
            world.InstallGroup(entry.Module, entry.EntryCursorByFid,
                entry.CursorByAddress, map, entry.RegisterDemand);
        }

        var wasm = new WasmPromotionStore(store)
        {
            Threshold = threshold,
            CompileAllOnConsult = batch,
            Promoter = (pred, at) =>
            {
                var m = new WasmGroupMember(pred, at, null);
                members.Add(m);
                try { Install(); return new WasmTierDelegate(pred.FunctorId, world).Invoke; }
                catch (WasmCompileException)
                {
                    members.Remove(m);
                    if (members.Count > 0) Install();
                    return null;
                }
            },
            BatchPromoter = cands =>
            {
                var added = new List<WasmGroupMember>();
                foreach (var (p, at) in cands) added.Add(new WasmGroupMember(p, at, null));
                members.AddRange(added);
                try { Install(); }
                catch (WasmCompileException)
                {
                    members.RemoveRange(members.Count - added.Count, added.Count);
                    var good = new List<WasmGroupMember>();
                    foreach (var m in added)
                    {
                        try
                        {
                            WasmPredicateCompiler.CompileGroup(
                                new List<WasmGroupMember> { m }, env);
                            good.Add(m);
                        }
                        catch (WasmCompileException) { }
                    }
                    if (good.Count == 0) return 0;
                    members.AddRange(good);
                    Install();
                    added = good;
                }
                foreach (var m in added)
                    store.RegisterBoundDelegate(m.Predicate.FunctorId,
                        new WasmTierDelegate(m.Predicate.FunctorId, world).Invoke);
                return added.Count;
            },
        };
        store.Wasm = wasm;
        engine.ConsultString(Corpus);
        return (engine, () => builds, wasm);
    }

    [Fact]
    public void TheBatchPathBuildsTheGroupOnce()
    {
        var (e, builds, w) = Tier(batch: true, threshold: 1);
        e.Query("true.");
        int promoted = w.PromoteAllStatics(e);
        Assert.True(e.Query("run(L), length(L, N), N == 1.").Success);
        o.WriteLine($"batch: promoted={promoted} builds={builds()}");

        // ANTI-VACUITY: "one build" is trivially true if nothing was promoted.
        Assert.True(promoted > 5, $"only {promoted} predicates promoted");
        Assert.Equal(1, builds());
    }

    [Fact]
    public void PromotingOneAtATimeRebuildsThatManyTimes()
    {
        // The cost this exists to prevent, stated as a fact rather than a
        // worry: without the batch, the build count TRACKS the promotions.
        var (e, builds, _) = Tier(batch: false, threshold: 1);
        Assert.True(e.Query("run(L), length(L, N), N == 1.").Success);
        int n = e.IlPromotion.PromotedFunctorIds().Count();
        o.WriteLine($"lazy: promoted={n} builds={builds()}");

        Assert.True(n > 1, $"only {n} promoted: the corpus did not exercise the tier");
        Assert.Equal(n, builds());
    }
}
