using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 381 (Phase 29, region compilation — Stage 6c): INDEXED members in a region.
/// An indexed (switch_on_term/arg) callee can now be a region member — the region
/// emits its inline index decision + per-node choice points instead of leaving it a
/// cross-region trampoline boundary. These tests pin the cursor PLANNER extension
/// (each dispatch node gets an <see cref="RegionCursorKind.IndexNode"/> cursor); the
/// end-to-end emit is validated by the full Embedding suite at SHUMWAY_REGION=1 + the
/// Blint self-lint (byte-identical, 24 regions fire incl. x3/x10/x16 indexed members)
/// + REPL discriminating findall cases across atom/integer/struct index node kinds.
/// </summary>
public class Chunk381Tests
{
    private static CompiledPredicate Pred(int fid, int clauseCount,
        params (int callee, bool isExecute)[] calls)
    {
        var body = new byte[10];
        body[0] = (byte)Opcode.Proceed;
        var sites = calls.Select((c, i) => new CallSite(i + 1, c.callee, c.isExecute)).ToList();
        return new CompiledPredicate(body, fid, 0, clauseCount, sites, Array.Empty<int>());
    }

    private static Dictionary<int, CompiledPredicate> Map(params CompiledPredicate[] ps)
        => ps.ToDictionary(p => p.FunctorId);

    [Fact]
    public void IndexedMember_GetsOneIndexNodeCursorPerNode()
    {
        // root (single-clause) calls m; m is an indexed member with 3 dispatch nodes.
        var root = Pred(1, 1, (2, false));
        var m = Pred(2, 3);
        var region = IlRegionBuilder.Build(root, Map(root, m));
        // The compiler passes node-count = info.Nodes.Count for an indexed member;
        // here we stand in for that with a direct callback (m → 3 nodes).
        var plan = IlRegionPlanner.Plan(region, p => p.FunctorId == 2 ? 3 : 0);

        var nodes = plan.Sites.Where(s => s.Kind == RegionCursorKind.IndexNode).ToList();
        Assert.Equal(3, nodes.Count);
        Assert.Equal(new[] { 0, 1, 2 }, nodes.Select(s => s.ClauseIndex).ToArray());  // node index
        Assert.All(nodes, s => Assert.Equal(1, s.MemberIndex));                        // all belong to m
        // No clause-alt cursors for an indexed member (it uses IndexNode instead).
        Assert.Empty(plan.Sites.Where(s => s.Kind == RegionCursorKind.ClauseAlt));
        // root entry + 3 node cursors + the root's a→m intra-return cursor + m's
        // chunk-402 MemberEntry cursor.
        Assert.Single(plan.Sites.Where(s => s.Kind == RegionCursorKind.IntraCallReturn));
        Assert.Equal(6, plan.TotalCursors);
    }

    [Fact]
    public void NonIndexedMultiClauseMember_StillUsesClauseAltCursors()
    {
        // When the node-count callback returns 0 (a try_me_else chain, not indexed),
        // the member keeps its clause-alt cursors — the Stage-4 behaviour.
        var root = Pred(1, 1, (2, false));
        var m = Pred(2, 3);
        var region = IlRegionBuilder.Build(root, Map(root, m));
        var plan = IlRegionPlanner.Plan(region, _ => 0);   // nothing is indexed

        Assert.Empty(plan.Sites.Where(s => s.Kind == RegionCursorKind.IndexNode));
        Assert.Equal(2, plan.Sites.Count(s => s.Kind == RegionCursorKind.ClauseAlt));  // clauses 1,2
    }

    [Fact]
    public void NoCallback_IsPreStage6cBehaviour()
    {
        // Plan() with no node-count callback never produces IndexNode cursors.
        var root = Pred(1, 1, (2, false));
        var m = Pred(2, 2);
        var plan = IlRegionPlanner.Plan(IlRegionBuilder.Build(root, Map(root, m)));
        Assert.Empty(plan.Sites.Where(s => s.Kind == RegionCursorKind.IndexNode));
        Assert.Single(plan.Sites.Where(s => s.Kind == RegionCursorKind.ClauseAlt));    // 2-clause m
    }
}
