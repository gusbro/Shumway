using System.Runtime.CompilerServices;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;

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
        if (_delegates.ContainsKey(functorId)) return _delegates[functorId];
        if (_unpromotable.Contains(functorId)) return null;
        if (IsExcludedFromPromotion(functorId))
        {
            _unpromotable.Add(functorId);
            return null;
        }

        _counters.TryGetValue(functorId, out int count);
        count++;
        _counters[functorId] = count;

        if (count < Threshold) return null;

        if (!Compiler.CanCompile(predicate, calleeMap))
        {
            _unpromotable.Add(functorId);
            return null;
        }

        // Chunk 76 — phase-1 PGO compile. For a PGO-eligible shape this
        // is the instrumented form (profile key ≥ 0); otherwise it's a
        // plain compile (profile key -1) and no phase 2 will fire.
        var result = Compiler.CompileInstrumented(predicate, calleeMap);
        _delegates[functorId] = result.Delegate;
        if (result.ProfileKey >= 0)
            _pgoProfileKeys[functorId] = result.ProfileKey;
        return result.Delegate;
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
            var optimized = Compiler.CompileOptimized(predicate, profileKey, calleeMap);
            _delegates[functorId] = optimized;
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
        if (IsExcludedFromPromotion(functorId))
        {
            _unpromotable.Add(functorId);
            return null;
        }
        if (!Compiler.CanCompile(predicate, calleeMap))
        {
            _unpromotable.Add(functorId);
            return null;
        }
        var del = Compiler.Compile(predicate, calleeMap);
        _delegates[functorId] = del;
        return del;
    }

    /// <summary>Returns the current invocation count for diagnostics /
    /// tests. Returns <c>0</c> when no count has been recorded yet.</summary>
    public int CountFor(int functorId)
        => _counters.TryGetValue(functorId, out int c) ? c : 0;

    /// <summary>True when <paramref name="functorId"/> has been bound to
    /// an IL delegate. Diagnostic surface for tests.</summary>
    public bool IsPromoted(int functorId) => _delegates.ContainsKey(functorId);

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
        _delegates[functorId] = del;
    }

    /// <summary>True when <paramref name="functorId"/> has been examined
    /// and rejected by the IL compiler — no further compile attempts
    /// will fire for it.</summary>
    public bool IsUnpromotable(int functorId) => _unpromotable.Contains(functorId);
}
