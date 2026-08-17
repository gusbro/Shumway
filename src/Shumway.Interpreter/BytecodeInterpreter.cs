using Shumway.Core;

namespace Shumway.Interpreter;

/// <summary>
/// Tier 0 WAM bytecode interpreter. Dispatches one opcode at a time on a target
/// <see cref="Activation"/>, calling into the engine's state-management APIs for the
/// actual work. The opcode encoding is defined in ADR-006 and the per-instruction
/// semantics in docs/design/wam-instruction-set.md.
///
/// <para>Currently implemented: control flow (halt/proceed/call/execute/allocate/
/// deallocate), the atomic get/put family (variable, value, constant, integer, atom,
/// nil — both X and Y forms), the A1/A2 consolidations, the
/// <c>try_me_else</c>/<c>retry_me_else</c>/<c>trust_me</c> choice-point family with
/// backtrack-on-failure, cut (<c>neck_cut</c>/<c>get_level</c>/<c>cut</c>), the
/// compound/list <c>get_*</c>/<c>put_*</c>/<c>unify_*</c> family with read/write mode
/// dispatch, and the PSTR family (<c>get_pstr</c>/<c>put_pstr</c>/<c>unify_pstr_head</c>).
/// Still <see cref="NotImplementedException"/>: indexed
/// <c>switch_on_*</c>/<c>try</c>/<c>retry</c>/<c>trust</c>, builtin dispatch and the
/// <c>meta</c>/<c>dbg_info</c> escape.</para>
/// </summary>
public sealed partial class BytecodeInterpreter
{
    private readonly Activation _engine;
#if SHUMWAY_PROFILE
    private static readonly bool _dispatchTrace =
        System.Environment.GetEnvironmentVariable("ITE_TRACE") == "1";
#endif
    // throw/1's builtin id, for the cheap-throw intercept in the CallBuiltin /
    // ExecuteBuiltin cases. Resolved lazily on first use (the registry is
    // populated by the embedding layer's static initialization).
    private static int _throwBuiltinId = -2;
    private static int ThrowBuiltinId
    {
        get
        {
            if (_throwBuiltinId == -2)
            {
                int fid = Shumway.Core.FunctorTable.Intern(
                    Shumway.Core.AtomTable.Intern("throw", permanent: true).Id, 1);
                _throwBuiltinId = Shumway.Builtins.BuiltinsRegistry
                    .TryGetByFunctor(fid, out int id) ? id : -1;
            }
            return _throwBuiltinId;
        }
    }

    /// <summary>Cheap throw: when the ball is caught by a catch frame opened
    /// in THIS dispatch invocation (index ≥ <paramref name="frameFloor"/> —
    /// no nested C# driver frames to unwind), resolve it to the recovery's
    /// address and jump, skipping .NET exception construction + EH dispatch.
    /// Returns true when handled (Pc set to the recovery).</summary>
    private bool TryInlineThrow(int frameFloor)
    {
        if (_engine.InlineThrowResolver is not { } resolve) return false;
        if (_engine.CatchFrameCount <= frameFloor) return false;
        Cell ball = _engine.GetRegister(0);
        int ballIdx;
        if (ball.Tag is Tag.Ref or Tag.AttVar) ballIdx = ball.AsHeapIndex;
        else
        {
            ballIdx = _engine.AllocateHeap(1);
            _engine.SetHeap(ballIdx, ball);
        }
        int recovery = resolve(ballIdx, frameFloor);
        if (recovery < 0)
        {
            Shumway.Core.Profiler.Note("throw_inline_miss");
            return false;
        }
        Shumway.Core.Profiler.Note("throw_inline_hit");
        _engine.SetPc(recovery);
        return true;
    }

    // SHUMWAY_PC_RING=1 forensics: record the last PcRingSize dispatched PCs
    // and dump them (with labels) when dispatch lands on reserved_invalid —
    // shows the exact control path into a corrupt jump. Off (null) by default.
    internal const int PcRingSize = 64;
    internal static readonly int[]? PcRing =
        System.Environment.GetEnvironmentVariable("SHUMWAY_PC_RING") == "1"
            ? new int[PcRingSize] : null;
    private static int _pcRingPos;

    // Not readonly: ADR-015 recompiles a dynamic predicate
    // mid-query, which may intern new literals into the persistent pools;
    // RefreshLiteralPools swaps in the grown snapshots.
    private IReadOnlyList<string> _stringLiterals;
    private IReadOnlyList<double> _floatLiterals;
    private IReadOnlyList<System.Numerics.BigInteger> _bigIntLiterals;
    private readonly IReadOnlyList<SwitchTable> _switchTables;

    /// <summary>Floor for <see cref="TryBacktrack"/>: choice points at or
    /// below this stack index belong to an outer computation and must
    /// not be unwound. <c>-1</c> (no floor) during normal execution;
    /// <see cref="RunGoalInEngine"/> raises it so an in-engine sub-goal's
    /// backtracking stays contained at its entry level.</summary>
    private int _backtrackFloor = -1;

    // Backing state for Activation.ReentrantSolve (the host→Prolog re-entrant
    // solve API): one cached closure, the current ProgramView held in a field so
    // the closure needn't capture it per Run (zero-alloc after the first Run).
    private ProgramView _reentrantCode;
    private Func<Cell, bool>? _reentrantSolve;

    // Functor ids the in-engine meta-call recognises (the control-construct
    // functors). The attribute-hook functor ids are resolved per module on the
    // Activation (ADR-040 Verify3FunctorId / Verify4FunctorId).
    private static readonly int ConjFunctorId =
        FunctorTable.Intern(AtomTable.Intern(",", permanent: true).Id, 2);
    private static readonly int TrueFunctorId =
        FunctorTable.Intern(AtomTable.Intern("true", permanent: true).Id, 0);
    private static readonly int FailFunctorId =
        FunctorTable.Intern(AtomTable.Intern("fail", permanent: true).Id, 0);
    private static readonly int CutFunctorId =
        FunctorTable.Intern(AtomTable.Intern("!", permanent: true).Id, 0);
    // Control-construct functors and their plainly-named prelude helpers:
    // a runtime call/1 goal that is a control construct is routed to the
    // helper, since the operator atoms are awkward to compile.
    private static readonly int DisjFunctorId =
        FunctorTable.Intern(AtomTable.Intern(";", permanent: true).Id, 2);
    private static readonly int ArrowFunctorId =
        FunctorTable.Intern(AtomTable.Intern("->", permanent: true).Id, 2);
    private static readonly int SoftArrowFunctorId =   // ADR-037 — *->/2
        FunctorTable.Intern(AtomTable.Intern("*->", permanent: true).Id, 2);
    private static readonly int NegFunctorId =
        FunctorTable.Intern(AtomTable.Intern("\\+", permanent: true).Id, 1);
    // `not/1` is the historical SWI / GNU / SICStus synonym for \+/1.
    // Most programs use one or the other interchangeably; the dispatch
    // routes both to the same prelude helper so they really are
    // synonymous at the call/1 level.
    private static readonly int NotFunctorId =
        FunctorTable.Intern(AtomTable.Intern("not", permanent: true).Id, 1);
    // conj/disj/arrow take a third argument — the cut barrier K — so a
    // `!` inside a runtime compound goal commits to the enclosing call
    //. $call_neg is opaque to cut and stays arity 1.
    private static readonly int CallConjFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$call_conj", permanent: true).Id, 3);
    private static readonly int CallDisjFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$call_disj", permanent: true).Id, 3);
    private static readonly int CallArrowFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$call_arrow", permanent: true).Id, 3);
    private static readonly int CallSoftArrowFunctorId =   // ADR-037 — bare *->/2
        FunctorTable.Intern(AtomTable.Intern("$call_softarrow", permanent: true).Id, 3);
    private static readonly int CallNegFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$call_neg", permanent: true).Id, 1);
    // '$mqual'(Module, Goal) — a runtime-variable meta-goal tagged with the
    // module of the clause that meta-called it (ModuleRewrite). Unwrapped in the
    // meta-dispatch so Goal's bare functor resolves against Module's locals first.
    private static readonly int MqualFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$mqual", permanent: true).Id, 2);
    // The ISO module-qualified goal `Module:Goal` — the same (Module, Goal)
    // shape as $mqual, written by the user (e.g. call(error:ilist, X)). Unwrapped
    // in the meta-dispatch exactly like $mqual so `call(M:G, Extra)` extends G,
    // not the ':' functor.
    private static readonly int ColonFunctorId =
        FunctorTable.Intern(AtomTable.Intern(":", permanent: true).Id, 2);

    /// <summary>Optional hook the interpreter consults on every
    /// <c>call</c> / <c>execute</c> to ask whether a Tier-1 IL
    /// replacement exists for the target predicate. <c>null</c> disables
    /// the Tier-1 path; set via <see cref="Tier1Dispatcher"/> once an
    /// embedder has wired in a promotion store.</summary>
    public ITier1Dispatcher? Tier1Dispatcher { get; set; }

    /// <summary>direct IL-delegate table indexed
    /// by functor id. Populated by <see cref="Shumway.Embedding"/> at
    /// link time after every IL registration. The
    /// <see cref="Opcode.CallIl"/> handler reads this directly,
    /// skipping the <see cref="ITier1Dispatcher.OnDispatch"/>
    /// interface call + cache probe that <see cref="Opcode.Call"/>
    /// pays per dispatch.
    ///
    /// <para>Null when no IL is wired (Tier-0-only mode); the linker
    /// must not emit <see cref="Opcode.CallIl"/> in that case.</para></summary>
    public Func<Activation, int, bool>?[]? IlByFunctorId { get; set; }

    public BytecodeInterpreter(Activation engine)
        : this(engine, Array.Empty<string>(), Array.Empty<double>(), Array.Empty<SwitchTable>())
    {
    }

    public BytecodeInterpreter(Activation engine, IReadOnlyList<string> stringLiterals)
        : this(engine, stringLiterals, Array.Empty<double>(), Array.Empty<SwitchTable>())
    {
    }

    public BytecodeInterpreter(
        Activation engine,
        IReadOnlyList<string> stringLiterals,
        IReadOnlyList<double> floatLiterals)
        : this(engine, stringLiterals, floatLiterals, Array.Empty<SwitchTable>())
    {
    }

    public BytecodeInterpreter(
        Activation engine,
        IReadOnlyList<string> stringLiterals,
        IReadOnlyList<double> floatLiterals,
        IReadOnlyList<SwitchTable> switchTables)
        : this(engine, stringLiterals, floatLiterals, switchTables,
               Array.Empty<System.Numerics.BigInteger>())
    {
    }

    /// <summary>Constructs an interpreter with literal pools and the
    /// program-absolute switch table list. PSTR opcodes look up strings,
    /// float-literal opcodes look up doubles, bigint-literal opcodes look
    /// up BigIntegers, and <c>switch_on_atom</c> / <c>switch_on_integer</c>
    /// / <c>switch_on_structure</c> look up <see cref="SwitchTable"/>s.
    /// Bundles provide all four at load time; for tests they're passed
    /// directly.</summary>
    public BytecodeInterpreter(
        Activation engine,
        IReadOnlyList<string> stringLiterals,
        IReadOnlyList<double> floatLiterals,
        IReadOnlyList<SwitchTable> switchTables,
        IReadOnlyList<System.Numerics.BigInteger> bigIntLiterals)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(stringLiterals);
        ArgumentNullException.ThrowIfNull(floatLiterals);
        ArgumentNullException.ThrowIfNull(switchTables);
        ArgumentNullException.ThrowIfNull(bigIntLiterals);
        _engine = engine;
        _stringLiterals = stringLiterals;
        _floatLiterals = floatLiterals;
        _switchTables = switchTables;
        _bigIntLiterals = bigIntLiterals;
        // Let Tier-1 IL code (which holds only an Activation) run pending
        // attribute wakeups before an IL-emitted cut commits — the IL
        // counterpart of the flush-before-cut. Wakeups run through
        // the interpreter's goal machinery; `code` is fetched live so the
        // wakeup goals see the current linked program.
        _engine.Tier1WakeupFlusher = () => FlushPendingWakeups(_engine.GetProgramView());
    }

    public Activation Activation => _engine;
    public IReadOnlyList<string> StringLiterals => _stringLiterals;
    public IReadOnlyList<double> FloatLiterals => _floatLiterals;
    public IReadOnlyList<System.Numerics.BigInteger> BigIntLiterals => _bigIntLiterals;

    /// <summary>ADR-015: swaps in the grown literal pools after a
    /// mid-query dynamic-predicate recompile interned new literals.</summary>
    public void RefreshLiteralPools(
        IReadOnlyList<string> strings,
        IReadOnlyList<double> floats,
        IReadOnlyList<System.Numerics.BigInteger> bigInts)
    {
        _stringLiterals = strings;
        _floatLiterals = floats;
        _bigIntLiterals = bigInts;
    }
    public IReadOnlyList<SwitchTable> SwitchTables => _switchTables;

    /// <summary>
    /// Runs <paramref name="code"/> starting at <paramref name="startPc"/> until the
    /// dispatch loop terminates. The engine's <c>P</c> is overwritten with the start PC
    /// and then advanced according to each instruction's semantics.
    /// </summary>
    public InterpreterResult Run(ProgramView code, int startPc)
    {
        ArgumentNullException.ThrowIfNull(code.Primary);
        // A resume-marker start PC is legal: a --strip-wam predicate has no WAM
        // address, so CurrentFunctorAddresses maps its functor to
        // EncodeResumeMarker(fid, 0) and a caller that resolved it that way (e.g.
        // RunCatching entering a catch frame's recovery goal) hands us the marker.
        // The dispatch loop's IsResumeMarker check — which sits BEFORE its own
        // bounds check for exactly this reason — routes it to the IL delegate.
        if ((startPc < 0 || startPc >= code.Length) && !Activation.IsResumeMarker(startPc))
            throw new ArgumentOutOfRangeException(nameof(startPc),
                $"startPc 0x{startPc:X} is outside [0, 0x{code.Length:X}).");

        _engine.SetPc(startPc);
        // Expose the re-entrant semidet solve to foreign code running under this
        // activation (see Activation.ReentrantSolve). The closure reads _reentrantCode
        // so it is allocated once; each Run refreshes the program view it targets.
        _reentrantCode = code;
        _engine.ReentrantSolve = _reentrantSolve ??= ReentrantSolveTransparent;
        try { return Dispatch(code); }
        catch (TopLevelFailure) { return InterpreterResult.Failed; }
        catch (System.Exception ex) when (PcRing is not null
            && ex is not Shumway.Core.PrologRuntimeException
            // Prolog-level control flow (thrown balls, halt/2), not corruption.
            && ex.GetType().Name is not "ShumwayPrologException"
                and not "PrologHaltException")
        {
            DumpPcRing(code, _engine.P, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>Backs <see cref="Activation.ReentrantSolve"/> (the host→Prolog
    /// SolveOnce API). Must be TRANSPARENT to the caller's argument registers: a
    /// foreign predicate's generated bridge reads its output register AFTER the user
    /// method returns, but <see cref="MetaCallInEngine"/> loads the nested goal's args
    /// into X0… So snapshot the register bank, run the nested semidet solve (which binds
    /// the shared heap/trail — the intended output), then restore the registers. The
    /// bank may have grown during the solve; restoring the saved low indices is
    /// in-bounds and enough (the caller only relies on its own argument registers).
    /// The save buffer is per-invocation (pool-rented, not a shared field) so nested
    /// SolveOnce — C#→Prolog→C#→Prolog — each preserves its own caller's registers.</summary>
    private bool ReentrantSolveTransparent(Cell goal)
    {
        int n = _engine.RegisterCount;
        Cell[] save = System.Buffers.ArrayPool<Cell>.Shared.Rent(n);
        for (int i = 0; i < n; i++) save[i] = _engine.GetRegister(i);
        try { return MetaCallInEngine(_reentrantCode, goal); }
        finally
        {
            for (int i = 0; i < n; i++) _engine.SetRegister(i, save[i]);
            System.Buffers.ArrayPool<Cell>.Shared.Return(save);
        }
    }

    /// <summary>SHUMWAY_PC_RING=1 forensics dump — the recent-PC ring with labels
    /// plus hex windows around the jump source and the current pc.</summary>
    private void DumpPcRing(ProgramView code, int pc, string why)
    {
        var ring = PcRing!;
        var sb = new System.Text.StringBuilder($"[PC-RING:{why}] last PCs: ");
        for (int r = PcRingSize; r >= 1; r--)
        {
            int rp = ring[(_pcRingPos - r) & (PcRingSize - 1)];
            sb.Append($"0x{rp:X}({_engine.ResolveAddressToLabel?.Invoke(rp) ?? "?"}) ");
        }
        System.Console.Error.WriteLine(sb.ToString());
        int prevPc = ring[(_pcRingPos - 2) & (PcRingSize - 1)];
        foreach (int center in new[] { prevPc, pc })
        {
            if (center < 0 || center >= code.Length) continue;
            int lo = System.Math.Max(0, center - 0x80);
            int hi = System.Math.Min(code.Length, center + 0x40);
            var hx = new System.Text.StringBuilder(
                $"[PC-RING] bytes 0x{lo:X}..0x{hi:X} (around 0x{center:X}): ");
            for (int b = lo; b < hi; b++)
                hx.Append(b == center ? $"|{code[b]:X2}| " : $"{code[b]:X2} ");
            System.Console.Error.WriteLine(hx.ToString());
        }
        var cp = new System.Text.StringBuilder("[PC-RING] CP chain: ");
        int depth = 0;
        foreach (var (stackB, savedBp, arity) in _engine.EnumerateChoicePoints())
        {
            cp.Append($"#{depth}(B={stackB},arity={arity},bp=0x{savedBp:X}="
                + $"{_engine.ResolveAddressToLabel?.Invoke(savedBp) ?? "?"}) ");
            if (++depth > 24) { cp.Append("..."); break; }
        }
        System.Console.Error.WriteLine(cp.ToString());
        if (Activation.CpPushRing is { } pushes)
        {
            // Most recent pushes whose bp equals the crash pc — the likely
            // origin of the bad resume address.
            var pr = new System.Text.StringBuilder(
                $"[PC-RING] pushes with bp=0x{pc:X} (most recent first): ");
            int found = 0;
            int total = System.Math.Min(Activation.CpPushRingPos, Activation.CpPushRingSize);
            for (int r = 1; r <= total && found < 8; r++)
            {
                long packed = pushes[(Activation.CpPushRingPos - r) & (Activation.CpPushRingSize - 1)];
                if ((int)packed != pc) continue;
                int pusher = (int)(packed >> 32);
                string kind = pusher switch
                {
                    -1 => "resume-bp",
                    -2 => "il-resume-cp",
                    -3 => "il-resume-tail",
                    -4 => "jump-to-user-goal",
                    -5 => "run-goal-in-engine",
                    _ => _engine.ResolveAddressToLabel?.Invoke(pusher) ?? "?",
                };
                pr.Append($"[-{r}] P=0x{pusher:X}({kind}) ");
                found++;
            }
            if (found == 0) pr.Append("(none in ring)");
            System.Console.Error.WriteLine(pr.ToString());
        }
    }

    // IL non-tail Calls are THREADED (set Cp = resume marker, Pc = callee,
    // IlTailCallPending = true, return to this loop) — never dispatched by a
    // recursive sub-Dispatch invocation, which would grow the C# stack with
    // Prolog depth and let backtracking cascade past the IL caller's CPs.
    // SubroutineSentinelCp is NOT a leftover of that rejected design:
    // RunGoalInEngine (the in-engine sub-goal driver for findall/3 etc.)
    // uses its Pc-negative trick to exit its dispatch loop.

    /// <summary>Cp sentinel used by in-engine sub-goal dispatch
    /// (<see cref="RunGoalInEngine"/>). Any negative value works because
    /// <c>Proceed</c> already returns <see cref="InterpreterResult.Halted"/>
    /// when Cp &lt; 0; we pick a distinctive value to make stack-traces
    /// friendlier.</summary>
    public const int SubroutineSentinelCp = -2;

    /// <summary>
    /// Forces a failure at the current execution point and runs the dispatch
    /// loop until it halts or runs out of choice points. The standard use is
    /// "give me the next solution": after <see cref="Run"/> returned
    /// <see cref="InterpreterResult.Halted"/>, calling <see cref="Backtrack"/>
    /// re-enters the engine via its current choice point's BP, the saved
    /// state restores, and execution continues looking for another success.
    /// Returns <see cref="InterpreterResult.Failed"/> immediately when no
    /// choice point is alive (the previous run committed to a single solution
    /// via cut, or the predicate had only one clause and it just finished).
    /// </summary>
    public InterpreterResult Backtrack(ProgramView code)
    {
        if (!TryBacktrack()) return InterpreterResult.Failed;
        try { return Dispatch(code); }
        catch (TopLevelFailure) { return InterpreterResult.Failed; }
    }

    private InterpreterResult Dispatch(ProgramView code)
    {
        // cache the ProgramView across dispatch iterations.
        // Refresh only when the engine's program generation has
        // changed (AppendCode reallocation, per-query rewire of
        // overlay/split).
        //
        // peel off a direct byte[] reference when the view
        // is single-buffer (the steady state — Overflow only appears
        // mid-query during persistent + per-query
        // split, and even then the per-query overlay is small). The
        // per-iteration `code[pc]` indexer otherwise compiles to a
        // branch on Split per dispatch tick.
        int cachedGen = -1;
        bool engineDriven = _engine.CurrentProgram is not null;
        byte[] codeArr = code.IsSingleBuffer ? code.Primary : System.Array.Empty<byte>();
        int codeLen = code.Length;
        // Catch frames below this index belong to OUTER drivers (their C#
        // frames sit between us and them) — a throw resolving to one of THOSE
        // must unwind via the .NET exception; frames at/above it were opened
        // by this invocation and take the cheap PC-jump path (TryInlineThrow).
        int dispatchCatchFloor = _engine.CatchFrameCount;
        bool inClause = false;   // I1: set by a straight-line op so the next
                                 // iteration skips the gen/marker/bounds checks.
        while (true)
        {
            int pc = _engine.P;
            // I1: a straight-line opcode advances pc by a fixed amount
            // within the current clause — it cannot change the program, land on a
            // resume marker, or exceed the code bounds, so it sets inClause to
            // skip these three per-iteration checks. Every control transfer
            // (Call/Execute/Proceed/backtrack/IL/marker) leaves inClause false so
            // the next iteration re-checks; _engine.P is always written, so it is
            // never stale for code that reads it (builtins, meta-calls, GC).
            if (!inClause)
            {
            if (engineDriven)
            {
                int gen = _engine.ProgramGeneration;
                if (gen != cachedGen)
                {
                    code = _engine.GetProgramView();
                    codeArr = code.IsSingleBuffer ? code.Primary : System.Array.Empty<byte>();
                    codeLen = code.Length;
                    cachedGen = gen;
                }
            }
            // Negative PC indicates "returned past the top" — the same
            // semantics as proceed's Cp<0 early-return. Used by
            // RunSubroutine: when an IL subroutine call
            // returns success but the caller's Cp is the
            // SubroutineSentinelCp, the IL dispatch path sets
            // Pc=Cp=sentinel; the next dispatch iteration sees it and
            // halts cleanly here instead of indexing into code[].
            if (pc < 0) return InterpreterResult.Halted;

            // threaded Tier-1: a resume-marker PC means an
            // IL non-tail Call site set Cp to this address before
            // dispatching the callee; the callee Proceeded, setting
            // Pc=Cp=marker. Decode the marker, look up the IL
            // delegate, and re-invoke it at the forward-resume
            // cursor. The marker check sits BEFORE the codeLen bounds
            // check because the marker's int value is intentionally
            // out of the bytecode range.
            if (Activation.IsResumeMarker(pc))
            {
                // ADR-016 safe point: an IL non-tail callee has Proceeded
                // back to its caller; caller state lives in the engine.
                _engine.MaybeCollectHeap();
                var (functorId, cursor) = Activation.DecodeResumeMarker(pc);
                // Direct index into the link-time IlByFunctorId array — the same
                // O(1) array access CallIl (bytecode→IL) uses, instead of the
                // dispatcher's interface call + dictionary + cached wrapper. Fall
                // back to the dispatcher only for a delegate promoted mid-query
                // (after the per-query link snapshot was taken — not in the array).
                var ilTable = IlByFunctorId;
                var del = ilTable is not null && (uint)functorId < (uint)ilTable.Length
                    ? ilTable[functorId] : null;
                del ??= Tier1Dispatcher?.ResolveByFunctorId(functorId);
                if (del is null)
                {
                    // cursor 0 = a forward CALL to this functor (an IL caller
                    // dispatches every callee by functor id, not by address).
                    // No IL delegate → the callee is bytecode-only; fall back to
                    // its WAM address. This is what lets an IL-only callee have
                    // no WAM body (WAM stripping). cursor > 0 = a genuine resume,
                    // where a missing delegate IS a bug.
                    if (cursor == 0)
                    {
                        var addrMap = _engine.CurrentFunctorAddresses;
                        if (addrMap is not null
                            && addrMap.TryGetValue(functorId, out int addr)
                            && !Shumway.Core.CallTarget.IsUnresolved(addr))
                        {
                            _engine.SetPc(addr);   // run the callee's bytecode
                            continue;
                        }
                        // Last chance: a MetaTransform helper whose delegate was
                        // evicted and whose bytecode THIS activation never linked
                        // (compiled by a different activation's setup/assert) —
                        // materialize it on demand.
                        int lateAddr = _engine.ResolveLateHelper?.Invoke(functorId) ?? -1;
                        if (lateAddr >= 0)
                        {
                            _engine.SetPc(lateAddr);
                            continue;
                        }
                        // honour the `unknown` flag (throws on error).
                        if (Shumway.Core.UnknownProcedure.Fails(_engine, functorId))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                            continue;
                        }
                    }
                    throw new InvalidOperationException(
                        $"Resume marker at PC 0x{pc:X} decodes to functor "
                        + $"id {functorId} / cursor {cursor} but no IL "
                        + "delegate is bound. (A Tier-1 promotion must "
                        + "have unwired itself mid-query, which is a bug.)");
                }
                if (!del(_engine, cursor))
                {
                    if (!TryBacktrack()) return InterpreterResult.Failed;
                    continue;
                }
                // Delegate returned true — it either set IlTailCallPending
                // + Pc for another tail call (loop continues; the new Pc
                // may itself be a marker or a normal bytecode address),
                // or finished its work and left Pc=Cp pointing at the
                // caller's continuation.
                if (_engine.IlTailCallPending)
                {
                    _engine.IlTailCallPending = false;
                }
                else
                {
                    _engine.SetPc(_engine.Cp);
                }
                continue;
            }

            if (pc >= codeLen)
                throw new InvalidOperationException(
                    $"Program counter 0x{pc:X} is outside code range [0, 0x{codeLen:X}).");
            }
            inClause = false;

            // when the view is split (Overflow != null) we
            // fall back to the indexer for both the opcode byte and
            // every operand read inside the case bodies (those still
            // go through BytecodeIO's ProgramView overloads, which
            // handle the split internally). The fast path skips the
            // per-tick Split branch entirely.
            byte opByte = code.Overflow is null ? codeArr[pc] : code[pc];
            Shumway.Core.Profiler.Opcode(opByte);
            if (PcRing is { } ring) { ring[_pcRingPos++ & (PcRingSize - 1)] = pc; }
#if SHUMWAY_PROFILE
            // Dispatch trace (profile builds only, ITE_TRACE=1): one line per
            // dispatched opcode with pc / B / Cp. Added for the ADR-025
            // bring-up; generally useful for control-flow forensics.
            if (_dispatchTrace)
                System.Console.Error.WriteLine(
                    $"[t] pc={pc,7} {(Opcode)opByte,-18} b={_engine.B} cp={_engine.Cp}");
#endif
        dispatch:
            switch ((Opcode)opByte)
            {
                case Opcode.ReservedInvalid:
                    if (PcRing is not null) DumpPcRing(code, pc, "reserved_invalid");
                    throw new InvalidOperationException(
                        $"Encountered reserved_invalid opcode at PC=0x{pc:X4}"
                        + $" ({_engine.ResolveAddressToLabel?.Invoke(pc) ?? "?"})"
                        + $" Cp=0x{_engine.Cp:X4} ({_engine.ResolveAddressToLabel?.Invoke(_engine.Cp) ?? "?"})"
                        + " — bytecode corruption.");

                case Opcode.Halt:
                    return InterpreterResult.Halted;

                case Opcode.Nop:
                    // ADR-015: padding bytes for asserta's
                    // in-place demotion of try_me_else (9 bytes) to
                    // retry_me_else (5 bytes); the trailing 4 arity-
                    // operand bytes become nops.
                    _engine.SetPc(pc + 1); inClause = true;
                    break;

                case Opcode.Proceed:
                {
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    // setup_call_cleanup: run any cleanup the engine enqueued from
                    // a teardown path (external cut, etc.) at this goal boundary.
                    FlushPendingCleanups(code);
                    if (_engine.Debug is { } dbgProceed)
                    {
                        dbgProceed.OnExit(_engine);            // ADR-035 exit port
                        // A Set Next Statement during the stop moved P: honour it
                        // instead of returning through the stale continuation.
                        if (_engine.TakeDebugPcRedirect()) { inClause = false; continue; }
                    }
                    int returnPc = _engine.Cp;
                    if (returnPc < 0)
                        return InterpreterResult.Halted;       // returned past the top
                    _engine.SetPc(returnPc);
                    break;
                }

                case Opcode.Call:
                {
                    // Operands BEFORE the wakeup flush: the flush runs arbitrary
                    // goals, and a background IL install draining inside them
                    // (OnCalleePromoted) may rewrite THIS site in place to
                    // CallIl <fid>. Reading the operand after the flush would
                    // pair the already-dispatched Call opcode with the fid and
                    // jump to it as an address (the clpz cross-query crash).
                    int target = ReadI32(code, codeArr, pc + 1);
                    int numLivePerms = ReadI32(code, codeArr, pc + 5);
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    target = ResolveTargetMaybeAutoPromoted(target);
                    if (target == UnknownFailTarget)   // unknown=fail
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    Shumway.Core.Profiler.Call(target);
                    // Env trimming: shrink the current frame to
                    // num_live_perms Y slots before dispatching, so the callee's
                    // pushes (CP, allocate) sit just above the live region of
                    // the parent frame.
                    _engine.TrimEnv(numLivePerms);
                    _engine.SetCp(pc + 9);  // Call is 9 bytes (opcode + addr + count)
                    _engine.SetB0(_engine.B);   // capture _b at procedure entry for neck_cut
                    DispatchToTier1OrBytecode(target, tailCall: false);
                    break;
                }

                // ADR-035 — an armed breakpoint: one byte the debugger wrote over
                // the opcode of the instruction we are about to run. Report the
                // stop, then run that instruction — read from the engine's
                // breakpoint table, at this same pc, with its operands untouched.
                //
                // The byte is never restored. Restoring it, stepping, and putting
                // it back would open a window in which another activation over the
                // same shared code space runs the un-patched instruction and misses
                // the breakpoint; re-dispatching from the table has no such window.
                // This branch is unreachable in code with no breakpoints armed, so
                // debugging costs nothing until someone actually sets one.
                case Opcode.Break:
                {
                    opByte = _engine.BreakpointOriginalAt(pc);
                    _engine.Debug?.OnBreak(_engine, pc);
                    // Set Next Statement during the stop: the pending instruction (held in
                    // the LOCALS pc/opByte) is abandoned; the loop re-enters at the moved P.
                    if (_engine.TakeDebugPcRedirect()) { inClause = false; continue; }
                    goto dispatch;
                }

                // ADR-035 — a step's landing at a goal that compiles inline (a `!`, an
                // `is/2`, an `=/2`, a comparison): those goals emit no call, so no other
                // port ever fires for them. One byte, compile_mode=debug code only; with
                // no session attached it is a null check and a fall-through.
                case Opcode.DebugPort:
                {
                    _engine.Debug?.OnInlineGoal(_engine);
                    if (_engine.TakeDebugPcRedirect()) { inClause = false; continue; }
                    _engine.SetPc(pc + 1); inClause = true;
                    break;
                }

                // ADR-035 — the debuggable last call. One of Call or Execute,
                // chosen per dispatch by the activation's LCO flag, so a
                // debugger can turn last-call optimisation off (and back on)
                // mid-session without recompiling. Only ever emitted under
                // compile_mode=debug, and only for a clause that has a frame —
                // with no frame there would be nothing to retain, and the
                // return stub could not restore Cp.
                case Opcode.DebugLastCall:
                {
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int target = ReadI32(code, codeArr, pc + 1);
                    target = ResolveTargetMaybeAutoPromoted(target);
                    if (target == UnknownFailTarget)   // unknown=fail
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    Shumway.Core.Profiler.Call(target);
                    bool lco = _engine.LastCallOptimisation;
                    if (lco)
                    {
                        // Pop the frame ourselves — the deallocate that would
                        // have preceded an Execute now lives in the stub AFTER
                        // us, which this path skips. Deallocate also restores Cp
                        // from the frame, which is exactly the tail-call
                        // continuation.
                        _engine.Deallocate();
                    }
                    else
                    {
                        // Keep the frame and return through the stub sitting
                        // right after us (deallocate_proceed). Deliberately no
                        // TrimEnv: the frame's Y slots are what a debugger reads
                        // the clause's variables from.
                        _engine.SetCp(pc + 9);
                    }
                    _engine.SetB0(_engine.B);
                    DispatchToTier1OrBytecode(target, tailCall: lco);
                    break;
                }

                case Opcode.Execute:
                {
                    // Operand BEFORE the wakeup flush — same in-place
                    // Execute→ExecuteIl repatch hazard as the Call case above.
                    int target = ReadI32(code, codeArr, pc + 1);
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    target = ResolveTargetMaybeAutoPromoted(target);
                    if (target == UnknownFailTarget)   // unknown=fail
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    Shumway.Core.Profiler.Call(target);
                    _engine.SetB0(_engine.B);   // tail call still enters a new procedure
                    DispatchToTier1OrBytecode(target, tailCall: true);
                    break;
                }

                // Call to a bundle-IL-promoted
                // predicate. Operand is the callee's functor id (not an
                // address); the IL delegate is looked up in the
                // interpreter's IlByFunctorId table — direct array
                // access, no interface call, no dictionary probe. Set
                // by the embedding layer at link time after every IL
                // registration; Call sites whose callee has IL are
                // rewritten to CallIl as a single opcode-byte swap
                // (Call and CallIl share width and operand offsets).
                case Opcode.CallIl:
                {
                    _engine.Inferences++;   // time/1 goal-dispatch counter
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int functorId = ReadI32(code, codeArr, pc + 1);
                    int numLivePerms = ReadI32(code, codeArr, pc + 5);
                    Shumway.Core.Profiler.Call(functorId);
                    _engine.Debug?.OnCallFunctor(_engine, functorId, false);   // ADR-035
                    if (_engine.TakeDebugPcRedirect()) { inClause = false; continue; }
                    _engine.TrimEnv(numLivePerms);
                    _engine.SetCp(pc + 9);  // CallIl is 9 bytes, same as Call
                    _engine.SetB0(_engine.B);
                    // ADR-016 safe point — heap GC needs every goal
                    // boundary regardless of dispatch tier.
                    _engine.MaybeCollectHeap();
                    var table = IlByFunctorId;
                    var ilFn = table is not null && (uint)functorId < (uint)table.Length
                        ? table[functorId] : null;
                    // Per-query table miss → the engine-wide dispatcher. A delegate
                    // promoted AFTER this query's setup snapshot (an interleaved
                    // debug evaluation promoting clpfd internals mid-stop is the
                    // real case) rewrote shared bytecode to CallIl; the rewrite is
                    // engine-global, the snapshot is not.
                    ilFn ??= Tier1Dispatcher?.ResolveByFunctorId(functorId);
                    if (ilFn is null)
                    {
                        // IL was unregistered after the link-time
                        // rewrite installed CallIl here. Shouldn't
                        // normally happen for Stage B.1, but bail to
                        // existence_error rather than NRE.
                        throw new InvalidOperationException(
                            $"CallIl: no IL delegate for functor id {functorId}. "
                            + "Bytecode rewrite invariant violated.");
                    }
                    if (!ilFn(_engine, 0))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    if (_engine.IlTailCallPending)
                    {
                        // IL set Pc to its tail-call target; the outer
                        // dispatch loop picks it up next iteration.
                        _engine.IlTailCallPending = false;
                    }
                    else
                    {
                        _engine.SetPc(_engine.Cp);
                    }
                    break;
                }

                // Call to a predicate the linker
                // knows will never have an IL delegate (dynamic / layout-
                // excluded, or any callee under an IL-disabled engine).
                // Operand is the absolute target address, unchanged from
                // the original Call — the linker's rewrite is a single
                // opcode-byte swap. Skips the full
                // Tier1Dispatcher?.OnDispatch path; just does the goal-
                // boundary safe point and jumps.
                case Opcode.CallBytecode:
                {
                    _engine.Inferences++;   // time/1 goal-dispatch counter
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int target = ReadI32(code, codeArr, pc + 1);
                    int numLivePerms = ReadI32(code, codeArr, pc + 5);
                    target = ResolveTargetMaybeAutoPromoted(target);
                    if (target == UnknownFailTarget)   // unknown=fail
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    Shumway.Core.Profiler.Call(target);
                    _engine.Debug?.OnCallAddress(_engine, target, false);   // ADR-035
                    if (_engine.TakeDebugPcRedirect()) { inClause = false; continue; }
                    _engine.TrimEnv(numLivePerms);
                    _engine.SetCp(pc + 9);  // CallBytecode is 9 bytes, same as Call
                    _engine.SetB0(_engine.B);
                    // ADR-035 D5+ — SNS re-enter by a chosen clause: the baked direct
                    // dispatch must honour it too (Cp is set; entering the clause is
                    // exactly a call that skips clause selection).
                    if (_engine.DebugClauseEntryArmed
                        && _engine.TryTakeDebugClauseEntry(target, out var enterClause))
                        target = enterClause(_engine);
                    // ADR-016 safe point.
                    _engine.MaybeCollectHeap();
                    _engine.SetPc(target);
                    break;
                }

                // tail-call to a bundle-IL
                // predicate. 5-byte opcode (same as Execute); operand
                // is the callee functor id. Direct delegate invoke from
                // IlByFunctorId — no OnDispatch.
                case Opcode.ExecuteIl:
                {
                    _engine.Inferences++;   // time/1 goal-dispatch counter
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int functorId = ReadI32(code, codeArr, pc + 1);
                    Shumway.Core.Profiler.Call(functorId);
                    _engine.Debug?.OnCallFunctor(_engine, functorId, true);   // ADR-035
                    if (_engine.TakeDebugPcRedirect()) { inClause = false; continue; }
                    _engine.SetB0(_engine.B);  // tail call still enters a new procedure
                    _engine.MaybeCollectHeap();
                    var table = IlByFunctorId;
                    var ilFn = table is not null && (uint)functorId < (uint)table.Length
                        ? table[functorId] : null;
                    // Same stale-snapshot fallback as CallIl above.
                    ilFn ??= Tier1Dispatcher?.ResolveByFunctorId(functorId);
                    if (ilFn is null)
                        throw new InvalidOperationException(
                            $"ExecuteIl: no IL delegate for functor id {functorId}. "
                            + "Bytecode rewrite invariant violated.");
                    if (!ilFn(_engine, 0))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    if (_engine.IlTailCallPending)
                    {
                        _engine.IlTailCallPending = false;
                    }
                    else
                    {
                        _engine.SetPc(_engine.Cp);
                    }
                    break;
                }

                // tail-call to a bytecode-only
                // predicate. 5-byte opcode (same as Execute); operand is
                // the absolute target address. Skips OnDispatch entirely.
                case Opcode.ExecuteBytecode:
                {
                    _engine.Inferences++;   // time/1 goal-dispatch counter
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int target = ReadI32(code, codeArr, pc + 1);
                    target = ResolveTargetMaybeAutoPromoted(target);
                    if (target == UnknownFailTarget)   // unknown=fail
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    Shumway.Core.Profiler.Call(target);
                    _engine.Debug?.OnCallAddress(_engine, target, true);   // ADR-035
                    if (_engine.TakeDebugPcRedirect()) { inClause = false; continue; }
                    _engine.SetB0(_engine.B);
                    // ADR-035 D5+ — SNS re-enter by a chosen clause (tail form; Cp is the
                    // caller's continuation, already correct for a tail call).
                    if (_engine.DebugClauseEntryArmed
                        && _engine.TryTakeDebugClauseEntry(target, out var enterClause))
                        target = enterClause(_engine);
                    _engine.MaybeCollectHeap();
                    _engine.SetPc(target);
                    break;
                }

                // tail-call to a builtin. 5-byte opcode
                // (same width as Execute / ExecuteIl / ExecuteBytecode);
                // operand is the builtin id. The linker emits this in
                // place of Execute when an Execute site's target is a
                // builtin — typically a foreign predicate the linker
                // discovered via --foreign-dll that wasn't in
                // BuiltinsRegistry at compile time. Behaviour: invoke
                // the builtin (no TrimEnv — we're returning to caller
                // and the caller's frame is already current), then
                // Pc = Cp to return to the caller's continuation.
                case Opcode.ExecuteBuiltin:
                {
                    _engine.Inferences++;   // time/1 goal-dispatch counter
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int builtinId = ReadI32(code, codeArr, pc + 1);
                    // Cheap throw (see CallBuiltin).
                    if (builtinId == ThrowBuiltinId
                        && TryInlineThrow(dispatchCatchFloor))
                        break;
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                    _engine.CurrentBuiltinName = entry.Name;
                    _engine.CurrentBuiltinArity = entry.Arity;
                    // Tail-call return: a backtrackable builtin's
                    // ResumeAtReturnPc must land at the caller's
                    // continuation (our Cp), not at the instruction
                    // after this ExecuteBuiltin (which would loop).
                    _engine.BuiltinReturnPc = _engine.Cp;
                    bool implOk;
                    Shumway.Core.Profiler.BuiltinEnter(builtinId);
                    // ADR-035 call port — tail: this builtin returns to the
                    // caller's continuation, not to our clause.
                    _engine.Debug?.OnCallBuiltin(_engine, builtinId, true);
                    if (_engine.TakeDebugPcRedirect())
                    {
                        Shumway.Core.Profiler.BuiltinExit(builtinId);
                        inClause = false; continue;   // SNS during the stop: skip the builtin
                    }
                    try { implOk = entry.Impl(_engine); }
                    catch (PrologRuntimeException re)
                    {
                        re.StampBuiltin(entry.Name, entry.Arity);
                        throw;
                    }
                    finally
                    {
                        Shumway.Core.Profiler.BuiltinExit(builtinId);
                    }
                    _engine.Debug?.OnBuiltinResult(_engine, builtinId, implOk);
                    if (!implOk)
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    // Return to the caller. A backtrackable builtin
                    // that set IlTailCallPending + Pc has already
                    // chosen the resume address; honour it.
                    if (_engine.IlTailCallPending)
                        _engine.IlTailCallPending = false;
                    else
                        _engine.SetPc(_engine.Cp);
                    break;
                }

                case Opcode.Allocate:
                {
                    int n = ReadI32(code, codeArr, pc + 1);
                    _engine.Allocate(n);
                    RunGetVariableYRun(code, codeArr, codeLen, pc + 5);
                    break;
                }

                case Opcode.Deallocate:
                    _engine.Deallocate();
                    _engine.SetPc(pc + 1); inClause = true;   // deallocate is 1 byte
                    break;

                // ---------- fused opcodes (peephole) ----------

                case Opcode.AllocateGetLevel:
                {
                    // 10-byte layout: [op:1] [count:4] [slot:4] [Nop:1]
                    int n = ReadI32(code, codeArr, pc + 1);
                    int slot = ReadI32(code, codeArr, pc + 5);
                    _engine.Allocate(n);
                    _engine.GetLevel(slot);
                    RunGetVariableYRun(code, codeArr, codeLen, pc + 10);
                    break;
                }

                case Opcode.DeallocateProceed:
                {
                    // 2-byte layout: [op:1] [Nop:1].
                    // Mirrors Deallocate + Proceed back-to-back: deallocate
                    // the env frame, then proceed (FlushPendingWakeups +
                    // SetPc(Cp), with Cp<0 → Halted).
                    _engine.Deallocate();
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.Debug?.OnExit(_engine);            // ADR-035 exit port
                    if (_engine.TakeDebugPcRedirect()) { inClause = false; continue; }
                    int returnPc = _engine.Cp;
                    if (returnPc < 0) return InterpreterResult.Halted;
                    _engine.SetPc(returnPc);
                    break;
                }

                // ---------- ADR-029 — clause-epilogue fusions ----------

                case Opcode.DeallocateExecute:
                {
                    // 6-byte layout: [op:1] [target:4] [Nop:1]. Mirrors
                    // Deallocate + Execute: trim the frame, then tail-call.
                    _engine.Deallocate();
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int target = ReadI32(code, codeArr, pc + 1);
                    target = ResolveTargetMaybeAutoPromoted(target);
                    if (target == UnknownFailTarget)   // unknown=fail
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    Shumway.Core.Profiler.Call(target);
                    _engine.SetB0(_engine.B);   // tail call enters a new procedure
                    DispatchToTier1OrBytecode(target, tailCall: true);
                    break;
                }

                case Opcode.CutDeallocateProceed:
                {
                    // 7-byte layout: [op:1] [slot:4] [Nop:1] [Nop:1]. Mirrors
                    // Cut + Deallocate + Proceed. Flush wakeups BEFORE the cut
                    // commits (see NeckCut/Cut); nothing schedules a wakeup
                    // between the cut and the return, so no second flush.
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int cutSlot = ReadI32(code, codeArr, pc + 1);
                    _engine.Cut((int)_engine.GetY(cutSlot).Data);
                    FlushPendingCleanups(code);   // setup_call_cleanup on cut
                    _engine.Deallocate();
                    _engine.Debug?.OnExit(_engine);            // ADR-035 exit port
                    if (_engine.TakeDebugPcRedirect()) { inClause = false; continue; }
                    int retPc = _engine.Cp;
                    if (retPc < 0) return InterpreterResult.Halted;
                    _engine.SetPc(retPc);
                    break;
                }

                case Opcode.CutProceed:
                {
                    // 6-byte layout: [op:1] [slot:4] [Nop:1]. Mirrors Cut +
                    // Proceed (frameless).
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int cpSlot = ReadI32(code, codeArr, pc + 1);
                    _engine.Cut((int)_engine.GetY(cpSlot).Data);
                    FlushPendingCleanups(code);   // setup_call_cleanup on cut
                    _engine.Debug?.OnExit(_engine);            // ADR-035 exit port
                    if (_engine.TakeDebugPcRedirect()) { inClause = false; continue; }
                    int rpc = _engine.Cp;
                    if (rpc < 0) return InterpreterResult.Halted;
                    _engine.SetPc(rpc);
                    break;
                }

                // ---------- Choice point opcodes ----------

                case Opcode.TryMeElse:
                {
                    int nextClause = ReadI32(code, codeArr, pc + 1);
                    int arity = ReadI32(code, codeArr, pc + 5);
                    // ADR-025 — a body try_me_else (inline ITE/disjunction)
                    // carries the InlineIteCpArity sentinel; its CP saves no
                    // argument registers (branch state lives in Y slots).
                    if (arity < 0) arity = 0;
                    _engine.PushChoicePoint(arity, nextClause);
                    // Peephole fusion: in dynamic chains the
                    // very next opcode is CheckVisible. Inline its
                    // dispatch to skip a switch trip + opcode-table
                    // lookup + profiler bump per chain step. Profiled
                    // Blint had 23.5M direct dispatch→CheckVisible
                    // pairs / run.
                    if (!TryInlineCheckVisible(code, codeArr, codeLen, pc + 9,
                            deadSkipTo: nextClause))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.RetryMeElse:
                {
                    Shumway.Core.Profiler.RetryAt(pc);
                    int nextClause = ReadI32(code, codeArr, pc + 1);
                    // Corruption guard — a retry_me_else whose <next> is its
                    // own address would re-enter itself on every backtrack:
                    // an unbreakable dispatch loop (a mid-chain self-splice
                    // from a bad in-place patch). Fail loudly instead, per
                    // the bytecode-corruption invariant.
                    if (nextClause == pc)
                        throw new Shumway.Core.PrologRuntimeException(
                            "system_error",
                            $"corrupted dynamic chain: retry_me_else at {pc} points at itself");
                    _engine.RetryMeElse(nextClause);
                    // A demoted chain head (asserta's in-place
                    // try_me_else -> retry_me_else demotion) is a 5-byte
                    // retry_me_else followed by 4 Nop bytes padding it
                    // back to the original 9-byte footprint. Skip the
                    // padding in this one step rather than dispatching
                    // four separate Nop instructions: profiling Blint
                    // showed those pad-Nops were ~47% of ALL executed
                    // opcodes (93M of 199M). A native (assertz) retry's
                    // pc+5 is a check_visible / body opcode, never Nop,
                    // so the single-byte peek distinguishes the two.
                    int padPc = pc + 5;
                    bool demoted = padPc < codeLen
                        && (code.Overflow is null ? codeArr[padPc] : code[padPc])
                           == (byte)Opcode.Nop;
                    int afterPc = pc + (demoted ? 9 : 5);
                    // Peephole fusion (see TryMeElse).
                    if (!TryInlineCheckVisible(code, codeArr, codeLen, afterPc,
                            deadSkipTo: nextClause))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.TrustMe:
                {
                    _engine.TrustMe();
                    // Peephole fusion (see TryMeElse).
                    if (!TryInlineCheckVisible(code, codeArr, codeLen, pc + 1))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                // ADR-025 — unconditional intra-predicate branch (inline
                // if-then-else: the then branch jumps over the else). The
                // operand was made program-absolute by the linker's
                // dispatch-site shift.
                case Opcode.Jump:
                {
                    _engine.SetPc(ReadI32(code, codeArr, pc + 1));
                    break;
                }

                // ADR-015 — generation-filtered dynamic dispatch.
                // Sample the dynamic-database generation into CurrentViewGen
                // so the surrounding try_me_else captures it into the CP and
                // every clause's CheckVisible reads the call's stable view.
                case Opcode.EnterDynamic:
                {
                    // sample through the shared GenerationBox (one
                    // field read) instead of invoking the Func<long> provider
                    // per dynamic-predicate call. The provider remains as the
                    // fallback for bare-Activation tests that wire it directly.
                    var box = _engine.DbGenerationBox;
                    if (box is not null)
                    {
                        _engine.CurrentViewGen = box.Value;
                    }
                    else
                    {
                        var provider = _engine.DbGenerationProvider;
                        _engine.CurrentViewGen = provider is null ? 0L : provider();
                    }
                    // ADR-041 — dispatch-time clause selection by the call's
                    // first argument. Determinism must not depend on whether
                    // the chain is indexed yet: with the arg bound and exactly
                    // one candidate clause, jump straight to its code with NO
                    // choice point; with zero candidates, fail outright. The
                    // host returns -2 for "no selection" (unbound arg,
                    // multiple candidates, indexed/unrecognised layout), and
                    // then the trampoline's `execute` runs the chain as ever.
                    var select = _engine.DynChainSelect;
                    if (select is not null)
                    {
                        int sel = select(_engine, pc);
                        if (sel >= 0)
                        {
                            _engine.SetPc(sel); inClause = true;
                            break;
                        }
                        if (sel == -1)
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                            break;
                        }
                    }
                    _engine.SetPc(pc + 1); inClause = true;
                    break;
                }

                // Per-clause visibility check. Reads born/died from the
                // bytecode (retract patches the died slot in place) and
                // backtracks if the calling goal's captured view-gen is
                // outside [born, died) — the ISO logical update view.
                case Opcode.CheckVisible:
                {
                    long born = ReadI64(code, codeArr, pc + 1);
                    long died = ReadI64(code, codeArr, pc + 9);
                    long g = _engine.CurrentViewGen;
                    if (born > g || died <= g)
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 17); inClause = true;
                    break;
                }

                case Opcode.Try:
                {
                    int target = BytecodeIO.ReadInt32(code, pc + 1);
                    int arity = BytecodeIO.ReadInt32(code, pc + 5);
                    // BP is the next opcode in the indexed bucket — that's
                    // where we'll backtrack to if this clause fails.
                    _engine.PushChoicePoint(arity, pc + 9);
                    _engine.SetPc(target);
                    break;
                }

                case Opcode.Retry:
                {
                    int target = BytecodeIO.ReadInt32(code, pc + 1);
                    _engine.RetryMeElse(pc + 5);
                    _engine.SetPc(target);
                    break;
                }

                case Opcode.Trust:
                {
                    int target = BytecodeIO.ReadInt32(code, pc + 1);
                    _engine.TrustMe();
                    _engine.SetPc(target);
                    break;
                }

                // ---------- First-argument indexing ----------

                case Opcode.SwitchOnTerm:
                {
                    int varAddr    = BytecodeIO.ReadInt32(code, pc + 1);
                    int constAddr  = BytecodeIO.ReadInt32(code, pc + 5);
                    int listAddr   = BytecodeIO.ReadInt32(code, pc + 9);
                    int structAddr = BytecodeIO.ReadInt32(code, pc + 13);

                    Cell a1 = DerefA1();
                    int target = a1.Tag switch
                    {
                        Tag.Ref => varAddr,
                        Tag.Atom or Tag.Int or Tag.Float => constAddr,
                        Tag.Lis => listAddr,
                        Tag.Str => structAddr,
                        // PSTR, BigInt, Rational, Foreign, String — fall back to
                        // the var-arg chain. These rarely appear as a clause-head
                        // first argument anyway.
                        _ => varAddr,
                    };
                    _engine.SetPc(target);
                    break;
                }

                case Opcode.SwitchOnAtom:
                {
                    int tableId = BytecodeIO.ReadInt32(code, pc + 1);
                    var table = _switchTables[tableId];
                    Cell a1 = DerefA1();
                    int target = a1.Tag == Tag.Atom
                        ? table.Lookup(a1.AsAtomId)
                        : table.DefaultAddress;
                    _engine.SetPc(target);
                    break;
                }

                case Opcode.SwitchOnInteger:
                {
                    int tableId = BytecodeIO.ReadInt32(code, pc + 1);
                    var table = _switchTables[tableId];
                    Cell a1 = DerefA1();
                    int target;
                    if (a1.Tag == Tag.Int)
                    {
                        long value = a1.AsInt;
                        // Switch tables key on 32-bit ints. Out-of-range values
                        // (those that won't fit in an int operand anyway) miss
                        // the table and fall to default.
                        target = value >= int.MinValue && value <= int.MaxValue
                            ? table.Lookup((int)value)
                            : table.DefaultAddress;
                    }
                    else
                    {
                        target = table.DefaultAddress;
                    }
                    _engine.SetPc(target);
                    break;
                }

                case Opcode.SwitchOnStructure:
                {
                    int tableId = BytecodeIO.ReadInt32(code, pc + 1);
                    var table = _switchTables[tableId];
                    Cell a1 = DerefA1();
                    int target;
                    if (a1.Tag == Tag.Str)
                    {
                        int functorIdx = a1.AsHeapIndex;
                        int functorId = _engine.GetHeap(functorIdx).AsFunctorId;
                        target = table.Lookup(functorId);
                    }
                    else
                    {
                        // Lis would have routed through switch_on_term's list
                        // branch already; anything else here is a type
                        // mismatch and falls to the default chain.
                        target = table.DefaultAddress;
                    }
                    _engine.SetPc(target);
                    break;
                }

                // ---------- Multi-arg indexing ----------

                case Opcode.SwitchOnArg:
                {
                    int argIdx    = BytecodeIO.ReadInt32(code, pc + 1);
                    int varAddr    = BytecodeIO.ReadInt32(code, pc + 5);
                    int constAddr  = BytecodeIO.ReadInt32(code, pc + 9);
                    int listAddr   = BytecodeIO.ReadInt32(code, pc + 13);
                    int structAddr = BytecodeIO.ReadInt32(code, pc + 17);

                    Cell ak = DerefArg(argIdx);
                    int target = ak.Tag switch
                    {
                        Tag.Ref => varAddr,
                        Tag.Atom or Tag.Int or Tag.Float => constAddr,
                        Tag.Lis => listAddr,
                        Tag.Str => structAddr,
                        _ => varAddr,
                    };
                    _engine.SetPc(target);
                    break;
                }

                case Opcode.SwitchOnAtomArg:
                {
                    int argIdx  = BytecodeIO.ReadInt32(code, pc + 1);
                    int tableId = BytecodeIO.ReadInt32(code, pc + 5);
                    var table = _switchTables[tableId];
                    Cell ak = DerefArg(argIdx);
                    int target = ak.Tag == Tag.Atom
                        ? table.Lookup(ak.AsAtomId)
                        : table.DefaultAddress;
                    _engine.SetPc(target);
                    break;
                }

                case Opcode.SwitchOnIntegerArg:
                {
                    int argIdx  = BytecodeIO.ReadInt32(code, pc + 1);
                    int tableId = BytecodeIO.ReadInt32(code, pc + 5);
                    var table = _switchTables[tableId];
                    Cell ak = DerefArg(argIdx);
                    int target;
                    if (ak.Tag == Tag.Int)
                    {
                        long value = ak.AsInt;
                        target = value >= int.MinValue && value <= int.MaxValue
                            ? table.Lookup((int)value)
                            : table.DefaultAddress;
                    }
                    else
                    {
                        target = table.DefaultAddress;
                    }
                    _engine.SetPc(target);
                    break;
                }

                case Opcode.SwitchOnStructureArg:
                {
                    int argIdx  = BytecodeIO.ReadInt32(code, pc + 1);
                    int tableId = BytecodeIO.ReadInt32(code, pc + 5);
                    var table = _switchTables[tableId];
                    Cell ak = DerefArg(argIdx);
                    int target;
                    if (ak.Tag == Tag.Str)
                    {
                        int functorIdx = ak.AsHeapIndex;
                        int functorId = _engine.GetHeap(functorIdx).AsFunctorId;
                        target = table.Lookup(functorId);
                    }
                    else
                    {
                        target = table.DefaultAddress;
                    }
                    _engine.SetPc(target);
                    break;
                }

                // ---------- Second-level (sub-argument) indexing (ADR-027) ----------

                case Opcode.SwitchOnAtomSub:
                {
                    int argIdx  = BytecodeIO.ReadInt32(code, pc + 1);
                    int sub0    = BytecodeIO.ReadInt32(code, pc + 5);
                    int sub1    = BytecodeIO.ReadInt32(code, pc + 9);
                    int tableId = BytecodeIO.ReadInt32(code, pc + 13);
                    var table = _switchTables[tableId];
                    int target = TrySubCell(DerefArg(argIdx), sub0, sub1, out Cell sub) && sub.Tag == Tag.Atom
                        ? table.Lookup(sub.AsAtomId)
                        : table.DefaultAddress;
                    _engine.SetPc(target);
                    break;
                }

                case Opcode.SwitchOnIntegerSub:
                {
                    int argIdx  = BytecodeIO.ReadInt32(code, pc + 1);
                    int sub0    = BytecodeIO.ReadInt32(code, pc + 5);
                    int sub1    = BytecodeIO.ReadInt32(code, pc + 9);
                    int tableId = BytecodeIO.ReadInt32(code, pc + 13);
                    var table = _switchTables[tableId];
                    int target = table.DefaultAddress;
                    if (TrySubCell(DerefArg(argIdx), sub0, sub1, out Cell sub) && sub.Tag == Tag.Int)
                    {
                        long v = sub.AsInt;
                        if (v >= int.MinValue && v <= int.MaxValue) target = table.Lookup((int)v);
                    }
                    _engine.SetPc(target);
                    break;
                }

                // ---------- Structure-keyed sub-argument indexing (ADR-028) ----------

                case Opcode.SwitchOnStructureSub:
                {
                    int argIdx  = BytecodeIO.ReadInt32(code, pc + 1);
                    int sub0    = BytecodeIO.ReadInt32(code, pc + 5);
                    int sub1    = BytecodeIO.ReadInt32(code, pc + 9);
                    int tableId = BytecodeIO.ReadInt32(code, pc + 13);
                    var table = _switchTables[tableId];
                    int target = table.DefaultAddress;
                    if (TrySubCell(DerefArg(argIdx), sub0, sub1, out Cell sub))
                    {
                        // A Str terminal keys by its functor id; a nested list
                        // ('.'/2, ADR-017 inline cons — no functor cell) keys as
                        // the pre-registered cons functor.
                        if (sub.Tag == Tag.Str)
                            target = table.Lookup(_engine.GetHeap(sub.AsHeapIndex).AsFunctorId);
                        else if (sub.Tag == Tag.Lis)
                            target = table.Lookup(AtomTable.ConsFunctorId);
                    }
                    _engine.SetPc(target);
                    break;
                }

                // ---------- Cut opcodes ----------

                case Opcode.NeckCut:
                    // A cut is a goal boundary: any attribute-hook wakeup
                    // queued by the preceding goal (e.g. a clpfd attvar bound
                    // to a value whose domain check is still pending) must run
                    // BEFORE the cut removes the choice points it might need to
                    // backtrack into. Without this, a constraint that fails
                    // after the cut commits has no surviving CP to retry —
                    // surfacing as an unsound whole-goal failure inside an
                    // if-then-else condition.
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.NeckCut();
                    FlushPendingCleanups(code);   // setup_call_cleanup on cut
                    _engine.SetPc(pc + 1); inClause = true;
                    break;

                case Opcode.GetLevel:
                {
                    int slot = ReadI32(code, codeArr, pc + 1);
                    _engine.GetLevel(slot);
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                case Opcode.GetLevelB:
                {
                    // ADR-025 — capture CURRENT B as the inline-ITE barrier.
                    int slot = ReadI32(code, codeArr, pc + 1);
                    _engine.GetLevelB(slot);
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                case Opcode.Cut:
                {
                    // See NeckCut: flush pending attribute wakeups before the
                    // cut commits, so a constraint that fails can still
                    // backtrack into the about-to-be-pruned choice points.
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int slot = ReadI32(code, codeArr, pc + 1);
                    int barrier = (int)_engine.GetY(slot).Data;
                    _engine.Cut(barrier);
                    FlushPendingCleanups(code);   // setup_call_cleanup on cut
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                case Opcode.SoftCut:
                {
                    // ADR-037 — commit the inline ( Cond *-> Then ; Else ): flush
                    // pending attribute wakeups first (as Cut does), then
                    // neutralise the ELSE choice point named by the slot. Cond's
                    // choice points survive, so no over-pruning.
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int slot = ReadI32(code, codeArr, pc + 1);
                    int barrier = (int)_engine.GetY(slot).Data;
                    _engine.SoftCut(barrier);
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                // ---------- Compound (STR) and list (LIS) — open instructions ----------

                case Opcode.GetStructure:
                {
                    int functorId = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    if (!_engine.GetStructure(functorId, arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 9))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.PutStructure:
                {
                    int functorId = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.PutStructure(functorId, arg);
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.PutStructureR:   // ADR-020 reserve-upfront root
                {
                    int functorId = ReadI32(code, codeArr, pc + 1);
                    int packed = ReadI32(code, codeArr, pc + 5);
                    _engine.PutStructureReserved(functorId, packed & 0xFFFFFF, packed >> 24);
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.PutListR:   // ADR-020 reserve-upfront cons root
                {
                    int arg = ReadI32(code, codeArr, pc + 1);
                    _engine.PutListReserved(arg);
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                case Opcode.GetList:
                {
                    int arg = ReadI32(code, codeArr, pc + 1);
                    if (!_engine.GetList(arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    // consume the following unify-family run inline.
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.PutList:
                {
                    int arg = ReadI32(code, codeArr, pc + 1);
                    _engine.PutList(arg);
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                case Opcode.GetListA1:
                    if (!_engine.GetList(0))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 1))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;

                case Opcode.GetListA2:
                    if (!_engine.GetList(1))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 1))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;

                // ---------- Unify-mode opcodes (consume cells via _unifyPointer) ----------

                case Opcode.UnifyVariableX:
                {
                    int target = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVariableX(target);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                        }
                        break;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeapUnbound();
                        _engine.SetRegister(target, Cell.Ref(idx));
                    }
                    else
                    {
                        // A bare ATTVAR at the unify pointer
                        // is a variable at its home — capture it as a
                        // REF to that home, never a copied ATTVAR cell.
                        Cell src = _engine.GetHeap(ptr);
                        _engine.SetRegister(target,
                            src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src);
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyVariableY:
                {
                    int target = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVariableY(target);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                        }
                        break;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeapUnbound();
                        _engine.SetY(target, Cell.Ref(idx));
                    }
                    else
                    {
                        // See UnifyVariableX: a bare ATTVAR is captured
                        // as a REF to its home.
                        Cell src = _engine.GetHeap(ptr);
                        _engine.SetY(target,
                            src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src);
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyValueX:
                {
                    int src = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyValueX(src);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                        }
                        break;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, _engine.GetRegister(src));
                    }
                    else
                    {
                        if (!_engine.UnifyRegisterWithHeapAt(src, ptr))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                            break;
                        }
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyValueY:
                {
                    int src = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyValueY(src);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                        }
                        break;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, _engine.GetY(src));
                    }
                    else
                    {
                        if (!_engine.UnifyPermanentWithHeapAt(src, ptr))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                            break;
                        }
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyConstant:
                case Opcode.UnifyAtom:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);
                    Cell value = Cell.Atom(atomId);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyArgCell(value);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                        }
                        break;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, value);
                    }
                    else
                    {
                        if (!_engine.UnifyHeapWithCell(ptr, value))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                            break;
                        }
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyInteger:
                {
                    int intValue = ReadI32(code, codeArr, pc + 1);
                    Cell value = Cell.Int(intValue);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyArgCell(value);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                        }
                        break;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, value);
                    }
                    else
                    {
                        if (!_engine.UnifyHeapWithCell(ptr, value))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                            break;
                        }
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyNil:
                {
                    Cell value = Cell.Atom(AtomTable.EmptyListId);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyArgCell(value);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 1))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                        }
                        break;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, value);
                    }
                    else
                    {
                        if (!_engine.UnifyHeapWithCell(ptr, value))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                            break;
                        }
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 1))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyStructure:
                {
                    int functorId = ReadI32(code, codeArr, pc + 1);
                    if (!_engine.UnifyStructure(functorId))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyList:
                {
                    if (!_engine.UnifyList())
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 1))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyVoid:
                {
                    int count = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVoid(count);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                        }
                        break;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        for (int i = 0; i < count; i++)
                            _engine.AllocateHeapUnbound();
                    }
                    _engine.SetUnifyPointer(ptr + count);
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                // ---------- Get instructions ----------

                case Opcode.GetVariableX:
                {
                    int dest = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetRegister(dest, _engine.GetRegister(arg));
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.GetVariableY:
                {
                    int dest = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetY(dest, _engine.GetRegister(arg));
                    RunGetVariableYRun(code, codeArr, codeLen, pc + 9);
                    break;
                }

                case Opcode.GetValueX:
                {
                    int src = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    if (!_engine.UnifyRegisters(src, arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.GetValueY:
                {
                    int src = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    if (!_engine.UnifyPermanentWithRegister(src, arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.GetConstant:
                case Opcode.GetAtom:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Atom(atomId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.GetInteger:
                {
                    int value = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Int(value)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.GetNil:
                {
                    int arg = ReadI32(code, codeArr, pc + 1);
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Atom(AtomTable.EmptyListId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                // ---------- Put instructions ----------

                case Opcode.PutVariableX:
                {
                    int dest = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    int heapIdx = _engine.AllocateHeapUnbound();
                    Cell refCell = Cell.Ref(heapIdx);
                    _engine.SetRegister(dest, refCell);
                    _engine.SetRegister(arg, refCell);
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.PutVariableY:
                {
                    int dest = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    int heapIdx = _engine.AllocateHeapUnbound();
                    Cell refCell = Cell.Ref(heapIdx);
                    _engine.SetY(dest, refCell);
                    _engine.SetRegister(arg, refCell);
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.PutValueX:
                {
                    int src = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetRegister(arg, _engine.GetRegister(src));
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.PutValueY:
                {
                    int src = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetRegister(arg, _engine.GetY(src));
                    RunPutValueYRun(code, codeArr, codeLen, pc + 9);
                    break;
                }

                case Opcode.PutConstant:
                case Opcode.PutAtom:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetRegister(arg, Cell.Atom(atomId));
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.PutInteger:
                {
                    int value = ReadI32(code, codeArr, pc + 1);
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetRegister(arg, Cell.Int(value));
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.PutNil:
                {
                    int arg = ReadI32(code, codeArr, pc + 1);
                    _engine.SetRegister(arg, Cell.Atom(AtomTable.EmptyListId));
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                // ---------- Consolidated A1/A2 specialisations ----------

                case Opcode.GetConstantA1:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);
                    if (!_engine.UnifyRegisterWithCell(0, Cell.Atom(atomId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                case Opcode.GetConstantA2:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);
                    if (!_engine.UnifyRegisterWithCell(1, Cell.Atom(atomId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                case Opcode.PutConstantA1:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);
                    _engine.SetRegister(0, Cell.Atom(atomId));
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                case Opcode.PutConstantA2:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);
                    _engine.SetRegister(1, Cell.Atom(atomId));
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                // ---------- Builtin call ----------

                case Opcode.CallBuiltin:
                {
                    _engine.Inferences++;   // time/1 goal-dispatch counter
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int builtinId = ReadI32(code, codeArr, pc + 1);
                    int numLivePerms = ReadI32(code, codeArr, pc + 5);
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                    // Env trimming: shrink the current frame BEFORE
                    // the builtin runs, so any choice point the builtin pushes
                    // (e.g. multi-solution call/N, non-deterministic
                    // append/atom_concat splits) lands at the trimmed _stackTop
                    // and isn't overwritten by a post-Impl trim. Trimming
                    // before is safe because the builtin reads its arguments
                    // from X registers — Y-slot reads from this frame happen
                    // in the get_value_y / put_value_y instructions that
                    // precede this call_builtin in source order.
                    _engine.TrimEnv(numLivePerms);
                    // call/1..7 — dispatch the runtime goal as a real goal
                    // in the live engine, with full backtracking,
                    // rather than running the sub-engine builtin.
                    if (entry.IsCall)
                    {
                        // A backtrackable (cursor)
                        // builtin reached THROUGH the meta-call captures
                        // BuiltinReturnPc for its resume; without this it
                        // kept the PREVIOUS call_builtin's continuation and
                        // a retry re-entered the middle of the clause
                        // (observed: call(stream_property(S, P)) re-running
                        // the call/1 with a clobbered X0 → type_error).
                        _engine.BuiltinReturnPc = pc + 9;
                        // A top-level call/N: a `!` written as the goal
                        // commits no further than the call itself, so the
                        // barrier is B as the call is entered.
                        if (!DispatchCall(code, entry.Arity, _engine.B))
                            return InterpreterResult.Failed;
                        break;
                    }
                    if (entry.IsDollarCall)
                    {
                        _engine.BuiltinReturnPc = pc + 9;   // see IsCall above
                        // Cut-barrier-carrying meta-call from a $call_*
                        // control helper: X1 carries the barrier
                        // the enclosing call established for a `!` in X0.
                        int barrier = (int)DerefCell(_engine.GetRegister(1)).AsInt;
                        if (!DispatchCall(code, 1, barrier))
                            return InterpreterResult.Failed;
                        break;
                    }
                    // Cheap throw — same-dispatch catcher resolves with a PC
                    // jump instead of a .NET exception (clpz's
                    // with_local_attributes throws once per propagation).
                    if (builtinId == ThrowBuiltinId
                        && TryInlineThrow(dispatchCatchFloor))
                        break;
                    // thread the offending builtin's identity
                    // so a thrown error term reports the right culprit.
                    //   * engine.CurrentBuiltinName for direct
                    //     ShumwayPrologException(IsoError.X(..., engine))
                    //     throws from inside the impl;
                    //   * StampBuiltin on a PrologRuntimeException as it
                    //     unwinds out of the impl, so the catch handler
                    //     way up the stack (possibly in a different
                    //     PrologEngine after a meta-call) still sees the
                    //     Name/Arity. Idempotent — outer dispatch can't
                    //     overwrite the innermost throw's identity.
                    _engine.CurrentBuiltinName = entry.Name;
                    _engine.CurrentBuiltinArity = entry.Arity;
                    // the post-call_builtin address — backtrackable
                    // builtins (between, append, atom_concat, repeat, retract,
                    // …) capture this on first invocation and pass it to
                    // ResumeAtReturnPc on each retry success. Builtins must
                    // not compute it as `engine.P + 9` themselves: under
                    // Tier-1 IL, Pc doesn't point at the opcode.
                    _engine.BuiltinReturnPc = pc + 9;
                    bool implOk;
                    Shumway.Core.Profiler.BuiltinEnter(builtinId);
                    // ADR-035 call port. Deliberately below the IsCall /
                    // IsDollarCall arms above: a meta-call wrapper is not a
                    // goal the user wrote — the goal it dispatches reports
                    // itself through DispatchToTier1OrBytecode.
                    _engine.Debug?.OnCallBuiltin(_engine, builtinId, false);
                    if (_engine.TakeDebugPcRedirect())
                    {
                        Shumway.Core.Profiler.BuiltinExit(builtinId);
                        inClause = false; continue;   // SNS during the stop: skip the builtin
                    }
                    try
                    {
                        implOk = entry.Impl(_engine);
                    }
                    catch (PrologRuntimeException re)
                    {
                        re.StampBuiltin(entry.Name, entry.Arity);
                        throw;
                    }
                    finally
                    {
                        Shumway.Core.Profiler.BuiltinExit(builtinId);
                    }
                    _engine.Debug?.OnBuiltinResult(_engine, builtinId, implOk);
                    if (!implOk)
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    // deliberately AdvancePc, NOT SetPc(pc + 9) —
                    // entry.Impl ran arbitrary builtin code between the pc
                    // capture and here, so the mechanical substitution's
                    // "P still equals pc" precondition can't be verified
                    // per-site for every builtin.
                    if (PcRing is not null && _engine.P != pc)
                        System.Console.Error.WriteLine(
                            $"[PC-RING] builtin '{entry.Name}/{entry.Arity}' (id {builtinId})"
                            + $" succeeded with P moved: pc=0x{pc:X} P=0x{_engine.P:X}"
                            + $" IlTailCallPending={_engine.IlTailCallPending}");
                    _engine.AdvancePc(9);
                    break;
                }

                // ---------- PSTR opcodes ----------

                case Opcode.GetPstr:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    int headerIdx = _engine.MakePstr(ResolveLiteral(literalId));
                    if (!_engine.UnifyRegisterWithHeapAt(arg, headerIdx))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.PutPstr:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    int headerIdx = _engine.MakePstr(ResolveLiteral(literalId));
                    _engine.SetRegister(arg, Cell.Ref(headerIdx));
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.UnifyPstrHead:
                {
                    int dest = BytecodeIO.ReadInt32(code, pc + 1);
                    if (!_engine.AdvancePstrHead(_engine.UnifyPointer, out Cell head))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetRegister(dest, head);
                    // The cursor stays put: heap[UnifyPointer] now holds either the
                    // advanced PSTR header (still iterable) or the PSTR's tail value
                    // (so subsequent unify_nil / unify_value can match against it).
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                // ---------- Float literal opcodes ----------

                case Opcode.GetFloat:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    int headerIdx = _engine.MakeFloat(ResolveFloatLiteral(literalId));
                    if (!_engine.UnifyRegisterWithHeapAt(arg, headerIdx))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.PutFloat:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    int headerIdx = _engine.MakeFloat(ResolveFloatLiteral(literalId));
                    _engine.SetRegister(arg, Cell.Ref(headerIdx));
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.UnifyFloat:
                {
                    // The compiler doesn't emit this opcode — float sub-args
                    // are routed through put_float-to-temp + unify_value_x
                    // by PreEmitMultiCellLiterals so they don't disrupt the
                    // unify_pointer == heap_top invariant. The dispatch is
                    // kept for completeness in read mode (write mode can't
                    // honour the invariant for a 2-cell value inline).
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int ptr = _engine.UnifyPointer;
                    double value = ResolveFloatLiteral(literalId);
                    int idx = _engine.MakeFloat(value);
                    if (_engine.WriteMode)
                    {
                        throw new NotSupportedException(
                            "unify_float in write mode would corrupt the compound's "
                            + "arg layout — emit put_float + unify_value_x instead.");
                    }
                    if (!_engine.UnifyHeapWithCell(ptr, Cell.Ref(idx)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                // ---------- BigInteger literal opcodes ----------

                case Opcode.GetBigInt:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    Cell bigCell = _engine.MakeBigInt(ResolveBigIntLiteral(literalId));
                    if (!_engine.UnifyRegisterWithCell(arg, bigCell))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.PutBigInt:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.SetRegister(arg, _engine.MakeBigInt(ResolveBigIntLiteral(literalId)));
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.UnifyBigInt:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    Cell value = _engine.MakeBigInt(ResolveBigIntLiteral(literalId));
                    // ADR-020: inside a reserve-upfront inline
                    // build the value must land in the RESERVED arg slot, not
                    // at the heap top — a bigint cell is a single cell (the
                    // payload is the aux-table id / an immediate), so it slots
                    // in exactly like unify_integer. Without this branch the
                    // reserved slot stayed unwritten (fresh heap: an
                    // accidental unbound var; recycled heap: a stale cell —
                    // the Logtalk random-library seed corruption).
                    if (_engine.ReservedWrite)
                    {
                        _engine.UnifyArgCell(value);
                        _engine.SetPc(pc + 5); inClause = true;
                        break;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, value);
                    }
                    else
                    {
                        if (!_engine.UnifyHeapWithCell(ptr, value))
                        {
                            if (!TryBacktrack()) return InterpreterResult.Failed;
                            break;
                        }
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    _engine.SetPc(pc + 5); inClause = true;
                    break;
                }

                // ---------- ADR-018 arithmetic instruction set ----------
                // The shared evaluation stack and operator logic live in
                // Shumway.Builtins.ArithEvalStack so Tier-0 and Tier-1 IL run
                // exactly the same code. Only the bigint / float literal
                // operands (kinds 1 / 2) need the interpreter's literal pools,
                // so they resolve here before pushing.
                case Opcode.AEvalPush:
                {
                    int kind = BytecodeIO.ReadInt32(code, pc + 1);
                    int operand = BytecodeIO.ReadInt32(code, pc + 5);
                    switch (kind)
                    {
                        case 0: Shumway.Builtins.ArithEvalStack.PushInt(operand); break;
                        case 1: Shumway.Builtins.ArithEvalStack.Push(
                            new Shumway.Builtins.Number(ResolveBigIntLiteral(operand))); break;
                        case 2: Shumway.Builtins.ArithEvalStack.Push(
                            new Shumway.Builtins.Number(ResolveFloatLiteral(operand))); break;
                        case 3: Shumway.Builtins.ArithEvalStack.PushReg(_engine, operand); break;
                        case 4: Shumway.Builtins.ArithEvalStack.PushY(_engine, operand); break;
                        default: throw new InvalidOperationException($"Bad a_eval_push kind {kind}.");
                    }
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.AEvalBin:
                    Shumway.Builtins.ArithEvalStack.Bin(BytecodeIO.ReadInt32(code, pc + 1), _engine.PreferRationals);
                    _engine.SetPc(pc + 5); inClause = true;
                    break;

                case Opcode.AEvalUn:
                    Shumway.Builtins.ArithEvalStack.Un(BytecodeIO.ReadInt32(code, pc + 1));
                    _engine.SetPc(pc + 5); inClause = true;
                    break;

                case Opcode.AEvalIs:
                {
                    int kind = BytecodeIO.ReadInt32(code, pc + 1);
                    int target = BytecodeIO.ReadInt32(code, pc + 5);
                    // kinds 3/4 unify the result with an existing var (X-reg /
                    // Y-slot); 5/6 store it into a first-occurrence var's home
                    // (no unify — always succeeds).
                    bool ok;
                    switch (kind)
                    {
                        case 5: Shumway.Builtins.ArithEvalStack.SetReg(_engine, target); ok = true; break;
                        case 6: Shumway.Builtins.ArithEvalStack.SetPerm(_engine, target); ok = true; break;
                        case 4: ok = Shumway.Builtins.ArithEvalStack.IsPerm(_engine, target); break;
                        default: ok = Shumway.Builtins.ArithEvalStack.IsReg(_engine, target); break;
                    }
                    if (!ok) { if (!TryBacktrack()) return InterpreterResult.Failed; break; }
                    _engine.SetPc(pc + 9); inClause = true;
                    break;
                }

                case Opcode.AEvalCmp:
                    if (!Shumway.Builtins.ArithEvalStack.Cmp(BytecodeIO.ReadInt32(code, pc + 1)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 5); inClause = true;
                    break;

                case Opcode.AIntBin:
                {
                    // Compact encoding: packed = aKind | bKind<<8 | tKind<<16 | op<<24.
                    int packed = BytecodeIO.ReadInt32(code, pc + 1);
                    bool ok = Shumway.Builtins.ArithEvalStack.FusedBin(_engine,
                        (packed >> 24) & 0xFF,                  // op
                        packed & 0xFF,                          // aKind
                        BytecodeIO.ReadInt32(code, pc + 5),     // aVal
                        (packed >> 8) & 0xFF,                   // bKind
                        BytecodeIO.ReadInt32(code, pc + 9),     // bVal
                        (packed >> 16) & 0xFF,                  // tKind
                        BytecodeIO.ReadInt32(code, pc + 13));   // tVal
                    if (!ok) { if (!TryBacktrack()) return InterpreterResult.Failed; break; }
                    _engine.SetPc(pc + 17); inClause = true;
                    break;
                }

                case Opcode.AIntCmp:
                {
                    // Compact encoding: packed = aKind | bKind<<8 | rel<<16.
                    int packed = BytecodeIO.ReadInt32(code, pc + 1);
                    if (!Shumway.Builtins.ArithEvalStack.FusedCmp(_engine,
                        (packed >> 16) & 0xFF,                  // rel
                        packed & 0xFF,                          // aKind
                        BytecodeIO.ReadInt32(code, pc + 5),     // aVal
                        (packed >> 8) & 0xFF,                   // bKind
                        BytecodeIO.ReadInt32(code, pc + 9)))    // bVal
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 13); inClause = true;
                    break;
                }

                case Opcode.Meta:
                {
                    // Runtime no-op. Meta opcodes (currently only DbgInfo)
                    // carry compile-time metadata for the stack-trace path;
                    // their byte size is determined by their sub-opcode
                    // (DbgInfo = 6: opcode + sub + 4-byte entry id).
                    var sub = (MetaSubOpcode)code[pc + 1];
                    int metaSize = sub switch
                    {
                        MetaSubOpcode.DbgInfo => 6,
                        _ => throw new InvalidOperationException(
                            $"Unknown meta sub-opcode 0x{(byte)sub:X2} at PC=0x{pc:X4}."),
                    };
                    _engine.SetPc(pc + metaSize); inClause = true;
                    break;
                }

                default:
                    throw new NotImplementedException(
                        $"Opcode 0x{opByte:X2} ({(Opcode)opByte}) is not implemented yet. " +
                        $"Reached at PC=0x{pc:X4}.");
            }
        }
    }

    /// <summary>
    /// Handles a unification failure by redirecting control to the current choice point's
    /// BP. The CP itself is preserved — the BP target (a <c>retry_me_else</c> or
    /// <c>trust_me</c>) is the instruction that knows whether to keep the CP or discard
    /// it, and either way it restores the saved engine state before continuing.
    /// </summary>
    /// <returns><c>true</c> if a CP exists and PC has been redirected to its BP;
    /// <c>false</c> if no CP is active, in which case the caller should report
    /// <see cref="InterpreterResult.Failed"/>.</returns>
    /// <summary>Shared dispatch logic for <c>Call</c> and <c>Execute</c>:
    /// if the target has an IL replacement, invoke it; otherwise set Pc
    /// to the target so the loop dispatches the target's bytecode. When
    /// an IL delegate returns with <c>IlTailCallPending</c>, the helper
    /// repeats the dispatch on the new target — so a chain of IL
    /// predicates that each tail-call another stays entirely in IL
    /// without bouncing through bytecode.</summary>
    private void DispatchToTier1OrBytecode(int target, bool tailCall)
    {
        _engine.Inferences++;   // time/1 goal-dispatch counter (Call + Execute)
        // A resume marker (not a real bytecode address) names an IL-only
        // predicate by functor id — e.g. a --strip-wam predicate reached via
        // a runtime meta-call (CurrentFunctorAddresses maps it to the marker).
        // Don't feed it to OnDispatch, which expects an address; hand it to the
        // outer Dispatch loop, whose IsResumeMarker check routes it to the IL
        // delegate via IlByFunctorId.
        if (Activation.IsResumeMarker(target))
        {
            _engine.Debug?.OnCallFunctor(                            // ADR-035 call port
                _engine, Activation.DecodeResumeMarker(target).FunctorId, tailCall);
            // SNS during the stop: P was moved and must not be overwritten with the
            // callee; the outer loop re-enters at the redirected position.
            if (_engine.TakeDebugPcRedirect()) return;
            _engine.SetPc(target);
            return;
        }

        _engine.Debug?.OnCallAddress(_engine, target, tailCall);     // ADR-035 call port
        if (_engine.TakeDebugPcRedirect()) return;                   // SNS during the stop

        // ADR-035 D5+ — Set Next Statement onto a SIBLING clause's head: this dispatch is
        // the re-run of the caller's call after the rewind, and it enters the chosen
        // clause directly (committed — no clause choice point) instead of the predicate's
        // entry. One-shot; armed only from a stop, so the armed check costs one field
        // read on the debug path only.
        if (_engine.DebugClauseEntryArmed
            && _engine.TryTakeDebugClauseEntry(target, out var enterClause))
            target = enterClause(_engine);

        while (true)
        {
            // ADR-016 safe point: a goal boundary. All live heap
            // references are in the engine (registers / Y slots / CPs /
            // trails) for both tiers, so a watermark-triggered collection
            // here is sound. Covers Tier-0 dispatch, Tier-1 entry, and
            // Tier-1 tail-call chains (which loop in this method).
            _engine.MaybeCollectHeap();
            var ilFn = Tier1Dispatcher?.OnDispatch(target);
            if (ilFn is null)
            {
                _engine.SetPc(target);
                return;
            }
            if (!ilFn(_engine))
            {
                if (!TryBacktrack()) throw new TopLevelFailure();
                return;
            }
            if (_engine.IlTailCallPending)
            {
                // The IL set Pc to its tail-call target. Try IL on
                // *that* target too.
                _engine.IlTailCallPending = false;
                target = _engine.P;
                continue;
            }
            _engine.SetPc(_engine.Cp);
            return;
        }
    }

    /// <summary>Internal flow-control exception used by
    /// <see cref="DispatchToTier1OrBytecode"/> to propagate
    /// "backtrack failed, return InterpreterResult.Failed" out of the
    /// IL-chained dispatch loop without restructuring the outer
    /// switch.</summary>
    private sealed class TopLevelFailure : Exception { }

    /// <summary>peeled operand read. The hot handlers read their
    /// operands through the same single-buffer <c>codeArr</c> the dispatch loop
    /// already peeled for the opcode byte, instead of
    /// <see cref="BytecodeIO.ReadInt32(in ProgramView, int)"/>, which re-tests
    /// <c>Overflow is null</c> + the split boundary per read through an
    /// <c>in</c>-struct indirection. The JIT inlines this and can CSE the
    /// <c>Overflow is null</c> branch with the handler's surrounding peeled
    /// reads. The split-view case (Overflow non-null — only mid-query during
    /// persistent + per-query split) falls back to the routing
    /// overload unchanged.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int ReadI32(in Shumway.Core.ProgramView code, byte[] codeArr, int offset)
        => code.Overflow is null
            ? BytecodeIO.ReadInt32(codeArr, offset)
            : BytecodeIO.ReadInt32(code, offset);

    /// <summary>peeled 8-byte operand read; see
    /// <see cref="ReadI32"/>. Worst pre-peel offender was
    /// <see cref="TryInlineCheckVisible"/>'s two ReadInt64s — 23.5M chain
    /// steps per Blint run.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static long ReadI64(in Shumway.Core.ProgramView code, byte[] codeArr, int offset)
        => code.Overflow is null
            ? BytecodeIO.ReadInt64(codeArr, offset)
            : BytecodeIO.ReadInt64(code, offset);

}
