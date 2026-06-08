using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 370 (Phase 29, region compilation — Stage 1): region DISCOVERY
/// (<see cref="IlRegionBuilder"/>). Builds the flat-local-code-space region — a
/// root predicate plus its transitively-reachable local callees, breadth-first,
/// up to an IL-size budget — over which a later stage emits one IL method with
/// `br` for intra-region calls. Pure analysis; no IL emitted. Synthetic
/// CompiledPredicates exercise the BFS, cycle handling, budget cutoff, and the
/// dynamic / non-member exclusions.
/// </summary>
public class Chunk370Tests
{
    // A synthetic predicate: functor id, bytecode size (budget metric), and its
    // call edges (callee fid + whether it's a tail Execute).
    private static CompiledPredicate Pred(int fid, int size, params (int callee, bool isExecute)[] calls)
    {
        var body = new byte[Math.Max(1, size)];          // non-empty, not enter_dynamic
        body[0] = (byte)Opcode.Proceed;
        var sites = calls.Select((c, i) => new CallSite(i, c.callee, c.isExecute)).ToList();
        return new CompiledPredicate(body, fid, 0, 1, sites, Array.Empty<int>());
    }

    private static CompiledPredicate Dynamic(int fid)
    {
        var body = new byte[] { (byte)Opcode.EnterDynamic, 0, 0, 0, 0 };
        return new CompiledPredicate(body, fid, 0, 1, Array.Empty<CallSite>(), Array.Empty<int>());
    }

    private static Dictionary<int, CompiledPredicate> Map(params CompiledPredicate[] ps)
        => ps.ToDictionary(p => p.FunctorId);

    private static int[] Fids(IlRegion r) => r.Members.Select(m => m.FunctorId).ToArray();

    [Fact]
    public void Chain_IncludesWholeChain()
    {
        var a = Pred(1, 10, (2, false)); var b = Pred(2, 10, (3, false));
        var c = Pred(3, 10, (4, false)); var d = Pred(4, 10);
        var r = IlRegionBuilder.Build(a, Map(a, b, c, d));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Fids(r));
        Assert.True(r.IsIntraRegion(3));
        Assert.False(r.IsIntraRegion(99));
    }

    [Fact]
    public void Tree_IncludesAllLeaves()
    {
        var a = Pred(1, 10, (2, false), (3, false), (4, true));   // tail call to d
        var b = Pred(2, 10); var c = Pred(3, 10); var d = Pred(4, 10);
        var r = IlRegionBuilder.Build(a, Map(a, b, c, d));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Fids(r));
    }

    [Fact]
    public void SharedCallee_IncludedOnce()
    {
        // a→b, a→c, b→d, c→d : d reachable two ways, must appear once.
        var a = Pred(1, 10, (2, false), (3, false));
        var b = Pred(2, 10, (4, false)); var c = Pred(3, 10, (4, false)); var d = Pred(4, 10);
        var r = IlRegionBuilder.Build(a, Map(a, b, c, d));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Fids(r));
        Assert.Equal(4, r.MemberCount);
    }

    [Fact]
    public void Cycle_DoesNotReExpand()
    {
        var a = Pred(1, 10, (2, false)); var b = Pred(2, 10, (1, false));  // b → a
        var r = IlRegionBuilder.Build(a, Map(a, b));
        Assert.Equal(new[] { 1, 2 }, Fids(r));   // a not re-added
    }

    [Fact]
    public void Budget_StopsAddingMembers()
    {
        var a = Pred(1, 1000, (2, false)); var b = Pred(2, 1000, (3, false)); var c = Pred(3, 1000);
        // a(1000)+b(1000)=2000 ≤ 2500; +c would be 3000 > 2500 → c stays a trampoline.
        var r = IlRegionBuilder.Build(a, Map(a, b, c), budgetBytes: 2500);
        Assert.Equal(new[] { 1, 2 }, Fids(r));
        Assert.False(r.IsIntraRegion(3));
    }

    [Fact]
    public void DynamicCallee_Excluded()
    {
        var a = Pred(1, 10, (2, false)); var b = Dynamic(2);
        var r = IlRegionBuilder.Build(a, Map(a, b));
        Assert.Equal(new[] { 1 }, Fids(r));
    }

    [Fact]
    public void NonMemberEdge_Ignored()
    {
        // callee 2 (a builtin / external) is not in the map → not a member.
        var a = Pred(1, 10, (2, false));
        var r = IlRegionBuilder.Build(a, Map(a));
        Assert.Equal(new[] { 1 }, Fids(r));
        Assert.False(r.IsIntraRegion(2));
    }

    [Fact]
    public void ExtraEligible_Filters()
    {
        var a = Pred(1, 10, (2, false), (3, false)); var b = Pred(2, 10); var c = Pred(3, 10);
        // Caller filter excludes fid 2 (e.g. a public predicate).
        var r = IlRegionBuilder.Build(a, Map(a, b, c), extraEligible: p => p.FunctorId != 2);
        Assert.Equal(new[] { 1, 3 }, Fids(r));
    }

    [Fact]
    public void NullCalleeMap_JustRoot()
    {
        var a = Pred(1, 10, (2, false));
        var r = IlRegionBuilder.Build(a, calleeMap: null);
        Assert.Equal(new[] { 1 }, Fids(r));
    }
}
