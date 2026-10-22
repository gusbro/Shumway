namespace Shumway.Compiler.Wam;

/// <summary>
/// Deduplicating pool of literal values keyed by content, whose ids the
/// bytecode embeds (<c>put_pstr</c>, <c>get_float</c>, <c>put_blit</c>, ...).
/// An entry is pinned (static code names it, for the pool's life) or
/// counted: every code unit that names it adds a reference when it enters
/// the live code and releases it when it leaves. A counted entry at zero is
/// freed by the next sweep, its id reused by a later intern.
///
/// <para><see cref="Version"/> changes whenever the value an id names may
/// have changed (a new or reused id, a sweep): a copy of the pool is current
/// while its version matches. A freed id reads as <c>default</c> until it is
/// reused; no live code names it.</para>
/// </summary>
public sealed class LiteralPool<T> where T : notnull
{
    private const int Pinned = -1;
    private const int Freed = -2;

    private readonly Dictionary<T, int> _byContent = new();
    private readonly List<T> _byId = new();
    // Pinned, Freed, or the count of code units naming the id.
    private readonly List<int> _refs = new();
    // In _zeroed: a counted entry at zero waits there for the sweep; one
    // referenced again since stays listed and is skipped. _zeroCount is
    // the listed entries still at zero, what the sweep would free.
    private readonly List<bool> _queued = new();
    private readonly List<int> _zeroed = new();
    private int _zeroCount;
    private readonly List<int> _free = new();

    /// <summary>Changes whenever the value at some id may have changed.</summary>
    public int Version { get; private set; }

    /// <summary>The id of <paramref name="value"/>, interned if new: a freed
    /// id first, else the next one. A new entry is counted at zero and
    /// waits for the sweep until a code unit references it.</summary>
    public int Intern(T value)
    {
        if (_byContent.TryGetValue(value, out int id)) return id;
        if (_free.Count > 0)
        {
            id = _free[_free.Count - 1];
            _free.RemoveAt(_free.Count - 1);
            _byId[id] = value;
            _refs[id] = 0;
        }
        else
        {
            id = _byId.Count;
            _byId.Add(value);
            _refs.Add(0);
            _queued.Add(false);
        }
        _byContent[value] = id;
        Version++;
        Queue(id);
        return id;
    }

    /// <summary>Interns and pins: the value keeps its id for the pool's life.</summary>
    public int InternPinned(T value)
    {
        int id = Intern(value);
        Pin(id);
        return id;
    }

    /// <summary>Pins <paramref name="id"/>: never counted, never freed.</summary>
    public void Pin(int id)
    {
        int r = Refs(id);
        if (r == Freed) throw Dead(id, "pin");
        if (r == 0) _zeroCount--;
        _refs[id] = Pinned;
    }

    /// <summary>A code unit naming <paramref name="id"/> entered the live code.</summary>
    public void AddRef(int id)
    {
        int r = Refs(id);
        if (r == 0) _zeroCount--;
        if (r >= 0) _refs[id] = r + 1;
        else if (r == Freed) throw Dead(id, "reference");
    }

    /// <summary>A code unit naming <paramref name="id"/> left the live code.
    /// At zero the entry waits for the sweep; a release past zero is a
    /// unit released twice or never added, a bug.</summary>
    public void Release(int id)
    {
        int r = Refs(id);
        if (r > 0)
        {
            if (--r == 0) Queue(id);
            _refs[id] = r;
        }
        else if (r != Pinned)
            throw new InvalidOperationException(
                $"literal {id} released {(r == Freed ? "after it was freed" : "below zero")}.");
    }

    private void Queue(int id)
    {
        _zeroCount++;
        if (_queued[id]) return;
        _queued[id] = true;
        _zeroed.Add(id);
    }

    /// <summary>Whether the entries released since the last sweep are worth
    /// one: a sweep walks only those, and the copies of the pool refresh
    /// after it, so it waits for a share of the pool.</summary>
    public bool SweepDue => _zeroCount >= SweepFloor && _zeroCount * SweepShare >= _byId.Count;

    private const int SweepFloor = 32;
    private const int SweepShare = 8;

    /// <summary>Frees every counted entry still at zero; returns how many.</summary>
    public int Sweep()
    {
        int freed = 0;
        foreach (int id in _zeroed)
        {
            _queued[id] = false;
            if (_refs[id] != 0) continue;
            _byContent.Remove(_byId[id]);
            _byId[id] = default!;
            _refs[id] = Freed;
            _free.Add(id);
            freed++;
        }
        _zeroed.Clear();
        _zeroCount = 0;
        if (freed > 0) Version++;
        return freed;
    }

    private int Refs(int id)
        => (uint)id < (uint)_refs.Count
            ? _refs[id]
            : throw new InvalidOperationException(
                $"literal {id} is not in the {typeof(T).Name} pool of {_refs.Count}: code compiled against another pool.");

    private static InvalidOperationException Dead(int id, string what)
        => new($"literal {id} is freed; cannot {what} it.");

    /// <summary>A copy of the ids' values, a freed id's being <c>default</c>.</summary>
    public IReadOnlyList<T> Snapshot() => _byId.ToArray();

    /// <summary>A live, copy-free view by id: a pinned or referenced id's
    /// value never moves.</summary>
    public IReadOnlyList<T> Items => _byId;

    /// <summary>The ids in use or free: the length of a copy.</summary>
    public int Count => _byId.Count;

    public bool TryGetId(T value, out int id) => _byContent.TryGetValue(value, out id);

    public bool IsPinned(int id) => _refs[id] == Pinned;
    public bool IsFree(int id) => _refs[id] == Freed;
    /// <summary>The code units naming <paramref name="id"/>; 0 for a pinned or freed one.</summary>
    public int RefCount(int id) => Math.Max(_refs[id], 0);
    public int FreeCount => _free.Count;
    /// <summary>The counted entries at zero, what the next sweep frees.</summary>
    public int PendingCount => _zeroCount;
}
