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
/// nil — both X and Y forms), and the A1/A2 consolidations. Compound (STR/LIS) and
/// unify-mode opcodes are still <see cref="NotImplementedException"/>; choice points
/// and cut land in later chunks.</para>
/// </summary>
public sealed class BytecodeInterpreter
{
    private readonly Engine _engine;

    public BytecodeInterpreter(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public Engine Engine => _engine;

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
                    _engine.SetPc(target);
                    break;
                }

                case Opcode.Execute:
                {
                    int target = BytecodeIO.ReadInt32(code, pc + 1);
                    _engine.SetPc(target);     // tail call — CP is inherited
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
                        return InterpreterResult.Failed;
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.GetValueY:
                {
                    int src = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    if (!_engine.UnifyPermanentWithRegister(src, arg))
                        return InterpreterResult.Failed;
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.GetConstant:
                case Opcode.GetAtom:
                {
                    int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Atom(atomId)))
                        return InterpreterResult.Failed;
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.GetInteger:
                {
                    int value = BytecodeIO.ReadInt32(code, pc + 1);
                    int arg = BytecodeIO.ReadInt32(code, pc + 5);
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Int(value)))
                        return InterpreterResult.Failed;
                    _engine.AdvancePc(9);
                    break;
                }

                case Opcode.GetNil:
                {
                    int arg = BytecodeIO.ReadInt32(code, pc + 1);
                    if (!_engine.UnifyRegisterWithCell(arg, Cell.Atom(AtomTable.EmptyListId)))
                        return InterpreterResult.Failed;
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
                        return InterpreterResult.Failed;
                    _engine.AdvancePc(5);
                    break;
                }

                case Opcode.GetConstantA2:
                {
                    int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                    if (!_engine.UnifyRegisterWithCell(1, Cell.Atom(atomId)))
                        return InterpreterResult.Failed;
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

                default:
                    throw new NotImplementedException(
                        $"Opcode 0x{opByte:X2} ({(Opcode)opByte}) is not implemented yet. " +
                        $"Reached at PC=0x{pc:X4}.");
            }
        }
    }
}
