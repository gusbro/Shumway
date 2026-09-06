namespace Shumway.Core;

/// <summary>Executes one compiled wasm predicate module against a live
/// activation: fill the <see cref="WasmAbi"/> mailbox, enter the module at a
/// cursor, sync the scalars back. Implementations differ by world -- the
/// browser pins the engine arrays inside the runtime's linear memory and
/// calls through a per-thread function-table index; the desktop test runner
/// copies the areas into an image around each entry -- and the verdict loop
/// on top is the same for both.</summary>
public interface IWasmActivationRunner : System.IDisposable
{
    /// <summary>Runs the module at <paramref name="cursor"/>. On return the
    /// engine has adopted the synced scalars
    /// (<see cref="Activation.SyncFromWasmMailbox"/>). The caller has already
    /// checked <see cref="Activation.WasmModeCompatible"/> -- an activation in
    /// a mode the compiled code does not honour never enters here.</summary>
    WasmVerdict Run(Activation engine, int cursor);

    /// <summary>A mailbox slot as of the last <see cref="Run"/> (the builtin
    /// request words, the deopt / tail-call Pc).</summary>
    long ReadSlot(int slot);
}
