using System.Collections.Generic;
using System.Reflection;
using Shumway.Core;

namespace Shumway.Compiler.Il;

// ADR-058 item 6 in the form of ADR-060 item 6: a region's choice point push
// and restore over the locals of its control registers, each emitted once per
// region. A push site branches to a ladder with one entry per arity (the
// entry for arity n stores argument n and falls into n - 1, then the control
// words) and returns through a switch. The restore common to retry and trust
// sits between the fail handler and the dispatch switch, where every
// resumption passes; a resume entry keeps the two or three stores that tell
// the two apart. The stores and loads are those of PushChoicePoint and
// RestoreCommonFromCurrentCp on the same frame layout. A hook or the
// debugger's trail-everything mode, read at the region's entry, and a stack
// that must grow take the methods.
public sealed partial class IlPredicateCompiler
{
    /// <summary>Choice point pushes and restores emitted over the region's
    /// locals, over every region compiled in this process.</summary>
    public static int InlineFrameSites;

    private static FieldInfo EngineField(string name) => typeof(Activation).GetField(name)!;

    private static readonly FieldInfo FrameHooksField = EngineField(nameof(Activation.FrameHooks));
    private static readonly FieldInfo EngTrailEverything = EngineField(nameof(Activation._trailEverything));
    private static readonly FieldInfo EngStack = EngineField(nameof(Activation._stack));
    private static readonly FieldInfo EngRegisters = EngineField(nameof(Activation._registers));
    private static readonly FieldInfo EngStackTop = EngineField(nameof(Activation._stackTop));
    private static readonly FieldInfo EngE = EngineField(nameof(Activation._e));
    private static readonly FieldInfo EngCp = EngineField(nameof(Activation._cp));
    private static readonly FieldInfo EngB = EngineField(nameof(Activation._b));
    private static readonly FieldInfo EngB0 = EngineField(nameof(Activation._b0));
    private static readonly FieldInfo EngHb = EngineField(nameof(Activation._hb));
    private static readonly FieldInfo EngHeapTop = EngineField(nameof(Activation._heapTop));
    private static readonly FieldInfo EngBindingTrailTop = EngineField(nameof(Activation._bindingTrailTop));
    private static readonly FieldInfo EngExtraTrailTop = EngineField(nameof(Activation._extraTrailTop));
    private static readonly FieldInfo EngViewGen = EngineField(nameof(Activation._currentViewGen));
    private static readonly MethodInfo EngineUnwindTrailsMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnwindTrails),
            new[] { typeof(int), typeof(int) })!;

    private const long RawIntTag = (long)Tag.RawInt << Cell.TagShift;

    // Offsets of the control words from the first of them, after the arity
    // cell and the saved arguments: the layout of Activation.Cp*Offset.
    private const int CtlCe = 0, CtlCp = 1, CtlB = 2, CtlBp = 3, CtlBindingTrail = 4,
        CtlExtraTrail = 5, CtlHeapTop = 6, CtlHb = 7, CtlViewGen = 8, CtlB0 = 9, CtlCount = 10;

    /// <summary>What the push sites, the ladder, the common restore and the
    /// resume entries of one region share.</summary>
    private sealed class FrameLocals
    {
        public Sigil.Local Slow = null!, Marker = null!, Arity = null!, Return = null!,
            Ctl = null!, Registers = null!, Index = null!, BindingTop = null!, ExtraTop = null!;
        public readonly Dictionary<int, Sigil.Label> Entries = new();
        public readonly List<Sigil.Label> Returns = new();
        public Sigil.Label Restore = null!;
        public int MaxArity = -1;
    }

    /// <summary>The value of an activation field; the region's local when
    /// the region holds that register (ADR-060).</summary>
    private static void EmitLoadEngineField(Sigil.Emit<PredicateDelegate> emit, FieldInfo field)
    {
        if (HeldLocalOf(emit, field) is { } local)
        {
            emit.LoadLocal(local);
            return;
        }
        emit.LoadArgument(0);
        emit.LoadField(field);
    }

    /// <summary>At the region's entry, when the region holds its control
    /// registers and pushes choice points: the frame locals, and whether the
    /// methods must run instead (a hook is on, or the activation trails
    /// everything, where HB is not the heap top).</summary>
    private static FrameLocals EmitFrameLocalsEntry(Sigil.Emit<PredicateDelegate> emit)
    {
        var l = new FrameLocals
        {
            Slow = emit.DeclareLocal<bool>("frame_slow"),
            Marker = emit.DeclareLocal<int>("frame_marker"),
            Arity = emit.DeclareLocal<int>("frame_arity"),
            Return = emit.DeclareLocal<int>("frame_return"),
            Ctl = emit.DeclareLocal<int>("frame_ctl"),
            Registers = emit.DeclareLocal<Cell[]>("frame_regs"),
            Index = emit.DeclareLocal<int>("frame_i"),
            BindingTop = emit.DeclareLocal<int>("frame_bt"),
            ExtraTop = emit.DeclareLocal<int>("frame_xt"),
            Restore = emit.DefineLabel($"frame_restore_{NextLabelSeq()}"),
        };
        emit.LoadField(FrameHooksField);
        emit.LoadArgument(0);
        emit.LoadField(EngTrailEverything);
        emit.Or();
        emit.StoreLocal(l.Slow);
        return l;
    }

    private static Sigil.Local Reg(Sigil.Emit<PredicateDelegate> emit, MachineRegs r)
        => RegisterFileOf(emit)!.Local(r);

    /// <summary>stack[from + offset] := a cell of the raw bits that
    /// <paramref name="pushBits"/> leaves on the stack (an int64). The cell is
    /// built in a local through its field: not ldelema, which makes the JIT
    /// reject a region, and not the constructor, one more call to inline.</summary>
    private static void EmitStoreCellBits(Sigil.Emit<PredicateDelegate> emit,
        Sigil.Local from, int offset, System.Action pushBits)
    {
        var rf = RegisterFileOf(emit)!;
        var cell = rf.Temp(typeof(Cell), 0);
        emit.LoadLocalAddress(cell);
        pushBits();
        emit.StoreField(CellDataField);
        emit.LoadLocal(rf.Local(MachineRegs.StackArray));
        emit.LoadLocal(from);
        emit.LoadConstant(offset);
        emit.Add();
        emit.LoadLocal(cell);
        emit.StoreElement<Cell>();
    }

    /// <summary>stack[from + offset] := Cell.RawInt(value), the int32 that
    /// <paramref name="pushValue"/> leaves on the stack.</summary>
    private static void EmitStoreCellInt(Sigil.Emit<PredicateDelegate> emit,
        Sigil.Local from, int offset, System.Action pushValue)
        => EmitStoreCellBits(emit, from, offset, () =>
        {
            pushValue();
            emit.Convert<long>();
            emit.LoadConstant(Cell.PayloadMask);
            emit.And();
            emit.LoadConstant(RawIntTag);
            emit.Or();
        });

    /// <summary>stack[from + offset].Data on the stack.</summary>
    private static void EmitLoadCellBits(Sigil.Emit<PredicateDelegate> emit, Sigil.Local from, int offset)
    {
        emit.LoadLocal(Reg(emit, MachineRegs.StackArray));
        emit.LoadLocal(from);
        emit.LoadConstant(offset);
        emit.Add();
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
    }

    private static void EmitLoadCellInt(Sigil.Emit<PredicateDelegate> emit, Sigil.Local from, int offset)
    {
        EmitLoadCellBits(emit, from, offset);
        emit.Convert<int>();
    }

    /// <summary>A push site: PushChoicePoint(arity, marker), the marker
    /// already in the frame locals. The frame is written by the ladder.</summary>
    private static void EmitFramePushSite(Sigil.Emit<PredicateDelegate> emit, FrameLocals l, int arity)
    {
        System.Threading.Interlocked.Increment(ref InlineFrameSites);
        var slow = emit.DefineLabel($"cpush_slow_{NextLabelSeq()}");
        var back = emit.DefineLabel($"cpush_back_{NextLabelSeq()}");
        if (!l.Entries.ContainsKey(arity))
            l.Entries[arity] = emit.DefineLabel($"cpush_entry{arity}_{NextLabelSeq()}");
        if (arity > l.MaxArity) l.MaxArity = arity;

        emit.LoadLocal(l.Slow);
        emit.BranchIfTrue(slow);
        // The frame must fit: growing the stack replaces the array.
        emit.LoadLocal(Reg(emit, MachineRegs.StackTop));
        emit.LoadConstant(Activation.CpSize(arity));
        emit.Add();
        emit.LoadLocal(Reg(emit, MachineRegs.StackArray));
        emit.LoadLength<Cell>();
        emit.Convert<int>();
        emit.BranchIfGreater(slow);
        if (arity > 0)
        {
            emit.LoadArgument(0);
            emit.LoadField(EngRegisters);
            emit.StoreLocal(l.Registers);
            emit.LoadConstant(arity);
            emit.LoadLocal(l.Registers);
            emit.LoadLength<Cell>();
            emit.Convert<int>();
            emit.BranchIfGreater(slow);
        }
        emit.LoadConstant(arity);
        emit.StoreLocal(l.Arity);
        emit.LoadConstant(l.Returns.Count);
        emit.StoreLocal(l.Return);
        l.Returns.Add(back);
        emit.Branch(l.Entries[arity]);

        emit.MarkLabel(slow);
        emit.LoadArgument(0);
        emit.LoadConstant(arity);
        emit.LoadLocal(l.Marker);
        EmitHelperCall(emit, EnginePushChoicePointMethod);
        emit.MarkLabel(back);
    }

    /// <summary>The push ladder, after the method's code: the entry for arity
    /// n stores argument n and falls into the entry for n - 1; the entry for 0
    /// writes the arity and the control words and returns to the site.</summary>
    private static void EmitFramePushLadder(Sigil.Emit<PredicateDelegate> emit, FrameLocals l)
    {
        if (l.Returns.Count == 0) return;
        var st = Reg(emit, MachineRegs.StackTop);
        var stack = Reg(emit, MachineRegs.StackArray);
        for (int n = l.MaxArity; n >= 0; n--)
        {
            if (!l.Entries.TryGetValue(n, out var entry))
                l.Entries[n] = entry = emit.DefineLabel($"cpush_entry{n}_{NextLabelSeq()}");
            emit.MarkLabel(entry);
            if (n == 0) break;
            emit.LoadLocal(stack);
            emit.LoadLocal(st);
            emit.LoadConstant(Activation.CpArg1Offset + n - 1);
            emit.Add();
            emit.LoadLocal(l.Registers);
            emit.LoadConstant(n - 1);
            emit.LoadElement<Cell>();
            emit.StoreElement<Cell>();
        }

        // The arity is not negative: its cell needs no mask.
        EmitStoreCellBits(emit, st, Activation.CpArityOffset, () =>
        {
            emit.LoadLocal(l.Arity);
            emit.Convert<long>();
            emit.LoadConstant(RawIntTag);
            emit.Or();
        });
        emit.LoadLocal(st);
        emit.LoadConstant(Activation.CpArg1Offset);
        emit.Add();
        emit.LoadLocal(l.Arity);
        emit.Add();
        emit.StoreLocal(l.Ctl);
        EmitStoreCellInt(emit, l.Ctl, CtlCe, () => emit.LoadLocal(Reg(emit, MachineRegs.E)));
        EmitStoreCellInt(emit, l.Ctl, CtlCp, () => emit.LoadLocal(Reg(emit, MachineRegs.Cp)));
        EmitStoreCellInt(emit, l.Ctl, CtlB, () => emit.LoadLocal(Reg(emit, MachineRegs.B)));
        EmitStoreCellInt(emit, l.Ctl, CtlBp, () => emit.LoadLocal(l.Marker));
        EmitStoreCellInt(emit, l.Ctl, CtlBindingTrail, () => EmitLoadEngineField(emit, EngBindingTrailTop));
        EmitStoreCellInt(emit, l.Ctl, CtlExtraTrail, () => EmitLoadEngineField(emit, EngExtraTrailTop));
        EmitStoreCellInt(emit, l.Ctl, CtlHeapTop, () => EmitLoadEngineField(emit, EngHeapTop));
        EmitStoreCellInt(emit, l.Ctl, CtlHb, () => EmitLoadEngineField(emit, EngHb));
        // The view generation is a 60-bit long, not an int.
        EmitStoreCellBits(emit, l.Ctl, CtlViewGen, () =>
        {
            EmitLoadEngineField(emit, EngViewGen);
            emit.LoadConstant(Cell.PayloadMask);
            emit.And();
            emit.LoadConstant(RawIntTag);
            emit.Or();
        });
        EmitStoreCellInt(emit, l.Ctl, CtlB0, () => emit.LoadLocal(Reg(emit, MachineRegs.B0)));

        var rf = RegisterFileOf(emit)!;
        EmitStoreRegister(emit, rf, MachineRegs.B, () => emit.LoadLocal(st));
        EmitStoreRegister(emit, rf, MachineRegs.StackTop, () =>
        {
            emit.LoadLocal(l.Ctl);
            emit.LoadConstant(CtlCount);
            emit.Add();
        });
        emit.LoadArgument(0);
        EmitLoadEngineField(emit, EngHeapTop);
        EmitStoreEngineField(emit, EngHb);

        emit.LoadLocal(l.Return);
        emit.Switch(l.Returns.ToArray());
        emit.Branch(l.Returns[0]);   // out of range: no site stores one
    }

    /// <summary>The restore common to retry and trust, reached from the fail
    /// handler and from an entry at a resume cursor, before the dispatch: the
    /// arguments, E, CP, the trails, the heap top, the view generation and B0
    /// from the frame on top. Leaves the base of its control words in
    /// <c>Ctl</c> for the resume entry. With the methods in force the resume
    /// entry calls them and this does nothing.</summary>
    private static void EmitFrameRestoreCommon(Sigil.Emit<PredicateDelegate> emit, FrameLocals l,
        Sigil.Label dispatch)
    {
        var b = Reg(emit, MachineRegs.B);
        var loop = emit.DefineLabel($"crest_loop_{NextLabelSeq()}");
        var loopTest = emit.DefineLabel($"crest_test_{NextLabelSeq()}");
        var unwind = emit.DefineLabel($"crest_unwind_{NextLabelSeq()}");
        var unwound = emit.DefineLabel($"crest_unwound_{NextLabelSeq()}");
        var rf = RegisterFileOf(emit)!;

        emit.MarkLabel(l.Restore);
        emit.LoadLocal(l.Slow);
        emit.BranchIfTrue(dispatch);
        EmitLoadCellInt(emit, b, Activation.CpArityOffset);
        emit.StoreLocal(l.Arity);
        emit.LoadLocal(b);
        emit.LoadConstant(Activation.CpArg1Offset);
        emit.Add();
        emit.LoadLocal(l.Arity);
        emit.Add();
        emit.StoreLocal(l.Ctl);

        // registers[i] := stack[B + 1 + i] for i < arity
        emit.LoadArgument(0);
        emit.LoadField(EngRegisters);
        emit.StoreLocal(l.Registers);
        emit.LoadConstant(0);
        emit.StoreLocal(l.Index);
        emit.Branch(loopTest);
        emit.MarkLabel(loop);
        emit.LoadLocal(l.Registers);
        emit.LoadLocal(l.Index);
        emit.LoadLocal(Reg(emit, MachineRegs.StackArray));
        emit.LoadLocal(b);
        emit.LoadConstant(Activation.CpArg1Offset);
        emit.Add();
        emit.LoadLocal(l.Index);
        emit.Add();
        emit.LoadElement<Cell>();
        emit.StoreElement<Cell>();
        emit.LoadLocal(l.Index);
        emit.LoadConstant(1);
        emit.Add();
        emit.StoreLocal(l.Index);
        emit.MarkLabel(loopTest);
        emit.LoadLocal(l.Index);
        emit.LoadLocal(l.Arity);
        emit.BranchIfLess(loop);

        EmitStoreRegister(emit, rf, MachineRegs.E, () => EmitLoadCellInt(emit, l.Ctl, CtlCe));
        EmitStoreRegister(emit, rf, MachineRegs.Cp, () => EmitLoadCellInt(emit, l.Ctl, CtlCp));

        // The unwind is a call, and only when something was trailed since
        // the push.
        EmitLoadCellInt(emit, l.Ctl, CtlBindingTrail);
        emit.StoreLocal(l.BindingTop);
        EmitLoadCellInt(emit, l.Ctl, CtlExtraTrail);
        emit.StoreLocal(l.ExtraTop);
        emit.LoadLocal(l.BindingTop);
        EmitLoadEngineField(emit, EngBindingTrailTop);
        emit.UnsignedBranchIfNotEqual(unwind);
        emit.LoadLocal(l.ExtraTop);
        EmitLoadEngineField(emit, EngExtraTrailTop);
        emit.BranchIfEqual(unwound);
        emit.MarkLabel(unwind);
        emit.LoadArgument(0);
        emit.LoadLocal(l.BindingTop);
        emit.LoadLocal(l.ExtraTop);
        EmitHelperCall(emit, EngineUnwindTrailsMethod);
        emit.MarkLabel(unwound);

        emit.LoadArgument(0);
        EmitLoadCellInt(emit, l.Ctl, CtlHeapTop);
        EmitStoreEngineField(emit, EngHeapTop);
        emit.LoadArgument(0);
        EmitLoadCellBits(emit, l.Ctl, CtlViewGen);
        emit.LoadConstant(Cell.PayloadMask);
        emit.And();
        emit.StoreField(EngViewGen);
        EmitStoreRegister(emit, rf, MachineRegs.B0, () => EmitLoadCellInt(emit, l.Ctl, CtlB0));
        emit.Branch(dispatch);
    }

    private static readonly FieldInfo EngBacktrackFloor = EngineField(nameof(Activation._backtrackFloor));
    private static readonly FieldInfo EngDebug = EngineField(nameof(Activation._debug));
    private static readonly FieldInfo EngCancelCountdown = EngineField(nameof(Activation._backtrackCancelCountdown));
    private static readonly FieldInfo ResumeMarkerPairsField =
        typeof(Activation).GetField(nameof(Activation._resumeMarkerPairs))!;
    private static readonly MethodInfo EngineBacktrackSafePointDueMethod =
        typeof(Activation).GetMethod(nameof(Activation.BacktrackSafePointDue))!;

    /// <summary>The fail handler's common case, before the call to
    /// TryResumeOwnChoicePoint: the choice point on top is this region's own,
    /// its BP a resume marker of the region. BP is read first: a choice point
    /// of the IL side stack has BP -1, never a marker, so the side stack need
    /// not be looked at. Reaches <paramref name="resumeCheck"/> with the
    /// marker's cursor in <paramref name="cur"/>, or <paramref name="failOut"/>
    /// when the top is another code's marker; anything else (a hook or a debug
    /// session on, the floor, BP not a marker) falls through to the call.</summary>
    private static void EmitResumeOwnFast(Sigil.Emit<PredicateDelegate> emit, FrameLocals l,
        int regionFid, Sigil.Local cur, Sigil.Label resumeCheck, Sigil.Label failOut)
    {
        var rf = RegisterFileOf(emit)!;
        var b = Reg(emit, MachineRegs.B);
        var bp = rf.Temp(typeof(int), 70);
        var pair = rf.Temp(typeof((int, int)), 0);
        var countdown = rf.Temp(typeof(int), 71);
        var callMethod = emit.DefineLabel($"rfail_call_{NextLabelSeq()}");
        var safe = emit.DefineLabel($"rfail_safe_{NextLabelSeq()}");

        emit.LoadLocal(l.Slow);
        emit.BranchIfTrue(callMethod);
        emit.LoadLocal(b);
        emit.LoadArgument(0);
        emit.LoadField(EngBacktrackFloor);
        emit.BranchIfLessOrEqual(callMethod);
        emit.LoadArgument(0);
        emit.LoadField(EngDebug);
        emit.BranchIfTrue(callMethod);
        // BP of the frame on top: B + 1 + arity + CtlBp.
        EmitLoadCellInt(emit, b, Activation.CpArityOffset);
        emit.StoreLocal(l.Arity);
        emit.LoadLocal(Reg(emit, MachineRegs.StackArray));
        emit.LoadLocal(b);
        emit.LoadConstant(Activation.CpArg1Offset + CtlBp);
        emit.Add();
        emit.LoadLocal(l.Arity);
        emit.Add();
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        emit.Convert<int>();
        emit.StoreLocal(bp);
        emit.LoadLocal(bp);
        emit.LoadConstant(Activation.ResumeMarkerBase);
        emit.BranchIfLess(callMethod);
        // The marker's (functor, cursor).
        emit.LoadField(ResumeMarkerPairsField);
        emit.LoadLocal(bp);
        emit.LoadConstant(Activation.ResumeMarkerBase);
        emit.Subtract();
        emit.LoadElement<(int, int)>();
        emit.StoreLocal(pair);
        emit.LoadLocal(pair);
        emit.LoadField(typeof((int, int)).GetField("Item1")!);
        EmitFunctorId(emit, regionFid);
        emit.UnsignedBranchIfNotEqual(failOut);
        // BacktrackSafePoint: the countdown here, the rest when it runs out.
        emit.LoadArgument(0);
        emit.LoadField(EngCancelCountdown);
        emit.LoadConstant(1);
        emit.Subtract();
        emit.StoreLocal(countdown);
        emit.LoadArgument(0);
        emit.LoadLocal(countdown);
        emit.StoreField(EngCancelCountdown);
        emit.LoadLocal(countdown);
        emit.LoadConstant(0);
        emit.BranchIfGreater(safe);
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineBacktrackSafePointDueMethod);
        emit.MarkLabel(safe);
        emit.LoadLocal(pair);
        emit.LoadField(typeof((int, int)).GetField("Item2")!);
        emit.StoreLocal(cur);
        emit.Branch(resumeCheck);
        emit.MarkLabel(callMethod);
    }

    /// <summary>A resume entry after the common restore: RetryMeElse's tail
    /// when <paramref name="nextMarker"/> is given (HB to the heap top, BP to
    /// the next alternative), TrustMe's otherwise (HB and B as before the
    /// push, the frame popped).</summary>
    private static void EmitFrameResumeTail(Sigil.Emit<PredicateDelegate> emit, FrameLocals l,
        System.Action? nextMarker)
    {
        System.Threading.Interlocked.Increment(ref InlineFrameSites);
        var slow = emit.DefineLabel($"crest_slow_{NextLabelSeq()}");
        var done = emit.DefineLabel($"crest_done_{NextLabelSeq()}");
        var rf = RegisterFileOf(emit)!;
        var b = Reg(emit, MachineRegs.B);

        emit.LoadLocal(l.Slow);
        emit.BranchIfTrue(slow);
        if (nextMarker is not null)
        {
            emit.LoadArgument(0);
            EmitLoadEngineField(emit, EngHeapTop);
            EmitStoreEngineField(emit, EngHb);
            EmitStoreCellInt(emit, l.Ctl, CtlBp, nextMarker);
        }
        else
        {
            emit.LoadArgument(0);
            EmitLoadCellInt(emit, l.Ctl, CtlHb);
            EmitStoreEngineField(emit, EngHb);
            EmitStoreRegister(emit, rf, MachineRegs.StackTop, () => emit.LoadLocal(b));
            EmitStoreRegister(emit, rf, MachineRegs.B, () => EmitLoadCellInt(emit, l.Ctl, CtlB));
        }
        emit.Branch(done);

        emit.MarkLabel(slow);
        emit.LoadArgument(0);
        if (nextMarker is not null)
        {
            nextMarker();
            EmitHelperCall(emit, EngineRetryMeElseMethod);
        }
        else
        {
            EmitHelperCall(emit, EngineTrustMeMethod);
        }
        emit.MarkLabel(done);
    }
}
