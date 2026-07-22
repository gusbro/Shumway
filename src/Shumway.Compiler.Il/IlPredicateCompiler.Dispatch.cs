using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

public sealed partial class IlPredicateCompiler
{
    private static void EmitTryMeElseChainBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        TryMeElseChainInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        SelfDelegateEmitter emitSelf,
        System.Type selfDelType)
    {
        var clauses = info.Clauses;
        var failLabel = emit.DefineLabel("fail");
        var gcCtx = new GuardContEmitContext();          // ADR-033 (no-op if unused)

        // The multi-clause TryMeElseChain threads
        // non-leaf Call sites just like the single-clause
        // path. The cursor space is partitioned:
        //   cursor 0..N-1   → clause entries
        //   cursor N..N+M-1 → forward-resume points for the M
        //                     non-tail Call sites across all clauses
        // EmitClauseBody receives cursorBase=N so each Call site's
        // resume marker encodes a unique global cursor and the
        // matching label is in resumeLabels[siteIdx-1].
        int N = clauses.Count;

        // ADR-031/034 pre-scan — recognise each non-last clause's CP-free
        // guard ONCE (the stats count per invocation, and the ADR-034 fallback
        // needs the result before the cursor space is sized): a clause whose
        // guard embeds dynamic SNAPSHOTS re-emits its guard Call sites on the
        // fallback path as threaded calls, each taking an EXTRA forward-resume
        // cursor beyond the one-per-site base count.
        var guardOk = new bool[N];
        var guardInfo = new CpFreeGuardInfo[N];
        int extraDynSites = 0;
        if (CpFreeGuardCommit)
            for (int gi = 0; gi < N - 1; gi++)
                if (TryGetCpFreeGuard(
                        predicate.BytecodeUnfused, clauses[gi].Start, clauses[gi].End,
                        predicate.Arity, calleeMap, predicate.CallSites, out guardInfo[gi]))
                {
                    guardOk[gi] = true;
                    if (guardInfo[gi].EmbeddedDynamicFids is { Count: > 0 })
                    {
                        int pcX = clauses[gi].Start;
                        byte[] codeX = predicate.BytecodeUnfused;
                        while (pcX < guardInfo[gi].CutPc)
                        {
                            if ((Opcode)codeX[pcX] == Opcode.Call) extraDynSites++;
                            pcX += (Opcode)codeX[pcX] == Opcode.Meta
                                ? 6 : OpcodeTable.Get(codeX[pcX]).Size;
                        }
                    }
                }
        int totalCallSites = CountNonTailCallOpcodes(predicate.BytecodeUnfused)
            + extraDynSites;
        var resumeLabels = new Sigil.Label[totalCallSites];
        for (int j = 0; j < totalCallSites; j++)
            resumeLabels[j] = emit.DefineLabel($"call_resume_{j + 1}");

        _emitOwnerFid = predicate.FunctorId;

        // CSE (mirrors the region Stage-11 hoist): every clause's
        // PushIlChoicePoint reloads the SAME self-delegate — a per-push holder
        // dictionary probe on the runtime path. Hoist it to ONE local ahead of
        // the cursor switch (which dominates every clause entry, fresh AND
        // backtrack re-entries); gate on ≥2 pushes so the load+store only ever
        // shrinks the per-invocation work. N clauses push N−1 CPs.
        SelfDelegateEmitter effectiveSelf = emitSelf;
        if (N - 1 >= 2)
        {
            var selfDelLoc = emit.DeclareLocal(selfDelType, "cselfdel");
            emitSelf(emit);
            emit.StoreLocal(selfDelLoc);
            effectiveSelf = e => e.LoadLocal(selfDelLoc);
        }

        // Top-level cursor dispatch. one O(1) jump table (IL
        // `switch`) over the dense cursor space — 0..N-1 → clause entry;
        // N..N+M-1 → call-site resume — replacing the linear compare chain
        // (resume compares + one compare interleaved per clause) that every
        // invocation used to walk. An out-of-range cursor falls through to
        // fail, exactly as the old chain's final fall-through did.
        var clauseLabels = new Sigil.Label[N];
        for (int i = 0; i < N; i++)
            clauseLabels[i] = emit.DefineLabel($"clause_entry_{i}");
        var cursorLabels = new Sigil.Label[N + totalCallSites];
        for (int i = 0; i < N; i++) cursorLabels[i] = clauseLabels[i];
        for (int j = 0; j < totalCallSites; j++)
            cursorLabels[N + j] = resumeLabels[j];
        emit.LoadArgument(1);
        emit.Switch(cursorLabels);
        // cursor out of [0..N+M-1] (unreachable) → fail.
        emit.Branch(failLabel);

        // Self-tail-recursion → in-method loop: a self Execute in
        // any clause body resets the cursor to 0 and branches here — clause
        // 0's entry (a fresh self-call must try the first clause, not re-enter
        // the clause it was called from).
        var selfEntry = emit.DefineLabel("chain_self_entry");
        emit.MarkLabel(selfEntry);

        int siteCounter = 0;
        for (int i = 0; i < clauses.Count; i++)
        {
            emit.MarkLabel(clauseLabels[i]);

            // ADR-031 — a non-last clause whose pre-cut prefix is a CP-free
            // guard skips its entry choice point: guard failure branches to the
            // next clause (directly, or via the restore stub), and the commit
            // materialises the CP lazily only in the rare pending-wakeups case
            // (see EmitCpFreeGuardClause). forceLeafRuleInline: a tier-G guard
            // Call MUST take the inline path (its failure is then a
            // direct branch to the guard's fail label). Recognition ran once
            // in the pre-scan above (guardOk/guardInfo).
            if (guardOk[i])
            {
                var ginfo = guardInfo[i];
                // ADR-034 — staleness test + fallback (see the region driver's
                // twin for the full story): a mutated embedded snapshot sends
                // the clause down a plain path — entry CP + un-inlined guard
                // (threaded by-fid calls reach the LIVE dynamic) + jump into
                // the shared post-commit body.
                var dynFids = ginfo.EmbeddedDynamicFids;
                Sigil.Label? dynFb = null, dynBody = null;
                if (dynFids is { Count: > 0 })
                {
                    dynFb = emit.DefineLabel($"dynfb_c{i}");
                    dynBody = emit.DefineLabel($"dynbody_c{i}");
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
                        // Guard slice: e == CutPc; post-commit body: e == end.
                        // Only the GUARD slice forces leaf inlining — the
                        // recognizer's snapshot-fid collection stops at the
                        // cut, so a forced inline in the body slice would
                        // bypass the ADR-034 staleness check.
                        bool isGuardSlice = e <= ginfo.CutPc;
                        if (dynBody is not null && !isGuardSlice)
                            emit.MarkLabel(dynBody);       // fallback re-joins here
                        EmitClauseBody(
                            emit, predicate.BytecodeUnfused, s, e, fl, predicate.CallSites,
                            callSiteIndexCounter: () => ++siteCounter,
                            resumeLabels: resumeLabels,
                            emitSelfDelegate: effectiveSelf,
                            calleeMap: calleeMap,
                            cursorBase: N,
                            selfFunctorId: predicate.FunctorId,
                            selfTailLabel: selfEntry,
                            resetCursorBeforeSelfTail: true,
                            forceLeafRuleInline: isGuardSlice,
                            guardContCtx: gcCtx);
                    },
                    predicate.BytecodeUnfused, clauses[i].Start, clauses[i].End, ginfo,
                    clauseLabels[i + 1], failLabel,
                    effectiveSelf, i + 1, predicate.Arity, salt: $"_c{i}");
                if (dynFb is not null)
                {
                    emit.MarkLabel(dynFb);
                    emit.LoadArgument(0);
                    effectiveSelf(emit);
                    emit.LoadConstant(i + 1);
                    emit.LoadConstant(predicate.Arity);
                    emit.Call(EnginePushIlCpMethod);
                    int cutSz = OpcodeTable.Get(
                        (Opcode)predicate.BytecodeUnfused[ginfo.CutPc]).Size;
                    // localSalt: the fallback re-emits the same pcs the
                    // optimized guard slice already emitted — pc-named IL
                    // locals must not collide.
                    EmitClauseBody(emit, predicate.BytecodeUnfused,
                        clauses[i].Start, ginfo.CutPc + cutSz,
                        failLabel, predicate.CallSites,
                        callSiteIndexCounter: () => ++siteCounter,
                        resumeLabels: resumeLabels,
                        emitSelfDelegate: effectiveSelf,
                        calleeMap: calleeMap,
                        cursorBase: N,
                        selfFunctorId: predicate.FunctorId,
                        selfTailLabel: selfEntry,
                        resetCursorBeforeSelfTail: true,
                        localSalt: $"_dynfb{i}");
                    emit.Branch(dynBody!);
                }
                continue;
            }

            // If there's a later clause, push an IL CP for it before
            // running this clause's body.
            if (i < clauses.Count - 1)
            {
                emit.LoadArgument(0);                      // engine
                effectiveSelf(emit);                       // → PredicateDelegate (hoisted local)
                emit.LoadConstant(i + 1);                  // next cursor
                emit.LoadConstant(predicate.Arity);
                emit.Call(EnginePushIlCpMethod);
            }

            // Emit the clause body. The shared siteCounter assigns a
            // unique 1-based ordinal per non-tail Call site; the
            // resume cursor in the emitted IL is cursorBase + ordinal
            // - 1 = N + (ordinal - 1).
            EmitClauseBody(emit, predicate.BytecodeUnfused, clauses[i].Start, clauses[i].End,
                failLabel, predicate.CallSites,
                callSiteIndexCounter: () => ++siteCounter,
                resumeLabels: resumeLabels,
                emitSelfDelegate: effectiveSelf,
                calleeMap: calleeMap,
                cursorBase: N,
                selfFunctorId: predicate.FunctorId,
                selfTailLabel: selfEntry,
                resetCursorBeforeSelfTail: true);
        }

        EmitGuardContEpilogues(emit, gcCtx, calleeMap, failLabel);   // ADR-033

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>Recognises the shape:
    /// <code>
    ///   switch_on_term VarLbl ConstLbl ListLbl StructLbl   (17 bytes)
    ///   [VarLbl: try / retry / trust chain over all clauses]
    ///   [ConstLbl: switch_on_atom tableId                  (5 bytes)]
    ///   [clause bodies: each `get_atom &lt;id&gt; A0 ; proceed`]
    /// </code>
    /// where the switch_on_atom table maps each clause's first-arg atom
    /// to its body offset, and every clause body is the trivial
    /// <c>get_atom &lt;id&gt; A0; proceed</c> form.</summary>
    private static bool TryDescribeIndexedAtomPredicate(
        CompiledPredicate predicate, out IndexedAtomInfo? info)
        => TryDescribeIndexedAtomPredicate(predicate, calleeMap: null, out info);

    private static bool TryDescribeIndexedAtomPredicate(
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out IndexedAtomInfo? info)
    {
        // memoized per predicate (see IlShapeMemo): the
        // structural walk runs once; the calleeMap-dependent Call check is
        // re-applied per call.
        if (predicate.IlIndexedAtomShapeMemo is not IlShapeMemo memo)
        {
            var callFids = new List<int>();
            TryDescribeIndexedAtomPredicateStructural(predicate, callFids, out var raw);
            memo = new IlShapeMemo(raw, callFids);
            predicate.IlIndexedAtomShapeMemo = memo;
        }
        return memo.Resolve(calleeMap, out info);
    }

    private static bool TryDescribeIndexedAtomPredicateStructural(
        CompiledPredicate predicate, List<int> callFids,
        out IndexedAtomInfo? info)
    {
        info = null;
        if (predicate.Arity != 1) return false;
        byte[] code = predicate.BytecodeUnfused;
        if (code.Length < 17) return false;
        if ((Opcode)code[0] != Opcode.SwitchOnTerm) return false;
        // ADR-025 — same linear-scan caveat as TryDescribeSwitchedChain.
        if (ContainsInlineIteOpcode(code)) return false;

        // VarLbl, ConstLbl, ListLbl, StructLbl operand offsets.
        int varLbl = BytecodeIO.ReadInt32(code, 1);
        int constLbl = BytecodeIO.ReadInt32(code, 5);
        // The shape we recognise has list and struct paths both pointing
        // at the var label (i.e. nothing concrete to dispatch). Allow them
        // to point anywhere — we only emit IL for atom dispatch — but
        // demand const points at a switch_on_atom.
        if (constLbl < 0 || constLbl >= code.Length) return false;
        if ((Opcode)code[constLbl] != Opcode.SwitchOnAtom) return false;

        int tableId = BytecodeIO.ReadInt32(code, constLbl + 1);
        if (tableId < 0 || tableId >= predicate.SwitchTables.Count) return false;

        // Verify the var-dispatch path is the standard try/retry/trust
        // chain — we don't need to walk it for IL emission (we'll handle
        // var-dispatch via IL CPs ourselves) but it's a sanity check that
        // we're looking at the shape we expect.
        if (varLbl < 0 || varLbl >= code.Length) return false;
        if ((Opcode)code[varLbl] != Opcode.Try) return false;

        var table = predicate.SwitchTables[tableId];
        // the table only carries atom-headed clauses. A
        // predicate with mixed list-pattern + atom-headed clauses
        // (e.g. main/1 = `main([F|_]) :- ... ; main([]) :- ...`) ends
        // up with the list-pattern clause UN-INDEXED — it's reachable
        // only through the var-dispatch try/retry/trust chain, not
        // through switch_on_atom. The IndexedAtom emit only emits the
        // atom-direct dispatch, so a query with a non-empty list
        // would fall through to fail. Reject this shape so the
        // SwitchedChain recogniser takes over — it walks the
        // var-dispatch chain which covers every clause.
        if (table.Count != predicate.ClauseCount) return false;
        // The switch table is sorted by atom id (the WAM compiler uses a
        // SortedDictionary) but the var-dispatch path must enumerate
        // clauses in *source* order — that's what every other Prolog
        // engine does. We recover source order by sorting on the body
        // offset, since the per-predicate bytecode lays clauses out in
        // source order.
        var raw = new List<(int AtomId, int BodyOffset)>(table.Count);
        for (int i = 0; i < table.Count; i++)
        {
            int bodyOffset = table.Values[i];
            // Skip a leading Meta(DbgInfo) opcode — the WAM
            // emitter places one at the start of each clause body for
            // stack-trace mapping; from the IL detector's perspective it's
            // pure metadata that lives before the actual head-matching ops.
            if (bodyOffset >= 0 && bodyOffset + 6 <= code.Length
                && (Opcode)code[bodyOffset] == Opcode.Meta)
                bodyOffset += 6;
            if (bodyOffset < 0 || bodyOffset >= code.Length) return false;
            if ((Opcode)code[bodyOffset] != Opcode.GetAtom) return false;
            int reg = BytecodeIO.ReadInt32(code, bodyOffset + 5);
            if (reg != 0) return false;
            int atomId = BytecodeIO.ReadInt32(code, bodyOffset + 1);
            raw.Add((atomId, bodyOffset));
        }
        if (raw.Count == 0) return false;
        // Sort by body offset → source order. Body i runs from its own
        // offset to the next clause's offset (or to end of bytecode for
        // the last one).
        raw.Sort((a, b) => a.BodyOffset.CompareTo(b.BodyOffset));
        var clauses = new List<IndexedAtomClause>(raw.Count);
        bool allTrivial = true;
        for (int i = 0; i < raw.Count; i++)
        {
            int start = raw[i].BodyOffset;
            int end = i + 1 < raw.Count ? raw[i + 1].BodyOffset : code.Length;
            // Trivial-body shape: get_atom (9 bytes) + proceed
            // (1 byte). Anything else qualifies as "non-trivial" and
            // emits the body via EmitClauseBody.
            bool trivial =
                end == start + 10
                && (Opcode)code[start + 9] == Opcode.Proceed;
            if (!trivial)
            {
                // Validate the full body is in the IL subset (same check
                // TryMeElseChain uses). If not, give up — fall back to
                // SwitchedChain (or another shape).
                int q = start;
                while (q < end)
                {
                    var op = (Opcode)code[q];
                    var opInfo = OpcodeTable.Get((byte)op);
                    if (!opInfo.IsDefined || opInfo.Size == 0) return false;
                    if (!IsClauseBodyOpcodeStructural(op, predicate, q, callFids)) return false;
                    q += opInfo.Size;
                }
                allTrivial = false;
            }
            clauses.Add(new IndexedAtomClause(raw[i].AtomId, start, end, trivial));
        }
        info = new IndexedAtomInfo { Clauses = clauses, AllTrivial = allTrivial };
        return true;
    }

    /// <summary>Emits the IL for an indexed-atom multi-clause predicate.
    /// The emitted delegate handles both the ground-A1 fast path (direct
    /// atom-id dispatch) and the unbound-A1 path (enumerate via the IL
    /// choice-point machinery from ADR-014).</summary>
    private PredicateDelegate CompileIndexedAtomPredicate(
        CompiledPredicate predicate, IndexedAtomInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        // Take the holder lock for the entire emit-and-register sequence so
        // two concurrent Compile calls don't both observe the same
        // _nextHolderKey, embed it into their IL, and overwrite each other
        // in the holder. The lock is short-lived (one emit call) and only
        // contended when two engines promote at the same wall-clock moment.
        lock (IndexedDelegateHolder.RegistrationLock)
        {
            return CompileIndexedAtomPredicateUnlocked(predicate, info, calleeMap: calleeMap);
        }
    }

    private PredicateDelegate CompileIndexedAtomPredicateUnlocked(
        CompiledPredicate predicate, IndexedAtomInfo info,
        int profileKey = -1, int[]? groundOrder = null,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        int holderKey = _nextHolderKey;
        var emitSelf = SelfFromHolder(holderKey);

        var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
            $"ShumwayIl_indexed_{predicate.FunctorId}",
            doVerify: DoVerify || DebugMode);
        EmitIndexedAtomBody(emit, predicate, info, emitSelf,
            typeof(Func<Activation, int, bool>),   // runtime path: SelfFromHolder → Func
            profileKey, groundOrder, calleeMap);

        var del = FinishEmit(emit,
            $"compile fid={predicate.FunctorId} {FidName(predicate.FunctorId)}/{predicate.Arity} clauses={predicate.ClauseCount}");
        IndexedDelegateHolder.Register(holderKey, del);
        _nextHolderKey = holderKey + 1;
        return del;
    }

    /// <summary>Shared indexed-atom-shape emit body used by both the
    /// DynamicMethod runtime path (above) and the persisted
    /// assembly path. Self-references for the per-clause IL CP push
    /// route through <paramref name="emitSelf"/>.
    ///
    /// <para>PGO. <paramref name="profileKey"/> ≥ 0 emits
    /// the <em>instrumented</em> ground-dispatch: each atom match
    /// lands on its own success label that records a hit via
    /// <see cref="IlProfileCounters.Bump"/>. <paramref name="groundOrder"/>,
    /// when non-null, is a permutation of clause indices giving the
    /// order in which to emit the ground-dispatch <c>cmp</c> chain —
    /// the phase-2 <em>optimised</em> form puts the
    /// most-frequently-matched atom first. The ground dispatch is a
    /// pure lookup (whichever atom matches, the answer is the same),
    /// so reordering it is always semantics-preserving. The
    /// var-dispatch path is never reordered — its clause order is the
    /// observable solution order.</para></summary>
    private static void EmitIndexedAtomBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        IndexedAtomInfo info,
        SelfDelegateEmitter emitSelf,
        System.Type selfDelType,
        int profileKey = -1,
        int[]? groundOrder = null,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        var clauses = info.Clauses;
        int[] atomIds = clauses.Select(c => c.AtomId).ToArray();
        int n = clauses.Count;

        var failLabel = emit.DefineLabel("fail");
        _emitOwnerFid = predicate.FunctorId;

        // Per-clause body labels. For trivial clauses the
        // body is `get_atom + proceed`; for non-trivial
        // it's whatever IL-supported opcodes the body holds. Both run
        // via EmitClauseBody.
        var bodyLabels = new Sigil.Label[n];
        for (int i = 0; i < n; i++)
            bodyLabels[i] = emit.DefineLabel($"body_{i}");

        // varEnter[i]: pushes CP for cursor=i+1 (unless last) and
        // jumps to bodyLabel[i]. Used by the var-dispatch path —
        // cursor i tries clause i, leaving an IL CP for clause i+1
        // on backtrack.
        var varEnterLabels = new Sigil.Label[n];
        for (int i = 0; i < n; i++)
            varEnterLabels[i] = emit.DefineLabel($"var_enter_{i}");

        // Call-site resume cursors for non-tail Calls inside any
        // clause body. The cursor space is partitioned:
        //   cursor 0          → tag dispatch (ground/var)
        //   cursor 1..n-1     → varEnter[cursor] (next clause on backtrack)
        //   cursor n..n+M-1   → resume after the j-th non-tail Call site
        int totalCallSites = 0;
        foreach (var c in clauses)
            totalCallSites += CountNonTailCallOpcodes(
                predicate.BytecodeUnfused, c.BodyStart, c.BodyEnd);
        var callResumeLabels = new Sigil.Label[totalCallSites];
        for (int j = 0; j < totalCallSites; j++)
            callResumeLabels[j] = emit.DefineLabel($"call_resume_{j + 1}");

        // CSE (mirrors the region Stage-11 hoist): every var-path
        // clause's PushIlChoicePoint reloads the SAME self-delegate — a
        // per-push holder dictionary probe on the runtime path. Hoist it to
        // ONE local ahead of the cursor switch (which dominates every
        // varEnter label, fresh AND backtrack re-entries); gate on ≥2 pushes
        // so the load+store only ever shrinks the per-invocation work.
        SelfDelegateEmitter effectiveSelf = emitSelf;
        if (n - 1 >= 2)
        {
            var selfDelLoc = emit.DeclareLocal(selfDelType, "aselfdel");
            emitSelf(emit);
            emit.StoreLocal(selfDelLoc);
            effectiveSelf = e => e.LoadLocal(selfDelLoc);
        }

        // Top-level cursor dispatch. one O(1) jump table (IL
        // `switch`) over the dense cursor space — 0 → tag dispatch; 1..n-1 →
        // varEnter[cursor]; n..n+M-1 → call-site resume — replacing the
        // linear compare chain that tested cursor==0 LAST, making the
        // fresh-call path (by far the most common) pay the whole chain. An
        // out-of-range cursor falls through to fail, exactly as the old
        // chain's explicit default did.
        var cursorZero = emit.DefineLabel("cursor_zero");
        var cursorLabels = new Sigil.Label[n + totalCallSites];
        cursorLabels[0] = cursorZero;
        for (int i = 1; i < n; i++) cursorLabels[i] = varEnterLabels[i];
        for (int j = 0; j < totalCallSites; j++)
            cursorLabels[n + j] = callResumeLabels[j];
        emit.LoadArgument(1);
        emit.Switch(cursorLabels);
        emit.Branch(failLabel);     // cursor out of range (unreachable) → fail
        emit.MarkLabel(cursorZero);

        // cursor == 0: deref A1, dispatch on tag.
        EmitDerefA0(emit);
        var a1Local = emit.DeclareLocal<Cell>("a1");
        emit.StoreLocal(a1Local);

        emit.LoadLocalAddress(a1Local);
        emit.Call(CellTagGetter);
        var tagLocal = emit.DeclareLocal<byte>("tag");
        emit.StoreLocal(tagLocal);

        var groundDispatchLabel = emit.DefineLabel("ground_dispatch");

        // if (tag == Tag.Ref) goto varEnter[0]
        emit.LoadLocal(tagLocal);
        emit.LoadConstant((int)Tag.Ref);
        emit.BranchIfEqual(varEnterLabels[0]);
        // if (tag == Tag.Atom) goto ground_dispatch
        emit.LoadLocal(tagLocal);
        emit.LoadConstant((int)Tag.Atom);
        emit.BranchIfEqual(groundDispatchLabel);
        // Any other tag → fail.
        emit.Branch(failLabel);

        // Ground dispatch: cmp atomId against each clause's atomId,
        // jump to that clause's body on match.
        emit.MarkLabel(groundDispatchLabel);
        emit.LoadLocalAddress(a1Local);
        emit.Call(CellAsAtomIdGetter);
        var atomIdLocal = emit.DeclareLocal<int>("atomId");
        emit.StoreLocal(atomIdLocal);

        int[] order = groundOrder ?? Enumerable.Range(0, n).ToArray();

        if (profileKey >= 0)
        {
            // PGO: per-clause success label that bumps the
            // hit counter, then jumps to the body.
            var successLabels = new Sigil.Label[n];
            for (int ci = 0; ci < n; ci++)
                successLabels[ci] = emit.DefineLabel($"ground_success_{ci}");
            foreach (int ci in order)
            {
                emit.LoadLocal(atomIdLocal);
                EmitAtomId(emit, atomIds[ci]);
                emit.BranchIfEqual(successLabels[ci]);
            }
            emit.Branch(failLabel);
            for (int ci = 0; ci < n; ci++)
            {
                emit.MarkLabel(successLabels[ci]);
                emit.LoadConstant(profileKey);
                emit.LoadConstant(ci);
                emit.Call(IlProfileCountersBump);
                emit.Branch(bodyLabels[ci]);
            }
        }
        else
        {
            foreach (int ci in order)
            {
                emit.LoadLocal(atomIdLocal);
                EmitAtomId(emit, atomIds[ci]);
                emit.BranchIfEqual(bodyLabels[ci]);
            }
            emit.Branch(failLabel);
        }

        // varEnter[i]: push CP for cursor=i+1 (unless last) and jump
        // to bodyLabel[i]. The body's own get_atom opcode does the
        // actual A0/atom unification.
        for (int i = 0; i < n; i++)
        {
            emit.MarkLabel(varEnterLabels[i]);
            if (i < n - 1)
            {
                emit.LoadArgument(0);                  // engine
                effectiveSelf(emit);                   // → PredicateDelegate (hoisted local)
                emit.LoadConstant(i + 1);              // next cursor
                emit.LoadConstant(1);                  // arity
                emit.Call(EnginePushIlCpMethod);
            }
            emit.Branch(bodyLabels[i]);
        }

        // Per-clause body emit. Each body's non-tail Calls use the
        // shared siteCounter (cursorBase = n places Call-site cursors
        // above the clause-entry range).
        int siteCounter = 0;
        for (int i = 0; i < n; i++)
        {
            emit.MarkLabel(bodyLabels[i]);
            EmitClauseBody(emit, predicate.BytecodeUnfused,
                clauses[i].BodyStart, clauses[i].BodyEnd,
                failLabel, predicate.CallSites,
                callSiteIndexCounter: () => ++siteCounter,
                resumeLabels: callResumeLabels,
                emitSelfDelegate: effectiveSelf,
                calleeMap: calleeMap,
                cursorBase: n);
        }

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>A counter the IL emission embeds into the bytecode as a
    /// constant to look up the freshly-emitted delegate at runtime. This
    /// is the Tier-1 equivalent of a self-reference; Sigil doesn't expose
    /// the dynamic method's delegate during emission, so we route through
    /// a static side table keyed by an integer.</summary>
    private static int _nextHolderKey = 1;

}
