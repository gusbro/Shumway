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
    private static readonly MethodInfo EngineSetRegisterMethod =
        typeof(Engine).GetMethod(nameof(Engine.SetRegister), new[] { typeof(int), typeof(Cell) })!;
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
    // Chunk 216 — indexed-dispatch entry resolver (mirrors the WAM switch
    // cascade, returns the entry chain-node cursor). Keyed by functor id
    // so the same IL works under runtime promotion AND a persisted bundle
    // loaded in a fresh process — the functor id is name-relative via
    // chunk-197 EmitFunctorId, and the resolver builds the dispatch model
    // lazily from the engine's linked code on first call.
    private static readonly MethodInfo IlIndexedDispatchResolveByFidMethod =
        typeof(IlIndexedDispatch).GetMethod(nameof(IlIndexedDispatch.ResolveEntryByFunctorId))!;
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
    private static readonly MethodInfo EngineCurrentFunctorAddressesGetter =
        typeof(Engine).GetProperty(nameof(Engine.CurrentFunctorAddresses))!.GetGetMethod()!;
    private static readonly MethodInfo IlExecuteHelperResolveMethod =
        typeof(IlExecuteHelper).GetMethod(nameof(IlExecuteHelper.Resolve))!;
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
    private static readonly MethodInfo EngineUnifyArgCellMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyArgCell), new[] { typeof(Cell) })!;
    private static readonly MethodInfo EngineUnifyVariableXMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyVariableX), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyValueXMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyValueX), new[] { typeof(int) })!;
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
        // PSTR + Call (chunk 50).
        Opcode.GetPstr => true,
        Opcode.PutPstr => true,
        Opcode.Call => true,
        // Meta dbg_info (chunk 55) — pure compile-time metadata; the
        // emit path skips it without producing any IL.
        Opcode.Meta => true,
        _ => false,
    };

    private PredicateDelegate CompileSingleClause(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        int callSiteCount = CountNonTailCallOpcodes(predicate.Bytecode);
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
        EmitClauseBody(emit, predicate.Bytecode, 0, predicate.Bytecode.Length,
            failLabel, predicate.CallSites,
            callSiteIndexCounter: null, resumeLabels: null,
            calleeMap: calleeMap);
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

        _emitOwnerFid = predicate.FunctorId;
        // Cursor dispatch: 0 → start; N → resume_N.
        for (int i = 0; i < callSiteCount; i++)
        {
            emit.LoadArgument(1);
            emit.LoadConstant(i + 1);
            emit.BranchIfEqual(resumeLabels[i]);
        }
        emit.Branch(startLabel);

        emit.MarkLabel(startLabel);
        int idxCounter = 0;
        EmitClauseBody(emit, predicate.Bytecode, 0, predicate.Bytecode.Length,
            failLabel, predicate.CallSites,
            callSiteIndexCounter: () => ++idxCounter,
            resumeLabels: resumeLabels,
            emitSelfDelegate: emitSelf,
            calleeMap: calleeMap);

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

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
                if (n == "call" || n == "$call") count++;
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
        int cursorBase = 1)
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
            if (op == Opcode.NeckCut)
            {
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
                // Deep cut: commit to the barrier stashed in Y[slot].
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
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
                emit.LoadConstant(builtinId);
                emit.Call(BuiltinsRegistryGetByIdMethod);
                emit.Call(BuiltinEntryImplGetter);
                emit.LoadArgument(0);
                emit.Call(BuiltinImplInvokeMethod);
                emit.BranchIfFalse(failLabel);
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

                // Inlining (chunk 69): if the callee is a small static
                // leaf, emit its body opcodes inline instead of routing
                // through IlCallHelper.Run. Leaves push no CPs so no
                // meta-CP is needed; the post-call label still gets
                // marked for any outer logic but no choice point lives
                // there.
                if (calleeMap is not null
                    && calleeMap.TryGetValue(siteFunctorId, out var calleePred)
                    && IsLeafPredicate(calleePred))
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

                // engine.SetPc(IlExecuteHelper.Resolve(engine, siteFunctorId));
                emit.LoadArgument(0);
                emit.LoadArgument(0);
                EmitFunctorId(emit, siteFunctorId);
                emit.Call(IlExecuteHelperResolveMethod);
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

                // Inlining (chunk 69): if the callee is a small static
                // leaf, emit its body opcodes inline instead of going
                // through the Pc-set / IlTailCallPending / outer-
                // dispatch dance. The callee's own proceed (= return
                // true) is exactly what the caller needs at the
                // tail-call site, so suppressProceedReturn stays false.
                if (calleeMap is not null
                    && calleeMap.TryGetValue(siteFunctorId, out var calleePredX)
                    && IsLeafPredicate(calleePredX))
                {
                    EmitClauseBody(emit, calleePredX.Bytecode, 0, calleePredX.Bytecode.Length,
                        failLabel, Array.Empty<CallSite>(),
                        calleeMap: calleeMap, suppressProceedReturn: false);
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
                emit.LoadArgument(0);
                EmitFunctorId(emit, siteFunctorId);
                emit.Call(IlExecuteHelperResolveMethod);
                emit.Call(EngineSetPcMethod);
                emit.LoadArgument(0);
                emit.LoadConstant(true);
                emit.Call(EngineIlTailCallPendingSetter);
                emit.LoadConstant(true);
                emit.Return();
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
        // cursor 0 — the initial call: resolve the entry node via the WAM
        // switch cascade, then branch to it. The functor id is emitted
        // through EmitFunctorId (chunk 197) so a persisted-bundle .dll
        // gets its functor id patched at LoadBundle to the runtime-process
        // value; for runtime promotion it's a direct ldc.i4.
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
                cursorBase: callBase);
        }

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
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
                cursorBase: N);

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
