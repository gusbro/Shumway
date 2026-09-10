using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>The wasm tier's promotion state: per-functor dispatch counters, a
/// threshold, and a reject set -- the IL store's shape without its IL. It
/// does NOT reference the wasm backend: the world wires a
/// <see cref="Promoter"/> (browser: compile + instantiate on the runtime
/// thread; desktop tests: compile + the copy runner) that returns the
/// finished delegate, or null for a predicate the backend refuses. Installed
/// delegates land in the ordinary <see cref="IlPromotionStore"/> table, so
/// dispatch rewrites, resume markers and eviction all work unchanged.</summary>
public sealed class WasmPromotionStore(IlPromotionStore ilStore)
{
    /// <summary>Dispatches before a compile is attempted. 0 disables the
    /// tier.</summary>
    public int Threshold { get; set; }

    /// <summary>Builds the delegate for a predicate: compile the module,
    /// bind it to an <see cref="IWasmActivationRunner"/>, wrap the verdict
    /// loop. Args: the predicate and its linked base address. Returns null
    /// for a refusal (the functor is then never tried again). The hook
    /// catches its own backend exceptions -- a throw here is a bug, not a
    /// reject.</summary>
    public System.Func<CompiledPredicate, int, PredicateDelegate?>? Promoter { get; set; }

    /// <summary>Compiles a whole candidate set into the group in ONE build
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

    /// <summary>wasm_compile(all): compile the whole static program as it is
    /// CONSULTED, not when the user's first query happens to need the link —
    /// deferring the batch would bill that query for every compile at once.
    /// While set, <see cref="CompileAllTick"/> re-runs the batch after any
    /// consult that changed the program.</summary>
    public bool CompileAllOnConsult { get; set; }

    // The static link the batch last reconciled against. IDENTITY, not a
    // program stamp: a consult replaces the link, while an assert or a
    // dynamic hotness flip bumps _programStamp without touching the static
    // program at all. Keying on the stamp made every other goal reconcile
    // 800-odd predicates -- hashing each one's bytecode -- for nothing, which
    // reads to the user as the engine thinking for a second after each query.
    private object? _lastLink;

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

    // FNV-1a over the linked bytecode and call sites: equal hashes mean the
    // predicate merely MOVED; a redefinition changes them.
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
    /// members and baked bookkeeping for them.</summary>
    public System.Action<IReadOnlyList<int>>? StaleEvicted { get; set; }

    /// <summary>Fires with the fresh (functor -> live address) map after a
    /// relink moved code: the tier hands it to its execution worlds, whose
    /// boundary translation keeps every installed build valid.</summary>
    public System.Action<IReadOnlyDictionary<int, int>>? LiveRefreshed { get; set; }

    /// <summary>Delegates evicted because a relink REDEFINED their
    /// predicates; running total, surfaced by the status report.</summary>
    public int RelinkEvictions { get; private set; }

    /// <summary>A wasm module bakes its members' linked ADDRESSES: deopt
    /// pcs, resume markers, BP encodings. ANY consult relinks the whole
    /// static program and moves every address (measured: two plain facts
    /// shifted all ~530 prelude predicates), after which a stale build
    /// address reaching the interpreter's SetPc runs what is now different
    /// code: "bytecode corruption" crashes. The bytecode itself only MOVES
    /// (hashes equal), so the builds stay valid: this refreshes the worlds'
    /// live-address maps (the boundary translation does the rest) and
    /// evicts only a delegate whose predicate was REDEFINED or dropped,
    /// which falls back to bytecode until re-promoted.</summary>
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
            foreach (int fid in stale)
            {
                ilStore.EvictDelegate(fid);
                _installed.Remove(fid);
            }
            RelinkEvictions += stale.Count;
            StaleEvicted?.Invoke(stale);
        }
        if (moved) LiveRefreshed?.Invoke(liveAddr);
        return stale?.Count ?? 0;
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

    public int CompileAllTick(PrologEngine engine)
    {
        // Only a change to the STATIC program can add candidates or move
        // code, and a consult is what changes it: it invalidates the link,
        // and the next query builds a new one. Anything else is a reference
        // compare.
        if (engine._staticLink is not null
            && ReferenceEquals(engine._staticLink, _lastLink)
            && _pendingBatch.Count == 0) return 0;
        bool anythingToDo = _installed.Count > 0
            || (CompileAllOnConsult && BatchPromoter is not null);
        if (!anythingToDo) return 0;
        if (engine._staticLink is null) engine.Query("true.");
        _lastLink = engine._staticLink;
        BatchTicksWorked++;
        // Evict the stale BEFORE the batch, so it recompiles them against
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
        var link = engine._staticLink;
        if (link is null) yield break;
        foreach (var (addr, pred) in link.PredicatesByAddress)
            yield return (addr, pred);
    }

    /// <summary>Every static predicate of <paramref name="engine"/>'s linked
    /// program through <see cref="BatchPromoter"/> in one build: the
    /// wasm_compile(all) path. Skips what is already promoted, already
    /// refused, or excluded from promotion (query wrappers). Returns how
    /// many predicates were newly compiled, or -1 when there is no batch
    /// promoter or no linked program to read.</summary>
    public int PromoteAllStatics(PrologEngine engine)
    {
        if (BatchPromoter is null) return -1;
        var link = engine._staticLink;
        if (link is null) return -1;
        var already = new HashSet<int>(ilStore.PromotedFunctorIds());
        var candidates = new List<(CompiledPredicate, int)>();
        foreach (var (addr, pred) in link.PredicatesByAddress)
        {
            int fid = pred.FunctorId;
            if (_unpromotable.Contains(fid)) continue;
            if (already.Contains(fid)) continue;
            if (IlPromotionStore.IsExcludedFromPromotion(fid)) continue;
            candidates.Add((pred, addr));
        }
        if (candidates.Count == 0) return 0;
        // Announced HERE and nowhere else: this is the first moment the count
        // is known and the last before the work. A notice keyed on "the tick
        // will run" instead fires after every consult, including the ones
        // whose whole program is already promoted -- "compiling... 0
        // predicates", which is noise that also happens to be false.
        BatchStarting?.Invoke(candidates.Count);
        return BatchPromoter(candidates);
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

    /// <summary>What the compiler said when it refused this functor, or
    /// null when it was refused without one.</summary>
    public string? RefusalReason(int functorId)
        => _refusalReason.TryGetValue(functorId, out string? r) ? r : null;

    /// <summary>The functors a compile actually REFUSED. The set also caches
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
    public PredicateDelegate? RecordDispatch(int functorId, CompiledPredicate predicate,
        int linkedAddress)
    {
        if (!Enabled || _unpromotable.Contains(functorId)) return null;
        // The synthetic __query__ wrappers have a different body per query
        // under one functor id: promoting one would replay a stale query.
        // Mid-consult suspension mirrors the IL store's reasoning too.
        if (IlPromotionStore.IsExcludedFromPromotion(functorId))
        {
            _unpromotable.Add(functorId);
            return null;
        }
        if (ilStore.PromotionsSuspended) return null;
        _counters.TryGetValue(functorId, out int count);
        count++;
        _counters[functorId] = count;
        if (count < Threshold) return null;

        // Under the batch, a straggler must NOT build on its own. The group is
        // one module: promoting a single predicate re-emits all of it, and
        // with the whole program on the tier that is a full rebuild landing
        // INSIDE the user's query -- after the goal has written its output,
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
            return null;
        }
        ilStore.RegisterBoundDelegate(functorId, del);
        NoteInstalled(functorId, linkedAddress, predicate);
        return del;
    }
}
