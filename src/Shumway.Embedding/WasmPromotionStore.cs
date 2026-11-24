using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>The wasm tier's promotion state: per-functor dispatch counters, a
/// threshold, and a reject set -- the IL store's shape without its IL. It
/// does not reference the wasm backend: the world wires a
/// <see cref="Promoter"/> (browser: compile + instantiate on the runtime
/// thread; desktop tests: compile + the copy runner) that returns the
/// finished delegate, or null for a predicate the backend refuses. Installed
/// delegates land in the ordinary <see cref="IlPromotionStore"/> table, so
/// dispatch rewrites, resume markers and eviction all work unchanged.</summary>
public sealed class WasmPromotionStore(IlPromotionStore ilStore)
{
    /// <summary>Dispatches before a compile is attempted. 0 disables the
    /// tier.</summary>
    public int Threshold
    {
        get => _threshold;
        set
        {
            bool wasEnabled = Enabled;
            bool wasOn = _threshold > 0;
            _threshold = value;
            if (wasEnabled != Enabled && ReferenceEquals(ilStore.Wasm, this))
                ilStore.WasmEnabledChanged();
            // On again: the modules held while it was off install at the
            // next goal.
            if (!wasOn && value > 0 && InstallBundles) ReleaseHeldBundles();
        }
    }
    private int _threshold;

    /// <summary>Builds the delegate for a predicate: compile the module,
    /// bind it to an <see cref="IWasmActivationRunner"/>, wrap the verdict
    /// loop. Args: the predicate and its linked base address. Returns null
    /// for a refusal (the functor is then never tried again). The hook
    /// catches its own backend exceptions -- a throw here is a bug, not a
    /// reject.</summary>
    public System.Func<CompiledPredicate, int, PredicateDelegate?>? Promoter { get; set; }

    /// <summary>Compiles a whole candidate set into the group in one build
    /// and installs a delegate per member, returning how many made it.
    /// The per-promotion <see cref="Promoter"/> rebuilds the group each
    /// time, which is O(n^2) over n promotions; a program that wants its
    /// whole static set compiled pays O(n) here instead. Optional -- a host
    /// that only ever promotes lazily never sets it.</summary>
    public System.Func<List<(CompiledPredicate Pred, int Addr)>, int>? BatchPromoter { get; set; }

    /// <summary>Raised with the candidate count immediately before a batch
    /// build, for hosts that want to say so: the build is the one visible
    /// pause the tier imposes, and it is worth a line.</summary>
    public System.Action<int>? BatchStarting { get; set; }

    /// <summary>Installs one relocatable wasm module shipped in a bundle
    /// (<c>shumway-link --wasm</c>) into the host's world, and returns the
    /// functors it installed with a note (the count, or why nothing was
    /// installed). The third argument says where the module's predicates
    /// are linked, address to predicate; null is the engine's current static
    /// link. Wired by the host that owns a world; without it the modules
    /// stay queued.</summary>
    public System.Func<PrologEngine, byte[], IReadOnlyDictionary<int, CompiledPredicate>?,
        (IReadOnlyList<int> Installed, string Note)>? BundleInstaller { get; set; }

    /// <summary>The functors running from a bundle's wasm module: a status
    /// report folds them into one count so the user's own promotions show.
    /// A relink that evicts one drops it here too.</summary>
    public HashSet<int> BundleFids { get; } = new();

    /// <summary>What became of the last bundle module offered to <see
    /// cref="BundleInstaller"/>.</summary>
    public string BundleInstallNote { get; private set; } = "no bundle modules";

    /// <summary>Installs every queued bundle module (<see
    /// cref="IlPromotionStore.PendingWasmModules"/>). Called by the query
    /// setup right after it links the static program, so a bundle's wasm is
    /// live before the first goal that could dispatch into it, and by the
    /// host's tick. Needs a link: with none it runs the throwaway goal that
    /// builds one. Returns how many predicates were installed.</summary>
    public int InstallPendingBundles(PrologEngine engine)
    {
        var pending = ilStore.PendingWasmModules;
        if (pending.Count == 0 || BundleInstaller is null) return 0;
        if (HoldsPending()) return 0;
        if (engine._staticLink is null)
        {
            engine.Query("true.");
            // The setup of that query drained the queue through this method.
            if (pending.Count == 0) return _lastBundleInstalled;
        }
        var modules = pending.ToArray();
        pending.Clear();
        _triedLive.Clear();
        int installed = 0;
        string? placed = null, refused = null;
        foreach (var module in modules)
        {
            _held = false;
            var (fids, note) = BundleInstaller(engine, module, null);
            // A snapshot is held for its predicate's first call: nothing
            // was installed, and nothing refused.
            if (_held) continue;
            foreach (int fid in fids) BundleFids.Add(fid);
            installed += fids.Count;
            if (fids.Count > 0)
            {
                _installedBundles.Add(module);
                // One note for the modules of the drain: the count of them all.
                placed = placed is null ? note : $"{installed} predicates installed from the bundle";
            }
            else refused ??= note;
        }
        if (placed is not null || refused is not null)
            BundleInstallNote = placed is null ? refused!
                : refused is null ? placed : $"{placed}; a module {refused}";
        _lastBundleInstalled = installed;
        return installed;
    }
    private int _lastBundleInstalled;

    // jit_compile(off) holds the queued modules too: one installed after the
    // switch would run its predicates in wasm all the same.
    private bool HoldsPending()
    {
        if (InstallBundles && Threshold > 0) return false;
        var pending = ilStore.PendingWasmModules;
        _heldBundles.AddRange(pending);
        pending.Clear();
        BundleInstallNote = InstallBundles
            ? "held: jit_compile(off)" : "held: jit_compile(bundles_off)";
        return true;
    }

    /// <summary><see cref="InstallPendingBundles"/> for the bundles a running
    /// goal loaded. That load linked their predicates into the goal's code,
    /// at the addresses of <paramref name="linked"/>, and a module installs
    /// against those: a session that is one long query has no query setup
    /// to install it at, and compiled a library's predicates one at a time
    /// as each ran enough. A module with a member that load did not link
    /// stays queued for a query setup, and is not tried here again.</summary>
    public int InstallPendingBundlesLive(PrologEngine engine,
        IReadOnlyDictionary<int, CompiledPredicate> linked)
    {
        var pending = ilStore.PendingWasmModules;
        if (pending.Count == 0 || BundleInstaller is null || HoldsPending()) return 0;
        int installed = 0;
        for (int i = 0; i < pending.Count;)
        {
            byte[] module = pending[i];
            if (!_triedLive.Add(module)) { i++; continue; }
            _held = false;
            var (fids, note) = BundleInstaller(engine, module, linked);
            if (_held)
            {
                _triedLive.Remove(module);
                pending.RemoveAt(i);
                continue;
            }
            BundleInstallNote = note;
            if (fids.Count == 0) { i++; continue; }
            foreach (int fid in fids) BundleFids.Add(fid);
            installed += fids.Count;
            _installedBundles.Add(module);
            _triedLive.Remove(module);
            pending.RemoveAt(i);
        }
        return installed;
    }

    // Queued modules a live install could not place, by reference.
    private readonly HashSet<byte[]> _triedLive = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether a bundle's baked wasm module is installed
    /// (jit_compile(bundles_on), jit_compile(bundles_off)). See <see cref="SetBundles"/>.</summary>
    public bool InstallBundles { get; private set; } = true;

    // Modules installed, kept so that turning bundles off and on again
    // installs them again; and modules held while bundles are off.
    private readonly List<byte[]> _installedBundles = new();
    private readonly List<byte[]> _heldBundles = new();

    private int ReleaseHeldBundles()
    {
        int released = _heldBundles.Count;
        ilStore.PendingWasmModules.AddRange(_heldBundles);
        _heldBundles.Clear();
        return released;
    }

    /// <summary>jit_compile(bundles_off): the predicates running from bundle
    /// modules are evicted and later modules held, so the tier compiles
    /// those predicates as it compiles the program's own; that is how a
    /// baked module is measured against what the tier builds. On: the held
    /// modules install at the next goal. Returns the predicates evicted (off)
    /// or the modules released (on).</summary>
    public int SetBundles(bool on)
    {
        InstallBundles = on;
        var pending = ilStore.PendingWasmModules;
        if (on) return ReleaseHeldBundles();
        _heldBundles.AddRange(_installedBundles);
        _installedBundles.Clear();
        _heldBundles.AddRange(pending);
        pending.Clear();
        // The snapshots running from a bundle leave too; the tier compiles
        // those predicates as it compiles any other dynamic one.
        foreach (int fid in new List<int>(_bakedShadows))
        {
            ilStore.EvictDelegate(fid, mutation: false);
            Retire(fid);
        }
        if (BundleFids.Count == 0) return 0;
        var all = new List<int>(BundleFids);
        Displaced(all);
        var gone = StaleEvicted?.Invoke(all);
        if (gone is not null) Displaced(gone);
        return all.Count;
    }

    /// <summary>jit_compile(all): compile the whole static program as it is
    /// consulted, not when the user's first query happens to need the link —
    /// deferring the batch would bill that query for every compile at once.
    /// While set, <see cref="CompileAllTick"/> re-runs the batch after any
    /// consult that changed the program.</summary>
    public bool CompileAllOnConsult { get; set; }

    // The static link the batch last reconciled against. Identity, not a
    // program stamp: a consult replaces the link, while an assert or a
    // dynamic hotness flip bumps _programStamp without touching the static
    // program at all. Keying on the stamp made every other goal reconcile
    // 800-odd predicates -- hashing each one's bytecode -- for nothing, which
    // reads to the user as the engine thinking for a second after each query.
    private object? _lastLink;
    private int _lastVersion = -1;

    // Functors that crossed the threshold while the batch owns compilation:
    // picked up whole at the next boundary instead of each rebuilding the
    // group where it stands.
    private readonly HashSet<int> _pendingBatch = new();

    /// <summary>Why the last batch ran: null for a consult (the static link
    /// was replaced, which is the expected cause), otherwise the functors
    /// that crossed the threshold and asked for it. "A build happened" is not
    /// actionable; "THIS predicate asked for it" is.</summary>
    public IReadOnlyList<int> LastBatchTrigger { get; private set; } = System.Array.Empty<int>();

    // (fid -> address and bytecode identity at install time) of every wasm
    // delegate, baked and compiled alike: what ReconcileWithLink compares
    // against the live link.
    private readonly Dictionary<int, (int Addr, ulong Hash)> _installed = new();

    /// <summary>Records where a wasm delegate's predicate was linked when it
    /// was installed, and what its bytecode was. Every install site must
    /// call this; an unrecorded delegate is invisible to the relink
    /// reconciliation below.</summary>
    public void NoteInstalled(int functorId, int linkedAddress,
        CompiledPredicate predicate)
        => _installed[functorId] = (linkedAddress, CodeHash(predicate));

    /// <summary>Whether the predicate's delegate is a wasm one, baked or
    /// compiled.</summary>
    public bool Covers(int functorId) => _installed.ContainsKey(functorId);

    // FNV-1a over the linked bytecode and call sites: equal hashes mean the
    // predicate merely moved; a redefinition changes them.
    private static ulong CodeHash(CompiledPredicate pred)
    {
        const ulong prime = 1099511628211UL;
        ulong h = 14695981039346656037UL;
        foreach (byte b in pred.Bytecode) { h ^= b; h *= prime; }
        h ^= (uint)pred.Arity; h *= prime;
        foreach (var site in pred.CallSites)
        {
            h ^= (uint)site.OpcodeOffset; h *= prime;
            h ^= (uint)site.CalleeFunctorId; h *= prime;
        }
        return h;
    }

    /// <summary>Fires after <see cref="ReconcileWithLink"/> evicts stale
    /// delegates, with the evicted functor ids: the tier drops its group
    /// members and baked bookkeeping for them, and answers with everything
    /// that left the tier, the baked callers its registry dragged along
    /// included (<see cref="IWasmExecutionWorld.Evict"/>); those lose their
    /// delegates here too.</summary>
    public System.Func<IReadOnlyList<int>, IReadOnlyList<int>>? StaleEvicted { get; set; }

    /// <summary>Fires with the fresh (functor -> live address) map after a
    /// relink moved code: the tier hands it to its execution worlds, whose
    /// boundary translation keeps every installed build valid.</summary>
    public System.Action<IReadOnlyDictionary<int, int>>? LiveRefreshed { get; set; }

    /// <summary>Delegates evicted because a relink redefined their
    /// predicates; running total, surfaced by the status report.</summary>
    public int RelinkEvictions { get; private set; }

    /// <summary>A wasm module bakes its members' linked addresses: deopt
    /// pcs, resume markers, BP encodings, and a stale one reaching the
    /// interpreter's SetPc runs what is now different code: "bytecode
    /// corruption" crashes. A relink moves an address when the space below
    /// it closes up — a predicate that disappeared, and in time a deliberate
    /// compaction — while what stays keeps its address. The bytecode itself
    /// only moves (hashes equal), so the builds stay valid: this refreshes
    /// the worlds' live-address maps (the boundary translation does the
    /// rest) and evicts only a delegate whose predicate was redefined or
    /// dropped, which falls back to bytecode until re-promoted.</summary>
    public int ReconcileWithLink(PrologEngine engine)
    {
        if (_installed.Count == 0) return 0;
        var liveAddr = new Dictionary<int, int>(_installed.Count);
        var livePred = new Dictionary<int, CompiledPredicate>(_installed.Count);
        foreach (var (addr, pred) in StaticPredicatesOf(engine))
        { liveAddr[pred.FunctorId] = addr; livePred[pred.FunctorId] = pred; }
        if (liveAddr.Count == 0) return 0;
        List<int>? stale = null;
        bool moved = false;
        foreach (var (fid, (addr, hash)) in _installed)
        {
            if (liveAddr.TryGetValue(fid, out int now)
                && CodeHash(livePred[fid]) == hash)
            {
                moved |= now != addr;
                continue;
            }
            (stale ??= new()).Add(fid);
        }
        if (stale is not null)
        {
            Displaced(stale);
            var gone = StaleEvicted?.Invoke(stale);
            if (gone is not null) Displaced(gone);
        }
        if (moved) LiveRefreshed?.Invoke(liveAddr);
        return stale?.Count ?? 0;
    }

    /// <summary>jit_compile(off), and a change of the tier's form: every
    /// build leaves new calls now, a static one as after a consult that
    /// defined its predicate again and a snapshot as after a mutation. A
    /// call running in one finishes: its choice points resume in the
    /// bytecode, or in the snapshot they began with. Waiting for the next
    /// query setup, as the IL tier does, left a session that is one long
    /// query compiled for good.</summary>
    public void LeaveNow()
    {
        if (_installed.Count > 0)
        {
            var all = new List<int>(_installed.Keys);
            Displaced(all);
            var gone = StaleEvicted?.Invoke(all);
            if (gone is not null) Displaced(gone);
        }
        foreach (int fid in new List<int>(_shadows.Keys))
        {
            ilStore.EvictDelegate(fid, mutation: false);
            Retire(fid);
        }
    }

    /// <summary>Drops the delegates of functors the tier no longer covers
    /// -- evicted, or displaced by a takeover (<see
    /// cref="IWasmExecutionWorld.InstallGroup"/>) -- so they run on
    /// bytecode and can be promoted again. Idempotent: a functor already
    /// dropped counts nothing.</summary>
    public void Displaced(IReadOnlyList<int> functorIds)
    {
        foreach (int fid in functorIds)
        {
            if (!_installed.Remove(fid)) continue;
            BundleFids.Remove(fid);
            ilStore.EvictDelegate(fid);
            RelinkEvictions++;
        }
    }

    /// <summary>Runs the relink reconciliation and, under
    /// <see cref="CompileAllOnConsult"/>, the batch. Called by the host
    /// after a consult and after each query completes (a query may consult).
    /// Cheap when nothing changed: one int compare. The static link only
    /// exists after a query setup, so when a consult just invalidated it
    /// this runs one trivial query to rebuild it — that throwaway goal, not
    /// the user's next real one, pays for the compile.</summary>
    /// <summary>How many ticks got past the early-out and actually
    /// reconciled. The tick runs after every query, and its work is O(all
    /// installed predicates) with a bytecode hash each: a tick that runs when
    /// nothing changed is invisible except as a pause, so it is counted.
    /// </summary>
    public int BatchTicksWorked { get; private set; }

    /// <summary>Makes the next <see cref="CompileAllTick"/> do the work
    /// even though the program has not changed. Turning the batch on is such
    /// a moment: the tick's early-out asks whether the program moved, and
    /// what moved here is the mode. Without this, jit_compile(all) compiled
    /// nothing and the batch ran later, triggered by predicates crossing the
    /// threshold during the user's next query -- which therefore ran
    /// interpreted. Measured in the browser: 107 s for the first goal, 1.8 s
    /// for the same goal after.</summary>
    public void ForceNextBatch() => _lastLink = null;

    public int CompileAllTick(PrologEngine engine)
    {
        // Only a change to the static program can add candidates or move
        // code, and a consult is what changes it: it invalidates the link,
        // and the next query builds a new one. Anything else is a reference
        // compare.
        if (engine._staticLink is not null
            && ReferenceEquals(engine._staticLink, _lastLink)
            && engine.StaticProgramVersion == _lastVersion
            && _pendingBatch.Count == 0
            && ilStore.PendingWasmModules.Count == 0) return 0;
        bool anythingToDo = _installed.Count > 0
            || (CompileAllOnConsult && BatchPromoter is not null)
            || ilStore.PendingWasmModules.Count > 0;
        if (!anythingToDo) return 0;
        if (engine._staticLink is null) engine.Query("true.");
        InstallPendingBundles(engine);
        _lastLink = engine._staticLink;
        _lastVersion = engine.StaticProgramVersion;
        BatchTicksWorked++;
        // Evict the stale before the batch, so it recompiles them against
        // the addresses the modules will actually bake.
        ReconcileWithLink(engine);
        if (!CompileAllOnConsult || BatchPromoter is null) return 0;
        LastBatchTrigger = _pendingBatch.Count > 0
            ? new List<int>(_pendingBatch) : System.Array.Empty<int>();
        _pendingBatch.Clear();          // PromoteAllStatics sweeps them up
        return System.Math.Max(0, PromoteAllStatics(engine));
    }

    /// <summary>The linked static predicates, (address, predicate) — what
    /// <see cref="PromoteAllStatics"/> feeds the batch. Public for the size
    /// diagnostics that decide the group-partitioning question.</summary>
    public static IEnumerable<(int Addr, CompiledPredicate Pred)>
        StaticPredicatesOf(PrologEngine engine)
    {
        foreach (var (addr, pred) in engine.LinkedStatics())
            yield return (addr, pred);
    }

    /// <summary>The digest of the clauses of each dynamic predicate that has
    /// any (<see cref="PrologEngine.DynamicClauseDigest"/>): what a bake
    /// compares before and after loading a bundle, to know the predicates
    /// the bundle gave clauses to.</summary>
    public static Dictionary<int, string> DynamicDigestsOf(PrologEngine engine)
    {
        var digests = new Dictionary<int, string>();
        foreach (int fid in engine.AllDynamicFunctors())
            if (engine.DynamicClauseDigest(fid) is { } digest) digests[fid] = digest;
        return digests;
    }

    /// <summary>The snapshot the tier would promote a dynamic predicate as
    /// (ADR-054), for a bake; null when it has none, or would not be
    /// promoted that way. Requires an engine a query has linked.</summary>
    public static CompiledPredicate? DynamicSnapshotOf(PrologEngine engine, int functorId)
        => engine._precompiledFloatPool.ContainsKey(functorId)
            ? null : engine.BuildDynamicSnapshot(functorId);

    /// <summary>Every static predicate of <paramref name="engine"/>'s linked
    /// program through <see cref="BatchPromoter"/> in one build: the
    /// jit_compile(all) path. Skips what is already promoted, already
    /// refused, or excluded from promotion (query wrappers). Returns how
    /// many predicates were newly compiled, or -1 when there is no batch
    /// promoter or no linked program to read.</summary>
    public int PromoteAllStatics(PrologEngine engine)
    {
        if (BatchPromoter is null) return -1;
        if (engine._staticLink is null) return -1;
        var already = new HashSet<int>(ilStore.PromotedFunctorIds());
        var candidates = new List<(CompiledPredicate, int)>();
        foreach (var (addr, pred) in engine.LinkedStatics())
        {
            int fid = pred.FunctorId;
            if (_unpromotable.Contains(fid)) continue;
            if (already.Contains(fid)) continue;
            if (IlPromotionStore.IsExcludedFromPromotion(fid)) continue;
            if (pred.IsDebuggable) continue;
            candidates.Add((pred, addr));
        }
        if (candidates.Count == 0) return 0;
        // Announced here and nowhere else: this is the first moment the count
        // is known and the last before the work. A notice keyed on "the tick
        // will run" instead fires after every consult, including the ones
        // whose whole program is already promoted -- "compiling... 0
        // predicates", which is noise that also happens to be false.
        BatchStarting?.Invoke(candidates.Count);
        return BatchPromoter(candidates);
    }

    /// <summary>Every static predicate of the linked program through
    /// <see cref="Promoter"/>, one module each: the per-predicate twin of
    /// <see cref="PromoteAllStatics"/>, for measuring the two grains against
    /// each other. Returns how many were newly installed, or -1 without a
    /// promoter or a linked program.</summary>
    public int PromoteAllStaticsIndividually(PrologEngine engine)
    {
        if (Promoter is null) return -1;
        if (engine._staticLink is null) engine.Query("true.");
        if (engine._staticLink is null) return -1;
        var already = new HashSet<int>(ilStore.PromotedFunctorIds());
        int installed = 0;
        foreach (var (addr, pred) in engine.LinkedStatics())
        {
            int fid = pred.FunctorId;
            if (_unpromotable.Contains(fid) || already.Contains(fid)) continue;
            if (IlPromotionStore.IsExcludedFromPromotion(fid)) continue;
            if (pred.IsDebuggable) continue;
            var del = Promoter(pred, addr);
            if (del is null) { _unpromotable.Add(fid); continue; }
            ilStore.RegisterBoundDelegate(fid, del, compiledFrom: pred);
            NoteInstalled(fid, addr, pred);
            installed++;
        }
        return installed;
    }

    private readonly Dictionary<int, int> _counters = new();
    private readonly HashSet<int> _unpromotable = new();

    public bool Enabled => Threshold > 0 && Promoter is not null;

    public bool IsUnpromotable(int functorId) => _unpromotable.Contains(functorId);

    // Why each refusal happened, so the report can say it. A refusal is
    // always the backend declining a shape it does not translate yet, and
    // that reason is the only actionable part of it.
    private readonly Dictionary<int, string> _refusalReason = new();

    /// <summary>Marks a functor refused (a batch build's poisoner) so no
    /// later dispatch or batch tries it again. <paramref name="reason"/> is
    /// the compiler's own message.</summary>
    public void MarkUnpromotable(int functorId, string? reason = null)
    {
        _unpromotable.Add(functorId);
        if (reason is { Length: > 0 }) _refusalReason[functorId] = reason;
    }

    /// <summary>Keeps the static predicates linked now off the tier, but for
    /// those already on it: the code a host loads for its own use before the
    /// user's program, which a batch would compile again in every engine.
    /// False when there is no linked program yet, and nothing was marked.</summary>
    public bool LeaveOutLinked(PrologEngine engine, string reason)
    {
        if (engine._staticLink is null) return false;
        var promoted = new HashSet<int>(ilStore.PromotedFunctorIds());
        foreach (var (_, pred) in engine.LinkedStatics())
        {
            int fid = pred.FunctorId;
            if (promoted.Contains(fid) || _unpromotable.Contains(fid)) continue;
            MarkUnpromotable(fid, reason);
        }
        return true;
    }

    /// <summary>What the compiler said when it refused this functor, or
    /// null when it was refused without one.</summary>
    public string? RefusalReason(int functorId)
        => _refusalReason.TryGetValue(functorId, out string? r) ? r : null;

    /// <summary>The functors a compile actually refused. The set also caches
    /// by-design exclusions (the synthetic __query__ wrappers, whose body
    /// changes per query under one functor id) so RecordDispatch decides
    /// once — but those are not refusals and reporting them as such reads
    /// like something went wrong.</summary>
    public IEnumerable<int> UnpromotableFunctorIds()
    {
        foreach (int fid in _unpromotable)
            if (!IlPromotionStore.IsExcludedFromPromotion(fid))
                yield return fid;
    }

    /// <summary>Records one dispatch; compiles, installs and returns the
    /// delegate when the count crosses the threshold. Engine-thread only,
    /// synchronous -- the browser wraps the promoter's instantiation half
    /// asynchronously and returns null until it lands.</summary>
    /// <param name="viewEpoch">The IL store's <see cref="IlPromotionStore.StaticEpoch"/>
    /// when the program view <paramref name="predicate"/> comes from was built.</param>
    public PredicateDelegate? RecordDispatch(int functorId, CompiledPredicate predicate,
        int linkedAddress, Activation? engine = null, int? viewEpoch = null)
        => RecordDispatch(functorId, predicate, linkedAddress, engine, viewEpoch, out _);

    /// <summary>As above; <paramref name="settled"/> is true when a null
    /// answer holds for the rest of the program view (see
    /// <see cref="IlPromotionStore.RecordInvocation(int, CompiledPredicate, Func{IReadOnlyDictionary{int, CompiledPredicate}}, int?, out bool)"/>).</summary>
    internal PredicateDelegate? RecordDispatch(int functorId, CompiledPredicate predicate,
        int linkedAddress, Activation? engine, int? viewEpoch, out bool settled)
    {
        settled = false;
        if (!Enabled || _unpromotable.Contains(functorId)) { settled = true; return null; }
        // The view's copy of it predates a consult that changed it.
        if (viewEpoch is { } view && view != ilStore.StaticEpoch
            && ilStore.ChangedAfter(functorId, view)) return null;
        // The synthetic __query__ wrappers have a different body per query
        // under one functor id: promoting one would replay a stale query.
        // Mid-consult suspension mirrors the IL store's reasoning too.
        if (IlPromotionStore.IsExcludedFromPromotion(functorId))
        {
            _unpromotable.Add(functorId);
            settled = true;
            return null;
        }
        if (ilStore.PromotionsSuspended) return null;
        // ADR-054: a dynamic predicate promotes as a snapshot, when the host
        // can retire one. Anywhere else it goes the old way, and the compiler
        // refuses its enter_dynamic body.
        bool shadow = engine is not null && ShadowRetired is not null
            && StaleEvicted is not null && ilStore.ShadowSnapshotProvider is not null
            && OpensDynamic(predicate);
        if (shadow && ilStore.DynamicChurnPinned(functorId)) return null;
        // A snapshot the bundle brought runs from the first call, as the IL
        // tier's does (ADR-023 priming).
        if (shadow && InstallBundles && !predicate.IsDebuggable
            && _bakedSnapshots.TryGetValue(functorId, out var baked)
            && InstallBakedSnapshot(functorId, engine!, baked) is { } bakedDelegate)
            return bakedDelegate;
        _counters.TryGetValue(functorId, out int count);
        count++;
        _counters[functorId] = count;
        if (count < Threshold) return null;
        // ADR-035: debuggable code stays on the interpreter (see
        // IlPromotionStore.CompileAtThreshold).
        if (predicate.IsDebuggable) { settled = true; return null; }
        // A snapshot is a module of its own, so it never waits for a batch.
        if (shadow) return PromoteShadow(functorId, engine!, out settled);

        // Under the batch, a straggler must not build on its own. The group is
        // one module: promoting a single predicate re-emits all of it, and
        // with the whole program on the tier that is a full rebuild landing
        // inside the user's query -- after the goal has written its output,
        // before it answers. Note it and let the next boundary tick take it
        // with the others; until then it keeps running on Tier-0, exactly as
        // it did between crossing the threshold and being installed.
        if (CompileAllOnConsult && BatchPromoter is not null)
        {
            _pendingBatch.Add(functorId);
            return null;
        }

        var del = Promoter!(predicate, linkedAddress);
        if (del is null)
        {
            _unpromotable.Add(functorId);
            settled = true;
            return null;
        }
        ilStore.RegisterBoundDelegate(functorId, del, compiledFrom: predicate);
        NoteInstalled(functorId, linkedAddress, predicate);
        return del;
    }

    private static bool OpensDynamic(CompiledPredicate predicate)
        => predicate.Bytecode.Length > 0
           && predicate.Bytecode[0] == (byte)Opcode.EnterDynamic;

    /// <summary>ADR-054: takes a dynamic predicate's snapshot out of new
    /// calls and keeps it for the calls running in it. Wired by a host that
    /// can (<see cref="WasmModuleRegistry.Retire"/>); without it, dynamic
    /// predicates are not promoted at all.</summary>
    public System.Func<int, bool>? ShadowRetired { get; set; }

    // Promoted snapshots: functor -> the shadow region's address. And the
    // ones retired since the code space was last built, whose resume rows
    // are still live in the world.
    private readonly Dictionary<int, int> _shadows = new();
    private readonly HashSet<int> _retiredShadows = new();

    /// <summary>Snapshots promoted and retired, for the status report.</summary>
    public int ShadowPromotions { get; private set; }
    public int ShadowRetirements { get; private set; }

    /// <summary>Whether a dynamic predicate runs as a promoted snapshot now.
    /// </summary>
    public bool HasShadow(int functorId) => _shadows.ContainsKey(functorId);

    private PredicateDelegate? PromoteShadow(int functorId, Activation engine, out bool settled)
    {
        var shadow = ilStore.ShadowSnapshotProvider!(engine, functorId);
        if (shadow.Snapshot is null)
        {
            // No refusal: no visible clause this query (none, or a mutation
            // since the setup), so a later query retries.
            if (shadow.Refusal is not null) MarkUnpromotable(functorId, shadow.Refusal);
            settled = true;
            return null;
        }
        var del = Promoter!(shadow.Snapshot, shadow.Address);
        if (del is null)
        {
            _unpromotable.Add(functorId);
            settled = true;
            return null;
        }
        settled = false;
        ilStore.RegisterBoundDelegate(functorId, del, compiledFrom: shadow.Snapshot);
        _shadows[functorId] = shadow.Address;
        ShadowPromotions++;
        return del;
    }

    // Snapshots a bundle brought (ADR-054), by functor: the digest of the
    // clauses each was compiled from, what installs it over a shadow region,
    // and whether this engine's clauses were found to be those. And the ones
    // running now.
    private readonly Dictionary<int, BakedSnapshot> _bakedSnapshots = new();
    private readonly HashSet<int> _bakedShadows = new();
    private bool _held;

    private sealed class BakedSnapshot(string digest,
        System.Func<CompiledPredicate, int, PredicateDelegate?> install)
    {
        public readonly string Digest = digest;
        public readonly System.Func<CompiledPredicate, int, PredicateDelegate?> Install = install;
        public bool Verified;
    }

    /// <summary>Snapshots held and not running, and installs of one so far,
    /// for the status report.</summary>
    public int BakedSnapshotsHeld => _bakedSnapshots.Count - _bakedShadows.Count;
    public int BakedSnapshotInstalls { get; private set; }

    /// <summary>Takes a snapshot of a dynamic predicate a bundle brought,
    /// compiled from the clauses of <paramref name="digest"/>
    /// (<see cref="PrologEngine.DynamicClauseDigest"/>). It is installed at
    /// the predicate's first call, and again at the first one after the code
    /// space is rebuilt, by <paramref name="install"/>: given the snapshot's
    /// bytecode and the address of its shadow region, the delegate, or null
    /// when the module does not fit that code. A mutation of the predicate
    /// drops it: the tier compiles the predicate then, when it has run
    /// enough. Called by a <see cref="BundleInstaller"/> for a module that
    /// is a snapshot.</summary>
    public void HoldBakedSnapshot(int functorId, string digest,
        System.Func<CompiledPredicate, int, PredicateDelegate?> install)
    {
        _bakedSnapshots[functorId] = new BakedSnapshot(digest, install);
        _held = true;
    }

    private PredicateDelegate? InstallBakedSnapshot(int functorId, Activation engine, BakedSnapshot baked)
    {
        if (!baked.Verified)
        {
            // Other clauses than it was compiled from: another bundle's too,
            // or the program's own.
            if (ilStore.DynamicDigestProvider?.Invoke(functorId) != baked.Digest)
            {
                _bakedSnapshots.Remove(functorId);
                return null;
            }
            baked.Verified = true;
        }
        var shadow = ilStore.ShadowSnapshotProvider!(engine, functorId);
        if (shadow.Snapshot is null)
        {
            if (shadow.Refusal is not null) _bakedSnapshots.Remove(functorId);
            return null;
        }
        var del = baked.Install(shadow.Snapshot, shadow.Address);
        if (del is null)
        {
            _bakedSnapshots.Remove(functorId);
            return null;
        }
        ilStore.RegisterBoundDelegate(functorId, del, compiledFrom: shadow.Snapshot);
        _shadows[functorId] = shadow.Address;
        _bakedShadows.Add(functorId);
        ShadowPromotions++;
        BakedSnapshotInstalls++;
        return del;
    }

    /// <summary>The predicate changed (assert, retract, abolish, a consult
    /// redefining it). A snapshot of it leaves new calls, which reach the
    /// predicate as it is now, and stays for the calls running in it; the
    /// next promotion waits for the threshold again. The IL store's own
    /// eviction runs first, in the caller.</summary>
    public void OnMutated(int functorId)
    {
        _bakedSnapshots.Remove(functorId);
        Retire(functorId);
    }

    // The snapshot leaves new calls and stays for the calls running in it.
    private void Retire(int functorId)
    {
        _bakedShadows.Remove(functorId);
        if (!_shadows.Remove(functorId)) return;
        ShadowRetired?.Invoke(functorId);
        _retiredShadows.Add(functorId);
        _counters.Remove(functorId);
        ShadowRetirements++;
    }

    /// <summary>A consult changed a static predicate's clauses: its module, and
    /// the modules that reach it by an internal jump, leave the tier now. The
    /// next relink would find it, but the rest of a query that consulted runs
    /// before that.</summary>
    public void OnStaticChanged(int functorId)
    {
        if (!_installed.ContainsKey(functorId)) return;
        var stale = new[] { functorId };
        Displaced(stale);
        var gone = StaleEvicted?.Invoke(stale);
        if (gone is not null) Displaced(gone);
    }

    /// <summary>The persistent code space was built again, and the shadow
    /// regions with it are gone: every snapshot is evicted, retired ones
    /// included, and promotes again on demand. Not a mutation, so it does
    /// not count toward the churn pin.</summary>
    public void OnPersistentRebuilt()
    {
        // A consult may have given a predicate other source clauses: each
        // held snapshot is checked against its predicate again.
        foreach (var baked in _bakedSnapshots.Values) baked.Verified = false;
        _bakedShadows.Clear();
        if (_shadows.Count == 0 && _retiredShadows.Count == 0) return;
        var all = new List<int>(_shadows.Keys);
        foreach (int fid in _retiredShadows)
            if (!_shadows.ContainsKey(fid)) all.Add(fid);
        foreach (int fid in _shadows.Keys)
        {
            ilStore.EvictDelegate(fid, mutation: false);
            _counters.Remove(fid);
        }
        _shadows.Clear();
        _retiredShadows.Clear();
        var gone = StaleEvicted?.Invoke(all);
        if (gone is not null) Displaced(gone);
    }
}

/// <summary>A dynamic predicate's snapshot linked into the running code
/// space (ADR-054): the snapshot under the predicate's own functor, and the
/// address of its shadow region. A null snapshot with no
/// <see cref="Refusal"/> means no visible clause yet (retry); with one, the
/// predicate cannot be promoted this way.</summary>
public readonly record struct DynamicShadow(CompiledPredicate? Snapshot, int Address,
    string? Refusal = null);
