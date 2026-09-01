using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

public sealed partial class IlPredicateCompiler
{
    /// <summary>Enable native-block inlining for subsequent compiles on this
    /// thread (see <see cref="_nativeInline"/>). Returns the previous context so
    /// the caller can restore it. Static — the context is thread-static, so it must
    /// be set on whichever thread actually runs the Sigil emit.</summary>
    public static Shumway.Compiler.NativeC.NativeInlineContext? BeginNativeInline(
        Shumway.Compiler.NativeC.NativeInlineContext? context)
    {
        var prev = _nativeInline;
        _nativeInline = context;
        return prev;
    }

    public static void EndNativeInline(Shumway.Compiler.NativeC.NativeInlineContext? restore)
        => _nativeInline = restore;

    /// <summary>The float-literal pool the predicate currently being compiled
    /// indexes (its own module's <c>FloatLiterals</c>), set by the driver before
    /// compiling. <c>get_float</c>/<c>put_float</c> resolve their <c>literalId</c>
    /// against this and bake the VALUE as an <c>ldc.r8</c> constant — so the IL is
    /// process-independent (no patch needed for persisted bundles). When null,
    /// the float opcodes report as unsupported and the predicate stays Tier-0
    /// (safe fallback). Thread-static: set on whichever thread runs the emit.</summary>
    [System.ThreadStaticAttribute]
    private static System.Collections.Generic.IReadOnlyList<double>? _ilFloatPool;

    /// <summary>Set the float-literal pool for subsequent compiles on this thread
    /// (see <see cref="_ilFloatPool"/>). Returns the previous pool to restore.</summary>
    public static System.Collections.Generic.IReadOnlyList<double>? BeginFloatPool(
        System.Collections.Generic.IReadOnlyList<double>? pool)
    {
        var prev = _ilFloatPool;
        _ilFloatPool = pool;
        return prev;
    }

    public static void EndFloatPool(System.Collections.Generic.IReadOnlyList<double>? restore)
        => _ilFloatPool = restore;

    /// <summary>True iff a <c>get_float</c>/<c>put_float</c> at <paramref name="pc"/>
    /// can be resolved against the current <see cref="_ilFloatPool"/> — the gate the
    /// opcode-support switches consult so the float opcodes are accepted only when
    /// the value can actually be baked.</summary>
    private static bool FloatLiteralResolvable(byte[] code, int pc)
    {
        if (_ilFloatPool is null) return false;
        int literalId = BytecodeIO.ReadInt32(code, pc + 1);
        return (uint)literalId < (uint)_ilFloatPool.Count;
    }

    /// <summary>ADR-022 item 2 — total native blocks inlined into IL across all
    /// compiles on this process (test/diagnostic observability: distinguishes a
    /// real inline from a fall-back to builtin dispatch, which is otherwise
    /// behaviourally identical).</summary>
    public static int NativeBlocksInlined;

    /// <summary>Begin a persisted-emit batch. Subsequent
    /// <see cref="EmitPersistedMethod"/> calls (until
    /// <see cref="EndPersistEmit"/>) accumulate patch sites into the
    /// returned list.</summary>
    public List<IlPatchSite> BeginPersistEmit()
    {
        var list = new List<IlPatchSite>();
        _persistPatches = list;
        _persistNextSentinel = IlPatchSiteCodec.SentinelBase;
        return list;
    }

    /// <summary>End the persisted-emit batch. Subsequent emits revert
    /// to direct-id mode (the runtime <c>DynamicMethod</c> path).</summary>
    public void EndPersistEmit()
    {
        _persistPatches = null;
    }

    private static void EmitAtomId(Sigil.Emit<PredicateDelegate> emit, int atomId)
    {
        if (_persistPatches is null) { emit.LoadConstant(atomId); return; }
        string? name = Shumway.Core.AtomTable.GetById(atomId)?.Name;
        if (name is null)
            throw new InvalidOperationException(
                $"Persisted IL emit: atom id {atomId} has no name in the build-process AtomTable.");
        int sentinel = _persistNextSentinel++;
        _persistPatches.Add(new IlPatchSite
        {
            Sentinel = sentinel,
            Kind = IlPatchKind.Atom,
            Name = name,
            Arity = 0,
        });
        emit.LoadConstant(sentinel);
    }

    private static void EmitFunctorId(Sigil.Emit<PredicateDelegate> emit, int functorId)
    {
        if (_persistPatches is null) { emit.LoadConstant(functorId); return; }
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(functorId);
        string? name = Shumway.Core.AtomTable.GetById(atomId)?.Name;
        if (name is null)
            throw new InvalidOperationException(
                $"Persisted IL emit: functor id {functorId} has no name in the build-process AtomTable.");
        int sentinel = _persistNextSentinel++;
        _persistPatches.Add(new IlPatchSite
        {
            Sentinel = sentinel,
            Kind = IlPatchKind.Functor,
            Name = name,
            Arity = arity,
        });
        emit.LoadConstant(sentinel);
    }

    private static void EmitResumeMarker(
        Sigil.Emit<PredicateDelegate> emit, int functorId, int cursor)
    {
        if (_persistPatches is null)
        {
            emit.LoadConstant(Shumway.Core.Activation.EncodeResumeMarker(functorId, cursor));
            return;
        }
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(functorId);
        string? name = Shumway.Core.AtomTable.GetById(atomId)?.Name;
        if (name is null)
            throw new InvalidOperationException(
                $"Persisted IL emit: resume marker functor id {functorId} has no name.");
        int sentinel = _persistNextSentinel++;
        _persistPatches.Add(new IlPatchSite
        {
            Sentinel = sentinel,
            Kind = IlPatchKind.ResumeMarker,
            Name = name,
            Arity = arity,
            Cursor = cursor,
        });
        emit.LoadConstant(sentinel);
    }

    private static void EmitBuiltinId(Sigil.Emit<PredicateDelegate> emit, int builtinId)
    {
        if (_persistPatches is null) { emit.LoadConstant(builtinId); return; }
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        int sentinel = _persistNextSentinel++;
        _persistPatches.Add(new IlPatchSite
        {
            Sentinel = sentinel,
            Kind = IlPatchKind.Builtin,
            Name = entry.Name,
            Arity = entry.Arity,
        });
        emit.LoadConstant(sentinel);
    }

    private static void EmitClauseBody(
        Sigil.Emit<PredicateDelegate> emit, byte[] code, int start, int end,
        Sigil.Label failLabel, IReadOnlyList<CallSite> callSites,
        Func<int>? callSiteIndexCounter = null,
        Sigil.Label[]? resumeLabels = null,
        SelfDelegateEmitter? emitSelfDelegate = null,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null,
        bool suppressProceedReturn = false,
        int cursorBase = 1,
        int selfFunctorId = -1,
        Sigil.Label? selfTailLabel = null,
        bool resetCursorBeforeSelfTail = false,
        IReadOnlyDictionary<int, InlineSite>? inlineSites = null,
        IReadOnlyDictionary<int, CompiledPredicate>? ruleInlineSites = null,
        RegionEmitContext? regionCtx = null,
        bool forceLeafRuleInline = false,
        string localSalt = "",
        GuardContEmitContext? guardContCtx = null)
    {
        // In region mode every member is emitted into ONE shared IL method, so a
        // pc-based local name (unique within a single predicate, where pc starts at
        // 0) collides across members — two members each with, say, a put_variable at
        // pc 0 would both declare `freshRef_pc0`. Salt every per-member-emitted local
        // name with the member index so the shared method's local namespace stays
        // collision-free (the N-methods-vs-1-method merge is opaque to the engine —
        // this is the local-naming half of making that true).
        // localSalt (ADR-031): the explicit salt for regionCtx-null emissions that
        // still share a method with other bytecode — a tier-G guard slice inside a
        // region method, or an inlined callee body (whose callee-relative pcs
        // collide with the caller's own pc-named locals).
        string lt = regionCtx is null ? localSalt : $"_rm{regionCtx.CurrentMemberIndex}";
        int pc = start;
        // ADR-022 item 2 — the atom most recently put into argument register 0, or
        // -1. A `'$native_run'('$nb$…', regs)` goal loads its block-name atom into
        // A0 just before the call (see the disasm in NativeBundleTests); tracking it
        // lets the CallBuiltin handler recover which block to inline.
        int regZeroAtom = -1;
        // ADR-025 stage (b) — inline-ITE labels. `jump` targets (the END join)
        // get a label marked when the walk reaches the address; the ELSE
        // resume label (a cursor-switch target) is marked at the trust_me.
        Dictionary<int, Sigil.Label>? jumpLabels = null;
        Dictionary<int, Sigil.Label>? iteElseLabels = null;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            if (jumpLabels is not null && jumpLabels.Remove(pc, out var joinLabel))
            {
                // The END join is reachable from the then-branch's jump AND by
                // falling through from the else branch; register state tracked
                // across the join is branch-dependent → reset.
                emit.MarkLabel(joinLabel);
                regZeroAtom = -1;
            }
            // Region compilation (Stage 3): a member block's proceed /
            // intra-region call become br's into the shared region method instead
            // of returning to the dispatch loop. Handled before the normal opcode
            // switch so the region layout takes precedence.
            if (regionCtx is not null
                && TryEmitRegionOpcode(emit, code, pc, op, regionCtx, ref pc))
                continue;
            if (op == Opcode.Meta)
            {
                // Dbg-info Meta opcode — runtime no-op. Skip
                // the 6 bytes (opcode + sub-byte + 4-byte payload) without
                // emitting any IL.
                pc += 6;
                continue;
            }
            if (op == Opcode.GetAtom)
            {
                int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                int regIdx = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(regIdx);
                EmitAtomId(emit, atomId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineUnifyMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetInteger)
            {
                int value = BytecodeIO.ReadInt32(code, pc + 1);
                int regIdx = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(regIdx);
                emit.LoadConstant((long)value);
                emit.Call(CellIntMethod);
                emit.Call(EngineUnifyMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetFloat)
            {
                // unify reg with Cell.Ref(MakeFloat(value)) — the value baked as
                // an ldc.r8 constant (resolved from the predicate's pool), so the
                // emitted IL is process-independent.
                int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                int regIdx = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(regIdx);
                emit.LoadArgument(0);
                emit.LoadConstant(_ilFloatPool![literalId]);
                emit.Call(EngineMakeFloatMethod);
                emit.Call(CellRefMethod);
                emit.Call(EngineUnifyMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetNil)
            {
                int regIdx = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(regIdx);
                emit.LoadConstant(AtomTable.EmptyListId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineUnifyMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetValueX)
            {
                int srcReg = BytecodeIO.ReadInt32(code, pc + 1);
                int argReg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(srcReg);
                emit.LoadConstant(argReg);
                emit.Call(EngineUnifyRegistersMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetVariableX)
            {
                // X[dest] := X[arg]
                int dest = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(dest);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.Call(EngineGetRegisterMethod);
                emit.Call(EngineSetRegisterMethod);
#if DEBUG
                if (DebugMode)
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(_emitOwnerFid);
                    emit.LoadConstant(dest);
                    emit.LoadConstant(arg);
                    emit.LoadConstant(pc);
                    emit.Call(DbgCheckGetVariableXMethod);
                }
#endif
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetVariableY)
            {
                // Y[slot] := X[arg]
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.Call(EngineGetRegisterMethod);
                emit.Call(EngineSetYMethod);
#if DEBUG
                if (DebugMode)
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(_emitOwnerFid);
                    emit.LoadConstant(slot);
                    emit.LoadConstant(arg);
                    emit.LoadConstant(pc);
                    emit.Call(DbgCheckGetVariableYMethod);
                }
#endif
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetValueY)
            {
                // unify(Y[slot], X[arg])
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineGetYMethod);
                emit.Call(EngineUnifyMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutAtom)
            {
                int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                if (arg == 0) regZeroAtom = atomId;   // ADR-022 — track A0 for $native_run
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                EmitAtomId(emit, atomId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutInteger)
            {
                int value = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadConstant((long)value);
                emit.Call(CellIntMethod);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutFloat)
            {
                // reg := Cell.Ref(MakeFloat(value)); value baked as ldc.r8.
                int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadArgument(0);
                emit.LoadConstant(_ilFloatPool![literalId]);
                emit.Call(EngineMakeFloatMethod);
                emit.Call(CellRefMethod);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutNil)
            {
                int arg = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadConstant(AtomTable.EmptyListId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutValueX)
            {
                // X[arg] := X[src]
                int src = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadArgument(0);
                emit.LoadConstant(src);
                emit.Call(EngineGetRegisterMethod);
                emit.Call(EngineSetRegisterMethod);
#if DEBUG
                if (DebugMode)
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(_emitOwnerFid);
                    emit.LoadConstant(src);
                    emit.LoadConstant(arg);
                    emit.LoadConstant(pc);
                    emit.Call(DbgCheckPutValueXMethod);
                }
#endif
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutValueY)
            {
                // X[arg] := Y[slot]
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineGetYMethod);
                emit.Call(EngineSetRegisterMethod);
#if DEBUG
                if (DebugMode)
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(_emitOwnerFid);
                    emit.LoadConstant(slot);
                    emit.LoadConstant(arg);
                    emit.LoadConstant(pc);
                    emit.Call(DbgCheckPutValueYMethod);
                }
#endif
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutVariableX)
            {
                // X[arg] := X[dest] := Cell.Ref(engine.AllocateHeapUnbound())
                int dest = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                // Allocate fresh unbound, save its REF cell in a local, then
                // assign it to both X[dest] and X[arg].
                var refLocal = emit.DeclareLocal<Cell>($"freshRef_pc{pc}{lt}");
                emit.LoadArgument(0);
                emit.Call(EngineAllocateHeapUnboundMethod);
                emit.Call(CellRefMethod);
                emit.StoreLocal(refLocal);
                // X[dest] = local
                emit.LoadArgument(0);
                emit.LoadConstant(dest);
                emit.LoadLocal(refLocal);
                emit.Call(EngineSetRegisterMethod);
                // X[arg] = local
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadLocal(refLocal);
                emit.Call(EngineSetRegisterMethod);
#if DEBUG
                if (DebugMode)
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(_emitOwnerFid);
                    emit.LoadConstant(dest);
                    emit.LoadConstant(arg);
                    emit.LoadConstant(pc);
                    emit.Call(DbgCheckPutVariableXMethod);
                }
#endif
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutVariableY)
            {
                // Y[slot] := X[arg] := Cell.Ref(engine.AllocateHeapUnbound())
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                var refLocal = emit.DeclareLocal<Cell>($"freshRefY_pc{pc}{lt}");
                emit.LoadArgument(0);
                emit.Call(EngineAllocateHeapUnboundMethod);
                emit.Call(CellRefMethod);
                emit.StoreLocal(refLocal);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.LoadLocal(refLocal);
                emit.Call(EngineSetYMethod);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadLocal(refLocal);
                emit.Call(EngineSetRegisterMethod);
#if DEBUG
                if (DebugMode)
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(_emitOwnerFid);
                    emit.LoadConstant(slot);
                    emit.LoadConstant(arg);
                    emit.LoadConstant(pc);
                    emit.Call(DbgCheckPutVariableYMethod);
                }
#endif
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Allocate)
            {
                int n = BytecodeIO.ReadInt32(code, pc + 1);
#if DEBUG
                if (DebugMode)
                {
                    var preELocal = emit.DeclareLocal<int>($"preE_alloc_pc{pc}{lt}");
                    emit.LoadArgument(0);
                    emit.Call(EngineEGetter);
                    emit.StoreLocal(preELocal);
                    emit.LoadArgument(0);
                    emit.LoadConstant(n);
                    emit.Call(EngineAllocateMethod);
                    emit.LoadArgument(0);
                    emit.LoadConstant(_emitOwnerFid);
                    emit.LoadConstant(n);
                    emit.LoadConstant(pc);
                    emit.LoadLocal(preELocal);
                    emit.Call(DbgCheckAllocateMethod);
                }
                else
#endif
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(n);
                    emit.Call(EngineAllocateMethod);
                }
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Deallocate)
            {
#if DEBUG
                if (DebugMode)
                {
                    var preELocal = emit.DeclareLocal<int>($"preE_dealloc_pc{pc}{lt}");
                    emit.LoadArgument(0);
                    emit.Call(EngineEGetter);
                    emit.StoreLocal(preELocal);
                    emit.LoadArgument(0);
                    emit.Call(EngineDeallocateMethod);
                    emit.LoadArgument(0);
                    emit.LoadConstant(_emitOwnerFid);
                    emit.LoadLocal(preELocal);
                    emit.LoadConstant(pc);
                    emit.Call(DbgCheckDeallocateMethod);
                }
                else
#endif
                {
                    emit.LoadArgument(0);
                    emit.Call(EngineDeallocateMethod);
                }
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            // fused opcodes. Emit the equivalent pair of
            // engine calls; the size includes the padding Nop.
            if (op == Opcode.AllocateGetLevel)
            {
                int n = BytecodeIO.ReadInt32(code, pc + 1);
                int slot = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(n);
                emit.Call(EngineAllocateMethod);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineGetLevelMethod);
                pc += OpcodeTable.Get(op).Size;   // 10
                continue;
            }
            if (op == Opcode.DeallocateProceed)
            {
                emit.LoadArgument(0);
                emit.Call(EngineDeallocateMethod);
                // Proceed semantics in IL: success return.
                if (!suppressProceedReturn)
                {
                    emit.LoadConstant(true);
                    emit.Return();
                }
                pc += OpcodeTable.Get(op).Size;   // 2
                continue;
            }
            if (op == Opcode.Nop)
            {
                // Padding inside a fused opcode; the outer
                // fused-opcode case has already advanced PC past it, so
                // a standalone Nop in the walker is just a 1-byte skip.
                pc += 1;
                continue;
            }
            if (op == Opcode.TryMeElse)
            {
                // ADR-025 — the inline-ITE choice point (body-CP arity
                // sentinel; the eligibility filters guarantee no
                // dispatch-chain try_me_else reaches a body emit). The CP's
                // cursor is the ELSE re-entry point, marked at TrustMe.
                int elseAddr = BytecodeIO.ReadInt32(code, pc + 1);
                int elseCursor;
                Sigil.Label elseResumeLabel;
                if (regionCtx is not null)
                {
                    // ITE in a REGION member: the plan gave this try_me_else
                    // pc a cursor (it rides the BuiltinResume site kind); the
                    // CP carries the REGION delegate + that cursor.
                    elseCursor = regionCtx.CursorBySite[
                        (regionCtx.CurrentMemberIndex, pc)];
                    elseResumeLabel = regionCtx.CursorLabels[elseCursor];
                }
                else
                {
                    if (callSiteIndexCounter is null || resumeLabels is null)
                        throw new InvalidOperationException(
                            "Inline ITE requires callSiteIndexCounter + resumeLabels "
                            + "for the ELSE resume cursor.");
                    int iteSiteIdx = callSiteIndexCounter();
                    elseCursor = cursorBase + iteSiteIdx - 1;
                    elseResumeLabel = resumeLabels[iteSiteIdx - 1];
                }
                (iteElseLabels ??= new())[elseAddr] = elseResumeLabel;
                emit.LoadArgument(0);
                if (emitSelfDelegate is not null)
                {
                    // Direct re-entry (ADR-025 follow-up): the CP carries this
                    // predicate's OWN delegate + the ELSE cursor — a failed
                    // condition re-enters the method straight at the cursor
                    // switch, the same contract as a clause-alt chain CP. The
                    // marker form below instead pays Resume → Pc=marker →
                    // dispatch loop → decode → delegate lookup → re-invoke per
                    // failed condition — measured +40% on boyer (a call-cond
                    // ITE per rewrite/2 entry) head-to-head without regions.
                    emitSelfDelegate(emit);
                    emit.LoadConstant(elseCursor);
                }
                else
                {
                    // Fallback: engine.PushIlChoicePoint(IlIteHelper.Resume,
                    // marker, 0) — resume via the dispatch loop.
                    emit.LoadField(IlIteHelperResumeField);
                    EmitResumeMarker(emit, _emitOwnerFid, elseCursor);
                }
                emit.LoadConstant(0);
                emit.Call(EnginePushIlCpMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.TrustMe)
            {
                // ADR-025 — the ELSE entry. Reached ONLY via the cursor switch
                // on backtrack (the CP pop already happened in TryBacktrack;
                // trust_me itself needs no IL); the first pass never falls
                // through — the preceding `jump` is unconditional.
                if (iteElseLabels is null || !iteElseLabels.Remove(pc, out var elseLabel))
                    throw new InvalidOperationException(
                        $"trust_me at pc={pc} has no pending inline-ITE else label.");
                emit.MarkLabel(elseLabel);
                regZeroAtom = -1;
                pc += 1;
                continue;
            }
            if (op == Opcode.Jump)
            {
                // ADR-025 — unconditional intra-clause branch (the then-branch
                // END join). Forward-only by construction.
                int target = BytecodeIO.ReadInt32(code, pc + 1);
                jumpLabels ??= new Dictionary<int, Sigil.Label>();
                if (!jumpLabels.TryGetValue(target, out var endLabel))
                {
                    endLabel = emit.DefineLabel($"ite_end_{target}_{NextLabelSeq()}");
                    jumpLabels[target] = endLabel;
                }
                emit.Branch(endLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.NeckCut)
            {
                // Flush pending attribute wakeups before committing — a failed
                // constraint must backtrack while choice points still exist.
                emit.LoadArgument(0);
                emit.Call(EngineFlushWakeupsForIlCutMethod);
                emit.BranchIfFalse(failLabel);
                emit.LoadArgument(0);
                emit.Call(EngineNeckCutMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetLevel)
            {
                // Y[slot] := RawInt(_b0) — capture the entry cut barrier.
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineGetLevelMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetLevelB)
            {
                // ADR-025 — Y[slot] := RawInt(B): the inline-ITE commit barrier.
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineGetLevelBMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Cut)
            {
                // Deep cut: commit to the barrier stashed in Y[slot]. Flush
                // pending attribute wakeups first (see NeckCut above) so a
                // constraint that fails can still backtrack into the
                // about-to-be-pruned choice points.
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.Call(EngineFlushWakeupsForIlCutMethod);
                emit.BranchIfFalse(failLabel);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineCutToLevelMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.SoftCut)
            {
                // ADR-037 — inline ( Cond *-> Then ; Else ) commit. As with cut,
                // flush pending attribute wakeups first; then neutralise ONLY the
                // ELSE choice point named by Y[slot], leaving the condition's CPs.
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.Call(EngineFlushWakeupsForIlCutMethod);
                emit.BranchIfFalse(failLabel);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineSoftCutToLevelMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.CallBuiltin)
            {
                int builtinId = BytecodeIO.ReadInt32(code, pc + 1);
                // one GetById (was two: Name then Arity), and the
                // precomputed IsCall / IsDollarCall / IsBacktrackable flags
                // instead of per-walk name compares.
                var builtinEntry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                int builtinArity = builtinEntry.Arity;

                // ADR-022 item 2 — inline an embedded native block directly into
                // this method's IL instead of dispatching `$native_run`. Declines
                // (falls through to the normal builtin dispatch, which runs the
                // block via the interpreter / runtime delegate) when no inline
                // context is set, the block can't be recovered, or it uses a
                // construct outside the compilable tier.
                if (_nativeInline is not null && builtinEntry.Name == "$native_run"
                    && NativeBlockInliner.TryEmit(emit, _nativeInline, regZeroAtom, failLabel))
                {
                    System.Threading.Interlocked.Increment(ref NativeBlocksInlined);
                    regZeroAtom = -1;
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                // ADR-024 fusion — emit fill_par/2 and reftype_term/2 inline (the
                // term ↔ slot marshalling) instead of dispatching them as builtins,
                // so the whole reftype flow is one IL sequence (no per-call dispatch).
                if (_nativeInline is not null && builtinArity == 2
                    && builtinEntry.Name == "fill_par"
                    && NativeBlockInliner.TryEmitFillPar(emit, _nativeInline))
                {
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }
                if (_nativeInline is not null && builtinArity == 2
                    && builtinEntry.Name == "reftype_term"
                    && NativeBlockInliner.TryEmitReftypeTerm(emit, _nativeInline, failLabel))
                {
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                if (builtinEntry.IsCall || builtinEntry.IsDollarCall)
                {
                    // meta-call dispatch. Three outcomes from
                    // IlMetaCallHelper.Dispatch:
                    //   target >= 0      → user predicate / control
                    //                      helper. Thread the dispatch
                    //                      exactly like a non-
                    //                      tail Call.
                    //   target == -2     → synchronous success (the goal
                    //                      was `!`, `true`, or a builtin
                    //                      that returned true). Fall
                    //                      through to the next opcode.
                    //   target == -1     → synchronous failure (the goal
                    //                      was `fail` or a builtin that
                    //                      returned false). Go to fail.
                    //
                    // Last-call optimisation: when the CallBuiltin is
                    // immediately followed by Proceed (or Deallocate +
                    // Proceed), the Tier-0 dispatcher leaves Cp alone so
                    // the called goal's proceed jumps straight back to
                    // the outer caller. The IL emit has to mirror this —
                    // setting Cp to a resume marker for the current IL
                    // would trap in an infinite loop (the bytecode
                    // interpreter would re-enter the IL at the resume
                    // cursor, immediately Proceed again, see Pc = Cp =
                    // same marker, and decode it again).
                    int nextOp = pc + OpcodeTable.Get(op).Size;
                    bool tailCall = nextOp < code.Length
                        && (Opcode)code[nextOp] == Opcode.Proceed
                        && !suppressProceedReturn;
                    if (!tailCall && nextOp + 1 <= code.Length
                        && (Opcode)code[nextOp] == Opcode.Deallocate)
                    {
                        // Deallocate restores the env frame's saved Cp;
                        // the meta-call still threads but Cp's restore
                        // is handled by the Deallocate emit downstream.
                        // No special handling here — the non-tail path
                        // is correct.
                    }
                    // inside a region the cursor comes from the
                    // PLAN (keyed by this site's pc) and the marker carries
                    // the REGION's fid, so the dispatch loop re-enters the
                    // region method at the right switch slot. Standalone
                    // keeps the sequential counter.
                    int resumeCursor;
                    Sigil.Label metaResumeLabel;
                    int markerOwnerFid;
                    if (regionCtx is not null)
                    {
                        resumeCursor = regionCtx.CursorBySite[
                            (regionCtx.CurrentMemberIndex, pc)];
                        metaResumeLabel = regionCtx.CursorLabels[resumeCursor];
                        markerOwnerFid = regionCtx.RegionFid;
                    }
                    else
                    {
                        if (callSiteIndexCounter is null || resumeLabels is null)
                            throw new InvalidOperationException(
                                "IL meta-call requires callSiteIndexCounter + "
                                + "resumeLabels for forward-resume cursor allocation.");
                        int siteIdx = callSiteIndexCounter();
                        resumeCursor = cursorBase + siteIdx - 1;
                        metaResumeLabel = resumeLabels[siteIdx - 1];
                        markerOwnerFid = _emitOwnerFid;
                    }

                    var target = emit.DeclareLocal<int>($"metaCallTarget_pc{pc}{lt}");

                    // A backtrackable builtin reached THROUGH this meta-call
                    // captures BuiltinReturnPc for its resume, exactly as one
                    // at a direct call_builtin site does. Without it the
                    // builtin keeps whatever the PREVIOUS builtin call left
                    // there, and its first retry re-enters somewhere that was
                    // never its continuation. Tier-0's meta-call arm sets it
                    // for the same reason (BytecodeInterpreter, the IsCall
                    // branch); Tier-1 did not, so `call(append(_, _, L))` came
                    // back wrong the moment the caller was promoted.
                    //
                    // A tail site takes the CALLER's continuation instead: Cp
                    // is already the outer caller's there, and re-entering
                    // this method's own cursor after a tail call would loop.
                    emit.LoadArgument(0);
                    if (tailCall)
                    {
                        emit.LoadArgument(0);
                        emit.Call(EngineCpGetter);
                    }
                    else
                    {
                        EmitResumeMarker(emit, markerOwnerFid, resumeCursor);
                    }
                    emit.Call(EngineBuiltinReturnPcSetter);

                    // Compute the call arity and cut barrier per builtin.
                    //   call/N : arity = N, barrier = engine.B
                    //   $call/2: arity = 1, barrier = X[1].AsInt
                    emit.LoadArgument(0);                    // engine
                    if (builtinEntry.IsDollarCall)
                    {
                        emit.LoadConstant(1);                // arity 1
                        // barrier = ReadIntRegister(engine, 1) — derefs the
                        // X[1] cell once and extracts the int payload.
                        emit.LoadArgument(0);
                        emit.LoadConstant(1);
                        emit.Call(IlMetaCallHelperReadIntRegisterMethod);
                    }
                    else
                    {
                        emit.LoadConstant(builtinArity);
                        emit.LoadArgument(0);
                        emit.Call(EngineBGetter);
                    }
                    // convertBody: call/N converts (SS7.6.2); $call/2
                    // dispatches an already-converted body.
                    emit.LoadConstant(!builtinEntry.IsDollarCall);
                    emit.Call(IlMetaCallHelperDispatchMethod);
                    emit.StoreLocal(target);

                    // target == -1 → fail
                    emit.LoadLocal(target);
                    emit.LoadConstant(IlMetaCallHelper.SyncFail);
                    emit.BranchIfEqual(failLabel);

                    // target == -2 → fall through (sync success)
                    var threadedLabel = emit.DefineLabel($"metaCallThread_pc{pc}_{NextLabelSeq()}");
                    emit.LoadLocal(target);
                    emit.LoadConstant(IlMetaCallHelper.SyncSuccess);
                    emit.UnsignedBranchIfNotEqual(threadedLabel);
                    // sync success — skip the threading and go to resume.
                    emit.Branch(metaResumeLabel);

                    emit.MarkLabel(threadedLabel);
                    // Threaded dispatch.
                    // Non-tail: SetCp(resume marker) so the bytecode
                    // interpreter re-enters this IL at resumeCursor
                    // after the callee proceeds.
                    // Tail: skip SetCp entirely. Cp stays as the outer
                    // caller's, and the callee's proceed jumps straight
                    // there — same last-call optimisation Tier-0 has
                    // for CallBuiltin followed by Proceed.
                    if (!tailCall)
                    {
                        emit.LoadArgument(0);
                        EmitResumeMarker(emit, markerOwnerFid, resumeCursor);
                        emit.Call(EngineSetCpMethod);
                    }
                    emit.LoadArgument(0);
                    emit.LoadLocal(target);
                    emit.Call(EngineSetPcMethod);
                    emit.LoadArgument(0);
                    emit.LoadConstant(true);
                    emit.Call(EngineIlTailCallPendingSetter);
                    emit.LoadConstant(true);
                    emit.Return();

                    emit.MarkLabel(metaResumeLabel);
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                // Regular builtin (non-meta) — invoke entry.Impl directly.
                // entry = BuiltinsRegistry.GetById(id)
                // if (!entry.Impl(engine)) goto fail
                //
                // backtrackable builtins (between/3, append/3,
                // repeat/0, retract/1, …) push a CP whose resume calls
                // ResumeAtReturnPc(returnPc) — the returnPc is captured at
                // first invocation as engine.BuiltinReturnPc. The IL emit
                // here allocates a resume cursor and pre-sets
                // BuiltinReturnPc to that marker, so on resume the
                // dispatcher decodes the marker and re-enters this IL at
                // the post-builtin label. Non-backtrackable builtins skip
                // the cursor allocation — straight invocation.
                bool isBacktrackable = builtinEntry.IsBacktrackable;
                Sigil.Label? builtinResumeLabel = null;
                if (isBacktrackable)
                {
                    // region members take their cursor from the
                    // PLAN (keyed by pc) with the REGION's fid in the marker;
                    // standalone keeps the sequential counter.
                    int resumeCursor;
                    int markerOwnerFid;
                    if (regionCtx is not null)
                    {
                        resumeCursor = regionCtx.CursorBySite[
                            (regionCtx.CurrentMemberIndex, pc)];
                        builtinResumeLabel = regionCtx.CursorLabels[resumeCursor];
                        markerOwnerFid = regionCtx.RegionFid;
                    }
                    else
                    {
                        if (callSiteIndexCounter is null || resumeLabels is null)
                            throw new InvalidOperationException(
                                "Backtrackable builtin in IL requires callSiteIndexCounter + resumeLabels.");
                        int builtinResumeIdx = callSiteIndexCounter();
                        resumeCursor = cursorBase + builtinResumeIdx - 1;
                        builtinResumeLabel = resumeLabels[builtinResumeIdx - 1];
                        markerOwnerFid = _emitOwnerFid;
                    }
                    // engine.BuiltinReturnPc = EncodeResumeMarker(ownerFid, resumeCursor);
                    emit.LoadArgument(0);
                    EmitResumeMarker(emit, markerOwnerFid, resumeCursor);
                    emit.Call(EngineBuiltinReturnPcSetter);
                }
                EmitBuiltinId(emit, builtinId);
                emit.Call(BuiltinsRegistryGetByIdMethod);
                emit.Call(BuiltinEntryImplGetter);
                emit.LoadArgument(0);
                emit.Call(BuiltinImplInvokeMethod);
                emit.BranchIfFalse(failLabel);
                if (isBacktrackable)
                {
                    // After a successful first invocation, fall through.
                    // On a CP-resume, the dispatcher decodes the marker and
                    // re-invokes this IL (or the region method) with
                    // cursor=resumeCursor; the top dispatch routes it to this
                    // label, which continues the body at the post-builtin
                    // position.
                    emit.MarkLabel(builtinResumeLabel!);
                }
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.ExecuteBuiltin)
            {
                // the fused tail builtin: dispatch the
                // builtin, then Proceed. Reaches the IL only for bundle-decoded
                // predicates (the linker's Execute→ExecuteBuiltin rewrite for
                // foreigns / late-resolved builtins); non-meta entries only
                // (IsClauseBodyOpcode gates the meta forms to Tier-0). The
                // inline / region-member detectors reject this opcode, so a
                // proceed-suppressed emit site can't legitimately see it.
                if (suppressProceedReturn)
                    throw new NotSupportedException(
                        "ExecuteBuiltin at a proceed-suppressed IL emit site.");
                int tailBuiltinId = BytecodeIO.ReadInt32(code, pc + 1);
                var tailEntry = Shumway.Builtins.BuiltinsRegistry.GetById(tailBuiltinId);
                if (tailEntry.IsBacktrackable)
                {
                    // Tail-return contract (mirrors the interpreter): a
                    // backtrackable builtin's ResumeAtReturnPc must land at
                    // the CALLER's continuation — the engine's current Cp —
                    // not at a cursor in this method (which would loop).
                    // A preceding Deallocate emit already restored Cp.
                    emit.LoadArgument(0);
                    emit.LoadArgument(0);
                    emit.Call(EngineCpGetter);
                    emit.Call(EngineBuiltinReturnPcSetter);
                }
                EmitBuiltinId(emit, tailBuiltinId);
                emit.Call(BuiltinsRegistryGetByIdMethod);
                emit.Call(BuiltinEntryImplGetter);
                emit.LoadArgument(0);
                emit.Call(BuiltinImplInvokeMethod);
                emit.BranchIfFalse(failLabel);
                // Proceed. A builtin that itself threaded a tail dispatch set
                // IlTailCallPending + Pc; returning true defers to the outer
                // dispatch loop either way (it honours pending, else runs the
                // caller's continuation).
                emit.LoadConstant(true);
                emit.Return();
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetStructure)
            {
                int functorId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                int sz = OpcodeTable.Get(op).Size;

                // Fused binary-structure peephole (2026-07), the get_list twin: the
                // window must consume the WHOLE structure — arity exactly 2 — or
                // the ops after it would read the mode state the fused call skips.
                int pc1 = pc + sz;
                int pc2 = pc1 + 5;
                if (pc2 + 5 <= end
                    && FunctorTable.Lookup(functorId).Arity == 2
                    && (jumpLabels is null
                        || (!jumpLabels.ContainsKey(pc1) && !jumpLabels.ContainsKey(pc2)))
                    && (iteElseLabels is null
                        || (!iteElseLabels.ContainsKey(pc1) && !iteElseLabels.ContainsKey(pc2))))
                {
                    var op1 = (Opcode)code[pc1];
                    var op2 = (Opcode)code[pc2];
                    bool varVar = op1 == Opcode.UnifyVariableX && op2 == Opcode.UnifyVariableX;
                    bool valVal = op1 == Opcode.UnifyValueX && op2 == Opcode.UnifyValueX;
                    if (varVar || valVal)
                    {
                        int slot1 = BytecodeIO.ReadInt32(code, pc1 + 1);
                        int slot2 = BytecodeIO.ReadInt32(code, pc2 + 1);
                        emit.LoadArgument(0);
                        EmitFunctorId(emit, functorId);
                        emit.LoadConstant(arg);
                        emit.LoadConstant(slot1);
                        emit.LoadConstant(slot2);
                        emit.Call(varVar
                            ? EngineGetStruct2VarXVarXMethod
                            : EngineGetStruct2ValXValXMethod);
                        emit.BranchIfFalse(failLabel);
                        pc = pc2 + 5;
                        continue;
                    }
                }

                emit.LoadArgument(0);
                EmitFunctorId(emit, functorId);
                emit.LoadConstant(arg);
                emit.Call(EngineGetStructureMethod);
                emit.BranchIfFalse(failLabel);
                pc += sz;
                continue;
            }
            if (op == Opcode.PutStructure)
            {
                int functorId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                EmitFunctorId(emit, functorId);
                emit.LoadConstant(arg);
                emit.Call(EnginePutStructureMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutStructureR)   // ADR-020
            {
                int functorId = BytecodeIO.ReadInt32(code, pc + 1);
                int packed = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                EmitFunctorId(emit, functorId);
                emit.LoadConstant(packed & 0xFFFFFF);
                emit.LoadConstant(packed >> 24);
                emit.Call(EnginePutStructureReservedMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutListR)   // ADR-020
            {
                int arg = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.Call(EnginePutListReservedMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyAtom)
            {
                int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                EmitAtomId(emit, atomId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineUnifyArgCellMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyInteger)
            {
                int value = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant((long)value);
                emit.Call(CellIntMethod);
                emit.Call(EngineUnifyArgCellMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyNil)
            {
                emit.LoadArgument(0);
                emit.LoadConstant(AtomTable.EmptyListId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineUnifyArgCellMethod);
                emit.BranchIfFalse(failLabel);
                pc += 1;
                continue;
            }
            if (op == Opcode.UnifyVariableX)
            {
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineUnifyVariableXMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyStructure)   // ADR-019
            {
                int functorId = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                EmitFunctorId(emit, functorId);
                emit.Call(EngineUnifyStructureMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyList)   // ADR-019
            {
                emit.LoadArgument(0);
                emit.Call(EngineUnifyListMethod);
                emit.BranchIfFalse(failLabel);
                pc += 1;
                continue;
            }
            if (op == Opcode.UnifyValueX)
            {
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineUnifyValueXMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyVariableY)
            {
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineUnifyVariableYMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyValueY)
            {
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineUnifyValueYMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyVoid)
            {
                int count = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(count);
                emit.Call(EngineUnifyVoidMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetList)
            {
                int arg = BytecodeIO.ReadInt32(code, pc + 1);
                int sz = OpcodeTable.Get(op).Size;

                // Fused cons peephole (2026-07): `get_list; unify_*_x; unify_variable_x`
                // — the complete match/build of one cons — becomes ONE Activation call
                // (see GetListVarXVarX / GetListValXVarX). Only when the whole window
                // is inside this range and no ADR-025 label lands mid-window (a branch
                // into the middle would skip the fused prefix).
                int pc1 = pc + sz;
                int pc2 = pc1 + 5;               // unify_*_x is 1 + 4 bytes
                if (pc2 + 5 <= end
                    && (jumpLabels is null
                        || (!jumpLabels.ContainsKey(pc1) && !jumpLabels.ContainsKey(pc2)))
                    && (iteElseLabels is null
                        || (!iteElseLabels.ContainsKey(pc1) && !iteElseLabels.ContainsKey(pc2)))
                    && (Opcode)code[pc2] == Opcode.UnifyVariableX)
                {
                    var op1 = (Opcode)code[pc1];
                    if (op1 is Opcode.UnifyVariableX or Opcode.UnifyValueX)
                    {
                        int slot1 = BytecodeIO.ReadInt32(code, pc1 + 1);
                        int slot2 = BytecodeIO.ReadInt32(code, pc2 + 1);
                        emit.LoadArgument(0);
                        emit.LoadConstant(arg);
                        emit.LoadConstant(slot1);
                        emit.LoadConstant(slot2);
                        emit.Call(op1 == Opcode.UnifyVariableX
                            ? EngineGetListVarXVarXMethod
                            : EngineGetListValXVarXMethod);
                        emit.BranchIfFalse(failLabel);
                        pc = pc2 + 5;
                        continue;
                    }
                }

                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.Call(EngineGetListMethod);
                emit.BranchIfFalse(failLabel);
                pc += sz;
                continue;
            }
            if (op == Opcode.PutList)
            {
                int arg = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.Call(EnginePutListMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetPstr)
            {
                int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(literalId);
                emit.LoadConstant(arg);
                emit.Call(IlGetPstrHelperMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutPstr)
            {
                int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(literalId);
                emit.LoadConstant(arg);
                emit.Call(IlPutPstrHelperMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Call)
            {
                // Non-tail Call — threaded: Cp is set to a resume marker
                // and control tail-transfers to the callee; a backtrack
                // re-enters this delegate at the post-call cursor.
                int siteFunctorId = FindCallSiteFunctorId(callSites, pc);
                if (siteFunctorId < 0)
                    throw new InvalidOperationException(
                        $"Call opcode at pc={pc} has no matching call site in the predicate's metadata.");

                // multi-clause fact inline (gated SHUMWAY_INLINE_FACTS).
                // Emit the callee fact's clause chain in-method instead of the
                // trampoline: each clause pushes a CP (this delegate @ the
                // alternative cursor) then head-matches the call args; a match
                // branches to the continuation (the post-call code), a head-match
                // failure returns false → backtrack pops the CP → re-enters this
                // delegate at the alternative cursor → next clause.
                if (inlineSites is not null && inlineSites.TryGetValue(pc, out var inl))
                {
                    EmitInlinedFact(emit, inl, failLabel, emitSelfDelegate!, calleeMap);
                    if (callSiteIndexCounter is not null && resumeLabels is not null)
                    {
                        // The inlined call has no trampoline resume; mark its
                        // (dead) resume label so the cursor switch's branch to it
                        // is well-formed, exactly as the leaf path does.
                        int inlSiteIdx = callSiteIndexCounter();
                        emit.MarkLabel(resumeLabels[inlSiteIdx - 1]);
                    }
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                // Inline-rule case 2: inline a single-clause rule that
                // makes user calls and/or cuts. Set B0 = engine.B at the inline
                // entry so the body's deep cut (allocate_get_level / get_level)
                // captures THIS barrier — the inlined cut then prunes only the
                // body's own choice points, not the caller's. The body is emitted
                // with the CALLER's threading context, so its non-tail calls take
                // forward-resume cursors in the caller's space (already counted
                // into callSiteCount); its proceed/deallocate_proceed is suppressed
                // so control falls through to the post-call continuation.
                if (ruleInlineSites is not null
                    && ruleInlineSites.TryGetValue(pc, out var ruleCallee)
                    && callSiteIndexCounter is not null && resumeLabels is not null)
                {
                    emit.LoadArgument(0);                    // engine.SetB0(engine.B)
                    emit.LoadArgument(0);
                    emit.Call(EngineBGetter);
                    emit.Call(EngineSetB0Method);
                    EmitClauseBody(emit, ruleCallee.BytecodeUnfused, 0, ruleCallee.BytecodeUnfused.Length,
                        failLabel, ruleCallee.CallSites,
                        callSiteIndexCounter: callSiteIndexCounter,
                        resumeLabels: resumeLabels,
                        emitSelfDelegate: emitSelfDelegate,
                        calleeMap: calleeMap,
                        suppressProceedReturn: true,
                        cursorBase: cursorBase);
                    // Consume + mark the dead resume cursor reserved for THIS
                    // Call site (no marker is ever set for it — the rule is
                    // inlined — but the cursor switch has a slot, so the label
                    // must be marked to keep the IL well-formed).
                    int ruleSiteIdx = callSiteIndexCounter();
                    emit.MarkLabel(resumeLabels[ruleSiteIdx - 1]);
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                // ADR-031 G2 — a CP-free guard's call to a FAIL-DIRECT callee
                // (multi-clause and/or self-tail-recursive; see
                // TryDescribeFailDirectCallee) is inlined as a sequential
                // alternative chain with an in-place loop, so its failure is a
                // direct branch to the guard's fail label. Only active from
                // CP-free guard slices (forceLeafRuleInline).
                //
                // ADR-033 — with the continuation-stack mechanism on, the
                // callee's code is NOT duplicated here: the site pushes its
                // packed (ok, fail) continuation cursors and branches to the
                // ONE shared per-method copy; the copy's epilogues pop and
                // dispatch back through the continuation switch.
                if (forceLeafRuleInline && calleeMap is not null
                    && calleeMap.TryGetValue(siteFunctorId, out var fdCallee)
                    && !(IsLeafPredicate(fdCallee) || IsInlinableLeafRule(fdCallee))
                    && TryDescribeFailDirectCallee(fdCallee, calleeMap, out var fdClauses, out _))
                {
                    if (CpFreeGuardContinuations && guardContCtx is not null)
                    {
                        var okLbl = emit.DefineLabel($"gc_ok{lt}_{pc}");
                        int okCur = guardContCtx.AllocCursor(okLbl);
                        int failCur = guardContCtx.AllocCursor(failLabel);
                        emit.LoadArgument(0);
                        emit.LoadConstant((okCur << 16) | failCur);
                        emit.Call(EnginePushGuardContMethod);
                        if (!guardContCtx.CalleeEntry.TryGetValue(
                                siteFunctorId, out var entryLbl))
                        {
                            entryLbl = emit.DefineLabel($"gc_callee_{siteFunctorId}");
                            guardContCtx.CalleeEntry[siteFunctorId] = entryLbl;
                            guardContCtx.PendingCallees.Add(fdCallee);
                        }
                        emit.Branch(entryLbl);
                        emit.MarkLabel(okLbl);
                    }
                    else
                    {
                        EmitFailDirectCalleeInline(emit, fdCallee, fdClauses!,
                            failLabel, calleeMap, $"{lt}_fd{pc}", guardContCtx);
                    }
                    if (callSiteIndexCounter is not null && resumeLabels is not null)
                    {
                        // Dead resume cursor for this inlined site (see the
                        // leaf-inline path above).
                        int fdSiteIdx = callSiteIndexCounter();
                        emit.MarkLabel(resumeLabels[fdSiteIdx - 1]);
                    }
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                // Inlining: if the callee is a small static
                // leaf, emit its body opcodes inline instead of routing
                // through IlCallHelper.Run. Leaves push no CPs so no
                // meta-CP is needed; the post-call label still gets
                // marked for any outer logic but no choice point lives
                // there.
                // ADR-034 — a dynamic SNAPSHOT may be inlined ONLY under the
                // checked-guard machinery (forceLeafRuleInline slices, whose
                // recognizer collected the fid for the clause-entry staleness
                // test); in any other position it takes the threaded by-fid
                // call, which dispatches against the LIVE dynamic.
                if (calleeMap is not null
                    && calleeMap.TryGetValue(siteFunctorId, out var calleePred)
                    && (!calleePred.IsDynamicSnapshot || forceLeafRuleInline)
                    && (IsLeafPredicate(calleePred)
                        || ((InlineLeafRules || forceLeafRuleInline)
                            && IsInlinableLeafRule(calleePred))))
                {
                    // The callee's pc-named locals must not collide with the
                    // caller's (both pc spaces start at 0) — salt per site.
                    EmitClauseBody(emit, calleePred.BytecodeUnfused, 0, calleePred.BytecodeUnfused.Length,
                        failLabel, Array.Empty<CallSite>(),
                        calleeMap: calleeMap, suppressProceedReturn: true,
                        localSalt: $"{lt}_inl{pc}");
                    if (callSiteIndexCounter is not null && resumeLabels is not null)
                    {
                        int leafSiteIdx = callSiteIndexCounter();
                        // Leaves leave no CPs behind, so under threading
                        // they don't set a resume marker; the
                        // cursor=leafSiteIdx entry is never invoked.
                        // Mark the resume label anyway so the cursor
                        // switch's branch has a target (dead code but
                        // keeps the IL well-formed).
                        emit.MarkLabel(resumeLabels[leafSiteIdx - 1]);
                    }
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                // Threaded non-tail Call: we tail-call to the callee (same machinery `Execute`
                // uses) and set Cp to a resume marker that the bytecode
                // interpreter will recognise when the callee Proceeds.
                // The marker encodes (this delegate's functor id,
                // siteIdx), so the dispatcher knows to re-invoke us at
                // the forward-resume cursor. No recursive C# stack
                // frame, and deliberately NO meta-CP push: backtracking
                // through the callee's CPs naturally lands at the
                // caller's marker again — the CP cascade alone carries
                // the semantics.
                if (callSiteIndexCounter is null || resumeLabels is null)
                    throw new InvalidOperationException(
                        "Threaded non-tail Call requires callSiteIndexCounter + "
                        + "resumeLabels for forward-resume cursor allocation "
                        + $"(owner {DescribeFid(_emitOwnerFid)}, pc={pc}, "
                        + $"callee {DescribeFid(siteFunctorId)}, salt='{lt}', "
                        + $"force={forceLeafRuleInline}, region={regionCtx is not null}).");

                int siteIdx = callSiteIndexCounter();
                // the cursor encoded in the resume marker is
                // cursorBase-relative — single-clause-meta-CP uses
                // cursorBase=1 (cursors 1..M), TryMeElseChain uses
                // cursorBase=N (cursors N..N+M-1, leaving 0..N-1 for
                // clause entries). The label array index stays 0-based
                // either way.
                int resumeCursor = cursorBase + siteIdx - 1;

                // engine.SetB0(engine.B);  — cut barrier for the callee
                emit.LoadArgument(0);
                emit.LoadArgument(0);
                emit.Call(EngineBGetter);
                emit.Call(EngineSetB0Method);

#if DEBUG
                if (DebugMode)
                {
                    int calleeArity = ResolveCalleeArity(siteFunctorId);
                    emit.LoadArgument(0);
                    emit.LoadConstant(_emitOwnerFid);
                    emit.LoadConstant(siteFunctorId);
                    emit.LoadConstant(calleeArity);
                    emit.LoadConstant(pc);
                    emit.Call(DbgCheckPreCallMethod);
                }
#endif

                // engine.SetCp(EncodeResumeMarker(ownerFid, resumeCursor));
                emit.LoadArgument(0);
                EmitResumeMarker(emit, _emitOwnerFid, resumeCursor);
                emit.Call(EngineSetCpMethod);

                // engine.SetPc(Activation.EncodeResumeMarker(siteFunctorId, 0));
                // The dispatcher routes the marker to the callee's IL delegate
                // (by functor id, cursor 0 = entry) or falls back to its WAM
                // address — no resolution through the callee's WAM address here.
                emit.LoadArgument(0);
                EmitFunctorId(emit, siteFunctorId);
                emit.LoadConstant(0);
                emit.Call(EngineEncodeResumeMarkerMethod);
                emit.Call(EngineSetPcMethod);

                // engine.IlTailCallPending = true; return true.
                emit.LoadArgument(0);
                emit.LoadConstant(true);
                emit.Call(EngineIlTailCallPendingSetter);
                emit.LoadConstant(true);
                emit.Return();

                // Resume label — reached via the cursor switch when the
                // callee proceeds and the dispatcher decodes our
                // marker. Cursor numbering: forward-resume cursors
                // come AFTER any clause-entry cursors the outer body
                // emitter reserved.
                emit.MarkLabel(resumeLabels[siteIdx - 1]);

#if DEBUG
                if (DebugMode)
                {
                    int calleeArity = ResolveCalleeArity(siteFunctorId);
                    emit.LoadArgument(0);
                    emit.LoadConstant(_emitOwnerFid);
                    emit.LoadConstant(siteFunctorId);
                    emit.LoadConstant(calleeArity);
                    emit.LoadConstant(pc);
                    emit.Call(DbgCheckPostCallMethod);
                }
#endif
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Execute)
            {
                // Tail call. The operand in the bytecode is a per-query
                // resolved address that's only valid for the link that
                // produced it; if we cached this delegate and the engine
                // re-links the program for a later query, the address
                // would point at the wrong place. So instead we look up
                // the callee's address via the engine's current functor
                // address map (set per query) using the stable functor
                // id from the call site metadata.
                int siteFunctorId = FindCallSiteFunctorId(callSites, pc);
                if (siteFunctorId < 0)
                    throw new InvalidOperationException(
                        $"Execute opcode at pc={pc} has no matching call site in the predicate's metadata.");

                // Un-tail. When this body is being INLINED at
                // a non-tail site (suppressProceedReturn), a trailing tail Execute
                // must become a threaded NON-TAIL call: control has to return to the
                // caller's continuation after the callee proceeds, not tail-return
                // past it. Same threading as a non-tail Call (a forward-resume cursor
                // in the caller's space, already counted by CountRuleBodyThreadedCalls
                // → +1 for the trailing Execute). Always threads (never leaf-inlines
                // the tail call) so the cursor count stays exact. In linked runtime
                // bytecode Execute targets a user predicate (a tail builtin is
                // ExecuteBuiltin), so the marker resolves to a real delegate/address.
                if (suppressProceedReturn
                    && callSiteIndexCounter is not null && resumeLabels is not null
                    && siteFunctorId != selfFunctorId)
                {
                    int untailIdx = callSiteIndexCounter();
                    int untailCursor = cursorBase + untailIdx - 1;
                    emit.LoadArgument(0);                 // engine.SetB0(engine.B)
                    emit.LoadArgument(0);
                    emit.Call(EngineBGetter);
                    emit.Call(EngineSetB0Method);
                    emit.LoadArgument(0);                 // engine.SetCp(resume marker)
                    EmitResumeMarker(emit, _emitOwnerFid, untailCursor);
                    emit.Call(EngineSetCpMethod);
                    emit.LoadArgument(0);                 // engine.SetPc(callee entry marker)
                    EmitFunctorId(emit, siteFunctorId);
                    emit.LoadConstant(0);
                    emit.Call(EngineEncodeResumeMarkerMethod);
                    emit.Call(EngineSetPcMethod);
                    emit.LoadArgument(0);                 // IlTailCallPending = true; return true
                    emit.LoadConstant(true);
                    emit.Call(EngineIlTailCallPendingSetter);
                    emit.LoadConstant(true);
                    emit.Return();
                    // Resume → fall through to the post-inline continuation.
                    emit.MarkLabel(resumeLabels[untailIdx - 1]);
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                // Inlining: if the callee is a small static
                // leaf, emit its body opcodes inline instead of going
                // through the Pc-set / IlTailCallPending / outer-
                // dispatch dance. The callee's own proceed (= return
                // true) is exactly what the caller needs at the
                // tail-call site, so suppressProceedReturn stays false.
                if (calleeMap is not null
                    && calleeMap.TryGetValue(siteFunctorId, out var calleePredX)
                    && (IsLeafPredicate(calleePredX)
                        || (InlineLeafRules && IsInlinableLeafRule(calleePredX))))
                {
                    EmitClauseBody(emit, calleePredX.BytecodeUnfused, 0, calleePredX.BytecodeUnfused.Length,
                        failLabel, Array.Empty<CallSite>(),
                        calleeMap: calleeMap, suppressProceedReturn: false);
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }
                // Self-tail-recursion → in-method loop (GProlog-style jump).
                // When the tail call targets the predicate being emitted, the
                // recursive call's args are already in the argument registers
                // (the put_* before this Execute set them), so instead of the
                // marker / return / dispatch-loop round trip we set the cut
                // barrier, run the heap-GC safe point (the back-edge the
                // dispatch loop would otherwise run), and branch straight to the
                // method's cursor-0 entry. Skips EncodeResumeMarker, the return,
                // OnDispatch, DecodeResumeMarker, the IlByFunctorId index and the
                // indirect delegate invoke — the bulk of the per-call trampoline
                // tax. Backtracking is unaffected: choice points are still on the
                // WAM stack with their own continuations; this only changes how
                // the FORWARD self-call reaches cursor 0. The Cp (the tail call's
                // continuation) is left as the caller set it, exactly as the
                // marker path does.
                if (selfTailLabel is not null && siteFunctorId == selfFunctorId)
                {
                    // engine.SetB0(engine.B);
                    // engine.MaybeCollectHeapAtCall(selfFunctorId);
                    emit.LoadArgument(0);
                    emit.LoadArgument(0);
                    emit.Call(EngineBGetter);
                    emit.Call(EngineSetB0Method);
                    emit.LoadArgument(0);
                    emit.LoadConstant(selfFunctorId);
                    emit.Call(EngineMaybeCollectHeapAtCallMethod);
                    // A chain predicate's cursor-0 entry re-reads the incoming
                    // cursor (arg 1) to pick clause 0; a fresh self-call must
                    // restart from clause 0, so reset it. Harmless for the
                    // indexed / single-clause entries (they branch past the
                    // cursor switch and never re-read arg 1).
                    if (resetCursorBeforeSelfTail)
                    {
                        emit.LoadConstant(0);
                        emit.StoreArgument(1);
                    }
                    emit.Branch(selfTailLabel);
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }
#if DEBUG
                if (DebugMode)
                {
                    int calleeArity = ResolveCalleeArity(siteFunctorId);
                    emit.LoadArgument(0);
                    emit.LoadConstant(_emitOwnerFid);
                    emit.LoadConstant(siteFunctorId);
                    emit.LoadConstant(calleeArity);
                    emit.LoadConstant(pc);
                    emit.Call(DbgCheckPreCallMethod);
                }
#endif
                // int target = IlExecuteHelper.Resolve(engine, siteFunctorId);
                // engine.SetB0(engine.B); engine.SetPc(target);
                // engine.IlTailCallPending = true; return true;
                emit.LoadArgument(0);
                emit.LoadArgument(0);
                emit.Call(EngineBGetter);
                emit.Call(EngineSetB0Method);
                emit.LoadArgument(0);
                EmitFunctorId(emit, siteFunctorId);
                emit.LoadConstant(0);
                emit.Call(EngineEncodeResumeMarkerMethod);
                emit.Call(EngineSetPcMethod);
                emit.LoadArgument(0);
                emit.LoadConstant(true);
                emit.Call(EngineIlTailCallPendingSetter);
                emit.LoadConstant(true);
                emit.Return();
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            // ---------- ADR-018 arithmetic instruction set ----------
            // Each opcode maps to one static call into ArithEvalStack, so
            // Tier-1 evaluates over the same Number eval stack as Tier-0 with
            // no heap allocation. Operand kinds 1/2 (bigint/float literals) are
            // filtered out by IsSupportedAEval before promotion, so only kinds
            // 0 (int32), 3 (X-reg) and 4 (Y-slot) reach here.
            if (op == Opcode.AEvalPush)
            {
                int kind = BytecodeIO.ReadInt32(code, pc + 1);
                int operand = BytecodeIO.ReadInt32(code, pc + 5);
                if (kind == 0)
                {
                    emit.LoadConstant((long)operand);
                    emit.Call(ArithPushIntMethod);
                }
                else
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(operand);
                    emit.Call(kind == 4 ? ArithPushYMethod : ArithPushRegMethod);
                }
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.AEvalBin)
            {
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 1));
                emit.LoadArgument(0);                 // engine
                emit.Call(PreferRationalsGetter);     // → prefer_rationals flag
                emit.Call(ArithBinMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.AEvalUn)
            {
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 1));
                emit.Call(ArithUnMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.AEvalIs)
            {
                int kind = BytecodeIO.ReadInt32(code, pc + 1);
                int target = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(target);
                switch (kind)
                {
                    case 5:   // store into first-occurrence X register (void, no branch)
                        emit.Call(ArithSetRegMethod);
                        break;
                    case 6:   // store into first-occurrence Y slot (void, no branch)
                        emit.Call(ArithSetPermMethod);
                        break;
                    case 4:   // unify with existing Y slot
                        emit.Call(ArithIsPermMethod);
                        emit.BranchIfFalse(failLabel);
                        break;
                    default:  // unify with existing X register
                        emit.Call(ArithIsRegMethod);
                        emit.BranchIfFalse(failLabel);
                        break;
                }
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.AEvalCmp)
            {
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 1));
                emit.Call(ArithCmpMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.AIntBin)
            {
                // Compact encoding: packed = aKind | bKind<<8 | tKind<<16 | op<<24.
                int packed = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant((packed >> 24) & 0xFF);               // op
                emit.LoadConstant(packed & 0xFF);                       // aKind
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 5));  // aVal
                emit.LoadConstant((packed >> 8) & 0xFF);                // bKind
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 9));  // bVal
                emit.LoadConstant((packed >> 16) & 0xFF);               // tKind
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 13)); // tVal
                emit.Call(ArithFusedBinMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.AIntCmp)
            {
                // Compact encoding: packed = aKind | bKind<<8 | rel<<16.
                int packed = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant((packed >> 16) & 0xFF);               // rel
                emit.LoadConstant(packed & 0xFF);                       // aKind
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 5));  // aVal
                emit.LoadConstant((packed >> 8) & 0xFF);                // bKind
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 9));  // bVal
                emit.Call(ArithFusedCmpMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Proceed)
            {
                // In inlined-Call mode the caller has more body after the
                // inlined block; skip the return and fall through to the
                // next opcode in the caller's stream. In normal mode (and
                // in inlined-Execute mode) proceed = return true.
                if (!suppressProceedReturn)
                {
                    emit.LoadConstant(true);
                    emit.Return();
                }
                pc += 1;
                continue;
            }
            throw new NotSupportedException(
                $"IL emission hit unsupported opcode 0x{(byte)op:X2} at pc={pc}.");
        }
    }

    // ============================================================================
    // Shape 2: switch_on_atom indexed multi-clause
    // ============================================================================

    /// <summary>The result of parsing an indexed-atom predicate's
    /// bytecode: each clause's first-arg atom id and the byte offset of
    /// its body in the bytecode. Used both as a "yes I can compile this"
    /// signal and as the dispatch table the IL emission consumes.</summary>
    private sealed record IndexedAtomClause(int AtomId, int BodyStart, int BodyEnd, bool IsTrivial);

    private sealed class IndexedAtomInfo
    {
        public required IReadOnlyList<IndexedAtomClause> Clauses { get; init; }
        /// <summary>True iff every clause's body is the trivial
        /// <c>get_atom + proceed</c> shape. Trivial bodies
        /// don't need an actual body emit — the switch_on_atom
        /// dispatch already matched the atom, so on a ground-key hit
        /// we just return true. Non-trivial bodies emit
        /// the body via <see cref="EmitClauseBody"/>.</summary>
        public required bool AllTrivial { get; init; }
    }

    /// <summary>Per-clause layout extracted from a try_me_else chain:
    /// the [start, end) byte offsets of each clause's body
    /// in the predicate's bytecode. Cursor N during IL dispatch runs
    /// the body at <c>Clauses[N]</c>.</summary>
    private sealed class TryMeElseChainInfo
    {
        public required IReadOnlyList<(int Start, int End)> Clauses { get; init; }
    }

    /// <summary>Recognises the classical non-indexed multi-clause shape
    /// <c>try_me_else / retry_me_else* / trust_me</c> with each clause
    /// body in the IL subset. This is the WAM compiler's output for
    /// multi-clause predicates that don't take first-argument indexing
    /// (e.g. arity 0, or every clause's first arg is a variable). When
    /// recognised, <paramref name="info"/> reports the per-clause body
    /// byte ranges so <see cref="CompileTryMeElseChain"/> can emit a
    /// cursor switch + IL choice points.</summary>
    private static bool TryDescribeTryMeElseChain(
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out TryMeElseChainInfo? info)
    {
        // memoized per predicate (see IlShapeMemo): the
        // structural walk runs once; the calleeMap-dependent Call check is
        // re-applied per call.
        if (predicate.IlTryMeElseShapeMemo is not IlShapeMemo memo)
        {
            var callFids = new List<int>();
            TryDescribeTryMeElseChainStructural(predicate, callFids, out var raw);
            memo = new IlShapeMemo(raw, callFids);
            predicate.IlTryMeElseShapeMemo = memo;
        }
        return memo.Resolve(calleeMap, out info);
    }

    private static bool TryDescribeTryMeElseChainStructural(
        CompiledPredicate predicate, List<int> callFids,
        out TryMeElseChainInfo? info)
    {
        info = null;
        byte[] code = predicate.BytecodeUnfused;
        if (code.Length == 0) return false;
        // First instruction must be try_me_else (size 9: opcode + bp +
        // arity). ADR-025 stage (b) — clause boundaries are derived by
        // FOLLOWING each dispatch opcode's address operand (try_me_else /
        // retry_me_else point at the NEXT clause's dispatch op; trust_me is
        // the last). The previous linear scan treated EVERY me-else-family
        // opcode as a boundary, which an inline-ITE's mid-body try_me_else /
        // trust_me would break.
        if ((Opcode)code[0] != Opcode.TryMeElse) return false;
        var clauseStarts = new List<int>();
        var boundaryPcs = new List<int>();
        int dpc = 0;
        while (true)
        {
            var dop = (Opcode)code[dpc];
            boundaryPcs.Add(dpc);
            if (dop == Opcode.TryMeElse || dop == Opcode.RetryMeElse)
            {
                clauseStarts.Add(dpc + OpcodeTable.Get(dop).Size);
                int next = BytecodeIO.ReadInt32(code, dpc + 1);
                if (next <= dpc || next >= code.Length) return false;
                dpc = next;
                continue;
            }
            if (dop == Opcode.TrustMe)
            {
                clauseStarts.Add(dpc + 1);
                break;
            }
            return false;   // chain link points at a non-dispatch opcode
        }
        if (clauseStarts.Count != predicate.ClauseCount) return false;

        // Derive (Start, End) per clause: each body ends at the NEXT clause's
        // dispatch opcode (known exactly from the operand walk); the last runs
        // to the end of the bytecode. Validate every body opcode against the
        // IL subset (the per-clause emission walks them again to emit).
        var ranges = new List<(int, int)>(clauseStarts.Count);
        for (int i = 0; i < clauseStarts.Count; i++)
        {
            int start = clauseStarts[i];
            int end = i + 1 < clauseStarts.Count ? boundaryPcs[i + 1] : code.Length;
            if (end < start) return false;
            int pc = start;
            while (pc < end)
            {
                var op = (Opcode)code[pc];
                if (op == Opcode.Meta) { pc += 6; continue; }
                if (!IsClauseBodyOpcodeStructural(op, predicate, pc, callFids)) return false;
                int size = OpcodeTable.Get(op).Size;
                if (size <= 0) return false;
                pc += size;
            }
            ranges.Add((start, end));
        }
        info = new TryMeElseChainInfo { Clauses = ranges };
        return true;
    }

    /// <summary>True iff <paramref name="op"/> is part of the IL-supported
    /// clause-body opcode set (anything that <see cref="EmitClauseBody"/>
    /// emits). Used by <see cref="TryDescribeTryMeElseChain"/> to verify
    /// each clause body fits the IL subset without re-emitting.</summary>
    private static bool IsClauseBodyOpcode(
        Opcode op, CompiledPredicate predicate, int pc,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        if (op == Opcode.Proceed) return true;
        if (op == Opcode.Execute) return true;
        if (op == Opcode.Call)
        {
            // Threading makes non-leaf callees uniform — the IL
            // emit sets Cp = resume marker and tail-calls; backtracking
            // through the callee's CPs naturally re-enters us at the
            // marker. No need to require IsLeafPredicate any more.
            if (calleeMap is null) return false;
            int siteFid = FindCallSiteFunctorId(predicate.CallSites, pc);
            if (siteFid < 0) return false;
            return calleeMap.ContainsKey(siteFid);
        }
        if (op == Opcode.CallBuiltin)
        {
            // call/N and '$call'/2 are IL-eligible via
            // IlMetaCallHelper.Dispatch — no longer rejected.
            return true;
        }
        if (op == Opcode.ExecuteBuiltin)
        {
            // the fused tail builtin (the linker's
            // Execute→ExecuteBuiltin rewrite for foreign / late-resolved
            // builtins in bundles). Deterministic and backtrackable entries
            // emit; a META goal in tail position (call/N, '$call'/2) would
            // need the meta-dispatch threading with proceed-on-sync-success —
            // left Tier-0 (rare: only a tail Execute that RESOLVED to a meta
            // builtin at link time takes this form).
            var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                BytecodeIO.ReadInt32(predicate.BytecodeUnfused, pc + 1));
            return !entry.IsCall && !entry.IsDollarCall;
        }
        if (op == Opcode.TryMeElse)
        {
            // ADR-025 stage (b) — a MID-BODY try_me_else is the inline-ITE
            // choice point, carrying the body-CP arity sentinel (the variable
            // discipline keeps branch state in Y slots). A dispatch-chain
            // try_me_else never reaches this filter (the describers walk
            // clause BODY ranges).
            return BytecodeIO.ReadInt32(predicate.BytecodeUnfused, pc + 5) == OpcodeTable.InlineIteCpArity;
        }
        if (IsAEvalOpcode(op))   // ADR-018 — gate operand kind (bigint/float lit)
            return IsSupportedAEval(predicate.BytecodeUnfused, pc);
        return IsSupportedOpcode(op);
    }

    private static int FindDispatchOpBefore(byte[] code, int clauseStart)
    {
        // Dispatch opcodes immediately precede each clauseStart except
        // for the first (which starts at pc=9, after the leading
        // try_me_else). Sizes: try_me_else 9, retry_me_else 5, trust_me 1.
        if (clauseStart == 0) return 0;
        // Walk backwards: the dispatch is either trust_me (1) or
        // retry_me_else (5). We check the byte just before clauseStart.
        if (clauseStart - 1 >= 0 && (Opcode)code[clauseStart - 1] == Opcode.TrustMe)
            return clauseStart - 1;
        if (clauseStart - 5 >= 0 && (Opcode)code[clauseStart - 5] == Opcode.RetryMeElse)
            return clauseStart - 5;
        // For clause 0, dispatch is try_me_else (9 bytes) at pc=0.
        if (clauseStart - 9 >= 0 && (Opcode)code[clauseStart - 9] == Opcode.TryMeElse)
            return clauseStart - 9;
        return clauseStart;
    }

    /// <summary>recognises the first/multi-arg
    /// indexed shape — bytecode opens with <c>switch_on_term</c>
    /// (level 0) and may chain into one or more <c>switch_on_arg</c>
    /// (levels 1+) before a final <c>try / retry* / trust</c> chain
    /// over every clause in source order. The shape is what the WAM
    /// compiler emits for any static multi-clause predicate whose
    /// first (or first-few) arguments can be discriminated against
    /// concrete atoms / ints / structures.
    ///
    /// <para>The IL emit (<see cref="CompileSwitchedChain"/>) does
    /// NOT reproduce the switch dispatch — it just walks the
    /// extracted clause bodies linearly, exactly like
    /// <see cref="CompileTryMeElseChain"/>. The switch tables in the
    /// bytecode are an optimisation that pre-filters by tag/key; the
    /// linear scan is correct because each clause body's own
    /// head-matching opcodes filter the same way (just without the
    /// pre-dispatch). The IL gives up the O(1) switch dispatch but
    /// gains everything else IL gives (native code for the body
    /// opcodes, no per-opcode interpreter overhead).</para></summary>
    private static bool TryDescribeSwitchedChain(
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out TryMeElseChainInfo? info)
    {
        // memoized per predicate (see IlShapeMemo): the
        // structural walk runs once; the calleeMap-dependent Call check is
        // re-applied per call.
        if (predicate.IlSwitchedChainShapeMemo is not IlShapeMemo memo)
        {
            var callFids = new List<int>();
            TryDescribeSwitchedChainStructural(predicate, callFids, out var raw);
            memo = new IlShapeMemo(raw, callFids);
            predicate.IlSwitchedChainShapeMemo = memo;
        }
        return memo.Resolve(calleeMap, out info);
    }

    private static bool TryDescribeSwitchedChainStructural(
        CompiledPredicate predicate, List<int> callFids,
        out TryMeElseChainInfo? info)
    {
        info = null;
        byte[] code = predicate.BytecodeUnfused;
        if (code.Length == 0) return false;
        if ((Opcode)code[0] != Opcode.SwitchOnTerm) return false;
        // ADR-025 — this legacy recogniser parses me-else boundaries with a
        // linear scan; an inline ITE (always carrying a `jump`) would be
        // MIS-parsed as a clause boundary, so reject the shape outright (the
        // operand-following TryMeElseChain / full indexed describers cover it).
        if (ContainsInlineIteOpcode(code)) return false;

        // Skip past the switch_on_term + switch_on_arg cascade.
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.SwitchOnTerm) { pc += 17; continue; }
            if (op == Opcode.SwitchOnArg) { pc += 21; continue; }
            break;
        }
        if (pc >= code.Length) return false;
        if ((Opcode)code[pc] != Opcode.Try) return false;

        // Walk the final chain: try (9) + retry* (5 each) + trust (5).
        // The address operands point at clause body Meta opcodes in
        // source order — the WAM lays out clauseBodyPos in source
        // order and the chain emit indexes into it the same way.
        var addresses = new List<int>(predicate.ClauseCount);
        addresses.Add(BytecodeIO.ReadInt32(code, pc + 1));
        pc += 9;
        bool sawTrust = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Retry)
            {
                addresses.Add(BytecodeIO.ReadInt32(code, pc + 1));
                pc += 5;
                continue;
            }
            if (op == Opcode.Trust)
            {
                addresses.Add(BytecodeIO.ReadInt32(code, pc + 1));
                pc += 5;
                sawTrust = true;
                break;
            }
            // Anything else after the chain (sub-dispatches, bucket
            // chains) — we're done walking the chain, just verify
            // we already collected N entries.
            break;
        }
        if (!sawTrust) return false;
        if (addresses.Count != predicate.ClauseCount) return false;

        // Sort addresses to get source order (chain emit is already
        // in source order, but defending against future changes).
        var sorted = addresses.ToList();
        sorted.Sort();
        var ranges = new List<(int, int)>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            int start = sorted[i];
            int end = i + 1 < sorted.Count ? sorted[i + 1] : code.Length;
            ranges.Add((start, end));
        }

        // Verify each clause body's opcodes are in the IL subset.
        // The bodies open with a Meta dbg-info marker which
        // EmitClauseBody handles as a no-op.
        foreach (var (s, e) in ranges)
        {
            int q = s;
            while (q < e)
            {
                var op = (Opcode)code[q];
                var opInfo = OpcodeTable.Get((byte)op);
                if (!opInfo.IsDefined || opInfo.Size == 0) return false;
                if (!IsClauseBodyOpcodeStructural(op, predicate, q, callFids)) return false;
                q += opInfo.Size;
            }
        }

        info = new TryMeElseChainInfo { Clauses = ranges };
        return true;
    }

    /// <summary>emits IL for a switched-chain predicate by
    /// reusing the <see cref="CompileTryMeElseChain"/>
    /// path. The two recognisers produce the same
    /// <see cref="TryMeElseChainInfo"/> shape (per-clause body
    /// ranges); the emit doesn't need to know which dispatch path
    /// the WAM emitted — it always walks clauses linearly with IL
    /// CPs at boundaries.</summary>
    private PredicateDelegate CompileSwitchedChain(
        CompiledPredicate predicate, TryMeElseChainInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
        => CompileTryMeElseChain(predicate, info, calleeMap);

    /// <summary>emits IL for a fully indexed predicate,
    /// reproducing the WAM switch dispatch (O(1) key lookup) and bucket
    /// backtracking via the <see cref="IlIndexedDispatchInfo"/> chain-node
    /// model, rather than the linear walk. Clause bodies are
    /// emitted once; chain nodes set up the next-node choice point and
    /// branch to their body; a runtime resolver picks the entry node from
    /// the indexed argument.</summary>
    private PredicateDelegate CompileIndexedDispatch(
        CompiledPredicate predicate, IlIndexedDispatchInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        lock (IndexedDelegateHolder.RegistrationLock)
        {
            int holderKey = _nextHolderKey;
            var emitSelf = SelfFromHolder(holderKey);
            var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
                $"ShumwayIl_idx_{predicate.FunctorId}", doVerify: DoVerify || DebugMode);
            EmitIndexedDispatchBody(emit, predicate, info, calleeMap, emitSelf,
                typeof(Func<Activation, int, bool>));   // runtime path: SelfFromHolder → Func
            var del = FinishEmit(emit,
            $"compile fid={predicate.FunctorId} {FidName(predicate.FunctorId)}/{predicate.Arity} clauses={predicate.ClauseCount}");
            IndexedDelegateHolder.Register(holderKey, del);
            _nextHolderKey = holderKey + 1;
            return del;
        }
    }

    private static void EmitIndexedDispatchBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        IlIndexedDispatchInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        SelfDelegateEmitter emitSelf,
        System.Type selfDelType)
    {
        if (CpFreeIndexedCensus)
            AnalyzeIndexedBucketGuards(predicate, info, calleeMap);
        int K = info.Nodes.Count;
        int N = info.Clauses.Count;
        // ADR-031 indexed buckets — recognise the CP-free guard clauses once
        // (per clause; every node of an accepted clause routes through its
        // shared guard block via the idxnext local).
        var guardPlan = PlanIndexedGuards(predicate, info, calleeMap);
        var gcCtx = guardPlan is not null ? new GuardContEmitContext() : null;
        int totalCallSites = CountNonTailCallOpcodes(predicate.BytecodeUnfused)
            + (guardPlan?.ExtraDynSites ?? 0);
        // Cursor layout: 0 = initial (resolve); 1..K = chain node
        // (cursor = nodeIndex + 1); K+1.. = call-site forward resumes.
        int callBase = K + 1;

        var failLabel = emit.DefineLabel("idx_fail");
        var nodeLabels = new Sigil.Label[K];
        for (int n = 0; n < K; n++) nodeLabels[n] = emit.DefineLabel($"idx_node_{n}");
        var bodyLabels = new Sigil.Label[N];
        for (int i = 0; i < N; i++) bodyLabels[i] = emit.DefineLabel($"idx_body_{i}");
        var resumeLabels = new Sigil.Label[totalCallSites];
        for (int j = 0; j < totalCallSites; j++)
            resumeLabels[j] = emit.DefineLabel($"idx_call_resume_{j + 1}");

        _emitOwnerFid = predicate.FunctorId;

        // CSE (mirrors the region Stage-11 hoist): every chain node's
        // PushIlChoicePoint reloads the SAME self-delegate — a per-push holder
        // dictionary probe on the runtime path. Hoist it to ONE local ahead of
        // the cursor switch (which dominates every node label, fresh AND
        // backtrack re-entries); gate on ≥2 pushes so the load+store only ever
        // shrinks the per-invocation work.
        SelfDelegateEmitter effectiveSelf = emitSelf;
        int pushSites = 0;
        for (int n = 0; n < K; n++)
            if (info.Nodes[n].NextCursor >= 0) pushSites++;
        if (pushSites >= 2)
        {
            var selfDelLoc = emit.DeclareLocal(selfDelType, "iselfdel");
            emitSelf(emit);
            emit.StoreLocal(selfDelLoc);
            effectiveSelf = e => e.LoadLocal(selfDelLoc);
        }

        // Self-tail-recursion target: a self Execute in a clause body branches
        // here (its args already in the argument registers) instead of the
        // marker / dispatch-loop round trip — an in-method loop. Marked before
        // the cursor-0 resolve so the loop re-runs the index decision on the
        // new arguments.
        var selfEntry = emit.DefineLabel("idx_self_entry");

        // ---- Top: dispatch on the incoming cursor (arg 1). ----
        // one O(1) jump table (IL `switch`) over the dense cursor
        // space — 0 → entry resolve; 1..K → chain node; K+1.. → call-site
        // resume — replacing the linear compare chain every invocation (fresh
        // calls AND backtrack re-entries) used to pay in full. An out-of-range
        // cursor falls through to the entry, exactly as the old chain did.
        var cursorLabels = new Sigil.Label[callBase + totalCallSites];
        cursorLabels[0] = selfEntry;
        for (int n = 0; n < K; n++) cursorLabels[n + 1] = nodeLabels[n];
        for (int j = 0; j < totalCallSites; j++)
            cursorLabels[callBase + j] = resumeLabels[j];
        emit.LoadArgument(1);
        emit.Switch(cursorLabels);
        emit.Branch(selfEntry);     // cursor out of range (unreachable) → entry
        emit.MarkLabel(selfEntry);
        // cursor 0 — the initial call: decide the entry node from the indexed
        // argument. The index decision is compiled to inline IL (deref + tag
        // test + key compares branching straight to the node labels — the
        // GProlog-style compiled switch), eliminating the per-call resolver
        // (a per-engine dictionary lookup + a runtime graph walk). Falls back
        // to that resolver only when the graph can't be built.
        if (!TryEmitInlineIndexResolve(emit, info, nodeLabels))
        {
            // Fallback. The functor id is emitted through EmitFunctorId
            // so a persisted-bundle .dll gets it patched at
            // LoadBundle; for runtime promotion it's a direct ldc.i4.
            var entry = emit.DeclareLocal<int>("idx_entry");
            emit.LoadArgument(0);
            EmitFunctorId(emit, predicate.FunctorId);
            emit.Call(IlIndexedDispatchResolveByFidMethod);
            emit.StoreLocal(entry);
            // node indices are dense 0..K-1 → O(1) jump table
            // instead of a linear compare chain.
            emit.LoadLocal(entry);
            emit.Switch(nodeLabels);
            emit.Branch(failLabel);   // unreachable: resolver always returns a valid node
        }

        // ---- Chain nodes: push the next-node CP (if any), run the clause body.
        //      ADR-031 indexed buckets: a node whose clause is an accepted
        //      guard skips the push — it stores the next node's ENGINE cursor
        //      (-1 for a chain tail) in the idxnext local; the shared guard
        //      block's fail stub dispatches on it. ----
        Sigil.Local? idxNext = guardPlan is not null
            ? emit.DeclareLocal<int>("idxnext") : null;
        for (int n = 0; n < K; n++)
        {
            emit.MarkLabel(nodeLabels[n]);
            int next = info.Nodes[n].NextCursor;
            if (guardPlan is not null && guardPlan.GuardOk[info.Nodes[n].ClauseIndex])
            {
                emit.LoadConstant(next >= 0 ? next + 1 : -1);
                emit.StoreLocal(idxNext!);
            }
            else if (next >= 0)
            {
                emit.LoadArgument(0);            // engine
                effectiveSelf(emit);             // → PredicateDelegate (hoisted local)
                emit.LoadConstant(next + 1);     // resume cursor of the next node
                emit.LoadConstant(predicate.Arity);
                emit.Call(EnginePushIlCpMethod);
            }
            emit.Branch(bodyLabels[info.Nodes[n].ClauseIndex]);
        }

        // ---- Clause bodies, emitted once and shared across nodes. ----
        int siteCounter = 0;
        for (int i = 0; i < N; i++)
        {
            emit.MarkLabel(bodyLabels[i]);
            if (guardPlan is not null && guardPlan.GuardOk[i])
            {
                var ginfo = guardPlan.Info[i];
                int guardEnd = ginfo.CutPc;
                // ADR-034 — the guard embeds dynamic snapshots: staleness
                // tests + a fallback (CP materialized from idxnext + plain
                // guard slice + jump into the shared post-commit body).
                var dynFids = ginfo.EmbeddedDynamicFids;
                Sigil.Label? dynFb = null, dynBody = null;
                if (dynFids is { Count: > 0 })
                {
                    dynFb = emit.DefineLabel($"idx_dynfb_{i}");
                    dynBody = emit.DefineLabel($"idx_dynbody_{i}");
                    foreach (int df in dynFids)
                    {
                        emit.LoadArgument(0);
                        EmitFunctorId(emit, df);
                        emit.Call(EngineIsDynMutatedMethod);
                        emit.BranchIfTrue(dynFb);
                    }
                }
                EmitCpFreeGuardClause(emit,
                    (s, e, fl) =>
                    {
                        bool isGuardSlice = e <= guardEnd;
                        if (dynBody is not null && !isGuardSlice)
                            emit.MarkLabel(dynBody);
                        EmitClauseBody(
                            emit, predicate.BytecodeUnfused, s, e, fl, predicate.CallSites,
                            callSiteIndexCounter: () => ++siteCounter,
                            resumeLabels: resumeLabels,
                            emitSelfDelegate: effectiveSelf,
                            calleeMap: calleeMap,
                            cursorBase: callBase,
                            selfFunctorId: predicate.FunctorId,
                            selfTailLabel: selfEntry,
                            forceLeafRuleInline: isGuardSlice,
                            localSalt: isGuardSlice ? $"_idxg{i}" : "",
                            guardContCtx: gcCtx);
                    },
                    predicate.BytecodeUnfused, info.Clauses[i].Start, info.Clauses[i].End,
                    ginfo,
                    failLabel /* unused: dynamic dispatch */, failLabel,
                    effectiveSelf, 0 /* unused */, predicate.Arity,
                    salt: $"_idx{i}",
                    dynamicFailDispatch: () =>
                    {
                        // Guard failed: continue at the chain's next node —
                        // the engine cursor in idxnext indexes the SAME label
                        // array the method's cursor dispatch uses; the tail
                        // sentinel (-1) falls through the unsigned switch.
                        emit.LoadLocal(idxNext!);
                        emit.Switch(cursorLabels);
                        emit.Branch(failLabel);
                    },
                    dynamicCursor: e2 => e2.LoadLocal(idxNext!));
                if (dynFb is not null)
                {
                    emit.MarkLabel(dynFb);
                    // Materialize the skipped bucket CP (unless chain tail),
                    // then the plain un-inlined guard + cut, then join the
                    // shared post-commit body.
                    var skipPush = emit.DefineLabel($"idx_dynfb_nopush_{i}");
                    emit.LoadLocal(idxNext!);
                    emit.LoadConstant(0);
                    emit.BranchIfLess(skipPush);
                    emit.LoadArgument(0);
                    effectiveSelf(emit);
                    emit.LoadLocal(idxNext!);
                    emit.LoadConstant(predicate.Arity);
                    emit.Call(EnginePushIlCpMethod);
                    emit.MarkLabel(skipPush);
                    int cutSz = OpcodeTable.Get(
                        (Opcode)predicate.BytecodeUnfused[guardEnd]).Size;
                    EmitClauseBody(emit, predicate.BytecodeUnfused,
                        info.Clauses[i].Start, guardEnd + cutSz,
                        failLabel, predicate.CallSites,
                        callSiteIndexCounter: () => ++siteCounter,
                        resumeLabels: resumeLabels,
                        emitSelfDelegate: effectiveSelf,
                        calleeMap: calleeMap,
                        cursorBase: callBase,
                        selfFunctorId: predicate.FunctorId,
                        selfTailLabel: selfEntry,
                        localSalt: $"_idxfb{i}");
                    emit.Branch(dynBody!);
                }
                continue;
            }
            EmitClauseBody(emit, predicate.BytecodeUnfused, info.Clauses[i].Start, info.Clauses[i].End,
                failLabel, predicate.CallSites,
                callSiteIndexCounter: () => ++siteCounter,
                resumeLabels: resumeLabels,
                emitSelfDelegate: effectiveSelf,
                calleeMap: calleeMap,
                cursorBase: callBase,
                selfFunctorId: predicate.FunctorId,
                selfTailLabel: selfEntry);
        }

        if (gcCtx is not null)
            EmitGuardContEpilogues(emit, gcCtx, calleeMap, failLabel);   // ADR-033

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>Emits the first-/multi-argument index decision as inline IL —
    /// the compile-time-known <see cref="IndexGraph"/> walk lowered to native
    /// branches (deref + tag test + key compares), branching straight to the
    /// dispatch node labels. Replaces the per-call
    /// <see cref="IlIndexedDispatch.ResolveEntryByFunctorId"/> (a per-engine
    /// dictionary lookup + a runtime graph walk) with code the JIT keeps in
    /// registers — the GProlog-style compiled switch. Returns false (emitting
    /// nothing) when the graph can't be built, so the caller keeps the
    /// resolver-call fallback.
    ///
    /// <para>Atom / functor keys go through <see cref="EmitAtomId"/> /
    /// <see cref="EmitFunctorId"/> so a persisted bundle patches them to the
    /// runtime ids at load; integer keys are stable literals. An integer cell
    /// whose value is out of the int range simply matches no key and falls to
    /// the default — exactly the runtime walk's semantics — so no range guard
    /// is needed.</para></summary>
    private static bool TryEmitInlineIndexResolve(
        Sigil.Emit<PredicateDelegate> emit, IlIndexedDispatchInfo info,
        Sigil.Label[] nodeLabels, string salt = "")
    {
        // `salt` makes the label / local names unique when several indexed members
        // share one IL method (region compilation) — empty for the standalone path.
        IndexGraph? graph = IlIndexGraph.Build(info);
        if (graph is null) return false;
        IndexNode[] gnodes = graph.Nodes;
        var gLabels = new Sigil.Label[gnodes.Length];
        for (int i = 0; i < gnodes.Length; i++)
            gLabels[i] = emit.DefineLabel($"idx_g{i}{salt}");

        var cellLoc = emit.DeclareLocal<Cell>($"idx_cell{salt}");
        var tmpCell = emit.DeclareLocal<Cell>($"idx_tmpcell{salt}");
        var tagLoc = emit.DeclareLocal<int>($"idx_tag{salt}");
        var keyLoc = emit.DeclareLocal<int>($"idx_key{salt}");
        var longLoc = emit.DeclareLocal<long>($"idx_long{salt}");

        Sigil.Label Target(IndexTarget t) => t.IsNode ? gLabels[t.Value] : nodeLabels[t.Value];

        emit.Branch(gLabels[0]);

        for (int i = 0; i < gnodes.Length; i++)
        {
            emit.MarkLabel(gLabels[i]);
            IndexNode node = gnodes[i];

            // ----- Deref the tested argument into cellLoc (one level, exactly
            //       like IlIndexGraph.DerefArg). -----
            emit.LoadArgument(0);
            emit.LoadConstant(node.ArgIdx);
            emit.Call(EngineGetRegisterMethod);
            emit.StoreLocal(cellLoc);
            // if cellLoc.Tag == Ref: cellLoc = engine.GetHeap(engine.Deref(cellLoc.AsHeapIndex))
            emit.LoadLocalAddress(cellLoc);
            emit.Call(CellTagIdGetter);
            emit.LoadConstant((int)Tag.Ref);
            var notRef = emit.DefineLabel($"idx_g{i}_notref{salt}");
            emit.UnsignedBranchIfNotEqual(notRef);
            emit.LoadArgument(0);                       // engine (receiver of GetHeap)
            emit.LoadArgument(0);                       // engine (receiver of Deref)
            emit.LoadLocalAddress(cellLoc);
            emit.Call(CellAsHeapIndexGetter);
            emit.Call(EngineDerefMethod);
            emit.Call(EngineGetHeapMethod);
            emit.StoreLocal(cellLoc);
            emit.MarkLabel(notRef);

            // ----- ADR-027: for a sub-argument node, walk the bounded path into
            //       the argument (list head/tail, struct arg) before keying. A
            //       miss returns a REF sentinel, so the tag test below routes it to
            //       the table default — exactly IlIndexGraph.TargetFor's semantics. -----
            if (node.Sub0 >= 0)
            {
                emit.LoadArgument(0);            // engine
                emit.LoadLocal(cellLoc);        // cell (deref'd arg)
                emit.LoadConstant(node.Sub0);
                emit.LoadConstant(node.Sub1);
                emit.Call(IlWalkSubOrMissMethod);
                emit.StoreLocal(cellLoc);       // cell = terminal sub-cell (or miss)
            }

            // ----- Load the (deref'd) tag once. -----
            emit.LoadLocalAddress(cellLoc);
            emit.Call(CellTagIdGetter);
            emit.StoreLocal(tagLoc);

            switch (node.Kind)
            {
                case IndexNodeKind.Term:
                    EmitTagBranch(emit, tagLoc, (int)Tag.Lis, Target(node.ListTarget));
                    EmitTagBranch(emit, tagLoc, (int)Tag.Str, Target(node.StructTarget));
                    EmitTagBranch(emit, tagLoc, (int)Tag.Atom, Target(node.ConstTarget));
                    EmitTagBranch(emit, tagLoc, (int)Tag.Int, Target(node.ConstTarget));
                    EmitTagBranch(emit, tagLoc, (int)Tag.Float, Target(node.ConstTarget));
                    // ADR-048: a NON-EMPTY packed list is a cons and takes the
                    // list bucket; the length guard keeps empty PSTR (= [])
                    // on the sound var chain, where the const bucket's []
                    // clauses are still reachable.
                    emit.LoadLocal(cellLoc);
                    emit.Call(IlIsNonEmptyPstrMethod);
                    emit.BranchIfTrue(Target(node.ListTarget));
                    emit.Branch(Target(node.VarTarget));   // Ref / anything else
                    break;

                case IndexNodeKind.Int:
                {
                    emit.LoadLocal(tagLoc);
                    emit.LoadConstant((int)Tag.Int);
                    emit.UnsignedBranchIfNotEqual(Target(node.DefaultTarget));
                    emit.LoadLocalAddress(cellLoc);
                    emit.Call(CellAsIntGetter);
                    emit.StoreLocal(longLoc);
                    int[] keys = node.Keys!;
                    for (int k = 0; k < keys.Length; k++)
                    {
                        emit.LoadLocal(longLoc);
                        emit.LoadConstant((long)keys[k]);
                        emit.BranchIfEqual(Target(node.Targets![k]));
                    }
                    emit.Branch(Target(node.DefaultTarget));
                    break;
                }

                case IndexNodeKind.Atom:
                {
                    emit.LoadLocal(tagLoc);
                    emit.LoadConstant((int)Tag.Atom);
                    emit.UnsignedBranchIfNotEqual(Target(node.DefaultTarget));
                    emit.LoadLocalAddress(cellLoc);
                    emit.Call(CellAsAtomIdGetter);
                    emit.StoreLocal(keyLoc);
                    int[] keys = node.Keys!;
                    for (int k = 0; k < keys.Length; k++)
                    {
                        emit.LoadLocal(keyLoc);
                        EmitAtomId(emit, keys[k]);          // patchable in persisted bundles
                        emit.BranchIfEqual(Target(node.Targets![k]));
                    }
                    emit.Branch(Target(node.DefaultTarget));
                    break;
                }

                case IndexNodeKind.Struct:
                {
                    // ADR-028: for a structure-sub node whose terminal is a nested
                    // list (Tag.Lis), the runtime resolver keys on the cons functor.
                    // The inline fast path instead routes it to the (sound)
                    // full-bucket default — the list-headed clause is in that chain,
                    // so the answer is identical; only its fast-path determinism is
                    // forgone. Str terminals (the common case) key precisely below.
                    emit.LoadLocal(tagLoc);
                    emit.LoadConstant((int)Tag.Str);
                    emit.UnsignedBranchIfNotEqual(Target(node.DefaultTarget));
                    // fid = engine.GetHeap(cellLoc.AsHeapIndex).AsFunctorId
                    emit.LoadArgument(0);
                    emit.LoadLocalAddress(cellLoc);
                    emit.Call(CellAsHeapIndexGetter);
                    emit.Call(EngineGetHeapMethod);
                    emit.StoreLocal(tmpCell);
                    emit.LoadLocalAddress(tmpCell);
                    emit.Call(CellAsFunctorIdGetter);
                    emit.StoreLocal(keyLoc);
                    int[] keys = node.Keys!;
                    for (int k = 0; k < keys.Length; k++)
                    {
                        emit.LoadLocal(keyLoc);
                        EmitFunctorId(emit, keys[k]);       // patchable in persisted bundles
                        emit.BranchIfEqual(Target(node.Targets![k]));
                    }
                    emit.Branch(Target(node.DefaultTarget));
                    break;
                }
            }
        }
        return true;
    }

    private static void EmitTagBranch(
        Sigil.Emit<PredicateDelegate> emit, Sigil.Local tagLoc, int tag, Sigil.Label target)
    {
        emit.LoadLocal(tagLoc);
        emit.LoadConstant(tag);
        emit.BranchIfEqual(target);
    }

    /// <summary>Emits the IL for a non-indexed multi-clause predicate
    /// (try_me_else chain). cursor 0 runs clause 1 with an IL CP push
    /// pointing at cursor 1, cursor N runs clause N+1, etc. The last
    /// clause runs without a CP push, matching the trust_me semantics.
    /// The CP-push trampoline reuses the same <see cref="IndexedDelegateHolder"/>
    /// machinery as the indexed path.</summary>
    private PredicateDelegate CompileTryMeElseChain(
        CompiledPredicate predicate, TryMeElseChainInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        lock (IndexedDelegateHolder.RegistrationLock)
        {
            return CompileTryMeElseChainUnlocked(predicate, info, calleeMap);
        }
    }

    private PredicateDelegate CompileTryMeElseChainUnlocked(
        CompiledPredicate predicate, TryMeElseChainInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        int holderKey = _nextHolderKey;
        var emitSelf = SelfFromHolder(holderKey);

        var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
            $"ShumwayIl_tryelse_{predicate.FunctorId}",
            doVerify: DoVerify || DebugMode);
        EmitTryMeElseChainBody(emit, predicate, info, calleeMap, emitSelf,
            typeof(Func<Activation, int, bool>));   // runtime path: SelfFromHolder → Func

        var del = FinishEmit(emit,
            $"compile fid={predicate.FunctorId} {FidName(predicate.FunctorId)}/{predicate.Arity} clauses={predicate.ClauseCount}");
        IndexedDelegateHolder.Register(holderKey, del);
        _nextHolderKey = holderKey + 1;
        return del;
    }

}
