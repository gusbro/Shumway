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

    /// <summary>wasm_compile(all): compile the whole static program as it is
    /// CONSULTED, not when the user's first query happens to need the link —
    /// deferring the batch would bill that query for every compile at once.
    /// While set, <see cref="CompileAllTick"/> re-runs the batch after any
    /// consult that changed the program.</summary>
    public bool CompileAllOnConsult { get; set; }

    private int _lastBatchStamp = -1;

    /// <summary>Runs the batch when the program changed since the last one.
    /// Called by the host after a consult and after each query completes (a
    /// query may consult). Cheap when nothing changed: one int compare. The
    /// static link only exists after a query setup, so when a consult just
    /// invalidated it this runs one trivial query to rebuild it — that
    /// throwaway goal, not the user's next real one, pays for the
    /// compile.</summary>
    public int CompileAllTick(PrologEngine engine)
    {
        if (!CompileAllOnConsult || BatchPromoter is null) return 0;
        // A consult INVALIDATES the static link (the program changed); a
        // dynamic-store change bumps the stamp. Either means the batch may
        // have new candidates; neither means one int/null compare otherwise.
        if (engine._staticLink is not null
            && engine._programStamp == _lastBatchStamp) return 0;
        if (engine._staticLink is null) engine.Query("true.");
        _lastBatchStamp = engine._programStamp;
        return System.Math.Max(0, PromoteAllStatics(engine));
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
        return BatchPromoter(candidates);
    }

    private readonly Dictionary<int, int> _counters = new();
    private readonly HashSet<int> _unpromotable = new();

    public bool Enabled => Threshold > 0 && Promoter is not null;

    public bool IsUnpromotable(int functorId) => _unpromotable.Contains(functorId);

    /// <summary>Marks a functor refused (a batch build's poisoner) so no
    /// later dispatch or batch tries it again.</summary>
    public void MarkUnpromotable(int functorId) => _unpromotable.Add(functorId);

    public IEnumerable<int> UnpromotableFunctorIds() => _unpromotable;

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

        var del = Promoter!(predicate, linkedAddress);
        if (del is null)
        {
            _unpromotable.Add(functorId);
            return null;
        }
        ilStore.RegisterBoundDelegate(functorId, del);
        return del;
    }
}
