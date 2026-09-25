using WebAssembly;
using WebAssembly.Runtime;

namespace Shumway.Compiler.Wasm;

/// <summary>What the modules of ONE engine share on the desktop: the linear
/// memory they all run against, the function table they reach each other
/// through, and the resume table that says which of them owns a marker.
/// The browser gets all three from emscripten, per thread; here they are made
/// explicitly so the same emitted code works in both.
///
/// <para>The memory has to be shared, not just the tables: a hop tail-calls
/// another module's <c>run</c> with a mailbox ADDRESS, and an address only
/// means something in the memory it was written to. A callee with a memory of
/// its own reads stale scalars and deopts on entry.</para></summary>
public sealed class DesktopWasmSpace : IDisposable
{
    internal const int MailboxAt = 1024;
    internal const int RegistersAt = 2048;
    internal const int Pages = 512;              // 32 MB: generous for tests

    public UnmanagedMemory Memory { get; } = new(Pages, Pages);
    public FunctionTable Functions { get; } = new(0, null);
    public Shumway.Core.WasmResumeTable ResumeTable { get; } = new();
    public Shumway.Core.WasmModuleRegistry Modules { get; }
    /// <summary>Indexed by module id. Never removed: a chain may be inside.</summary>
    public List<Instance<WasmRunExports>> Instances { get; } = new();

    // The functor mirror lives in the memory, so its sync state does too:
    // where it sits, and how many ids the image already holds.
    internal int FunctorAt = -1;
    internal int FunctorSynced;
    // Same idea for the call-marker table: where it sits, and the version of
    // the table the image already holds. It changes only on install/evict,
    // so most stagings copy nothing.
    internal int CallMarkerAt = -1;
    internal int CallMarkerCopied = -1;
    internal int MetaCacheAt = -1;
    internal int MetaCacheCopied = -1;

    public DesktopWasmSpace()
    {
        Modules = new(ResumeTable);
        WasmBuiltinMarkers.Publish(ResumeTable);
    }

    public void Dispose() => Memory.Dispose();
}
