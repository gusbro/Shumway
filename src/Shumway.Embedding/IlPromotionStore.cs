using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Per-engine store of invocation counters and IL-compiled delegates for Tier-1
/// auto-promotion. Keyed by functor id (stable across queries, unlike bytecode
/// addresses), so promotion state survives re-links. The interpreter consults it
/// on every call/execute dispatch; when a counter crosses <see cref="Threshold"/>
/// the predicate is compiled (on a background worker by default) and subsequent
/// calls run the delegate. Predicates outside the IL subset are marked
/// unpromotable once and never re-attempted.
/// </summary>
public sealed class IlPromotionStore
{
    private readonly Dictionary<int, int> _counters = new();
    private readonly Dictionary<int, PredicateDelegate> _delegates = new();
    private readonly HashSet<int> _unpromotable = new();

    // Dispatch/resume wrapper closures, cached for the engine's lifetime (one pair
    // per promoted predicate, not per query). Every install/replace/evict drops the
    // pair so a swapped delegate is never shadowed by a stale wrapper.
    private readonly Dictionary<int, Func<Activation, bool>> _dispatchWrappers = new();
    private readonly Dictionary<int, Func<Activation, int, bool>> _resumeWrappers = new();

    private void InstallDelegate(int functorId, PredicateDelegate del)
    {
        _delegates[functorId] = del;
        _dispatchWrappers.Remove(functorId);
        _resumeWrappers.Remove(functorId);
    }

    /// <summary>The cached <c>engine => del(engine, 0)</c> dispatch wrapper, or null
    /// when not promoted.</summary>
    internal Func<Activation, bool>? TryGetDispatchWrapper(int functorId)
    {
        if (_dispatchWrappers.TryGetValue(functorId, out var w)) return w;
        if (!_delegates.TryGetValue(functorId, out var del)) return null;
        Func<Activation, bool> wrapper = engine => del(engine, 0);
        _dispatchWrappers[functorId] = wrapper;
        return wrapper;
    }

    /// <summary>The cached resume wrapper (delegate invoked at a cursor), or null
    /// when not promoted.</summary>
    internal Func<Activation, int, bool>? TryGetResumeWrapper(int functorId)
    {
        if (_resumeWrappers.TryGetValue(functorId, out var w)) return w;
        if (!_delegates.TryGetValue(functorId, out var del)) return null;
        Func<Activation, int, bool> wrapper = (engine, cursor) => del(engine, cursor);
        _resumeWrappers[functorId] = wrapper;
        return wrapper;
    }

    // Rejection reason per unpromotable predicate, for the coverage diagnostics.
    private readonly Dictionary<int, string> _unpromotableReason = new();

    private void MarkUnpromotable(int functorId, string reason)
    {
        _unpromotable.Add(functorId);
        _unpromotableReason[functorId] = reason;
    }

    // Under Native AOT there is no runtime codegen: the store stays a pure Tier-0
    // counter and the IL compiler (reflection-heavy type init) is never constructed.
    private static readonly bool DynamicCodeSupported =
        RuntimeFeature.IsDynamicCodeSupported;

    private IlPredicateCompiler? _compilerInstance;

    private IlPredicateCompiler Compiler => _compilerInstance ??= new IlPredicateCompiler();

    /// <summary>ADR-022 — supplies the native-block inline context; consulted on the
    /// compile worker thread so the thread-static context is established there.</summary>
    public Func<Shumway.Compiler.NativeC.NativeInlineContext?>? NativeInlineProvider { get; set; }

    private T WithNativeInline<T>(Func<T> compile)
    {
        var prev = IlPredicateCompiler.BeginNativeInline(NativeInlineProvider?.Invoke());
        try { return compile(); }
        finally { IlPredicateCompiler.EndNativeInline(prev); }
    }

    /// <summary>Supplies the float-literal pool a predicate indexes; get_float/put_float
    /// bake ldc.r8 constants from it. Null → predicates with float literals stay Tier-0.</summary>
    public Func<int, System.Collections.Generic.IReadOnlyList<double>?>? FloatPoolProvider { get; set; }

    private T WithFloatPool<T>(int functorId, Func<T> compile)
    {
        var prev = IlPredicateCompiler.BeginFloatPool(FloatPoolProvider?.Invoke(functorId));
        try { return compile(); }
        finally { IlPredicateCompiler.EndFloatPool(prev); }
    }

    /// <summary>ADR-023 — builds a static-style snapshot of a dynamic predicate's
    /// currently-visible clauses. Null when it has no visible clauses yet.</summary>
    public Func<int, CompiledPredicate?>? DynamicSnapshotProvider { get; set; }

    // ADR-023 churn pinning: a dynamic snapshot evicted EvictionChurnLimit times is
    // mutation-hot and stays on Tier 0; the pin re-arms after ChurnRearmCalls
    // mutation-free invocations (load-then-read predicates must not be banished by
    // their startup mutation phase).
    private readonly Dictionary<int, int> _evictions = new();

    public int EvictionChurnLimit { get; set; } = 3;

    public int ChurnRearmCalls { get; set; } = 4096;

    private readonly Dictionary<int, int> _churnQuietCalls = new();

    /// <summary>Rises on every eviction. Address-keyed dispatch caches outside the
    /// store (Tier1DispatcherAdapter) hold wrappers eviction cannot reach; they
    /// compare this stamp per dispatch and self-clear when it moved — a stale
    /// wrapper serving an evicted dynamic snapshot violates the logical update
    /// view (wrong answers, not errors).</summary>
    public int EvictionStamp { get; private set; }

    /// <summary>True while a consult is loading program text (set by the engine
    /// around the outermost consult): new Tier-1 promotions are deferred so a
    /// still-growing predicate is never snapshotted mid-load. Existing
    /// delegates keep serving.</summary>
    public bool PromotionsSuspended { get; set; }


    /// <summary>ADR-023 — drops a dynamic predicate's IL snapshot after a mutation;
    /// the next call falls back to the in-place-patched Tier-0 chain and the
    /// predicate re-warms. Counts toward the churn limit only when a delegate was
    /// actually present.</summary>
    public void EvictDelegate(int functorId)
    {
        EvictionStamp++;
        // A mutation breaks the churn-pinned mutation-free streak and invalidates
        // any background compile in flight (its snapshot predates the mutation;
        // the drain discards a stale stamp) — with or without a live delegate.
        _churnQuietCalls.Remove(functorId);
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

    // ADR-023 priming: a dynamic/visible predicate declared WITH source clauses is
    // read-hot and mutation-cold — promote on its first call (still fully evictable).
    private readonly HashSet<int> _primeImmediately = new();

    /// <summary>Marks <paramref name="functorId"/> for promote-on-first-call.</summary>
    public void MarkPrime(int functorId) => _primeImmediately.Add(functorId);

    /// <summary>Predicates whose bytecode exceeds this stay on Tier 0 when compiled
    /// SYNCHRONOUSLY — sync callers opted into bounded latency. See
    /// <see cref="EffectiveMaxBytecodeBytes"/>.</summary>
    public int MaxIlPromotionBytecodeBytes { get; set; } = 16384;

    /// <summary>Runs a compile on the shared large-stack worker: Sigil's recursive
    /// ReturnTracer can overflow the default 1 MB stack on large predicates, and
    /// StackOverflowException is uncatchable — prevention is the only option.</summary>
    private static T RunOnLargeStack<T>(Func<T> work) => IlCompileWorker.RunSync(work);

    /// <summary>When true (default), a threshold-crossing compile is queued to the
    /// worker and the predicate stays on Tier-0 until the delegate drains in — the
    /// query thread never stalls on a Sigil emit. <see cref="IsPromoted"/> and
    /// <see cref="WaitForPendingPromotions"/> give tests deterministic settling.</summary>
    public bool BackgroundCompilation { get; set; } = true;

    /// <summary>Invoked on the engine thread when a fresh delegate installs (sync or
    /// drained). The engine patches remaining generic Call/Execute sites to
    /// CallIl/ExecuteIl so the rest of the running query dispatches directly.</summary>
    public Action<int, PredicateDelegate>? OnPromotionInstalled { get; set; }

    // In-flight background compiles (engine thread only) and their results
    // (worker → engine thread hand-off).
    private readonly HashSet<int> _pendingCompiles = new();
    private readonly ConcurrentQueue<CompletedCompile> _completedCompiles = new();
    private sealed record CompletedCompile(
        int Fid, IlPredicateCompiler.PgoCompileResult? Result, int Stamp, string? Error,
        bool IsDynamicSnapshot);

    // Per-fid mutation stamp, bumped by EvictDelegate on every mutation: a background
    // compile whose snapshot was mutated while in flight must be discarded at drain
    // (installing it would violate the logical-update view with stale clauses).
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
            if (now != c.Stamp) continue;   // mutated mid-compile: stale, re-warm
            var drainDel = c.IsDynamicSnapshot
                ? GuardDynamicSnapshot(c.Fid, c.Result!.Value.Delegate)
                : c.Result!.Value.Delegate;
            InstallDelegate(c.Fid, drainDel);
            if (c.Result.Value.ProfileKey >= 0) _pgoProfileKeys[c.Fid] = c.Result.Value.ProfileKey;
            OnPromotionInstalled?.Invoke(c.Fid, drainDel);
        }
    }

    /// <summary>True while any background compile is in flight.</summary>
    public bool HasPendingPromotions => _pendingCompiles.Count > 0;

    /// <summary>Waits until every in-flight background compile has completed and been
    /// installed. False on timeout. Engine-thread only.</summary>
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

    // PGO: an eligible promoted predicate carries a profile key until enough samples
    // accumulate, then recompiles to the optimised form and moves to _pgoOptimized.
    private readonly Dictionary<int, int> _pgoProfileKeys = new();
    private readonly HashSet<int> _pgoOptimized = new();

    /// <summary>Invocation count before an IL compile is attempted. 0 (the default)
    /// disables promotion.</summary>
    public int Threshold { get; set; }

    /// <summary>Profile samples required before the phase-2 PGO recompile.</summary>
    public int PgoSampleThreshold { get; set; } = 32;

    /// <summary>The delegate bound to <paramref name="functorId"/>, or null.</summary>
    public PredicateDelegate? TryGet(int functorId)
        => _delegates.TryGetValue(functorId, out var d) ? d : null;

    /// <summary>Records one invocation; compiles and returns the delegate when the
    /// count crosses the threshold. Null otherwise (under-threshold, in-flight,
    /// unpromotable, or promotion disabled).</summary>
    public PredicateDelegate? RecordInvocation(int functorId, CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        if (Threshold <= 0 || !DynamicCodeSupported) return null;
        if (!_completedCompiles.IsEmpty) DrainCompletedCompiles();
        if (_delegates.ContainsKey(functorId)) return _delegates[functorId];
        // Mid-consult, no NEW promotions: the program is still growing — a
        // predicate promoted now (the expansion hooks are the hot case: one
        // term_expansion call per consulted clause) would snapshot a clause set
        // a later file in the same load extends. Already-promoted delegates
        // keep serving (consult-commit eviction drops any this load extends);
        // counting resumes when the outermost consult finishes.
        if (PromotionsSuspended) return null;
        if (_unpromotable.Contains(functorId)) return null;
        if (_pendingCompiles.Contains(functorId)) return null;   // compile in flight
        if (IsExcludedFromPromotion(functorId)) { MarkUnpromotable(functorId, "query"); return null; }

        // ADR-023 — a dynamic predicate promotes as a SNAPSHOT of its visible
        // clauses; mutation evicts it. Churn-pinned predicates stay Tier-0 until
        // the re-arm streak completes.
        bool isDynamic = IsExcludedByLayout(predicate);
        if (isDynamic && _evictions.TryGetValue(functorId, out int ev) && ev >= EvictionChurnLimit)
        {
            _churnQuietCalls.TryGetValue(functorId, out int quiet);
            quiet++;
            if (quiet < ChurnRearmCalls)
            {
                _churnQuietCalls[functorId] = quiet;
                return null;   // pinned (not via _unpromotable — re-armable)
            }
            _churnQuietCalls.Remove(functorId);
            _evictions[functorId] = EvictionChurnLimit - 1;
        }

        _counters.TryGetValue(functorId, out int count);
        count++;
        _counters[functorId] = count;

        int effectiveThreshold = _primeImmediately.Contains(functorId) ? 1 : Threshold;
        if (count < effectiveThreshold) return null;

        // A null snapshot (no visible clauses yet) is a retry, not a rejection —
        // clauses may arrive on a later assertz.
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

        // CanCompile consults the float pool, so establish it on THIS thread too
        // (the worker-thread emit sets its own).
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

        if (BackgroundCompilation)
        {
            // The engine-state-reading providers are invoked HERE, on the engine
            // thread, and their values captured — the worker must not touch engine
            // state (List<T> reads racing an Add are unsafe).
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
                    error?.Message,
                    isDynamic)));
            return null;
        }

        var syncResult = RunOnLargeStack(() =>
            WithFloatPool(functorId, () =>
                WithNativeInline(() => Compiler.CompileInstrumented(target, calleeMap))));
        var installedDel = isDynamic
            ? GuardDynamicSnapshot(functorId, syncResult.Delegate)
            : syncResult.Delegate;
        InstallDelegate(functorId, installedDel);
        if (syncResult.ProfileKey >= 0)
            _pgoProfileKeys[functorId] = syncResult.ProfileKey;
        OnPromotionInstalled?.Invoke(functorId, installedDel);
        return installedDel;
    }

    /// <summary>ADR-023 — wraps a dynamic-snapshot delegate so it self-guards against
    /// staleness: eviction clears every table, but a reference already hoisted into a
    /// running frame survives, and a pre-mutation snapshot answering a FRESH call
    /// violates the logical update view. On a fresh entry (cursor 0) with the
    /// mutation stamp moved, the guard self-evicts and redirects to the live Tier-0
    /// chain (SetPc + IlTailCallPending — the tail contract every dispatch site
    /// honours). A RESUME (cursor &gt; 0) deliberately keeps the old snapshot: a call
    /// that began before the mutation must enumerate its call-time view.</summary>
    private PredicateDelegate GuardDynamicSnapshot(int fid, PredicateDelegate inner)
    {
        _mutationStamp.TryGetValue(fid, out int stampAtInstall);
        return (engine, clauseCursor) =>
        {
            if (clauseCursor == 0)
            {
                _mutationStamp.TryGetValue(fid, out int now);
                if (now != stampAtInstall
                    && engine.CurrentFunctorAddresses is { } map
                    && map.TryGetValue(fid, out int addr)
                    && !CallTarget.IsUnresolved(addr)
                    && !Activation.IsResumeMarker(addr))
                {
                    // Self-evict: one redirect, then the ordinary machinery
                    // re-warms — a permanently-redirecting delegate would be
                    // pure overhead on every call.
                    EvictDelegate(fid);
                    engine.SetPc(addr);
                    engine.IlTailCallPending = true;
                    return true;
                }
            }
            return inner(engine, clauseCursor);
        };
    }

    /// <summary>Phase-2 PGO: recompiles every instrumented predicate whose profile
    /// reached <see cref="PgoSampleThreshold"/> to its optimised form and swaps the
    /// delegate. Called once per query setup, off the hot path.</summary>
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
                continue;   // not in this query's program — retry later
            var optimized = RunOnLargeStack(
                () => WithFloatPool(functorId, () =>
                    WithNativeInline(() => Compiler.CompileOptimized(predicate, profileKey, calleeMap))));
            InstallDelegate(functorId, optimized);
            _pgoProfileKeys.Remove(functorId);
            _pgoOptimized.Add(functorId);
        }
    }

    public bool IsPgoOptimized(int functorId) => _pgoOptimized.Contains(functorId);

    public bool IsPgoInstrumented(int functorId) => _pgoProfileKeys.ContainsKey(functorId);

    // The synthetic __query__/N wrappers have a DIFFERENT body per query under the
    // same functor id — caching one query's IL would replay it for every later query
    // of that arity.
    private static bool IsExcludedFromPromotion(int functorId)
    {
        var (atomId, _) = Shumway.Core.FunctorTable.Lookup(functorId);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        return name == "__query__";
    }

    // A bytecode body opening with enter_dynamic is mutation-driven dispatch
    // (per-clause check_visible + in-place chain patches, ADR-015): a cached IL
    // delegate of that FORM would not observe a mid-life retract. Such predicates
    // promote only via the ADR-023 snapshot path.
    private static bool IsExcludedByLayout(CompiledPredicate predicate)
    {
        if (predicate.Bytecode.Length == 0) return false;
        return predicate.Bytecode[0] == (byte)Shumway.Core.Opcode.EnterDynamic;
    }

    private bool IsExcludedBySize(CompiledPredicate predicate)
        => predicate.Bytecode.Length > EffectiveMaxBytecodeBytes;

    // Under background compilation a long compile is worker latency, not a query
    // stall, so the cap relaxes; sync callers keep the tight bound they opted into.
    private int EffectiveMaxBytecodeBytes
        => BackgroundCompilation ? MaxIlPromotionBytecodeBytesBackground : MaxIlPromotionBytecodeBytes;

    /// <summary>Size cap under <see cref="BackgroundCompilation"/>. Compile time is
    /// linear in bytecode size on the current emitter (measured to 768 KB), so the
    /// cap only bounds one-time worker latency (~2 s at 256 KB).</summary>
    public int MaxIlPromotionBytecodeBytesBackground { get; set; } = 262144;

    /// <summary>Eagerly promotes without the counter (warm-up paths). Returns the
    /// delegate, or null when rejected.</summary>
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

    public int CountFor(int functorId)
        => _counters.TryGetValue(functorId, out int c) ? c : 0;

    public int PromotedCount => _delegates.Count;
    public int UnpromotableCount => _unpromotable.Count;
    public int TrackedCount => _counters.Count;

    /// <summary>True when the functor is bound to an IL delegate. Deterministic under
    /// background compilation: an in-flight compile of this functor settles first.
    /// Engine-thread only, like every promotion API.</summary>
    public bool IsPromoted(int functorId)
    {
        if (_pendingCompiles.Contains(functorId)) WaitForPendingPromotions();
        else if (!_completedCompiles.IsEmpty) DrainCompletedCompiles();
        return _delegates.ContainsKey(functorId);
    }

    /// <summary>Binds a pre-built delegate (persisted-IL bundles). Idempotent — the
    /// first delegate wins.</summary>
    public void RegisterBoundDelegate(int functorId, PredicateDelegate del)
    {
        if (_delegates.ContainsKey(functorId)) return;
        if (_unpromotable.Contains(functorId)) _unpromotable.Remove(functorId);
        InstallDelegate(functorId, del);
    }

    /// <summary>True when the functor was examined and rejected — no further compile
    /// attempts will fire.</summary>
    public bool IsUnpromotable(int functorId) => _unpromotable.Contains(functorId);

    /// <summary>True when this predicate can never have an IL delegate, decidable
    /// without a RecordInvocation. Lets the linker rewrite its call sites to
    /// CallBytecode immediately, skipping OnDispatch per dispatch.</summary>
    public bool IsPermanentlyBytecodeOnly(int functorId, CompiledPredicate predicate)
    {
        if (Threshold <= 0) return true;
        if (!DynamicCodeSupported) return true;
        if (_unpromotable.Contains(functorId)) return true;
        if (IsExcludedByLayout(predicate)) return true;
        if (IsExcludedBySize(predicate)) return true;
        return false;
    }

    /// <summary>Every rejected predicate with its reason ("dynamic" / "size" /
    /// "cannot-compile" / "query") — the Tier-1 coverage diagnostic.</summary>
    public IEnumerable<(int FunctorId, string Reason)> UnpromotableEntries()
    {
        foreach (var kv in _unpromotableReason)
            yield return (kv.Key, kv.Value);
    }

    public IEnumerable<int> PromotedFunctorIds() => _delegates.Keys;
}
