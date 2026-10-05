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
    private static readonly FieldInfo EngBindingTrail = EngineField(nameof(Activation._bindingTrail));

    /// <summary>Bindings a hot method trails inline, over the methods emitted
    /// in this process.</summary>
    internal static int HotTrailSites;

    // The tags of a cell that no list unifies with: get_list's helper would
    // only say no. An attributed variable and a packed string are not among
    // them (they unify with a list), nor a reference, which may lead to one.
    private const int NeverAListTags = 1 << (int)Tag.Str | 1 << (int)Tag.Atom | 1 << (int)Tag.Int
        | 1 << (int)Tag.Float | 1 << (int)Tag.BigInt;

    // An atom or a small integer is its bits: a cell with other bits and one
    // of these tags is another term.
    private const int ImmediateTags = 1 << (int)Tag.Atom | 1 << (int)Tag.Int;
    private const int NeverAnotherImmediateTags = ImmediateTags | 1 << (int)Tag.Str | 1 << (int)Tag.Lis;

    /// <summary>Branches when the tag of the raw bits in
    /// <paramref name="bits"/> is one of <paramref name="tags"/> (1 &lt;&lt; tag each).</summary>
    private static void EmitBranchIfTagIn(IlEmit emit, IlLocal bits, int tags, IlLabel target)
    {
        emit.LoadConstant(tags);
        emit.LoadLocal(bits);
        emit.LoadConstant(Cell.TagShift);
        emit.UnsignedShiftRight();
        emit.Convert<int>();
        emit.UnsignedShiftRight();
        emit.LoadConstant(1);
        emit.And();
        emit.BranchIfTrue(target);
    }

    /// <summary>In a hot method, the trail entry of the binding about to be
    /// stored at <paramref name="home"/>, when the cell is older than HB:
    /// TrailBind inline. Emitted after the site's last exit to the cold
    /// method and before its first write: a full trail is the cold method's,
    /// which grows it and runs the instruction again. A hot method makes no
    /// call that returns (ADR-061 item 6).</summary>
    private static void EmitHotTrail(IlEmit emit, IlLocal home, IlLabel slow)
    {
        System.Threading.Interlocked.Increment(ref HotTrailSites);
        var done = emit.DefineLabel($"trail_done_{NextLabelSeq()}");
        emit.LoadLocal(home);
        EmitLoadEngineField(emit, EngHb);
        emit.BranchIfGreaterOrEqual(done);
        EmitLoadEngineField(emit, EngBindingTrailTop);
        EmitLoadEngineField(emit, EngBindingTrail);
        emit.LoadLength<int>();
        emit.Convert<int>();
        emit.BranchIfGreaterOrEqual(slow);
        EmitLoadEngineField(emit, EngBindingTrail);
        EmitLoadEngineField(emit, EngBindingTrailTop);
        emit.LoadLocal(home);
        emit.StoreElement<int>();
        emit.LoadArgument(0);
        EmitLoadEngineField(emit, EngBindingTrailTop);
        emit.LoadConstant(1);
        emit.Add();
        EmitStoreEngineField(emit, EngBindingTrailTop);
        emit.MarkLabel(done);
    }
    private static readonly FieldInfo EngCellsAllocated = EngineField(nameof(Activation._cellsAllocated));
    private static readonly FieldInfo EngWriteMode = EngineField(nameof(Activation._writeMode));
    private static readonly FieldInfo EngUnifyPointer = EngineField(nameof(Activation._unifyPointer));
    private static readonly FieldInfo EngReservedWrite = EngineField(nameof(Activation._reservedWrite));

    /// <summary>Whether get_list and SetRegister are emitted in the region. An
    /// off switch for measuring them against the helpers.</summary>
    internal static bool InlineUnify { get; set; } =
        Environment.GetEnvironmentVariable("SHUMWAY_IL_INLINE_UNIFY") != "0";

    /// <summary>Whether UnifyRegisterWithCell's fast path is emitted inline. An
    /// off switch for measuring it.</summary>
    internal static bool InlineUnifyRegisterWithCell { get; set; } =
        Environment.GetEnvironmentVariable("SHUMWAY_IL_INLINE_URC") != "0";

    private const int KU = 60;

    private static long TagBits(Tag t) => (long)t << Cell.TagShift;

    private enum ListWindow { Plain, VarXVarX, ValXVarX }

    /// <summary>A get_list at a region site: <paramref name="window"/> says
    /// whether the two unify instructions after it are part of it (the fused
    /// forms) and <paramref name="s1"/>, <paramref name="s2"/> are their
    /// registers. False when the region does not take this form.</summary>
    private static bool TryEmitGetList(IlEmit emit, ListWindow window,
        int arg, int s1, int s2, IlLabel failLabel)
    {
        var rf = RegisterFileOf(emit);
        if (!InlineUnify || rf is null) return false;

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
        // A hot method's slow path leaves for the cold method: a cell no list
        // unifies with fails here instead. Its test is off the paths of a
        // list and of a variable.
        bool hotFail = CpsHotExit;
        var other = hotFail ? emit.DefineLabel($"gl_other_{NextLabelSeq()}") : slow;
        var otherRef = hotFail ? emit.DefineLabel($"gl_other_ref_{NextLabelSeq()}") : slow;

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
        emit.BranchIfFalse(other);
        // A hot method follows the whole chain of references; elsewhere the
        // second step is the helper's.
        var deref = emit.DefineLabel($"gl_deref_{NextLabelSeq()}");
        if (hotFail) emit.MarkLabel(deref);
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
        emit.BranchIfFalse(otherRef);
        emit.LoadLocal(p);
        emit.LoadLocal(home);
        emit.BranchIfEqual(write);
        emit.Branch(hotFail ? deref : slow);

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
        bool hotWrite = CpsHotExit;
        if (window == ListWindow.ValXVarX)
        {
            // The occurs check tests the head value against the variable.
            emit.LoadArgument(0);
            EmitHelperCall(emit, EngineOccursModeGetter);
            emit.BranchIfTrue(slow);
        }
        EmitLoadEngineField(emit, EngHeapTop);
        emit.StoreLocal(p);
        if (window == ListWindow.Plain)
        {
            if (hotWrite) EmitHotTrail(emit, home, slow);
        }
        else
        {
            // The pair is allocated here: it must fit.
            emit.LoadLocal(p);
            emit.LoadConstant(2);
            emit.Add();
            emit.LoadLocal(heap);
            emit.LoadLength<Cell>();
            emit.Convert<int>();
            emit.BranchIfGreater(slow);
            if (hotWrite) EmitHotTrail(emit, home, slow);
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

        if (hotFail)
        {
            emit.MarkLabel(other);
            EmitBranchIfTagIn(emit, c, NeverAListTags, failLabel);
            emit.Branch(slow);
            emit.MarkLabel(otherRef);
            EmitBranchIfTagIn(emit, d, NeverAListTags, failLabel);
        }
        emit.MarkLabel(slow);
        if (!EmitColdExit(emit))   // ADR-061: a hot method leaves for the cold one
        {
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
        }
        emit.MarkLabel(done);
        return true;
    }

    /// <summary>X[index] := the cell <paramref name="pushValue"/> leaves, for a
    /// constant index: below the bank's floor (<see
    /// cref="Activation.MinRegisterCount"/>) a plain store, with no capacity
    /// check and no slow path. The value must not grow the register bank.</summary>
    private static void EmitSetRegisterConst(IlEmit emit, int index, Action pushValue)
    {
        if (index >= 0 && index < Activation.MinRegisterCount && RegisterFileOf(emit) is not null)
        {
            EmitLoadEngineField(emit, EngRegisters);
            emit.LoadConstant(index);
            pushValue();
            emit.StoreElement<Cell>();
            return;
        }
        emit.LoadArgument(0);
        emit.LoadConstant(index);
        pushValue();
        EmitHelperCall(emit, EngineSetRegisterMethod);
    }

    /// <summary>Y[slot] pushed. With the frame in the method's locals, an
    /// element load at a constant offset from E.</summary>
    private static void EmitLoadY(IlEmit emit, int slot)
    {
        if (RegisterFileOf(emit) is { } rf && rf.Holds(MachineRegs.E | MachineRegs.StackArray))
        {
            EmitYAddress(emit, rf, slot);
            emit.LoadElement<Cell>();
            return;
        }
        emit.LoadArgument(0);
        emit.LoadConstant(slot);
        EmitHelperCall(emit, EngineGetYMethod);
    }

    /// <summary>Y[slot] := the cell <paramref name="pushValue"/> pushes, which
    /// must not move the frame stack: the array is loaded before it.</summary>
    private static void EmitStoreY(IlEmit emit, int slot, Action pushValue)
    {
        if (RegisterFileOf(emit) is { } rf && rf.Holds(MachineRegs.E | MachineRegs.StackArray))
        {
            EmitYAddress(emit, rf, slot);
            pushValue();
            emit.StoreElement<Cell>();
            return;
        }
        emit.LoadArgument(0);
        emit.LoadConstant(slot);
        pushValue();
        EmitHelperCall(emit, EngineSetYMethod);
    }

    private static void EmitYAddress(IlEmit emit, RegisterFile rf, int slot)
    {
        emit.LoadLocal(rf.Local(MachineRegs.StackArray));
        emit.LoadLocal(rf.Local(MachineRegs.E));
        emit.LoadConstant(Activation.EnvY1Offset + slot);
        emit.Add();
    }

    private static readonly MethodInfo EngineCellsIdenticalMethod =
        typeof(Activation).GetMethod(nameof(Activation.AreCellsIdentical))!;

    /// <summary><c>put_value Ya, A0; put_value Yb, A1; call_builtin ==/2</c> (or
    /// <c>\==/2</c>): the two slots compared as they are. The argument registers
    /// are written only when the code after the call may read them.</summary>
    private static bool TryEmitSlotIdentity(IlEmit emit, byte[] code, int pc,
        int end, IReadOnlyList<CallSite> callSites, IlLabel failLabel, out int next)
    {
        next = pc;
        if (RegisterFileOf(emit) is null) return false;
        int size = OpcodeTable.Get(Opcode.PutValueY).Size;
        int pc2 = pc + size, pc3 = pc2 + size;
        if (pc3 >= end || (Opcode)code[pc2] != Opcode.PutValueY || (Opcode)code[pc3] != Opcode.CallBuiltin)
            return false;
        if (BytecodeIO.ReadInt32(code, pc + 5) != 0 || BytecodeIO.ReadInt32(code, pc2 + 5) != 1) return false;
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(BytecodeIO.ReadInt32(code, pc3 + 1));
        if (entry.Arity != 2 || entry.Name is not ("==" or "\\==")) return false;
        int slotA = BytecodeIO.ReadInt32(code, pc + 1), slotB = BytecodeIO.ReadInt32(code, pc2 + 1);
        int after = pc3 + OpcodeTable.Get(Opcode.CallBuiltin).Size;

        var a = emit.DeclareLocal<Cell>($"ident_a_{NextLabelSeq()}");
        var b = emit.DeclareLocal<Cell>($"ident_b_{NextLabelSeq()}");
        EmitLoadY(emit, slotA);
        emit.StoreLocal(a);
        EmitLoadY(emit, slotB);
        emit.StoreLocal(b);
        if (!RegisterDeadAfter(code, after, end, 0, callSites))
            EmitSetRegisterConst(emit, 0, () => emit.LoadLocal(a));
        if (!RegisterDeadAfter(code, after, end, 1, callSites))
            EmitSetRegisterConst(emit, 1, () => emit.LoadLocal(b));
        emit.LoadArgument(0);
        emit.LoadLocal(a);
        emit.LoadLocal(b);
        EmitHelperCall(emit, EngineCellsIdenticalMethod);
        if (entry.Name == "==") emit.BranchIfFalse(failLabel);
        else emit.BranchIfTrue(failLabel);
        next = after;
        return true;
    }

    /// <summary>Whether no instruction from <paramref name="pc"/> on reads
    /// argument register <paramref name="reg"/> before writing it: a put into
    /// it, or a call or execute of lower arity (a call clobbers the registers
    /// above its arguments), or the end of the clause. Any other instruction
    /// counts as a read.</summary>
    private static bool RegisterDeadAfter(byte[] code, int pc, int end, int reg,
        IReadOnlyList<CallSite> callSites)
    {
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            switch (op)
            {
                case Opcode.PutValueY or Opcode.PutVariableY or Opcode.PutAtom or Opcode.PutInteger
                    or Opcode.PutFloat:
                    if (BytecodeIO.ReadInt32(code, pc + 5) == reg) return true;
                    break;
                case Opcode.PutNil:
                    if (BytecodeIO.ReadInt32(code, pc + 1) == reg) return true;
                    break;
                case Opcode.PutValueX:
                    if (BytecodeIO.ReadInt32(code, pc + 1) == reg) return false;
                    if (BytecodeIO.ReadInt32(code, pc + 5) == reg) return true;
                    break;
                case Opcode.PutVariableX:
                    if (BytecodeIO.ReadInt32(code, pc + 1) == reg || BytecodeIO.ReadInt32(code, pc + 5) == reg)
                        return true;
                    break;
                case Opcode.Call or Opcode.Execute:
                {
                    int fid = FindCallSiteFunctorId(callSites, pc);
                    return fid >= 0 && reg >= FunctorTable.Lookup(fid).Arity;
                }
                case Opcode.Proceed or Opcode.DeallocateProceed:
                    return true;
                case Opcode.Deallocate or Opcode.Allocate:
                    break;
                default:
                    return false;
            }
            pc += OpcodeTable.Get(op).Size;
        }
        return false;
    }

    /// <summary>SetRegister over the register array's field (or the region's
    /// local, when it holds it): the store when the index is in the bank, the
    /// method when the bank must grow. The stack holds [activation, index, cell].</summary>
    private static bool TryEmitSetRegister(IlEmit emit, RegisterFile rf)
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
        if (!EmitColdExit(emit))
        {
            emit.LoadLocal(act);
            emit.LoadLocal(idx);
            emit.LoadLocal(value);
            EmitRowCall(emit, rf, EngineSetRegisterMethod);
        }
        emit.MarkLabel(done);
        return true;
    }

    /// <summary>UnifyRegisterWithCell(reg, value), with the activation, the
    /// register and the cell on the stack: a register holding the value, or a
    /// reference to a plain unbound variable (bound here, with the trail when
    /// it is older than HB) or to the value. Anything else takes the helper.
    /// Leaves the result.</summary>
    private static bool TryEmitUnifyRegisterWithCell(IlEmit emit, RegisterFile rf)
    {
        if (!InlineUnify || !InlineUnifyRegisterWithCell) return false;
        // ADR-061: where a slow path calls anyway (no cold exit: an inlined
        // fact), one copy per method, entered by a branch and left by a switch.
        if (_cps is { } shared && !CpsHotExit)
        {
            shared.UrcValue ??= emit.DeclareLocal<Cell>("urc_shared_value");
            shared.UrcReg ??= emit.DeclareLocal<int>("urc_shared_reg");
            shared.UrcRet ??= emit.DeclareLocal<int>("urc_shared_ret");
            shared.UrcResult ??= emit.DeclareLocal<bool>("urc_shared_result");
            shared.UrcEntry ??= emit.DefineLabel("urc_shared");
            var back = emit.DefineLabel($"urc_back_{NextLabelSeq()}");
            emit.StoreLocal(shared.UrcValue);
            emit.StoreLocal(shared.UrcReg);
            emit.Pop();
            emit.LoadConstant(shared.UrcReturns.Count);
            emit.StoreLocal(shared.UrcRet);
            shared.UrcReturns.Add(back);
            emit.Branch(shared.UrcEntry);
            emit.MarkLabel(back);
            emit.LoadLocal(shared.UrcResult);
            return true;
        }
        EmitUnifyRegisterWithCellBody(emit, rf, sharedIn: null);
        return true;
    }

    /// <summary>The shared copy, after the method's code (see above).</summary>
    private static void EmitSharedUnifyRegisterWithCell(IlEmit emit, CpsEmitContext c)
    {
        if (c.UrcEntry is not { } entry || c.UrcReturns.Count == 0) return;
        var rf = RegisterFileOf(emit)!;
        emit.MarkLabel(entry);
        emit.LoadArgument(0);
        emit.LoadLocal(c.UrcReg!);
        emit.LoadLocal(c.UrcValue!);
        EmitUnifyRegisterWithCellBody(emit, rf, sharedIn: c);
        emit.StoreLocal(c.UrcResult!);
        emit.LoadLocal(c.UrcRet!);
        emit.Switch(c.UrcReturns.ToArray());
        emit.Branch(c.UrcReturns[0]);   // out of range: no site stores one
    }

    private static void EmitUnifyRegisterWithCellBody(IlEmit emit, RegisterFile rf,
        CpsEmitContext? sharedIn)
    {
        var value = rf.Temp(typeof(Cell), KU);
        var reg = rf.Temp(typeof(int), KU);
        var home = rf.Temp(typeof(int), KU + 1);
        var bits = rf.Temp(typeof(long), KU);
        var c = rf.Temp(typeof(long), KU + 1);
        var regs = rf.Temp(typeof(Cell[]), KU);
        var heap = rf.Temp(typeof(Cell[]), KU + 1);
        var bind = emit.DefineLabel($"urc_bind_{NextLabelSeq()}");
        var yes = emit.DefineLabel($"urc_yes_{NextLabelSeq()}");
        var slow = emit.DefineLabel($"urc_slow_{NextLabelSeq()}");
        var done = emit.DefineLabel($"urc_done_{NextLabelSeq()}");
        var trailed = emit.DefineLabel($"urc_trailed_{NextLabelSeq()}");
        // A hot method's slow path leaves for the cold method: two distinct
        // constants fail here instead. The test is off the paths of the same
        // constant and of a variable.
        bool hot = sharedIn is null && CpsHotExit;
        var other = hot ? emit.DefineLabel($"urc_other_{NextLabelSeq()}") : slow;

        emit.StoreLocal(value);
        emit.StoreLocal(reg);
        emit.Pop();
        emit.LoadLocal(value);
        emit.LoadField(CellDataField);
        emit.StoreLocal(bits);
        EmitLoadEngineField(emit, EngRegisters);
        emit.StoreLocal(regs);
        emit.LoadLocal(reg);
        emit.LoadLocal(regs);
        emit.LoadLength<Cell>();
        emit.Convert<int>();
        emit.BranchIfGreaterOrEqual(slow);
        // c := X[reg]; the value itself, or a reference followed one step.
        emit.LoadLocal(regs);
        emit.LoadLocal(reg);
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        emit.StoreLocal(c);
        emit.LoadLocal(c);
        emit.LoadLocal(bits);
        emit.BranchIfEqual(yes);
        EmitTagIs(emit, c, Tag.Ref);
        emit.BranchIfFalse(other);
        EmitLoadEngineField(emit, EngHeap);
        emit.StoreLocal(heap);
        // As get_list: a hot method follows the whole chain.
        var deref = emit.DefineLabel($"urc_deref_{NextLabelSeq()}");
        if (hot) emit.MarkLabel(deref);
        emit.LoadLocal(c);
        emit.Convert<int>();
        emit.StoreLocal(home);
        emit.LoadLocal(heap);
        emit.LoadLocal(home);
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        emit.StoreLocal(c);
        emit.LoadLocal(c);
        emit.LoadLocal(bits);
        emit.BranchIfEqual(yes);
        // A plain unbound variable is a Ref to itself.
        EmitTagIs(emit, c, Tag.Ref);
        emit.BranchIfFalse(other);
        emit.LoadLocal(c);
        emit.Convert<int>();
        emit.LoadLocal(home);
        emit.BranchIfEqual(bind);
        emit.Branch(hot ? deref : slow);

        // Bind(home, value): the store, and the trail when home is older than HB.
        emit.MarkLabel(bind);
        if (hot) EmitHotTrail(emit, home, slow);
        EmitHeapCell(emit, heap, home, 0, () => emit.LoadLocal(bits));
        if (!hot)
        {
            emit.LoadLocal(home);
            EmitLoadEngineField(emit, EngHb);
            emit.BranchIfGreaterOrEqual(trailed);
            emit.LoadArgument(0);
            emit.LoadLocal(home);
            EmitHelperCall(emit, EngineTrailBindMethod);
        }
        emit.MarkLabel(trailed);
        emit.MarkLabel(yes);
        emit.LoadConstant(true);
        emit.Branch(done);

        if (hot)
        {
            // c is neither the constant nor a reference: with an atom or a
            // small integer for the constant (it is its bits), a cell of
            // these tags is another term.
            var no = emit.DefineLabel($"urc_no_{NextLabelSeq()}");
            var immediate = emit.DefineLabel($"urc_imm_{NextLabelSeq()}");
            emit.MarkLabel(other);
            EmitBranchIfTagIn(emit, bits, ImmediateTags, immediate);
            emit.Branch(slow);
            emit.MarkLabel(immediate);
            EmitBranchIfTagIn(emit, c, NeverAnotherImmediateTags, no);
            emit.Branch(slow);
            emit.MarkLabel(no);
            emit.LoadConstant(false);
            emit.Branch(done);
        }

        emit.MarkLabel(slow);
        if (sharedIn is not null || !EmitColdExit(emit))
        {
            emit.LoadArgument(0);
            emit.LoadLocal(reg);
            emit.LoadLocal(value);
            EmitRowCall(emit, rf, EngineUnifyMethod);
        }
        emit.MarkLabel(done);
    }

    /// <summary>Pushes whether the tag of the raw bits in <paramref name="bits"/> is <paramref name="tag"/>.</summary>
    private static void EmitTagIs(IlEmit emit, IlLocal bits, Tag tag)
    {
        emit.LoadLocal(bits);
        emit.LoadConstant(Cell.TagShift);
        emit.UnsignedShiftRight();
        emit.LoadConstant((long)tag);
        emit.CompareEqual();
    }

    /// <summary>Ref(base + offset) as raw bits: the Ref tag is 0.</summary>
    private static void EmitRefBitsOf(IlEmit emit, IlLocal @base, int offset)
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
    private static void EmitHeapCell(IlEmit emit, IlLocal heap,
        IlLocal @base, int offset, Action pushBits)
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
    private static void EmitRegisterBits(IlEmit emit, IlLocal regs,
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
    private static void EmitRegisterFromHeap(IlEmit emit, IlLocal regs,
        IlLocal heap, int slot, IlLocal p, int offset, RegisterFile rf)
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
    private static void EmitSetUnifyMode(IlEmit emit, bool writeMode, Action pushPointer)
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
