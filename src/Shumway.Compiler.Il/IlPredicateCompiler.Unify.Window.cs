using System;
using System.Collections.Generic;
using Shumway.Core;

namespace Shumway.Compiler.Il;

// ADR-060 stage 4: a get_list and the two unify instructions after it, of
// any of the simple kinds, as one window. Past the get_list the mode is
// known in each branch, so the unify instructions are emitted for it: in the
// read branch against the pair's cells, in the write branch as the two cells
// of a pair allocated at once. The helpers read the mode from the
// activation's fields at every instruction, and their write paths are not
// inlined.
public sealed partial class IlPredicateCompiler
{
    private readonly record struct WindowArg(Opcode Op, int Operand);

    /// <summary>The two unify instructions at <paramref name="pc"/>, when both
    /// are of the kinds a window emits and no label falls among them.</summary>
    private static WindowArg[]? ReadWindowArgs(byte[] code, int pc, int end, Func<int, bool> labelAt,
        out int next)
    {
        next = pc;
        var args = new WindowArg[2];
        for (int k = 0; k < 2; k++)
        {
            if (pc >= end || labelAt(pc)) return null;
            var op = (Opcode)code[pc];
            int operand = 0;
            switch (op)
            {
                case Opcode.UnifyVariableX:
                case Opcode.UnifyValueX:
                case Opcode.UnifyVariableY:
                case Opcode.UnifyValueY:
                case Opcode.UnifyAtom:
                case Opcode.UnifyInteger:
                    operand = BytecodeIO.ReadInt32(code, pc + 1);
                    pc += OpcodeTable.Get(op).Size;
                    break;
                case Opcode.UnifyNil:
                    pc += 1;
                    break;
                case Opcode.UnifyVoid:
                    operand = BytecodeIO.ReadInt32(code, pc + 1);
                    if (operand != 1) return null;
                    pc += OpcodeTable.Get(op).Size;
                    break;
                default:
                    return null;
            }
            args[k] = new WindowArg(op, operand);
        }
        next = pc;
        return args;
    }

    /// <summary>The window at a get_list; returns the pc after it, or -1 when
    /// it does not apply.</summary>
    private static int TryEmitGetListWindow(IlEmit emit, byte[] code,
        int pc, int end, int arg, Func<int, bool> labelAt, IlLabel failLabel)
    {
        var rf = RegisterFileOf(emit);
        if (!InlineUnify || rf is null) return -1;
        int after = pc + OpcodeTable.Get((byte)code[pc]).Size;
        var args = ReadWindowArgs(code, after, end, labelAt, out int next);
        if (args is null) return -1;
        int maxReg = arg;
        foreach (var a in args)
            if (a.Op is Opcode.UnifyVariableX or Opcode.UnifyValueX && a.Operand > maxReg)
                maxReg = a.Operand;

        var regs = rf.Temp(typeof(Cell[]), KU);
        var heap = rf.Temp(typeof(Cell[]), KU + 1);
        var c = rf.Temp(typeof(long), KU);
        var d = rf.Temp(typeof(long), KU + 1);
        var home = rf.Temp(typeof(int), KU);
        var p = rf.Temp(typeof(int), KU + 1);
        var read = emit.DefineLabel($"glw_read_{NextLabelSeq()}");
        var write = emit.DefineLabel($"glw_write_{NextLabelSeq()}");
        var slow = emit.DefineLabel($"glw_slow_{NextLabelSeq()}");
        var done = emit.DefineLabel($"glw_done_{NextLabelSeq()}");

        EmitLoadEngineField(emit, EngRegisters);
        emit.StoreLocal(regs);
        EmitLoadEngineField(emit, EngHeap);
        emit.StoreLocal(heap);
        emit.LoadConstant(maxReg);
        emit.LoadLocal(regs);
        emit.LoadLength<Cell>();
        emit.Convert<int>();
        emit.BranchIfGreaterOrEqual(slow);

        // As TryEmitGetList: a list is read, a reference followed one step, a
        // plain unbound variable written.
        emit.LoadLocal(regs);
        emit.LoadConstant(arg);
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        emit.StoreLocal(c);
        emit.LoadLocal(c);
        emit.Convert<int>();
        emit.StoreLocal(p);
        EmitTagIs(emit, c, Tag.Lis);
        emit.BranchIfTrue(read);
        EmitTagIs(emit, c, Tag.Ref);
        emit.BranchIfFalse(slow);
        emit.LoadLocal(p);
        emit.StoreLocal(home);
        emit.LoadLocal(heap);
        emit.LoadLocal(home);
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        emit.StoreLocal(d);
        emit.LoadLocal(d);
        emit.Convert<int>();
        emit.StoreLocal(p);
        EmitTagIs(emit, d, Tag.Lis);
        emit.BranchIfTrue(read);
        EmitTagIs(emit, d, Tag.Ref);
        emit.BranchIfFalse(slow);
        emit.LoadLocal(p);
        emit.LoadLocal(home);
        emit.BranchIfEqual(write);
        emit.Branch(slow);

        // Read: each instruction against its cell of the pair at p.
        emit.MarkLabel(read);
        for (int k = 0; k < 2; k++)
        {
            var a = args[k];
            switch (a.Op)
            {
                case Opcode.UnifyVariableX:
                    EmitRegisterFromHeap(emit, regs, heap, a.Operand, p, k, rf);
                    break;
                case Opcode.UnifyVariableY:
                    EmitYFromHeap(emit, heap, a.Operand, p, k, rf);
                    break;
                case Opcode.UnifyVoid:
                    break;
                default:
                    // A value: the general unification, which may grow the
                    // heap or the register bank.
                    emit.LoadArgument(0);
                    emit.LoadLocal(p);
                    if (k != 0)
                    {
                        emit.LoadConstant(k);
                        emit.Add();
                    }
                    EmitWindowValue(emit, a, regs);
                    EmitHelperCall(emit, EngineUnifyHeapWithCellMethod);
                    emit.BranchIfFalse(failLabel);
                    EmitLoadEngineField(emit, EngRegisters);
                    emit.StoreLocal(regs);
                    EmitLoadEngineField(emit, EngHeap);
                    emit.StoreLocal(heap);
                    break;
            }
        }
        emit.Branch(done);

        // Write: the pair at the heap top, its two cells, then the binding.
        emit.MarkLabel(write);
        bool hotWrite = CpsHotExit;
        if (hotWrite)
        {
            // A trailed binding is the cold method's, decided before any write.
            emit.LoadLocal(home);
            EmitLoadEngineField(emit, EngHb);
            emit.BranchIfLess(slow);
        }
        bool storesValue = false;
        foreach (var a in args)
            if (a.Op is Opcode.UnifyValueX or Opcode.UnifyValueY) storesValue = true;
        if (storesValue)
        {
            // The occurs check tests a stored value; the helpers run it.
            emit.LoadArgument(0);
            EmitHelperCall(emit, EngineOccursModeGetter);
            emit.BranchIfTrue(slow);
        }
        EmitLoadEngineField(emit, EngHeapTop);
        emit.StoreLocal(p);
        emit.LoadLocal(p);
        emit.LoadConstant(2);
        emit.Add();
        emit.LoadLocal(heap);
        emit.LoadLength<Cell>();
        emit.Convert<int>();
        emit.BranchIfGreater(slow);
        emit.LoadArgument(0);
        emit.LoadLocal(p);
        emit.LoadConstant(2);
        emit.Add();
        EmitStoreEngineField(emit, EngHeapTop);
        emit.LoadArgument(0);
        emit.LoadArgument(0);
        emit.LoadField(EngCellsAllocated);
        emit.LoadConstant(2L);
        emit.Add();
        emit.StoreField(EngCellsAllocated);
        for (int k = 0; k < 2; k++)
        {
            var a = args[k];
            int at = k;
            switch (a.Op)
            {
                case Opcode.UnifyVariableX:
                    EmitHeapCell(emit, heap, p, at, () => EmitRefBitsOf(emit, p, at));
                    EmitRegisterBits(emit, regs, a.Operand, () => EmitRefBitsOf(emit, p, at));
                    break;
                case Opcode.UnifyVariableY:
                    EmitHeapCell(emit, heap, p, at, () => EmitRefBitsOf(emit, p, at));
                    EmitYBits(emit, a.Operand, () => EmitRefBitsOf(emit, p, at), rf);
                    break;
                case Opcode.UnifyVoid:
                    EmitHeapCell(emit, heap, p, at, () => EmitRefBitsOf(emit, p, at));
                    break;
                default:
                    EmitHeapCell(emit, heap, p, at, () =>
                    {
                        EmitWindowValue(emit, a, regs);
                        emit.LoadField(CellDataField);
                        EmitAttVarBitsAsRef(emit, rf);
                    });
                    break;
            }
        }
        var trailed = emit.DefineLabel($"glw_trailed_{NextLabelSeq()}");
        EmitHeapCell(emit, heap, home, 0, () =>
        {
            emit.LoadConstant(TagBits(Tag.Lis));
            EmitRefBitsOf(emit, p, 0);
            emit.Or();
        });
        if (!hotWrite)
        {
            emit.LoadLocal(home);
            EmitLoadEngineField(emit, EngHb);
            emit.BranchIfGreaterOrEqual(trailed);
            emit.LoadArgument(0);
            emit.LoadLocal(home);
            EmitHelperCall(emit, EngineTrailBindMethod);
        }
        emit.MarkLabel(trailed);
        emit.Branch(done);

        // The helpers, as the instructions compile alone.
        emit.MarkLabel(slow);
        if (!EmitColdExit(emit))   // ADR-061: a hot method leaves for the cold one
        {
            emit.LoadArgument(0);
            emit.LoadConstant(arg);
            EmitHelperCall(emit, EngineGetListMethod);
            emit.BranchIfFalse(failLabel);
            foreach (var a in args)
            {
                emit.LoadArgument(0);
                switch (a.Op)
                {
                    case Opcode.UnifyVariableX:
                        emit.LoadConstant(a.Operand);
                        EmitHelperCall(emit, EngineUnifyVariableXMethod);
                        break;
                    case Opcode.UnifyVariableY:
                        emit.LoadConstant(a.Operand);
                        EmitHelperCall(emit, EngineUnifyVariableYMethod);
                        break;
                    case Opcode.UnifyValueX:
                        emit.LoadConstant(a.Operand);
                        EmitHelperCall(emit, EngineUnifyValueXMethod);
                        emit.BranchIfFalse(failLabel);
                        break;
                    case Opcode.UnifyValueY:
                        emit.LoadConstant(a.Operand);
                        EmitHelperCall(emit, EngineUnifyValueYMethod);
                        emit.BranchIfFalse(failLabel);
                        break;
                    case Opcode.UnifyVoid:
                        emit.LoadConstant(1);
                        EmitHelperCall(emit, EngineUnifyVoidMethod);
                        break;
                    default:
                        EmitConstantCell(emit, a);
                        EmitHelperCall(emit, EngineUnifyArgCellMethod);
                        emit.BranchIfFalse(failLabel);
                        break;
                }
            }
        }
        emit.MarkLabel(done);
        return next;
    }

    /// <summary>The cell of a value instruction on the stack: a register, a Y
    /// slot or a constant.</summary>
    private static void EmitWindowValue(IlEmit emit, WindowArg a, IlLocal regs)
    {
        switch (a.Op)
        {
            case Opcode.UnifyValueX:
                emit.LoadLocal(regs);
                emit.LoadConstant(a.Operand);
                emit.LoadElement<Cell>();
                break;
            case Opcode.UnifyValueY:
                EmitYAddress(emit, a.Operand, RegisterFileOf(emit)!);
                emit.LoadElement<Cell>();
                break;
            default:
                EmitConstantCell(emit, a);
                break;
        }
    }

    private static void EmitConstantCell(IlEmit emit, WindowArg a)
    {
        switch (a.Op)
        {
            case Opcode.UnifyAtom:
                EmitAtomId(emit, a.Operand);
                EmitHelperCall(emit, CellAtomMethod);
                break;
            case Opcode.UnifyInteger:
                emit.LoadConstant((long)a.Operand);
                EmitHelperCall(emit, CellIntMethod);
                break;
            default:   // nil
                emit.LoadConstant(AtomTable.EmptyListId);
                EmitHelperCall(emit, CellAtomMethod);
                break;
        }
    }

    /// <summary>The stack array and the index of Y[slot], from the region's
    /// locals or the fields.</summary>
    private static void EmitYAddress(IlEmit emit, int slot, RegisterFile rf)
    {
        EmitLoadEngineField(emit, EngStack);
        EmitLoadEngineField(emit, EngE);
        emit.LoadConstant(Activation.EnvY1Offset + slot);
        emit.Add();
    }

    /// <summary>Y[slot] := a cell of the raw bits <paramref name="pushBits"/> leaves.</summary>
    private static void EmitYBits(IlEmit emit, int slot, Action pushBits, RegisterFile rf)
    {
        var cell = rf.Temp(typeof(Cell), KU + 1);
        emit.LoadLocalAddress(cell);
        pushBits();
        emit.StoreField(CellDataField);
        EmitYAddress(emit, slot, rf);
        emit.LoadLocal(cell);
        emit.StoreElement<Cell>();
    }

    /// <summary>Y[slot] := heap[p + offset], a bare ATTVAR taken as a Ref to its home.</summary>
    private static void EmitYFromHeap(IlEmit emit, IlLocal heap,
        int slot, IlLocal p, int offset, RegisterFile rf)
    {
        var bits = rf.Temp(typeof(long), KU + 2);
        var plain = emit.DefineLabel($"glwy_plain_{NextLabelSeq()}");
        emit.LoadLocal(heap);
        emit.LoadLocal(p);
        if (offset != 0)
        {
            emit.LoadConstant(offset);
            emit.Add();
        }
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        emit.StoreLocal(bits);
        EmitTagIs(emit, bits, Tag.AttVar);
        emit.BranchIfFalse(plain);
        EmitRefBitsOf(emit, p, offset);
        emit.StoreLocal(bits);
        emit.MarkLabel(plain);
        EmitYBits(emit, slot, () => emit.LoadLocal(bits), rf);
    }

    /// <summary>The raw bits on the stack, a bare ATTVAR turned into a Ref to
    /// its home, as the write mode of UnifyArgCell stores it.</summary>
    private static void EmitAttVarBitsAsRef(IlEmit emit, RegisterFile rf)
    {
        var bits = rf.Temp(typeof(long), KU + 3);
        var plain = emit.DefineLabel($"glwa_plain_{NextLabelSeq()}");
        emit.StoreLocal(bits);
        EmitTagIs(emit, bits, Tag.AttVar);
        emit.BranchIfFalse(plain);
        emit.LoadLocal(bits);
        emit.Convert<uint>();
        emit.Convert<ulong>();
        emit.StoreLocal(bits);
        emit.MarkLabel(plain);
        emit.LoadLocal(bits);
    }
}
