using Shumway.Core;

namespace Shumway.Interpreter;

/// <summary>
/// Tier 0 WAM bytecode interpreter. Dispatches one opcode at a time on a target
/// <see cref="Engine"/>, calling into the engine's state-management APIs for the
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
    private readonly Engine _engine;
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

    public BytecodeInterpreter(Engine engine)
        : this(engine, Array.Empty<string>(), Array.Empty<double>(), Array.Empty<SwitchTable>())
    {
    }

    public BytecodeInterpreter(Engine engine, IReadOnlyList<string> stringLiterals)
        : this(engine, stringLiterals, Array.Empty<double>(), Array.Empty<SwitchTable>())
    {
    }

    public BytecodeInterpreter(
        Engine engine,
        IReadOnlyList<string> stringLiterals,
        IReadOnlyList<double> floatLiterals)
        : this(engine, stringLiterals, floatLiterals, Array.Empty<SwitchTable>())
    {
    }

    public BytecodeInterpreter(
        Engine engine,
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
        Engine engine,
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
    }

    public Engine Engine => _engine;
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
        if (startPc < 0 || startPc >= code.Length)
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
        ArgumentNullException.ThrowIfNull(code);
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
        while (true)
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
            int pc = _engine.P;
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
            if (Engine.IsResumeMarker(pc))
            {
                // ADR-016 safe point: an IL non-tail callee has Proceeded
                // back to its caller; caller state lives in the engine.
                _engine.MaybeCollectHeap();
                var (functorId, cursor) = Engine.DecodeResumeMarker(pc);
                var del = Tier1Dispatcher?.ResolveByFunctorId(functorId);
                if (del is null)
                    throw new InvalidOperationException(
                        $"Resume marker at PC 0x{pc:X} decodes to functor "
                        + $"id {functorId} / cursor {cursor} but no IL "
                        + "delegate is bound. (A Tier-1 promotion must "
                        + "have unwired itself mid-query, which is a bug.)");
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

            // Chunk 170: when the view is split (Overflow != null) we
            // fall back to the indexer for both the opcode byte and
            // every operand read inside the case bodies (those still
            // go through BytecodeIO's ProgramView overloads, which
            // handle the split internally). The fast path skips the
            // per-tick Split branch entirely.
            byte opByte = code.Overflow is null ? codeArr[pc] : code[pc];
            Shumway.Core.Profiler.Opcode(opByte);
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
                    _engine.AdvancePc(1);
                    break;

                case Opcode.Proceed:
                {
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
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
                    int target = BytecodeIO.ReadInt32(code, pc + 1);
                    int numLivePerms = BytecodeIO.ReadInt32(code, pc + 5);
                    target = ResolveTargetMaybeAutoPromoted(target);
                    Shumway.Core.Profiler.Call(target);
                    // Env trimming (chunk 57): shrink the current frame to
                    // num_live_perms Y slots before dispatching, so the callee's
                    // pushes (CP, allocate) sit just above the live region of
                    // the parent frame.
                    _engine.TrimEnv(numLivePerms);
                    _engine.SetCp(pc + 9);  // Call is 9 bytes (opcode + addr + count)
                    _engine.SetB0(_engine.B);   // capture _b at procedure entry for neck_cut
                    DispatchToTier1OrBytecode(target);
                    break;
                }

                case Opcode.Execute:
                {
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int target = BytecodeIO.ReadInt32(code, pc + 1);
                    target = ResolveTargetMaybeAutoPromoted(target);
                    Shumway.Core.Profiler.Call(target);
                    _engine.SetB0(_engine.B);   // tail call still enters a new procedure
                    DispatchToTier1OrBytecode(target);
                    break;
                }

                case Opcode.Allocate:
                {
                    int n = BytecodeIO.ReadInt32(code, pc + 1);
                    _engine.Allocate(n);
                    _engine.AdvancePc(5);   // allocate is 5 bytes
                    break;
                }

                case Opcode.Deallocate:
                    _engine.Deallocate();
                    _engine.AdvancePc(1);   // deallocate is 1 byte
                    break;

                // ---------- Chunk 220 — fused opcodes (peephole) ----------

                case Opcode.AllocateGetLevel:
                {
                    // 10-byte layout: [op:1] [count:4] [slot:4] [Nop:1]
                    int n = BytecodeIO.ReadInt32(code, pc + 1);
                    int slot = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.Allocate(n);
                    _engine.GetLevel(slot);
                    _engine.AdvancePc(10);
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
                    int returnPc = _engine.Cp;
                    if (returnPc < 0) return InterpreterResult.Halted;
                    _engine.SetPc(returnPc);
                    break;
                }

                // ---------- Choice point opcodes ----------

                case Opcode.TryMeElse:
                {
                    int nextClause = BytecodeIO.ReadInt32(code, pc + 1);
                    int arity = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.PushChoicePoint(arity, nextClause);
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.RetryMeElse:
                {
                    int nextClause = BytecodeIO.ReadInt32(code, pc + 1);
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
                    _engine.AdvancePc(demoted ? 9 : 5);
                    break;
                }

                case Opcode.TrustMe:
                    _engine.TrustMe();
                    _engine.AdvancePc(1);
                    break;

                // ADR-015 chunk C — generation-filtered dynamic dispatch.
                // Sample the dynamic-database generation into CurrentViewGen
                // so the surrounding try_me_else captures it into the CP and
                // every clause's CheckVisible reads the call's stable view.
                case Opcode.EnterDynamic:
                {
                    var provider = _engine.DbGenerationProvider;
                    _engine.CurrentViewGen = provider is null ? 0L : provider();
                    _engine.AdvancePc(1);
                    break;
                }

                // Per-clause visibility check. Reads born/died from the
                // bytecode (retract patches the died slot in place) and
                // backtracks if the calling goal's captured view-gen is
                // outside [born, died) — the ISO logical update view.
                case Opcode.CheckVisible:
                {
                    long born = BytecodeIO.ReadInt64(code, pc + 1);
                    long died = BytecodeIO.ReadInt64(code, pc + 9);
                    long g = _engine.CurrentViewGen;
                    if (born > g || died <= g)
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(17);
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

                // ---------- Cut opcodes ----------

                case Opcode.NeckCut:
                    _engine.NeckCut();
                    _engine.AdvancePc(1);
                    break;

                case Opcode.GetLevel:
                {
                    int slot = BytecodeIO.ReadInt32(code, pc + 1);
                    _engine.GetLevel(slot);
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.Cut:
                {
                    int slot = BytecodeIO.ReadInt32(code, pc + 1);
                    int barrier = (int)_engine.GetY(slot).Data;
                    _engine.Cut(barrier);
                    _engine.AdvancePc(5);
                    break;
                }

                // ---------- Compound (STR) and list (LIS) — open instructions ----------

                case Opcode.GetStructure:
                {
                    int functorId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    if (!_engine.GetStructure(functorId, arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.PutStructure:
                {
                    int functorId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.PutStructure(functorId, arg);
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.GetList:
                {
                    int arg = BytecodeIO.ReadInt32(code, pc + 1);
                    if (!_engine.GetList(arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.PutList:
                {
                    int arg = BytecodeIO.ReadInt32(code, pc + 1);
                    _engine.PutList(arg);
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.GetListA1:
                    if (!_engine.GetList(0))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(1);
                    break;

                case Opcode.GetListA2:
                    if (!_engine.GetList(1))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(1);
                    break;

                // ---------- Unify-mode opcodes (consume cells via _unifyPointer) ----------

                case Opcode.UnifyVariableX:
                {
                    int target = BytecodeIO.ReadInt32(code, pc + 1);
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
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.UnifyVariableY:
                {
                    int target = BytecodeIO.ReadInt32(code, pc + 1);
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
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.UnifyValueX:
                {
                    int src = BytecodeIO.ReadInt32(code, pc + 1);
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
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.UnifyValueY:
                {
                    int src = BytecodeIO.ReadInt32(code, pc + 1);
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
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.UnifyConstant:
                case Opcode.UnifyAtom:
                {
                    int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                    int ptr = _engine.UnifyPointer;
                    Cell value = Cell.Atom(atomId);
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
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.UnifyInteger:
                {
                    int intValue = BytecodeIO.ReadInt32(code, pc + 1);
                    int ptr = _engine.UnifyPointer;
                    Cell value = Cell.Int(intValue);
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
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.UnifyNil:
                {
                    int ptr = _engine.UnifyPointer;
                    Cell value = Cell.Atom(AtomTable.EmptyListId);
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
                    _engine.AdvancePc(1);
                    break;
                }

                case Opcode.UnifyVoid:
                {
                    int count = BytecodeIO.ReadInt32(code, pc + 1);
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        for (int i = 0; i < count; i++)
                            _engine.AllocateHeapUnbound();
                    }
                    _engine.SetUnifyPointer(ptr + count);
                    _engine.AdvancePc(5);
                    break;
                }

                // ---------- Get instructions ----------

                case Opcode.GetVariableX:
                {
                    int dest = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.SetRegister(dest, _engine.GetRegister(arg));
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.GetVariableY:
                {
                    int dest = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.SetY(dest, _engine.GetRegister(arg));
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.GetValueX:
                {
                    int src = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    if (!_engine.UnifyRegisters(src, arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.GetValueY:
                {
                    int src = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    if (!_engine.UnifyPermanentWithRegister(src, arg))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.GetConstant:
                case Opcode.GetAtom:
                {
                    int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Atom(atomId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.GetInteger:
                {
                    int value = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Int(value)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.GetNil:
                {
                    int arg = BytecodeIO.ReadInt32(code, pc + 1);
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Atom(AtomTable.EmptyListId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(5);
                    break;
                }

                // ---------- Put instructions ----------

                case Opcode.PutVariableX:
                {
                    int dest = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    int heapIdx = _engine.AllocateHeapUnbound();
                    Cell refCell = Cell.Ref(heapIdx);
                    _engine.SetRegister(dest, refCell);
                    _engine.SetRegister(arg, refCell);
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.PutVariableY:
                {
                    int dest = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    int heapIdx = _engine.AllocateHeapUnbound();
                    Cell refCell = Cell.Ref(heapIdx);
                    _engine.SetY(dest, refCell);
                    _engine.SetRegister(arg, refCell);
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.PutValueX:
                {
                    int src = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.SetRegister(arg, _engine.GetRegister(src));
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.PutValueY:
                {
                    int src = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.SetRegister(arg, _engine.GetY(src));
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.PutConstant:
                case Opcode.PutAtom:
                {
                    int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.SetRegister(arg, Cell.Atom(atomId));
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.PutInteger:
                {
                    int value = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.SetRegister(arg, Cell.Int(value));
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.PutNil:
                {
                    int arg = BytecodeIO.ReadInt32(code, pc + 1);
                    _engine.SetRegister(arg, Cell.Atom(AtomTable.EmptyListId));
                    _engine.AdvancePc(5);
                    break;
                }

                // ---------- Consolidated A1/A2 specialisations ----------

                case Opcode.GetConstantA1:
                {
                    int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                    if (!_engine.UnifyRegisterWithCell(0, Cell.Atom(atomId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.GetConstantA2:
                {
                    int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                    if (!_engine.UnifyRegisterWithCell(1, Cell.Atom(atomId)))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.PutConstantA1:
                {
                    int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                    _engine.SetRegister(0, Cell.Atom(atomId));
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.PutConstantA2:
                {
                    int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                    _engine.SetRegister(1, Cell.Atom(atomId));
                    _engine.AdvancePc(5);
                    break;
                }

                // ---------- Builtin call ----------

                case Opcode.CallBuiltin:
                {
                    if (!FlushPendingWakeups(code))
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
                    int builtinId = BytecodeIO.ReadInt32(code, pc + 1);
                    int numLivePerms = BytecodeIO.ReadInt32(code, pc + 5);
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
                    if (entry.Name == "call")
                    {
                        // A top-level call/N: a `!` written as the goal
                        // commits no further than the call itself, so the
                        // barrier is B as the call is entered.
                        if (!DispatchCall(code, entry.Arity, _engine.B))
                            return InterpreterResult.Failed;
                        break;
                    }
                    if (entry.Name == "$call")
                    {
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
                    if (!implOk)
                    {
                        if (!TryBacktrack()) return InterpreterResult.Failed;
                        break;
                    }
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
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.PutPstr:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    int headerIdx = _engine.MakePstr(ResolveLiteral(literalId));
                    _engine.SetRegister(arg, Cell.Ref(headerIdx));
                    _engine.AdvancePc(9);
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
                    _engine.AdvancePc(5);
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
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.PutFloat:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    int headerIdx = _engine.MakeFloat(ResolveFloatLiteral(literalId));
                    _engine.SetRegister(arg, Cell.Ref(headerIdx));
                    _engine.AdvancePc(9);
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
                    _engine.AdvancePc(5);
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
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.PutBigInt:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    _engine.SetRegister(arg, _engine.MakeBigInt(ResolveBigIntLiteral(literalId)));
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.UnifyBigInt:
                {
                    int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                    int ptr = _engine.UnifyPointer;
                    Cell value = _engine.MakeBigInt(ResolveBigIntLiteral(literalId));
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
                    _engine.AdvancePc(5);
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
                    _engine.AdvancePc(metaSize);
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
    private void DispatchToTier1OrBytecode(int target)
    {

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

    /// <summary>Runs any <c>verify_attributes</c> wakeups queued by a
    /// just-completed unification (chunk 80). Checked at every goal
    /// boundary — Call / Execute / CallBuiltin / Proceed. The
    /// <c>'$wakeup_attributes'/1</c> driver runs in the *live* engine
    /// (via <see cref="RunGoalInEngine"/>) so the hooks observe the real
    /// attributed variables. Returns false when a hook — or a goal it
    /// returned — failed, which the caller turns into a backtrack so the
    /// triggering unification fails. A no-op (returns true) when nothing
    /// is queued, the overwhelmingly common case.</summary>
    private bool FlushPendingWakeups(ProgramView code)
    {
        if (!_engine.HasPendingWakeups) return true;

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

        throw PrologRuntimeException.UndefinedProcedure(functorId);
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
        int pc = _engine.P;
        Cell goal = DerefCell(_engine.GetRegister(0));

        // Save call/N's extra arguments before the registers are reloaded.
        Cell[] extra = callArity > 1 ? new Cell[callArity - 1] : System.Array.Empty<Cell>();
        for (int i = 0; i < callArity - 1; i++)
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

        int totalArity = goalArity + (callArity - 1);
        for (int i = 0; i < goalArity; i++)
            _engine.SetRegister(i, _engine.GetHeap(argBase + i));
        for (int i = 0; i < callArity - 1; i++)
            _engine.SetRegister(goalArity + i, extra[i]);

        int functorId = FunctorTable.Intern(atomId, totalArity);

        // A control construct in a runtime goal routes to its prelude
        // helper. conj/disj/arrow are cut-transparent, so they take the
        // barrier as a third argument (X2): a `!` threaded down through
        // them commits to the enclosing call (chunk 88). \+ is opaque to
        // cut, so $call_neg needs no barrier.
        if (functorId == ConjFunctorId)
        {
            _engine.SetRegister(2, Cell.Int(barrier));
            functorId = CallConjFunctorId;
        }
        else if (functorId == DisjFunctorId)
        {
            _engine.SetRegister(2, Cell.Int(barrier));
            functorId = CallDisjFunctorId;
        }
        else if (functorId == ArrowFunctorId)
        {
            _engine.SetRegister(2, Cell.Int(barrier));
            functorId = CallArrowFunctorId;
        }
        else if (functorId == NegFunctorId || functorId == NotFunctorId)
        {
            functorId = CallNegFunctorId;
        }

        // ! as the whole goal: commit to the barrier the enclosing call
        // established (chunk 88). For a top-level call(!) the barrier is B
        // at call entry, so Cut() removes nothing; for a `!` threaded in
        // from a $call_* helper it cuts the runtime goal's choice points,
        // and no further — the parent's CPs sit at or below the barrier.
        if (functorId == CutFunctorId)
        {
            _engine.Cut(barrier);
            _engine.AdvancePc(9);
            return true;
        }
        if (functorId == TrueFunctorId)
        {
            _engine.AdvancePc(9);
            return true;
        }
        if (functorId == FailFunctorId)
            return TryBacktrack();

        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int builtinId))
        {
            var builtin = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
            // call(call(...)): recurse rather than invoking the call
            // builtin. The inner call is itself a fresh cut barrier, so
            // capture B again rather than passing the outer `barrier`.
            if (builtin.Name == "call")
                return DispatchCall(code, builtin.Arity, _engine.B);
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

        var addresses = _engine.CurrentFunctorAddresses;
        if (addresses is not null && addresses.TryGetValue(functorId, out int address))
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
            DispatchToTier1OrBytecode(address);
            return true;
        }

        throw PrologRuntimeException.UndefinedProcedure(functorId);
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
        }
        throw PrologRuntimeException.UndefinedProcedure(fid);
    }

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
            int arity = (int)_engine.GetStack(_engine.B + Engine.CpArityOffset).Data;
            int bp = (int)_engine.GetStack(_engine.B + Engine.CpBpOffset(arity)).Data;
            _engine.SetPc(bp);
            return true;
        }
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

    private System.Numerics.BigInteger ResolveBigIntLiteral(int literalId)
    {
        if (literalId < 0 || literalId >= _bigIntLiterals.Count)
            throw new InvalidOperationException(
                $"BigInt literal id {literalId} is out of range [0, {_bigIntLiterals.Count}). " +
                "Pass the BigInt literal pool to the BytecodeInterpreter constructor.");
        return _bigIntLiterals[literalId];
    }
}
