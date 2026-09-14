using System.Collections.Generic;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Wasm;

public static class WasmGroupInstall
{
    /// <summary>Installs a compiled group into a world, refusing a module
    /// compiled against another id: its probes would take the rows of a
    /// sibling for their own and dispatch foreign cursors locally. Returns
    /// the functors of other modules the install displaced (see
    /// <see cref="IWasmExecutionWorld.InstallGroup"/>).</summary>
    public static IReadOnlyList<int> InstallInto(this WasmGroupEntry entry,
        IWasmExecutionWorld world, IReadOnlyDictionary<int, int> entryAddressByFid)
    {
        if (entry.ModuleId != world.NextModuleId)
            throw new WasmCompileException(
                $"module compiled as id {entry.ModuleId}, the world would install it as"
                + $" {world.NextModuleId}");
        return world.InstallGroup(entry.Module, entry.EntryCursorByFid, entry.CursorByAddress,
                                  entryAddressByFid, entry.RegisterDemand, entry.CallSites.Keys);
    }

    /// <summary>The (caller, callee) pairs of a set of predicates, as a
    /// module compiled from them would bake: for installing a module whose
    /// group entry is not at hand (the baked prelude).</summary>
    public static IEnumerable<(int Caller, int Callee)> EdgesOf(
        IEnumerable<CompiledPredicate> predicates)
    {
        var seen = new HashSet<(int, int)>();
        foreach (var pred in predicates)
            foreach (var site in pred.CallSites)
                if (seen.Add((pred.FunctorId, site.CalleeFunctorId)))
                    yield return (pred.FunctorId, site.CalleeFunctorId);
    }
}
