using System.Reflection;
using System.Reflection.Emit;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

// ADR-061 stage 1: continuation methods. Besides its delegate, a promoted
// predicate gets one method per entry point (its entry and each continuation
// after a non-tail call) in a collectible assembly. A call, an execute and a
// proceed between such methods are tail calls; anything else goes through the
// dispatch loop as before.
public sealed partial class IlPredicateCompiler
{
    /// <summary>ADR-061: compile continuation methods beside each promoted
    /// predicate's delegate. Off by default; forces region compilation off.</summary>
    public static bool CpsMode { get; set; }
#if NETFRAMEWORK
        = false;
#else
        = Environment.GetEnvironmentVariable("SHUMWAY_IL_CPS") == "1";
#endif

    /// <summary>ADR-061: the largest predicate (bytecode bytes) that gets
    /// continuation methods. <c>SHUMWAY_IL_CPS_MAX</c>.</summary>
    public static int CpsMaxBytecodeBytes { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("SHUMWAY_IL_CPS_MAX"), out int max) ? max : 4096;

    /// <summary>ADR-061: the largest cold method (IL bytes) whose predicate gets
    /// continuation methods. The JIT compiles a method of about 60 000 IL
    /// bytes or 2 000 blocks without optimization.</summary>
    public static int CpsMaxIlBytes { get; set; } = 40_000;

    /// <summary>A predicate's continuation methods: the native code of its entry
    /// and of each continuation, by resume marker.</summary>
    public sealed class CpsCode
    {
        public required nint Entry { get; init; }
        /// <summary>The entry as a delegate, for callers outside continuation
        /// methods (the dispatch loop, bytecode).</summary>
        public required PredicateDelegate EntryDelegate { get; init; }
        public required (int Marker, nint Code)[] Resumes { get; init; }
        /// <summary>The alternatives method, where a choice point of the
        /// predicate resumes; 0 when it pushes none.</summary>
        public nint AltEntry { get; init; }
        /// <summary>The cold method, which the dispatch loop enters at a cursor
        /// from <see cref="ColdCursorBase"/> (a wake's resume, ADR-049).</summary>
        public required PredicateDelegate ColdDelegate { get; init; }
        // Raw code pointers do not keep a collectible assembly alive.
        public required Type KeepAlive { get; init; }
    }

    private sealed class CpsEmitContext
    {
        public required TypeBuilder Type;
        public required PredicateDelegate Base;
        // The stubs that make the indirect tail calls (CpsStubs).
        public required MethodInfo Jump, Fail;
        // The generation's code by cursor, which its choice points carry.
        public required FieldBuilder Alt;
        public int Cursor;
        public readonly List<(int Cursor, string Name)> Methods = new();
        // The cold method: the cursor is its argument; its slow paths call.
        public bool Cold;
        public IlEmit? ColdEmit;
        public IlLabel? ColdDispatch;
        public readonly List<IlLabel?> ColdLabels = new();
        // Instruction boundaries, numbered in emission order: the same number
        // for the same point in every pass.
        public int Boundary;
        public int Current = -1;
        public int Opaque;
        // The method's inline choice point frames (ADR-058), when it holds
        // the control registers.
        public FrameLocals? Frames;
        // Cursors a choice point resumes (entered from Fail, which leaves the
        // frame to restore) and cursors a proceed enters, from the cold pass.
        public readonly HashSet<int> Alternatives = new();
        public readonly HashSet<int> Continuations = new();
        // Where each continuation resumes, from the cold pass, when nothing
        // before that point is reachable from it; and the one the method being
        // emitted starts at.
        public readonly Dictionary<int, CpsResumePoint> ResumePoints = new();
        public CpsResumePoint? Prune;
        public int Pruned;
        public bool Restored;
        // The alternatives method: its cursor is its argument, and it is
        // entered from Fail only, with the frame to restore.
        public bool IsAlt;
        public int ColdIlSize;
        // The cold method's resume entries, which no hot method has.
        public int ColdResumeEntryBytes;
        // ADR-024: a native block was inlined.
        public bool NativeCode;
        // The method's shared register = constant unification.
        public IlLocal? UrcValue, UrcReg, UrcRet, UrcResult;
        public IlLabel? UrcEntry;
        public readonly List<IlLabel> UrcReturns = new();
        // The method's cursor switch, which a local resumption reuses; null
        // with none, or with more than one.
        public IlLabel[]? SwitchLabels;
        public int Switches;
    }

    [ThreadStatic] private static bool _wamCps;
    // The alternatives the method being emitted pushes, and its cursor
    // switches with their entry trampolines (the base delegate's).
    [ThreadStatic] private static HashSet<int>? _methodAlternatives, _methodContinuations;
    [ThreadStatic] private static List<(IlLabel[] Labels, IlLabel[] Entries)>? _resumeEntries;

    /// <summary>ADR-061: while the scope is on, a predicate's choice points are
    /// WAM choice points whose BP is a resume marker (ADR-058), the kind its
    /// continuation methods resume: the delegate and the continuation methods
    /// of one predicate must push the same kind. Off for a dynamic snapshot,
    /// whose choice points keep their snapshot (IL), and for the instrumented
    /// PGO variant.</summary>
    public static WamScope WamChoicePoints(bool on)
    {
        var scope = new WamScope(_wamCps);
        _wamCps = on;
        return scope;
    }

    public readonly struct WamScope : IDisposable
    {
        private readonly bool _prev;
        internal WamScope(bool prev) => _prev = prev;
        public void Dispose() => _wamCps = _prev;
    }

    private static bool WamCps => _wamCps && !DebugMode;

    // Cursors at and above this enter the cold method at an instruction boundary.
    private const int CpsBoundaryBase = 1 << 20;

    // The batch's record of the cold method and of the alternatives method.
    private const int CpsColdCursor = -1, CpsAltCursor = -2;

    [ThreadStatic] private static CpsEmitContext? _cps;

    // MethodImplAttributes.AggressiveOptimization, which .NET Framework's
    // enum lacks and its runtime ignores.
    private const MethodImplAttributes CpsAggressiveOptimization = (MethodImplAttributes)0x0200;

    // The cold and the alternatives methods compiled, for tests.
    internal static int CpsCompiledColdMethods, CpsCompiledAlternativesMethods;

    // A predicate's code must stay alive while any activation's table points
    // into it; stage 1 keeps every generation.
    private static readonly List<Type> CpsGenerations = new();

    /// <summary>Emits <paramref name="predicate"/>'s continuation methods, whose
    /// choice points resume through <paramref name="baseDelegate"/>. Null when
    /// the mode is off or the predicate cannot have them.</summary>
    public CpsCode? CompileCps(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, PredicateDelegate baseDelegate)
    {
        if (!CpsMode || DebugMode) return null;
        var type = CpsTypeBuilder(predicate.FunctorId);
        var layout = EmitCps(type, predicate, calleeMap, baseDelegate, "Alt", (CpsStubs.Jump, CpsStubs.Fail));
        if (layout is null) return null;
        var created = type.CreateType()!;
        var code = BindCps(created, layout, predicate.FunctorId);
        lock (CpsGenerations) CpsGenerations.Add(created);
        return code;
    }

    /// <summary>Emits a predicate's continuation methods into a persisted
    /// assembly's type. Null, with nothing emitted, when the predicate cannot
    /// have them; the loader binds the layout (<see cref="BindCps"/>).</summary>
    public CpsLayout? EmitPersistedCps(TypeBuilder type, CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, int slot)
    {
        if (!CpsMode || DebugMode || _persistPatches is not { } patches) return null;
        // A method left half emitted in the persisted type would fail the whole
        // assembly: a first pass into a scratch type proves the emission, and
        // its patch sites are dropped.
        int sites = patches.Count, sentinel = _persistNextSentinel;
        CpsLayout? trial;
        _persistScratch = true;
        try
        {
            trial = EmitCps(CpsTypeBuilder(predicate.FunctorId), predicate, calleeMap,
                static (_, _) => false, "Alt", (CpsStubs.Jump, CpsStubs.Fail));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IlEmitException)
        {
            trial = null;
        }
        finally
        {
            _persistScratch = false;
            patches.RemoveRange(sites, patches.Count - sites);
            _persistNextSentinel = sentinel;
        }
        if (trial is null) return null;
        // The assembly carries its own stubs, the process's being dynamic.
        if (_persistedStubs is not { } stubs || stubs.Type != type)
        {
            var (jump, fail) = CpsStubs.Define(type, PersistedStubPrefix);
            _persistedStubs = stubs = (type, jump, fail);
        }
        return EmitCps(type, predicate, calleeMap, static (_, _) => false, $"Alt_{slot}",
            (stubs.Jump, stubs.Fail));
    }

    /// <summary>Where a predicate's continuation methods are in their type: a
    /// method name per cursor (the cold method and the alternatives method at
    /// their own), and the cursors the alternatives method enters.</summary>
    public sealed class CpsLayout
    {
        public required (int Cursor, string Method)[] Methods { get; init; }

        /// <summary>The cold method: the predicate at any cursor. A bundle
        /// that keeps its bytecode binds it as the predicate's delegate.</summary>
        public string ColdMethod
        {
            get
            {
                foreach (var (cursor, method) in Methods)
                    if (cursor == CpsColdCursor) return method;
                throw new InvalidOperationException("ADR-061: a layout with no cold method.");
            }
        }

        public required int[] Alternatives { get; init; }
        public required string AltField { get; init; }
    }

    [ThreadStatic] private static (TypeBuilder Type, MethodInfo Jump, MethodInfo Fail)? _persistedStubs;
    private const string PersistedStubPrefix = "Cps";

    private CpsLayout? EmitCps(TypeBuilder type, CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, PredicateDelegate baseDelegate,
        string altField, (MethodInfo Jump, MethodInfo Fail) stubs)
    {
        // Each predicate is emitted two or more extra times, the cold method
        // with a label per instruction: a large one stays on its delegate.
        if (predicate.BytecodeUnfused.Length > CpsMaxBytecodeBytes) return null;
        var alt = type.DefineField(altField, typeof(nint[]), FieldAttributes.Public | FieldAttributes.Static);
        var ctx = new CpsEmitContext
        {
            Type = type, Base = baseDelegate, Alt = alt, Jump = stubs.Jump, Fail = stubs.Fail,
        };
        var prev = _cps;
        _cps = ctx;
        try
        {
            // The cold method first: it records the continuations, and the
            // hot methods' slow paths call it.
            ctx.Cold = true;
            Compile(predicate, calleeMap);
            ctx.Cold = false;
            // Past the JIT's size limits a method compiles without
            // optimization (MinOpts): the predicate stays on its delegate.
            if (ctx.ColdIlSize > CpsMaxIlBytes) return null;
            // ADR-024: an inlined native block calls into the embedding layer
            // and the program's interop types, which a collectible assembly
            // cannot reach; the delegate (a DynamicMethod) can.
            if (ctx.NativeCode) return null;
            // A method per continuation, the cursor a callee's proceed or a
            // builtin's return enters; the alternatives of the choice points
            // share one, whose cursor is its argument: a method per alternative
            // made a predicate with many (inlined facts) take too long to
            // compile. A cursor that only fills the switch (an inlined call's)
            // gets none; one entered all the same resumes through the delegate.
            var cursors = ctx.Continuations.Where(c => c != 0).ToArray();
            ctx.Cursor = 0;
            Compile(predicate, calleeMap);
            foreach (int c in cursors)
            {
                ctx.Cursor = c;
                Compile(predicate, calleeMap);
            }
            if (ctx.Alternatives.Count > 0)
            {
                ctx.IsAlt = true;
                Compile(predicate, calleeMap);
                ctx.IsAlt = false;
            }
        }
        finally { _cps = prev; }
        CpsPrunedMethods[predicate.FunctorId] = ctx.Pruned;
        return new CpsLayout
        {
            Methods = ctx.Methods.ToArray(),
            Alternatives = ctx.Alternatives.OrderBy(c => c).ToArray(),
            AltField = altField,
        };
    }

    /// <summary>The continuation methods of <paramref name="functorId"/> in a
    /// created type, as the dispatch reaches them: native code by cursor, the
    /// entry and the cold method as delegates; the alternatives' table filled.
    /// Every method is compiled here, on the caller's thread (the compile
    /// worker's): one left for its first call would compile on the engine's.</summary>
    public static CpsCode BindCps(Type created, CpsLayout layout, int functorId)
    {
        // A bundle's type carries its own tail-call stubs.
        if (created.GetMethod(PersistedStubPrefix + "Jump") is { } jump)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(jump.MethodHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(
                created.GetMethod(PersistedStubPrefix + "Fail")!.MethodHandle);
        }
        nint entry = 0, altEntry = 0;
        PredicateDelegate? entryDelegate = null, coldDelegate = null;
        var resumes = new List<(int, nint)>();
        int maxCursor = 0;
        foreach (int c in layout.Alternatives) maxCursor = Math.Max(maxCursor, c);
        var byCursor = new nint[maxCursor + 1];
        foreach (var (cursor, name) in layout.Methods)
        {
            var method = created.GetMethod(name)
                ?? throw new InvalidOperationException($"ADR-061: no continuation method {name}.");
            var handle = method.MethodHandle;
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(handle);
            if (cursor == CpsColdCursor)   // its hot methods, and a wake's resume
            {
                coldDelegate = (PredicateDelegate)method.CreateDelegate(typeof(PredicateDelegate));
                Interlocked.Increment(ref CpsCompiledColdMethods);
                continue;
            }
            // The native code, not the precode stub (one jump less per transfer).
            nint code = handle.GetFunctionPointer();
            if (cursor == CpsAltCursor)
            {
                Interlocked.Increment(ref CpsCompiledAlternativesMethods);
                altEntry = code;
                foreach (int c in layout.Alternatives) byCursor[c] = code;
                continue;
            }
            if (cursor == 0)
            {
                entry = code;
                entryDelegate = (PredicateDelegate)method.CreateDelegate(typeof(PredicateDelegate));
            }
            else resumes.Add((Activation.EncodeResumeMarker(functorId, cursor), code));
        }
        created.GetField(layout.AltField)!.SetValue(null, byCursor);
        return new CpsCode
        {
            Entry = entry, EntryDelegate = entryDelegate!, Resumes = resumes.ToArray(), KeepAlive = created,
            AltEntry = altEntry, ColdDelegate = coldDelegate!,
        };
    }

    private static TypeBuilder CpsTypeBuilder(int functorId)
    {
        var ab = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"ShumwayCps_{functorId}_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.RunAndCollect);
        // The runtime emitter reaches engine internals, as a DynamicMethod may.
        var ignores = typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute)
            .GetConstructor(new[] { typeof(string) })!;
        foreach (var asm in new[] { typeof(Activation).Assembly, typeof(IlPredicateCompiler).Assembly,
                     typeof(Shumway.Builtins.BuiltinsRegistry).Assembly, typeof(CompiledPredicate).Assembly })
            ab.SetCustomAttribute(new CustomAttributeBuilder(ignores, new object[] { asm.GetName().Name! }));
        var module = ab.DefineDynamicModule(ab.GetName().Name!);
        return module.DefineType("Cps", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
    }

    /// <summary>Resets what one method's emission gathers for its finish.</summary>
    private static void BeginMethodEmission()
    {
        _methodAlternatives = new HashSet<int>();
        _methodContinuations = new HashSet<int>();
        _resumeEntries = new List<(IlLabel[], IlLabel[])>();
        ResetWakeEmission();
    }

    /// <summary>A new method for a predicate's code: a <c>DynamicMethod</c>, or,
    /// while continuation methods are emitted, a method of the batch's type
    /// whose cursor argument is fixed to the continuation it enters.</summary>
    private IlEmit NewPredicateEmit(string name)
    {
        var cps = _cps;
        BeginMethodEmission();
        if (cps is null)
            return IlEmit.NewDynamicMethod(name, record: IlDumpPath is not null);
        cps.Boundary = 0;
        cps.Current = -1;
        cps.Opaque = 0;
        cps.Frames = null;
        cps.UrcValue = cps.UrcReg = cps.UrcRet = cps.UrcResult = null;
        cps.UrcEntry = null;
        cps.UrcReturns.Clear();
        cps.Restored = false;
        cps.ColdResumeEntryBytes = 0;
        cps.SwitchLabels = null;
        cps.Switches = 0;
        cps.Prune = CpsPruneContinuations && !cps.Cold && !cps.IsAlt
            && cps.ResumePoints.TryGetValue(cps.Cursor, out var rp)
            ? rp with { State = CpsPruneState.Pending } : null;
        var emit = IlEmit.BuildMethod(cps.Type,
            cps.Cold ? $"{name}_cold" : cps.IsAlt ? $"{name}_alt" : $"{name}_c{cps.Cursor}",
            MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Standard,
            record: IlDumpPath is not null);
        if (cps.Cold)
        {
            cps.ColdEmit = emit;
            cps.ColdDispatch = emit.DefineLabel("cps_cold_dispatch");
            return emit;
        }
        if (cps.IsAlt) return emit;
        // The body's cursor switch folds to the one continuation this method enters.
        emit.LoadConstant(cps.Cursor);
        emit.StoreArgument(1);
        return emit;
    }

    /// <summary>Finishes a method from <see cref="NewPredicateEmit"/>; under
    /// continuation emission the result is the base delegate, which the body's
    /// choice points resume through.</summary>
    private bool TryFinishCps(IlEmit emit, string header, out PredicateDelegate del)
    {
        var cps = _cps;
        if (cps is null) { del = null!; return false; }
        EmitSharedUnifyRegisterWithCell(emit, cps);
        if (cps.Frames is { } frames) EmitFramePushLadder(emit, frames);
        if (cps.IsAlt && !cps.Restored)
            throw new InvalidOperationException("ADR-061: the alternatives method has no restore.");
        if (cps.Cold)
        {
            // The boundary dispatch, after the body so that every label exists.
            var none = emit.DefineLabel("cps_cold_none");
            emit.MarkLabel(cps.ColdDispatch!);
            emit.LoadArgument(1);
            emit.LoadConstant(CpsBoundaryBase);
            emit.Subtract();
            var targets = new IlLabel[Math.Max(1, cps.ColdLabels.Count)];
            for (int i = 0; i < targets.Length; i++)
                targets[i] = i < cps.ColdLabels.Count && cps.ColdLabels[i] is { } l ? l : none;
            emit.Switch(targets);
            emit.MarkLabel(none);
            emit.LoadConstant(false);
            EmitReturn(emit);
        }
        EmitSharedColdExit(emit, cps.ColdEmit);
        if (cps.Prune is not null)
        {
            EmitUnmarkedLabels(emit);
            cps.Pruned++;
        }
        DumpIl(emit, header);
        var method = emit.CreateMethod();
        method.SetImplementationFlags(CpsAggressiveOptimization);
        // The size limit is on a body the hot methods repeat: the resume
        // entries are the cold method's alone.
        if (cps.Cold) cps.ColdIlSize = method.GetILGenerator().ILOffset - cps.ColdResumeEntryBytes;
        if (_persistPatches is not null && !_persistScratch)
            _persistIlBytes += method.GetILGenerator().ILOffset;
        method.InitLocals = ZeroLocals;
        cps.Methods.Add((cps.Cold ? CpsColdCursor : cps.IsAlt ? CpsAltCursor : cps.Cursor, method.Name));
        del = cps.Base;
        return true;
    }

    /// <summary>At a non-tail call site under continuation emission: records the
    /// continuation's cursor for the batch.</summary>
    private static void CpsRecordContinuation(int cursor)
    {
        _methodContinuations?.Add(cursor);
        if (_cps is { Cold: true } c) c.Continuations.Add(cursor);
    }

    /// <summary>At a push of a choice point that resumes this predicate at
    /// <paramref name="cursor"/>: that cursor's method restores the frame.</summary>
    private static void CpsRecordAlternative(int cursor)
    {
        _methodAlternatives?.Add(cursor);
        if (_cps is { Cold: true } c) c.Alternatives.Add(cursor);
    }

    private static bool CpsEmitting => _cps is not null;

    private enum CpsPruneState { Pending, Active, Done }

    /// <summary>A continuation's resume point: the code and the pc after its
    /// call, the call's site index, and the instruction boundary that follows.
    /// A continuation method with one emits from there only: nothing before it
    /// can be reached, the clause ends after it without a branch back to the
    /// predicate's entry, and the alternatives method holds the rest.</summary>
    private sealed record CpsResumePoint(byte[] Code, int PcAfter, int SiteIdx, int Boundary)
    {
        public CpsPruneState State { get; set; }
    }

    /// <summary>At a non-tail call in the cold pass, after its resume label:
    /// records the resume point when the continuation's method can start
    /// there (outside a construct that keeps state in IL locals, with the
    /// rest of the clause in this emission and no self tail call in it).</summary>
    private static void CpsRecordResumePoint(int cursor, byte[] code, int pcAfter, int end, int siteIdx,
        bool eligible, IReadOnlyList<CallSite> callSites, int selfFunctorId, bool selfLoops)
    {
        if (_cps is not { Cold: true } c || !eligible || c.Opaque > 0 || pcAfter >= end) return;
        int last = -1;
        for (int p = pcAfter; p < end; p += OpcodeTable.Get((Opcode)code[p]).Size) last = p;
        if (last < 0 || (Opcode)code[last] is not (Opcode.Proceed or Opcode.Execute or Opcode.ExecuteIl
                or Opcode.ExecuteBytecode or Opcode.ExecuteBuiltin or Opcode.DeallocateProceed
                or Opcode.DeallocateExecute or Opcode.CutDeallocateProceed or Opcode.CutProceed or Opcode.Halt))
            return;
        // A self tail call branches to the predicate's entry, from which the
        // code before this point is reachable again.
        if (selfLoops)
            foreach (var cs in callSites)
                if (cs.IsExecute && cs.CalleeFunctorId == selfFunctorId
                    && cs.OpcodeOffset >= pcAfter && cs.OpcodeOffset < end)
                    return;
        c.ResumePoints[cursor] = new CpsResumePoint(code, pcAfter, siteIdx, c.Boundary);
    }

    /// <summary>The start of a pruned continuation method's clause emission:
    /// the site counter and the boundary numbering where the cold pass had
    /// them at the resume point, and the resume label the method's entry
    /// branches to. Returns the pc to emit from.</summary>
    private static int CpsEnterResumePoint(IlEmit emit, CpsResumePoint rp,
        Func<int>? callSiteIndexCounter, IlLabel[]? resumeLabels)
    {
        if (callSiteIndexCounter is null || resumeLabels is null)
            throw new InvalidOperationException("ADR-061: a resume point needs the site counter.");
        int got = 0;
        while (got < rp.SiteIdx) got = callSiteIndexCounter();
        if (got != rp.SiteIdx)
            throw new InvalidOperationException("ADR-061: the site counter passed the resume point.");
        _cps!.Boundary = rp.Boundary;
        _cps.Current = -1;
        emit.MarkLabel(resumeLabels[rp.SiteIdx - 1]);
        return rp.PcAfter;
    }

    /// <summary>Per predicate, its continuation methods emitted from their
    /// resume point in the last batch that compiled.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> CpsPrunedMethods = new();

    /// <summary>In a continuation method that starts at its resume point, a
    /// clause in [start, end) that does not hold the point: its code, the
    /// dispatch to it included, cannot be reached.</summary>
    private static bool CpsPrunedAway(int start, int end)
        => _cps?.Prune is { } rp && !(rp.PcAfter > start && rp.PcAfter < end);

    private static bool CpsPruning => _cps?.Prune is not null;

    private static readonly ConstructorInfo InvalidOperationCtor =
        typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!;

    /// <summary>A pruned method's labels that the code left out would have
    /// marked, marked at a throw: a method with any unmarked is rejected, and
    /// no reachable branch may target one.</summary>
    private static void EmitUnmarkedLabels(IlEmit emit)
    {
        var pending = emit.UnmarkedLabels;
        if (pending.Count == 0) return;
        foreach (var l in pending) emit.MarkLabel(l);
        emit.LoadConstant("ADR-061: a continuation method reached code it left out.");
        emit.NewObject(InvalidOperationCtor);
        emit.Throw();
    }

    /// <summary>At the start of an instruction, its stack empty and the
    /// machine's state in the activation: a point the cold method can resume
    /// at, unless a construct that keeps state in IL locals is open.</summary>
    private static void CpsInstructionBoundary(IlEmit emit, bool inRegion)
    {
        var c = _cps;
        if (c is null || inRegion) return;
        int id = c.Boundary++;
        if (c.Opaque > 0)
        {
            c.Current = -1;
            if (c.Cold) c.ColdLabels.Add(null);
            return;
        }
        if (c.Cold)
        {
            var l = emit.DefineLabel($"cps_b{id}");
            emit.MarkLabel(l);
            c.ColdLabels.Add(l);
        }
        c.Current = id;
    }

    /// <summary>In a hot method, a slow path that would call: the cold method
    /// runs the instruction again from its start instead, so the hot method
    /// keeps no call that returns. False where it cannot (the cold method, or
    /// no boundary is valid here): the caller emits the call.</summary>
    private static bool EmitColdExit(IlEmit emit)
    {
        var c = _cps;
        if (c is null || c.Cold || c.Current < 0 || c.Opaque > 0 || c.ColdEmit is null || !CpsColdExits)
            return false;
        if (RegisterFileOf(emit) is { } rf)
        {
            // The method's one exit to the cold method: a site leaves its
            // boundary on the stack and branches there.
            rf.ColdBoundary ??= emit.DeclareLocal<int>("cold_boundary");
            rf.SharedColdExit ??= emit.DefineLabel("shared_cold_exit");
            emit.LoadConstant(CpsBoundaryBase + c.Current);
            emit.Branch(rf.SharedColdExit);
            return true;
        }
        emit.LoadArgument(0);
        emit.LoadConstant(CpsBoundaryBase + c.Current);
        emit.Call(c.ColdEmit);
        emit.Return();
        return true;
    }

    /// <summary>Whether a slow path here leaves for the cold method.</summary>
    private static bool CpsHotExit
        => _cps is { Cold: false, Current: >= 0, ColdEmit: not null, Opaque: 0 } && CpsColdExits;

    private static readonly MethodInfo ArithTryFusedBinIntMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(nameof(Shumway.Builtins.ArithEvalStack.TryFusedBinInt))!;
    private static readonly MethodInfo ArithTryFusedCmpIntMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(nameof(Shumway.Builtins.ArithEvalStack.TryFusedCmpInt))!;

    /// <summary>A fused integer operation in a hot method: the integer result,
    /// delivered by the register, Y-slot or unify intrinsics; anything else
    /// leaves for the cold method, which runs FusedBin.</summary>
    private static void EmitHotFusedBin(IlEmit emit, int packed,
        int aVal, int bVal, int tVal, IlLabel failLabel)
    {
        var rf = RegisterFileOf(emit)!;
        var result = rf.Temp(typeof(long), KU + 2);
        var ok = emit.DefineLabel($"fbin_ok_{NextLabelSeq()}");
        emit.LoadArgument(0);
        emit.LoadConstant((packed >> 24) & 0xFF);
        emit.LoadConstant(packed & 0xFF);
        emit.LoadConstant(aVal);
        emit.LoadConstant((packed >> 8) & 0xFF);
        emit.LoadConstant(bVal);
        emit.LoadLocalAddress(result);
        emit.Call(ArithTryFusedBinIntMethod);
        emit.BranchIfTrue(ok);
        EmitColdExit(emit);
        emit.MarkLabel(ok);
        int tKind = (packed >> 16) & 0xFF;
        void PushResult()
        {
            emit.LoadLocal(result);
            emit.Call(CellIntMethod);
        }
        if (tKind == 5) EmitSetRegisterConst(emit, tVal, PushResult);
        else if (tKind == 6) EmitStoreY(emit, tVal, PushResult);
        else
        {
            emit.LoadArgument(0);
            emit.LoadConstant(tVal);
            PushResult();
            EmitHelperCall(emit, EngineUnifyMethod);
            emit.BranchIfFalse(failLabel);
        }
    }

    /// <summary>A fused integer comparison in a hot method; a non-integer
    /// operand leaves for the cold method, which runs FusedCmp.</summary>
    private static void EmitHotFusedCmp(IlEmit emit, int packed,
        int aVal, int bVal, IlLabel failLabel)
    {
        var rf = RegisterFileOf(emit)!;
        var r = rf.Temp(typeof(int), KU + 2);
        var cold = emit.DefineLabel($"fcmp_cold_{NextLabelSeq()}");
        var ok = emit.DefineLabel($"fcmp_ok_{NextLabelSeq()}");
        emit.LoadArgument(0);
        emit.LoadConstant((packed >> 16) & 0xFF);
        emit.LoadConstant(packed & 0xFF);
        emit.LoadConstant(aVal);
        emit.LoadConstant((packed >> 8) & 0xFF);
        emit.LoadConstant(bVal);
        emit.Call(ArithTryFusedCmpIntMethod);
        emit.StoreLocal(r);
        emit.LoadLocal(r);
        emit.LoadConstant(0);
        emit.BranchIfLess(cold);
        emit.LoadLocal(r);
        emit.BranchIfFalse(failLabel);
        emit.Branch(ok);
        emit.MarkLabel(cold);
        EmitColdExit(emit);
        emit.MarkLabel(ok);
    }

    /// <summary>AllocateHeapUnbound in a hot method, the activation on the
    /// stack: a full heap leaves for the cold method. Leaves the index.</summary>
    private static bool TryEmitHotAllocateHeapUnbound(IlEmit emit, RegisterFile rf)
    {
        if (!CpsHotExit) return false;
        var idx = rf.Temp(typeof(int), KU + 3);
        var heap = rf.Temp(typeof(Cell[]), KU + 1);
        var room = emit.DefineLabel($"ahu_room_{NextLabelSeq()}");
        emit.Pop();
        EmitLoadEngineField(emit, EngHeapTop);
        emit.StoreLocal(idx);
        EmitLoadEngineField(emit, EngHeap);
        emit.StoreLocal(heap);
        emit.LoadLocal(idx);
        emit.LoadLocal(heap);
        emit.LoadLength<Cell>();
        emit.Convert<int>();
        emit.BranchIfLess(room);
        EmitColdExit(emit);
        emit.MarkLabel(room);
        EmitHeapCell(emit, heap, idx, 0, () => EmitRefBitsOf(emit, idx, 0));
        emit.LoadArgument(0);
        emit.LoadLocal(idx);
        emit.LoadConstant(1);
        emit.Add();
        EmitStoreEngineField(emit, EngHeapTop);
        emit.LoadArgument(0);
        emit.LoadArgument(0);
        emit.LoadField(EngCellsAllocated);
        emit.LoadConstant(1L);
        emit.Add();
        emit.StoreField(EngCellsAllocated);
        emit.LoadLocal(idx);
        return true;
    }

    private static readonly FieldInfo CpsGuardBtField = typeof(Activation).GetField(nameof(Activation._cpsGuardBt))!;
    private static readonly FieldInfo CpsGuardXtField = typeof(Activation).GetField(nameof(Activation._cpsGuardXt))!;
    private static readonly FieldInfo CpsGuardHField = typeof(Activation).GetField(nameof(Activation._cpsGuardH))!;
    private static readonly FieldInfo CpsGuardHbField = typeof(Activation).GetField(nameof(Activation._cpsGuardHb))!;
    private static readonly FieldInfo CpsGuardEField = typeof(Activation).GetField(nameof(Activation._cpsGuardE))!;
    private static readonly FieldInfo CpsGuardNextField = typeof(Activation).GetField(nameof(Activation._cpsGuardNext))!;
    private static readonly MethodInfo EngineTryFailIlGuardQuickMethod =
        typeof(Activation).GetMethod(nameof(Activation.TryFailIlGuardQuick))!;

    /// <summary>ADR-061: hot methods leave their slow paths for the cold method.
    /// <c>SHUMWAY_IL_CPS_COLD=0</c> keeps the calls.</summary>
    public static bool CpsColdExits { get; set; } =
        Environment.GetEnvironmentVariable("SHUMWAY_IL_CPS_COLD") != "0";

    /// <summary>ADR-061: a continuation method with a resume point is emitted
    /// from there (CpsResumePoint). <c>SHUMWAY_IL_CPS_PRUNE=0</c> emits the
    /// whole body.</summary>
    public static bool CpsPruneContinuations { get; set; } =
        Environment.GetEnvironmentVariable("SHUMWAY_IL_CPS_PRUNE") != "0";

    /// <summary>The cursor switch of a predicate's method: in the cold method,
    /// a cursor from <see cref="CpsBoundaryBase"/> enters at an instruction boundary.</summary>
    private static void EmitCursorSwitch(IlEmit emit, IlLabel[] labels)
    {
        if (_cps is { } c && c.Switches++ == 0) c.SwitchLabels = labels;
        else if (_cps is { } c2) c2.SwitchLabels = null;
        if (WamCps && _cps is null or { Cold: true } && _resumeEntries is { } entries)
        {
            // The dispatch loop resumes a WAM choice point here, its frame
            // unrestored: an alternative's entry restores it first.
            // One entry per target: a fact enumerator's alternatives share one.
            var wrapped = new IlLabel[labels.Length];
            var byTarget = new Dictionary<IlLabel, IlLabel>();
            wrapped[0] = labels[0];
            for (int i = 1; i < labels.Length; i++)
            {
                if (!byTarget.TryGetValue(labels[i], out var w))
                    byTarget[labels[i]] = w = emit.DefineLabel($"wam_resume_{i}_{NextLabelSeq()}");
                wrapped[i] = w;
            }
            entries.Add((labels, wrapped));
            labels = wrapped;
        }
        CpsColdDispatchCheck(emit);
        // A hot method enters at its own cursor only: a branch, so that the
        // JIT imports what that cursor reaches and not the whole body. Out of
        // range falls through, as the switch does.
        if (_cps is { Cold: false, IsAlt: false } hot)
        {
            if (hot.Cursor < labels.Length)
            {
                emit.Branch(labels[hot.Cursor]);
                // The switch's fall-through follows; the emitter wants a label before it.
                emit.MarkLabel(emit.DefineLabel($"cps_fixed_cursor_{NextLabelSeq()}"));
            }
            return;
        }
        emit.LoadArgument(1);
        emit.Switch(labels);
        EmitWakeCursorCheck(emit);   // ADR-049: out of range, a wake cursor
    }

    private static void CpsColdDispatchCheck(IlEmit emit)
    {
        if (_cps is not { Cold: true, ColdDispatch: { } dispatch }) return;
        emit.LoadArgument(1);
        emit.LoadConstant(CpsBoundaryBase);
        emit.BranchIfGreaterOrEqual(dispatch);
    }

    /// <summary>Opens a construct whose state lives in IL locals across
    /// instructions (a guard, an inlined fact): no boundary inside is valid.</summary>
    private static CpsOpaqueScope CpsOpaque()
    {
        _opaqueDepth++;
        if (_cps is { } c)
        {
            c.Opaque++;
            // Inside, the cold method cannot resume: a slow path must not leave
            // for it at the boundary before the construct, which would run the
            // construct again.
            c.Current = -1;
        }
        return new CpsOpaqueScope(_cps);
    }

    private readonly struct CpsOpaqueScope : IDisposable
    {
        private readonly CpsEmitContext? _ctx;
        private readonly bool _open;

        public CpsOpaqueScope(CpsEmitContext? ctx)
        {
            _ctx = ctx;
            _open = true;
        }

        public void Dispose()
        {
            if (!_open) return;
            _opaqueDepth--;
            if (_ctx is { } c) c.Opaque--;
        }
    }

    private static readonly MethodInfo EngineResumeMarkerOfMethod =
        typeof(Activation).GetMethod(nameof(Activation.ResumeMarkerOf))!;
    private static readonly MethodInfo EnginePushCpWithMarksMethod =
        typeof(Activation).GetMethod(nameof(Activation.PushChoicePointWithMarks))!;

    /// <summary>The resume entries of a delegate or a cold method (see
    /// EmitCursorSwitch): an alternative's restores the frame on top (a
    /// trust), any other branches straight on.</summary>
    private static void EmitResumeEntries(IlEmit emit)
    {
        if (_resumeEntries is not { Count: > 0 } entries) return;
        int start = emit.Size;
        var alts = _methodAlternatives!;
        if (alts.Overlaps(_methodContinuations!))
            throw new NotSupportedException(
                "ADR-061: a cursor is both a choice point's alternative and a continuation.");
        foreach (var (labels, wrapped) in entries)
        {
            var done = new HashSet<IlLabel>();
            for (int i = 1; i < labels.Length; i++)
            {
                if (!done.Add(wrapped[i])) continue;
                // The cursors that share this entry are all alternatives or none.
                bool alt = alts.Contains(i);
                for (int j = i + 1; j < labels.Length; j++)
                    if (wrapped[j] == wrapped[i] && alts.Contains(j) != alt)
                        throw new NotSupportedException(
                            "ADR-061: one label is both an alternative's and a continuation's.");
                emit.MarkLabel(wrapped[i]);
                if (alt)
                {
                    emit.LoadArgument(0);
                    EmitHelperCall(emit, EngineTrustMeMethod);
                }
                emit.Branch(labels[i]);
            }
        }
        entries.Clear();
        if (_cps is { Cold: true } cold) cold.ColdResumeEntryBytes = emit.Size - start;
    }

    /// <summary>ADR-057/058 in the alternatives method: a failure whose top
    /// choice point is a WAM one of this predicate (its BP a resume marker of
    /// the functor) restores it inline (a trust) and runs the alternative here.
    /// Anything else falls through to the Fail stub.</summary>
    private static void EmitCpsResumeOwnWam(IlEmit emit, FrameLocals l,
        IlLabel[] labels)
    {
        var rf = RegisterFileOf(emit)!;
        var b = Reg(emit, MachineRegs.B);
        var cur = emit.DeclareLocal<int>($"cps_own_cur_{NextLabelSeq()}");
        var own = emit.DefineLabel($"cps_own_{NextLabelSeq()}");
        var no = emit.DefineLabel($"cps_own_no_{NextLabelSeq()}");
        var slow = emit.DefineLabel($"cps_own_slow_{NextLabelSeq()}");
        var go = emit.DefineLabel($"cps_own_go_{NextLabelSeq()}");
        EmitResumeOwnFast(emit, l, _emitOwnerFid, cur, own, no);
        emit.Branch(no);
        emit.MarkLabel(own);
        emit.LoadLocal(l.Slow);
        emit.BranchIfTrue(slow);
        EmitFrameRestoreBody(emit, l);
        emit.LoadArgument(0);
        EmitLoadCellInt(emit, l.Ctl, CtlHb);
        EmitStoreEngineField(emit, EngHb);
        EmitStoreRegister(emit, rf, MachineRegs.StackTop, () => emit.LoadLocal(b));
        EmitStoreRegister(emit, rf, MachineRegs.B, () => EmitLoadCellInt(emit, l.Ctl, CtlB));
        emit.Branch(go);
        emit.MarkLabel(slow);
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineTrustMeMethod);
        emit.MarkLabel(go);
        // An alternative may read its cursor (a fact enumerator's index).
        emit.LoadLocal(cur);
        emit.StoreArgument(1);
        emit.LoadLocal(cur);
        emit.Switch(labels);
        emit.MarkLabel(no);
    }

    private static readonly MethodInfo EnginePushIlCpEntryCpsMethod =
        typeof(Activation).GetMethod(nameof(Activation.PushIlChoicePointEntryCps))!;

    /// <summary>At a continuation method's entry, after its registers: the
    /// locals of its inline choice point frames.</summary>
    private static void EmitCpsFrameEntry(IlEmit emit)
    {
        if (_cps is not { } cps) return;
        if (RegisterFileOf(emit) is { } rf && rf.Holds(ControlRegs))
            cps.Frames = EmitFrameLocalsEntry(emit);
        if (!cps.IsAlt) return;
        // Entered from Fail: the trust of the frame on top, inline.
        cps.Restored = true;
        if (cps.Frames is not { } l)
        {
            emit.LoadArgument(0);
            EmitHelperCall(emit, EngineTrustMeMethod);
            return;
        }
        var slow = emit.DefineLabel($"cps_alt_slow_{NextLabelSeq()}");
        var done = emit.DefineLabel($"cps_alt_done_{NextLabelSeq()}");
        var rfl = RegisterFileOf(emit)!;
        var b = Reg(emit, MachineRegs.B);
        emit.LoadLocal(l.Slow);
        emit.BranchIfTrue(slow);
        EmitFrameRestoreBody(emit, l);
        emit.LoadArgument(0);
        EmitLoadCellInt(emit, l.Ctl, CtlHb);
        EmitStoreEngineField(emit, EngHb);
        EmitStoreRegister(emit, rfl, MachineRegs.StackTop, () => emit.LoadLocal(b));
        EmitStoreRegister(emit, rfl, MachineRegs.B, () => EmitLoadCellInt(emit, l.Ctl, CtlB));
        emit.Branch(done);
        emit.MarkLabel(slow);
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineTrustMeMethod);
        emit.MarkLabel(done);
    }

    /// <summary>A push of an IL choice point that resumes this predicate's own
    /// delegate at <paramref name="cursor"/>. In a continuation method the
    /// frame is written inline (ADR-058) and a helper adds the side-stack
    /// entry, with the generation's code by cursor.</summary>
    private static void EmitPushIlChoicePoint(IlEmit emit,
        SelfDelegateEmitter self, Action<IlEmit> cursor, int arity,
        int constCursor = -1, Action<IlEmit>? marker = null)
    {
        if (constCursor >= 0) CpsRecordAlternative(constCursor);
        if (WamCps)
        {
            void Marker()
            {
                if (constCursor >= 0) EmitResumeMarker(emit, _emitOwnerFid, constCursor);
                else if (marker is not null) marker(emit);
                else
                {
                    EmitFunctorId(emit, _emitOwnerFid);
                    cursor(emit);
                    EmitHelperCall(emit, EngineResumeMarkerOfMethod);
                }
            }
            if (_cps is { Frames: { } wl })
            {
                Marker();
                emit.StoreLocal(wl.Marker);
                EmitFramePushSite(emit, wl, arity);
                return;
            }
            emit.LoadArgument(0);
            emit.LoadConstant(arity);
            Marker();
            EmitHelperCall(emit, EnginePushChoicePointMethod);
            return;
        }
        if (_cps is { Frames: { } l } cps)
        {
            emit.LoadConstant(Activation.IlChoicePointSentinelBp);
            emit.StoreLocal(l.Marker);
            EmitFramePushSite(emit, l, arity);
            emit.LoadArgument(0);
            self(emit);
            cursor(emit);
            emit.LoadField(cps.Alt);
            EmitHelperCall(emit, EnginePushIlCpEntryCpsMethod);
            return;
        }
        emit.LoadArgument(0);
        self(emit);
        cursor(emit);
        emit.LoadConstant(arity);
        EmitHelperCall(emit, EnginePushIlCpMethod);
        EmitCpsTagChoicePoint(emit);
    }

    private static readonly MethodInfo EngineTagIlCpCpsMethod =
        typeof(Activation).GetMethod(nameof(Activation.TagIlChoicePointCps))!;

    /// <summary>After a push of a choice point that resumes this predicate's own
    /// delegate: under continuation emission, gives it the generation's code by
    /// cursor, so a failure enters the alternative by a tail call.</summary>
    private static void EmitCpsTagChoicePoint(IlEmit emit)
    {
        if (_cps is not { } cps) return;
        emit.LoadArgument(0);
        emit.LoadField(cps.Alt);
        EmitHelperCall(emit, EngineTagIlCpCpsMethod);
    }

    /// <summary>A failure: under continuation emission, a tail call to the
    /// <c>Fail</c> stub (item 8), which enters the top choice point's
    /// alternative or returns false to the dispatch loop. The failing method
    /// makes no call that returns, so its entry saves no registers for it.</summary>
    private static void EmitFailReturn(IlEmit emit)
    {
        // Only the alternatives method resumes its own choice points: the
        // entry and the continuations keep their switch folded.
        if (_cps is { IsAlt: true, Frames: { } l, SwitchLabels: { } labels } c)
        {
            if (WamCps) EmitCpsResumeOwnWam(emit, l, labels);
            else EmitCpsResumeOwn(emit, c, l, labels);
        }
        if (CpsEmitting)
        {
            emit.LoadArgument(0);
            emit.LoadConstant(0);
            // IlEmit gives a call before a ret the tail. prefix.
            emit.Call(_cps!.Fail);
            emit.Return();
            return;
        }
        emit.LoadConstant(false);
        EmitReturn(emit);
    }

    private static readonly Type IlCpEntryType = typeof(Activation.IlChoicePointEntry);
    private static readonly FieldInfo IlCpStackField =
        typeof(Activation).GetField("_ilCpStack", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo IlCpTopField =
        typeof(Activation).GetField("_ilCpTop", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>ADR-057 in a continuation method: a failure whose top choice
    /// point this generation pushed, here or in another of its methods, runs
    /// the alternative in this method (item 13): the side-stack entry popped,
    /// the frame restored and popped inline, a branch to the cursor's label.
    /// No call that returns; anything else (another code's choice point, the
    /// floor, a debug session, the cancellation countdown run out) falls
    /// through to the Fail stub.</summary>
    private static void EmitCpsResumeOwn(IlEmit emit, CpsEmitContext c,
        FrameLocals l, IlLabel[] labels)
    {
        var rf = RegisterFileOf(emit)!;
        var b = Reg(emit, MachineRegs.B);
        var stub = emit.DefineLabel($"cps_own_no_{NextLabelSeq()}");
        var slow = emit.DefineLabel($"cps_own_slow_{NextLabelSeq()}");
        var go = emit.DefineLabel($"cps_own_go_{NextLabelSeq()}");
        var top = emit.DeclareLocal<int>($"cps_own_top_{NextLabelSeq()}");
        var entry = emit.DeclareLocal(IlCpEntryType, $"cps_own_e_{NextLabelSeq()}");
        var cur = emit.DeclareLocal<int>($"cps_own_cur_{NextLabelSeq()}");
        var countdown = emit.DeclareLocal<int>($"cps_own_cd_{NextLabelSeq()}");

        emit.LoadArgument(0);
        emit.LoadField(IlCpTopField);
        emit.StoreLocal(top);
        emit.LoadLocal(top);
        emit.BranchIfFalse(stub);
        emit.LoadArgument(0);
        emit.LoadField(IlCpStackField);
        emit.LoadLocal(top);
        emit.LoadConstant(1);
        emit.Subtract();
        emit.LoadElement(IlCpEntryType);
        emit.StoreLocal(entry);
        emit.LoadLocalAddress(entry);
        emit.LoadField(typeof(Activation.IlChoicePointEntry).GetField(nameof(Activation.IlChoicePointEntry.Key))!);
        emit.LoadLocal(b);
        emit.UnsignedBranchIfNotEqual(stub);
        emit.LoadLocalAddress(entry);
        emit.LoadField(typeof(Activation.IlChoicePointEntry).GetField(nameof(Activation.IlChoicePointEntry.CpsAlt))!);
        emit.LoadField(c.Alt);
        emit.UnsignedBranchIfNotEqual(stub);
        emit.LoadLocal(b);
        emit.LoadArgument(0);
        emit.LoadField(EngBacktrackFloor);
        emit.BranchIfLessOrEqual(stub);
        emit.LoadArgument(0);
        emit.LoadField(EngDebug);
        emit.BranchIfTrue(stub);
        // BacktrackSafePoint's countdown; when it runs out the stub runs it.
        emit.LoadArgument(0);
        emit.LoadField(EngCancelCountdown);
        emit.LoadConstant(1);
        emit.Subtract();
        emit.StoreLocal(countdown);
        emit.LoadLocal(countdown);
        emit.LoadConstant(0);
        emit.BranchIfLessOrEqual(stub);
        emit.LoadArgument(0);
        emit.LoadLocal(countdown);
        emit.StoreField(EngCancelCountdown);
        // Pop the entry; its references stay until the slot is reused.
        emit.LoadArgument(0);
        emit.LoadLocal(top);
        emit.LoadConstant(1);
        emit.Subtract();
        emit.StoreField(IlCpTopField);
        emit.LoadLocalAddress(entry);
        emit.LoadField(typeof(Activation.IlChoicePointEntry).GetField(nameof(Activation.IlChoicePointEntry.Cursor))!);
        emit.StoreLocal(cur);
        // The trust, inline.
        emit.LoadLocal(l.Slow);
        emit.BranchIfTrue(slow);
        EmitFrameRestoreBody(emit, l);
        emit.LoadArgument(0);
        EmitLoadCellInt(emit, l.Ctl, CtlHb);
        EmitStoreEngineField(emit, EngHb);
        EmitStoreRegister(emit, rf, MachineRegs.StackTop, () => emit.LoadLocal(b));
        EmitStoreRegister(emit, rf, MachineRegs.B, () => EmitLoadCellInt(emit, l.Ctl, CtlB));
        emit.Branch(go);
        emit.MarkLabel(slow);
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineTrustMeMethod);
        emit.MarkLabel(go);
        emit.LoadLocal(cur);
        emit.StoreArgument(1);
        emit.LoadLocal(cur);
        emit.Switch(labels);
        emit.MarkLabel(stub);
    }

    private static readonly MethodInfo EngineCpsCallTargetMethod =
        typeof(Activation).GetMethod(nameof(Activation.CpsCallTarget))!;
    private static readonly MethodInfo EngineCpsProceedTargetMethod =
        typeof(Activation).GetMethod(nameof(Activation.CpsProceedTarget))!;

    /// <summary>Under continuation emission: enters the callee's entry method by
    /// a tail call when the activation allows it. Otherwise a hot method leaves
    /// for the cold one, which takes the dispatch-loop path, and the result is
    /// true: the caller emits no fallback. False: the fallback follows.</summary>
    private static bool EmitCpsCall(IlEmit emit, int calleeFid)
    {
        if (!CpsEmitting) return false;
        emit.LoadArgument(0);
        EmitFunctorId(emit, calleeFid);
        EmitResumeMarker(emit, calleeFid, 0);
        EmitHelperCall(emit, EngineCpsCallTargetMethod);
        EmitCpsJump(emit);
        return EmitColdExit(emit);
    }

    /// <summary>Under continuation emission: enters the continuation Cp names by
    /// a tail call when the activation allows it, else falls through to the
    /// return that follows.</summary>
    private static void EmitCpsProceed(IlEmit emit)
    {
        if (!CpsEmitting) return;
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineCpsProceedTargetMethod);
        EmitCpsJump(emit);
    }

    // The target code is on the stack: jump to it if non-zero.
    private static void EmitCpsJump(IlEmit emit)
    {
        var code = emit.DeclareLocal<nint>($"cps_code_{NextLabelSeq()}");
        var none = emit.DefineLabel($"cps_none_{NextLabelSeq()}");
        emit.StoreLocal(code);
        emit.LoadLocal(code);
        emit.BranchIfFalse(none);
        emit.LoadArgument(0);
        emit.LoadConstant(0);
        emit.LoadLocal(code);
        // IlEmit gives a call before a ret the tail. prefix; the stub's own
        // tail. calli is emitted by hand.
        emit.Call(_cps!.Jump);
        emit.Return();
        emit.MarkLabel(none);
    }

    /// <summary>The one method that makes the indirect tail call: <see cref="IlEmit"/>
    /// has no <c>calli</c>.</summary>
    private static class CpsStubs
    {
        private static readonly (MethodInfo Jump, MethodInfo Fail) Stubs = Build();
        public static readonly MethodInfo Jump = Stubs.Jump;
        public static readonly MethodInfo Fail = Stubs.Fail;

        private static (MethodInfo Jump, MethodInfo Fail) Build()
        {
            var ab = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("ShumwayCpsStubs"), AssemblyBuilderAccess.Run);
            var type = ab.DefineDynamicModule("ShumwayCpsStubs").DefineType("CpsStubs",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            Define(type, "");
            var stubs = type.CreateType()!;
            var (jump, fail) = (stubs.GetMethod("Jump")!, stubs.GetMethod("Fail")!);
            // Compiled where they are built (a compile worker), not at their first call.
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(jump.MethodHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(fail.MethodHandle);
            return (jump, fail);
        }

        /// <summary>Defines the stubs on <paramref name="type"/>, their names
        /// prefixed.</summary>
        internal static (MethodInfo Jump, MethodInfo Fail) Define(TypeBuilder type, string prefix)
        {
            var m = type.DefineMethod(prefix + "Jump", MethodAttributes.Public | MethodAttributes.Static,
                typeof(bool), new[] { typeof(Activation), typeof(int), typeof(nint) });
            m.SetImplementationFlags(CpsAggressiveOptimization);
            var il = m.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Tailcall);
            il.EmitCalli(OpCodes.Calli, CallingConventions.Standard, typeof(bool),
                new[] { typeof(Activation), typeof(int) }, null);
            il.Emit(OpCodes.Ret);

            // Fail(a, _): the alternatives method of the top choice point by
            // a tail call, with its cursor (its entry restores the frame);
            // false when the dispatch loop must backtrack.
            var f = type.DefineMethod(prefix + "Fail", MethodAttributes.Public | MethodAttributes.Static,
                typeof(bool), new[] { typeof(Activation), typeof(int) });
            f.SetImplementationFlags(CpsAggressiveOptimization);
            il = f.GetILGenerator();
            var code = il.DeclareLocal(typeof(nint));
            var none = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(Activation).GetMethod(nameof(Activation.CpsFailTarget))!);
            il.Emit(OpCodes.Stloc, code);
            il.Emit(OpCodes.Ldloc, code);
            il.Emit(OpCodes.Brfalse, none);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, typeof(Activation).GetField(nameof(Activation._cpsFailCursor))!);
            il.Emit(OpCodes.Ldloc, code);
            il.Emit(OpCodes.Tailcall);
            il.EmitCalli(OpCodes.Calli, CallingConventions.Standard, typeof(bool),
                new[] { typeof(Activation), typeof(int) }, null);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(none);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            return (m, f);
        }
    }
}
