using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

public sealed partial class IlPredicateCompiler
{
    // ============================================================================
    // Tier-1 IL local-predicate inlining, Phase 1 (multi-clause facts)
    // (docs/design/il-local-inlining.md). Gated OFF by default behind
    // SHUMWAY_INLINE_FACTS=1 — a backtracking/cursor bug would give wrong
    // answers, so the default path is untouched while this is validated.
    // ============================================================================

    internal static readonly bool InlineFacts =
        System.Environment.GetEnvironmentVariable("SHUMWAY_INLINE_FACTS") != "0";

    /// <summary>A non-tail <c>Call p/n</c> site whose callee <c>p</c> is an
    /// eligible multi-clause fact, to be inlined into the caller's IL method.
    /// The fact's clause alternatives 2..K get cursors <see cref="BaseCursor"/>
    /// .. in the CALLER's cursor space; on backtrack the caller's delegate is
    /// re-entered at one of <see cref="AltLabels"/>; a clause match branches to
    /// <see cref="Continuation"/> (the caller's post-call code).</summary>
    private sealed class InlineSite
    {
        public required CompiledPredicate Fact { get; init; }
        public required IReadOnlyList<(int Start, int End)> ClauseRanges { get; init; }
        public required int BaseCursor { get; init; }
        public required Sigil.Label[] AltLabels { get; init; }      // length K-1, clauses 2..K
        public required Sigil.Label Continuation { get; init; }
    }

    /// <summary>Pre-scan a caller's body for inlinable multi-clause-fact Call
    /// sites, allocating each <c>K-1</c> alternative cursors starting at
    /// <paramref name="firstCursor"/> (must clear the call-site resume cursors)
    /// and pre-defining their labels. Returns the per-call-offset map and, via
    /// <paramref name="cursorsUsed"/>, how many cursors were taken. Empty when
    /// inlining is off / the budget would overflow.</summary>
    private static Dictionary<int, InlineSite> ComputeInlineSites(
        Sigil.Emit<PredicateDelegate> emit, CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        int firstCursor, out int cursorsUsed)
    {
        cursorsUsed = 0;
        var sites = new Dictionary<int, InlineSite>();
        if (!InlineFacts || calleeMap is null) return sites;
        byte[] code = predicate.BytecodeUnfused;
        int cursor = firstCursor;
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Call)
            {
                int fid = FindCallSiteFunctorId(predicate.CallSites, pc);
                if (fid >= 0 && calleeMap.TryGetValue(fid, out var callee)
                    && !callee.IsDynamicSnapshot       // ADR-034 — no unchecked inline
                    && callee.ClauseCount >= 2 && IsFactPredicate(callee)
                    && TryGetFactClauseRanges(callee, out var ranges)
                    && ranges.Count == callee.ClauseCount
                    // Profitability gate: inline ONLY facts whose
                    // every clause has a distinct constant first arg, so the
                    // index pre-filter makes a BOUND call deterministic
                    // (the clear crypt-style win). A fact without that index
                    // (a grammar/dictionary fact with compound or repeated first
                    // args) inlines as a plain linear chain — no indexing gain —
                    // so the trampoline keeps those. This is the ONLY size-ish
                    // gate: re-entry is an O(1) jump table (see the cursor switch
                    // in EmitSingleClauseMetaCpBody), so inlining a wide fact no
                    // longer costs more than the trampoline — no clause-count
                    // budget is needed (an earlier one was masking that flaw).
                    && TryGetFactFirstArgKeys(callee.BytecodeUnfused, ranges, out _, out _))
                {
                    int k = ranges.Count;
                    if (cursor + (k - 1) >= Activation.ResumeMarkerCursorStride) break; // budget
                    int seq = NextLabelSeq();
                    var alt = new Sigil.Label[k - 1];
                    for (int j = 0; j < k - 1; j++)
                        alt[j] = emit.DefineLabel($"inl_{pc}_{seq}_alt{j}");
                    sites[pc] = new InlineSite
                    {
                        Fact = callee, ClauseRanges = ranges, BaseCursor = cursor,
                        AltLabels = alt, Continuation = emit.DefineLabel($"inl_{pc}_{seq}_cont"),
                    };
                    cursor += k - 1;
                }
            }
            pc += op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
        }
        cursorsUsed = cursor - firstCursor;
        DiagShape("1", sites.Count > 0, () => string.Join("\n", sites.Values.Select(s =>
            $"[inline] caller fid={predicate.FunctorId} callee fid={s.Fact.FunctorId} "
            + $"arity={s.Fact.Arity} clauses={s.ClauseRanges.Count}")));
        return sites;
    }

    /// <summary>Exploratory diagnostic (SHUMWAY_IL_SHAPE=2): classify every
    /// non-tail <c>Call</c> site's callee by inline-candidate shape, to see what
    /// an EXTENDED inliner could reach beyond today's index-eligible multi-clause
    /// fact. One <c>[cand] category callee=fid clauses=N</c> line per site;
    /// aggregate a run with <c>sort | uniq -c</c>. Categories: <c>1cl-fact</c>
    /// (leaf-inlinable today), <c>1cl-rule</c> (single-clause rule w/ body),
    /// <c>Ncl-rule</c> (multi-clause rule), <c>Nfact-IDX(inlines)</c> (what the
    /// current inliner takes), <c>Nfact-NOIDX</c> (multi-clause fact without a
    /// unique-constant first-arg index), <c>Nfact-unshaped</c>,
    /// <c>ext-or-builtin</c>, <c>var-or-control</c>.</summary>
    /// <summary>Stage-1 diagnostic (SHUMWAY_IL_SHAPE=3): for each promoted
    /// predicate, build its IL-eligible region and
    /// report its size at the default budget and uncapped — to size real regions
    /// and tune the budget before the emit stages. No emit.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private void DiagnoseRegion(
        CompiledPredicate predicate, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_IL_SHAPE") != "3") return;
        if (calleeMap is null) return;
        bool Eligible(CompiledPredicate p) => IsRegionMemberEligible(p, calleeMap);
        var capped = IlRegionBuilder.Build(predicate, calleeMap, extraEligible: Eligible);
        var uncapped = IlRegionBuilder.Build(predicate, calleeMap, budgetBytes: 1_000_000, extraEligible: Eligible);
        if (uncapped.MemberCount <= 1) return;   // no local closure → uninteresting
        System.Console.Error.WriteLine(
            $"[region] root fid={predicate.FunctorId} members={capped.MemberCount}"
            + $" bytes={capped.TotalBytecodeBytes} (uncapped members={uncapped.MemberCount}"
            + $" bytes={uncapped.TotalBytecodeBytes})");
    }

    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private static void DiagnoseInlineCandidates(
        CompiledPredicate predicate, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_IL_SHAPE") != "2") return;
        byte[] code = predicate.BytecodeUnfused;
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Call)
            {
                int fid = FindCallSiteFunctorId(predicate.CallSites, pc);
                int clauses = 0;
                string cat;
                if (fid < 0) cat = "var-or-control";
                else if (calleeMap is null || !calleeMap.TryGetValue(fid, out var callee))
                    cat = "ext-or-builtin";
                else
                {
                    clauses = callee.ClauseCount;
                    // "leaf" = body makes no non-tail call to another predicate
                    // (only builtins / arith / unify) — the easiest rule to inline.
                    bool leaf = callee.CallSites is null || callee.CallSites.Count == 0;
                    string lt = leaf ? "leaf" : "nonleaf";
                    if (clauses == 1)
                        cat = IsFactPredicate(callee) ? "1cl-fact" : $"1cl-rule-{lt}";
                    else if (!IsFactPredicate(callee))
                        cat = $"Ncl-rule-{lt}";
                    else if (!TryGetFactClauseRanges(callee, out var ranges) || ranges.Count != clauses)
                        cat = "Nfact-unshaped";
                    else if (TryGetFactFirstArgKeys(callee.BytecodeUnfused, ranges, out _, out _))
                        cat = "Nfact-IDX(inlines)";
                    else cat = "Nfact-NOIDX";
                    // Case-2 eligibility (single-clause, cut-free, no meta /
                    // backtrackable builtin) — the opportunity the rule-body
                    // inliner targets. Reported as a suffix tag.
                    if (clauses == 1 && IsInlinableRule(callee)) cat += " [inl2]";
                }
                System.Console.Error.WriteLine($"[cand] {cat} callee={fid} clauses={clauses}");
            }
            pc += op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
        }
    }

    /// <summary>A fact's per-clause head-match byte ranges (the
    /// <see cref="TryDescribeSwitchedChain"/> / try-me-else describers give them
    /// for the multi-clause dispatch shapes a compiled fact takes).</summary>
    private static bool TryGetFactClauseRanges(
        CompiledPredicate fact, out IReadOnlyList<(int Start, int End)> ranges)
    {
        if (TryDescribeSwitchedChain(fact, calleeMap: null, out var sc) && sc is not null)
        { ranges = sc.Clauses; return true; }
        if (TryDescribeTryMeElseChain(fact, calleeMap: null, out var tc) && tc is not null)
        { ranges = tc.Clauses; return true; }
        ranges = System.Array.Empty<(int, int)>();
        return false;
    }

    /// <summary>Emits an inlined multi-clause fact's clause chain at a non-tail
    /// call site. For each clause c (0..K-1): if not the last, push
    /// an IL CP `(this delegate, BaseCursor+c, fact arity)` — its continuation
    /// is the caller's delegate re-entered at the alternative cursor, which the
    /// caller's cursor switch routes to <c>AltLabels[c]</c>; then emit clause c's
    /// head match against the call args (already in the argument registers) with
    /// the proceed suppressed; on match branch to the shared continuation (the
    /// last clause falls through). A head-match failure branches to
    /// <paramref name="failLabel"/> → returns false → the engine backtracks,
    /// popping the CP and restoring the saved argument registers, and re-enters
    /// at the next clause's alternative cursor.</summary>
    private static void EmitInlinedFact(
        Sigil.Emit<PredicateDelegate> emit, InlineSite site, Sigil.Label failLabel,
        SelfDelegateEmitter emitSelf, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        int factArity = site.Fact.Arity;
        byte[] fcode = site.Fact.BytecodeUnfused;
        int k = site.ClauseRanges.Count;

        // Phase 1b: when every clause has a DISTINCT constant first
        // argument (all integer or all atom — crypt's odd/even/lefteven), emit a
        // first-argument index pre-filter so a BOUND arg jumps straight to its
        // single clause (deterministic, no choice point) instead of the linear
        // scan — recovering the first-arg indexing the trampoline had. Only an
        // UNBOUND arg falls to the chain (generate, try-all). A bound value with
        // no matching key, or a bound non-indexed type, fails outright (a pure
        // constant fact has no catch-all clause).
        if (factArity >= 1
            && TryGetFactFirstArgKeys(fcode, site.ClauseRanges, out bool isAtom, out int[] keys))
        {
            // Names must be unique per inline site (a caller can inline several
            // facts); the site's BaseCursor is unique.
            int u = site.BaseCursor;
            var chainLabel = emit.DefineLabel($"inl{u}_chain");
            var detLabels = new Sigil.Label[k];
            for (int c = 0; c < k; c++) detLabels[c] = emit.DefineLabel($"inl{u}_det{c}");
            var cellLoc = emit.DeclareLocal<Cell>($"inl{u}_cell");
            var tagLoc = emit.DeclareLocal<int>($"inl{u}_tag");

            // cell = deref(X0) (one level)
            emit.LoadArgument(0);
            emit.LoadConstant(0);
            emit.Call(EngineGetRegisterMethod);
            emit.StoreLocal(cellLoc);
            emit.LoadLocalAddress(cellLoc);
            emit.Call(CellTagIdGetter);
            emit.LoadConstant((int)Tag.Ref);
            var notRef = emit.DefineLabel($"inl{u}_notref");
            emit.UnsignedBranchIfNotEqual(notRef);
            emit.LoadArgument(0);
            emit.LoadArgument(0);
            emit.LoadLocalAddress(cellLoc);
            emit.Call(CellAsHeapIndexGetter);
            emit.Call(EngineDerefMethod);
            emit.Call(EngineGetHeapMethod);
            emit.StoreLocal(cellLoc);
            emit.MarkLabel(notRef);

            emit.LoadLocalAddress(cellLoc);
            emit.Call(CellTagIdGetter);
            emit.StoreLocal(tagLoc);
            var notWant = emit.DefineLabel($"inl{u}_notwant");
            emit.LoadLocal(tagLoc);
            emit.LoadConstant((int)(isAtom ? Tag.Atom : Tag.Int));
            emit.UnsignedBranchIfNotEqual(notWant);
            // Bound indexed type → switch on the value to the single clause.
            if (isAtom)
            {
                var keyLoc = emit.DeclareLocal<int>($"inl{u}_key");
                emit.LoadLocalAddress(cellLoc);
                emit.Call(CellAsAtomIdGetter);
                emit.StoreLocal(keyLoc);
                for (int c = 0; c < k; c++)
                {
                    emit.LoadLocal(keyLoc);
                    EmitAtomId(emit, keys[c]);   // patchable: a persisted bundle resolves
                                                 // the runtime atom id at load (a raw
                                                 // build-time id would mismatch a fresh
                                                 // process — the inliner's
                                                 // persisted-bundle correctness bug).
                    emit.BranchIfEqual(detLabels[c]);
                }
            }
            else
            {
                var vLoc = emit.DeclareLocal<long>($"inl{u}_v");
                emit.LoadLocalAddress(cellLoc);
                emit.Call(CellAsIntGetter);
                emit.StoreLocal(vLoc);
                for (int c = 0; c < k; c++)
                {
                    emit.LoadLocal(vLoc);
                    emit.LoadConstant((long)keys[c]);
                    emit.BranchIfEqual(detLabels[c]);
                }
            }
            emit.Branch(failLabel);             // bound, no matching key → fail
            emit.MarkLabel(notWant);
            emit.LoadLocal(tagLoc);
            emit.LoadConstant((int)Tag.Ref);
            emit.BranchIfEqual(chainLabel);     // unbound → generate via the chain
            emit.Branch(failLabel);             // bound non-indexed type → fail

            // Deterministic single-clause entries (no CP): the head match
            // re-checks the (already-matched) indexed arg and unifies the rest;
            // a failure on a non-indexed arg falls through to the caller's fail
            // since the unique key leaves no other clause to try.
            for (int c = 0; c < k; c++)
            {
                emit.MarkLabel(detLabels[c]);
                EmitClauseBody(emit, fcode, site.ClauseRanges[c].Start, site.ClauseRanges[c].End,
                    failLabel, Array.Empty<CallSite>(),
                    calleeMap: calleeMap, suppressProceedReturn: true);
                emit.Branch(site.Continuation);
            }
            emit.MarkLabel(chainLabel);
        }

        // The linear clause chain — the generate (unbound-arg) path for an
        // index-eligible fact, or every call for a non-index-eligible one.
        for (int c = 0; c < k; c++)
        {
            if (c > 0) emit.MarkLabel(site.AltLabels[c - 1]);   // backtrack re-entry for clause c+1
            if (c < k - 1)
            {
                emit.LoadArgument(0);                      // engine
                emitSelf(emit);                            // → this PredicateDelegate
                emit.LoadConstant(site.BaseCursor + c);    // alternative cursor (next clause)
                emit.LoadConstant(factArity);              // save the fact's argument registers
                emit.Call(EnginePushIlCpMethod);
            }
            EmitClauseBody(emit, fcode, site.ClauseRanges[c].Start, site.ClauseRanges[c].End,
                failLabel, Array.Empty<CallSite>(),
                calleeMap: calleeMap, suppressProceedReturn: true);
            if (c < k - 1) emit.Branch(site.Continuation);
        }
        emit.MarkLabel(site.Continuation);
    }

    /// <summary>For an all-constant-first-arg fact (every clause's first head
    /// match against arg 0 is a distinct constant of one kind), returns the kind
    /// (<paramref name="isAtom"/>) and the per-clause key. Enables the
    /// index pre-filter; returns false (→ plain linear chain) for any clause
    /// whose first arg is a variable, a compound, a mixed kind, or a duplicate
    /// of another clause's.</summary>
    private static bool TryGetFactFirstArgKeys(
        byte[] code, IReadOnlyList<(int Start, int End)> ranges, out bool isAtom, out int[] keys)
    {
        isAtom = false;
        keys = new int[ranges.Count];
        if (ranges.Count == 0) return false;
        bool? atomKind = null;
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int c = 0; c < ranges.Count; c++)
        {
            if (!TryReadFirstArgConst(code, ranges[c].Start, ranges[c].End, out bool a, out int key))
                return false;
            if (atomKind is null) atomKind = a;
            else if (atomKind.Value != a) return false;   // mixed integer / atom
            if (!seen.Add(key)) return false;             // duplicate key
            keys[c] = key;
        }
        isAtom = atomKind!.Value;
        return true;
    }

    private static bool TryReadFirstArgConst(byte[] code, int start, int end, out bool isAtom, out int key)
    {
        isAtom = false;
        key = 0;
        int pc = start;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Meta) { pc += 6; continue; }
            if (op == Opcode.GetInteger)
            {
                if (BytecodeIO.ReadInt32(code, pc + 5) != 0) return false;
                key = BytecodeIO.ReadInt32(code, pc + 1); isAtom = false; return true;
            }
            if (op == Opcode.GetAtom)
            {
                if (BytecodeIO.ReadInt32(code, pc + 5) != 0) return false;
                key = BytecodeIO.ReadInt32(code, pc + 1); isAtom = true; return true;
            }
            return false;   // first head-match op isn't a constant on arg 0
        }
        return false;
    }

    /// <summary>Shared meta-CP body emitter — used by both the
    /// DynamicMethod path (above) and the persisted path. The
    /// self-reference for re-pushing the meta-CP on each retry routes
    /// through <paramref name="emitSelf"/>.</summary>
    private static void EmitSingleClauseMetaCpBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        int callSiteCount,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        SelfDelegateEmitter emitSelf,
        System.Type selfDelType,
        Dictionary<int, CompiledPredicate>? ruleInlineSites = null)
    {
        var failLabel = emit.DefineLabel("fail");
        var startLabel = emit.DefineLabel("start");
        // a single label per forward-resume cursor. The
        // cursor switch branches here directly; the same label is
        // marked at the post-Call body point. The backtrack-
        // drive bodies are gone — backtracking through the callee's
        // CPs is handled naturally by the engine's CP cascade, with
        // each callee-clause's saved Cp pointing back at our resume
        // marker.
        var resumeLabels = new Sigil.Label[callSiteCount];
        for (int i = 0; i < callSiteCount; i++)
            resumeLabels[i] = emit.DefineLabel($"resume_{i + 1}");

        // inlined multi-clause facts take cursors after the call-site
        // resume cursors (1..callSiteCount), i.e. from callSiteCount+1.
        var inlineSites = ComputeInlineSites(emit, predicate, calleeMap,
            firstCursor: callSiteCount + 1, out _);
        // Case-2 rule inline: the bodies of these callees are emitted
        // inline; their non-tail calls thread through this caller's resume cursors
        // (already counted into callSiteCount → resumeLabels). the
        // runtime path precomputed this when sizing callSiteCount and passes it
        // down; the persisted path (null) computes it here (a no-op under
        // _persistPatches — ComputeRuleInlineSites returns the shared empty map).
        ruleInlineSites ??= ComputeRuleInlineSites(predicate, calleeMap);

        _emitOwnerFid = predicate.FunctorId;
        // Cursor dispatch: 0 → start; N → resume_N; baseCursor+j → inlined
        // fact clause-(j+2) re-entry (the backtrack alternative). Cursors are
        // dense small ints from 0 (ComputeInlineSites allocates contiguous
        // ranges), so this is a single O(1) jump table (IL `switch`) — NOT a
        // linear compare chain. That matters: every backtrack re-enters the
        // delegate HERE, and an inlined fact's generate chain re-enters once per
        // clause alternative; a linear switch would make that O(cursors) and grow
        // with each inline site — making inlining cost MORE than the trampoline it
        // replaces (the trampoline re-enters the callee's own compact dispatch).
        // The jump table keeps re-entry constant, so inlining is strictly cheaper.
        int maxCursor = callSiteCount;
        foreach (var site in inlineSites.Values)
        {
            int last = site.BaseCursor + site.AltLabels.Length - 1;
            if (last > maxCursor) maxCursor = last;
        }
        var cursorLabels = new Sigil.Label[maxCursor + 1];
        for (int i = 0; i <= maxCursor; i++) cursorLabels[i] = startLabel; // 0 + any gap
        for (int i = 0; i < callSiteCount; i++) cursorLabels[i + 1] = resumeLabels[i];
        foreach (var site in inlineSites.Values)
            for (int j = 0; j < site.AltLabels.Length; j++)
                cursorLabels[site.BaseCursor + j] = site.AltLabels[j];

        // CSE (mirrors the region Stage-11 hoist): every inlined-fact
        // clause alternative's PushIlChoicePoint reloads the SAME self-delegate —
        // a per-push holder dictionary probe on the runtime path. Hoist that load
        // to ONE local ahead of the cursor switch (which dominates every push
        // site, including the backtrack re-entries), so each push is a LoadLocal.
        // Gate on ≥2 pushes: below that the hoist's load+store would only grow
        // the method. An inline site with k clauses pushes k−1 CPs = AltLabels.
        SelfDelegateEmitter effectiveSelf = emitSelf;
        int pushSites = 0;
        foreach (var site in inlineSites.Values)
            pushSites += site.AltLabels.Length;
        if (pushSites >= 2)
        {
            var selfDelLoc = emit.DeclareLocal(selfDelType, "mselfdel");
            emitSelf(emit);
            emit.StoreLocal(selfDelLoc);
            effectiveSelf = e => e.LoadLocal(selfDelLoc);
        }

        emit.LoadArgument(1);
        emit.Switch(cursorLabels);
        emit.Branch(startLabel);    // cursor out of range (unreachable) → start

        emit.MarkLabel(startLabel);
        int idxCounter = 0;
        // Self-tail-recursion → in-method loop: startLabel is the
        // cursor-0 entry (the cursor switch above already branched the resume
        // cursors away), so a self Execute branches straight back here.
        EmitClauseBody(emit, predicate.BytecodeUnfused, 0, predicate.BytecodeUnfused.Length,
            failLabel, predicate.CallSites,
            callSiteIndexCounter: () => ++idxCounter,
            resumeLabels: resumeLabels,
            emitSelfDelegate: effectiveSelf,
            calleeMap: calleeMap,
            selfFunctorId: predicate.FunctorId, selfTailLabel: startLabel,
            inlineSites: inlineSites, ruleInlineSites: ruleInlineSites);

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    // Builtins that push a CP and call
    // ResumeAtReturnPc on retry, whose IL call_builtin site needs a resume
    // marker — is now BuiltinEntry.IsBacktrackable, DERIVED by reflection
    // (BacktrackableDetector) from each builtin's IL rather than a hand list, so
    // a new cursor builtin can't be silently forgotten. Every emit-time site
    // reads the per-entry flag.

    /// <summary>Counts non-tail <c>Call</c> opcodes in a clause's
    /// bytecode (Opcode.Call only — Opcode.Execute is the tail-call
    /// form and doesn't need a meta-CP).</summary>
    /// <summary>ADR-025 — true when the bytecode contains an inline
    /// ITE/disjunction, recognised by a try_me_else carrying the body-CP
    /// arity sentinel (never emitted by dispatch skeletons). Cheap pre-filter
    /// for the legacy recognisers whose linear me-else boundary scans would
    /// mis-parse the inline shape. (Previously keyed on the `jump` opcode,
    /// which the branch-tail-LCO shape no longer emits for a last-goal ITE.)</summary>
    private static bool ContainsInlineIteOpcode(byte[] code)
    {
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.TryMeElse
                && pc + 9 <= code.Length
                && BytecodeIO.ReadInt32(code, pc + 5) == OpcodeTable.InlineIteCpArity)
                return true;
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
            if (size <= 0) return false;
            pc += size;
        }
        return false;
    }

    private static int CountNonTailCallOpcodes(byte[] bytecode)
        => CountNonTailCallOpcodes(bytecode, 0, bytecode.Length);

    private static int CountNonTailCallOpcodes(byte[] bytecode, int start, int end)
    {
        int count = 0;
        int pc = start;
        while (pc < end)
        {
            byte b = bytecode[pc];
            if (b == (byte)Opcode.Call) count++;
            // ADR-025 — each inline ITE consumes ONE resume cursor (the ELSE
            // entry). Counted via its try_me_else's body-CP arity SENTINEL,
            // which a dispatch-chain try_me_else never carries. (It used to be
            // counted via the `jump` opcode, but the branch-tail-LCO shape
            // emits no jump when the ITE is the clause's last goal.)
            else if (b == (byte)Opcode.TryMeElse
                     && BytecodeIO.ReadInt32(bytecode, pc + 5) == OpcodeTable.InlineIteCpArity)
                count++;
            // CallBuiltin call/N and CallBuiltin '$call'/2 are
            // also non-tail Calls — they thread through
            // IlMetaCallHelper.Dispatch and need a resume-cursor slot.
            else if (b == (byte)Opcode.CallBuiltin)
            {
                int builtinId = BytecodeIO.ReadInt32(bytecode, pc + 1);
                var e = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                // call/$call thread through IlMetaCallHelper.Dispatch;
                // backtrackable builtins need a resume marker for their
                // CP's resume. Precomputed flags instead of name compares.
                if (e.IsCall || e.IsDollarCall || e.IsBacktrackable) count++;
            }
            var info = OpcodeTable.Get(b);
            if (!info.IsDefined || info.Size == 0) break;
            pc += info.Size;
        }
        return count;
    }

    /// <summary>Emits IL for a contiguous span of supported-opcode
    /// clause-body bytes. <paramref name="failLabel"/> is jumped to on any
    /// unification failure; a successful <c>proceed</c> emits an inline
    /// <c>return true</c>. <paramref name="callSites"/> is consulted by
    /// the Execute emission to resolve each call site's callee functor
    /// id (which is stable across queries, unlike the absolute bytecode
    /// address embedded in the operand).
    ///
    /// <para><paramref name="calleeMap"/> turns on inlining of
    /// small leaf callees: when a Call or Execute site references a
    /// predicate that's in the map and passes <see cref="IsLeafPredicate"/>,
    /// the callee's body opcodes are emitted directly into the caller's
    /// IL stream instead of going through the
    /// <see cref="IlRuntimeHelpers.Call"/> / <c>IlExecuteHelper.Resolve</c>
    /// thunk. Saves a managed call, a Pc-set, and the bytecode-interpreter
    /// re-entry per call site.</para>
    /// <para><paramref name="suppressProceedReturn"/> applies inside the
    /// inlined-Call case: the callee's <c>proceed</c> becomes a fall-through
    /// (the caller has more body to execute after the inlined block)
    /// instead of <c>return true</c>.</para></summary>
    /// <summary>Owner fid threaded through the body emit so
    /// debug markers can identify which predicate's IL each marker
    /// belongs to. Set by the public Compile/CompileInstrumented
    /// entry points and the persisted-assembly path.
    /// Also used by threaded non-tail Call sites
    /// to encode the resume marker (functorId, cursor).
    /// THREAD-STATIC on purpose: compiles run concurrently on the shared
    /// IlCompileWorker AND on engine threads (bundle / persisted builds —
    /// see _labelSeq's note), and this was the one piece of mutable emit
    /// state left plain-static. A concurrent compile clobbering it bakes
    /// ANOTHER predicate's fid into this delegate's resume markers, so a
    /// post-backtrack resume re-enters the WRONG delegate at an arbitrary
    /// cursor — rare, arbitrary corruption far from the cause. Set and read
    /// strictly within one synchronous emit, so thread-static is exact.</summary>
    [System.ThreadStatic]
    private static int _emitOwnerFid;

    /// <summary>when non-null, the emit pipeline is building
    /// a persisted-bundle .dll. Every functor/atom/resume-marker constant
    /// is replaced with a unique sentinel int (drawn from
    /// <see cref="IlPatchSiteCodec.SentinelBase"/>); the corresponding
    /// <see cref="IlPatchSite"/> is appended here so the post-Save PE
    /// scan can locate the sentinel's byte offset and the LoadBundle
    /// path can overwrite it with the runtime-process id. When null,
    /// the runtime <c>DynamicMethod</c> path emits real ids directly
    /// — atom/functor ids are stable in-process so no remap is
    /// needed.</summary>
    [System.ThreadStaticAttribute] private static List<IlPatchSite>? _persistPatches;
    [System.ThreadStaticAttribute] private static int _persistNextSentinel;

    // ADR-022 item 2 — set (by Shumway.Embedding, which owns the block table and
    // interop class) while compiling, so a `'$native_run'('$nb$…', regs)` call is
    // inlined directly into the predicate's IL instead of dispatched as a builtin.
    // Null → no inlining (the call stays a normal builtin dispatch, run via the
    // interpreter / runtime delegate). All emitted calls are MemberRefs (interop +
    // marshalling), which the CLR resolves by name/signature at load — so this is
    // persisted-IL-safe with no entry in the Phase-17 patch table.
    [System.ThreadStaticAttribute]
    private static Shumway.Compiler.NativeC.NativeInlineContext? _nativeInline;

}
