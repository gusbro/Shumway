using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>The meta cache is what lets a module resolve a meta-call without
/// leaving. It was keyed by (module, goal functor), which sufficed while
/// call/1 was the only inline form: nothing is appended, so the goal's
/// functor IS the resolved one.
///
/// <para>call/N breaks that. <c>call(G, X)</c> and <c>call(G, X, Y)</c> have
/// the same goal and resolve to different predicates, and the module cannot
/// derive the resolved functor -- it reads the goal off the heap, and
/// functor ids are interned rather than computed. So the key is what the
/// module can form: the goal, and how many arguments this call site
/// appends.</para></summary>
public sealed class MetaCacheAppendedKeyTests
{
    private static readonly object Map = new();

    private static WasmResumeTable Filled(params (int Appended, int Resolved)[] rows)
    {
        var t = new WasmResumeTable();
        foreach (var (appended, resolved) in rows)
            t.NoteMetaResolution(Map, moduleAtomId: 5, goalFid: 77,
                                 appended: appended, resolvedFid: resolved);
        return t;
    }

    /// <summary>THE POINT: one goal, two call sites of different width, two
    /// answers. Keyed on the goal alone the second would overwrite the
    /// first, and a call(G,X) would jump into the call(G,X,Y) predicate --
    /// the same arity check that guards the module would then be the only
    /// thing between that and a wrong answer.</summary>
    [Fact]
    public void TheSameGoalAtDifferentWidthsKeepsSeparateRows()
    {
        var t = Filled((1, 111), (2, 222));
        Assert.Equal(111, t.MetaLookup(5, 77, 1));
        Assert.Equal(222, t.MetaLookup(5, 77, 2));
    }

    /// <summary>call/1 is appended = 0, and it must not collide with the
    /// wider ones either.</summary>
    [Fact]
    public void CallOneHasItsOwnRow()
    {
        var t = Filled((0, 10), (1, 11), (7, 17));
        Assert.Equal(10, t.MetaLookup(5, 77, 0));
        Assert.Equal(11, t.MetaLookup(5, 77, 1));
        Assert.Equal(17, t.MetaLookup(5, 77, 7));
    }

    /// <summary>A width nobody filled is a miss, not another row's answer.
    /// </summary>
    [Fact]
    public void AnUnfilledWidthMisses()
    {
        var t = Filled((1, 111));
        Assert.Equal(111, t.MetaLookup(5, 77, 1));
        Assert.Equal(-1, t.MetaLookup(5, 77, 2));
        Assert.Equal(-1, t.MetaLookup(5, 78, 1));      // different goal
        Assert.Equal(-1, t.MetaLookup(6, 77, 1));      // different module
    }

    /// <summary>The key and the probe are two halves of one agreement, and
    /// the module recomputes BOTH. Distinct inputs must land on distinct
    /// keys, or a probe that finds a slot would accept the wrong row.
    /// </summary>
    [Fact]
    public void DistinctInputsGiveDistinctKeys()
    {
        var seen = new HashSet<long>();
        for (int m = 0; m < 4; m++)
            for (int g = 0; g < 4; g++)
                for (int a = 0; a <= WasmResumeTable.MaxAppended; a++)
                    Assert.True(seen.Add(WasmResumeTable.MetaKey(m, g, a)),
                        $"key collision at module {m}, goal {g}, appended {a}");
    }

    /// <summary>A width past what the key carries is REFUSED rather than
    /// folded onto a narrower row: call/9 and wider keep going to the host,
    /// which is what they did before there was a cache.</summary>
    [Fact]
    public void AWidthPastTheKeyIsNotCached()
    {
        var t = Filled((WasmResumeTable.MaxAppended + 1, 999));
        Assert.Equal(-1, t.MetaLookup(5, 77, WasmResumeTable.MaxAppended + 1));
        // ...and it did not land on appended = 0 either.
        Assert.Equal(-1, t.MetaLookup(5, 77, 0));
    }
}
