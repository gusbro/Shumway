using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

public sealed partial class IlPredicateCompiler
{
    // ========================================================================
    // IL REGION COMPILATION (flat local code space).
    // docs/design/il-region-compilation.md. A region (root + reachable local
    // callees, IlRegionBuilder) compiles to ONE IL method: each member a labeled
    // block emitted once, an intra-region call a `br`. Stage 3 = single-clause
    // members, intra-region calls + deterministic builtins only (no backtracking,
    // no cut, no cross-region user calls — those are Stages 4-6).
    // ========================================================================

    /// <summary>Region compilation toggle. DEFAULT ON since the
    /// validation showed regions fix the if-then-else lowering tax
    /// (the <c>$disj</c> helper costs two trampoline round-trips per iteration
    /// and breaks self-loop detection — regions make both intra-method
    /// branches: ~2× on ITE-recursion shapes, qsort −22%, boyer −15%, corpus
    /// output-identical, one-shot neutral under default promotion). Set
    /// <c>SHUMWAY_REGION=0</c> to disable. The PERSISTED bundle path ignores
    /// this default — BundleWriter region-compiles a bundle only together
    /// with the dead-region prune (all-as-roots region bundles measured 2.3×
    /// bigger). Settable (CLI dumps, tests); read once per
    /// <c>Compile</c>.</summary>
    public static bool RegionCompile { get; set; } =
        System.Environment.GetEnvironmentVariable("SHUMWAY_REGION") != "0";

    /// <summary>The persisted build's per-thread override of <see cref="RegionCompile"/>.
    /// ThreadStatic like <see cref="RegionMemberScopeFids"/> and for the same reason:
    /// a bundle build must not change what a concurrently-promoting engine compiles —
    /// the process default above stays untouched and other threads keep reading it.
    /// Null = no override (the runtime promotion path).</summary>
    [System.ThreadStatic]
    public static bool? RegionCompileOverride;

    internal static bool EffectiveRegionCompile => RegionCompileOverride ?? RegionCompile;

    /// <summary>ADR-031 — delayed choice point for the neck-cut guard clause.
    /// A non-last chain clause of the shape <c>Head :- InlineGuard, !, Body.</c>
    /// (guard = non-binding, non-allocating inline ops — currently the
    /// <c>a_int_cmp</c> integer-comparison fast lane) is emitted WITHOUT its
    /// entry <c>PushIlChoicePoint</c>: guard failure is a direct IL branch to
    /// the next clause's label (the guard mutated no engine state, so there is
    /// nothing to restore), and the commit needs no <c>engine.Cut</c> teardown
    /// (nothing was pushed). The one caveat — attribute wakeups pending at the
    /// cut need a choice point to fail into — is handled by materialising the
    /// skipped CP LAZILY at the commit when <see cref="Activation.HasPendingWakeups"/>
    /// (state-identical to an entry push because the guard changed nothing).
    /// Set <c>SHUMWAY_CPFREE_GUARD=0</c> to disable (A/B lever).</summary>
    public static bool CpFreeGuardCommit { get; set; } =
        System.Environment.GetEnvironmentVariable("SHUMWAY_CPFREE_GUARD") != "0";

    /// <summary>Stage 9c (cost-based root selection): functor ids FORCED to be region
    /// ROOTS — excluded from absorption into any OTHER region. Promoting a shared member
    /// to its own root trades N duplicated copies of its sub-region for one copy + N
    /// cross-region trampolines, cutting the all-as-roots inter-root duplication. Set by
    /// the bundle build (save/restore) before a pruned-IL build; null = none.
    /// ThreadStatic on purpose (it carries the BUNDLE module's fids): a concurrent
    /// promotion on another thread reading these would plan regions with an unrelated
    /// module's roots. The root selector's probe loop mutates this between probes,
    /// which is why it is an ambient rather than a parameter — same thread, so safe.</summary>
    [System.ThreadStatic]
    public static ISet<int>? RegionForcedRootFids;

    /// <summary>When non-null, region
    /// membership is restricted to these functor ids (the bundle entry's own
    /// predicates). The persisted build sets it (save/restore) so an entry
    /// compiled against the whole bundle's predicate map never absorbs a
    /// cross-module callee; null = no scope (the runtime promotion path).
    /// ThreadStatic on purpose: a bundle build on the caller thread must not
    /// scope background promotions running on the IlCompileWorker.</summary>
    [System.ThreadStatic]
    public static ISet<int>? RegionMemberScopeFids;

    /// <summary>The labels + cursor map threaded into <see cref="EmitClauseBody"/>
    /// while emitting a region member's block.</summary>
    private sealed class RegionEmitContext
    {
        public IlRegion Region = null!;
        public int RegionFid;
        public Sigil.Label RetLabel = null!;
        public Sigil.Label DispatchLabel = null!;
        public Sigil.Label FailLabel = null!;
        public IReadOnlyDictionary<int, Sigil.Label> MemberEntry = null!;
        public Sigil.Label[] CursorLabels = null!;
        public Dictionary<(int Member, int Pc), int> CursorBySite = null!;
        // (member index, clause index 1..N-1) → the clause-alternative cursor.
        public Dictionary<(int Member, int Clause), int> ClauseAltCursor = null!;
        // (member index, node index 0..K-1) → the IndexNode cursor (Stage 6c).
        public Dictionary<(int Member, int Node), int> IndexNodeCursor = null!;
        public int CurrentMemberIndex;
    }

    /// <summary>Stage-3 eligibility: a region this minimal emit can handle — at
    /// least two members, every member single-clause, and every member's body
    /// containing only intra-region <c>Call</c>/<c>Execute</c> and deterministic
    /// builtins (no cut, no backtrackable / meta builtin, no cross-region user call,
    /// no multi-clause dispatch). The unhandled shapes (backtracking, cut,
    /// cross-region) come in Stages 4-6.</summary>
    private static string FidName(int fid)
    {
        if (fid < 0) return "?";
        try { var (a, ar) = Shumway.Core.FunctorTable.Lookup(fid);
              return Shumway.Core.AtomTable.GetById(a)?.Name ?? "?"; }
        catch { return "?"; }
    }

    internal static bool IsRegionEmittable(
        IlRegion region, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
        => IsRegionEmittable(region, calleeMap, out _);

    /// <summary>As <see cref="IsRegionEmittable(IlRegion, IReadOnlyDictionary{int, CompiledPredicate})"/>,
    /// but on rejection sets <paramref name="reason"/> to a human-readable cause (which
    /// member, which opcode) — surfaced under <c>SHUMWAY_IL_SHAPE=1</c> to explain why a
    /// predicate with a local closure did NOT become a region (the coverage gaps).</summary>
    internal static bool IsRegionEmittable(
        IlRegion region, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out string? reason)
    {
        reason = null;
        if (region.MemberCount < 2) { reason = "members<2 (no local closure)"; return false; }
        foreach (var m in region.Members)
            if (!RegionMemberOk(m, calleeMap, out var r))
            { reason = $"member {FidName(m.FunctorId)}/{m.Arity}: {r}"; return false; }
        return true;
    }

    /// <summary>The per-member validation shared by <see cref="IsRegionEmittable(IlRegion,
    /// IReadOnlyDictionary{int, CompiledPredicate}, out string)"/> (which members of a
    /// formed region are all OK) and <see cref="IsRegionMemberEligible"/> (whether a
    /// callee may be PULLED IN as a member). A member must be a shape the region emit
    /// handles — single-clause, try_me_else chain, or indexed switch_on_term/arg — and
    /// its emitted body (the full bytecode, or per-clause ranges for an indexed member,
    /// since the resolve replaces the dispatch cascade) must use only opcodes the region
    /// handles (cut OK; Call/Execute with metadata; no backtrackable / meta builtin —
    /// those need a resume cursor the planner doesn't yet allocate). Sharing this between
    /// the two callers is what makes path-1 work: a callee whose body has a backtrackable
    /// builtin is now refused MEMBERSHIP (stays a cross-region trampoline) instead of
    /// being pulled in and then rejecting the whole region.</summary>
    private static bool RegionMemberOk(
        CompiledPredicate m, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out string? reason)
    {
        reason = null;
        if (m.ClauseCount > 1 && !TryDescribeTryMeElseChain(m, calleeMap, out _))
        {
            if (!TryDescribeIndexed(m, calleeMap, out var info))
            { reason = "multi-clause, neither chain nor indexed"; return false; }
            foreach (var (start, end) in info!.Clauses)
                if (!RegionBodyOpcodesOk(m.BytecodeUnfused, start, end, m.CallSites, out var r))
                { reason = $"(indexed body) {r}"; return false; }
            return true;
        }
        if (!RegionBodyOpcodesOk(m.BytecodeUnfused, 0, m.BytecodeUnfused.Length, m.CallSites, out var r2))
        { reason = r2; return false; }
        return true;
    }

    /// <summary>Validates that a region member's body code (<paramref name="start"/>..
    /// <paramref name="end"/>) uses only opcodes the region emit handles: cut is
    /// allowed (barrier scoping); a <c>Call</c>/<c>Execute</c> must have
    /// call-site metadata (intra-region <c>br</c> / cross-region trampoline, Stage 6);
    /// a <c>CallBuiltin</c> must be deterministic (a backtrackable / meta builtin
    /// needs a resume cursor the region planner doesn't yet allocate).</summary>
    private static bool RegionBodyOpcodesOk(
        byte[] code, int start, int end, IReadOnlyList<CallSite> callSites)
        => RegionBodyOpcodesOk(code, start, end, callSites, out _);

    private static bool RegionBodyOpcodesOk(
        byte[] code, int start, int end, IReadOnlyList<CallSite> callSites, out string? reason)
    {
        reason = null;
        int pc = start;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            switch (op)
            {
                case Opcode.Call:
                case Opcode.Execute:
                    if (FindCallSiteFunctorId(callSites, pc) < 0)
                    { reason = $"{op} @{pc} has no call-site metadata"; return false; }
                    break;
                // ADR-025 (ITE in regions) — an inline ITE/disjunction body is
                // now region-emittable: the planner gives its try_me_else pc
                // an ELSE re-entry cursor (via CollectBuiltinResumePcs) and
                // the emit pushes the region delegate + that cursor; TrustMe
                // marks the label; Jump is a local forward branch. A
                // dispatch-chain try_me_else (real arity >= 0) stays accepted
                // as before — it is the member's own clause dispatch.
                // backtrackable / meta CallBuiltin sites are now
                // region-emittable — the planner allocates each a
                // BuiltinResume cursor and the emit threads the /
                // markers with the REGION's fid+cursor.
            }
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
            if (size <= 0) { reason = $"undecodable opcode {op} @{pc}"; return false; }
            pc += size;
        }
        return true;
    }

    /// <summary>the (sorted) byte offsets of <paramref name="m"/>'s
    /// <c>CallBuiltin</c> sites that need a <see cref="RegionCursorKind.BuiltinResume"/>
    /// cursor: backtrackable builtins (the <c>BuiltinReturnPc</c> resume) and
    /// runtime meta-calls (<c>call/N</c>, <c>'$call'/2</c> — threading).
    /// Walks the same ranges <see cref="RegionMemberOk"/> validates: clause ranges for
    /// an indexed member (its dispatch tables aren't linearly decodable), the whole
    /// body otherwise.</summary>
    private static IReadOnlyList<int> RegionBuiltinResumePcs(
        CompiledPredicate m, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        var pcs = new List<int>();
        if (m.ClauseCount > 1 && !TryDescribeTryMeElseChain(m, calleeMap, out _)
            && TryDescribeIndexed(m, calleeMap, out var info))
        {
            foreach (var (start, end) in info!.Clauses)
                CollectBuiltinResumePcs(m.BytecodeUnfused, start, end, pcs);
        }
        else
        {
            CollectBuiltinResumePcs(m.BytecodeUnfused, 0, m.BytecodeUnfused.Length, pcs);
        }
        pcs.Sort();
        return pcs;
    }

    private static void CollectBuiltinResumePcs(byte[] code, int start, int end, List<int> pcs)
    {
        int pc = start;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.CallBuiltin)
            {
                var e = Shumway.Builtins.BuiltinsRegistry.GetById(
                    BytecodeIO.ReadInt32(code, pc + 1));
                // precomputed flag instead of the name switch.
                if (e.IsCall || e.IsDollarCall || e.IsBacktrackable)
                    pcs.Add(pc);
            }
            // ADR-025 (ITE in regions) — an inline ITE/disjunction's body
            // try_me_else (the arity sentinel) needs an ELSE re-entry cursor:
            // the CP carries the REGION delegate + this cursor, and a failed
            // condition re-enters the region method at the TrustMe-marked
            // label. Rides the BuiltinResume site kind — the planner merges
            // pcs in order and the emit resolves by (member, pc), so no new
            // plumbing; nothing else consults the site's Kind for body sites.
            else if (op == Opcode.TryMeElse
                     && BytecodeIO.ReadInt32(code, pc + 5) == OpcodeTable.InlineIteCpArity)
            {
                pcs.Add(pc);
            }
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
            if (size <= 0) return;
            pc += size;
        }
    }

    /// <summary>Region-membership filter (Stage 6b/6c/6d). A callee is pulled into a
    /// region only if it is itself IL-compilable AND <see cref="RegionMemberOk"/> — a
    /// shape the region emit handles (single-clause / try_me_else chain / indexed) whose
    /// emitted body uses only region-handled opcodes. Stage 6d (path 1): a callee whose
    /// body contains a backtrackable / meta builtin (<c>retract</c>, <c>atom_concat</c>,
    /// <c>call</c>, ...) is NOT pulled in — it stays a cross-region trampoline boundary
    /// (Stage 6a) and the rest of the region still forms, instead of one such callee
    /// poisoning the whole region (which is what blocked ~60 Blint local-closure
    /// predicates). The resume-cursor threading that would let such a builtin live INSIDE
    /// a member is a later step.</summary>
    private bool IsRegionMemberEligible(CompiledPredicate p,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        // When the persisted build compiles
        // an entry against the WHOLE bundle's predicate map, region membership
        // stays scoped to the entry's OWN predicates: absorbing a cross-module
        // callee would duplicate its body into this entry's region method
        // (semantically sound — static predicates are immutable — but it
        // changes region shapes and bloats the entry for no dispatch win; the
        // member's standalone IL lives in its own entry).
        if (RegionMemberScopeFids?.Contains(p.FunctorId) == false) return false;
        // Stage 9c: forced root. Checked LIVE (not cached) — the bundle build
        // mutates the RegionForcedRootFids static between the root-selector
        // probe phase and the compile phase.
        if (RegionForcedRootFids?.Contains(p.FunctorId) == true) return false;
        // the rest (CanCompileCore + RegionMemberOk) is a pure
        // function of (predicate, calleeMap), recomputed thousands of times by
        // the RegionRootSelector fixpoint (once per call-site edge per region
        // build per iteration). Cache per fid for the current calleeMap
        // instance (fid → predicate is unique within one map; a new map —
        // e.g. the next query's promotion view — resets the cache).
        if (!ReferenceEquals(_regionMemberPureCacheMap, calleeMap)
            || _regionMemberPureCache is null)
        {
            _regionMemberPureCache = new Dictionary<int, bool>();
            _regionMemberPureCacheMap = calleeMap;
        }
        if (_regionMemberPureCache.TryGetValue(p.FunctorId, out bool ok)) return ok;
        ok = CanCompileCore(p, calleeMap)
             && RegionMemberOk(p, calleeMap, out _);
        _regionMemberPureCache[p.FunctorId] = ok;
        return ok;
    }

    /// <summary>see <see cref="IsRegionMemberEligible"/>.</summary>
    private Dictionary<int, bool>? _regionMemberPureCache;
    private IReadOnlyDictionary<int, CompiledPredicate>? _regionMemberPureCacheMap;

    /// <summary>The set of functor ids a region rooted at <paramref name="root"/> would
    /// ABSORB as <c>br</c>-members when emitted (Stage 9 input) — the predicates whose
    /// standalone form this root makes intra-region, INCLUDING the root itself. Matches
    /// exactly what <see cref="Compile"/> emits: it builds the region with the runtime
    /// membership filter, and returns just <c>{root}</c> when the region is not emittable
    /// (root stays a per-predicate method, so every callee trampolines out). Independent
    /// of <see cref="RegionCompile"/> — it answers "if region-compiled, what is absorbed",
    /// which the dead-region reachability (<see cref="RegionReachability"/>) consumes when
    /// the bundle is built in region mode.</summary>
    public IReadOnlyCollection<int> RegionMemberFids(
        CompiledPredicate root, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
        => RegionMemberFids(root, calleeMap, extraExcluded: null);

    /// <param name="extraExcluded">Stage 9c: additional functor ids excluded from
    /// absorption (treated as forced roots) for THIS computation, on top of
    /// <see cref="RegionForcedRootFids"/> — lets the root selector probe regions for a
    /// candidate promotion set without mutating the global static.</param>
    public IReadOnlyCollection<int> RegionMemberFids(
        CompiledPredicate root, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        ISet<int>? extraExcluded)
    {
        if (calleeMap is null) return new[] { root.FunctorId };
        var region = IlRegionBuilder.Build(root, calleeMap,
            extraEligible: p => IsRegionMemberEligible(p, calleeMap)
                && extraExcluded?.Contains(p.FunctorId) != true);
        if (!IsRegionEmittable(region, calleeMap)) return new[] { root.FunctorId };
        var s = new HashSet<int>(region.Members.Count);
        foreach (var m in region.Members) s.Add(m.FunctorId);
        return s;
    }

    /// <summary>Emit a whole region as one IL method (Stage 3). Layout: a `cur`
    /// local seeded from <c>arg1</c>; a `dispatch` jump table over the plan's cursor
    /// space (0 = root entry); each member as a labeled block; a shared `ret` handler
    /// that decodes <c>Cp</c> (<see cref="Activation.RegionReturnCursor"/>) — intra-region
    /// → <c>br dispatch</c> at the return cursor, cross-region → <c>return true</c>
    /// (the loop runs <c>Cp</c>).</summary>
    private PredicateDelegate CompileRegion(IlRegion region, IlRegionPlan plan,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        // The holder pattern gives the region method a reference to its OWN delegate
        // (for PushIlChoicePoint when a multi-clause member's clause dispatch pushes
        // a choice point that re-enters this method on backtrack).
        lock (IndexedDelegateHolder.RegistrationLock)
        {
            int holderKey = _nextHolderKey;
            var emitSelf = SelfFromHolder(holderKey);
            var del = CompileRegionUnlocked(region, plan, calleeMap, emitSelf);
            IndexedDelegateHolder.Register(holderKey, del);
            _nextHolderKey = holderKey + 1;
            return del;
        }
    }

    private PredicateDelegate CompileRegionUnlocked(IlRegion region, IlRegionPlan plan,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, SelfDelegateEmitter emitSelf)
    {
        var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
            $"ShumwayIlRegion_{region.Root.FunctorId}_{region.Root.Arity}",
            doVerify: DoVerify || DebugMode);
        EmitRegionInto(emit, emitSelf, region, plan, calleeMap,
            typeof(Func<Activation, int, bool>));   // runtime path: SelfFromHolder → Func
        int regionFid = region.Root.FunctorId;
        return FinishEmit(emit,
            $"region root={regionFid} {FidName(regionFid)} members=["
            + string.Join(",", region.Members.Select(m => $"{m.FunctorId}:{FidName(m.FunctorId)}/{m.Arity}x{m.ClauseCount}"))
            + "]");
    }

    /// <summary>Emit a region's body into <paramref name="emit"/> — shared by the runtime
    /// DynamicMethod path (<see cref="CompileRegionUnlocked"/>) and the persisted-IL
    /// TypeBuilder path (<see cref="EmitPersistedMethod"/>, prereq-i for the bundle
    /// dead-region prune). The two differ only in how the method is created and how
    /// <paramref name="emitSelf"/> resolves the self-delegate (holder vs delegates-array
    /// field); the region layout — dispatch switch, member blocks, ret / fail handlers —
    /// and its functor-id / resume-marker uses (all through the patchable
    /// helpers) are identical, so persisted region methods patch correctly cross-process.</summary>
    private void EmitRegionInto(
        Sigil.Emit<PredicateDelegate> emit, SelfDelegateEmitter emitSelf,
        IlRegion region, IlRegionPlan plan,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        System.Type selfDelType)
    {
        int regionFid = region.Root.FunctorId;
        _emitOwnerFid = regionFid;

        // Stage 11 (IL-size / CSE): every multi-clause / indexed member's
        // PushIlChoicePoint reloads the SAME region self-delegate. Hoist that load to ONE
        // local here, ahead of the dispatch switch (which dominates every member / cursor
        // label, so the store reaches every push site), and hand members a loader that
        // just reads it. The break-even depends on which self-loader is in play, and the
        // two paths that share this emit use different ones:
        //   • Persisted path — SelfFromArrayField, 3 cheap IL ops (ldsfld/ldc.i4/ldelem),
        //     no runtime cost beyond the array index. Pure IL-SIZE play: hoist costs 4 ops
        //     once (load+store) and saves 2/push, so it only shrinks at ≥3 (saving 2·P−4).
        //   • Runtime-promotion path — SelfFromHolder, 2 IL ops but each executes a
        //     ConcurrentDictionary lookup at RUNTIME on the CP-push (backtracking) path.
        //     Replacing that per-push dict probe with a hoisted local load is a runtime
        //     win at ≥2 (worth the +1 IL op the size math costs at P=2) — the same call
        //     the inline-fact hoist already makes for its holder-only pushes.
        // So gate by the loader kind: selfDelType is PredicateDelegate on the persisted
        // (array-field) path, Func<Activation,int,bool> on the runtime (holder) path.
        SelfDelegateEmitter effectiveSelf = emitSelf;
        int pushSites = 0;
        foreach (var s in plan.Sites)
            if (s.Kind == RegionCursorKind.ClauseAlt || s.Kind == RegionCursorKind.IndexNode)
                pushSites++;
        int hoistGate = selfDelType == typeof(PredicateDelegate) ? 3 : 2;
        if (pushSites >= hoistGate)
        {
            var selfDelLoc = emit.DeclareLocal(selfDelType, "rselfdel");
            emitSelf(emit);
            emit.StoreLocal(selfDelLoc);
            effectiveSelf = e => e.LoadLocal(selfDelLoc);
        }

        var failLabel = emit.DefineLabel("rfail");
        var retLabel = emit.DefineLabel("rret");
        var dispatchLabel = emit.DefineLabel("rdispatch");
        var curLoc = emit.DeclareLocal<int>("rcur");

        var memberEntry = new Dictionary<int, Sigil.Label>();
        foreach (var m in region.Members)
            memberEntry[m.FunctorId] = emit.DefineLabel($"rmember_{m.FunctorId}");

        var cursorLabels = new Sigil.Label[plan.TotalCursors];
        cursorLabels[0] = memberEntry[regionFid];           // cursor 0 = root entry
        var cursorBySite = new Dictionary<(int, int), int>();
        var clauseAltCursor = new Dictionary<(int, int), int>();
        var indexNodeCursor = new Dictionary<(int, int), int>();
        foreach (var s in plan.Sites)
        {
            if (s.Kind == RegionCursorKind.MemberEntry)
            {
                // an external-entry cursor — its switch slot IS the member's
                // entry label (already defined above); no separate block, no site map.
                cursorLabels[s.Cursor] =
                    memberEntry[region.Members[s.MemberIndex].FunctorId];
                continue;
            }
            cursorLabels[s.Cursor] = emit.DefineLabel($"rcur_{s.Cursor}");
            if (s.Kind == RegionCursorKind.ClauseAlt)
                clauseAltCursor[(s.MemberIndex, s.ClauseIndex)] = s.Cursor;
            else if (s.Kind == RegionCursorKind.IndexNode)
                indexNodeCursor[(s.MemberIndex, s.ClauseIndex)] = s.Cursor;
            else
                cursorBySite[(s.MemberIndex, s.Pc)] = s.Cursor;
        }

        var ctx = new RegionEmitContext
        {
            Region = region, RegionFid = regionFid, RetLabel = retLabel,
            DispatchLabel = dispatchLabel, FailLabel = failLabel, MemberEntry = memberEntry,
            CursorLabels = cursorLabels, CursorBySite = cursorBySite,
            ClauseAltCursor = clauseAltCursor, IndexNodeCursor = indexNodeCursor,
        };

        // cur = arg1; br dispatch (the switch routes the cursor to its label).
        emit.LoadArgument(1);
        emit.StoreLocal(curLoc);
        emit.MarkLabel(dispatchLabel);
        emit.LoadLocal(curLoc);
        emit.Switch(cursorLabels);
        emit.Branch(failLabel);                              // out of range (unreachable)

        var gcCtx = new GuardContEmitContext();              // ADR-033 (no-op if unused)
        for (int mi = 0; mi < region.Members.Count; mi++)
        {
            var member = region.Members[mi];
            ctx.CurrentMemberIndex = mi;
            emit.MarkLabel(memberEntry[member.FunctorId]);   // clause 0 / single-clause entry
            if (member.ClauseCount == 1)
                EmitClauseBody(emit, member.BytecodeUnfused, 0, member.BytecodeUnfused.Length,
                    failLabel, member.CallSites, emitSelfDelegate: effectiveSelf,
                    calleeMap: calleeMap, regionCtx: ctx);
            else if (TryDescribeIndexed(member, calleeMap, out var idxInfo))
                EmitRegionIndexedMember(emit, member, mi, idxInfo!, ctx, effectiveSelf, calleeMap, gcCtx);
            else
                EmitRegionMultiClauseMember(emit, member, mi, ctx, effectiveSelf, calleeMap, gcCtx);
        }

        EmitGuardContEpilogues(emit, gcCtx, calleeMap, failLabel);   // ADR-033

        emit.MarkLabel(retLabel);
        emit.LoadArgument(0);
        // MUST go through EmitFunctorId, not a raw LoadConstant:
        // in persist mode a build-process fid means nothing at runtime. With
        // the raw constant baked, a persisted region whose BUILD-time fid
        // happened to equal the RUNTIME fid of a caller's region claimed the
        // caller's resume marker as its own and branched into a bogus
        // internal cursor — an infinite CP-push loop (Blint --exe hang, the
        // ILO mass parse failures, the member/2 8 GB stack crash).
        EmitFunctorId(emit, regionFid);
        emit.Call(EngineRegionReturnCursorMethod);
        emit.StoreLocal(curLoc);
        emit.LoadLocal(curLoc);
        emit.LoadConstant(0);
        emit.BranchIfGreaterOrEqual(dispatchLabel);          // intra-region return
        emit.LoadConstant(true);                              // cross-region return
        emit.Return();

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>Emit a MULTI-clause member's block (Stage 4) — a try_me_else chain.
    /// Clause 0 is at the member-entry label (already marked); clauses 1..N-1 are at
    /// their <c>ClauseAlt</c> cursor labels. Before each clause except the last, push
    /// a choice point carrying the NEXT clause's cursor + the region delegate, so a
    /// head-match (or later) failure returns false → backtrack → the CP → re-enters
    /// the region method at the next clause via <c>dispatch</c>. Each clause body is
    /// region-aware (its proceed → <c>br ret</c>, its calls threaded by the plan).</summary>
    private static void EmitRegionMultiClauseMember(
        Sigil.Emit<PredicateDelegate> emit, CompiledPredicate member, int mi,
        RegionEmitContext ctx, SelfDelegateEmitter emitSelf,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        GuardContEmitContext? gcCtx = null)
    {
        if (!TryDescribeTryMeElseChain(member, calleeMap, out var chain) || chain is null)
            throw new InvalidOperationException(
                $"Region member fid={member.FunctorId} is multi-clause but not a try_me_else chain.");
        var clauses = chain.Clauses;
        int n = clauses.Count;
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
                emit.MarkLabel(ctx.CursorLabels[ctx.ClauseAltCursor[(mi, i)]]);

            // ADR-031 — CP-free guard clause (see EmitCpFreeGuardClause): guard
            // failure branches to the next clause-alternative's region cursor
            // label (directly, or via the restore stub); the entry CP push is
            // skipped (lazily materialised at the commit only under pending
            // wakeups). The GUARD slice is emitted with regionCtx null +
            // forceLeafRuleInline so a tier-G guard Call takes the leaf
            // INLINE path (failure = a direct branch to the guard's fail label)
            // instead of the region br (whose failure would go to the region
            // fail label — past this clause). The post-commit body slice keeps
            // the region context. The plan's forward-resume cursors for the
            // inlined guard Call sites are marked dead afterwards.
            if (CpFreeGuardCommit && i < n - 1
                && TryGetCpFreeGuard(
                    member.BytecodeUnfused, clauses[i].Start, clauses[i].End,
                    member.Arity, calleeMap, member.CallSites, out var ginfo))
            {
                int guardEnd = ginfo.CutPc;
                int mi0 = mi, i0 = i;
                // ADR-034 — the guard inlines dynamic SNAPSHOTS: prefix the
                // clause with a staleness test per embedded fid; a mutated one
                // takes the fallback path — plain entry CP + un-inlined guard
                // (its dynamic call is a threaded by-fid call that dispatches
                // against the LIVE predicate) + jump into the shared
                // post-commit body. The guard's planned Call cursors are NOT
                // dead-marked in that case: the fallback's threaded calls own
                // them.
                var dynFids = ginfo.EmbeddedDynamicFids;
                Sigil.Label? dynFb = null, dynBody = null;
                if (dynFids is { Count: > 0 })
                {
                    dynFb = emit.DefineLabel($"dynfb_rm{mi}_{i}");
                    dynBody = emit.DefineLabel($"dynbody_rm{mi}_{i}");
                    foreach (int df in dynFids)
                    {
                        emit.LoadArgument(0);
                        EmitFunctorId(emit, df);
                        emit.Call(EngineIsDynMutatedMethod);
                        emit.BranchIfTrue(dynFb);
                    }
                }
                EmitCpFreeGuardClause(emit,
                    (s, e, fl) =>
                    {
                        if (e <= guardEnd)
                            EmitClauseBody(
                                emit, member.BytecodeUnfused, s, e, fl, member.CallSites,
                                emitSelfDelegate: emitSelf, calleeMap: calleeMap,
                                forceLeafRuleInline: true,
                                localSalt: $"_rm{mi0}g{i0}",
                                guardContCtx: gcCtx);
                        else
                        {
                            if (dynBody is not null)
                                emit.MarkLabel(dynBody);   // fallback re-joins here
                            EmitClauseBody(
                                emit, member.BytecodeUnfused, s, e, fl, member.CallSites,
                                emitSelfDelegate: emitSelf, calleeMap: calleeMap,
                                regionCtx: ctx);
                        }
                    },
                    member.BytecodeUnfused, clauses[i].Start, clauses[i].End, ginfo,
                    ctx.CursorLabels[ctx.ClauseAltCursor[(mi, i + 1)]], ctx.FailLabel,
                    emitSelf, ctx.ClauseAltCursor[(mi, i + 1)], member.Arity,
                    salt: $"_rm{mi}_c{i}",
                    markDeadCursors: dynFids is { Count: > 0 } ? (Action?)null : () =>
                    {
                        // The plan allocated a forward-resume cursor per Call
                        // site in the member; the guard's Calls were inlined, so
                        // their cursors are unreachable — mark the labels (the
                        // dispatch switch still references them).
                        int pc2 = clauses[i0].Start;
                        byte[] code2 = member.BytecodeUnfused;
                        while (pc2 < guardEnd)
                        {
                            if ((Opcode)code2[pc2] == Opcode.Call
                                && ctx.CursorBySite.TryGetValue((mi0, pc2), out int deadCur))
                                emit.MarkLabel(ctx.CursorLabels[deadCur]);
                            pc2 += (Opcode)code2[pc2] == Opcode.Meta
                                ? 6 : OpcodeTable.Get(code2[pc2]).Size;
                        }
                    });
                if (dynFb is not null)
                {
                    emit.MarkLabel(dynFb);
                    emit.LoadArgument(0);                         // engine
                    emitSelf(emit);                               // → region delegate
                    emit.LoadConstant(ctx.ClauseAltCursor[(mi, i + 1)]);
                    emit.LoadConstant(member.Arity);
                    emit.Call(EnginePushIlCpMethod);
                    int cutSz = OpcodeTable.Get(
                        (Opcode)member.BytecodeUnfused[guardEnd]).Size;
                    // localSalt: the fallback re-emits the same pcs the
                    // optimized guard slice already emitted — pc-named IL
                    // locals must not collide.
                    EmitClauseBody(emit, member.BytecodeUnfused,
                        clauses[i].Start, guardEnd + cutSz,
                        ctx.FailLabel, member.CallSites, emitSelfDelegate: emitSelf,
                        calleeMap: calleeMap, regionCtx: ctx,
                        localSalt: $"_dynfb{mi}_{i}");
                    emit.Branch(dynBody!);
                }
                continue;
            }

            if (i < n - 1)
            {
                emit.LoadArgument(0);                         // engine
                emitSelf(emit);                               // → region delegate
                emit.LoadConstant(ctx.ClauseAltCursor[(mi, i + 1)]);
                emit.LoadConstant(member.Arity);
                emit.Call(EnginePushIlCpMethod);
            }
            EmitClauseBody(emit, member.BytecodeUnfused, clauses[i].Start, clauses[i].End,
                ctx.FailLabel, member.CallSites, emitSelfDelegate: emitSelf,
                calleeMap: calleeMap, regionCtx: ctx);
        }
    }

    /// <summary>Emit an INDEXED member's block (Stage 6c) — the region analog of
    /// <see cref="EmitIndexedDispatchBody"/>. The member-entry label (already marked)
    /// holds the inline index decision (deref + tag/key tests, lowered from the
    /// compile-time index graph), branching forward to a chain node's label. A node
    /// pushes the region delegate's choice point carrying the NEXT node's region
    /// cursor (so a bucket-chain backtrack re-enters this method at that node via the
    /// dispatch switch), then branches to its clause body. Clause bodies are emitted
    /// once and region-aware (proceed → <c>br ret</c>, intra calls → <c>br</c>, their
    /// own calls threaded by the plan) exactly like every other member — the only
    /// indexed-specific code is the resolve + the per-node CP push. The node labels
    /// ARE the region cursor labels, so forward (resolve) and backward (CP) reach the
    /// same block. Index resolve labels/locals are salted per member
    /// (<c>_rm{mi}</c>) so several indexed members share one IL method cleanly.</summary>
    private static void EmitRegionIndexedMember(
        Sigil.Emit<PredicateDelegate> emit, CompiledPredicate member, int mi,
        IlIndexedDispatchInfo info, RegionEmitContext ctx, SelfDelegateEmitter emitSelf,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        GuardContEmitContext? gcCtx = null)
    {
        if (CpFreeIndexedCensus)
            AnalyzeIndexedBucketGuards(member, info, calleeMap);
        // ADR-031 indexed buckets — see EmitIndexedDispatchBody's twin. The
        // idxnext local holds the next node's REGION cursor (the same value
        // the bucket CP carries), -1 for a chain tail.
        var guardPlan = PlanIndexedGuards(member, info, calleeMap);
        int K = info.Nodes.Count;
        int N = info.Clauses.Count;
        string salt = $"_rm{mi}";

        // Node entry = the region cursor label (shared forward-resolve + backtrack
        // re-entry). Body labels are local to this member's block.
        var nodeLabels = new Sigil.Label[K];
        for (int n = 0; n < K; n++)
            nodeLabels[n] = ctx.CursorLabels[ctx.IndexNodeCursor[(mi, n)]];
        var bodyLabels = new Sigil.Label[N];
        for (int i = 0; i < N; i++)
            bodyLabels[i] = emit.DefineLabel($"ridx_body_{mi}_{i}");

        // ---- Resolve (member entry, forward): pick the entry node from the indexed
        //      argument. Inline when the graph builds; else the runtime resolver. ----
        if (!TryEmitInlineIndexResolve(emit, info, nodeLabels, salt))
        {
            var entry = emit.DeclareLocal<int>($"ridx_entry{salt}");
            emit.LoadArgument(0);
            EmitFunctorId(emit, member.FunctorId);
            emit.Call(IlIndexedDispatchResolveByFidMethod);
            emit.StoreLocal(entry);
            for (int n = 0; n < K; n++)
            {
                emit.LoadLocal(entry);
                emit.LoadConstant(n);
                emit.BranchIfEqual(nodeLabels[n]);
            }
            emit.Branch(ctx.FailLabel);   // unreachable: resolver returns a valid node
        }

        // ---- Chain nodes: push the next-node region CP (if any), run the body.
        //      ADR-031 indexed buckets: a guard clause's node stores the next
        //      node's region cursor in idxnext instead (-1 for a tail). ----
        Sigil.Local? idxNext = guardPlan is not null
            ? emit.DeclareLocal<int>($"idxnext{salt}") : null;
        for (int n = 0; n < K; n++)
        {
            emit.MarkLabel(nodeLabels[n]);
            int next = info.Nodes[n].NextCursor;
            if (guardPlan is not null && guardPlan.GuardOk[info.Nodes[n].ClauseIndex])
            {
                emit.LoadConstant(next >= 0 ? ctx.IndexNodeCursor[(mi, next)] : -1);
                emit.StoreLocal(idxNext!);
            }
            else if (next >= 0)
            {
                emit.LoadArgument(0);                            // engine
                emitSelf(emit);                                 // → region delegate
                emit.LoadConstant(ctx.IndexNodeCursor[(mi, next)]);   // next node's region cursor
                emit.LoadConstant(member.Arity);
                emit.Call(EnginePushIlCpMethod);
            }
            emit.Branch(bodyLabels[info.Nodes[n].ClauseIndex]);
        }

        // ---- Clause bodies, region-aware, emitted once and shared across nodes. ----
        for (int i = 0; i < N; i++)
        {
            emit.MarkLabel(bodyLabels[i]);
            if (guardPlan is not null && guardPlan.GuardOk[i])
            {
                var ginfo = guardPlan.Info[i];
                int guardEnd = ginfo.CutPc;
                int i0 = i;
                var dynFids = ginfo.EmbeddedDynamicFids;
                Sigil.Label? dynFb = null, dynBody = null;
                if (dynFids is { Count: > 0 })
                {
                    dynFb = emit.DefineLabel($"ridx_dynfb{salt}_{i}");
                    dynBody = emit.DefineLabel($"ridx_dynbody{salt}_{i}");
                    foreach (int df in dynFids)
                    {
                        emit.LoadArgument(0);
                        EmitFunctorId(emit, df);
                        emit.Call(EngineIsDynMutatedMethod);
                        emit.BranchIfTrue(dynFb);
                    }
                }
                EmitCpFreeGuardClause(emit,
                    (s, e, fl) =>
                    {
                        if (e <= guardEnd)
                            EmitClauseBody(
                                emit, member.BytecodeUnfused, s, e, fl, member.CallSites,
                                emitSelfDelegate: emitSelf, calleeMap: calleeMap,
                                forceLeafRuleInline: true,
                                localSalt: $"_ridx{mi}g{i0}",
                                guardContCtx: gcCtx);
                        else
                        {
                            if (dynBody is not null)
                                emit.MarkLabel(dynBody);
                            EmitClauseBody(
                                emit, member.BytecodeUnfused, s, e, fl, member.CallSites,
                                emitSelfDelegate: emitSelf, calleeMap: calleeMap,
                                regionCtx: ctx);
                        }
                    },
                    member.BytecodeUnfused, info.Clauses[i].Start, info.Clauses[i].End,
                    ginfo,
                    ctx.FailLabel /* unused: dynamic dispatch */, ctx.FailLabel,
                    emitSelf, 0 /* unused */, member.Arity,
                    salt: $"_ridx{mi}_{i}",
                    markDeadCursors: dynFids is { Count: > 0 } ? (Action?)null : () =>
                    {
                        // The plan allocated forward-resume cursors for the
                        // guard's (now inlined) Call sites — mark them dead
                        // (the fallback, when present, uses them instead).
                        int pc2 = info.Clauses[i0].Start;
                        byte[] code2 = member.BytecodeUnfused;
                        while (pc2 < guardEnd)
                        {
                            if ((Opcode)code2[pc2] == Opcode.Call
                                && ctx.CursorBySite.TryGetValue((mi, pc2), out int deadCur))
                                emit.MarkLabel(ctx.CursorLabels[deadCur]);
                            pc2 += (Opcode)code2[pc2] == Opcode.Meta
                                ? 6 : OpcodeTable.Get(code2[pc2]).Size;
                        }
                    },
                    dynamicFailDispatch: () =>
                    {
                        // Guard failed: dispatch on the region cursor in
                        // idxnext (the region-wide label table — the same
                        // mapping the method's cursor switch uses); the tail
                        // sentinel (-1) falls through to the region fail.
                        emit.LoadLocal(idxNext!);
                        emit.Switch(ctx.CursorLabels);
                        emit.Branch(ctx.FailLabel);
                    },
                    dynamicCursor: e2 => e2.LoadLocal(idxNext!));
                if (dynFb is not null)
                {
                    emit.MarkLabel(dynFb);
                    var skipPush = emit.DefineLabel($"ridx_dynfb_nopush{salt}_{i}");
                    emit.LoadLocal(idxNext!);
                    emit.LoadConstant(0);
                    emit.BranchIfLess(skipPush);
                    emit.LoadArgument(0);
                    emitSelf(emit);
                    emit.LoadLocal(idxNext!);
                    emit.LoadConstant(member.Arity);
                    emit.Call(EnginePushIlCpMethod);
                    emit.MarkLabel(skipPush);
                    int cutSz = OpcodeTable.Get(
                        (Opcode)member.BytecodeUnfused[guardEnd]).Size;
                    EmitClauseBody(emit, member.BytecodeUnfused,
                        info.Clauses[i].Start, guardEnd + cutSz,
                        ctx.FailLabel, member.CallSites, emitSelfDelegate: emitSelf,
                        calleeMap: calleeMap, regionCtx: ctx,
                        localSalt: $"_ridxfb{mi}_{i}");
                    emit.Branch(dynBody!);
                }
                continue;
            }
            EmitClauseBody(emit, member.BytecodeUnfused, info.Clauses[i].Start, info.Clauses[i].End,
                ctx.FailLabel, member.CallSites, emitSelfDelegate: emitSelf,
                calleeMap: calleeMap, regionCtx: ctx);
        }
    }

    /// <summary>Region-mode opcode handling (Stage 3). Returns true (and advances
    /// <paramref name="pcRef"/>) for the opcodes the region layout rewrites:
    /// <c>proceed</c> / <c>deallocate_proceed</c> → <c>br ret</c>; an intra-region
    /// non-tail <c>Call</c> → <c>SetB0</c> + <c>SetCp(return marker)</c> +
    /// <c>br member</c> + the return-continuation label; an intra-region tail
    /// <c>Execute</c> → <c>SetB0</c> + <c>br member</c> (Cp unchanged = the caller's
    /// continuation). Returns false for every other opcode (head match, unify,
    /// arith, allocate/deallocate, deterministic builtin), which the normal switch
    /// emits unchanged.</summary>
    /// <summary>Flush pending attribute wakeups at a region goal boundary (a
    /// `br`-call or a proceed), then fail (backtrack) if a constraint failed. The
    /// interpreter flushes at every Call/Execute/Proceed/Deallocate; IL code relies
    /// on control passing through the dispatch loop between trampoline calls to get
    /// those flushes — but an intra-region call/return is a `br` that bypasses the
    /// loop, so the region must flush at its OWN boundaries (same class as the
    /// IL-cut flush). Cheap: a `_pendingWakeups.Count==0` fast path.</summary>
    private static void EmitRegionWakeupFlush(
        Sigil.Emit<PredicateDelegate> emit, Sigil.Label failLabel)
    {
        emit.LoadArgument(0);
        emit.Call(EngineFlushWakeupsForIlCutMethod);
        emit.BranchIfFalse(failLabel);
    }

    /// <summary>ADR-049 stage 2: the wake INTERRUPT at a region boundary,
    /// replacing the bool drain. The helper's verdict: 0 — nothing pending
    /// (or the drain fallback succeeded), fall through; 1 — interrupt armed
    /// (P at the driver, IlTailCallPending set), return true so the dispatch
    /// loop runs it; 2 — the drain fallback failed, branch to fail.
    /// <paramref name="calleeFid"/> &lt; 0 means the proceed shape (resume =
    /// the continuation CP); otherwise the call shape (resume = the callee's
    /// forward marker — CP must already hold the callee's continuation when
    /// this runs).</summary>
    private static void EmitRegionWakeBoundary(
        Sigil.Emit<PredicateDelegate> emit, Sigil.Label failLabel, int calleeFid)
    {
        emit.LoadArgument(0);
        if (calleeFid >= 0)
        {
            EmitFunctorId(emit, calleeFid);
            emit.Call(EngineWakeBoundaryCallMethod);
        }
        else
        {
            emit.Call(EngineWakeBoundaryProceedMethod);
        }
        var verdict = emit.DeclareLocal<int>($"wake_v_{NextLabelSeq()}");
        emit.StoreLocal(verdict);
        emit.LoadLocal(verdict);
        emit.LoadConstant(2);
        emit.BranchIfEqual(failLabel);
        var goOn = emit.DefineLabel($"wake_go_{NextLabelSeq()}");
        emit.LoadLocal(verdict);
        emit.LoadConstant(0);
        emit.BranchIfEqual(goOn);
        emit.LoadConstant(true);   // verdict 1: suspended — the loop takes P
        emit.Return();
        emit.MarkLabel(goOn);
    }

    private static bool TryEmitRegionOpcode(
        Sigil.Emit<PredicateDelegate> emit, byte[] code, int pc, Opcode op,
        RegionEmitContext ctx, ref int pcRef)
    {
        switch (op)
        {
            case Opcode.Proceed:
                // ADR-049 stage 2: pending wakeups interrupt here instead of
                // draining — CP already holds the continuation the resume
                // will jump to.
                EmitRegionWakeBoundary(emit, ctx.FailLabel, calleeFid: -1);
                emit.Branch(ctx.RetLabel);
                pcRef = pc + 1;
                return true;
            case Opcode.DeallocateProceed:
                emit.LoadArgument(0);
                emit.Call(EngineDeallocateMethod);
                // After the deallocate, so CP is the caller continuation the
                // proceed-shape resume captures.
                EmitRegionWakeBoundary(emit, ctx.FailLabel, calleeFid: -1);
                emit.Branch(ctx.RetLabel);
                pcRef = pc + OpcodeTable.Get((byte)op).Size;
                return true;
            case Opcode.Call:
            case Opcode.Execute:
            {
                var member = ctx.Region.Members[ctx.CurrentMemberIndex];
                int fid = FindCallSiteFunctorId(member.CallSites, pc);
                if (fid < 0) return false;   // malformed — let the normal path throw
                // The cut barrier for the callee — set BEFORE the wake
                // boundary: the call-shape resume dispatches the callee by
                // forward marker without re-running this site, and
                // WakeReturn re-establishes B0 = B post-wake so a cut in the
                // callee can never prune the wake's alternatives.
                emit.LoadArgument(0);
                emit.LoadArgument(0);
                emit.Call(EngineBGetter);
                emit.Call(EngineSetB0Method);
                bool intra = ctx.Region.IsIntraRegion(fid);
                if (op == Opcode.Call)
                {
                    // Non-tail: register the forward continuation (Cp = a resume
                    // marker into THIS region at the plan's cursor for this site)
                    // BEFORE the wake boundary — the wake frame captures it as
                    // the continuation the callee will proceed into.
                    int cursor = ctx.CursorBySite[(ctx.CurrentMemberIndex, pc)];
                    emit.LoadArgument(0);
                    EmitResumeMarker(emit, ctx.RegionFid, cursor);
                    emit.Call(EngineSetCpMethod);
                    // ADR-049 stage 2: the interrupt in front of the call.
                    EmitRegionWakeBoundary(emit, ctx.FailLabel, fid);
                    if (intra)
                    {
                        // Intra-region: br to the member block; its proceed returns
                        // here via `ret` → dispatch → the continuation label.
                        emit.Branch(ctx.MemberEntry[fid]);
                    }
                    else
                    {
                        // Cross-region: the Phase-16 trampoline — set Pc = callee
                        // entry marker, return to the dispatch loop; when the callee
                        // proceeds the loop re-enters this region at `cursor`.
                        emit.LoadArgument(0);
                        EmitFunctorId(emit, fid);
                        emit.LoadConstant(0);
                        emit.Call(EngineEncodeResumeMarkerMethod);
                        emit.Call(EngineSetPcMethod);
                        emit.LoadArgument(0);
                        emit.LoadConstant(true);
                        emit.Call(EngineIlTailCallPendingSetter);
                        emit.LoadConstant(true);
                        emit.Return();
                    }
                    emit.MarkLabel(ctx.CursorLabels[cursor]);   // the continuation
                }
                else if (intra)
                {
                    // Intra-region tail call: Cp already holds this member's caller
                    // continuation, so the callee's proceed returns straight to it.
                    EmitRegionWakeBoundary(emit, ctx.FailLabel, fid);   // ADR-049
                    emit.Branch(ctx.MemberEntry[fid]);
                }
                else
                {
                    // Cross-region tail call: tail-trampoline (Cp unchanged = the
                    // region's caller continuation; the callee's proceed returns to it).
                    EmitRegionWakeBoundary(emit, ctx.FailLabel, fid);   // ADR-049
                    emit.LoadArgument(0);
                    EmitFunctorId(emit, fid);
                    emit.LoadConstant(0);
                    emit.Call(EngineEncodeResumeMarkerMethod);
                    emit.Call(EngineSetPcMethod);
                    emit.LoadArgument(0);
                    emit.LoadConstant(true);
                    emit.Call(EngineIlTailCallPendingSetter);
                    emit.LoadConstant(true);
                    emit.Return();
                }
                pcRef = pc + OpcodeTable.Get((byte)op).Size;
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>A single-clause RULE whose body can be inlined FLAT into a caller
    /// (inline-rule case 1) — like <see cref="IsLeafPredicate"/> but allowing a body
    /// of deterministic builtins, arithmetic and unification. It must create no
    /// choice point, need no environment frame, make no user call, and not cut: so
    /// NO allocate/deallocate, NO cut/neck_cut/get_level, NO Call/Execute (any
    /// tier), and a <c>CallBuiltin</c> only to a deterministic, non-meta builtin.
    /// Such a body runs to completion in one shot exactly like a leaf's head match
    /// (a failing body op branches to the caller's fail label), so the EXISTING
    /// leaf-inline emit (<see cref="EmitClauseBody"/> with
    /// <c>suppressProceedReturn</c>) handles it with no new machinery — det
    /// builtins emit no resume cursor, arith/unify ops branch to the fail
    /// label.</summary>
    internal static bool IsInlinableLeafRule(CompiledPredicate pred)
    {
        if (pred.ClauseCount != 1) return false;
        byte[] code = pred.BytecodeUnfused;
        int pc = 0;
        bool sawProceed = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            switch (op)
            {
                case Opcode.Proceed: sawProceed = true; pc += 1; continue;
                case Opcode.Meta: pc += 6; continue;   // dbg-info, runtime no-op
                // CP-creating / env / cut / user-call → not flat-inlinable.
                case Opcode.Allocate:
                case Opcode.Deallocate:
                case Opcode.Cut:
                case Opcode.NeckCut:
                case Opcode.GetLevel:
                case Opcode.Call:
                case Opcode.Execute:
                case Opcode.CallIl:
                case Opcode.ExecuteIl:
                case Opcode.CallBytecode:
                case Opcode.ExecuteBytecode:
                case Opcode.ExecuteBuiltin:
                // ADR-025 — inline ITE (CP push + jump labels): not flat.
                case Opcode.TryMeElse:
                case Opcode.TrustMe:
                case Opcode.Jump:
                    return false;
                case Opcode.CallBuiltin:
                {
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                        BytecodeIO.ReadInt32(code, pc + 1));
                    // meta-call + backtrackable builtins need resume cursors /
                    // the enclosing-call machinery — not a flat body.
                    // precomputed flags instead of name compares.
                    if (entry.IsCall || entry.IsDollarCall || entry.IsBacktrackable)
                        return false;
                    pc += OpcodeTable.Get((byte)op).Size;
                    continue;
                }
                default:
                {
                    int size = OpcodeTable.Get((byte)op).Size;
                    if (size <= 0) return false;   // unknown / variable-size → bail
                    pc += size;
                    continue;
                }
            }
        }
        return sawProceed;
    }

    /// <summary>True iff <paramref name="pred"/> is a pure FACT predicate: every
    /// clause is only head matching, and the bytecode is otherwise just the
    /// clause-dispatch skeleton (switch_on_* / try / retry / trust /
    /// try_me_else …) and <c>proceed</c> — no body calls, no environment
    /// (permanent Y variables → there are no get_variable_y / allocate
    /// opcodes), no arithmetic. Generalises <see cref="IsLeafPredicate"/> (the
    /// single-clause special case) to any clause count. Eligibility for inlining
    /// a multi-clause fact's clause dispatch into its caller's IL method —
    /// Phase 1 of docs/design/il-local-inlining.md. (Single-clause facts are
    /// already inlined by the leaf path; this covers the multi-clause
    /// generators, e.g. crypt's odd/even.)</summary>
    internal static bool IsFactPredicate(CompiledPredicate pred)
    {
        byte[] code = pred.BytecodeUnfused;
        int pc = 0;
        bool sawProceed = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Proceed) { sawProceed = true; pc += 1; continue; }
            if (op == Opcode.Meta) { pc += 6; continue; }   // dbg-info, runtime no-op
            if (IsHeadMatchingOpcode(op) || IsFactDispatchOpcode(op))
            {
                int size = OpcodeTable.Get((byte)op).Size;
                if (size <= 0) return false;
                pc += size;
                continue;
            }
            return false;   // a call / allocate / arith / Y-slot op → not a pure fact
        }
        return sawProceed;
    }

    /// <summary>The clause-dispatch skeleton opcodes a fact predicate may
    /// contain besides head matching and <c>proceed</c> (first-argument indexing
    /// + the try/retry/trust or try_me_else/retry_me_else/trust_me chains).</summary>
    private static bool IsFactDispatchOpcode(Opcode op) => op switch
    {
        Opcode.SwitchOnTerm or Opcode.SwitchOnArg => true,
        Opcode.SwitchOnAtom or Opcode.SwitchOnInteger or Opcode.SwitchOnStructure => true,
        Opcode.SwitchOnAtomArg or Opcode.SwitchOnIntegerArg or Opcode.SwitchOnStructureArg => true,
        Opcode.Try or Opcode.Retry or Opcode.Trust => true,
        Opcode.TryMeElse or Opcode.RetryMeElse or Opcode.TrustMe => true,
        Opcode.Nop => true,
        _ => false,
    };

}
