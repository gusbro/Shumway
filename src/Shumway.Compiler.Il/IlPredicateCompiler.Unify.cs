using System;
using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

// ADR-060 stage 4: get_list, alone and in the fused windows, and SetRegister,
// emitted in the region. The helpers are marked for inlining, but their fast
// path covers only a register that holds the list itself; the cases a
// program meets most (a reference to the list, an unbound variable to bind to
// a new one) are in their NoInlining slow paths, and a large region runs out
// of inlining budget for the rest. Emitted here: the read through one
// reference and the write over a plain unbound variable, with the binding
// rule of Bind (the trail when the variable is older than HB). An
// attributed variable, a longer chain, a packed string, a register or heap
// that must grow, and the occurs check take the helper, before anything was
// written.
public sealed partial class IlPredicateCompiler
{
    private static readonly MethodInfo EngineUnifyHeapWithCellMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyHeapWithCell),
            new[] { typeof(int), typeof(Cell) })!;
    private static readonly MethodInfo EngineTrailBindMethod =
        typeof(Activation).GetMethod(nameof(Activation.TrailBind))!;
    private static readonly MethodInfo EngineOccursModeGetter =
        typeof(Activation).GetProperty(nameof(Activation.OccursMode))!.GetGetMethod()!;
    private static readonly FieldInfo EngHeap = EngineField(nameof(Activation._heap));
    private static readonly FieldInfo EngCellsAllocated = EngineField(nameof(Activation._cellsAllocated));
    private static readonly FieldInfo EngWriteMode = EngineField(nameof(Activation._writeMode));
    private static readonly FieldInfo EngUnifyPointer = EngineField(nameof(Activation._unifyPointer));
    private static readonly FieldInfo EngReservedWrite = EngineField(nameof(Activation._reservedWrite));

    /// <summary>Whether get_list and SetRegister are emitted in the region. An
    /// off switch for measuring them against the helpers.</summary>
    internal static bool InlineUnify { get; set; } =
        Environment.GetEnvironmentVariable("SHUMWAY_IL_INLINE_UNIFY") != "0";

    private const int KU = 60;

    private static long TagBits(Tag t) => (long)t << Cell.TagShift;

    private enum ListWindow { Plain, VarXVarX, ValXVarX }

    /// <summary>A get_list at a region site: <paramref name="window"/> says
    /// whether the two unify instructions after it are part of it (the fused
    /// forms) and <paramref name="s1"/>, <paramref name="s2"/> are their
    /// registers. False when the region does not take this form.</summary>
    private static bool TryEmitGetList(Sigil.Emit<PredicateDelegate> emit, ListWindow window,
        int arg, int s1, int s2, Sigil.Label failLabel)
    {
        var rf = RegisterFileOf(emit);
        if (!InlineUnify || rf is null || rf.Held == MachineRegs.None) return false;

        var regs = rf.Temp(typeof(Cell[]), KU);
        var heap = rf.Temp(typeof(Cell[]), KU + 1);
        var c = rf.Temp(typeof(long), KU);
        var d = rf.Temp(typeof(long), KU + 1);
        var home = rf.Temp(typeof(int), KU);
        var p = rf.Temp(typeof(int), KU + 1);
        var read = emit.DefineLabel($"gl_read_{NextLabelSeq()}");
        var write = emit.DefineLabel($"gl_write_{NextLabelSeq()}");
        var slow = emit.DefineLabel($"gl_slow_{NextLabelSeq()}");
        var done = emit.DefineLabel($"gl_done_{NextLabelSeq()}");

        EmitLoadEngineField(emit, EngRegisters);
        emit.StoreLocal(regs);
        EmitLoadEngineField(emit, EngHeap);
        emit.StoreLocal(heap);
        if (window != ListWindow.Plain)
        {
            // The window's registers must exist; growing the bank is the helper's.
            emit.LoadConstant(Math.Max(s1, s2));
            emit.LoadLocal(regs);
            emit.LoadLength<Cell>();
            emit.Convert<int>();
            emit.BranchIfGreaterOrEqual(slow);
        }

        // c := X[arg]; a list is read, a reference is followed one step.
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
        // A plain unbound variable is a Ref to itself.
        EmitTagIs(emit, d, Tag.Ref);
        emit.BranchIfFalse(slow);
        emit.LoadLocal(p);
        emit.LoadLocal(home);
        emit.BranchIfEqual(write);
        emit.Branch(slow);

        // Read: p is the head's cell.
        emit.MarkLabel(read);
        switch (window)
        {
            case ListWindow.Plain:
                EmitSetUnifyMode(emit, writeMode: false, () => emit.LoadLocal(p));
                break;
            case ListWindow.VarXVarX:
                EmitRegisterFromHeap(emit, regs, heap, s1, p, 0, rf);
                EmitRegisterFromHeap(emit, regs, heap, s2, p, 1, rf);
                break;
            case ListWindow.ValXVarX:
                emit.LoadArgument(0);
                emit.LoadLocal(p);
                emit.LoadLocal(regs);
                emit.LoadConstant(s1);
                emit.LoadElement<Cell>();
                EmitHelperCall(emit, EngineUnifyHeapWithCellMethod);
                emit.BranchIfFalse(failLabel);
                // The unify may have grown the heap or the register bank.
                EmitLoadEngineField(emit, EngRegisters);
                emit.StoreLocal(regs);
                EmitLoadEngineField(emit, EngHeap);
                emit.StoreLocal(heap);
                EmitRegisterFromHeap(emit, regs, heap, s2, p, 1, rf);
                break;
        }
        emit.Branch(done);

        // Write: bind the variable at home to a list at the heap top.
        emit.MarkLabel(write);
        if (window == ListWindow.ValXVarX)
        {
            // The occurs check tests the head value against the variable.
            emit.LoadArgument(0);
            EmitHelperCall(emit, EngineOccursModeGetter);
            emit.BranchIfTrue(slow);
        }
        EmitLoadEngineField(emit, EngHeapTop);
        emit.StoreLocal(p);
        if (window != ListWindow.Plain)
        {
            // The pair is allocated here: it must fit.
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
            if (window == ListWindow.VarXVarX)
                EmitHeapCell(emit, heap, p, 0, () => EmitRefBitsOf(emit, p, 0));
            else
                EmitHeapCell(emit, heap, p, 0, () =>
                {
                    emit.LoadLocal(regs);
                    emit.LoadConstant(s1);
                    emit.LoadElement<Cell>();
                    emit.LoadField(CellDataField);
                });
            EmitHeapCell(emit, heap, p, 1, () => EmitRefBitsOf(emit, p, 1));
        }
        // Bind(home, Lis(p)): the store, and the trail when home is older than HB.
        var trailed = emit.DefineLabel($"gl_trailed_{NextLabelSeq()}");
        EmitHeapCell(emit, heap, home, 0, () =>
        {
            emit.LoadConstant(TagBits(Tag.Lis));
            EmitRefBitsOf(emit, p, 0);
            emit.Or();
        });
        emit.LoadLocal(home);
        EmitLoadEngineField(emit, EngHb);
        emit.BranchIfGreaterOrEqual(trailed);
        emit.LoadArgument(0);
        emit.LoadLocal(home);
        EmitHelperCall(emit, EngineTrailBindMethod);
        emit.MarkLabel(trailed);
        switch (window)
        {
            case ListWindow.Plain:
                EmitSetUnifyMode(emit, writeMode: true, () => emit.LoadLocal(p));
                break;
            case ListWindow.VarXVarX:
                EmitRegisterBits(emit, regs, s1, () => EmitRefBitsOf(emit, p, 0));
                EmitRegisterBits(emit, regs, s2, () => EmitRefBitsOf(emit, p, 1));
                break;
            case ListWindow.ValXVarX:
                EmitRegisterBits(emit, regs, s2, () => EmitRefBitsOf(emit, p, 1));
                break;
        }
        emit.Branch(done);

        emit.MarkLabel(slow);
        emit.LoadArgument(0);
        emit.LoadConstant(arg);
        if (window == ListWindow.Plain)
        {
            EmitHelperCall(emit, EngineGetListMethod);
        }
        else
        {
            emit.LoadConstant(s1);
            emit.LoadConstant(s2);
            EmitHelperCall(emit, window == ListWindow.VarXVarX
                ? EngineGetListVarXVarXMethod : EngineGetListValXVarXMethod);
        }
        emit.BranchIfFalse(failLabel);
        emit.MarkLabel(done);
        return true;
    }

    /// <summary>SetRegister over the register array's field (or the region's
    /// local, when it holds it): the store when the index is in the bank, the
    /// method when the bank must grow. The stack holds [activation, index, cell].</summary>
    private static bool TryEmitSetRegister(Sigil.Emit<PredicateDelegate> emit, RegisterFile rf)
    {
        if (!InlineUnify) return false;
        var value = rf.Temp(typeof(Cell), KU);
        var idx = rf.Temp(typeof(int), KU + 2);
        var act = rf.Temp(typeof(Activation), 0);
        var regs = rf.Temp(typeof(Cell[]), KU);
        var slow = emit.DefineLabel($"setreg_slow_{NextLabelSeq()}");
        var done = emit.DefineLabel($"setreg_done_{NextLabelSeq()}");
        emit.StoreLocal(value);
        emit.StoreLocal(idx);
        emit.StoreLocal(act);
        EmitLoadEngineField(emit, EngRegisters);
        emit.StoreLocal(regs);
        emit.LoadLocal(idx);
        emit.LoadLocal(regs);
        emit.LoadLength<Cell>();
        emit.Convert<int>();
        emit.BranchIfGreaterOrEqual(slow);
        emit.LoadLocal(regs);
        emit.LoadLocal(idx);
        emit.LoadLocal(value);
        emit.StoreElement<Cell>();
        emit.Branch(done);
        emit.MarkLabel(slow);
        emit.LoadLocal(act);
        emit.LoadLocal(idx);
        emit.LoadLocal(value);
        EmitRowCall(emit, rf, EngineSetRegisterMethod);
        emit.MarkLabel(done);
        return true;
    }

    /// <summary>Pushes whether the tag of the raw bits in <paramref name="bits"/> is <paramref name="tag"/>.</summary>
    private static void EmitTagIs(Sigil.Emit<PredicateDelegate> emit, Sigil.Local bits, Tag tag)
    {
        emit.LoadLocal(bits);
        emit.LoadConstant(Cell.TagShift);
        emit.UnsignedShiftRight();
        emit.LoadConstant((long)tag);
        emit.CompareEqual();
    }

    /// <summary>Ref(base + offset) as raw bits: the Ref tag is 0.</summary>
    private static void EmitRefBitsOf(Sigil.Emit<PredicateDelegate> emit, Sigil.Local @base, int offset)
    {
        emit.LoadLocal(@base);
        if (offset != 0)
        {
            emit.LoadConstant(offset);
            emit.Add();
        }
        emit.Convert<uint>();
        emit.Convert<ulong>();
    }

    /// <summary>heap[base + offset] := a cell of the raw bits <paramref name="pushBits"/> leaves.</summary>
    private static void EmitHeapCell(Sigil.Emit<PredicateDelegate> emit, Sigil.Local heap,
        Sigil.Local @base, int offset, Action pushBits)
    {
        var cell = RegisterFileOf(emit)!.Temp(typeof(Cell), KU + 1);
        emit.LoadLocalAddress(cell);
        pushBits();
        emit.StoreField(CellDataField);
        emit.LoadLocal(heap);
        emit.LoadLocal(@base);
        if (offset != 0)
        {
            emit.LoadConstant(offset);
            emit.Add();
        }
        emit.LoadLocal(cell);
        emit.StoreElement<Cell>();
    }

    /// <summary>X[slot] := a cell of the raw bits <paramref name="pushBits"/> leaves.</summary>
    private static void EmitRegisterBits(Sigil.Emit<PredicateDelegate> emit, Sigil.Local regs,
        int slot, Action pushBits)
    {
        var cell = RegisterFileOf(emit)!.Temp(typeof(Cell), KU + 1);
        emit.LoadLocalAddress(cell);
        pushBits();
        emit.StoreField(CellDataField);
        emit.LoadLocal(regs);
        emit.LoadConstant(slot);
        emit.LoadLocal(cell);
        emit.StoreElement<Cell>();
    }

    /// <summary>X[slot] := heap[p + offset], a bare ATTVAR taken as a Ref to
    /// its home, as unify_variable_x reads it.</summary>
    private static void EmitRegisterFromHeap(Sigil.Emit<PredicateDelegate> emit, Sigil.Local regs,
        Sigil.Local heap, int slot, Sigil.Local p, int offset, RegisterFile rf)
    {
        var bits = rf.Temp(typeof(long), KU + 2);
        var plain = emit.DefineLabel($"glv_plain_{NextLabelSeq()}");
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
        EmitRegisterBits(emit, regs, slot, () => emit.LoadLocal(bits));
    }

    /// <summary>The unify mode get_list leaves: reserved write off, write mode
    /// as given, the unify pointer at the value <paramref name="pushPointer"/> pushes.</summary>
    private static void EmitSetUnifyMode(Sigil.Emit<PredicateDelegate> emit, bool writeMode, Action pushPointer)
    {
        emit.LoadArgument(0);
        emit.LoadConstant(false);
        emit.StoreField(EngReservedWrite);
        emit.LoadArgument(0);
        emit.LoadConstant(writeMode);
        emit.StoreField(EngWriteMode);
        emit.LoadArgument(0);
        pushPointer();
        emit.StoreField(EngUnifyPointer);
    }
}
