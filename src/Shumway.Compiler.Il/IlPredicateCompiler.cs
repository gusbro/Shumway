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
/// <para>Supported shapes:</para>
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
public sealed partial class IlPredicateCompiler
{
    /// <summary>when <c>true</c>, every WAM opcode the IL
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
    /// size (measured: a 13 KB predicate took ~13 s with
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
    /// 1280-clause benchmark <c>PatchBranches</c> +
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

    /// <summary>env-gated shape diagnostics, stripped from normal
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

    /// <summary>per-call output of <see cref="EmitPersistedMethod"/>: when
    /// the method compiled as a REGION, the (memberFunctorName, arity, entryCursor)
    /// table of its non-root members (the <see cref="RegionCursorKind.MemberEntry"/>
    /// cursors); null for a non-region method. <see cref="PersistedIlBuilder"/> persists
    /// it per entry so LoadBundle can alias a stripped member's functor to
    /// <c>EncodeResumeMarker(rootFid, entryCursor)</c>.</summary>
    internal List<(string Name, int Arity, int Cursor)>? LastRegionMemberCursors;

    // Sigil label names must be unique per METHOD,
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
        typeof(Activation).GetMethod(
            nameof(Activation.UnifyRegisterWithCell),
            new[] { typeof(int), typeof(Cell) })!;
    private static readonly MethodInfo EngineUnifyRegistersMethod =
        typeof(Activation).GetMethod(
            nameof(Activation.UnifyRegisters),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo EngineGetRegisterMethod =
        typeof(Activation).GetMethod(nameof(Activation.GetRegister), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineGetHeapMethod =
        typeof(Activation).GetMethod(nameof(Activation.GetHeap), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineDerefMethod =
        typeof(Activation).GetMethod(nameof(Activation.Deref), new[] { typeof(int) })!;
    private static readonly MethodInfo EnginePushIlCpMethod =
        typeof(Activation).GetMethod(
            nameof(Activation.PushIlChoicePoint),
            new[] { typeof(Func<Activation, int, bool>), typeof(int), typeof(int) })!;
    // PGO: instrumented IL calls this on each clause success.
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
        typeof(Activation).GetMethod(nameof(Activation.SetRegister), new[] { typeof(int), typeof(Cell) })!;
    // Float literals (get_float / put_float). MakeFloat allocates the 2-cell
    // heap float and returns the header index; Cell.Ref wraps it so the value
    // unifies / binds exactly like the interpreter's float path. The float VALUE
    // is baked as an ldc.r8 constant (resolved from the predicate's pool at emit
    // time), so it is process-independent — no Phase-17 patch needed for persist.
    private static readonly MethodInfo EngineMakeFloatMethod =
        typeof(Activation).GetMethod(nameof(Activation.MakeFloat), new[] { typeof(double) })!;
    // ADR-018 — arithmetic instruction set runtime helpers (Shumway.Builtins.
    // ArithEvalStack). The Tier-1 emit calls these statics directly, so the
    // a_eval_* opcodes run the same eval-stack code as the Tier-0 interpreter.
    private static readonly MethodInfo ArithPushIntMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.PushInt), new[] { typeof(long) })!;
    private static readonly MethodInfo ArithPushRegMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.PushReg), new[] { typeof(Activation), typeof(int) })!;
    private static readonly MethodInfo ArithPushYMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.PushY), new[] { typeof(Activation), typeof(int) })!;
    private static readonly MethodInfo ArithBinMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.Bin), new[] { typeof(int) })!;
    private static readonly MethodInfo ArithUnMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.Un), new[] { typeof(int) })!;
    private static readonly MethodInfo ArithIsRegMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.IsReg), new[] { typeof(Activation), typeof(int) })!;
    private static readonly MethodInfo ArithIsPermMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.IsPerm), new[] { typeof(Activation), typeof(int) })!;
    private static readonly MethodInfo ArithSetRegMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.SetReg), new[] { typeof(Activation), typeof(int) })!;
    private static readonly MethodInfo ArithSetPermMethod =
        typeof(Shumway.Builtins.ArithEvalStack).GetMethod(
            nameof(Shumway.Builtins.ArithEvalStack.SetPerm), new[] { typeof(Activation), typeof(int) })!;
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
        typeof(Activation).GetMethod(nameof(Activation.GetY), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineSetYMethod =
        typeof(Activation).GetMethod(nameof(Activation.SetY), new[] { typeof(int), typeof(Cell) })!;
    private static readonly MethodInfo EngineAllocateMethod =
        typeof(Activation).GetMethod(nameof(Activation.Allocate), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineDeallocateMethod =
        typeof(Activation).GetMethod(nameof(Activation.Deallocate), Type.EmptyTypes)!;
    private static readonly MethodInfo EngineNeckCutMethod =
        typeof(Activation).GetMethod(nameof(Activation.NeckCut), Type.EmptyTypes)!;
    // ADR-034 — the clause-entry staleness test for inlined dynamic snapshots.
    private static readonly MethodInfo EngineIsDynMutatedMethod =
        typeof(Activation).GetMethod(nameof(Activation.IsDynMutated), new[] { typeof(int) })!;
    // ADR-031 rare path — patch the lazy CP's saved args back to clause entry.
    private static readonly MethodInfo EngineSetTopCpArgRegisterMethod =
        typeof(Activation).GetMethod(nameof(Activation.SetTopCpArgRegister),
            new[] { typeof(int), typeof(Cell) })!;
    // deep cut (get_level + cut). GetLevel stashes the
    // procedure-entry barrier (_b0) into a Y slot; CutToLevel reads it
    // back and commits. Both are plain engine calls — the CP / _b0
    // infrastructure is identical to Tier-0 (B0 set at entry by the
    // caller's Call/Execute, saved per-CP in CpB0Offset, IL clause CPs are
    // real engine CPs that Cut removes).
    private static readonly MethodInfo EngineGetLevelMethod =
        typeof(Activation).GetMethod(nameof(Activation.GetLevel), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineCutToLevelMethod =
        typeof(Activation).GetMethod(nameof(Activation.CutToLevel), new[] { typeof(int) })!;
    // a cut is a goal boundary, so pending attribute wakeups must
    // run before the IL-emitted cut commits (the IL counterpart of the
    // flush-before-cut). Returns false when a wakeup failed, which
    // the emit turns into a branch to the clause fail label. Fast-returns true
    // with a single field read when nothing is queued, so non-attvar programs
    // pay essentially nothing per cut.
    private static readonly MethodInfo EngineFlushWakeupsForIlCutMethod =
        typeof(Activation).GetMethod(nameof(Activation.FlushWakeupsForIlCut), Type.EmptyTypes)!;
    // ADR-031 — CP-free guard commit: the fast-path check that lets the
    // emitted commit skip materialising the clause choice point entirely
    // (see EmitCpFreeGuardCommit).
    private static readonly MethodInfo EngineHasPendingWakeupsGetter =
        typeof(Activation).GetProperty(nameof(Activation.HasPendingWakeups))!.GetGetMethod()!;
    // ADR-031 case B — the binding-guard snapshot/restore surface.
    private static readonly MethodInfo EngineBindingTrailTopGetter =
        typeof(Activation).GetProperty(nameof(Activation.BindingTrailTop))!.GetGetMethod()!;
    private static readonly MethodInfo EngineExtraTrailTopGetter =
        typeof(Activation).GetProperty(nameof(Activation.ExtraTrailTop))!.GetGetMethod()!;
    private static readonly MethodInfo EngineHeapTopGetter =
        typeof(Activation).GetProperty(nameof(Activation.HeapTop))!.GetGetMethod()!;
    private static readonly MethodInfo EngineBeginIlGuardMethod =
        typeof(Activation).GetMethod(nameof(Activation.BeginIlGuard), Type.EmptyTypes)!;
    private static readonly MethodInfo EngineCommitIlGuardMethod =
        typeof(Activation).GetMethod(nameof(Activation.CommitIlGuard), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineFailIlGuardMethod =
        typeof(Activation).GetMethod(nameof(Activation.FailIlGuard),
            new[] { typeof(int), typeof(int), typeof(int), typeof(int) })!;
    private static readonly MethodInfo EnginePushIlCpWithMarksMethod =
        typeof(Activation).GetMethod(nameof(Activation.PushIlChoicePointWithMarks),
            new[] { typeof(Func<Activation, int, bool>), typeof(int), typeof(int),
                    typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) })!;
    // ADR-031 G2 — the counter-throttled cancellation poll (NO heap GC: a GC
    // would move the heap under the guard's snapshot locals) emitted at the
    // back-edge of an inlined fail-direct callee's self-tail loop.
    private static readonly MethodInfo EngineBacktrackSafePointMethod =
        typeof(Activation).GetMethod(nameof(Activation.BacktrackSafePoint), Type.EmptyTypes)!;
    // ADR-033 — the guard continuation stack (shared fail-direct callee copies).
    private static readonly MethodInfo EnginePushGuardContMethod =
        typeof(Activation).GetMethod(nameof(Activation.PushGuardCont), new[] { typeof(int) })!;
    private static readonly MethodInfo EnginePopGuardContOkMethod =
        typeof(Activation).GetMethod(nameof(Activation.PopGuardContOk), Type.EmptyTypes)!;
    private static readonly MethodInfo EnginePopGuardContFailMethod =
        typeof(Activation).GetMethod(nameof(Activation.PopGuardContFail), Type.EmptyTypes)!;
    // indexed-dispatch entry resolver (mirrors the WAM switch
    // cascade, returns the entry chain-node cursor). Keyed by functor id
    // so the same IL works under runtime promotion AND a persisted bundle
    // loaded in a fresh process — the functor id is name-relative via
    // EmitFunctorId, and the resolver builds the dispatch model
    // lazily from the engine's linked code on first call.
    private static readonly MethodInfo IlIndexedDispatchResolveByFidMethod =
        typeof(IlIndexedDispatch).GetMethod(nameof(IlIndexedDispatch.ResolveEntryByFunctorId))!;
    // ADR-027 — inline sub-argument walk for the compiled index resolver.
    private static readonly MethodInfo IlWalkSubOrMissMethod =
        typeof(IlIndexedDispatch).GetMethod(nameof(IlIndexedDispatch.WalkSubOrMiss))!;
    // setter for engine.BuiltinReturnPc. The IL emit pre-sets
    // this to a resume marker before invoking a backtrackable builtin, so
    // the builtin's CP resume re-enters the IL caller correctly.
    private static readonly MethodInfo EngineBuiltinReturnPcSetter =
        typeof(Activation).GetProperty(nameof(Activation.BuiltinReturnPc))!.GetSetMethod()!;
    private static readonly MethodInfo EngineSetPcMethod =
        typeof(Activation).GetMethod(
            nameof(Activation.SetPc),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(int) }, null)!;
    private static readonly MethodInfo EngineSetB0Method =
        typeof(Activation).GetMethod(
            nameof(Activation.SetB0),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(int) }, null)!;
    // Threaded IL non-tail Call. Setting Cp to a
    // resume marker before transferring to the callee is how the IL
    // caller registers its forward continuation.
    private static readonly MethodInfo EngineSetCpMethod =
        typeof(Activation).GetMethod(
            nameof(Activation.SetCp),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(int) }, null)!;
    private static readonly MethodInfo EngineBGetter =
        typeof(Activation).GetProperty(nameof(Activation.B))!.GetGetMethod()!;
    // ExecuteBuiltin's tail-return contract reads the caller's
    // continuation (Cp) for BuiltinReturnPc.
    private static readonly MethodInfo EngineCpGetter =
        typeof(Activation).GetProperty(nameof(Activation.Cp))!.GetGetMethod()!;
    // ADR-025 stage (b) — the inline-ITE choice point's resume callback.
    private static readonly FieldInfo IlIteHelperResumeField =
        typeof(IlIteHelper).GetField(nameof(IlIteHelper.Resume))!;
    // ADR-025 — capture CURRENT B (the inline-ITE barrier; see Opcode.GetLevelB).
    private static readonly MethodInfo EngineGetLevelBMethod =
        typeof(Activation).GetMethod(nameof(Activation.GetLevelB), new[] { typeof(int) })!;
    // Was DEBUG-only (diagnostic dumps); ADR-031 case G reads E at clause entry
    // for the lazy CP's entry marks, so the binding is now unconditional.
    private static readonly MethodInfo EngineEGetter =
        typeof(Activation).GetProperty(nameof(Activation.E))!.GetGetMethod()!;
    private static readonly MethodInfo EngineIlTailCallPendingSetter =
        typeof(Activation).GetProperty(nameof(Activation.IlTailCallPending))!.GetSetMethod()!;
    // The watermark-gated heap-GC safe point the dispatch loop runs at every
    // goal boundary; a self-tail-recursion in-method loop must call it at the
    // back-edge so an allocating loop still collects (the loop bypasses the
    // dispatch loop that would otherwise run it).
    private static readonly MethodInfo EngineMaybeCollectHeapMethod =
        typeof(Activation).GetMethod(nameof(Activation.MaybeCollectHeap), Type.EmptyTypes)!;
    private static readonly MethodInfo EngineCurrentFunctorAddressesGetter =
        typeof(Activation).GetProperty(nameof(Activation.CurrentFunctorAddresses))!.GetGetMethod()!;
    private static readonly MethodInfo IlExecuteHelperResolveMethod =
        typeof(IlExecuteHelper).GetMethod(nameof(IlExecuteHelper.Resolve))!;
    // Theme-1 / WAM stripping: an IL caller dispatches a callee by FUNCTOR ID
    // (a resume marker with cursor 0 = entry), not by resolving it to a WAM
    // address. The dispatcher routes the marker to the callee's IL delegate
    // directly via IlByFunctorId when it has IL, or falls back to its WAM
    // address otherwise — so an IL-only callee needs no WAM body/address.
    private static readonly MethodInfo EngineEncodeResumeMarkerMethod =
        typeof(Activation).GetMethod(nameof(Activation.EncodeResumeMarker))!;
    // Region compilation — a member's proceed decodes Cp via this to
    // choose intra-region br (a return cursor) vs cross-region return-to-loop (-1).
    private static readonly MethodInfo EngineRegionReturnCursorMethod =
        typeof(Activation).GetMethod(nameof(Activation.RegionReturnCursor))!;
    // meta-call dispatch helper.
    private static readonly MethodInfo IlMetaCallHelperDispatchMethod =
        typeof(IlMetaCallHelper).GetMethod(nameof(IlMetaCallHelper.Dispatch))!;
    private static readonly MethodInfo IlMetaCallHelperReadIntRegisterMethod =
        typeof(IlMetaCallHelper).GetMethod(nameof(IlMetaCallHelper.ReadIntRegister))!;
    // ---------- get_structure / put_structure ----------
    private static readonly MethodInfo EngineGetStructureMethod =
        typeof(Activation).GetMethod(nameof(Activation.GetStructure), new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo EnginePutStructureMethod =
        typeof(Activation).GetMethod(nameof(Activation.PutStructure), new[] { typeof(int), typeof(int) })!;
    // ADR-020 reserve-upfront roots.
    private static readonly MethodInfo EnginePutStructureReservedMethod =
        typeof(Activation).GetMethod(nameof(Activation.PutStructureReserved), new[] { typeof(int), typeof(int), typeof(int) })!;
    private static readonly MethodInfo EnginePutListReservedMethod =
        typeof(Activation).GetMethod(nameof(Activation.PutListReserved), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyArgCellMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyArgCell), new[] { typeof(Cell) })!;
    private static readonly MethodInfo EngineUnifyVariableXMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyVariableX), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyValueXMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyValueX), new[] { typeof(int) })!;
    // ADR-019 inline nested compound build/match.
    private static readonly MethodInfo EngineUnifyStructureMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyStructure), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyListMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyList), Type.EmptyTypes)!;
    private static readonly MethodInfo EngineUnifyVariableYMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyVariableY), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyValueYMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyValueY), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyVoidMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyVoid), new[] { typeof(int) })!;
    // ---------- get_list / put_list / pstr ----------
    private static readonly MethodInfo EngineGetListMethod =
        typeof(Activation).GetMethod(nameof(Activation.GetList), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineGetListVarXVarXMethod =
        typeof(Activation).GetMethod(nameof(Activation.GetListVarXVarX),
            new[] { typeof(int), typeof(int), typeof(int) })!;
    private static readonly MethodInfo EngineGetListValXVarXMethod =
        typeof(Activation).GetMethod(nameof(Activation.GetListValXVarX),
            new[] { typeof(int), typeof(int), typeof(int) })!;
    private static readonly MethodInfo EngineGetStruct2VarXVarXMethod =
        typeof(Activation).GetMethod(nameof(Activation.GetStruct2VarXVarX),
            new[] { typeof(int), typeof(int), typeof(int), typeof(int) })!;
    private static readonly MethodInfo EngineGetStruct2ValXValXMethod =
        typeof(Activation).GetMethod(nameof(Activation.GetStruct2ValXValX),
            new[] { typeof(int), typeof(int), typeof(int), typeof(int) })!;
    private static readonly MethodInfo EnginePutListMethod =
        typeof(Activation).GetMethod(nameof(Activation.PutList), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineMakePstrMethod =
        typeof(Activation).GetMethod(nameof(Activation.MakePstr), new[] { typeof(string) })!;
    private static readonly MethodInfo EngineUnifyRegisterWithHeapAtMethod =
        typeof(Activation).GetMethod(
            nameof(Activation.UnifyRegisterWithHeapAt),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo IlGetPstrHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.GetPstr))!;
    private static readonly MethodInfo IlPutPstrHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.PutPstr))!;
#if DEBUG
    // Debug-mode marker methods. Reflection lookups stripped
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

    // IL non-tail Call is threaded (resume-marker dispatch); the natural
    // CP cascade handles backtracking across IL/bytecode boundaries.
    private static readonly MethodInfo EngineAllocateHeapUnboundMethod =
        typeof(Activation).GetMethod(nameof(Activation.AllocateHeapUnbound), Type.EmptyTypes)!;
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
    /// <paramref name="calleeMap"/> lets the check inspect
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

    /// <summary>Eligibility check with control over the indexed-
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
        // full indexed dispatch (O(1) switch + bucket chains).
        // Preferred over the linear IndexedAtom / SwitchedChain recognisers
        // for any switch-led shape; those remain as fallbacks for shapes it
        // doesn't model.
        if (allowIndexedDispatch && TryDescribeIndexed(predicate, calleeMap, out _)) return true;
        if (TryDescribeIndexedAtomPredicate(predicate, calleeMap, out _)) return true;
        if (TryDescribeTryMeElseChain(predicate, calleeMap, out _)) return true;
        return TryDescribeSwitchedChain(predicate, calleeMap, out _);
    }

    /// <summary>Wraps <see cref="IlIndexedDispatch.TryDescribe"/> with the
    /// IL-subset body-opcode check. memoized per predicate (see
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

    /// <summary>structural variant of
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

    /// <summary>True iff this predicate compiles to the full
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
    /// indexed predicate, so the bundle can persist it and strip the
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
                // only a META tail builtin blocks.
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
        // the typed switch tables are dispatch skeleton too.
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
        // Region compilation (Stage 3, gated): emit the root + its local
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
        // Deep cut: get_level captures the entry barrier into
        // a Y slot, cut commits to it. Emitted as engine.GetLevel /
        // engine.CutToLevel.
        Opcode.GetLevel => true,
        Opcode.Cut => true,
        // fused opcodes. Emit pair of engine calls; the
        // single-opcode-walk advances by the fused size, skipping the
        // padding Nop.
        Opcode.AllocateGetLevel => true,
        Opcode.DeallocateProceed => true,
        Opcode.Nop => true,   // padding inside fused opcodes; emit no-op
        Opcode.Execute => true,
        // Compound argument structure.
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
        // List head matching.
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
        // PSTR + Call.
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
        // Meta dbg_info — pure compile-time metadata; the
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
        // Case-2 rule inline: each inlined rule body's own non-tail
        // calls thread through THIS caller's forward-resume cursor space, so the
        // resume-label array must be sized to include them. computed
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
    /// <c>DynamicMethod</c>) and the persisted-assembly path
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
        // Self-tail-recursion → in-method loop: a self Execute
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

    /// <summary>defines a static method named
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

        // reset the per-method member-cursor output so a non-region method
        // doesn't inherit the previous region's table.
        LastRegionMemberCursors = null;

        // Prereq-i for the Stage-9 bundle prune: region compilation in the persisted-IL
        // path. A region method bakes its absorbed members' bodies in, so once it ships
        // their standalone forms can be pruned. The region emit uses the
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
                // hand the builder the (memberName, arity, entryCursor) table
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
            // full indexed dispatch (O(1) + buckets) in persisted
            // IL. The emit bakes the functor id via the patching
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
            // switch_on_term-headed predicates emit through
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
            typeof(Func<Activation, int, bool>),   // runtime path: SelfFromHolder → Func
            ruleInlineSites);                  // precomputed by the caller
        var del = FinishEmit(emit,
            $"compile fid={predicate.FunctorId} {FidName(predicate.FunctorId)}/{predicate.Arity} clauses={predicate.ClauseCount}");
        IndexedDelegateHolder.Register(holderKey, del);
        _nextHolderKey = holderKey + 1;
        return del;
    }

}
