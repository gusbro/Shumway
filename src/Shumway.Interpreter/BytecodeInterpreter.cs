using Shumway.Core;

namespace Shumway.Interpreter;

/// <summary>
/// Tier 0 WAM bytecode interpreter. Dispatches one opcode at a time on a target
/// <see cref="Engine"/>, calling into the engine's state-management APIs for the
/// actual work. The opcode encoding is defined in ADR-006 and the per-instruction
/// semantics in docs/design/wam-instruction-set.md.
///
/// <para>This MVP implements only the control-flow subset: <c>halt</c>, <c>proceed</c>,
/// <c>call</c>, <c>execute</c>, <c>allocate</c>, <c>deallocate</c>, plus the
/// <c>reserved_invalid</c> canary. Any other opcode throws <see cref="NotImplementedException"/>
/// — they land in later chunks (get/put/unify, choice points, cut, etc.). This is enough
/// to drive a hand-crafted "call-and-return" bytecode end-to-end.</para>
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

                default:
                    throw new NotImplementedException(
                        $"Opcode 0x{opByte:X2} ({(Opcode)opByte}) is not implemented in the 5a subset. " +
                        $"Reached at PC=0x{pc:X4}.");
            }
        }
    }
}
