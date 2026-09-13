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
/// one, and a module every functor has left simply owns no rows.</para></summary>
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

        internal Module(int id,
            IReadOnlyDictionary<int, int> entryCursorByFid,
            IReadOnlyDictionary<int, int> cursorByAddress,
            IReadOnlyDictionary<int, int> entryAddressByFid,
            int registerDemand)
        {
            Id = id;
            EntryCursorByFid = entryCursorByFid;
            CursorByAddress = cursorByAddress;
            EntryAddressByFid = entryAddressByFid;
            RegisterDemand = registerDemand;
            AddrIndex = new WasmBuildAddressIndex(entryAddressByFid);
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
    /// the caller registers its runtime handle under it.</summary>
    public Module Install(
        IReadOnlyDictionary<int, int> entryCursorByFid,
        IReadOnlyDictionary<int, int> cursorByAddress,
        IReadOnlyDictionary<int, int> entryAddressByFid,
        int registerDemand)
    {
        int id = Table.NextModuleId();
        if (id != _byId.Count)
            throw new System.InvalidOperationException(
                "the resume table minted an id this registry did not see");
        var m = new Module(id, entryCursorByFid, cursorByAddress, entryAddressByFid,
                           registerDemand);
        _byId.Add(m);
        foreach (int fid in entryCursorByFid.Keys)
        {
            if (_byFid.ContainsKey(fid)) Table.ClearFunctor(fid);
            _byFid[fid] = m;
            _entryAddressByFid[fid] = entryAddressByFid[fid];
        }
        // A fresh entry is address 0 under the member's own functor; every
        // other re-entry point belongs to whichever member's range it falls
        // in. Rows are keyed by the OWNER's functor -- the host must decode a
        // build address to that same functor before it can look one up.
        foreach (var (fid, cursor) in entryCursorByFid)
            Table.Set(Activation.EncodeResumeMarker(fid, 0), id, cursor);
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
    /// back to bytecode. The modules stay.</summary>
    public void Evict(IEnumerable<int> functorIds)
    {
        foreach (int fid in functorIds)
            if (_byFid.Remove(fid)) Table.ClearFunctor(fid);
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
