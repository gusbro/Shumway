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
    // The hosts whose outermost run is in this domain: they mark at its safe
    // points. Read and changed on the domain's thread only.
    private readonly List<HostAtomClaim> _hosts = new();
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

    // Every member is asked: the one running marks the domain, and the hosts running
    // in it, at its next safe point; the others find it marked when they resume.
    void IAtomClaimantOwner.RequestMark()
    {
        foreach (var member in Volatile.Read(ref _members)) member.RequestAtomMarkAtSafePoint();
    }

    internal void AddHost(HostAtomClaim host) => _hosts.Add(host);

    internal void RemoveHost(HostAtomClaim host) => _hosts.Remove(host);

    // At a safe point of a member: the hosts running here that a collection waits for.
    internal void MarkRunningHosts()
    {
        for (int i = 0; i < _hosts.Count; i++) _hosts[i].MarkIfWaited();
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

/// <summary>
/// The atoms a host keeps beyond its activations (ADR-003): an engine's global
/// variables, recorded database and flag values. The host changes them only from
/// its activations' runs, so its claimant runs from the outermost of those runs to
/// its end, and meanwhile marks at the safe points of the domain that run is in.
/// </summary>
public sealed class HostAtomClaim : IAtomClaimantOwner
{
    private readonly IAtomHolder _host;
    private readonly AtomMarkSet _found = new();
    private int _runDepth;
    // The domain of the outermost run.
    private AtomDomain? _runningIn;

    /// <summary>What the host claims atoms through.</summary>
    public AtomClaimant Claimant { get; }

    public HostAtomClaim(IAtomHolder host)
    {
        _host = host;
        Claimant = AtomTable.RegisterClaimant(this);
    }

    internal void EnterRun(AtomDomain? domain)
    {
        if (_runDepth++ != 0) return;
        Claimant.EnterRun();
        domain?.AddHost(this);
        _runningIn = domain;
    }

    internal void ExitRun(bool completed)
    {
        if (--_runDepth != 0) return;
        _runningIn?.RemoveHost(this);
        _runningIn = null;
        Claimant.ExitRun(completed);
    }

    /// <summary>The host changes what it keeps from outside a run (a consult, the
    /// setup of a query): busy as in a run until <see cref="ExitHostChange"/>, with no
    /// safe point, so a collection waits for the change to end and marks there.</summary>
    public void EnterHostChange() => EnterRun(null);

    /// <summary>The change <see cref="EnterHostChange"/> began is over;
    /// <paramref name="completed"/> unless by an exception.</summary>
    public void ExitHostChange(bool completed) => ExitRun(completed);

    internal void MarkIfWaited()
    {
        if (AtomTable.WaitsFor(Claimant.LastMark)) Mark();
    }

    void IAtomClaimantOwner.MarkHeld() => Mark();

    // Nothing to ask: a host runs inside a run of its domain, which registered
    // before the host's run began, so the collection asks the domain too, and its
    // safe points serve the host first.
    void IAtomClaimantOwner.RequestMark() { }

    private void Mark()
    {
        long epoch = AtomTable.BeginMark();
        _found.Reset();
        _host.MarkAtoms(_found);
        Claimant.Apply(epoch, _found.Transient());
    }
}

public sealed partial class Activation
{
    private AtomDomain? _atomDomain;
    private HostAtomClaim? _hostAtomClaim;
    // Where GcMarkReferents records the atoms it reaches; null outside an atom mark.
    private AtomMarkSet? _atomMarkSet;
    private bool _atomMarkRequested;

    /// <summary>The domain this activation claims atoms with, once it joined one.</summary>
    public AtomDomain? AtomDomain => _atomDomain;

    /// <summary>The host whose runs this activation's runs are, once it joined a domain.</summary>
    public HostAtomClaim? HostAtomClaim => _hostAtomClaim;

    /// <summary>Sets <c>reached[i]</c> for each range [<paramref name="starts"/>[i],
    /// <paramref name="ends"/>[i]) of the buffer this activation runs on (ascending,
    /// disjoint) that a word of its control state points into: code dropped from
    /// the buffer that it can still run. Dropped code has no way in, so only a word
    /// copied while running in it leads back. Read conservatively: a word that only
    /// looks like an address keeps a range longer.</summary>
    public void MarkReachedCode(ReadOnlySpan<int> starts, ReadOnlySpan<int> ends, Span<bool> reached)
    {
        // Every word of the control state that can hold a code address: the
        // continuation, the program counter and the return of the builtin in
        // progress, the control stack's words, the catches' recoveries.
        Reach(_cp, starts, ends, reached);
        Reach(_p, starts, ends, reached);
        Reach(BuiltinReturnPc, starts, ends, reached);
        for (int i = 0; i < _stackTop; i++)
            if (_stack[i].Tag == Tag.RawInt) Reach((int)_stack[i].Data, starts, ends, reached);
        for (int i = 0; i < _catchFrames.Count; i++) Reach(_catchFrames[i].RecoveryCp, starts, ends, reached);
    }

    /// <summary>Sets <c>reached[i]</c> for each of <paramref name="functorIds"/>
    /// (ascending) that a resume marker among this activation's control words
    /// names: compiled code of that predicate it can still resume into. The
    /// words are those of <see cref="MarkReachedCode"/>.</summary>
    public void MarkReachedMarkers(ReadOnlySpan<int> functorIds, Span<bool> reached)
    {
        ReachMarker(_cp, functorIds, reached);
        ReachMarker(_p, functorIds, reached);
        ReachMarker(BuiltinReturnPc, functorIds, reached);
        for (int i = 0; i < _stackTop; i++)
            if (_stack[i].Tag == Tag.RawInt) ReachMarker((int)_stack[i].Data, functorIds, reached);
        for (int i = 0; i < _catchFrames.Count; i++) ReachMarker(_catchFrames[i].RecoveryCp, functorIds, reached);
    }

    private static void ReachMarker(int word, ReadOnlySpan<int> functorIds, Span<bool> reached)
    {
        if (!IsResumeMarker(word)) return;
        int fid = DecodeResumeMarker(word).FunctorId;
        int lo = 0, hi = functorIds.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (functorIds[mid] < fid) lo = mid + 1;
            else if (functorIds[mid] > fid) hi = mid - 1;
            else
            {
                reached[mid] = true;
                return;
            }
        }
    }

    /// <summary>The compiled code that stored itself as running
    /// (<see cref="_ilArtifact"/>); 0 for none.</summary>
    public int RunningArtifact => _ilArtifact;

    /// <summary>Adds to <paramref name="into"/> the delegate of every compiled
    /// choice point on the stack, and the delegate it wraps: the code those
    /// choice points resume into.</summary>
    public void CollectIlChoicePointDelegates(List<object> into)
    {
        for (int i = 0; i < _ilCpTop; i++)
        {
            var del = _ilCpStack[i].Del;
            into.Add(del);
            if (del.Target is { } target) into.Add(target);
        }
    }

    private static void Reach(int word, ReadOnlySpan<int> starts, ReadOnlySpan<int> ends, Span<bool> reached)
    {
        int lo = 0, hi = starts.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (word < starts[mid]) hi = mid - 1;
            else if (word >= ends[mid]) lo = mid + 1;
            else
            {
                reached[mid] = true;
                return;
            }
        }
    }

    /// <summary>Joins the domain of <paramref name="outer"/>, the activation whose builtin
    /// this one runs inside, or a new one: at its first run, once its setup wrote
    /// what it writes, since a collection may mark it from another thread from here.
    /// The runs of the activation are runs of <paramref name="host"/> as well.</summary>
    public void JoinAtomDomain(Activation? outer, HostAtomClaim? host = null)
    {
        if (_atomDomain is not null) return;
        _atomDomain = outer?._atomDomain ?? new AtomDomain();
        _atomDomain.Join(this);
        _hostAtomClaim = host;
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
    public void EnterAtomRun()
    {
        if (_atomDomain is not { } domain) return;
        domain.EnterRun();
        _hostAtomClaim?.EnterRun(domain);
    }

    /// <summary>The run <see cref="EnterAtomRun"/> began has stopped;
    /// <paramref name="completed"/> unless by an exception.</summary>
    public void LeaveAtomRun(bool completed)
    {
        if (_atomDomain is not { } domain) return;
        _hostAtomClaim?.ExitRun(completed);
        domain.ExitRun(completed);
    }

    /// <summary>From any thread: the activation marks its domain at its next safe
    /// point, for a collection that waits for it.</summary>
    public void RequestAtomMarkAtSafePoint() => SetSafePointRequest(SafePointAtomMark);

    // The hosts running in the domain first. A heap collection due at this safe
    // point marks the domain in its own trace; without one, the trace alone, from
    // wherever the safe point stands.
    private void ServeAtomMarkRequest(bool heapCollectionFollows)
    {
        if (_atomDomain is not { } domain) return;
        domain.MarkRunningHosts();
        if (!AtomTable.WaitsFor(domain.Claimant.LastMark)) return;
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
