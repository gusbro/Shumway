using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles a single Prolog clause to WAM bytecode. Current scope covers facts,
/// rules with non-trivial bodies, head args ranging over atoms / integers /
/// variables / anonymous / compounds (including lists), and the conjunctive
/// body operator <c>,/2</c>. Disjunction, cut and other control constructs
/// follow elsewhere.
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
public sealed partial class ClauseCompiler
{
    private LiteralPool<string> _stringLiterals = new();
    private LiteralPool<double> _floatLiterals = new();
    private LiteralPool<System.Numerics.BigInteger> _bigIntLiterals = new();

    /// <summary>ADR-035 debug codegen (<c>compile_mode=debug</c>). Two changes,
    /// both in service of a debuggable call stack:
    /// <list type="bullet">
    /// <item>every rule clause gets a frame, even one the WAM would happily run
    /// frameless — with no frame there is nothing for a debugger to show, and no
    /// way to turn the last call into a non-tail one;</item>
    /// <item>the clause's final user-predicate call is emitted as
    /// <see cref="Opcode.DebugLastCall"/> plus a return stub, instead of
    /// <c>deallocate; execute</c> — so last-call optimisation becomes a runtime
    /// switch rather than a property of the compiled code.</item>
    /// </list>
    /// Off by default: this is not what release code should look like.</summary>
    public bool DebugCodegen { get; set; }

    /// <summary>ADR-035 — the file to blame when a position does not know its own
    /// (<see cref="Shumway.Compiler.Lexer.SourcePosition.FileId"/> is 0). Only read under
    /// <see cref="DebugCodegen"/>.
    ///
    /// <para>The position wins whenever it has an answer, and it nearly always does: it was
    /// stamped by the lexer that read the file. This fallback is for terms nobody parsed —
    /// a synthetic clause the engine built for a query, say — where the caller's idea of
    /// "the current file" is as good as it gets.</para></summary>
    public int DebugFileId { get; set; }

    /// <summary>ADR-035 — record that a debugger may stop at the instruction about
    /// to be emitted. NOTHING is emitted: a stop site is a note about an offset,
    /// not an instruction. Debug code that nobody is stopping in therefore runs at
    /// full speed, and arming a breakpoint later is a one-byte patch — which is
    /// what every real VM does. A position of 0:0 (a synthetic term one of the
    /// transforms produced, with no source of its own) gets no site: a debugger
    /// must not offer to stop somewhere the user cannot see.</summary>
    private bool _suppressBreaks;

    private void MarkStop(CompileState s, Shumway.Compiler.Lexer.SourcePosition pos)
    {
        if (!DebugCodegen || _suppressBreaks || pos.Line <= 0) return;
        int fileId = pos.FileId != 0 ? pos.FileId : DebugFileId;
        s.DebugStops.Add(new DebugStop(
            s.Emitter.Position, DebugSiteTable.Intern(fileId, pos.Line, pos.Column)));
    }


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

        // ADR-035 — the engine wraps every query in a synthetic __query__ clause. It gets no
        // STOP SITES: there is no source file to stop in, and a debugger stops inside the
        // program, not inside the machinery that launched it.
        //
        // Its VARIABLES are a different matter, and conflating the two was a bug. `X` in
        // `?- X = 41, debugger_break.` is the user's variable — the top level itself prints
        // it as the answer — and a debugger stopped in the query must show it. So the
        // variable machinery below (permanence, frame initialisation, no trimming, the frame
        // map) applies to a query exactly as it does to a clause; only the stop sites do not.
        _suppressBreaks = name == "__query__";

        // ADR-018: `X is Expr` and the six comparisons compile to the
        // arithmetic instruction set (a_eval_*) in CompileBodyGoal — no term,
        // no synthetic variables.

        // For each named (non-anonymous) variable, record which chunk indices it
        // appears in. Chunk 0 = head + first goal; chunk i >= 1 = goal i.
        var permanents = ClassifyPermanents(headArgs, goals);

        // ADR-035 — under debug codegen EVERY named source variable is permanent, not
        // just the ones that outlive a call. A variable the WAM leaves in an X register
        // is unreadable a moment later: the next call overwrites it. A debugger has to
        // be able to show `X` for as long as the clause is on the stack, so debug code
        // pays a Y slot for every one of them. This is the whole reason release code
        // and debug code are not the same code.
        if (DebugCodegen)
            permanents = AllNamedVariables(headArgs, goals, permanents);

        // Register-allocator survey (ADR-021), diag builds only —
        // see DiagYSurvey. Stripped from normal builds.
        DiagYSurvey(name, headArgs, goals, permanents);

        // Cut analysis: a `!` is a NECK cut — reading the engine's _b0 register
        // directly, no barrier slot — when every goal before it is inline
        // Inline goals (`is`/comparisons → a_int_*/a_eval_*, and
        // earlier cuts) never CALL and never push choice points, so _b0 is
        // still the predicate-entry barrier when the cut runs; the pervasive
        // Arity guard shape `p(X) :- X > 0, !, Body.` is thus frameless.
        // Only a `!` after a real call needs the get_level + Y-slot deep cut.
        int inlinePrefixLen = 0;
        while (inlinePrefixLen < goals.Count && IsInlineBodyGoal(goals[inlinePrefixLen]))
            inlinePrefixLen++;
        bool needsDeepCut = false;
        for (int i = 1; i < goals.Count; i++)
        {
            if (goals[i] is AtomTerm { Name: "!" } && i >= inlinePrefixLen)
            {
                needsDeepCut = true;
                break;
            }
        }

        // ADR-025 — inline if-then-else goals surviving MetaTransform. Every
        // named variable inside one is forced PERMANENT (the else-branch resume
        // restores no X registers — the try_me_else CP is arity-0 — so branch
        // state must live in Y slots, exactly what the helper-call form implied),
        // and each `->` construct gets one extra Y slot for its commit barrier.
        var inlineItes = new List<int>();   // goal indices
        for (int i = 0; i < goals.Count; i++)
            if (goals[i] is CompoundTerm { Functor: ";", Args.Length: 2 } d
                && Shumway.Compiler.InlineIte.IsEligible(d))
                inlineItes.Add(i);
        if (inlineItes.Count > 0)
            ForceIteVarsPermanent(goals, inlineItes, permanents);

        int cutSlot = needsDeepCut ? permanents.Count : -1;
        int iteBarrierBase = permanents.Count + (needsDeepCut ? 1 : 0);
        int iteBarrierCount = 0;
        var iteBarrierSlot = new Dictionary<int, int>();   // goal index → Y slot
        foreach (int gi in inlineItes)
            if (((CompoundTerm)goals[gi]).Args[0]
                is CompoundTerm { Functor: "->" or "*->", Args.Length: 2 })
                iteBarrierSlot[gi] = iteBarrierBase + iteBarrierCount++;   // ADR-037: *-> too

        var state = new CompileState(
            headArgs.Length,
            permanents,
            extraPermanentSlots: (needsDeepCut ? 1 : 0) + iteBarrierCount);

        // Frame is required to (a) host permanent Y slots / the cut-barrier
        // slot, or (b) preserve the caller's CP across a non-tail CALL. An
        // inline goal — a cut or arithmetic (`is`/comparisons → a_int_*/a_eval_*)
        // — does NOT clobber CP, so a body that is inline goals followed by at
        // most one final (tail) call needs no frame. (A neck cut
        // before the single recursive call no longer forces an empty
        // `allocate [0]`, matching GProlog.) A real call BEFORE the last goal
        // does need the frame, since it overwrites CP and we must still return.
        bool needFrame = permanents.Count > 0 || needsDeepCut
            // ADR-025 — an inline ITE always needs a frame: its inner goals are
            // non-tail calls (control flows to END, then proceed), so the
            // continuation must be protected even when the ITE is the last goal.
            || inlineItes.Count > 0;
        if (!needFrame)
            for (int i = 0; i < goals.Count - 1; i++)
                if (!IsInlineBodyGoal(goals[i])) { needFrame = true; break; }
        // ADR-035 — under debug codegen every rule clause gets a frame, even the
        // frameless shapes above (a chain rule `p :- q.`, or inline goals then a
        // single tail call). Without one there is no environment for a debugger
        // to attribute to the clause, and the last call could not be made
        // non-tail: debug_lastcall's LCO-off path returns through a stub that
        // needs the frame to restore Cp from.
        if (DebugCodegen && goals.Count > 0)
            needFrame = true;

        // fuse the common Allocate+GetLevel prologue when both
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
        // CSE is valid only while matching the head: a sub-term equal to an
        // already-matched top-level head-argument compound references that
        // compound's (stable) argument register instead of rebuilding it. Once
        // the body starts, the argument registers are overwritten by call setup,
        // so CSE is switched off.
        state.CseActive = true;
        for (int i = 0; i < headArgs.Length; i++)
            CompileHeadArg(state, headArgs[i], i);
        DrainPendingCompounds(state);
        state.CseActive = false;

        // ADR-035 — initialise the frame. The WAM writes a Y slot at the variable's
        // first occurrence and not before, so until then the slot holds whatever the
        // stack happened to contain. Running code never looks, but a debugger does —
        // and stack garbage can look exactly like a valid heap reference, so it would
        // not fail loudly, it would print a plausible lie. Every source variable the
        // head did not already bind therefore gets a fresh unbound variable here, and
        // reports itself honestly as unbound until something binds it.
        //
        // The pre-initialised cell IS the variable from here on: the names are added to
        // YsInitialized below, so the body's first occurrence compiles as the VALUE
        // flavour (put_value / set_value / unify_value) against this cell instead of
        // put_variable allocating a fresh one. Semantically identical — either way the
        // goal receives an unbound heap cell — but it makes the cell the debugger shows
        // in Locals the cell the program actually uses, with two consequences that
        // matter: a variable keeps ONE identity across its life (no _G rename when the
        // first occurrence executes), and a binding the debugger commits INTO the frame
        // before the first occurrence (ADR-035 D5+ bind-into-frame) is seen by the
        // program instead of being orphaned by put_variable's fresh cell.
        if (DebugCodegen && needFrame)
        {
            int scratch = -1;
            foreach (var (varName, slot) in state.Ys)
            {
                if (varName.Length == 0 || varName[0] == '_') continue;
                if (state.YsInitialized.Contains(varName)) continue;
                if (scratch < 0) scratch = state.Xs.AllocateAnonymousSlot();
                state.Emitter.EmitPutVariableY(slot, scratch);
                state.YsInitialized.Add(varName);
            }
        }

        // ADR-035 — a FACT's stop site, placed after the head has matched rather than at
        // the clause's first byte. Two things fall out of that, both of them what a
        // debugger wants: a clause whose head does not match is never stopped in (the
        // user asked to stop when THIS clause runs, not when it is tried and rejected),
        // and by the time we stop, the head arguments are bound — so the fact's
        // variables can be read.
        //
        // A RULE gets no entry site, because it would not be a place of its own: with
        // the head matched, the very next instruction is the first body goal's, and that
        // goal has a site already. "The clause was entered" and "the first goal is about
        // to run" are the same point in the machine, and one point deserves one stop. A
        // breakpoint on a rule's head line snaps forward to it (AddBreakpoint), the way
        // a breakpoint on a line with no code of its own always has.
        if (goals.Count == 0)
            MarkStop(state, headTerm.Position);

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
        // Per-call env trimming: for each Call / CallBuiltin
        // emission, compute how many Y slots are still live AFTER the
        // call returns. The interpreter trims the frame accordingly so
        // subsequent CPs / sub-frames pack tightly. The vector is
        // indexed by goal position.
        int[] liveAfter = ComputeLivePermsAfterEachGoal(
            goals, state.Ys, cutSlot, state.PermanentCount, iteBarrierSlot);
        // ADR-035 — debug codegen does not trim. Trimming discards the Y slots a call
        // does not need afterwards, which is exactly the debugger's problem: a variable
        // the clause is done with is still one the user can see on the line they are
        // stopped at. So every call keeps the whole frame.
        if (DebugCodegen)
            for (int i = 0; i < liveAfter.Length; i++) liveAfter[i] = state.PermanentCount;

        if (goals.Count == 0)
        {
            // Pure fact / trivial-body rule. Under debug codegen a fact whose head has
            // variables now HAS a frame (they were made permanent so the debugger can
            // read them), and a frame that is allocated must be deallocated: leaving it
            // would hand the caller a stale E, and it would read its own Y slots out of
            // the wrong environment.
            if (needFrame) state.Emitter.EmitDeallocateProceed();
            else state.Emitter.EmitProceed();
        }
        else
        {
            for (int i = 0; i < goals.Count; i++)
            {
                bool isLast = i == goals.Count - 1;
                Term goal = goals[i];

                MarkStop(state, goal.Position);   // ADR-035 — this goal's stop site

                // ADR-035 — an INLINE goal (`!`, `is`, `=`, a comparison) emits no call
                // and so raises no port: a step walked straight over it, and the user
                // could never stand at the `!` and look at the variables before it
                // commits. Under debug codegen each one gets a one-byte port of its own.
                // Placed AFTER the stop site so a breakpoint armed on this goal patches
                // the debug_port byte: the Break reports first, the re-dispatched port is
                // deduplicated as the same stop (see DebugService's reported-call-site).
                // (Not in the __query__ wrapper — its goals have no source to stand on.)
                if (DebugCodegen && !_suppressBreaks && IsInlineBodyGoal(goal))
                    state.Emitter.EmitDebugPort();

                if (goal is AtomTerm { Name: "!" })
                {
                    // Cut emission. Neck cut — position 0, or preceded only by
                    // inline goals — reads _b0 directly; a deep
                    // cut (after a real call) reads the saved barrier from
                    // Y[cutSlot].
                    if (i < inlinePrefixLen || i == 0)
                        state.Emitter.EmitNeckCut();
                    else
                        state.Emitter.EmitCut(cutSlot);

                    if (isLast)
                    {
                        // `!` as the final goal: no execute/call follows.
                        // Just close the frame (if any) and return to caller.
                        // fuse Deallocate+Proceed when both fire.
                        if (needFrame)
                            state.Emitter.EmitDeallocateProceed();
                        else
                            state.Emitter.EmitProceed();
                    }
                }
                else if (goal is CompoundTerm { Functor: ";", Args.Length: 2 } iteGoal
                         && inlineItes.Contains(i))
                {
                    // ADR-025 — inline if-then-else / disjunction in the host clause.
                    CompileInlineIte(state, iteGoal, isLast, needFrame,
                        iteBarrierSlot.TryGetValue(i, out int slot) ? slot : -1);
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

        // ADR-035 — the frame map: which Y slot each source variable ended up in. Only
        // the ones the source named; the cut barrier and the if-then-else barriers also
        // live in Y slots, but they are the machine's, not the user's.
        List<DebugVariable>? debugVars = null;
        if (DebugCodegen && needFrame)
        {
            debugVars = new List<DebugVariable>();
            foreach (var (varName, slot) in state.Ys)
                if (varName.Length > 0 && varName[0] != '_')
                    debugVars.Add(new DebugVariable(varName, slot));
            debugVars.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        }

        return new CompiledClause(
            state.Emitter.ToBytes(),
            functorId,
            headArgs.Length,
            state.Xs.RegisterCount,
            state.PermanentCount,
            state.CallSites,
            state.DispatchSites.Count == 0 ? null : state.DispatchSites,
            state.DebugStops.Count == 0 ? null : state.DebugStops,
            debugVars is { Count: > 0 } ? debugVars : null,
            hasFrame: needFrame,
            // ADR-035 — the head skeleton, so a stack frame can show the call with its
            // arguments' CURRENT values. Debug-only: release keeps no AST behind.
            debugHeadArgs: DebugCodegen && headArgs.Length > 0 ? headArgs : null);
    }

    /// <summary>ADR-025 — adds every named variable occurring inside an inline
    /// ITE goal to <paramref name="permanents"/> (first-occurrence order kept).</summary>
    private static void ForceIteVarsPermanent(
        List<Term> goals, List<int> inlineItes, List<string> permanents)
    {
        var have = new HashSet<string>(permanents);
        void Walk(Term t)
        {
            switch (t)
            {
                case VarTerm v when v.Name != "_" && have.Add(v.Name):
                    permanents.Add(v.Name);
                    break;
                case CompoundTerm c:
                    foreach (var a in c.Args) Walk(a);
                    break;
            }
        }
        foreach (int gi in inlineItes) Walk(goals[gi]);
    }

    /// <summary>ADR-025 — emits an eligible <c>(C -&gt; T ; E)</c> /
    /// <c>(A ; B)</c> inline:
    /// <code>
    /// [get_level Yb]           ; if-then-else only
    /// try_me_else ELSE (arity 0)
    /// C…  [cut Yb]  T…
    /// jump END
    /// ELSE: trust_me
    /// E…
    /// END:
    /// </code>
    /// Inner goals compile as ordinary NON-last body goals with no Y trimming
    /// (every branch variable is already a permanent — see
    /// <see cref="ForceIteVarsPermanent"/>). Both address operands are
    /// clause-local and recorded as dispatch sites for the predicate/link
    /// shifts.</summary>
    private void CompileInlineIte(
        CompileState s, CompoundTerm disj, bool isLast, bool hasFrame, int barrierSlot)
    {
        bool isIte = disj.Args[0] is CompoundTerm { Functor: "->", Args.Length: 2 };
        // ADR-037 — ( Cond *-> Then ; Else ): same inline shape as ->, committed
        // with soft_cut instead of cut, and the barrier captured AFTER the
        // try_me_else (so it names the ELSE CP, not the parent).
        bool isSoftCut = disj.Args[0] is CompoundTerm { Functor: "*->", Args.Length: 2 };
        bool hasCond = isIte || isSoftCut;
        Term? condPart = hasCond ? ((CompoundTerm)disj.Args[0]).Args[0] : null;
        Term thenPart = hasCond ? ((CompoundTerm)disj.Args[0]).Args[1] : disj.Args[0];
        Term elsePart = disj.Args[1];

        // ADR-025 bring-up fix: capture CURRENT B, not B0 — a pre-ITE body
        // call resets B0, so cutting to it pruned a preceding generator's
        // choice points (boyer lost solutions / crashed). get_level_b takes
        // B at the try point: the cut pops exactly the ITE CP + Cond's CPs.
        // For -> the barrier is captured BEFORE the try_me_else (names the
        // parent, so cut pops the ITE CP too). For *-> it is captured AFTER
        // (below), so it names the ITE CP itself and soft_cut neutralises ONLY
        // that one.
        if (isIte) s.Emitter.EmitGetLevelB(barrierSlot);
        int tryPos = s.Emitter.Position;
        // ELSE target patched below. The arity operand is the body-CP
        // SENTINEL (not 0): it marks this try_me_else as an inline ITE for
        // every whole-bytecode scan (cursor budget, shape guards) — a
        // dispatch-chain try_me_else always carries a real arity >= 0. The
        // interpreter saves 0 argument registers for it either way.
        s.Emitter.EmitTryMeElse(0, arity: OpcodeTable.InlineIteCpArity);
        s.DispatchSites.Add(tryPos + 1);
        // ADR-037 — *-> captures the barrier AFTER the try_me_else: the slot now
        // names the ELSE choice point just pushed, so soft_cut neutralises that
        // one and leaves the condition's CPs (pushed above) alive.
        if (isSoftCut) s.Emitter.EmitGetLevelB(barrierSlot);
        // The emitter's Y-initialization tracking is per-EMISSION-ORDER, but at
        // runtime only ONE branch executes: the else branch must be emitted as if
        // starting from the try-point state (else a variable first bound in the
        // then branch would be read as "already initialized" on the else path —
        // an uninitialized-slot read), and after the join only variables
        // initialized on BOTH paths may be assumed initialized.
        var initAtTry = new HashSet<string>(s.YsInitialized);
        if (hasCond)
        {
            foreach (var g in FlattenConjunction(condPart!))
                CompileBodyGoal(s, g, isLast: false, hasFrame, s.PermanentCount);
            if (isSoftCut)
                s.Emitter.EmitSoftCut(barrierSlot);   // ADR-037: neutralise ONLY the ELSE CP
            else
                s.Emitter.EmitCut(barrierSlot);       // ->: pop the ITE CP (+ Cond's CPs)
        }
        // Branch-tail LCO (ADR-025 follow-up): when the ITE is the clause's
        // LAST goal, each branch's last goal compiles as a last goal —
        // `deallocate; execute` for a user call, `call_builtin;
        // deallocate_proceed` otherwise — exactly what the helper lowering
        // gave it. The old shape compiled every branch goal as non-last and
        // joined at END + one shared epilogue, so a tail-recursive call
        // through a branch lost LCO: O(depth) frames (a stack-robustness
        // regression vs the helper form) and a call/return round trip per
        // iteration (the dominant share of boyer's Tier-1 inline-ITE cost).
        // Each branch then self-terminates: no `jump`, no join, no epilogue.
        // A branch's goal list can be EMPTY (FlattenConjunction elides `true`,
        // so `-> true` / `; true` flatten to nothing). Under isLast such a
        // branch must still CLOSE the clause explicitly — without it the flow
        // would fall through into the ELSE block (or off the clause end).
        void EmitBranch(List<Term> branchGoals)
        {
            if (isLast && branchGoals.Count == 0)
            {
                if (hasFrame) s.Emitter.EmitDeallocateProceed();
                else s.Emitter.EmitProceed();
                return;
            }
            for (int i = 0; i < branchGoals.Count; i++)
                CompileBodyGoal(s, branchGoals[i], isLast && i == branchGoals.Count - 1,
                    hasFrame, s.PermanentCount);
        }

        EmitBranch(FlattenConjunction(thenPart));
        var initAfterThen = new HashSet<string>(s.YsInitialized);
        int jumpPos = -1;
        if (!isLast)
        {
            jumpPos = s.Emitter.Position;
            s.Emitter.EmitJump(0);              // END target patched below
            s.DispatchSites.Add(jumpPos + 1);
        }
        s.Emitter.PatchInt32(tryPos + 1, s.Emitter.Position);   // ELSE:
        s.Emitter.EmitTrustMe();
        s.YsInitialized.Clear();
        s.YsInitialized.UnionWith(initAtTry);   // else path starts at the try point
        EmitBranch(FlattenConjunction(elsePart));
        if (!isLast)
        {
            s.Emitter.PatchInt32(jumpPos + 1, s.Emitter.Position);  // END:
            s.YsInitialized.IntersectWith(initAfterThen);   // join: both-paths only
        }
    }

    // ============================================================================
    // Chunk classification
    // ============================================================================

    /// <summary>Walks the head and body, collecting the set of chunks each named
    /// variable appears in. Returns the names that appear in at least two
    /// chunks — those need permanent (Y) storage to survive an intervening call.
    /// The result is deterministic (insertion-ordered) so Y slot indices are
    /// stable for given source.</summary>
    /// <summary>Survey output (see the call site above): per
    /// <c>name/arity</c>, the total permanents allocated across its clauses and
    /// how many are Class B (live only across inline goals). Null = off (the
    /// default; the CLI sets it under <c>SHUMWAY_Y_SURVEY=1</c>).
    /// The per-clause collection only exists in <c>-p:ShumwayDiag=true</c>
    /// builds — in a normal build the survey stays empty.</summary>
    public static Dictionary<string, (int PermTotal, int ClassB)>? YSurvey;

    /// <summary>Register-allocator design survey (ADR-021):
    /// quantifies the CEILING of the classic-allocator arc by classifying each
    /// permanent. Class B = a permanent whose chunk-crossings are ALL over
    /// inline-compiled goals (cut / =/2 / is / the six comparisons), i.e. what
    /// a chunk-transparency allocator would demote; Class A = crosses a real
    /// call — irreducible in the WAM model. Diagnostic only; stripped from
    /// normal builds via <c>[Conditional("SHUMWAY_DIAG")]</c>.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private static void DiagYSurvey(
        string name, Term[] headArgs, List<Term> goals, List<string> permanents)
    {
        if (YSurvey is null) return;
        var transparent = ClassifyPermanentsInlineTransparent(headArgs, goals);
        int classB = 0;
        foreach (var p in permanents)
            if (!transparent.Contains(p)) classB++;
        string key = $"{name}/{headArgs.Length}";
        YSurvey.TryGetValue(key, out var prev);
        YSurvey[key] = (prev.PermTotal + permanents.Count, prev.ClassB + classB);
    }

    /// <summary>Survey variant — <see cref="ClassifyPermanents"/> under the
    /// refuted inline-transparency model: a goal the compiler lowers WITHOUT a
    /// call (cut, <c>=/2</c>, <c>is/2</c>, the six arithmetic comparisons) does
    /// not end a chunk, so a variable whose uses straddle only such goals stays
    /// temporary. NOT used for codegen (unsound — choice-point liveness is not
    /// clause-local); used only to
    /// size what a sound allocator could ever reclaim.</summary>
    private static HashSet<string> ClassifyPermanentsInlineTransparent(
        Term[] headArgs, List<Term> goals)
    {
        var occurs = new Dictionary<string, HashSet<int>>();
        var stack = new Stack<Term>();
        // Iterative walk: a recursive descent used one C# frame
        // per node, so a deeply-nested argument (e.g. a long list) overflowed
        // the stack. The explicit work-stack keeps C# stack use O(1).
        void Visit(Term root, int chunk)
        {
            stack.Push(root);
            while (stack.Count > 0)
            {
                Term t = stack.Pop();
                switch (t)
                {
                    case VarTerm v when v.Name != "_":
                        if (!occurs.TryGetValue(v.Name, out var s))
                            occurs[v.Name] = s = new HashSet<int>();
                        s.Add(chunk);
                        break;
                    case CompoundTerm c:
                        // Push in reverse so args pop left-to-right — identical
                        // pre-order DFS to the former recursion, so the
                        // first-occurrence variable order (which drives Y-slot
                        // assignment) is unchanged.
                        for (int i = c.Args.Length - 1; i >= 0; i--) stack.Push(c.Args[i]);
                        break;
                }
            }
        }
        static bool IsInlineGoal(Term g) => g switch
        {
            AtomTerm { Name: "!" or "true" } => true,
            CompoundTerm { Args.Length: 2 } c =>
                c.Functor is "=" or "is" or "<" or ">" or "=<" or ">=" or "=:=" or "=\\=",
            _ => false,
        };
        foreach (Term arg in headArgs) Visit(arg, 0);
        int ch = 0;
        for (int i = 0; i < goals.Count; i++)
        {
            Visit(goals[i], ch);
            if (!IsInlineGoal(goals[i])) ch++;
        }
        var perms = new HashSet<string>();
        foreach (var (nm, set) in occurs)
            if (set.Count >= 2) perms.Add(nm);
        return perms;
    }

    private static List<string> ClassifyPermanents(Term[] headArgs, List<Term> goals)
    {
        var occurs = new Dictionary<string, HashSet<int>>();
        var order = new List<string>();
        var stack = new Stack<Term>();

        // Iterative walk: see ClassifyPermanentsInlineTransparent
        // — a recursive descent overflowed on a deeply-nested argument.
        void Visit(Term root, int chunk)
        {
            stack.Push(root);
            while (stack.Count > 0)
            {
                Term t = stack.Pop();
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
                        // Push in reverse so args pop left-to-right — identical
                        // pre-order DFS to the former recursion, so the
                        // first-occurrence variable order (which drives Y-slot
                        // assignment) is unchanged.
                        for (int i = c.Args.Length - 1; i >= 0; i--) stack.Push(c.Args[i]);
                        break;
                }
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

    /// <summary>ADR-035 — every named variable in the clause, in first-occurrence
    /// order, with the ones <see cref="ClassifyPermanents"/> already chose keeping
    /// their slots. Debug codegen makes them all permanent so a debugger can read them
    /// out of the frame; <c>_</c> and <c>_Foo</c> are left out, being the two spellings
    /// of "I do not care about this one".</summary>
    private static List<string> AllNamedVariables(
        Term[] headArgs, List<Term> goals, List<string> already)
    {
        var perms = new List<string>(already);
        var have = new HashSet<string>(already);
        var stack = new Stack<Term>();

        void Visit(Term root)
        {
            stack.Push(root);
            while (stack.Count > 0)
            {
                switch (stack.Pop())
                {
                    case VarTerm v when v.Name.Length > 0 && v.Name[0] != '_' && have.Add(v.Name):
                        perms.Add(v.Name);
                        break;
                    case CompoundTerm c:
                        for (int i = c.Args.Length - 1; i >= 0; i--) stack.Push(c.Args[i]);
                        break;
                }
            }
        }

        foreach (Term arg in headArgs) Visit(arg);
        foreach (Term goal in goals) Visit(goal);
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
        // Target the first CALL of chunk 0 — which, with a transparent neck
        // cut, may sit at index 1. Mirrors the Warren scheduler so a
        // head var preferenced into its register is the one the scheduler then
        // leaves in place (`p :- !, recur(Args)` extracts Args
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

    private static void CountVarOccurrences(Term root, Dictionary<string, int> counts)
    {
        // Iterative: a recursive descent overflowed on a
        // deeply-nested argument (e.g. a long list).
        var stack = new Stack<Term>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case VarTerm v when v.Name != "_":
                    counts[v.Name] = counts.GetValueOrDefault(v.Name) + 1;
                    break;
                case CompoundTerm c:
                    foreach (Term a in c.Args) stack.Push(a);
                    break;
            }
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
    /// <summary>A body goal whose compiled form never clobbers the continuation
    /// pointer, never updates <c>B0</c>, and never pushes a choice point: the
    /// cut (<c>!</c>), arithmetic (<c>is/2</c> and the six comparisons →
    /// <c>a_int_*</c> / <c>a_eval_*</c>), and — <c>=/2</c>.
    /// Such a goal needs no environment frame to survive it, and a <c>!</c>
    /// after a prefix of them is still a NECK cut.
    /// <para><c>=/2</c> CP-safety across BOTH its lowerings: the inline form
    /// (get_*/unify_* head-style matching) is plain unification; the
    /// fallback is a <c>call_builtin</c> of the non-backtrackable <c>=/2</c>,
    /// which runs inline in the dispatch loop — Cp untouched, B0 untouched
    /// (only Call/Execute update it), no choice point. An attvar unification
    /// schedules wakeups, which the neck-cut/cut dispatch flushes before
    /// committing — same as after arithmetic.</para></summary>
    private static bool IsInlineBodyGoal(Term goal) => goal switch
    {
        AtomTerm { Name: "!" } => true,
        CompoundTerm { Functor: "is", Args.Length: 2 } => true,
        CompoundTerm { Functor: "=", Args.Length: 2 } => true,
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
        // (the loop only reads the map, so iterate Names
        // directly instead of materialising a defensive ToList copy.)
        var homeToVar = new Dictionary<int, string>();
        foreach (var name in s.Xs.Names)
        {
            if (s.Ys.ContainsKey(name)) continue;
            int home = s.Xs.GetSlot(name);
            if (home < N) homeToVar[home] = name;
        }

        // === Step 1: Forced saves for depth-≥2 reads (drained compounds). ===
        var forced = new HashSet<string>();
        for (int i = 0; i < N; i++)
            CollectForcedSaves(gArgs[i], rootDepth: 0, s, N, forced);
        // in-place sort instead of LINQ OrderBy. The Seq component
        // reproduces OrderBy's stability over the set's enumeration order
        // exactly (two names CAN share a slot via head-arg aliasing).
        var forcedOrder = new List<(int Slot, int Seq, string Name)>(forced.Count);
        foreach (string name in forced)
            forcedOrder.Add((s.Xs.GetSlot(name), forcedOrder.Count, name));
        forcedOrder.Sort(static (a, b) => a.Slot != b.Slot
            ? a.Slot.CompareTo(b.Slot) : a.Seq.CompareTo(b.Seq));
        foreach (var (home, _, name) in forcedOrder)
        {
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
        // the per-node reads sets and successor lists are
        // allocated ONCE and cleared/refilled per cycle-break iteration
        // (Recompute used to allocate N fresh HashSets per iteration, and
        // FindCycleNode + TopoSort each rebuilt per-node successor lists).
        var reads = new HashSet<int>[N];
        var succ = new List<int>[N];
        var writesDst = new bool[N];
        for (int i = 0; i < N; i++) { reads[i] = new HashSet<int>(); succ[i] = new List<int>(); }
        Recompute();

        while (FindCycleNode(succ, N) is int cycleNode)
        {
            if (!homeToVar.TryGetValue(cycleNode, out var hv)) break;
            int safe = s.Xs.AllocateAnonymousSlot();
            s.Emitter.EmitPutValueX(cycleNode, safe);
            s.Xs.Rebind(hv, safe);
            homeToVar.Remove(cycleNode);
            Recompute();
        }

        // === Step 4: Topological sort of the now-acyclic graph. ===
        return TopoSort(succ, N);

        void Recompute()
        {
            for (int i = 0; i < N; i++)
            {
                reads[i].Clear();
                FillDirectReads(gArgs[i], s, N, reads[i]);
                writesDst[i] = ArgWritesDst(gArgs[i], i, s);
            }
            // Successor lists (edge i → j iff j ∈ reads[i], j ≠ i, arg j
            // writes X[j]), sorted ascending — shared by the cycle finder
            // (which visits them in this order) and
            // the topo sort (whose result is order-insensitive: in-degrees
            // and the SortedSet ready queue decide the output).
            for (int i = 0; i < N; i++)
            {
                succ[i].Clear();
                foreach (int j in reads[i])
                    if (j != i && j < N && writesDst[j])
                        succ[i].Add(j);
                succ[i].Sort();
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
        Term root, int rootDepth, CompileState s, int N, HashSet<string> sink)
    {
        // Iterative: a recursive descent overflowed on a
        // deeply-nested body argument (e.g. a long list literal in a goal).
        var stack = new Stack<(Term Term, int Depth)>();
        stack.Push((root, rootDepth));
        while (stack.Count > 0)
        {
            var (t, depth) = stack.Pop();
            if (t is CompoundTerm c)
            {
                foreach (Term sub in c.Args) stack.Push((sub, depth + 1));
            }
            else if (t is VarTerm v && v.Name != "_" && depth >= 2)
            {
                if (s.Ys.ContainsKey(v.Name)) continue;
                if (s.Xs.IsNewName(v.Name)) continue;
                int home = s.Xs.GetSlot(v.Name);
                if (home < N) sink.Add(v.Name);
            }
        }
    }

    /// <summary>Set of X-register slots in <c>[0, N)</c> that <paramref name="arg"/>'s
    /// own emission would read directly — flat-var sources (and direct
    /// sub-args of a top-level compound). Sub-compounds contribute via
    /// <see cref="CollectForcedSaves"/>, not here. (fills a
    /// caller-owned set so the scheduler reuses one set per node across
    /// cycle-break iterations instead of allocating fresh ones.)</summary>
    private static void FillDirectReads(Term arg, CompileState s, int N, HashSet<int> reads)
    {
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
    /// dependency graph (the precomputed sorted successor lists from the
    /// scheduler's <c>Recompute</c> — previously rebuilt + sorted
    /// per DFS node here). Returns the node's index, or <c>null</c> when the
    /// graph is acyclic. Iterative DFS with three-colour marking keeps the
    /// stack bounded.</summary>
    private static int? FindCycleNode(List<int>[] succ, int N)
    {
        var color = new int[N]; // 0 white, 1 gray, 2 black
        for (int start = 0; start < N; start++)
        {
            if (color[start] != 0) continue;
            var stack = new Stack<(int Node, List<int>.Enumerator Iter)>();
            color[start] = 1;
            stack.Push((start, succ[start].GetEnumerator()));
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
                        stack.Push((next, succ[next].GetEnumerator()));
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

    /// <summary>Kahn's topological sort of the arg dependency graph,
    /// preferring lower indices on ties for deterministic output.
    /// (consumes the scheduler's precomputed successor lists
    /// instead of re-deriving per-node edge lists from the reads sets;
    /// the SortedSet ready queue makes the output order-insensitive to
    /// edge-list order, so the result is unchanged.)</summary>
    private static int[] TopoSort(List<int>[] succ, int N)
    {
        var inDeg = new int[N];
        for (int i = 0; i < N; i++)
            foreach (int j in succ[i])
                inDeg[j]++;
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
            foreach (int next in succ[n])
                if (--inDeg[next] == 0) ready.Add(next);
        }
        if (outIdx != N)
            for (int i = 0; i < N; i++) result[i] = i;
        return result;
    }

}
