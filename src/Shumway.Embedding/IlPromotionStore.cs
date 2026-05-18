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
    private readonly IlPredicateCompiler _compiler = new();

    /// <summary>Invocation count required before the store attempts an IL
    /// compile for a predicate. <c>0</c> disables promotion entirely (the
    /// store still works but never produces a delegate). Defaults to
    /// <c>0</c>; callers / tests opt in by setting a positive value.</summary>
    public int Threshold { get; set; }

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
    public PredicateDelegate? RecordInvocation(int functorId, CompiledPredicate predicate)
    {
        if (Threshold <= 0) return null;
        if (_delegates.ContainsKey(functorId)) return _delegates[functorId];
        if (_unpromotable.Contains(functorId)) return null;

        _counters.TryGetValue(functorId, out int count);
        count++;
        _counters[functorId] = count;

        if (count < Threshold) return null;

        if (!_compiler.CanCompile(predicate))
        {
            _unpromotable.Add(functorId);
            return null;
        }

        var del = _compiler.Compile(predicate);
        _delegates[functorId] = del;
        return del;
    }

    /// <summary>Eagerly promotes <paramref name="predicate"/> without
    /// going through the counter, returning the resulting delegate on
    /// success. Useful for warm-up paths (e.g. AOT bundles) that want
    /// hot predicates IL-compiled before the first query.</summary>
    public PredicateDelegate? Warm(int functorId, CompiledPredicate predicate)
    {
        if (_delegates.TryGetValue(functorId, out var existing)) return existing;
        if (_unpromotable.Contains(functorId)) return null;
        if (!_compiler.CanCompile(predicate))
        {
            _unpromotable.Add(functorId);
            return null;
        }
        var del = _compiler.Compile(predicate);
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

    /// <summary>True when <paramref name="functorId"/> has been examined
    /// and rejected by the IL compiler — no further compile attempts
    /// will fire for it.</summary>
    public bool IsUnpromotable(int functorId) => _unpromotable.Contains(functorId);
}
