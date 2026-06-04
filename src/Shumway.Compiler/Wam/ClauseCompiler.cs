using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles a single Prolog clause to WAM bytecode. Current scope covers facts,
/// rules with non-trivial bodies, head args ranging over atoms / integers /
/// variables / anonymous / compounds (including lists), and the conjunctive
/// body operator <c>,/2</c>. Disjunction, cut and other control constructs
/// follow in later chunks (8d, 8e).
///
/// <para><b>Head compilation</b> is the two-pass BFS from 8b. Pass 1 handles
/// top-level head arguments, deferring compounds; pass 2 drains the worklist,
/// emitting <c>get_structure</c> / <c>get_list</c> + <c>unify_*</c>. The pass-1
/// dispatcher routes permanent variables (see below) through
/// <c>get_variable_y</c> / <c>get_value_y</c> instead of leaving them in X.</para>
///
/// <para><b>Body compilation</b> uses chunk analysis to classify variables:</para>
/// <list type="bullet">
/// <item>The clause's body is split into chunks at every <c>call</c>. Head + the
///   first body goal share chunk 0; each subsequent goal is its own chunk.</item>
/// <item>A variable that appears in two or more chunks must survive a call and
///   is allocated a <b>permanent</b> Y slot. Variables confined to one chunk
///   stay in X (temporary).</item>
/// </list>
///
/// <para>Each body goal compiles to argument-prep instructions followed by a
/// <c>call</c>. For temp vars: <c>put_variable_x</c> on first occurrence,
/// <c>put_value_x</c> after. For permanents: <c>put_variable_y</c> on first
/// occurrence (initializes Y[i] to a fresh unbound and writes a REF to X[arg]),
/// <c>put_value_y</c> after (copies Y[i] into X[arg]). Atoms / integers / nil
/// use the obvious <c>put_*</c> instructions; compound args use
/// <c>put_structure</c> / <c>put_list</c> + the same <c>unify_*</c> family,
/// with nested compounds going through the BFS worklist as in the head.</para>
///
/// <para><b>Allocate / deallocate</b> wrap multi-chunk bodies. The clause starts
/// with <c>allocate N</c> (N = number of permanents); the last goal's
/// argument-prep is followed by <c>deallocate; execute target</c>. Single-chunk
/// bodies don't need a frame and the only goal is a tail call
/// (<c>put-args; execute target</c>).</para>
///
/// <para><b>Last call optimization (LCO)</b>: the last body goal always uses
/// <c>execute</c> instead of <c>call</c>, so the engine doesn't push a return
/// frame just to come right back. The <c>deallocate</c> precedes the
/// <c>execute</c> when a frame is active, freeing the Y slots before transfer.</para>
///
/// <para>Inter-clause references are emitted with the target operand set to 0.
/// Each <c>call</c> / <c>execute</c> is recorded in
/// <see cref="CompiledClause.CallSites"/> so the linker (the test harness for
/// now; a real linker comes with the bundler) can patch the operand once all
/// clauses' addresses are known.</para>
/// </summary>
public sealed class ClauseCompiler
{
    private LiteralPool<string> _stringLiterals = new();
    private LiteralPool<double> _floatLiterals = new();
    private LiteralPool<System.Numerics.BigInteger> _bigIntLiterals = new();


    public CompiledClause Compile(Clause clause)
        => Compile(clause,
            new LiteralPool<string>(),
            new LiteralPool<double>(),
            new LiteralPool<System.Numerics.BigInteger>());

    public CompiledClause Compile(
        Clause clause,
        LiteralPool<string> stringLiterals,
        LiteralPool<double> floatLiterals,
        LiteralPool<System.Numerics.BigInteger> bigIntLiterals)
    {
        ArgumentNullException.ThrowIfNull(clause);
        _stringLiterals = stringLiterals;
        _floatLiterals = floatLiterals;
        _bigIntLiterals = bigIntLiterals;

        switch (clause.Kind)
        {
            case ClauseKind.Fact:
                return CompileClauseTerm(clause.Term, bodyTerm: null);
            case ClauseKind.Rule:
                CompoundTerm rule = (CompoundTerm)clause.Term;
                Term head = rule.Args[0];
                Term body = rule.Args[1];
                if (body is AtomTerm { Name: "true" })
                    return CompileClauseTerm(head, bodyTerm: null);
                return CompileClauseTerm(head, body);
            case ClauseKind.Directive:
                throw new NotSupportedException(
                    "Directives are handled by ClauseReader, not by the clause compiler.");
            case ClauseKind.DcgRule:
                throw new NotSupportedException(
                    "DCG rules require a separate translation pass — not yet implemented.");
            default:
                throw new InvalidOperationException($"Unknown clause kind: {clause.Kind}.");
        }
    }

    private CompiledClause CompileClauseTerm(Term headTerm, Term? bodyTerm)
    {
        (string name, Term[] headArgs) = DecomposeHead(headTerm);
        List<Term> goals = bodyTerm is null ? new List<Term>() : FlattenConjunction(bodyTerm);

        // ADR-018: `X is Expr` and the six comparisons compile to the
        // arithmetic instruction set (a_eval_*) in CompileBodyGoal — no term,
        // no synthetic variables. (The chunk-295/296 goal-rewriting ArithInline
        // is superseded.)

        // For each named (non-anonymous) variable, record which chunk indices it
        // appears in. Chunk 0 = head + first goal; chunk i >= 1 = goal i.
        var permanents = ClassifyPermanents(headArgs, goals);

        // Cut analysis: a `!` after position 0 in the goal list is a deep cut
        // and needs an extra Y slot to hold the cut barrier (captured by
        // get_level at body start). A `!` at position 0 is a neck cut and
        // uses the engine's _b0 register directly — no slot required.
        bool needsDeepCut = false;
        for (int i = 1; i < goals.Count; i++)
        {
            if (goals[i] is AtomTerm { Name: "!" })
            {
                needsDeepCut = true;
                break;
            }
        }

        int cutSlot = needsDeepCut ? permanents.Count : -1;
        var state = new CompileState(
            headArgs.Length,
            permanents,
            extraPermanentSlots: needsDeepCut ? 1 : 0);

        // Frame is required to (a) host permanent Y slots / the cut-barrier
        // slot, or (b) preserve the caller's CP across a non-tail CALL. An
        // inline goal — a cut or arithmetic (`is`/comparisons → a_int_*/a_eval_*)
        // — does NOT clobber CP, so a body that is inline goals followed by at
        // most one final (tail) call needs no frame. (Phase 26 B: a neck cut
        // before the single recursive call no longer forces an empty
        // `allocate [0]`, matching GProlog.) A real call BEFORE the last goal
        // does need the frame, since it overwrites CP and we must still return.
        bool needFrame = permanents.Count > 0 || needsDeepCut;
        if (!needFrame)
            for (int i = 0; i < goals.Count - 1; i++)
                if (!IsInlineBodyGoal(goals[i])) { needFrame = true; break; }
        // Chunk 220 — fuse the common Allocate+GetLevel prologue when both
        // are emitted; otherwise emit individually.
        if (needFrame && needsDeepCut)
        {
            state.Emitter.EmitAllocateGetLevel(state.PermanentCount, cutSlot);
        }
        else
        {
            if (needFrame)
                state.Emitter.EmitAllocate(state.PermanentCount);
            if (needsDeepCut)
                state.Emitter.EmitGetLevel(cutSlot);
        }

        // Argument-register preferencing (must run before the head is compiled,
        // so a head-extracted variable's unify_variable targets its call-arg
        // register directly).
        ComputePreferredArgRegisters(headArgs, goals, permanents, state.PreferredReg);

        // ----- Head -----
        for (int i = 0; i < headArgs.Length; i++)
            CompileHeadArg(state, headArgs[i], i);
        DrainPendingCompounds(state);

        // ----- Pre-body preparation -----
        // First, bump the anonymous-slot counter so any temp register handed
        // out during body emission lives outside the body's argument range
        // (otherwise nested-compound captures would clash with put_* writes
        // to the same slot — see ReserveBodyArgRegisters for the example).
        // Then run the head-var preservation pass for the argument-shuffling
        // fix.
        ReserveBodyArgRegisters(state, goals);
        int[]? firstGoalArgOrder = WarrenScheduleFirstGoal(state, goals);

        // ----- Body goals -----
        // Per-call env trimming (chunk 57): for each Call / CallBuiltin
        // emission, compute how many Y slots are still live AFTER the
        // call returns. The interpreter trims the frame accordingly so
        // subsequent CPs / sub-frames pack tightly. The vector is
        // indexed by goal position.
        int[] liveAfter = ComputeLivePermsAfterEachGoal(
            goals, state.Ys, cutSlot, state.PermanentCount);
        if (goals.Count == 0)
        {
            // Pure fact / trivial-body rule.
            state.Emitter.EmitProceed();
        }
        else
        {
            for (int i = 0; i < goals.Count; i++)
            {
                bool isLast = i == goals.Count - 1;
                Term goal = goals[i];

                if (goal is AtomTerm { Name: "!" })
                {
                    // Cut emission. Neck cut at position 0 reads _b0 directly;
                    // deep cut anywhere else reads the saved barrier from
                    // Y[cutSlot].
                    if (i == 0)
                        state.Emitter.EmitNeckCut();
                    else
                        state.Emitter.EmitCut(cutSlot);

                    if (isLast)
                    {
                        // `!` as the final goal: no execute/call follows.
                        // Just close the frame (if any) and return to caller.
                        // Chunk 220 — fuse Deallocate+Proceed when both fire.
                        if (needFrame)
                            state.Emitter.EmitDeallocateProceed();
                        else
                            state.Emitter.EmitProceed();
                    }
                }
                else
                {
                    int[]? thisOrder =
                        i == FirstScheduledCallIndex(goals) ? firstGoalArgOrder : null;
                    CompileBodyGoal(state, goal, isLast, needFrame, liveAfter[i], thisOrder);
                }
            }
        }

        int functorId = InternFunctor(name, headArgs.Length);
        return new CompiledClause(
            state.Emitter.ToBytes(),
            functorId,
            headArgs.Length,
            state.Xs.RegisterCount,
            state.PermanentCount,
            state.CallSites);
    }

    // ============================================================================
    // Chunk classification
    // ============================================================================

    /// <summary>Walks the head and body, collecting the set of chunks each named
    /// variable appears in. Returns the names that appear in at least two
    /// chunks — those need permanent (Y) storage to survive an intervening call.
    /// The result is deterministic (insertion-ordered) so Y slot indices are
    /// stable for given source.</summary>
    private static List<string> ClassifyPermanents(Term[] headArgs, List<Term> goals)
    {
        var occurs = new Dictionary<string, HashSet<int>>();
        var order = new List<string>();

        void Visit(Term t, int chunk)
        {
            switch (t)
            {
                case VarTerm v when v.Name != "_":
                    if (!occurs.TryGetValue(v.Name, out var s))
                    {
                        occurs[v.Name] = s = new HashSet<int>();
                        order.Add(v.Name);
                    }
                    s.Add(chunk);
                    break;
                case CompoundTerm c:
                    foreach (Term arg in c.Args) Visit(arg, chunk);
                    break;
            }
        }

        // Head is in chunk 0.
        foreach (Term arg in headArgs) Visit(arg, 0);
        // EXPERIMENT: neck cut (position 0) is transparent — does not end a chunk.
        int chunk = 0;
        for (int i = 0; i < goals.Count; i++)
        {
            Visit(goals[i], chunk);
            bool neckCut = i == 0 && goals[i] is AtomTerm { Name: "!" };
            if (!neckCut) chunk++;
        }

        var perms = new List<string>();
        foreach (string name in order)
            if (occurs[name].Count >= 2)
                perms.Add(name);
        return perms;
    }

    /// <summary>Computes argument-register preferencing: a head-extracted
    /// temporary variable whose single body use is a first-goal call argument is
    /// allocated directly into that call's argument register, eliminating the
    /// redundant <c>unify_variable_x temp</c> + <c>put_value_x temp, argReg</c>
    /// pair (the put is auto-skipped once the variable already lives in the arg
    /// register). Safe because, by the time the variable is extracted, the
    /// destination register is free: it was a non-variable head argument
    /// (<c>headArgs[R]</c> is not a bare variable) whose <c>get_*</c> has
    /// consumed it, and the head argument's binding lives on the heap, not in the
    /// register — so reusing the register is sound in both read and write mode.
    ///
    /// <para>Conditions for preferencing variable V to register R: V is not a
    /// permanent; V occurs exactly once in the whole body, as a depth-1 argument
    /// at position R of the FIRST goal (so no intervening goal can clobber
    /// register R before the use); V appears in exactly one head argument, at
    /// index i ≥ R, and nested (not as the top-level head argument itself); and
    /// <c>headArgs[R]</c> is a non-variable. At most one variable is preferenced
    /// to a given register.</para></summary>
    private static void ComputePreferredArgRegisters(
        Term[] headArgs, List<Term> goals,
        IReadOnlyCollection<string> permanents, Dictionary<string, int> outPreferred)
    {
        // Target the first CALL of chunk 0 — which, with a transparent neck cut
        // (chunk 309), may sit at index 1. Mirrors the Warren scheduler so a
        // head var preferenced into its register is the one the scheduler then
        // leaves in place (Phase 26 A: `p :- !, recur(Args)` extracts Args
        // straight into the recursive call's argument registers, no put_value).
        if (goals.Count == 0) return;
        if (goals[FirstScheduledCallIndex(goals)] is not CompoundTerm firstGoal) return;

        // Total body occurrences (any depth) per variable.
        var bodyCount = new Dictionary<string, int>();
        foreach (Term g in goals) CountVarOccurrences(g, bodyCount);

        // The single head-argument index each variable appears in (-1 if it
        // appears in more than one), plus the set that appears as a bare
        // top-level head argument (already register-resident).
        var headArgIndex = new Dictionary<string, int>();
        var headTopLevel = new HashSet<string>();
        var seenInThisArg = new HashSet<string>();
        for (int i = 0; i < headArgs.Length; i++)
        {
            if (headArgs[i] is VarTerm tv && tv.Name != "_") headTopLevel.Add(tv.Name);
            seenInThisArg.Clear();
            CollectVarNames(headArgs[i], seenInThisArg);
            foreach (string name in seenInThisArg)
                headArgIndex[name] = headArgIndex.ContainsKey(name) ? -1 : i;
        }

        var permSet = permanents as ISet<string> ?? new HashSet<string>(permanents);
        for (int r = 0; r < firstGoal.Args.Length; r++)
        {
            if (firstGoal.Args[r] is not VarTerm v || v.Name == "_") continue;
            string name = v.Name;
            if (permSet.Contains(name)) continue;                 // temporaries only
            if (bodyCount.GetValueOrDefault(name) != 1) continue; // single body use
            if (headTopLevel.Contains(name)) continue;            // already arg-resident
            if (!headArgIndex.TryGetValue(name, out int i) || i < 0) continue; // one head arg
            if (r > i) continue;                                  // register r free at extraction
            if (headArgs[r] is VarTerm) continue;                 // register r holds no named var
            if (outPreferred.ContainsValue(r)) continue;          // one variable per register
            outPreferred[name] = r;
        }
    }

    private static void CountVarOccurrences(Term t, Dictionary<string, int> counts)
    {
        switch (t)
        {
            case VarTerm v when v.Name != "_":
                counts[v.Name] = counts.GetValueOrDefault(v.Name) + 1;
                break;
            case CompoundTerm c:
                foreach (Term a in c.Args) CountVarOccurrences(a, counts);
                break;
        }
    }

    /// <summary>Bumps the X-register counter so any further anonymous
    /// allocations land beyond the largest body goal's argument range. This
    /// is essential to avoid <see cref="VariableMap.AllocateAnonymousSlot"/>
    /// handing out a register that the compiler is about to put a body
    /// argument into. (Concrete bug it fixes: for <c>X is -(...)</c>, the
    /// body has is/2 at arity 2 so X[0] and X[1] are live during arg setup;
    /// without this bump the nested-compound capture inside <c>-/1</c> at
    /// arg 1 would get handed slot 1 from AllocateAnonymousSlot, clashing
    /// with the put_structure that just wrote X[1] = Ref(outer).)</summary>
    private static void ReserveBodyArgRegisters(CompileState s, List<Term> goals)
    {
        int max = 0;
        foreach (var g in goals)
        {
            if (g is AtomTerm { Name: "!" }) continue;
            int arity = g is CompoundTerm c ? c.Args.Length : 0;
            if (arity > max) max = arity;
        }
        if (max > 0) s.Xs.EnsureFreeAtLeast(max);
    }

    /// <summary>Warren's classical argument-shuffling scheduler for the
    /// first body goal. Replaces the upfront conservative
    /// <c>PreserveClobberedHeadVars</c> with a per-call dependency-graph
    /// approach: emit only the saves needed to break cycles or self-
    /// clobbers, then topologically order the arg puts so each one's X
    /// reads fire before the writer of the read slot.
    ///
    /// <para>Three sources of saves:</para>
    /// <list type="number">
    /// <item><description><b>Forced saves</b> — vars referenced at depth
    /// ≥ 2 inside a top-level compound. Their <c>unify_value_x</c> reads
    /// happen during <c>DrainPendingCompounds</c>, after every main
    /// <c>put_*</c> has clobbered the arg slots, so the home must be
    /// preserved upfront.</description></item>
    /// <item><description><b>Self-loop saves</b> — top-level compound at
    /// dst <c>i</c> with a direct (depth-1) flat-var sub-arg whose home
    /// is <c>i</c>. The <c>put_structure</c>/<c>put_list</c> clobbers
    /// X[i] before the inner <c>unify_value_x</c> reads it.</description></item>
    /// <item><description><b>Cycle-breaking saves</b> — when the
    /// cross-arg dependency graph has a cycle (e.g. classical swap
    /// <c>foo(X, Y) :- bar(Y, X)</c>), one head-var save breaks the
    /// cycle and the remaining graph topo-sorts.</description></item>
    /// </list>
    /// <para>Only the first body goal is scheduled. Head-vars relevant
    /// to scheduling live in X only when they appear exclusively in
    /// chunk 0 (head + first goal); any head-var referenced by a later
    /// goal is permanent and resides in Y, which <c>put_*</c> writes
    /// never touch.</para></summary>
    /// <summary>Index of the first body CALL the Warren scheduler targets — the
    /// first goal of chunk 0. A neck cut (position 0) is chunk-transparent (see
    /// <see cref="ClassifyPermanents"/>), so the call sits at index 1; otherwise
    /// index 0.</summary>
    /// <summary>A body goal that compiles to inline opcodes which never clobber
    /// the continuation pointer: the cut (<c>!</c>) and arithmetic (<c>is/2</c>
    /// and the six comparisons → <c>a_int_*</c> / <c>a_eval_*</c>). Such a goal
    /// needs no environment frame to survive it.</summary>
    private static bool IsInlineBodyGoal(Term goal) => goal switch
    {
        AtomTerm { Name: "!" } => true,
        CompoundTerm { Functor: "is", Args.Length: 2 } => true,
        CompoundTerm { Args.Length: 2 } c
            when Shumway.Builtins.ArithmeticEvaluator.TryRelOp(c.Functor, out _) => true,
        _ => false,
    };

    private static int FirstScheduledCallIndex(List<Term> goals)
        => goals.Count > 1 && goals[0] is AtomTerm { Name: "!" } ? 1 : 0;

    private static int[]? WarrenScheduleFirstGoal(CompileState s, List<Term> goals)
    {
        if (goals.Count == 0) return null;
        // A neck cut (position 0) is chunk-transparent, so the first CALL of
        // chunk 0 — the goal whose argument shuffle this scheduler fixes — may
        // sit right after it. Schedule that call so a head-var that lives in an
        // argument register gets saved before a later arg reuses its home (the
        // GProlog `get_variable(x4,1)` trick).
        Term first = goals[FirstScheduledCallIndex(goals)];
        if (first is AtomTerm { Name: "!" }) return null;
        if (first is not CompoundTerm c) return null;
        Term[] gArgs = c.Args;
        int N = gArgs.Length;
        if (N == 0) return null;

        // ADR-018: an arithmetic first goal (`is`/2 or a comparison) is compiled
        // to a_int_* / a_eval_* — it reads operands from their register / Y-slot
        // homes and never puts arguments into the low arg registers, so the
        // head-var preservation / arg shuffle below is pure overhead (it would
        // emit a needless put_value_x save of every operand). Skip scheduling;
        // the arith path ignores the returned order anyway.
        if ((c.Functor == "is" && N == 2)
            || (N == 2 && Shumway.Builtins.ArithmeticEvaluator.TryRelOp(c.Functor, out _)))
            return null;

        // Snapshot home → head-var name for X-mapped vars with home < N.
        // Updated as saves rebind vars out of the arg range.
        var homeToVar = new Dictionary<int, string>();
        foreach (var name in s.Xs.Names.ToList())
        {
            if (s.Ys.ContainsKey(name)) continue;
            int home = s.Xs.GetSlot(name);
            if (home < N) homeToVar[home] = name;
        }

        // === Step 1: Forced saves for depth-≥2 reads (drained compounds). ===
        var forced = new HashSet<string>();
        for (int i = 0; i < N; i++)
            CollectForcedSaves(gArgs[i], depth: 0, s, N, forced);
        foreach (string name in forced.OrderBy(n => s.Xs.GetSlot(n)))
        {
            int home = s.Xs.GetSlot(name);
            int safe = s.Xs.AllocateAnonymousSlot();
            s.Emitter.EmitPutValueX(home, safe);
            s.Xs.Rebind(name, safe);
            homeToVar.Remove(home);
        }

        // === Step 2: Self-loop saves (top-level compound at dst i
        //              with direct flat sub-arg whose home is i). ===
        for (int i = 0; i < N; i++)
        {
            if (gArgs[i] is not CompoundTerm cmp) continue;
            foreach (Term sub in cmp.Args)
            {
                if (sub is VarTerm v && v.Name != "_"
                    && !s.Xs.IsNewName(v.Name)
                    && !s.Ys.ContainsKey(v.Name)
                    && s.Xs.GetSlot(v.Name) == i
                    && homeToVar.ContainsKey(i))
                {
                    int safe = s.Xs.AllocateAnonymousSlot();
                    s.Emitter.EmitPutValueX(i, safe);
                    s.Xs.Rebind(v.Name, safe);
                    homeToVar.Remove(i);
                    break;
                }
            }
        }

        // === Step 3: Iteratively break cycles in the cross-arg graph. ===
        var reads = new HashSet<int>[N];
        var writesDst = new bool[N];
        Recompute();

        while (FindCycleNode(reads, writesDst, N) is int cycleNode)
        {
            if (!homeToVar.TryGetValue(cycleNode, out var hv)) break;
            int safe = s.Xs.AllocateAnonymousSlot();
            s.Emitter.EmitPutValueX(cycleNode, safe);
            s.Xs.Rebind(hv, safe);
            homeToVar.Remove(cycleNode);
            Recompute();
        }

        // === Step 4: Topological sort of the now-acyclic graph. ===
        return TopoSort(reads, writesDst, N);

        void Recompute()
        {
            for (int i = 0; i < N; i++)
            {
                reads[i] = ComputeDirectReads(gArgs[i], s, N);
                writesDst[i] = ArgWritesDst(gArgs[i], i, s);
            }
        }
    }

    /// <summary>Walks <paramref name="t"/> and, for each <see cref="VarTerm"/>
    /// at depth ≥ 2 (i.e. inside a sub-compound that will be drained via
    /// <see cref="DrainPendingCompounds"/> after every main put_* has
    /// fired), adds its head-var name to <paramref name="sink"/> when the
    /// var is an X-mapped head-var with home in <c>[0, N)</c>. Depth 0 is
    /// the top-level body arg itself; depth 1 is a direct sub-arg of a
    /// top-level compound (read during the arg's own emission); depth 2+
    /// requires upfront preservation.</summary>
    private static void CollectForcedSaves(
        Term t, int depth, CompileState s, int N, HashSet<string> sink)
    {
        if (t is CompoundTerm c)
        {
            foreach (Term sub in c.Args)
                CollectForcedSaves(sub, depth + 1, s, N, sink);
        }
        else if (t is VarTerm v && v.Name != "_" && depth >= 2)
        {
            if (s.Ys.ContainsKey(v.Name)) return;
            if (s.Xs.IsNewName(v.Name)) return;
            int home = s.Xs.GetSlot(v.Name);
            if (home < N) sink.Add(v.Name);
        }
    }

    /// <summary>Set of X-register slots in <c>[0, N)</c> that <paramref name="arg"/>'s
    /// own emission would read directly — flat-var sources (and direct
    /// sub-args of a top-level compound). Sub-compounds contribute via
    /// <see cref="CollectForcedSaves"/>, not here.</summary>
    private static HashSet<int> ComputeDirectReads(Term arg, CompileState s, int N)
    {
        var reads = new HashSet<int>();
        switch (arg)
        {
            case VarTerm v when v.Name != "_" && !s.Ys.ContainsKey(v.Name) && !s.Xs.IsNewName(v.Name):
                int slot = s.Xs.GetSlot(v.Name);
                if (slot < N) reads.Add(slot);
                break;
            case CompoundTerm c:
                foreach (Term sub in c.Args)
                {
                    if (sub is VarTerm sv && sv.Name != "_"
                        && !s.Ys.ContainsKey(sv.Name)
                        && !s.Xs.IsNewName(sv.Name))
                    {
                        int subSlot = s.Xs.GetSlot(sv.Name);
                        if (subSlot < N) reads.Add(subSlot);
                    }
                }
                break;
        }
        return reads;
    }

    /// <summary>True iff <paramref name="arg"/> at dst <paramref name="dst"/>
    /// emits something that writes <c>X[dst]</c>. A flat-var arg whose
    /// current X home is its dst is a no-op (the
    /// <c>CompileBodyArg</c> dispatcher skips the <c>put_value_x</c>
    /// when src == dst); every other arg shape writes its dst.</summary>
    private static bool ArgWritesDst(Term arg, int dst, CompileState s)
    {
        if (arg is VarTerm v && v.Name != "_"
            && !s.Ys.ContainsKey(v.Name)
            && !s.Xs.IsNewName(v.Name)
            && s.Xs.GetSlot(v.Name) == dst)
        {
            return false;
        }
        return true;
    }

    /// <summary>Finds any node that participates in a cycle of the arg
    /// dependency graph (edge <c>i → j</c> iff <c>j ∈ reads[i]</c> and
    /// <c>j ≠ i</c> and arg <c>j</c> writes <c>X[j]</c>). Returns the
    /// node's index, or <c>null</c> when the graph is acyclic. Iterative
    /// DFS with three-colour marking keeps the stack bounded.</summary>
    private static int? FindCycleNode(HashSet<int>[] reads, bool[] writesDst, int N)
    {
        var color = new int[N]; // 0 white, 1 gray, 2 black
        for (int start = 0; start < N; start++)
        {
            if (color[start] != 0) continue;
            var stack = new Stack<(int Node, List<int>.Enumerator Iter)>();
            color[start] = 1;
            stack.Push((start, Successors(reads[start], start, writesDst, N).GetEnumerator()));
            while (stack.Count > 0)
            {
                var top = stack.Pop();
                var iter = top.Iter;
                if (iter.MoveNext())
                {
                    int next = iter.Current;
                    stack.Push((top.Node, iter));
                    if (color[next] == 1) return next;
                    if (color[next] == 0)
                    {
                        color[next] = 1;
                        stack.Push((next, Successors(reads[next], next, writesDst, N).GetEnumerator()));
                    }
                }
                else
                {
                    color[top.Node] = 2;
                }
            }
        }
        return null;
    }

    private static List<int> Successors(HashSet<int> r, int self, bool[] writesDst, int N)
    {
        var list = new List<int>();
        foreach (int j in r)
            if (j != self && j < N && writesDst[j])
                list.Add(j);
        list.Sort();
        return list;
    }

    /// <summary>Kahn's topological sort of the arg dependency graph,
    /// preferring lower indices on ties for deterministic output.</summary>
    private static int[] TopoSort(HashSet<int>[] reads, bool[] writesDst, int N)
    {
        var inDeg = new int[N];
        var outEdges = new List<int>[N];
        for (int i = 0; i < N; i++) outEdges[i] = new List<int>();
        for (int i = 0; i < N; i++)
        {
            foreach (int j in reads[i])
            {
                if (j == i || j >= N || !writesDst[j]) continue;
                outEdges[i].Add(j);
                inDeg[j]++;
            }
        }
        var result = new int[N];
        int outIdx = 0;
        var ready = new SortedSet<int>();
        for (int i = 0; i < N; i++)
            if (inDeg[i] == 0) ready.Add(i);
        while (ready.Count > 0)
        {
            int n = ready.Min;
            ready.Remove(n);
            result[outIdx++] = n;
            foreach (int succ in outEdges[n])
                if (--inDeg[succ] == 0) ready.Add(succ);
        }
        if (outIdx != N)
            for (int i = 0; i < N; i++) result[i] = i;
        return result;
    }

    private static void CollectVarNames(Term t, HashSet<string> sink)
    {
        switch (t)
        {
            case VarTerm v when v.Name != "_":
                sink.Add(v.Name);
                break;
            case CompoundTerm c:
                foreach (var arg in c.Args) CollectVarNames(arg, sink);
                break;
        }
    }

    private static List<Term> FlattenConjunction(Term body)
    {
        var goals = new List<Term>();
        var stack = new Stack<Term>();
        stack.Push(body);
        while (stack.Count > 0)
        {
            Term t = stack.Pop();
            if (t is CompoundTerm { Functor: ",", Args.Length: 2 } c)
            {
                stack.Push(c.Args[1]);
                stack.Push(c.Args[0]);
            }
            else if (t is AtomTerm { Name: "true" })
            {
                // 'true' has no effect — skip it.
            }
            else
            {
                goals.Add(t);
            }
        }
        return goals;
    }

    // ============================================================================
    // Head compilation (extends 8a / 8b with permanent-routed variables)
    // ============================================================================

    /// <summary>Phase 26 — tries to compile <c>A = B</c> inline as head-style
    /// get / unify instead of a call to the <c>=/2</c> builtin. Handles the case
    /// where one side is a temporary (X-register) variable: a SEEN temp unifies
    /// the other side against its register (via <see cref="CompileHeadArg"/>); a
    /// FIRST-OCCURRENCE temp is bound to the other side (built with
    /// <see cref="CompileBodyArg"/> for a non-var, aliased for a seen var).
    /// Returns false — fall back to the builtin call — for permanent (Y)
    /// variables, both-first-occurrence vars, the anonymous variable, and
    /// both-non-var goals.</summary>
    private bool TryCompileUnifyInline(CompileState s, Term a, Term b)
        => TryUnifyVarWithPattern(s, a, b) || TryUnifyVarWithPattern(s, b, a);

    private bool TryUnifyVarWithPattern(CompileState s, Term vTerm, Term p)
    {
        if (vTerm is not VarTerm v || v.Name == "_") return false;
        if (s.Ys.ContainsKey(v.Name)) return false;   // permanent var — fall back

        if (!s.Xs.IsNewName(v.Name))
        {
            // Seen temporary: unify the pattern against V's register, exactly as
            // a head argument is matched.
            CompileHeadArg(s, p, s.Xs.GetSlot(v.Name));
            DrainPendingCompounds(s);
            return true;
        }

        // First-occurrence temporary: V := P.
        switch (p)
        {
            case VarTerm pv when pv.Name != "_"
                    && !s.Ys.ContainsKey(pv.Name) && !s.Xs.IsNewName(pv.Name):
                // V = W where W is a seen temp: V aliases W's register (no opcode).
                s.Xs.Bind(v.Name, s.Xs.GetSlot(pv.Name));
                return true;
            case VarTerm:
                return false;   // V = W with W first-occurrence / permanent / _ — fall back
            default:
                // V = <non-var term>: build the term into V's fresh home.
                int slot = s.Xs.AllocateFresh(v.Name);
                CompileBodyArg(s, p, slot);
                DrainPendingCompounds(s);
                return true;
        }
    }

    private void CompileHeadArg(CompileState s, Term arg, int argSlot)
    {
        switch (arg)
        {
            case AtomTerm a:
                s.Emitter.EmitGetAtom(InternAtom(a.Name), argSlot);
                break;

            case IntTerm n:
                if (FitsInt32(n.Value))
                    s.Emitter.EmitGetInteger((int)n.Value, argSlot);
                else
                    s.Emitter.EmitGetBigInt(
                        _bigIntLiterals.Intern(new System.Numerics.BigInteger(n.Value)),
                        argSlot);
                break;

            case BigIntTerm bn:
                s.Emitter.EmitGetBigInt(_bigIntLiterals.Intern(bn.Value), argSlot);
                break;

            case VarTerm v when v.Name == "_":
                // Anonymous — no constraint, no opcode.
                return;

            case VarTerm v when s.Ys.TryGetValue(v.Name, out int yIdx):
                // Permanent variable. The head provides its first known binding:
                // copy X[argSlot] into Y[yIdx]. Subsequent head occurrences of the
                // same variable get unified against the saved Y.
                if (s.YsInitialized.Contains(v.Name))
                {
                    s.Emitter.EmitGetValueY(yIdx, argSlot);
                }
                else
                {
                    s.Emitter.EmitGetVariableY(yIdx, argSlot);
                    s.YsInitialized.Add(v.Name);
                }
                break;

            case VarTerm v when s.Xs.IsNewName(v.Name):
                // Temp variable, first occurrence. Claim X[argSlot] as its home.
                s.Xs.Bind(v.Name, argSlot);
                break;

            case VarTerm v:
                // Temp variable, subsequent occurrence.
                s.Emitter.EmitGetValueX(s.Xs.GetSlot(v.Name), argSlot);
                break;

            case CompoundTerm c:
                s.Pending.Enqueue((argSlot, c));
                break;

            case FloatTerm f:
                s.Emitter.EmitGetFloat(_floatLiterals.Intern(f.Value), argSlot);
                break;
            case StringTerm str:
                s.Emitter.EmitGetPstr(_stringLiterals.Intern(str.Content), argSlot);
                break;
            default:
                throw new NotSupportedException(
                    $"Head argument type {arg.GetType().Name} is not supported.");
        }
    }

    /// <summary>Drains <see cref="CompileState.Pending"/> until empty. Each item is
    /// a compound that lives at some X slot; expanding it means emitting an open
    /// instruction (<c>get_list</c> or <c>get_structure</c>) and one
    /// <c>unify_*</c> per sub-arg.</summary>
    private void DrainPendingCompounds(CompileState s)
    {
        while (s.Pending.Count > 0)
        {
            var (slot, comp) = s.Pending.Dequeue();
            bool isList = comp.Functor == "." && comp.Args.Length == 2;

            var multiCellTemps = PreEmitMultiCellLiterals(s, comp.Args);

            if (isList)
                s.Emitter.EmitGetList(slot);
            else
                s.Emitter.EmitGetStructure(InternFunctor(comp.Functor, comp.Args.Length), slot);

            for (int i = 0; i < comp.Args.Length; i++)
            {
                if (multiCellTemps.TryGetValue(i, out int t))
                    s.Emitter.EmitUnifyValueX(t);
                else
                    CompileUnifyArg(s, comp.Args[i]);
            }
        }
    }

    /// <summary>Pre-emits <c>put_float</c> / <c>put_pstr</c> for any float or
    /// string literal among the sub-args, allocating an anonymous X slot for
    /// each. Returns a map from sub-arg index to that slot; the caller emits
    /// <c>unify_value_x</c> against the slot in lieu of the inline <c>unify_*</c>.
    ///
    /// <para>Multi-cell literals can't live inline inside a compound being built
    /// in write mode: they'd corrupt the contiguous arg layout and break the
    /// <c>unify_pointer == heap_top</c> invariant for any subsequent
    /// <c>unify_*</c>. By allocating them ahead of the <c>put_structure</c> we
    /// keep arg cells one-each and let the compound just reference the literal
    /// via the temp register.</para></summary>
    private Dictionary<int, int> PreEmitMultiCellLiterals(CompileState s, Term[] args)
    {
        Dictionary<int, int>? temps = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case FloatTerm f:
                    temps ??= new Dictionary<int, int>();
                    int floatSlot = s.Xs.AllocateAnonymousSlot();
                    s.Emitter.EmitPutFloat(_floatLiterals.Intern(f.Value), floatSlot);
                    temps[i] = floatSlot;
                    break;
                case StringTerm str:
                    temps ??= new Dictionary<int, int>();
                    int strSlot = s.Xs.AllocateAnonymousSlot();
                    s.Emitter.EmitPutPstr(_stringLiterals.Intern(str.Content), strSlot);
                    temps[i] = strSlot;
                    break;
            }
        }
        return temps ?? new Dictionary<int, int>();
    }

    private void CompileUnifyArg(CompileState s, Term arg)
    {
        switch (arg)
        {
            case AtomTerm a:
                if (a.Name == "[]")
                    s.Emitter.EmitUnifyNil();
                else
                    s.Emitter.EmitUnifyAtom(InternAtom(a.Name));
                break;

            case IntTerm n:
                if (FitsInt32(n.Value))
                    s.Emitter.EmitUnifyInteger((int)n.Value);
                else
                    s.Emitter.EmitUnifyBigInt(
                        _bigIntLiterals.Intern(new System.Numerics.BigInteger(n.Value)));
                break;

            case BigIntTerm bn:
                s.Emitter.EmitUnifyBigInt(_bigIntLiterals.Intern(bn.Value));
                break;

            case VarTerm v when v.Name == "_":
                s.Emitter.EmitUnifyVoid(1);
                break;

            case VarTerm v when s.Ys.TryGetValue(v.Name, out int yIdx):
                if (s.YsInitialized.Contains(v.Name))
                {
                    s.Emitter.EmitUnifyValueY(yIdx);
                }
                else
                {
                    s.Emitter.EmitUnifyVariableY(yIdx);
                    s.YsInitialized.Add(v.Name);
                }
                break;

            case VarTerm v when s.Xs.IsNewName(v.Name):
                // Argument-register preferencing: extract straight into the
                // call-arg register this variable flows to, so the later
                // put_value_x is skipped (it sees the variable already in place).
                int xFresh;
                if (s.PreferredReg.TryGetValue(v.Name, out int pref))
                {
                    xFresh = pref;
                    s.Xs.Bind(v.Name, pref);
                }
                else
                {
                    xFresh = s.Xs.AllocateFresh(v.Name);
                }
                s.Emitter.EmitUnifyVariableX(xFresh);
                break;

            case VarTerm v:
                s.Emitter.EmitUnifyValueX(s.Xs.GetSlot(v.Name));
                break;

            case CompoundTerm c:
                int temp = s.Xs.AllocateAnonymousSlot();
                s.Emitter.EmitUnifyVariableX(temp);
                s.Pending.Enqueue((temp, c));
                break;

            // FloatTerm and StringTerm are handled upstream by
            // PreEmitMultiCellLiterals — they can't live inline as compound
            // sub-args in write mode without corrupting the heap layout. If
            // one slips through to here it's a bug in the caller.
            default:
                throw new NotSupportedException(
                    $"Unsupported sub-argument type {arg.GetType().Name}.");
        }
    }

    // ============================================================================
    // Body compilation
    // ============================================================================

    private void CompileBodyGoal(CompileState s, Term goal, bool isLast, bool hasFrame, int livePermsAfter, int[]? argOrder = null)
    {
        // Decompose into functor name + args.
        string fName;
        Term[] gArgs;
        switch (goal)
        {
            case AtomTerm a:
                fName = a.Name;
                gArgs = Array.Empty<Term>();
                break;
            case CompoundTerm c:
                fName = c.Functor;
                gArgs = c.Args;
                break;
            case VarTerm v:
                // ISO §7.8.3: a variable in goal position is the
                // meta-call call/1 of that variable. Most Prolog
                // sources (Blint.pl's `ifthen(X,Y) :- X -> !, Y.`,
                // SWI's library, etc.) rely on this. Rewrite to
                // call(X) so the standard meta-call dispatch fires.
                fName = "call";
                gArgs = new Term[] { v };
                break;
            default:
                throw new NotSupportedException(
                    $"Goal type {goal.GetType().Name} is not yet supported in clause bodies.");
        }

        // ADR-018 — arithmetic instruction set. `X is Expr` and the six
        // comparisons compile to a postfix a_eval_* sequence over the eval
        // stack: no expression term, no synthetic variables on the heap.
        if (fName == "is" && gArgs.Length == 2)
        {
            CompileArithIs(s, gArgs[0], gArgs[1], isLast, hasFrame);
            return;
        }
        if (gArgs.Length == 2 &&
            Shumway.Builtins.ArithmeticEvaluator.TryRelOp(fName, out var relOp))
        {
            // Fuse the flat `A cmp B` over simple leaves into one a_int_cmp;
            // otherwise fall back to the postfix a_eval_* sequence.
            if (TryResolveLeaf(s, gArgs[0], out int caK, out int caV)
                && TryResolveLeaf(s, gArgs[1], out int cbK, out int cbV))
            {
                s.Emitter.EmitAIntCmp((int)relOp, caK, caV, cbK, cbV);
            }
            else
            {
                CompileArithExpr(s, gArgs[0]);
                CompileArithExpr(s, gArgs[1]);
                s.Emitter.EmitAEvalCmp((int)relOp);
            }
            EmitArithEpilogue(s, isLast, hasFrame);
            return;
        }

        // Phase 26 — inline `=/2` unification. Compile `Var = Term` with the
        // head-matching machinery (get_* / unify_*) instead of a call to the
        // =/2 builtin (which builds the term separately and dispatches). Mirrors
        // GProlog (`X = [A|B]` → get_list + unify). Only the safe temp-X-var
        // cases are inlined here; permanent (Y) vars and both-non-var goals fall
        // back to the builtin path below. This is a pure codegen change — it does
        // not affect the permanent/temporary classification.
        if (fName == "=" && gArgs.Length == 2
            && TryCompileUnifyInline(s, gArgs[0], gArgs[1]))
        {
            EmitArithEpilogue(s, isLast, hasFrame);
            return;
        }

        // Emit argument-prep for each goal arg. When argOrder is supplied
        // (Warren scheduler picked a topological order to minimise saves),
        // emit in that order; otherwise emit in natural arg order.
        if (argOrder is not null)
        {
            foreach (int i in argOrder)
                CompileBodyArg(s, gArgs[i], i);
        }
        else
        {
            for (int i = 0; i < gArgs.Length; i++)
                CompileBodyArg(s, gArgs[i], i);
        }
        DrainPendingCompounds(s);

        int functorId = InternFunctor(fName, gArgs.Length);

        // Builtin dispatch: if this functor is registered as a builtin, emit
        // call_builtin instead of call/execute. Builtins don't jump — they run
        // inline and return — so there's no "execute_builtin" form; the last-
        // goal path is just "call_builtin; (deallocate; ) proceed".
        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int builtinId))
        {
            // A last goal must not trim the environment. When the clause has
            // no frame the "current" environment is the caller's, and trimming
            // it would discard the caller's still-live Y slots; when the clause
            // does have a frame, the deallocate emitted right after reclaims it
            // anyway. -1 is the interpreter's no-trim sentinel — the parallel
            // to Execute, which carries no trim operand at all.
            s.Emitter.EmitCallBuiltin(builtinId, isLast ? -1 : livePermsAfter);
            if (isLast)
            {
                // Chunk 220 — fuse Deallocate+Proceed for the common end-of-body epilogue.
                if (hasFrame) s.Emitter.EmitDeallocateProceed();
                else s.Emitter.EmitProceed();
            }
            return;
        }

        if (isLast)
        {
            // Last-call optimization: deallocate (if a frame is live) then execute.
            if (hasFrame)
                s.Emitter.EmitDeallocate();
            int execPos = s.Emitter.Position;
            s.Emitter.EmitExecute(targetAddress: 0);
            s.CallSites.Add(new CallSite(execPos, functorId, IsExecute: true));
        }
        else
        {
            int callPos = s.Emitter.Position;
            s.Emitter.EmitCall(targetAddress: 0, numLivePermanents: livePermsAfter);
            s.CallSites.Add(new CallSite(callPos, functorId, IsExecute: false));
        }
    }

    /// <summary>For each body-goal position <c>i</c>, computes how many Y
    /// slots are still live <em>after</em> goal <c>i</c> completes — i.e.
    /// referenced by any later goal. The result is one more than the
    /// highest Y index used in <c>goals[i+1..]</c> (or 0 when no later
    /// goal touches any permanent). Walking right-to-left in a single
    /// pass keeps the computation linear in clause length.
    ///
    /// <para>The deep-cut Y slot (if one was allocated) counts as a
    /// "permanent" for trimming purposes: it must survive every call
    /// up to the deep <c>!</c> that reads it. <paramref name="cutSlot"/>
    /// is the Y index of that slot when applicable, or -1 when there's
    /// no deep cut.</para></summary>
    private static int[] ComputeLivePermsAfterEachGoal(
        List<Term> goals, IReadOnlyDictionary<string, int> ys,
        int cutSlot, int totalPerms)
    {
        int n = goals.Count;
        var result = new int[n];
        int maxLiveYIdx = -1;
        for (int i = n - 1; i >= 0; i--)
        {
            // result[i] is the live count AFTER goal i, so it reflects
            // accumulated uses from goals[i+1..n-1] only.
            result[i] = Math.Min(maxLiveYIdx + 1, totalPerms);
            // Now fold in goal[i]'s own usage so result[i-1] sees it.
            if (goals[i] is AtomTerm { Name: "!" })
            {
                // Deep cut at position > 0 reads Y[cutSlot]; neck cut at
                // position 0 reads _b0 directly and doesn't touch any Y.
                if (i > 0 && cutSlot >= 0 && cutSlot > maxLiveYIdx)
                    maxLiveYIdx = cutSlot;
            }
            else
            {
                UpdateMaxLiveYIdxFromTerm(goals[i], ys, ref maxLiveYIdx);
            }
        }
        return result;
    }

    private static void UpdateMaxLiveYIdxFromTerm(
        Term t, IReadOnlyDictionary<string, int> ys, ref int maxYIdx)
    {
        switch (t)
        {
            case VarTerm v:
                if (ys.TryGetValue(v.Name, out int idx) && idx > maxYIdx)
                    maxYIdx = idx;
                break;
            case CompoundTerm c:
                foreach (Term arg in c.Args)
                    UpdateMaxLiveYIdxFromTerm(arg, ys, ref maxYIdx);
                break;
        }
    }

    // ---------- ADR-018 arithmetic instruction set compilation ----------

    /// <summary>Constant-folds a fully-literal arithmetic expression at compile
    /// time. Returns the result as a numeric literal term, or false for any
    /// expression with a non-literal leaf (variable, atom constant such as
    /// <c>pi</c>, non-arithmetic compound) or one that raises at evaluation (a
    /// zero divisor, an overflow guard) — those are left to runtime so the
    /// behaviour / error fires exactly as <c>is/2</c> would.</summary>
    private static bool TryFoldConstExpr(Term expr, out Term folded)
    {
        folded = null!;
        if (!TryEvalConst(expr, out Shumway.Builtins.Number n)) return false;
        folded = n.IsFloat ? new FloatTerm(n.FloatValue)
            : n.IsBig ? new BigIntTerm(n.BigValue)
            : new IntTerm(n.IntValue);
        return true;
    }

    private static bool TryEvalConst(Term expr, out Shumway.Builtins.Number result)
    {
        result = default;
        switch (expr)
        {
            case IntTerm i: result = new Shumway.Builtins.Number(i.Value); return true;
            case BigIntTerm b: result = new Shumway.Builtins.Number(b.Value); return true;
            case FloatTerm f: result = new Shumway.Builtins.Number(f.Value); return true;
            case CompoundTerm c when c.Args.Length == 2
                    && Shumway.Builtins.ArithmeticEvaluator.TryBinOp(c.Functor, out var bop):
                if (!TryEvalConst(c.Args[0], out var a2) || !TryEvalConst(c.Args[1], out var b2))
                    return false;
                try { result = Shumway.Builtins.ArithmeticEvaluator.ApplyBin(bop, a2, b2); return true; }
                catch (Exception) { return false; }
            case CompoundTerm c when c.Args.Length == 1
                    && Shumway.Builtins.ArithmeticEvaluator.TryUnOp(c.Functor, out var uop):
                if (!TryEvalConst(c.Args[0], out var a1)) return false;
                try { result = Shumway.Builtins.ArithmeticEvaluator.ApplyUn(uop, a1); return true; }
                catch (Exception) { return false; }
            default: return false;
        }
    }

    /// <summary>Compiles <c>Target is Expr</c>: the postfix evaluation of
    /// <paramref name="expr"/> followed by an <c>a_eval_is</c> that delivers the
    /// popped result to <paramref name="target"/>. The target reaches its home
    /// directly (no scratch copy): an existing variable is unified in place
    /// (kind 3 X-reg / 4 Y-slot); a *first-occurrence* variable is bound by a
    /// plain register/Y store (kind 5 / 6) — no unbound heap cell, no
    /// unification — since the result simply becomes its value. Anything else
    /// (a literal target like <c>5 is 2+3</c>) falls back to a scratch + unify.
    /// No expression term is built.</summary>
    private void CompileArithIs(CompileState s, Term target, Term expr, bool isLast, bool hasFrame)
    {
        // Phase 26 constant folding: a fully-literal arithmetic expression is
        // evaluated at compile time and delivered as a DIRECT unification of the
        // target with the resulting literal — `X is 1*2` becomes `X = 2` (a
        // put_integer; no eval stack, no runtime multiply). The fold reuses the
        // runtime ArithmeticEvaluator, so overflow→bigint, integer division and
        // float coercion are bit-identical to evaluating at run time. An
        // expression that would raise (zero divisor, non-evaluable leaf) is NOT
        // folded — it falls through so the error fires at the right time.
        if (TryFoldConstExpr(expr, out Term folded))
        {
            if (TryCompileUnifyInline(s, target, folded))
            {
                EmitArithEpilogue(s, isLast, hasFrame);
                return;
            }
            // Target the inline =/2 can't take (a permanent Y / literal target):
            // still deliver the folded constant — drops the runtime computation,
            // just keeps the eval-stack delivery below.
            expr = folded;
        }

        // Fuse the flat `Target is A op B` over simple leaf operands into a
        // single a_int_bin (operands resolved before the target, so a
        // first-occurrence target allocation never shadows an operand). Falls
        // through to the postfix a_eval_* sequence for nested / non-leaf cases.
        if (expr is CompoundTerm fc && fc.Args.Length == 2
            && Shumway.Builtins.ArithmeticEvaluator.TryBinOp(fc.Functor, out var fbop)
            && TryResolveLeaf(s, fc.Args[0], out int faK, out int faV)
            && TryResolveLeaf(s, fc.Args[1], out int fbK, out int fbV)
            && TryResolveTarget(s, target, out int ftK, out int ftV))
        {
            s.Emitter.EmitAIntBin((int)fbop, faK, faV, fbK, fbV, ftK, ftV);
            EmitArithEpilogue(s, isLast, hasFrame);
            return;
        }

        CompileArithExpr(s, expr);
        switch (target)
        {
            // Existing variable home — unify the result in place.
            case VarTerm v when v.Name != "_" && s.Ys.TryGetValue(v.Name, out int yIdx)
                    && s.YsInitialized.Contains(v.Name):
                s.Emitter.EmitAEvalIs(4, yIdx);
                break;
            case VarTerm v when v.Name != "_" && !s.Ys.ContainsKey(v.Name)
                    && !s.Xs.IsNewName(v.Name):
                s.Emitter.EmitAEvalIs(3, s.Xs.GetSlot(v.Name));
                break;
            // First-occurrence permanent (Y) variable — store the result and
            // mark it initialised; it never held an unbound var to unify.
            case VarTerm v when v.Name != "_" && s.Ys.ContainsKey(v.Name):
                int newY = s.Ys[v.Name];
                s.YsInitialized.Add(v.Name);
                s.Emitter.EmitAEvalIs(6, newY);   // kind 6 = set Y-slot
                break;
            // First-occurrence temporary (X) variable — store the result into
            // its fresh register home.
            case VarTerm v when v.Name != "_" && s.Xs.IsNewName(v.Name):
                int newX = s.Xs.AllocateFresh(v.Name);
                s.Emitter.EmitAEvalIs(5, newX);   // kind 5 = set X-register
                break;
            // Literal / anonymous / compound target — materialise it and unify.
            default:
                int scratch = s.Xs.AllocateAnonymousSlot();
                CompileBodyArg(s, target, scratch);
                DrainPendingCompounds(s);
                s.Emitter.EmitAEvalIs(3, scratch);
                break;
        }
        EmitArithEpilogue(s, isLast, hasFrame);
    }

    /// <summary>Emits the postfix instructions that leave the value of
    /// <paramref name="expr"/> on the eval stack. Numeric literals push
    /// directly; an existing variable pushes straight from its register / Y-slot
    /// home; a recognised arithmetic compound recurses then applies its op;
    /// anything else (a first-occurrence variable, an atom, a non-arithmetic
    /// compound) is loaded into a scratch register and pushed via
    /// <c>a_eval_push x-reg</c>, which derefs + arithmetically evaluates it at
    /// run time — handling a bound sub-expression, an unbound var
    /// (instantiation_error) and a non-evaluable term (type_error) exactly as
    /// is/2 does.</summary>
    private void CompileArithExpr(CompileState s, Term expr)
    {
        switch (expr)
        {
            case IntTerm n when FitsInt32(n.Value):
                s.Emitter.EmitAEvalPush(0, (int)n.Value);
                return;
            case IntTerm n:
                s.Emitter.EmitAEvalPush(1,
                    _bigIntLiterals.Intern(new System.Numerics.BigInteger(n.Value)));
                return;
            case BigIntTerm bn:
                s.Emitter.EmitAEvalPush(1, _bigIntLiterals.Intern(bn.Value));
                return;
            case FloatTerm f:
                s.Emitter.EmitAEvalPush(2, _floatLiterals.Intern(f.Value));
                return;
            // An already-bound variable evaluates from its home directly — no
            // copy. (An initialised Y-slot or an existing X register; a
            // first-occurrence variable is unbound and falls through to the
            // scratch path, which reproduces is/2's instantiation_error.)
            case VarTerm v when v.Name != "_" && s.Ys.TryGetValue(v.Name, out int yIdx)
                    && s.YsInitialized.Contains(v.Name):
                s.Emitter.EmitAEvalPush(4, yIdx);
                return;
            case VarTerm v when v.Name != "_" && !s.Ys.ContainsKey(v.Name)
                    && !s.Xs.IsNewName(v.Name):
                s.Emitter.EmitAEvalPush(3, s.Xs.GetSlot(v.Name));
                return;
            case CompoundTerm c when c.Args.Length == 2
                    && Shumway.Builtins.ArithmeticEvaluator.TryBinOp(c.Functor, out var bop):
                CompileArithExpr(s, c.Args[0]);
                CompileArithExpr(s, c.Args[1]);
                s.Emitter.EmitAEvalBin((int)bop);
                return;
            case CompoundTerm c when c.Args.Length == 1
                    && Shumway.Builtins.ArithmeticEvaluator.TryUnOp(c.Functor, out var uop):
                CompileArithExpr(s, c.Args[0]);
                s.Emitter.EmitAEvalUn((int)uop);
                return;
            default:
                int scratch = s.Xs.AllocateAnonymousSlot();
                CompileBodyArg(s, expr, scratch);
                DrainPendingCompounds(s);
                s.Emitter.EmitAEvalPush(3, scratch);   // kind 3 = X-register
                return;
        }
    }

    private static void EmitArithEpilogue(CompileState s, bool isLast, bool hasFrame)
    {
        if (!isLast) return;
        if (hasFrame) s.Emitter.EmitDeallocateProceed();
        else s.Emitter.EmitProceed();
    }

    /// <summary>Resolves a simple leaf operand for the fused a_int_* opcodes to
    /// its <c>(kind, value)</c> encoding: a 32-bit integer literal (kind 0), an
    /// already-bound X register (kind 3) or an initialised Y-slot (kind 4).
    /// Returns false for anything that needs the general path — a
    /// first-occurrence (unbound) variable, a bigint / float literal, an atom,
    /// or a nested compound.</summary>
    private static bool TryResolveLeaf(CompileState s, Term term, out int kind, out int val)
    {
        switch (term)
        {
            case IntTerm n when FitsInt32(n.Value):
                kind = 0; val = (int)n.Value; return true;
            case VarTerm v when v.Name != "_" && s.Ys.TryGetValue(v.Name, out int yIdx)
                    && s.YsInitialized.Contains(v.Name):
                kind = 4; val = yIdx; return true;
            case VarTerm v when v.Name != "_" && !s.Ys.ContainsKey(v.Name)
                    && !s.Xs.IsNewName(v.Name):
                kind = 3; val = s.Xs.GetSlot(v.Name); return true;
            default:
                kind = 0; val = 0; return false;
        }
    }

    /// <summary>Resolves a fused a_int_bin target to its <c>(kind, value)</c>:
    /// unify with an existing X register (3) / Y-slot (4), or store into a
    /// first-occurrence X register (5) / Y-slot (6) — the latter allocating /
    /// marking the variable as the result home. Returns false for a literal /
    /// anonymous / compound target (handled by the general path).</summary>
    private static bool TryResolveTarget(CompileState s, Term target, out int kind, out int val)
    {
        switch (target)
        {
            case VarTerm v when v.Name != "_" && s.Ys.TryGetValue(v.Name, out int yIdx)
                    && s.YsInitialized.Contains(v.Name):
                kind = 4; val = yIdx; return true;
            case VarTerm v when v.Name != "_" && !s.Ys.ContainsKey(v.Name)
                    && !s.Xs.IsNewName(v.Name):
                kind = 3; val = s.Xs.GetSlot(v.Name); return true;
            case VarTerm v when v.Name != "_" && s.Ys.ContainsKey(v.Name):
                val = s.Ys[v.Name]; s.YsInitialized.Add(v.Name); kind = 6; return true;
            case VarTerm v when v.Name != "_" && s.Xs.IsNewName(v.Name):
                val = s.Xs.AllocateFresh(v.Name); kind = 5; return true;
            default:
                kind = 0; val = 0; return false;
        }
    }

    private void CompileBodyArg(CompileState s, Term arg, int argSlot)
    {
        switch (arg)
        {
            case AtomTerm a:
                if (a.Name == "[]")
                    s.Emitter.EmitPutNil(argSlot);
                else
                    s.Emitter.EmitPutAtom(InternAtom(a.Name), argSlot);
                break;

            case IntTerm n:
                if (FitsInt32(n.Value))
                    s.Emitter.EmitPutInteger((int)n.Value, argSlot);
                else
                    s.Emitter.EmitPutBigInt(
                        _bigIntLiterals.Intern(new System.Numerics.BigInteger(n.Value)),
                        argSlot);
                break;

            case BigIntTerm bn:
                s.Emitter.EmitPutBigInt(_bigIntLiterals.Intern(bn.Value), argSlot);
                break;

            case VarTerm v when v.Name == "_":
                // Each anonymous gets a fresh heap unbound at argSlot. We give it
                // its own anonymous X slot too so the put_variable_x has somewhere
                // to dispose its REF.
                int anonSlot = s.Xs.AllocateAnonymousSlot();
                s.Emitter.EmitPutVariableX(anonSlot, argSlot);
                break;

            case VarTerm v when s.Ys.TryGetValue(v.Name, out int yIdx):
                if (s.YsInitialized.Contains(v.Name))
                {
                    s.Emitter.EmitPutValueY(yIdx, argSlot);
                }
                else
                {
                    s.Emitter.EmitPutVariableY(yIdx, argSlot);
                    s.YsInitialized.Add(v.Name);
                }
                break;

            case VarTerm v when s.Xs.IsNewName(v.Name):
                // First-time temp var in body context: allocate a slot, then
                // emit put_variable_x to materialise it on heap and replicate
                // the REF into both slot and argSlot.
                int xFresh = s.Xs.AllocateFresh(v.Name);
                s.Emitter.EmitPutVariableX(xFresh, argSlot);
                break;

            case VarTerm v:
                int existingSlot = s.Xs.GetSlot(v.Name);
                // Optimisation: skip the put_value_x when the variable already
                // lives at the destination register. Eliminates the put_value_x N, N
                // no-ops that show up frequently for clauses like p(X) :- q(X).
                if (existingSlot != argSlot)
                    s.Emitter.EmitPutValueX(existingSlot, argSlot);
                break;

            case CompoundTerm c:
                bool isList = c.Functor == "." && c.Args.Length == 2;
                // Float / string sub-args go through put_*-to-temp + unify_value_x;
                // see PreEmitMultiCellLiterals for why they can't live inline.
                var multiCellTemps = PreEmitMultiCellLiterals(s, c.Args);
                if (isList)
                    s.Emitter.EmitPutList(argSlot);
                else
                    s.Emitter.EmitPutStructure(InternFunctor(c.Functor, c.Args.Length), argSlot);
                // Sub-args run in write mode; the same CompileUnifyArg dispatcher
                // handles them. Nested compounds are deferred onto the pending
                // queue and drained by DrainPendingCompounds.
                for (int i = 0; i < c.Args.Length; i++)
                {
                    if (multiCellTemps.TryGetValue(i, out int t))
                        s.Emitter.EmitUnifyValueX(t);
                    else
                        CompileUnifyArg(s, c.Args[i]);
                }
                break;

            case FloatTerm f:
                s.Emitter.EmitPutFloat(_floatLiterals.Intern(f.Value), argSlot);
                break;
            case StringTerm str:
                s.Emitter.EmitPutPstr(_stringLiterals.Intern(str.Content), argSlot);
                break;
            default:
                throw new NotSupportedException(
                    $"Body argument type {arg.GetType().Name} is not supported.");
        }
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private static (string name, Term[] args) DecomposeHead(Term head)
    {
        return head switch
        {
            AtomTerm a => (a.Name, Array.Empty<Term>()),
            CompoundTerm c => (c.Functor, c.Args),
            _ => throw new NotSupportedException(
                $"Clause head must be an atom or compound, got {head.GetType().Name}."),
        };
    }

    /// <summary>Whether <paramref name="value"/> fits in the WAM bytecode's
    /// 32-bit integer-operand encoding. Anything wider rides the
    /// <c>BigInt</c> literal pool via <see cref="Opcode.GetBigInt"/> /
    /// <see cref="Opcode.PutBigInt"/> / <see cref="Opcode.UnifyBigInt"/>
    /// (see ADR-013).</summary>
    private static bool FitsInt32(long value) =>
        value >= int.MinValue && value <= int.MaxValue;

    private static int InternAtom(string name) =>
        AtomTable.Intern(name, permanent: true).Id;

    private static int InternFunctor(string name, int arity) =>
        FunctorTable.Intern(InternAtom(name), arity);

    /// <summary>
    /// Mutable state threaded through head + body compilation. Owns the byte
    /// buffer, the X / Y allocators, the pending-compound queue, and the list
    /// of call sites the linker will patch.
    /// </summary>
    private sealed class CompileState
    {
        public BytecodeEmitter Emitter { get; } = new();
        public VariableMap Xs { get; }
        public Dictionary<string, int> Ys { get; } = new();
        public HashSet<string> YsInitialized { get; } = new();

        /// <summary>Argument-register preferencing: a first-occurrence,
        /// head-extracted X variable that flows to a single first-goal call
        /// argument is allocated directly into that call's argument register, so
        /// the redundant <c>unify_variable_x temp</c> + <c>put_value_x temp,
        /// argReg</c> collapses to one <c>unify_variable_x argReg</c>. Populated
        /// before head compilation; consumed when the variable's
        /// <c>unify_variable</c> is emitted.</summary>
        public Dictionary<string, int> PreferredReg { get; } = new();
        public int PermanentCount { get; }
        public Queue<(int Slot, CompoundTerm Compound)> Pending { get; } = new();
        public List<CallSite> CallSites { get; } = new();

        public CompileState(int arity, IReadOnlyList<string> permanents, int extraPermanentSlots = 0)
        {
            Xs = new VariableMap(arity);
            for (int i = 0; i < permanents.Count; i++)
                Ys[permanents[i]] = i;
            PermanentCount = permanents.Count + extraPermanentSlots;
        }
    }
}
