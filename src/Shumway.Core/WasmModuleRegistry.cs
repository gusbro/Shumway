using System.Collections.Generic;

namespace Shumway.Core;

/// <summary>Where a marker runs: a module of the engine and a dispatch cursor
/// inside it.</summary>
public readonly record struct WasmTarget(int ModuleId, int Cursor);

/// <summary>The modules installed in one engine's wasm world, and the resume
/// table they resolve through. A world keeps only the runtime handle of each
/// module (an instance, a table index); everything the host needs to resolve
/// a marker, translate a pc or attribute a deopt lives here, once, for both
/// worlds.
///
/// <para>A functor belongs to at most one module at a time. Installing a
/// module that covers a functor another module already covers takes it over:
/// the old rows of that functor are cleared and the old module keeps running
/// for its other members, reaching the moved functor through the table like
/// any foreign one. Modules are never removed: an open chain may be inside
/// one, and a module every functor has left simply owns no rows.</para>
///
/// <para>INVARIANT: a baked in-module jump never lands in code the table no
/// longer owns. A member calls a sibling of its own module by a baked jump,
/// not a probe (the hot path pays nothing), so when a functor leaves a
/// module, every member of that module that reaches it by baked jumps,
/// transitively, leaves with it: they fall back to bytecode and are
/// re-promoted against the live code. Evict and Install both report the
/// full set so the store drops those delegates too.</para></summary>
public sealed class WasmModuleRegistry
{
    /// <summary>One installed module's maps. <see cref="AddrIndex"/> says
    /// which member owns a build-space address, which is what a relink
    /// translation and a deopt attribution both need.</summary>
    public sealed class Module
    {
        public int Id { get; }
        public IReadOnlyDictionary<int, int> EntryCursorByFid { get; }
        public IReadOnlyDictionary<int, int> CursorByAddress { get; }
        public IReadOnlyDictionary<int, int> EntryAddressByFid { get; }
        public int RegisterDemand { get; }
        public WasmBuildAddressIndex AddrIndex { get; }

        // callee -> the members that reach it by a baked jump. Member pairs
        // only: a call to a non-member is a probe.
        internal readonly Dictionary<int, List<int>> BakedCallersOf = new();

        internal Module(int id,
            IReadOnlyDictionary<int, int> entryCursorByFid,
            IReadOnlyDictionary<int, int> cursorByAddress,
            IReadOnlyDictionary<int, int> entryAddressByFid,
            int registerDemand,
            IEnumerable<(int Caller, int Callee)> callEdges)
        {
            Id = id;
            EntryCursorByFid = entryCursorByFid;
            CursorByAddress = cursorByAddress;
            EntryAddressByFid = entryAddressByFid;
            RegisterDemand = registerDemand;
            AddrIndex = new WasmBuildAddressIndex(entryAddressByFid);
            foreach (var (caller, callee) in callEdges)
            {
                if (caller == callee || !entryCursorByFid.ContainsKey(caller)
                    || !entryCursorByFid.ContainsKey(callee)) continue;
                if (!BakedCallersOf.TryGetValue(callee, out var callers))
                    BakedCallersOf[callee] = callers = new List<int>();
                if (!callers.Contains(caller)) callers.Add(caller);
            }
        }
    }

    private readonly Dictionary<int, Module> _byFid = new();
    private readonly List<Module> _byId = new();
    // Survives eviction: a marker left open by an evicted functor still has
    // to find the bytecode its module was built from.
    private readonly Dictionary<int, int> _entryAddressByFid = new();

    public WasmModuleRegistry(WasmResumeTable table) => Table = table;

    public WasmResumeTable Table { get; }

    /// <summary>The widest register bank any installed module wants; a chain
    /// may hop into any of them, so the engine has to satisfy all.</summary>
    public int RegisterDemand { get; private set; }

    public int ModuleCount => _byId.Count;

    public Module ById(int moduleId) => _byId[moduleId];

    public bool Contains(int functorId) => _byFid.ContainsKey(functorId);

    /// <summary>The module a functor currently runs in, or null.</summary>
    public Module? OwnerOf(int functorId)
        => _byFid.TryGetValue(functorId, out var m) ? m : null;

    /// <summary>Records a module and writes its rows. The id is minted here:
    /// the caller registers its runtime handle under it. <paramref
    /// name="callEdges"/> are the (caller, callee) pairs the module was
    /// compiled with; the member-to-member ones are its baked jumps.
    /// <paramref name="displaced"/> are the functors of OTHER modules this
    /// install pushed to bytecode: the baked callers the taken-over functors
    /// drag along (see the class remarks).</summary>
    public Module Install(
        IReadOnlyDictionary<int, int> entryCursorByFid,
        IReadOnlyDictionary<int, int> cursorByAddress,
        IReadOnlyDictionary<int, int> entryAddressByFid,
        int registerDemand,
        IEnumerable<(int Caller, int Callee)> callEdges,
        out IReadOnlyList<int> displaced)
    {
        int id = Table.NextModuleId();
        if (id != _byId.Count)
            throw new System.InvalidOperationException(
                "the resume table minted an id this registry did not see");
        var m = new Module(id, entryCursorByFid, cursorByAddress, entryAddressByFid,
                           registerDemand, callEdges);
        _byId.Add(m);
        var gone = new HashSet<int>();
        foreach (int fid in entryCursorByFid.Keys) Leave(fid, gone);
        // The taken-over functors themselves run in the new module; only
        // their dragged callers are displaced.
        foreach (int fid in entryCursorByFid.Keys) gone.Remove(fid);
        displaced = new List<int>(gone);
        foreach (int fid in entryCursorByFid.Keys)
        {
            _byFid[fid] = m;
            _entryAddressByFid[fid] = entryAddressByFid[fid];
        }
        // A fresh entry is address 0 under the member's own functor; every
        // other re-entry point belongs to whichever member's range it falls
        // in. Rows are keyed by the OWNER's functor -- the host must decode a
        // build address to that same functor before it can look one up.
        foreach (var (fid, cursor) in entryCursorByFid)
        {
            int marker = Activation.EncodeResumeMarker(fid, 0);
            Table.Set(marker, id, cursor);
            Table.SetCallMarker(fid, marker);
        }
        foreach (var (address, cursor) in cursorByAddress)
        {
            int fid = m.AddrIndex.OwnerFunctorOf(address);
            if (fid < 0) continue;              // precedes every member
            Table.Set(Activation.EncodeResumeMarker(fid, address), id, cursor);
        }
        if (registerDemand > RegisterDemand) RegisterDemand = registerDemand;
        return m;
    }

    /// <summary>Forgets the functors: their rows go to zero, so a marker of
    /// theirs resolves nowhere -- in wasm and on the host alike -- and falls
    /// back to bytecode. The modules stay. Returns everything that left,
    /// the baked callers dragged along included.</summary>
    public IReadOnlyList<int> Evict(IEnumerable<int> functorIds)
    {
        var gone = new HashSet<int>();
        foreach (int fid in functorIds) Leave(fid, gone);
        return new List<int>(gone);
    }

    /// <summary>Takes the functor out of its module together with every
    /// member that reaches it by baked jumps, transitively. A worklist, not
    /// recursion: a module can have thousands of members. A member that
    /// already left (taken over elsewhere) no longer runs the old code, so
    /// the walk stops there.</summary>
    private void Leave(int functorId, HashSet<int> gone)
    {
        if (!_byFid.TryGetValue(functorId, out var m)) return;
        var work = new Stack<int>();
        work.Push(functorId);
        while (work.Count > 0)
        {
            int fid = work.Pop();
            if (!_byFid.TryGetValue(fid, out var owner) || owner != m) continue;
            _byFid.Remove(fid);
            Table.ClearFunctor(fid);
            Table.ClearCallMarker(fid);
            gone.Add(fid);
            if (m.BakedCallersOf.TryGetValue(fid, out var callers))
                foreach (int caller in callers) work.Push(caller);
        }
    }

    /// <summary>Resolves (functor, address) through the rows, exactly as a
    /// module does. A pair no module ever baked has no marker and resolves
    /// nowhere.</summary>
    public bool TryResolve(int functorId, int address, out WasmTarget target)
    {
        target = default;
        if (!Activation.TryGetResumeMarker(functorId, address, out int marker)) return false;
        if (!Table.TryGet(marker, out int moduleId, out int cursor)) return false;
        target = new WasmTarget(moduleId, cursor);
        return true;
    }

    /// <summary>A functor's entry address as its module baked it; still
    /// known after an eviction.</summary>
    public int EntryAddressOf(int functorId) => _entryAddressByFid[functorId];

    /// <summary>Build-space to live-space for an address the functor's own
    /// module baked. Identity for a functor no module covers.</summary>
    public long TranslatePcToLive(int functorId, long buildPc,
        IReadOnlyDictionary<int, int>? liveByFid)
        => _byFid.TryGetValue(functorId, out var m)
            ? m.AddrIndex.Translate(buildPc, liveByFid) : buildPc;
}
