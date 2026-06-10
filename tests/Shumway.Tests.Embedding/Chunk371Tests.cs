using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 371 (Phase 29, region compilation — Stage 2): the cursor PLANNER
/// (<see cref="IlRegionPlanner"/>). Assigns a region's cursor space — cursor 0 is
/// the root entry, and each non-tail Call site gets the next cursor (intra-region
/// → a return continuation reached by `br` then a dispatch-switch return;
/// cross-region → a trampoline resume), walked per member in region order and per
/// call site in pc order, which is the exact order the emit will consume. Tail
/// Execute sites take no cursor. Pure analysis; no IL emitted.
/// </summary>
public class Chunk371Tests
{
    // fid, bytecode size, and call edges (callee fid, isExecute).
    private static CompiledPredicate Pred(int fid, int size, params (int callee, bool isExecute)[] calls)
    {
        var body = new byte[Math.Max(1, size)];
        body[0] = (byte)Opcode.Proceed;
        // OpcodeOffset i+1 so they sort deterministically; values are not executed.
        var sites = calls.Select((c, i) => new CallSite(i + 1, c.callee, c.isExecute)).ToList();
        return new CompiledPredicate(body, fid, 0, 1, sites, Array.Empty<int>());
    }

    private static Dictionary<int, CompiledPredicate> Map(params CompiledPredicate[] ps)
        => ps.ToDictionary(p => p.FunctorId);

    // Chunk 402 adds one MemberEntry cursor per NON-root member, appended AFTER every
    // other cursor (so the pre-402 consumption order is untouched). These tests assert
    // the call/alt cursor structure, so they filter the entry cursors out — and a
    // dedicated test below pins the MemberEntry tail itself.
    private static List<RegionCursorSite> CallSites(IlRegionPlan plan)
        => plan.Sites.Where(s => s.Kind != RegionCursorKind.MemberEntry).ToList();

    [Fact]
    public void Chain_AssignsIntraReturnCursors()
    {
        // a→b→c, all in region → each non-tail call is an intra-region return.
        var a = Pred(1, 10, (2, false)); var b = Pred(2, 10, (3, false)); var c = Pred(3, 10);
        var plan = IlRegionPlanner.Plan(IlRegionBuilder.Build(a, Map(a, b, c)));
        var sites = CallSites(plan);
        Assert.Equal(2, sites.Count);                      // a→b and b→c
        Assert.Equal(5, plan.TotalCursors);                // + root entry + 2 member entries
        Assert.All(sites, s => Assert.Equal(RegionCursorKind.IntraCallReturn, s.Kind));
        Assert.Equal(new[] { 1, 2 }, sites.Select(s => s.Cursor).ToArray());
    }

    [Fact]
    public void CrossRegionCall_IsResumeCursor()
    {
        // a→b is intra; a→x (x not in map) is cross-region → a trampoline resume.
        var a = Pred(1, 10, (2, false), (99, false)); var b = Pred(2, 10);
        var plan = IlRegionPlanner.Plan(IlRegionBuilder.Build(a, Map(a, b)));
        var sites = CallSites(plan);
        Assert.Equal(2, sites.Count);
        Assert.Equal(RegionCursorKind.IntraCallReturn, sites[0].Kind);   // →b
        Assert.Equal(RegionCursorKind.CrossCallResume, sites[1].Kind);   // →x
        Assert.Equal(99, sites[1].CalleeFid);
    }

    [Fact]
    public void TailExecute_TakesNoCursor()
    {
        // a→b is a tail call (Execute) — no cursor (intra: br; cross: tail trampoline).
        var a = Pred(1, 10, (2, true)); var b = Pred(2, 10);
        var plan = IlRegionPlanner.Plan(IlRegionBuilder.Build(a, Map(a, b)));
        Assert.Empty(CallSites(plan));
        Assert.Equal(2, plan.TotalCursors);   // root entry + b's member entry
    }

    [Fact]
    public void Cursors_FollowMemberThenPcOrder()
    {
        // a calls b then c (pc order); b calls d. Cursor order: a→b, a→c, then b→d.
        var a = Pred(1, 10, (2, false), (3, false));
        var b = Pred(2, 10, (4, false)); var c = Pred(3, 10); var d = Pred(4, 10);
        var plan = IlRegionPlanner.Plan(IlRegionBuilder.Build(a, Map(a, b, c, d)));
        // member order is BFS: a(0), b(1), c(2), d(3).
        var sites = CallSites(plan);
        Assert.Equal(3, sites.Count);
        Assert.Equal((1, 0, 2), (sites[0].Cursor, sites[0].MemberIndex, sites[0].CalleeFid)); // a→b
        Assert.Equal((2, 0, 3), (sites[1].Cursor, sites[1].MemberIndex, sites[1].CalleeFid)); // a→c
        Assert.Equal((3, 1, 4), (sites[2].Cursor, sites[2].MemberIndex, sites[2].CalleeFid)); // b→d
    }

    [Fact]
    public void MemberEntryCursors_AppendedLast_OnePerNonRootMember()
    {
        // Chunk 402: a→b→c gives 2 call cursors (1, 2), then one MemberEntry per
        // non-root member (b, c) at cursors 3 and 4 — after everything else, so the
        // emit's existing consumption order is untouched.
        var a = Pred(1, 10, (2, false)); var b = Pred(2, 10, (3, false)); var c = Pred(3, 10);
        var plan = IlRegionPlanner.Plan(IlRegionBuilder.Build(a, Map(a, b, c)));
        var entries = plan.Sites.Where(s => s.Kind == RegionCursorKind.MemberEntry).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { 3, 4 }, entries.Select(s => s.Cursor).ToArray());
        Assert.Equal(new[] { 1, 2 }, entries.Select(s => s.MemberIndex).ToArray());
        // They are the LAST cursors in the plan.
        Assert.Equal(plan.TotalCursors - 1, entries[^1].Cursor);
    }

    // A synthetic multi-clause member (clauseCount overrideable).
    private static CompiledPredicate PredN(int fid, int clauseCount, params (int callee, bool isExecute)[] calls)
    {
        var body = new byte[10];
        body[0] = (byte)Opcode.Proceed;
        var sites = calls.Select((c, i) => new CallSite(i + 1, c.callee, c.isExecute)).ToList();
        return new CompiledPredicate(body, fid, 0, clauseCount, sites, Array.Empty<int>());
    }

    [Fact]
    public void MultiClauseMember_GetsClauseAltCursors()
    {
        // a (root) calls b; b is a 3-clause member → 2 clause-alt cursors (clauses
        // 1 and 2). a→b is one intra return cursor. Root (a) single-clause.
        var a = PredN(1, 1, (2, false));
        var b = PredN(2, 3);
        var plan = IlRegionPlanner.Plan(IlRegionBuilder.Build(a, Map(a, b)));
        var alts = plan.Sites.Where(s => s.Kind == RegionCursorKind.ClauseAlt).ToList();
        Assert.Equal(2, alts.Count);
        Assert.Equal(new[] { 1, 2 }, alts.Select(s => s.ClauseIndex).ToArray());
        Assert.All(alts, s => Assert.Equal(1, s.MemberIndex));   // all belong to b (member 1)
        // plus the a→b intra return cursor.
        Assert.Single(plan.Sites.Where(s => s.Kind == RegionCursorKind.IntraCallReturn));
        Assert.Equal(5, plan.TotalCursors);   // root entry + 2 alts + 1 return + b's entry
    }

    [Fact]
    public void SingleClauseMembers_HaveNoClauseAltCursors()
    {
        var a = PredN(1, 1, (2, false)); var b = PredN(2, 1);
        var plan = IlRegionPlanner.Plan(IlRegionBuilder.Build(a, Map(a, b)));
        Assert.Empty(plan.Sites.Where(s => s.Kind == RegionCursorKind.ClauseAlt));
    }

    [Fact]
    public void SharedCallee_OneBlock_TwoReturnCursors()
    {
        // a→b, a→b again: b is one member/block, but two call sites → two distinct
        // return cursors (each call returns to its own continuation).
        var a = Pred(1, 10, (2, false), (2, false)); var b = Pred(2, 10);
        var region = IlRegionBuilder.Build(a, Map(a, b));
        Assert.Equal(2, region.MemberCount);                 // b once
        var plan = IlRegionPlanner.Plan(region);
        var sites = CallSites(plan);
        Assert.Equal(2, sites.Count);                        // two return cursors
        Assert.Equal(new[] { 1, 2 }, sites.Select(s => s.Cursor).ToArray());
        Assert.All(sites, s => Assert.Equal(2, s.CalleeFid));
    }
}
