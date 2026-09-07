namespace Shumway.Core;

/// <summary>The wasm tier's execution world: ONE group module covering every
/// promoted predicate of a store, plus the machinery to run CHAINS against a
/// live activation. Group compilation makes a cross-member call an internal
/// dispatch jump, so the per-hop interpreted C# the chain removed at module
/// boundaries disappears for in-group calls entirely.
///
/// <para>Markers and choice-point BPs encode (functor, ADDRESS) -- never
/// cursor ordinals, which renumber when a promotion rebuilds the group. The
/// world translates an address to the CURRENT build's cursor at entry;
/// address 0 is the fresh-entry convention.</para>
///
/// <para>Implementations: the browser pins the engine arrays in place; the
/// desktop test world copies them into a private image around the chain.
/// Staging cost matters doubly in the browser, where all of this C# runs
/// Mono-interpreted.</para></summary>
public interface IWasmExecutionWorld
{
    /// <summary>Installs a freshly compiled group build, replacing the
    /// current one. Chains already open keep the build they captured (an
    /// older build stays valid for its own members). Engine-thread only,
    /// never called mid-chain.</summary>
    void InstallGroup(byte[] module,
        System.Collections.Generic.IReadOnlyDictionary<int, int> entryCursorByFid,
        System.Collections.Generic.IReadOnlyDictionary<int, int> cursorByAddress,
        System.Collections.Generic.IReadOnlyDictionary<int, int> entryAddressByFid,
        int registerDemand);

    bool Contains(int functorId);

    /// <summary>The current build's cursor for a marker's (functor, address)
    /// pair; address 0 means the functor's fresh entry. False when the
    /// functor is not in the current group.</summary>
    bool TryResolve(int functorId, int address, out int cursor);

    /// <summary>The functor's entry address (its linked base) -- the
    /// bytecode fallback target when an entry cannot run on the tier.</summary>
    int EntryAddressOf(int functorId);

    /// <summary>Opens a chain against the engine's live state: areas staged,
    /// mailbox filled, the current build captured. The caller must Dispose
    /// exactly once. Chains nest only through builtins (a findall
    /// sub-engine, a reentrant solve), and the builtin path re-syncs around
    /// the nested work, so each context is self-contained.</summary>
    IWasmChainContext BeginChain(Activation engine);
}

/// <summary>One open chain over the build captured at open time. The mailbox
/// is authoritative between calls (the module syncs its scalars into it on
/// every return); the ENGINE object is stale until <see cref="SyncEngine"/>.
/// Exactly one of the two is current at any moment, and Dispose only writes
/// back when the mailbox side is.</summary>
public interface IWasmChainContext : System.IDisposable
{
    /// <summary>Runs the captured build at a cursor against the current
    /// mailbox/image. No per-call marshalling: the previous call's synced
    /// scalars ARE the entry state.</summary>
    WasmVerdict Call(int cursor);

    /// <summary>Resolves a marker's (functor, address) against the build
    /// THIS chain captured -- not the world's latest, which a nested
    /// promotion may have replaced.</summary>
    bool TryResolve(int functorId, int address, out int cursor);

    long ReadSlot(int slot);

    /// <summary>Adopts the mailbox scalars into the engine (and, for a copy
    /// world, the areas). After this the ENGINE is authoritative: managed
    /// code may run builtins, grow arrays, bind. Dispose becomes a no-op
    /// until <see cref="RefreshFromEngine"/>.</summary>
    void SyncEngine();

    /// <summary>Re-stages the chain from the engine after managed code ran:
    /// arrays may have been replaced (growth), any scalar may have moved.
    /// The mailbox side is authoritative again.</summary>
    void RefreshFromEngine();
}
