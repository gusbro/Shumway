using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Per-engine store of invocation counters and IL-compiled delegates for
/// Tier-1 auto-promotion. The store is keyed by predicate functor id (a
/// stable global identifier — unlike the per-query bytecode address) so
/// promotion state survives across queries on the same engine.
///
/// <para>Promotion is driven by <see cref="BytecodeInterpreter"/> on every
/// <c>call</c> / <c>execute</c> dispatch: it consults the store to find an
/// already-promoted delegate, and otherwise bumps the counter. When the
/// counter crosses <see cref="Threshold"/>, the store attempts a synchronous
/// IL compile through <see cref="IlPredicateCompiler"/>; on success the
/// delegate is cached and every subsequent call (including this one) goes
/// through the IL path. Predicates that fall outside the IL subset are
/// marked unpromotable and left on Tier 0 forever — preventing repeated
/// compile attempts.</para>
///
/// <para>Background-thread promotion is the obvious next refinement; the
/// synchronous shape here keeps the data flow tight enough to reason about
/// while still demonstrating the end-to-end promote-and-dispatch path.</para>
/// </summary>
public sealed class IlPromotionStore
{
    private readonly Dictionary<int, int> _counters = new();
    private readonly Dictionary<int, PredicateDelegate> _delegates = new();
    private readonly HashSet<int> _unpromotable = new();

    // Phase 33 B3 — the dispatch/resume wrapper closures the per-query
    // Tier1DispatcherAdapter used to allocate PER PROMOTED PREDICATE PER QUERY,
    // cached here for the engine's lifetime instead (a delegate's identity is
    // stable between installs). Every install / replace / remove goes through
    // InstallDelegate / the evict path, which drop the cached pair — so a
    // swapped delegate (PGO phase-2, churn re-promote, evict) is never
    // shadowed by a stale wrapper across queries.
    private readonly Dictionary<int, Func<Activation, bool>> _dispatchWrappers = new();
    private readonly Dictionary<int, Func<Activation, int, bool>> _resumeWrappers = new();

    private void InstallDelegate(int functorId, PredicateDelegate del)
    {
        _delegates[functorId] = del;
        _dispatchWrappers.Remove(functorId);
        _resumeWrappers.Remove(functorId);
    }

    /// <summary>The cached `engine => del(engine, 0)` dispatch wrapper for a
    /// promoted predicate (null when not promoted). Activation-lifetime — the
    /// per-query adapter probes this instead of allocating its own.</summary>
    internal Func<Activation, bool>? TryGetDispatchWrapper(int functorId)
    {
        if (_dispatchWrappers.TryGetValue(functorId, out var w)) return w;
        if (!_delegates.TryGetValue(functorId, out var del)) return null;
        Func<Activation, bool> wrapper = engine => del(engine, 0);
        _dispatchWrappers[functorId] = wrapper;
        return wrapper;
    }

    /// <summary>The cached `(engine, cursor) => del(engine, cursor)` resume
    /// wrapper for a promoted predicate (null when not promoted). Activation-lifetime.</summary>
    internal Func<Activation, int, bool>? TryGetResumeWrapper(int functorId)
    {
        if (_resumeWrappers.TryGetValue(functorId, out var w)) return w;
        if (!_delegates.TryGetValue(functorId, out var del)) return null;
        Func<Activation, int, bool> wrapper = (engine, cursor) => del(engine, cursor);
        _resumeWrappers[functorId] = wrapper;
        return wrapper;
    }
    // Diagnostic: why each unpromotable predicate was rejected
    // ("dynamic" / "size" / "cannot-compile" / "query"). Populated
    // alongside _unpromotable; surfaced by UnpromotableEntries for the
    // Tier-1 coverage analysis.
    private readonly Dictionary<int, string> _unpromotableReason = new();

    private void MarkUnpromotable(int functorId, string reason)
    {
        _unpromotable.Add(functorId);
        _unpromotableReason[functorId] = reason;
    }

    // Tier-1 is runtime code generation, so it is unavailable under
    // Native AOT. Under AOT the store stays a pure Tier-0 counter — it
    // never compiles, and the IL compiler (which itself does reflection
    // at type-init) is never even constructed.
    private static readonly bool DynamicCodeSupported =
        RuntimeFeature.IsDynamicCodeSupported;

    private IlPredicateCompiler? _compilerInstance;

    /// <summary>The IL compiler, created on first use. Left null forever
    /// when Tier-1 never runs (the default, and always under AOT), so the
    /// reflection in its type initialiser is not reached.</summary>
    private IlPredicateCompiler Compiler => _compilerInstance ??= new IlPredicateCompiler();

    /// <summary>ADR-022 item 2 — supplies the native-block inline context (the
    /// engine sets this). Consulted on the IL-compile worker thread so the
    /// thread-static context is established there; null → no inlining.</summary>
    public Func<Shumway.Compiler.NativeC.NativeInlineContext?>? NativeInlineProvider { get; set; }

    /// <summary>Runs <paramref name="compile"/> with this engine's native-block
    /// inline context established on the current thread (the IL-compile worker).</summary>
    private T WithNativeInline<T>(Func<T> compile)
    {
        var prev = IlPredicateCompiler.BeginNativeInline(NativeInlineProvider?.Invoke());
        try { return compile(); }
        finally { IlPredicateCompiler.EndNativeInline(prev); }
    }

    /// <summary>Float-literal support — supplies the pool the predicate at a given
    /// fid indexes (the engine sets this). get_float/put_float resolve their value
    /// against it and bake an ldc.r8 constant. Null → the float opcodes report as
    /// unsupported, so a float-bearing predicate just stays Tier-0.</summary>
    public Func<int, System.Collections.Generic.IReadOnlyList<double>?>? FloatPoolProvider { get; set; }

    /// <summary>Runs <paramref name="compile"/> with the float pool for
    /// <paramref name="functorId"/> established on the current (IL-compile) thread.</summary>
    private T WithFloatPool<T>(int functorId, Func<T> compile)
    {
        var prev = IlPredicateCompiler.BeginFloatPool(FloatPoolProvider?.Invoke(functorId));
        try { return compile(); }
        finally { IlPredicateCompiler.EndFloatPool(prev); }
    }

    /// <summary>ADR-023 — builds a static-style snapshot <see cref="CompiledPredicate"/>
    /// of a dynamic predicate's currently-visible clauses (set by the engine).
    /// Returns null when the predicate has no visible clauses or its rewrite cache
    /// isn't built yet.</summary>
    public Func<int, CompiledPredicate?>? DynamicSnapshotProvider { get; set; }

    /// <summary>ADR-023 — per-functor count of how many times a promoted dynamic
    /// snapshot has been evicted by a mutation. Past <see cref="EvictionChurnLimit"/>
    /// the predicate is pinned to Tier 0.</summary>
    private readonly Dictionary<int, int> _evictions = new();

    /// <summary>ADR-023 — a dynamic predicate evicted this many times is mutation-
    /// hot; stop the promote→evict churn and keep it on Tier 0. Phase 33 L5 — the
    /// pin re-arms after <see cref="ChurnRearmCalls"/> mutation-free invocations
    /// (see RecordInvocation), so a load-then-read predicate isn't banished from
    /// IL forever by its startup mutation phase.</summary>
    public int EvictionChurnLimit { get; set; } = 3;

    /// <summary>Phase 33 L5 — invocations a churn-pinned dynamic predicate must run
    /// WITHOUT any further mutation before the pin re-arms (one more promotion is
    /// allowed). A mutation resets the streak (see <see cref="EvictDelegate"/>).</summary>
    public int ChurnRearmCalls { get; set; } = 4096;

    // Per-functor mutation-free invocation streak for churn-pinned predicates.
    private readonly Dictionary<int, int> _churnQuietCalls = new();

    /// <summary>ADR-023 — drops a dynamic predicate's cached IL snapshot after a
    /// mutation (assert/retract/abolish), so the next call falls back to the
    /// in-place-patched Tier-0 bytecode (the current database) and the predicate
    /// re-warms before re-snapshotting. Counts the eviction toward the churn limit
    /// only when a delegate was actually present (a real promote→evict cycle).
    /// A no-op for a predicate that was never promoted.</summary>
    public void EvictDelegate(int functorId)
    {
        // Phase 33 L5 — any mutation breaks a churn-pinned predicate's
        // mutation-free streak, whether or not a delegate was present.
        _churnQuietCalls.Remove(functorId);
        // Phase 33 L2 — and invalidates any background compile in flight (its
        // snapshot predates this mutation; the drain discards a stale stamp).
        _mutationStamp.TryGetValue(functorId, out int stamp);
        _mutationStamp[functorId] = stamp + 1;
        if (!_delegates.Remove(functorId)) return;
        _dispatchWrappers.Remove(functorId);
        _resumeWrappers.Remove(functorId);
        _counters.Remove(functorId);
        _pgoProfileKeys.Remove(functorId);
        _pgoOptimized.Remove(functorId);
        _evictions.TryGetValue(functorId, out int e);
        _evictions[functorId] = e + 1;
    }

    /// <summary>ADR-023 priming — a `:- dynamic` / `:- visible` predicate that
    /// is DECLARED WITH CLAUSES (source facts/rules, not a runtime-only assert
    /// target) is read-hot and mutation-cold: it should run as its Tier-1 IL
    /// snapshot from the FIRST call, not after the usual warm-up. Marking it
    /// here drops its promotion threshold to 1, so the first dispatch builds and
    /// installs the snapshot. It stays fully evictable: the first assert/retract
    /// runs <see cref="EvictDelegate"/> and the live dynamic chain (ADR-015
    /// logical-update view) takes over — and the churn guard still pins a
    /// genuinely mutation-hot predicate to Tier 0 after
    /// <see cref="EvictionChurnLimit"/> evictions.</summary>
    private readonly HashSet<int> _primeImmediately = new();

    /// <summary>Marks <paramref name="functorId"/> for promote-on-first-call (see
    /// <see cref="_primeImmediately"/>). Idempotent; a no-op under AOT (where no
    /// IL is generated) since <see cref="RecordInvocation"/> bails on
    /// <c>!DynamicCodeSupported</c> regardless.</summary>
    public void MarkPrime(int functorId) => _primeImmediately.Add(functorId);

    /// <summary>Stack size for the worker thread that drives every
    /// Sigil-emitted IL compile. Defensive belt — the size
    /// threshold (<see cref="MaxIlPromotionBytecodeBytes"/>) keeps
    /// us out of the deep-recursion regime where Sigil's
    /// <c>ReturnTracer</c> overflows. The expanded stack catches
    /// anything that slips past anyway, since
    /// <see cref="StackOverflowException"/> is uncatchable and
    /// otherwise tears the process down.</summary>
    private const int IlCompileStackBytes = 16 * 1024 * 1024;

    /// <summary>Predicates whose compiled bytecode exceeds this size
    /// are kept on Tier 0 (the bytecode interpreter) and never
    /// IL-promoted.
    ///
    /// <para>Sigil's emit-time validation (the
    /// <c>RollingVerifier.Transition</c> + <c>VerifiableTracker.
    /// CollapseAndVerify</c> per-instruction state-tracking pass)
    /// is O(N²) in the bytecode size, and its <c>Seal</c> phase
    /// adds an always-on <c>InjectTailCall</c> step whose
    /// <c>InsertInstruction</c> does an O(N) scan of every
    /// branch / mark / return table to re-index — also O(N²)
    /// across the full predicate. Chunk 171 quantified both:
    /// on a 13 KB / 640-atom-fact predicate the original Sigil
    /// path took ~13 s; with <c>doVerify=false</c> and
    /// <c>OptimizationOptions.None</c> the same compile runs
    /// in ~0.9 s (≈14× faster). A 27 KB / 1280-atom-fact
    /// predicate now compiles in ~5 s.</para>
    ///
    /// <para>So the threshold is raised — IL promotion now
    /// covers the bulk of Blint.pl's hot predicates without
    /// pathological compile times. A future linear-validation
    /// emitter (or vendoring Sigil with patched
    /// <c>InsertInstruction</c>) would let this be removed.</para></summary>
    public int MaxIlPromotionBytecodeBytes { get; set; } = 16384;

    /// <summary>Runs <paramref name="work"/> on the shared persistent large-stack
    /// compile worker so Sigil's recursive validation has room, and waits for the
    /// result. Phase 33 L2 — previously this created (and joined) a fresh 16 MB
    /// thread PER COMPILE on the query thread; the worker pays the stack once for
    /// the process. Propagates any exception back to the caller.</summary>
    private static T RunOnLargeStack<T>(Func<T> work) => IlCompileWorker.RunSync(work);

    // ---- Phase 33 L2 — background compilation (default ON) --------------------

    /// <summary>When <c>true</c>, a threshold-crossing predicate's IL compile is
    /// queued to the shared worker and the predicate STAYS ON TIER-0 until the
    /// delegate is ready (installed at the next dispatch through
    /// <see cref="RecordInvocation"/>, which drains completed compiles) — the
    /// query thread never stalls for a Sigil emit. Phase 33 L2 default-flip:
    /// default <c>true</c>, realizing the day-one architecture contract
    /// ("promotion happens in a background thread; the swap is atomic").
    /// Deterministic timing for tests/diagnostics comes from
    /// <see cref="IsPromoted"/> settling an in-flight compile of the queried
    /// functor and from <see cref="WaitForPendingPromotions"/>; set
    /// <c>false</c> to make the promoting call itself wait (still on the
    /// persistent worker, so no per-compile thread cost either way).</summary>
    public bool BackgroundCompilation { get; set; } = true;

    /// <summary>Phase 33 L1 — invoked on the ENGINE thread whenever a freshly
    /// compiled delegate is installed (synchronously at the promoting call, or at
    /// the drain of a background compile). The engine uses it to patch remaining
    /// generic <c>Call</c>/<c>Execute</c> sites targeting the callee to
    /// <c>CallIl</c>/<c>ExecuteIl</c> and to update the interpreter's direct
    /// dispatch table — so the REST OF THE RUNNING QUERY dispatches directly
    /// instead of paying OnDispatch until the next query setup.</summary>
    public Action<int, PredicateDelegate>? OnPromotionInstalled { get; set; }

    // In-flight background compiles (engine thread only) and their results
    // (worker → engine thread hand-off).
    private readonly HashSet<int> _pendingCompiles = new();
    private readonly ConcurrentQueue<CompletedCompile> _completedCompiles = new();
    private sealed record CompletedCompile(
        int Fid, IlPredicateCompiler.PgoCompileResult? Result, int Stamp, string? Error);

    // Per-fid mutation stamp: bumped by EvictDelegate on EVERY mutation, whether
    // or not a delegate was present, so a background compile of a dynamic
    // SNAPSHOT that was mutated while in flight is discarded at drain (installing
    // it would violate the logical-update view with stale clauses).
    private readonly Dictionary<int, int> _mutationStamp = new();

    private void DrainCompletedCompiles()
    {
        while (_completedCompiles.TryDequeue(out var c))
        {
            _pendingCompiles.Remove(c.Fid);
            if (c.Error is not null)
            {
                MarkUnpromotable(c.Fid, "compile-failed:" + c.Error);
                continue;
            }
            _mutationStamp.TryGetValue(c.Fid, out int now);
            if (now != c.Stamp) continue;   // mutated mid-compile: stale snapshot, re-warm
            InstallDelegate(c.Fid, c.Result!.Value.Delegate);
            if (c.Result.Value.ProfileKey >= 0) _pgoProfileKeys[c.Fid] = c.Result.Value.ProfileKey;
            OnPromotionInstalled?.Invoke(c.Fid, c.Result.Value.Delegate);
        }
    }

    /// <summary>True while any background compile is in flight (diagnostics/tests).</summary>
    public bool HasPendingPromotions => _pendingCompiles.Count > 0;

    /// <summary>Waits (engine thread) until every in-flight background compile has
    /// completed AND been installed. Returns false on timeout. Test/embedding
    /// barrier for the async mode.</summary>
    public bool WaitForPendingPromotions(int timeoutMs = 10_000)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            DrainCompletedCompiles();
            if (_pendingCompiles.Count == 0) return true;
            if (deadline.ElapsedMilliseconds > timeoutMs) return false;
            System.Threading.Thread.Sleep(1);
        }
    }

    // Chunk 76 — PGO. A promoted predicate whose shape supports
    // profile-guided optimisation gets a profile key here; once enough
    // samples accumulate the engine recompiles it (phase 2) and the
    // functor moves into _pgoOptimized.
    private readonly Dictionary<int, int> _pgoProfileKeys = new();
    private readonly HashSet<int> _pgoOptimized = new();

    /// <summary>Invocation count required before the store attempts an IL
    /// compile for a predicate. <c>0</c> disables promotion entirely (the
    /// store still works but never produces a delegate). Defaults to
    /// <c>0</c>; callers / tests opt in by setting a positive value.</summary>
    public int Threshold { get; set; }

    /// <summary>Chunk 76 — total profile samples a PGO-instrumented
    /// predicate must accumulate before the engine recompiles it to its
    /// optimised (profile-reordered) form. Tunable so tests can force
    /// the phase-2 transition cheaply.</summary>
    public int PgoSampleThreshold { get; set; } = 32;

    /// <summary>Returns the IL delegate currently bound to
    /// <paramref name="functorId"/>, or <c>null</c> if no promotion has
    /// happened yet (or if the predicate is marked unpromotable).</summary>
    public PredicateDelegate? TryGet(int functorId)
        => _delegates.TryGetValue(functorId, out var d) ? d : null;

    /// <summary>Records one invocation of <paramref name="functorId"/>.
    /// When the running count first crosses <see cref="Threshold"/>, the
    /// store synchronously asks <see cref="IlPredicateCompiler"/> whether
    /// the predicate's bytecode fits the IL subset; on success the
    /// delegate is registered and returned. Returns <c>null</c> in every
    /// other case (under-threshold, already promoted, or outside the
    /// IL subset).</summary>
    public PredicateDelegate? RecordInvocation(int functorId, CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        if (Threshold <= 0 || !DynamicCodeSupported) return null;
        // Phase 33 L2 — install any background compiles that finished since the
        // last dispatch (cheap: one IsEmpty check on the steady state).
        if (!_completedCompiles.IsEmpty) DrainCompletedCompiles();
        if (_delegates.ContainsKey(functorId)) return _delegates[functorId];
        if (_unpromotable.Contains(functorId)) return null;
        if (_pendingCompiles.Contains(functorId)) return null;   // compile in flight → stay Tier-0
        if (IsExcludedFromPromotion(functorId)) { MarkUnpromotable(functorId, "query"); return null; }

        // ADR-023 — a `:- dynamic` predicate (bytecode opens with enter_dynamic) is
        // promoted as a SNAPSHOT of its currently-visible clauses, not the mutable
        // chain; a later mutation evicts it (EvictDelegate). A predicate that has
        // proven mutation-hot (≥ EvictionChurnLimit evictions) is pinned to Tier 0.
        // Phase 33 L5 — the pin RE-ARMS: a churn-pinned predicate that then runs
        // ChurnRearmCalls invocations without a single further mutation has gone
        // read-hot (the Arity load-mutate-then-read-forever profile); its eviction
        // count resets to one-below-limit so it may promote again (and one more
        // promote→evict cycle re-pins it quickly if the mutation phase returns).
        bool isDynamic = IsExcludedByLayout(predicate);
        if (isDynamic && _evictions.TryGetValue(functorId, out int ev) && ev >= EvictionChurnLimit)
        {
            _churnQuietCalls.TryGetValue(functorId, out int quiet);
            quiet++;
            if (quiet < ChurnRearmCalls)
            {
                _churnQuietCalls[functorId] = quiet;
                return null;   // stays pinned (not via _unpromotable — re-armable)
            }
            // Read-hot with no mutations for a long stretch: re-arm.
            _churnQuietCalls.Remove(functorId);
            _evictions[functorId] = EvictionChurnLimit - 1;
        }

        _counters.TryGetValue(functorId, out int count);
        count++;
        _counters[functorId] = count;

        // ADR-023 priming — a declared-with-clauses dynamic/visible predicate
        // promotes on its first call (threshold 1); everything else warms up
        // normally.
        int effectiveThreshold = _primeImmediately.Contains(functorId) ? 1 : Threshold;
        if (count < effectiveThreshold) return null;

        // For a dynamic predicate, compile a static-style snapshot of its visible
        // clauses instead of the enter_dynamic chain. A null snapshot (no visible
        // clauses, or the rewrite cache not yet built) is a retry, NOT a permanent
        // rejection — the clauses may arrive on a later assertz.
        CompiledPredicate target = predicate;
        if (isDynamic)
        {
            var snapshot = DynamicSnapshotProvider?.Invoke(functorId);
            if (snapshot is null) return null;
            target = snapshot;
        }

        if (IsExcludedBySize(target))
        {
            MarkUnpromotable(functorId, isDynamic ? "dynamic-size" : "size");
            return null;
        }

        // CanCompile / DescribeRejection consult the float pool (get_float /
        // put_float are accepted only when it is set), so establish it here too —
        // this runs on the CALLING thread, the worker-thread emit sets its own.
        var prevFloatPool = IlPredicateCompiler.BeginFloatPool(FloatPoolProvider?.Invoke(functorId));
        bool canCompile;
        string? rejection = null;
        try
        {
            canCompile = Compiler.CanCompile(target, calleeMap);
            if (!canCompile) rejection = Compiler.DescribeRejection(target, calleeMap);
        }
        finally { IlPredicateCompiler.EndFloatPool(prevFloatPool); }
        if (!canCompile)
        {
            MarkUnpromotable(functorId, "cannot-compile:" + rejection);
            return null;
        }

        // Chunk 76 — phase-1 PGO compile. For a PGO-eligible shape this
        // is the instrumented form (profile key ≥ 0); otherwise it's a
        // plain compile (profile key -1) and no phase 2 will fire.
        // Sigil's recursive ReturnTracer can overflow the default 1 MB
        // stack on large predicates (200+ clauses, e.g. Blint.pl's
        // parse_args/2), so every compile runs on the shared expanded-stack
        // worker — StackOverflowException is uncatchable, so prevention is
        // the only option.
        if (BackgroundCompilation)
        {
            // Phase 33 L2 — queue the compile; the predicate stays Tier-0 until
            // the delegate is drained in. The engine-state-reading providers
            // (float pool, native-inline context) are invoked HERE on the engine
            // thread and their values captured — the worker must not touch
            // engine state (the pools are append-only lists, but List<T> reads
            // racing an Add are not safe).
            var floatPool = FloatPoolProvider?.Invoke(functorId);
            var nativeCtx = NativeInlineProvider?.Invoke();
            _mutationStamp.TryGetValue(functorId, out int stamp);
            var capturedTarget = target;
            var capturedCallees = calleeMap;
            _pendingCompiles.Add(functorId);
            IlCompileWorker.RunAsync(
                () =>
                {
                    var prevF = IlPredicateCompiler.BeginFloatPool(floatPool);
                    var prevN = IlPredicateCompiler.BeginNativeInline(nativeCtx);
                    try { return Compiler.CompileInstrumented(capturedTarget, capturedCallees); }
                    finally
                    {
                        IlPredicateCompiler.EndNativeInline(prevN);
                        IlPredicateCompiler.EndFloatPool(prevF);
                    }
                },
                (result, error) => _completedCompiles.Enqueue(new CompletedCompile(
                    functorId,
                    (IlPredicateCompiler.PgoCompileResult?)result,
                    stamp,
                    error?.Message)));
            return null;
        }

        var syncResult = RunOnLargeStack(() =>
            WithFloatPool(functorId, () =>
                WithNativeInline(() => Compiler.CompileInstrumented(target, calleeMap))));
        InstallDelegate(functorId, syncResult.Delegate);
        if (syncResult.ProfileKey >= 0)
            _pgoProfileKeys[functorId] = syncResult.ProfileKey;
        // Phase 33 L1 — let the engine patch this callee's remaining generic call
        // sites to CallIl/ExecuteIl for the rest of the running query.
        OnPromotionInstalled?.Invoke(functorId, syncResult.Delegate);
        return syncResult.Delegate;
    }

    /// <summary>Chunk 76 — phase-2 PGO pass. For every promoted,
    /// instrumented predicate whose accumulated profile has reached
    /// <see cref="PgoSampleThreshold"/> samples, recompiles it to the
    /// optimised (dispatch-reordered) form and swaps the bound
    /// delegate. <paramref name="predicateLookup"/> resolves a functor
    /// id to its current <see cref="CompiledPredicate"/> — the engine
    /// supplies the per-query functor→predicate view. Called once per
    /// query setup, off the hot path.</summary>
    public void ConsiderPgoRecompiles(
        IReadOnlyDictionary<int, CompiledPredicate> predicateLookup,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        if (_pgoProfileKeys.Count == 0 || !DynamicCodeSupported) return;
        // Snapshot the keys — the loop mutates _pgoProfileKeys.
        foreach (var functorId in _pgoProfileKeys.Keys.ToList())
        {
            int profileKey = _pgoProfileKeys[functorId];
            if (Shumway.Compiler.Il.IlProfileCounters.TotalSamples(profileKey)
                < PgoSampleThreshold)
            {
                continue;
            }
            if (!predicateLookup.TryGetValue(functorId, out var predicate))
                continue;   // predicate not in this query's program — retry later
            var optimized = RunOnLargeStack(
                () => WithFloatPool(functorId, () =>
                    WithNativeInline(() => Compiler.CompileOptimized(predicate, profileKey, calleeMap))));
            InstallDelegate(functorId, optimized);
            _pgoProfileKeys.Remove(functorId);
            _pgoOptimized.Add(functorId);
        }
    }

    /// <summary>True once <paramref name="functorId"/> has been
    /// recompiled to its profile-optimised form. Diagnostic surface
    /// for tests.</summary>
    public bool IsPgoOptimized(int functorId) => _pgoOptimized.Contains(functorId);

    /// <summary>True while <paramref name="functorId"/> is running in
    /// the instrumented phase-1 form, awaiting enough samples for the
    /// phase-2 recompile. Diagnostic surface for tests.</summary>
    public bool IsPgoInstrumented(int functorId) => _pgoProfileKeys.ContainsKey(functorId);

    /// <summary>The synthetic <c>__query__/N</c> predicates that
    /// <see cref="PrologEngine.SetupQueryFromTerm"/> wraps every query
    /// in have a *different body per query* but the same functor id
    /// (one per arity). IL-caching them would cache the body of the
    /// first query and replay it for every subsequent query of the
    /// same arity — almost certainly a wrong answer. Skip them.</summary>
    private static bool IsExcludedFromPromotion(int functorId)
    {
        var (atomId, _) = Shumway.Core.FunctorTable.Lookup(functorId);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        return name == "__query__";
    }

    /// <summary>Phase 12 chunk 159 — dynamic predicates compiled
    /// under chunks 155+/156 prefix their bytecode with
    /// <c>enter_dynamic</c> (0x66) and rely on per-clause
    /// <c>check_visible</c> + in-place chain mutations (assertz /
    /// asserta / retract / abolish) for the ISO logical-update
    /// view. The IL compiler's <see cref="IlPredicateCompiler.CanCompile"/>
    /// shapes (single-clause / indexed-atom / try_me_else chain)
    /// don't model that prefix or the runtime mutation hooks, and
    /// a cached IL delegate wouldn't observe a mid-life
    /// <c>retract</c> patching a clause's died slot — almost
    /// certainly a wrong answer. Exclude every predicate whose
    /// bytecode opens with <c>enter_dynamic</c>: that's the chunk
    /// 155+/156 signature for "this predicate's dispatch is
    /// mutation-driven and must run on Tier 0." Static predicates
    /// (which never carry the prefix) stay IL-eligible.</summary>
    private static bool IsExcludedByLayout(CompiledPredicate predicate)
    {
        if (predicate.Bytecode.Length == 0) return false;
        return predicate.Bytecode[0] == (byte)Shumway.Core.Opcode.EnterDynamic;
    }

    /// <summary>True when <paramref name="predicate"/> is larger
    /// than <see cref="MaxIlPromotionBytecodeBytes"/> and is
    /// therefore parked on Tier 0 forever. See the property's
    /// docs for the why.</summary>
    private bool IsExcludedBySize(CompiledPredicate predicate)
        => predicate.Bytecode.Length > EffectiveMaxBytecodeBytes;

    /// <summary>Phase 33 L3 — the size cap that actually applies. The 16 KB
    /// sync default bounds how long an explicitly-synchronous promoting call
    /// may stall the query thread (the historical O(N²)-Sigil stall — ~5 s at
    /// 27 KB, chunk 171 — no longer reproduces against today's emit shapes,
    /// but sync callers opted into bounded latency, so the cap stays). Under
    /// <see cref="BackgroundCompilation"/> (the default) the emit runs on the
    /// worker and the predicate keeps executing on Tier-0 in the meantime —
    /// a long compile is latency, not a stall — so the cap relaxes to
    /// <see cref="MaxIlPromotionBytecodeBytesBackground"/>. Large Arity fact
    /// tables (the classic 16 KB+ victims) earn IL that way.</summary>
    private int EffectiveMaxBytecodeBytes
        => BackgroundCompilation ? MaxIlPromotionBytecodeBytesBackground : MaxIlPromotionBytecodeBytes;

    /// <summary>Phase 33 L3 — the size cap when <see cref="BackgroundCompilation"/>
    /// is on (see <see cref="EffectiveMaxBytecodeBytes"/>). The O(N²)-Sigil
    /// premise behind the old 64 KB value was RE-MEASURED and REFUTED against
    /// the current emitter: the chunk-363 O(1) jump tables and chunk-216
    /// model-based indexed dispatch removed the long compare/branch chains
    /// that triggered Sigil's quadratic branch validation. Measured curve
    /// (fact tables, the only corpus shapes this size): 6 KB→0.2 s,
    /// 48 KB→0.6 s, 96 KB→1.0 s, 192 KB→1.6 s, 384 KB→3.6 s, 768 KB→5.3 s —
    /// linear. 256 KB covers the corpus's largest predicate (pty_name_l/3,
    /// 101.6 KB) with headroom at ~2 s of one-time worker latency; the true
    /// -fix follow-up (linear-validation emitter / vendored Sigil) is
    /// CLOSED as unnecessary.</summary>
    public int MaxIlPromotionBytecodeBytesBackground { get; set; } = 262144;

    /// <summary>Eagerly promotes <paramref name="predicate"/> without
    /// going through the counter, returning the resulting delegate on
    /// success. Useful for warm-up paths (e.g. AOT bundles) that want
    /// hot predicates IL-compiled before the first query.</summary>
    public PredicateDelegate? Warm(int functorId, CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        if (!DynamicCodeSupported) return null;
        if (_delegates.TryGetValue(functorId, out var existing)) return existing;
        if (_unpromotable.Contains(functorId)) return null;
        if (IsExcludedFromPromotion(functorId)
            || IsExcludedByLayout(predicate)
            || IsExcludedBySize(predicate))
        {
            _unpromotable.Add(functorId);
            return null;
        }
        var warmPrevPool = IlPredicateCompiler.BeginFloatPool(FloatPoolProvider?.Invoke(functorId));
        bool warmCanCompile;
        try { warmCanCompile = Compiler.CanCompile(predicate, calleeMap); }
        finally { IlPredicateCompiler.EndFloatPool(warmPrevPool); }
        if (!warmCanCompile)
        {
            _unpromotable.Add(functorId);
            return null;
        }
        var del = RunOnLargeStack(() =>
            WithFloatPool(functorId, () => WithNativeInline(() => Compiler.Compile(predicate, calleeMap))));
        InstallDelegate(functorId, del);
        return del;
    }

    /// <summary>Returns the current invocation count for diagnostics /
    /// tests. Returns <c>0</c> when no count has been recorded yet.</summary>
    public int CountFor(int functorId)
        => _counters.TryGetValue(functorId, out int c) ? c : 0;

    /// <summary>Diagnostic counts. <c>PromotedCount</c> = predicates
    /// successfully compiled to IL; <c>UnpromotableCount</c> = predicates
    /// rejected (excluded by layout, size, or compiler bail-out);
    /// <c>TrackedCount</c> = predicates that hit
    /// <see cref="RecordInvocation"/> at least once.</summary>
    public int PromotedCount => _delegates.Count;
    public int UnpromotableCount => _unpromotable.Count;
    public int TrackedCount => _counters.Count;

    /// <summary>True when <paramref name="functorId"/> has been bound to
    /// an IL delegate. Diagnostic surface for tests. Phase 33 L2 — under
    /// background compilation the answer stays DETERMINISTIC: an in-flight
    /// compile of this functor settles (bounded wait + drain) before
    /// answering, so the suite-wide "N warm queries → promoted" assertions
    /// hold regardless of worker latency. Activation-thread only, like every
    /// promotion API.</summary>
    public bool IsPromoted(int functorId)
    {
        if (_pendingCompiles.Contains(functorId)) WaitForPendingPromotions();
        else if (!_completedCompiles.IsEmpty) DrainCompletedCompiles();
        return _delegates.ContainsKey(functorId);
    }


    /// <summary>Binds a pre-built <see cref="PredicateDelegate"/>
    /// (typically created from a persisted-IL <c>MethodInfo</c> by
    /// <see cref="PrologEngine.LoadBundle(Bundle)"/>) for the given
    /// functor. Subsequent calls into the predicate dispatch through
    /// the bound delegate without going through the Sigil emit path.
    /// Idempotent: a second registration with the same functor id is
    /// silently dropped — the first delegate wins.</summary>
    public void RegisterBoundDelegate(int functorId, PredicateDelegate del)
    {
        if (_delegates.ContainsKey(functorId)) return;
        if (_unpromotable.Contains(functorId)) _unpromotable.Remove(functorId);
        InstallDelegate(functorId, del);
    }

    /// <summary>True when <paramref name="functorId"/> has been examined
    /// and rejected by the IL compiler — no further compile attempts
    /// will fire for it.</summary>
    public bool IsUnpromotable(int functorId) => _unpromotable.Contains(functorId);

    /// <summary>Chunk 226 Stage B.2 — true when this predicate will
    /// never have an IL delegate, even without RecordInvocation having
    /// fired yet. Used by PrologEngine at link time to classify Call
    /// sites: bytecode-only callees can be rewritten to
    /// <see cref="Shumway.Core.Opcode.CallBytecode"/> immediately,
    /// skipping the OnDispatch interface call + dictionary probe per
    /// dispatch. Mirrors the rejection logic in <see cref="RecordInvocation"/>:
    /// promotion disabled (<see cref="Threshold"/> == 0), AOT (no
    /// dynamic code), already-rejected, layout-excluded (chunk 159:
    /// dynamic predicates), or oversized.</summary>
    public bool IsPermanentlyBytecodeOnly(int functorId, CompiledPredicate predicate)
    {
        if (Threshold <= 0) return true;
        if (!DynamicCodeSupported) return true;
        if (_unpromotable.Contains(functorId)) return true;
        if (IsExcludedByLayout(predicate)) return true;
        if (IsExcludedBySize(predicate)) return true;
        return false;
    }

    /// <summary>Diagnostic: every predicate rejected from IL promotion,
    /// paired with the reason ("dynamic" / "size" / "cannot-compile" /
    /// "query"). Used by the Tier-1 coverage analysis to see whether the
    /// hot predicates are excluded for architectural reasons (dynamic) or
    /// fixable ones (size limit, compiler subset gaps).</summary>
    public IEnumerable<(int FunctorId, string Reason)> UnpromotableEntries()
    {
        foreach (var kv in _unpromotableReason)
            yield return (kv.Key, kv.Value);
    }

    /// <summary>Diagnostic: functor ids that were promoted to an IL
    /// delegate.</summary>
    public IEnumerable<int> PromotedFunctorIds() => _delegates.Keys;
}
