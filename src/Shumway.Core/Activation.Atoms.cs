namespace Shumway.Core;

/// <summary>
/// The activations that claim atoms together (ADR-003): one that runs, and those
/// that run inside its builtins on the same thread and whatever runs inside theirs.
/// A mark of the domain marks every member, the one that marks inside its heap
/// collection and the others without moving anything: a member suspended inside a
/// builtin keeps heap indices in that builtin's locals. The domain runs on one
/// thread at a time; its outermost run holds the claimant.
/// </summary>
public sealed class AtomDomain : IAtomClaimantOwner
{
    // Replaced, never changed: a collection on another thread reads it.
    private Activation[] _members = System.Array.Empty<Activation>();
    private int _runDepth;
    /// <summary>What the domain claims atoms through.</summary>
    public AtomClaimant Claimant { get; }
    internal readonly AtomMarkSet Found = new();

    public AtomDomain() => Claimant = AtomTable.RegisterClaimant(this);

    internal void Join(Activation member)
    {
        var members = new Activation[_members.Length + 1];
        System.Array.Copy(_members, members, _members.Length);
        members[^1] = member;
        Volatile.Write(ref _members, members);
    }

    // The last member out gives up the domain's claims.
    internal void Leave(Activation member)
    {
        int i = System.Array.IndexOf(_members, member);
        if (i < 0) return;
        var members = new Activation[_members.Length - 1];
        System.Array.Copy(_members, members, i);
        System.Array.Copy(_members, i + 1, members, i, members.Length - i);
        Volatile.Write(ref _members, members);
        if (members.Length == 0) Claimant.Release();
    }

    internal void EnterRun()
    {
        if (_runDepth++ == 0) Claimant.EnterRun();
    }

    internal void ExitRun(bool completed)
    {
        if (--_runDepth == 0) Claimant.ExitRun(completed);
    }

    void IAtomClaimantOwner.MarkHeld()
    {
        long epoch = BeginMark();
        foreach (var member in _members) member.MarkAtomsOnly(Found);
        Claimant.Apply(epoch, Found.Transient());
    }

    // Every member is asked: the one running marks the domain at its next safe
    // point, and the others find it marked when they resume.
    void IAtomClaimantOwner.RequestMark()
    {
        foreach (var member in Volatile.Read(ref _members)) member.RequestAtomMarkAtSafePoint();
    }

    internal long BeginMark()
    {
        long epoch = AtomTable.BeginMark();
        Found.Reset();
        return epoch;
    }

    // The members other than the one whose collection marked itself.
    internal void EndMark(long epoch, Activation marked)
    {
        foreach (var member in _members)
            if (!ReferenceEquals(member, marked)) member.MarkAtomsOnly(Found);
        Claimant.Apply(epoch, Found.Transient());
    }
}

public sealed partial class Activation
{
    private AtomDomain? _atomDomain;
    // Where GcMarkReferents records the atoms it reaches; null outside an atom mark.
    private AtomMarkSet? _atomMarkSet;
    private bool _atomMarkRequested;

    /// <summary>The domain this activation claims atoms with, once it joined one.</summary>
    public AtomDomain? AtomDomain => _atomDomain;

    /// <summary>Joins the domain of <paramref name="outer"/>, the activation whose builtin
    /// this one runs inside, or a new one: at its first run, once its setup wrote
    /// what it writes, since a collection may mark it from another thread from here.</summary>
    public void JoinAtomDomain(Activation? outer)
    {
        if (_atomDomain is not null) return;
        _atomDomain = outer?._atomDomain ?? new AtomDomain();
        _atomDomain.Join(this);
    }

    /// <summary>The activation is done: it leaves its domain, inside a run of the
    /// domain, since the last member out releases the claims.</summary>
    public void LeaveAtomDomain()
    {
        if (_atomDomain is not { } domain) return;
        _atomDomain = null;
        domain.EnterRun();
        try { domain.Leave(this); }
        finally { domain.ExitRun(completed: false); }
    }

    /// <summary>The activation starts or resumes running: from here until
    /// <see cref="LeaveAtomRun"/> a collection does not mark its domain from another
    /// thread. Anything that writes into the activation runs inside.</summary>
    public void EnterAtomRun() => _atomDomain?.EnterRun();

    /// <summary>The run <see cref="EnterAtomRun"/> began has stopped;
    /// <paramref name="completed"/> unless by an exception.</summary>
    public void LeaveAtomRun(bool completed) => _atomDomain?.ExitRun(completed);

    /// <summary>From any thread: the activation marks its domain at its next safe
    /// point, for a collection that waits for it.</summary>
    public void RequestAtomMarkAtSafePoint() => SetSafePointRequest(SafePointAtomMark);

    // A heap collection due at this safe point marks in its own trace; without one,
    // the trace alone, from wherever the safe point stands.
    private void ServeAtomMarkRequest(bool heapCollectionFollows)
    {
        if (_atomDomain is not { } domain || !AtomTable.WaitsFor(domain.Claimant.LastMark)) return;
        if (heapCollectionFollows && !_gcDiagActive && _gcInhibit == 0
            && _gcThreshold > 0 && _heapTop >= _gcThreshold)
            return;
        AtomTracesAlone++;
        long epoch = domain.BeginMark();
        MarkAtomsOnly(domain.Found);
        domain.EndMark(epoch, this);
    }

    /// <summary>Marks of the domain made by a trace of their own, outside a heap
    /// collection. For tests.</summary>
    internal int AtomTracesAlone { get; private set; }

    /// <summary>The next heap collection marks this activation's domain as well.</summary>
    public void RequestAtomMark() => _atomMarkRequested = _atomDomain is not null;

    /// <summary>Marks the domain now, at a safe point: a heap collection that marks,
    /// or a mark alone when a collection cannot run. False without a domain.</summary>
    public bool MarkAtomDomain()
    {
        if (_atomDomain is not { } domain) return false;
        _atomMarkRequested = true;
        CollectHeap();
        if (!_atomMarkRequested) return true;
        _atomMarkRequested = false;
        long epoch = domain.BeginMark();
        MarkAtomsOnly(domain.Found);
        domain.EndMark(epoch, this);
        return true;
    }

    // Starts an atom mark inside a collection that is about to trace, when one is
    // requested or an atom collection waits for this running domain:
    // GcMarkReferents records into the domain's set from here.
    private long BeginCollectionAtomMark()
    {
        if (_atomDomain is not { } domain
            || !(_atomMarkRequested
                 || (domain.Claimant.IsRunning && AtomTable.WaitsFor(domain.Claimant.LastMark))))
            return 0;
        long epoch = domain.BeginMark();
        _atomMarkSet = domain.Found;
        return epoch;
    }

    // Ends it once the trace and the sweeps are done.
    private void EndCollectionAtomMark(long epoch)
    {
        if (_atomMarkSet is not { } set) return;
        MarkAtomsOutsideCells(set);
        _atomMarkSet = null;
        _atomMarkRequested = false;
        _atomDomain!.EndMark(epoch, this);
    }

    /// <summary>Adds every atom this activation reaches to <paramref name="set"/>
    /// without collecting: the trace of a heap collection, and the atom ids kept
    /// outside cells.</summary>
    internal void MarkAtomsOnly(AtomMarkSet set)
    {
        var saved = _atomMarkSet;
        _atomMarkSet = set;
        try
        {
            int oldTop = _heapTop;
            GcMarkBits(oldTop);
            if (_gcWork is null || _gcWork.Length < 1024) _gcWork = new int[1024];
            _gcWorkTop = 0;
            _gcOldTop = oldTop;
            MarkRoots(oldTop);
            MarkExternalHolders(oldTop);
            MarkExternalTrailRoots(GcMarkReferents);
            OnGcMark?.Invoke(GcMarkCell, GcMarkReferents);
            while (_gcWorkTop > 0)
                GcMarkReferents(_heap[_gcWork[--_gcWorkTop]]);
            MarkAtomsOutsideCells(set);
        }
        finally { _atomMarkSet = saved; }
    }

    // The atom ids an activation keeps outside cells: attribute modules in the
    // trail log and the wakeup queue (those of the table are added as the trace
    // reaches their variables); the keys of the external trail, which are atom
    // ids (IExternalTrailTarget); the findall, bagof and setof solutions in
    // progress; and the objects of builtin choice points and of the foreign
    // table that hold any.
    private void MarkAtomsOutsideCells(AtomMarkSet set)
    {
        foreach (var (home, module, _) in _attrTrailLog)
            if (home != int.MinValue) set.Add(module);
        for (int i = 0; i < _pendingWakeups.Count; i++)
            set.Add(_pendingWakeups[i].Module);
        if (_externalTrailLog is { } external)
            foreach (var (_, key, _, _) in external) set.Add(key);
        if (_allSolutions is { } solutions)
            foreach (var buffer in solutions) buffer.MarkAtoms(set);
        for (int i = 0; i < _ilCpTop; i++)
        {
            if (_ilCpStack[i].Del?.Target is IAtomHolder resume) resume.MarkAtoms(set);
            if (_ilCpStack[i].OnPrune?.Target is IAtomHolder prune) prune.MarkAtoms(set);
        }
        foreach (object? o in _foreignTable)
            if (o is IAtomHolder holder) holder.MarkAtoms(set);
    }
}
