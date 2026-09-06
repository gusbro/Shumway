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

    private readonly Dictionary<int, int> _counters = new();
    private readonly HashSet<int> _unpromotable = new();

    public bool Enabled => Threshold > 0 && Promoter is not null;

    public bool IsUnpromotable(int functorId) => _unpromotable.Contains(functorId);

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
