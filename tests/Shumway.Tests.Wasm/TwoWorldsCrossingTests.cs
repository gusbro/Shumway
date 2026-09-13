using Shumway.Compiler.Wasm;
using Shumway.Compiler.Wam;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Two wasm modules, one engine, calling and backtracking into each
/// other. In the browser this shape has existed since the baked prelude got a
/// world of its own, and it had no test anywhere: every desktop test installs a
/// single group.
///
/// <para>It is the shape the many-modules arc is about. Today a call that
/// leaves a module closes the chain, goes back through the interpreter and
/// opens another one; the arc replaces that with a tail call inside wasm. So
/// this fixture pins down what the crossing DOES before anything changes, and
/// counts how often it happens -- because the counts are the only thing that
/// can tell the two designs apart. Answers cannot: a crossing is correct
/// either way.</para></summary>
public sealed class TwoWorldsCrossingTests(ITestOutputHelper o)
{
    // Two halves that call each other in both directions and leave choice
    // points behind, so backtracking crosses too.
    private const string Corpus = """
        :- public lo/1.
        :- public hi/1.
        :- public both/1.
        :- public chase/2.
        lo(1). lo(2). lo(3).
        hi(X) :- lo(X), X > 1.
        both(X) :- lo(X).
        both(X) :- hi(X).
        chase(X, Y) :- both(X), hi(Y), Y >= X.
        """;

    /// <summary>Installs the corpus across TWO worlds, splitting the predicates
    /// by the caller's choice, and returns the engine.</summary>
    private static (PrologEngine Engine, int InA, int InB) TwoWorlds(
        System.Func<string, bool> goesToA)
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
        engine.ConsultString(Corpus);
        engine.Query("true.");                      // materialise the static link

        var env = new EngineWasmCompileEnv();
        // Siblings of ONE engine: one memory, one function table, one resume
        // table. That is what lets a module discover a marker is another's
        // AND reach it without going out to the host.
        // Lives as long as the engine does: the worlds are bound into it.
        var space = new DesktopWasmSpace();
        var worldA = new DesktopWasmWorld(space);
        var worldB = new DesktopWasmWorld(space);
        var inA = new List<WasmGroupMember>();
        var inB = new List<WasmGroupMember>();

        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (aid, _) = Shumway.Core.FunctorTable.Lookup(pred.FunctorId);
            string name = Shumway.Core.AtomTable.GetById(aid)?.Name ?? "";
            if (!name.EndsWith("lo") && !name.EndsWith("hi")
                && !name.EndsWith("both") && !name.EndsWith("chase")) continue;
            (goesToA(name) ? inA : inB).Add(new WasmGroupMember(pred, addr, null));
        }

        void Install(DesktopWasmWorld world, List<WasmGroupMember> members)
        {
            if (members.Count == 0) return;
            TieredEngine.Install(world, members, env);
            foreach (var m in members)
                store.RegisterBoundDelegate(m.Predicate.FunctorId,
                    new WasmTierDelegate(m.Predicate.FunctorId, world).Invoke);
        }
        Install(worldA, inA);
        Install(worldB, inB);
        return (engine, inA.Count, inB.Count);
    }

    // The count is NOT written here: Tier-0 is the oracle, so a wrong
    // expectation cannot quietly become the thing being asserted.
    private const string Goal = "findall(X-Y, chase(X, Y), L), length(L, N).";

    private static string Answer(PrologEngine e)
    {
        var r = e.Query(Goal);
        Assert.True(r.Success, "the goal failed");
        var parts = new List<string>();
        foreach (var (name, value) in r.Bindings) parts.Add($"{name}={value}");
        parts.Sort(System.StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    /// <summary>The crossing answers exactly what one world answers. This is
    /// the part that CANNOT regress silently, and also the part that proves
    /// nothing about cost.</summary>
    [Fact]
    public void TwoWorldsAnswerWhatOneWorldAnswers()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        string oracle = Answer(plain);

        var (whole, allA, none) = TwoWorlds(_ => true);
        Assert.True(none == 0 && allA > 0, $"the one-world split is not one world: {allA}/{none}");
        var (split, inA, inB) = TwoWorlds(n => n.EndsWith("lo") || n.EndsWith("both"));
        Assert.True(inA > 0 && inB > 0, $"the split put everything on one side: {inA}/{inB}");

        o.WriteLine($"tier0={oracle} oneWorld={Answer(whole)} twoWorlds={Answer(split)} "
            + $"(split {inA}/{inB})");
        Assert.Equal(oracle, Answer(whole));
        Assert.Equal(oracle, Answer(split));
    }

    /// <summary>What the crossing COSTS, stated as a count. A call, a return
    /// or a backtrack whose target lives in the other module is resolved
    /// through the shared table and taken as a tail call INSIDE wasm: a hop.
    /// It must not close the chain (a foreign exit), nor go out to the host
    /// and back (a switch), nor deopt. So the split costs exactly the chains
    /// one world costs, plus hops; and one world takes no hop at all.
    ///
    /// <para>This is the counter that catches "correct but slow": a hop that
    /// quietly fell back to the host still answers right, and only these
    /// numbers move.</para></summary>
    [Fact]
    public void CrossingModulesHopsInsideWasmAndCostsNoExtraChain()
    {
        var (whole, _, _) = TwoWorlds(_ => true);
        WasmTierDelegate.ResetDiag();
        _ = Answer(whole);
        long wholeForeign = WasmTierDelegate.DiagForeignExits;
        long wholeEntries = WasmTierDelegate.DiagEntries;
        long wholeSwitches = WasmTierDelegate.DiagSwitches;
        long wholeHops = WasmTierDelegate.DiagInWasmHops;

        var (split, _, _) = TwoWorlds(n => n.EndsWith("lo") || n.EndsWith("both"));
        WasmTierDelegate.ResetDiag();
        _ = Answer(split);
        long splitForeign = WasmTierDelegate.DiagForeignExits;
        long splitEntries = WasmTierDelegate.DiagEntries;

        o.WriteLine($"one world : entries={wholeEntries} switches={wholeSwitches} "
            + $"foreignExits={wholeForeign} hops={wholeHops}");
        o.WriteLine($"two worlds: entries={splitEntries} foreignExits={splitForeign} "
            + $"deopts={WasmTierDelegate.DiagDeopts} "
            + $"boundary={WasmTierDelegate.DiagBoundaryExits} "
            + $"tailExits={WasmTierDelegate.DiagTailExits} "
            + $"switches={WasmTierDelegate.DiagSwitches} "
            + $"hops={WasmTierDelegate.DiagInWasmHops} "
            + $"builtins={WasmTierDelegate.DiagBuiltins}");
        foreach (var (pc, hits) in WasmTierDelegate.DeoptRanking())
            o.WriteLine($"  deopt 0x{pc:X} x{hits}");
        foreach (var (fid, addr, hits) in WasmTierDelegate.SwitchRanking())
        {
            var (aid, ar) = Shumway.Core.FunctorTable.Lookup(fid);
            string name = Shumway.Core.AtomTable.GetById(aid)?.Name ?? "?";
            o.WriteLine($"  switch {name}/{ar} @0x{addr:X} x{hits}");
        }

        // ANTI-VACUITY: the tier has to have run at all.
        Assert.True(wholeEntries > 0, "nothing entered the tier");
        // One module: every target resolves in the chain, and there is
        // nowhere to hop to.
        Assert.Equal(0, wholeForeign);
        Assert.Equal(0, wholeHops);
        // Two modules: the crossings happened (ANTI-VACUITY: as hops), and
        // none of them left wasm in any of the three ways it could.
        Assert.True(WasmTierDelegate.DiagInWasmHops > 0, "the split never hopped");
        Assert.Equal(0, splitForeign);
        Assert.Equal(0, WasmTierDelegate.DiagSwitches);
        Assert.Equal(0, WasmTierDelegate.DiagDeopts);
        Assert.Equal(wholeEntries, splitEntries);
    }
}
