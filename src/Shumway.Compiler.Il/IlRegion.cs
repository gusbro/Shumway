using System;
using System.Collections.Generic;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// A REGION for flat local code-space compilation
/// (<c>docs/design/il-region-compilation.md</c>): a root predicate plus its
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
        get { int s = 0; foreach (var m in Members) s += m.BytecodeUnfused.Length; return s; }
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
    /// <summary>Fallback budget in bytecode bytes when nothing else is set.
    /// Conservative: WAM bytecode lowers to several times its size in IL, so this
    /// stays well under the 64 KB method / Sigil ReturnTracer ceilings once
    /// expanded.</summary>
    public const int FallbackBudgetBytes = 3072;

    /// <summary>The active budget — the "aggressiveness" knob. Configurable via the
    /// <c>SHUMWAY_REGION_BUDGET</c> env var (bytecode bytes); a higher value pulls
    /// more locals into a region (bigger method, more calls flattened to <c>br</c>),
    /// a lower one keeps regions small. A CLI / compiler option can map an
    /// aggressiveness level onto this later. The budget is the prune point: a member
    /// that would push the region past it stays a trampoline boundary — i.e. a
    /// region that would otherwise overflow is pruned, and the un-pulled callees are
    /// treated as ordinary (visible) predicates reached by the trampoline. (A real
    /// post-emit IL-size guard — fall back if the EMITTED method nears 64 KB — is a
    /// later stage; this bytecode proxy is the first-line bound.)</summary>
    public static readonly int DefaultBudgetBytes =
        int.TryParse(Environment.GetEnvironmentVariable("SHUMWAY_REGION_BUDGET"), out var b) && b > 0
            ? b : FallbackBudgetBytes;

    public static IlRegion Build(
        CompiledPredicate root,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        int budgetBytes = -1,
        Func<CompiledPredicate, bool>? extraEligible = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (budgetBytes < 0) budgetBytes = DefaultBudgetBytes;
        var members = new List<CompiledPredicate> { root };
        var memberFids = new HashSet<int> { root.FunctorId };
        if (calleeMap is null)
            return new IlRegion(root, members, memberFids);

        int sizeSum = root.BytecodeUnfused.Length;
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
                if (sizeSum + callee.BytecodeUnfused.Length > budgetBytes) continue;       // budget → stays trampoline
                members.Add(callee);
                memberFids.Add(fid);
                sizeSum += callee.BytecodeUnfused.Length;
                queue.Enqueue(callee);
            }
        }
        return new IlRegion(root, members, memberFids);
    }

    /// <summary>A region member must be a static IL body: non-empty and NOT a
    /// dynamic predicate (whose bytecode opens with <c>enter_dynamic</c> —
    /// mutation-driven dispatch must stay Tier 0). Public-vs-local and
    /// full IL-eligibility are the caller's <c>extraEligible</c> filter (it needs
    /// the linker's visibility map / the compiler instance).</summary>
    private static bool IsStructurallyEligible(CompiledPredicate p)
        => p.BytecodeUnfused.Length > 0 && p.BytecodeUnfused[0] != (byte)Opcode.EnterDynamic;
}

/// <summary>What a region cursor re-enters at (Stage 2). The region method's
/// dispatch switch routes a cursor to its label; a cursor is set either from the
/// method's <c>arg1</c> (initial call / backtrack from the loop) or from the
/// decoded <c>Cp</c> on an intra-region return.</summary>
internal enum RegionCursorKind
{
    /// <summary>The continuation after an INTRA-region call (the callee's block
    /// runs via a <c>br</c>; its proceed returns here through the dispatch switch).</summary>
    IntraCallReturn,
    /// <summary>The forward-resume point after a CROSS-region call (the
    /// trampoline returns to the loop, which re-enters here).</summary>
    CrossCallResume,
    /// <summary>A non-first clause of a MULTI-clause member (Stage 4). The member's
    /// clause dispatch pushes a choice point carrying this cursor; a backtrack
    /// re-enters the region method here to try the next clause.</summary>
    ClauseAlt,
    /// <summary>A chain node of an INDEXED member (Stage 6c). The member's index
    /// decision branches forward to a node's label; a bucket-chain backtrack pushes a
    /// choice point carrying the NEXT node's cursor, re-entering the region method at
    /// that node. One cursor per <see cref="IlIndexedDispatchInfo"/> node; the node
    /// index rides in <see cref="RegionCursorSite.ClauseIndex"/>.</summary>
    IndexNode,
    /// <summary>A non-root member's ENTRY. Lets an EXTERNAL by-fid call
    /// dispatch INTO the region at that member — the load path maps a stripped
    /// member's functor to <c>EncodeResumeMarker(rootFid, thisCursor)</c> in
    /// <c>CurrentFunctorAddresses</c>, and the dispatch loop's marker route invokes
    /// the region delegate at this cursor. The cursor's label IS the member's entry
    /// label (no separate block); assigned AFTER all other cursors so the existing
    /// site-consumption order is untouched.</summary>
    MemberEntry,
    /// <summary>The post-site resume point of a backtrackable builtin
    /// (<c>between/3</c>, <c>retract/1</c>, …) or a runtime meta-call
    /// (<c>call/N</c>, <c>'$call'/2</c>) inside a member's body. A backtrackable
    /// builtin's choice-point closure calls <c>ResumeAtReturnPc</c> with
    /// <c>EncodeResumeMarker(rootFid, thisCursor)</c> (the REGION's fid+cursor
    /// instead of a standalone predicate's); a non-tail meta-call threads its
    /// dispatch with <c>Cp</c> set to the same marker. Either way the dispatch loop re-enters the region method here.
    /// Keyed by (member, pc) like the call cursors.</summary>
    BuiltinResume,
}

/// <summary>One assigned cursor in a region's cursor space. Cursor 0 is the root's
/// entry (implicit); these carry 1..N. For a <see cref="RegionCursorKind.ClauseAlt"/>
/// the <see cref="ClauseIndex"/> is the clause number (1..ClauseCount-1) and
/// <see cref="Pc"/>/<see cref="CalleeFid"/> are unused (-1).</summary>
internal readonly record struct RegionCursorSite(
    int Cursor, RegionCursorKind Kind, int MemberIndex, int Pc, int CalleeFid,
    int ClauseIndex = -1);

/// <summary>The cursor plan for a region (Stage 2 artifact): the assignment of the
/// region's forward-resume / intra-return cursor space, in the exact order the emit
/// (a later stage) will consume it — per member (region order), per non-tail call
/// site (pc order). The plan IS the spec the emit follows, so the dispatch jump
/// table and the emit's cursor consumption agree by construction.
///
/// <para>STAGE 2 scope: single-clause members' non-tail <c>Call</c> sites only.
/// Multi-clause clause-alternative cursors and backtrackable-builtin resume cursors
/// are added when those member shapes are handled (Stages 4+).</para></summary>
internal sealed class IlRegionPlan
{
    public IlRegion Region { get; }
    /// <summary>Cursors 1..N (cursor 0 = the root entry, implicit).</summary>
    public IReadOnlyList<RegionCursorSite> Sites { get; }

    internal IlRegionPlan(IlRegion region, IReadOnlyList<RegionCursorSite> sites)
    { Region = region; Sites = sites; }

    /// <summary>Size of the region's cursor space (jump-table width) — N + 1 for
    /// the root entry at cursor 0.</summary>
    public int TotalCursors => Sites.Count + 1;
}

/// <summary>Stage 2 — assigns a region's cursor space. Walks each member in region
/// order, and within a member its non-tail <c>Call</c> sites in pc order, giving
/// each the next cursor (intra-region → a return continuation; cross-region → a
/// trampoline resume). Tail <c>Execute</c> sites take no cursor (intra-region is a
/// <c>br</c>; cross-region is a tail trampoline) — so the region model needs no
/// un-tailing.</summary>
internal static class IlRegionPlanner
{
    /// <param name="indexNodeCount">For an INDEXED member, the number of dispatch
    /// nodes (<see cref="IlIndexedDispatchInfo"/>.Nodes.Count) — each gets an
    /// <see cref="RegionCursorKind.IndexNode"/> cursor instead of the try_me_else
    /// chain's clause-alt cursors. Returns 0 for a non-indexed member. Null (the
    /// default) means no member is indexed — the pre-Stage-6c behaviour.</param>
    /// <param name="builtinResumePcs">Per member, the (sorted) byte
    /// offsets of <c>CallBuiltin</c> sites that need a
    /// <see cref="RegionCursorKind.BuiltinResume"/> cursor: backtrackable builtins
    /// and runtime meta-calls. Null means none (the pre-424 behaviour). The
    /// classification lives in the compiler (it needs the builtins registry); the
    /// planner only assigns cursors.</param>
    public static IlRegionPlan Plan(
        IlRegion region, Func<CompiledPredicate, int>? indexNodeCount = null,
        Func<CompiledPredicate, IReadOnlyList<int>>? builtinResumePcs = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        var sites = new List<RegionCursorSite>();
        int cursor = 1;   // cursor 0 = root entry
        for (int mi = 0; mi < region.Members.Count; mi++)
        {
            var member = region.Members[mi];
            int nodes = indexNodeCount?.Invoke(member) ?? 0;
            if (nodes > 0)
            {
                // Stage 6c: an indexed member — one IndexNode cursor per dispatch
                // node (a bucket-chain backtrack pushes the next node's cursor). The
                // node index rides in the ClauseIndex slot. Assigned before the
                // member's call cursors, in node order.
                for (int n = 0; n < nodes; n++)
                    sites.Add(new RegionCursorSite(cursor++, RegionCursorKind.IndexNode, mi, -1, -1, n));
            }
            else
            // Stage 4: a multi-clause member's non-first clauses (1..N-1) each get
            // a clause-alternative cursor — the clause dispatch pushes a choice
            // point carrying this cursor, and a backtrack re-enters the region method
            // here to try the next clause. (Clause 0 is reached by the forward br /
            // cursor 0 for the root, so it takes no separate cursor.) Assigned before
            // the member's call cursors, in clause order.
            for (int c = 1; c < member.ClauseCount; c++)
                sites.Add(new RegionCursorSite(cursor++, RegionCursorKind.ClauseAlt, mi, -1, -1, c));
            // Body-site cursors in pc order — the emit walks bytecode forward, so
            // cursor numbers must follow the byte offsets. Two site kinds merge here:
            // non-tail Call sites (intra return / cross resume) and
            // builtin-resume sites (backtrackable / meta CallBuiltin).
            var ordered = new List<CallSite>(member.CallSites);
            ordered.Sort((x, y) => x.OpcodeOffset.CompareTo(y.OpcodeOffset));
            IReadOnlyList<int> bpcs = builtinResumePcs?.Invoke(member)
                ?? Array.Empty<int>();
            int bi = 0;
            foreach (var cs in ordered)
            {
                while (bi < bpcs.Count && bpcs[bi] < cs.OpcodeOffset)
                    sites.Add(new RegionCursorSite(cursor++,
                        RegionCursorKind.BuiltinResume, mi, bpcs[bi++], -1));
                if (cs.IsExecute) continue;   // tail call: br (intra) or tail trampoline (cross)
                var kind = region.IsIntraRegion(cs.CalleeFunctorId)
                    ? RegionCursorKind.IntraCallReturn
                    : RegionCursorKind.CrossCallResume;
                sites.Add(new RegionCursorSite(cursor++, kind, mi, cs.OpcodeOffset, cs.CalleeFunctorId));
            }
            while (bi < bpcs.Count)
                sites.Add(new RegionCursorSite(cursor++,
                    RegionCursorKind.BuiltinResume, mi, bpcs[bi++], -1));
        }
        // One MemberEntry cursor per NON-root member (the root is cursor 0),
        // assigned after every other cursor so the emit's existing consumption order is
        // untouched. The cursor's switch slot points at the member's entry label, making
        // the member externally callable by fid via a resume-marker alias — the
        // prerequisite for stripping an absorbed-only member's WAM.
        for (int mi = 1; mi < region.Members.Count; mi++)
            sites.Add(new RegionCursorSite(cursor++, RegionCursorKind.MemberEntry, mi, -1, -1));
        return new IlRegionPlan(region, sites);
    }
}
