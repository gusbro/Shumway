using System.Collections.Concurrent;

namespace Shumway.Embedding;

/// <summary>How <see cref="EnginePool"/> treats an engine returned by a lease.</summary>
public enum PoolReusePolicy
{
    /// <summary>The returned engine goes back to the idle set <b>with its state
    /// intact</b>: clauses asserted, recorded-database entries, global variables and
    /// native scalar globals from one rental are visible to the next renter. Fastest
    /// (no re-consult), correct only when rentals don't mutate engine state or when
    /// cross-rental state is intended.</summary>
    ReuseState,

    /// <summary>The returned engine is discarded; the next rental builds a fresh one
    /// via the factory. Every renter sees exactly the factory's post-consult state —
    /// no leakage of asserts / recorded entries / globals between rentals — at the
    /// cost of re-running the factory per rental.</summary>
    FreshEngine,
}

/// <summary>
/// A bounded pool of <see cref="PrologEngine"/> instances for concurrent
/// embedding scenarios (e.g. a server answering many requests at once).
///
/// <para>An engine is single-threaded internally but thread-agile: it may be
/// used from different threads as long as access is serialised. The pool
/// enforces that — at most one renter holds a given engine at a time — while
/// letting up to <see cref="MaxSize"/> queries run in parallel, each on its own
/// engine.</para>
///
/// <para>Each engine is produced (and consulted) by the caller-supplied
/// <c>factory</c>, lazily on first need. Under the default
/// <see cref="PoolReusePolicy.ReuseState"/> the engine is then <b>reused as-is</b> —
/// state a rental mutates (assertz, recorda, global variables) carries over to the
/// next rental. Workloads that mutate per request should use
/// <see cref="PoolReusePolicy.FreshEngine"/>. The global atom / functor /
/// code-cache tables are thread-safe and shared across engines, so concurrent
/// factory calls and concurrent queries are safe.</para>
///
/// <example><code>
/// using var pool = EnginePool.FromSource("ancestor(X, Y) :- parent(X, Y). ...", maxSize: 8);
/// using (var lease = pool.Rent())
///     foreach (var s in lease.Engine.QueryAll("ancestor(tom, X)."))
///         Console.WriteLine(s["X"]);
/// </code></example>
/// </summary>
public sealed class EnginePool : IDisposable
{
    private readonly Func<PrologEngine> _factory;
    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentBag<PrologEngine> _idle = new();
    private readonly PoolReusePolicy _reusePolicy;
    private int _created;
    private int _disposed;

    /// <summary>Creates a pool that builds each engine with
    /// <paramref name="factory"/> (typically <c>new PrologEngine()</c> plus
    /// whatever <c>ConsultString</c> / <c>LoadBundle</c> / <c>UseClpfd</c> the
    /// workload needs) and lends at most <paramref name="maxSize"/> at once.
    /// <paramref name="reusePolicy"/> chooses whether a returned engine is reused
    /// with its state intact (default) or discarded so every rental starts from
    /// the factory's clean state.</summary>
    public EnginePool(Func<PrologEngine> factory, int maxSize,
        PoolReusePolicy reusePolicy = PoolReusePolicy.ReuseState)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        if (maxSize < 1)
            throw new ArgumentOutOfRangeException(nameof(maxSize), maxSize, "Pool size must be at least 1.");
        MaxSize = maxSize;
        _reusePolicy = reusePolicy;
        _gate = new SemaphoreSlim(maxSize, maxSize);
    }

    /// <summary>Convenience: a pool whose every engine is a fresh
    /// <see cref="PrologEngine"/> consulted with <paramref name="program"/>.</summary>
    public static EnginePool FromSource(string program, int maxSize,
        PoolReusePolicy reusePolicy = PoolReusePolicy.ReuseState)
    {
        ArgumentNullException.ThrowIfNull(program);
        return new EnginePool(() =>
        {
            var e = new PrologEngine();
            e.ConsultString(program);
            return e;
        }, maxSize, reusePolicy);
    }

    /// <summary>The maximum number of engines lent simultaneously.</summary>
    public int MaxSize { get; }

    /// <summary>The number of engines created so far (≤ <see cref="MaxSize"/>).
    /// Engines are created lazily, so this grows only under contention.</summary>
    public int Created => Volatile.Read(ref _created);

    /// <summary>Rents an engine, blocking until one is free (or
    /// <paramref name="cancellationToken"/> fires). Dispose the returned
    /// <see cref="Lease"/> to return the engine to the pool.</summary>
    public Lease Rent(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _gate.Wait(cancellationToken);
        return Acquire();
    }

    /// <summary>Asynchronously rents an engine, awaiting a free slot. The query
    /// itself still runs synchronously on the rented engine — use
    /// <c>PrologEngine.QueryAsync</c> to run it off the calling thread.</summary>
    public async Task<Lease> RentAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Acquire();
    }

    private Lease Acquire()
    {
        // Hold a permit; hand out an idle engine or build a fresh one. If the
        // factory throws, release the permit so the slot isn't leaked.
        try
        {
            if (_idle.TryTake(out var engine))
                return new Lease(this, engine);
            engine = _factory();
            Interlocked.Increment(ref _created);
            return new Lease(this, engine);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private void Return(PrologEngine engine)
    {
        // FreshEngine: drop the used engine — the next Acquire builds a clean one
        // via the factory, so no rental sees a previous rental's mutations.
        if (_reusePolicy == PoolReusePolicy.ReuseState)
            _idle.Add(engine);
        _gate.Release();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(EnginePool));
    }

    /// <summary>Disposes the pool. In-flight leases are unaffected — returning
    /// one after disposal simply drops its engine. Does not abort running
    /// queries.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        while (_idle.TryTake(out _)) { }
        _gate.Dispose();
    }

    /// <summary>A rented engine. Dispose to return it to the pool; access the
    /// engine through <see cref="Engine"/> until then.</summary>
    public sealed class Lease : IDisposable
    {
        private EnginePool? _pool;

        internal Lease(EnginePool pool, PrologEngine engine)
        {
            _pool = pool;
            Engine = engine;
        }

        /// <summary>The rented engine. Valid until this lease is disposed.</summary>
        public PrologEngine Engine { get; }

        /// <summary>Returns the engine to the pool. Idempotent.</summary>
        public void Dispose()
        {
            var pool = Interlocked.Exchange(ref _pool, null);
            if (pool is null) return;
            if (Volatile.Read(ref pool._disposed) != 0) return;   // pool gone: drop the engine
            pool.Return(Engine);
        }
    }
}
