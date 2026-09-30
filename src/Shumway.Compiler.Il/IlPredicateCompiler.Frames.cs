using System.Reflection;
using Shumway.Core;

namespace Shumway.Compiler.Il;

// ADR-058 item 6: a region's choice point push and restore, emitted in the
// method as the wasm tier emits them in the module (EmitPushChoicePoint,
// EmitRestoreCommon). The sequences are PushChoicePoint's stores and
// RestoreCommonFromCurrentCp's loads on the same frame layout, over the
// activation's public fields, so nothing here depends on the JIT inlining a
// call. A hook, the debugger's trail-everything mode, a stack that must grow
// or a frame of another arity take the methods instead.
public sealed partial class IlPredicateCompiler
{
    /// <summary>Choice point pushes and restores emitted inline, over every
    /// region compiled in this process.</summary>
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

    // Offsets of the control words from the frame's base, after the arity
    // cell and the saved arguments: the layout of Activation.Cp*Offset.
    private const int CtlCe = 0, CtlCp = 1, CtlB = 2, CtlBp = 3, CtlBindingTrail = 4,
        CtlExtraTrail = 5, CtlHeapTop = 6, CtlHb = 7, CtlViewGen = 8, CtlB0 = 9;

    /// <summary>The locals the inline sequences share, one set per method: a
    /// region has many push and restore sites, and the JIT stops inlining in a
    /// method with too many locals.</summary>
    private sealed class FrameLocals
    {
        public Sigil.Local Stack = null!, Registers = null!, Base = null!,
            Marker = null!, BindingTop = null!, ExtraTop = null!, Cell = null!;
    }

    private static FrameLocals FrameLocalsOf(
        Sigil.Emit<PredicateDelegate> emit, RegionEmitContext ctx)
        => ctx.Frame ??= new FrameLocals
        {
            Stack = emit.DeclareLocal<Cell[]>(),
            Registers = emit.DeclareLocal<Cell[]>(),
            Base = emit.DeclareLocal<int>(),
            Marker = emit.DeclareLocal<int>(),
            BindingTop = emit.DeclareLocal<int>(),
            ExtraTop = emit.DeclareLocal<int>(),
            Cell = emit.DeclareLocal<Cell>(),
        };

    private static void EmitLoadEngineField(Sigil.Emit<PredicateDelegate> emit, FieldInfo field)
    {
        emit.LoadArgument(0);
        emit.LoadField(field);
    }

    /// <summary>The stack array and the index of the frame's cell at
    /// <paramref name="offset"/>, for the ldelem or stelem that follows. Not
    /// ldelema: a region with one does not pass the JIT.</summary>
    private static void EmitFrameCellIndex(
        Sigil.Emit<PredicateDelegate> emit, FrameLocals l, int offset)
    {
        emit.LoadLocal(l.Stack);
        emit.LoadLocal(l.Base);
        if (offset != 0)
        {
            emit.LoadConstant(offset);
            emit.Add();
        }
    }

    /// <summary>frame[offset] = a cell of the raw bits that
    /// <paramref name="pushBits"/> leaves on the stack (an int64). The cell is
    /// built in a local through its field: a constructor call would be one
    /// more call for the JIT to inline.</summary>
    private static void EmitStoreFrameBits(
        Sigil.Emit<PredicateDelegate> emit, FrameLocals l, int offset, System.Action pushBits)
    {
        emit.LoadLocalAddress(l.Cell);
        pushBits();
        emit.StoreField(CellDataField);
        EmitFrameCellIndex(emit, l, offset);
        emit.LoadLocal(l.Cell);
        emit.StoreElement<Cell>();
    }

    /// <summary>frame[offset] = Cell.RawInt(value), the value an int32 that
    /// <paramref name="pushValue"/> leaves on the stack.</summary>
    private static void EmitStoreFrameInt(
        Sigil.Emit<PredicateDelegate> emit, FrameLocals l, int offset, System.Action pushValue)
        => EmitStoreFrameBits(emit, l, offset, () =>
        {
            pushValue();
            emit.Convert<long>();
            emit.LoadConstant(Cell.PayloadMask);
            emit.And();
            emit.LoadConstant(RawIntTag);
            emit.Or();
        });

    /// <summary>frame[offset].Data on the stack.</summary>
    private static void EmitLoadFrameBits(
        Sigil.Emit<PredicateDelegate> emit, FrameLocals l, int offset)
    {
        EmitFrameCellIndex(emit, l, offset);
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
    }

    /// <summary>(int)frame[offset].Data on the stack.</summary>
    private static void EmitLoadFrameInt(
        Sigil.Emit<PredicateDelegate> emit, FrameLocals l, int offset)
    {
        EmitLoadFrameBits(emit, l, offset);
        emit.Convert<int>();
    }

    /// <summary>Branches to <paramref name="slow"/> when the methods must run
    /// instead of the inline form: a hook is on, or the activation trails
    /// everything (a debug session), where HB is not the heap top.</summary>
    private static void EmitFrameFormGuard(
        Sigil.Emit<PredicateDelegate> emit, Sigil.Label slow)
    {
        emit.LoadField(FrameHooksField);
        emit.BranchIfTrue(slow);
        EmitLoadEngineField(emit, EngTrailEverything);
        emit.BranchIfTrue(slow);
    }

    /// <summary>PushChoicePoint(arity, marker) inline. The marker is already in
    /// the frame locals.</summary>
    private static void EmitInlinePush(
        Sigil.Emit<PredicateDelegate> emit, FrameLocals l, int arity)
    {
        System.Threading.Interlocked.Increment(ref InlineFrameSites);
        int size = Activation.CpSize(arity);
        int ctl = 1 + arity;
        var slow = emit.DefineLabel($"cpush_slow_{NextLabelSeq()}");
        var done = emit.DefineLabel($"cpush_done_{NextLabelSeq()}");

        EmitFrameFormGuard(emit, slow);
        EmitLoadEngineField(emit, EngStack);
        emit.StoreLocal(l.Stack);
        EmitLoadEngineField(emit, EngStackTop);
        emit.StoreLocal(l.Base);
        // The frame must fit: growing the stack replaces the array.
        emit.LoadLocal(l.Base);
        emit.LoadConstant(size);
        emit.Add();
        emit.LoadLocal(l.Stack);
        emit.LoadLength<Cell>();
        emit.Convert<int>();
        emit.BranchIfGreater(slow);
        if (arity > 0)
        {
            EmitLoadEngineField(emit, EngRegisters);
            emit.StoreLocal(l.Registers);
            emit.LoadConstant(arity);
            emit.LoadLocal(l.Registers);
            emit.LoadLength<Cell>();
            emit.Convert<int>();
            emit.BranchIfGreater(slow);
        }

        EmitStoreFrameBits(emit, l, Activation.CpArityOffset,
            () => emit.LoadConstant(RawIntTag | (uint)arity));
        for (int i = 0; i < arity; i++)
        {
            emit.LoadLocal(l.Stack);
            emit.LoadLocal(l.Base);
            emit.LoadConstant(Activation.CpArg1Offset + i);
            emit.Add();
            emit.LoadLocal(l.Registers);
            emit.LoadConstant(i);
            emit.LoadElement<Cell>();
            emit.StoreElement<Cell>();
        }
        EmitStoreFrameInt(emit, l, ctl + CtlCe, () => EmitLoadEngineField(emit, EngE));
        EmitStoreFrameInt(emit, l, ctl + CtlCp, () => EmitLoadEngineField(emit, EngCp));
        EmitStoreFrameInt(emit, l, ctl + CtlB, () => EmitLoadEngineField(emit, EngB));
        EmitStoreFrameInt(emit, l, ctl + CtlBp, () => emit.LoadLocal(l.Marker));
        EmitStoreFrameInt(emit, l, ctl + CtlBindingTrail,
            () => EmitLoadEngineField(emit, EngBindingTrailTop));
        EmitStoreFrameInt(emit, l, ctl + CtlExtraTrail,
            () => EmitLoadEngineField(emit, EngExtraTrailTop));
        EmitStoreFrameInt(emit, l, ctl + CtlHeapTop, () => EmitLoadEngineField(emit, EngHeapTop));
        EmitStoreFrameInt(emit, l, ctl + CtlHb, () => EmitLoadEngineField(emit, EngHb));
        // The view generation is a 60-bit long, not an int.
        EmitStoreFrameBits(emit, l, ctl + CtlViewGen, () =>
        {
            EmitLoadEngineField(emit, EngViewGen);
            emit.LoadConstant(Cell.PayloadMask);
            emit.And();
            emit.LoadConstant(RawIntTag);
            emit.Or();
        });
        EmitStoreFrameInt(emit, l, ctl + CtlB0, () => EmitLoadEngineField(emit, EngB0));

        emit.LoadArgument(0);
        emit.LoadLocal(l.Base);
        emit.LoadConstant(size);
        emit.Add();
        emit.StoreField(EngStackTop);
        emit.LoadArgument(0);
        emit.LoadLocal(l.Base);
        emit.StoreField(EngB);
        emit.LoadArgument(0);
        EmitLoadEngineField(emit, EngHeapTop);
        emit.StoreField(EngHb);
        emit.Branch(done);

        emit.MarkLabel(slow);
        emit.LoadArgument(0);
        emit.LoadConstant(arity);
        emit.LoadLocal(l.Marker);
        EmitHelperCall(emit, EnginePushChoicePointMethod);
        emit.MarkLabel(done);
    }

    /// <summary>RetryMeElse(marker) inline when <paramref name="retry"/>, the
    /// marker in the frame locals; TrustMe() otherwise.</summary>
    private static void EmitInlineRestore(
        Sigil.Emit<PredicateDelegate> emit, FrameLocals l, int arity, bool retry)
    {
        System.Threading.Interlocked.Increment(ref InlineFrameSites);
        int ctl = 1 + arity;
        var slow = emit.DefineLabel($"crest_slow_{NextLabelSeq()}");
        var done = emit.DefineLabel($"crest_done_{NextLabelSeq()}");
        var unwind = emit.DefineLabel($"crest_unwind_{NextLabelSeq()}");
        var unwound = emit.DefineLabel($"crest_unwound_{NextLabelSeq()}");

        EmitFrameFormGuard(emit, slow);
        EmitLoadEngineField(emit, EngB);
        emit.StoreLocal(l.Base);
        EmitLoadEngineField(emit, EngStack);
        emit.StoreLocal(l.Stack);
        // The frame on top is the one this alternative's push wrote.
        EmitLoadFrameBits(emit, l, Activation.CpArityOffset);
        emit.LoadConstant(RawIntTag | (uint)arity);
        emit.UnsignedBranchIfNotEqual(slow);

        if (arity > 0)
        {
            EmitLoadEngineField(emit, EngRegisters);
            emit.StoreLocal(l.Registers);
            for (int i = 0; i < arity; i++)
            {
                emit.LoadLocal(l.Registers);
                emit.LoadConstant(i);
                emit.LoadLocal(l.Stack);
                emit.LoadLocal(l.Base);
                emit.LoadConstant(Activation.CpArg1Offset + i);
                emit.Add();
                emit.LoadElement<Cell>();
                emit.StoreElement<Cell>();
            }
        }
        emit.LoadArgument(0);
        EmitLoadFrameInt(emit, l, ctl + CtlCe);
        emit.StoreField(EngE);
        emit.LoadArgument(0);
        EmitLoadFrameInt(emit, l, ctl + CtlCp);
        emit.StoreField(EngCp);

        // The unwind is a call, and only when something was trailed since
        // the push.
        EmitLoadFrameInt(emit, l, ctl + CtlBindingTrail);
        emit.StoreLocal(l.BindingTop);
        EmitLoadFrameInt(emit, l, ctl + CtlExtraTrail);
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
        EmitLoadEngineField(emit, EngStack);
        emit.StoreLocal(l.Stack);
        emit.MarkLabel(unwound);

        emit.LoadArgument(0);
        EmitLoadFrameInt(emit, l, ctl + CtlHeapTop);
        emit.StoreField(EngHeapTop);
        emit.LoadArgument(0);
        EmitLoadFrameBits(emit, l, ctl + CtlViewGen);
        emit.LoadConstant(Cell.PayloadMask);
        emit.And();
        emit.StoreField(EngViewGen);
        emit.LoadArgument(0);
        EmitLoadFrameInt(emit, l, ctl + CtlB0);
        emit.StoreField(EngB0);

        if (retry)
        {
            // The frame stays: HB to the restored heap top, BP to the next
            // alternative.
            emit.LoadArgument(0);
            EmitLoadEngineField(emit, EngHeapTop);
            emit.StoreField(EngHb);
            EmitStoreFrameInt(emit, l, ctl + CtlBp, () => emit.LoadLocal(l.Marker));
        }
        else
        {
            // The frame goes: HB and B as they were before the push.
            emit.LoadArgument(0);
            EmitLoadFrameInt(emit, l, ctl + CtlHb);
            emit.StoreField(EngHb);
            emit.LoadArgument(0);
            EmitLoadFrameInt(emit, l, ctl + CtlB);
            emit.StoreField(EngB);
            emit.LoadArgument(0);
            emit.LoadLocal(l.Base);
            emit.StoreField(EngStackTop);
        }
        emit.Branch(done);

        emit.MarkLabel(slow);
        emit.LoadArgument(0);
        if (retry)
        {
            emit.LoadLocal(l.Marker);
            EmitHelperCall(emit, EngineRetryMeElseMethod);
        }
        else
        {
            EmitHelperCall(emit, EngineTrustMeMethod);
        }
        emit.MarkLabel(done);
    }
}
