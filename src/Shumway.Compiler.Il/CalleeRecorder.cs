using System.Collections;
using System.Collections.Generic;
using Shumway.Compiler.Wam;

namespace Shumway.Compiler.Il;

/// <summary>
/// The callee map of one compile, recording which predicates the compiler
/// looked up. Every callee whose code a compile copies in (a region member, an
/// inlined leaf or rule, a fail-direct chain) is reached through a lookup, so
/// <see cref="LookedUp"/> holds all of them, and some it only examined: when
/// one of them gets new clauses, the code is stale and has to go.
///
/// <para>The compiler never enumerates a callee map; a compile that did would
/// copy callees this does not see.</para>
/// </summary>
public sealed class CalleeRecorder : IReadOnlyDictionary<int, CompiledPredicate>
{
    private readonly IReadOnlyDictionary<int, CompiledPredicate> _inner;
    private readonly HashSet<int> _lookedUp = new();

    public CalleeRecorder(IReadOnlyDictionary<int, CompiledPredicate> inner) => _inner = inner;

    /// <summary>The map this one reads through: what a cache keyed by the
    /// callee map is keyed by.</summary>
    public IReadOnlyDictionary<int, CompiledPredicate> Inner => _inner;

    /// <summary>The functor ids found by a lookup.</summary>
    public IReadOnlyCollection<int> LookedUp => _lookedUp;

    /// <summary>When the compile emitted a region: each non-root member and the
    /// cursor that enters it (<see cref="RegionCursorKind.MemberEntry"/>).</summary>
    public IReadOnlyList<(int FunctorId, int Cursor)>? RegionMembers { get; internal set; }

    public bool TryGetValue(int key, out CompiledPredicate value)
    {
        if (!_inner.TryGetValue(key, out value!)) return false;
        _lookedUp.Add(key);
        return true;
    }

    public CompiledPredicate this[int key]
    {
        get
        {
            var value = _inner[key];
            _lookedUp.Add(key);
            return value;
        }
    }

    public bool ContainsKey(int key)
    {
        if (!_inner.ContainsKey(key)) return false;
        _lookedUp.Add(key);
        return true;
    }

    public int Count => _inner.Count;
    public IEnumerable<int> Keys => _inner.Keys;
    public IEnumerable<CompiledPredicate> Values => _inner.Values;
    public IEnumerator<KeyValuePair<int, CompiledPredicate>> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The map a cache keyed by callee-map identity should key on.</summary>
    internal static IReadOnlyDictionary<int, CompiledPredicate>? Underlying(
        IReadOnlyDictionary<int, CompiledPredicate>? map)
        => map is CalleeRecorder r ? r._inner : map;
}
