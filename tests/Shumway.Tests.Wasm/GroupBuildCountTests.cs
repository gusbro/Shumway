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

    internal static (PrologEngine, Func<int>, WasmPromotionStore) TierForProbe()
        => Tier(batch: true, threshold: 1);

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
    public void AGoalThatChangesNothingDoesNoWork()
    {
        // The tick runs after EVERY query, not only after a consult, and its
        // work is O(all installed predicates) with a bytecode hash each. A
        // goal that leaves the static program alone must therefore cost
        // nothing here: no build, no reconcile, no notice.
        var (e, builds, w) = Tier(batch: true, threshold: 1);
        int announced = 0;
        w.BatchStarting = _ => announced++;
        // Through the tick, which is the host's only entry point: it is what
        // records the link the batch reconciled against.
        Assert.True(w.CompileAllTick(e) > 5);
        int afterFirst = builds();
        Assert.Equal(1, announced);         // the real build DID announce
        announced = 0;

        for (int i = 0; i < 5; i++)
            Assert.True(e.Query("run(L), length(L, N), N == 1.").Success);
        int worked0 = w.BatchTicksWorked;
        int extra = 0;
        for (int i = 0; i < 5; i++) extra += w.CompileAllTick(e);

        o.WriteLine($"builds after batch={afterFirst}, after 5 goals+ticks="
            + $"{builds()}, compiled={extra}, announced={announced}, "
            + $"ticksThatWorked={w.BatchTicksWorked - worked0}");
        // Nothing static changed across those goals, so no tick may work.
        Assert.Equal(afterFirst, builds());
        Assert.Equal(0, extra);
        Assert.Equal(0, announced);
        Assert.Equal(worked0, w.BatchTicksWorked);
    }

    [Fact]
    public void ATickWithNothingToCompileSaysNothing()
    {
        // What the user saw: after a restart the whole program is already on
        // the tier (the baked prelude), the next tick still runs because the
        // link was rebuilt, and it announced a build of zero predicates. The
        // notice has to be keyed on the candidate COUNT, which only the batch
        // itself knows, not on "a tick is about to run".
        var (e, builds, w) = Tier(batch: true, threshold: 1);
        var announcedCounts = new List<int>();
        w.BatchStarting = n => announcedCounts.Add(n);
        Assert.True(w.CompileAllTick(e) > 5);
        int afterFirst = builds();

        // A consult that adds nothing new: it invalidates the link, so the
        // tick DOES run, and it must still find nothing to compile.
        e.ConsultString("% nothing here\n");
        int n2 = w.CompileAllTick(e);

        o.WriteLine($"first announce={string.Join(",", announcedCounts)} "
            + $"secondTick={n2} builds={builds()} (was {afterFirst})");
        // The property that matters: a notice is only ever raised with real
        // candidates in hand. It may still announce a batch the backend then
        // refuses in full (n2 == 0 here) -- that is a refusal, not a phantom.
        Assert.All(announcedCounts, n => Assert.True(n > 0,
            "announced a build of zero predicates"));
        Assert.True(announcedCounts[0] > 5);
    }

    [Fact]
    public void AStragglerDoesNotRebuildTheGroupInsideTheQuery()
    {
        // The group is one module, so promoting a single latecomer re-emits
        // ALL of it. Under the batch the whole program is on the tier, which
        // makes that a full rebuild landing in the middle of the user's
        // query -- after the goal has written its output, before it answers.
        // The straggler waits for the boundary instead.
        var (e, builds, w) = Tier(batch: true, threshold: 1);
        Assert.True(w.CompileAllTick(e) > 5);
        int afterBatch = builds();

        // A predicate the batch never saw, dispatched from inside a query.
        e.ConsultString(":- public latecomer/1.\nlatecomer(X) :- e(X).\n");
        Assert.True(e.Query("latecomer(X), X == 3.").Success);
        int duringQuery = builds();

        int afterTick = w.CompileAllTick(e);
        o.WriteLine($"builds: afterBatch={afterBatch} duringQuery={duringQuery} "
            + $"afterBoundary={builds()} tickCompiled={afterTick}");

        // Nothing was rebuilt while the query ran...
        Assert.Equal(afterBatch, duringQuery);
        // ...and the boundary picked the latecomer up.
        Assert.True(afterTick > 0, "the boundary tick compiled nothing");
        Assert.True(builds() > duringQuery, "the boundary tick did not build");

        // And the build NAMES who asked for it. A rebuild nobody can explain
        // is the thing that made this hard to find in the first place: the
        // count alone leaves you guessing between the consult you just did
        // and some predicate quietly demanding one.
        Assert.NotEmpty(w.LastBatchTrigger);
        var (aid, ar) = Shumway.Core.FunctorTable.Lookup(w.LastBatchTrigger[0]);
        string who = $"{Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar}";
        o.WriteLine($"trigger: {who}");
        Assert.Contains("latecomer", who);
    }

    [Theory]
    // The shape MetaTransform gives a query stub's helpers, bare and
    // module-mangled (MetaTransform.HelperName: "{prefix}${kind}_{id}").
    [InlineData("$q$disj_1", true)]
    [InlineData("$q$neg_2", true)]
    [InlineData("user$$q$disj_1", true)]
    [InlineData("__query__", true)]
    // ...and what must NOT be swept up with them: consult-time helpers carry
    // the engine's monotonic id and are perfectly stable.
    [InlineData("$disj_1", false)]
    [InlineData("user$$disj_17", false)]
    [InlineData("clpfd$clpfd_run", false)]
    [InlineData("queens", false)]
    public void QueryStubHelpersAreExcludedFromPromotion(string name, bool excluded)
    {
        // A query stub synthesises helpers for its ;, -> and \+, named with a
        // reserved "$q" prefix precisely so the names are REUSED
        // query-to-query (MetaTransform.HelperPrefix). One functor id, a
        // different body every time: the same replay hazard __query__ is
        // excluded for. On the wasm tier promoting one also rebuilt the whole
        // group module on every such query -- 13 seconds after consulting
        // boards.pl, attributed by the status line to '$q$disj_1'/5.
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern(name, permanent: true).Id, 5);
        Assert.Equal(excluded, IlPromotionStore.IsExcludedFromPromotion(fid));
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
