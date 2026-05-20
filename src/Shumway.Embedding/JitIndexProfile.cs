namespace Shumway.Embedding;

/// <summary>
/// Chunk 75 — JIT indexing. A per-engine runtime profile of how often
/// each predicate is called, used to decide <em>when</em> a dynamic
/// predicate is worth indexing.
///
/// <para>ADR-007 deferred JIT indexing to Phase 3: "observes runtime
/// call patterns and builds indexes adaptively". Shumway's realisation:
/// a dynamic predicate compiles to a plain <c>try_me_else</c> chain
/// (O(N) dispatch, cheap to build) until its call count crosses
/// <see cref="Threshold"/>. The first query after that recompiles it
/// with full multi-arg indexing (O(1) dispatch, switch tables). A
/// dynamic predicate that's rarely called — or one churning under
/// heavy <c>assertz</c>/<c>retract</c> — never pays the switch-table
/// build cost.</para>
///
/// <para>The counter is bumped on every Call/Execute dispatch through
/// <see cref="Tier1DispatcherAdapter"/> (the same hook the Tier-1 IL
/// promotion store already uses). The profile is engine-wide and
/// survives across queries, so the call count accumulates over a
/// program's lifetime.</para>
/// </summary>
public sealed class JitIndexProfile
{
    private readonly Dictionary<int, int> _callCounts = new();

    // The hotness decision baked into each functor's most recent
    // compile. Lets the query-setup path detect a cold→hot flip and
    // invalidate the stale (unindexed) cached compile.
    private readonly Dictionary<int, bool> _compiledHot = new();

    /// <summary>Call count at which a dynamic predicate becomes
    /// eligible for indexed recompilation. Configurable so tests can
    /// force the transition cheaply; the default is tuned so a
    /// genuinely hot predicate crosses it quickly while a one-off call
    /// never does.</summary>
    public int Threshold { get; set; } = 16;

    /// <summary>Records one call to <paramref name="functorId"/>.
    /// Cheap — a single dictionary increment, the same cost the IL
    /// promotion counter already pays per dispatch.</summary>
    public void RecordCall(int functorId)
    {
        _callCounts.TryGetValue(functorId, out int count);
        _callCounts[functorId] = count + 1;
    }

    /// <summary>The accumulated call count for diagnostics / tests.</summary>
    public int CallCountFor(int functorId)
        => _callCounts.TryGetValue(functorId, out int c) ? c : 0;

    /// <summary>True once <paramref name="functorId"/> has been called
    /// at least <see cref="Threshold"/> times — the engine should
    /// compile it with indexing enabled.</summary>
    public bool IsHot(int functorId) => CallCountFor(functorId) >= Threshold;

    /// <summary>True when the hotness of <paramref name="functorId"/>
    /// differs from what its most recent compile assumed — i.e. a
    /// cold predicate has gone hot (or, in principle, the reverse). The
    /// query-setup path uses this to drop a stale cached compile so the
    /// predicate is rebuilt at the right indexing level.</summary>
    public bool HotnessChangedSinceCompile(int functorId)
    {
        bool now = IsHot(functorId);
        return !_compiledHot.TryGetValue(functorId, out bool wasHot) || wasHot != now;
    }

    /// <summary>Records the hotness decision used to compile
    /// <paramref name="functorId"/>, so a later
    /// <see cref="HotnessChangedSinceCompile"/> can detect a flip.</summary>
    public void RecordCompileDecision(int functorId, bool indexed)
        => _compiledHot[functorId] = indexed;

    /// <summary>Copies this profile's state into <paramref name="other"/>
    /// — used by <see cref="PrologEngine.CreateSubEngine"/> so a forked
    /// engine starts from the parent's accumulated call history.</summary>
    public void CopyInto(JitIndexProfile other)
    {
        other.Threshold = Threshold;
        foreach (var (fid, count) in _callCounts) other._callCounts[fid] = count;
        foreach (var (fid, hot) in _compiledHot) other._compiledHot[fid] = hot;
    }
}
