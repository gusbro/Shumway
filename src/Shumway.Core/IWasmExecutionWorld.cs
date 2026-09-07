namespace Shumway.Core;

/// <summary>The wasm tier's execution world: the registered modules of one
/// promotion store plus the machinery to run CHAINS against a live
/// activation. A chain is the unit that killed the old per-entry model: the
/// per-entry marshalling (pin the arrays, fill the mailbox, sync back) runs
/// as INTERPRETED C# in the browser and costs ~150 us -- 70x the wasm
/// execution it wraps -- so the world pays it once per chain and the verdict
/// loop hops module-to-module on the mailbox the wasm itself keeps synced.
/// Implementations: the browser pins the engine arrays in place; the desktop
/// test world copies them into a private image around the chain.</summary>
public interface IWasmExecutionWorld
{
    /// <summary>Registers a compiled module for a functor; returns its
    /// handle. Engine-thread only, never called mid-chain.</summary>
    int RegisterModule(int functorId, byte[] module, int registerDemand);

    bool TryGetHandle(int functorId, out int handle);

    /// <summary>The functor a handle runs -- the verdict loop needs it for
    /// <see cref="Activation.BuiltinReturnPc"/> when a builtin fires from a
    /// module it chained into.</summary>
    int FunctorOfHandle(int handle);

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
/// any moment, and Dispose only writes back when the mailbox side is.</summary>
public interface IWasmChainContext : System.IDisposable
{
    /// <summary>Runs a module at a cursor against the current mailbox/image.
    /// No per-call marshalling: the previous call's synced scalars ARE the
    /// entry state.</summary>
    WasmVerdict Call(int handle, int cursor);

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
