namespace Shumway.Core;

/// <summary>The wasm tier's execution world: the modules of ONE engine, one
/// linear memory, one function table and one resume table, plus the
/// machinery to run CHAINS against a live activation. A module resolving a
/// marker reads the table; when the marker belongs to a sibling module it
/// tail-calls into it through the function table, so a chain crosses modules
/// without leaving wasm. What comes back to the host is the entry itself,
/// builtins, deopts, and markers no module covers.
///
/// <para>Markers and choice-point BPs encode (functor, ADDRESS) -- never
/// cursor ordinals, which are private to a module build. The registry
/// translates a pair to (module, cursor) through the same rows the modules
/// read; address 0 is the fresh-entry convention.</para>
///
/// <para>Implementations: the browser pins the engine arrays in place; the
/// desktop test world copies them into a private image around the chain.
/// Staging cost matters doubly in the browser, where all of this C# runs
/// Mono-interpreted.</para></summary>
public interface IWasmExecutionWorld
{
    /// <summary>Installs a freshly compiled module. Its members take over
    /// from whatever module covered them before (see
    /// <see cref="WasmModuleRegistry.Install"/>); <paramref
    /// name="callEdges"/> are the module's (caller, callee) pairs, and the
    /// returned list is the functors of OTHER modules the takeover pushed
    /// to bytecode -- the caller drops their delegates. Engine-thread only,
    /// never called mid-chain.</summary>
    System.Collections.Generic.IReadOnlyList<int> InstallGroup(byte[] module,
        System.Collections.Generic.IReadOnlyDictionary<int, int> entryCursorByFid,
        System.Collections.Generic.IReadOnlyDictionary<int, int> cursorByAddress,
        System.Collections.Generic.IReadOnlyDictionary<int, int> entryAddressByFid,
        int registerDemand,
        System.Collections.Generic.IEnumerable<(int Caller, int Callee)> callEdges);

    /// <summary>Drops the functors from the tier: their markers stop
    /// resolving anywhere and fall back to bytecode. Returns everything
    /// that left, the baked callers the registry drags along included.
    /// Boundary-tick only.</summary>
    System.Collections.Generic.IReadOnlyList<int> Evict(
        System.Collections.Generic.IEnumerable<int> functorIds);

    /// <summary>The id the next install will get. A module bakes its own id
    /// into every probe, so it has to be compiled against this value.</summary>
    int NextModuleId { get; }

    bool Contains(int functorId);

    /// <summary>Where a marker's (functor, address) runs; address 0 means
    /// the functor's fresh entry. False when no module covers it.</summary>
    bool TryResolve(int functorId, int address, out WasmTarget target);

    /// <summary>The functor's entry address (its linked base) -- the
    /// bytecode fallback target when an entry cannot run on the tier.</summary>
    int EntryAddressOf(int functorId);

    /// <summary>A CONSULT relinks the whole static program and moves every
    /// linked address; a module's baked addresses (deopt pcs, markers, BP
    /// encodings) then live in BUILD space, one generation behind. The
    /// bytecode itself does not change (it only moves), so a module stays
    /// valid — every place a build address crosses into the live code space
    /// goes through the translation below, and this hands the world the
    /// current (functor -> live address) map. Boundary-tick only, never
    /// mid-chain.</summary>
    void RefreshLiveAddresses(
        System.Collections.Generic.IReadOnlyDictionary<int, int> liveByFid);

    /// <summary>The functor's entry address in the LIVE code space — where
    /// the interpreter must run its bytecode now. Falls back to the build
    /// address for a world that never relinks (test harnesses).</summary>
    int LiveEntryAddressOf(int functorId);

    /// <summary>Translates an address the functor's module baked to the
    /// live code space: the pc moves by its member's own displacement.
    /// Identity for a world that never relinks.</summary>
    long TranslatePcToLive(int functorId, long buildPc);

    /// <summary>Opens a chain against the engine's live state: areas staged,
    /// mailbox filled. The caller must Dispose exactly once. Chains nest only
    /// through builtins (a findall sub-engine, a reentrant solve), and the
    /// builtin path re-syncs around the nested work, so each context is
    /// self-contained.</summary>
    IWasmChainContext BeginChain(Activation engine);
}

/// <summary>One open chain. The mailbox is authoritative between calls (the
/// module syncs its scalars into it on every return); the ENGINE object is
/// stale until <see cref="SyncEngine"/>. Exactly one of the two is current at
/// any moment, and Dispose only writes back when the mailbox side is.
///
/// <para>Every verdict names the module that produced it in
/// <see cref="WasmAbi.CurrentModuleId"/>: after in-wasm hops the chain has
/// no other way to know whose build space a pc is in.</para></summary>
public interface IWasmChainContext : System.IDisposable
{
    /// <summary>Runs a module at a cursor against the current mailbox/image.
    /// No per-call marshalling: the previous call's synced scalars ARE the
    /// entry state.</summary>
    WasmVerdict Call(WasmTarget target);

    /// <summary>Resolves a marker's (functor, address) through the rows as
    /// they are now -- an install during a nested builtin is visible, and
    /// the image was restaged after it.</summary>
    bool TryResolve(int functorId, int address, out WasmTarget target);

    /// <summary>Translates an address of the module that produced the LAST
    /// verdict (a deopt pc, a marker payload falling back to bytecode) to
    /// the live code space. Identity for a world that never relinks.</summary>
    long TranslatePcToLive(long buildPc);

    /// <summary>The functor owning a build address of the module that
    /// produced the last verdict: the functor a marker for that address is
    /// keyed under.</summary>
    int OwnerFunctorOf(long buildPc);

    long ReadSlot(int slot);

    /// <summary>One i64 of linear memory at an ABSOLUTE address, the way a
    /// module's i64.load would see it. For VERIFICATION: an image the host
    /// stages and only compiled code ever reads is otherwise unfalsifiable
    /// from managed tests, and a wrong offset would first show up as a wrong
    /// answer from generated code.</summary>
    long ReadWord(long address);

    /// <summary>Adopts the mailbox scalars into the engine (and, for a copy
    /// world, the areas). After this the ENGINE is authoritative: managed
    /// code may run builtins, grow arrays, bind. Dispose becomes a no-op
    /// until <see cref="RefreshFromEngine"/>.</summary>
    void SyncEngine();

    /// <summary>Re-stages the chain from the engine after managed code ran:
    /// arrays may have been replaced (growth), any scalar may have moved,
    /// modules may have been installed. The mailbox side is authoritative
    /// again.</summary>
    void RefreshFromEngine();
}
