using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>The dynamic-code patcher (extracted component) — see
    /// <see cref="DynamicCodePatcher"/>. Lazy so construction needs no
    /// ctor edit; the engine forwards the surface below.</summary>
    private DynamicCodePatcher? _chainPatcher;
    private DynamicCodePatcher ChainPatcher => _chainPatcher ??= new DynamicCodePatcher(this);

    internal DynChainTable? GetChainTable(Activation engine) => ChainPatcher.GetChainTable(engine);
    internal DynChainTable GetOrCreateChainTable(Activation engine) => ChainPatcher.GetOrCreateChainTable(engine);
    internal void RegisterLiveEngine(Activation engine) => ChainPatcher.RegisterLiveEngine(engine);
    internal List<Activation>? OtherLiveEnginesByTable(Activation except) => ChainPatcher.OtherLiveEnginesByTable(except);
    internal bool EngineOwnsHostBuffer(Activation engine) => ChainPatcher.EngineOwnsHostBuffer(engine);
    internal void SyncOrInvalidateAfterMutation(Activation engine, bool ownedHostBuffer)
        => ChainPatcher.SyncOrInvalidateAfterMutation(engine, ownedHostBuffer);
    internal void SyncPersistentFromEngine(Activation engine) => ChainPatcher.SyncPersistentFromEngine(engine);
    internal void ResyncOwnerAppendPosition(Activation engine) => ChainPatcher.ResyncOwnerAppendPosition(engine);
    internal void ChainCorruptionRecover(string site, Activation engine, int functorId, string detail)
        => ChainPatcher.ChainCorruptionRecover(site, engine, functorId, detail);
    internal bool IsExtensibleIndexedLayout(Activation engine, int functorId)
        => ChainPatcher.IsExtensibleIndexedLayout(engine, functorId);
    internal int FindFinalVarChainHead(Activation engine, int predAddr)
        => ChainPatcher.FindFinalVarChainHead(engine, predAddr);
    internal bool TryAppendToIndexedDynamic(Activation engine, int functorId, Clause clause)
        => ChainPatcher.TryAppendToIndexedDynamic(engine, functorId, clause);
    internal bool TryPrependToIndexedDynamic(Activation engine, int functorId, Clause clause)
        => ChainPatcher.TryPrependToIndexedDynamic(engine, functorId, clause);
    internal bool TryPatchDiedInAllIndexedChains(Activation engine, int functorId, int bodyAddr)
        => ChainPatcher.TryPatchDiedInAllIndexedChains(engine, functorId, bodyAddr);
    internal int FindBodyAddrForClauseIndex(Activation engine, int functorId, int clauseIndex)
        => ChainPatcher.FindBodyAddrForClauseIndex(engine, functorId, clauseIndex);
    internal void MirrorSwitchTableIntoDynamicLink(int mergedTableId, SwitchTable newTable)
        => ChainPatcher.MirrorSwitchTableIntoDynamicLink(mergedTableId, newTable);
    internal int? PeekDiedAddr(int functorId, int clauseIndex) => ChainPatcher.PeekDiedAddr(functorId, clauseIndex);
    internal int? PeekNextAddr(int functorId, int clauseIndex) => ChainPatcher.PeekNextAddr(functorId, clauseIndex);

    /// <summary>The chain table for the CURRENT persistent buffer (owned by
    /// the patcher component).</summary>
    internal DynChainTable DynChains => ChainPatcher.Chains;
    internal void ResetDynChains() => ChainPatcher.ResetChains();
    internal void AssociateEngineWithCurrentChains(Activation engine)
        => ChainPatcher.AssociateEngineWithCurrentChains(engine);

    internal static int TryReuseFreeChunk(
        List<(int Addr, int Length)> freeChunks, int needed)
        => DynamicCodePatcher.TryReuseFreeChunk(freeChunks, needed);
}
