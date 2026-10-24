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

    // SHUMWAY_TIER1_DIAG=1: log each delegate install to stderr — the
    // forensic knob that cracked the lost-wakeups bug (which predicate had
    // just promoted when coroutining went quiet).
    private static readonly bool PromotionDiag =
        Environment.GetEnvironmentVariable("SHUMWAY_TIER1_DIAG") == "1";

    // ADR-061: continuation methods, beside the delegate they resume through.
    private readonly Dictionary<int, IlPredicateCompiler.CpsCode> _cpsCode = new();

    /// <summary>ADR-061: the functor's continuation methods, or null.</summary>
    internal IlPredicateCompiler.CpsCode? TryGetCps(int functorId)
        => _cpsCode.TryGetValue(functorId, out var c) ? c : null;

    internal IEnumerable<KeyValuePair<int, IlPredicateCompiler.CpsCode>> CpsEntries() => _cpsCode;

    /// <summary>ADR-061: predicates with continuation methods. A table built
    /// before one arrived routes none of its cold cursors (TableEntry), so a
    /// table template is valid only while this holds.</summary>
    internal int CpsCodeCount => _cpsCode.Count;

    /// <summary>What the interpreter's tables invoke for <paramref name="functorId"/>:
    /// with continuation methods, an entry (cursor 0) goes to the entry
    /// method, an instruction boundary to the cold method (where its wake
    /// points resume, ADR-049), a continuation's cursor to its method and a
    /// choice point's to the alternatives method; any other cursor to the
    /// delegate.</summary>
    internal Func<Activation, int, bool> TableEntry(int functorId, PredicateDelegate del)
    {
        if (!_cpsCode.TryGetValue(functorId, out var cps))
        {
            if (_boundCps.TryGetValue(functorId, out var waiting) && ReferenceEquals(waiting.Delegate, del))
                return (engine, cursor) =>
                {
                    if (cursor == 0) CountBound(functorId, waiting);
                    return del(engine, cursor);
                };
            return del.Invoke;
        }
        var entry = cps.EntryDelegate;
        var cold = cps.ColdDelegate;
        var byCursor = cps.ByCursor;
        if (IlPredicateCompiler.CountColdEntries)   // a test's count
            return (engine, cursor) =>
            {
                if (cursor == 0)
                {
                    IlPredicateCompiler.LoopCalls++;
                    return entry(engine, 0);
                }
                if (cursor >= IlPredicateCompiler.ColdCursorBase) return cold(engine, cursor);
                if ((uint)cursor >= (uint)byCursor.Length || byCursor[cursor] is not { } own)
                    return del(engine, cursor);
                if (ReferenceEquals(own, cps.AltDelegate)) IlPredicateCompiler.LoopAlternatives++;
                else IlPredicateCompiler.LoopContinuations++;
                return own(engine, cursor);
            };
        return (engine, cursor) => cursor == 0 ? entry(engine, 0)
            : cursor >= IlPredicateCompiler.ColdCursorBase ? cold(engine, cursor)
            : (uint)cursor < (uint)byCursor.Length && byCursor[cursor] is { } own ? own(engine, cursor)
            : del(engine, cursor);
    }

    // ---- A bundle's compiled code ----
    // Nothing compiles on the engine's thread. A predicate whose bytecode the
    // engine has runs on it until it has earned its compiled code (Earned);
    // then the compile worker loads and compiles that code (the delegate and
    // every continuation method), and the engine switches when that drains
    // in. One with no bytecode (--strip-wam) has its delegate bound at load,
    // and its continuation methods wait in the same way.

    /// <summary>A bundle's compiled code for a predicate the engine runs from
    /// its bytecode until it is hot.</summary>
    internal sealed class Offer
    {
        public required int FunctorId;
        public required bool Wakes;
        public required bool IsDynamic;
        // On the compile worker: the code loaded, bound and compiled.
        public required Func<(PredicateDelegate Delegate, IlPredicateCompiler.CpsCode? Cps)> Bind;
        // On the engine's thread, when that failed.
        public Action<string>? Failed;
        // The functor's mutation stamp when offered: a dynamic predicate's
        // snapshot is its clauses only while it stands.
        public required int Stamp;
        // What compiling the code costs: the IL bytes of its methods; zero
        // when the bundle does not say.
        public int Cost;
        public long Count;
        public bool Requested;
        // Installed or withdrawn: the functor no longer waits for this code.
        public bool Done;
    }

    private sealed class BoundCps
    {
        public required PredicateDelegate Delegate;
        public required Func<IlPredicateCompiler.CpsCode?> Bind;
        public int Cost;
        public long Count;
        public bool Requested;
    }

    private readonly Dictionary<int, Offer> _offers = new();
    private readonly Dictionary<int, BoundCps> _boundCps = new();

    /// <summary>Invocations before a bundle's compiled code for a predicate is
    /// considered at all. It needs no <see cref="Threshold"/>: nothing is
    /// emitted. Zero, read when the bundle loads, binds the code at load
    /// instead: each method compiles at its first call, on the thread that
    /// calls it. <c>SHUMWAY_IL_BUNDLE_PROMOTE=threshold[,freeBytes[,callsPerByte]]</c>
    /// sets the three defaults.</summary>
    public int PersistedThreshold { get; set; } = DefaultPersistedThreshold;

    /// <summary>What <see cref="PersistedThreshold"/> starts at in a new engine.</summary>
    public static int DefaultPersistedThreshold
    {
        // Not a field initialized from PersistedDefaults: static initializers
        // run in textual order, and this one would read it unset.
        get => _defaultPersistedThreshold ?? PersistedDefaults.Threshold;
        set => _defaultPersistedThreshold = value;
    }

    private static int? _defaultPersistedThreshold;

    /// <summary>ADR-061: the IL bytes of compiled code taken as soon as its
    /// predicates pass <see cref="PersistedThreshold"/>. A small program fits
    /// whole and runs compiled at once.</summary>
    public int PersistedFreeBytes { get; set; } = PersistedDefaults.FreeBytes;

    /// <summary>ADR-061: past <see cref="PersistedFreeBytes"/>, the invocations
    /// a predicate needs per byte of its IL before that code is compiled:
    /// compiling costs in proportion to the IL, and it is paid for by what
    /// interpreting the predicate has already cost. Zero takes every predicate
    /// at the threshold.</summary>
    public int PersistedCallsPerByte { get; set; } = PersistedDefaults.CallsPerByte;

    private static readonly (int Threshold, int FreeBytes, int CallsPerByte) PersistedDefaults =
        ReadPersistedDefaults(Environment.GetEnvironmentVariable("SHUMWAY_IL_BUNDLE_PROMOTE"));

    internal static (int Threshold, int FreeBytes, int CallsPerByte) ReadPersistedDefaults(string? spec)
    {
        int threshold = 32, freeBytes = 64_000, callsPerByte = 4;
        string[] parts = (spec ?? "").Split(',');
        if (parts.Length > 0 && int.TryParse(parts[0], out int t) && t >= 0) threshold = t;
        if (parts.Length > 1 && int.TryParse(parts[1], out int f) && f >= 0) freeBytes = f;
        if (parts.Length > 2 && int.TryParse(parts[2], out int c) && c >= 0) callsPerByte = c;
        return (threshold, freeBytes, callsPerByte);
    }

    // IL bytes of the code taken so far, against PersistedFreeBytes.
    private long _persistedTaken;

    private bool Earned(int cost, long count)
        => _persistedTaken + cost <= PersistedFreeBytes || count >= (long)cost * PersistedCallsPerByte;

    /// <summary>Whether a bundle offers compiled code for the functor, not yet bound.</summary>
    public bool HasOffer(int functorId) => _offers.ContainsKey(functorId);

    /// <summary>Whether any offer stands: a promotion can install mid-query.</summary>
    public bool HasOffers => _offers.Count > 0;

    internal Offer? TryGetOffer(int functorId) => _offers.TryGetValue(functorId, out var o) ? o : null;

    /// <summary>Offers a bundle's compiled code for a predicate the engine has
    /// the bytecode of; <paramref name="bind"/> runs on the compile worker when
    /// the predicate has earned it (<paramref name="cost"/>: the IL bytes of
    /// its methods). The first offer wins, and none replaces a delegate.
    /// Code that <paramref name="followsTier"/> (the engine's own prelude)
    /// stands only while the IL tier is on: with the tier off the engine runs
    /// Tier-0, and the code waits for the tier.</summary>
    public void OfferPersisted(int functorId, bool wakes, bool isDynamic,
        Func<(PredicateDelegate Delegate, IlPredicateCompiler.CpsCode? Cps)> bind,
        Action<string>? failed = null, int cost = 0, bool followsTier = false)
    {
        if (!DynamicCodeSupported || _delegates.ContainsKey(functorId) || _offers.ContainsKey(functorId)
            || _pendingCompiles.Contains(functorId))
            return;
        if (followsTier)
        {
            _heldForTier[functorId] = new HeldOffer(wakes, isDynamic, bind, failed, cost);
            if (!IlTierOn) return;
        }
        Stand(functorId, wakes, isDynamic, bind, failed, cost);
        // With no tier on, the linker made every site bytecode-only; these
        // have to count their dispatches.
        if (Threshold <= 0 && Wasm is not { Enabled: true })
            PromotabilityChanged?.Invoke();
    }

    private void Stand(int functorId, bool wakes, bool isDynamic,
        Func<(PredicateDelegate Delegate, IlPredicateCompiler.CpsCode? Cps)> bind,
        Action<string>? failed, int cost)
    {
        _mutationStamp.TryGetValue(functorId, out int stamp);
        _offers[functorId] = new Offer
        {
            FunctorId = functorId, Wakes = wakes, IsDynamic = isDynamic, Bind = bind, Stamp = stamp,
            Failed = failed, Cost = cost,
        };
        _bundleCode.Add(functorId);
        _unpromotable.Remove(functorId);
    }

    private sealed record HeldOffer(bool Wakes, bool IsDynamic,
        Func<(PredicateDelegate Delegate, IlPredicateCompiler.CpsCode? Cps)> Bind,
        Action<string>? Failed, int Cost);

    // Code offered only while the IL tier is on, by functor.
    private readonly Dictionary<int, HeldOffer> _heldForTier = new();

    /// <summary>Whether the IL compiler is this engine's Tier-1 and promotes.</summary>
    private bool IlTierOn => Threshold > 0 && Wasm is not { Enabled: true };

    // The tier went on or off: code that follows it stands, or its untaken
    // offers go. A taken one stays until jit_compile(off) returns every
    // promoted predicate to its bytecode.
    private void FollowTier()
    {
        if (_heldForTier.Count == 0) return;
        bool on = IlTierOn;
        foreach (var (fid, held) in _heldForTier)
        {
            if (on)
            {
                if (!_delegates.ContainsKey(fid) && !_offers.ContainsKey(fid) && !_pendingCompiles.Contains(fid))
                    Stand(fid, held.Wakes, held.IsDynamic, held.Bind, held.Failed, held.Cost);
            }
            else if (_offers.TryGetValue(fid, out var standing) && !standing.Requested)
            {
                Withdraw(standing);
                _bundleCode.Remove(fid);
            }
        }
    }

    /// <summary>Offers the continuation methods of a delegate bound at load.</summary>
    public void OfferBoundCps(int functorId, PredicateDelegate del, Func<IlPredicateCompiler.CpsCode?> bind,
        int cost = 0)
    {
        if (!_delegates.TryGetValue(functorId, out var current) || !ReferenceEquals(current, del)
            || _cpsCode.ContainsKey(functorId) || _boundCps.ContainsKey(functorId))
            return;
        _boundCps[functorId] = new BoundCps { Delegate = del, Bind = bind, Cost = cost };
        _dispatchWrappers.Remove(functorId);
        _resumeWrappers.Remove(functorId);
    }

    /// <summary>Counts a dispatch of a predicate with an offer. Once it has
    /// earned its code the worker loads and compiles it; the engine stays on
    /// the bytecode until that drains in. True once the functor no longer
    /// waits for the offer (its code is installed, or the offer is gone).</summary>
    internal bool Tick(Offer offer)
    {
        if (!_completedCompiles.IsEmpty) DrainCompletedCompiles();
        if (offer.Done) return true;
        if (offer.Requested || PromotionsSuspended || ++offer.Count < PersistedThreshold
            || !Earned(offer.Cost, offer.Count))
            return false;
        return Request(offer);
    }

    // Not in Tick: a method that creates a lambda allocates what the lambda
    // captures on entry, and Tick runs at every dispatch of a waiting predicate.
    private bool Request(Offer offer)
    {
        _mutationStamp.TryGetValue(offer.FunctorId, out int stamp);
        if (stamp != offer.Stamp)
        {
            Withdraw(offer);
            return true;
        }
        offer.Requested = true;
        _persistedTaken += offer.Cost;
        _pendingCompiles.Add(offer.FunctorId);
        NoteCode(offer.FunctorId, CodePending);
        int functorId = offer.FunctorId;
        IlCompileWorker.RunAsync(
            () => (object?)Prepare(offer),
            (result, error) => _completedCompiles.Enqueue(new CompletedCompile(
                functorId, (IlPredicateCompiler.PgoCompileResult?)result, stamp, error?.Message, offer.IsDynamic)));
        return false;
    }

    private void Withdraw(Offer offer)
    {
        offer.Done = true;
        _offers.Remove(offer.FunctorId);
    }

    // On the worker: the delegate's method and every continuation method compiled.
    private static IlPredicateCompiler.PgoCompileResult Prepare(Offer offer)
    {
        var (del, cps) = offer.Bind();
        return new IlPredicateCompiler.PgoCompileResult(del, -1, cps);
    }

    private void CountBound(int functorId, BoundCps waiting)
    {
        if (waiting.Requested)
        {
            if (!_completedCompiles.IsEmpty) DrainCompletedCompiles();
            return;
        }
        // Threshold zero: nothing waits for its calls.
        if (PromotionsSuspended
            || (PersistedThreshold > 0
                && (++waiting.Count < PersistedThreshold || !Earned(waiting.Cost, waiting.Count))))
            return;
        RequestBound(functorId, waiting);
    }

    // Not in CountBound, for Request's reason.
    private void RequestBound(int functorId, BoundCps waiting)
    {
        waiting.Requested = true;
        _persistedTaken += waiting.Cost;
        _mutationStamp.TryGetValue(functorId, out int stamp);
        _pendingCps.Add(functorId);
        var del = waiting.Delegate;
        var bind = waiting.Bind;
        IlCompileWorker.RunAsync(
            () => (object?)new IlPredicateCompiler.PgoCompileResult(del, -1, bind()),
            (result, _) => _completedCompiles.Enqueue(new CompletedCompile(
                functorId, (IlPredicateCompiler.PgoCompileResult?)result, stamp, null, false, CpsOnly: true)),
            lowPriority: true);
    }

    // The caller waits for the worker: an offer's code, installed now.
    private bool InstallNow(Offer offer)
    {
        _mutationStamp.TryGetValue(offer.FunctorId, out int stamp);
        Withdraw(offer);
        if (stamp != offer.Stamp || _delegates.ContainsKey(offer.FunctorId)) return false;
        var prepared = RunOnLargeStack(() => Prepare(offer));
        if (!offer.Wakes) _bound.Add(offer.FunctorId);
        InstallDelegate(offer.FunctorId,
            offer.IsDynamic ? GuardDynamicSnapshot(offer.FunctorId, prepared.Delegate) : prepared.Delegate);
        if (prepared.Cps is { } cps) _cpsCode[offer.FunctorId] = cps;
        return true;
    }

    /// <summary>Loads and compiles, now, the code of every standing offer, and
    /// the continuation methods of every delegate bound at load: for a host
    /// that pays at load instead, and for tests. The caller waits.</summary>
    public void PromoteOffers()
    {
        foreach (var offer in _offers.Values.ToList())
            if (InstallNow(offer))
                OnPromotionInstalled?.Invoke(offer.FunctorId, _delegates[offer.FunctorId]);
        foreach (var (functorId, waiting) in _boundCps.ToList())
        {
            _boundCps.Remove(functorId);
            if (!_delegates.TryGetValue(functorId, out var current) || !ReferenceEquals(current, waiting.Delegate))
                continue;
            if (RunOnLargeStack(waiting.Bind) is not { } cps) continue;
            _cpsCode[functorId] = cps;
            _dispatchWrappers.Remove(functorId);
            _resumeWrappers.Remove(functorId);
            OnPromotionInstalled?.Invoke(functorId, current);
        }
    }

    // A failure here leaves the predicate on its delegate: continuation
    // methods are an addition, never a condition of promotion.
    private IlPredicateCompiler.CpsCode? TryCompileCps(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, PredicateDelegate del)
    {
        if (!IlPredicateCompiler.CpsMode) return null;
        try
        {
            using var wam = IlPredicateCompiler.WamChoicePoints(true);
            var code = Compiler.CompileCps(predicate, calleeMap, del);
            if (PromotionDiag)
                Console.Error.WriteLine($"[tier1] cps fid={predicate.FunctorId} "
                    + (code is null ? "none" : $"entry=0x{code.Entry:X} continuations={code.Resumes.Length}"));
            return code;
        }
        catch (Exception ex)
        {
            if (PromotionDiag) Console.Error.WriteLine($"[tier1] cps failed: {ex.Message}");
            return null;
        }
    }

    private void InstallDelegate(int functorId, PredicateDelegate del)
    {
        _delegates[functorId] = del;
        NoteCode(functorId, CodeInstalled);
        _dispatchWrappers.Remove(functorId);
        _resumeWrappers.Remove(functorId);
        if (PromotionDiag)
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(functorId);
            Console.Error.WriteLine(
                $"[tier1] promoted {Shumway.Core.AtomTable.GetById(atomId)?.Name}/{arity}");
        }
    }

    /// <summary>The cached <c>engine => del(engine, 0)</c> dispatch wrapper, or null
    /// when not promoted.</summary>
    internal Func<Activation, bool>? TryGetDispatchWrapper(int functorId)
    {
        if (_dispatchWrappers.TryGetValue(functorId, out var w)) return w;
        return _delegates.TryGetValue(functorId, out var del) ? NewDispatchWrapper(functorId, del) : null;
    }

    // Not in TryGetDispatchWrapper, for Request's reason.
    private Func<Activation, bool> NewDispatchWrapper(int functorId, PredicateDelegate del)
    {
        Func<Activation, bool> wrapper;
        if (_cpsCode.TryGetValue(functorId, out var cps))
        {
            var entry = cps.EntryDelegate;
            wrapper = engine => entry(engine, 0);
        }
        else if (_boundCps.TryGetValue(functorId, out var waiting) && ReferenceEquals(waiting.Delegate, del))
            wrapper = engine =>
            {
                CountBound(functorId, waiting);
                return del(engine, 0);
            };
        else wrapper = engine => del(engine, 0);
        _dispatchWrappers[functorId] = wrapper;
        return wrapper;
    }

    /// <summary>The cached resume wrapper (delegate invoked at a cursor), or null
    /// when not promoted.</summary>
    internal Func<Activation, int, bool>? TryGetResumeWrapper(int functorId)
    {
        if (_resumeWrappers.TryGetValue(functorId, out var w)) return w;
        if (!_delegates.TryGetValue(functorId, out var del)) return null;
        var wrapper = TableEntry(functorId, del);
        _resumeWrappers[functorId] = wrapper;
        return wrapper;
    }

    // Rejection reason per unpromotable predicate, for the coverage diagnostics.
    private readonly Dictionary<int, string> _unpromotableReason = new();

    // On the worker: a delegate's method compiles here, not at its first call
    // on the engine's thread.
    private static PredicateDelegate Compiled(PredicateDelegate del)
    {
        RuntimeHelpers.PrepareDelegate(del);
        return del;
    }

    private static IlPredicateCompiler.PgoCompileResult Compiled(IlPredicateCompiler.PgoCompileResult result)
    {
        RuntimeHelpers.PrepareDelegate(result.Delegate);
        return result;
    }

    private void MarkUnpromotable(int functorId, string reason)
    {
        _unpromotable.Add(functorId);
        _unpromotableReason[functorId] = reason;
    }

    // Where there is no runtime codegen (Native AOT, browser-wasm) the store stays
    // a pure Tier-0 counter and the IL compiler (reflection-heavy type init) is
    // never constructed. See RuntimeCaps for why the browser needs more than
    // IsDynamicCodeSupported to be excluded.
    //
    // Forwards to the property on every read rather than caching it in a static
    // field: RuntimeCaps.SupportsRuntimeCodegen is a trimmer feature switch, and a
    // cached field is opaque to the trimmer — the guarded branches would survive
    // and drag the IL compiler into a build that can never run them.
    private static bool DynamicCodeSupported => Shumway.Core.RuntimeCaps.SupportsRuntimeCodegen;

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

    /// <summary>ADR-063: supplies the image pool a predicate's images index:
    /// the wasm tier bakes each image into its module, the IL tier follows
    /// an image's cells for the read-only form of a head stream. Null,
    /// predicates with images stay Tier-0 on wasm and keep the stream's
    /// helpers on IL.</summary>
    public Func<int, System.Collections.Generic.IReadOnlyList<TermImage>?>? ImagePoolProvider { get; set; }

    /// <summary>Supplies the packed-text pool a predicate's <c>get_pstr</c> /
    /// <c>put_pstr</c> index; the emit puts each text in the method's literal
    /// table. Null, predicates with packed text stay Tier-0.</summary>
    public Func<int, System.Collections.Generic.IReadOnlyList<TextLiteral>?>? StringPoolProvider { get; set; }

    /// <summary>The pools a compile of <paramref name="target"/> reads. A
    /// snapshot of a dynamic predicate carries its own, frozen when it was
    /// built: the pools may be swept while it compiles on the worker.</summary>
    private IlPredicateCompiler.LiteralPoolSet PoolsFor(int functorId, CompiledPredicate? target)
        => target?.LiteralView is { } view
            ? new(view.FloatView, view.ImageView, view.StringView)
            : new(FloatPoolProvider?.Invoke(functorId), ImagePoolProvider?.Invoke(functorId),
                StringPoolProvider?.Invoke(functorId));

    private static IlPredicateCompiler.LiteralPoolSet EnterPools(IlPredicateCompiler.LiteralPoolSet pools)
        => IlPredicateCompiler.BeginPools(pools);

    private static void LeavePools(IlPredicateCompiler.LiteralPoolSet prev)
        => IlPredicateCompiler.EndPools(prev);

    private static T WithPools<T>(IlPredicateCompiler.LiteralPoolSet pools, Func<T> compile)
    {
        var prev = EnterPools(pools);
        try { return compile(); }
        finally { LeavePools(prev); }
    }

    private T WithFloatPool<T>(int functorId, Func<T> compile) => WithPools(PoolsFor(functorId, null), compile);

    // The predicates whose code a bundle carries, bound or offered. A region
    // compiled at run time calls them: absorbing one would compile its body
    // again, and the calls inside the region would bypass the bundle's code.
    private readonly HashSet<int> _bundleCode = new();

    // What a compile keeps out of its region: the bundle's code (forced
    // roots) and, past IlRegionCost.ReinlineLimit, the code there is when the
    // compile begins, which the region calls instead.
    private sealed class CompileRoots
    {
        public int Fid;
        public ISet<int>? Forced;
        // Read once, on the compiling thread: a root's continuation methods
        // (ADR-061) plan against the set its delegate planned against.
        public ISet<int>? Installed;
        public bool Resolved;
    }

    // On the engine's thread.
    private CompileRoots RootsFor(int functorId)
    {
        ISet<int>? forced = null;
        if (_bundleCode.Count > 0)
        {
            forced = new HashSet<int>(_bundleCode);
            forced.Remove(functorId);
        }
        return new CompileRoots { Fid = functorId, Forced = forced };
    }

    // The functor ids with code, or with code coming, and how: a delegate
    // installed, a compile in flight, a member of a region the worker has
    // emitted and the drain has not installed yet (its alias comes with the
    // region). _delegates is the engine thread's; this is what the worker reads.
    private readonly Dictionary<int, byte> _withCode = new();
    private const byte CodeInstalled = 1, CodePending = 2, CodeAsMember = 4;

    private void NoteCode(int functorId, byte how)
    {
        lock (_withCode) _withCode[functorId] = (byte)(_withCode.GetValueOrDefault(functorId) | how);
    }

    private void ForgetCode(int functorId, byte how)
    {
        lock (_withCode)
        {
            if (!_withCode.TryGetValue(functorId, out var f)) return;
            f &= (byte)~how;
            if (f == 0) _withCode.Remove(functorId); else _withCode[functorId] = f;
        }
    }

    // On the worker, after an emission: the members get aliases at the drain.
    private void NoteMembersEmitted(CalleeRecorder? callees)
    {
        if (callees?.RegionMembers is not { Count: > 0 } members) return;
        foreach (var (member, _) in members)
            if (!IsExcludedFromPromotion(member)) NoteCode(member, CodeAsMember);
    }

    // On the worker, before a compile: a predicate that became a region's
    // member since it was queued runs that region, entered at its block; code
    // of its own would be the same code again, never called.
    private bool HasCodeComing(int functorId)
    {
        lock (_withCode)
            return _withCode.TryGetValue(functorId, out var f) && (f & (CodeInstalled | CodeAsMember)) != 0;
    }

    // At the drain: the shadow follows what was installed.
    private void ReconcileCode(int functorId, CalleeRecorder? callees)
    {
        lock (_withCode)
        {
            Reconcile(functorId);
            if (callees?.RegionMembers is { Count: > 0 } members)
                foreach (var (member, _) in members) Reconcile(member);
        }
        void Reconcile(int fid)
        {
            byte f = (byte)((_delegates.ContainsKey(fid) ? CodeInstalled : 0)
                            | (_pendingCompiles.Contains(fid) ? CodePending : 0));
            if (f == 0) _withCode.Remove(fid); else _withCode[fid] = f;
        }
    }

    private ISet<int>? InstalledFor(CompileRoots roots)
    {
        if (roots.Resolved) return roots.Installed;
        HashSet<int> s;
        lock (_withCode) s = new HashSet<int>(_withCode.Keys);
        s.Remove(roots.Fid);
        roots.Installed = s.Count > 0 ? s : null;
        roots.Resolved = true;
        return roots.Installed;
    }

    // On the compiling thread.
    private T WithRoots<T>(CompileRoots roots, Func<T> compile)
    {
        var prev = IlPredicateCompiler.RegionForcedRootFids;
        var prevInstalled = IlPredicateCompiler.RegionInstalledFids;
        var forced = roots.Forced;
        if (forced is not null && prev is not null)
        {
            var both = new HashSet<int>(prev);
            both.UnionWith(forced);
            forced = both;
        }
        if (forced is not null) IlPredicateCompiler.RegionForcedRootFids = forced;
        IlPredicateCompiler.RegionInstalledFids = InstalledFor(roots);
        try { return compile(); }
        finally
        {
            IlPredicateCompiler.RegionForcedRootFids = prev;
            IlPredicateCompiler.RegionInstalledFids = prevInstalled;
        }
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
    public void EvictDelegate(int functorId) => EvictDelegate(functorId, mutation: true);

    /// <summary>Evicts, counting toward the churn pin only when
    /// <paramref name="mutation"/>: a code space rebuilt under a snapshot is
    /// not the predicate changing.</summary>
    internal void EvictDelegate(int functorId, bool mutation)
    {
        EvictionStamp++;
        // A mutation breaks the churn-pinned mutation-free streak and invalidates
        // any background compile in flight (its snapshot predates the mutation;
        // the drain discards a stale stamp) — with or without a live delegate.
        _churnQuietCalls.Remove(functorId);
        _mutationStamp.TryGetValue(functorId, out int stamp);
        _mutationStamp[functorId] = stamp + 1;
        // Kept for resumes only: a call that began before the eviction
        // finishes on the delegate it began with (ADR-054).
        if (_delegates.TryGetValue(functorId, out var retiring))
            _retiredResume[functorId] = (engine, cursor) => retiring(engine, cursor);
        _cpsCode.Remove(functorId);
        if (_offers.TryGetValue(functorId, out var withdrawn)) Withdraw(withdrawn);
        _bundleCode.Remove(functorId);
        _boundCps.Remove(functorId);
        ForgetCopies(functorId);
        if (_aliasRoot.Remove(functorId, out int root) && _aliasesOf.TryGetValue(root, out var siblings))
            siblings.Remove(functorId);
        if (_aliasesOf.Remove(functorId, out var aliased))
            foreach (int member in aliased)
            {
                _aliasRoot.Remove(member);
                EvictDelegate(member, mutation: false);
            }
        if (!_delegates.Remove(functorId)) return;
        ForgetCode(functorId, CodeInstalled);
        OnDelegateEvicted?.Invoke(functorId);
        _dispatchWrappers.Remove(functorId);
        _resumeWrappers.Remove(functorId);
        _counters.Remove(functorId);
        _pgoProfileKeys.Remove(functorId);
        _pgoOptimized.Remove(functorId);
        if (!mutation) return;
        _evictions.TryGetValue(functorId, out int e);
        _evictions[functorId] = e + 1;
    }

    private readonly Dictionary<int, Func<Activation, int, bool>> _retiredResume = new();

    /// <summary>The delegate a functor had when it was last evicted, for a
    /// resume into a call that began before (ADR-054); null when none. The
    /// interpreter asks only for a cursor past the entry, and only when the
    /// functor has no delegate now.</summary>
    internal Func<Activation, int, bool>? TryGetRetiredResumeWrapper(int functorId)
        => _retiredResume.TryGetValue(functorId, out var w) ? w : null;

    /// <summary>ADR-023's churn pin, for any tier promoting a dynamic
    /// snapshot: true while a predicate evicted <see cref="EvictionChurnLimit"/>
    /// times has not yet gone <see cref="ChurnRearmCalls"/> calls without a
    /// mutation. Each call here counts toward that streak.</summary>
    internal bool DynamicChurnPinned(int functorId)
    {
        if (!_evictions.TryGetValue(functorId, out int ev) || ev < EvictionChurnLimit)
            return false;
        _churnQuietCalls.TryGetValue(functorId, out int quiet);
        quiet++;
        if (quiet < ChurnRearmCalls)
        {
            _churnQuietCalls[functorId] = quiet;
            return true;   // pinned (not via _unpromotable — re-armable)
        }
        _churnQuietCalls.Remove(functorId);
        _evictions[functorId] = EvictionChurnLimit - 1;
        return false;
    }

    /// <summary>ADR-054: links a dynamic predicate's snapshot into the running
    /// code space for the wasm tier (<see cref="WasmPromotionStore"/>).</summary>
    internal Func<Activation, int, DynamicShadow>? ShadowSnapshotProvider { get; set; }

    // ADR-023 priming: a dynamic/visible predicate declared with source clauses is
    // read-hot and mutation-cold — promote on its first call (still fully evictable).
    private readonly HashSet<int> _primeImmediately = new();

    /// <summary>Marks <paramref name="functorId"/> for promote-on-first-call.</summary>
    public void MarkPrime(int functorId) => _primeImmediately.Add(functorId);

    /// <summary>Predicates whose bytecode exceeds this stay on Tier 0 when compiled
    /// synchronously — sync callers opted into bounded latency. See
    /// <see cref="EffectiveMaxBytecodeBytes"/>.</summary>
    public int MaxIlPromotionBytecodeBytes { get; set; } = 16384;

    /// <summary>Runs a compile on the shared large-stack worker (see
    /// <see cref="IlCompileWorker"/>).</summary>
    private static T RunOnLargeStack<T>(Func<T> work) => IlCompileWorker.RunSync(work);

    /// <summary>When true (default), a threshold-crossing compile is queued to the
    /// worker and the predicate stays on Tier-0 until the delegate drains in — the
    /// query thread never stalls on an IL emit. <see cref="IsPromoted"/> and
    /// <see cref="WaitForPendingPromotions"/> give tests deterministic settling.</summary>
    public bool BackgroundCompilation { get; set; } = true;

    /// <summary>Invoked on the engine thread when a fresh delegate installs (sync or
    /// drained). The engine patches remaining generic Call/Execute sites to
    /// CallIl/ExecuteIl so the rest of the running query dispatches directly.</summary>
    public Action<int, PredicateDelegate>? OnPromotionInstalled { get; set; }

    /// <summary>Invoked on the engine thread when a functor's delegate goes, so
    /// a running query's direct-dispatch tables let go of it too.</summary>
    public Action<int>? OnDelegateEvicted { get; set; }

    // In-flight background compiles (engine thread only) and their results
    // (worker → engine thread hand-off).
    private readonly HashSet<int> _pendingCompiles = new();
    // ADR-061: continuation methods compiled after their delegate is installed.
    private readonly HashSet<int> _pendingCps = new();
    private readonly ConcurrentQueue<CompletedCompile> _completedCompiles = new();
    private sealed record CompletedCompile(
        int Fid, IlPredicateCompiler.PgoCompileResult? Result, int Stamp, string? Error,
        bool IsDynamicSnapshot, bool CpsOnly = false, CalleeRecorder? Callees = null, int Epoch = 0);

    // Per-fid mutation stamp, bumped by EvictDelegate on every mutation: a background
    // compile whose snapshot was mutated while in flight must be discarded at drain
    // (installing it would violate the logical-update view with stale clauses).
    private readonly Dictionary<int, int> _mutationStamp = new();

    // The predicates each installed delegate copied into its code (a region's
    // members, inlined callees), and the reverse: a consult that changes one's
    // clauses evicts every delegate holding a copy, or it keeps running the old.
    private readonly Dictionary<int, HashSet<int>> _copies = new();
    private readonly Dictionary<int, HashSet<int>> _copiedBy = new();
    // Static clause changes, numbered: a background compile that looked at a
    // predicate changed after it started is stale when it lands.
    private int _staticEpoch;
    private readonly Dictionary<int, int> _staticChangedAt = new();
    // A region member with no delegate of its own runs its root's code, entered
    // at its member-entry cursor; it goes when the root does.
    private readonly Dictionary<int, List<int>> _aliasesOf = new();
    private readonly Dictionary<int, int> _aliasRoot = new();

    private static CalleeRecorder? Recording(IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
        => calleeMap is null ? null : new CalleeRecorder(calleeMap);

    /// <summary>The root whose region code <paramref name="functorId"/> runs,
    /// being a member with no code of its own; null otherwise.</summary>
    public int? RegionRootOf(int functorId)
        => _aliasRoot.TryGetValue(functorId, out int root) ? root : null;

    /// <summary>A consult gave <paramref name="functorId"/> new clauses, or
    /// replaced them: the delegates that copied the old ones go.</summary>
    public void StaticClausesChanged(int functorId)
    {
        _staticChangedAt[functorId] = ++_staticEpoch;
        if (_copiedBy.TryGetValue(functorId, out var holders))
            foreach (int holder in holders.ToList()) EvictDelegate(holder, mutation: false);
        Wasm?.OnStaticChanged(functorId);
    }

    /// <summary>True when a consult changed <paramref name="functorId"/>'s
    /// clauses after the program view of <paramref name="viewEpoch"/> was built.</summary>
    internal bool ChangedAfter(int functorId, int viewEpoch)
        => _staticChangedAt.TryGetValue(functorId, out int at) && at > viewEpoch;

    private bool ChangedSince(CalleeRecorder? callees, int epoch)
    {
        if (callees is null || epoch == _staticEpoch) return false;
        foreach (int fid in callees.LookedUp)
            if (_staticChangedAt.TryGetValue(fid, out int at) && at > epoch) return true;
        return false;
    }

    private void RecordCopies(int functorId, CalleeRecorder? callees)
    {
        if (callees is null) return;
        if (!_copies.TryGetValue(functorId, out var copied)) _copies[functorId] = copied = new HashSet<int>();
        foreach (int fid in callees.LookedUp)
        {
            if (fid == functorId || !copied.Add(fid)) continue;
            if (!_copiedBy.TryGetValue(fid, out var holders)) _copiedBy[fid] = holders = new HashSet<int>();
            holders.Add(functorId);
        }
    }

    private void ForgetCopies(int functorId)
    {
        if (!_copies.Remove(functorId, out var copied)) return;
        foreach (int fid in copied)
            if (_copiedBy.TryGetValue(fid, out var holders) && holders.Remove(functorId) && holders.Count == 0)
                _copiedBy.Remove(fid);
    }

    /// <summary>Gives each member of the region rooted at <paramref name="root"/>
    /// that has no code of its own the root's, entered where the member
    /// starts: compiling it again for its own callers would be the same code
    /// twice, and until then those callers ran it on the interpreter.</summary>
    private void InstallMemberAliases(int root, PredicateDelegate rootDelegate, CalleeRecorder? callees)
    {
        if (callees?.RegionMembers is not { Count: > 0 } members) return;
        foreach (var (member, cursor) in members)
        {
            if (_delegates.ContainsKey(member)
                || _offers.ContainsKey(member) || _unpromotable.Contains(member)
                || IsExcludedFromPromotion(member)
                || !callees.Inner.TryGetValue(member, out var memberCode) || memberCode.IsDebuggable)
                continue;
            int entry = cursor;
            // A resume (cursor > 0) by the member's id is a call that began on
            // code of its own, since retired; the root's cursors are its own.
            var earlier = _retiredResume.GetValueOrDefault(member);
            PredicateDelegate alias = (engine, c) => c == 0
                ? rootDelegate(engine, entry)
                : earlier is not null ? earlier(engine, c)
                : throw new InvalidOperationException(
                    $"resume at cursor {c} of region member {member}, which has no code of its own");
            InstallDelegate(member, alias);
            _aliasRoot[member] = root;
            if (!_aliasesOf.TryGetValue(root, out var aliased)) _aliasesOf[root] = aliased = new List<int>();
            aliased.Add(member);
            OnPromotionInstalled?.Invoke(member, alias);
        }
    }

    private void DrainCompletedCompiles()
    {
        while (_completedCompiles.TryDequeue(out var c))
        {
            if (c.CpsOnly)
            {
                _pendingCps.Remove(c.Fid);
                _mutationStamp.TryGetValue(c.Fid, out int stampNow);
                // Only for the delegate it was compiled against, still in place.
                if (stampNow == c.Stamp && c.Result is { Cps: { } lateCps } late
                    && _delegates.TryGetValue(c.Fid, out var current)
                    && ReferenceEquals(current, late.Delegate)
                    && !ChangedSince(c.Callees, c.Epoch))
                {
                    RecordCopies(c.Fid, c.Callees);
                    _boundCps.Remove(c.Fid);
                    _cpsCode[c.Fid] = lateCps;
                    _dispatchWrappers.Remove(c.Fid);
                    _resumeWrappers.Remove(c.Fid);
                    OnPromotionInstalled?.Invoke(c.Fid, current);
                }
                continue;
            }
            _pendingCompiles.Remove(c.Fid);
            try
            {
                // An installed delegate is never replaced: the choice points it
                // pushed resume through the functor's current delegate, at cursors
                // of its own code.
                bool installed = _delegates.ContainsKey(c.Fid);
                // A bundle's code that came from an offer: one compiled without
                // wake points wakes at its returns.
                if (_offers.TryGetValue(c.Fid, out var offered))
                {
                    Withdraw(offered);
                    if (c.Error is not null) offered.Failed?.Invoke(c.Error);
                    else if (!offered.Wakes && !installed) _bound.Add(c.Fid);
                }
                if (installed) continue;
                if (c.Error is not null)
                {
                    MarkUnpromotable(c.Fid, "compile-failed:" + c.Error);
                    continue;
                }
                // Skipped on the worker: a region's member since it was queued.
                // Its alias is in, or the region was not installed and it re-warms.
                if (c.Result is null) continue;
                _mutationStamp.TryGetValue(c.Fid, out int now);
                if (now != c.Stamp) continue;   // mutated mid-compile: stale, re-warm
                if (ChangedSince(c.Callees, c.Epoch))
                {
                    // A callee it copied changed after its view was built.
                    _staleForView[c.Fid] = c.Epoch;
                    continue;
                }
                var drainDel = c.IsDynamicSnapshot
                    ? GuardDynamicSnapshot(c.Fid, c.Result!.Value.Delegate)
                    : c.Result!.Value.Delegate;
                InstallDelegate(c.Fid, drainDel);
                RecordCopies(c.Fid, c.Callees);
                if (c.Result.Value.Cps is { } drainCps) _cpsCode[c.Fid] = drainCps;
                if (c.Result.Value.ProfileKey >= 0) _pgoProfileKeys[c.Fid] = c.Result.Value.ProfileKey;
                OnPromotionInstalled?.Invoke(c.Fid, drainDel);
                // A dynamic snapshot goes at its next mutation, and its aliases with it.
                if (!c.IsDynamicSnapshot) InstallMemberAliases(c.Fid, drainDel, c.Callees);
            }
            finally { ReconcileCode(c.Fid, c.Callees); }
        }
    }

    /// <summary>True while any background compile is in flight.</summary>
    public bool HasPendingPromotions => _pendingCompiles.Count > 0 || _pendingCps.Count > 0;

    /// <summary>Waits until every in-flight background compile has completed and been
    /// installed. False on timeout. Engine-thread only.</summary>
    public bool WaitForPendingPromotions(int timeoutMs = 10_000)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            DrainCompletedCompiles();
            if (_pendingCompiles.Count == 0 && _pendingCps.Count == 0) return true;
            if (deadline.ElapsedMilliseconds > timeoutMs) return false;
            System.Threading.Thread.Sleep(1);
        }
    }

    // PGO: an eligible promoted predicate carries a profile key until enough samples
    // accumulate, then recompiles to the optimised form and moves to _pgoOptimized.
    private readonly Dictionary<int, int> _pgoProfileKeys = new();
    private readonly HashSet<int> _pgoOptimized = new();

    /// <summary>What <see cref="Threshold"/> starts at.</summary>
    public const int DefaultThreshold = 32;

    /// <summary>Invocation count before an IL compile is attempted
    /// (<see cref="DefaultThreshold"/> unless set). 0 or less turns the tier
    /// off: the engine runs Tier-0 only.</summary>
    public int Threshold
    {
        get => _threshold;
        set
        {
            bool flip = (_threshold <= 0) != (value <= 0);
            _threshold = value;
            if (!flip) return;
            FollowTier();
            PromotabilityChanged?.Invoke();
        }
    }
    private int _threshold = DefaultThreshold;

    /// <summary>Whether compiled IL may run: the tier is on and this runtime
    /// can generate code.</summary>
    public bool IlTierActive => Threshold > 0 && DynamicCodeSupported;

    /// <summary>Fires when the answer of <see cref="IsPermanentlyBytecodeOnly"/>
    /// may have changed for every predicate: the tier turned on or off, a
    /// wasm store was attached. The linker bakes that answer into the
    /// persistent program as CallBytecode sites, which skip dispatch for
    /// good -- a tier enabled afterwards would count nothing and promote
    /// nothing below the top level until the next consult -- so the engine
    /// invalidates the persistent program here and the next query relinks.
    /// </summary>
    internal Action? PromotabilityChanged { get; set; }

    /// <summary>Profile samples required before the phase-2 PGO recompile.</summary>
    public int PgoSampleThreshold { get; set; } = 32;

    /// <summary>jit_compile/1's implementation: sets the promotion threshold
    /// of the tier this build has, and reports whether there was one to set.
    ///
    /// <para>A build has exactly one Tier-1. When a wasm store is attached it
    /// is that one (WebShumway, and the desktop differential tests, where
    /// wasm is what is under test); otherwise Tier-1 is the IL compiler, which
    /// needs runtime codegen and so does not exist under Native AOT.</para>
    ///
    /// <para>0 stops further promotion at once -- safe, since no live
    /// delegate is touched -- and queues the return of what already promoted.
    /// Dropping those here would strand a choice point created inside Tier-1
    /// code with nowhere to redo, so the eviction waits for the next query
    /// setup: "off" governs the goals after it.</para></summary>
    public bool SetJitThreshold(int threshold)
    {
        // A host whose tier needs more than a threshold supplies its own
        // policy: WebShumway attaches its world lazily and, for "all",
        // compiles the whole program up front rather than billing the user's
        // first real query for it. Kept on the store and not on the
        // activation, which query setup rebinds.
        if (JitPolicy is { } policy) return policy(threshold);
        if (_wasm is not null)
        {
            // An attached store that can promote is proof enough that this
            // build can: RuntimeCaps describes the browser's capability, and
            // the desktop differential tests attach a world without it.
            if (threshold > 0 && _wasm.Promoter is null && _wasm.BatchPromoter is null)
                return false;
            _wasm.Threshold = threshold;
        }
        else
        {
            if (threshold > 0 && !Shumway.Core.RuntimeCaps.SupportsRuntimeCodegen) return false;
            Threshold = threshold;
        }
        if (threshold == 0) _jitOffPending = true;
        return true;
    }

    /// <summary>The host's own jit_compile/1 implementation, when attaching
    /// or configuring the tier is more than setting a threshold. It owns the
    /// whole decision, including whether the mode could be established at
    /// all; <see cref="QueueJitOff"/> is how it asks for the eviction.
    /// </summary>
    public Func<int, bool>? JitPolicy { get; set; }

    /// <summary>jit_compile(cps) and jit_compile(nocps). A host whose tier
    /// can change form in a live session supplies <see cref="JitFormPolicy"/>
    /// (WebShumway's wasm tier). Without one only the form the process
    /// started with holds: the IL compiler's comes from SHUMWAY_IL_CPS, and
    /// what it compiled is cached across the process, so a switch here would
    /// leave the cached predicates in the form they had.</summary>
    public bool SetJitForm(bool cps)
    {
        if (JitFormPolicy is { } policy) return policy(cps);
        return cps == Shumway.Compiler.Il.IlPredicateCompiler.CpsMode;
    }

    /// <summary>The host's own jit_compile(cps|nocps), when its tier can
    /// change form in a live session.</summary>
    public Func<bool, bool>? JitFormPolicy { get; set; }

    /// <summary>Queues the return of every promoted predicate to its
    /// bytecode, for a host policy that turned the tier off.</summary>
    public void QueueJitOff() => _jitOffPending = true;

    private bool _jitOffPending;

    /// <summary>Applies a queued jit_compile(off): every promoted predicate
    /// goes back to its bytecode. Called at query setup, where no choice
    /// point holds a position inside Tier-1 code.</summary>
    public void ApplyPendingJitChange()
    {
        if (!_jitOffPending) return;
        _jitOffPending = false;
        foreach (int fid in PromotedFunctorIds().ToList()) EvictDelegate(fid);
        PromotabilityChanged?.Invoke();
    }

    /// <summary>The wasm tier's promotion state, when a world wired one
    /// (browser boot; desktop differential tests). Its delegates install into
    /// this store's table, so dispatch and eviction are shared.</summary>
    public WasmPromotionStore? Wasm
    {
        get => _wasm;
        set
        {
            bool flip = (_wasm is { Enabled: true }) != (value is { Enabled: true });
            _wasm = value;
            if (!flip) return;
            FollowTier();
            PromotabilityChanged?.Invoke();
        }
    }
    private WasmPromotionStore? _wasm;

    /// <summary>Relocatable wasm modules of loaded bundles, not yet installed:
    /// <see cref="WasmPromotionStore.InstallPendingBundles"/> drains this
    /// once a static link exists. Stays queued (a handful of references)
    /// while no tier is attached.</summary>
    public List<byte[]> PendingWasmModules { get; } = new();

    /// <summary>The wasm store's <see cref="WasmPromotionStore.Enabled"/>
    /// flipped in place (its threshold set to or from zero).</summary>
    internal void WasmEnabledChanged()
    {
        FollowTier();
        PromotabilityChanged?.Invoke();
    }

    /// <summary>The delegate bound to <paramref name="functorId"/>, or null.</summary>
    public PredicateDelegate? TryGet(int functorId)
        => _delegates.TryGetValue(functorId, out var d) ? d : null;

    /// <summary>Records one invocation; compiles and returns the delegate when the
    /// count crosses the threshold. Null otherwise (under-threshold, in-flight,
    /// unpromotable, or promotion disabled). An engine has one Tier-1: with a
    /// wasm tier attached the IL compiler promotes nothing.</summary>
    /// <param name="viewEpoch">The <see cref="StaticEpoch"/> the program view
    /// <paramref name="calleeMap"/> was built at; null when it is current.</param>
    public PredicateDelegate? RecordInvocation(int functorId, CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null, int? viewEpoch = null)
        => RecordInvocation(functorId, predicate, calleeMap, viewEpoch, out _);

    /// <summary>As above; <paramref name="settled"/> is true when a null
    /// answer holds for the rest of the program view: the predicate is
    /// unpromotable, the view's copy of it is stale, or it is a dynamic
    /// predicate with no snapshot this query (no clauses, or mutated since
    /// the query's setup, which makes it stale until the next one). A count
    /// below the threshold, a compile in flight or a churn pin is not
    /// settled: a later call may promote.</summary>
    internal PredicateDelegate? RecordInvocation(int functorId, CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, int? viewEpoch, out bool settled)
    {
        settled = false;
        if (!IlTierOn || !DynamicCodeSupported) return null;
        if (!_completedCompiles.IsEmpty) DrainCompletedCompiles();
        if (_delegates.ContainsKey(functorId)) return _delegates[functorId];
        // Mid-consult, no new promotions: the program is still growing — a
        // predicate promoted now (the expansion hooks are the hot case: one
        // term_expansion call per consulted clause) would snapshot a clause set
        // a later file in the same load extends. Already-promoted delegates
        // keep serving (consult-commit eviction drops any this load extends);
        // counting resumes when the outermost consult finishes.
        if (PromotionsSuspended) return null;
        if (_unpromotable.Contains(functorId)) { settled = true; return null; }
        if (_pendingCompiles.Contains(functorId)) return null;   // compile in flight
        if (IsExcludedFromPromotion(functorId)) { MarkUnpromotable(functorId, "query"); settled = true; return null; }

        // ADR-023 — a dynamic predicate promotes as a snapshot of its visible
        // clauses; mutation evicts it. Churn-pinned predicates stay Tier-0 until
        // the re-arm streak completes.
        bool isDynamic = IsExcludedByLayout(predicate);
        if (isDynamic && DynamicChurnPinned(functorId)) return null;

        _counters.TryGetValue(functorId, out int count);
        count++;
        _counters[functorId] = count;

        int effectiveThreshold = _primeImmediately.Contains(functorId) ? 1 : Threshold;
        if (count < effectiveThreshold) return null;
        // Its compile from this view copied a predicate changed since: the
        // count stands, and a later view promotes it.
        int epoch = viewEpoch ?? _staticEpoch;
        // A view built at the current epoch has nothing newer than it.
        if (epoch != _staticEpoch)
        {
            if (_staleForView.TryGetValue(functorId, out int staleView) && staleView == epoch) { settled = true; return null; }
            // The view's own copy of it predates its new clauses.
            if (ChangedAfter(functorId, epoch)) { settled = true; return null; }
        }
        return CompileAtThreshold(functorId, predicate, calleeMap, isDynamic, epoch, out settled);
    }

    // Predicates whose compile copied a callee changed after the program view
    // it compiled from was built, and that view's epoch.
    private readonly Dictionary<int, int> _staleForView = new();

    /// <summary>How many consult-time static clause changes there have been: a
    /// program view records it when built.</summary>
    public int StaticEpoch => _staticEpoch;

    // Not in RecordInvocation, for Request's reason.
    private PredicateDelegate? CompileAtThreshold(int functorId, CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, bool isDynamic, int viewEpoch, out bool settled)
    {
        settled = false;
        // ADR-035: debuggable code stays on the interpreter. Not left to the
        // debug opcodes the compiler refuses: a fact or a rule ending in a
        // builtin carries none, and compiled, a breakpoint in it never fires.
        if (predicate.IsDebuggable) { settled = true; return null; }
        // A null snapshot (no visible clauses, or clauses the query's setup
        // has not seen) is a retry at the next query, not a rejection: an
        // assertz now leaves the predicate stale until that setup.
        CompiledPredicate target = predicate;
        if (isDynamic)
        {
            var snapshot = DynamicSnapshotProvider?.Invoke(functorId);
            if (snapshot is null) { settled = true; return null; }
            target = snapshot;
        }

        if (IsExcludedBySize(target))
        {
            MarkUnpromotable(functorId, isDynamic ? "dynamic-size" : "size");
            settled = true;
            return null;
        }

        // CanCompile consults the pools, so establish them on this thread too
        // (the worker-thread emit sets its own).
        var targetPools = PoolsFor(functorId, target);
        var prevPools = EnterPools(targetPools);
        bool canCompile;
        string? rejection = null;
        try
        {
            canCompile = Compiler.CanCompile(target, calleeMap);
            if (!canCompile) rejection = Compiler.DescribeRejection(target, calleeMap);
        }
        finally { LeavePools(prevPools); }
        if (!canCompile)
        {
            MarkUnpromotable(functorId, "cannot-compile:" + rejection);
            settled = true;
            return null;
        }

        if (BackgroundCompilation)
        {
            // The engine-state-reading providers are invoked here, on the engine
            // thread, and their values captured — the worker must not touch engine
            // state (List<T> reads racing an Add are unsafe).
            var pools = targetPools;
            var nativeCtx = NativeInlineProvider?.Invoke();
            _mutationStamp.TryGetValue(functorId, out int stamp);
            var capturedTarget = target;
            var capturedCallees = calleeMap;
            var callees = Recording(calleeMap);
            int epoch = viewEpoch;
            var roots = RootsFor(functorId);
            _pendingCompiles.Add(functorId);
            NoteCode(functorId, CodePending);
            // An instrumented (PGO) delegate has its own cursor layout:
            // continuation methods compiled from the plain form would push
            // choice points it cannot resume.
            bool wantCps = !isDynamic && IlPredicateCompiler.CpsMode;
            if (wantCps) _pendingCps.Add(functorId);
            T WithContexts<T>(Func<T> body)
            {
                var prevPools = EnterPools(pools);
                var prevN = IlPredicateCompiler.BeginNativeInline(nativeCtx);
                try { return WithRoots(roots, body); }
                finally
                {
                    IlPredicateCompiler.EndNativeInline(prevN);
                    LeavePools(prevPools);
                }
            }
            IlCompileWorker.RunAsync(
                () => WithContexts(() =>
                {
                    // ADR-061: the delegate and its continuation methods push
                    // the same choice points (WAM, with resume markers).
                    if (HasCodeComing(functorId)) return null;
                    using var wam = IlPredicateCompiler.WamChoicePoints(wantCps);
                    var compiled = Compiled(Compiler.CompileInstrumented(capturedTarget, callees));
                    NoteMembersEmitted(callees);
                    return (object?)compiled;
                }),
                (result, error) =>
                {
                    var r = (IlPredicateCompiler.PgoCompileResult?)result;
                    _completedCompiles.Enqueue(new CompletedCompile(
                        functorId, r, stamp, error?.Message, isDynamic, Callees: callees, Epoch: epoch));
                    // ADR-061: the delegate is in use while its continuation
                    // methods compile, which takes several emissions.
                    if (!wantCps) return;
                    if (r is not { ProfileKey: < 0 } plain)
                    {
                        _completedCompiles.Enqueue(new CompletedCompile(
                            functorId, null, stamp, null, false, CpsOnly: true));
                        return;
                    }
                    var cpsCallees = Recording(capturedCallees);
                    IlCompileWorker.RunAsync(
                        () => WithContexts(() => (object?)(plain with
                        {
                            Cps = TryCompileCps(capturedTarget, cpsCallees, plain.Delegate),
                        })),
                        (cpsResult, _) => _completedCompiles.Enqueue(new CompletedCompile(
                            functorId, (IlPredicateCompiler.PgoCompileResult?)cpsResult, stamp,
                            null, false, CpsOnly: true, Callees: cpsCallees, Epoch: epoch)),
                        lowPriority: true);
                });
            return null;
        }

        var syncRoots = RootsFor(functorId);
        var syncCallees = Recording(calleeMap);
        var syncResult = RunOnLargeStack(() =>
            WithPools(targetPools, () =>
                WithNativeInline(() => WithRoots(syncRoots, () =>
                {
                    using var wam = IlPredicateCompiler.WamChoicePoints(!isDynamic && IlPredicateCompiler.CpsMode);
                    return Compiled(Compiler.CompileInstrumented(target, syncCallees));
                }))));
        if (ChangedSince(syncCallees, viewEpoch))
        {
            _staleForView[functorId] = viewEpoch;
            settled = true;
            return null;
        }
        var installedDel = isDynamic
            ? GuardDynamicSnapshot(functorId, syncResult.Delegate)
            : syncResult.Delegate;
        InstallDelegate(functorId, installedDel);
        RecordCopies(functorId, syncCallees);
        var syncCpsCallees = Recording(calleeMap);
        if (!isDynamic && syncResult.ProfileKey < 0 && RunOnLargeStack(() => WithPools(targetPools, () =>
                WithNativeInline(() => WithRoots(syncRoots,
                    () => TryCompileCps(target, syncCpsCallees, syncResult.Delegate))))) is { } syncCps)
        {
            _cpsCode[functorId] = syncCps;
            RecordCopies(functorId, syncCpsCallees);
        }
        if (syncResult.ProfileKey >= 0)
            _pgoProfileKeys[functorId] = syncResult.ProfileKey;
        OnPromotionInstalled?.Invoke(functorId, installedDel);
        if (!isDynamic) InstallMemberAliases(functorId, installedDel, syncCallees);
        return installedDel;
    }

    /// <summary>ADR-023 — wraps a dynamic-snapshot delegate so it self-guards against
    /// staleness: eviction clears every table, but a reference already hoisted into a
    /// running frame survives, and a pre-mutation snapshot answering a fresh call
    /// violates the logical update view. On a fresh entry (cursor 0) with the
    /// mutation stamp moved, the guard self-evicts and redirects to the live Tier-0
    /// chain (SetPc + IlTailCallPending — the tail contract every dispatch site
    /// honours). A resume (cursor &gt; 0) deliberately keeps the old snapshot: a call
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
    /// <summary>True when a PGO recompile could actually run — lets query setup
    /// skip building the O(all-predicates) functor-keyed lookup in the common
    /// no-pending-profiles case.</summary>
    public bool HasPgoWork => (_pgoProfileKeys.Count > 0 || !_pgoReady.IsEmpty) && DynamicCodeSupported;

    // Optimised forms the worker finished, for the next query setup.
    private readonly ConcurrentQueue<(int Fid, PredicateDelegate? Delegate, int Stamp)> _pgoReady = new();

    public void ConsiderPgoRecompiles(
        IReadOnlyDictionary<int, CompiledPredicate> predicateLookup,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        if (!DynamicCodeSupported) return;
        while (_pgoReady.TryDequeue(out var ready))
        {
            _mutationStamp.TryGetValue(ready.Fid, out int now);
            if (ready.Delegate is null || now != ready.Stamp || !_delegates.ContainsKey(ready.Fid)) continue;
            InstallDelegate(ready.Fid, ready.Delegate);
            _pgoOptimized.Add(ready.Fid);
        }
        if (_pgoProfileKeys.Count == 0) return;
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
            // The profile was recorded on the shape promotion compiled — for a
            // dynamic predicate that is the ADR-023 static snapshot, but the
            // program's entry for its fid is the dynamic-dispatch form
            // (enter_dynamic + check_visible), which is not IL-compilable and
            // would throw here. A shape that no longer compiles cannot take an
            // optimized recompile at all: drop the profile and keep whatever
            // delegate is installed (the snapshot re-warms and re-profiles on
            // its own). Guarding here mirrors how ordinary promotion guards
            // with CanCompile before Compile.
            if (!WithFloatPool(functorId, () =>
                    WithNativeInline(() => Compiler.CanCompile(predicate, calleeMap))))
            {
                Shumway.Compiler.Il.IlProfileCounters.Release(profileKey);
                _pgoProfileKeys.Remove(functorId);
                continue;
            }
            if (BackgroundCompilation)
            {
                // Compiled on the worker, the engine not waiting: the query
                // setup that finds it ready swaps it in, where this one would
                // have. The providers read engine state, so they run here.
                var pgoPools = PoolsFor(functorId, predicate);
                var nativeCtx = NativeInlineProvider?.Invoke();
                var pgoRoots = RootsFor(functorId);
                _mutationStamp.TryGetValue(functorId, out int stamp);
                _pgoProfileKeys.Remove(functorId);
                IlCompileWorker.RunAsync(
                    () =>
                    {
                        var prevPools = EnterPools(pgoPools);
                        var prevN = IlPredicateCompiler.BeginNativeInline(nativeCtx);
                        try
                        {
                            return WithRoots(pgoRoots,
                                () => Compiled(Compiler.CompileOptimized(predicate, profileKey, calleeMap)));
                        }
                        finally
                        {
                            IlPredicateCompiler.EndNativeInline(prevN);
                            LeavePools(prevPools);
                        }
                    },
                    (result, _) => _pgoReady.Enqueue((functorId, result as PredicateDelegate, stamp)),
                    lowPriority: true);
                continue;
            }
            var syncPgoRoots = RootsFor(functorId);
            var optimized = RunOnLargeStack(
                () => WithFloatPool(functorId, () => WithNativeInline(() => WithRoots(syncPgoRoots,
                    () => Compiled(Compiler.CompileOptimized(predicate, profileKey, calleeMap))))));
            InstallDelegate(functorId, optimized);
            _pgoProfileKeys.Remove(functorId);
            _pgoOptimized.Add(functorId);
        }
    }

    public bool IsPgoOptimized(int functorId) => _pgoOptimized.Contains(functorId);

    public bool IsPgoInstrumented(int functorId) => _pgoProfileKeys.ContainsKey(functorId);

    // The synthetic __query__/N wrappers have a different body per query under the
    // same functor id — caching one query's IL would replay it for every later query
    // of that arity.
    //
    // The same is true of the helpers a query stub synthesises for its `;`,
    // `->` and `\+`. MetaTransform names those with the reserved "$q" prefix
    // precisely so they are "REUSED query-to-query" and stay bounded
    // (MetaTransform.HelperPrefix) — which means '$q$disj_1'/5 is one functor
    // id carrying a different body every time. Promoting it is the same
    // replay bug, and on the wasm tier it also rebuilds the whole group
    // module on every query that contains a disjunction (measured: 13
    // seconds after consulting boards.pl).
    public static bool IsExcludedFromPromotion(int functorId)
    {
        var (atomId, _) = Shumway.Core.FunctorTable.Lookup(functorId);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        return name == "__query__" || IsQueryStubHelper(name);
    }

    /// <summary>A helper synthesised for the current query's stub: named
    /// <c>$q$kind_N</c>, module-mangled to <c>mod$$q$kind_N</c>.</summary>
    public static bool IsQueryStubHelper(string name)
        => IlPredicateCompiler.IsQueryStubHelper(name);

    // A bytecode body opening with enter_dynamic is mutation-driven dispatch
    // (per-clause check_visible + in-place chain patches, ADR-015): a cached IL
    // delegate of that form would not observe a mid-life retract. Such predicates
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
        if (_offers.TryGetValue(functorId, out var offer) && InstallNow(offer))
            return _delegates[functorId];
        if (_unpromotable.Contains(functorId)) return null;
        if (IsExcludedFromPromotion(functorId)
            || IsExcludedByLayout(predicate)
            || IsExcludedBySize(predicate))
        {
            _unpromotable.Add(functorId);
            return null;
        }
        bool warmCanCompile = WithFloatPool(functorId, () => Compiler.CanCompile(predicate, calleeMap));
        if (!warmCanCompile)
        {
            _unpromotable.Add(functorId);
            return null;
        }
        var warmRoots = RootsFor(functorId);
        var warmCallees = Recording(calleeMap);
        var del = RunOnLargeStack(() =>
            WithFloatPool(functorId, () => WithNativeInline(() => WithRoots(warmRoots, () =>
            {
                using var wam = IlPredicateCompiler.WamChoicePoints(IlPredicateCompiler.CpsMode);
                return Compiled(Compiler.Compile(predicate, warmCallees));
            }))));
        InstallDelegate(functorId, del);
        RecordCopies(functorId, warmCallees);
        var warmCpsCallees = Recording(calleeMap);
        if (RunOnLargeStack(() => WithFloatPool(functorId, () =>
                WithNativeInline(() => WithRoots(warmRoots,
                    () => TryCompileCps(predicate, warmCpsCallees, del))))) is { } warmCps)
        {
            _cpsCode[functorId] = warmCps;
            RecordCopies(functorId, warmCpsCallees);
        }
        InstallMemberAliases(functorId, del, warmCallees);
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
    /// first delegate wins. <paramref name="wakes"/>: it was compiled with wake
    /// points (ADR-049); else the engine wakes at its returns.
    /// <paramref name="fromBundle"/>: the code is a bundle's, which the regions
    /// compiled at run time call instead of absorbing.</summary>
    public void RegisterBoundDelegate(int functorId, PredicateDelegate del, bool wakes = false,
        bool fromBundle = false)
    {
        if (_delegates.ContainsKey(functorId)) return;
        if (fromBundle) _bundleCode.Add(functorId);
        if (!wakes) _bound.Add(functorId);
        if (_unpromotable.Contains(functorId)) _unpromotable.Remove(functorId);
        // With no tier on, the linker made every site bytecode-only; a
        // delegate bound by hand still has to be reached from them.
        if (Threshold <= 0 && Wasm is not { Enabled: true })
            PromotabilityChanged?.Invoke();
        InstallDelegate(functorId, del);
    }

    /// <summary>True when the functor was examined and rejected — no further compile
    /// attempts will fire.</summary>
    public bool IsUnpromotable(int functorId) => _unpromotable.Contains(functorId);

    // Delegates bound from a bundle's persisted IL compiled with no wake points.
    private readonly HashSet<int> _bound = new();

    /// <summary>ADR-049: whether the functor's delegate came bound from a
    /// bundle compiled without wake points, so that the engine wakes at its
    /// returns.</summary>
    public bool IsBound(int functorId) => _bound.Contains(functorId);

    /// <summary>True when this predicate can never have an IL delegate, decidable
    /// without a RecordInvocation. Lets the linker rewrite its call sites to
    /// CallBytecode immediately, skipping OnDispatch per dispatch.</summary>
    public bool IsPermanentlyBytecodeOnly(int functorId, CompiledPredicate predicate)
    {
        // An enabled wasm tier promotes through dispatch too: the static
        // CallBytecode rewrite would starve it of the dispatches it counts.
        if (Wasm is { Enabled: true }) return Wasm.IsUnpromotable(functorId);
        if (!DynamicCodeSupported) return true;
        if (_offers.ContainsKey(functorId)) return false;
        if (Threshold <= 0) return true;
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

    /// <summary>Multi-line promotion forensics for a machine one cannot attach
    /// to (the net48/x86 smoke prints this when zero predicates promoted):
    /// per tracked candidate its invocation count and state — a candidate
    /// stuck at "pending" with a dead or never-started worker is a compile
    /// that was queued and never completed.</summary>
    public string DescribePromotionState()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("        " + IlCompileWorker.Describe());
        sb.AppendLine(
            $"        suspended={PromotionsSuspended} background={BackgroundCompilation} "
            + $"dynamicCode={DynamicCodeSupported} completedQueue={_completedCompiles.Count}");
        foreach (var kv in _counters)
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(kv.Key);
            string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? $"#{atomId}";
            string state =
                _delegates.ContainsKey(kv.Key) ? "promoted"
                : _pendingCompiles.Contains(kv.Key) ? "PENDING-FOREVER?"
                : _unpromotableReason.TryGetValue(kv.Key, out var why) ? $"unpromotable:{why}"
                : "counting";
            sb.AppendLine($"        candidate {name}/{arity}: count={kv.Value} {state}");
        }
        foreach (var (member, root) in _aliasRoot)
            sb.AppendLine($"        member {Indicator(member)} runs the region of {Indicator(root)}");
        return sb.ToString();
    }

    private static string Indicator(int functorId)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(functorId);
        return $"{Shumway.Core.AtomTable.GetById(atomId)?.Name ?? $"#{atomId}"}/{arity}";
    }
}
