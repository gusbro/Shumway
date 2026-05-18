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
    private readonly IReadOnlyList<string> _stringLiterals;
    private readonly IReadOnlyList<double> _floatLiterals;
    private readonly IReadOnlyList<System.Numerics.BigInteger> _bigIntLiterals;
    private readonly IReadOnlyList<SwitchTable> _switchTables;

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
    public IReadOnlyList<SwitchTable> SwitchTables => _switchTables;

    /// <summary>
    /// Runs <paramref name="code"/> starting at <paramref name="startPc"/> until the
    /// dispatch loop terminates. The engine's <c>P</c> is overwritten with the start PC
    /// and then advanced according to each instruction's semantics.
    /// </summary>
    public InterpreterResult Run(byte[] code, int startPc)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (startPc < 0 || startPc >= code.Length)
            throw new ArgumentOutOfRangeException(nameof(startPc),
                $"startPc 0x{startPc:X} is outside [0, 0x{code.Length:X}).");

        _engine.SetPc(startPc);
        try { return Dispatch(code); }
        catch (TopLevelFailure) { return InterpreterResult.Failed; }
    }

    /// <summary>Synchronous sub-predicate dispatch (chunk 50): saves
    /// the current Pc / Cp, sets Cp to the sub-routine sentinel
    /// (any negative value), points Pc at <paramref name="target"/>,
    /// and runs Dispatch until the sub-predicate's <c>proceed</c> sets
    /// Pc=Cp=sentinel which trips <c>Proceed</c>'s "returned past the
    /// top" early exit. Returns <c>true</c> on the sub-predicate's
    /// success / <c>false</c> on failure.
    ///
    /// <para>The IL <c>Call</c> emission's CanCompile only accepts
    /// callees that are leaf predicates (single-clause body-less head
    /// matching), so the sub-predicate never pushes choice points — the
    /// sentinel trick is safe (no later backtrack restores the sentinel
    /// as the saved Cp of a still-active CP).</para></summary>
    public bool RunSubroutine(byte[] code, int target)
    {
        ArgumentNullException.ThrowIfNull(code);
        int savedPc = _engine.P;
        int savedCp = _engine.Cp;
        _engine.SetCp(SubroutineSentinelCp);
        _engine.SetPc(target);
        InterpreterResult result;
        try { result = Dispatch(code); }
        catch (TopLevelFailure) { result = InterpreterResult.Failed; }
        _engine.SetPc(savedPc);
        _engine.SetCp(savedCp);
        return result == InterpreterResult.Halted;
    }

    /// <summary>Cp sentinel used by <see cref="RunSubroutine"/>. Any
    /// negative value works because <c>Proceed</c> already returns
    /// <see cref="InterpreterResult.Halted"/> when Cp &lt; 0; we pick
    /// a distinctive value to make stack-traces friendlier.</summary>
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
    public InterpreterResult Backtrack(byte[] code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (!TryBacktrack()) return InterpreterResult.Failed;
        try { return Dispatch(code); }
        catch (TopLevelFailure) { return InterpreterResult.Failed; }
    }

    private InterpreterResult Dispatch(byte[] code)
    {
        while (true)
        {
            int pc = _engine.P;
            if (pc < 0 || pc >= code.Length)
                throw new InvalidOperationException(
                    $"Program counter 0x{pc:X} is outside code range [0, 0x{code.Length:X}).");

            byte opByte = code[pc];
            switch ((Opcode)opByte)
            {
                case Opcode.ReservedInvalid:
                    throw new InvalidOperationException(
                        $"Encountered reserved_invalid opcode at PC=0x{pc:X4} — bytecode corruption.");

                case Opcode.Halt:
                    return InterpreterResult.Halted;

                case Opcode.Proceed:
                {
                    int returnPc = _engine.Cp;
                    if (returnPc < 0)
                        return InterpreterResult.Halted;       // returned past the top
                    _engine.SetPc(returnPc);
                    break;
                }

                case Opcode.Call:
                {
                    int target = BytecodeIO.ReadInt32(code, pc + 1);
                    // pc + 5 holds num_live_perms (informational, used by env trimming
                    // in a future chunk). Skip for now.
                    _engine.SetCp(pc + OpcodeTable.Get(Opcode.Call).Size);
                    _engine.SetB0(_engine.B);   // capture _b at procedure entry for neck_cut
                    DispatchToTier1OrBytecode(target);
                    break;
                }

                case Opcode.Execute:
                {
                    int target = BytecodeIO.ReadInt32(code, pc + 1);
                    _engine.SetB0(_engine.B);   // tail call still enters a new procedure
                    DispatchToTier1OrBytecode(target);
                    break;
                }

                case Opcode.Allocate:
                {
                    int n = BytecodeIO.ReadInt32(code, pc + 1);
                    _engine.Allocate(n);
                    _engine.AdvancePc(OpcodeTable.Get(Opcode.Allocate).Size);
                    break;
                }

                case Opcode.Deallocate:
                    _engine.Deallocate();
                    _engine.AdvancePc(OpcodeTable.Get(Opcode.Deallocate).Size);
                    break;

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
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.TrustMe:
                    _engine.TrustMe();
                    _engine.AdvancePc(1);
                    break;

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
                        _engine.SetRegister(target, _engine.GetHeap(ptr));
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
                        _engine.SetY(target, _engine.GetHeap(ptr));
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
                    int builtinId = BytecodeIO.ReadInt32(code, pc + 1);
                    // pc + 5 holds num_live_perms — unused today (reserved for
                    // future env trimming, like the regular Call opcode).
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                    if (!entry.Impl(_engine))
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

    private bool TryBacktrack()
    {
        // Loop so that an IL retry that itself fails immediately falls
        // through to the next choice point without burning stack.
        while (_engine.B >= 0)
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
    /// Used by every <c>switch_on_*</c> opcode to decide where to dispatch.</summary>
    private Cell DerefA1()
    {
        Cell a1 = _engine.GetRegister(0);
        if (a1.Tag == Tag.Ref)
            return _engine.GetHeap(_engine.Deref(a1.AsHeapIndex));
        return a1;
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
