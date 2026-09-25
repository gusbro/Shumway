using Shumway.Core;

namespace Shumway.Compiler.Wasm;

/// <summary>Gives every DIRECT builtin -- one a module can request through
/// <see cref="WasmVerdict.BuiltinRequest"/> -- a negative call marker in a
/// world's resume table, so a meta-call whose goal names a builtin requests
/// it from inside the module instead of stepping aside for the host to
/// dispatch. The $call helpers stay out: they need the interpreter's
/// dispatcher. Done once per world; a builtin registered later is published
/// the first time the host resolves a meta-call to it.</summary>
public static class WasmBuiltinMarkers
{
    public static void Publish(WasmResumeTable table)
    {
        foreach (int fid in Shumway.Builtins.BuiltinsRegistry.AllRegisteredFunctorIds())
        {
            if (!Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out int id)) continue;
            var entry = Shumway.Builtins.BuiltinsRegistry.GetById(id);
            if (entry.IsCall || entry.IsDollarCall) continue;
            table.PublishBuiltin(fid, id);
        }
    }
}
