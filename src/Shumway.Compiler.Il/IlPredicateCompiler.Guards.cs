using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

public sealed partial class IlPredicateCompiler
{
    /// <summary>Shared try-me-else-chain emit body used by both the
    /// DynamicMethod runtime path (above) and the persisted
    /// assembly path (<see cref="EmitPersistedTryMeElseChain"/>). All
    /// self-references for the per-clause IL CP push route through
    /// <paramref name="emitSelf"/>; callers pick the holder-based or
    /// field-based variant.</summary>
    /// <summary>ADR-032 sizing — promotion/link-time counters for the CP-free
    /// guard recogniser: which tier each accepted clause took, and WHY each
    /// cut-shaped clause was rejected. The reject reasons map 1:1 to the
    /// ADR-032 static-widening alternatives (Caps → raise the fail-direct
    /// caps; CalleeCut → callee-internal cuts; CalleeCalls → true-G3 nested
    /// inlining), so running a real program with these counters IS the impact
    /// estimate for each widening. Surfaced by <c>shumway-link --verbose</c>
    /// (persisted IL build) and <c>SHUMWAY_CPFREE_STATS=1</c> in the REPL
    /// (runtime promotion). Counts are per-emission (a PGO
    /// instrumented→optimised recompile counts twice) — indicative, not exact.</summary>
    public static class CpFreeGuardStats
    {
        public static long TierA, TierB, TierGLeaf, TierG2;
        public static long RejectGuardShape;       // non-whitelist op in a cut-shaped guard
        public static long RejectCalleeUnresolved; // no calleeMap / fid unresolved
        public static long RejectCalleeCalls;      // callee body calls others → G3 candidate
        public static long RejectCalleeCaps;       // callee over clause/byte caps
        public static long RejectCalleeCut;        // cut inside the callee
        public static long RejectCalleeShape;      // other callee shape

        /// <summary>Per-opcode breakdown of <see cref="RejectGuardShape"/> —
        /// WHICH non-whitelist opcode rejected the cut-shaped guard, so the
        /// whitelist-widening candidates rank by real frequency. Indexed by the
        /// opcode byte.</summary>
        public static readonly long[] RejectGuardOpByOpcode = new long[256];

        /// <summary>Sub-reason breakdown of <see cref="RejectCalleeShape"/> —
        /// the bucket is a mixed bag (non-whitelist callee ops, backtrackable
        /// builtins, unrecognised clause ranges, frame discipline, the
        /// multi-solution position rule …); this ranks its composition.</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long>
            RejectShapeDetail = new();

        internal static void BumpShapeDetail(string detail)
            => RejectShapeDetail.AddOrUpdate(detail, 1, static (_, v) => v + 1);

        /// <summary>Stable-dynamic census — functor ids of dynamic predicates
        /// whose clause store contains RULE clauses (bodies). Populated by the
        /// link-time IL build from the warm engine's rehydrated seeds (the
        /// link calleeMap only sees hollow trampolines); the rules/facts
        /// shape-detail split consults this before falling back to the
        /// bytecode scan. Concurrent: parallel test hosts build bundles
        /// side by side (one link per process in the CLI).</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte>
            DynamicFidsWithRules = new();

        /// <summary>Stable-dynamic census — dynamic predicates with clauses,
        /// split into with-rules (the mutation-cold fast-path candidate pool)
        /// vs fact-only (the real assert/retract targets).</summary>
        public static long DynPoolRules, DynPoolFacts;

        /// <summary>ADR-034 — accepted CP-free guard clauses whose guard
        /// inlines one or more dynamic SNAPSHOTS (each such clause carries the
        /// clause-entry staleness test + fallback).</summary>
        public static long AcceptWithDynSnapshot;

        /// <summary>ADR-031 indexed-bucket sizing (census-only, gated
        /// <see cref="CpFreeIndexedCensus"/>) — chain nodes inside INDEXED
        /// dispatch that push a bucket choice point (NextCursor ≥ 0), how
        /// many of them run a cut-shaped clause, and how many of those the
        /// CP-free recognizer would accept. The emission does NOT act on
        /// this — it sizes the deferred "indexed buckets" extension.</summary>
        public static long IndexedBucketCpNodes, IndexedBucketCandidates, IndexedBucketAccept;

        public static void Reset()
        {
            TierA = TierB = TierGLeaf = TierG2 = 0;
            RejectGuardShape = RejectCalleeUnresolved = RejectCalleeCalls = 0;
            RejectCalleeCaps = RejectCalleeCut = RejectCalleeShape = 0;
            System.Array.Clear(RejectGuardOpByOpcode);
            RejectShapeDetail.Clear();
            DynamicFidsWithRules.Clear();
            DynPoolRules = DynPoolFacts = 0;
            AcceptWithDynSnapshot = 0;
            IndexedBucketCpNodes = IndexedBucketCandidates = IndexedBucketAccept = 0;
        }

        public static long AcceptTotal => TierA + TierB + TierGLeaf + TierG2;
        public static long RejectTotal =>
            RejectGuardShape + RejectCalleeUnresolved + RejectCalleeCalls
            + RejectCalleeCaps + RejectCalleeCut + RejectCalleeShape;

        public static string Summary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("cp-free guard clauses (ADR-031):");
            sb.AppendLine($"  accepted {AcceptTotal}: tierA(cmp)={TierA} tierB(bind)={TierB} "
                + $"tierG(leaf-call)={TierGLeaf} tierG2(fail-direct-call)={TierG2}");
            sb.AppendLine($"  rejected {RejectTotal} (cut-shaped clauses keeping their CP):");
            sb.AppendLine($"    guard op outside whitelist        : {RejectGuardShape}");
            sb.AppendLine($"    callee unresolved                 : {RejectCalleeUnresolved}");
            sb.AppendLine($"    callee calls others (G3 candidate): {RejectCalleeCalls}");
            sb.AppendLine($"    callee over caps (raise candidate): {RejectCalleeCaps}");
            sb.AppendLine($"    callee has cut                    : {RejectCalleeCut}");
            if (DynPoolRules + DynPoolFacts > 0)
                sb.AppendLine($"    dynamic pool: with-rules={DynPoolRules} fact-only={DynPoolFacts}");
            if (AcceptWithDynSnapshot > 0)
                sb.AppendLine($"    accepted w/ inlined dynamic snapshot (checked): {AcceptWithDynSnapshot}");
            if (IndexedBucketCpNodes > 0)
                sb.AppendLine($"    indexed-bucket census: CP nodes={IndexedBucketCpNodes} "
                    + $"cut-shaped={IndexedBucketCandidates} recognizer-acceptable={IndexedBucketAccept}");
            sb.Append($"    callee shape (control/backtrack)  : {RejectCalleeShape}");
            return sb.ToString();
        }
    }

    /// <summary>ADR-033 — gates the guard CONTINUATION-STACK mechanism: a
    /// CP-free guard's call to a (non-leaf) fail-direct callee pushes its
    /// ok/fail continuation cursors and branches to ONE shared per-method copy
    /// of the callee, instead of duplicating the callee's code at every call
    /// site. Prototype opt-in (<c>SHUMWAY_CPFREE_CONT=1</c>); the duplication
    /// path remains the default.</summary>
    public static bool CpFreeGuardContinuations { get; set; } =
        System.Environment.GetEnvironmentVariable("SHUMWAY_CPFREE_CONT") == "1";

    /// <summary>ADR-031 indexed buckets — CP-free guard commit inside INDEXED
    /// dispatch (default ON; <c>SHUMWAY_CPFREE_IDXBUCKET=0</c> disables). A
    /// chain node whose clause is an accepted CP-free guard skips its bucket
    /// choice-point push: the node stores the next node's cursor in a
    /// per-member IL local (<c>-1</c> for a chain tail) and branches to the
    /// clause's SHARED guard block; guard failure restores and dispatches on
    /// the local (an IL <c>switch</c> — out-of-range <c>-1</c> falls through
    /// to the method fail), replacing the push + engine-backtrack round trip.
    /// The rare paths (pending-wakeup lazy CP, ADR-034 stale-snapshot
    /// fallback) materialize the skipped CP FROM the local, skipping the push
    /// on the tail sentinel. ONE local per indexed member suffices: its live
    /// range is [node entry → guard resolution], and fail-direct guards never
    /// re-enter a node (an indexed callee is not fail-direct-describable), so
    /// the windows cannot nest — if guards ever accept indexed callees, this
    /// must graduate to the ADR-033 continuation stack.</summary>
    public static bool CpFreeIndexedBuckets { get; set; } =
        System.Environment.GetEnvironmentVariable("SHUMWAY_CPFREE_IDXBUCKET") != "0";

    /// <summary>ADR-031 indexed-bucket sizing census, opt-in
    /// (<c>SHUMWAY_CPFREE_IDXCENSUS=1</c>): at the two indexed emit sites,
    /// replay the CP-free recognizer over every bucket chain node that
    /// pushes a choice point, WITHOUT changing emission — the main
    /// accept/reject counters stay clean (the census calls suppress them);
    /// only the <c>indexed-bucket census</c> summary line and (unavoidably)
    /// the describe-level shape-detail labels reflect the census.</summary>
    internal static readonly bool CpFreeIndexedCensus =
        System.Environment.GetEnvironmentVariable("SHUMWAY_CPFREE_IDXCENSUS") == "1";

    /// <summary>See <see cref="CpFreeIndexedCensus"/>.</summary>
    private static void AnalyzeIndexedBucketGuards(
        CompiledPredicate pred, IlIndexedDispatchInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        byte[] code = pred.BytecodeUnfused;
        foreach (var node in info.Nodes)
        {
            if (node.NextCursor < 0) continue;      // chain tail — no CP push
            System.Threading.Interlocked.Increment(
                ref CpFreeGuardStats.IndexedBucketCpNodes);
            var (s, e) = info.Clauses[node.ClauseIndex];
            if (!HasCutAhead(code, s, e)) continue;
            System.Threading.Interlocked.Increment(
                ref CpFreeGuardStats.IndexedBucketCandidates);
            if (TryGetCpFreeGuard(code, s, e, pred.Arity, calleeMap,
                    pred.CallSites, out _, suppressStats: true))
                System.Threading.Interlocked.Increment(
                    ref CpFreeGuardStats.IndexedBucketAccept);
        }
    }

    /// <summary>ADR-031 indexed buckets — the per-predicate guard plan: which
    /// clauses are accepted CP-free guards (recognised ONCE per clause; every
    /// node referencing the clause routes through its shared guard block),
    /// and the extra forward-resume cursors the ADR-034 fallbacks need
    /// (standalone sizing).</summary>
    private sealed class IndexedGuardPlan
    {
        public required bool[] GuardOk;
        public required CpFreeGuardInfo[] Info;
        public int ExtraDynSites;
    }

    private static IndexedGuardPlan? PlanIndexedGuards(
        CompiledPredicate pred, IlIndexedDispatchInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        if (!CpFreeIndexedBuckets || !CpFreeGuardCommit) return null;
        int N = info.Clauses.Count;
        // Only clauses referenced by at least one CP-pushing node profit —
        // a clause reached solely through chain tails has no push to skip.
        var hasCpNode = new bool[N];
        foreach (var node in info.Nodes)
            if (node.NextCursor >= 0) hasCpNode[node.ClauseIndex] = true;
        var plan = new IndexedGuardPlan
        {
            GuardOk = new bool[N],
            Info = new CpFreeGuardInfo[N],
        };
        byte[] code = pred.BytecodeUnfused;
        bool any = false;
        for (int i = 0; i < N; i++)
        {
            if (!hasCpNode[i]) continue;
            var (s, e) = info.Clauses[i];
            if (!TryGetCpFreeGuard(code, s, e, pred.Arity, calleeMap,
                    pred.CallSites, out plan.Info[i]))
                continue;
            plan.GuardOk[i] = true;
            any = true;
            if (plan.Info[i].EmbeddedDynamicFids is { Count: > 0 })
            {
                // The ADR-034 fallback re-emits the guard's Call sites as
                // threaded calls — each takes an extra resume cursor.
                int pc = s;
                while (pc < plan.Info[i].CutPc)
                {
                    if ((Opcode)code[pc] == Opcode.Call) plan.ExtraDynSites++;
                    pc += (Opcode)code[pc] == Opcode.Meta
                        ? 6 : OpcodeTable.Get(code[pc]).Size;
                }
            }
        }
        return any ? plan : null;
    }

    // Empty-dynamic-as-fail: MEASURED AND REJECTED (2026-07-10). Inlining a
    // guard call to a link-time-empty dynamic as FAIL (under the ADR-034
    // staleness test) converted +69/+111% of the corpus guards STATICALLY —
    // but in any reasonable program the assert DOES happen, so the steady
    // state is the fallback (the plain pre-feature path) PLUS a per-entry
    // membership probe: a net runtime cost for the dominant
    // assert-before-call idiom. The corpus counts were also inflated by GX
    // host-interface placeholders (i_*) that production links declare as
    // FOREIGN predicates — whose det-ness the guard machinery already derives
    // from the implementation (BacktrackableDetector), needing no dynamic
    // modelling at all.

    /// <summary>ADR-033 — per-IL-method state for the continuation mechanism:
    /// the continuation label table (cursor = index), the shared callee-copy
    /// entry labels, and the lazily-created pop/dispatch epilogues.</summary>
    internal sealed class GuardContEmitContext
    {
        public readonly List<Sigil.Label> ContLabels = new();
        public readonly Dictionary<int, Sigil.Label> CalleeEntry = new();
        public readonly List<CompiledPredicate> PendingCallees = new();
        public Sigil.Label? FailEpilogue;
        public Sigil.Label? OkEpilogue;
        public int AllocCursor(Sigil.Label label)
        {
            ContLabels.Add(label);
            return ContLabels.Count - 1;
        }
    }

    /// <summary>ADR-033 — the method-end epilogues: each pending callee's ONE
    /// shared copy (entered by <c>br</c> from its call sites), then the ok /
    /// fail pop-and-dispatch blocks switching over the continuation label
    /// table. No-op when no site used the mechanism.</summary>
    /// <summary>ADR-033 — the shared-copy entry label for <paramref name="fid"/>,
    /// registering the callee for method-end emission on first request.</summary>
    private static Sigil.Label GetOrAddGuardContCopy(
        Sigil.Emit<PredicateDelegate> emit, GuardContEmitContext ctx,
        int fid, CompiledPredicate callee)
    {
        if (!ctx.CalleeEntry.TryGetValue(fid, out var entryLbl))
        {
            entryLbl = emit.DefineLabel($"gc_callee_{fid}");
            ctx.CalleeEntry[fid] = entryLbl;
            ctx.PendingCallees.Add(callee);
        }
        return entryLbl;
    }

    private static void EmitGuardContEpilogues(
        Sigil.Emit<PredicateDelegate> emit, GuardContEmitContext ctx,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        Sigil.Label methodFail)
    {
        if (ctx.PendingCallees.Count == 0) return;
        ctx.FailEpilogue ??= emit.DefineLabel("gc_fail_epi");
        ctx.OkEpilogue ??= emit.DefineLabel("gc_ok_epi");
        // Index loop: emitting a copy may register FURTHER pending callees
        // (cross-tail targets, shared inners).
        for (int ci = 0; ci < ctx.PendingCallees.Count; ci++)
        {
            var callee = ctx.PendingCallees[ci];
            emit.MarkLabel(ctx.CalleeEntry[callee.FunctorId]);
            // Deterministic re-describe (the call site already validated it).
            TryDescribeFailDirectCallee(callee, calleeMap, out var cls, out _);
            EmitFailDirectCalleeInline(emit, callee, cls!, ctx.FailEpilogue,
                calleeMap, $"_gcc{callee.FunctorId}", ctx);
            emit.Branch(ctx.OkEpilogue);
        }
        var targets = ctx.ContLabels.ToArray();
        emit.MarkLabel(ctx.OkEpilogue);
        emit.LoadArgument(0);
        emit.Call(EnginePopGuardContOkMethod);
        emit.Switch(targets);
        emit.Branch(methodFail);                 // out of range — unreachable
        emit.MarkLabel(ctx.FailEpilogue);
        emit.LoadArgument(0);
        emit.Call(EnginePopGuardContFailMethod);
        emit.Switch(targets);
        emit.Branch(methodFail);
    }

    /// <summary>ADR-031 — the recognised shape of a CP-free guard clause (see
    /// <see cref="TryGetCpFreeGuard"/>).</summary>
    internal readonly struct CpFreeGuardInfo
    {
        /// <summary>pc of the committing cut opcode (<c>neck_cut</c> or the deep
        /// <c>cut</c>).</summary>
        public int CutPc { get; init; }
        /// <summary>True → the framed <c>cut [slot]</c> (case G); false →
        /// frameless <c>neck_cut</c> (tiers A/B).</summary>
        public bool DeepCut { get; init; }
        /// <summary>The guard can bind / allocate — take the trail/heap/HB
        /// snapshot and restore it on the fail path.</summary>
        public bool NeedsSnapshot { get; init; }
        /// <summary>The guard writes argument registers (call staging, callee
        /// body temps) — save A0..arity-1 in IL locals at entry and restore
        /// them on the fail path.</summary>
        public bool NeedsRegSave { get; init; }
        /// <summary>The clause allocated an environment frame before the cut —
        /// the fail path must <c>Deallocate</c> before branching on.</summary>
        public bool Framed { get; init; }
        /// <summary>ADR-034 — functor ids of the dynamic SNAPSHOTS this
        /// clause's guard (transitively, through fail-direct callees and
        /// shared copies) inlines. Non-null → the emit prefixes the clause
        /// with a staleness test per fid (<c>Activation.IsDynMutated</c>) and an
        /// un-inlined fallback path (plain guard + real by-fid call to the
        /// live dynamic + jump into the shared post-commit body).</summary>
        public List<int>? EmbeddedDynamicFids { get; init; }
    }

    /// <summary>ADR-034 — side-channel collected by the fail-direct describe
    /// walk: the dynamic-snapshot fids a guard would inline (transitively) and
    /// whether any walked code calls a database-mutation builtin. A guard that
    /// embeds a snapshot AND can mutate the database is rejected — the
    /// clause-entry staleness test would be stale by the time the inlined
    /// snapshot runs.</summary>
    internal sealed class FailDirectExtras
    {
        public List<int>? DynFids;
        public bool DbMutation;
        public void AddDyn(int fid)
        {
            DynFids ??= new List<int>();
            if (!DynFids.Contains(fid)) DynFids.Add(fid);
        }
    }

    /// <summary>ADR-034 — builtins that mutate the dynamic clause store (the
    /// staleness-window set: none may run between a clause's entry test and
    /// its inlined snapshot code). <c>retract/1</c> is backtrackable and
    /// already rejected by the guard whitelist.</summary>
    private static bool IsDbMutationBuiltin(Shumway.Builtins.BuiltinEntry e)
        => (e.Name, e.Arity) is ("assert", 1) or ("asserta", 1) or ("assertz", 1)
            or ("retractall", 1) or ("abolish", 1) or ("abolish", 2)
            or ("consult", 1) or ("reconsult", 1) or ("restore_state", 1);

    /// <summary>ADR-031 recogniser — true when the clause byte range is a
    /// CP-free guard committing via a cut. Three tiers share one walk:
    ///
    /// <para><b>Tier A</b> — only <c>a_int_cmp</c> comparisons: non-binding,
    /// non-allocating, register-preserving. Guard failure branches DIRECTLY to
    /// the next clause with NO restore.</para>
    ///
    /// <para><b>Tier B</b> — additionally the head-unification / <c>=/2</c> op
    /// family: these can BIND and allocate → entry snapshot + restoring fail
    /// path (<see cref="CpFreeGuardInfo.NeedsSnapshot"/>).</para>
    ///
    /// <para><b>Tier G (guard calls)</b> — a FRAMED clause
    /// (<c>allocate_get_level; get_variable_y*; staging; call; cut slot</c>)
    /// whose every <c>Call</c> targets an INLINABLE single-clause leaf
    /// (<see cref="IsLeafPredicate"/> / <see cref="IsInlinableLeafRule"/>): the
    /// call is emitted INLINE (path, forced), so callee failure is a
    /// direct branch to the guard's fail label — fail-direct, no CP machinery.
    /// Call staging and the callee's body temps may write argument registers,
    /// so the clause saves/restores A0..arity-1
    /// (<see cref="CpFreeGuardInfo.NeedsRegSave"/>); the fail path deallocates
    /// the frame (<see cref="CpFreeGuardInfo.Framed"/>).</para></summary>
    internal static bool TryGetCpFreeGuard(
        byte[] code, int start, int end, int arity,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        IReadOnlyList<CallSite> callSites,
        out CpFreeGuardInfo info,
        bool analysisOnly = false,
        bool suppressStats = false)
    {
        info = default;
        bool snapshot = false, regSave = false, framed = false, sawRealOp = false;
        bool sawCall = false, sawFdCallee = false;
        var extras = new FailDirectExtras();           // ADR-034 collector
        int pc = start;

        // ADR-032 sizing — count a rejection only when the clause actually has
        // the commit-cut shape ahead (else this is just an ordinary clause).
        // suppressStats: the indexed-bucket census replays the recognizer
        // without perturbing the emission-driven counters.
        void CountReject(ref long counter, int fromPc)
        {
            if (suppressStats || !HasCutAhead(code, fromPc, end)) return;
            System.Threading.Interlocked.Increment(ref counter);
        }
        // The guard-op variant additionally records WHICH opcode rejected.
        void CountGuardOpReject(Opcode rejectedOp, int fromPc)
        {
            if (suppressStats || !HasCutAhead(code, fromPc, end)) return;
            System.Threading.Interlocked.Increment(ref CpFreeGuardStats.RejectGuardShape);
            System.Threading.Interlocked.Increment(
                ref CpFreeGuardStats.RejectGuardOpByOpcode[(byte)rejectedOp]);
        }
        void CountAccept()
        {
            if (suppressStats) return;
            if (sawCall)
                System.Threading.Interlocked.Increment(
                    ref sawFdCallee ? ref CpFreeGuardStats.TierG2 : ref CpFreeGuardStats.TierGLeaf);
            else if (snapshot)
                System.Threading.Interlocked.Increment(ref CpFreeGuardStats.TierB);
            else
                System.Threading.Interlocked.Increment(ref CpFreeGuardStats.TierA);
            if (extras.DynFids is { Count: > 0 })
                System.Threading.Interlocked.Increment(ref CpFreeGuardStats.AcceptWithDynSnapshot);
        }
        // ADR-034 — a guard that inlines a dynamic snapshot must not also be
        // able to MUTATE the database (the clause-entry staleness test would
        // be stale by the time the inlined code runs). Checked at the accept
        // point so both orders (mutate-then-call, call-then-mutate) reject.
        bool AcceptEmbeddedDynamics()
        {
            if (extras.DynFids is not { Count: > 0 } || !extras.DbMutation) return true;
            if (!suppressStats) CpFreeGuardStats.BumpShapeDetail("dyn+mutation");
            CountReject(ref CpFreeGuardStats.RejectCalleeShape, pc);
            return false;
        }

        while (pc < end)
        {
            var op = (Opcode)code[pc];
            switch (op)
            {
                case Opcode.Meta:                      // dbg-info — transparent
                    pc += 6;
                    continue;
                case Opcode.NeckCut:
                    if (!AcceptEmbeddedDynamics()) return false;
                    info = new CpFreeGuardInfo
                    {
                        CutPc = pc, DeepCut = false,
                        NeedsSnapshot = snapshot, NeedsRegSave = regSave, Framed = framed,
                        EmbeddedDynamicFids = extras.DynFids,
                    };
                    CountAccept();
                    return true;
                case Opcode.Cut:
                    if (!framed) return false;          // deep cut needs the frame's Y slot
                    if (!AcceptEmbeddedDynamics()) return false;
                    info = new CpFreeGuardInfo
                    {
                        CutPc = pc, DeepCut = true,
                        NeedsSnapshot = snapshot, NeedsRegSave = regSave, Framed = true,
                        EmbeddedDynamicFids = extras.DynFids,
                    };
                    CountAccept();
                    return true;
                case Opcode.AllocateGetLevel:
                case Opcode.Allocate:                  // framed neck-cut clause (no get_level)
                    if (sawRealOp || framed) return false;   // only as the clause's first real op
                    framed = true;
                    break;
                case Opcode.AIntCmp:
                    break;                             // non-binding compare
                // Binding / allocating unify ops — no register writes.
                case Opcode.GetAtom:
                case Opcode.GetInteger:
                case Opcode.GetNil:
                case Opcode.GetFloat:
                case Opcode.GetValueX:
                case Opcode.GetStructure:
                case Opcode.GetList:
                case Opcode.GetListA1:
                case Opcode.GetListA2:
                case Opcode.UnifyValueX:
                case Opcode.UnifyConstant:
                case Opcode.UnifyInteger:
                case Opcode.UnifyAtom:
                case Opcode.UnifyNil:
                case Opcode.UnifyVoid:
                case Opcode.UnifyFloat:
                case Opcode.UnifyBigInt:
                case Opcode.UnifyStructure:
                case Opcode.UnifyList:
                    snapshot = true;
                    break;
                // Frame-local Y moves / unifies (frame required).
                case Opcode.GetVariableY:              // Yn := Ai — frame write only
                    if (!framed) return false;
                    break;
                case Opcode.GetValueY:                 // unify(Yn, Ai) — binds
                    if (!framed) return false;
                    snapshot = true;
                    break;
                case Opcode.PutVariableY:              // fresh var → Yn AND Ai (call staging)
                    if (!framed) return false;
                    snapshot = true;                   // allocates the fresh heap var
                    regSave = true;                    // writes the argument register
                    break;
                // Register-writing moves: covered by the entry register save.
                case Opcode.GetVariableX:              // Xn := Ai
                case Opcode.UnifyVariableX:            // Xn := subterm
                case Opcode.PutValueX:                 // A(target) := Xn
                case Opcode.PutValueY:                 // A(target) := Yn
                case Opcode.PutAtom:
                case Opcode.PutInteger:
                case Opcode.PutNil:
                case Opcode.PutFloat:
                case Opcode.PutVariableX:
                case Opcode.PutStructureR:
                case Opcode.PutListR:
                // Compound-argument builds for the guard call: allocate heap
                // (snapshot's heap reset covers), write the argument register
                // (regSave covers), and set write mode for the following
                // unify_* ops (already whitelisted). No CP, no pre-existing
                // binding.
                case Opcode.PutStructure:
                case Opcode.PutList:
                case Opcode.PutPstr:
                    snapshot = true;                   // put_structure/put_variable allocate
                    regSave = true;
                    break;
                case Opcode.Call:
                {
                    // Tier G: the call must resolve to a callee the guard-slice
                    // emission inlines, making its failure a direct branch
                    // (fail-direct): an inlinable single-clause leaf (the
                    // inline path), or — G2 — a fail-direct multi-clause / self-tail-
                    // recursive predicate (sequential-chain inline). Anything
                    // else keeps the CP.
                    if (calleeMap is null)
                    {
                        CountReject(ref CpFreeGuardStats.RejectCalleeUnresolved, pc);
                        return false;
                    }
                    int fid = FindCallSiteFunctorId(callSites, pc);
                    // ANALYSIS-ONLY: a Call whose target is a registered
                    // BUILTIN — in LINKED bytecode this is already a
                    // CallBuiltin (the linker rewrite), so the
                    // emit sites never see it; the --cpfree sweep analyses
                    // UNLINKED bytecode, where the classification must match
                    // what the linked form would get. NOT enabled for emission:
                    // emitting an unlinked builtin Call as a guard would take
                    // the threaded-call path whose failure bypasses the stub.
                    if (analysisOnly && fid >= 0
                        && Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out int bid))
                    {
                        var bentry = Shumway.Builtins.BuiltinsRegistry.GetById(bid);
                        if (bentry.IsCall || bentry.IsDollarCall || bentry.IsBacktrackable)
                        {
                            CountGuardOpReject(Opcode.CallBuiltin, pc);
                            return false;
                        }
                        snapshot = true;
                        regSave = true;
                        break;
                    }
                    if (fid < 0 || !calleeMap.TryGetValue(fid, out var callee))
                    {
                        CountReject(ref CpFreeGuardStats.RejectCalleeUnresolved, pc);
                        return false;
                    }
                    // ADR-034 — a dynamic SNAPSHOT callee (ADR-023 bake): its
                    // truth can change at runtime, so inlining is allowed only
                    // for dynamics with rules (mutation-cold), and only with
                    // the clause-entry staleness test the collected fid
                    // triggers. Fact-only dynamics are the real assert
                    // targets — never caller-inlined.
                    if (callee.IsDynamicSnapshot)
                    {
                        if (!callee.SnapshotHasRules)
                        {
                            if (!suppressStats)
                                CpFreeGuardStats.BumpShapeDetail("dyn-snapshot-facts");
                            CountReject(ref CpFreeGuardStats.RejectCalleeShape, pc);
                            return false;
                        }
                        extras.AddDyn(callee.FunctorId);
                    }
                    if (IsLeafPredicate(callee) || IsInlinableLeafRule(callee))
                    {
                        sawCall = true;
                    }
                    else if (TryDescribeFailDirectCallee(callee, calleeMap, out var fdCls, out var fdReject, extras))
                    {
                        // SOUNDNESS — a MULTI-solution callee (overlapping
                        // clauses binding differently, or a cross-tail into a
                        // nondet target — even from a single clause): the
                        // sequential-chain inline commits to the first
                        // solution, so a fallible guard goal AFTER the call
                        // could never retry it. Sound when the callee is
                        // DETERMINISTIC (FailDirectCalleeIsDet — every
                        // non-last clause cut-commits AND every cross-tail
                        // target det) OR the call is IMMEDIATELY followed by
                        // the commit cut (nothing can fail back into it). No
                        // ClauseCount==1 shortcut: a single clause inherits a
                        // nondet cross-tail target's multiplicity.
                        if (!FailDirectCalleeIsDet(fdCls!)
                            && !NextRealOpIsCut(code, pc + OpcodeTable.Get(op).Size, end))
                        {
                            if (!suppressStats)
                                CpFreeGuardStats.BumpShapeDetail("multi-mid-guard");
                            CountReject(ref CpFreeGuardStats.RejectCalleeShape, pc);
                            return false;
                        }
                        sawCall = true;
                        sawFdCallee = true;
                    }
                    else
                    {
                        switch (fdReject)
                        {
                            case FailDirectReject.Caps:
                                CountReject(ref CpFreeGuardStats.RejectCalleeCaps, pc); break;
                            case FailDirectReject.Cut:
                                CountReject(ref CpFreeGuardStats.RejectCalleeCut, pc); break;
                            case FailDirectReject.HasCalls:
                                CountReject(ref CpFreeGuardStats.RejectCalleeCalls, pc); break;
                            default:
                                CountReject(ref CpFreeGuardStats.RejectCalleeShape, pc); break;
                        }
                        return false;
                    }
                    snapshot = true;                   // callee head unify binds
                    regSave = true;                    // staging + callee temps clobber
                    break;
                }
                case Opcode.CallBuiltin:
                {
                    // A deterministic, non-meta builtin guard (type test, ==/2,
                    // compare…): its IL emit already fails via a direct
                    // BranchIfFalse — fail-direct. Meta-call and backtrackable
                    // builtins need the CP machinery → reject.
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                        BytecodeIO.ReadInt32(code, pc + 1));
                    if (entry.IsCall || entry.IsDollarCall || entry.IsBacktrackable)
                    {
                        CountGuardOpReject(op, pc);
                        return false;
                    }
                    if (IsDbMutationBuiltin(entry))
                        extras.DbMutation = true;      // ADR-034 (see AcceptEmbeddedDynamics)
                    snapshot = true;                   // builtins may bind/allocate
                    regSave = true;                    // arg staging clobbers
                    break;
                }
                case Opcode.AIntBin:                   // X := A op B (is/2 fast lane)
                    snapshot = true;                   // may escalate/allocate
                    regSave = true;                    // writes the target register
                    break;
                default:
                    CountGuardOpReject(op, pc);
                    return false;
            }
            sawRealOp = true;
            pc += OpcodeTable.Get(op).Size;
        }
        return false;
    }

    /// <summary>Diagnostic — renders a functor id as <c>name/arity(fid)</c>
    /// for emit-time error messages (fids are process-local, useless alone).</summary>
    private static string DescribeFid(int fid)
    {
        try
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
            string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "?";
            return $"{name}/{arity}(fid={fid})";
        }
        catch
        {
            return $"fid={fid}";
        }
    }

    /// <summary>Stats classifier — does a dynamic predicate's compiled chain
    /// contain RULE bodies (vs. facts only)? The practical Arity model: a
    /// dynamic that already has rules with bodies (`:- visible` for
    /// findall/setof meta-call visibility) is never mutated at runtime, so it
    /// is a stable-dynamic inline candidate; fact-only dynamics are the real
    /// assert/retract targets. Scans the chain blob for body markers: frame
    /// allocation, body-arg staging (put_*), calls, arithmetic, cut. The
    /// ADR-015 chain tail's <c>call_builtin fail/0</c> stub and the internal
    /// body-dispatch <c>execute</c> of the indexed-dynamic layout (not a
    /// CallSite) are fact-compatible. A body-less tail-call rule
    /// (<c>p(X) :- q(X).</c>, execute recorded as a CallSite) counts as a
    /// rule.</summary>
    private static bool DynamicHasRuleBodies(CompiledPredicate pred)
    {
        // Link-time: the calleeMap holds a hollow trampoline (clauses live in
        // DynamicSeeds) — the census set fed from the warm engine's clause
        // store is the ground truth there. Runtime: the set is empty and the
        // bytecode scan below sees the real in-place chain with clause bodies.
        if (CpFreeGuardStats.DynamicFidsWithRules.ContainsKey(pred.FunctorId))
            return true;
        byte[] code = pred.BytecodeUnfused;
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            int size = OpcodeTable.Get(op).Size;
            if (size <= 0) return false;               // corrupt — don't classify
            switch (op)
            {
                case >= Opcode.PutVariableX and <= Opcode.PutBigInt:
                case Opcode.PutStructureR:
                case Opcode.PutListR:
                case Opcode.PutPstr:
                case Opcode.Allocate:
                case Opcode.AllocateGetLevel:
                case Opcode.Deallocate:
                case Opcode.DeallocateProceed:
                case Opcode.Call:
                case Opcode.CallIl:
                case Opcode.CallBytecode:
                case Opcode.ExecuteBuiltin:
                case Opcode.ExecuteIl:
                case Opcode.ExecuteBytecode:
                case Opcode.AEvalPush:
                case Opcode.AEvalBin:
                case Opcode.AEvalUn:
                case Opcode.AEvalIs:
                case Opcode.AEvalCmp:
                case Opcode.AIntBin:
                case Opcode.AIntCmp:
                case Opcode.Cut:
                case Opcode.NeckCut:
                case Opcode.GetLevel:
                case Opcode.CutDeallocateProceed:
                case Opcode.CutProceed:
                    return true;
                case Opcode.CallBuiltin:
                {
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                        BytecodeIO.ReadInt32(code, pc + 1));
                    if (entry.Name != "fail" || entry.Arity != 0) return true;
                    break;                             // ADR-015 fail stub
                }
                case Opcode.Execute:
                {
                    // A tail-call to another predicate is a rule body; the
                    // indexed-dynamic layout's internal body dispatch is not
                    // recorded as a CallSite.
                    foreach (var site in pred.CallSites)
                        if (site.OpcodeOffset == pc) return true;
                    break;
                }
            }
            pc += size;
        }
        return false;
    }

    /// <summary>ADR-031 G2 — one clause of a fail-direct callee (see
    /// <see cref="TryDescribeFailDirectCallee"/>).</summary>
    internal readonly struct FailDirectClause
    {
        public int Start { get; init; }
        /// <summary>pc of the terminator: <c>proceed</c>,
        /// <c>deallocate_proceed</c>, or the self-tail <c>execute</c>.</summary>
        public int TermPc { get; init; }
        /// <summary>Terminator is a self-tail <c>execute</c> — the inline
        /// emission loops back to the callee's inlined entry.</summary>
        public bool SelfTail { get; init; }
        /// <summary>The clause allocates an environment frame (its first real
        /// op is <c>allocate</c>) — mid-clause failure must deallocate before
        /// trying the next alternative.</summary>
        public bool Framed { get; init; }
        /// <summary>Terminator is the fused <c>deallocate_proceed</c> — the
        /// emit deallocates then joins.</summary>
        public bool DeallocProceed { get; init; }
        /// <summary>pc of the clause's FIRST top-level <c>neck_cut</c>, or -1.
        /// The cut commits the callee's clause selection: failures BEFORE it
        /// go to the next alternative, failures AFTER it exit the callee
        /// entirely. (In a fail-direct callee every cut is a neck cut — a deep
        /// cut implies a preceding call, which the shape excludes.)</summary>
        public int CutPc { get; init; }
        /// <summary>ADR-033 — the functor id of a CROSS-tail target (the clause
        /// ends <c>execute OTHER</c>), or -1. Only under the continuation
        /// mechanism: the terminator branches to the TARGET's shared copy,
        /// inheriting the continuations on the stack — sound because the
        /// clause has no remaining alternatives at the tail (last clause, or
        /// cut-committed — the same position rule as self-tail).</summary>
        public int CrossTailFid { get; init; }
        /// <summary>Whether the cross-tail target is itself deterministic —
        /// the target's multiplicity IS this clause's multiplicity, so the
        /// caller's det classification must fold it in (a committed clause
        /// selection does NOT commit the target's alternatives).</summary>
        public bool CrossTailDet { get; init; }
    }

    /// <summary>True when the described callee is DETERMINISTIC (at most one
    /// solution): every clause except the last carries a top-level cut, so
    /// whichever clause yields commits (the bytecode analogue of ADR-030's
    /// all-but-last-commit dispatch rule; the last clause's whitelist body
    /// yields at most once) — AND every cross-tail target is det (its
    /// solutions are the clause's solutions). A det callee may sit ANYWHERE in
    /// the guard — the multi-solution retry hazard needs a second solution to
    /// exist.</summary>
    internal static bool FailDirectCalleeIsDet(List<FailDirectClause> clauses)
    {
        for (int i = 0; i < clauses.Count - 1; i++)
            if (clauses[i].CutPc < 0) return false;
        foreach (var c in clauses)
            if (c.CrossTailFid >= 0 && !c.CrossTailDet) return false;
        return true;
    }

    /// <summary>ADR-032 sizing tooling (<c>shumway-disasm --cpfree</c>) — replays
    /// the CP-free guard recogniser over a predicate exactly as the two chain
    /// emit sites would, bumping <see cref="CpFreeGuardStats"/>. Indexed / single
    /// predicates don't participate in the CP-free path and are skipped, matching
    /// the shipped emission.</summary>
    public static void AnalyzeCpFreeGuards(
        CompiledPredicate pred, IReadOnlyDictionary<int, CompiledPredicate> calleeMap)
    {
        // Structural chain describe (not the memoized calleeMap-resolving one:
        // an intra-file map misses cross-file callees, which must land in the
        // recogniser's "unresolved" bucket rather than skip the predicate).
        TryDescribeTryMeElseChainStructural(pred, new List<int>(), out var chain);
        if (chain is null) return;
        var cls = chain.Clauses;
        for (int i = 0; i < cls.Count - 1; i++)
            TryGetCpFreeGuard(pred.BytecodeUnfused, cls[i].Start, cls[i].End,
                pred.Arity, calleeMap, pred.CallSites, out _, analysisOnly: true);
    }

    /// <summary>True when the next non-dbg opcode at <paramref name="pc"/> is the
    /// commit cut — the position constraint that makes a MULTI-clause fail-direct
    /// callee sound (see the soundness note in <c>TryGetCpFreeGuard</c>).</summary>
    private static bool NextRealOpIsCut(byte[] code, int pc, int end)
    {
        while (pc < end && (Opcode)code[pc] == Opcode.Meta) pc += 6;
        return pc < end && (Opcode)code[pc] is Opcode.NeckCut or Opcode.Cut;
    }

    /// <summary>ADR-032 sizing — true when a top-level commit cut
    /// (<c>neck_cut</c> / <c>cut</c>) appears ahead in the clause range: the
    /// clause IS the guard-commit shape, so a recogniser rejection is a real
    /// missed CP-free opportunity worth counting (an ordinary cut-less clause
    /// is not).</summary>
    private static bool HasCutAhead(byte[] code, int pc, int end)
    {
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            if (op is Opcode.NeckCut or Opcode.Cut) return true;
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get(op).Size;
            if (size <= 0) return false;
            pc += size;
        }
        return false;
    }

    /// <summary>Why <see cref="TryDescribeFailDirectCallee"/> rejected a callee
    /// (ADR-032 sizing — <see cref="CpFreeGuardStats"/>).</summary>
    internal enum FailDirectReject
    {
        None,
        /// <summary>Over the clause-count / byte-size caps — a CAP-raise
        /// candidate.</summary>
        Caps,
        /// <summary>A cut inside a callee clause — the static-widening
        /// candidate "callee-internal cuts".</summary>
        Cut,
        /// <summary>A user Call/Execute to another predicate in a callee body —
        /// the TRUE-G3 (nested inline) candidate population.</summary>
        HasCalls,
        /// <summary>Anything else: control constructs, backtrackable builtins,
        /// unrecognised clause ranges, non-whitelist ops.</summary>
        Shape,
    }

    /// <summary>ADR-031 G2 — true when <paramref name="callee"/> is a
    /// FAIL-DIRECT predicate: its whole execution provably creates NO engine
    /// choice point and every failure path is (in the inlined emission) a
    /// direct IL branch. Requirements per clause: frameless, or a frame whose
    /// <c>allocate</c> is the first real op and whose <c>deallocate</c>
    /// immediately precedes the terminator; body ops restricted to the
    /// non-CP whitelist (head unification / <c>=/2</c> family, integer
    /// arithmetic, register moves, deterministic non-meta builtins — NO user
    /// calls, NO cuts, NO control constructs); terminator <c>proceed</c> /
    /// <c>deallocate_proceed</c> / a self-tail <c>execute</c> (det tail
    /// recursion — the canonical list-walking validator). Clause dispatch is
    /// IGNORED (the inline emission is a sequential alternative chain, so the
    /// callee's own try/switch machinery — which WOULD push CPs — never runs).
    /// This is the bytecode-level counterpart of ADR-030's determinism proof,
    /// strengthened to "emits zero choice points". Capped (clauses ≤ 4, code ≤
    /// 512 bytes) to bound inline growth.</summary>
    internal static bool TryDescribeFailDirectCallee(
        CompiledPredicate callee, out List<FailDirectClause>? clauses)
        => TryDescribeFailDirectCallee(callee, null, out clauses, out _);

    internal static bool TryDescribeFailDirectCallee(
        CompiledPredicate callee, out List<FailDirectClause>? clauses,
        out FailDirectReject reject)
        => TryDescribeFailDirectCallee(callee, null, out clauses, out reject);

    /// <summary>ADR-031 G2 fail-direct caps — a callee over these bounds keeps
    /// its choice point. Prudence bounds (per-site inline growth + the linear
    /// alternative chain replacing indexed dispatch), NOT soundness bounds:
    /// raising them is safe, it just inlines more code and scans more
    /// alternatives per call. <see cref="CpFreeGuardStats.RejectCalleeCaps"/>
    /// counts the population a raise would admit. NOTE (user directive,
    /// recorded in ADR-031): raising <see cref="FailDirectMaxClauses"/> must
    /// come with a proper IL switch emission, never a wider linear chain.</summary>
    internal static int FailDirectMaxClauses { get; set; } = 4;
    internal static int FailDirectMaxBytes { get; set; } = 512;

    /// <summary>ADR-031 G3 — the TOTAL bytecode budget across a nested
    /// fail-direct inline (the guard callee plus every transitively inlined
    /// inner callee, per site). Bounds the compounding code growth of the
    /// nesting; the per-callee caps above still apply at every level.</summary>
    internal static int FailDirectMaxTotalBytes { get; set; } = 1536;

    /// <summary>G3 entry — with a <paramref name="calleeMap"/>, callee bodies
    /// may CALL other predicates when each inner callee is itself
    /// leaf-inlinable or fail-direct (recursively; DAG only — a visited set
    /// rejects mutual recursion) AND deterministic or immediately followed by
    /// the clause's commit cut (the nested multi-solution rule).</summary>
    internal static bool TryDescribeFailDirectCallee(
        CompiledPredicate callee,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out List<FailDirectClause>? clauses,
        out FailDirectReject reject,
        FailDirectExtras? extras = null)
    {
        // visiting maps each on-path fid to the number of NON-TAIL edges on
        // the path when it was entered — a tail-cycle back-edge is sound only
        // when the whole cycle segment is tail edges (counts equal), which
        // also makes the describe ENTRY-POINT-INDEPENDENT for cyclic SCCs
        // (a mixed cycle rejects from every entry; the emit re-describes from
        // a different node than the recognizer validated).
        var visiting = new Dictionary<int, int> { [callee.FunctorId] = 0 };
        int budget = FailDirectMaxTotalBytes - callee.BytecodeUnfused.Length;
        return DescribeFailDirectCore(
            callee, calleeMap, visiting, 0, ref budget, out clauses, out reject, extras);
    }

    private static bool DescribeFailDirectCore(
        CompiledPredicate callee,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        Dictionary<int, int> visiting, int nonTailCount, ref int budget,
        out List<FailDirectClause>? clauses,
        out FailDirectReject reject,
        FailDirectExtras? extras = null)
    {
        clauses = null;
        reject = FailDirectReject.None;
        byte[] code = callee.BytecodeUnfused;
        if (callee.ClauseCount < 1 || callee.ClauseCount > FailDirectMaxClauses
            || code.Length > FailDirectMaxBytes)
        {
            reject = FailDirectReject.Caps;
            return false;
        }

        // Clause byte ranges, dispatch-skeleton-free. STRUCTURAL chain describe
        // (not the memoized calleeMap-resolving one): a chain with Call sites
        // fails the memo's resolve against a null/partial map, which would
        // misclassify chains-with-calls as "ranges" — this walk validates every
        // Call itself (the G3 rules).
        IReadOnlyList<(int Start, int End)> ranges;
        if (callee.ClauseCount == 1)
        {
            ranges = new[] { (0, code.Length) };
        }
        else if (TryDescribeTryMeElseChainStructural(callee, new List<int>(), out var chain)
                 && chain is not null)
        {
            ranges = chain.Clauses.Select(c => (c.Start, c.End)).ToArray();
        }
        else if (IlIndexedDispatch.TryDescribe(callee, static (_, _) => true, out var idx)
                 && idx is not null)
        {
            ranges = idx.Clauses;
        }
        else
        {
            CpFreeGuardStats.BumpShapeDetail("ranges");
            if (System.Environment.GetEnvironmentVariable("SHUMWAY_CPFREE_DETAIL") == "1")
            {
                var (atomId, ar) = FunctorTable.Lookup(callee.FunctorId);
                CpFreeGuardStats.BumpShapeDetail(
                    $"ranges:{AtomTable.GetById(atomId)?.Name}/{ar}");
            }
            reject = FailDirectReject.Shape;
            return false;
        }

        var result = new List<FailDirectClause>(ranges.Count);
        foreach (var (start, end) in ranges)
        {
            bool framed = false, sawRealOp = false;
            int pc = start;
            int termPc = -1, cutPc = -1;
            int crossTailFid = -1;
            bool crossTailDet = false;
            bool selfTail = false, deallocProceed = false;
            while (pc < end)
            {
                var op = (Opcode)code[pc];
                if (op == Opcode.Meta) { pc += 6; continue; }
                if (op == Opcode.Proceed) { termPc = pc; break; }
                if (op == Opcode.DeallocateProceed)
                {
                    if (!framed) { CpFreeGuardStats.BumpShapeDetail("frame"); reject = FailDirectReject.Shape; return false; }
                    termPc = pc; deallocProceed = true; break;
                }
                if (op == Opcode.Execute)
                {
                    int fid = FindCallSiteFunctorId(callee.CallSites, pc);
                    if (fid == callee.FunctorId)
                    {
                        termPc = pc; selfTail = true; break;
                    }
                    // ADR-033 — a CROSS tail: acceptable under the continuation
                    // mechanism when the target is itself leaf/fail-direct
                    // (recursive describe). The target's det-ness is recorded
                    // for the caller's multiplicity (FailDirectCalleeIsDet).
                    if (!CpFreeGuardContinuations || calleeMap is null
                        || !calleeMap.TryGetValue(fid, out var tailTgt))
                    {
                        CpFreeGuardStats.BumpShapeDetail("g3:cross-tail");
                        reject = FailDirectReject.HasCalls;      // cross tail — G3 candidate
                        return false;
                    }
                    // ADR-034 — a dynamic-snapshot target: only one with rules,
                    // and the caller clause carries its staleness test.
                    if (tailTgt.IsDynamicSnapshot && !tailTgt.SnapshotHasRules)
                    {
                        CpFreeGuardStats.BumpShapeDetail("dyn-snapshot-facts");
                        reject = FailDirectReject.HasCalls;
                        return false;
                    }
                    // ADR-033 deep G3 — a TAIL CYCLE (mutual tail recursion,
                    // the even/odd idiom): the target is already on the
                    // describe path, so it has (or will have) its own shared
                    // copy — the emit is a plain `br` into it, inheriting the
                    // continuations (LCO; nothing pushed, O(1) stack). Sound
                    // with per-copy IL locals ONLY when the WHOLE cycle
                    // segment is tail edges (no non-tail edge since the
                    // target was entered — counts equal): the position rule
                    // (last-clause-or-cut-committed) then forfeits every
                    // abandoned activation's alternatives, so its entry marks
                    // are dead when the next activation of the same copy
                    // overwrites them. A MIXED cycle (a non-tail edge inside,
                    // e.g. A -Call-> B -Execute-> A) nests activations of the
                    // same copy → IL-local clobber → rejected (the case-3
                    // frame machinery would be needed). Det is unknown at the
                    // cycle edge → conservative FALSE.
                    if (visiting.TryGetValue(fid, out int tgtEntryNt))
                    {
                        if (tgtEntryNt != nonTailCount)
                        {
                            CpFreeGuardStats.BumpShapeDetail("g3:cycle-mixed");
                            reject = FailDirectReject.HasCalls;
                            return false;
                        }
                        termPc = pc;
                        crossTailFid = fid;
                        crossTailDet = false;
                        break;
                    }
                    visiting[fid] = nonTailCount;
                    bool tgtOk, tgtDet = false;
                    try
                    {
                        // Det via FailDirectCalleeIsDet in BOTH branches — no
                        // ClauseCount==1 shortcut: a single-clause target whose
                        // body itself cross-tails a NONDET target inherits that
                        // multiplicity (the det check follows CrossTailDet).
                        if (IsLeafPredicate(tailTgt) || IsInlinableLeafRule(tailTgt))
                        {
                            // Leaves still need a describable copy — run the
                            // core describe (single-clause leaves pass it).
                            // Tail edge: nonTailCount unchanged.
                            tgtOk = DescribeFailDirectCore(tailTgt, calleeMap,
                                visiting, nonTailCount, ref budget,
                                out var leafCls, out _, extras);
                            tgtDet = tgtOk && FailDirectCalleeIsDet(leafCls!);
                        }
                        else
                        {
                            // ADR-033 deep G3 — FRESH budget: the target is ONE
                            // shared copy per method, not per-site duplication,
                            // so the cumulative budget does not apply (the
                            // per-callee caps inside the describe still bound
                            // each copy). Tail edge: nonTailCount unchanged.
                            int tgtBudget = FailDirectMaxTotalBytes
                                - tailTgt.BytecodeUnfused.Length;
                            List<FailDirectClause>? tgtCls = null;
                            tgtOk = tgtBudget >= 0
                                && DescribeFailDirectCore(tailTgt, calleeMap,
                                    visiting, nonTailCount, ref tgtBudget,
                                    out tgtCls, out _, extras);
                            if (tgtOk)
                                tgtDet = FailDirectCalleeIsDet(tgtCls!);
                        }
                    }
                    finally
                    {
                        visiting.Remove(fid);
                    }
                    if (tgtOk && tailTgt.IsDynamicSnapshot)
                        extras?.AddDyn(tailTgt.FunctorId);
                    if (!tgtOk)
                    {
                        CpFreeGuardStats.BumpShapeDetail("g3:cross-tail");
                        reject = FailDirectReject.HasCalls;
                        return false;
                    }
                    termPc = pc;
                    crossTailFid = fid;
                    crossTailDet = tgtDet;
                    break;
                }
                switch (op)
                {
                    case Opcode.Call:
                    {
                        // G3 — a non-tail call to ANOTHER predicate is
                        // acceptable when the inner callee is itself
                        // leaf-inlinable or fail-direct (recursive describe;
                        // the visiting set rejects mutual recursion) AND
                        // deterministic or immediately followed by this
                        // clause's commit cut (nested multi-solution rule),
                        // within the total inline budget.
                        if (calleeMap is null)
                        {
                            CpFreeGuardStats.BumpShapeDetail("g3:no-map");
                            reject = FailDirectReject.HasCalls;
                            return false;
                        }
                        int ifid = FindCallSiteFunctorId(callee.CallSites, pc);
                        if (ifid < 0 || !calleeMap.TryGetValue(ifid, out var inner))
                        {
                            CpFreeGuardStats.BumpShapeDetail("g3:unresolved");
                            reject = FailDirectReject.HasCalls;
                            return false;
                        }
                        if (!visiting.TryAdd(ifid, nonTailCount + 1))   // cycle = mutual recursion
                        {
                            // ADR-033 deep G3 v1 — NON-tail cycles stay
                            // rejected even under continuations: a re-entered
                            // copy's IL locals (entry marks, saved registers)
                            // would clobber the outer activation's, and a
                            // later goal in the outer alternative could then
                            // under-restore. Sound support needs real frames
                            // (marks + registers pushed per activation) —
                            // deferred; the distinct label measures the true
                            // residual (the plain g3:cycle count undercounts:
                            // upper levels report as g3:inner-calls).
                            CpFreeGuardStats.BumpShapeDetail(
                                CpFreeGuardContinuations ? "g3:cycle-nontail" : "g3:cycle");
                            reject = FailDirectReject.HasCalls;
                            return false;
                        }
                        bool innerOk;
                        string g3Detail = "g3:inner-shape";
                        try
                        {
                            // ADR-034 — a dynamic-SNAPSHOT inner: only one with
                            // rules (fact-only dynamics are real assert
                            // targets); an accepted one is collected below so
                            // the top-level clause carries its staleness test.
                            if (inner.IsDynamicSnapshot && !inner.SnapshotHasRules)
                            {
                                innerOk = false;
                                g3Detail = "dyn-snapshot-facts";
                            }
                            else if (IsLeafPredicate(inner) || IsInlinableLeafRule(inner))
                            {
                                innerOk = true;                  // single-clause → det
                            }
                            else if ((Opcode)inner.BytecodeUnfused[0] == Opcode.EnterDynamic)
                            {
                                // A dynamic inner can never be statically
                                // inlined (its clauses change at runtime) —
                                // classify without recursing (the recursion
                                // would pollute the inner-reject counters).
                                // Split by clause shape: a dynamic with rules
                                // (:- visible for findall/setof visibility, the
                                // Arity idiom) is mutation-cold in practice —
                                // the stable-dynamic fast-path candidate pool.
                                // (Inlining an EMPTY dynamic as fail was
                                // measured and REJECTED — see the note at
                                // CpFreeGuardContinuations.)
                                innerOk = false;
                                g3Detail = DynamicHasRuleBodies(inner)
                                    ? "g3:inner-dynamic-rules"
                                    : "g3:inner-dynamic-facts";
                            }
                            else
                            {
                                int innerLen = inner.BytecodeUnfused.Length;
                                // ADR-033 deep G3 — under continuations the
                                // inner is ONE shared copy per method, not a
                                // per-site duplication: the cumulative budget
                                // does not apply — each copy gets a FRESH one
                                // (the per-callee caps inside the describe
                                // still bound every copy individually). The
                                // duplication path keeps the shared budget.
                                int freshBudget = FailDirectMaxTotalBytes - innerLen;
                                if (!CpFreeGuardContinuations) budget -= innerLen;
                                ref int innerBudget = ref CpFreeGuardContinuations
                                    ? ref freshBudget : ref budget;
                                if (innerBudget < 0)
                                {
                                    innerOk = false;
                                    g3Detail = "g3:budget";
                                }
                                else if (!DescribeFailDirectCore(inner, calleeMap,
                                             visiting, nonTailCount + 1,   // non-tail edge
                                             ref innerBudget, out var innerCls,
                                             out var innerReject, extras))
                                {
                                    innerOk = false;
                                    g3Detail = innerReject switch
                                    {
                                        FailDirectReject.Caps => "g3:inner-caps",
                                        FailDirectReject.HasCalls => "g3:inner-calls",
                                        _ => "g3:inner-shape",
                                    };
                                }
                                // No ClauseCount==1 shortcut: a single-clause
                                // inner cross-tailing a NONDET target inherits
                                // its multiplicity (FailDirectCalleeIsDet
                                // follows CrossTailDet).
                                else if (!(FailDirectCalleeIsDet(innerCls!)
                                           || NextRealOpIsCut(
                                               code, pc + OpcodeTable.Get(op).Size, end)))
                                {
                                    innerOk = false;
                                    g3Detail = "g3:nondet-mid";
                                }
                                else
                                {
                                    innerOk = true;
                                }
                            }
                        }
                        finally
                        {
                            visiting.Remove(ifid);
                        }
                        if (!innerOk)
                        {
                            CpFreeGuardStats.BumpShapeDetail(g3Detail);
                            reject = FailDirectReject.HasCalls;
                            return false;
                        }
                        if (inner.IsDynamicSnapshot)
                            extras?.AddDyn(inner.FunctorId);     // ADR-034
                        break;
                    }
                    case Opcode.CallIl:
                    case Opcode.CallBytecode:
                    case Opcode.ExecuteIl:
                    case Opcode.ExecuteBytecode:
                        CpFreeGuardStats.BumpShapeDetail("g3:exec-variant");
                        reject = FailDirectReject.HasCalls;      // G3 candidate
                        return false;
                    case Opcode.NeckCut:
                    case Opcode.Cut:
                        // The callee-internal commit — record the FIRST one
                        // (selection is committed from there on; later cuts are
                        // flush-only no-ops the emit handles inline). A DEEP
                        // cut (after inlined calls) gets the same flush-only
                        // split: the inlined inner callees push no choice
                        // points, so there is nothing for an engine cut to
                        // prune; the GetLevel-captured barrier is never
                        // consumed.
                        if (op == Opcode.Cut && !framed)
                        { CpFreeGuardStats.BumpShapeDetail("frame"); reject = FailDirectReject.Shape; return false; }
                        if (cutPc < 0) cutPc = pc;
                        break;
                    case Opcode.GetLevel:
                        // Writes the cut barrier into a Y slot — harmless here
                        // (the flush-only cut emission never reads it back).
                        if (!framed) { CpFreeGuardStats.BumpShapeDetail("frame"); reject = FailDirectReject.Shape; return false; }
                        break;
                    case Opcode.Allocate:
                    case Opcode.AllocateGetLevel:      // deep-cut framed opener
                        if (sawRealOp || framed) { CpFreeGuardStats.BumpShapeDetail("frame"); reject = FailDirectReject.Shape; return false; }
                        framed = true;
                        break;
                    case Opcode.Deallocate:
                        // Only as part of the tail sequence: deallocate must be
                        // immediately followed by the (self) execute.
                        if (!framed) { CpFreeGuardStats.BumpShapeDetail("frame"); reject = FailDirectReject.Shape; return false; }
                        if ((Opcode)code[pc + OpcodeTable.Get(op).Size] != Opcode.Execute)
                        { CpFreeGuardStats.BumpShapeDetail("frame"); reject = FailDirectReject.Shape; return false; }
                        break;
                    case Opcode.AIntCmp:
                    case Opcode.AIntBin:
                    case Opcode.GetAtom:
                    case Opcode.GetInteger:
                    case Opcode.GetNil:
                    case Opcode.GetFloat:
                    case Opcode.GetValueX:
                    case Opcode.GetStructure:
                    case Opcode.GetList:
                    case Opcode.GetListA1:
                    case Opcode.GetListA2:
                    case Opcode.GetVariableX:
                    case Opcode.UnifyValueX:
                    case Opcode.UnifyVariableX:
                    case Opcode.UnifyConstant:
                    case Opcode.UnifyInteger:
                    case Opcode.UnifyAtom:
                    case Opcode.UnifyNil:
                    case Opcode.UnifyVoid:
                    case Opcode.UnifyFloat:
                    case Opcode.UnifyBigInt:
                    case Opcode.UnifyStructure:
                    case Opcode.UnifyList:
                    case Opcode.PutValueX:
                    case Opcode.PutAtom:
                    case Opcode.PutInteger:
                    case Opcode.PutNil:
                    case Opcode.PutFloat:
                    case Opcode.PutVariableX:
                    case Opcode.PutStructureR:
                    case Opcode.PutListR:
                    case Opcode.PutStructure:
                    case Opcode.PutList:
                    case Opcode.PutPstr:
                        break;
                    case Opcode.GetVariableY:
                    case Opcode.GetValueY:
                    case Opcode.UnifyVariableY:
                    case Opcode.UnifyValueY:
                    case Opcode.PutValueY:
                    case Opcode.PutVariableY:
                        if (!framed) { CpFreeGuardStats.BumpShapeDetail("frame"); reject = FailDirectReject.Shape; return false; }
                        break;
                    case Opcode.CallBuiltin:
                    {
                        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                            BytecodeIO.ReadInt32(code, pc + 1));
                        if (entry.IsCall || entry.IsDollarCall || entry.IsBacktrackable)
                        {
                            CpFreeGuardStats.BumpShapeDetail(
                                $"builtin:{entry.Name}/{entry.Arity}");
                            reject = FailDirectReject.Shape;
                            return false;
                        }
                        if (IsDbMutationBuiltin(entry) && extras is not null)
                            extras.DbMutation = true;            // ADR-034
                        break;
                    }
                    case Opcode.EnterDynamic:
                        // The callee itself is dynamic (direct guard call to a
                        // dynamic predicate). Same rules/facts split as the
                        // inner-dynamic case — dynamics with rules are the
                        // stable-dynamic fast-path candidate pool.
                        CpFreeGuardStats.BumpShapeDetail(DynamicHasRuleBodies(callee)
                            ? "op:EnterDynamic-rules" : "op:EnterDynamic-facts");
                        reject = FailDirectReject.Shape;
                        return false;
                    default:
                        CpFreeGuardStats.BumpShapeDetail($"op:{op}");
                        reject = FailDirectReject.Shape;
                        return false;
                }
                sawRealOp = true;
                pc += OpcodeTable.Get(op).Size;
            }
            if (termPc < 0) { CpFreeGuardStats.BumpShapeDetail("no-term"); reject = FailDirectReject.Shape; return false; }
            result.Add(new FailDirectClause
            {
                Start = start, TermPc = termPc,
                SelfTail = selfTail, Framed = framed, DeallocProceed = deallocProceed,
                CutPc = cutPc,
                CrossTailFid = crossTailFid, CrossTailDet = crossTailDet,
            });
        }
        // SOUNDNESS — a tail transfer (self-recursion OR a cross-tail) in a
        // NON-LAST clause without a preceding cut: if the transferred-to code
        // fails, real backtracking returns to THIS clause's remaining
        // alternatives, which neither the in-place loop nor the inherited
        // continuation can do. Sound only when the tail clause is the last
        // (no alternatives after it) or its cut committed the selection first.
        for (int i = 0; i < result.Count - 1; i++)
        {
            if ((result[i].SelfTail || result[i].CrossTailFid >= 0)
                && result[i].CutPc < 0)
            {
                CpFreeGuardStats.BumpShapeDetail("selftail-pos");
                reject = FailDirectReject.Shape;
                return false;
            }
        }
        clauses = result;
        return true;
    }

    /// <summary>ADR-031 G2 — inlines a fail-direct callee at a CP-free guard
    /// call site as a SEQUENTIAL alternative chain with an in-place self-tail
    /// loop. Clause i's failure branches to clause i+1 (restoring the callee's
    /// entry argument registers first — a partially-matched clause may have
    /// clobbered them via <c>unify_variable_x</c>/staging); the last clause's
    /// failure branches to <paramref name="outerFail"/> (the guard's restore
    /// stub). A framed clause's mid-body failure detours through a
    /// deallocate-then-fail stub. A self-tail <c>execute</c> becomes a branch
    /// back to the inlined entry (its staging + deallocate already ran inside
    /// the slice) with a throttled cancellation poll — but NO heap-GC safe
    /// point: a collection would move the heap under the enclosing guard's
    /// snapshot locals, so allocation during the walk grows the heap until the
    /// guard exits (same acceptance as tier B).</summary>
    private static void EmitFailDirectCalleeInline(
        Sigil.Emit<PredicateDelegate> emit, CompiledPredicate callee,
        List<FailDirectClause> fdClauses, Sigil.Label outerFail,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, string salt,
        GuardContEmitContext? gcCtx = null)
    {
        int arity = callee.Arity;
        var join = emit.DefineLabel($"fd_join{salt}");
        var entry = emit.DefineLabel($"fd_entry{salt}");
        var argSaves = new Sigil.Local[arity];
        for (int r = 0; r < arity; r++)
            argSaves[r] = emit.DeclareLocal<Cell>($"fd_a{r}{salt}");
        // Callee-entry trail/heap marks: a partially-matched clause may have
        // BOUND caller-visible terms (head unification with unbound arguments)
        // before failing — the next alternative must see them undone, exactly
        // as the clause choice point's restore would have done. (The enclosing
        // guard's snapshot covers the whole clause; these marks cover just the
        // callee, so guard bindings made BEFORE the call survive.)
        var mBt = emit.DeclareLocal<int>($"fd_bt{salt}");
        var mXt = emit.DeclareLocal<int>($"fd_xt{salt}");
        var mH = emit.DeclareLocal<int>($"fd_h{salt}");
        var mHb = emit.DeclareLocal<int>($"fd_hb{salt}");
        int k = fdClauses.Count;
        var altLabels = new Sigil.Label[k + 1];
        for (int i = 0; i < k; i++)
            altLabels[i] = emit.DefineLabel($"fd_alt{i}{salt}");
        altLabels[k] = outerFail;

        bool anySelfTail = fdClauses.Any(c => c.SelfTail);
        emit.MarkLabel(entry);
        if (anySelfTail)
        {
            // Cancellation poll at the loop head (throttled field read).
            emit.LoadArgument(0);
            emit.Call(EngineBacktrackSafePointMethod);
        }
        for (int r = 0; r < arity; r++)
        {
            emit.LoadArgument(0);
            emit.LoadConstant(r);
            emit.Call(EngineGetRegisterMethod);
            emit.StoreLocal(argSaves[r]);
        }
        emit.LoadArgument(0); emit.Call(EngineBindingTrailTopGetter); emit.StoreLocal(mBt);
        emit.LoadArgument(0); emit.Call(EngineExtraTrailTopGetter); emit.StoreLocal(mXt);
        emit.LoadArgument(0); emit.Call(EngineHeapTopGetter); emit.StoreLocal(mH);
        // NESTED HB raise: the guard's own staging creates fresh vars AFTER the
        // guard-level raise (put_variable_y outputs) — young w.r.t. the guard's
        // HB, so a callee binding them would go UNTRAILED and survive the
        // per-alternative untrail. Raising HB again to the CALLEE-entry heap
        // top makes every pre-callee term old; restored at the join.
        emit.LoadArgument(0); emit.Call(EngineBeginIlGuardMethod); emit.StoreLocal(mHb);

        byte[] code = callee.BytecodeUnfused;
        for (int i = 0; i < k; i++)
        {
            var c = fdClauses[i];
            emit.MarkLabel(altLabels[i]);
            if (i > 0)
            {
                // Undo the previous alternative's partial work: untrail to the
                // callee-entry marks (head-unify bindings!), reset the heap,
                // clear wakeups its bindings queued, then restore the entry
                // argument registers it may have clobbered. HB stays at the
                // RAISED callee boundary (mH) — the next alternative's bindings
                // must trail too.
                emit.LoadArgument(0);
                emit.LoadLocal(mBt); emit.LoadLocal(mXt); emit.LoadLocal(mH); emit.LoadLocal(mH);
                emit.Call(EngineFailIlGuardMethod);
                for (int r = 0; r < arity; r++)
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(r);
                    emit.LoadLocal(argSaves[r]);
                    emit.Call(EngineSetRegisterMethod);
                }
            }

            // Fail routing. Pre-cut: the next alternative (via a deallocating
            // stub when framed). Post-cut: clause selection is COMMITTED — the
            // callee fails outright (via its own deallocating stub when framed).
            Sigil.Label preCutFail = altLabels[i + 1];
            Sigil.Label? deallocFail = null;
            if (c.Framed)
            {
                deallocFail = emit.DefineLabel($"fd_df{i}{salt}");
                preCutFail = deallocFail;
            }

            if (c.CutPc >= 0)
            {
                // Slice 1 — up to the committing neck cut.
                EmitClauseBody(emit, code, c.Start, c.CutPc,
                    preCutFail, callee.CallSites, calleeMap: calleeMap,
                    suppressProceedReturn: true, forceLeafRuleInline: true, localSalt: $"{salt}_c{i}a", guardContCtx: gcCtx);
                // The cut: a goal boundary (flush pending wakeups; a failing
                // hook backtracks into the next alternative, pre-commit) — but
                // NO engine Cut call: a fail-direct callee pushed nothing.
                emit.LoadArgument(0);
                emit.Call(EngineFlushWakeupsForIlCutMethod);
                emit.BranchIfFalse(preCutFail);
                // Slice 2 — post-commit: failures exit the callee.
                Sigil.Label committedFail = outerFail;
                if (c.Framed)
                {
                    var df2 = emit.DefineLabel($"fd_dfc{i}{salt}");
                    committedFail = df2;
                    EmitClauseBody(emit, code, c.CutPc + OpcodeTable.Get((Opcode)code[c.CutPc]).Size, c.TermPc,
                        committedFail, callee.CallSites, calleeMap: calleeMap,
                        suppressProceedReturn: true, forceLeafRuleInline: true, localSalt: $"{salt}_c{i}b", guardContCtx: gcCtx);
                    EmitFailDirectTerminator(emit, c, entry, join, gcCtx, calleeMap);
                    emit.MarkLabel(df2);
                    emit.LoadArgument(0);
                    emit.Call(EngineDeallocateMethod);
                    emit.Branch(outerFail);
                }
                else
                {
                    EmitClauseBody(emit, code, c.CutPc + OpcodeTable.Get((Opcode)code[c.CutPc]).Size, c.TermPc,
                        committedFail, callee.CallSites, calleeMap: calleeMap,
                        suppressProceedReturn: true, forceLeafRuleInline: true, localSalt: $"{salt}_c{i}b", guardContCtx: gcCtx);
                    EmitFailDirectTerminator(emit, c, entry, join, gcCtx, calleeMap);
                }
            }
            else
            {
                EmitClauseBody(emit, code, c.Start, c.TermPc,
                    preCutFail, callee.CallSites, calleeMap: calleeMap,
                    suppressProceedReturn: true, forceLeafRuleInline: true, localSalt: $"{salt}_c{i}", guardContCtx: gcCtx);
                EmitFailDirectTerminator(emit, c, entry, join, gcCtx, calleeMap);
            }

            if (deallocFail is not null)
            {
                emit.MarkLabel(deallocFail);
                emit.LoadArgument(0);
                emit.Call(EngineDeallocateMethod);
                emit.Branch(altLabels[i + 1]);
            }
        }
        emit.MarkLabel(join);
        // Success: drop the nested HB raise back to the guard-level boundary.
        // (The failure exits skip this — the outer restore stub reinstates the
        // clause-entry HB itself.)
        emit.LoadArgument(0);
        emit.LoadLocal(mHb);
        emit.Call(EngineCommitIlGuardMethod);
    }

    /// <summary>The terminator of one inlined fail-direct clause: rejoin the
    /// guard (<c>proceed</c> / <c>deallocate_proceed</c>), loop (self-tail),
    /// or — ADR-033 — branch to a cross-tail target's shared copy, inheriting
    /// the continuations on the stack (tail-call composition).</summary>
    private static void EmitFailDirectTerminator(
        Sigil.Emit<PredicateDelegate> emit, FailDirectClause c,
        Sigil.Label entry, Sigil.Label join,
        GuardContEmitContext? gcCtx = null,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        if (c.DeallocProceed)
        {
            emit.LoadArgument(0);
            emit.Call(EngineDeallocateMethod);
            emit.Branch(join);
        }
        else if (c.SelfTail)
        {
            emit.Branch(entry);          // staging + deallocate already in the slice
        }
        else if (c.CrossTailFid >= 0)
        {
            // Only reachable under the continuation mechanism (the describe
            // gates cross-tails on CpFreeGuardContinuations).
            if (gcCtx is null || calleeMap is null
                || !calleeMap.TryGetValue(c.CrossTailFid, out var tailTgt))
                throw new InvalidOperationException(
                    "cross-tail clause emitted without a continuation context");
            emit.Branch(GetOrAddGuardContCopy(emit, gcCtx, c.CrossTailFid, tailTgt));
        }
        else
        {
            emit.Branch(join);           // proceed
        }
    }

    /// <summary>ADR-031 — emits one whole CP-free guard clause, replacing the
    /// entry <c>PushIlChoicePoint</c> + guard + <c>neck_cut</c> + body chain
    /// emission. <paramref name="emitSlice"/> abstracts the two call sites'
    /// differing <c>EmitClauseBody</c> parameter sets: it must emit the byte
    /// range <c>[start, end)</c> with the given fail label.
    ///
    /// <para><b>Tier A</b> (<paramref name="needsSnapshot"/> = false — pure
    /// comparisons): guard failure branches DIRECTLY to
    /// <paramref name="nextClauseLabel"/>; nothing to restore. <b>Tier B</b>
    /// (binding guard): clause entry snapshots the two trail tops + heap top in
    /// IL locals and <see cref="Activation.BeginIlGuard"/> raises HB so every guard
    /// binding is trailed; guard failure lands on a restore stub
    /// (<see cref="Activation.FailIlGuard"/> — untrail, heap reset, HB restore,
    /// wakeup clear) before branching to the next clause.</para>
    ///
    /// <para><b>Commit</b> (both tiers): fast path (no pending attribute
    /// wakeups — every non-attvar program) is just <c>engine.NeckCut()</c>, a
    /// runtime no-op unless self-tail-loop body CPs exist (where it must prune
    /// exactly as today), plus the HB restore for tier B. Rare path: wakeups
    /// pend at the cut and a failing hook must have a clause choice point to
    /// backtrack into — the SKIPPED CP is materialised lazily here (tier B via
    /// <see cref="Activation.PushIlChoicePointWithMarks"/> carrying the CLAUSE-ENTRY
    /// marks, so backtracking into it undoes the guard's bindings), then flush
    /// + cut run exactly as the standard emit.</para></summary>
    private static void EmitCpFreeGuardClause(
        Sigil.Emit<PredicateDelegate> emit,
        Action<int, int, Sigil.Label> emitSlice,
        byte[] code, int clauseStart, int clauseEnd, CpFreeGuardInfo g,
        Sigil.Label nextClauseLabel, Sigil.Label failLabel,
        SelfDelegateEmitter self, int lazyCpCursor, int arity, string salt,
        Action? markDeadCursors = null,
        Action? dynamicFailDispatch = null,
        Action<Sigil.Emit<PredicateDelegate>>? dynamicCursor = null)
    {
        // ADR-031 indexed buckets — dynamicFailDispatch replaces the static
        // guard-fail branch (the stub ends with a switch over the per-member
        // next-node local instead of `br nextClauseLabel`), and dynamicCursor
        // replaces the constant lazy-CP cursor with a load of that local
        // (value -1 = chain tail → the rare paths SKIP the CP push).
        Sigil.Local? bt = null, xt = null, h = null, hb = null, ee = null;
        Sigil.Local[]? regs = null;
        Sigil.Label guardFail = nextClauseLabel;
        bool needsStub = g.NeedsSnapshot || g.NeedsRegSave || g.Framed
            || dynamicFailDispatch is not null;
        if (g.NeedsRegSave && arity > 0)
        {
            regs = new Sigil.Local[arity];
            for (int r = 0; r < arity; r++)
            {
                regs[r] = emit.DeclareLocal<Cell>($"cf_r{r}{salt}");
                emit.LoadArgument(0);
                emit.LoadConstant(r);
                emit.Call(EngineGetRegisterMethod);
                emit.StoreLocal(regs[r]);
            }
        }
        if (g.NeedsSnapshot)
        {
            bt = emit.DeclareLocal<int>($"cf_bt{salt}");
            xt = emit.DeclareLocal<int>($"cf_xt{salt}");
            h = emit.DeclareLocal<int>($"cf_h{salt}");
            hb = emit.DeclareLocal<int>($"cf_hb{salt}");
            ee = emit.DeclareLocal<int>($"cf_e{salt}");
            emit.LoadArgument(0); emit.Call(EngineBindingTrailTopGetter); emit.StoreLocal(bt);
            emit.LoadArgument(0); emit.Call(EngineExtraTrailTopGetter); emit.StoreLocal(xt);
            emit.LoadArgument(0); emit.Call(EngineHeapTopGetter); emit.StoreLocal(h);
            emit.LoadArgument(0); emit.Call(EngineEGetter); emit.StoreLocal(ee);
            emit.LoadArgument(0); emit.Call(EngineBeginIlGuardMethod); emit.StoreLocal(hb);
        }
        if (needsStub)
            guardFail = emit.DefineLabel($"cf_restore{salt}");

        emitSlice(clauseStart, g.CutPc, guardFail);         // head/guard prefix

        // The commit's cut: neck_cut, or the framed deep cut to Y[slot].
        void EmitTheCut()
        {
            if (g.DeepCut)
            {
                int slot = BytecodeIO.ReadInt32(code, g.CutPc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineCutToLevelMethod);
            }
            else
            {
                emit.LoadArgument(0);
                emit.Call(EngineNeckCutMethod);
            }
        }

        // ---- Commit (replaces the cut opcode). ----
        var rare = emit.DefineLabel($"cf_rare{salt}");
        var after = emit.DefineLabel($"cf_after{salt}");
        emit.LoadArgument(0);
        emit.Call(EngineHasPendingWakeupsGetter);
        emit.BranchIfTrue(rare);
        EmitTheCut();
        if (g.NeedsSnapshot)
        { emit.LoadArgument(0); emit.LoadLocal(hb!); emit.Call(EngineCommitIlGuardMethod); }
        emit.Branch(after);
        emit.MarkLabel(rare);
        Sigil.Label? rareNoCp = null;
        if (dynamicCursor is not null)
        {
            // Chain-tail sentinel (-1): no CP existed to materialize — the
            // wakeup flush + cut still run (a goal boundary), CP-less.
            rareNoCp = emit.DefineLabel($"cf_rarenocp{salt}");
            dynamicCursor(emit);
            emit.LoadConstant(-1);
            emit.BranchIfEqual(rareNoCp);
        }
        emit.LoadArgument(0);
        self(emit);
        if (dynamicCursor is not null) dynamicCursor(emit);
        else emit.LoadConstant(lazyCpCursor);
        emit.LoadConstant(arity);
        if (g.NeedsSnapshot)
        {
            emit.LoadLocal(bt!); emit.LoadLocal(xt!); emit.LoadLocal(h!);
            emit.LoadLocal(hb!); emit.LoadLocal(ee!);
            emit.Call(EnginePushIlCpWithMarksMethod);
        }
        else
        {
            emit.Call(EnginePushIlCpMethod);
        }
        // The push saved the CURRENT registers — but the guard may have
        // clobbered argument registers with call staging (regSave). Patch the
        // CP's saved args back to the clause-ENTRY values so a failing wakeup
        // hook backtracks the next clause/bucket-node into entry state, not
        // the guard's staging. (Latent since case B shipped; exposed by the
        // indexed-bucket extension on `choose(X,[V|_]) :- X = V, !.` — the
        // guard's unify_variable_x clobbers A1 with the list head, and the
        // clpfd wakeup's failure then re-entered the sibling node with
        // A1 = 9 instead of the list.)
        if (regs is not null)
        {
            for (int r = 0; r < arity; r++)
            {
                emit.LoadArgument(0);
                emit.LoadConstant(r);
                emit.LoadLocal(regs[r]);
                emit.Call(EngineSetTopCpArgRegisterMethod);
            }
        }
        if (rareNoCp is not null) emit.MarkLabel(rareNoCp);
        emit.LoadArgument(0);
        emit.Call(EngineFlushWakeupsForIlCutMethod);
        emit.BranchIfFalse(failLabel);
        EmitTheCut();
        if (g.NeedsSnapshot)
        { emit.LoadArgument(0); emit.LoadLocal(hb!); emit.Call(EngineCommitIlGuardMethod); }
        emit.MarkLabel(after);

        emitSlice(g.CutPc + OpcodeTable.Get((Opcode)code[g.CutPc]).Size,
            clauseEnd, failLabel);                          // post-commit body

        // Region mode: the plan allocated forward-resume cursors for the guard's
        // (now inlined) Call sites; their labels must be marked (dead — no
        // resume marker is ever set for an inlined call).
        markDeadCursors?.Invoke();

        if (needsStub)
        {
            // Guard-fail restore stub: undo the guard, then fall to the next
            // clause. Reached only by the guard prefix's fail branches.
            emit.MarkLabel(guardFail);
            if (g.Framed)
            {
                emit.LoadArgument(0);
                emit.Call(EngineDeallocateMethod);
            }
            if (g.NeedsSnapshot)
            {
                emit.LoadArgument(0);
                emit.LoadLocal(bt!); emit.LoadLocal(xt!); emit.LoadLocal(h!); emit.LoadLocal(hb!);
                emit.Call(EngineFailIlGuardMethod);
            }
            if (regs is not null)
            {
                for (int r = 0; r < arity; r++)
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(r);
                    emit.LoadLocal(regs[r]);
                    emit.Call(EngineSetRegisterMethod);
                }
            }
            if (dynamicFailDispatch is not null) dynamicFailDispatch();
            else emit.Branch(nextClauseLabel);
        }
    }

}
