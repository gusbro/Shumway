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

    /// <summary>When set (the <c>SHUMWAY_IL_DUMP</c> env var by default, or a CLI flag
    /// such as <c>shumway-compile --dump-il</c>), each compiled IL method's textual
    /// instruction stream (Sigil <c>Instructions()</c>) is appended to this file with a
    /// header — for manual analysis of what the compiler emits (region or otherwise).
    /// Off (null) by default; appends, so delete the file between runs.</summary>
    public static string? IlDumpPath { get; set; } =
        System.Environment.GetEnvironmentVariable("SHUMWAY_IL_DUMP");
    private static readonly object IlDumpLock = new();

    /// <summary>Chunk 414 — env-gated shape diagnostics, stripped from normal
    /// builds (Release AND Debug) via <c>[Conditional("SHUMWAY_DIAG")]</c>;
    /// build with <c>-p:ShumwayDiag=true</c> to compile them in, then activate
    /// with <c>SHUMWAY_IL_SHAPE=&lt;level&gt;</c> at run time. The
    /// <paramref name="message"/> closure (and its captures) only exists in
    /// diag builds — the call site is removed otherwise.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private static void DiagShape(string level, bool when, Func<string> message)
    {
        if (when && System.Environment.GetEnvironmentVariable("SHUMWAY_IL_SHAPE") == level)
            System.Console.Error.WriteLine(message());
    }

    /// <summary>Chunk 402 — per-call output of <see cref="EmitPersistedMethod"/>: when
    /// the method compiled as a REGION, the (memberFunctorName, arity, entryCursor)
    /// table of its non-root members (the <see cref="RegionCursorKind.MemberEntry"/>
    /// cursors); null for a non-region method. <see cref="PersistedIlBuilder"/> persists
    /// it per entry so LoadBundle can alias a stripped member's functor to
    /// <c>EncodeResumeMarker(rootFid, entryCursor)</c>.</summary>
    internal List<(string Name, int Arity, int Cursor)>? LastRegionMemberCursors;

    // Phase 33 (corpus evidence) — Sigil label names must be unique per METHOD,
    // but a REGION method emits several member bodies with body-local pcs, so a
    // pc-keyed label name can collide across members (seen on the Arity corpus:
    // two members with a meta-call at the same body pc → "Label with name
    // 'metaCallThread_pc50' already exists"). A monotonic global sequence
    // suffixes every such label (uniqueness only needs to hold within one
    // method; Interlocked because compiles run on the worker + engine threads).
    private static int _labelSeq;
    private static int NextLabelSeq() => System.Threading.Interlocked.Increment(ref _labelSeq);

    /// <summary>Finalize an emit into a delegate, dumping its IL first when
    /// <c>SHUMWAY_IL_DUMP</c> is set. Call instead of <c>emit.CreateDelegate</c> at
    /// every compile site so the dump covers them all.</summary>
    private PredicateDelegate FinishEmit(Sigil.Emit<PredicateDelegate> emit, string header)
    {
        if (IlDumpPath is not null)
        {
            string text;
            try { text = emit.Instructions(); }
            catch (System.Exception ex) { text = $"(Instructions() failed: {ex.Message})"; }
            lock (IlDumpLock)
                System.IO.File.AppendAllText(IlDumpPath,
                    $"\n;;; ===== {header} =====\n{text}\n");
        }
        return emit.CreateDelegate(Optimizations);
    }

    /// <summary>Persisted-path counterpart of <see cref="FinishEmit"/>: dumps the IL to
    /// <see cref="IlDumpPath"/> (when set) then finalizes into a <c>MethodBuilder</c>.
    /// Used at the <see cref="EmitPersistedMethod"/> create sites so a LINKER dump
    /// (<c>shumway-link --dump-il</c>) shows the EXACT IL the bundle ships — post-prune,
    /// region mode, forced roots — rather than the runtime all-as-roots superset.</summary>
    private System.Reflection.Emit.MethodBuilder FinishPersistedEmit(
        Sigil.Emit<PredicateDelegate> emit, string header)
    {
        if (IlDumpPath is not null)
        {
            string text;
            try { text = emit.Instructions(); }
            catch (System.Exception ex) { text = $"(Instructions() failed: {ex.Message})"; }
            lock (IlDumpLock)
                System.IO.File.AppendAllText(IlDumpPath,
                    $"\n;;; ===== {header} =====\n{text}\n");
        }
        return emit.CreateMethod(Optimizations);
    }

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
    // Float literals (get_float / put_float). MakeFloat allocates the 2-cell
    // heap float and returns the header index; Cell.Ref wraps it so the value
    // unifies / binds exactly like the interpreter's float path. The float VALUE
    // is baked as an ldc.r8 constant (resolved from the predicate's pool at emit
    // time), so it is process-independent — no Phase-17 patch needed for persist.
    private static readonly MethodInfo EngineMakeFloatMethod =
        typeof(Engine).GetMethod(nameof(Engine.MakeFloat), new[] { typeof(double) })!;
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
    // ADR-031 — CP-free guard commit: the fast-path check that lets the
    // emitted commit skip materialising the clause choice point entirely
    // (see EmitCpFreeGuardCommit).
    private static readonly MethodInfo EngineHasPendingWakeupsGetter =
        typeof(Engine).GetProperty(nameof(Engine.HasPendingWakeups))!.GetGetMethod()!;
    // ADR-031 case B — the binding-guard snapshot/restore surface.
    private static readonly MethodInfo EngineBindingTrailTopGetter =
        typeof(Engine).GetProperty(nameof(Engine.BindingTrailTop))!.GetGetMethod()!;
    private static readonly MethodInfo EngineExtraTrailTopGetter =
        typeof(Engine).GetProperty(nameof(Engine.ExtraTrailTop))!.GetGetMethod()!;
    private static readonly MethodInfo EngineHeapTopGetter =
        typeof(Engine).GetProperty(nameof(Engine.HeapTop))!.GetGetMethod()!;
    private static readonly MethodInfo EngineBeginIlGuardMethod =
        typeof(Engine).GetMethod(nameof(Engine.BeginIlGuard), Type.EmptyTypes)!;
    private static readonly MethodInfo EngineCommitIlGuardMethod =
        typeof(Engine).GetMethod(nameof(Engine.CommitIlGuard), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineFailIlGuardMethod =
        typeof(Engine).GetMethod(nameof(Engine.FailIlGuard),
            new[] { typeof(int), typeof(int), typeof(int), typeof(int) })!;
    private static readonly MethodInfo EnginePushIlCpWithMarksMethod =
        typeof(Engine).GetMethod(nameof(Engine.PushIlChoicePointWithMarks),
            new[] { typeof(Func<Engine, int, bool>), typeof(int), typeof(int),
                    typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) })!;
    // ADR-031 G2 — the counter-throttled cancellation poll (NO heap GC: a GC
    // would move the heap under the guard's snapshot locals) emitted at the
    // back-edge of an inlined fail-direct callee's self-tail loop.
    private static readonly MethodInfo EngineBacktrackSafePointMethod =
        typeof(Engine).GetMethod(nameof(Engine.BacktrackSafePoint), Type.EmptyTypes)!;
    // Chunk 216 — indexed-dispatch entry resolver (mirrors the WAM switch
    // cascade, returns the entry chain-node cursor). Keyed by functor id
    // so the same IL works under runtime promotion AND a persisted bundle
    // loaded in a fresh process — the functor id is name-relative via
    // chunk-197 EmitFunctorId, and the resolver builds the dispatch model
    // lazily from the engine's linked code on first call.
    private static readonly MethodInfo IlIndexedDispatchResolveByFidMethod =
        typeof(IlIndexedDispatch).GetMethod(nameof(IlIndexedDispatch.ResolveEntryByFunctorId))!;
    // ADR-027 — inline sub-argument walk for the compiled index resolver.
    private static readonly MethodInfo IlWalkSubOrMissMethod =
        typeof(IlIndexedDispatch).GetMethod(nameof(IlIndexedDispatch.WalkSubOrMiss))!;
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
    // Phase 33 W6 — ExecuteBuiltin's tail-return contract reads the caller's
    // continuation (Cp) for BuiltinReturnPc.
    private static readonly MethodInfo EngineCpGetter =
        typeof(Engine).GetProperty(nameof(Engine.Cp))!.GetGetMethod()!;
    // ADR-025 stage (b) — the inline-ITE choice point's resume callback.
    private static readonly FieldInfo IlIteHelperResumeField =
        typeof(IlIteHelper).GetField(nameof(IlIteHelper.Resume))!;
    // ADR-025 — capture CURRENT B (the inline-ITE barrier; see Opcode.GetLevelB).
    private static readonly MethodInfo EngineGetLevelBMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetLevelB), new[] { typeof(int) })!;
    // Was DEBUG-only (diagnostic dumps); ADR-031 case G reads E at clause entry
    // for the lazy CP's entry marks, so the binding is now unconditional.
    private static readonly MethodInfo EngineEGetter =
        typeof(Engine).GetProperty(nameof(Engine.E))!.GetGetMethod()!;
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
    // Phase 29 region compilation — a member's proceed decodes Cp via this to
    // choose intra-region br (a return cursor) vs cross-region return-to-loop (-1).
    private static readonly MethodInfo EngineRegionReturnCursorMethod =
        typeof(Engine).GetMethod(nameof(Engine.RegionReturnCursor))!;
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
    /// IL-subset body-opcode check. Chunk 433 — memoized per predicate (see
    /// <see cref="IlShapeMemo"/>): the structural walk runs once per
    /// immutable <see cref="CompiledPredicate"/>; the calleeMap-dependent
    /// Call-resolvability check is re-applied per call.</summary>
    private static bool TryDescribeIndexed(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out IlIndexedDispatchInfo? info)
    {
        if (predicate.IlIndexedShapeMemo is not IlShapeMemo memo)
        {
            var callFids = new List<int>();
            IlIndexedDispatch.TryDescribe(predicate,
                (op, pc) => IsClauseBodyOpcodeStructural(op, predicate, pc, callFids),
                out var raw);
            memo = new IlShapeMemo(raw, callFids);
            predicate.IlIndexedShapeMemo = memo;
        }
        return memo.Resolve(calleeMap, out info);
    }

    /// <summary>Chunk 433 — structural variant of
    /// <see cref="IsClauseBodyOpcode"/> for the memoized describers: a
    /// <c>Call</c> site is always accepted and its callee fid RECORDED into
    /// <paramref name="callFids"/> (−1 when the site has no metadata), making
    /// the describe result a pure function of the immutable predicate. The
    /// calleeMap-dependent rejection the original applied at each Call is
    /// re-applied by <see cref="IlShapeMemo.Resolve{T}"/> — equivalent,
    /// because the describers use the predicate only as a conjunctive
    /// accept/reject filter.</summary>
    private static bool IsClauseBodyOpcodeStructural(
        Opcode op, CompiledPredicate predicate, int pc, List<int> callFids)
    {
        if (op == Opcode.Call)
        {
            callFids.Add(FindCallSiteFunctorId(predicate.CallSites, pc));
            return true;
        }
        // Every non-Call opcode ignores the calleeMap, so delegating with
        // null is exact.
        return IsClauseBodyOpcode(op, predicate, pc, calleeMap: null);
    }

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
        byte[] code = predicate.BytecodeUnfused;
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
            else if (op == Opcode.ExecuteBuiltin)
            {
                // Phase 33 W6 — only a META tail builtin blocks.
                var e = Shumway.Builtins.BuiltinsRegistry.GetById(
                    BytecodeIO.ReadInt32(code, pc + 1));
                if (e.IsCall || e.IsDollarCall)
                    unsupported.Add("ExecuteBuiltin(meta)");
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
        // Phase 33 L10 — the typed switch tables are dispatch skeleton too.
        // Leaving them out made DescribeRejection list "SwitchOnAtomArg" etc.
        // for EVERY indexed predicate rejected for an unrelated reason (an
        // unresolved Call, a poolless float literal), which mis-drove a whole
        // audit finding: the corpus census read 1 666 such rejects as "multi-
        // arg shapes not IL-describable" when 1 663 were masked
        // call->unresolved and 3 were a missing float pool. Multi-arg
        // switch_on_*_arg shapes describe + compile fine (IlIndexedDispatch).
        Opcode.SwitchOnAtom or Opcode.SwitchOnInteger or Opcode.SwitchOnStructure => true,
        Opcode.SwitchOnAtomArg or Opcode.SwitchOnIntegerArg
            or Opcode.SwitchOnStructureArg => true,
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
        // Phase 29 region compilation (Stage 3, gated): emit the root + its local
        // closure as ONE IL method when the region is in the minimal subset.
        if (RegionCompile && calleeMap is not null)
        {
            var region = IlRegionBuilder.Build(predicate, calleeMap,
                extraEligible: p => IsRegionMemberEligible(p, calleeMap));
            if (IsRegionEmittable(region, calleeMap, out var why))
            {
                DiagShape("1", true, () =>
                    $"[region-emit] root fid={predicate.FunctorId} members={region.MemberCount}"
                    + " [" + string.Join(",", region.Members.Select(m => $"{m.FunctorId}({FidName(m.FunctorId)}x{m.ClauseCount})")) + "]");
                var rplan = IlRegionPlanner.Plan(region,
                    m => TryDescribeIndexed(m, calleeMap, out var ii) ? ii!.Nodes.Count : 0,
                    m => RegionBuiltinResumePcs(m, calleeMap));
                return CompileRegion(region, rplan, calleeMap);
            }
            // Explain why a predicate WITH a local closure didn't become a region —
            // the coverage gaps (a backtrackable-builtin member, etc.).
            DiagShape("1", region.MemberCount >= 2, () =>
                $"[region-skip] root fid={predicate.FunctorId} {FidName(predicate.FunctorId)}/{predicate.Arity}"
                + $" members={region.MemberCount}: {why}");
        }
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
        byte[] code = predicate.BytecodeUnfused;
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
            if (op == Opcode.DeallocateProceed)
            {
                // Fused deallocate+proceed (chunk 220) — a body terminator. A
                // single-clause body with a frame ending in a non-tail-call goal
                // (a cut or a builtin) ends here; EmitClauseBody emits the
                // deallocate then the proceed-return, so it IS compilable. Must be
                // checked BEFORE IsSupportedOpcode (which also accepts it but does
                // not record the terminator). Without this, e.g. `p(X):-a(X),!.`
                // was wrongly rejected as cannot-compile.
                sawTerminator = true;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.ExecuteBuiltin)
            {
                // Phase 33 W6 — fused tail builtin (chunk 248): dispatch +
                // proceed in one opcode, a body terminator. Non-meta only
                // (IsClauseBodyOpcode has the same gate for the multi-clause
                // describers).
                var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                    BytecodeIO.ReadInt32(code, pc + 1));
                if (entry.IsCall || entry.IsDollarCall) return false;
                sawTerminator = true;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.TryMeElse)
            {
                // ADR-025 stage (b) — inline ITE (the body-CP arity sentinel).
                // A single-clause predicate has no dispatch chain, so any
                // try_me_else here must be the ITE form.
                if (BytecodeIO.ReadInt32(code, pc + 5) != OpcodeTable.InlineIteCpArity) return false;
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

    /// <summary>Chunk 433 — binary search. <see cref="CompiledPredicate.CallSites"/>
    /// is built in ascending <c>OpcodeOffset</c> order at every construction
    /// site (per-clause emission appends sites forward, predicate assembly
    /// concatenates clauses at increasing offsets, and the bundle codec
    /// round-trips that order), and offsets are unique (one site per opcode),
    /// so the linear scan this replaces — run per Call/Execute opcode inside
    /// every describe walk, region validation and emit — was O(sites) for no
    /// reason. A miss falls back to the linear scan, so behaviour is exact
    /// even for an unsorted list.</summary>
    private static int FindCallSiteFunctorId(
        IReadOnlyList<CallSite> sites, int opcodeOffset)
    {
        int lo = 0, hi = sites.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int off = sites[mid].OpcodeOffset;
            if (off == opcodeOffset) return sites[mid].CalleeFunctorId;
            if (off < opcodeOffset) lo = mid + 1;
            else hi = mid - 1;
        }
        // Defensive miss path: exact parity with the pre-433 linear scan in
        // case a future construction site ever emits out-of-order sites.
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
        byte[] code = pred.BytecodeUnfused;
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
        byte[] code = pred.BytecodeUnfused;
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
                // ADR-025 — an inline ITE needs a resume cursor + jump labels
                // the inline emit site doesn't plan; keep such bodies out of
                // rule inlining (they still compile standalone).
                case Opcode.TryMeElse:
                case Opcode.TrustMe:
                case Opcode.Jump:
                    return false;
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
                    // chunk 433 — precomputed flags instead of name compares.
                    if (entry.IsCall || entry.IsDollarCall || entry.IsBacktrackable)
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
    /// <summary>Chunk 433 — shared empty result so the gated-off path (the
    /// default: <see cref="InlineRules2"/> unset) allocates nothing per call.
    /// Callers only read the returned map.</summary>
    private static readonly Dictionary<int, CompiledPredicate> NoRuleInlineSites = new();

    private static Dictionary<int, CompiledPredicate> ComputeRuleInlineSites(
        CompiledPredicate predicate, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        // Runtime DynamicMethod path only: the persisted-bundle emit computes its
        // own (base) callSiteCount and would desync against the extended resume
        // cursors. (Restriction lifted when the persisted path counts them too.)
        if (!InlineRules2 || calleeMap is null || _persistPatches is not null)
            return NoRuleInlineSites;
        var sites = new Dictionary<int, CompiledPredicate>();
        byte[] code = predicate.BytecodeUnfused;
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
                    DiagShape("1", true, () =>
                        $"[rule-inline] caller fid={predicate.FunctorId} callee fid={callee.FunctorId} "
                        + $"bodycalls={CountNonTailCallOpcodes(callee.BytecodeUnfused)}");
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
            extra += CountRuleBodyThreadedCalls(callee.BytecodeUnfused);
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

    // ========================================================================
    // Phase 29 — IL REGION COMPILATION (flat local code space).
    // docs/design/il-region-compilation.md. A region (root + reachable local
    // callees, IlRegionBuilder) compiles to ONE IL method: each member a labeled
    // block emitted once, an intra-region call a `br`. Stage 3 = single-clause
    // members, intra-region calls + deterministic builtins only (no backtracking,
    // no cut, no cross-region user calls — those are Stages 4-6).
    // ========================================================================

    /// <summary>Region compilation toggle. DEFAULT ON since chunk 418: the
    /// chunk-418 validation showed regions fix the if-then-else lowering tax
    /// (the <c>$disj</c> helper costs two trampoline round-trips per iteration
    /// and breaks self-loop detection — regions make both intra-method
    /// branches: ~2× on ITE-recursion shapes, qsort −22%, boyer −15%, corpus
    /// output-identical, one-shot neutral under default promotion). Set
    /// <c>SHUMWAY_REGION=0</c> to disable. The PERSISTED bundle path ignores
    /// this default — BundleWriter region-compiles a bundle only together
    /// with the dead-region prune (all-as-roots region bundles measured 2.3×
    /// bigger, chunk 391). Settable (CLI dumps, tests); read once per
    /// <c>Compile</c>.</summary>
    public static bool RegionCompile { get; set; } =
        System.Environment.GetEnvironmentVariable("SHUMWAY_REGION") != "0";

    /// <summary>ADR-031 — delayed choice point for the neck-cut guard clause.
    /// A non-last chain clause of the shape <c>Head :- InlineGuard, !, Body.</c>
    /// (guard = non-binding, non-allocating inline ops — currently the
    /// <c>a_int_cmp</c> integer-comparison fast lane) is emitted WITHOUT its
    /// entry <c>PushIlChoicePoint</c>: guard failure is a direct IL branch to
    /// the next clause's label (the guard mutated no engine state, so there is
    /// nothing to restore), and the commit needs no <c>engine.Cut</c> teardown
    /// (nothing was pushed). The one caveat — attribute wakeups pending at the
    /// cut need a choice point to fail into — is handled by materialising the
    /// skipped CP LAZILY at the commit when <see cref="Engine.HasPendingWakeups"/>
    /// (state-identical to an entry push because the guard changed nothing).
    /// Set <c>SHUMWAY_CPFREE_GUARD=0</c> to disable (A/B lever).</summary>
    public static bool CpFreeGuardCommit { get; set; } =
        System.Environment.GetEnvironmentVariable("SHUMWAY_CPFREE_GUARD") != "0";

    /// <summary>Stage 9c (cost-based root selection): functor ids FORCED to be region
    /// ROOTS — excluded from absorption into any OTHER region. Promoting a shared member
    /// to its own root trades N duplicated copies of its sub-region for one copy + N
    /// cross-region trampolines, cutting the all-as-roots inter-root duplication. Set by
    /// the bundle build (save/restore) before a pruned-IL build; null = none.</summary>
    public static IReadOnlySet<int>? RegionForcedRootFids { get; set; }

    /// <summary>Phase 33 (bundle-wide calleeMap) — when non-null, region
    /// membership is restricted to these functor ids (the bundle entry's own
    /// predicates). The persisted build sets it (save/restore) so an entry
    /// compiled against the whole bundle's predicate map never absorbs a
    /// cross-module callee; null = no scope (the runtime promotion path).
    /// ThreadStatic on purpose: a bundle build on the caller thread must not
    /// scope background promotions running on the IlCompileWorker.</summary>
    [System.ThreadStatic]
    public static IReadOnlySet<int>? RegionMemberScopeFids;

    /// <summary>The labels + cursor map threaded into <see cref="EmitClauseBody"/>
    /// while emitting a region member's block.</summary>
    private sealed class RegionEmitContext
    {
        public IlRegion Region = null!;
        public int RegionFid;
        public Sigil.Label RetLabel = null!;
        public Sigil.Label DispatchLabel = null!;
        public Sigil.Label FailLabel = null!;
        public IReadOnlyDictionary<int, Sigil.Label> MemberEntry = null!;
        public Sigil.Label[] CursorLabels = null!;
        public Dictionary<(int Member, int Pc), int> CursorBySite = null!;
        // (member index, clause index 1..N-1) → the clause-alternative cursor.
        public Dictionary<(int Member, int Clause), int> ClauseAltCursor = null!;
        // (member index, node index 0..K-1) → the IndexNode cursor (Stage 6c).
        public Dictionary<(int Member, int Node), int> IndexNodeCursor = null!;
        public int CurrentMemberIndex;
    }

    /// <summary>Stage-3 eligibility: a region this minimal emit can handle — at
    /// least two members, every member single-clause, and every member's body
    /// containing only intra-region <c>Call</c>/<c>Execute</c> and deterministic
    /// builtins (no cut, no backtrackable / meta builtin, no cross-region user call,
    /// no multi-clause dispatch). The unhandled shapes (backtracking, cut,
    /// cross-region) come in Stages 4-6.</summary>
    private static string FidName(int fid)
    {
        if (fid < 0) return "?";
        try { var (a, ar) = Shumway.Core.FunctorTable.Lookup(fid);
              return Shumway.Core.AtomTable.GetById(a)?.Name ?? "?"; }
        catch { return "?"; }
    }

    internal static bool IsRegionEmittable(
        IlRegion region, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
        => IsRegionEmittable(region, calleeMap, out _);

    /// <summary>As <see cref="IsRegionEmittable(IlRegion, IReadOnlyDictionary{int, CompiledPredicate})"/>,
    /// but on rejection sets <paramref name="reason"/> to a human-readable cause (which
    /// member, which opcode) — surfaced under <c>SHUMWAY_IL_SHAPE=1</c> to explain why a
    /// predicate with a local closure did NOT become a region (the coverage gaps).</summary>
    internal static bool IsRegionEmittable(
        IlRegion region, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out string? reason)
    {
        reason = null;
        if (region.MemberCount < 2) { reason = "members<2 (no local closure)"; return false; }
        foreach (var m in region.Members)
            if (!RegionMemberOk(m, calleeMap, out var r))
            { reason = $"member {FidName(m.FunctorId)}/{m.Arity}: {r}"; return false; }
        return true;
    }

    /// <summary>The per-member validation shared by <see cref="IsRegionEmittable(IlRegion,
    /// IReadOnlyDictionary{int, CompiledPredicate}, out string)"/> (which members of a
    /// formed region are all OK) and <see cref="IsRegionMemberEligible"/> (whether a
    /// callee may be PULLED IN as a member). A member must be a shape the region emit
    /// handles — single-clause, try_me_else chain, or indexed switch_on_term/arg — and
    /// its emitted body (the full bytecode, or per-clause ranges for an indexed member,
    /// since the resolve replaces the dispatch cascade) must use only opcodes the region
    /// handles (cut OK; Call/Execute with metadata; no backtrackable / meta builtin —
    /// those need a resume cursor the planner doesn't yet allocate). Sharing this between
    /// the two callers is what makes path-1 work: a callee whose body has a backtrackable
    /// builtin is now refused MEMBERSHIP (stays a cross-region trampoline) instead of
    /// being pulled in and then rejecting the whole region.</summary>
    private static bool RegionMemberOk(
        CompiledPredicate m, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out string? reason)
    {
        reason = null;
        if (m.ClauseCount > 1 && !TryDescribeTryMeElseChain(m, calleeMap, out _))
        {
            if (!TryDescribeIndexed(m, calleeMap, out var info))
            { reason = "multi-clause, neither chain nor indexed"; return false; }
            foreach (var (start, end) in info!.Clauses)
                if (!RegionBodyOpcodesOk(m.BytecodeUnfused, start, end, m.CallSites, out var r))
                { reason = $"(indexed body) {r}"; return false; }
            return true;
        }
        if (!RegionBodyOpcodesOk(m.BytecodeUnfused, 0, m.BytecodeUnfused.Length, m.CallSites, out var r2))
        { reason = r2; return false; }
        return true;
    }

    /// <summary>Validates that a region member's body code (<paramref name="start"/>..
    /// <paramref name="end"/>) uses only opcodes the region emit handles: cut is
    /// allowed (chunk-367 barrier scoping); a <c>Call</c>/<c>Execute</c> must have
    /// call-site metadata (intra-region <c>br</c> / cross-region trampoline, Stage 6);
    /// a <c>CallBuiltin</c> must be deterministic (a backtrackable / meta builtin
    /// needs a resume cursor the region planner doesn't yet allocate).</summary>
    private static bool RegionBodyOpcodesOk(
        byte[] code, int start, int end, IReadOnlyList<CallSite> callSites)
        => RegionBodyOpcodesOk(code, start, end, callSites, out _);

    private static bool RegionBodyOpcodesOk(
        byte[] code, int start, int end, IReadOnlyList<CallSite> callSites, out string? reason)
    {
        reason = null;
        int pc = start;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            switch (op)
            {
                case Opcode.Call:
                case Opcode.Execute:
                    if (FindCallSiteFunctorId(callSites, pc) < 0)
                    { reason = $"{op} @{pc} has no call-site metadata"; return false; }
                    break;
                // ADR-025 (ITE in regions) — an inline ITE/disjunction body is
                // now region-emittable: the planner gives its try_me_else pc
                // an ELSE re-entry cursor (via CollectBuiltinResumePcs) and
                // the emit pushes the region delegate + that cursor; TrustMe
                // marks the label; Jump is a local forward branch. A
                // dispatch-chain try_me_else (real arity >= 0) stays accepted
                // as before — it is the member's own clause dispatch.
                // Chunk 424: backtrackable / meta CallBuiltin sites are now
                // region-emittable — the planner allocates each a
                // BuiltinResume cursor and the emit threads the chunk-218 /
                // chunk-182 markers with the REGION's fid+cursor.
            }
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
            if (size <= 0) { reason = $"undecodable opcode {op} @{pc}"; return false; }
            pc += size;
        }
        return true;
    }

    /// <summary>Chunk 424 — the (sorted) byte offsets of <paramref name="m"/>'s
    /// <c>CallBuiltin</c> sites that need a <see cref="RegionCursorKind.BuiltinResume"/>
    /// cursor: backtrackable builtins (chunk 218's <c>BuiltinReturnPc</c> resume) and
    /// runtime meta-calls (<c>call/N</c>, <c>'$call'/2</c> — chunk-182 threading).
    /// Walks the same ranges <see cref="RegionMemberOk"/> validates: clause ranges for
    /// an indexed member (its dispatch tables aren't linearly decodable), the whole
    /// body otherwise.</summary>
    private static IReadOnlyList<int> RegionBuiltinResumePcs(
        CompiledPredicate m, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        var pcs = new List<int>();
        if (m.ClauseCount > 1 && !TryDescribeTryMeElseChain(m, calleeMap, out _)
            && TryDescribeIndexed(m, calleeMap, out var info))
        {
            foreach (var (start, end) in info!.Clauses)
                CollectBuiltinResumePcs(m.BytecodeUnfused, start, end, pcs);
        }
        else
        {
            CollectBuiltinResumePcs(m.BytecodeUnfused, 0, m.BytecodeUnfused.Length, pcs);
        }
        pcs.Sort();
        return pcs;
    }

    private static void CollectBuiltinResumePcs(byte[] code, int start, int end, List<int> pcs)
    {
        int pc = start;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.CallBuiltin)
            {
                var e = Shumway.Builtins.BuiltinsRegistry.GetById(
                    BytecodeIO.ReadInt32(code, pc + 1));
                // chunk 433 — precomputed flag instead of the name switch.
                if (e.IsCall || e.IsDollarCall || e.IsBacktrackable)
                    pcs.Add(pc);
            }
            // ADR-025 (ITE in regions) — an inline ITE/disjunction's body
            // try_me_else (the arity sentinel) needs an ELSE re-entry cursor:
            // the CP carries the REGION delegate + this cursor, and a failed
            // condition re-enters the region method at the TrustMe-marked
            // label. Rides the BuiltinResume site kind — the planner merges
            // pcs in order and the emit resolves by (member, pc), so no new
            // plumbing; nothing else consults the site's Kind for body sites.
            else if (op == Opcode.TryMeElse
                     && BytecodeIO.ReadInt32(code, pc + 5) == OpcodeTable.InlineIteCpArity)
            {
                pcs.Add(pc);
            }
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
            if (size <= 0) return;
            pc += size;
        }
    }

    /// <summary>Region-membership filter (Stage 6b/6c/6d). A callee is pulled into a
    /// region only if it is itself IL-compilable AND <see cref="RegionMemberOk"/> — a
    /// shape the region emit handles (single-clause / try_me_else chain / indexed) whose
    /// emitted body uses only region-handled opcodes. Stage 6d (path 1): a callee whose
    /// body contains a backtrackable / meta builtin (<c>retract</c>, <c>atom_concat</c>,
    /// <c>call</c>, ...) is NOT pulled in — it stays a cross-region trampoline boundary
    /// (Stage 6a) and the rest of the region still forms, instead of one such callee
    /// poisoning the whole region (which is what blocked ~60 Blint local-closure
    /// predicates). The resume-cursor threading that would let such a builtin live INSIDE
    /// a member is a later step.</summary>
    private bool IsRegionMemberEligible(CompiledPredicate p,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        // Phase 33 (bundle-wide calleeMap) — when the persisted build compiles
        // an entry against the WHOLE bundle's predicate map, region membership
        // stays scoped to the entry's OWN predicates: absorbing a cross-module
        // callee would duplicate its body into this entry's region method
        // (semantically sound — static predicates are immutable — but it
        // changes region shapes and bloats the entry for no dispatch win; the
        // member's standalone IL lives in its own entry).
        if (RegionMemberScopeFids?.Contains(p.FunctorId) == false) return false;
        // Stage 9c: forced root. Checked LIVE (not cached) — the bundle build
        // mutates the RegionForcedRootFids static between the root-selector
        // probe phase and the compile phase.
        if (RegionForcedRootFids?.Contains(p.FunctorId) == true) return false;
        // Chunk 433 — the rest (CanCompileCore + RegionMemberOk) is a pure
        // function of (predicate, calleeMap), recomputed thousands of times by
        // the RegionRootSelector fixpoint (once per call-site edge per region
        // build per iteration). Cache per fid for the current calleeMap
        // instance (fid → predicate is unique within one map; a new map —
        // e.g. the next query's promotion view — resets the cache).
        if (!ReferenceEquals(_regionMemberPureCacheMap, calleeMap)
            || _regionMemberPureCache is null)
        {
            _regionMemberPureCache = new Dictionary<int, bool>();
            _regionMemberPureCacheMap = calleeMap;
        }
        if (_regionMemberPureCache.TryGetValue(p.FunctorId, out bool ok)) return ok;
        ok = CanCompileCore(p, calleeMap, allowIndexedDispatch: true)
             && RegionMemberOk(p, calleeMap, out _);
        _regionMemberPureCache[p.FunctorId] = ok;
        return ok;
    }

    /// <summary>Chunk 433 — see <see cref="IsRegionMemberEligible"/>.</summary>
    private Dictionary<int, bool>? _regionMemberPureCache;
    private IReadOnlyDictionary<int, CompiledPredicate>? _regionMemberPureCacheMap;

    /// <summary>The set of functor ids a region rooted at <paramref name="root"/> would
    /// ABSORB as <c>br</c>-members when emitted (Stage 9 input) — the predicates whose
    /// standalone form this root makes intra-region, INCLUDING the root itself. Matches
    /// exactly what <see cref="Compile"/> emits: it builds the region with the runtime
    /// membership filter, and returns just <c>{root}</c> when the region is not emittable
    /// (root stays a per-predicate method, so every callee trampolines out). Independent
    /// of <see cref="RegionCompile"/> — it answers "if region-compiled, what is absorbed",
    /// which the dead-region reachability (<see cref="RegionReachability"/>) consumes when
    /// the bundle is built in region mode.</summary>
    public IReadOnlyCollection<int> RegionMemberFids(
        CompiledPredicate root, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
        => RegionMemberFids(root, calleeMap, extraExcluded: null);

    /// <param name="extraExcluded">Stage 9c: additional functor ids excluded from
    /// absorption (treated as forced roots) for THIS computation, on top of
    /// <see cref="RegionForcedRootFids"/> — lets the root selector probe regions for a
    /// candidate promotion set without mutating the global static.</param>
    public IReadOnlyCollection<int> RegionMemberFids(
        CompiledPredicate root, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        IReadOnlySet<int>? extraExcluded)
    {
        if (calleeMap is null) return new[] { root.FunctorId };
        var region = IlRegionBuilder.Build(root, calleeMap,
            extraEligible: p => IsRegionMemberEligible(p, calleeMap)
                && extraExcluded?.Contains(p.FunctorId) != true);
        if (!IsRegionEmittable(region, calleeMap)) return new[] { root.FunctorId };
        var s = new HashSet<int>(region.Members.Count);
        foreach (var m in region.Members) s.Add(m.FunctorId);
        return s;
    }

    /// <summary>Emit a whole region as one IL method (Stage 3). Layout: a `cur`
    /// local seeded from <c>arg1</c>; a `dispatch` jump table over the plan's cursor
    /// space (0 = root entry); each member as a labeled block; a shared `ret` handler
    /// that decodes <c>Cp</c> (<see cref="Engine.RegionReturnCursor"/>) — intra-region
    /// → <c>br dispatch</c> at the return cursor, cross-region → <c>return true</c>
    /// (the loop runs <c>Cp</c>).</summary>
    private PredicateDelegate CompileRegion(IlRegion region, IlRegionPlan plan,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        // The holder pattern gives the region method a reference to its OWN delegate
        // (for PushIlChoicePoint when a multi-clause member's clause dispatch pushes
        // a choice point that re-enters this method on backtrack).
        lock (IndexedDelegateHolder.RegistrationLock)
        {
            int holderKey = _nextHolderKey;
            var emitSelf = SelfFromHolder(holderKey);
            var del = CompileRegionUnlocked(region, plan, calleeMap, emitSelf);
            IndexedDelegateHolder.Register(holderKey, del);
            _nextHolderKey = holderKey + 1;
            return del;
        }
    }

    private PredicateDelegate CompileRegionUnlocked(IlRegion region, IlRegionPlan plan,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, SelfDelegateEmitter emitSelf)
    {
        var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
            $"ShumwayIlRegion_{region.Root.FunctorId}_{region.Root.Arity}",
            doVerify: DoVerify || DebugMode);
        EmitRegionInto(emit, emitSelf, region, plan, calleeMap,
            typeof(Func<Engine, int, bool>));   // runtime path: SelfFromHolder → Func
        int regionFid = region.Root.FunctorId;
        return FinishEmit(emit,
            $"region root={regionFid} {FidName(regionFid)} members=["
            + string.Join(",", region.Members.Select(m => $"{m.FunctorId}:{FidName(m.FunctorId)}/{m.Arity}x{m.ClauseCount}"))
            + "]");
    }

    /// <summary>Emit a region's body into <paramref name="emit"/> — shared by the runtime
    /// DynamicMethod path (<see cref="CompileRegionUnlocked"/>) and the persisted-IL
    /// TypeBuilder path (<see cref="EmitPersistedMethod"/>, prereq-i for the bundle
    /// dead-region prune). The two differ only in how the method is created and how
    /// <paramref name="emitSelf"/> resolves the self-delegate (holder vs delegates-array
    /// field); the region layout — dispatch switch, member blocks, ret / fail handlers —
    /// and its functor-id / resume-marker uses (all through the chunk-194 patchable
    /// helpers) are identical, so persisted region methods patch correctly cross-process.</summary>
    private void EmitRegionInto(
        Sigil.Emit<PredicateDelegate> emit, SelfDelegateEmitter emitSelf,
        IlRegion region, IlRegionPlan plan,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        System.Type selfDelType)
    {
        int regionFid = region.Root.FunctorId;
        _emitOwnerFid = regionFid;

        // Stage 11 (IL-size / CSE): every multi-clause / indexed member's
        // PushIlChoicePoint reloads the SAME region self-delegate. Hoist that load to ONE
        // local here, ahead of the dispatch switch (which dominates every member / cursor
        // label, so the store reaches every push site), and hand members a loader that
        // just reads it. The break-even depends on which self-loader is in play, and the
        // two paths that share this emit use different ones:
        //   • Persisted path — SelfFromArrayField, 3 cheap IL ops (ldsfld/ldc.i4/ldelem),
        //     no runtime cost beyond the array index. Pure IL-SIZE play: hoist costs 4 ops
        //     once (load+store) and saves 2/push, so it only shrinks at ≥3 (saving 2·P−4).
        //   • Runtime-promotion path — SelfFromHolder, 2 IL ops but each executes a
        //     ConcurrentDictionary lookup at RUNTIME on the CP-push (backtracking) path.
        //     Replacing that per-push dict probe with a hoisted local load is a runtime
        //     win at ≥2 (worth the +1 IL op the size math costs at P=2) — the same call
        //     the chunk-426 inline-fact hoist already makes for its holder-only pushes.
        // So gate by the loader kind: selfDelType is PredicateDelegate on the persisted
        // (array-field) path, Func<Engine,int,bool> on the runtime (holder) path.
        SelfDelegateEmitter effectiveSelf = emitSelf;
        int pushSites = 0;
        foreach (var s in plan.Sites)
            if (s.Kind == RegionCursorKind.ClauseAlt || s.Kind == RegionCursorKind.IndexNode)
                pushSites++;
        int hoistGate = selfDelType == typeof(PredicateDelegate) ? 3 : 2;
        if (pushSites >= hoistGate)
        {
            var selfDelLoc = emit.DeclareLocal(selfDelType, "rselfdel");
            emitSelf(emit);
            emit.StoreLocal(selfDelLoc);
            effectiveSelf = e => e.LoadLocal(selfDelLoc);
        }

        var failLabel = emit.DefineLabel("rfail");
        var retLabel = emit.DefineLabel("rret");
        var dispatchLabel = emit.DefineLabel("rdispatch");
        var curLoc = emit.DeclareLocal<int>("rcur");

        var memberEntry = new Dictionary<int, Sigil.Label>();
        foreach (var m in region.Members)
            memberEntry[m.FunctorId] = emit.DefineLabel($"rmember_{m.FunctorId}");

        var cursorLabels = new Sigil.Label[plan.TotalCursors];
        cursorLabels[0] = memberEntry[regionFid];           // cursor 0 = root entry
        var cursorBySite = new Dictionary<(int, int), int>();
        var clauseAltCursor = new Dictionary<(int, int), int>();
        var indexNodeCursor = new Dictionary<(int, int), int>();
        foreach (var s in plan.Sites)
        {
            if (s.Kind == RegionCursorKind.MemberEntry)
            {
                // Chunk 402: an external-entry cursor — its switch slot IS the member's
                // entry label (already defined above); no separate block, no site map.
                cursorLabels[s.Cursor] =
                    memberEntry[region.Members[s.MemberIndex].FunctorId];
                continue;
            }
            cursorLabels[s.Cursor] = emit.DefineLabel($"rcur_{s.Cursor}");
            if (s.Kind == RegionCursorKind.ClauseAlt)
                clauseAltCursor[(s.MemberIndex, s.ClauseIndex)] = s.Cursor;
            else if (s.Kind == RegionCursorKind.IndexNode)
                indexNodeCursor[(s.MemberIndex, s.ClauseIndex)] = s.Cursor;
            else
                cursorBySite[(s.MemberIndex, s.Pc)] = s.Cursor;
        }

        var ctx = new RegionEmitContext
        {
            Region = region, RegionFid = regionFid, RetLabel = retLabel,
            DispatchLabel = dispatchLabel, FailLabel = failLabel, MemberEntry = memberEntry,
            CursorLabels = cursorLabels, CursorBySite = cursorBySite,
            ClauseAltCursor = clauseAltCursor, IndexNodeCursor = indexNodeCursor,
        };

        // cur = arg1; br dispatch (the switch routes the cursor to its label).
        emit.LoadArgument(1);
        emit.StoreLocal(curLoc);
        emit.MarkLabel(dispatchLabel);
        emit.LoadLocal(curLoc);
        emit.Switch(cursorLabels);
        emit.Branch(failLabel);                              // out of range (unreachable)

        for (int mi = 0; mi < region.Members.Count; mi++)
        {
            var member = region.Members[mi];
            ctx.CurrentMemberIndex = mi;
            emit.MarkLabel(memberEntry[member.FunctorId]);   // clause 0 / single-clause entry
            if (member.ClauseCount == 1)
                EmitClauseBody(emit, member.BytecodeUnfused, 0, member.BytecodeUnfused.Length,
                    failLabel, member.CallSites, emitSelfDelegate: effectiveSelf,
                    calleeMap: calleeMap, regionCtx: ctx);
            else if (TryDescribeIndexed(member, calleeMap, out var idxInfo))
                EmitRegionIndexedMember(emit, member, mi, idxInfo!, ctx, effectiveSelf, calleeMap);
            else
                EmitRegionMultiClauseMember(emit, member, mi, ctx, effectiveSelf, calleeMap);
        }

        emit.MarkLabel(retLabel);
        emit.LoadArgument(0);
        // Phase 33 — MUST go through EmitFunctorId, not a raw LoadConstant:
        // in persist mode a build-process fid means nothing at runtime. With
        // the raw constant baked, a persisted region whose BUILD-time fid
        // happened to equal the RUNTIME fid of a caller's region claimed the
        // caller's resume marker as its own and branched into a bogus
        // internal cursor — an infinite CP-push loop (Blint --exe hang, the
        // ILO mass parse failures, the member/2 8 GB stack crash).
        EmitFunctorId(emit, regionFid);
        emit.Call(EngineRegionReturnCursorMethod);
        emit.StoreLocal(curLoc);
        emit.LoadLocal(curLoc);
        emit.LoadConstant(0);
        emit.BranchIfGreaterOrEqual(dispatchLabel);          // intra-region return
        emit.LoadConstant(true);                              // cross-region return
        emit.Return();

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>Emit a MULTI-clause member's block (Stage 4) — a try_me_else chain.
    /// Clause 0 is at the member-entry label (already marked); clauses 1..N-1 are at
    /// their <c>ClauseAlt</c> cursor labels. Before each clause except the last, push
    /// a choice point carrying the NEXT clause's cursor + the region delegate, so a
    /// head-match (or later) failure returns false → backtrack → the CP → re-enters
    /// the region method at the next clause via <c>dispatch</c>. Each clause body is
    /// region-aware (its proceed → <c>br ret</c>, its calls threaded by the plan).</summary>
    private static void EmitRegionMultiClauseMember(
        Sigil.Emit<PredicateDelegate> emit, CompiledPredicate member, int mi,
        RegionEmitContext ctx, SelfDelegateEmitter emitSelf,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        if (!TryDescribeTryMeElseChain(member, calleeMap, out var chain) || chain is null)
            throw new InvalidOperationException(
                $"Region member fid={member.FunctorId} is multi-clause but not a try_me_else chain.");
        var clauses = chain.Clauses;
        int n = clauses.Count;
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
                emit.MarkLabel(ctx.CursorLabels[ctx.ClauseAltCursor[(mi, i)]]);

            // ADR-031 — CP-free guard clause (see EmitCpFreeGuardClause): guard
            // failure branches to the next clause-alternative's region cursor
            // label (directly, or via the restore stub); the entry CP push is
            // skipped (lazily materialised at the commit only under pending
            // wakeups). The GUARD slice is emitted with regionCtx null +
            // forceLeafRuleInline so a tier-G guard Call takes the chunk-69
            // INLINE path (failure = a direct branch to the guard's fail label)
            // instead of the region br (whose failure would go to the region
            // fail label — past this clause). The post-commit body slice keeps
            // the region context. The plan's forward-resume cursors for the
            // inlined guard Call sites are marked dead afterwards.
            if (CpFreeGuardCommit && i < n - 1
                && TryGetCpFreeGuard(
                    member.BytecodeUnfused, clauses[i].Start, clauses[i].End,
                    member.Arity, calleeMap, member.CallSites, out var ginfo))
            {
                int guardEnd = ginfo.CutPc;
                int mi0 = mi, i0 = i;
                EmitCpFreeGuardClause(emit,
                    (s, e, fl) =>
                    {
                        if (e <= guardEnd)
                            EmitClauseBody(
                                emit, member.BytecodeUnfused, s, e, fl, member.CallSites,
                                emitSelfDelegate: emitSelf, calleeMap: calleeMap,
                                forceLeafRuleInline: true,
                                localSalt: $"_rm{mi0}g{i0}");
                        else
                            EmitClauseBody(
                                emit, member.BytecodeUnfused, s, e, fl, member.CallSites,
                                emitSelfDelegate: emitSelf, calleeMap: calleeMap,
                                regionCtx: ctx);
                    },
                    member.BytecodeUnfused, clauses[i].Start, clauses[i].End, ginfo,
                    ctx.CursorLabels[ctx.ClauseAltCursor[(mi, i + 1)]], ctx.FailLabel,
                    emitSelf, ctx.ClauseAltCursor[(mi, i + 1)], member.Arity,
                    salt: $"_rm{mi}_c{i}",
                    markDeadCursors: () =>
                    {
                        // The plan allocated a forward-resume cursor per Call
                        // site in the member; the guard's Calls were inlined, so
                        // their cursors are unreachable — mark the labels (the
                        // dispatch switch still references them).
                        int pc2 = clauses[i0].Start;
                        byte[] code2 = member.BytecodeUnfused;
                        while (pc2 < guardEnd)
                        {
                            if ((Opcode)code2[pc2] == Opcode.Call
                                && ctx.CursorBySite.TryGetValue((mi0, pc2), out int deadCur))
                                emit.MarkLabel(ctx.CursorLabels[deadCur]);
                            pc2 += (Opcode)code2[pc2] == Opcode.Meta
                                ? 6 : OpcodeTable.Get(code2[pc2]).Size;
                        }
                    });
                continue;
            }

            if (i < n - 1)
            {
                emit.LoadArgument(0);                         // engine
                emitSelf(emit);                               // → region delegate
                emit.LoadConstant(ctx.ClauseAltCursor[(mi, i + 1)]);
                emit.LoadConstant(member.Arity);
                emit.Call(EnginePushIlCpMethod);
            }
            EmitClauseBody(emit, member.BytecodeUnfused, clauses[i].Start, clauses[i].End,
                ctx.FailLabel, member.CallSites, emitSelfDelegate: emitSelf,
                calleeMap: calleeMap, regionCtx: ctx);
        }
    }

    /// <summary>Emit an INDEXED member's block (Stage 6c) — the region analog of
    /// <see cref="EmitIndexedDispatchBody"/>. The member-entry label (already marked)
    /// holds the inline index decision (deref + tag/key tests, lowered from the
    /// compile-time index graph), branching forward to a chain node's label. A node
    /// pushes the region delegate's choice point carrying the NEXT node's region
    /// cursor (so a bucket-chain backtrack re-enters this method at that node via the
    /// dispatch switch), then branches to its clause body. Clause bodies are emitted
    /// once and region-aware (proceed → <c>br ret</c>, intra calls → <c>br</c>, their
    /// own calls threaded by the plan) exactly like every other member — the only
    /// indexed-specific code is the resolve + the per-node CP push. The node labels
    /// ARE the region cursor labels, so forward (resolve) and backward (CP) reach the
    /// same block. Index resolve labels/locals are salted per member
    /// (<c>_rm{mi}</c>) so several indexed members share one IL method cleanly.</summary>
    private static void EmitRegionIndexedMember(
        Sigil.Emit<PredicateDelegate> emit, CompiledPredicate member, int mi,
        IlIndexedDispatchInfo info, RegionEmitContext ctx, SelfDelegateEmitter emitSelf,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        int K = info.Nodes.Count;
        int N = info.Clauses.Count;
        string salt = $"_rm{mi}";

        // Node entry = the region cursor label (shared forward-resolve + backtrack
        // re-entry). Body labels are local to this member's block.
        var nodeLabels = new Sigil.Label[K];
        for (int n = 0; n < K; n++)
            nodeLabels[n] = ctx.CursorLabels[ctx.IndexNodeCursor[(mi, n)]];
        var bodyLabels = new Sigil.Label[N];
        for (int i = 0; i < N; i++)
            bodyLabels[i] = emit.DefineLabel($"ridx_body_{mi}_{i}");

        // ---- Resolve (member entry, forward): pick the entry node from the indexed
        //      argument. Inline when the graph builds; else the runtime resolver. ----
        if (!TryEmitInlineIndexResolve(emit, info, nodeLabels, salt))
        {
            var entry = emit.DeclareLocal<int>($"ridx_entry{salt}");
            emit.LoadArgument(0);
            EmitFunctorId(emit, member.FunctorId);
            emit.Call(IlIndexedDispatchResolveByFidMethod);
            emit.StoreLocal(entry);
            for (int n = 0; n < K; n++)
            {
                emit.LoadLocal(entry);
                emit.LoadConstant(n);
                emit.BranchIfEqual(nodeLabels[n]);
            }
            emit.Branch(ctx.FailLabel);   // unreachable: resolver returns a valid node
        }

        // ---- Chain nodes: push the next-node region CP (if any), run the body. ----
        for (int n = 0; n < K; n++)
        {
            emit.MarkLabel(nodeLabels[n]);
            int next = info.Nodes[n].NextCursor;
            if (next >= 0)
            {
                emit.LoadArgument(0);                            // engine
                emitSelf(emit);                                 // → region delegate
                emit.LoadConstant(ctx.IndexNodeCursor[(mi, next)]);   // next node's region cursor
                emit.LoadConstant(member.Arity);
                emit.Call(EnginePushIlCpMethod);
            }
            emit.Branch(bodyLabels[info.Nodes[n].ClauseIndex]);
        }

        // ---- Clause bodies, region-aware, emitted once and shared across nodes. ----
        for (int i = 0; i < N; i++)
        {
            emit.MarkLabel(bodyLabels[i]);
            EmitClauseBody(emit, member.BytecodeUnfused, info.Clauses[i].Start, info.Clauses[i].End,
                ctx.FailLabel, member.CallSites, emitSelfDelegate: emitSelf,
                calleeMap: calleeMap, regionCtx: ctx);
        }
    }

    /// <summary>Region-mode opcode handling (Stage 3). Returns true (and advances
    /// <paramref name="pcRef"/>) for the opcodes the region layout rewrites:
    /// <c>proceed</c> / <c>deallocate_proceed</c> → <c>br ret</c>; an intra-region
    /// non-tail <c>Call</c> → <c>SetB0</c> + <c>SetCp(return marker)</c> +
    /// <c>br member</c> + the return-continuation label; an intra-region tail
    /// <c>Execute</c> → <c>SetB0</c> + <c>br member</c> (Cp unchanged = the caller's
    /// continuation). Returns false for every other opcode (head match, unify,
    /// arith, allocate/deallocate, deterministic builtin), which the normal switch
    /// emits unchanged.</summary>
    /// <summary>Flush pending attribute wakeups at a region goal boundary (a
    /// `br`-call or a proceed), then fail (backtrack) if a constraint failed. The
    /// interpreter flushes at every Call/Execute/Proceed/Deallocate; IL code relies
    /// on control passing through the dispatch loop between trampoline calls to get
    /// those flushes — but an intra-region call/return is a `br` that bypasses the
    /// loop, so the region must flush at its OWN boundaries (same class as the
    /// chunk-339 IL-cut flush). Cheap: a `_pendingWakeups.Count==0` fast path.</summary>
    private static void EmitRegionWakeupFlush(
        Sigil.Emit<PredicateDelegate> emit, Sigil.Label failLabel)
    {
        emit.LoadArgument(0);
        emit.Call(EngineFlushWakeupsForIlCutMethod);
        emit.BranchIfFalse(failLabel);
    }

    private static bool TryEmitRegionOpcode(
        Sigil.Emit<PredicateDelegate> emit, byte[] code, int pc, Opcode op,
        RegionEmitContext ctx, ref int pcRef)
    {
        switch (op)
        {
            case Opcode.Proceed:
                EmitRegionWakeupFlush(emit, ctx.FailLabel);
                emit.Branch(ctx.RetLabel);
                pcRef = pc + 1;
                return true;
            case Opcode.DeallocateProceed:
                emit.LoadArgument(0);
                emit.Call(EngineDeallocateMethod);
                EmitRegionWakeupFlush(emit, ctx.FailLabel);
                emit.Branch(ctx.RetLabel);
                pcRef = pc + OpcodeTable.Get((byte)op).Size;
                return true;
            case Opcode.Call:
            case Opcode.Execute:
            {
                var member = ctx.Region.Members[ctx.CurrentMemberIndex];
                int fid = FindCallSiteFunctorId(member.CallSites, pc);
                if (fid < 0) return false;   // malformed — let the normal path throw
                // Flush pending wakeups at this goal boundary (the br/trampoline
                // bypasses the dispatch loop's flush); then set the cut barrier.
                EmitRegionWakeupFlush(emit, ctx.FailLabel);
                // engine.SetB0(engine.B) — the cut barrier for the callee.
                emit.LoadArgument(0);
                emit.LoadArgument(0);
                emit.Call(EngineBGetter);
                emit.Call(EngineSetB0Method);
                bool intra = ctx.Region.IsIntraRegion(fid);
                if (op == Opcode.Call)
                {
                    // Non-tail: register the forward continuation (Cp = a resume
                    // marker into THIS region at the plan's cursor for this site).
                    int cursor = ctx.CursorBySite[(ctx.CurrentMemberIndex, pc)];
                    emit.LoadArgument(0);
                    EmitResumeMarker(emit, ctx.RegionFid, cursor);
                    emit.Call(EngineSetCpMethod);
                    if (intra)
                    {
                        // Intra-region: br to the member block; its proceed returns
                        // here via `ret` → dispatch → the continuation label.
                        emit.Branch(ctx.MemberEntry[fid]);
                    }
                    else
                    {
                        // Cross-region: the Phase-16 trampoline — set Pc = callee
                        // entry marker, return to the dispatch loop; when the callee
                        // proceeds the loop re-enters this region at `cursor`.
                        emit.LoadArgument(0);
                        EmitFunctorId(emit, fid);
                        emit.LoadConstant(0);
                        emit.Call(EngineEncodeResumeMarkerMethod);
                        emit.Call(EngineSetPcMethod);
                        emit.LoadArgument(0);
                        emit.LoadConstant(true);
                        emit.Call(EngineIlTailCallPendingSetter);
                        emit.LoadConstant(true);
                        emit.Return();
                    }
                    emit.MarkLabel(ctx.CursorLabels[cursor]);   // the continuation
                }
                else if (intra)
                {
                    // Intra-region tail call: Cp already holds this member's caller
                    // continuation, so the callee's proceed returns straight to it.
                    emit.Branch(ctx.MemberEntry[fid]);
                }
                else
                {
                    // Cross-region tail call: tail-trampoline (Cp unchanged = the
                    // region's caller continuation; the callee's proceed returns to it).
                    emit.LoadArgument(0);
                    EmitFunctorId(emit, fid);
                    emit.LoadConstant(0);
                    emit.Call(EngineEncodeResumeMarkerMethod);
                    emit.Call(EngineSetPcMethod);
                    emit.LoadArgument(0);
                    emit.LoadConstant(true);
                    emit.Call(EngineIlTailCallPendingSetter);
                    emit.LoadConstant(true);
                    emit.Return();
                }
                pcRef = pc + OpcodeTable.Get((byte)op).Size;
                return true;
            }
            default:
                return false;
        }
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
        byte[] code = pred.BytecodeUnfused;
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
                // ADR-025 — inline ITE (CP push + jump labels): not flat.
                case Opcode.TryMeElse:
                case Opcode.TrustMe:
                case Opcode.Jump:
                    return false;
                case Opcode.CallBuiltin:
                {
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                        BytecodeIO.ReadInt32(code, pc + 1));
                    // meta-call + backtrackable builtins need resume cursors /
                    // the enclosing-call machinery — not a flat body.
                    // chunk 433 — precomputed flags instead of name compares.
                    if (entry.IsCall || entry.IsDollarCall || entry.IsBacktrackable)
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
        byte[] code = pred.BytecodeUnfused;
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
        // get_float: supported only when the driver supplied the float pool to
        // resolve the literal value from (else the predicate stays Tier-0).
        Opcode.GetFloat => _ilFloatPool is not null,
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
        // Float literals — only when the driver supplied the float pool (the
        // value is baked as an ldc.r8 constant, so persisted bundles need no
        // patch). Without a pool the predicate stays Tier-0.
        Opcode.GetFloat => _ilFloatPool is not null,
        Opcode.PutFloat => _ilFloatPool is not null,
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
        // ADR-025 stage (b) — inline if-then-else / disjunction. The mid-body
        // try_me_else is operand-gated in IsClauseBodyOpcode (arity 0 only);
        // trust_me marks the ELSE entry (the CP pop happened at backtrack);
        // jump is a plain unconditional br; get_level_b captures current B.
        Opcode.TrustMe => true,
        Opcode.Jump => true,
        Opcode.GetLevelB => true,
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
        // resume-label array must be sized to include them. Chunk 433 — computed
        // ONCE here and passed down (EmitSingleClauseMetaCpBody used to recompute).
        var ruleInlineSites = ComputeRuleInlineSites(predicate, calleeMap);
        int callSiteCount = CountNonTailCallOpcodes(predicate.BytecodeUnfused)
            + CountRuleInlineExtraCursors(ruleInlineSites);
        if (callSiteCount == 0)
        {
            // No meta-CP needed: pure head match + tail call (or no body).
            var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
                $"ShumwayIl_{predicate.FunctorId}_{predicate.Arity}",
                doVerify: DoVerify || DebugMode);
            EmitSingleClauseLeafBody(emit, predicate, calleeMap);
            return FinishEmit(emit,
                $"single-leaf fid={predicate.FunctorId} {FidName(predicate.FunctorId)}/{predicate.Arity}");
        }
        lock (IndexedDelegateHolder.RegistrationLock)
            return CompileSingleClauseWithMetaCpUnlocked(
                predicate, callSiteCount, calleeMap, ruleInlineSites);
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
        EmitClauseBody(emit, predicate.BytecodeUnfused, 0, predicate.BytecodeUnfused.Length,
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

        // Chunk 402: reset the per-method member-cursor output so a non-region method
        // doesn't inherit the previous region's table.
        LastRegionMemberCursors = null;

        // Prereq-i for the Stage-9 bundle prune: region compilation in the persisted-IL
        // path. A region method bakes its absorbed members' bodies in, so once it ships
        // their standalone forms can be pruned. The region emit uses the chunk-194
        // patchable functor-id / resume-marker helpers, so it patches cross-process like
        // every other persisted method. (Region compilation is off unless RegionCompile
        // is set; with it on, EVERY predicate compiles as a region root — correct but
        // duplicative until the prune skips absorbed-only members.)
        if (RegionCompile && calleeMap is not null)
        {
            var region = IlRegionBuilder.Build(predicate, calleeMap,
                extraEligible: p => IsRegionMemberEligible(p, calleeMap));
            if (IsRegionEmittable(region, calleeMap))
            {
                if (emitSelf is null)
                    throw new InvalidOperationException(
                        "Region predicate needs a delegates field for self-reference.");
                var plan = IlRegionPlanner.Plan(region,
                    m => TryDescribeIndexed(m, calleeMap, out var ii) ? ii!.Nodes.Count : 0,
                    m => RegionBuiltinResumePcs(m, calleeMap));
                // Chunk 402: hand the builder the (memberName, arity, entryCursor) table
                // so the load path can alias a stripped member's functor to
                // EncodeResumeMarker(rootFid, entryCursor) — name-relative (the runtime
                // process re-interns the name; functor ids drift cross-process).
                var memberCursors = new List<(string Name, int Arity, int Cursor)>();
                foreach (var s in plan.Sites)
                    if (s.Kind == RegionCursorKind.MemberEntry)
                    {
                        var m = region.Members[s.MemberIndex];
                        var (mAtom, mArity) = FunctorTable.Lookup(m.FunctorId);
                        memberCursors.Add(
                            (AtomTable.GetById(mAtom)?.Name ?? "", mArity, s.Cursor));
                    }
                LastRegionMemberCursors = memberCursors;
                DiagShape("1", true, () =>
                    $"[region-persist] root fid={predicate.FunctorId} {FidName(predicate.FunctorId)} "
                    + $"members={region.MemberCount}");
                EmitRegionInto(emit, emitSelf, region, plan, calleeMap,
                    typeof(PredicateDelegate));   // persisted path: SelfFromArrayField → PredicateDelegate
                return FinishPersistedEmit(emit,
                    $"persist region root fid={predicate.FunctorId} {FidName(predicate.FunctorId)}"
                    + $"/{predicate.Arity} members={region.MemberCount} "
                    + $"[{string.Join(", ", region.Members.Select(m => FidName(m.FunctorId) + "/" + m.Arity))}]");
            }
        }

        if (predicate.ClauseCount == 1)
        {
            int callSiteCount = CountNonTailCallOpcodes(predicate.BytecodeUnfused);
            if (callSiteCount == 0)
            {
                EmitSingleClauseLeafBody(emit, predicate, calleeMap);
            }
            else
            {
                if (emitSelf is null)
                    throw new InvalidOperationException(
                        "Single-clause meta-CP predicate needs a delegates field for self-reference.");
                EmitSingleClauseMetaCpBody(emit, predicate, callSiteCount, calleeMap, emitSelf,
                    typeof(PredicateDelegate));   // persisted path: SelfFromArrayField → PredicateDelegate
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
            EmitIndexedDispatchBody(emit, predicate, indexedInfo!, calleeMap, emitSelf,
                typeof(PredicateDelegate));   // persisted path: SelfFromArrayField → PredicateDelegate
        }
        else if (TryDescribeIndexedAtomPredicate(predicate, calleeMap, out var atomInfo))
        {
            if (emitSelf is null)
                throw new InvalidOperationException(
                    "Indexed-atom predicate needs a delegates field for self-reference.");
            EmitIndexedAtomBody(emit, predicate, atomInfo!, emitSelf,
                typeof(PredicateDelegate), calleeMap: calleeMap);
        }
        else if (TryDescribeTryMeElseChain(predicate, calleeMap, out var chainInfo))
        {
            if (emitSelf is null)
                throw new InvalidOperationException(
                    "Try-me-else chain predicate needs a delegates field for self-reference.");
            EmitTryMeElseChainBody(emit, predicate, chainInfo!, calleeMap, emitSelf,
                typeof(PredicateDelegate));   // persisted path: SelfFromArrayField → PredicateDelegate
        }
        else if (TryDescribeSwitchedChain(predicate, calleeMap, out var switchedInfo))
        {
            // Chunk 189: switch_on_term-headed predicates emit through
            // the same try_me_else body emitter — only the recogniser
            // differs.
            if (emitSelf is null)
                throw new InvalidOperationException(
                    "Switched-chain predicate needs a delegates field for self-reference.");
            EmitTryMeElseChainBody(emit, predicate, switchedInfo!, calleeMap, emitSelf,
                typeof(PredicateDelegate));   // persisted path: SelfFromArrayField → PredicateDelegate
        }
        else
        {
            throw new NotSupportedException(
                $"Predicate (fid={predicate.FunctorId}, clauses={predicate.ClauseCount}) "
                + "is outside the IL subset.");
        }

        return FinishPersistedEmit(emit,
            $"persist fid={predicate.FunctorId} {FidName(predicate.FunctorId)}"
            + $"/{predicate.Arity} clauses={predicate.ClauseCount}");
    }

    private PredicateDelegate CompileSingleClauseWithMetaCpUnlocked(
        CompiledPredicate predicate, int callSiteCount,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null,
        Dictionary<int, CompiledPredicate>? ruleInlineSites = null)
    {
        int holderKey = _nextHolderKey;
        var emitSelf = SelfFromHolder(holderKey);
        var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
            $"ShumwayIl_metacp_{predicate.FunctorId}_{predicate.Arity}",
            doVerify: DoVerify || DebugMode);
        EmitSingleClauseMetaCpBody(emit, predicate, callSiteCount, calleeMap, emitSelf,
            typeof(Func<Engine, int, bool>),   // runtime path: SelfFromHolder → Func
            ruleInlineSites);                  // chunk 433 — precomputed by the caller
        var del = FinishEmit(emit,
            $"compile fid={predicate.FunctorId} {FidName(predicate.FunctorId)}/{predicate.Arity} clauses={predicate.ClauseCount}");
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
        byte[] code = predicate.BytecodeUnfused;
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
                    && TryGetFactFirstArgKeys(callee.BytecodeUnfused, ranges, out _, out _))
                {
                    int k = ranges.Count;
                    if (cursor + (k - 1) >= Engine.ResumeMarkerCursorStride) break; // budget
                    int seq = NextLabelSeq();
                    var alt = new Sigil.Label[k - 1];
                    for (int j = 0; j < k - 1; j++)
                        alt[j] = emit.DefineLabel($"inl_{pc}_{seq}_alt{j}");
                    sites[pc] = new InlineSite
                    {
                        Fact = callee, ClauseRanges = ranges, BaseCursor = cursor,
                        AltLabels = alt, Continuation = emit.DefineLabel($"inl_{pc}_{seq}_cont"),
                    };
                    cursor += k - 1;
                }
            }
            pc += op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
        }
        cursorsUsed = cursor - firstCursor;
        DiagShape("1", sites.Count > 0, () => string.Join("\n", sites.Values.Select(s =>
            $"[inline] caller fid={predicate.FunctorId} callee fid={s.Fact.FunctorId} "
            + $"arity={s.Fact.Arity} clauses={s.ClauseRanges.Count}")));
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
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private void DiagnoseRegion(
        CompiledPredicate predicate, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_IL_SHAPE") != "3") return;
        if (calleeMap is null) return;
        bool Eligible(CompiledPredicate p) => IsRegionMemberEligible(p, calleeMap);
        var capped = IlRegionBuilder.Build(predicate, calleeMap, extraEligible: Eligible);
        var uncapped = IlRegionBuilder.Build(predicate, calleeMap, budgetBytes: 1_000_000, extraEligible: Eligible);
        if (uncapped.MemberCount <= 1) return;   // no local closure → uninteresting
        System.Console.Error.WriteLine(
            $"[region] root fid={predicate.FunctorId} members={capped.MemberCount}"
            + $" bytes={capped.TotalBytecodeBytes} (uncapped members={uncapped.MemberCount}"
            + $" bytes={uncapped.TotalBytecodeBytes})");
    }

    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private static void DiagnoseInlineCandidates(
        CompiledPredicate predicate, IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_IL_SHAPE") != "2") return;
        byte[] code = predicate.BytecodeUnfused;
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
                    else if (TryGetFactFirstArgKeys(callee.BytecodeUnfused, ranges, out _, out _))
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
        byte[] fcode = site.Fact.BytecodeUnfused;
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
                    EmitAtomId(emit, keys[c]);   // patchable: a persisted bundle resolves
                                                 // the runtime atom id at load (a raw
                                                 // build-time id would mismatch a fresh
                                                 // process — the chunk-359 inliner's
                                                 // persisted-bundle correctness bug).
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
        SelfDelegateEmitter emitSelf,
        System.Type selfDelType,
        Dictionary<int, CompiledPredicate>? ruleInlineSites = null)
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
        // (already counted into callSiteCount → resumeLabels). Chunk 433 — the
        // runtime path precomputed this when sizing callSiteCount and passes it
        // down; the persisted path (null) computes it here (a no-op under
        // _persistPatches — ComputeRuleInlineSites returns the shared empty map).
        ruleInlineSites ??= ComputeRuleInlineSites(predicate, calleeMap);

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

        // Chunk 426 (CSE, mirrors the region Stage-11 hoist): every inlined-fact
        // clause alternative's PushIlChoicePoint reloads the SAME self-delegate —
        // a per-push holder dictionary probe on the runtime path. Hoist that load
        // to ONE local ahead of the cursor switch (which dominates every push
        // site, including the backtrack re-entries), so each push is a LoadLocal.
        // Gate on ≥2 pushes: below that the hoist's load+store would only grow
        // the method. An inline site with k clauses pushes k−1 CPs = AltLabels.
        SelfDelegateEmitter effectiveSelf = emitSelf;
        int pushSites = 0;
        foreach (var site in inlineSites.Values)
            pushSites += site.AltLabels.Length;
        if (pushSites >= 2)
        {
            var selfDelLoc = emit.DeclareLocal(selfDelType, "mselfdel");
            emitSelf(emit);
            emit.StoreLocal(selfDelLoc);
            effectiveSelf = e => e.LoadLocal(selfDelLoc);
        }

        emit.LoadArgument(1);
        emit.Switch(cursorLabels);
        emit.Branch(startLabel);    // cursor out of range (unreachable) → start

        emit.MarkLabel(startLabel);
        int idxCounter = 0;
        // Self-tail-recursion → in-method loop (chunk 349): startLabel is the
        // cursor-0 entry (the cursor switch above already branched the resume
        // cursors away), so a self Execute branches straight back here.
        EmitClauseBody(emit, predicate.BytecodeUnfused, 0, predicate.BytecodeUnfused.Length,
            failLabel, predicate.CallSites,
            callSiteIndexCounter: () => ++idxCounter,
            resumeLabels: resumeLabels,
            emitSelfDelegate: effectiveSelf,
            calleeMap: calleeMap,
            selfFunctorId: predicate.FunctorId, selfTailLabel: startLabel,
            inlineSites: inlineSites, ruleInlineSites: ruleInlineSites);

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    // Chunk 218's IsBacktrackableBuiltinName — builtins that push a CP and call
    // ResumeAtReturnPc on retry, whose IL call_builtin site needs a resume
    // marker — is now BuiltinEntry.IsBacktrackable, DERIVED by reflection
    // (BacktrackableDetector) from each builtin's IL rather than a hand list, so
    // a new cursor builtin can't be silently forgotten. Every emit-time site
    // reads the per-entry flag.

    /// <summary>Counts non-tail <c>Call</c> opcodes in a clause's
    /// bytecode (Opcode.Call only — Opcode.Execute is the tail-call
    /// form and doesn't need a meta-CP).</summary>
    /// <summary>ADR-025 — true when the bytecode contains an inline
    /// ITE/disjunction, recognised by a try_me_else carrying the body-CP
    /// arity sentinel (never emitted by dispatch skeletons). Cheap pre-filter
    /// for the legacy recognisers whose linear me-else boundary scans would
    /// mis-parse the inline shape. (Previously keyed on the `jump` opcode,
    /// which the branch-tail-LCO shape no longer emits for a last-goal ITE.)</summary>
    private static bool ContainsInlineIteOpcode(byte[] code)
    {
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.TryMeElse
                && pc + 9 <= code.Length
                && BytecodeIO.ReadInt32(code, pc + 5) == OpcodeTable.InlineIteCpArity)
                return true;
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
            if (size <= 0) return false;
            pc += size;
        }
        return false;
    }

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
            // ADR-025 — each inline ITE consumes ONE resume cursor (the ELSE
            // entry). Counted via its try_me_else's body-CP arity SENTINEL,
            // which a dispatch-chain try_me_else never carries. (It used to be
            // counted via the `jump` opcode, but the branch-tail-LCO shape
            // emits no jump when the ITE is the clause's last goal.)
            else if (b == (byte)Opcode.TryMeElse
                     && BytecodeIO.ReadInt32(bytecode, pc + 5) == OpcodeTable.InlineIteCpArity)
                count++;
            // Phase 19: CallBuiltin call/N and CallBuiltin '$call'/2 are
            // also non-tail Calls — they thread through
            // IlMetaCallHelper.Dispatch and need a resume-cursor slot.
            else if (b == (byte)Opcode.CallBuiltin)
            {
                int builtinId = BytecodeIO.ReadInt32(bytecode, pc + 1);
                var e = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                // call/$call thread through IlMetaCallHelper.Dispatch
                // (chunk 182); backtrackable builtins need a resume marker
                // for their CP's resume (chunk 218). Chunk 433 —
                // precomputed flags instead of name compares.
                if (e.IsCall || e.IsDollarCall || e.IsBacktrackable) count++;
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
    /// to encode the resume marker (functorId, cursor).
    /// THREAD-STATIC on purpose: compiles run concurrently on the shared
    /// IlCompileWorker AND on engine threads (bundle / persisted builds —
    /// see _labelSeq's note), and this was the one piece of mutable emit
    /// state left plain-static. A concurrent compile clobbering it bakes
    /// ANOTHER predicate's fid into this delegate's resume markers, so a
    /// post-backtrack resume re-enters the WRONG delegate at an arbitrary
    /// cursor — rare, arbitrary corruption far from the cause. Set and read
    /// strictly within one synchronous emit, so thread-static is exact.</summary>
    [System.ThreadStatic]
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

    // ADR-022 item 2 — set (by Shumway.Embedding, which owns the block table and
    // interop class) while compiling, so a `'$native_run'('$nb$…', regs)` call is
    // inlined directly into the predicate's IL instead of dispatched as a builtin.
    // Null → no inlining (the call stays a normal builtin dispatch, run via the
    // interpreter / runtime delegate). All emitted calls are MemberRefs (interop +
    // marshalling), which the CLR resolves by name/signature at load — so this is
    // persisted-IL-safe with no entry in the Phase-17 patch table.
    [System.ThreadStaticAttribute]
    private static Shumway.Compiler.NativeC.NativeInlineContext? _nativeInline;

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
        IReadOnlyDictionary<int, CompiledPredicate>? ruleInlineSites = null,
        RegionEmitContext? regionCtx = null,
        bool forceLeafRuleInline = false,
        string localSalt = "")
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
            // Phase 29 region compilation (Stage 3): a member block's proceed /
            // intra-region call become br's into the shared region method instead
            // of returning to the dispatch loop. Handled before the normal opcode
            // switch so the region layout takes precedence.
            if (regionCtx is not null
                && TryEmitRegionOpcode(emit, code, pc, op, regionCtx, ref pc))
                continue;
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
            if (op == Opcode.CallBuiltin)
            {
                int builtinId = BytecodeIO.ReadInt32(code, pc + 1);
                // chunk 433 — one GetById (was two: Name then Arity), and the
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
                    // Chunk 424 — inside a region the cursor comes from the
                    // PLAN (keyed by this site's pc) and the marker carries
                    // the REGION's fid, so the dispatch loop re-enters the
                    // region method at the right switch slot. Standalone
                    // keeps the chunk-182 sequential counter.
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
                // Chunk 218: backtrackable builtins (between/3, append/3,
                // repeat/0, retract/1, …) push a CP whose resume calls
                // ResumeAtReturnPc(returnPc) — the returnPc is captured at
                // first invocation as engine.BuiltinReturnPc. The IL emit
                // here allocates a resume cursor and pre-sets
                // BuiltinReturnPc to that marker, so on resume the
                // dispatcher decodes the marker and re-enters this IL at
                // the post-builtin label. Non-backtrackable builtins skip
                // the cursor allocation — straight invocation.
                bool isBacktrackable = builtinEntry.IsBacktrackable;   // chunk 433
                Sigil.Label? builtinResumeLabel = null;
                if (isBacktrackable)
                {
                    // Chunk 424 — region members take their cursor from the
                    // PLAN (keyed by pc) with the REGION's fid in the marker;
                    // standalone keeps the chunk-218 sequential counter.
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
                // Phase 33 W6 — the chunk-248 fused tail builtin: dispatch the
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
                emit.LoadConstant(tailBuiltinId);
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
                int siteFunctorId = FindCallSiteFunctorId(callSites, pc);   // chunk 433
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
                if (forceLeafRuleInline && calleeMap is not null
                    && calleeMap.TryGetValue(siteFunctorId, out var fdCallee)
                    && !(IsLeafPredicate(fdCallee) || IsInlinableLeafRule(fdCallee))
                    && TryDescribeFailDirectCallee(fdCallee, out var fdClauses))
                {
                    EmitFailDirectCalleeInline(emit, fdCallee, fdClauses!,
                        failLabel, calleeMap, $"{lt}_fd{pc}");
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

                // Inlining (chunk 69): if the callee is a small static
                // leaf, emit its body opcodes inline instead of routing
                // through IlCallHelper.Run. Leaves push no CPs so no
                // meta-CP is needed; the post-call label still gets
                // marked for any outer logic but no choice point lives
                // there.
                if (calleeMap is not null
                    && calleeMap.TryGetValue(siteFunctorId, out var calleePred)
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
                int siteFunctorId = FindCallSiteFunctorId(callSites, pc);   // chunk 433
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
        // Chunk 433 — memoized per predicate (see IlShapeMemo): the
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
        if (op == Opcode.ExecuteBuiltin)
        {
            // Phase 33 W6 — the chunk-248 fused tail builtin (the linker's
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
        // Chunk 433 — memoized per predicate (see IlShapeMemo): the
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
                if (!IsClauseBodyOpcodeStructural(op, predicate, q, callFids)) return false;
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
            EmitIndexedDispatchBody(emit, predicate, info, calleeMap, emitSelf,
                typeof(Func<Engine, int, bool>));   // runtime path: SelfFromHolder → Func
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
        int K = info.Nodes.Count;
        int N = info.Clauses.Count;
        int totalCallSites = CountNonTailCallOpcodes(predicate.BytecodeUnfused);
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

        // Chunk 426 (CSE, mirrors the region Stage-11 hoist): every chain node's
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
        // Chunk 426: one O(1) jump table (IL `switch`) over the dense cursor
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
            // (chunk 197) so a persisted-bundle .dll gets it patched at
            // LoadBundle; for runtime promotion it's a direct ldc.i4.
            var entry = emit.DeclareLocal<int>("idx_entry");
            emit.LoadArgument(0);
            EmitFunctorId(emit, predicate.FunctorId);
            emit.Call(IlIndexedDispatchResolveByFidMethod);
            emit.StoreLocal(entry);
            // Chunk 426: node indices are dense 0..K-1 → O(1) jump table
            // instead of a linear compare chain.
            emit.LoadLocal(entry);
            emit.Switch(nodeLabels);
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
                effectiveSelf(emit);             // → PredicateDelegate (chunk-426 hoisted local)
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
            typeof(Func<Engine, int, bool>));   // runtime path: SelfFromHolder → Func

        var del = FinishEmit(emit,
            $"compile fid={predicate.FunctorId} {FidName(predicate.FunctorId)}/{predicate.Arity} clauses={predicate.ClauseCount}");
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
    /// <summary>ADR-032 sizing — promotion/link-time counters for the CP-free
    /// guard recogniser: which tier each accepted clause took, and WHY each
    /// cut-shaped clause was rejected. The reject reasons map 1:1 to the
    /// ADR-032 static-widening alternatives (Caps → raise the fail-direct
    /// caps; CalleeCut → callee-internal cuts; CalleeCalls → true-G3 nested
    /// inlining), so running a real program with these counters IS the impact
    /// estimate for each widening. Surfaced by <c>shumway-link --verbose</c>
    /// (persisted IL build) and <c>SHUMWAY_CPFREE_STATS=1</c> in the REPL
    /// (runtime promotion). Counts are per-emission (a PGO
    /// instrumented→optimised recompile counts twice) — indicative, not exact.</summary>
    public static class CpFreeGuardStats
    {
        public static long TierA, TierB, TierGLeaf, TierG2;
        public static long RejectGuardShape;       // non-whitelist op in a cut-shaped guard
        public static long RejectCalleeUnresolved; // no calleeMap / fid unresolved
        public static long RejectCalleeCalls;      // callee body calls others → G3 candidate
        public static long RejectCalleeCaps;       // callee over clause/byte caps
        public static long RejectCalleeCut;        // cut inside the callee
        public static long RejectCalleeShape;      // other callee shape

        /// <summary>Per-opcode breakdown of <see cref="RejectGuardShape"/> —
        /// WHICH non-whitelist opcode rejected the cut-shaped guard, so the
        /// whitelist-widening candidates rank by real frequency. Indexed by the
        /// opcode byte.</summary>
        public static readonly long[] RejectGuardOpByOpcode = new long[256];

        public static void Reset()
        {
            TierA = TierB = TierGLeaf = TierG2 = 0;
            RejectGuardShape = RejectCalleeUnresolved = RejectCalleeCalls = 0;
            RejectCalleeCaps = RejectCalleeCut = RejectCalleeShape = 0;
            System.Array.Clear(RejectGuardOpByOpcode);
        }

        public static long AcceptTotal => TierA + TierB + TierGLeaf + TierG2;
        public static long RejectTotal =>
            RejectGuardShape + RejectCalleeUnresolved + RejectCalleeCalls
            + RejectCalleeCaps + RejectCalleeCut + RejectCalleeShape;

        public static string Summary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("cp-free guard clauses (ADR-031):");
            sb.AppendLine($"  accepted {AcceptTotal}: tierA(cmp)={TierA} tierB(bind)={TierB} "
                + $"tierG(leaf-call)={TierGLeaf} tierG2(fail-direct-call)={TierG2}");
            sb.AppendLine($"  rejected {RejectTotal} (cut-shaped clauses keeping their CP):");
            sb.AppendLine($"    guard op outside whitelist        : {RejectGuardShape}");
            sb.AppendLine($"    callee unresolved                 : {RejectCalleeUnresolved}");
            sb.AppendLine($"    callee calls others (G3 candidate): {RejectCalleeCalls}");
            sb.AppendLine($"    callee over caps (raise candidate): {RejectCalleeCaps}");
            sb.AppendLine($"    callee has cut                    : {RejectCalleeCut}");
            sb.Append($"    callee shape (control/backtrack)  : {RejectCalleeShape}");
            return sb.ToString();
        }
    }

    /// <summary>ADR-031 — the recognised shape of a CP-free guard clause (see
    /// <see cref="TryGetCpFreeGuard"/>).</summary>
    internal readonly struct CpFreeGuardInfo
    {
        /// <summary>pc of the committing cut opcode (<c>neck_cut</c> or the deep
        /// <c>cut</c>).</summary>
        public int CutPc { get; init; }
        /// <summary>True → the framed <c>cut [slot]</c> (case G); false →
        /// frameless <c>neck_cut</c> (tiers A/B).</summary>
        public bool DeepCut { get; init; }
        /// <summary>The guard can bind / allocate — take the trail/heap/HB
        /// snapshot and restore it on the fail path.</summary>
        public bool NeedsSnapshot { get; init; }
        /// <summary>The guard writes argument registers (call staging, callee
        /// body temps) — save A0..arity-1 in IL locals at entry and restore
        /// them on the fail path.</summary>
        public bool NeedsRegSave { get; init; }
        /// <summary>The clause allocated an environment frame before the cut —
        /// the fail path must <c>Deallocate</c> before branching on.</summary>
        public bool Framed { get; init; }
    }

    /// <summary>ADR-031 recogniser — true when the clause byte range is a
    /// CP-free guard committing via a cut. Three tiers share one walk:
    ///
    /// <para><b>Tier A</b> — only <c>a_int_cmp</c> comparisons: non-binding,
    /// non-allocating, register-preserving. Guard failure branches DIRECTLY to
    /// the next clause with NO restore.</para>
    ///
    /// <para><b>Tier B</b> — additionally the head-unification / <c>=/2</c> op
    /// family: these can BIND and allocate → entry snapshot + restoring fail
    /// path (<see cref="CpFreeGuardInfo.NeedsSnapshot"/>).</para>
    ///
    /// <para><b>Tier G (guard calls)</b> — a FRAMED clause
    /// (<c>allocate_get_level; get_variable_y*; staging; call; cut slot</c>)
    /// whose every <c>Call</c> targets an INLINABLE single-clause leaf
    /// (<see cref="IsLeafPredicate"/> / <see cref="IsInlinableLeafRule"/>): the
    /// call is emitted INLINE (chunk-69 path, forced), so callee failure is a
    /// direct branch to the guard's fail label — fail-direct, no CP machinery.
    /// Call staging and the callee's body temps may write argument registers,
    /// so the clause saves/restores A0..arity-1
    /// (<see cref="CpFreeGuardInfo.NeedsRegSave"/>); the fail path deallocates
    /// the frame (<see cref="CpFreeGuardInfo.Framed"/>).</para></summary>
    internal static bool TryGetCpFreeGuard(
        byte[] code, int start, int end, int arity,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        IReadOnlyList<CallSite> callSites,
        out CpFreeGuardInfo info,
        bool analysisOnly = false)
    {
        info = default;
        bool snapshot = false, regSave = false, framed = false, sawRealOp = false;
        bool sawCall = false, sawFdCallee = false;
        int pc = start;

        // ADR-032 sizing — count a rejection only when the clause actually has
        // the commit-cut shape ahead (else this is just an ordinary clause).
        void CountReject(ref long counter, int fromPc)
        {
            if (!HasCutAhead(code, fromPc, end)) return;
            System.Threading.Interlocked.Increment(ref counter);
        }
        // The guard-op variant additionally records WHICH opcode rejected.
        void CountGuardOpReject(Opcode rejectedOp, int fromPc)
        {
            if (!HasCutAhead(code, fromPc, end)) return;
            System.Threading.Interlocked.Increment(ref CpFreeGuardStats.RejectGuardShape);
            System.Threading.Interlocked.Increment(
                ref CpFreeGuardStats.RejectGuardOpByOpcode[(byte)rejectedOp]);
        }
        void CountAccept()
        {
            if (sawCall)
                System.Threading.Interlocked.Increment(
                    ref sawFdCallee ? ref CpFreeGuardStats.TierG2 : ref CpFreeGuardStats.TierGLeaf);
            else if (snapshot)
                System.Threading.Interlocked.Increment(ref CpFreeGuardStats.TierB);
            else
                System.Threading.Interlocked.Increment(ref CpFreeGuardStats.TierA);
        }

        while (pc < end)
        {
            var op = (Opcode)code[pc];
            switch (op)
            {
                case Opcode.Meta:                      // dbg-info — transparent
                    pc += 6;
                    continue;
                case Opcode.NeckCut:
                    info = new CpFreeGuardInfo
                    {
                        CutPc = pc, DeepCut = false,
                        NeedsSnapshot = snapshot, NeedsRegSave = regSave, Framed = framed,
                    };
                    CountAccept();
                    return true;
                case Opcode.Cut:
                    if (!framed) return false;          // deep cut needs the frame's Y slot
                    info = new CpFreeGuardInfo
                    {
                        CutPc = pc, DeepCut = true,
                        NeedsSnapshot = snapshot, NeedsRegSave = regSave, Framed = true,
                    };
                    CountAccept();
                    return true;
                case Opcode.AllocateGetLevel:
                case Opcode.Allocate:                  // framed neck-cut clause (no get_level)
                    if (sawRealOp || framed) return false;   // only as the clause's first real op
                    framed = true;
                    break;
                case Opcode.AIntCmp:
                    break;                             // non-binding compare
                // Binding / allocating unify ops — no register writes.
                case Opcode.GetAtom:
                case Opcode.GetInteger:
                case Opcode.GetNil:
                case Opcode.GetFloat:
                case Opcode.GetValueX:
                case Opcode.GetStructure:
                case Opcode.GetList:
                case Opcode.GetListA1:
                case Opcode.GetListA2:
                case Opcode.UnifyValueX:
                case Opcode.UnifyConstant:
                case Opcode.UnifyInteger:
                case Opcode.UnifyAtom:
                case Opcode.UnifyNil:
                case Opcode.UnifyVoid:
                case Opcode.UnifyFloat:
                case Opcode.UnifyBigInt:
                case Opcode.UnifyStructure:
                case Opcode.UnifyList:
                    snapshot = true;
                    break;
                // Frame-local Y moves / unifies (frame required).
                case Opcode.GetVariableY:              // Yn := Ai — frame write only
                    if (!framed) return false;
                    break;
                case Opcode.GetValueY:                 // unify(Yn, Ai) — binds
                    if (!framed) return false;
                    snapshot = true;
                    break;
                case Opcode.PutVariableY:              // fresh var → Yn AND Ai (call staging)
                    if (!framed) return false;
                    snapshot = true;                   // allocates the fresh heap var
                    regSave = true;                    // writes the argument register
                    break;
                // Register-writing moves: covered by the entry register save.
                case Opcode.GetVariableX:              // Xn := Ai
                case Opcode.UnifyVariableX:            // Xn := subterm
                case Opcode.PutValueX:                 // A(target) := Xn
                case Opcode.PutValueY:                 // A(target) := Yn
                case Opcode.PutAtom:
                case Opcode.PutInteger:
                case Opcode.PutNil:
                case Opcode.PutFloat:
                case Opcode.PutVariableX:
                case Opcode.PutStructureR:
                case Opcode.PutListR:
                // Compound-argument builds for the guard call: allocate heap
                // (snapshot's heap reset covers), write the argument register
                // (regSave covers), and set write mode for the following
                // unify_* ops (already whitelisted). No CP, no pre-existing
                // binding.
                case Opcode.PutStructure:
                case Opcode.PutList:
                case Opcode.PutPstr:
                    snapshot = true;                   // put_structure/put_variable allocate
                    regSave = true;
                    break;
                case Opcode.Call:
                {
                    // Tier G: the call must resolve to a callee the guard-slice
                    // emission inlines, making its failure a direct branch
                    // (fail-direct): an inlinable single-clause leaf (chunk-69
                    // path), or — G2 — a fail-direct multi-clause / self-tail-
                    // recursive predicate (sequential-chain inline). Anything
                    // else keeps the CP.
                    if (calleeMap is null)
                    {
                        CountReject(ref CpFreeGuardStats.RejectCalleeUnresolved, pc);
                        return false;
                    }
                    int fid = FindCallSiteFunctorId(callSites, pc);
                    // ANALYSIS-ONLY: a Call whose target is a registered
                    // BUILTIN — in LINKED bytecode this is already a
                    // CallBuiltin (the chunk-247/248 linker rewrite), so the
                    // emit sites never see it; the --cpfree sweep analyses
                    // UNLINKED bytecode, where the classification must match
                    // what the linked form would get. NOT enabled for emission:
                    // emitting an unlinked builtin Call as a guard would take
                    // the threaded-call path whose failure bypasses the stub.
                    if (analysisOnly && fid >= 0
                        && Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out int bid))
                    {
                        var bentry = Shumway.Builtins.BuiltinsRegistry.GetById(bid);
                        if (bentry.IsCall || bentry.IsDollarCall || bentry.IsBacktrackable)
                        {
                            CountGuardOpReject(Opcode.CallBuiltin, pc);
                            return false;
                        }
                        snapshot = true;
                        regSave = true;
                        break;
                    }
                    if (fid < 0 || !calleeMap.TryGetValue(fid, out var callee))
                    {
                        CountReject(ref CpFreeGuardStats.RejectCalleeUnresolved, pc);
                        return false;
                    }
                    if (IsLeafPredicate(callee) || IsInlinableLeafRule(callee))
                    {
                        sawCall = true;
                    }
                    else if (TryDescribeFailDirectCallee(callee, out var fdCls, out var fdReject))
                    {
                        // SOUNDNESS — a MULTI-clause callee can yield MULTIPLE
                        // solutions (overlapping clauses binding differently);
                        // the sequential-chain inline commits to the first, so a
                        // fallible guard goal AFTER the call could never retry
                        // it. Sound when the callee is DETERMINISTIC (every
                        // non-last clause cut-commits — at most one solution,
                        // nothing to retry) OR the call is IMMEDIATELY followed
                        // by the commit cut (nothing can fail back into it).
                        // Single-clause callees are trivially det.
                        if (callee.ClauseCount > 1
                            && !FailDirectCalleeIsDet(fdCls!)
                            && !NextRealOpIsCut(code, pc + OpcodeTable.Get(op).Size, end))
                        {
                            CountReject(ref CpFreeGuardStats.RejectCalleeShape, pc);
                            return false;
                        }
                        sawCall = true;
                        sawFdCallee = true;
                    }
                    else
                    {
                        switch (fdReject)
                        {
                            case FailDirectReject.Caps:
                                CountReject(ref CpFreeGuardStats.RejectCalleeCaps, pc); break;
                            case FailDirectReject.Cut:
                                CountReject(ref CpFreeGuardStats.RejectCalleeCut, pc); break;
                            case FailDirectReject.HasCalls:
                                CountReject(ref CpFreeGuardStats.RejectCalleeCalls, pc); break;
                            default:
                                CountReject(ref CpFreeGuardStats.RejectCalleeShape, pc); break;
                        }
                        return false;
                    }
                    snapshot = true;                   // callee head unify binds
                    regSave = true;                    // staging + callee temps clobber
                    break;
                }
                case Opcode.CallBuiltin:
                {
                    // A deterministic, non-meta builtin guard (type test, ==/2,
                    // compare…): its IL emit already fails via a direct
                    // BranchIfFalse — fail-direct. Meta-call and backtrackable
                    // builtins need the CP machinery → reject.
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                        BytecodeIO.ReadInt32(code, pc + 1));
                    if (entry.IsCall || entry.IsDollarCall || entry.IsBacktrackable)
                    {
                        CountGuardOpReject(op, pc);
                        return false;
                    }
                    snapshot = true;                   // builtins may bind/allocate
                    regSave = true;                    // arg staging clobbers
                    break;
                }
                case Opcode.AIntBin:                   // X := A op B (is/2 fast lane)
                    snapshot = true;                   // may escalate/allocate
                    regSave = true;                    // writes the target register
                    break;
                default:
                    CountGuardOpReject(op, pc);
                    return false;
            }
            sawRealOp = true;
            pc += OpcodeTable.Get(op).Size;
        }
        return false;
    }

    /// <summary>ADR-031 G2 — one clause of a fail-direct callee (see
    /// <see cref="TryDescribeFailDirectCallee"/>).</summary>
    internal readonly struct FailDirectClause
    {
        public int Start { get; init; }
        /// <summary>pc of the terminator: <c>proceed</c>,
        /// <c>deallocate_proceed</c>, or the self-tail <c>execute</c>.</summary>
        public int TermPc { get; init; }
        /// <summary>Terminator is a self-tail <c>execute</c> — the inline
        /// emission loops back to the callee's inlined entry.</summary>
        public bool SelfTail { get; init; }
        /// <summary>The clause allocates an environment frame (its first real
        /// op is <c>allocate</c>) — mid-clause failure must deallocate before
        /// trying the next alternative.</summary>
        public bool Framed { get; init; }
        /// <summary>Terminator is the fused <c>deallocate_proceed</c> — the
        /// emit deallocates then joins.</summary>
        public bool DeallocProceed { get; init; }
        /// <summary>pc of the clause's FIRST top-level <c>neck_cut</c>, or -1.
        /// The cut commits the callee's clause selection: failures BEFORE it
        /// go to the next alternative, failures AFTER it exit the callee
        /// entirely. (In a fail-direct callee every cut is a neck cut — a deep
        /// cut implies a preceding call, which the shape excludes.)</summary>
        public int CutPc { get; init; }
    }

    /// <summary>True when the described callee is DETERMINISTIC (at most one
    /// solution): every clause except the last carries a top-level cut, so
    /// whichever clause yields commits (the bytecode analogue of ADR-030's
    /// all-but-last-commit dispatch rule; the last clause's whitelist body
    /// yields at most once). A det callee may sit ANYWHERE in the guard — the
    /// multi-solution retry hazard needs a second solution to exist.</summary>
    internal static bool FailDirectCalleeIsDet(List<FailDirectClause> clauses)
    {
        for (int i = 0; i < clauses.Count - 1; i++)
            if (clauses[i].CutPc < 0) return false;
        return true;
    }

    /// <summary>ADR-032 sizing tooling (<c>shumway-disasm --cpfree</c>) — replays
    /// the CP-free guard recogniser over a predicate exactly as the two chain
    /// emit sites would, bumping <see cref="CpFreeGuardStats"/>. Indexed / single
    /// predicates don't participate in the CP-free path and are skipped, matching
    /// the shipped emission.</summary>
    public static void AnalyzeCpFreeGuards(
        CompiledPredicate pred, IReadOnlyDictionary<int, CompiledPredicate> calleeMap)
    {
        // Structural chain describe (not the memoized calleeMap-resolving one:
        // an intra-file map misses cross-file callees, which must land in the
        // recogniser's "unresolved" bucket rather than skip the predicate).
        TryDescribeTryMeElseChainStructural(pred, new List<int>(), out var chain);
        if (chain is null) return;
        var cls = chain.Clauses;
        for (int i = 0; i < cls.Count - 1; i++)
            TryGetCpFreeGuard(pred.BytecodeUnfused, cls[i].Start, cls[i].End,
                pred.Arity, calleeMap, pred.CallSites, out _, analysisOnly: true);
    }

    /// <summary>True when the next non-dbg opcode at <paramref name="pc"/> is the
    /// commit cut — the position constraint that makes a MULTI-clause fail-direct
    /// callee sound (see the soundness note in <c>TryGetCpFreeGuard</c>).</summary>
    private static bool NextRealOpIsCut(byte[] code, int pc, int end)
    {
        while (pc < end && (Opcode)code[pc] == Opcode.Meta) pc += 6;
        return pc < end && (Opcode)code[pc] is Opcode.NeckCut or Opcode.Cut;
    }

    /// <summary>ADR-032 sizing — true when a top-level commit cut
    /// (<c>neck_cut</c> / <c>cut</c>) appears ahead in the clause range: the
    /// clause IS the guard-commit shape, so a recogniser rejection is a real
    /// missed CP-free opportunity worth counting (an ordinary cut-less clause
    /// is not).</summary>
    private static bool HasCutAhead(byte[] code, int pc, int end)
    {
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            if (op is Opcode.NeckCut or Opcode.Cut) return true;
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get(op).Size;
            if (size <= 0) return false;
            pc += size;
        }
        return false;
    }

    /// <summary>Why <see cref="TryDescribeFailDirectCallee"/> rejected a callee
    /// (ADR-032 sizing — <see cref="CpFreeGuardStats"/>).</summary>
    internal enum FailDirectReject
    {
        None,
        /// <summary>Over the clause-count / byte-size caps — a CAP-raise
        /// candidate.</summary>
        Caps,
        /// <summary>A cut inside a callee clause — the static-widening
        /// candidate "callee-internal cuts".</summary>
        Cut,
        /// <summary>A user Call/Execute to another predicate in a callee body —
        /// the TRUE-G3 (nested inline) candidate population.</summary>
        HasCalls,
        /// <summary>Anything else: control constructs, backtrackable builtins,
        /// unrecognised clause ranges, non-whitelist ops.</summary>
        Shape,
    }

    /// <summary>ADR-031 G2 — true when <paramref name="callee"/> is a
    /// FAIL-DIRECT predicate: its whole execution provably creates NO engine
    /// choice point and every failure path is (in the inlined emission) a
    /// direct IL branch. Requirements per clause: frameless, or a frame whose
    /// <c>allocate</c> is the first real op and whose <c>deallocate</c>
    /// immediately precedes the terminator; body ops restricted to the
    /// non-CP whitelist (head unification / <c>=/2</c> family, integer
    /// arithmetic, register moves, deterministic non-meta builtins — NO user
    /// calls, NO cuts, NO control constructs); terminator <c>proceed</c> /
    /// <c>deallocate_proceed</c> / a self-tail <c>execute</c> (det tail
    /// recursion — the canonical list-walking validator). Clause dispatch is
    /// IGNORED (the inline emission is a sequential alternative chain, so the
    /// callee's own try/switch machinery — which WOULD push CPs — never runs).
    /// This is the bytecode-level counterpart of ADR-030's determinism proof,
    /// strengthened to "emits zero choice points". Capped (clauses ≤ 4, code ≤
    /// 512 bytes) to bound inline growth.</summary>
    internal static bool TryDescribeFailDirectCallee(
        CompiledPredicate callee, out List<FailDirectClause>? clauses)
        => TryDescribeFailDirectCallee(callee, out clauses, out _);

    /// <summary>ADR-031 G2 fail-direct caps — a callee over these bounds keeps
    /// its choice point. Prudence bounds (per-site inline growth + the linear
    /// alternative chain replacing indexed dispatch), NOT soundness bounds:
    /// raising them is safe, it just inlines more code and scans more
    /// alternatives per call. <see cref="CpFreeGuardStats.RejectCalleeCaps"/>
    /// counts the population a raise would admit.</summary>
    internal static int FailDirectMaxClauses { get; set; } = 4;
    internal static int FailDirectMaxBytes { get; set; } = 512;

    internal static bool TryDescribeFailDirectCallee(
        CompiledPredicate callee, out List<FailDirectClause>? clauses,
        out FailDirectReject reject)
    {
        clauses = null;
        reject = FailDirectReject.None;
        byte[] code = callee.BytecodeUnfused;
        if (callee.ClauseCount < 1 || callee.ClauseCount > FailDirectMaxClauses
            || code.Length > FailDirectMaxBytes)
        {
            reject = FailDirectReject.Caps;
            return false;
        }

        // Clause byte ranges, dispatch-skeleton-free.
        IReadOnlyList<(int Start, int End)> ranges;
        if (callee.ClauseCount == 1)
        {
            ranges = new[] { (0, code.Length) };
        }
        else if (TryDescribeTryMeElseChain(callee, null, out var chain) && chain is not null)
        {
            ranges = chain.Clauses.Select(c => (c.Start, c.End)).ToArray();
        }
        else if (IlIndexedDispatch.TryDescribe(callee, static (_, _) => true, out var idx)
                 && idx is not null)
        {
            ranges = idx.Clauses;
        }
        else
        {
            reject = FailDirectReject.Shape;
            return false;
        }

        var result = new List<FailDirectClause>(ranges.Count);
        foreach (var (start, end) in ranges)
        {
            bool framed = false, sawRealOp = false;
            int pc = start;
            int termPc = -1, cutPc = -1;
            bool selfTail = false, deallocProceed = false;
            while (pc < end)
            {
                var op = (Opcode)code[pc];
                if (op == Opcode.Meta) { pc += 6; continue; }
                if (op == Opcode.Proceed) { termPc = pc; break; }
                if (op == Opcode.DeallocateProceed)
                {
                    if (!framed) { reject = FailDirectReject.Shape; return false; }
                    termPc = pc; deallocProceed = true; break;
                }
                if (op == Opcode.Execute)
                {
                    int fid = FindCallSiteFunctorId(callee.CallSites, pc);
                    if (fid != callee.FunctorId)
                    {
                        reject = FailDirectReject.HasCalls;      // cross tail — G3 candidate
                        return false;
                    }
                    termPc = pc; selfTail = true; break;
                }
                switch (op)
                {
                    case Opcode.Call:
                    case Opcode.CallIl:
                    case Opcode.CallBytecode:
                    case Opcode.ExecuteIl:
                    case Opcode.ExecuteBytecode:
                        reject = FailDirectReject.HasCalls;      // G3 candidate
                        return false;
                    case Opcode.NeckCut:
                        // The callee-internal commit — record the FIRST one
                        // (selection is committed from there on; later cuts are
                        // flush-only no-ops the emit handles inline).
                        if (cutPc < 0) cutPc = pc;
                        break;
                    // A deep cut / its barrier plumbing implies a preceding
                    // call — impossible in this shape; reject defensively.
                    case Opcode.Cut:
                    case Opcode.GetLevel:
                    case Opcode.AllocateGetLevel:
                        reject = FailDirectReject.Cut;
                        return false;
                    case Opcode.Allocate:
                        if (sawRealOp || framed) { reject = FailDirectReject.Shape; return false; }
                        framed = true;
                        break;
                    case Opcode.Deallocate:
                        // Only as part of the tail sequence: deallocate must be
                        // immediately followed by the (self) execute.
                        if (!framed) { reject = FailDirectReject.Shape; return false; }
                        if ((Opcode)code[pc + OpcodeTable.Get(op).Size] != Opcode.Execute)
                        { reject = FailDirectReject.Shape; return false; }
                        break;
                    case Opcode.AIntCmp:
                    case Opcode.AIntBin:
                    case Opcode.GetAtom:
                    case Opcode.GetInteger:
                    case Opcode.GetNil:
                    case Opcode.GetFloat:
                    case Opcode.GetValueX:
                    case Opcode.GetStructure:
                    case Opcode.GetList:
                    case Opcode.GetListA1:
                    case Opcode.GetListA2:
                    case Opcode.GetVariableX:
                    case Opcode.UnifyValueX:
                    case Opcode.UnifyVariableX:
                    case Opcode.UnifyConstant:
                    case Opcode.UnifyInteger:
                    case Opcode.UnifyAtom:
                    case Opcode.UnifyNil:
                    case Opcode.UnifyVoid:
                    case Opcode.UnifyFloat:
                    case Opcode.UnifyBigInt:
                    case Opcode.UnifyStructure:
                    case Opcode.UnifyList:
                    case Opcode.PutValueX:
                    case Opcode.PutAtom:
                    case Opcode.PutInteger:
                    case Opcode.PutNil:
                    case Opcode.PutFloat:
                    case Opcode.PutVariableX:
                    case Opcode.PutStructureR:
                    case Opcode.PutListR:
                    case Opcode.PutStructure:
                    case Opcode.PutList:
                    case Opcode.PutPstr:
                        break;
                    case Opcode.GetVariableY:
                    case Opcode.GetValueY:
                    case Opcode.UnifyVariableY:
                    case Opcode.UnifyValueY:
                    case Opcode.PutValueY:
                    case Opcode.PutVariableY:
                        if (!framed) { reject = FailDirectReject.Shape; return false; }
                        break;
                    case Opcode.CallBuiltin:
                    {
                        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(
                            BytecodeIO.ReadInt32(code, pc + 1));
                        if (entry.IsCall || entry.IsDollarCall || entry.IsBacktrackable)
                        { reject = FailDirectReject.Shape; return false; }
                        break;
                    }
                    default:
                        reject = FailDirectReject.Shape;
                        return false;
                }
                sawRealOp = true;
                pc += OpcodeTable.Get(op).Size;
            }
            if (termPc < 0) { reject = FailDirectReject.Shape; return false; }
            result.Add(new FailDirectClause
            {
                Start = start, TermPc = termPc,
                SelfTail = selfTail, Framed = framed, DeallocProceed = deallocProceed,
                CutPc = cutPc,
            });
        }
        // SOUNDNESS — a self-tail recursion in a NON-LAST clause without a
        // preceding cut: if a deeper iteration fails, real backtracking returns
        // to THIS iteration's remaining alternatives, which the in-place loop
        // cannot do. Sound only when the recursive clause is the last (no
        // alternatives after it) or its cut committed the selection first.
        for (int i = 0; i < result.Count - 1; i++)
        {
            if (result[i].SelfTail && result[i].CutPc < 0)
            {
                reject = FailDirectReject.Shape;
                return false;
            }
        }
        clauses = result;
        return true;
    }

    /// <summary>ADR-031 G2 — inlines a fail-direct callee at a CP-free guard
    /// call site as a SEQUENTIAL alternative chain with an in-place self-tail
    /// loop. Clause i's failure branches to clause i+1 (restoring the callee's
    /// entry argument registers first — a partially-matched clause may have
    /// clobbered them via <c>unify_variable_x</c>/staging); the last clause's
    /// failure branches to <paramref name="outerFail"/> (the guard's restore
    /// stub). A framed clause's mid-body failure detours through a
    /// deallocate-then-fail stub. A self-tail <c>execute</c> becomes a branch
    /// back to the inlined entry (its staging + deallocate already ran inside
    /// the slice) with a throttled cancellation poll — but NO heap-GC safe
    /// point: a collection would move the heap under the enclosing guard's
    /// snapshot locals, so allocation during the walk grows the heap until the
    /// guard exits (same acceptance as tier B).</summary>
    private static void EmitFailDirectCalleeInline(
        Sigil.Emit<PredicateDelegate> emit, CompiledPredicate callee,
        List<FailDirectClause> fdClauses, Sigil.Label outerFail,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, string salt)
    {
        int arity = callee.Arity;
        var join = emit.DefineLabel($"fd_join{salt}");
        var entry = emit.DefineLabel($"fd_entry{salt}");
        var argSaves = new Sigil.Local[arity];
        for (int r = 0; r < arity; r++)
            argSaves[r] = emit.DeclareLocal<Cell>($"fd_a{r}{salt}");
        // Callee-entry trail/heap marks: a partially-matched clause may have
        // BOUND caller-visible terms (head unification with unbound arguments)
        // before failing — the next alternative must see them undone, exactly
        // as the clause choice point's restore would have done. (The enclosing
        // guard's snapshot covers the whole clause; these marks cover just the
        // callee, so guard bindings made BEFORE the call survive.)
        var mBt = emit.DeclareLocal<int>($"fd_bt{salt}");
        var mXt = emit.DeclareLocal<int>($"fd_xt{salt}");
        var mH = emit.DeclareLocal<int>($"fd_h{salt}");
        var mHb = emit.DeclareLocal<int>($"fd_hb{salt}");
        int k = fdClauses.Count;
        var altLabels = new Sigil.Label[k + 1];
        for (int i = 0; i < k; i++)
            altLabels[i] = emit.DefineLabel($"fd_alt{i}{salt}");
        altLabels[k] = outerFail;

        bool anySelfTail = fdClauses.Any(c => c.SelfTail);
        emit.MarkLabel(entry);
        if (anySelfTail)
        {
            // Cancellation poll at the loop head (throttled field read).
            emit.LoadArgument(0);
            emit.Call(EngineBacktrackSafePointMethod);
        }
        for (int r = 0; r < arity; r++)
        {
            emit.LoadArgument(0);
            emit.LoadConstant(r);
            emit.Call(EngineGetRegisterMethod);
            emit.StoreLocal(argSaves[r]);
        }
        emit.LoadArgument(0); emit.Call(EngineBindingTrailTopGetter); emit.StoreLocal(mBt);
        emit.LoadArgument(0); emit.Call(EngineExtraTrailTopGetter); emit.StoreLocal(mXt);
        emit.LoadArgument(0); emit.Call(EngineHeapTopGetter); emit.StoreLocal(mH);
        // NESTED HB raise: the guard's own staging creates fresh vars AFTER the
        // guard-level raise (put_variable_y outputs) — young w.r.t. the guard's
        // HB, so a callee binding them would go UNTRAILED and survive the
        // per-alternative untrail. Raising HB again to the CALLEE-entry heap
        // top makes every pre-callee term old; restored at the join.
        emit.LoadArgument(0); emit.Call(EngineBeginIlGuardMethod); emit.StoreLocal(mHb);

        byte[] code = callee.BytecodeUnfused;
        for (int i = 0; i < k; i++)
        {
            var c = fdClauses[i];
            emit.MarkLabel(altLabels[i]);
            if (i > 0)
            {
                // Undo the previous alternative's partial work: untrail to the
                // callee-entry marks (head-unify bindings!), reset the heap,
                // clear wakeups its bindings queued, then restore the entry
                // argument registers it may have clobbered. HB stays at the
                // RAISED callee boundary (mH) — the next alternative's bindings
                // must trail too.
                emit.LoadArgument(0);
                emit.LoadLocal(mBt); emit.LoadLocal(mXt); emit.LoadLocal(mH); emit.LoadLocal(mH);
                emit.Call(EngineFailIlGuardMethod);
                for (int r = 0; r < arity; r++)
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(r);
                    emit.LoadLocal(argSaves[r]);
                    emit.Call(EngineSetRegisterMethod);
                }
            }

            // Fail routing. Pre-cut: the next alternative (via a deallocating
            // stub when framed). Post-cut: clause selection is COMMITTED — the
            // callee fails outright (via its own deallocating stub when framed).
            Sigil.Label preCutFail = altLabels[i + 1];
            Sigil.Label? deallocFail = null;
            if (c.Framed)
            {
                deallocFail = emit.DefineLabel($"fd_df{i}{salt}");
                preCutFail = deallocFail;
            }

            if (c.CutPc >= 0)
            {
                // Slice 1 — up to the committing neck cut.
                EmitClauseBody(emit, code, c.Start, c.CutPc,
                    preCutFail, callee.CallSites, calleeMap: calleeMap,
                    suppressProceedReturn: true, localSalt: $"{salt}_c{i}a");
                // The cut: a goal boundary (flush pending wakeups; a failing
                // hook backtracks into the next alternative, pre-commit) — but
                // NO engine Cut call: a fail-direct callee pushed nothing.
                emit.LoadArgument(0);
                emit.Call(EngineFlushWakeupsForIlCutMethod);
                emit.BranchIfFalse(preCutFail);
                // Slice 2 — post-commit: failures exit the callee.
                Sigil.Label committedFail = outerFail;
                if (c.Framed)
                {
                    var df2 = emit.DefineLabel($"fd_dfc{i}{salt}");
                    committedFail = df2;
                    EmitClauseBody(emit, code, c.CutPc + 1, c.TermPc,
                        committedFail, callee.CallSites, calleeMap: calleeMap,
                        suppressProceedReturn: true, localSalt: $"{salt}_c{i}b");
                    EmitFailDirectTerminator(emit, c, entry, join);
                    emit.MarkLabel(df2);
                    emit.LoadArgument(0);
                    emit.Call(EngineDeallocateMethod);
                    emit.Branch(outerFail);
                }
                else
                {
                    EmitClauseBody(emit, code, c.CutPc + 1, c.TermPc,
                        committedFail, callee.CallSites, calleeMap: calleeMap,
                        suppressProceedReturn: true, localSalt: $"{salt}_c{i}b");
                    EmitFailDirectTerminator(emit, c, entry, join);
                }
            }
            else
            {
                EmitClauseBody(emit, code, c.Start, c.TermPc,
                    preCutFail, callee.CallSites, calleeMap: calleeMap,
                    suppressProceedReturn: true, localSalt: $"{salt}_c{i}");
                EmitFailDirectTerminator(emit, c, entry, join);
            }

            if (deallocFail is not null)
            {
                emit.MarkLabel(deallocFail);
                emit.LoadArgument(0);
                emit.Call(EngineDeallocateMethod);
                emit.Branch(altLabels[i + 1]);
            }
        }
        emit.MarkLabel(join);
        // Success: drop the nested HB raise back to the guard-level boundary.
        // (The failure exits skip this — the outer restore stub reinstates the
        // clause-entry HB itself.)
        emit.LoadArgument(0);
        emit.LoadLocal(mHb);
        emit.Call(EngineCommitIlGuardMethod);
    }

    /// <summary>The terminator of one inlined fail-direct clause: rejoin the
    /// guard (<c>proceed</c> / <c>deallocate_proceed</c>) or loop (self-tail).</summary>
    private static void EmitFailDirectTerminator(
        Sigil.Emit<PredicateDelegate> emit, FailDirectClause c,
        Sigil.Label entry, Sigil.Label join)
    {
        if (c.DeallocProceed)
        {
            emit.LoadArgument(0);
            emit.Call(EngineDeallocateMethod);
            emit.Branch(join);
        }
        else if (c.SelfTail)
        {
            emit.Branch(entry);          // staging + deallocate already in the slice
        }
        else
        {
            emit.Branch(join);           // proceed
        }
    }

    /// <summary>ADR-031 — emits one whole CP-free guard clause, replacing the
    /// entry <c>PushIlChoicePoint</c> + guard + <c>neck_cut</c> + body chain
    /// emission. <paramref name="emitSlice"/> abstracts the two call sites'
    /// differing <c>EmitClauseBody</c> parameter sets: it must emit the byte
    /// range <c>[start, end)</c> with the given fail label.
    ///
    /// <para><b>Tier A</b> (<paramref name="needsSnapshot"/> = false — pure
    /// comparisons): guard failure branches DIRECTLY to
    /// <paramref name="nextClauseLabel"/>; nothing to restore. <b>Tier B</b>
    /// (binding guard): clause entry snapshots the two trail tops + heap top in
    /// IL locals and <see cref="Engine.BeginIlGuard"/> raises HB so every guard
    /// binding is trailed; guard failure lands on a restore stub
    /// (<see cref="Engine.FailIlGuard"/> — untrail, heap reset, HB restore,
    /// wakeup clear) before branching to the next clause.</para>
    ///
    /// <para><b>Commit</b> (both tiers): fast path (no pending attribute
    /// wakeups — every non-attvar program) is just <c>engine.NeckCut()</c>, a
    /// runtime no-op unless self-tail-loop body CPs exist (where it must prune
    /// exactly as today), plus the HB restore for tier B. Rare path: wakeups
    /// pend at the cut and a failing hook must have a clause choice point to
    /// backtrack into — the SKIPPED CP is materialised lazily here (tier B via
    /// <see cref="Engine.PushIlChoicePointWithMarks"/> carrying the CLAUSE-ENTRY
    /// marks, so backtracking into it undoes the guard's bindings), then flush
    /// + cut run exactly as the standard emit.</para></summary>
    private static void EmitCpFreeGuardClause(
        Sigil.Emit<PredicateDelegate> emit,
        Action<int, int, Sigil.Label> emitSlice,
        byte[] code, int clauseStart, int clauseEnd, CpFreeGuardInfo g,
        Sigil.Label nextClauseLabel, Sigil.Label failLabel,
        SelfDelegateEmitter self, int lazyCpCursor, int arity, string salt,
        Action? markDeadCursors = null)
    {
        Sigil.Local? bt = null, xt = null, h = null, hb = null, ee = null;
        Sigil.Local[]? regs = null;
        Sigil.Label guardFail = nextClauseLabel;
        bool needsStub = g.NeedsSnapshot || g.NeedsRegSave || g.Framed;
        if (g.NeedsRegSave && arity > 0)
        {
            regs = new Sigil.Local[arity];
            for (int r = 0; r < arity; r++)
            {
                regs[r] = emit.DeclareLocal<Cell>($"cf_r{r}{salt}");
                emit.LoadArgument(0);
                emit.LoadConstant(r);
                emit.Call(EngineGetRegisterMethod);
                emit.StoreLocal(regs[r]);
            }
        }
        if (g.NeedsSnapshot)
        {
            bt = emit.DeclareLocal<int>($"cf_bt{salt}");
            xt = emit.DeclareLocal<int>($"cf_xt{salt}");
            h = emit.DeclareLocal<int>($"cf_h{salt}");
            hb = emit.DeclareLocal<int>($"cf_hb{salt}");
            ee = emit.DeclareLocal<int>($"cf_e{salt}");
            emit.LoadArgument(0); emit.Call(EngineBindingTrailTopGetter); emit.StoreLocal(bt);
            emit.LoadArgument(0); emit.Call(EngineExtraTrailTopGetter); emit.StoreLocal(xt);
            emit.LoadArgument(0); emit.Call(EngineHeapTopGetter); emit.StoreLocal(h);
            emit.LoadArgument(0); emit.Call(EngineEGetter); emit.StoreLocal(ee);
            emit.LoadArgument(0); emit.Call(EngineBeginIlGuardMethod); emit.StoreLocal(hb);
        }
        if (needsStub)
            guardFail = emit.DefineLabel($"cf_restore{salt}");

        emitSlice(clauseStart, g.CutPc, guardFail);         // head/guard prefix

        // The commit's cut: neck_cut, or the framed deep cut to Y[slot].
        void EmitTheCut()
        {
            if (g.DeepCut)
            {
                int slot = BytecodeIO.ReadInt32(code, g.CutPc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineCutToLevelMethod);
            }
            else
            {
                emit.LoadArgument(0);
                emit.Call(EngineNeckCutMethod);
            }
        }

        // ---- Commit (replaces the cut opcode). ----
        var rare = emit.DefineLabel($"cf_rare{salt}");
        var after = emit.DefineLabel($"cf_after{salt}");
        emit.LoadArgument(0);
        emit.Call(EngineHasPendingWakeupsGetter);
        emit.BranchIfTrue(rare);
        EmitTheCut();
        if (g.NeedsSnapshot)
        { emit.LoadArgument(0); emit.LoadLocal(hb!); emit.Call(EngineCommitIlGuardMethod); }
        emit.Branch(after);
        emit.MarkLabel(rare);
        emit.LoadArgument(0);
        self(emit);
        emit.LoadConstant(lazyCpCursor);
        emit.LoadConstant(arity);
        if (g.NeedsSnapshot)
        {
            emit.LoadLocal(bt!); emit.LoadLocal(xt!); emit.LoadLocal(h!);
            emit.LoadLocal(hb!); emit.LoadLocal(ee!);
            emit.Call(EnginePushIlCpWithMarksMethod);
        }
        else
        {
            emit.Call(EnginePushIlCpMethod);
        }
        emit.LoadArgument(0);
        emit.Call(EngineFlushWakeupsForIlCutMethod);
        emit.BranchIfFalse(failLabel);
        EmitTheCut();
        if (g.NeedsSnapshot)
        { emit.LoadArgument(0); emit.LoadLocal(hb!); emit.Call(EngineCommitIlGuardMethod); }
        emit.MarkLabel(after);

        emitSlice(g.CutPc + OpcodeTable.Get((Opcode)code[g.CutPc]).Size,
            clauseEnd, failLabel);                          // post-commit body

        // Region mode: the plan allocated forward-resume cursors for the guard's
        // (now inlined) Call sites; their labels must be marked (dead — no
        // resume marker is ever set for an inlined call).
        markDeadCursors?.Invoke();

        if (needsStub)
        {
            // Guard-fail restore stub: undo the guard, then fall to the next
            // clause. Reached only by the guard prefix's fail branches.
            emit.MarkLabel(guardFail);
            if (g.Framed)
            {
                emit.LoadArgument(0);
                emit.Call(EngineDeallocateMethod);
            }
            if (g.NeedsSnapshot)
            {
                emit.LoadArgument(0);
                emit.LoadLocal(bt!); emit.LoadLocal(xt!); emit.LoadLocal(h!); emit.LoadLocal(hb!);
                emit.Call(EngineFailIlGuardMethod);
            }
            if (regs is not null)
            {
                for (int r = 0; r < arity; r++)
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(r);
                    emit.LoadLocal(regs[r]);
                    emit.Call(EngineSetRegisterMethod);
                }
            }
            emit.Branch(nextClauseLabel);
        }
    }

    private static void EmitTryMeElseChainBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        TryMeElseChainInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        SelfDelegateEmitter emitSelf,
        System.Type selfDelType)
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
        int totalCallSites = CountNonTailCallOpcodes(predicate.BytecodeUnfused);
        var resumeLabels = new Sigil.Label[totalCallSites];
        for (int j = 0; j < totalCallSites; j++)
            resumeLabels[j] = emit.DefineLabel($"call_resume_{j + 1}");

        _emitOwnerFid = predicate.FunctorId;

        // Chunk 426 (CSE, mirrors the region Stage-11 hoist): every clause's
        // PushIlChoicePoint reloads the SAME self-delegate — a per-push holder
        // dictionary probe on the runtime path. Hoist it to ONE local ahead of
        // the cursor switch (which dominates every clause entry, fresh AND
        // backtrack re-entries); gate on ≥2 pushes so the load+store only ever
        // shrinks the per-invocation work. N clauses push N−1 CPs.
        SelfDelegateEmitter effectiveSelf = emitSelf;
        if (N - 1 >= 2)
        {
            var selfDelLoc = emit.DeclareLocal(selfDelType, "cselfdel");
            emitSelf(emit);
            emit.StoreLocal(selfDelLoc);
            effectiveSelf = e => e.LoadLocal(selfDelLoc);
        }

        // Top-level cursor dispatch. Chunk 426: one O(1) jump table (IL
        // `switch`) over the dense cursor space — 0..N-1 → clause entry;
        // N..N+M-1 → call-site resume — replacing the linear compare chain
        // (resume compares + one compare interleaved per clause) that every
        // invocation used to walk. An out-of-range cursor falls through to
        // fail, exactly as the old chain's final fall-through did.
        var clauseLabels = new Sigil.Label[N];
        for (int i = 0; i < N; i++)
            clauseLabels[i] = emit.DefineLabel($"clause_entry_{i}");
        var cursorLabels = new Sigil.Label[N + totalCallSites];
        for (int i = 0; i < N; i++) cursorLabels[i] = clauseLabels[i];
        for (int j = 0; j < totalCallSites; j++)
            cursorLabels[N + j] = resumeLabels[j];
        emit.LoadArgument(1);
        emit.Switch(cursorLabels);
        // cursor out of [0..N+M-1] (unreachable) → fail.
        emit.Branch(failLabel);

        // Self-tail-recursion → in-method loop (chunk 350): a self Execute in
        // any clause body resets the cursor to 0 and branches here — clause
        // 0's entry (a fresh self-call must try the first clause, not re-enter
        // the clause it was called from).
        var selfEntry = emit.DefineLabel("chain_self_entry");
        emit.MarkLabel(selfEntry);

        int siteCounter = 0;
        for (int i = 0; i < clauses.Count; i++)
        {
            emit.MarkLabel(clauseLabels[i]);

            // ADR-031 — a non-last clause whose pre-cut prefix is a CP-free
            // guard skips its entry choice point: guard failure branches to the
            // next clause (directly, or via the restore stub), and the commit
            // materialises the CP lazily only in the rare pending-wakeups case
            // (see EmitCpFreeGuardClause). forceLeafRuleInline: a tier-G guard
            // Call MUST take the chunk-69 inline path (its failure is then a
            // direct branch to the guard's fail label).
            if (CpFreeGuardCommit && i < clauses.Count - 1
                && TryGetCpFreeGuard(
                    predicate.BytecodeUnfused, clauses[i].Start, clauses[i].End,
                    predicate.Arity, calleeMap, predicate.CallSites, out var ginfo))
            {
                EmitCpFreeGuardClause(emit,
                    (s, e, fl) => EmitClauseBody(
                        emit, predicate.BytecodeUnfused, s, e, fl, predicate.CallSites,
                        callSiteIndexCounter: () => ++siteCounter,
                        resumeLabels: resumeLabels,
                        emitSelfDelegate: effectiveSelf,
                        calleeMap: calleeMap,
                        cursorBase: N,
                        selfFunctorId: predicate.FunctorId,
                        selfTailLabel: selfEntry,
                        resetCursorBeforeSelfTail: true,
                        forceLeafRuleInline: true),
                    predicate.BytecodeUnfused, clauses[i].Start, clauses[i].End, ginfo,
                    clauseLabels[i + 1], failLabel,
                    effectiveSelf, i + 1, predicate.Arity, salt: $"_c{i}");
                continue;
            }

            // If there's a later clause, push an IL CP for it before
            // running this clause's body.
            if (i < clauses.Count - 1)
            {
                emit.LoadArgument(0);                      // engine
                effectiveSelf(emit);                       // → PredicateDelegate (chunk-426 hoisted local)
                emit.LoadConstant(i + 1);                  // next cursor
                emit.LoadConstant(predicate.Arity);
                emit.Call(EnginePushIlCpMethod);
            }

            // Emit the clause body. The shared siteCounter assigns a
            // unique 1-based ordinal per non-tail Call site; the
            // resume cursor in the emitted IL is cursorBase + ordinal
            // - 1 = N + (ordinal - 1).
            EmitClauseBody(emit, predicate.BytecodeUnfused, clauses[i].Start, clauses[i].End,
                failLabel, predicate.CallSites,
                callSiteIndexCounter: () => ++siteCounter,
                resumeLabels: resumeLabels,
                emitSelfDelegate: effectiveSelf,
                calleeMap: calleeMap,
                cursorBase: N,
                selfFunctorId: predicate.FunctorId,
                selfTailLabel: selfEntry,
                resetCursorBeforeSelfTail: true);
        }

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
        // Chunk 433 — memoized per predicate (see IlShapeMemo): the
        // structural walk runs once; the calleeMap-dependent Call check is
        // re-applied per call.
        if (predicate.IlIndexedAtomShapeMemo is not IlShapeMemo memo)
        {
            var callFids = new List<int>();
            TryDescribeIndexedAtomPredicateStructural(predicate, callFids, out var raw);
            memo = new IlShapeMemo(raw, callFids);
            predicate.IlIndexedAtomShapeMemo = memo;
        }
        return memo.Resolve(calleeMap, out info);
    }

    private static bool TryDescribeIndexedAtomPredicateStructural(
        CompiledPredicate predicate, List<int> callFids,
        out IndexedAtomInfo? info)
    {
        info = null;
        if (predicate.Arity != 1) return false;
        byte[] code = predicate.BytecodeUnfused;
        if (code.Length < 17) return false;
        if ((Opcode)code[0] != Opcode.SwitchOnTerm) return false;
        // ADR-025 — same linear-scan caveat as TryDescribeSwitchedChain.
        if (ContainsInlineIteOpcode(code)) return false;

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
                    if (!IsClauseBodyOpcodeStructural(op, predicate, q, callFids)) return false;
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
        EmitIndexedAtomBody(emit, predicate, info, emitSelf,
            typeof(Func<Engine, int, bool>),   // runtime path: SelfFromHolder → Func
            profileKey, groundOrder, calleeMap);

        var del = FinishEmit(emit,
            $"compile fid={predicate.FunctorId} {FidName(predicate.FunctorId)}/{predicate.Arity} clauses={predicate.ClauseCount}");
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
        System.Type selfDelType,
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
                predicate.BytecodeUnfused, c.BodyStart, c.BodyEnd);
        var callResumeLabels = new Sigil.Label[totalCallSites];
        for (int j = 0; j < totalCallSites; j++)
            callResumeLabels[j] = emit.DefineLabel($"call_resume_{j + 1}");

        // Chunk 426 (CSE, mirrors the region Stage-11 hoist): every var-path
        // clause's PushIlChoicePoint reloads the SAME self-delegate — a
        // per-push holder dictionary probe on the runtime path. Hoist it to
        // ONE local ahead of the cursor switch (which dominates every
        // varEnter label, fresh AND backtrack re-entries); gate on ≥2 pushes
        // so the load+store only ever shrinks the per-invocation work.
        SelfDelegateEmitter effectiveSelf = emitSelf;
        if (n - 1 >= 2)
        {
            var selfDelLoc = emit.DeclareLocal(selfDelType, "aselfdel");
            emitSelf(emit);
            emit.StoreLocal(selfDelLoc);
            effectiveSelf = e => e.LoadLocal(selfDelLoc);
        }

        // Top-level cursor dispatch. Chunk 426: one O(1) jump table (IL
        // `switch`) over the dense cursor space — 0 → tag dispatch; 1..n-1 →
        // varEnter[cursor]; n..n+M-1 → call-site resume — replacing the
        // linear compare chain that tested cursor==0 LAST, making the
        // fresh-call path (by far the most common) pay the whole chain. An
        // out-of-range cursor falls through to fail, exactly as the old
        // chain's explicit default did.
        var cursorZero = emit.DefineLabel("cursor_zero");
        var cursorLabels = new Sigil.Label[n + totalCallSites];
        cursorLabels[0] = cursorZero;
        for (int i = 1; i < n; i++) cursorLabels[i] = varEnterLabels[i];
        for (int j = 0; j < totalCallSites; j++)
            cursorLabels[n + j] = callResumeLabels[j];
        emit.LoadArgument(1);
        emit.Switch(cursorLabels);
        emit.Branch(failLabel);     // cursor out of range (unreachable) → fail
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
                effectiveSelf(emit);                   // → PredicateDelegate (chunk-426 hoisted local)
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
            EmitClauseBody(emit, predicate.BytecodeUnfused,
                clauses[i].BodyStart, clauses[i].BodyEnd,
                failLabel, predicate.CallSites,
                callSiteIndexCounter: () => ++siteCounter,
                resumeLabels: callResumeLabels,
                emitSelfDelegate: effectiveSelf,
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

    /// <summary>Emits IL that leaves a <see cref="PredicateDelegate"/> on
    /// the evaluation stack — the running predicate's own delegate, used
    /// as the callback target for <c>engine.PushIlChoicePoint</c>. Two
    /// implementations, both a direct <c>ldsfld / ldc / ldelem.ref</c> slot
    /// load (Phase 33 IL round 2 — the DynamicMethod path used to be
    /// <c>call IndexedDelegateHolder.Get</c>, a ConcurrentDictionary probe
    /// per multi-clause region invocation, ~3% of engine time on the Tier-1
    /// profile):
    /// <list type="bullet">
    /// <item>DynamicMethod: the process-wide <see cref="IndexedDelegateHolder.Slots"/>
    /// array, indexed by the registration key.</item>
    /// <item>Persisted assembly: a static array field on the emitted type,
    /// resolved at load time.</item>
    /// </list></summary>
    internal delegate void SelfDelegateEmitter(Sigil.Emit<PredicateDelegate> emit);

    internal static SelfDelegateEmitter SelfFromHolder(int holderKey) =>
        e =>
        {
            e.LoadField(IndexedDelegateHolder.SlotsField);
            e.LoadConstant(holderKey);
            e.LoadElement<Func<Engine, int, bool>>();
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
    /// runtime the slot array resolves it to the stored delegate. The
    /// table is process-wide but write-once-per-key.</summary>
    internal static class IndexedDelegateHolder
    {
        // Phase 33 IL round 2 — the store is a plain slot ARRAY indexed by
        // the (sequential, RegistrationLock-serialised) holder key, and
        // SelfFromHolder emits a direct `ldsfld / ldc / ldelem.ref` instead
        // of a call — the Tier-1 profile showed the previous
        // ConcurrentDictionary.TryGetValue as ~3% of engine time, one
        // hash+bucket probe per multi-clause region invocation (it had
        // replaced a contended `lock` in chunk 232; this removes the probe
        // altogether). Publication safety: Register runs under
        // RegistrationLock; a grow copies the old entries and stores the
        // new delegate into the NEW array BEFORE Volatile.Write publishes
        // it, so any array version a reader can observe after delegate X
        // escaped (always through a fenced channel — the compile-result
        // queue or the promotion tables) already contains X's slot.
        public static Func<Engine, int, bool>?[] Slots = new Func<Engine, int, bool>?[256];
        private static readonly object _lock = new();

        internal static readonly System.Reflection.FieldInfo SlotsField =
            typeof(IndexedDelegateHolder).GetField(nameof(Slots))!;

        /// <summary>The lock the IL emission takes around the
        /// emit-and-register sequence so two concurrent compiles don't
        /// race on <c>_nextHolderKey</c>.</summary>
        public static object RegistrationLock => _lock;

        public static void Register(int key, PredicateDelegate del)
        {
            lock (_lock)
            {
                var wrapped = new Func<Engine, int, bool>(del);
                var arr = Slots;
                if (key >= arr.Length)
                {
                    var grown = new Func<Engine, int, bool>?[System.Math.Max(arr.Length * 2, key + 1)];
                    System.Array.Copy(arr, grown, arr.Length);
                    grown[key] = wrapped;
                    System.Threading.Volatile.Write(ref Slots, grown);
                }
                else
                {
                    arr[key] = wrapped;
                }
            }
        }

        public static Func<Engine, int, bool> Get(int key) => Slots[key]!;
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
            // Per-engine scratch (chunk 416): consumed into registers below,
            // before any recursion or builtin can re-enter.
            int extraCount = callArity - 1;
            Cell[] extra = extraCount <= 0
                ? System.Array.Empty<Cell>()
                : extraCount <= engine.MetaExtraScratch.Length
                    ? engine.MetaExtraScratch
                    : new Cell[extraCount];
            for (int i = 0; i < extraCount; i++)
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

            int totalArity = goalArity + extraCount;
            for (int i = 0; i < goalArity; i++)
                engine.SetRegister(i, engine.GetHeap(argBase + i));
            for (int i = 0; i < extraCount; i++)
                engine.SetRegister(goalArity + i, extra[i]);

            // Chunk 416 — shared meta-call route cache (see MetaRoute.cs).
            // Same cache the bytecode interpreter's DispatchCall fills; each
            // dispatcher executes a cached kind exactly as its own slow path.
            var addresses = engine.CurrentFunctorAddresses;
            var cache = engine.MetaRouteCache;
            if (cache is null || !ReferenceEquals(engine.MetaRouteCacheStamp, addresses))
            {
                cache = engine.MetaRouteCache =
                    new System.Collections.Generic.Dictionary<long, MetaRoute>();
                engine.MetaRouteCacheStamp = addresses;
            }
            bool routeCacheable = (uint)totalArity <= 0xFFFF;
            long routeKey = ((long)atomId << 16) | (uint)totalArity;
            if (routeCacheable && cache.TryGetValue(routeKey, out var route))
            {
                switch (route.Kind)
                {
                    case MetaRouteKind.Cut:
                        engine.Cut(cutBarrier);
                        return SyncSuccess;
                    case MetaRouteKind.True:
                        return SyncSuccess;
                    case MetaRouteKind.Fail:
                        return SyncFail;
                    case MetaRouteKind.CallRecurse:
                        return Dispatch(engine,
                            Shumway.Builtins.BuiltinsRegistry.GetById(route.Arg).Arity,
                            engine.B);
                    case MetaRouteKind.DollarCall:
                        return Dispatch(engine, 1,
                            (int)DerefCell(engine, engine.GetRegister(1)).AsInt);
                    case MetaRouteKind.Builtin:
                        return InvokeBuiltinGoal(engine, route.Arg);
                    case MetaRouteKind.BarrierHelperJump:
                        engine.SetRegister(2, Cell.Int(cutBarrier));
                        engine.SetB0(cutBarrier);
                        return route.Arg;
                    case MetaRouteKind.Jump:
                        engine.SetB0(cutBarrier);
                        return route.Arg;
                }
            }

            int functorId = FunctorTable.Intern(atomId, totalArity);
            // Chunk-88 control-construct routing — `!` inside the
            // runtime goal commits to the call's barrier via the
            // $call_* helpers' arity-3 form (X[2] carries the barrier).
            var userKind = MetaRouteKind.Jump;
            if (functorId == ConjFid)
            {
                engine.SetRegister(2, Cell.Int(cutBarrier));
                functorId = CallConjFid;
                userKind = MetaRouteKind.BarrierHelperJump;
            }
            else if (functorId == DisjFid)
            {
                engine.SetRegister(2, Cell.Int(cutBarrier));
                functorId = CallDisjFid;
                userKind = MetaRouteKind.BarrierHelperJump;
            }
            else if (functorId == ArrowFid)
            {
                engine.SetRegister(2, Cell.Int(cutBarrier));
                functorId = CallArrowFid;
                userKind = MetaRouteKind.BarrierHelperJump;
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
                if (routeCacheable)
                    cache[routeKey] = new MetaRoute(MetaRouteKind.Cut, 0);
                engine.Cut(cutBarrier);
                return SyncSuccess;
            }
            if (functorId == TrueFid)
            {
                if (routeCacheable)
                    cache[routeKey] = new MetaRoute(MetaRouteKind.True, 0);
                return SyncSuccess;
            }
            if (functorId == FailFid)
            {
                if (routeCacheable)
                    cache[routeKey] = new MetaRoute(MetaRouteKind.Fail, 0);
                return SyncFail;
            }

            // Builtin-as-goal. The recursion case (call(call(...))) is
            // handled by re-entering Dispatch with the recovered arity
            // — the inner call's X[0] already holds its own inner goal.
            if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int builtinId))
            {
                var builtin = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                if (builtin.IsCall)
                {
                    // call(call(...)) — inner call's arity is the
                    // builtin's arity, barrier resets to engine.B
                    // (a fresh call boundary).
                    if (routeCacheable)
                        cache[routeKey] = new MetaRoute(MetaRouteKind.CallRecurse, builtinId);
                    return Dispatch(engine, builtin.Arity, engine.B);
                }
                if (builtin.IsDollarCall)
                {
                    if (routeCacheable)
                        cache[routeKey] = new MetaRoute(MetaRouteKind.DollarCall, builtinId);
                    int innerBarrier = (int)DerefCell(engine, engine.GetRegister(1)).AsInt;
                    return Dispatch(engine, 1, innerBarrier);
                }
                if (routeCacheable)
                    cache[routeKey] = new MetaRoute(MetaRouteKind.Builtin, builtinId);
                return InvokeBuiltinGoal(engine, builtinId);
            }

            // User predicate. Set the cut barrier the call's `!` will
            // commit to, then return the dispatch address — the IL
            // caller threads Cp = resume_marker, Pc = target,
            // IlTailCallPending = true.
            engine.SetB0(cutBarrier);
            if (addresses is null
                || !addresses.TryGetValue(functorId, out int address))
            {
                // Chunk 417: honour the `unknown` flag (throws on error).
                if (UnknownProcedure.Fails(engine, functorId))
                    return SyncFail;
                throw PrologRuntimeException.UndefinedProcedure(functorId);   // unreachable
            }
            if (routeCacheable)
                cache[routeKey] = new MetaRoute(userKind, address);
            return address;
        }

        /// <summary>Invokes a builtin reached as a runtime meta-call goal
        /// (chunk 416 — shared by the slow path and the cached
        /// Builtin route).</summary>
        private static int InvokeBuiltinGoal(Engine engine, int builtinId)
        {
            var builtin = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
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
