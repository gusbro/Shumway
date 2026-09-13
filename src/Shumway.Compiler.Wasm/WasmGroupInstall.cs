using System.Collections.Generic;
using Shumway.Core;

namespace Shumway.Compiler.Wasm;

public static class WasmGroupInstall
{
    /// <summary>Installs a compiled group into a world, refusing a module
    /// compiled against another id: its probes would take the rows of a
    /// sibling for their own and dispatch foreign cursors locally.</summary>
    public static void InstallInto(this WasmGroupEntry entry, IWasmExecutionWorld world,
        IReadOnlyDictionary<int, int> entryAddressByFid)
    {
        if (entry.ModuleId != world.NextModuleId)
            throw new WasmCompileException(
                $"module compiled as id {entry.ModuleId}, the world would install it as"
                + $" {world.NextModuleId}");
        world.InstallGroup(entry.Module, entry.EntryCursorByFid, entry.CursorByAddress,
                           entryAddressByFid, entry.RegisterDemand);
    }
}
