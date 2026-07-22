using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

public sealed partial class IlPredicateCompiler
{
    // ============================================================================
    // PGO: two-phase profile-guided IL compilation
    // ============================================================================

    /// <summary>Profile key counter — allocated per instrumented
    /// predicate, indexing <see cref="IlProfileCounters"/>. Separate
    /// namespace from <see cref="_nextHolderKey"/>.</summary>
    private static int _nextProfileKey = 1;

    /// <summary>Result of a phase-1 PGO compile: the (instrumented)
    /// delegate plus the profile key the engine later passes to
    /// <see cref="CompileOptimized"/>. A <see cref="ProfileKey"/> of
    /// <c>-1</c> means the predicate's shape isn't PGO-eligible — it
    /// was compiled normally and no phase-2 recompile should fire.</summary>
    public readonly record struct PgoCompileResult(
        PredicateDelegate Delegate, int ProfileKey);

    /// <summary>Phase-1 PGO compile. For the indexed-atom shape this
    /// emits the <em>instrumented</em> form whose ground dispatch
    /// records which atom matched; for every other shape it's an
    /// ordinary <see cref="Compile"/> with <see cref="PgoCompileResult.ProfileKey"/>
    /// set to <c>-1</c>.</summary>
    public PgoCompileResult CompileInstrumented(
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (predicate.ClauseCount > 1
            && TryDescribeIndexedAtomPredicate(predicate, out var info))
        {
            lock (IndexedDelegateHolder.RegistrationLock)
            {
                int profileKey = _nextProfileKey++;
                IlProfileCounters.Allocate(profileKey, info!.Clauses.Count);
                var del = CompileIndexedAtomPredicateUnlocked(
                    predicate, info, profileKey, groundOrder: null);
                return new PgoCompileResult(del, profileKey);
            }
        }
        return new PgoCompileResult(Compile(predicate, calleeMap), -1);
    }

    /// <summary>Phase-2 PGO compile. Reads the hit counts accumulated
    /// under <paramref name="profileKey"/> and recompiles the
    /// indexed-atom predicate with the ground-dispatch <c>cmp</c> chain
    /// ordered most-frequently-matched-atom first. Releases the profile
    /// counters afterwards. Falls back to a plain compile when the
    /// shape isn't indexed-atom (defensive — the engine only calls this
    /// for keys produced by an indexed-atom phase 1).</summary>
    public PredicateDelegate CompileOptimized(
        CompiledPredicate predicate, int profileKey,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (profileKey < 0
            || !TryDescribeIndexedAtomPredicate(predicate, out var info))
        {
            return Compile(predicate, calleeMap);
        }
        long[]? counts = IlProfileCounters.Get(profileKey);
        int n = info!.Clauses.Count;
        var order = Enumerable.Range(0, n).ToArray();
        if (counts is not null)
        {
            // Descending by hit count; Array.Sort isn't stable but ties
            // among equally-cold atoms don't matter.
            Array.Sort(order, (a, b) => counts[b].CompareTo(counts[a]));
        }
        lock (IndexedDelegateHolder.RegistrationLock)
        {
            var del = CompileIndexedAtomPredicateUnlocked(
                predicate, info, profileKey: -1, groundOrder: order);
            IlProfileCounters.Release(profileKey);
            return del;
        }
    }

    // ============================================================================
    // Shape 1: single-clause facts
    // ============================================================================

    private static bool CanCompileSingleClause(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        byte[] code = predicate.BytecodeUnfused;
        int pc = 0;
        bool sawTerminator = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Execute)
            {
                // Execute is a body-tail terminator: control transfers
                // to the callee, which proceeds back to our caller's
                // continuation. The IL emission for Execute returns
                // from the delegate (with the IlTailCallPending flag
                // set), so any opcodes after it in the bytecode are
                // unreachable.
                sawTerminator = true;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Call)
            {
                // Non-tail Call: threaded via resume markers — callee
                // alternatives retry through the natural CP cascade and
                // rejoin the body at a post-call cursor. No leaf
                // restriction needed — just confirm we have a calleeMap
                // entry so the runtime can resolve the functor.
                if (calleeMap is null) return false;
                int siteFid = FindCallSiteFunctorId(predicate.CallSites, pc);
                if (siteFid < 0) return false;
                if (!calleeMap.TryGetValue(siteFid, out _)) return false;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.CallBuiltin)
            {
                // call/1..7 and '$call'/2 are now IL-eligible via
                // IlMetaCallHelper.Dispatch. The CallBuiltin emit at
                // EmitClauseBody treats them as threaded non-tail calls
                // (forward-resume + cursor switch).
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (IsAEvalOpcode(op))
            {
                if (!IsSupportedAEval(code, pc)) return false;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.DeallocateProceed)
            {
                // Fused deallocate+proceed — a body terminator. A
                // single-clause body with a frame ending in a non-tail-call goal
                // (a cut or a builtin) ends here; EmitClauseBody emits the
                // deallocate then the proceed-return, so it IS compilable. Must be
                // checked BEFORE IsSupportedOpcode (which also accepts it but does
                // not record the terminator). Without this, e.g. `p(X):-a(X),!.`
                // was wrongly rejected as cannot-compile.
                sawTerminator = true;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.ExecuteBuiltin)
            {
                // fused tail builtin: dispatch +
                // proceed in one opcode, a body terminator. Non-meta only
                // (IsClauseBodyOpcode has the same gate for the multi-clause
                // describers).
                var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                    BytecodeIO.ReadInt32(code, pc + 1));
                if (entry.IsCall || entry.IsDollarCall) return false;
                sawTerminator = true;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.TryMeElse)
            {
                // ADR-025 stage (b) — inline ITE (the body-CP arity sentinel).
                // A single-clause predicate has no dispatch chain, so any
                // try_me_else here must be the ITE form.
                if (BytecodeIO.ReadInt32(code, pc + 5) != OpcodeTable.InlineIteCpArity) return false;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (IsSupportedOpcode(op))
            {
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Proceed)
            {
                sawTerminator = true;
                pc += 1;
                continue;
            }
            return false;
        }
        return sawTerminator;
    }

    /// <summary>arity of the callee a Call / Execute
    /// site dispatches to, recovered from the functor table so
    /// debug markers know how many X registers to dump. The
    /// FunctorTable.Lookup result is the canonical
    /// <c>(atomId, arity)</c> pair the linker keyed off.</summary>
#if DEBUG
    private static int ResolveCalleeArity(int siteFunctorId)
    {
        var (_, arity) = Shumway.Core.FunctorTable.Lookup(siteFunctorId);
        return arity;
    }
#endif

    /// <summary>binary search. <see cref="CompiledPredicate.CallSites"/>
    /// is built in ascending <c>OpcodeOffset</c> order at every construction
    /// site (per-clause emission appends sites forward, predicate assembly
    /// concatenates clauses at increasing offsets, and the bundle codec
    /// round-trips that order), and offsets are unique (one site per opcode),
    /// so the linear scan this replaces — run per Call/Execute opcode inside
    /// every describe walk, region validation and emit — was O(sites) for no
    /// reason. A miss falls back to the linear scan, so behaviour is exact
    /// even for an unsorted list.</summary>
    private static int FindCallSiteFunctorId(
        IReadOnlyList<CallSite> sites, int opcodeOffset)
    {
        int lo = 0, hi = sites.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int off = sites[mid].OpcodeOffset;
            if (off == opcodeOffset) return sites[mid].CalleeFunctorId;
            if (off < opcodeOffset) lo = mid + 1;
            else hi = mid - 1;
        }
        // Defensive miss path: exact parity with the pre-433 linear scan in
        // case a future construction site ever emits out-of-order sites.
        for (int i = 0; i < sites.Count; i++)
            if (sites[i].OpcodeOffset == opcodeOffset) return sites[i].CalleeFunctorId;
        return -1;
    }

    /// <summary>A "leaf" predicate is a single-clause predicate whose
    /// body is purely head matching + a trailing proceed — no body
    /// calls, no cut, no allocate. Calling it can't push choice points
    /// (no try_me_else) and can't escape with a tail call (no Execute
    /// / Call). The IL <c>Call</c> emission relies on this so the
    /// sub-call always runs to completion in one shot.</summary>
    private static bool IsLeafPredicate(CompiledPredicate pred)
    {
        if (pred.ClauseCount != 1) return false;
        byte[] code = pred.BytecodeUnfused;
        int pc = 0;
        bool sawProceed = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Proceed) { sawProceed = true; pc += 1; continue; }
            if (IsHeadMatchingOpcode(op))
            {
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            return false;
        }
        return sawProceed;
    }

    /// <summary>Inline-rule case 2 (detector) — a single-clause RULE that can be
    /// inlined into a caller's IL method, generalising
    /// <see cref="IsInlinableLeafRule"/> to a body that also makes USER calls and
    /// uses an environment frame (permanents). Single clause; ends in
    /// proceed / deallocate_proceed (a trailing tail <c>Execute</c> is rejected —
    /// un-tailing it at a non-tail inline site, and telling a user predicate from
    /// a tail-position backtrackable/meta builtin which also lowers to Execute, is
    /// deferred); no meta (<c>call</c>/<c>$call</c>) or backtrackable builtin.
    /// Everything else — allocate/deallocate, Y-slots, head/body unify+arith,
    /// deterministic <c>CallBuiltin</c>, non-tail user <c>Call</c> — is allowed.
    /// <para><paramref name="allowCut"/>: when false (the diagnostic / sizing use)
    /// a cut disqualifies; when true (the emit use) the deep-cut family
    /// (allocate_get_level / get_level / cut) and neck_cut are admitted — the emit
    /// sets <c>B0 = engine.B</c> at the inline entry so the captured barrier prunes
    /// only the inlined body's choice points.</para></summary>
    internal static bool IsInlinableRule(CompiledPredicate pred, bool allowCut = false)
    {
        if (pred.ClauseCount != 1) return false;
        byte[] code = pred.BytecodeUnfused;
        int pc = 0;
        bool endsTerminal = false;   // last op was proceed / deallocate_proceed
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            switch (op)
            {
                case Opcode.Proceed: endsTerminal = true; pc += 1; continue;
                case Opcode.DeallocateProceed:
                    endsTerminal = true; pc += OpcodeTable.Get((byte)op).Size; continue;
                // A trailing tail call to a USER predicate: the emit
                // un-tails it into a threaded non-tail call at a non-tail inline
                // site. In linked runtime bytecode `Execute` always targets a user
                // predicate (a tail-position builtin is ExecuteBuiltin, rejected
                // below), so this is safe to thread.
                case Opcode.Execute: endsTerminal = true; pc += OpcodeTable.Get((byte)op).Size; continue;
                case Opcode.ExecuteBuiltin: return false;   // tail builtin — needs CallBuiltin machinery
                // ADR-025 — an inline ITE needs a resume cursor + jump labels
                // the inline emit site doesn't plan; keep such bodies out of
                // rule inlining (they still compile standalone).
                case Opcode.TryMeElse:
                case Opcode.TrustMe:
                case Opcode.Jump:
                    return false;
                case Opcode.Meta: pc += 6; continue;
                case Opcode.Cut:
                case Opcode.NeckCut:
                case Opcode.GetLevel:
                case Opcode.AllocateGetLevel:
                    if (!allowCut) return false;
                    endsTerminal = false;
                    pc += OpcodeTable.Get((byte)op).Size;
                    continue;
                case Opcode.CallBuiltin:
                {
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                        BytecodeIO.ReadInt32(code, pc + 1));
                    // precomputed flags instead of name compares.
                    if (entry.IsCall || entry.IsDollarCall || entry.IsBacktrackable)
                        return false;
                    endsTerminal = false;
                    pc += OpcodeTable.Get((byte)op).Size;
                    continue;
                }
                default:
                {
                    int size = OpcodeTable.Get((byte)op).Size;
                    if (size <= 0) return false;
                    endsTerminal = false;
                    pc += size;
                    continue;
                }
            }
        }
        return endsTerminal;
    }

    /// <summary>Inline-rule case 1 — gates the extension of the leaf inline
    /// to single-clause RULES with a deterministic builtin/arith/unify body
    /// (<see cref="IsInlinableLeafRule"/>). Default OFF; <c>SHUMWAY_INLINE_RULES=1</c>
    /// enables it while it is validated, before the default flips.</summary>
    internal static readonly bool InlineLeafRules =
        System.Environment.GetEnvironmentVariable("SHUMWAY_INLINE_RULES") == "1";

    /// <summary>Inline-rule case 2 — gates inlining a single-clause RULE that makes
    /// USER calls and/or cuts (<see cref="IsInlinableRule"/> with allowCut) into a
    /// metaCp caller. Default OFF; <c>SHUMWAY_INLINE_RULES2=1</c> while validated.
    /// Restricted to the metaCp caller path (where the forward-resume cursor count
    /// is extended to cover the inlined body's threaded calls).</summary>
    internal static readonly bool InlineRules2 =
        System.Environment.GetEnvironmentVariable("SHUMWAY_INLINE_RULES2") == "1";

    /// <summary>The non-tail <c>Call</c> sites in <paramref name="predicate"/> whose
    /// callee is a case-2 inlinable single-clause rule (has a body call and/or a
    /// cut — a pure leaf rule stays on the case-1 path). Maps the call-site
    /// <c>pc</c> to the callee. Empty unless <see cref="InlineRules2"/>.</summary>
    /// <summary>shared empty result so the gated-off path (the
    /// default: <see cref="InlineRules2"/> unset) allocates nothing per call.
    /// Callers only read the returned map.</summary>
    private static readonly Dictionary<int, CompiledPredicate> NoRuleInlineSites = new();

    private static Dictionary<int, CompiledPredicate> ComputeRuleInlineSites(
        CompiledPredicate predicate, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        // Runtime DynamicMethod path only: the persisted-bundle emit computes its
        // own (base) callSiteCount and would desync against the extended resume
        // cursors. (Restriction lifted when the persisted path counts them too.)
        if (!InlineRules2 || calleeMap is null || _persistPatches is not null)
            return NoRuleInlineSites;
        var sites = new Dictionary<int, CompiledPredicate>();
        byte[] code = predicate.BytecodeUnfused;
        int pc = 0;
        while (pc < code.Length)
        {
            if ((Opcode)code[pc] == Opcode.Call)
            {
                int fid = FindCallSiteFunctorId(predicate.CallSites, pc);
                if (fid >= 0 && calleeMap.TryGetValue(fid, out var callee)
                    && !callee.IsDynamicSnapshot       // ADR-034 — no unchecked inline
                    && IsInlinableRule(callee, allowCut: true)
                    && !IsInlinableLeafRule(callee))   // pure leaf rules → case 1
                {
                    sites[pc] = callee;
                    DiagShape("1", true, () =>
                        $"[rule-inline] caller fid={predicate.FunctorId} callee fid={callee.FunctorId} "
                        + $"bodycalls={CountNonTailCallOpcodes(callee.BytecodeUnfused)}");
                }
            }
            pc += (Opcode)code[pc] == Opcode.Meta ? 6 : OpcodeTable.Get(code[pc]).Size;
        }
        return sites;
    }

    /// <summary>Extra forward-resume cursors the inlined rule bodies need — each
    /// body's own non-tail <c>Call</c> sites thread through the CALLER's cursor
    /// space, PLUS a trailing tail <c>Execute</c>, which the emit
    /// un-tails into a threaded non-tail call and so also takes a cursor. The
    /// caller's resume-label array must be sized to include all of them.</summary>
    private static int CountRuleInlineExtraCursors(
        IReadOnlyDictionary<int, CompiledPredicate> sites)
    {
        int extra = 0;
        foreach (var callee in sites.Values)
            extra += CountRuleBodyThreadedCalls(callee.BytecodeUnfused);
        return extra;
    }

    /// <summary>Threaded-call cursors an inlined rule body consumes: its non-tail
    /// <c>Call</c>s plus a trailing tail <c>Execute</c> (un-tailed at a non-tail
    /// inline site). Must match exactly what the body emission consumes.</summary>
    private static int CountRuleBodyThreadedCalls(byte[] body)
    {
        int n = CountNonTailCallOpcodes(body);
        if (BodyEndsInExecute(body)) n++;
        return n;
    }

    /// <summary>True iff the body's terminal opcode is a tail <c>Execute</c>
    /// (a user-predicate tail call) — the case the inline emit un-tails.</summary>
    private static bool BodyEndsInExecute(byte[] body)
    {
        int pc = 0;
        bool lastWasExecute = false;
        while (pc < body.Length)
        {
            var op = (Opcode)body[pc];
            if (op == Opcode.Meta) { pc += 6; continue; }
            lastWasExecute = op == Opcode.Execute;
            int size = OpcodeTable.Get((byte)op).Size;
            if (size <= 0) return false;
            pc += size;
        }
        return lastWasExecute;
    }

}
