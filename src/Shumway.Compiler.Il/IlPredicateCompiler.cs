using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Tier-1 IL compiler. Translates supported WAM bytecode shapes into a
/// <see cref="PredicateDelegate"/> via Sigil's typed IL emission so the
/// promoted predicate runs without going through the bytecode dispatch
/// loop. The Tier-0/1 promotion infrastructure (counter, store,
/// dispatcher) lives in <see cref="Shumway.Embedding.IlPromotionStore"/>.
///
/// <para>Supported shapes (Phase 1):</para>
/// <list type="bullet">
/// <item><b>Single-clause facts</b> whose body uses only
///   <c>get_atom</c>, <c>get_integer</c>, <c>get_nil</c>,
///   <c>get_value_x</c>, and a trailing <c>proceed</c>.</item>
/// <item><b>Multi-clause indexed predicates</b> shaped as
///   <c>switch_on_term + switch_on_atom + per-clause bodies</c> where
///   every clause is the trivial <c>get_atom &lt;id&gt; A0 ; proceed</c>
///   form (i.e. each clause matches a distinct atom in argument 1).
///   This shape is what the WAM compiler emits for predicates like
///   <c>color(red). color(green). color(blue).</c></item>
/// </list>
///
/// <para>Predicates outside the supported subset cause
/// <see cref="CanCompile"/> to return <c>false</c>; <see cref="Compile"/>
/// throws <see cref="NotSupportedException"/>. Callers (the promotion
/// store) fall back to Tier 0 in either case.</para>
/// </summary>
public sealed class IlPredicateCompiler
{
    /// <summary>Chunk 173: when <c>true</c>, every WAM opcode the IL
    /// emitter handles gets an extra <see cref="IlDebugMarkers"/>
    /// call wired in immediately after its main emit. The marker
    /// re-checks the opcode's WAM-level post-condition against the
    /// engine's runtime state (e.g. after <c>put_value_y slot, arg</c>
    /// the marker asserts <c>X[arg] == Y[slot]</c>) and throws if
    /// the IL diverged — pinpoints exactly which opcode's IL emit
    /// is buggy when running a known-bad flow. Per-call cost is
    /// one method call + a cell compare, so debug-mode IL is
    /// noticeably slower but still fast enough for end-to-end
    /// reproduction. Default <c>false</c>; flip on from a test or
    /// REPL session to bug-hunt.
    ///
    /// <para>Static rather than per-instance because the IL emit
    /// methods on this type are static and threading the flag
    /// through each call site would be invasive — the alternative
    /// would be wrapping every opcode emit in a virtual dispatch
    /// or a closure capture, both heavier than a single static
    /// read at IL-emit time. The flag is read once when each
    /// opcode is being emitted, not per IL-invocation.</para></summary>
    // Every `if (DebugMode) { ... }` body that emits markers lives under
    // `#if DEBUG` so Release builds strip the calls entirely — the
    // generated IL has no DbgCheck_* call sites at all. The property
    // itself stays writable in both configurations: a Release executable
    // emitted via ExecutableEmitter still compiles when its embedded
    // bootstrap sets `DebugMode = true` from `SHUMWAY_IL_DEBUG=1`; the
    // write just has no observable effect because no Release code path
    // reads it.
    public static bool DebugMode { get; set; }

    /// <summary>When <c>true</c>, every Sigil <c>Emit&lt;T&gt;</c> we
    /// allocate runs with verification enabled — Sigil's continuous
    /// stack-state tracking that catches malformed IL at emit time
    /// rather than at JIT time. Verification is O(N²) in the bytecode
    /// size (chunk 171 measurements on a 13 KB predicate: ~13 s with
    /// verification on, ~250 ms with verification off), so for any
    /// large hot predicate we want it off — Sigil's
    /// <c>doVerify=false</c> mode emits the same IL but skips the
    /// per-instruction <c>RollingVerifier.Transition</c> /
    /// <c>VerifiableTracker.CollapseAndVerify</c> work, and the JIT
    /// catches any genuine corruption when the delegate is invoked.
    /// Tests / debug paths leave this on. Default <c>false</c> so
    /// auto-promotion (the hot path) pays the linear cost only.</summary>
    public bool DoVerify { get; set; } = false;

    /// <summary>Sigil's <c>Seal</c> runs three optional passes —
    /// <c>ElideCasts</c>, <c>PatchBranches</c>, and an always-on
    /// <c>InjectTailCall</c> — and <c>PatchBranches</c> in
    /// particular is O(N²) because every short-form patch calls
    /// <c>InsertInstruction</c> which O(N)-scans the branches /
    /// marks / returns tables to shift their indices. On the
    /// chunk-171 1280-clause benchmark <c>PatchBranches</c> +
    /// <c>InsertInstruction</c> was 35% of total compile time.
    /// Default to <see cref="OptimizationOptions.None"/> so we
    /// only emit the IL we asked for — the JIT can do its own
    /// short-branch patching at the same cost we'd avoid.</summary>
    public Sigil.OptimizationOptions Optimizations { get; set; }
        = Sigil.OptimizationOptions.None;

    private static readonly MethodInfo CellAtomMethod =
        typeof(Cell).GetMethod(nameof(Cell.Atom), new[] { typeof(int) })!;
    private static readonly MethodInfo CellIntMethod =
        typeof(Cell).GetMethod(nameof(Cell.Int), new[] { typeof(long) })!;
    private static readonly MethodInfo EngineUnifyMethod =
        typeof(Engine).GetMethod(
            nameof(Engine.UnifyRegisterWithCell),
            new[] { typeof(int), typeof(Cell) })!;
    private static readonly MethodInfo EngineUnifyRegistersMethod =
        typeof(Engine).GetMethod(
            nameof(Engine.UnifyRegisters),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo EngineGetRegisterMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetRegister), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineGetHeapMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetHeap), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineDerefMethod =
        typeof(Engine).GetMethod(nameof(Engine.Deref), new[] { typeof(int) })!;
    private static readonly MethodInfo EnginePushIlCpMethod =
        typeof(Engine).GetMethod(
            nameof(Engine.PushIlChoicePoint),
            new[] { typeof(Func<Engine, int, bool>), typeof(int), typeof(int) })!;
    // Chunk 76 — PGO: instrumented IL calls this on each clause success.
    private static readonly MethodInfo IlProfileCountersBump =
        typeof(IlProfileCounters).GetMethod(nameof(IlProfileCounters.Bump))!;
    private static readonly MethodInfo CellTagGetter =
        typeof(Cell).GetProperty(nameof(Cell.Tag))!.GetGetMethod()!;
    private static readonly MethodInfo CellAsHeapIndexGetter =
        typeof(Cell).GetProperty(nameof(Cell.AsHeapIndex))!.GetGetMethod()!;
    private static readonly MethodInfo CellAsAtomIdGetter =
        typeof(Cell).GetProperty(nameof(Cell.AsAtomId))!.GetGetMethod()!;
    private static readonly MethodInfo CellAsIntGetter =
        typeof(Cell).GetProperty(nameof(Cell.AsInt))!.GetGetMethod()!;
    private static readonly MethodInfo CellAsFunctorIdGetter =
        typeof(Cell).GetProperty(nameof(Cell.AsFunctorId))!.GetGetMethod()!;
    // Cell.TagId yields the tag as a clean int32 on the IL stack (avoids
    // enum-vs-int comparison friction in the inline index-resolve emit). It
    // lives in Shumway.Core so a persisted-bundle .dll can call it.
    private static readonly MethodInfo CellTagIdGetter =
        typeof(Cell).GetProperty(nameof(Cell.TagId))!.GetGetMethod()!;
    private static readonly MethodInfo EngineSetRegisterMethod =
        typeof(Engine).GetMethod(nameof(Engine.SetRegister), new[] { typeof(int), typeof(Cell) })!;
    // ADR-018 — arithmetic instruction set runtime helpers (Shumway.Builtins.
    // ArithEvalStack). The Tier-1 emit calls these statics directly, so the
    // a_eval_* opcodes run the same eval-stack code as the Tier-0 interpreter.
    private static readonly MethodInfo ArithPushIntMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.PushInt), new[] { typeof(long) })!;
    private static readonly MethodInfo ArithPushRegMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.PushReg), new[] { typeof(Engine), typeof(int) })!;
    private static readonly MethodInfo ArithPushYMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.PushY), new[] { typeof(Engine), typeof(int) })!;
    private static readonly MethodInfo ArithBinMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.Bin), new[] { typeof(int) })!;
    private static readonly MethodInfo ArithUnMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.Un), new[] { typeof(int) })!;
    private static readonly MethodInfo ArithIsRegMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.IsReg), new[] { typeof(Engine), typeof(int) })!;
    private static readonly MethodInfo ArithIsPermMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.IsPerm), new[] { typeof(Engine), typeof(int) })!;
    private static readonly MethodInfo ArithSetRegMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.SetReg), new[] { typeof(Engine), typeof(int) })!;
    private static readonly MethodInfo ArithSetPermMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.SetPerm), new[] { typeof(Engine), typeof(int) })!;
    private static readonly MethodInfo ArithFusedBinMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.FusedBin))!;
    private static readonly MethodInfo ArithFusedCmpMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.FusedCmp))!;
    private static readonly MethodInfo ArithCmpMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.Cmp), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineGetYMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetY), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineSetYMethod =
        typeof(Engine).GetMethod(nameof(Engine.SetY), new[] { typeof(int), typeof(Cell) })!;
    private static readonly MethodInfo EngineAllocateMethod =
        typeof(Engine).GetMethod(nameof(Engine.Allocate), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineDeallocateMethod =
        typeof(Engine).GetMethod(nameof(Engine.Deallocate), Type.EmptyTypes)!;
    private static readonly MethodInfo EngineNeckCutMethod =
        typeof(Engine).GetMethod(nameof(Engine.NeckCut), Type.EmptyTypes)!;
    // Chunk 215 — deep cut (get_level + cut). GetLevel stashes the
    // procedure-entry barrier (_b0) into a Y slot; CutToLevel reads it
    // back and commits. Both are plain engine calls — the CP / _b0
    // infrastructure is identical to Tier-0 (B0 set at entry by the
    // caller's Call/Execute, saved per-CP in CpB0Offset, IL clause CPs are
    // real engine CPs that Cut removes).
    private static readonly MethodInfo EngineGetLevelMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetLevel), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineCutToLevelMethod =
        typeof(Engine).GetMethod(nameof(Engine.CutToLevel), new[] { typeof(int) })!;
    // Phase 28 — a cut is a goal boundary, so pending attribute wakeups must
    // run before the IL-emitted cut commits (the IL counterpart of the
    // chunk-335 flush-before-cut). Returns false when a wakeup failed, which
    // the emit turns into a branch to the clause fail label. Fast-returns true
    // with a single field read when nothing is queued, so non-attvar programs
    // pay essentially nothing per cut.
    private static readonly MethodInfo EngineFlushWakeupsForIlCutMethod =
        typeof(Engine).GetMethod(nameof(Engine.FlushWakeupsForIlCut), Type.EmptyTypes)!;
    // Chunk 216 — indexed-dispatch entry resolver (mirrors the WAM switch
    // cascade, returns the entry chain-node cursor). Keyed by functor id
    // so the same IL works under runtime promotion AND a persisted bundle
    // loaded in a fresh process — the functor id is name-relative via
    // chunk-197 EmitFunctorId, and the resolver builds the dispatch model
    // lazily from the engine's linked code on first call.
    private static readonly MethodInfo IlIndexedDispatchResolveByFidMethod =
        typeof(IlIndexedDispatch).GetMethod(nameof(IlIndexedDispatch.ResolveEntryByFunctorId))!;
    // Chunk 218 — setter for engine.BuiltinReturnPc. The IL emit pre-sets
    // this to a resume marker before invoking a backtrackable builtin, so
    // the builtin's CP resume re-enters the IL caller correctly.
    private static readonly MethodInfo EngineBuiltinReturnPcSetter =
        typeof(Engine).GetProperty(nameof(Engine.BuiltinReturnPc))!.GetSetMethod()!;
    private static readonly MethodInfo EngineSetPcMethod =
        typeof(Engine).GetMethod(
            nameof(Engine.SetPc),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(int) }, null)!;
    private static readonly MethodInfo EngineSetB0Method =
        typeof(Engine).GetMethod(
            nameof(Engine.SetB0),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(int) }, null)!;
    // Phase 16 chunk 182 — threaded IL non-tail Call. Setting Cp to a
    // resume marker before transferring to the callee is how the IL
    // caller registers its forward continuation.
    private static readonly MethodInfo EngineSetCpMethod =
        typeof(Engine).GetMethod(
            nameof(Engine.SetCp),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(int) }, null)!;
    private static readonly MethodInfo EngineBGetter =
        typeof(Engine).GetProperty(nameof(Engine.B))!.GetGetMethod()!;
#if DEBUG
    private static readonly MethodInfo EngineEGetter =
        typeof(Engine).GetProperty(nameof(Engine.E))!.GetGetMethod()!;
#endif
    private static readonly MethodInfo EngineIlTailCallPendingSetter =
        typeof(Engine).GetProperty(nameof(Engine.IlTailCallPending))!.GetSetMethod()!;
    // The watermark-gated heap-GC safe point the dispatch loop runs at every
    // goal boundary; a self-tail-recursion in-method loop must call it at the
    // back-edge so an allocating loop still collects (the loop bypasses the
    // dispatch loop that would otherwise run it).
    private static readonly MethodInfo EngineMaybeCollectHeapMethod =
        typeof(Engine).GetMethod(nameof(Engine.MaybeCollectHeap), Type.EmptyTypes)!;
    private static readonly MethodInfo EngineCurrentFunctorAddressesGetter =
        typeof(Engine).GetProperty(nameof(Engine.CurrentFunctorAddresses))!.GetGetMethod()!;
    private static readonly MethodInfo IlExecuteHelperResolveMethod =
        typeof(IlExecuteHelper).GetMethod(nameof(IlExecuteHelper.Resolve))!;
    // Theme-1 / WAM stripping: an IL caller dispatches a callee by FUNCTOR ID
    // (a resume marker with cursor 0 = entry), not by resolving it to a WAM
    // address. The dispatcher routes the marker to the callee's IL delegate
    // directly via IlByFunctorId when it has IL, or falls back to its WAM
    // address otherwise — so an IL-only callee needs no WAM body/address.
    private static readonly MethodInfo EngineEncodeResumeMarkerMethod =
        typeof(Engine).GetMethod(nameof(Engine.EncodeResumeMarker))!;
    // Phase 19 — meta-call dispatch helper.
    private static readonly MethodInfo IlMetaCallHelperDispatchMethod =
        typeof(IlMetaCallHelper).GetMethod(nameof(IlMetaCallHelper.Dispatch))!;
    private static readonly MethodInfo IlMetaCallHelperReadIntRegisterMethod =
        typeof(IlMetaCallHelper).GetMethod(nameof(IlMetaCallHelper.ReadIntRegister))!;
    // ---------- get_structure / put_structure (chunk 48) ----------
    private static readonly MethodInfo EngineGetStructureMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetStructure), new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo EnginePutStructureMethod =
        typeof(Engine).GetMethod(nameof(Engine.PutStructure), new[] { typeof(int), typeof(int) })!;
    // ADR-020 reserve-upfront roots.
    private static readonly MethodInfo EnginePutStructureReservedMethod =
        typeof(Engine).GetMethod(nameof(Engine.PutStructureReserved), new[] { typeof(int), typeof(int), typeof(int) })!;
    private static readonly MethodInfo EnginePutListReservedMethod =
        typeof(Engine).GetMethod(nameof(Engine.PutListReserved), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyArgCellMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyArgCell), new[] { typeof(Cell) })!;
    private static readonly MethodInfo EngineUnifyVariableXMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyVariableX), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyValueXMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyValueX), new[] { typeof(int) })!;
    // ADR-019 inline nested compound build/match.
    private static readonly MethodInfo EngineUnifyStructureMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyStructure), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyListMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyList), Type.EmptyTypes)!;
    private static readonly MethodInfo EngineUnifyVariableYMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyVariableY), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyValueYMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyValueY), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyVoidMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyVoid), new[] { typeof(int) })!;
    // ---------- get_list / put_list / pstr (chunk 49) ----------
    private static readonly MethodInfo EngineGetListMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetList), new[] { typeof(int) })!;
    private static readonly MethodInfo EnginePutListMethod =
        typeof(Engine).GetMethod(nameof(Engine.PutList), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineMakePstrMethod =
        typeof(Engine).GetMethod(nameof(Engine.MakePstr), new[] { typeof(string) })!;
    private static readonly MethodInfo EngineUnifyRegisterWithHeapAtMethod =
        typeof(Engine).GetMethod(
            nameof(Engine.UnifyRegisterWithHeapAt),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo IlGetPstrHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.GetPstr))!;
    private static readonly MethodInfo IlPutPstrHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.PutPstr))!;
#if DEBUG
    // Chunk 173 debug-mode marker methods. Reflection lookups stripped
    // from Release — no static init cost, no field, no IL site.
    private static readonly MethodInfo DbgCheckPutValueYMethod =
        typeof(IlDebugMarkers).GetMethod(nameof(IlDebugMarkers.Check_PutValueY))!;
    private static readonly MethodInfo DbgCheckPutValueXMethod =
        typeof(IlDebugMarkers).GetMethod(nameof(IlDebugMarkers.Check_PutValueX))!;
    private static readonly MethodInfo DbgCheckGetVariableYMethod =
        typeof(IlDebugMarkers).GetMethod(nameof(IlDebugMarkers.Check_GetVariableY))!;
    private static readonly MethodInfo DbgCheckGetVariableXMethod =
        typeof(IlDebugMarkers).GetMethod(nameof(IlDebugMarkers.Check_GetVariableX))!;
    private static readonly MethodInfo DbgCheckPutVariableYMethod =
        typeof(IlDebugMarkers).GetMethod(nameof(IlDebugMarkers.Check_PutVariableY))!;
    private static readonly MethodInfo DbgCheckPutVariableXMethod =
        typeof(IlDebugMarkers).GetMethod(nameof(IlDebugMarkers.Check_PutVariableX))!;
    private static readonly MethodInfo DbgCheckPreCallMethod =
        typeof(IlDebugMarkers).GetMethod(nameof(IlDebugMarkers.Check_PreCall))!;
    private static readonly MethodInfo DbgCheckPostCallMethod =
        typeof(IlDebugMarkers).GetMethod(nameof(IlDebugMarkers.Check_PostCall))!;
    private static readonly MethodInfo DbgCheckAllocateMethod =
        typeof(IlDebugMarkers).GetMethod(nameof(IlDebugMarkers.Check_Allocate))!;
    private static readonly MethodInfo DbgCheckDeallocateMethod =
        typeof(IlDebugMarkers).GetMethod(nameof(IlDebugMarkers.Check_Deallocate))!;
#endif

    // Phase 16 chunk 183: chunk-50 IL Call helper, chunk-66 meta-CP
    // backtrack-driver and PreCallB reader, and chunk-174 floor-pinning
    // variant are all gone — IL non-tail Call is now threaded
    // (resume-marker dispatch in chunk 182), and the natural CP cascade
    // handles backtracking across IL/bytecode boundaries without help.
    private static readonly MethodInfo EngineAllocateHeapUnboundMethod =
        typeof(Engine).GetMethod(nameof(Engine.AllocateHeapUnbound), Type.EmptyTypes)!;
    private static readonly MethodInfo CellRefMethod =
        typeof(Cell).GetMethod(nameof(Cell.Ref), new[] { typeof(int) })!;
    private static readonly MethodInfo BuiltinsRegistryGetByIdMethod =
        typeof(Shumway.Builtins.BuiltinsRegistry).GetMethod(
            nameof(Shumway.Builtins.BuiltinsRegistry.GetById),
            new[] { typeof(int) })!;
    private static readonly MethodInfo BuiltinEntryImplGetter =
        typeof(Shumway.Builtins.BuiltinEntry).GetProperty(
            nameof(Shumway.Builtins.BuiltinEntry.Impl))!.GetGetMethod()!;
    private static readonly MethodInfo BuiltinImplInvokeMethod =
        typeof(Shumway.Builtins.BuiltinImpl).GetMethod(
            nameof(Shumway.Builtins.BuiltinImpl.Invoke))!;

    /// <summary>Returns <c>true</c> iff <paramref name="predicate"/> is in
    /// the supported subset. See the class docstring for the catalog.
    /// <paramref name="calleeMap"/> (chunk 50) lets the check inspect
    /// <c>Call</c> targets — an IL <c>Call</c> only compiles when the
    /// callee is itself a "leaf" predicate (single-clause, body-less,
    /// only head matching + proceed), so the synchronous sub-call can
    /// never push choice points that would survive past the IL caller.</summary>
    public bool CanCompile(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return CanCompileCore(predicate, calleeMap, allowIndexedDispatch: true);
    }

    /// <summary>Eligibility check with control over the chunk-216 indexed-
    /// dispatch shape. The runtime promotion path allows it (fast O(1)
    /// dispatch); the persisted-bundle path
    /// (<see cref="PersistedIlBuilder.CanPersist"/>) passes
    /// <paramref name="allowIndexedDispatch"/>=false because its IL bakes a
    /// runtime model-holder key that a fresh process wouldn't have — those
    /// predicates fall back to bytecode in the bundle. Both paths still
    /// accept the older indexed-atom / try-me-else / switched-chain
    /// shapes.</summary>
    internal bool CanCompileCore(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, bool allowIndexedDispatch)
    {
        if (predicate.ClauseCount == 1) return CanCompileSingleClause(predicate, calleeMap);
        // Chunk 216 — full indexed dispatch (O(1) switch + bucket chains).
        // Preferred over the linear IndexedAtom / SwitchedChain recognisers
        // for any switch-led shape; those remain as fallbacks for shapes it
        // doesn't model.
        if (allowIndexedDispatch && TryDescribeIndexed(predicate, calleeMap, out _)) return true;
        if (TryDescribeIndexedAtomPredicate(predicate, calleeMap, out _)) return true;
        if (TryDescribeTryMeElseChain(predicate, calleeMap, out _)) return true;
        return TryDescribeSwitchedChain(predicate, calleeMap, out _);
    }

    /// <summary>Wraps <see cref="IlIndexedDispatch.TryDescribe"/> with the
    /// IL-subset body-opcode check.</summary>
    private static bool TryDescribeIndexed(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out IlIndexedDispatchInfo? info)
        => IlIndexedDispatch.TryDescribe(predicate,
            (op, pc) => IsClauseBodyOpcode(op, predicate, pc, calleeMap), out info);

    /// <summary>True iff this predicate compiles to the chunk-216/217 full
    /// indexed-dispatch IL, whose delegate rebuilds its switch model lazily by
    /// reading the predicate's WAM bytecode at first call
    /// (<see cref="IlIndexedDispatch"/>). Such a predicate's WAM body must NOT
    /// be stripped (--strip-wam) — it would crash on first dispatch. Every other
    /// IL shape (single-clause, indexed-atom, try-me-else / switched chain) bakes
    /// the whole dispatch into the IL and is safe to strip.</summary>
    internal static bool UsesWamBackedIndexedDispatch(
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
        => TryDescribeIndexed(predicate, calleeMap, out _);

    /// <summary>Builds and serialises the WAM-independent dispatch graph for a
    /// chunk-216 indexed predicate, so the bundle can persist it and strip the
    /// predicate's WAM body (--strip-wam). Returns null when the predicate
    /// doesn't use that shape (its IL is already self-contained, no graph
    /// needed).</summary>
    internal static byte[]? BuildPersistableIndexGraph(
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        if (!TryDescribeIndexed(predicate, calleeMap, out var info)) return null;
        var graph = IlIndexGraph.Build(info!);
        return graph is null ? null : IndexGraphCodec.Encode(graph);
    }

    /// <summary>Diagnostic (Tier-1 coverage analysis): when a predicate is
    /// not IL-compilable, returns a short reason — the distinct body
    /// opcodes outside the IL subset (e.g. "get_level,cut" for a deep-cut
    /// predicate), or "call->unresolved" when a Call's callee is missing
    /// from the calleeMap, or "shape" when every opcode is supported but
    /// the bytecode doesn't match one of the recognised clause layouts.
    /// Returns null when the predicate <i>is</i> compilable.</summary>
    public string DescribeRejection(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (CanCompile(predicate, calleeMap)) return null!;
        byte[] code = predicate.Bytecode;
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);
        bool callUnresolved = false;
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Call)
            {
                int siteFid = FindCallSiteFunctorId(predicate.CallSites, pc);
                if (calleeMap is null || siteFid < 0 || !calleeMap.ContainsKey(siteFid))
                    callUnresolved = true;
            }
            else if (IsAEvalOpcode(op))
            {
                // ADR-018 — only a bigint/float-literal operand blocks (report
                // the kind so the rejection points at the real cause).
                if (!IsSupportedAEval(code, pc))
                    unsupported.Add($"{op}(lit)");
            }
            else if (!IsSupportedOpcode(op) && !IsHeadMatchingOpcode(op)
                     && !IsStructuralDispatchOpcode(op))
            {
                unsupported.Add(op.ToString());
            }
            pc += OpcodeTable.Get(op).Size;
        }
        if (unsupported.Count > 0) return string.Join(",", unsupported);
        if (callUnresolved) return "call->unresolved";
        return "shape";
    }

    /// <summary>Opcodes that form the clause-dispatch skeleton the shape
    /// detectors consume (try/retry/trust chains, switch dispatch, the
    /// dynamic-predicate prefix) plus the terminators handled inline by
    /// the emit. Not "unsupported body opcodes" — excluded from the
    /// <see cref="DescribeRejection"/> opcode report so the genuine
    /// blockers stand out.</summary>
    private static bool IsStructuralDispatchOpcode(Opcode op) => op switch
    {
        Opcode.TryMeElse or Opcode.RetryMeElse or Opcode.TrustMe => true,
        Opcode.Try or Opcode.Retry or Opcode.Trust => true,
        Opcode.SwitchOnTerm or Opcode.SwitchOnArg => true,
        Opcode.Proceed or Opcode.Execute or Opcode.Call or Opcode.CallBuiltin => true,
        Opcode.EnterDynamic or Opcode.CheckVisible => true,
        _ => false,
    };

    /// <summary>Emits a <see cref="PredicateDelegate"/> for the predicate.
    /// The caller is responsible for first checking
    /// <see cref="CanCompile"/>; passing in an unsupported predicate
    /// throws <see cref="NotSupportedException"/>.</summary>
    public PredicateDelegate Compile(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        DiagnoseInlineCandidates(predicate, calleeMap);
        DiagnoseRegion(predicate, calleeMap);
        if (predicate.ClauseCount == 1)
        {
            if (!CanCompileSingleClause(predicate, calleeMap))
                throw new NotSupportedException(
                    $"Single-clause predicate (fid={predicate.FunctorId}) is outside the IL subset.");
            return CompileSingleClause(predicate, calleeMap);
        }
        if (TryDescribeIndexed(predicate, calleeMap, out var indexed))
            return CompileIndexedDispatch(predicate, indexed!, calleeMap);
        if (TryDescribeIndexedAtomPredicate(predicate, calleeMap, out var info))
            return CompileIndexedAtomPredicate(predicate, info!, calleeMap);
        if (TryDescribeTryMeElseChain(predicate, calleeMap, out var chain))
            return CompileTryMeElseChain(predicate, chain!, calleeMap);
        if (TryDescribeSwitchedChain(predicate, calleeMap, out var switched))
            return CompileSwitchedChain(predicate, switched!, calleeMap);
        throw new NotSupportedException(
            $"Multi-clause predicate (fid={predicate.FunctorId}, clauses={predicate.ClauseCount}) "
            + "is outside the IL subset.");
    }

    // ============================================================================
    // Chunk 76 — PGO: two-phase profile-guided IL compilation
    // ============================================================================

    /// <summary>Profile key counter — allocated per instrumented
    /// predicate, indexing <see cref="IlProfileCounters"/>. Separate
    /// namespace from <see cref="_nextHolderKey"/>.</summary>
    private static int _nextProfileKey = 1;

    /// <summary>Result of a phase-1 PGO compile: the (instrumented)
    /// delegate plus the profile key the engine later passes to
    /// <see cref="CompileOptimized"/>. A <see cref="ProfileKey"/> of
    /// <c>-1</c> means the predicate's shape isn't PGO-eligible — it
    /// was compiled normally and no phase-2 recompile should fire.</summary>
    public readonly record struct PgoCompileResult(
        PredicateDelegate Delegate, int ProfileKey);

    /// <summary>Phase-1 PGO compile. For the indexed-atom shape this
    /// emits the <em>instrumented</em> form whose ground dispatch
    /// records which atom matched; for every other shape it's an
    /// ordinary <see cref="Compile"/> with <see cref="PgoCompileResult.ProfileKey"/>
    /// set to <c>-1</c>.</summary>
    public PgoCompileResult CompileInstrumented(
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (predicate.ClauseCount > 1
            && TryDescribeIndexedAtomPredicate(predicate, out var info))
        {
            lock (IndexedDelegateHolder.RegistrationLock)
            {
                int profileKey = _nextProfileKey++;
                IlProfileCounters.Allocate(profileKey, info!.Clauses.Count);
                var del = CompileIndexedAtomPredicateUnlocked(
                    predicate, info, profileKey, groundOrder: null);
                return new PgoCompileResult(del, profileKey);
            }
        }
        return new PgoCompileResult(Compile(predicate, calleeMap), -1);
    }

    /// <summary>Phase-2 PGO compile. Reads the hit counts accumulated
    /// under <paramref name="profileKey"/> and recompiles the
    /// indexed-atom predicate with the ground-dispatch <c>cmp</c> chain
    /// ordered most-frequently-matched-atom first. Releases the profile
    /// counters afterwards. Falls back to a plain compile when the
    /// shape isn't indexed-atom (defensive — the engine only calls this
    /// for keys produced by an indexed-atom phase 1).</summary>
    public PredicateDelegate CompileOptimized(
        CompiledPredicate predicate, int profileKey,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (profileKey < 0
            || !TryDescribeIndexedAtomPredicate(predicate, out var info))
        {
            return Compile(predicate, calleeMap);
        }
        long[]? counts = IlProfileCounters.Get(profileKey);
        int n = info!.Clauses.Count;
        var order = Enumerable.Range(0, n).ToArray();
        if (counts is not null)
        {
            // Descending by hit count; Array.Sort isn't stable but ties
            // among equally-cold atoms don't matter.
            Array.Sort(order, (a, b) => counts[b].CompareTo(counts[a]));
        }
        lock (IndexedDelegateHolder.RegistrationLock)
        {
            var del = CompileIndexedAtomPredicateUnlocked(
                predicate, info, profileKey: -1, groundOrder: order);
            IlProfileCounters.Release(profileKey);
            return del;
        }
    }

    // ============================================================================
    // Shape 1: single-clause facts
    // ============================================================================

    private static bool CanCompileSingleClause(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        byte[] code = predicate.Bytecode;
        int pc = 0;
        bool sawTerminator = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Execute)
            {
                // Execute is a body-tail terminator: control transfers
                // to the callee, which proceeds back to our caller's
                // continuation. The IL emission for Execute returns
                // from the delegate (with the IlTailCallPending flag
                // set), so any opcodes after it in the bytecode are
                // unreachable.
                sawTerminator = true;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Call)
            {
                // Non-tail Call: chunk 66 emits a meta-CP at every IL
                // Call site that drives Engine.BacktrackRunner on
                // resume to retry callee alternatives and rejoin the
                // body at a post-call cursor. No leaf restriction
                // needed — just confirm we have a calleeMap entry so
                // the runtime can resolve the functor.
                if (calleeMap is null) return false;
                int siteFid = FindCallSiteFunctorId(predicate.CallSites, pc);
                if (siteFid < 0) return false;
                if (!calleeMap.TryGetValue(siteFid, out _)) return false;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.CallBuiltin)
            {
                // Phase 19: call/1..7 and '$call'/2 are now IL-eligible via
                // IlMetaCallHelper.Dispatch. The CallBuiltin emit at
                // EmitClauseBody treats them as threaded non-tail calls
                // (chunk-182 forward-resume + cursor switch).
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (IsAEvalOpcode(op))
            {
                if (!IsSupportedAEval(code, pc)) return false;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (IsSupportedOpcode(op))
            {
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Proceed)
            {
                sawTerminator = true;
                pc += 1;
                continue;
            }
            return false;
        }
        return sawTerminator;
    }

    /// <summary>Chunk 173: arity of the callee a Call / Execute
    /// site dispatches to, recovered from the functor table so
    /// debug markers know how many X registers to dump. The
    /// FunctorTable.Lookup result is the canonical
    /// <c>(atomId, arity)</c> pair the linker keyed off.</summary>
#if DEBUG
    private static int ResolveCalleeArity(int siteFunctorId)
    {
        var (_, arity) = Shumway.Core.FunctorTable.Lookup(siteFunctorId);
        return arity;
    }
#endif

    private static int FindCallSiteFunctorId(
        IReadOnlyList<CallSite> sites, int opcodeOffset)
    {
        for (int i = 0; i < sites.Count; i++)
            if (sites[i].OpcodeOffset == opcodeOffset) return sites[i].CalleeFunctorId;
        return -1;
    }

    /// <summary>A "leaf" predicate is a single-clause predicate whose
    /// body is purely head matching + a trailing proceed — no body
    /// calls, no cut, no allocate. Calling it can't push choice points
    /// (no try_me_else) and can't escape with a tail call (no Execute
    /// / Call). The IL <c>Call</c> emission relies on this so the
    /// sub-call always runs to completion in one shot.</summary>
    private static bool IsLeafPredicate(CompiledPredicate pred)
    {
        if (pred.ClauseCount != 1) return false;
        byte[] code = pred.Bytecode;
        int pc = 0;
        bool sawProceed = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Proceed) { sawProceed = true; pc += 1; continue; }
            if (IsHeadMatchingOpcode(op))
            {
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            return false;
        }
        return sawProceed;
    }

    /// <summary>Phase 29 case 2 (detector) — a single-clause RULE that can be
    /// inlined into a caller's IL method, generalising
    /// <see cref="IsInlinableLeafRule"/> to a body that also makes USER calls and
    /// uses an environment frame (permanents). Single clause; ends in
    /// proceed / deallocate_proceed (a trailing tail <c>Execute</c> is rejected —
    /// un-tailing it at a non-tail inline site, and telling a user predicate from
    /// a tail-position backtrackable/meta builtin which also lowers to Execute, is
    /// deferred); no meta (<c>call</c>/<c>$call</c>) or backtrackable builtin.
    /// Everything else — allocate/deallocate, Y-slots, head/body unify+arith,
    /// deterministic <c>CallBuiltin</c>, non-tail user <c>Call</c> — is allowed.
    /// <para><paramref name="allowCut"/>: when false (the diagnostic / sizing use)
    /// a cut disqualifies; when true (the emit use) the deep-cut family
    /// (allocate_get_level / get_level / cut) and neck_cut are admitted — the emit
    /// sets <c>B0 = engine.B</c> at the inline entry so the captured barrier prunes
    /// only the inlined body's choice points.</para></summary>
    internal static bool IsInlinableRule(CompiledPredicate pred, bool allowCut = false)
    {
        if (pred.ClauseCount != 1) return false;
        byte[] code = pred.Bytecode;
        int pc = 0;
        bool endsTerminal = false;   // last op was proceed / deallocate_proceed
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            switch (op)
            {
                case Opcode.Proceed: endsTerminal = true; pc += 1; continue;
                case Opcode.DeallocateProceed:
                    endsTerminal = true; pc += OpcodeTable.Get((byte)op).Size; continue;
                // A trailing tail call to a USER predicate (chunk 368): the emit
                // un-tails it into a threaded non-tail call at a non-tail inline
                // site. In linked runtime bytecode `Execute` always targets a user
                // predicate (a tail-position builtin is ExecuteBuiltin, rejected
                // below), so this is safe to thread.
                case Opcode.Execute: endsTerminal = true; pc += OpcodeTable.Get((byte)op).Size; continue;
                case Opcode.ExecuteBuiltin: return false;   // tail builtin — needs CallBuiltin machinery
                case Opcode.Meta: pc += 6; continue;
                case Opcode.Cut:
                case Opcode.NeckCut:
                case Opcode.GetLevel:
                case Opcode.AllocateGetLevel:
                    if (!allowCut) return false;
                    endsTerminal = false;
                    pc += OpcodeTable.Get((byte)op).Size;
                    continue;
                case Opcode.CallBuiltin:
                {
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                        BytecodeIO.ReadInt32(code, pc + 1));
                    if (entry.Name is "call" or "$call"
                        || IsBacktrackableBuiltinName(entry.Name))
                        return false;
                    endsTerminal = false;
                    pc += OpcodeTable.Get((byte)op).Size;
                    continue;
                }
                default:
                {
                    int size = OpcodeTable.Get((byte)op).Size;
                    if (size <= 0) return false;
                    endsTerminal = false;
                    pc += size;
                    continue;
                }
            }
        }
        return endsTerminal;
    }

    /// <summary>Phase 29 case 1 — gates the extension of the chunk-69 leaf inline
    /// to single-clause RULES with a deterministic builtin/arith/unify body
    /// (<see cref="IsInlinableLeafRule"/>). Default OFF; <c>SHUMWAY_INLINE_RULES=1</c>
    /// enables it while it is validated, before the default flips.</summary>
    internal static readonly bool InlineLeafRules =
        System.Environment.GetEnvironmentVariable("SHUMWAY_INLINE_RULES") == "1";

    /// <summary>Phase 29 case 2 — gates inlining a single-clause RULE that makes
    /// USER calls and/or cuts (<see cref="IsInlinableRule"/> with allowCut) into a
    /// metaCp caller. Default OFF; <c>SHUMWAY_INLINE_RULES2=1</c> while validated.
    /// Restricted to the metaCp caller path (where the forward-resume cursor count
    /// is extended to cover the inlined body's threaded calls).</summary>
    internal static readonly bool InlineRules2 =
        System.Environment.GetEnvironmentVariable("SHUMWAY_INLINE_RULES2") == "1";

    /// <summary>The non-tail <c>Call</c> sites in <paramref name="predicate"/> whose
    /// callee is a case-2 inlinable single-clause rule (has a body call and/or a
    /// cut — a pure leaf rule stays on the case-1 path). Maps the call-site
    /// <c>pc</c> to the callee. Empty unless <see cref="InlineRules2"/>.</summary>
    private static Dictionary<int, CompiledPredicate> ComputeRuleInlineSites(
        CompiledPredicate predicate, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        var sites = new Dictionary<int, CompiledPredicate>();
        // Runtime DynamicMethod path only: the persisted-bundle emit computes its
        // own (base) callSiteCount and would desync against the extended resume
        // cursors. (Restriction lifted when the persisted path counts them too.)
        if (!InlineRules2 || calleeMap is null || _persistPatches is not null) return sites;
        byte[] code = predicate.Bytecode;
        int pc = 0;
        while (pc < code.Length)
        {
            if ((Opcode)code[pc] == Opcode.Call)
            {
                int fid = FindCallSiteFunctorId(predicate.CallSites, pc);
                if (fid >= 0 && calleeMap.TryGetValue(fid, out var callee)
                    && IsInlinableRule(callee, allowCut: true)
                    && !IsInlinableLeafRule(callee))   // pure leaf rules → case 1
                {
                    sites[pc] = callee;
                    if (System.Environment.GetEnvironmentVariable("SHUMWAY_IL_SHAPE") == "1")
                        System.Console.Error.WriteLine(
                            $"[rule-inline] caller fid={predicate.FunctorId} callee fid={callee.FunctorId} "
                            + $"bodycalls={CountNonTailCallOpcodes(callee.Bytecode)}");
                }
            }
            pc += (Opcode)code[pc] == Opcode.Meta ? 6 : OpcodeTable.Get(code[pc]).Size;
        }
        return sites;
    }

    /// <summary>Extra forward-resume cursors the inlined rule bodies need — each
    /// body's own non-tail <c>Call</c> sites thread through the CALLER's cursor
    /// space, PLUS a trailing tail <c>Execute</c> (chunk 368), which the emit
    /// un-tails into a threaded non-tail call and so also takes a cursor. The
    /// caller's resume-label array must be sized to include all of them.</summary>
    private static int CountRuleInlineExtraCursors(
        IReadOnlyDictionary<int, CompiledPredicate> sites)
    {
        int extra = 0;
        foreach (var callee in sites.Values)
            extra += CountRuleBodyThreadedCalls(callee.Bytecode);
        return extra;
    }

    /// <summary>Threaded-call cursors an inlined rule body consumes: its non-tail
    /// <c>Call</c>s plus a trailing tail <c>Execute</c> (un-tailed at a non-tail
    /// inline site). Must match exactly what the body emission consumes.</summary>
    private static int CountRuleBodyThreadedCalls(byte[] body)
    {
        int n = CountNonTailCallOpcodes(body);
        if (BodyEndsInExecute(body)) n++;
        return n;
    }

    /// <summary>True iff the body's terminal opcode is a tail <c>Execute</c>
    /// (a user-predicate tail call) — the case the inline emit un-tails.</summary>
    private static bool BodyEndsInExecute(byte[] body)
    {
        int pc = 0;
        bool lastWasExecute = false;
        while (pc < body.Length)
        {
            var op = (Opcode)body[pc];
            if (op == Opcode.Meta) { pc += 6; continue; }
            lastWasExecute = op == Opcode.Execute;
            int size = OpcodeTable.Get((byte)op).Size;
            if (size <= 0) return false;
            pc += size;
        }
        return lastWasExecute;
    }

    /// <summary>A single-clause RULE whose body can be inlined FLAT into a caller
    /// (Phase 29 case 1) — like <see cref="IsLeafPredicate"/> but allowing a body
    /// of deterministic builtins, arithmetic and unification. It must create no
    /// choice point, need no environment frame, make no user call, and not cut: so
    /// NO allocate/deallocate, NO cut/neck_cut/get_level, NO Call/Execute (any
    /// tier), and a <c>CallBuiltin</c> only to a deterministic, non-meta builtin.
    /// Such a body runs to completion in one shot exactly like a leaf's head match
    /// (a failing body op branches to the caller's fail label), so the EXISTING
    /// leaf-inline emit (<see cref="EmitClauseBody"/> with
    /// <c>suppressProceedReturn</c>) handles it with no new machinery — det
    /// builtins emit no resume cursor, arith/unify ops branch to the fail
    /// label.</summary>
    internal static bool IsInlinableLeafRule(CompiledPredicate pred)
    {
        if (pred.ClauseCount != 1) return false;
        byte[] code = pred.Bytecode;
        int pc = 0;
        bool sawProceed = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            switch (op)
            {
                case Opcode.Proceed: sawProceed = true; pc += 1; continue;
                case Opcode.Meta: pc += 6; continue;   // dbg-info, runtime no-op
                // CP-creating / env / cut / user-call → not flat-inlinable.
                case Opcode.Allocate:
                case Opcode.Deallocate:
                case Opcode.Cut:
                case Opcode.NeckCut:
                case Opcode.GetLevel:
                case Opcode.Call:
                case Opcode.Execute:
                case Opcode.CallIl:
                case Opcode.ExecuteIl:
                case Opcode.CallBytecode:
                case Opcode.ExecuteBytecode:
                case Opcode.ExecuteBuiltin:
                    return false;
                case Opcode.CallBuiltin:
                {
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                        BytecodeIO.ReadInt32(code, pc + 1));
                    // meta-call + backtrackable builtins need resume cursors /
                    // the enclosing-call machinery — not a flat body.
                    if (entry.Name is "call" or "$call"
                        || IsBacktrackableBuiltinName(entry.Name))
                        return false;
                    pc += OpcodeTable.Get((byte)op).Size;
                    continue;
                }
                default:
                {
                    int size = OpcodeTable.Get((byte)op).Size;
                    if (size <= 0) return false;   // unknown / variable-size → bail
                    pc += size;
                    continue;
                }
            }
        }
        return sawProceed;
    }

    /// <summary>True iff <paramref name="pred"/> is a pure FACT predicate: every
    /// clause is only head matching, and the bytecode is otherwise just the
    /// clause-dispatch skeleton (switch_on_* / try / retry / trust /
    /// try_me_else …) and <c>proceed</c> — no body calls, no environment
    /// (permanent Y variables → there are no get_variable_y / allocate
    /// opcodes), no arithmetic. Generalises <see cref="IsLeafPredicate"/> (the
    /// single-clause special case) to any clause count. Eligibility for inlining
    /// a multi-clause fact's clause dispatch into its caller's IL method —
    /// Phase 1 of docs/design/il-local-inlining.md. (Single-clause facts are
    /// already inlined by the chunk-69 leaf path; this covers the multi-clause
    /// generators, e.g. crypt's odd/even.)</summary>
    internal static bool IsFactPredicate(CompiledPredicate pred)
    {
        byte[] code = pred.Bytecode;
        int pc = 0;
        bool sawProceed = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Proceed) { sawProceed = true; pc += 1; continue; }
            if (op == Opcode.Meta) { pc += 6; continue; }   // dbg-info, runtime no-op
            if (IsHeadMatchingOpcode(op) || IsFactDispatchOpcode(op))
            {
                int size = OpcodeTable.Get((byte)op).Size;
                if (size <= 0) return false;
                pc += size;
                continue;
            }
            return false;   // a call / allocate / arith / Y-slot op → not a pure fact
        }
        return sawProceed;
    }

    /// <summary>The clause-dispatch skeleton opcodes a fact predicate may
    /// contain besides head matching and <c>proceed</c> (first-argument indexing
    /// + the try/retry/trust or try_me_else/retry_me_else/trust_me chains).</summary>
    private static bool IsFactDispatchOpcode(Opcode op) => op switch
    {
        Opcode.SwitchOnTerm or Opcode.SwitchOnArg => true,
        Opcode.SwitchOnAtom or Opcode.SwitchOnInteger or Opcode.SwitchOnStructure => true,
        Opcode.SwitchOnAtomArg or Opcode.SwitchOnIntegerArg or Opcode.SwitchOnStructureArg => true,
        Opcode.Try or Opcode.Retry or Opcode.Trust => true,
        Opcode.TryMeElse or Opcode.RetryMeElse or Opcode.TrustMe => true,
        Opcode.Nop => true,
        _ => false,
    };

    private static bool IsHeadMatchingOpcode(Opcode op) => op switch
    {
        Opcode.GetAtom => true,
        Opcode.GetInteger => true,
        Opcode.GetNil => true,
        Opcode.GetValueX => true,
        Opcode.GetVariableX => true,
        Opcode.GetStructure => true,
        Opcode.GetList => true,
        Opcode.UnifyAtom => true,
        Opcode.UnifyInteger => true,
        Opcode.UnifyNil => true,
        Opcode.UnifyVariableX => true,
        Opcode.UnifyValueX => true,
        Opcode.UnifyVoid => true,
        Opcode.UnifyStructure => true,   // ADR-019
        Opcode.UnifyList => true,        // ADR-019
        _ => false,
    };

    /// <summary>Catalog of opcodes that <see cref="EmitClauseBody"/>
    /// knows how to translate to IL. Excludes the control-flow tail
    /// (<c>proceed</c>), which is handled inline by the emit loop.</summary>
    private static bool IsSupportedOpcode(Opcode op) => op switch
    {
        // Head matching.
        Opcode.GetAtom => true,
        Opcode.GetInteger => true,
        Opcode.GetNil => true,
        Opcode.GetValueX => true,
        Opcode.GetVariableX => true,
        Opcode.GetVariableY => true,
        Opcode.GetValueY => true,
        // Body argument setup.
        Opcode.PutAtom => true,
        Opcode.PutInteger => true,
        Opcode.PutNil => true,
        Opcode.PutValueX => true,
        Opcode.PutValueY => true,
        Opcode.PutVariableX => true,
        Opcode.PutVariableY => true,
        // Body control.
        Opcode.CallBuiltin => true,
        Opcode.Allocate => true,
        Opcode.Deallocate => true,
        Opcode.NeckCut => true,
        // Deep cut (chunk 215): get_level captures the entry barrier into
        // a Y slot, cut commits to it. Emitted as engine.GetLevel /
        // engine.CutToLevel.
        Opcode.GetLevel => true,
        Opcode.Cut => true,
        // Chunk 220 — fused opcodes. Emit pair of engine calls; the
        // single-opcode-walk advances by the fused size, skipping the
        // padding Nop.
        Opcode.AllocateGetLevel => true,
        Opcode.DeallocateProceed => true,
        Opcode.Nop => true,   // padding inside fused opcodes; emit no-op
        Opcode.Execute => true,
        // Compound argument structure (chunk 48).
        Opcode.GetStructure => true,
        Opcode.PutStructure => true,
        Opcode.UnifyAtom => true,
        Opcode.UnifyInteger => true,
        Opcode.UnifyNil => true,
        Opcode.UnifyVariableX => true,
        Opcode.UnifyValueX => true,
        Opcode.UnifyVariableY => true,
        Opcode.UnifyValueY => true,
        Opcode.UnifyVoid => true,
        // List head matching (chunk 49).
        Opcode.GetList => true,
        Opcode.PutList => true,
        // ADR-019 inline nested compound build/match.
        Opcode.UnifyStructure => true,
        Opcode.UnifyList => true,
        // ADR-020 reserve-upfront roots (non-last nested compound build).
        Opcode.PutStructureR => true,
        Opcode.PutListR => true,
        // PSTR + Call (chunk 50).
        Opcode.GetPstr => true,
        Opcode.PutPstr => true,
        Opcode.Call => true,
        // ADR-018 arithmetic instruction set. a_eval_bin / a_eval_un /
        // a_eval_cmp carry no literal operand and are always emittable; the
        // operand-sensitive a_eval_push / a_eval_is are gated by
        // IsSupportedAEval (the callers check it before this opcode-only
        // method, so reaching here means the operand kind is in the subset).
        Opcode.AEvalPush or Opcode.AEvalIs => true,
        Opcode.AEvalBin or Opcode.AEvalUn or Opcode.AEvalCmp => true,
        // Fused flat ops carry only register / Y / int-literal operands — no
        // bigint/float-literal gating needed, so always IL-emittable.
        Opcode.AIntBin or Opcode.AIntCmp => true,
        // Meta dbg_info (chunk 55) — pure compile-time metadata; the
        // emit path skips it without producing any IL.
        Opcode.Meta => true,
        _ => false,
    };

    /// <summary>ADR-018: whether an <c>a_eval_*</c> opcode at <paramref name="pc"/>
    /// is within the IL subset. Every operator opcode is; the operand-carrying
    /// <c>a_eval_push</c> rejects a bigint (kind 1) or float (kind 2) literal,
    /// and <c>a_eval_is</c> only accepts a register (kind 3) or Y-slot (kind 4)
    /// target — mirroring the IL scalar subset's lack of a float/bigint literal
    /// path. A predicate that uses one of those falls back to Tier-0.</summary>
    private static bool IsSupportedAEval(byte[] code, int pc) => (Opcode)code[pc] switch
    {
        Opcode.AEvalPush => BytecodeIO.ReadInt32(code, pc + 1) is 0 or 3 or 4,
        Opcode.AEvalIs => BytecodeIO.ReadInt32(code, pc + 1) is 3 or 4 or 5 or 6,
        Opcode.AEvalBin or Opcode.AEvalUn or Opcode.AEvalCmp => true,
        _ => false,
    };

    private static bool IsAEvalOpcode(Opcode op) =>
        op is Opcode.AEvalPush or Opcode.AEvalBin or Opcode.AEvalUn
           or Opcode.AEvalIs or Opcode.AEvalCmp;

    private PredicateDelegate CompileSingleClause(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        // Case-2 rule inline (chunk 367): each inlined rule body's own non-tail
        // calls thread through THIS caller's forward-resume cursor space, so the
        // resume-label array must be sized to include them.
        int callSiteCount = CountNonTailCallOpcodes(predicate.Bytecode)
            + CountRuleInlineExtraCursors(ComputeRuleInlineSites(predicate, calleeMap));
        if (callSiteCount == 0)
        {
            // No meta-CP needed: pure head match + tail call (or no body).
            var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
                $"ShumwayIl_{predicate.FunctorId}_{predicate.Arity}",
                doVerify: DoVerify || DebugMode);
            EmitSingleClauseLeafBody(emit, predicate, calleeMap);
            return emit.CreateDelegate(Optimizations);
        }
        lock (IndexedDelegateHolder.RegistrationLock)
            return CompileSingleClauseWithMetaCpUnlocked(predicate, callSiteCount, calleeMap);
    }

    /// <summary>The shared single-clause-leaf body emit used by both the
    /// runtime path (<see cref="CompileSingleClause"/>, which builds a
    /// <c>DynamicMethod</c>) and the chunk-71 persisted-assembly path
    /// (<see cref="EmitToMethodBuilder"/>, which builds a static method
    /// on a <see cref="System.Reflection.Emit.TypeBuilder"/>). Pure head
    /// match + optional tail call, no IL choice points, no
    /// self-reference into <see cref="IndexedDelegateHolder"/>.</summary>
    private static void EmitSingleClauseLeafBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        var failLabel = emit.DefineLabel("fail");
        _emitOwnerFid = predicate.FunctorId;
        // Self-tail-recursion → in-method loop (chunk 349): a self Execute
        // branches here (args already in registers) rather than the marker /
        // dispatch-loop round trip. For a leaf the body start IS the cursor-0
        // entry (no cursor switch).
        var selfEntry = emit.DefineLabel("self_entry");
        emit.MarkLabel(selfEntry);
        EmitClauseBody(emit, predicate.Bytecode, 0, predicate.Bytecode.Length,
            failLabel, predicate.CallSites,
            callSiteIndexCounter: null, resumeLabels: null,
            calleeMap: calleeMap,
            selfFunctorId: predicate.FunctorId, selfTailLabel: selfEntry);
        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>Chunk 71: defines a static method named
    /// <paramref name="methodName"/> on <paramref name="typeBuilder"/>
    /// and emits the predicate's IL into it. Returns the
    /// <c>MethodBuilder</c> so the caller can later bake the type and
    /// resolve the method via reflection.
    ///
    /// <para>Routes to the right emission shape based on the predicate:
    /// single-clause-leaf (no IL CPs), single-clause-with-meta-CP,
    /// indexed-atom, or general try-me-else chain. The latter three
    /// need a static self-reference — they read their own delegate
    /// from <paramref name="delegatesField"/>[<paramref name="slot"/>],
    /// which the loader populates at runtime from the same method's
    /// <c>MethodInfo.CreateDelegate</c>.</para>
    ///
    /// <para><paramref name="delegatesField"/> and
    /// <paramref name="slot"/> are unused (and may be passed as
    /// <c>null</c> / <c>-1</c>) for the leaf shape, which never emits
    /// an IL CP push.</para></summary>
    public System.Reflection.Emit.MethodBuilder EmitPersistedMethod(
        System.Reflection.Emit.TypeBuilder typeBuilder,
        string methodName,
        CompiledPredicate predicate,
        System.Reflection.FieldInfo? delegatesField,
        int slot,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(typeBuilder);
        ArgumentNullException.ThrowIfNull(methodName);
        ArgumentNullException.ThrowIfNull(predicate);

        var emit = Sigil.Emit<PredicateDelegate>.BuildMethod(
            typeBuilder,
            methodName,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            System.Reflection.CallingConventions.Standard,
            doVerify: DoVerify || DebugMode);

        SelfDelegateEmitter? emitSelf = delegatesField is null
            ? null
            : SelfFromArrayField(delegatesField, slot);

        if (predicate.ClauseCount == 1)
        {
            int callSiteCount = CountNonTailCallOpcodes(predicate.Bytecode);
            if (callSiteCount == 0)
            {
                EmitSingleClauseLeafBody(emit, predicate, calleeMap);
            }
            else
            {
                if (emitSelf is null)
                    throw new InvalidOperationException(
                        "Single-clause meta-CP predicate needs a delegates field for self-reference.");
                EmitSingleClauseMetaCpBody(emit, predicate, callSiteCount, calleeMap, emitSelf);
            }
        }
        else if (TryDescribeIndexed(predicate, calleeMap, out var indexedInfo))
        {
            // Chunk 217 — full indexed dispatch (O(1) + buckets) in persisted
            // IL. The emit bakes the functor id via the chunk-197 patching
            // mechanism so a fresh process resolves the runtime id at
            // LoadBundle; the dispatch model is rebuilt lazily on first call
            // from the engine's linked code.
            if (emitSelf is null)
                throw new InvalidOperationException(
                    "Indexed-dispatch predicate needs a delegates field for self-reference.");
            EmitIndexedDispatchBody(emit, predicate, indexedInfo!, calleeMap, emitSelf);
        }
        else if (TryDescribeIndexedAtomPredicate(predicate, calleeMap, out var atomInfo))
        {
            if (emitSelf is null)
                throw new InvalidOperationException(
                    "Indexed-atom predicate needs a delegates field for self-reference.");
            EmitIndexedAtomBody(emit, predicate, atomInfo!, emitSelf, calleeMap: calleeMap);
        }
        else if (TryDescribeTryMeElseChain(predicate, calleeMap, out var chainInfo))
        {
            if (emitSelf is null)
                throw new InvalidOperationException(
                    "Try-me-else chain predicate needs a delegates field for self-reference.");
            EmitTryMeElseChainBody(emit, predicate, chainInfo!, calleeMap, emitSelf);
        }
        else if (TryDescribeSwitchedChain(predicate, calleeMap, out var switchedInfo))
        {
            // Chunk 189: switch_on_term-headed predicates emit through
            // the same try_me_else body emitter — only the recogniser
            // differs.
            if (emitSelf is null)
                throw new InvalidOperationException(
                    "Switched-chain predicate needs a delegates field for self-reference.");
            EmitTryMeElseChainBody(emit, predicate, switchedInfo!, calleeMap, emitSelf);
        }
        else
        {
            throw new NotSupportedException(
                $"Predicate (fid={predicate.FunctorId}, clauses={predicate.ClauseCount}) "
                + "is outside the IL subset.");
        }

        return emit.CreateMethod(Optimizations);
    }

    private PredicateDelegate CompileSingleClauseWithMetaCpUnlocked(
        CompiledPredicate predicate, int callSiteCount,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        int holderKey = _nextHolderKey;
        var emitSelf = SelfFromHolder(holderKey);
        var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
            $"ShumwayIl_metacp_{predicate.FunctorId}_{predicate.Arity}",
            doVerify: DoVerify || DebugMode);
        EmitSingleClauseMetaCpBody(emit, predicate, callSiteCount, calleeMap, emitSelf);
        var del = emit.CreateDelegate(Optimizations);
        IndexedDelegateHolder.Register(holderKey, del);
        _nextHolderKey = holderKey + 1;
        return del;
    }

    // ============================================================================
    // Chunk 359 — Tier-1 IL local-predicate inlining, Phase 1 (multi-clause facts)
    // (docs/design/il-local-inlining.md). Gated OFF by default behind
    // SHUMWAY_INLINE_FACTS=1 — a backtracking/cursor bug would give wrong
    // answers, so the default path is untouched while this is validated.
    // ============================================================================

    internal static readonly bool InlineFacts =
        System.Environment.GetEnvironmentVariable("SHUMWAY_INLINE_FACTS") != "0";

    /// <summary>A non-tail <c>Call p/n</c> site whose callee <c>p</c> is an
    /// eligible multi-clause fact, to be inlined into the caller's IL method.
    /// The fact's clause alternatives 2..K get cursors <see cref="BaseCursor"/>
    /// .. in the CALLER's cursor space; on backtrack the caller's delegate is
    /// re-entered at one of <see cref="AltLabels"/>; a clause match branches to
    /// <see cref="Continuation"/> (the caller's post-call code).</summary>
    private sealed class InlineSite
    {
        public required CompiledPredicate Fact { get; init; }
        public required IReadOnlyList<(int Start, int End)> ClauseRanges { get; init; }
        public required int BaseCursor { get; init; }
        public required Sigil.Label[] AltLabels { get; init; }      // length K-1, clauses 2..K
        public required Sigil.Label Continuation { get; init; }
    }

    /// <summary>Pre-scan a caller's body for inlinable multi-clause-fact Call
    /// sites, allocating each <c>K-1</c> alternative cursors starting at
    /// <paramref name="firstCursor"/> (must clear the call-site resume cursors)
    /// and pre-defining their labels. Returns the per-call-offset map and, via
    /// <paramref name="cursorsUsed"/>, how many cursors were taken. Empty when
    /// inlining is off / the budget would overflow.</summary>
    private static Dictionary<int, InlineSite> ComputeInlineSites(
        Sigil.Emit<PredicateDelegate> emit, CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        int firstCursor, out int cursorsUsed)
    {
        cursorsUsed = 0;
        var sites = new Dictionary<int, InlineSite>();
        if (!InlineFacts || calleeMap is null) return sites;
        byte[] code = predicate.Bytecode;
        int cursor = firstCursor;
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Call)
            {
                int fid = FindCallSiteFunctorId(predicate.CallSites, pc);
                if (fid >= 0 && calleeMap.TryGetValue(fid, out var callee)
                    && callee.ClauseCount >= 2 && IsFactPredicate(callee)
                    && TryGetFactClauseRanges(callee, out var ranges)
                    && ranges.Count == callee.ClauseCount
                    // Profitability gate (chunk 362): inline ONLY facts whose
                    // every clause has a distinct constant first arg, so the
                    // chunk-360 index pre-filter makes a BOUND call deterministic
                    // (the clear crypt-style win). A fact without that index
                    // (a grammar/dictionary fact with compound or repeated first
                    // args) inlines as a plain linear chain — no indexing gain —
                    // so the trampoline keeps those. This is the ONLY size-ish
                    // gate: re-entry is an O(1) jump table (see the cursor switch
                    // in EmitSingleClauseMetaCpBody), so inlining a wide fact no
                    // longer costs more than the trampoline — no clause-count
                    // budget is needed (an earlier one was masking that flaw).
                    && TryGetFactFirstArgKeys(callee.Bytecode, ranges, out _, out _))
                {
                    int k = ranges.Count;
                    if (cursor + (k - 1) >= Engine.ResumeMarkerCursorStride) break; // budget
                    var alt = new Sigil.Label[k - 1];
                    for (int j = 0; j < k - 1; j++)
                        alt[j] = emit.DefineLabel($"inl_{pc}_alt{j}");
                    sites[pc] = new InlineSite
                    {
                        Fact = callee, ClauseRanges = ranges, BaseCursor = cursor,
                        AltLabels = alt, Continuation = emit.DefineLabel($"inl_{pc}_cont"),
                    };
                    cursor += k - 1;
                }
            }
            pc += op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
        }
        cursorsUsed = cursor - firstCursor;
        if (sites.Count > 0 && System.Environment.GetEnvironmentVariable("SHUMWAY_IL_SHAPE") == "1")
            foreach (var s in sites.Values)
                System.Console.Error.WriteLine(
                    $"[inline] caller fid={predicate.FunctorId} callee fid={s.Fact.FunctorId} "
                    + $"arity={s.Fact.Arity} clauses={s.ClauseRanges.Count}");
        return sites;
    }

    /// <summary>Exploratory diagnostic (SHUMWAY_IL_SHAPE=2): classify every
    /// non-tail <c>Call</c> site's callee by inline-candidate shape, to see what
    /// an EXTENDED inliner could reach beyond today's index-eligible multi-clause
    /// fact. One <c>[cand] category callee=fid clauses=N</c> line per site;
    /// aggregate a run with <c>sort | uniq -c</c>. Categories: <c>1cl-fact</c>
    /// (leaf-inlinable today), <c>1cl-rule</c> (single-clause rule w/ body),
    /// <c>Ncl-rule</c> (multi-clause rule), <c>Nfact-IDX(inlines)</c> (what the
    /// current inliner takes), <c>Nfact-NOIDX</c> (multi-clause fact without a
    /// unique-constant first-arg index), <c>Nfact-unshaped</c>,
    /// <c>ext-or-builtin</c>, <c>var-or-control</c>.</summary>
    /// <summary>Stage-1 diagnostic (SHUMWAY_IL_SHAPE=3): for each promoted
    /// predicate, build its IL-eligible region (Phase 29 region compilation) and
    /// report its size at the default budget and uncapped — to size real regions
    /// and tune the budget before the emit stages. No emit.</summary>
    private void DiagnoseRegion(
        CompiledPredicate predicate, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_IL_SHAPE") != "3") return;
        if (calleeMap is null) return;
        bool Eligible(CompiledPredicate p) => CanCompileCore(p, calleeMap, allowIndexedDispatch: true);
        var capped = IlRegionBuilder.Build(predicate, calleeMap, extraEligible: Eligible);
        var uncapped = IlRegionBuilder.Build(predicate, calleeMap, budgetBytes: 1_000_000, extraEligible: Eligible);
        if (uncapped.MemberCount <= 1) return;   // no local closure → uninteresting
        System.Console.Error.WriteLine(
            $"[region] root fid={predicate.FunctorId} members={capped.MemberCount}"
            + $" bytes={capped.TotalBytecodeBytes} (uncapped members={uncapped.MemberCount}"
            + $" bytes={uncapped.TotalBytecodeBytes})");
    }

    private static void DiagnoseInlineCandidates(
        CompiledPredicate predicate, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_IL_SHAPE") != "2") return;
        byte[] code = predicate.Bytecode;
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Call)
            {
                int fid = FindCallSiteFunctorId(predicate.CallSites, pc);
                int clauses = 0;
                string cat;
                if (fid < 0) cat = "var-or-control";
                else if (calleeMap is null || !calleeMap.TryGetValue(fid, out var callee))
                    cat = "ext-or-builtin";
                else
                {
                    clauses = callee.ClauseCount;
                    // "leaf" = body makes no non-tail call to another predicate
                    // (only builtins / arith / unify) — the easiest rule to inline.
                    bool leaf = callee.CallSites is null || callee.CallSites.Count == 0;
                    string lt = leaf ? "leaf" : "nonleaf";
                    if (clauses == 1)
                        cat = IsFactPredicate(callee) ? "1cl-fact" : $"1cl-rule-{lt}";
                    else if (!IsFactPredicate(callee))
                        cat = $"Ncl-rule-{lt}";
                    else if (!TryGetFactClauseRanges(callee, out var ranges) || ranges.Count != clauses)
                        cat = "Nfact-unshaped";
                    else if (TryGetFactFirstArgKeys(callee.Bytecode, ranges, out _, out _))
                        cat = "Nfact-IDX(inlines)";
                    else cat = "Nfact-NOIDX";
                    // Case-2 eligibility (single-clause, cut-free, no meta /
                    // backtrackable builtin) — the opportunity the rule-body
                    // inliner targets. Reported as a suffix tag.
                    if (clauses == 1 && IsInlinableRule(callee)) cat += " [inl2]";
                }
                System.Console.Error.WriteLine($"[cand] {cat} callee={fid} clauses={clauses}");
            }
            pc += op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
        }
    }

    /// <summary>A fact's per-clause head-match byte ranges (the
    /// <see cref="TryDescribeSwitchedChain"/> / try-me-else describers give them
    /// for the multi-clause dispatch shapes a compiled fact takes).</summary>
    private static bool TryGetFactClauseRanges(
        CompiledPredicate fact, out IReadOnlyList<(int Start, int End)> ranges)
    {
        if (TryDescribeSwitchedChain(fact, calleeMap: null, out var sc) && sc is not null)
        { ranges = sc.Clauses; return true; }
        if (TryDescribeTryMeElseChain(fact, calleeMap: null, out var tc) && tc is not null)
        { ranges = tc.Clauses; return true; }
        ranges = System.Array.Empty<(int, int)>();
        return false;
    }

    /// <summary>Emits an inlined multi-clause fact's clause chain at a non-tail
    /// call site (chunk 359). For each clause c (0..K-1): if not the last, push
    /// an IL CP `(this delegate, BaseCursor+c, fact arity)` — its continuation
    /// is the caller's delegate re-entered at the alternative cursor, which the
    /// caller's cursor switch routes to <c>AltLabels[c]</c>; then emit clause c's
    /// head match against the call args (already in the argument registers) with
    /// the proceed suppressed; on match branch to the shared continuation (the
    /// last clause falls through). A head-match failure branches to
    /// <paramref name="failLabel"/> → returns false → the engine backtracks,
    /// popping the CP and restoring the saved argument registers, and re-enters
    /// at the next clause's alternative cursor.</summary>
    private static void EmitInlinedFact(
        Sigil.Emit<PredicateDelegate> emit, InlineSite site, Sigil.Label failLabel,
        SelfDelegateEmitter emitSelf, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        int factArity = site.Fact.Arity;
        byte[] fcode = site.Fact.Bytecode;
        int k = site.ClauseRanges.Count;

        // Phase 1b (chunk 360): when every clause has a DISTINCT constant first
        // argument (all integer or all atom — crypt's odd/even/lefteven), emit a
        // first-argument index pre-filter so a BOUND arg jumps straight to its
        // single clause (deterministic, no choice point) instead of the linear
        // scan — recovering the first-arg indexing the trampoline had. Only an
        // UNBOUND arg falls to the chain (generate, try-all). A bound value with
        // no matching key, or a bound non-indexed type, fails outright (a pure
        // constant fact has no catch-all clause).
        if (factArity >= 1
            && TryGetFactFirstArgKeys(fcode, site.ClauseRanges, out bool isAtom, out int[] keys))
        {
            // Names must be unique per inline site (a caller can inline several
            // facts); the site's BaseCursor is unique.
            int u = site.BaseCursor;
            var chainLabel = emit.DefineLabel($"inl{u}_chain");
            var detLabels = new Sigil.Label[k];
            for (int c = 0; c < k; c++) detLabels[c] = emit.DefineLabel($"inl{u}_det{c}");
            var cellLoc = emit.DeclareLocal<Cell>($"inl{u}_cell");
            var tagLoc = emit.DeclareLocal<int>($"inl{u}_tag");

            // cell = deref(X0) (one level)
            emit.LoadArgument(0);
            emit.LoadConstant(0);
            emit.Call(EngineGetRegisterMethod);
            emit.StoreLocal(cellLoc);
            emit.LoadLocalAddress(cellLoc);
            emit.Call(CellTagIdGetter);
            emit.LoadConstant((int)Tag.Ref);
            var notRef = emit.DefineLabel($"inl{u}_notref");
            emit.UnsignedBranchIfNotEqual(notRef);
            emit.LoadArgument(0);
            emit.LoadArgument(0);
            emit.LoadLocalAddress(cellLoc);
            emit.Call(CellAsHeapIndexGetter);
            emit.Call(EngineDerefMethod);
            emit.Call(EngineGetHeapMethod);
            emit.StoreLocal(cellLoc);
            emit.MarkLabel(notRef);

            emit.LoadLocalAddress(cellLoc);
            emit.Call(CellTagIdGetter);
            emit.StoreLocal(tagLoc);
            var notWant = emit.DefineLabel($"inl{u}_notwant");
            emit.LoadLocal(tagLoc);
            emit.LoadConstant((int)(isAtom ? Tag.Atom : Tag.Int));
            emit.UnsignedBranchIfNotEqual(notWant);
            // Bound indexed type → switch on the value to the single clause.
            if (isAtom)
            {
                var keyLoc = emit.DeclareLocal<int>($"inl{u}_key");
                emit.LoadLocalAddress(cellLoc);
                emit.Call(CellAsAtomIdGetter);
                emit.StoreLocal(keyLoc);
                for (int c = 0; c < k; c++)
                {
                    emit.LoadLocal(keyLoc);
                    emit.LoadConstant(keys[c]);
                    emit.BranchIfEqual(detLabels[c]);
                }
            }
            else
            {
                var vLoc = emit.DeclareLocal<long>($"inl{u}_v");
                emit.LoadLocalAddress(cellLoc);
                emit.Call(CellAsIntGetter);
                emit.StoreLocal(vLoc);
                for (int c = 0; c < k; c++)
                {
                    emit.LoadLocal(vLoc);
                    emit.LoadConstant((long)keys[c]);
                    emit.BranchIfEqual(detLabels[c]);
                }
            }
            emit.Branch(failLabel);             // bound, no matching key → fail
            emit.MarkLabel(notWant);
            emit.LoadLocal(tagLoc);
            emit.LoadConstant((int)Tag.Ref);
            emit.BranchIfEqual(chainLabel);     // unbound → generate via the chain
            emit.Branch(failLabel);             // bound non-indexed type → fail

            // Deterministic single-clause entries (no CP): the head match
            // re-checks the (already-matched) indexed arg and unifies the rest;
            // a failure on a non-indexed arg falls through to the caller's fail
            // since the unique key leaves no other clause to try.
            for (int c = 0; c < k; c++)
            {
                emit.MarkLabel(detLabels[c]);
                EmitClauseBody(emit, fcode, site.ClauseRanges[c].Start, site.ClauseRanges[c].End,
                    failLabel, Array.Empty<CallSite>(),
                    calleeMap: calleeMap, suppressProceedReturn: true);
                emit.Branch(site.Continuation);
            }
            emit.MarkLabel(chainLabel);
        }

        // The linear clause chain — the generate (unbound-arg) path for an
        // index-eligible fact, or every call for a non-index-eligible one.
        for (int c = 0; c < k; c++)
        {
            if (c > 0) emit.MarkLabel(site.AltLabels[c - 1]);   // backtrack re-entry for clause c+1
            if (c < k - 1)
            {
                emit.LoadArgument(0);                      // engine
                emitSelf(emit);                            // → this PredicateDelegate
                emit.LoadConstant(site.BaseCursor + c);    // alternative cursor (next clause)
                emit.LoadConstant(factArity);              // save the fact's argument registers
                emit.Call(EnginePushIlCpMethod);
            }
            EmitClauseBody(emit, fcode, site.ClauseRanges[c].Start, site.ClauseRanges[c].End,
                failLabel, Array.Empty<CallSite>(),
                calleeMap: calleeMap, suppressProceedReturn: true);
            if (c < k - 1) emit.Branch(site.Continuation);
        }
        emit.MarkLabel(site.Continuation);
    }

    /// <summary>For an all-constant-first-arg fact (every clause's first head
    /// match against arg 0 is a distinct constant of one kind), returns the kind
    /// (<paramref name="isAtom"/>) and the per-clause key. Enables the chunk-360
    /// index pre-filter; returns false (→ plain linear chain) for any clause
    /// whose first arg is a variable, a compound, a mixed kind, or a duplicate
    /// of another clause's.</summary>
    private static bool TryGetFactFirstArgKeys(
        byte[] code, IReadOnlyList<(int Start, int End)> ranges, out bool isAtom, out int[] keys)
    {
        isAtom = false;
        keys = new int[ranges.Count];
        if (ranges.Count == 0) return false;
        bool? atomKind = null;
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int c = 0; c < ranges.Count; c++)
        {
            if (!TryReadFirstArgConst(code, ranges[c].Start, ranges[c].End, out bool a, out int key))
                return false;
            if (atomKind is null) atomKind = a;
            else if (atomKind.Value != a) return false;   // mixed integer / atom
            if (!seen.Add(key)) return false;             // duplicate key
            keys[c] = key;
        }
        isAtom = atomKind!.Value;
        return true;
    }

    private static bool TryReadFirstArgConst(byte[] code, int start, int end, out bool isAtom, out int key)
    {
        isAtom = false;
        key = 0;
        int pc = start;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Meta) { pc += 6; continue; }
            if (op == Opcode.GetInteger)
            {
                if (BytecodeIO.ReadInt32(code, pc + 5) != 0) return false;
                key = BytecodeIO.ReadInt32(code, pc + 1); isAtom = false; return true;
            }
            if (op == Opcode.GetAtom)
            {
                if (BytecodeIO.ReadInt32(code, pc + 5) != 0) return false;
                key = BytecodeIO.ReadInt32(code, pc + 1); isAtom = true; return true;
            }
            return false;   // first head-match op isn't a constant on arg 0
        }
        return false;
    }

    /// <summary>Shared meta-CP body emitter — used by both the
    /// DynamicMethod path (above) and the persisted path. The
    /// self-reference for re-pushing the meta-CP on each retry routes
    /// through <paramref name="emitSelf"/>.</summary>
    private static void EmitSingleClauseMetaCpBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        int callSiteCount,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        SelfDelegateEmitter emitSelf)
    {
        var failLabel = emit.DefineLabel("fail");
        var startLabel = emit.DefineLabel("start");
        // Phase 16: a single label per forward-resume cursor. The
        // cursor switch branches here directly; the same label is
        // marked at the post-Call body point. The chunk-66 backtrack-
        // drive bodies are gone — backtracking through the callee's
        // CPs is handled naturally by the engine's CP cascade, with
        // each callee-clause's saved Cp pointing back at our resume
        // marker.
        var resumeLabels = new Sigil.Label[callSiteCount];
        for (int i = 0; i < callSiteCount; i++)
            resumeLabels[i] = emit.DefineLabel($"resume_{i + 1}");

        // Chunk 359: inlined multi-clause facts take cursors after the call-site
        // resume cursors (1..callSiteCount), i.e. from callSiteCount+1.
        var inlineSites = ComputeInlineSites(emit, predicate, calleeMap,
            firstCursor: callSiteCount + 1, out _);
        // Case-2 rule inline (chunk 367): the bodies of these callees are emitted
        // inline; their non-tail calls thread through this caller's resume cursors
        // (already counted into callSiteCount → resumeLabels).
        var ruleInlineSites = ComputeRuleInlineSites(predicate, calleeMap);

        _emitOwnerFid = predicate.FunctorId;
        // Cursor dispatch: 0 → start; N → resume_N; baseCursor+j → inlined
        // fact clause-(j+2) re-entry (the backtrack alternative). Cursors are
        // dense small ints from 0 (ComputeInlineSites allocates contiguous
        // ranges), so this is a single O(1) jump table (IL `switch`) — NOT a
        // linear compare chain. That matters: every backtrack re-enters the
        // delegate HERE, and an inlined fact's generate chain re-enters once per
        // clause alternative; a linear switch would make that O(cursors) and grow
        // with each inline site — making inlining cost MORE than the trampoline it
        // replaces (the trampoline re-enters the callee's own compact dispatch).
        // The jump table keeps re-entry constant, so inlining is strictly cheaper.
        int maxCursor = callSiteCount;
        foreach (var site in inlineSites.Values)
        {
            int last = site.BaseCursor + site.AltLabels.Length - 1;
            if (last > maxCursor) maxCursor = last;
        }
        var cursorLabels = new Sigil.Label[maxCursor + 1];
        for (int i = 0; i <= maxCursor; i++) cursorLabels[i] = startLabel; // 0 + any gap
        for (int i = 0; i < callSiteCount; i++) cursorLabels[i + 1] = resumeLabels[i];
        foreach (var site in inlineSites.Values)
            for (int j = 0; j < site.AltLabels.Length; j++)
                cursorLabels[site.BaseCursor + j] = site.AltLabels[j];
        emit.LoadArgument(1);
        emit.Switch(cursorLabels);
        emit.Branch(startLabel);    // cursor out of range (unreachable) → start

        emit.MarkLabel(startLabel);
        int idxCounter = 0;
        // Self-tail-recursion → in-method loop (chunk 349): startLabel is the
        // cursor-0 entry (the cursor switch above already branched the resume
        // cursors away), so a self Execute branches straight back here.
        EmitClauseBody(emit, predicate.Bytecode, 0, predicate.Bytecode.Length,
            failLabel, predicate.CallSites,
            callSiteIndexCounter: () => ++idxCounter,
            resumeLabels: resumeLabels,
            emitSelfDelegate: emitSelf,
            calleeMap: calleeMap,
            selfFunctorId: predicate.FunctorId, selfTailLabel: startLabel,
            inlineSites: inlineSites, ruleInlineSites: ruleInlineSites);

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>Chunk 218 — builtins that push a CP and call
    /// <c>ResumeAtReturnPc</c> on retry. Their IL <c>call_builtin</c> site
    /// needs a resume marker so the resume re-enters the IL caller (the
    /// builtin reads <c>engine.BuiltinReturnPc</c> at first invocation;
    /// the IL pre-sets it to the marker).</summary>
    private static bool IsBacktrackableBuiltinName(string name) => name switch
    {
        "between" or "append" or "atom_concat" or "string_concat"
        or "nb_current" or "current_op" or "current_char_conversion"
        or "current_stream" or "stream_property" or "repeat" or "retract" => true,
        _ => false,
    };

    /// <summary>Counts non-tail <c>Call</c> opcodes in a clause's
    /// bytecode (Opcode.Call only — Opcode.Execute is the tail-call
    /// form and doesn't need a meta-CP).</summary>
    private static int CountNonTailCallOpcodes(byte[] bytecode)
        => CountNonTailCallOpcodes(bytecode, 0, bytecode.Length);

    private static int CountNonTailCallOpcodes(byte[] bytecode, int start, int end)
    {
        int count = 0;
        int pc = start;
        while (pc < end)
        {
            byte b = bytecode[pc];
            if (b == (byte)Opcode.Call) count++;
            // Phase 19: CallBuiltin call/N and CallBuiltin '$call'/2 are
            // also non-tail Calls — they thread through
            // IlMetaCallHelper.Dispatch and need a resume-cursor slot.
            else if (b == (byte)Opcode.CallBuiltin)
            {
                int builtinId = BytecodeIO.ReadInt32(bytecode, pc + 1);
                string n = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId).Name;
                // call/$call thread through IlMetaCallHelper.Dispatch
                // (chunk 182); backtrackable builtins need a resume marker
                // for their CP's resume (chunk 218).
                if (n == "call" || n == "$call" || IsBacktrackableBuiltinName(n)) count++;
            }
            var info = OpcodeTable.Get(b);
            if (!info.IsDefined || info.Size == 0) break;
            pc += info.Size;
        }
        return count;
    }

    /// <summary>Emits IL for a contiguous span of supported-opcode
    /// clause-body bytes. <paramref name="failLabel"/> is jumped to on any
    /// unification failure; a successful <c>proceed</c> emits an inline
    /// <c>return true</c>. <paramref name="callSites"/> is consulted by
    /// the Execute emission to resolve each call site's callee functor
    /// id (which is stable across queries, unlike the absolute bytecode
    /// address embedded in the operand).
    ///
    /// <para><paramref name="calleeMap"/> turns on chunk-69 inlining of
    /// small leaf callees: when a Call or Execute site references a
    /// predicate that's in the map and passes <see cref="IsLeafPredicate"/>,
    /// the callee's body opcodes are emitted directly into the caller's
    /// IL stream instead of going through the
    /// <see cref="IlRuntimeHelpers.Call"/> / <c>IlExecuteHelper.Resolve</c>
    /// thunk. Saves a managed call, a Pc-set, and the bytecode-interpreter
    /// re-entry per call site.</para>
    /// <para><paramref name="suppressProceedReturn"/> applies inside the
    /// inlined-Call case: the callee's <c>proceed</c> becomes a fall-through
    /// (the caller has more body to execute after the inlined block)
    /// instead of <c>return true</c>.</para></summary>
    /// <summary>Owner fid threaded through the body emit so chunk-173
    /// debug markers can identify which predicate's IL each marker
    /// belongs to. Set by the public Compile/CompileInstrumented
    /// entry points and the persisted-assembly path.
    /// Phase 16 chunk 182: also used by threaded non-tail Call sites
    /// to encode the resume marker (functorId, cursor).</summary>
    private static int _emitOwnerFid;

    /// <summary>Phase 17 — when non-null, the emit pipeline is building
    /// a persisted-bundle .dll. Every functor/atom/resume-marker constant
    /// is replaced with a unique sentinel int (drawn from
    /// <see cref="IlPatchSiteCodec.SentinelBase"/>); the corresponding
    /// <see cref="IlPatchSite"/> is appended here so the post-Save PE
    /// scan can locate the sentinel's byte offset and the LoadBundle
    /// path can overwrite it with the runtime-process id. When null,
    /// the runtime <c>DynamicMethod</c> path emits real ids directly
    /// — atom/functor ids are stable in-process so no remap is
    /// needed.</summary>
    [System.ThreadStaticAttribute] private static List<IlPatchSite>? _persistPatches;
    [System.ThreadStaticAttribute] private static int _persistNextSentinel;

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
            emit.LoadConstant(Shumway.Core.Engine.EncodeResumeMarker(functorId, cursor));
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
        IReadOnlyDictionary<int, CompiledPredicate>? ruleInlineSites = null)
    {
        int pc = start;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Meta)
            {
                // Dbg-info Meta opcode (chunk 55) — runtime no-op. Skip
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
                var refLocal = emit.DeclareLocal<Cell>($"freshRef_pc{pc}");
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
                var refLocal = emit.DeclareLocal<Cell>($"freshRefY_pc{pc}");
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
                    var preELocal = emit.DeclareLocal<int>($"preE_alloc_pc{pc}");
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
                    var preELocal = emit.DeclareLocal<int>($"preE_dealloc_pc{pc}");
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
            // Chunk 220 — fused opcodes. Emit the equivalent pair of
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
                // Padding inside a fused opcode (chunk 220); the outer
                // fused-opcode case has already advanced PC past it, so
                // a standalone Nop in the walker is just a 1-byte skip.
                pc += 1;
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
            if (op == Opcode.CallBuiltin)
            {
                int builtinId = BytecodeIO.ReadInt32(code, pc + 1);
                string builtinName =
                    Shumway.Builtins.BuiltinsRegistry.GetById(builtinId).Name;
                int builtinArity =
                    Shumway.Builtins.BuiltinsRegistry.GetById(builtinId).Arity;

                if (builtinName == "call" || builtinName == "$call")
                {
                    // Phase 19 — meta-call dispatch. Three outcomes from
                    // IlMetaCallHelper.Dispatch:
                    //   target >= 0      → user predicate / control
                    //                      helper. Thread the dispatch
                    //                      exactly like a chunk-182 non-
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
                    if (callSiteIndexCounter is null || resumeLabels is null)
                        throw new InvalidOperationException(
                            "IL meta-call requires callSiteIndexCounter + "
                            + "resumeLabels for forward-resume cursor allocation.");
                    int siteIdx = callSiteIndexCounter();
                    int resumeCursor = cursorBase + siteIdx - 1;

                    var target = emit.DeclareLocal<int>($"metaCallTarget_pc{pc}");

                    // Compute the call arity and cut barrier per builtin.
                    //   call/N : arity = N, barrier = engine.B
                    //   $call/2: arity = 1, barrier = X[1].AsInt
                    emit.LoadArgument(0);                    // engine
                    if (builtinName == "$call")
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
                    emit.Call(IlMetaCallHelperDispatchMethod);
                    emit.StoreLocal(target);

                    // target == -1 → fail
                    emit.LoadLocal(target);
                    emit.LoadConstant(IlMetaCallHelper.SyncFail);
                    emit.BranchIfEqual(failLabel);

                    // target == -2 → fall through (sync success)
                    var threadedLabel = emit.DefineLabel($"metaCallThread_pc{pc}");
                    emit.LoadLocal(target);
                    emit.LoadConstant(IlMetaCallHelper.SyncSuccess);
                    emit.UnsignedBranchIfNotEqual(threadedLabel);
                    // sync success — skip the threading and go to resume.
                    emit.Branch(resumeLabels[siteIdx - 1]);

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
                        EmitResumeMarker(emit, _emitOwnerFid, resumeCursor);
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

                    emit.MarkLabel(resumeLabels[siteIdx - 1]);
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                // Regular builtin (non-meta) — invoke entry.Impl directly.
                // entry = BuiltinsRegistry.GetById(id)
                // if (!entry.Impl(engine)) goto fail
                //
                // Chunk 218: backtrackable builtins (between/3, append/3,
                // repeat/0, retract/1, …) push a CP whose resume calls
                // ResumeAtReturnPc(returnPc) — the returnPc is captured at
                // first invocation as engine.BuiltinReturnPc. The IL emit
                // here allocates a resume cursor and pre-sets
                // BuiltinReturnPc to that marker, so on resume the
                // dispatcher decodes the marker and re-enters this IL at
                // the post-builtin label. Non-backtrackable builtins skip
                // the cursor allocation — straight invocation.
                bool isBacktrackable = IsBacktrackableBuiltinName(builtinName);
                int builtinResumeIdx = -1;
                if (isBacktrackable)
                {
                    if (callSiteIndexCounter is null || resumeLabels is null)
                        throw new InvalidOperationException(
                            "Backtrackable builtin in IL requires callSiteIndexCounter + resumeLabels.");
                    builtinResumeIdx = callSiteIndexCounter();
                    int resumeCursor = cursorBase + builtinResumeIdx - 1;
                    // engine.BuiltinReturnPc = EncodeResumeMarker(ownerFid, resumeCursor);
                    emit.LoadArgument(0);
                    EmitResumeMarker(emit, _emitOwnerFid, resumeCursor);
                    emit.Call(EngineBuiltinReturnPcSetter);
                }
                emit.LoadConstant(builtinId);
                emit.Call(BuiltinsRegistryGetByIdMethod);
                emit.Call(BuiltinEntryImplGetter);
                emit.LoadArgument(0);
                emit.Call(BuiltinImplInvokeMethod);
                emit.BranchIfFalse(failLabel);
                if (isBacktrackable)
                {
                    // After a successful first invocation, fall through.
                    // On a CP-resume, the dispatcher decodes the marker and
                    // re-invokes this IL with cursor=resumeCursor; the top
                    // dispatch routes it to this label, which continues the
                    // body at the post-builtin position.
                    // resumeLabels was null-checked above where
                    // builtinResumeIdx was assigned; the compiler
                    // doesn't track the invariant across the
                    // `isBacktrackable` branches.
                    emit.MarkLabel(resumeLabels![builtinResumeIdx - 1]);
                }
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetStructure)
            {
                int functorId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                EmitFunctorId(emit, functorId);
                emit.LoadConstant(arg);
                emit.Call(EngineGetStructureMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
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
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.Call(EngineGetListMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
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
                // Non-tail Call. With chunk 66 the IL site captures
                // engine.B (preCallB) before invoking the sub-call
                // helper, then on success pushes a meta-CP that saves
                // preCallB as Cell.Int(preCallB) in arity-1 of the
                // CP frame. On backtrack the resume path reads
                // preCallB back, drives Engine.BacktrackRunner to
                // fetch the callee's next solution, and re-enters
                // the body at the post-call label.
                int siteFunctorId = -1;
                for (int i = 0; i < callSites.Count; i++)
                {
                    if (callSites[i].OpcodeOffset == pc)
                    {
                        siteFunctorId = callSites[i].CalleeFunctorId;
                        break;
                    }
                }
                if (siteFunctorId < 0)
                    throw new InvalidOperationException(
                        $"Call opcode at pc={pc} has no matching call site in the predicate's metadata.");

                // Chunk 359 — multi-clause fact inline (gated SHUMWAY_INLINE_FACTS).
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
                        // is well-formed, exactly as the chunk-69 leaf path does.
                        int inlSiteIdx = callSiteIndexCounter();
                        emit.MarkLabel(resumeLabels[inlSiteIdx - 1]);
                    }
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                // Phase 29 case 2 (chunk 367): inline a single-clause rule that
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
                    EmitClauseBody(emit, ruleCallee.Bytecode, 0, ruleCallee.Bytecode.Length,
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

                // Inlining (chunk 69): if the callee is a small static
                // leaf, emit its body opcodes inline instead of routing
                // through IlCallHelper.Run. Leaves push no CPs so no
                // meta-CP is needed; the post-call label still gets
                // marked for any outer logic but no choice point lives
                // there.
                if (calleeMap is not null
                    && calleeMap.TryGetValue(siteFunctorId, out var calleePred)
                    && (IsLeafPredicate(calleePred)
                        || (InlineLeafRules && IsInlinableLeafRule(calleePred))))
                {
                    EmitClauseBody(emit, calleePred.Bytecode, 0, calleePred.Bytecode.Length,
                        failLabel, Array.Empty<CallSite>(),
                        calleeMap: calleeMap, suppressProceedReturn: true);
                    if (callSiteIndexCounter is not null && resumeLabels is not null)
                    {
                        int leafSiteIdx = callSiteIndexCounter();
                        // Leaves leave no CPs behind, so under Phase 16
                        // threading they don't set a resume marker; the
                        // cursor=leafSiteIdx entry is never invoked.
                        // Mark the resume label anyway so the cursor
                        // switch's branch has a target (dead code but
                        // keeps the IL well-formed).
                        emit.MarkLabel(resumeLabels[leafSiteIdx - 1]);
                    }
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                // Phase 16 chunk 182 — threaded non-tail Call. Instead
                // of recursing into RunSubroutine via IlCallHelper.Run,
                // we tail-call to the callee (same machinery `Execute`
                // uses) and set Cp to a resume marker that the bytecode
                // interpreter will recognise when the callee Proceeds.
                // The marker encodes (this delegate's functor id,
                // siteIdx), so the dispatcher knows to re-invoke us at
                // the forward-resume cursor. No recursive C# stack
                // frame; backtracking through the callee's CPs
                // naturally lands at the caller's marker again. The
                // chunk-66 meta-CP push is gone — backtracking
                // semantics fall out of the natural CP cascade.
                if (callSiteIndexCounter is null || resumeLabels is null)
                    throw new InvalidOperationException(
                        "Threaded non-tail Call requires callSiteIndexCounter + "
                        + "resumeLabels for forward-resume cursor allocation.");

                int siteIdx = callSiteIndexCounter();
                // Phase 16: the cursor encoded in the resume marker is
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

                // engine.SetPc(Engine.EncodeResumeMarker(siteFunctorId, 0));
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
                int siteFunctorId = -1;
                for (int i = 0; i < callSites.Count; i++)
                {
                    if (callSites[i].OpcodeOffset == pc)
                    {
                        siteFunctorId = callSites[i].CalleeFunctorId;
                        break;
                    }
                }
                if (siteFunctorId < 0)
                    throw new InvalidOperationException(
                        $"Execute opcode at pc={pc} has no matching call site in the predicate's metadata.");

                // Phase 29 chunk 368 — un-tail. When this body is being INLINED at
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

                // Inlining (chunk 69): if the callee is a small static
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
                    EmitClauseBody(emit, calleePredX.Bytecode, 0, calleePredX.Bytecode.Length,
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
                    // engine.SetB0(engine.B); engine.MaybeCollectHeap();
                    emit.LoadArgument(0);
                    emit.LoadArgument(0);
                    emit.Call(EngineBGetter);
                    emit.Call(EngineSetB0Method);
                    emit.LoadArgument(0);
                    emit.Call(EngineMaybeCollectHeapMethod);
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
        /// <c>get_atom + proceed</c> shape (chunk 52). Trivial bodies
        /// don't need an actual body emit — the switch_on_atom
        /// dispatch already matched the atom, so on a ground-key hit
        /// we just return true. Non-trivial bodies (chunk 190) emit
        /// the body via <see cref="EmitClauseBody"/>.</summary>
        public required bool AllTrivial { get; init; }
    }

    /// <summary>Per-clause layout extracted from a try_me_else chain
    /// (chunk 52): the [start, end) byte offsets of each clause's body
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
        info = null;
        byte[] code = predicate.Bytecode;
        if (code.Length == 0) return false;
        // First instruction must be try_me_else (size 9: opcode + bp +
        // arity). After that we expect alternating "clause body"
        // chunks separated by retry_me_else (size 5) and terminated by
        // trust_me (size 1) preceding the last clause.
        if ((Opcode)code[0] != Opcode.TryMeElse) return false;
        var clauseStarts = new List<int>();
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.TryMeElse || op == Opcode.RetryMeElse)
            {
                pc += OpcodeTable.Get(op).Size;
                clauseStarts.Add(pc);
                continue;
            }
            if (op == Opcode.TrustMe)
            {
                pc += 1;
                clauseStarts.Add(pc);
                continue;
            }
            // Skip clause-body opcodes until the next dispatch op or
            // end of bytecode. Body opcodes must all be in the IL
            // subset (the per-clause emission walks them again to emit
            // IL; we just need to size-walk here).
            if (!IsClauseBodyOpcode(op, predicate, pc, calleeMap)) return false;
            pc += OpcodeTable.Get(op).Size;
        }

        // Derive (Start, End) for each clause body.
        if (clauseStarts.Count != predicate.ClauseCount) return false;
        var ranges = new List<(int, int)>(clauseStarts.Count);
        for (int i = 0; i < clauseStarts.Count; i++)
        {
            int start = clauseStarts[i];
            int end = i + 1 < clauseStarts.Count
                ? FindDispatchOpBefore(code, clauseStarts[i + 1])
                : code.Length;
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
            // Phase 16 threading makes non-leaf callees work the same
            // way they do for the single-clause-meta-CP path — the IL
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
            // Phase 19: call/N and '$call'/2 are IL-eligible via
            // IlMetaCallHelper.Dispatch — no longer rejected.
            return true;
        }
        if (IsAEvalOpcode(op))   // ADR-018 — gate operand kind (bigint/float lit)
            return IsSupportedAEval(predicate.Bytecode, pc);
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

    /// <summary>Chunk 189: recognises the chunk-67 first/multi-arg
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
        info = null;
        byte[] code = predicate.Bytecode;
        if (code.Length == 0) return false;
        if ((Opcode)code[0] != Opcode.SwitchOnTerm) return false;

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
        // The bodies open with a Meta dbg-info marker (chunk 55) which
        // EmitClauseBody handles as a no-op.
        foreach (var (s, e) in ranges)
        {
            int q = s;
            while (q < e)
            {
                var op = (Opcode)code[q];
                var opInfo = OpcodeTable.Get((byte)op);
                if (!opInfo.IsDefined || opInfo.Size == 0) return false;
                if (!IsClauseBodyOpcode(op, predicate, q, calleeMap)) return false;
                q += opInfo.Size;
            }
        }

        info = new TryMeElseChainInfo { Clauses = ranges };
        return true;
    }

    /// <summary>Chunk 189: emits IL for a switched-chain predicate by
    /// reusing the chunk-188 <see cref="CompileTryMeElseChain"/>
    /// path. The two recognisers produce the same
    /// <see cref="TryMeElseChainInfo"/> shape (per-clause body
    /// ranges); the emit doesn't need to know which dispatch path
    /// the WAM emitted — it always walks clauses linearly with IL
    /// CPs at boundaries.</summary>
    private PredicateDelegate CompileSwitchedChain(
        CompiledPredicate predicate, TryMeElseChainInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
        => CompileTryMeElseChain(predicate, info, calleeMap);

    /// <summary>Chunk 216 — emits IL for a fully indexed predicate,
    /// reproducing the WAM switch dispatch (O(1) key lookup) and bucket
    /// backtracking via the <see cref="IlIndexedDispatchInfo"/> chain-node
    /// model, rather than the chunk-189 linear walk. Clause bodies are
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
            EmitIndexedDispatchBody(emit, predicate, info, calleeMap, emitSelf);
            var del = emit.CreateDelegate(Optimizations);
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
        SelfDelegateEmitter emitSelf)
    {
        int K = info.Nodes.Count;
        int N = info.Clauses.Count;
        int totalCallSites = CountNonTailCallOpcodes(predicate.Bytecode);
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

        // ---- Top: dispatch on the incoming cursor (arg 1). ----
        // Call-site resumes first (a backtrack into a body's post-Call point).
        for (int j = 0; j < totalCallSites; j++)
        {
            emit.LoadArgument(1);
            emit.LoadConstant(callBase + j);
            emit.BranchIfEqual(resumeLabels[j]);
        }
        // Chain-node resumes (a backtrack into the next bucket node).
        for (int n = 0; n < K; n++)
        {
            emit.LoadArgument(1);
            emit.LoadConstant(n + 1);
            emit.BranchIfEqual(nodeLabels[n]);
        }
        // Self-tail-recursion target: a self Execute in a clause body branches
        // here (its args already in the argument registers) instead of the
        // marker / dispatch-loop round trip — an in-method loop. Marked before
        // the cursor-0 resolve so the loop re-runs the index decision on the
        // new arguments.
        var selfEntry = emit.DefineLabel("idx_self_entry");
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
            // (chunk 197) so a persisted-bundle .dll gets it patched at
            // LoadBundle; for runtime promotion it's a direct ldc.i4.
            var entry = emit.DeclareLocal<int>("idx_entry");
            emit.LoadArgument(0);
            EmitFunctorId(emit, predicate.FunctorId);
            emit.Call(IlIndexedDispatchResolveByFidMethod);
            emit.StoreLocal(entry);
            for (int n = 0; n < K; n++)
            {
                emit.LoadLocal(entry);
                emit.LoadConstant(n);
                emit.BranchIfEqual(nodeLabels[n]);
            }
            emit.Branch(failLabel);   // unreachable: resolver always returns a valid node
        }

        // ---- Chain nodes: push the next-node CP (if any), run the clause body. ----
        for (int n = 0; n < K; n++)
        {
            emit.MarkLabel(nodeLabels[n]);
            int next = info.Nodes[n].NextCursor;
            if (next >= 0)
            {
                emit.LoadArgument(0);            // engine
                emitSelf(emit);                  // → PredicateDelegate
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
            EmitClauseBody(emit, predicate.Bytecode, info.Clauses[i].Start, info.Clauses[i].End,
                failLabel, predicate.CallSites,
                callSiteIndexCounter: () => ++siteCounter,
                resumeLabels: resumeLabels,
                emitSelfDelegate: emitSelf,
                calleeMap: calleeMap,
                cursorBase: callBase,
                selfFunctorId: predicate.FunctorId,
                selfTailLabel: selfEntry);
        }

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
        Sigil.Label[] nodeLabels)
    {
        IndexGraph? graph = IlIndexGraph.Build(info);
        if (graph is null) return false;
        IndexNode[] gnodes = graph.Nodes;
        var gLabels = new Sigil.Label[gnodes.Length];
        for (int i = 0; i < gnodes.Length; i++)
            gLabels[i] = emit.DefineLabel($"idx_g{i}");

        var cellLoc = emit.DeclareLocal<Cell>("idx_cell");
        var tmpCell = emit.DeclareLocal<Cell>("idx_tmpcell");
        var tagLoc = emit.DeclareLocal<int>("idx_tag");
        var keyLoc = emit.DeclareLocal<int>("idx_key");
        var longLoc = emit.DeclareLocal<long>("idx_long");

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
            var notRef = emit.DefineLabel($"idx_g{i}_notref");
            emit.UnsignedBranchIfNotEqual(notRef);
            emit.LoadArgument(0);                       // engine (receiver of GetHeap)
            emit.LoadArgument(0);                       // engine (receiver of Deref)
            emit.LoadLocalAddress(cellLoc);
            emit.Call(CellAsHeapIndexGetter);
            emit.Call(EngineDerefMethod);
            emit.Call(EngineGetHeapMethod);
            emit.StoreLocal(cellLoc);
            emit.MarkLabel(notRef);

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
        EmitTryMeElseChainBody(emit, predicate, info, calleeMap, emitSelf);

        var del = emit.CreateDelegate(Optimizations);
        IndexedDelegateHolder.Register(holderKey, del);
        _nextHolderKey = holderKey + 1;
        return del;
    }

    /// <summary>Shared try-me-else-chain emit body used by both the
    /// DynamicMethod runtime path (above) and the chunk-71 persisted
    /// assembly path (<see cref="EmitPersistedTryMeElseChain"/>). All
    /// self-references for the per-clause IL CP push route through
    /// <paramref name="emitSelf"/>; callers pick the holder-based or
    /// field-based variant.</summary>
    private static void EmitTryMeElseChainBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        TryMeElseChainInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        SelfDelegateEmitter emitSelf)
    {
        var clauses = info.Clauses;
        var failLabel = emit.DefineLabel("fail");

        // Phase 16 chunk 188: multi-clause TryMeElseChain threads
        // non-leaf Call sites just like the chunk-182 single-clause
        // path. The cursor space is partitioned:
        //   cursor 0..N-1   → clause entries
        //   cursor N..N+M-1 → forward-resume points for the M
        //                     non-tail Call sites across all clauses
        // EmitClauseBody receives cursorBase=N so each Call site's
        // resume marker encodes a unique global cursor and the
        // matching label is in resumeLabels[siteIdx-1].
        int N = clauses.Count;
        int totalCallSites = CountNonTailCallOpcodes(predicate.Bytecode);
        var resumeLabels = new Sigil.Label[totalCallSites];
        for (int j = 0; j < totalCallSites; j++)
            resumeLabels[j] = emit.DefineLabel($"call_resume_{j + 1}");

        _emitOwnerFid = predicate.FunctorId;

        // Top-level Call-site cursor dispatch — runs before the
        // clause-entry chain so an incoming cursor=N+j short-circuits
        // straight to its resume point inside whichever clause the
        // Call site lives in.
        for (int j = 0; j < totalCallSites; j++)
        {
            emit.LoadArgument(1);
            emit.LoadConstant(N + j);
            emit.BranchIfEqual(resumeLabels[j]);
        }

        // Self-tail-recursion → in-method loop (chunk 350): a self Execute in
        // any clause body resets the cursor to 0 and branches here, so the
        // clause-entry chain re-dispatches from clause 0 (a fresh self-call must
        // try the first clause, not re-enter the clause it was called from).
        var selfEntry = emit.DefineLabel("chain_self_entry");
        emit.MarkLabel(selfEntry);

        int siteCounter = 0;
        for (int i = 0; i < clauses.Count; i++)
        {
            var nextLabel = emit.DefineLabel($"after_clause_{i}");
            emit.LoadArgument(1);
            emit.LoadConstant(i);
            emit.UnsignedBranchIfNotEqual(nextLabel);

            // If there's a later clause, push an IL CP for it before
            // running this clause's body.
            if (i < clauses.Count - 1)
            {
                emit.LoadArgument(0);                      // engine
                emitSelf(emit);                            // → PredicateDelegate
                emit.LoadConstant(i + 1);                  // next cursor
                emit.LoadConstant(predicate.Arity);
                emit.Call(EnginePushIlCpMethod);
            }

            // Emit the clause body. The shared siteCounter assigns a
            // unique 1-based ordinal per non-tail Call site; the
            // resume cursor in the emitted IL is cursorBase + ordinal
            // - 1 = N + (ordinal - 1).
            EmitClauseBody(emit, predicate.Bytecode, clauses[i].Start, clauses[i].End,
                failLabel, predicate.CallSites,
                callSiteIndexCounter: () => ++siteCounter,
                resumeLabels: resumeLabels,
                emitSelfDelegate: emitSelf,
                calleeMap: calleeMap,
                cursorBase: N,
                selfFunctorId: predicate.FunctorId,
                selfTailLabel: selfEntry,
                resetCursorBeforeSelfTail: true);

            emit.MarkLabel(nextLabel);
        }

        // cursor out of [0..N-1] (and not a Call-site resume above) → fail.
        emit.Branch(failLabel);
        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>Recognises the shape:
    /// <code>
    ///   switch_on_term VarLbl ConstLbl ListLbl StructLbl   (17 bytes)
    ///   [VarLbl: try / retry / trust chain over all clauses]
    ///   [ConstLbl: switch_on_atom tableId                  (5 bytes)]
    ///   [clause bodies: each `get_atom &lt;id&gt; A0 ; proceed`]
    /// </code>
    /// where the switch_on_atom table maps each clause's first-arg atom
    /// to its body offset, and every clause body is the trivial
    /// <c>get_atom &lt;id&gt; A0; proceed</c> form.</summary>
    private static bool TryDescribeIndexedAtomPredicate(
        CompiledPredicate predicate, out IndexedAtomInfo? info)
        => TryDescribeIndexedAtomPredicate(predicate, calleeMap: null, out info);

    private static bool TryDescribeIndexedAtomPredicate(
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out IndexedAtomInfo? info)
    {
        info = null;
        if (predicate.Arity != 1) return false;
        byte[] code = predicate.Bytecode;
        if (code.Length < 17) return false;
        if ((Opcode)code[0] != Opcode.SwitchOnTerm) return false;

        // VarLbl, ConstLbl, ListLbl, StructLbl operand offsets.
        int varLbl = BytecodeIO.ReadInt32(code, 1);
        int constLbl = BytecodeIO.ReadInt32(code, 5);
        // The shape we recognise has list and struct paths both pointing
        // at the var label (i.e. nothing concrete to dispatch). Allow them
        // to point anywhere — we only emit IL for atom dispatch — but
        // demand const points at a switch_on_atom.
        if (constLbl < 0 || constLbl >= code.Length) return false;
        if ((Opcode)code[constLbl] != Opcode.SwitchOnAtom) return false;

        int tableId = BytecodeIO.ReadInt32(code, constLbl + 1);
        if (tableId < 0 || tableId >= predicate.SwitchTables.Count) return false;

        // Verify the var-dispatch path is the standard try/retry/trust
        // chain — we don't need to walk it for IL emission (we'll handle
        // var-dispatch via IL CPs ourselves) but it's a sanity check that
        // we're looking at the shape we expect.
        if (varLbl < 0 || varLbl >= code.Length) return false;
        if ((Opcode)code[varLbl] != Opcode.Try) return false;

        var table = predicate.SwitchTables[tableId];
        // Phase 18: the table only carries atom-headed clauses. A
        // predicate with mixed list-pattern + atom-headed clauses
        // (e.g. main/1 = `main([F|_]) :- ... ; main([]) :- ...`) ends
        // up with the list-pattern clause UN-INDEXED — it's reachable
        // only through the var-dispatch try/retry/trust chain, not
        // through switch_on_atom. The IndexedAtom emit only emits the
        // atom-direct dispatch, so a query with a non-empty list
        // would fall through to fail. Reject this shape so the
        // SwitchedChain recogniser takes over — it walks the
        // var-dispatch chain which covers every clause.
        if (table.Count != predicate.ClauseCount) return false;
        // The switch table is sorted by atom id (the WAM compiler uses a
        // SortedDictionary) but the var-dispatch path must enumerate
        // clauses in *source* order — that's what every other Prolog
        // engine does. We recover source order by sorting on the body
        // offset, since the per-predicate bytecode lays clauses out in
        // source order.
        var raw = new List<(int AtomId, int BodyOffset)>(table.Count);
        for (int i = 0; i < table.Count; i++)
        {
            int bodyOffset = table.Values[i];
            // Skip a leading Meta(DbgInfo) opcode (chunk 55) — the WAM
            // emitter places one at the start of each clause body for
            // stack-trace mapping; from the IL detector's perspective it's
            // pure metadata that lives before the actual head-matching ops.
            if (bodyOffset >= 0 && bodyOffset + 6 <= code.Length
                && (Opcode)code[bodyOffset] == Opcode.Meta)
                bodyOffset += 6;
            if (bodyOffset < 0 || bodyOffset >= code.Length) return false;
            if ((Opcode)code[bodyOffset] != Opcode.GetAtom) return false;
            int reg = BytecodeIO.ReadInt32(code, bodyOffset + 5);
            if (reg != 0) return false;
            int atomId = BytecodeIO.ReadInt32(code, bodyOffset + 1);
            raw.Add((atomId, bodyOffset));
        }
        if (raw.Count == 0) return false;
        // Sort by body offset → source order. Body i runs from its own
        // offset to the next clause's offset (or to end of bytecode for
        // the last one).
        raw.Sort((a, b) => a.BodyOffset.CompareTo(b.BodyOffset));
        var clauses = new List<IndexedAtomClause>(raw.Count);
        bool allTrivial = true;
        for (int i = 0; i < raw.Count; i++)
        {
            int start = raw[i].BodyOffset;
            int end = i + 1 < raw.Count ? raw[i + 1].BodyOffset : code.Length;
            // Trivial-body shape (chunk 52): get_atom (9 bytes) + proceed
            // (1 byte). Anything else qualifies as "non-trivial" and
            // chunk-190 emits the body via EmitClauseBody.
            bool trivial =
                end == start + 10
                && (Opcode)code[start + 9] == Opcode.Proceed;
            if (!trivial)
            {
                // Validate the full body is in the IL subset (same check
                // TryMeElseChain uses). If not, give up — fall back to
                // SwitchedChain (or another shape).
                int q = start;
                while (q < end)
                {
                    var op = (Opcode)code[q];
                    var opInfo = OpcodeTable.Get((byte)op);
                    if (!opInfo.IsDefined || opInfo.Size == 0) return false;
                    if (!IsClauseBodyOpcode(op, predicate, q, calleeMap)) return false;
                    q += opInfo.Size;
                }
                allTrivial = false;
            }
            clauses.Add(new IndexedAtomClause(raw[i].AtomId, start, end, trivial));
        }
        info = new IndexedAtomInfo { Clauses = clauses, AllTrivial = allTrivial };
        return true;
    }

    /// <summary>Emits the IL for an indexed-atom multi-clause predicate.
    /// The emitted delegate handles both the ground-A1 fast path (direct
    /// atom-id dispatch) and the unbound-A1 path (enumerate via the IL
    /// choice-point machinery from ADR-014).</summary>
    private PredicateDelegate CompileIndexedAtomPredicate(
        CompiledPredicate predicate, IndexedAtomInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        // Take the holder lock for the entire emit-and-register sequence so
        // two concurrent Compile calls don't both observe the same
        // _nextHolderKey, embed it into their IL, and overwrite each other
        // in the holder. The lock is short-lived (one emit call) and only
        // contended when two engines promote at the same wall-clock moment.
        lock (IndexedDelegateHolder.RegistrationLock)
        {
            return CompileIndexedAtomPredicateUnlocked(predicate, info, calleeMap: calleeMap);
        }
    }

    private PredicateDelegate CompileIndexedAtomPredicateUnlocked(
        CompiledPredicate predicate, IndexedAtomInfo info,
        int profileKey = -1, int[]? groundOrder = null,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        int holderKey = _nextHolderKey;
        var emitSelf = SelfFromHolder(holderKey);

        var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
            $"ShumwayIl_indexed_{predicate.FunctorId}",
            doVerify: DoVerify || DebugMode);
        EmitIndexedAtomBody(emit, predicate, info, emitSelf, profileKey, groundOrder, calleeMap);

        var del = emit.CreateDelegate(Optimizations);
        IndexedDelegateHolder.Register(holderKey, del);
        _nextHolderKey = holderKey + 1;
        return del;
    }

    /// <summary>Shared indexed-atom-shape emit body used by both the
    /// DynamicMethod runtime path (above) and the chunk-71 persisted
    /// assembly path. Self-references for the per-clause IL CP push
    /// route through <paramref name="emitSelf"/>.
    ///
    /// <para>Chunk 76 — PGO. <paramref name="profileKey"/> ≥ 0 emits
    /// the <em>instrumented</em> ground-dispatch: each atom match
    /// lands on its own success label that records a hit via
    /// <see cref="IlProfileCounters.Bump"/>. <paramref name="groundOrder"/>,
    /// when non-null, is a permutation of clause indices giving the
    /// order in which to emit the ground-dispatch <c>cmp</c> chain —
    /// the phase-2 <em>optimised</em> form puts the
    /// most-frequently-matched atom first. The ground dispatch is a
    /// pure lookup (whichever atom matches, the answer is the same),
    /// so reordering it is always semantics-preserving. The
    /// var-dispatch path is never reordered — its clause order is the
    /// observable solution order.</para></summary>
    private static void EmitIndexedAtomBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        IndexedAtomInfo info,
        SelfDelegateEmitter emitSelf,
        int profileKey = -1,
        int[]? groundOrder = null,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        var clauses = info.Clauses;
        int[] atomIds = clauses.Select(c => c.AtomId).ToArray();
        int n = clauses.Count;

        var failLabel = emit.DefineLabel("fail");
        _emitOwnerFid = predicate.FunctorId;

        // Per-clause body labels. For trivial clauses (chunk 52) the
        // body is `get_atom + proceed`; for non-trivial (chunk 190)
        // it's whatever IL-supported opcodes the body holds. Both run
        // via EmitClauseBody.
        var bodyLabels = new Sigil.Label[n];
        for (int i = 0; i < n; i++)
            bodyLabels[i] = emit.DefineLabel($"body_{i}");

        // varEnter[i]: pushes CP for cursor=i+1 (unless last) and
        // jumps to bodyLabel[i]. Used by the var-dispatch path —
        // cursor i tries clause i, leaving an IL CP for clause i+1
        // on backtrack.
        var varEnterLabels = new Sigil.Label[n];
        for (int i = 0; i < n; i++)
            varEnterLabels[i] = emit.DefineLabel($"var_enter_{i}");

        // Chunk-182 Call-site cursors for non-tail Calls inside any
        // clause body. The cursor space is partitioned:
        //   cursor 0          → tag dispatch (ground/var)
        //   cursor 1..n-1     → varEnter[cursor] (next clause on backtrack)
        //   cursor n..n+M-1   → resume after the j-th non-tail Call site
        int totalCallSites = 0;
        foreach (var c in clauses)
            totalCallSites += CountNonTailCallOpcodes(
                predicate.Bytecode, c.BodyStart, c.BodyEnd);
        var callResumeLabels = new Sigil.Label[totalCallSites];
        for (int j = 0; j < totalCallSites; j++)
            callResumeLabels[j] = emit.DefineLabel($"call_resume_{j + 1}");

        // Top-level cursor switch.
        // Call-site resume cursors first (chunk 182 forward-resume).
        for (int j = 0; j < totalCallSites; j++)
        {
            emit.LoadArgument(1);
            emit.LoadConstant(n + j);
            emit.BranchIfEqual(callResumeLabels[j]);
        }
        // Clause-entry cursors 1..n-1 → varEnter[cursor].
        for (int i = 1; i < n; i++)
        {
            emit.LoadArgument(1);
            emit.LoadConstant(i);
            emit.BranchIfEqual(varEnterLabels[i]);
        }
        // cursor == 0 falls through to tag dispatch; anything else
        // unreachable → fail.
        var cursorZero = emit.DefineLabel("cursor_zero");
        emit.LoadArgument(1);
        emit.LoadConstant(0);
        emit.BranchIfEqual(cursorZero);
        emit.Branch(failLabel);
        emit.MarkLabel(cursorZero);

        // cursor == 0: deref A1, dispatch on tag.
        EmitDerefA0(emit);
        var a1Local = emit.DeclareLocal<Cell>("a1");
        emit.StoreLocal(a1Local);

        emit.LoadLocalAddress(a1Local);
        emit.Call(CellTagGetter);
        var tagLocal = emit.DeclareLocal<byte>("tag");
        emit.StoreLocal(tagLocal);

        var groundDispatchLabel = emit.DefineLabel("ground_dispatch");

        // if (tag == Tag.Ref) goto varEnter[0]
        emit.LoadLocal(tagLocal);
        emit.LoadConstant((int)Tag.Ref);
        emit.BranchIfEqual(varEnterLabels[0]);
        // if (tag == Tag.Atom) goto ground_dispatch
        emit.LoadLocal(tagLocal);
        emit.LoadConstant((int)Tag.Atom);
        emit.BranchIfEqual(groundDispatchLabel);
        // Any other tag → fail.
        emit.Branch(failLabel);

        // Ground dispatch: cmp atomId against each clause's atomId,
        // jump to that clause's body on match.
        emit.MarkLabel(groundDispatchLabel);
        emit.LoadLocalAddress(a1Local);
        emit.Call(CellAsAtomIdGetter);
        var atomIdLocal = emit.DeclareLocal<int>("atomId");
        emit.StoreLocal(atomIdLocal);

        int[] order = groundOrder ?? Enumerable.Range(0, n).ToArray();

        if (profileKey >= 0)
        {
            // Chunk 76 PGO: per-clause success label that bumps the
            // hit counter, then jumps to the body.
            var successLabels = new Sigil.Label[n];
            for (int ci = 0; ci < n; ci++)
                successLabels[ci] = emit.DefineLabel($"ground_success_{ci}");
            foreach (int ci in order)
            {
                emit.LoadLocal(atomIdLocal);
                EmitAtomId(emit, atomIds[ci]);
                emit.BranchIfEqual(successLabels[ci]);
            }
            emit.Branch(failLabel);
            for (int ci = 0; ci < n; ci++)
            {
                emit.MarkLabel(successLabels[ci]);
                emit.LoadConstant(profileKey);
                emit.LoadConstant(ci);
                emit.Call(IlProfileCountersBump);
                emit.Branch(bodyLabels[ci]);
            }
        }
        else
        {
            foreach (int ci in order)
            {
                emit.LoadLocal(atomIdLocal);
                EmitAtomId(emit, atomIds[ci]);
                emit.BranchIfEqual(bodyLabels[ci]);
            }
            emit.Branch(failLabel);
        }

        // varEnter[i]: push CP for cursor=i+1 (unless last) and jump
        // to bodyLabel[i]. The body's own get_atom opcode does the
        // actual A0/atom unification.
        for (int i = 0; i < n; i++)
        {
            emit.MarkLabel(varEnterLabels[i]);
            if (i < n - 1)
            {
                emit.LoadArgument(0);                  // engine
                emitSelf(emit);                        // → PredicateDelegate
                emit.LoadConstant(i + 1);              // next cursor
                emit.LoadConstant(1);                  // arity
                emit.Call(EnginePushIlCpMethod);
            }
            emit.Branch(bodyLabels[i]);
        }

        // Per-clause body emit. Each body's non-tail Calls use the
        // shared siteCounter (cursorBase = n places Call-site cursors
        // above the clause-entry range).
        int siteCounter = 0;
        for (int i = 0; i < n; i++)
        {
            emit.MarkLabel(bodyLabels[i]);
            EmitClauseBody(emit, predicate.Bytecode,
                clauses[i].BodyStart, clauses[i].BodyEnd,
                failLabel, predicate.CallSites,
                callSiteIndexCounter: () => ++siteCounter,
                resumeLabels: callResumeLabels,
                emitSelfDelegate: emitSelf,
                calleeMap: calleeMap,
                cursorBase: n);
        }

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>A counter the IL emission embeds into the bytecode as a
    /// constant to look up the freshly-emitted delegate at runtime. This
    /// is the Tier-1 equivalent of a self-reference; Sigil doesn't expose
    /// the dynamic method's delegate during emission, so we route through
    /// a static side table keyed by an integer.</summary>
    private static int _nextHolderKey = 1;
    private static readonly MethodInfo IndexedDelegateHolderGet =
        typeof(IndexedDelegateHolder).GetMethod(nameof(IndexedDelegateHolder.Get))!;

    /// <summary>Emits IL that leaves a <see cref="PredicateDelegate"/> on
    /// the evaluation stack — the running predicate's own delegate, used
    /// as the callback target for <c>engine.PushIlChoicePoint</c>. Two
    /// implementations:
    /// <list type="bullet">
    /// <item>DynamicMethod: <c>LoadConstant(holderKey); Call(IndexedDelegateHolderGet)</c>,
    /// resolved at runtime from a process-wide dictionary.</item>
    /// <item>Persisted assembly: <c>LoadField(arrayField); LoadConstant(slot); LoadElement&lt;PredicateDelegate&gt;()</c>,
    /// resolved at load time from a static array field on the emitted type.</item>
    /// </list></summary>
    internal delegate void SelfDelegateEmitter(Sigil.Emit<PredicateDelegate> emit);

    internal static SelfDelegateEmitter SelfFromHolder(int holderKey) =>
        e =>
        {
            e.LoadConstant(holderKey);
            e.Call(IndexedDelegateHolderGet);
        };

    internal static SelfDelegateEmitter SelfFromArrayField(
        System.Reflection.FieldInfo arrayField, int slot) =>
        e =>
        {
            e.LoadField(arrayField);
            e.LoadConstant(slot);
            e.LoadElement<PredicateDelegate>();
        };

    /// <summary>Side table that lets a freshly-emitted IL delegate
    /// reference itself for the <c>PushIlChoicePoint</c> call without
    /// running into the chicken-and-egg of "the delegate must exist
    /// before we can name it in IL". The IL embeds an integer key; at
    /// runtime <see cref="Get"/> resolves it to the stored delegate. The
    /// table is process-wide but write-once-per-key, so there's no
    /// thread-safety concern beyond the lock around the dictionary.</summary>
    internal static class IndexedDelegateHolder
    {
        // ConcurrentDictionary so Get — called from IL-emitted dispatch
        // code on every multi-clause Tier-1 call — reads lock-free.
        // Profiling Blint showed the previous `lock (_lock)` here
        // dominating wall time (~40%, blocked on the global lock) once
        // any predicate was promoted. The stored value is the already-
        // wrapped Func, so Get no longer allocates a fresh delegate per
        // call either (the old `new Func<...>(del)` was per-dispatch GC
        // pressure on the hottest path).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Func<Engine, int, bool>> _byKey = new();
        private static readonly object _lock = new();

        /// <summary>The lock the IL emission takes around the
        /// emit-and-register sequence so two concurrent compiles don't
        /// race on <c>_nextHolderKey</c>.</summary>
        public static object RegistrationLock => _lock;

        public static void Register(int key, PredicateDelegate del)
            => _byKey[key] = new Func<Engine, int, bool>(del);

        public static Func<Engine, int, bool> Get(int key) => _byKey[key];
    }

    /// <summary>Resolves a callee functor id to its current-query
    /// bytecode address by consulting <see cref="Engine.CurrentFunctorAddresses"/>.
    /// Called from IL-emitted Execute opcodes (chunk 47) so the tail-call
    /// target stays correct across queries even when the link layout
    /// changes between them.</summary>
    public static class IlExecuteHelper
    {
        public static int Resolve(Engine engine, int functorId)
        {
            var map = engine.CurrentFunctorAddresses;
            if (map is null)
                throw new InvalidOperationException(
                    "IL Execute: engine has no CurrentFunctorAddresses set. "
                    + "The embedding layer must populate it at query setup.");
            if (!map.TryGetValue(functorId, out int address))
                throw PrologRuntimeException.UndefinedProcedure(functorId);
            // Phase 19+ — the address may be a CallTarget.ForUndefined
            // sentinel left by the linker (the IL caller's static
            // rewrite baked a direct Call/Execute against an
            // unresolved functor) AND the implicit_dynamic auto-
            // promote may since have materialised a trampoline.
            // Re-look-up the live entry; if it's still unresolved,
            // raise existence_error.
            if (Shumway.Core.CallTarget.IsUnresolved(address))
                throw PrologRuntimeException.UndefinedProcedure(functorId);
            return address;
        }
    }

    /// <summary>Phase 19 — runtime helper that the IL emit calls from
    /// <c>CallBuiltin call/N</c> and <c>CallBuiltin '$call'/2</c> sites.
    /// Mirrors the bytecode interpreter's <c>DispatchCall</c> (chunks 86,
    /// 88) but returns a sentinel value so the IL caller can branch on
    /// the three outcomes: synchronous success (the goal was a control
    /// construct that resolved inline — cut, true, or a builtin that
    /// returned true), synchronous failure (fail or a builtin that
    /// returned false), or "dispatch this target via the chunk-182
    /// threaded path" (an ordinary user predicate / a builtin replaced by
    /// a $call_* helper).
    ///
    /// <para>The IL caller sets up <c>Cp = resume_marker</c> only when
    /// the return is &gt;= 0 (an actual target address). For sync
    /// success the caller falls through to its next opcode; for sync
    /// fail the caller jumps to its fail label.</para>
    /// </summary>
    public static class IlMetaCallHelper
    {
        public const int SyncFail = -1;
        public const int SyncSuccess = -2;

        // Cached control-construct ids — the bytecode interpreter
        // re-interns these as private statics; we do the same so the
        // IL emit doesn't pay an Intern per dispatch.
        private static readonly int ConjFid =
            FunctorTable.Intern(AtomTable.Intern(",", permanent: true).Id, 2);
        private static readonly int DisjFid =
            FunctorTable.Intern(AtomTable.Intern(";", permanent: true).Id, 2);
        private static readonly int ArrowFid =
            FunctorTable.Intern(AtomTable.Intern("->", permanent: true).Id, 2);
        private static readonly int NegFid =
            FunctorTable.Intern(AtomTable.Intern("\\+", permanent: true).Id, 1);
        private static readonly int NotFid =
            FunctorTable.Intern(AtomTable.Intern("not", permanent: true).Id, 1);
        private static readonly int CutFid =
            FunctorTable.Intern(AtomTable.Intern("!", permanent: true).Id, 0);
        private static readonly int TrueFid =
            FunctorTable.Intern(AtomTable.Intern("true", permanent: true).Id, 0);
        private static readonly int FailFid =
            FunctorTable.Intern(AtomTable.Intern("fail", permanent: true).Id, 0);
        private static readonly int CallConjFid =
            FunctorTable.Intern(AtomTable.Intern("$call_conj", permanent: true).Id, 3);
        private static readonly int CallDisjFid =
            FunctorTable.Intern(AtomTable.Intern("$call_disj", permanent: true).Id, 3);
        private static readonly int CallArrowFid =
            FunctorTable.Intern(AtomTable.Intern("$call_arrow", permanent: true).Id, 3);
        private static readonly int CallNegFid =
            FunctorTable.Intern(AtomTable.Intern("$call_neg", permanent: true).Id, 1);

        /// <summary>Dispatches <c>call/N</c> with <paramref name="callArity"/>
        /// extra-arg count and the supplied cut barrier. Returns the
        /// callee's address (&gt;= 0), or <see cref="SyncSuccess"/>
        /// (the goal was <c>!</c>, <c>true</c>, or a builtin that
        /// returned true), or <see cref="SyncFail"/> (the goal was
        /// <c>fail</c>, or a builtin that returned false).
        ///
        /// <para>Side effects when returning a non-negative address:
        /// the X registers hold the dispatched goal's arguments
        /// (goal args + appended call/N extra args), and
        /// <c>engine.B0</c> is set to <paramref name="cutBarrier"/> so
        /// a neck_cut at the callee entry commits to the call's
        /// barrier rather than the IL caller's.</para>
        /// </summary>
        public static int Dispatch(Engine engine, int callArity, int cutBarrier)
        {
            Cell goal = DerefCell(engine, engine.GetRegister(0));

            // Save call/N's extra args before SetRegister reshuffles them.
            Cell[] extra = callArity > 1 ? new Cell[callArity - 1] : System.Array.Empty<Cell>();
            for (int i = 0; i < callArity - 1; i++)
                extra[i] = engine.GetRegister(i + 1);

            int atomId;
            int goalArity;
            int argBase;
            switch (goal.Tag)
            {
                case Tag.Atom:
                    atomId = goal.AsAtomId;
                    goalArity = 0;
                    argBase = -1;
                    break;
                case Tag.Str:
                    int functorIdx = goal.AsHeapIndex;
                    (atomId, goalArity) =
                        FunctorTable.Lookup(engine.GetHeap(functorIdx).AsFunctorId);
                    argBase = functorIdx + 1;
                    break;
                case Tag.Ref:
                case Tag.AttVar:
                    throw new PrologRuntimeException("instantiation_error");
                default:
                    throw new PrologRuntimeException("type_error", "callable");
            }

            int totalArity = goalArity + (callArity - 1);
            for (int i = 0; i < goalArity; i++)
                engine.SetRegister(i, engine.GetHeap(argBase + i));
            for (int i = 0; i < callArity - 1; i++)
                engine.SetRegister(goalArity + i, extra[i]);

            int functorId = FunctorTable.Intern(atomId, totalArity);
            // Chunk-88 control-construct routing — `!` inside the
            // runtime goal commits to the call's barrier via the
            // $call_* helpers' arity-3 form (X[2] carries the barrier).
            if (functorId == ConjFid)
            {
                engine.SetRegister(2, Cell.Int(cutBarrier));
                functorId = CallConjFid;
            }
            else if (functorId == DisjFid)
            {
                engine.SetRegister(2, Cell.Int(cutBarrier));
                functorId = CallDisjFid;
            }
            else if (functorId == ArrowFid)
            {
                engine.SetRegister(2, Cell.Int(cutBarrier));
                functorId = CallArrowFid;
            }
            else if (functorId == NegFid || functorId == NotFid)
            {
                functorId = CallNegFid;
            }

            // Cut as the runtime goal: commits to the call's barrier.
            // The interpreter's DispatchCall AdvancePc's after Cut;
            // for IL we just report sync success so the caller falls
            // through to its next opcode.
            if (functorId == CutFid)
            {
                engine.Cut(cutBarrier);
                return SyncSuccess;
            }
            if (functorId == TrueFid) return SyncSuccess;
            if (functorId == FailFid) return SyncFail;

            // Builtin-as-goal. The recursion case (call(call(...))) is
            // handled by re-entering Dispatch with the recovered arity
            // — the inner call's X[0] already holds its own inner goal.
            if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int builtinId))
            {
                var builtin = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                if (builtin.Name == "call")
                {
                    // call(call(...)) — inner call's arity is the
                    // builtin's arity, barrier resets to engine.B
                    // (a fresh call boundary).
                    return Dispatch(engine, builtin.Arity, engine.B);
                }
                if (builtin.Name == "$call")
                {
                    int innerBarrier = (int)DerefCell(engine, engine.GetRegister(1)).AsInt;
                    return Dispatch(engine, 1, innerBarrier);
                }
                engine.CurrentBuiltinName = builtin.Name;
                engine.CurrentBuiltinArity = builtin.Arity;
                try
                {
                    return builtin.Impl(engine) ? SyncSuccess : SyncFail;
                }
                catch (PrologRuntimeException re)
                {
                    re.StampBuiltin(builtin.Name, builtin.Arity);
                    throw;
                }
            }

            // User predicate. Set the cut barrier the call's `!` will
            // commit to, then return the dispatch address — the IL
            // caller threads Cp = resume_marker, Pc = target,
            // IlTailCallPending = true.
            engine.SetB0(cutBarrier);
            if (engine.CurrentFunctorAddresses is null
                || !engine.CurrentFunctorAddresses.TryGetValue(functorId, out int address))
                throw PrologRuntimeException.UndefinedProcedure(functorId);
            return address;
        }

        private static Cell DerefCell(Engine engine, Cell c) =>
            c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;

        /// <summary>Reads <c>engine.GetRegister(reg)</c>, dereferences
        /// once if it's a <c>Tag.Ref</c>, and returns the embedded int
        /// payload. Used by the IL emit to fetch <c>$call/2</c>'s
        /// cut-barrier argument (X[1]) without the IL needing to
        /// inline the deref logic.</summary>
        public static int ReadIntRegister(Engine engine, int reg)
        {
            Cell c = engine.GetRegister(reg);
            if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
            return (int)c.AsInt;
        }
    }

    /// <summary>Emits IL that loads <c>engine.GetRegister(0)</c>, derefs
    /// it if it's a REF, and leaves the resulting <see cref="Cell"/> on
    /// the evaluation stack.</summary>
    private static void EmitDerefA0(Sigil.Emit<PredicateDelegate> emit)
    {
        var a1Tmp = emit.DeclareLocal<Cell>("a1Tmp");
        var notRef = emit.DefineLabel("a1_not_ref");
        emit.LoadArgument(0);
        emit.LoadConstant(0);
        emit.Call(EngineGetRegisterMethod);
        emit.StoreLocal(a1Tmp);

        emit.LoadLocalAddress(a1Tmp);
        emit.Call(CellTagGetter);
        emit.LoadConstant((int)Tag.Ref);
        emit.UnsignedBranchIfNotEqual(notRef);

        // a1 is a REF: follow the chain. engine.GetHeap(engine.Deref(a1.AsHeapIndex)).
        emit.LoadArgument(0);
        emit.LoadArgument(0);
        emit.LoadLocalAddress(a1Tmp);
        emit.Call(CellAsHeapIndexGetter);
        emit.Call(EngineDerefMethod);
        emit.Call(EngineGetHeapMethod);
        emit.StoreLocal(a1Tmp);

        emit.MarkLabel(notRef);
        emit.LoadLocal(a1Tmp);
    }
}
