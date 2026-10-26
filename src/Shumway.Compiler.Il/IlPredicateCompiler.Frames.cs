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
//
// The frame is addressed through a reference to its first cell (ldelema,
// one bounds check), the words at constant offsets from it: an index into
// the stack array per word costs a bounds check and the address again, and
// the JIT proves none of them from the headroom check, since the ladder's
// control words sit at an offset that is the arity, a local. The reference
// is taken after the headroom check and held across no call that can grow
// the stack (UnwindTrails and DropUndoneWakes do not).
public sealed partial class IlPredicateCompiler
{
    /// <summary>Choice point pushes and restores emitted over the region's
    /// locals, over every region compiled in this process.</summary>
    public static int InlineFrameSites;

    /// <summary>The frames a region writes and reads inline addressed through
    /// a reference to the frame (one bounds check per frame), as are the
    /// environments of allocate and deallocate; off, every word indexes the
    /// stack array. An off switch for measuring it.</summary>
    internal static bool InlineFrameRefs { get; set; } =
        System.Environment.GetEnvironmentVariable("SHUMWAY_IL_FRAME_REFS") != "0";

    /// <summary>Regions whose frames were emitted through references, over
    /// every region compiled in this process.</summary>
    public static int InlineFrameRefRegions;

    private static readonly System.Type CellRefType = typeof(Cell).MakeByRefType();

    /// <summary>Regions compiled in this process whose markers form one run
    /// (a return or a fail decodes its own by subtraction), and those whose
    /// markers were interned apart before (the pairs table decodes).</summary>
    public static int RegionMarkerRuns, RegionMarkerRunMisses;

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
    private static readonly MethodInfo EngineDropUndoneWakesMethod =
        typeof(Activation).GetMethod(nameof(Activation.DropUndoneWakes))!;
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
        public IlLocal Slow = null!, Marker = null!, Arity = null!, Return = null!,
            Registers = null!, Index = null!, BindingTop = null!, ExtraTop = null!;
        /// <summary>The index of the frame's first control word, when the
        /// words index the stack array.</summary>
        public IlLocal? Ctl;
        /// <summary>References (<c>Cell&amp;</c>) to the frame on top, to its
        /// first control word and to the register bank's first cell, with
        /// <see cref="InlineFrameRefs"/>.</summary>
        public IlLocal? Frame, CtlRef, Regs;
        public readonly Dictionary<int, IlLabel> Entries = new();
        public readonly Dictionary<int, IlLabel> Slows = new();
        public readonly List<IlLabel> Returns = new();
        public IlLabel? Dispatch;
        public IlLabel Restore = null!;
        public int MaxArity = -1;
    }

    /// <summary>The value of an activation field; the region's local when
    /// the region holds that register (ADR-060).</summary>
    private static void EmitLoadEngineField(IlEmit emit, FieldInfo field)
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
    private static FrameLocals EmitFrameLocalsEntry(IlEmit emit)
    {
        var l = new FrameLocals
        {
            Slow = emit.DeclareLocal<bool>("frame_slow"),
            Marker = emit.DeclareLocal<int>("frame_marker"),
            Arity = emit.DeclareLocal<int>("frame_arity"),
            Return = emit.DeclareLocal<int>("frame_return"),
            Registers = emit.DeclareLocal<Cell[]>("frame_regs"),
            Index = emit.DeclareLocal<int>("frame_i"),
            BindingTop = emit.DeclareLocal<int>("frame_bt"),
            ExtraTop = emit.DeclareLocal<int>("frame_xt"),
            Restore = emit.DefineLabel($"frame_restore_{NextLabelSeq()}"),
        };
        if (InlineFrameRefs)
        {
            System.Threading.Interlocked.Increment(ref InlineFrameRefRegions);
            l.Frame = emit.DeclareLocal(CellRefType, "frame_ref");
            l.CtlRef = emit.DeclareLocal(CellRefType, "frame_ctlref");
            l.Regs = emit.DeclareLocal(CellRefType, "frame_regsref");
        }
        else
        {
            l.Ctl = emit.DeclareLocal<int>("frame_ctl");
        }
        emit.LoadField(FrameHooksField);
        emit.LoadArgument(0);
        emit.LoadField(EngTrailEverything);
        emit.Or();
        emit.StoreLocal(l.Slow);
        return l;
    }

    private static IlLocal Reg(IlEmit emit, MachineRegs r)
        => RegisterFileOf(emit)!.Local(r);

    /// <summary>stack[from + offset] := a cell of the raw bits that
    /// <paramref name="pushBits"/> leaves on the stack (an int64). The cell is
    /// built in a local through its field: not ldelema, which makes the JIT
    /// reject a region, and not the constructor, one more call to inline.</summary>
    private static void EmitStoreCellBits(IlEmit emit,
        IlLocal from, int offset, System.Action pushBits)
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
    private static void EmitStoreCellInt(IlEmit emit,
        IlLocal from, int offset, System.Action pushValue)
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
    private static void EmitLoadCellBits(IlEmit emit, IlLocal from, int offset)
    {
        emit.LoadLocal(Reg(emit, MachineRegs.StackArray));
        emit.LoadLocal(from);
        emit.LoadConstant(offset);
        emit.Add();
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
    }

    private static void EmitLoadCellInt(IlEmit emit, IlLocal from, int offset)
    {
        EmitLoadCellBits(emit, from, offset);
        emit.Convert<int>();
    }

    /// <summary>The reference <paramref name="cells"/> cells past
    /// <paramref name="byref"/> (a <c>Cell&amp;</c> local) on the stack.</summary>
    private static void EmitRefOffset(IlEmit emit, IlLocal byref, int cells)
    {
        emit.LoadLocal(byref);
        if (cells != 0)
        {
            emit.LoadConstant(cells * 8);
            emit.Add();
        }
    }

    /// <summary>byref[offset].Data := the raw bits <paramref name="pushBits"/>
    /// leaves on the stack (an int64), through the reference.</summary>
    private static void EmitStoreRefBits(IlEmit emit, IlLocal byref, int offset, System.Action pushBits)
    {
        EmitRefOffset(emit, byref, offset);
        pushBits();
        emit.StoreField(CellDataField);
    }

    /// <summary>byref[offset] := Cell.RawInt(value), the int32 that
    /// <paramref name="pushValue"/> leaves on the stack.</summary>
    private static void EmitStoreRefInt(IlEmit emit, IlLocal byref, int offset, System.Action pushValue)
        => EmitStoreRefBits(emit, byref, offset, () =>
        {
            pushValue();
            emit.Convert<long>();
            emit.LoadConstant(Cell.PayloadMask);
            emit.And();
            emit.LoadConstant(RawIntTag);
            emit.Or();
        });

    /// <summary>byref[offset].Data on the stack.</summary>
    private static void EmitLoadRefBits(IlEmit emit, IlLocal byref, int offset)
    {
        EmitRefOffset(emit, byref, offset);
        emit.LoadField(CellDataField);
    }

    /// <summary>The reference to <c>array[index]</c> (one bounds check) into
    /// <paramref name="byref"/>.</summary>
    private static void EmitTakeRef(IlEmit emit, IlLocal array, System.Action pushIndex, IlLocal byref)
    {
        emit.LoadLocal(array);
        pushIndex();
        emit.LoadElementAddress(typeof(Cell));
        emit.StoreLocal(byref);
    }

    // The control word at offset of the frame the restore or the ladder
    // addressed: through the reference, or stack[Ctl + offset].
    private static void EmitStoreCtlInt(IlEmit emit, FrameLocals l, int offset, System.Action pushValue)
    {
        if (l.CtlRef is { } r) EmitStoreRefInt(emit, r, offset, pushValue);
        else EmitStoreCellInt(emit, l.Ctl!, offset, pushValue);
    }

    private static void EmitStoreCtlBits(IlEmit emit, FrameLocals l, int offset, System.Action pushBits)
    {
        if (l.CtlRef is { } r) EmitStoreRefBits(emit, r, offset, pushBits);
        else EmitStoreCellBits(emit, l.Ctl!, offset, pushBits);
    }

    private static void EmitLoadCtlBits(IlEmit emit, FrameLocals l, int offset)
    {
        if (l.CtlRef is { } r) EmitLoadRefBits(emit, r, offset);
        else EmitLoadCellBits(emit, l.Ctl!, offset);
    }

    private static void EmitLoadCtlInt(IlEmit emit, FrameLocals l, int offset)
    {
        EmitLoadCtlBits(emit, l, offset);
        emit.Convert<int>();
    }

    /// <summary>Ctl, the frame's first control word, from the frame's base
    /// (<paramref name="frameIndex"/> or <see cref="FrameLocals.Frame"/>) and
    /// the arity in its local: base + CpArg1Offset + arity.</summary>
    private static void EmitSetCtl(IlEmit emit, FrameLocals l, IlLocal frameIndex)
    {
        if (l.CtlRef is { } r)
        {
            emit.LoadLocal(l.Frame!);
            emit.LoadLocal(l.Arity);
            emit.LoadConstant(Activation.CpArg1Offset);
            emit.Add();
            emit.LoadConstant(8);
            emit.Multiply();
            emit.Add();
            emit.StoreLocal(r);
            return;
        }
        emit.LoadLocal(frameIndex);
        emit.LoadConstant(Activation.CpArg1Offset);
        emit.Add();
        emit.LoadLocal(l.Arity);
        emit.Add();
        emit.StoreLocal(l.Ctl!);
    }

    /// <summary>The arity of the frame at <paramref name="frameIndex"/> into
    /// its local; with references, the frame's reference first.</summary>
    private static void EmitReadArity(IlEmit emit, FrameLocals l, IlLocal frameIndex)
    {
        if (l.Frame is { } f)
        {
            EmitTakeRef(emit, Reg(emit, MachineRegs.StackArray), () => emit.LoadLocal(frameIndex), f);
            EmitLoadRefBits(emit, f, Activation.CpArityOffset);
            emit.Convert<int>();
        }
        else
        {
            EmitLoadCellInt(emit, frameIndex, Activation.CpArityOffset);
        }
        emit.StoreLocal(l.Arity);
    }

    /// <summary>A push site: PushChoicePoint(arity, marker), the marker
    /// already in the frame locals. The frame is written by the ladder.</summary>
    private static void EmitFramePushSite(IlEmit emit, FrameLocals l, int arity)
    {
        System.Threading.Interlocked.Increment(ref InlineFrameSites);
        var back = emit.DefineLabel($"cpush_back_{NextLabelSeq()}");
        if (!l.Entries.ContainsKey(arity))
            l.Entries[arity] = emit.DefineLabel($"cpush_entry{arity}_{NextLabelSeq()}");
        // The slow path is the arity's, after the method's code
        // (EmitFramePushLadder); both paths return here through Return.
        if (!l.Slows.TryGetValue(arity, out var slow))
            l.Slows[arity] = slow = emit.DefineLabel($"cpush_slow{arity}_{NextLabelSeq()}");
        if (arity > l.MaxArity) l.MaxArity = arity;

        emit.LoadConstant(l.Returns.Count);
        emit.StoreLocal(l.Return);
        l.Returns.Add(back);
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
        // The frame's reference, after the check: growing replaces the array.
        if (l.Frame is { } frame)
            EmitTakeRef(emit, Reg(emit, MachineRegs.StackArray), () => emit.LoadLocal(Reg(emit, MachineRegs.StackTop)), frame);
        if (arity > 0)
        {
            emit.LoadArgument(0);
            emit.LoadField(EngRegisters);
            emit.StoreLocal(l.Registers);
            // The bank holds at least MinRegisterCount registers.
            if (arity > Activation.MinRegisterCount)
            {
                emit.LoadConstant(arity);
                emit.LoadLocal(l.Registers);
                emit.LoadLength<Cell>();
                emit.Convert<int>();
                emit.BranchIfGreater(slow);
            }
            if (l.Regs is { } regs)
                EmitTakeRef(emit, l.Registers, () => emit.LoadConstant(0), regs);
        }
        emit.LoadConstant(arity);
        emit.StoreLocal(l.Arity);
        emit.Branch(l.Entries[arity]);
        emit.MarkLabel(back);
    }

    /// <summary>The push ladder, after the method's code: the entry for arity
    /// n stores argument n and falls into the entry for n - 1; the entry for 0
    /// writes the arity and the control words and returns to the site.</summary>
    private static void EmitFramePushLadder(IlEmit emit, FrameLocals l)
    {
        if (l.Returns.Count == 0) return;
        var st = Reg(emit, MachineRegs.StackTop);
        var stack = Reg(emit, MachineRegs.StackArray);
        l.Dispatch = emit.DefineLabel($"cpush_dispatch_{NextLabelSeq()}");
        foreach (var (arity, slow) in l.Slows)
        {
            emit.MarkLabel(slow);
            emit.LoadArgument(0);
            emit.LoadConstant(arity);
            emit.LoadLocal(l.Marker);
            EmitHelperCall(emit, EnginePushChoicePointMethod);
            emit.Branch(l.Dispatch!);
        }
        for (int n = l.MaxArity; n >= 0; n--)
        {
            if (!l.Entries.TryGetValue(n, out var entry))
                l.Entries[n] = entry = emit.DefineLabel($"cpush_entry{n}_{NextLabelSeq()}");
            emit.MarkLabel(entry);
            if (n == 0) break;
            if (l.Frame is { } frame)
            {
                EmitRefOffset(emit, frame, Activation.CpArg1Offset + n - 1);
                EmitLoadRefBits(emit, l.Regs!, n - 1);
                emit.StoreField(CellDataField);
                continue;
            }
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
        void PushArityBits()
        {
            emit.LoadLocal(l.Arity);
            emit.Convert<long>();
            emit.LoadConstant(RawIntTag);
            emit.Or();
        }
        if (l.Frame is { } frameRef) EmitStoreRefBits(emit, frameRef, Activation.CpArityOffset, PushArityBits);
        else EmitStoreCellBits(emit, st, Activation.CpArityOffset, PushArityBits);
        EmitSetCtl(emit, l, st);
        EmitStoreCtlInt(emit, l, CtlCe, () => emit.LoadLocal(Reg(emit, MachineRegs.E)));
        EmitStoreCtlInt(emit, l, CtlCp, () => emit.LoadLocal(Reg(emit, MachineRegs.Cp)));
        EmitStoreCtlInt(emit, l, CtlB, () => emit.LoadLocal(Reg(emit, MachineRegs.B)));
        EmitStoreCtlInt(emit, l, CtlBp, () => emit.LoadLocal(l.Marker));
        EmitStoreCtlInt(emit, l, CtlBindingTrail, () => EmitLoadEngineField(emit, EngBindingTrailTop));
        EmitStoreCtlInt(emit, l, CtlExtraTrail, () => EmitLoadEngineField(emit, EngExtraTrailTop));
        EmitStoreCtlInt(emit, l, CtlHeapTop, () => EmitLoadEngineField(emit, EngHeapTop));
        EmitStoreCtlInt(emit, l, CtlHb, () => EmitLoadEngineField(emit, EngHb));
        // The view generation is a 60-bit long, not an int.
        EmitStoreCtlBits(emit, l, CtlViewGen, () =>
        {
            EmitLoadEngineField(emit, EngViewGen);
            emit.LoadConstant(Cell.PayloadMask);
            emit.And();
            emit.LoadConstant(RawIntTag);
            emit.Or();
        });
        EmitStoreCtlInt(emit, l, CtlB0, () => emit.LoadLocal(Reg(emit, MachineRegs.B0)));

        var rf = RegisterFileOf(emit)!;
        EmitStoreRegister(emit, rf, MachineRegs.B, () => emit.LoadLocal(st));
        // The frame's size: CpSize(arity) = CpArg1Offset + arity + CtlCount.
        EmitStoreRegister(emit, rf, MachineRegs.StackTop, () =>
        {
            emit.LoadLocal(st);
            emit.LoadLocal(l.Arity);
            emit.Add();
            emit.LoadConstant(Activation.CpArg1Offset + CtlCount);
            emit.Add();
        });
        emit.LoadArgument(0);
        EmitLoadEngineField(emit, EngHeapTop);
        EmitStoreEngineField(emit, EngHb);

        emit.MarkLabel(l.Dispatch!);
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
    private static void EmitFrameRestoreCommon(IlEmit emit, FrameLocals l,
        IlLabel dispatch)
    {
        emit.MarkLabel(l.Restore);
        emit.LoadLocal(l.Slow);
        emit.BranchIfTrue(dispatch);
        EmitFrameRestoreBody(emit, l);
        emit.Branch(dispatch);
    }

    /// <summary>The common restore itself, from the frame on top, for any
    /// arity; leaves the base of its control words in <c>Ctl</c>.</summary>
    private static void EmitFrameRestoreBody(IlEmit emit, FrameLocals l)
    {
        var b = Reg(emit, MachineRegs.B);
        var loop = emit.DefineLabel($"crest_loop_{NextLabelSeq()}");
        var loopTest = emit.DefineLabel($"crest_test_{NextLabelSeq()}");
        var unwind = emit.DefineLabel($"crest_unwind_{NextLabelSeq()}");
        var unwound = emit.DefineLabel($"crest_unwound_{NextLabelSeq()}");
        var rf = RegisterFileOf(emit)!;

        EmitReadArity(emit, l, b);
        EmitSetCtl(emit, l, b);

        // registers[i] := stack[B + 1 + i] for i < arity
        emit.LoadArgument(0);
        emit.LoadField(EngRegisters);
        emit.StoreLocal(l.Registers);
        emit.LoadConstant(0);
        emit.StoreLocal(l.Index);
        if (l.Frame is { } frame)
        {
            // Two references walk the arguments: the bank's first cell (the
            // bank holds at least MinRegisterCount) and the frame's.
            var src = rf.Temp(CellRefType, 0);
            EmitTakeRef(emit, l.Registers, () => emit.LoadConstant(0), l.Regs!);
            EmitRefOffset(emit, frame, Activation.CpArg1Offset);
            emit.StoreLocal(src);
            emit.Branch(loopTest);
            emit.MarkLabel(loop);
            emit.LoadLocal(l.Regs!);
            EmitLoadRefBits(emit, src, 0);
            emit.StoreField(CellDataField);
            EmitRefOffset(emit, l.Regs!, 1);
            emit.StoreLocal(l.Regs!);
            EmitRefOffset(emit, src, 1);
            emit.StoreLocal(src);
        }
        else
        {
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
        }
        emit.LoadLocal(l.Index);
        emit.LoadConstant(1);
        emit.Add();
        emit.StoreLocal(l.Index);
        emit.MarkLabel(loopTest);
        emit.LoadLocal(l.Index);
        emit.LoadLocal(l.Arity);
        emit.BranchIfLess(loop);

        EmitStoreRegister(emit, rf, MachineRegs.E, () => EmitLoadCtlInt(emit, l, CtlCe));
        EmitStoreRegister(emit, rf, MachineRegs.Cp, () => EmitLoadCtlInt(emit, l, CtlCp));

        // The unwind is a call, and only when something was trailed since
        // the push.
        EmitLoadCtlInt(emit, l, CtlBindingTrail);
        emit.StoreLocal(l.BindingTop);
        EmitLoadCtlInt(emit, l, CtlExtraTrail);
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
        EmitLoadCtlInt(emit, l, CtlHeapTop);
        EmitStoreEngineField(emit, EngHeapTop);
        // A wake whose binding the unwind undid goes before anything is
        // allocated over its home (ADR-049 §8): a call, only when one pends.
        var noWakes = emit.DefineLabel($"crest_nowakes_{NextLabelSeq()}");
        emit.LoadArgument(0);
        EmitHasPendingWakeups(emit);
        emit.BranchIfFalse(noWakes);
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineDropUndoneWakesMethod);
        emit.MarkLabel(noWakes);
        emit.LoadArgument(0);
        EmitLoadCtlBits(emit, l, CtlViewGen);
        emit.LoadConstant(Cell.PayloadMask);
        emit.And();
        emit.StoreField(EngViewGen);
        EmitStoreRegister(emit, rf, MachineRegs.B0, () => EmitLoadCtlInt(emit, l, CtlB0));
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
    /// session on, the floor, BP not a marker) falls through to the call. A
    /// marker of the region's own run (<paramref name="emitRunBase"/>, the base,
    /// <paramref name="runCount"/> cursors from 1) gives its cursor by
    /// subtraction; the pairs table decodes the rest.</summary>
    private static void EmitResumeOwnFast(IlEmit emit, FrameLocals l,
        int regionFid, Action<IlEmit> emitRunBase, int runCount,
        IlLocal cur, IlLabel resumeCheck, IlLabel failOut)
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
        EmitReadArity(emit, l, b);
        if (l.Frame is { } frame)
        {
            emit.LoadLocal(frame);
            emit.LoadLocal(l.Arity);
            emit.LoadConstant(Activation.CpArg1Offset + CtlBp);
            emit.Add();
            emit.LoadConstant(8);
            emit.Multiply();
            emit.Add();
            emit.LoadField(CellDataField);
        }
        else
        {
            emit.LoadLocal(Reg(emit, MachineRegs.StackArray));
            emit.LoadLocal(b);
            emit.LoadConstant(Activation.CpArg1Offset + CtlBp);
            emit.Add();
            emit.LoadLocal(l.Arity);
            emit.Add();
            emit.LoadElement<Cell>();
            emit.LoadField(CellDataField);
        }
        emit.Convert<int>();
        emit.StoreLocal(bp);
        emit.LoadLocal(bp);
        emit.LoadConstant(Activation.ResumeMarkerBase);
        emit.BranchIfLess(callMethod);
        // The region's own run (the base a constant, -1 for no run: the test
        // of its sign folds): the cursor by subtraction.
        var byTable = emit.DefineLabel($"rfail_table_{NextLabelSeq()}");
        var own = emit.DefineLabel($"rfail_own_{NextLabelSeq()}");
        emitRunBase(emit);
        emit.LoadConstant(0);
        emit.BranchIfLess(byTable);
        emit.LoadLocal(bp);
        emitRunBase(emit);
        emit.Subtract();
        emit.StoreLocal(cur);
        emit.LoadLocal(cur);
        emit.LoadConstant(runCount);
        emit.UnsignedBranchIfGreaterOrEqual(byTable);
        emit.LoadLocal(cur);
        emit.LoadConstant(1);
        emit.Add();
        emit.StoreLocal(cur);
        emit.Branch(own);
        // The marker's (functor, cursor) from the pairs table: a wake
        // alternative's cursor, or another code's marker.
        emit.MarkLabel(byTable);
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
        emit.LoadLocal(pair);
        emit.LoadField(typeof((int, int)).GetField("Item2")!);
        emit.StoreLocal(cur);
        emit.MarkLabel(own);
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
        emit.Branch(resumeCheck);
        emit.MarkLabel(callMethod);
    }

    /// <summary>A resume entry after the common restore: RetryMeElse's tail
    /// when <paramref name="nextMarker"/> is given (HB to the heap top, BP to
    /// the next alternative), TrustMe's otherwise (HB and B as before the
    /// push, the frame popped).</summary>
    private static void EmitFrameResumeTail(IlEmit emit, FrameLocals l,
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
            EmitStoreCtlInt(emit, l, CtlBp, nextMarker);
        }
        else
        {
            emit.LoadArgument(0);
            EmitLoadCtlInt(emit, l, CtlHb);
            EmitStoreEngineField(emit, EngHb);
            EmitStoreRegister(emit, rf, MachineRegs.StackTop, () => emit.LoadLocal(b));
            EmitStoreRegister(emit, rf, MachineRegs.B, () => EmitLoadCtlInt(emit, l, CtlB));
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
