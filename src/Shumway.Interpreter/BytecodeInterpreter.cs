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
public sealed class BytecodeInterpreter
{
    private readonly Activation _engine;
#if SHUMWAY_PROFILE
    private static readonly bool _dispatchTrace =
        System.Environment.GetEnvironmentVariable("ITE_TRACE") == "1";
#endif
    // Not readonly: ADR-015 chunk C recompiles a dynamic predicate
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
    /// backtracking stays contained at its entry level (chunk 80).</summary>
    private int _backtrackFloor = -1;

    // Functor ids the chunk-80 attributed-variable wakeup driver needs,
    // interned once: the verify_attributes/4 user hook (its presence in
    // the linked program is what enables wakeups at all) and the few
    // control-construct functors the in-engine meta-call recognises.
    private static readonly int VerifyAttributesFunctorId =
        FunctorTable.Intern(AtomTable.Intern("verify_attributes", permanent: true).Id, 4);
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
    // helper (chunk 86), since the operator atoms are awkward to compile.
    private static readonly int DisjFunctorId =
        FunctorTable.Intern(AtomTable.Intern(";", permanent: true).Id, 2);
    private static readonly int ArrowFunctorId =
        FunctorTable.Intern(AtomTable.Intern("->", permanent: true).Id, 2);
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
    // (chunk 88). $call_neg is opaque to cut and stays arity 1.
    private static readonly int CallConjFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$call_conj", permanent: true).Id, 3);
    private static readonly int CallDisjFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$call_disj", permanent: true).Id, 3);
    private static readonly int CallArrowFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$call_arrow", permanent: true).Id, 3);
    private static readonly int CallNegFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$call_neg", permanent: true).Id, 1);

    /// <summary>Optional hook the interpreter consults on every
    /// <c>call</c> / <c>execute</c> to ask whether a Tier-1 IL
    /// replacement exists for the target predicate. <c>null</c> disables
    /// the Tier-1 path; set via <see cref="Tier1Dispatcher"/> once an
    /// embedder has wired in a promotion store.</summary>
    public ITier1Dispatcher? Tier1Dispatcher { get; set; }

    /// <summary>Chunk 225 Stage B.1 — direct IL-delegate table indexed
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
        // counterpart of the chunk-335 flush-before-cut. Wakeups run through
        // the interpreter's goal machinery; `code` is fetched live so the
        // wakeup goals see the current linked program. (Phase 28)
        _engine.Tier1WakeupFlusher = () => FlushPendingWakeups(_engine.GetProgramView());
    }

    public Activation Activation => _engine;
    public IReadOnlyList<string> StringLiterals => _stringLiterals;
    public IReadOnlyList<double> FloatLiterals => _floatLiterals;
    public IReadOnlyList<System.Numerics.BigInteger> BigIntLiterals => _bigIntLiterals;

    /// <summary>ADR-015 chunk C: swaps in the grown literal pools after a
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
        try { return Dispatch(code); }
        catch (TopLevelFailure) { return InterpreterResult.Failed; }
    }

    // Phase 16 chunk 183: chunk-50 RunSubroutine + chunk-174 floor-pin
    // additions are gone. The IL non-tail Call site is now threaded
    // (set Cp = resume marker, set Pc = callee, IlTailCallPending = true,
    // return) so no recursive sub-Dispatch invocation is needed. The
    // SubroutineSentinelCp constant survives because RunGoalInEngine
    // (chunk 80's in-engine sub-goal driver for findall/3 etc.) still
    // uses the same Pc-negative trick to exit its dispatch loop.

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
        // Chunk 169: cache the ProgramView across dispatch iterations.
        // Refresh only when the engine's program generation has
        // changed (AppendCode reallocation, per-query rewire of
        // overlay/split).
        //
        // Chunk 170: peel off a direct byte[] reference when the view
        // is single-buffer (the steady state — Overflow only appears
        // mid-query during chunk-151b's persistent + per-query
        // split, and even then the per-query overlay is small). The
        // per-iteration `code[pc]` indexer otherwise compiles to a
        // branch on Split per dispatch tick.
        int cachedGen = -1;
        bool engineDriven = _engine.CurrentProgram is not null;
        byte[] codeArr = code.IsSingleBuffer ? code.Primary : System.Array.Empty<byte>();
        int codeLen = code.Length;
        bool inClause = false;   // I1: set by a straight-line op so the next
                                 // iteration skips the gen/marker/bounds checks.
        while (true)
        {
            int pc = _engine.P;
            // I1 (Phase 33): a straight-line opcode advances pc by a fixed amount
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
            // RunSubroutine (chunk 63): when an IL subroutine call
            // returns success but the caller's Cp is the
            // SubroutineSentinelCp, the IL dispatch path sets
            // Pc=Cp=sentinel; the next dispatch iteration sees it and
            // halts cleanly here instead of indexing into code[].
            if (pc < 0) return InterpreterResult.Halted;

            // Phase 16 — threaded Tier-1: a resume-marker PC means an
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
                        // Chunk 417: honour the `unknown` flag (throws on error).
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

            // Chunk 170: when the view is split (Overflow != null) we
            // fall back to the indexer for both the opcode byte and
            // every operand read inside the case bodies (those still
            // go through BytecodeIO's ProgramView overloads, which
            // handle the split internally). The fast path skips the
            // per-tick Split branch entirely.
            byte opByte = code.Overflow is null ? codeArr[pc] : code[pc];
            Shumway.Core.Profiler.Opcode(opByte);
#if SHUMWAY_PROFILE
            // Dispatch trace (profile builds only, ITE_TRACE=1): one line per
            // dispatched opcode with pc / B / Cp. Added for the ADR-025
            // bring-up; generally useful for control-flow forensics.
            if (_dispatchTrace)
                System.Console.Error.WriteLine(
                    $"[t] pc={pc,7} {(Opcode)opByte,-18} b={_engine.B} cp={_engine.Cp}");
#endif
            switch ((Opcode)opByte)
            {
                case Opcode.ReservedInvalid:
                    throw new InvalidOperationException(
                        $"Encountered reserved_invalid opcode at PC=0x{pc:X4} — bytecode corruption.");

                case Opcode.Halt:
                    return InterpreterResult.Halted;

                case Opcode.Nop:
                    // ADR-015 chunk C step 4: padding bytes for asserta's
                    // in-place demotion of try_me_else (9 bytes) to
                    // retry_me_else (5 bytes); the trailing 4 arity-
                    // operand bytes become nops.
                    _engine.SetPc(pc + 1); inClause = true;   // chunk 429
                    break;

                case Opcode.Proceed:
                {
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.Debug?.OnExit(_engine);            // ADR-035 exit port
                    int returnPc = _engine.Cp;
                    if (returnPc < 0)
                        return InterpreterResult.Halted;       // returned past the top
                    _engine.SetPc(returnPc);
                    break;
                }

                case Opcode.Call:
                {
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int target = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int numLivePerms = ReadI32(code, codeArr, pc + 5);
                    target = ResolveTargetMaybeAutoPromoted(target);
                    if (target == UnknownFailTarget)   // chunk 417: unknown=fail
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    Shumway.Core.Profiler.Call(target);
                    // Env trimming (chunk 57): shrink the current frame to
                    // num_live_perms Y slots before dispatching, so the callee's
                    // pushes (CP, allocate) sit just above the live region of
                    // the parent frame.
                    _engine.TrimEnv(numLivePerms);
                    _engine.SetCp(pc + 9);  // Call is 9 bytes (opcode + addr + count)
                    _engine.SetB0(_engine.B);   // capture _b at procedure entry for neck_cut
                    DispatchToTier1OrBytecode(target, tailCall: false);
                    break;
                }

                // ADR-035 — a place a debugger may stop. Present in debug-compiled
                // code before every body goal and at every clause entry; with no
                // session attached it is a null test and a 5-byte step.
                case Opcode.Break:
                {
                    if (_engine.Debug is { } dbg)
                        dbg.OnBreak(_engine, ReadI32(code, codeArr, pc + 1));
                    _engine.SetPc(pc + 5);
                    inClause = true;
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
                    if (target == UnknownFailTarget)   // chunk 417: unknown=fail
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
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int target = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    target = ResolveTargetMaybeAutoPromoted(target);
                    if (target == UnknownFailTarget)   // chunk 417: unknown=fail
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    Shumway.Core.Profiler.Call(target);
                    _engine.SetB0(_engine.B);   // tail call still enters a new procedure
                    DispatchToTier1OrBytecode(target, tailCall: true);
                    break;
                }

                // Chunk 225 Stage B.1 — Call to a bundle-IL-promoted
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
                    int functorId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int numLivePerms = ReadI32(code, codeArr, pc + 5);
                    Shumway.Core.Profiler.Call(functorId);
                    _engine.Debug?.OnCallFunctor(_engine, functorId, false);   // ADR-035
                    _engine.TrimEnv(numLivePerms);
                    _engine.SetCp(pc + 9);  // CallIl is 9 bytes, same as Call
                    _engine.SetB0(_engine.B);
                    // ADR-016 safe point — heap GC needs every goal
                    // boundary regardless of dispatch tier.
                    _engine.MaybeCollectHeap();
                    var table = IlByFunctorId;
                    var ilFn = table is not null && (uint)functorId < (uint)table.Length
                        ? table[functorId] : null;
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

                // Chunk 226 Stage B.2 — Call to a predicate the linker
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
                    int target = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int numLivePerms = ReadI32(code, codeArr, pc + 5);
                    target = ResolveTargetMaybeAutoPromoted(target);
                    if (target == UnknownFailTarget)   // chunk 417: unknown=fail
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    Shumway.Core.Profiler.Call(target);
                    _engine.Debug?.OnCallAddress(_engine, target, false);   // ADR-035
                    _engine.TrimEnv(numLivePerms);
                    _engine.SetCp(pc + 9);  // CallBytecode is 9 bytes, same as Call
                    _engine.SetB0(_engine.B);
                    // ADR-016 safe point.
                    _engine.MaybeCollectHeap();
                    _engine.SetPc(target);
                    break;
                }

                // Chunk 227 Stage B.3 — tail-call to a bundle-IL
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
                    int functorId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    Shumway.Core.Profiler.Call(functorId);
                    _engine.Debug?.OnCallFunctor(_engine, functorId, true);   // ADR-035
                    _engine.SetB0(_engine.B);  // tail call still enters a new procedure
                    _engine.MaybeCollectHeap();
                    var table = IlByFunctorId;
                    var ilFn = table is not null && (uint)functorId < (uint)table.Length
                        ? table[functorId] : null;
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

                // Chunk 227 Stage B.3 — tail-call to a bytecode-only
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
                    int target = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    target = ResolveTargetMaybeAutoPromoted(target);
                    if (target == UnknownFailTarget)   // chunk 417: unknown=fail
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    Shumway.Core.Profiler.Call(target);
                    _engine.Debug?.OnCallAddress(_engine, target, true);   // ADR-035
                    _engine.SetB0(_engine.B);
                    _engine.MaybeCollectHeap();
                    _engine.SetPc(target);
                    break;
                }

                // Chunk 248 — tail-call to a builtin. 5-byte opcode
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
                    int builtinId = ReadI32(code, codeArr, pc + 1);   // chunk 429
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
                    int n = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    _engine.Allocate(n);
                    RunGetVariableYRun(code, codeArr, codeLen, pc + 5);   // chunk 415
                    break;
                }

                case Opcode.Deallocate:
                    _engine.Deallocate();
                    _engine.SetPc(pc + 1); inClause = true;   // deallocate is 1 byte (chunk 429)
                    break;

                // ---------- Chunk 220 — fused opcodes (peephole) ----------

                case Opcode.AllocateGetLevel:
                {
                    // 10-byte layout: [op:1] [count:4] [slot:4] [Nop:1]
                    int n = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int slot = ReadI32(code, codeArr, pc + 5);
                    _engine.Allocate(n);
                    _engine.GetLevel(slot);
                    RunGetVariableYRun(code, codeArr, codeLen, pc + 10);   // chunk 415
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
                    if (target == UnknownFailTarget)   // chunk 417: unknown=fail
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
                    _engine.Deallocate();
                    _engine.Debug?.OnExit(_engine);            // ADR-035 exit port
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
                    _engine.Debug?.OnExit(_engine);            // ADR-035 exit port
                    int rpc = _engine.Cp;
                    if (rpc < 0) return InterpreterResult.Halted;
                    _engine.SetPc(rpc);
                    break;
                }

                // ---------- Choice point opcodes ----------

                case Opcode.TryMeElse:
                {
                    int nextClause = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arity = ReadI32(code, codeArr, pc + 5);
                    // ADR-025 — a body try_me_else (inline ITE/disjunction)
                    // carries the InlineIteCpArity sentinel; its CP saves no
                    // argument registers (branch state lives in Y slots).
                    if (arity < 0) arity = 0;
                    _engine.PushChoicePoint(arity, nextClause);
                    // Chunk 221 peephole fusion: in dynamic chains the
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
                    int nextClause = ReadI32(code, codeArr, pc + 1);   // chunk 429
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
                    // Chunk 221 peephole fusion (see TryMeElse).
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
                    // Chunk 221 peephole fusion (see TryMeElse).
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

                // ADR-015 chunk C — generation-filtered dynamic dispatch.
                // Sample the dynamic-database generation into CurrentViewGen
                // so the surrounding try_me_else captures it into the CP and
                // every clause's CheckVisible reads the call's stable view.
                case Opcode.EnterDynamic:
                {
                    // chunk 432: sample through the shared GenerationBox (one
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
                    _engine.SetPc(pc + 1); inClause = true;   // chunk 429
                    break;
                }

                // Per-clause visibility check. Reads born/died from the
                // bytecode (retract patches the died slot in place) and
                // backtracks if the calling goal's captured view-gen is
                // outside [born, died) — the ISO logical update view.
                case Opcode.CheckVisible:
                {
                    long born = ReadI64(code, codeArr, pc + 1);   // chunk 429
                    long died = ReadI64(code, codeArr, pc + 9);
                    long g = _engine.CurrentViewGen;
                    if (born > g || died <= g)
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 17); inClause = true;   // chunk 429
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
                        // PSTR, BigInt, Foreign, String — fall back to the
                        // var-arg chain. These rarely appear as a clause-head
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

                // ---------- Multi-arg indexing (chunk 67) ----------

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
                    // if-then-else condition. (Phase 28)
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.NeckCut();
                    _engine.SetPc(pc + 1); inClause = true;   // chunk 429
                    break;

                case Opcode.GetLevel:
                {
                    int slot = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    _engine.GetLevel(slot);
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
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
                    int slot = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int barrier = (int)_engine.GetY(slot).Data;
                    _engine.Cut(barrier);
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
                    break;
                }

                // ---------- Compound (STR) and list (LIS) — open instructions ----------

                case Opcode.GetStructure:
                {
                    int functorId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    if (!_engine.GetStructure(functorId, arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 9))   // chunk 415
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.PutStructure:
                {
                    int functorId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.PutStructure(functorId, arg);
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutStructureR:   // ADR-020 reserve-upfront root
                {
                    int functorId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int packed = ReadI32(code, codeArr, pc + 5);
                    _engine.PutStructureReserved(functorId, packed & 0xFFFFFF, packed >> 24);
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutListR:   // ADR-020 reserve-upfront cons root
                {
                    int arg = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    _engine.PutListReserved(arg);
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.GetList:
                {
                    int arg = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (!_engine.GetList(arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    // Chunk 415 — consume the following unify-family run inline.
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.PutList:
                {
                    int arg = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    _engine.PutList(arg);
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.GetListA1:
                    if (!_engine.GetList(0))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 1))   // chunk 415
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
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 1))   // chunk 415
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;

                // ---------- Unify-mode opcodes (consume cells via _unifyPointer) ----------

                case Opcode.UnifyVariableX:
                {
                    int target = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVariableX(target);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 415
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
                        // A bare ATTVAR (chunk 77) at the unify pointer
                        // is a variable at its home — capture it as a
                        // REF to that home, never a copied ATTVAR cell.
                        Cell src = _engine.GetHeap(ptr);
                        _engine.SetRegister(target,
                            src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src);
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 415
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyVariableY:
                {
                    int target = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVariableY(target);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 429
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
                        // as a REF to its home. (chunk 77)
                        Cell src = _engine.GetHeap(ptr);
                        _engine.SetY(target,
                            src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src);
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 429
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyValueX:
                {
                    int src = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyValueX(src);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 415
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
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 415
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyValueY:
                {
                    int src = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyValueY(src);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 429
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
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 429
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyConstant:
                case Opcode.UnifyAtom:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    Cell value = Cell.Atom(atomId);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyArgCell(value);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 415
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
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 415
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyInteger:
                {
                    int intValue = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    Cell value = Cell.Int(intValue);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyArgCell(value);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 415
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
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 415
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
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 1))   // chunk 415
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
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 1))   // chunk 415
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyStructure:
                {
                    int functorId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (!_engine.UnifyStructure(functorId))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 429
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
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 1))   // chunk 415
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                case Opcode.UnifyVoid:
                {
                    int count = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVoid(count);
                        if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 415
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
                    if (!RunUnifySequence(code, codeArr, codeLen, pc + 5))   // chunk 415
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                    }
                    break;
                }

                // ---------- Get instructions ----------

                case Opcode.GetVariableX:
                {
                    int dest = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetRegister(dest, _engine.GetRegister(arg));
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.GetVariableY:
                {
                    int dest = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetY(dest, _engine.GetRegister(arg));
                    RunGetVariableYRun(code, codeArr, codeLen, pc + 9);   // chunk 415
                    break;
                }

                case Opcode.GetValueX:
                {
                    int src = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    if (!_engine.UnifyRegisters(src, arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.GetValueY:
                {
                    int src = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    if (!_engine.UnifyPermanentWithRegister(src, arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.GetConstant:
                case Opcode.GetAtom:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Atom(atomId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.GetInteger:
                {
                    int value = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Int(value)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.GetNil:
                {
                    int arg = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Atom(AtomTable.EmptyListId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
                    break;
                }

                // ---------- Put instructions ----------

                case Opcode.PutVariableX:
                {
                    int dest = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    int heapIdx = _engine.AllocateHeapUnbound();
                    Cell refCell = Cell.Ref(heapIdx);
                    _engine.SetRegister(dest, refCell);
                    _engine.SetRegister(arg, refCell);
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutVariableY:
                {
                    int dest = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    int heapIdx = _engine.AllocateHeapUnbound();
                    Cell refCell = Cell.Ref(heapIdx);
                    _engine.SetY(dest, refCell);
                    _engine.SetRegister(arg, refCell);
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutValueX:
                {
                    int src = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetRegister(arg, _engine.GetRegister(src));
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutValueY:
                {
                    int src = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetRegister(arg, _engine.GetY(src));
                    RunPutValueYRun(code, codeArr, codeLen, pc + 9);   // chunk 415
                    break;
                }

                case Opcode.PutConstant:
                case Opcode.PutAtom:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetRegister(arg, Cell.Atom(atomId));
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutInteger:
                {
                    int value = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int arg = ReadI32(code, codeArr, pc + 5);
                    _engine.SetRegister(arg, Cell.Int(value));
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutNil:
                {
                    int arg = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    _engine.SetRegister(arg, Cell.Atom(AtomTable.EmptyListId));
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
                    break;
                }

                // ---------- Consolidated A1/A2 specialisations ----------

                case Opcode.GetConstantA1:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (!_engine.UnifyRegisterWithCell(0, Cell.Atom(atomId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.GetConstantA2:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (!_engine.UnifyRegisterWithCell(1, Cell.Atom(atomId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutConstantA1:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    _engine.SetRegister(0, Cell.Atom(atomId));
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutConstantA2:
                {
                    int atomId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    _engine.SetRegister(1, Cell.Atom(atomId));
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
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
                    int builtinId = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    int numLivePerms = ReadI32(code, codeArr, pc + 5);
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                    // Env trimming (chunk 57): shrink the current frame BEFORE
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
                    // in the live engine, with full backtracking (chunk 86),
                    // rather than running the sub-engine builtin.
                    if (entry.IsCall)
                    {
                        // Phase 33 ISO audit — a backtrackable (cursor)
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
                        _engine.BuiltinReturnPc = pc + 9;   // Phase 33 — see IsCall above
                        // Cut-barrier-carrying meta-call from a $call_*
                        // control helper (chunk 88): X1 carries the barrier
                        // the enclosing call established for a `!` in X0.
                        int barrier = (int)DerefCell(_engine.GetRegister(1)).AsInt;
                        if (!DispatchCall(code, 1, barrier))
                            return InterpreterResult.Failed;
                        break;
                    }
                    // Chunk 130: thread the offending builtin's identity
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
                    // Chunk 218: the post-call_builtin address — backtrackable
                    // builtins (between, append, atom_concat, repeat, retract,
                    // …) capture this on first invocation and pass it to
                    // ResumeAtReturnPc on each retry success. Was previously
                    // read as `engine.P + 9` inside each builtin, which broke
                    // Tier-1 IL where Pc doesn't point at the opcode.
                    _engine.BuiltinReturnPc = pc + 9;
                    bool implOk;
                    Shumway.Core.Profiler.BuiltinEnter(builtinId);
                    // ADR-035 call port. Deliberately below the IsCall /
                    // IsDollarCall arms above: a meta-call wrapper is not a
                    // goal the user wrote — the goal it dispatches reports
                    // itself through DispatchToTier1OrBytecode.
                    _engine.Debug?.OnCallBuiltin(_engine, builtinId, false);
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
                    // Chunk 429: deliberately AdvancePc, NOT SetPc(pc + 9) —
                    // entry.Impl ran arbitrary builtin code between the pc
                    // capture and here, so the mechanical substitution's
                    // "P still equals pc" precondition can't be verified
                    // per-site for every builtin.
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
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutPstr:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    int headerIdx = _engine.MakePstr(ResolveLiteral(literalId));
                    _engine.SetRegister(arg, Cell.Ref(headerIdx));
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
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
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
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
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutFloat:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    int headerIdx = _engine.MakeFloat(ResolveFloatLiteral(literalId));
                    _engine.SetRegister(arg, Cell.Ref(headerIdx));
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
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
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
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
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.PutBigInt:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.SetRegister(arg, _engine.MakeBigInt(ResolveBigIntLiteral(literalId)));
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.UnifyBigInt:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    Cell value = _engine.MakeBigInt(ResolveBigIntLiteral(literalId));
                    // ADR-020 (Phase 33 fix): inside a reserve-upfront inline
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
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
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
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.AEvalBin:
                    Shumway.Builtins.ArithEvalStack.Bin(BytecodeIO.ReadInt32(code, pc + 1));
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
                    break;

                case Opcode.AEvalUn:
                    Shumway.Builtins.ArithEvalStack.Un(BytecodeIO.ReadInt32(code, pc + 1));
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
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
                    _engine.SetPc(pc + 9); inClause = true;   // chunk 429
                    break;
                }

                case Opcode.AEvalCmp:
                    if (!Shumway.Builtins.ArithEvalStack.Cmp(BytecodeIO.ReadInt32(code, pc + 1)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.SetPc(pc + 5); inClause = true;   // chunk 429
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
                    _engine.SetPc(pc + 17); inClause = true;   // chunk 429
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
                    _engine.SetPc(pc + 13); inClause = true;   // chunk 429
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
                    _engine.SetPc(pc + metaSize); inClause = true;   // chunk 429
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
    /// without bouncing through bytecode (chunk 47).</summary>
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
            _engine.SetPc(target);
            return;
        }

        _engine.Debug?.OnCallAddress(_engine, target, tailCall);     // ADR-035 call port

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

    /// <summary>Chunk 429 — peeled operand read. The hot handlers read their
    /// operands through the same single-buffer <c>codeArr</c> the dispatch loop
    /// already peeled for the opcode byte, instead of
    /// <see cref="BytecodeIO.ReadInt32(in ProgramView, int)"/>, which re-tests
    /// <c>Overflow is null</c> + the split boundary per read through an
    /// <c>in</c>-struct indirection. The JIT inlines this and can CSE the
    /// <c>Overflow is null</c> branch with the handler's surrounding peeled
    /// reads. The split-view case (Overflow non-null — only mid-query during
    /// chunk-151b's persistent + per-query split) falls back to the routing
    /// overload unchanged.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int ReadI32(in Shumway.Core.ProgramView code, byte[] codeArr, int offset)
        => code.Overflow is null
            ? BytecodeIO.ReadInt32(codeArr, offset)
            : BytecodeIO.ReadInt32(code, offset);

    /// <summary>Chunk 429 — peeled 8-byte operand read; see
    /// <see cref="ReadI32"/>. Worst pre-peel offender was
    /// <see cref="TryInlineCheckVisible"/>'s two ReadInt64s — 23.5M chain
    /// steps per Blint run.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static long ReadI64(in Shumway.Core.ProgramView code, byte[] codeArr, int offset)
        => code.Overflow is null
            ? BytecodeIO.ReadInt64(codeArr, offset)
            : BytecodeIO.ReadInt64(code, offset);

    /// <summary>Runs any <c>verify_attributes</c> wakeups queued by a
    /// just-completed unification (chunk 80). Checked at every goal
    /// boundary — Call / Execute / CallBuiltin / Proceed. The
    /// <c>'$wakeup_attributes'/1</c> driver runs in the *live* engine
    /// (via <see cref="RunGoalInEngine"/>) so the hooks observe the real
    /// attributed variables. Returns false when a hook — or a goal it
    /// returned — failed, which the caller turns into a backtrack so the
    /// triggering unification fails. A no-op (returns true) when nothing
    /// is queued, the overwhelmingly common case.
    ///
    /// <para>Chunk 429: split into an aggressively-inlined guard over a
    /// NoInlining slow body (the <see cref="Activation.FlushWakeupsForIlCut"/>
    /// precedent), so the 12 goal-boundary call sites pay only the inline
    /// queue-count check when nothing is queued instead of a call into a
    /// method too large to inline.</para></summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool FlushPendingWakeups(ProgramView code)
        => !_engine.HasPendingWakeups || FlushPendingWakeupsSlow(code);

    /// <summary>Cold body of <see cref="FlushPendingWakeups"/> — only reached
    /// when wakeups are actually queued (chunk 429).</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool FlushPendingWakeupsSlow(ProgramView code)
    {
        var addrs = _engine.CurrentFunctorAddresses;
        if (addrs is null || !addrs.ContainsKey(VerifyAttributesFunctorId))
        {
            // No verify_attributes/4 linked into this program — attributed
            // variables stay hookless (the chunk-77 foundation).
            _engine.ClearPendingWakeups();
            return true;
        }

        // The wakeup processing clobbers X registers and may push choice
        // points; snapshot the registers and the CP level so the goal
        // boundary we resume into is left exactly as it was.
        int regCount = _engine.RegisterCount;
        Cell[] savedRegs = new Cell[regCount];
        for (int i = 0; i < regCount; i++) savedRegs[i] = _engine.GetRegister(i);
        int savedB = _engine.B;

        bool ok = RunWakeups(code);

        if (ok && _engine.B > savedB) _engine.Cut(savedB);   // once-semantics
        for (int i = 0; i < regCount; i++) _engine.SetRegister(i, savedRegs[i]);
        return ok;
    }

    /// <summary>Drains the wakeup queue (chunk 80): for each batch, runs
    /// every module's <c>verify_attributes/4</c> hook, then every goal
    /// the hooks returned — all in the live engine. A hook's goal can
    /// unify further attributed variables and queue more wakeups, so the
    /// queue is drained in a loop.</summary>
    private bool RunWakeups(ProgramView code)
    {
        while (_engine.HasPendingWakeups)
        {
            var batch = _engine.TakePendingWakeups();
            // All modules' hooks run first, then every returned goal —
            // the SICStus/Scryer ordering, so each hook sees the
            // pre-goal state.
            var goalLists = new Cell[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                var (moduleId, attrValueIdx, otherIdx) = batch[i];
                int goalsVarIdx = _engine.AllocateHeapUnbound();
                Cell verifyGoal = BuildVerifyGoal(moduleId, attrValueIdx, otherIdx, goalsVarIdx);
                if (!MetaCallInEngine(code, verifyGoal)) return false;
                goalLists[i] = Cell.Ref(goalsVarIdx);
            }
            for (int i = 0; i < batch.Count; i++)
                if (!RunGoalList(code, goalLists[i])) return false;
        }
        return true;
    }

    /// <summary>Builds <c>verify_attributes(Module, AttrValue, Value,
    /// Goals)</c> on the heap and returns the goal cell. <c>Goals</c> is
    /// the fresh variable at <paramref name="goalsVarIdx"/> the hook
    /// binds to its returned goal list.</summary>
    private Cell BuildVerifyGoal(int moduleId, int attrValueIdx, int otherIdx, int goalsVarIdx)
    {
        int f = _engine.AllocateHeap(5);
        _engine.SetHeap(f,     Cell.Functor(VerifyAttributesFunctorId));
        _engine.SetHeap(f + 1, Cell.Atom(moduleId));
        _engine.SetHeap(f + 2, Cell.Ref(attrValueIdx));
        _engine.SetHeap(f + 3, Cell.Ref(otherIdx));
        _engine.SetHeap(f + 4, Cell.Ref(goalsVarIdx));
        return Cell.Str(f);
    }

    /// <summary>Meta-calls every goal in a hook's returned list, in
    /// order. An unbound or empty list runs nothing; a non-list term is
    /// a malformed hook result and fails.</summary>
    private bool RunGoalList(ProgramView code, Cell listCell)
    {
        Cell cursor = DerefCell(listCell);
        while (cursor.Tag == Tag.Lis)
        {
            int headIdx = cursor.AsHeapIndex;
            if (!MetaCallInEngine(code, _engine.GetHeap(headIdx))) return false;
            cursor = DerefCell(_engine.GetHeap(headIdx + 1));
        }
        // [] or an unbound tail → no (more) goals; anything else is malformed.
        return cursor.Tag == Tag.Ref
            || cursor.Tag == Tag.AttVar
            || (cursor.Tag == Tag.Atom && cursor.AsAtomId == AtomTable.EmptyListId);
    }

    /// <summary>Runs one goal term in the live engine (chunk 80). Handles
    /// the <c>,/2</c> conjunction and the <c>true</c> / <c>fail</c>
    /// constants; any other goal is dispatched as a plain call — a
    /// builtin runs directly, a user/prelude predicate runs via
    /// <see cref="RunGoalInEngine"/>. An undefined predicate raises an
    /// existence error.</summary>
    private bool MetaCallInEngine(ProgramView code, Cell goal)
    {
        goal = DerefCell(goal);
        int functorId;
        int argBase;
        int arity;
        switch (goal.Tag)
        {
            case Tag.Atom:
                functorId = FunctorTable.Intern(goal.AsAtomId, 0);
                arity = 0;
                argBase = -1;
                break;
            case Tag.Str:
                int fIdx = goal.AsHeapIndex;
                functorId = _engine.GetHeap(fIdx).AsFunctorId;
                (_, arity) = FunctorTable.Lookup(functorId);
                argBase = fIdx + 1;
                break;
            case Tag.Ref:
            case Tag.AttVar:
                throw new PrologRuntimeException("instantiation_error");
            default:
                throw new PrologRuntimeException("type_error", "callable");
        }

        if (functorId == ConjFunctorId)
            return MetaCallInEngine(code, _engine.GetHeap(argBase))
                && MetaCallInEngine(code, _engine.GetHeap(argBase + 1));
        if (functorId == TrueFunctorId) return true;
        if (functorId == FailFunctorId) return false;

        // Plain goal: load X0..X[arity-1] from the goal's arguments.
        for (int i = 0; i < arity; i++)
            _engine.SetRegister(i, _engine.GetHeap(argBase + i));

        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int builtinId))
        {
            var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
            _engine.CurrentBuiltinName = entry.Name;        // chunk 130
            _engine.CurrentBuiltinArity = entry.Arity;
            try { return entry.Impl(_engine); }
            catch (PrologRuntimeException re)
            { re.StampBuiltin(entry.Name, entry.Arity); throw; }
        }

        var addrs = _engine.CurrentFunctorAddresses;
        if (addrs is not null && addrs.TryGetValue(functorId, out int addr))
            return RunGoalInEngine(code, addr);

        // Chunk 417: honour the `unknown` flag (throws on error).
        return !Shumway.Core.UnknownProcedure.Fails(_engine, functorId);
    }

    /// <summary>Backtrackable runtime dispatch for <c>call/1..7</c> (chunk
    /// 86). The goal in <c>X0</c> — with <c>call/N</c>'s extra arguments
    /// <c>X1..X[callArity-1]</c> appended — is decoded and run as a real
    /// goal in the live engine: a user or prelude predicate is entered with
    /// a tail jump so it keeps its choice points and the call's
    /// continuation flows on success; a builtin runs inline. Control
    /// constructs in a runtime goal reach the prelude <c>$call_conj</c>,
    /// <c>$call_disj</c>, <c>$call_arrow</c>, <c>$call_neg</c> helpers.
    ///
    /// <para><paramref name="barrier"/> is the choice-point level a
    /// <c>!</c> reached as the goal cuts back to (chunk 88). For a
    /// top-level <c>call/N</c> it is B at entry, so a bare <c>call(!)</c>
    /// is a no-op; the conj/disj/arrow helpers thread it on through
    /// <c>'$call'/2</c> so a <c>!</c> inside a runtime compound goal
    /// commits exactly as far as the enclosing call — no further.</para>
    ///
    /// <para>Returns false only on an unrecoverable failure (no choice
    /// point remains).</para></summary>
    private bool DispatchCall(ProgramView code, int callArity, int barrier)
    {
        // Chunk 406 sizing (profile builds only): how many goals are dispatched by
        // runtime term inspection — the cost class the link-time meta-wrapper
        // unfold (ADR-021 candidate #2) removes.
        Shumway.Core.Profiler.Note("meta_dispatch (DispatchCall)");
        int pc = _engine.P;
        Cell goal = DerefCell(_engine.GetRegister(0));

        // Save call/N's extra arguments before the registers are reloaded.
        // The per-engine scratch is safe here: the extras are consumed into
        // registers below, before any recursion or builtin can re-enter.
        int extraCount = callArity - 1;
        Cell[] extra = extraCount <= 0
            ? System.Array.Empty<Cell>()
            : extraCount <= _engine.MetaExtraScratch.Length
                ? _engine.MetaExtraScratch
                : new Cell[extraCount];
        for (int i = 0; i < extraCount; i++)
            extra[i] = _engine.GetRegister(i + 1);

        int atomId;
        int goalArity;
        int argBase;
        switch (goal.Tag)
        {
            case Tag.Atom:
                atomId = goal.AsAtomId;
                goalArity = 0;
                argBase = -1;
                break;
            case Tag.Str:
                int functorIdx = goal.AsHeapIndex;
                (atomId, goalArity) =
                    FunctorTable.Lookup(_engine.GetHeap(functorIdx).AsFunctorId);
                argBase = functorIdx + 1;
                break;
            case Tag.Ref:
            case Tag.AttVar:
                throw new PrologRuntimeException("instantiation_error");
            default:
                throw new PrologRuntimeException("type_error", "callable");
        }

        int totalArity = goalArity + extraCount;
        for (int i = 0; i < goalArity; i++)
            _engine.SetRegister(i, _engine.GetHeap(argBase + i));
        for (int i = 0; i < extraCount; i++)
            _engine.SetRegister(goalArity + i, extra[i]);

        // Chunk 416 — route cache. A repeat goal functor skips the intern,
        // the control-construct compares and the registry/address probes.
        // See MetaRoute.cs for the lifetime/soundness argument.
        var addresses = _engine.CurrentFunctorAddresses;
        var cache = _engine.MetaRouteCache;
        if (cache is null || !ReferenceEquals(_engine.MetaRouteCacheStamp, addresses))
        {
            cache = _engine.MetaRouteCache =
                new System.Collections.Generic.Dictionary<long, Shumway.Core.MetaRoute>();
            _engine.MetaRouteCacheStamp = addresses;
        }
        bool routeCacheable = (uint)totalArity <= 0xFFFF;   // key packs arity in 16 bits
        long routeKey = ((long)atomId << 16) | (uint)totalArity;
        if (routeCacheable && cache.TryGetValue(routeKey, out var route))
        {
            switch (route.Kind)
            {
                case Shumway.Core.MetaRouteKind.Cut:
                    _engine.Cut(barrier);
                    _engine.AdvancePc(9);
                    return true;
                case Shumway.Core.MetaRouteKind.True:
                    _engine.AdvancePc(9);
                    return true;
                case Shumway.Core.MetaRouteKind.Fail:
                    return TryBacktrack();
                case Shumway.Core.MetaRouteKind.CallRecurse:
                    return DispatchCall(code,
                        Shumway.Builtins.BuiltinsRegistry.GetById(route.Arg).Arity,
                        _engine.B);
                case Shumway.Core.MetaRouteKind.DollarCall:
                case Shumway.Core.MetaRouteKind.Builtin:
                    return InvokeBuiltinGoal(route.Arg);
                case Shumway.Core.MetaRouteKind.BarrierHelperJump:
                    _engine.SetRegister(2, Cell.Int(barrier));
                    goto case Shumway.Core.MetaRouteKind.Jump;
                case Shumway.Core.MetaRouteKind.Jump:
                    return JumpToUserGoal(code, pc, route.Arg);
            }
        }

        int functorId = FunctorTable.Intern(atomId, totalArity);

        // A control construct in a runtime goal routes to its prelude
        // helper. conj/disj/arrow are cut-transparent, so they take the
        // barrier as a third argument (X2): a `!` threaded down through
        // them commits to the enclosing call (chunk 88). \+ is opaque to
        // cut, so $call_neg needs no barrier.
        var userKind = Shumway.Core.MetaRouteKind.Jump;
        if (functorId == ConjFunctorId)
        {
            Shumway.Core.Profiler.Note("meta_dispatch: control construct");
            _engine.SetRegister(2, Cell.Int(barrier));
            functorId = CallConjFunctorId;
            userKind = Shumway.Core.MetaRouteKind.BarrierHelperJump;
        }
        else if (functorId == DisjFunctorId)
        {
            Shumway.Core.Profiler.Note("meta_dispatch: control construct");
            _engine.SetRegister(2, Cell.Int(barrier));
            functorId = CallDisjFunctorId;
            userKind = Shumway.Core.MetaRouteKind.BarrierHelperJump;
        }
        else if (functorId == ArrowFunctorId)
        {
            Shumway.Core.Profiler.Note("meta_dispatch: control construct");
            _engine.SetRegister(2, Cell.Int(barrier));
            functorId = CallArrowFunctorId;
            userKind = Shumway.Core.MetaRouteKind.BarrierHelperJump;
        }
        else if (functorId == NegFunctorId || functorId == NotFunctorId)
        {
            Shumway.Core.Profiler.Note("meta_dispatch: control construct");
            functorId = CallNegFunctorId;
        }

        // ! as the whole goal: commit to the barrier the enclosing call
        // established (chunk 88). For a top-level call(!) the barrier is B
        // at call entry, so Cut() removes nothing; for a `!` threaded in
        // from a $call_* helper it cuts the runtime goal's choice points,
        // and no further — the parent's CPs sit at or below the barrier.
        if (functorId == CutFunctorId)
        {
            if (routeCacheable)
                cache[routeKey] = new Shumway.Core.MetaRoute(Shumway.Core.MetaRouteKind.Cut, 0);
            _engine.Cut(barrier);
            _engine.AdvancePc(9);
            return true;
        }
        if (functorId == TrueFunctorId)
        {
            if (routeCacheable)
                cache[routeKey] = new Shumway.Core.MetaRoute(Shumway.Core.MetaRouteKind.True, 0);
            _engine.AdvancePc(9);
            return true;
        }
        if (functorId == FailFunctorId)
        {
            if (routeCacheable)
                cache[routeKey] = new Shumway.Core.MetaRoute(Shumway.Core.MetaRouteKind.Fail, 0);
            return TryBacktrack();
        }

        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int builtinId))
        {
            var builtin = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
            // call(call(...)): recurse rather than invoking the call
            // builtin. The inner call is itself a fresh cut barrier, so
            // capture B again rather than passing the outer `barrier`.
            if (builtin.IsCall)
            {
                if (routeCacheable)
                    cache[routeKey] = new Shumway.Core.MetaRoute(
                        Shumway.Core.MetaRouteKind.CallRecurse, builtinId);
                return DispatchCall(code, builtin.Arity, _engine.B);
            }
            if (routeCacheable)
                cache[routeKey] = new Shumway.Core.MetaRoute(
                    builtin.IsDollarCall
                        ? Shumway.Core.MetaRouteKind.DollarCall
                        : Shumway.Core.MetaRouteKind.Builtin,
                    builtinId);
            return InvokeBuiltinGoal(builtinId);
        }

        if (addresses is not null && addresses.TryGetValue(functorId, out int address))
        {
            if (routeCacheable)
                cache[routeKey] = new Shumway.Core.MetaRoute(userKind, address);
            return JumpToUserGoal(code, pc, address);
        }

        // No negative caching: an unresolved functor can become resolvable
        // later in the same query (chunk-207 auto-promotion).
        // Chunk 417: honour the `unknown` flag (throws on error).
        if (Shumway.Core.UnknownProcedure.Fails(_engine, functorId))
            return TryBacktrack();
        throw PrologRuntimeException.UndefinedProcedure(functorId);   // unreachable
    }

    /// <summary>Invokes a builtin reached as a runtime meta-call goal
    /// (chunk 416 — shared by DispatchCall's slow path and its cached
    /// Builtin/DollarCall routes).</summary>
    private bool InvokeBuiltinGoal(int builtinId)
    {
        var builtin = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        _engine.CurrentBuiltinName = builtin.Name;      // chunk 130
        _engine.CurrentBuiltinArity = builtin.Arity;
        bool ok;
        try { ok = builtin.Impl(_engine); }
        catch (PrologRuntimeException re)
        { re.StampBuiltin(builtin.Name, builtin.Arity); throw; }
        if (!ok)
            return TryBacktrack();
        _engine.AdvancePc(9);
        return true;
    }

    /// <summary>Transfers control to a user predicate reached as a runtime
    /// meta-call goal (chunk 416 — shared by DispatchCall's slow path and
    /// its cached Jump/BarrierHelperJump routes).</summary>
    private bool JumpToUserGoal(ProgramView code, int pc, int address)
    {
        // Last-call optimisation: when this call is the clause's final
        // goal, tail-jump so the goal returns to the clause's caller.
        // Setting Cp to the Proceed sitting right after this
        // CallBuiltin would spin — Proceed does not advance Cp.
        Opcode following = (Opcode)code[pc + 9];
        if (following == Opcode.Deallocate)
            _engine.Deallocate();              // last goal, frame: pop it
        else if (following != Opcode.Proceed)
            _engine.SetCp(pc + 9);             // non-last: resume after the call
        _engine.SetB0(_engine.B);
        // Cp untouched (Deallocate / Proceed follows) => the goal returns
        // straight to our caller: a tail call, for the debug ports (ADR-035).
        bool tail = following == Opcode.Deallocate || following == Opcode.Proceed;
        DispatchToTier1OrBytecode(address, tail);
        return true;
    }

    /// <summary>Dereferences a cell, following REF chains to the term it
    /// names (or to an unbound REF / ATTVAR).</summary>
    private Cell DerefCell(Cell c) =>
        c.Tag == Tag.Ref ? _engine.GetHeap(_engine.Deref(c.AsHeapIndex)) : c;

    /// <summary>Phase 19+ — when a Call/Execute target is an
    /// unresolved-procedure sentinel baked into the bytecode at link
    /// time, check whether the predicate has been auto-promoted
    /// mid-query (the <c>implicit_dynamic</c> flag's runtime path
    /// materialised a trampoline after the call site was already
    /// linked). If the current address map now holds a real address
    /// for the functor, use it. Otherwise raise the standard
    /// <c>existence_error(procedure, Name/Arity)</c>.</summary>
    private int ResolveTargetMaybeAutoPromoted(int target)
    {
        if (!CallTarget.IsUnresolved(target)) return target;
        int fid = CallTarget.FunctorIdOf(target);
        var map = _engine.CurrentFunctorAddresses;
        if (map is not null
            && map.TryGetValue(fid, out int latest)
            && !CallTarget.IsUnresolved(latest))
        {
            // Restrict resolution to predicates whose layout starts
            // with `enter_dynamic` — i.e. a dynamic trampoline emitted
            // by the auto-promotion path. A non-dynamic predicate
            // present in CurrentFunctorAddresses under the same fid
            // (e.g. a module-local predicate that the link layer
            // deliberately did NOT expose to this call site) must
            // still raise the standard existence_error rather than
            // breaking module visibility.
            var prog = _engine.CurrentProgram;
            if (prog is not null
                && latest >= 0
                && latest < prog.Length
                && (Opcode)prog[latest] == Opcode.EnterDynamic)
                return latest;
            // Phase 33 — a mid-query consult (consult/1 from a live query)
            // live-links STATIC predicates into the running query's code
            // space; a call site compiled at THIS query's setup (before the
            // consult) baked the undefined sentinel for them. The consult
            // made these fids globally visible exactly as a top-level
            // consult would, so resolving the sentinel to the live-linked
            // static address is sound — the fid is on the explicit
            // visibility set the live-link populated, not an accidental
            // module-local collision.
            if (_engine.LiveConsultVisibleFids is { } visible
                && visible.Contains(fid))
                return latest;
            // Chunk 402: a --strip-wam predicate has no WAM address; its map entry is
            // a resume MARKER (a standalone delegate's (fid, 0), or a region member's
            // (rootFid, memberEntryCursor) alias). Accept it — the Call/Execute handler
            // SetPc's it and the dispatch loop's marker route invokes the IL. Module
            // visibility is not widened: the sentinel's fid was chosen by the LINK
            // layer (mangled for a local), so resolving that exact fid's own alias
            // grants nothing the link didn't already grant. Cold path — sentinels only.
            if (Activation.IsResumeMarker(latest))
                return latest;
        }
        // Chunk 417: honour the `unknown` flag — error throws here,
        // fail/warning hand the caller the fail sentinel.
        if (Shumway.Core.UnknownProcedure.Fails(_engine, fid))
            return UnknownFailTarget;
        throw PrologRuntimeException.UndefinedProcedure(fid);   // unreachable
    }

    /// <summary>Chunk 417 — sentinel returned by
    /// <see cref="ResolveTargetMaybeAutoPromoted"/> when the target is an
    /// undefined procedure and the <c>unknown</c> flag says fail: the
    /// Call/Execute handlers backtrack instead of dispatching.</summary>
    private const int UnknownFailTarget = int.MinValue;

    /// <summary>Runs the predicate at <paramref name="target"/> as a goal
    /// in the <em>current</em> engine — same heap, trail, stack and
    /// attribute table — then resumes the caller. Unlike
    /// <see cref="RunSubroutine"/> this is safe for a goal that pushes
    /// choice points or fails: a backtrack floor pins inner backtracking
    /// at the entry choice-point level, and on success any choice points
    /// the goal left are cut away (once semantics). Returns true iff the
    /// goal succeeded. The caller saves/restores X registers (chunk 80).</summary>
    private bool RunGoalInEngine(ProgramView code, int target)
    {
        int savedPc    = _engine.P;
        int savedCp    = _engine.Cp;
        int savedB0    = _engine.B0;
        int savedB     = _engine.B;
        int savedFloor = _backtrackFloor;

        // Inner backtracking may not unwind past the entry CP level.
        _backtrackFloor = savedB;
        _engine.SetB0(savedB);               // a cut inside the goal stops here
        _engine.SetCp(SubroutineSentinelCp); // the goal's final proceed → Halted
        _engine.SetPc(target);

        InterpreterResult result;
        try { result = Dispatch(code); }
        catch (TopLevelFailure) { result = InterpreterResult.Failed; }

        _backtrackFloor = savedFloor;
        _engine.SetPc(savedPc);
        _engine.SetCp(savedCp);
        _engine.SetB0(savedB0);

        if (result == InterpreterResult.Halted)
        {
            // Once-semantics: discard any choice points the goal left so
            // the outer computation never backtracks into it.
            if (_engine.B > savedB) _engine.Cut(savedB);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Chunk 221 peephole fusion. Called by the {Try,Retry,Trust}MeElse
    /// handlers after they've done their choice-point work and computed
    /// the cursor of the byte that follows the dispatch opcode.
    ///
    /// <para>If that byte is <see cref="Opcode.CheckVisible"/> — the
    /// shape every dynamic-predicate chain entry has, since the chain
    /// emit always writes dispatch + check_visible — decode it inline
    /// and either advance past it (visible) or signal backtrack
    /// (invisible). Returns false ONLY when the caller must backtrack;
    /// in every other case (no CheckVisible at <paramref name="afterPc"/>,
    /// or visible), it updates PC and returns true.</para>
    ///
    /// <para>This is purely an interpreter speedup — it does NOT change
    /// any bytecode layout, emit-site, or opcode encoding. Skipping
    /// one switch trip + opcode-table lookup + profiler bump per
    /// chain step adds up on dynamic-predicate-heavy workloads
    /// (Blint saw 23.5M direct dispatch→CheckVisible pairs / run).</para>
    /// </summary>
    /// <param name="deadSkipTo">Chunk 403 — the dispatch opcode's own <c>next</c>
    /// operand (the following chain entry), or -1 (trust_me, no next). When the
    /// visibility check fails and this is >= 0, jump STRAIGHT to the next entry
    /// instead of failing into a full backtrack: the check is the FIRST thing after
    /// the dispatch opcode, so nothing has mutated since the choice point's state
    /// was pushed/restored — the backtrack would restore registers/trail to values
    /// they already hold. The CP's next-clause slot was already advanced by the
    /// dispatch opcode, so the direct jump leaves identical machine state, minus
    /// the redundant restore. On Blint this removes one full backtrack per DEAD
    /// chain entry (a retract-heavy dynamic predicate accumulates thousands —
    /// 1.56M of the 3.38M backtracks in a self-lint were exactly this).</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool TryInlineCheckVisible(Shumway.Core.ProgramView code, byte[] codeArr, int codeLen, int afterPc,
        int deadSkipTo = -1)
    {
        if (afterPc + 17 > codeLen
            || (code.Overflow is null ? codeArr[afterPc] : code[afterPc])
               != (byte)Opcode.CheckVisible)
        {
            _engine.SetPc(afterPc);
            return true;
        }
        long born = ReadI64(code, codeArr, afterPc + 1);   // chunk 429 — peeled
        long died = ReadI64(code, codeArr, afterPc + 9);
        long g = _engine.CurrentViewGen;
        if (born > g || died <= g)
        {
            if (deadSkipTo < 0) return false;        // trust_me: genuine fail
            // (Chunk 404's in-place tombstone unlink that used to run here was
            // REVERTED in chunk 410: it corrupted Blint's dynamic unget-buffer
            // tokenization — found via the mini.pl repro + a worktree bisect that
            // pinned the regression to 404 exactly; its measured wall-clock win
            // was neutral, so the complexity wasn't paying for the risk. The
            // chunk-403 direct dead-entry jump below is kept — bisected clean.)
            _engine.SetPc(deadSkipTo);
            return true;
        }
        // Phase 33 — the last clause of a dynamic chain terminates at the
        // fail-stub, so its chain instruction is `retry_me_else <fail-stub>`
        // (never `trust_me`). A bare push/retry therefore leaves a choice
        // point whose only alternative is `call_builtin fail/0` — harmless on
        // backtracking, but it makes EVERY deterministic dynamic call report
        // as non-deterministic (a single dynamic fact `c(x)` called `c(x)`
        // left a CP). Once this clause is confirmed visible AND it is the last
        // one (its chain-next is the fail-stub), discard that dead choice
        // point with trust semantics — the choice point governing this clause
        // is the one try_me_else/retry_me_else just pushed/updated (nothing
        // runs between the chain instruction and this check), and its saved
        // machine state equals the current state (check_visible precedes head
        // unification), so TrustMe's restore is a no-op and only the pop
        // takes effect. Brings dynamic dispatch to parity with static
        // trust_me and is what lgtunit's deterministic/1 measures.
        if (deadSkipTo == _engine.DynamicFailStubAddr
            && _engine.DynamicFailStubAddr > 0
            && _engine.B >= 0)
        {
            _engine.TrustMe();
        }
        _engine.SetPc(afterPc + 17);
        return true;
    }

    /// <summary>Chunk 415 — unify-run fusion. After a unify-family opcode
    /// succeeds, the head/argument-matching code is almost always a RUN of more
    /// unify-family opcodes (Blint pairs: unify_list→unify_atom 945K,
    /// unify_atom→unify_list 782K, get_list→unify_value_x 666K, …): consume the
    /// whole run here in a tight loop with a small switch instead of going back
    /// around the main dispatch loop (marker check + bounds check + split-view
    /// branch + the big switch) once per opcode. Bodies are EXACT MIRRORS of the
    /// main-loop cases — keep them in sync when touching either (the chunk-221
    /// precedent). Chunk 429 closes the fusion gaps: <c>unify_variable_y</c>,
    /// <c>unify_value_y</c> and <c>unify_structure</c> are in the run switch too
    /// (mirrored from the main loop, including the ReservedWrite / ADR-020
    /// branches and UnifyVariableY's AttVar capture), and their main-loop cases
    /// chain into this run like the X-forms always did. Profiler counts stay
    /// truthful: each opcode consumed here is recorded. On failure returns false
    /// WITHOUT touching Pc — the caller backtracks, which restores Pc from the
    /// choice point, exactly as the individual cases behave. On success Pc is
    /// written ONCE, at run exit.</summary>
    private bool RunUnifySequence(Shumway.Core.ProgramView code, byte[] codeArr, int codeLen, int pc)
    {
        while (pc < codeLen)
        {
            byte op = code.Overflow is null ? codeArr[pc] : code[pc];
            switch ((Opcode)op)
            {
                case Opcode.UnifyVariableX:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int target = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVariableX(target);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeapUnbound();
                        _engine.SetRegister(target, Cell.Ref(idx));
                    }
                    else
                    {
                        Cell src = _engine.GetHeap(ptr);
                        _engine.SetRegister(target,
                            src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src);
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyVariableY:   // chunk 429 — mirror of the main-loop case
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int target = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVariableY(target);
                        pc += 5;
                        continue;
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
                        // as a REF to its home. (chunk 77)
                        Cell src = _engine.GetHeap(ptr);
                        _engine.SetY(target,
                            src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src);
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyValueX:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int src = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyValueX(src);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, _engine.GetRegister(src));
                    }
                    else if (!_engine.UnifyRegisterWithHeapAt(src, ptr))
                    {
                        return false;
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyValueY:   // chunk 429 — mirror of the main-loop case
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int src = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyValueY(src);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, _engine.GetY(src));
                    }
                    else if (!_engine.UnifyPermanentWithHeapAt(src, ptr))
                    {
                        return false;
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyConstant:
                case Opcode.UnifyAtom:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    Cell value = Cell.Atom(ReadI32(code, codeArr, pc + 1));   // chunk 429
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyArgCell(value);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, value);
                    }
                    else if (!_engine.UnifyHeapWithCell(ptr, value))
                    {
                        return false;
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyInteger:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    Cell value = Cell.Int(ReadI32(code, codeArr, pc + 1));   // chunk 429
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyArgCell(value);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, value);
                    }
                    else if (!_engine.UnifyHeapWithCell(ptr, value))
                    {
                        return false;
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyNil:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    Cell value = Cell.Atom(AtomTable.EmptyListId);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyArgCell(value);
                        pc += 1;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, value);
                    }
                    else if (!_engine.UnifyHeapWithCell(ptr, value))
                    {
                        return false;
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 1;
                    continue;
                }
                case Opcode.UnifyList:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    if (!_engine.UnifyList()) return false;
                    pc += 1;
                    continue;
                }
                case Opcode.UnifyStructure:   // chunk 429 — mirror of the main-loop case
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int functorId = ReadI32(code, codeArr, pc + 1);
                    if (!_engine.UnifyStructure(functorId)) return false;
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyVoid:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int count = ReadI32(code, codeArr, pc + 1);   // chunk 429
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVoid(count);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        for (int i = 0; i < count; i++)
                            _engine.AllocateHeapUnbound();
                    }
                    _engine.SetUnifyPointer(ptr + count);
                    pc += 5;
                    continue;
                }
                default:
                    _engine.SetPc(pc);
                    return true;
            }
        }
        _engine.SetPc(pc);
        return true;
    }

    /// <summary>Chunk 415 — clause-prologue / call-setup move runs. Consecutive
    /// <c>get_variable_y</c> (save args to permanents at clause entry: Blint
    /// pairs 862K+578K+294K) and consecutive <c>put_value_y</c> (load call args
    /// from permanents: 677K) never fail — fuse each run into one dispatch.
    /// Pc is written once at exit.</summary>
    private void RunGetVariableYRun(Shumway.Core.ProgramView code, byte[] codeArr, int codeLen, int pc)
    {
        while (pc + 9 <= codeLen
               && (code.Overflow is null ? codeArr[pc] : code[pc]) == (byte)Opcode.GetVariableY)
        {
            Shumway.Core.Profiler.Opcode((byte)Opcode.GetVariableY);
            int slot = ReadI32(code, codeArr, pc + 1);   // chunk 429
            int arg = ReadI32(code, codeArr, pc + 5);
            _engine.SetY(slot, _engine.GetRegister(arg));
            pc += 9;
        }
        _engine.SetPc(pc);
    }

    private void RunPutValueYRun(Shumway.Core.ProgramView code, byte[] codeArr, int codeLen, int pc)
    {
        while (pc + 9 <= codeLen
               && (code.Overflow is null ? codeArr[pc] : code[pc]) == (byte)Opcode.PutValueY)
        {
            Shumway.Core.Profiler.Opcode((byte)Opcode.PutValueY);
            int slot = ReadI32(code, codeArr, pc + 1);   // chunk 429
            int arg = ReadI32(code, codeArr, pc + 5);
            _engine.SetRegister(arg, _engine.GetY(slot));
            pc += 9;
        }
        _engine.SetPc(pc);
    }

    private bool TryBacktrack()
    {
        Shumway.Core.Profiler.Backtrack();
        // Wakeups belong to the computation being abandoned — drop any
        // that a failed clause queued but never ran (chunk 78).
        _engine.ClearPendingWakeups();
        // Loop so that an IL retry that itself fails immediately falls
        // through to the next choice point without burning stack. The
        // floor (chunk 80) keeps an in-engine sub-goal's backtracking
        // from unwinding choice points the outer computation owns.
        while (_engine.B > _backtrackFloor)
        {
            if (_engine.TopChoicePointIsIl)
            {
                // Cancellation safe point for backtrackable-BUILTIN loops
                // (between/fail, repeat/fail) — they re-satisfy via a builtin
                // choice point (PushBuiltinChoicePoint → an IL CP) without ever
                // crossing a call-boundary MaybeCollectHeap, so this is the only
                // place the REPL's ESC can reach them. Clause-backtracking loops
                // re-satisfy through Call and are already cancellable there, so
                // they pay nothing here. Counter-throttled → negligible per-pop
                // cost even for Tier-1 IL clause backtracking.
                _engine.BacktrackSafePoint();
                // ADR-035 redo port for an IL choice point — under a debug
                // session that means a backtrackable builtin re-satisfying
                // (between/3, repeat/0, clause/2, …), since debuggable code
                // runs Tier-0. There is no bytecode retry address to report;
                // what the session needs is the reconciliation point, and B
                // still names the CP being resumed here.
                _engine.Debug?.OnRedo(_engine, -1);
                var (del, cursor) = _engine.PopIlChoicePointAndRestore();
                if (del(_engine, cursor))
                {
                    // Success: if the IL signalled a tail-call (chunk 47),
                    // leave Pc alone so the next dispatch picks up at the
                    // tail-call target. Otherwise resume at the caller's
                    // continuation, just like bytecode proceed would.
                    if (_engine.IlTailCallPending)
                        _engine.IlTailCallPending = false;
                    else
                        _engine.SetPc(_engine.Cp);
                    return true;
                }
                // The IL clause that cursor selected didn't unify — try
                // the next CP (which may be another IL CP that the just-
                // failed IL pushed before its match attempt).
                continue;
            }
            int arity = (int)_engine.GetStack(_engine.B + Activation.CpArityOffset).Data;
            int bp = (int)_engine.GetStack(_engine.B + Activation.CpBpOffset(arity)).Data;
            // ADR-035 redo port. Raised BEFORE the jump, while B still names
            // the choice point being resumed — the session identifies which
            // goals died (those called after this CP was pushed) from it.
            _engine.Debug?.OnRedo(_engine, bp);
            _engine.SetPc(bp);
            return true;
        }
        _engine.Debug?.OnFail(_engine);   // ADR-035 fail port: no CP left
        return false;
    }

    /// <summary>Returns the deref'd cell at <c>X[0]</c> (the first argument
    /// register), following REF chains so the caller sees the concrete tag.
    /// Used by every arg-0 <c>switch_on_*</c> opcode to decide where to
    /// dispatch.</summary>
    private Cell DerefA1() => DerefArg(0);

    /// <summary>Generalised <see cref="DerefA1"/>: returns the deref'd cell at
    /// <c>X[argIdx]</c>. The multi-arg indexing opcodes (chunk 67) read
    /// arbitrary <c>A[k]</c> rather than just A1.</summary>
    private Cell DerefArg(int argIdx)
    {
        Cell c = _engine.GetRegister(argIdx);
        if (c.Tag == Tag.Ref)
            return _engine.GetHeap(_engine.Deref(c.AsHeapIndex));
        return c;
    }

    /// <summary>ADR-027 — walks a bounded sub-argument path from a deref'd
    /// argument <paramref name="cell"/>: hop <paramref name="sub0"/>, then (if
    /// <paramref name="sub1"/> &gt;= 0) hop sub1. Returns the deref'd cell
    /// reached, or false if any hop lands on a non-compound / out-of-range
    /// position — the caller then takes the switch default.</summary>
    private bool TrySubCell(Cell cell, int sub0, int sub1, out Cell result)
    {
        if (!TryHop(cell, sub0, out result)) return false;
        if (sub1 >= 0 && !TryHop(result, sub1, out result)) return false;
        return true;
    }

    /// <summary>One hop of a sub-argument path: indexes into a list cell
    /// (0 = head, 1 = tail; ADR-017 inline cons) or a struct (idx = argument
    /// position, bounds-checked against the functor arity). The result is
    /// deref'd. Returns false for any other tag or an out-of-range index.</summary>
    private bool TryHop(Cell cell, int idx, out Cell next)
    {
        next = default;
        if (cell.Tag == Tag.Lis)
        {
            if ((uint)idx > 1u) return false;
            next = DerefCell(_engine.GetHeap(cell.AsHeapIndex + idx));
            return true;
        }
        if (cell.Tag == Tag.Str)
        {
            int structIdx = cell.AsHeapIndex;
            int arity = FunctorTable.Lookup(_engine.GetHeap(structIdx).AsFunctorId).Arity;
            if ((uint)idx >= (uint)arity) return false;
            next = DerefCell(_engine.GetHeap(structIdx + 1 + idx));
            return true;
        }
        return false;
    }

    private string ResolveLiteral(int literalId)
    {
        if (literalId < 0 || literalId >= _stringLiterals.Count)
            throw new InvalidOperationException(
                $"String literal id {literalId} is out of range [0, {_stringLiterals.Count}). " +
                "Pass the literal pool to the BytecodeInterpreter constructor.");
        return _stringLiterals[literalId];
    }

    private double ResolveFloatLiteral(int literalId)
    {
        if (literalId < 0 || literalId >= _floatLiterals.Count)
            throw new InvalidOperationException(
                $"Float literal id {literalId} is out of range [0, {_floatLiterals.Count}). " +
                "Pass the float literal pool to the BytecodeInterpreter constructor.");
        return _floatLiterals[literalId];
    }

    // ADR-018 — resolves an a_eval_push leaf to a Number. kind ∈ {0 int (operand
    // is the value), 1 bigint-lit, 2 float-lit, 3 X-reg, 4 Y-slot}. For a
    // register / Y-slot the cell is deref'd and arithmetically evaluated, so a
    // variable bound to an unevaluated expression term is handled exactly as
    // is/2 would (recursively), and an unbound one raises instantiation_error.
    private System.Numerics.BigInteger ResolveBigIntLiteral(int literalId)
    {
        if (literalId < 0 || literalId >= _bigIntLiterals.Count)
            throw new InvalidOperationException(
                $"BigInt literal id {literalId} is out of range [0, {_bigIntLiterals.Count}). " +
                "Pass the BigInt literal pool to the BytecodeInterpreter constructor.");
        return _bigIntLiterals[literalId];
    }
}
