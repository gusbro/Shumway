using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>ADR-060: the machine registers a region may hold in locals of
/// its method. Each is a field of the activation; the local is a working
/// copy that lives for one invocation of the region.</summary>
[Flags]
public enum MachineRegs
{
    None = 0,
    E = 1 << 0,
    Cp = 1 << 1,
    B = 1 << 2,
    B0 = 1 << 3,
    StackTop = 1 << 4,
    HeapTop = 1 << 5,
    Hb = 1 << 6,
    TrailTop = 1 << 7,
    StackArray = 1 << 8,
    HeapArray = 1 << 9,
    RegisterArray = 1 << 10,
    TrailArray = 1 << 11,
    All = (1 << 12) - 1,
}

/// <summary>ADR-060: what a helper does to the machine registers, the ones
/// it reads and the ones it writes. A method that can throw reads all of
/// them; one that can run Prolog code or collect the heap reads and writes
/// all of them.</summary>
public readonly record struct HelperEffects(MachineRegs Reads, MachineRegs Writes)
{
    public static readonly HelperEffects Nothing = new(MachineRegs.None, MachineRegs.None);
    public static readonly HelperEffects Everything = new(MachineRegs.All, MachineRegs.All);
}

/// <summary>The failure a region raises under <see
/// cref="IlPredicateCompiler.CheckedRegisters"/> when a local and its field
/// disagree at a point where they must agree.</summary>
public static class IlRegisterCheck
{
    public static void Mismatch(string what)
        => throw new InvalidOperationException(
            $"ADR-060 register check failed: {what}.");
}

public sealed partial class IlPredicateCompiler
{
    /// <summary>ADR-060 item 8: emit, at every helper call of a region, the
    /// comparison of each held register with its field where the two must
    /// agree. For the test gate; a release build does not set it.</summary>
    public static bool CheckedRegisters { get; set; } =
        Environment.GetEnvironmentVariable("SHUMWAY_IL_CHECKED_REGS") == "1";

    /// <summary>Register checks emitted under <see cref="CheckedRegisters"/>,
    /// over every region compiled in this process.</summary>
    public static int RegisterChecksEmitted;

    /// <summary>ADR-060 stage 2: the control registers.</summary>
    internal const MachineRegs ControlRegs = MachineRegs.E | MachineRegs.Cp | MachineRegs.B
        | MachineRegs.B0 | MachineRegs.StackTop | MachineRegs.StackArray;

    /// <summary>ADR-060 item 1: the registers a region holds. With none held
    /// a region compiles to the code it compiles to without a register
    /// file.</summary>
    internal static MachineRegs HeldByDefault { get; set; } =
        Environment.GetEnvironmentVariable("SHUMWAY_IL_HELD_REGS") == "0"
            ? MachineRegs.None : ControlRegs;

    private static readonly FieldInfo?[] RegisterFields = BuildRegisterFields();

    private static FieldInfo?[] BuildRegisterFields()
    {
        var f = new FieldInfo?[12];
        f[Index(MachineRegs.E)] = EngineField(nameof(Activation._e));
        f[Index(MachineRegs.Cp)] = EngineField(nameof(Activation._cp));
        f[Index(MachineRegs.B)] = EngineField(nameof(Activation._b));
        f[Index(MachineRegs.B0)] = EngineField(nameof(Activation._b0));
        f[Index(MachineRegs.StackTop)] = EngineField(nameof(Activation._stackTop));
        f[Index(MachineRegs.HeapTop)] = EngineField(nameof(Activation._heapTop));
        f[Index(MachineRegs.Hb)] = EngineField(nameof(Activation._hb));
        f[Index(MachineRegs.TrailTop)] = EngineField(nameof(Activation._bindingTrailTop));
        f[Index(MachineRegs.StackArray)] = EngineField(nameof(Activation._stack));
        f[Index(MachineRegs.RegisterArray)] = EngineField(nameof(Activation._registers));
        f[Index(MachineRegs.HeapArray)] = EngineField(nameof(Activation._heap));
        // The binding trail array has no entry: no region holds it.
        return f;
    }

    private static int Index(MachineRegs one)
    {
        int i = 0;
        while ((int)one >> i != 1) i++;
        return i;
    }

    private static string RegisterName(int index) => ((MachineRegs)(1 << index)).ToString();

    /// <summary>ADR-060 item 2: the locals of one region method and what they
    /// hold. Between two synchronization points the local of a held register
    /// is the truth and its field is stale.</summary>
    // Stores are written through: a store to a held register writes its local
    // and its field, so the fields are current at every call, return and
    // exception, and a call only reloads what its row says it writes.
    private sealed class RegisterFile
    {
        public MachineRegs Held;
        // ADR-061: the method's one exit to its cold method, and the boundary
        // its sites leave on the stack.
        public IlLocal? ColdBoundary;
        public IlLabel? SharedColdExit;
        private readonly IlLocal?[] _locals = new IlLocal?[12];
        private readonly IlEmit _emit;
        private readonly Dictionary<(Type, int), IlLocal> _temps = new();

        public bool Holds(MachineRegs regs) => (Held & regs) == regs;

        public IlLocal? LocalOf(int index) => _locals[index];

        public IlLocal Local(MachineRegs one) => _locals[Index(one)]!;

        /// <summary>A scratch local of <paramref name="type"/>, the
        /// <paramref name="k"/>-th of its type. Shared by every sequence of the
        /// method, none of which is live across another.</summary>
        public IlLocal Temp(Type type, int k = 0)
        {
            if (!_temps.TryGetValue((type, k), out var l))
                _temps[(type, k)] = l = _emit.DeclareLocal(type, $"regtmp_{type.Name}_{k}");
            return l;
        }

        public RegisterFile(IlEmit emit, MachineRegs held)
        {
            _emit = emit;
            Held = held;
            for (int i = 0; i < 12; i++)
            {
                if ((held & (MachineRegs)(1 << i)) == 0) continue;
                var field = RegisterFields[i]
                    ?? throw new InvalidOperationException(
                        $"ADR-060: {RegisterName(i)} is not on the activation's public surface.");
                _locals[i] = emit.DeclareLocal(field.FieldType, $"reg_{RegisterName(i)}");
            }
        }

        /// <summary>field to local, for every register in <paramref name="regs"/> that is held.</summary>
        public void EmitLoad(IlEmit emit, MachineRegs regs)
        {
            regs &= Held;
            for (int i = 0; i < 12; i++)
            {
                if ((regs & (MachineRegs)(1 << i)) == 0) continue;
                emit.LoadArgument(0);
                emit.LoadField(RegisterFields[i]!);
                emit.StoreLocal(_locals[i]!);
            }
        }

        /// <summary>Under <see cref="CheckedRegisters"/>: fail unless each held
        /// register in <paramref name="regs"/> has the same value in its local
        /// and its field.</summary>
        public void EmitCheck(IlEmit emit, MachineRegs regs, string where)
        {
            regs &= Held;
            for (int i = 0; i < 12; i++)
            {
                if ((regs & (MachineRegs)(1 << i)) == 0) continue;
                System.Threading.Interlocked.Increment(ref RegisterChecksEmitted);
                var same = emit.DefineLabel($"regck_{NextLabelSeq()}");
                emit.LoadArgument(0);
                emit.LoadField(RegisterFields[i]!);
                emit.LoadLocal(_locals[i]!);
                emit.BranchIfEqual(same);
                emit.LoadConstant($"{RegisterName(i)} {where}");
                emit.Call(IlRegisterCheckMismatchMethod);
                emit.MarkLabel(same);
            }
        }
    }

    private static readonly MethodInfo IlRegisterCheckMismatchMethod =
        typeof(IlRegisterCheck).GetMethod(nameof(IlRegisterCheck.Mismatch))!;

    // Keyed by the emit, so that the emitters (static methods that receive
    // only the emit) find the region's register file without a parameter
    // threaded through every one of them.
    private static readonly ConditionalWeakTable<IlEmit, RegisterFile>
        RegisterFiles = new();

    private static RegisterFile? RegisterFileOf(IlEmit emit)
        => RegisterFiles.TryGetValue(emit, out var rf) ? rf : null;

    /// <summary>The local that holds the register stored in <paramref
    /// name="field"/>, when the region holds it.</summary>
    private static IlLocal? HeldLocalOf(IlEmit emit, FieldInfo field)
    {
        var rf = RegisterFileOf(emit);
        if (rf is null) return null;
        for (int i = 0; i < 12; i++)
            if (RegisterFields[i] == field) return rf.LocalOf(i);
        return null;
    }

    /// <summary>field := value, the stack holding [activation, value]; the
    /// register's local too when the region holds it.</summary>
    private static void EmitStoreEngineField(IlEmit emit, FieldInfo field)
    {
        if (HeldLocalOf(emit, field) is { } local)
        {
            emit.Duplicate();
            emit.StoreLocal(local);
        }
        emit.StoreField(field);
    }

    /// <summary>Gives a region method its register file, holding <see
    /// cref="HeldByDefault"/>, and loads the held registers: the region's
    /// entry (ADR-060 item 2).</summary>
    private static void EmitRegionRegistersEntry(IlEmit emit, bool hold)
    {
        var rf = new RegisterFile(emit, hold ? HeldByDefault : MachineRegs.None);
        RegisterFiles.Remove(emit);
        RegisterFiles.Add(emit, rf);
        rf.EmitLoad(emit, rf.Held);
    }

    /// <summary>A method outside a region gets a register file that holds
    /// nothing, for the operations emitted over fields (get_list,
    /// SetRegister). Not under verification or the debugger, which want the
    /// calls.</summary>
    private void AttachRegisterFile(IlEmit emit)
    {
        if (DebugMode) return;
        // ADR-061: a continuation method holds the registers a region holds.
        if (CpsEmitting)
        {
            EmitRegionRegistersEntry(emit, hold: true);
            EmitCpsFrameEntry(emit);
        }
        else
        {
            RegisterFiles.Remove(emit);
            RegisterFiles.Add(emit, new RegisterFile(emit, MachineRegs.None));
        }
    }

    /// <summary>The method's shared exit to its cold method, after its code.</summary>
    private static void EmitSharedColdExit(IlEmit emit, IlEmit? cold)
    {
        if (RegisterFileOf(emit) is not { SharedColdExit: { } coldExit } rf) return;
        emit.MarkLabel(coldExit);
        emit.StoreLocal(rf.ColdBoundary!);
        emit.LoadArgument(0);
        emit.LoadLocal(rf.ColdBoundary!);
        emit.Call(cold!);
        emit.Return();
    }

    /// <summary>ADR-060 item 3: a call to a helper, with the held registers it
    /// reads spilled before and the ones it writes reloaded after. A method
    /// without a row reads and writes all of them. Outside a region, or when
    /// nothing is held, the plain call.</summary>
    internal static void EmitHelperCall(IlEmit emit, MethodInfo method)
    {
        var rf = RegisterFileOf(emit);
        if (rf is null)
        {
            emit.Call(method);
            return;
        }
        if (rf.Held == MachineRegs.None)
        {
            if (method != EngineSetRegisterMethod || !TryEmitSetRegister(emit, rf))
                emit.Call(method);
            return;
        }
        if (TryEmitIntrinsic(emit, rf, method)) return;
        EmitRowCall(emit, rf, method);
    }

    /// <summary>The call itself, with the reload of what its row writes.</summary>
    private static void EmitRowCall(IlEmit emit, RegisterFile rf, MethodInfo method)
    {
        var fx = HelperTable.TryGetValue(method, out var row) ? row : HelperEffects.Everything;
        emit.Call(method);
        if (CheckedRegisters) rf.EmitCheck(emit, ~fx.Writes, $"changed by {method.Name}");
        rf.EmitLoad(emit, fx.Writes);
    }

    /// <summary>A return from a region or a standalone method. The fields are
    /// current: stores are written through.</summary>
    private static void EmitReturn(IlEmit emit) => emit.Return();

    /// <summary>ADR-060 item 3: one row per helper the emitter calls. Verified
    /// against the methods' own code by a test; a row that is wider than the
    /// method is a wasted spill, one that is narrower is a stale register.</summary>
    // Built on first use, after every static MethodInfo of the partial class
    // exists: field initializers of different partial files run in no
    // defined order.
    internal static IReadOnlyDictionary<MethodInfo, HelperEffects> HelperTable
        => _helperTable ??= BuildHelperTable();
    private static Dictionary<MethodInfo, HelperEffects>? _helperTable;

    /// <summary>Replaces a row. For the test that proves a wrong row is caught.</summary>
    internal static void OverrideHelperRow(MethodInfo method, HelperEffects effects)
        => ((Dictionary<MethodInfo, HelperEffects>)HelperTable)[method] = effects;

    internal static MethodInfo HelperHandle(string fieldName)
        => (MethodInfo)typeof(IlPredicateCompiler).GetField(fieldName,
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static Dictionary<MethodInfo, HelperEffects> BuildHelperTable()
    {
        var t = new Dictionary<MethodInfo, HelperEffects>();
        void Row(MethodInfo m, MachineRegs reads, MachineRegs writes) => t[m] = new(reads, writes);
        // Generated by IlHelperTableTests.EachRowIsWhatTheMethodDoes, which
        // fails with the corrected rows when a method changes.
        Row(ArithBinMethod, MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop, MachineRegs.None);
        Row(ArithCmpMethod, MachineRegs.None, MachineRegs.None);
        Row(ArithFusedBinMethod, MachineRegs.All, MachineRegs.All);
        Row(ArithFusedCmpMethod, MachineRegs.All, MachineRegs.All);
        Row(ArithIsEmptyGetter, MachineRegs.None, MachineRegs.None);
        Row(ArithIsPermMethod, MachineRegs.E | MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(ArithIsRegMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(ArithOperandUnboundMethod, MachineRegs.E | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(ArithPushIntMethod, MachineRegs.None, MachineRegs.None);
        Row(ArithPushRegMethod, MachineRegs.All, MachineRegs.All);
        Row(ArithPushYMethod, MachineRegs.All, MachineRegs.All);
        Row(ArithSetPermMethod, MachineRegs.E | MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray, MachineRegs.HeapTop | MachineRegs.HeapArray);
        Row(ArithSetRegMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.HeapTop | MachineRegs.HeapArray | MachineRegs.RegisterArray);
        Row(ArithUnMethod, MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop, MachineRegs.None);
        Row(BuiltinEntryImplGetter, MachineRegs.None, MachineRegs.None);
        Row(BuiltinImplInvokeMethod, MachineRegs.All, MachineRegs.All);
        Row(BuiltinsRegistryGetByIdMethod, MachineRegs.None, MachineRegs.None);
        Row(CellAsAtomIdGetter, MachineRegs.None, MachineRegs.None);
        Row(CellAsFunctorIdGetter, MachineRegs.None, MachineRegs.None);
        Row(CellAsHeapIndexGetter, MachineRegs.None, MachineRegs.None);
        Row(CellAsIntGetter, MachineRegs.None, MachineRegs.None);
        Row(CellAtomMethod, MachineRegs.None, MachineRegs.None);
        Row(CellIntMethod, MachineRegs.None, MachineRegs.None);
        Row(CellRefMethod, MachineRegs.None, MachineRegs.None);
        Row(CellTagGetter, MachineRegs.None, MachineRegs.None);
        Row(CellTagIdGetter, MachineRegs.None, MachineRegs.None);
#if DEBUG
        Row(DbgCheckAllocateMethod, MachineRegs.E, MachineRegs.None);
        Row(DbgCheckDeallocateMethod, MachineRegs.E, MachineRegs.None);
        Row(DbgCheckGetVariableXMethod, MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(DbgCheckGetVariableYMethod, MachineRegs.E | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(DbgCheckPostCallMethod, MachineRegs.E | MachineRegs.B | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(DbgCheckPreCallMethod, MachineRegs.E | MachineRegs.B | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(DbgCheckPutValueXMethod, MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(DbgCheckPutValueYMethod, MachineRegs.E | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(DbgCheckPutVariableXMethod, MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(DbgCheckPutVariableYMethod, MachineRegs.E | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
#endif
        Row(EngineAllocateHeapUnboundMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray, MachineRegs.HeapTop | MachineRegs.HeapArray);
        Row(EngineAllocateMethod, MachineRegs.E | MachineRegs.Cp | MachineRegs.B | MachineRegs.StackTop | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray, MachineRegs.E | MachineRegs.StackTop | MachineRegs.StackArray);
        Row(EngineBGetter, MachineRegs.B, MachineRegs.None);
        Row(EngineBacktrackSafePointDueMethod, MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop, MachineRegs.None);
        Row(EngineBacktrackSafePointMethod, MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop, MachineRegs.None);
        Row(EngineBeginIlGuardMethod, MachineRegs.HeapTop | MachineRegs.Hb, MachineRegs.Hb);
        Row(EngineBindingTrailTopGetter, MachineRegs.TrailTop, MachineRegs.None);
        Row(EngineBuiltinReturnPcSetter, MachineRegs.None, MachineRegs.None);
        Row(EngineCallSafePointDueGetter, MachineRegs.HeapTop, MachineRegs.None);
        Row(EngineCommitIlGuardMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray, MachineRegs.Hb | MachineRegs.TrailTop);
        Row(EngineCpGetter, MachineRegs.Cp, MachineRegs.None);
        Row(EngineCurrentFunctorAddressesGetter, MachineRegs.None, MachineRegs.None);
        Row(EngineCutToLevelMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineDeallocateMethod, MachineRegs.E | MachineRegs.B | MachineRegs.StackTop | MachineRegs.StackArray, MachineRegs.E | MachineRegs.Cp | MachineRegs.StackTop);
        Row(EngineDerefMethod, MachineRegs.HeapArray, MachineRegs.None);
        Row(EngineEGetter, MachineRegs.E, MachineRegs.None);
        Row(EngineEncodeResumeMarkerMethod, MachineRegs.None, MachineRegs.None);
        Row(EngineExtraTrailTopGetter, MachineRegs.None, MachineRegs.None);
        Row(EngineFailIlGuardMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineFlushWakeupsForIlCutMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineGetHeapMethod, MachineRegs.HeapArray, MachineRegs.None);
        Row(EngineGetLevelBMethod, MachineRegs.E | MachineRegs.B | MachineRegs.StackArray, MachineRegs.None);
        Row(EngineGetLevelMethod, MachineRegs.E | MachineRegs.B0 | MachineRegs.StackArray, MachineRegs.None);
        Row(EngineGetListMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineGetListValXVarXMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray);
        Row(EngineGetListVarXVarXMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray);
        Row(EngineGetRegisterMethod, MachineRegs.RegisterArray, MachineRegs.None);
        Row(EngineGetStruct2ValXValXMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineGetStruct2VarXVarXMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray);
        Row(EngineGetStructureMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineGetYMethod, MachineRegs.E | MachineRegs.StackArray, MachineRegs.None);
        Row(EngineHasPendingWakeupsGetter, MachineRegs.None, MachineRegs.None);
        Row(EngineHeapTopGetter, MachineRegs.HeapTop, MachineRegs.None);
        Row(EngineIlTailCallPendingSetter, MachineRegs.None, MachineRegs.None);
        Row(EngineIsDynMutatedMethod, MachineRegs.None, MachineRegs.None);
        Row(EngineMakeFloatMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray, MachineRegs.HeapTop | MachineRegs.HeapArray);
        Row(EngineMaybeCollectHeapAtCallMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineMaybeCollectHeapMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineNeckCutMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineOccursModeGetter, MachineRegs.None, MachineRegs.None);
        Row(EngineTrailBindMethod, MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.TrailArray, MachineRegs.TrailTop | MachineRegs.TrailArray);
        Row(EngineUnifyHeapWithCellMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EnginePopGuardContFailMethod, MachineRegs.None, MachineRegs.None);
        Row(EnginePopGuardContOkMethod, MachineRegs.None, MachineRegs.None);
        Row(EnginePushChoicePointMethod, MachineRegs.E | MachineRegs.Cp | MachineRegs.B | MachineRegs.B0 | MachineRegs.StackTop | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.RegisterArray, MachineRegs.B | MachineRegs.StackTop | MachineRegs.Hb | MachineRegs.StackArray | MachineRegs.RegisterArray);
        Row(EnginePushGuardContMethod, MachineRegs.None, MachineRegs.None);
        Row(EnginePushIlCpMethod, MachineRegs.E | MachineRegs.Cp | MachineRegs.B | MachineRegs.B0 | MachineRegs.StackTop | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.RegisterArray, MachineRegs.B | MachineRegs.StackTop | MachineRegs.Hb | MachineRegs.StackArray | MachineRegs.RegisterArray);
        Row(EnginePushIlCpWithMarksMethod, MachineRegs.E | MachineRegs.Cp | MachineRegs.B | MachineRegs.B0 | MachineRegs.StackTop | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.RegisterArray, MachineRegs.B | MachineRegs.StackTop | MachineRegs.Hb | MachineRegs.StackArray | MachineRegs.RegisterArray);
        Row(EnginePutListMethod, MachineRegs.HeapTop | MachineRegs.RegisterArray, MachineRegs.RegisterArray);
        Row(EnginePutListReservedMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.HeapTop | MachineRegs.HeapArray | MachineRegs.RegisterArray);
        Row(EnginePutStructureMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.HeapTop | MachineRegs.HeapArray | MachineRegs.RegisterArray);
        Row(EnginePutStructureReservedMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.HeapTop | MachineRegs.HeapArray | MachineRegs.RegisterArray);
        Row(EngineRegionReturnCursorMethod, MachineRegs.Cp, MachineRegs.None);
        Row(EngineContinuationMethod, MachineRegs.HeapTop, MachineRegs.None);
        Row(ContinuationInvokeMethod, MachineRegs.All, MachineRegs.All);
        Row(ArithTryFusedBinIntMethod, MachineRegs.E | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(ArithTryFusedCmpIntMethod, MachineRegs.E | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(EngineTryFailIlGuardQuickMethod, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray, MachineRegs.HeapTop | MachineRegs.Hb);
        Row(EngineCpsCallTargetMethod, MachineRegs.HeapTop, MachineRegs.None);
        Row(EngineCpsProceedTargetMethod, MachineRegs.Cp | MachineRegs.HeapTop, MachineRegs.None);
        Row(EngineTagIlCpCpsMethod, MachineRegs.None, MachineRegs.None);
        Row(EnginePushIlCpEntryCpsMethod, MachineRegs.B, MachineRegs.None);
        Row(EngineResumeMarkerOfMethod, MachineRegs.None, MachineRegs.None);
        Row(EnginePushCpWithMarksMethod, MachineRegs.E | MachineRegs.Cp | MachineRegs.B | MachineRegs.B0 | MachineRegs.StackTop | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.RegisterArray, MachineRegs.B | MachineRegs.StackTop | MachineRegs.Hb | MachineRegs.StackArray | MachineRegs.RegisterArray);
        Row(EnginePushLateChoicePointMethod, MachineRegs.E | MachineRegs.Cp | MachineRegs.B | MachineRegs.B0 | MachineRegs.StackTop | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.RegisterArray, MachineRegs.B | MachineRegs.StackTop | MachineRegs.Hb | MachineRegs.StackArray | MachineRegs.RegisterArray);
        Row(EngineRegistersIdenticalMethod, MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(EngineCellsIdenticalMethod, MachineRegs.HeapArray, MachineRegs.None);
        Row(EngineRetryMeElseMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineSetB0Method, MachineRegs.None, MachineRegs.B0);
        Row(EngineSetCpMethod, MachineRegs.None, MachineRegs.Cp);
        Row(EngineCodeAddressOfMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineDeoptToMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineEnvPrevMethod, MachineRegs.StackArray, MachineRegs.None);
        Row(EngineRetargetFrameMethod, MachineRegs.StackArray, MachineRegs.None);
        Row(EngineGuardContOkAtMethod, MachineRegs.None, MachineRegs.None);
        Row(EngineWakeScratchMethod, MachineRegs.None, MachineRegs.None);
        Row(EngineGuardContTopGetter, MachineRegs.None, MachineRegs.None);
        Row(EngineResetGuardContTopMethod, MachineRegs.None, MachineRegs.None);
        Row(EngineSetPcMethod, MachineRegs.None, MachineRegs.None);
        Row(EngineSetRegisterMethod, MachineRegs.RegisterArray, MachineRegs.RegisterArray);
        Row(EngineSetTopCpArgRegisterMethod, MachineRegs.B | MachineRegs.StackArray, MachineRegs.None);
        Row(EngineSetYMethod, MachineRegs.E | MachineRegs.StackArray, MachineRegs.None);
        Row(EngineSoftCutToLevelMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineTrustMeMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineTryResumeOwnCpMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineUnifyArgCellMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineUnifyListMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineUnifyMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineUnifyPermanentMethod, MachineRegs.E | MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineUnifyRegisterWithHeapAtMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineUnifyRegistersMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineUnifyStructureMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineUnifyValueXMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineUnifyValueYMethod, MachineRegs.E | MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(EngineUnifyVariableXMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.HeapTop | MachineRegs.HeapArray | MachineRegs.RegisterArray);
        Row(EngineUnifyVariableYMethod, MachineRegs.E | MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray, MachineRegs.HeapTop | MachineRegs.HeapArray);
        Row(EngineUnifyVoidMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray, MachineRegs.HeapTop | MachineRegs.HeapArray);
        Row(EngineUnwindTrailsMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineDropUndoneWakesMethod, MachineRegs.HeapTop | MachineRegs.HeapArray, MachineRegs.None);
        Row(EngineWakeBoundaryCallMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineWakeBoundaryProceedMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineWakeBoundaryAtMethod, MachineRegs.All, MachineRegs.All);
        Row(IlExecuteHelperResolveMethod, MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop, MachineRegs.None);
        Row(IlGetPstrHelperMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray | MachineRegs.TrailArray, MachineRegs.HeapTop | MachineRegs.TrailTop | MachineRegs.HeapArray | MachineRegs.TrailArray);
        Row(IlIndexedDispatchResolveByFidMethod, MachineRegs.All, MachineRegs.All);
        Row(IlIsNonEmptyPstrMethod, MachineRegs.None, MachineRegs.None);
        Row(IlRegisterCheckMismatchMethod, MachineRegs.None, MachineRegs.None);
        Row(IlMetaCallHelperDispatchMethod, MachineRegs.All, MachineRegs.All);
        Row(IlMetaCallHelperReadIntRegisterMethod, MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(IlProfileCountersBump, MachineRegs.None, MachineRegs.None);
        Row(IlPutPstrHelperMethod, MachineRegs.B | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop | MachineRegs.StackArray | MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.HeapTop | MachineRegs.HeapArray | MachineRegs.RegisterArray);
        Row(IlWalkSubOrMissMethod, MachineRegs.HeapArray, MachineRegs.None);
        Row(PreferRationalsGetter, MachineRegs.None, MachineRegs.None);
        return t;
    }
}
