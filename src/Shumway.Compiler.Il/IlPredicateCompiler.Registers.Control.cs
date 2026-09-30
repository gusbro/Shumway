using System;
using System.Reflection;
using Shumway.Core;

namespace Shumway.Compiler.Il;

// ADR-060 stage 2: the operations on the control registers, emitted over the
// region's locals instead of called. Each is the code of the activation
// method it replaces; the rare path of each (a cut that discards, a frame
// that does not fit, a hook) is the method.
public sealed partial class IlPredicateCompiler
{
    private static readonly MethodInfo EngineCallSafePointDueGetter =
        typeof(Activation).GetProperty(nameof(Activation.CallSafePointDue))!.GetGetMethod()!;

    private const int KSlot = 20, KValue = 21, KOldE = 22, KTopA = 23, KTopB = 24, KArg = 30;

    /// <summary>The operation <paramref name="method"/> stands for, emitted
    /// inline when the region holds what it touches. The stack holds the
    /// call's arguments; false leaves them for the call.</summary>
    private static bool TryEmitIntrinsic(
        Sigil.Emit<PredicateDelegate> emit, RegisterFile rf, MethodInfo method)
    {
        if (method == EngineSetRegisterMethod && !rf.Holds(MachineRegs.RegisterArray))
            return TryEmitSetRegister(emit, rf);
        if (method == EngineEGetter) return EmitRegisterRead(emit, rf, MachineRegs.E);
        if (method == EngineBGetter) return EmitRegisterRead(emit, rf, MachineRegs.B);
        if (method == EngineCpGetter) return EmitRegisterRead(emit, rf, MachineRegs.Cp);
        if (method == EngineHeapTopGetter) return EmitRegisterRead(emit, rf, MachineRegs.HeapTop);
        if (method == EngineBindingTrailTopGetter) return EmitRegisterRead(emit, rf, MachineRegs.TrailTop);
        if (method == EngineSetB0Method) return EmitRegisterWrite(emit, rf, MachineRegs.B0);
        if (method == EngineSetCpMethod) return EmitRegisterWrite(emit, rf, MachineRegs.Cp);
        if (method == EngineGetRegisterMethod && rf.Holds(MachineRegs.RegisterArray))
        {
            var idx = rf.Temp(typeof(int), KSlot);
            emit.StoreLocal(idx);
            emit.Pop();
            emit.LoadLocal(rf.Local(MachineRegs.RegisterArray));
            emit.LoadLocal(idx);
            emit.LoadElement<Cell>();
            return true;
        }
        const MachineRegs env = MachineRegs.E | MachineRegs.StackArray;
        if (method == EngineGetYMethod && rf.Holds(env))
        {
            var slot = rf.Temp(typeof(int), KSlot);
            emit.StoreLocal(slot);
            emit.Pop();
            EmitYIndex(emit, rf, slot);
            emit.LoadElement<Cell>();
            return true;
        }
        if (method == EngineSetYMethod && rf.Holds(env))
        {
            var slot = rf.Temp(typeof(int), KSlot);
            var value = rf.Temp(typeof(Cell), 0);
            emit.StoreLocal(value);
            emit.StoreLocal(slot);
            emit.Pop();
            EmitYIndex(emit, rf, slot);
            emit.LoadLocal(value);
            emit.StoreElement<Cell>();
            return true;
        }
        if ((method == EngineGetLevelMethod && rf.Holds(env | MachineRegs.B0))
            || (method == EngineGetLevelBMethod && rf.Holds(env | MachineRegs.B)))
        {
            // Y[slot] := RawInt(B0) (get_level) or RawInt(B) (get_level_b).
            var slot = rf.Temp(typeof(int), KSlot);
            emit.StoreLocal(slot);
            emit.Pop();
            var level = rf.Local(method == EngineGetLevelMethod ? MachineRegs.B0 : MachineRegs.B);
            EmitBuildRawInt(emit, rf, () => emit.LoadLocal(level));
            EmitYIndex(emit, rf, slot);
            emit.LoadLocal(rf.Temp(typeof(Cell), 0));
            emit.StoreElement<Cell>();
            return true;
        }
        if (method == EngineNeckCutMethod && rf.Holds(MachineRegs.B | MachineRegs.B0))
        {
            // Cut(B0) does nothing unless B0 < B.
            var skip = emit.DefineLabel($"neckcut_none_{NextLabelSeq()}");
            emit.LoadLocal(rf.Local(MachineRegs.B0));
            emit.LoadLocal(rf.Local(MachineRegs.B));
            emit.BranchIfGreaterOrEqual(skip);
            emit.Duplicate();
            EmitRowCall(emit, rf, method);
            emit.MarkLabel(skip);
            emit.Pop();
            return true;
        }
        if (method == EngineCutToLevelMethod && rf.Holds(env | MachineRegs.B))
        {
            // Cut(Y[slot]) does nothing unless the barrier is below B.
            var slot = rf.Temp(typeof(int), KSlot);
            var act = rf.Temp(typeof(Activation), 0);
            emit.StoreLocal(slot);
            emit.StoreLocal(act);
            var skip = emit.DefineLabel($"cut_none_{NextLabelSeq()}");
            EmitYIndex(emit, rf, slot);
            emit.LoadElement<Cell>();
            emit.LoadField(CellDataField);
            emit.Convert<int>();
            emit.LoadLocal(rf.Local(MachineRegs.B));
            emit.BranchIfGreaterOrEqual(skip);
            emit.LoadLocal(act);
            emit.LoadLocal(slot);
            EmitRowCall(emit, rf, method);
            emit.MarkLabel(skip);
            return true;
        }
        if (method == EngineDeallocateMethod
            && rf.Holds(env | MachineRegs.Cp | MachineRegs.B | MachineRegs.StackTop))
        {
            emit.Pop();
            EmitDeallocate(emit, rf);
            return true;
        }
        if (method == EngineFlushWakeupsForIlCutMethod)
            return EmitGuardedCall(emit, rf, method, EngineHasPendingWakeupsGetter, true);
        if (method == EngineWakeBoundaryCallMethod || method == EngineWakeBoundaryProceedMethod)
            return EmitGuardedCall(emit, rf, method, EngineHasPendingWakeupsGetter, 0);
        if (method == EngineMaybeCollectHeapAtCallMethod)
            return EmitGuardedCall(emit, rf, method, EngineCallSafePointDueGetter, null);
        return false;
    }

    private static bool EmitRegisterRead(
        Sigil.Emit<PredicateDelegate> emit, RegisterFile rf, MachineRegs reg)
    {
        if (!rf.Holds(reg)) return false;
        emit.Pop();
        emit.LoadLocal(rf.Local(reg));
        return true;
    }

    /// <summary>The stack holds [activation, value].</summary>
    private static bool EmitRegisterWrite(
        Sigil.Emit<PredicateDelegate> emit, RegisterFile rf, MachineRegs reg)
    {
        if (!rf.Holds(reg)) return false;
        emit.Duplicate();
        emit.StoreLocal(rf.Local(reg));
        emit.StoreField(RegisterFields[Index(reg)]!);
        return true;
    }

    /// <summary>reg := value, both the local and the field.</summary>
    private static void EmitStoreRegister(Sigil.Emit<PredicateDelegate> emit, RegisterFile rf,
        MachineRegs reg, Action pushValue)
    {
        emit.LoadArgument(0);
        pushValue();
        emit.Duplicate();
        emit.StoreLocal(rf.Local(reg));
        emit.StoreField(RegisterFields[Index(reg)]!);
    }

    /// <summary>The stack array and the index of Y[slot] of the current
    /// environment.</summary>
    private static void EmitYIndex(Sigil.Emit<PredicateDelegate> emit, RegisterFile rf, Sigil.Local slot)
    {
        emit.LoadLocal(rf.Local(MachineRegs.StackArray));
        emit.LoadLocal(rf.Local(MachineRegs.E));
        emit.LoadConstant(Activation.EnvY1Offset);
        emit.Add();
        emit.LoadLocal(slot);
        emit.Add();
    }

    /// <summary>(int)stack[index].Data, the index pushed by <paramref name="pushIndex"/>.</summary>
    private static void EmitStackInt(Sigil.Emit<PredicateDelegate> emit, RegisterFile rf, Action pushIndex)
    {
        emit.LoadLocal(rf.Local(MachineRegs.StackArray));
        pushIndex();
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        emit.Convert<int>();
    }

    /// <summary>The Cell scratch local := Cell.RawInt(value), the int value
    /// pushed by <paramref name="pushValue"/>.</summary>
    private static void EmitBuildRawInt(Sigil.Emit<PredicateDelegate> emit, RegisterFile rf, Action pushValue)
    {
        emit.LoadLocalAddress(rf.Temp(typeof(Cell), 0));
        pushValue();
        emit.Convert<long>();
        emit.LoadConstant(Cell.PayloadMask);
        emit.And();
        emit.LoadConstant(RawIntTag);
        emit.Or();
        emit.StoreField(CellDataField);
    }

    /// <summary>stack[base + offset] := Cell.RawInt(value).</summary>
    private static void EmitStoreStackRawInt(Sigil.Emit<PredicateDelegate> emit, RegisterFile rf,
        Sigil.Local @base, int offset, Action pushValue)
    {
        EmitBuildRawInt(emit, rf, pushValue);
        emit.LoadLocal(rf.Local(MachineRegs.StackArray));
        emit.LoadLocal(@base);
        if (offset != 0)
        {
            emit.LoadConstant(offset);
            emit.Add();
        }
        emit.LoadLocal(rf.Temp(typeof(Cell), 0));
        emit.StoreElement<Cell>();
    }

    /// <summary>allocate n: Activation.Allocate over the locals. The method
    /// runs when a hook is on or the frame does not fit.</summary>
    private static void EmitAllocateOp(Sigil.Emit<PredicateDelegate> emit, int n)
    {
        var rf = RegisterFileOf(emit);
        if (rf is null || !rf.Holds(MachineRegs.E | MachineRegs.Cp | MachineRegs.StackTop | MachineRegs.StackArray))
        {
            emit.LoadArgument(0);
            emit.LoadConstant(n);
            EmitHelperCall(emit, EngineAllocateMethod);
            return;
        }
        int size = Activation.EnvSize(n);
        var slow = emit.DefineLabel($"alloc_slow_{NextLabelSeq()}");
        var done = emit.DefineLabel($"alloc_done_{NextLabelSeq()}");
        var newE = rf.Temp(typeof(int), KOldE);

        emit.LoadField(FrameHooksField);
        emit.BranchIfTrue(slow);
        emit.LoadLocal(rf.Local(MachineRegs.StackTop));
        emit.StoreLocal(newE);
        emit.LoadLocal(newE);
        emit.LoadConstant(size);
        emit.Add();
        emit.LoadLocal(rf.Local(MachineRegs.StackArray));
        emit.LoadLength<Cell>();
        emit.Convert<int>();
        emit.BranchIfGreater(slow);

        EmitStoreStackRawInt(emit, rf, newE, Activation.EnvCeOffset, () => emit.LoadLocal(rf.Local(MachineRegs.E)));
        EmitStoreStackRawInt(emit, rf, newE, Activation.EnvCpOffset, () => emit.LoadLocal(rf.Local(MachineRegs.Cp)));
        EmitStoreStackRawInt(emit, rf, newE, Activation.EnvNOffset, () => emit.LoadConstant(n));
        // The Y slots are RawInt(0), which the heap collector's scan skips.
        for (int i = 0; i < n; i++)
            EmitStoreStackRawInt(emit, rf, newE, Activation.EnvY1Offset + i, () => emit.LoadConstant(0));
        EmitStoreRegister(emit, rf, MachineRegs.StackTop, () =>
        {
            emit.LoadLocal(newE);
            emit.LoadConstant(size);
            emit.Add();
        });
        EmitStoreRegister(emit, rf, MachineRegs.E, () => emit.LoadLocal(newE));
        emit.Branch(done);

        emit.MarkLabel(slow);
        emit.LoadArgument(0);
        emit.LoadConstant(n);
        EmitRowCall(emit, rf, EngineAllocateMethod);
        emit.MarkLabel(done);
    }

    /// <summary>deallocate: Activation.Deallocate over the locals, the
    /// reclaim of the popped frame's space included.</summary>
    private static void EmitDeallocate(Sigil.Emit<PredicateDelegate> emit, RegisterFile rf)
    {
        var oldE = rf.Temp(typeof(int), KOldE);
        var eTop = rf.Temp(typeof(int), KTopA);
        var bTop = rf.Temp(typeof(int), KTopB);
        var e = rf.Local(MachineRegs.E);
        var b = rf.Local(MachineRegs.B);
        var st = rf.Local(MachineRegs.StackTop);
        var done = emit.DefineLabel($"dealloc_done_{NextLabelSeq()}");
        var noE = emit.DefineLabel($"dealloc_noe_{NextLabelSeq()}");
        var noB = emit.DefineLabel($"dealloc_nob_{NextLabelSeq()}");
        var eBigger = emit.DefineLabel($"dealloc_etop_{NextLabelSeq()}");
        var clamp = emit.DefineLabel($"dealloc_clamp_{NextLabelSeq()}");
        var clamped = emit.DefineLabel($"dealloc_clamped_{NextLabelSeq()}");

        emit.LoadLocal(e);
        emit.StoreLocal(oldE);
        EmitStoreRegister(emit, rf, MachineRegs.Cp, () => EmitStackInt(emit, rf, () =>
        {
            emit.LoadLocal(oldE);
            emit.LoadConstant(Activation.EnvCpOffset);
            emit.Add();
        }));
        EmitStoreRegister(emit, rf, MachineRegs.E, () => EmitStackInt(emit, rf, () =>
        {
            emit.LoadLocal(oldE);
            emit.LoadConstant(Activation.EnvCeOffset);
            emit.Add();
        }));

        // The popped frame's space is free when no choice point sits at or
        // above it: reclaim down to the top of what the E and B chains keep.
        emit.LoadLocal(b);
        emit.LoadLocal(oldE);
        emit.BranchIfGreaterOrEqual(done);
        emit.LoadLocal(st);
        emit.LoadLocal(oldE);
        emit.BranchIfLessOrEqual(done);

        emit.LoadConstant(0);
        emit.StoreLocal(eTop);
        emit.LoadLocal(e);
        emit.LoadConstant(0);
        emit.BranchIfLess(noE);
        // eTop = E + EnvSize(max(N, 0))
        EmitStackInt(emit, rf, () =>
        {
            emit.LoadLocal(e);
            emit.LoadConstant(Activation.EnvNOffset);
            emit.Add();
        });
        emit.StoreLocal(bTop);
        emit.LoadLocal(bTop);
        emit.LoadConstant(0);
        emit.BranchIfGreaterOrEqual(clamp);
        emit.LoadConstant(0);
        emit.StoreLocal(bTop);
        emit.MarkLabel(clamp);
        emit.LoadLocal(e);
        emit.LoadConstant(Activation.EnvSize(0));
        emit.Add();
        emit.LoadLocal(bTop);
        emit.Add();
        emit.StoreLocal(eTop);
        emit.MarkLabel(noE);

        emit.LoadConstant(0);
        emit.StoreLocal(bTop);
        emit.LoadLocal(b);
        emit.LoadConstant(0);
        emit.BranchIfLess(noB);
        // bTop = B + CpSize(arity)
        emit.LoadLocal(b);
        emit.LoadConstant(Activation.CpSize(0));
        emit.Add();
        EmitStackInt(emit, rf, () =>
        {
            emit.LoadLocal(b);
            emit.LoadConstant(Activation.CpArityOffset);
            emit.Add();
        });
        emit.Add();
        emit.StoreLocal(bTop);
        emit.MarkLabel(noB);

        // live = max(eTop, bTop) into eTop; stack top = min(live, oldE)
        emit.LoadLocal(eTop);
        emit.LoadLocal(bTop);
        emit.BranchIfGreaterOrEqual(eBigger);
        emit.LoadLocal(bTop);
        emit.StoreLocal(eTop);
        emit.MarkLabel(eBigger);
        emit.LoadLocal(eTop);
        emit.LoadLocal(oldE);
        emit.BranchIfLess(clamped);
        emit.LoadLocal(oldE);
        emit.StoreLocal(eTop);
        emit.MarkLabel(clamped);
        EmitStoreRegister(emit, rf, MachineRegs.StackTop, () => emit.LoadLocal(eTop));
        emit.MarkLabel(done);
    }

    /// <summary>A call whose method has nothing to do unless
    /// <paramref name="guard"/> (a property of the activation that touches no
    /// register) says so: the test inline, the call and its reload behind
    /// it. <paramref name="skipped"/> is the method's result when the guard
    /// is false (null for a void method).</summary>
    private static bool EmitGuardedCall(Sigil.Emit<PredicateDelegate> emit, RegisterFile rf,
        MethodInfo method, MethodInfo guard, object? skipped)
    {
        var ps = method.GetParameters();
        var args = new Sigil.Local[ps.Length];
        for (int i = ps.Length - 1; i >= 0; i--)
        {
            args[i] = rf.Temp(ps[i].ParameterType, KArg + i);
            emit.StoreLocal(args[i]);
        }
        var act = rf.Temp(typeof(Activation), 0);
        emit.StoreLocal(act);
        var skip = emit.DefineLabel($"guarded_skip_{NextLabelSeq()}");
        var done = emit.DefineLabel($"guarded_done_{NextLabelSeq()}");
        emit.LoadLocal(act);
        emit.Call(guard);
        emit.BranchIfFalse(skip);
        emit.LoadLocal(act);
        foreach (var a in args) emit.LoadLocal(a);
        EmitRowCall(emit, rf, method);
        emit.Branch(done);
        emit.MarkLabel(skip);
        switch (skipped)
        {
            case bool v: emit.LoadConstant(v); break;
            case int v: emit.LoadConstant(v); break;
            case null: break;
            default: throw new ArgumentException($"no constant for {skipped.GetType()}");
        }
        emit.MarkLabel(done);
        return true;
    }
}
