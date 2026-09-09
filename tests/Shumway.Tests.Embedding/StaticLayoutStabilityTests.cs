using System.Linq;
using Shumway.Compiler.Wam;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Consulting a source must not move the code that was already
/// compiled. The static region is laid out append-only: a predicate keeps
/// the ordinal it was first laid out with, and anything new — or CHANGED by
/// a reconsult — takes a fresh ordinal at the end.
///
/// <para>The layout used to be "whatever the module compiler produced, then
/// the precompiled prelude appended", so consulting one fact pushed the
/// whole prelude down by that fact's size. Every address baked against the
/// old layout then pointed into different code: the wasm tier's modules bake
/// deopt pcs, resume markers and BP encodings, and that is how a
/// library load turned into "reserved_invalid opcode … bytecode
/// corruption".</para></summary>
public sealed class StaticLayoutStabilityTests
{
    private static Dictionary<int, (int Addr, string Name)> Layout(PrologEngine e)
    {
        var d = new Dictionary<int, (int, string)>();
        var link = e._staticLink;
        if (link is null) return d;
        foreach (var (addr, pred) in link.PredicatesByAddress)
        {
            var (aid, ar) = Shumway.Core.FunctorTable.Lookup(pred.FunctorId);
            d[pred.FunctorId] =
                (addr, $"{Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar}");
        }
        return d;
    }

    private static (int Same, int Moved, List<string> MovedNames) Compare(
        Dictionary<int, (int Addr, string Name)> before,
        Dictionary<int, (int Addr, string Name)> after)
    {
        int same = 0, moved = 0;
        var names = new List<string>();
        foreach (var (fid, (addr, name)) in before)
        {
            if (!after.TryGetValue(fid, out var now)) continue;
            if (now.Addr == addr) same++;
            else { moved++; if (names.Count < 5) names.Add($"{name} {addr}->{now.Addr}"); }
        }
        return (same, moved, names);
    }

    [Fact]
    public void ConsultingNewCodeDoesNotMoveWhatWasAlreadyThere()
    {
        var e = new PrologEngine();
        e.Query("true.");                       // materialise the static link
        var before = Layout(e);
        Assert.True(before.Count > 100, $"expected the prelude, saw {before.Count}");

        e.ConsultString("newfact(1).\nnewfact(2).\nnewrule(X) :- newfact(X).\n");
        e.Query("true.");
        var after = Layout(e);
        Assert.True(after.Count > before.Count, "the new predicates should be linked");

        var (same, moved, names) = Compare(before, after);
        Assert.Equal(0, moved);
        Assert.Equal(before.Count, same);
        Assert.True(e.Query("newrule(2).").Success);
        Assert.True(e.Query("member(b, [a,b,c]).").Success);
        Assert.Empty(names);
    }

    [Fact]
    public void ALibraryLoadDoesNotMoveThePreludeEither()
    {
        // The reported shape: use_module(library(clpfd)) brings hundreds of
        // predicates in one go.
        var e = new PrologEngine();
        e.Query("true.");
        var before = Layout(e);

        e.ConsultString(":- use_module(library(clpfd)).\nsmall(X) :- X in 1..3, X #> 1.\n");
        e.Query("true.");
        var after = Layout(e);
        Assert.True(after.Count > before.Count,
            $"expected the library to link, {before.Count} -> {after.Count}");

        var (same, moved, _) = Compare(before, after);
        Assert.Equal(0, moved);
        Assert.Equal(before.Count, same);
        Assert.True(e.Query("findall(X, (small(X), label([X])), [2,3]).").Success);
    }

    /// <summary>A RECONSULT keeps every address too. The sets: what is
    /// being loaded (A) against what the layout already holds (B). B minus A
    /// keeps its ordinal; A minus B is appended; A intersect B splits —
    /// unchanged keeps its place, and a CHANGED predicate leaves its old
    /// version behind as a dead region while the new one is appended like a
    /// fresh predicate. The dead version owns nothing (the functor resolves
    /// to the new address) and is never executed; it is there so the code
    /// laid out after it does not slide up.</summary>
    [Fact]
    public void AReconsultMovesNothingAndRecordsTheDeadRegion()
    {
        var e = new PrologEngine();
        e.ConsultString("p(1).  q(X) :- p(X).  r(1).");
        e.Query("true.");
        var before = Layout(e);
        Assert.Empty(e.StaticDeadRegions);

        // p/1 grows: it moves, and its old bytes stay where they were.
        e.ReconsultString("p(1).  p(2).  p(3).  q(X) :- p(X).  r(1).");
        e.Query("true.");
        var after = Layout(e);

        // Only the predicate the reconsult CHANGED moved; everything else,
        // the prelude included, is exactly where it was.
        var (_, moved, names) = Compare(before, after);
        Assert.Equal(1, moved);
        Assert.Single(names);
        Assert.StartsWith("user$p/1 ", names[0]);
        Assert.True(e.Query("findall(X, q(X), [1,2,3]).").Success);
        Assert.True(e.Query("r(1).").Success);
        Assert.True(e.Query("member(b, [a,b,c]).").Success);

        // The hole is BOOKKEPT: where it is, how big, and whose it was —
        // what a future pass needs to reuse it. It sits at p/1's OLD
        // address, and the live p/1 is elsewhere.
        var dead = Assert.Single(e.StaticDeadRegions);
        var (pAtom, _) = Shumway.Core.FunctorTable.Lookup(dead.FunctorId);
        Assert.Equal("user$p", Shumway.Core.AtomTable.GetById(pAtom)?.Name);
        Assert.Equal(before[dead.FunctorId].Addr, dead.Address);
        Assert.True(dead.Size > 0);
        Assert.NotEqual(dead.Address, after[dead.FunctorId].Addr);
    }

    [Fact]
    public void RepeatedReconsultsKeepAddressesAndAccumulateDeadRegions()
    {
        var e = new PrologEngine();
        e.ConsultString("p(1).  q(X) :- p(X).");
        e.Query("true.");
        var first = Layout(e);

        for (int i = 2; i <= 5; i++)
        {
            var clauses = string.Join(" ",
                Enumerable.Range(1, i).Select(n => $"p({n})."));
            e.ReconsultString(clauses + " q(X) :- p(X).");
            e.Query("true.");
            // Round after round, only p/1 keeps moving to the end.
            var (_, moved, names) = Compare(first, Layout(e));
            Assert.Equal(1, moved);
            Assert.StartsWith("user$p/1 ", names[0]);
        }
        Assert.True(e.Query("findall(X, q(X), [1,2,3,4,5]).").Success);
        // One hole per superseded version of p/1 (q/1 is recompiled too, so
        // the count is at least the number of rounds).
        Assert.True(e.StaticDeadRegions.Count >= 4,
            $"expected the holes to be recorded, saw {e.StaticDeadRegions.Count}");
    }
}
