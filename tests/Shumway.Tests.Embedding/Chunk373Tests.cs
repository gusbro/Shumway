using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 373 (Phase 29, region compilation — Stage 3): the minimal region method
/// emit (root + single-clause local members, intra-region calls + deterministic
/// builtins only). End-to-end correctness is validated by the full Embedding suite
/// run with SHUMWAY_REGION=1 (the region method produces identical answers to the
/// trampoline) and by REPL cases; these tests pin the Stage-3 eligibility
/// (<see cref="IlPredicateCompiler.IsRegionEmittable"/>) on synthetic
/// regions, plus the default-off flag.
/// </summary>
public class Chunk373Tests
{
    private static CompiledPredicate Pred(int fid, int clauses, byte[] body,
        params (int callee, bool isExecute)[] calls)
    {
        var sites = calls.Select((c, i) => new CallSite(/*offset*/ 0, c.callee, c.isExecute)).ToList();
        // offsets must match the Call/Execute positions in `body`; the helpers
        // below place a single call at offset 0.
        if (sites.Count > 0) sites[0] = new CallSite(0, calls[0].callee, calls[0].isExecute);
        return new CompiledPredicate(body, fid, 1, clauses, sites, Array.Empty<int>());
    }

    // bytecode: `call <calleeFid-via-callsite>; proceed`  (Call is 9 bytes)
    private static byte[] CallThenProceed()
    {
        var b = new byte[10];
        b[0] = (byte)Opcode.Call;          // operands (addr/arity) unused by the walk
        b[9] = (byte)Opcode.Proceed;
        return b;
    }

    private static byte[] LeafProceed() => new[] { (byte)Opcode.Proceed };
    private static byte[] NeckCutProceed() => new[] { (byte)Opcode.NeckCut, (byte)Opcode.Proceed };

    private static Dictionary<int, CompiledPredicate> Map(params CompiledPredicate[] ps)
        => ps.ToDictionary(p => p.FunctorId);

    [Fact]
    public void Flag_DefaultsOff()
        => Assert.False(IlPredicateCompiler.RegionCompile);

    [Fact]
    public void SingleClauseLeafRegion_IsEmittable()
    {
        // root → leaf (intra-region call), both single-clause.
        var leaf = Pred(2, 1, LeafProceed());
        var root = Pred(1, 1, CallThenProceed(), (2, false));
        var region = IlRegionBuilder.Build(root, Map(root, leaf));
        Assert.Equal(2, region.MemberCount);
        Assert.True(IlPredicateCompiler.IsRegionEmittable(region));
    }

    [Fact]
    public void NonChainMultiClauseMember_NotEmittable()
    {
        // A multi-clause member that is NOT a plain try_me_else chain (here a
        // synthetic 2-clause body with no try_me_else) is not emittable — only
        // chain members are (Stage 4). A REAL try_me_else-chain member IS emittable;
        // that path is validated end-to-end (REPL findall cases + the full Embedding
        // suite run with SHUMWAY_REGION=1), since the flag can't be toggled per-test.
        var leaf = Pred(2, 2, LeafProceed());
        var root = Pred(1, 1, CallThenProceed(), (2, false));
        var region = IlRegionBuilder.Build(root, Map(root, leaf));
        Assert.False(IlPredicateCompiler.IsRegionEmittable(region));
    }

    [Fact]
    public void MemberWithCut_IsEmittable_Stage5()
    {
        // A member with a cut IS emittable since Stage 5: the intra-region call
        // emits SetB0(e.B), so the member's cut prunes only its own choice points
        // (chunk-367 barrier scoping). Validated end-to-end with discriminating
        // findall cases (a member commits its body to the first solution while a
        // caller CP created before the call survives) + the Embedding suite at
        // SHUMWAY_REGION=1.
        var leaf = Pred(2, 1, NeckCutProceed());
        var root = Pred(1, 1, CallThenProceed(), (2, false));
        var region = IlRegionBuilder.Build(root, Map(root, leaf));
        Assert.True(IlPredicateCompiler.IsRegionEmittable(region));
    }

    [Fact]
    public void SingleMemberRegion_NotEmittable()
    {
        // No local closure → nothing to flatten.
        var root = Pred(1, 1, LeafProceed());
        var region = IlRegionBuilder.Build(root, Map(root));
        Assert.Equal(1, region.MemberCount);
        Assert.False(IlPredicateCompiler.IsRegionEmittable(region));
    }
}
