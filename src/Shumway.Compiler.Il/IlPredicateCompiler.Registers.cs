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

    /// <summary>ADR-060 item 1: the registers a region holds. With none held
    /// a region compiles to the code it compiles to without a register
    /// file.</summary>
    internal static MachineRegs HeldByDefault { get; set; } = MachineRegs.None;

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
        // The heap and binding trail arrays are not on the public surface yet
        // (ADR-060 item 10); a region cannot hold them until they are.
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
    private sealed class RegisterFile
    {
        public MachineRegs Held;
        private readonly Sigil.Local?[] _locals = new Sigil.Local?[12];

        public RegisterFile(Sigil.Emit<PredicateDelegate> emit, MachineRegs held)
        {
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
        public void EmitLoad(Sigil.Emit<PredicateDelegate> emit, MachineRegs regs)
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

        /// <summary>local to field, for every register in <paramref name="regs"/> that is held.</summary>
        public void EmitSpill(Sigil.Emit<PredicateDelegate> emit, MachineRegs regs)
        {
            regs &= Held;
            for (int i = 0; i < 12; i++)
            {
                if ((regs & (MachineRegs)(1 << i)) == 0) continue;
                emit.LoadArgument(0);
                emit.LoadLocal(_locals[i]!);
                emit.StoreField(RegisterFields[i]!);
            }
        }

        /// <summary>Under <see cref="CheckedRegisters"/>: fail unless each held
        /// register in <paramref name="regs"/> has the same value in its local
        /// and its field.</summary>
        public void EmitCheck(Sigil.Emit<PredicateDelegate> emit, MachineRegs regs, string where)
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
    private static readonly ConditionalWeakTable<Sigil.Emit<PredicateDelegate>, RegisterFile>
        RegisterFiles = new();

    private static RegisterFile? RegisterFileOf(Sigil.Emit<PredicateDelegate> emit)
        => RegisterFiles.TryGetValue(emit, out var rf) ? rf : null;

    /// <summary>Gives a region method its register file, holding <see
    /// cref="HeldByDefault"/>, and loads the held registers: the region's
    /// entry (ADR-060 item 2).</summary>
    private static void EmitRegionRegistersEntry(Sigil.Emit<PredicateDelegate> emit)
    {
        var rf = new RegisterFile(emit, HeldByDefault);
        RegisterFiles.Remove(emit);
        RegisterFiles.Add(emit, rf);
        rf.EmitLoad(emit, rf.Held);
    }

    /// <summary>ADR-060 item 3: a call to a helper, with the held registers it
    /// reads spilled before and the ones it writes reloaded after. A method
    /// without a row reads and writes all of them. Outside a region, or when
    /// nothing is held, the plain call.</summary>
    internal static void EmitHelperCall(Sigil.Emit<PredicateDelegate> emit, MethodInfo method)
    {
        var rf = RegisterFileOf(emit);
        if (rf is null || rf.Held == MachineRegs.None)
        {
            emit.Call(method);
            return;
        }
        var fx = HelperTable.TryGetValue(method, out var row) ? row : HelperEffects.Everything;
        // Checked: everything spilled, so that a field the row says the method
        // leaves alone must still equal its local afterwards.
        rf.EmitSpill(emit, CheckedRegisters ? MachineRegs.All : fx.Reads);
        emit.Call(method);
        if (CheckedRegisters) rf.EmitCheck(emit, ~fx.Writes, $"changed by {method.Name}");
        rf.EmitLoad(emit, fx.Writes);
    }

    /// <summary>A return from a region or a standalone method: the held
    /// registers are spilled first, since the caller reads the fields.</summary>
    private static void EmitReturn(Sigil.Emit<PredicateDelegate> emit)
    {
        var rf = RegisterFileOf(emit);
        if (rf is not null) rf.EmitSpill(emit, rf.Held);
        emit.Return();
    }

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
        Row(EngineBacktrackSafePointMethod, MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop, MachineRegs.None);
        Row(EngineBeginIlGuardMethod, MachineRegs.HeapTop | MachineRegs.Hb, MachineRegs.Hb);
        Row(EngineBindingTrailTopGetter, MachineRegs.TrailTop, MachineRegs.None);
        Row(EngineBuiltinReturnPcSetter, MachineRegs.None, MachineRegs.None);
        Row(EngineCommitIlGuardMethod, MachineRegs.None, MachineRegs.Hb);
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
        Row(EngineRegistersIdenticalMethod, MachineRegs.HeapArray | MachineRegs.RegisterArray, MachineRegs.None);
        Row(EngineRetryMeElseMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineSetB0Method, MachineRegs.None, MachineRegs.B0);
        Row(EngineSetCpMethod, MachineRegs.None, MachineRegs.Cp);
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
        Row(EngineWakeBoundaryCallMethod, MachineRegs.All, MachineRegs.All);
        Row(EngineWakeBoundaryProceedMethod, MachineRegs.All, MachineRegs.All);
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
