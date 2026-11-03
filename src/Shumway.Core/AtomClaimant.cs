namespace Shumway.Core;

/// <summary>
/// What owns an <see cref="AtomClaimant"/> (ADR-003): it marks from any thread
/// while its claimant is held, and is asked to mark itself while it runs.
/// </summary>
public interface IAtomClaimantOwner
{
    /// <summary>Marks every atom the owner holds and applies the claims, with its
    /// claimant held: the owner is not running, or it is the one marking.</summary>
    void MarkHeld();

    /// <summary>From any thread: asks the running owner to mark at its next safe
    /// point.</summary>
    void RequestMark();
}

/// <summary>
/// A holder of transient atoms (ADR-003): the activations of one domain, or an
/// engine. It holds the ascending list of transient atom ids its last mark found,
/// and the epoch of that mark: every atom it took and has not found since was
/// stamped at or after it. A claimant whose owner the .NET GC collected without
/// releasing it is released by the next collection.
///
/// Its owner runs between <see cref="EnterRun"/> and <see cref="ExitRun"/>. A
/// collection marks a claimant on its own thread when it can hold it idle, and asks
/// a running one to mark itself, so a claimant that does not run never holds a
/// collection back.
/// </summary>
public sealed class AtomClaimant
{
    private const int StateIdle = 0, StateRunning = 1, StateHeld = 2;

    private int[] _held = System.Array.Empty<int>();
    private int _heldCount;
    private long _lastMark;
    private int _state;
    // Runs begun, and their count at the last mark made with the claimant held:
    // while they are equal the owner has taken nothing since that mark.
    private long _runs;
    private long _runsAtHeldMark = -1;

    // Claims that follow what the owner holds as it changes (an engine's code), a
    // count per atom: the claim is taken when the count leaves zero and given up
    // when it returns there. Changed while the owner runs; a release gives them up.
    private Dictionary<int, int>? _standing;
    private int[] _changed = System.Array.Empty<int>();

    internal readonly WeakReference<IAtomClaimantOwner> Owner;
    internal bool Registered;

    internal AtomClaimant(IAtomClaimantOwner owner, long epoch)
    {
        Owner = new WeakReference<IAtomClaimantOwner>(owner);
        _lastMark = epoch;
    }

    /// <summary>The epoch of the last mark, or of the registration before any.</summary>
    public long LastMark => Volatile.Read(ref _lastMark);

    /// <summary>The transient atoms the last mark found, ascending.</summary>
    public ReadOnlySpan<int> Held => new(_held, 0, _heldCount);

    /// <summary>Whether its owner is between <see cref="EnterRun"/> and <see cref="ExitRun"/>.</summary>
    public bool IsRunning => Volatile.Read(ref _state) == StateRunning;

    /// <summary>Marks a collection made with the claimant held that scanned the
    /// owner. For tests.</summary>
    internal int HeldScans { get; private set; }

    /// <summary>Ends a mark that began at <paramref name="markEpoch"/>
    /// (<see cref="AtomTable.BeginMark"/>): the claimant now holds
    /// <paramref name="found"/>, ascending and distinct, and publishes the epoch
    /// once the claims are counted.</summary>
    public void Apply(long markEpoch, ReadOnlySpan<int> found)
    {
        AtomTable.ApplyClaims(Held, found);
        if (_held.Length < found.Length) _held = new int[found.Length + (found.Length >> 2) + 16];
        found.CopyTo(_held);
        _heldCount = found.Length;
        Volatile.Write(ref _lastMark, markEpoch);
        AtomTable.CompletePendingCollection();
    }

    /// <summary>Its owner starts running, waiting while a collection marks it.</summary>
    public void EnterRun()
    {
        if (Interlocked.CompareExchange(ref _state, StateRunning, StateIdle) != StateIdle)
        {
            var spin = new SpinWait();
            do spin.SpinOnce();
            while (Interlocked.CompareExchange(ref _state, StateRunning, StateIdle) != StateIdle);
        }
        _runs++;
    }

    /// <summary>Its owner stops running. A mark a collection waits for is made now,
    /// unless the run ended in an exception: the owner's state may be half updated.</summary>
    public void ExitRun(bool completed)
    {
        // A full fence before the collection's target is read; a collection
        // publishes its target before it tries to hold the claimant. One of the two
        // sees the other, so a collection is never left waiting for this mark.
        Interlocked.Exchange(ref _state, StateIdle);
        if (completed && AtomTable.WaitsFor(LastMark)) TryMarkHeld();
    }

    /// <summary>The owner holds each of <paramref name="atomIds"/> once more, beyond
    /// what its marks find, until it drops them: an engine's code as it lands. Called
    /// while the owner runs, so a collection cannot complete between the intern of
    /// an atom and its claim.</summary>
    public void Retain(ReadOnlySpan<int> atomIds)
    {
        System.Diagnostics.Debug.Assert(IsRunning, "a standing claim changed outside a run");
        var standing = _standing ??= new Dictionary<int, int>();
        int n = 0;
        foreach (int id in atomIds)
        {
            standing.TryGetValue(id, out int count);
            standing[id] = count + 1;
            if (count == 0) Changed(ref n, id);
        }
        if (n == 0) return;
        System.Array.Sort(_changed, 0, n);
        AtomTable.ApplyClaims(ReadOnlySpan<int>.Empty, new ReadOnlySpan<int>(_changed, 0, n));
    }

    /// <summary>The owner no longer holds <paramref name="atomIds"/> once each, as
    /// <see cref="Retain"/> took them: an atom it holds no more loses its claim,
    /// stamped now, so it outlives the claimants that have not marked since. Called
    /// on the owner's thread, running or not.</summary>
    public void Drop(ReadOnlySpan<int> atomIds)
    {
        if (_standing is not { } standing) return;
        int n = 0;
        foreach (int id in atomIds)
        {
            if (!standing.TryGetValue(id, out int count)) continue;
            if (count > 1) { standing[id] = count - 1; continue; }
            standing.Remove(id);
            Changed(ref n, id);
        }
        if (n == 0) return;
        System.Array.Sort(_changed, 0, n);
        AtomTable.ApplyClaims(new ReadOnlySpan<int>(_changed, 0, n), ReadOnlySpan<int>.Empty);
    }

    /// <summary>How many times the owner holds <paramref name="atomId"/> beyond its
    /// marks. For tests.</summary>
    internal int StandingCount(int atomId)
        => _standing is { } s && s.TryGetValue(atomId, out int n) ? n : 0;

    private void Changed(ref int n, int id)
    {
        if (n == _changed.Length) System.Array.Resize(ref _changed, System.Math.Max(16, n * 2));
        _changed[n++] = id;
    }

    /// <summary>Gives up every claim and leaves the registry.</summary>
    public void Release()
    {
        AtomTable.Unregister(this);
        AtomTable.CompletePendingCollection();
    }

    // A collection's turn: the claimant is marked here when it is idle, and asked
    // to mark itself when it runs.
    internal void MarkOrAsk()
    {
        if (!AtomTable.WaitsFor(LastMark) || !Owner.TryGetTarget(out var owner)) return;
        if (TryMarkHeld()) return;
        owner.RequestMark();
        // It may have stopped between the two, and its stop seen nothing to do.
        TryMarkHeld();
    }

    private bool TryMarkHeld()
    {
        if (Interlocked.CompareExchange(ref _state, StateHeld, StateIdle) != StateIdle) return false;
        try
        {
            if (AtomTable.WaitsFor(LastMark) && Owner.TryGetTarget(out var owner))
            {
                if (_runs == _runsAtHeldMark)
                {
                    // Nothing ran since the last mark: it holds what it held.
                    Volatile.Write(ref _lastMark, AtomTable.BeginMark());
                    AtomTable.CompletePendingCollection();
                }
                else
                {
                    HeldScans++;
                    owner.MarkHeld();
                    _runsAtHeldMark = _runs;
                }
            }
        }
        finally { Volatile.Write(ref _state, StateIdle); }
        return true;
    }

    // Under the table lock: the claims are given up, and an atom that loses its
    // last one is stamped, as any lost claim is.
    internal void ReleaseLocked()
    {
        AtomTable.ApplyClaims(Held, ReadOnlySpan<int>.Empty);
        _heldCount = 0;
        if (_standing is not { Count: > 0 } standing) return;
        var ids = new int[standing.Count];
        standing.Keys.CopyTo(ids, 0);
        System.Array.Sort(ids);
        _standing = null;
        AtomTable.ApplyClaims(ids, ReadOnlySpan<int>.Empty);
    }
}
