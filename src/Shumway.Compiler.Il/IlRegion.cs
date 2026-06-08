using System;
using System.Collections.Generic;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// A REGION for flat local code-space compilation (Phase 29,
/// <c>docs/design/il-region-compilation.md</c>): a root predicate plus its
/// transitively-reachable LOCAL callees, up to an IL-size budget. A later stage
/// compiles the whole region into ONE IL method where each member is a labeled
/// block emitted ONCE and an intra-region call is a <c>br</c> (a cheap
/// intra-method jump) — the flat local code space, replacing the body-duplication
/// inliner for real programs.
///
/// <para>This type is the STAGE 1 (discovery) artifact: the member set and the
/// intra-region test. No IL is emitted here.</para>
/// </summary>
internal sealed class IlRegion
{
    /// <summary>The promoted predicate the region is rooted at; always
    /// <see cref="Members"/>[0].</summary>
    public CompiledPredicate Root { get; }

    /// <summary>Region members in discovery (breadth-first) order, root first.
    /// Each is emitted once as a labeled block.</summary>
    public IReadOnlyList<CompiledPredicate> Members { get; }

    private readonly HashSet<int> _memberFids;

    internal IlRegion(CompiledPredicate root, IReadOnlyList<CompiledPredicate> members,
        HashSet<int> memberFids)
    {
        Root = root;
        Members = members;
        _memberFids = memberFids;
    }

    /// <summary>True iff a call to <paramref name="calleeFunctorId"/> stays inside
    /// the region — the emit makes it a <c>br</c> to the member's block; otherwise
    /// it is a cross-region trampoline / builtin call.</summary>
    public bool IsIntraRegion(int calleeFunctorId) => _memberFids.Contains(calleeFunctorId);

    public int MemberCount => Members.Count;

    /// <summary>Sum of member bytecode sizes — the budget metric (a proxy for the
    /// emitted IL size).</summary>
    public int TotalBytecodeBytes
    {
        get { int s = 0; foreach (var m in Members) s += m.Bytecode.Length; return s; }
    }
}

/// <summary>Stage 1 — builds an <see cref="IlRegion"/> by walking LOCAL call edges
/// from a root, breadth-first, until a member would push the region past its
/// IL-size budget. A call to an already-included member (a cycle or a shared
/// callee) is not re-expanded — at emit time it becomes a <c>br</c> to the existing
/// block. A call to a non-member (a builtin, a not-yet-compiled predicate, a
/// dynamic predicate, or one past the budget) stays a trampoline.</summary>
internal static class IlRegionBuilder
{
    /// <summary>Default budget in bytecode bytes. Conservative: WAM bytecode lowers
    /// to several times its size in IL, so this stays well under the 64 KB method /
    /// Sigil ReturnTracer ceilings once expanded.</summary>
    public const int DefaultBudgetBytes = 3072;

    public static IlRegion Build(
        CompiledPredicate root,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        int budgetBytes = DefaultBudgetBytes,
        Func<CompiledPredicate, bool>? extraEligible = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var members = new List<CompiledPredicate> { root };
        var memberFids = new HashSet<int> { root.FunctorId };
        if (calleeMap is null)
            return new IlRegion(root, members, memberFids);

        int sizeSum = root.Bytecode.Length;
        var queue = new Queue<CompiledPredicate>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var pred = queue.Dequeue();
            foreach (var cs in pred.CallSites)
            {
                int fid = cs.CalleeFunctorId;
                if (memberFids.Contains(fid)) continue;          // cycle / shared → br to existing block
                if (!calleeMap.TryGetValue(fid, out var callee)) continue;   // builtin / external / uncompiled
                if (!IsStructurallyEligible(callee)) continue;   // dynamic / empty
                if (extraEligible is not null && !extraEligible(callee)) continue;  // caller filter
                if (sizeSum + callee.Bytecode.Length > budgetBytes) continue;       // budget → stays trampoline
                members.Add(callee);
                memberFids.Add(fid);
                sizeSum += callee.Bytecode.Length;
                queue.Enqueue(callee);
            }
        }
        return new IlRegion(root, members, memberFids);
    }

    /// <summary>A region member must be a static IL body: non-empty and NOT a
    /// dynamic predicate (whose bytecode opens with <c>enter_dynamic</c> —
    /// mutation-driven dispatch must stay Tier 0, chunk 159). Public-vs-local and
    /// full IL-eligibility are the caller's <c>extraEligible</c> filter (it needs
    /// the linker's visibility map / the compiler instance).</summary>
    private static bool IsStructurallyEligible(CompiledPredicate p)
        => p.Bytecode.Length > 0 && p.Bytecode[0] != (byte)Opcode.EnterDynamic;
}
