using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.Web;

/// <summary>The browser half of the wasm Tier-1 spike
/// (docs/design/wasm-tier1-plan.md, phase 0).
///
/// <para>Everything the arc rests on is decided here, and nowhere else can
/// decide it. The module imports the RUNTIME's memory, so the engine's arrays
/// are addressable without copying (D2). Its export goes into the runtime's
/// function table, and a wasm function pointer IS a table index, so C# calls
/// it through a plain function pointer with no JavaScript on the path (D1).
/// And the counter is timed against the same counter run by Tier-0, which in
/// the browser is an interpreter running on an interpreter -- the gap the arc
/// is about.</para>
///
/// <para>The page instantiates and passes the index in: interop is affine to
/// the runtime thread, so a design where C# had to reach JavaScript on every
/// call would have no product in it.</para></summary>
internal static partial class WebShumwayApp
{
    /// <summary>The module the page is to instantiate, base64.</summary>
    [JSExport]
    internal static Task<string> WasmSpikeModule(bool shared)
        => Task.FromResult(Convert.ToBase64String(
               SpikeCounterModule.ToBytes(shared: shared, cacheInLocal: false)));

    [JSImport("wasm.callHandle", "main.js")]
    internal static partial int WasmCallHandle(int handle, int a, int b);

    /// <summary>What the rejected path costs: C# to JavaScript to wasm, per
    /// call, on the runtime thread (the only thread that may take it).
    /// </summary>
    [JSExport]
    internal static Task<string> WasmThunkCost(int handle, int calls)
    {
        var report = new StringBuilder();
        try
        {
            for (int i = 0; i < 1000; i++) WasmCallHandle(handle, 0, 1);
            double best = double.MaxValue;
            for (int r = 0; r < 3; r++)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < calls; i++) WasmCallHandle(handle, 0, 1);
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / calls);
            }
            report.Append("thunk (C# to JS to wasm): ").Append(best.ToString("F1"))
                  .Append(" ns per call, on the runtime thread").Append("\n");
        }
        catch (Exception ex)
        {
            report.Append("thunk STOPPED: ").Append(ex.GetType().Name)
                  .Append(": ").Append(ex.Message).Append("\n");
        }
        return Task.FromResult(report.ToString());
    }

    /// <summary>The module that cannot loop, for telling a call that does not
    /// work from a callee that never finishes.</summary>
    [JSExport]
    internal static Task<string> WasmEchoModule(bool shared)
        => Task.FromResult(Convert.ToBase64String(EchoModule.ToBytes(shared)));

    /// <summary>Calls the echo module and reports what came back. Anything at
    /// all coming back means the call path works.</summary>
    [JSExport]
    internal static async Task<string> WasmEchoCall(int tableIndex, bool onPool)
    {
        if (!onPool) return Echo(tableIndex);
        return await Task.Run(() => Echo(tableIndex)).ConfigureAwait(false);
    }

    private static unsafe string Echo(int tableIndex)
    {
        try
        {
            var fn = (delegate* unmanaged<int, int, int>)(void*)(nint)tableIndex;
            int back = fn(0, 4242);
            return $"echo on thread {Environment.CurrentManagedThreadId}: {back}" + "\n";
        }
        catch (Exception ex)
        {
            return $"echo STOPPED: {ex.GetType().Name}: {ex.Message}" + "\n";
        }
    }

    /// <summary>Runs the spike against a module the page already put in the
    /// runtime's table.
    ///
    /// <para><paramref name="iterations"/> also says how far to go, because a
    /// hang is not an exception and the only way to find where one is is to be
    /// able to stop before each step: -1 calls once on the RUNTIME thread,
    /// where the index was made; 0 pins the arrays on a pool thread and calls
    /// nothing; 1 calls once from there; anything more is the whole
    /// measurement.</para></summary>
    [JSExport]
    internal static async Task<string> WasmSpike(int tableIndex, int iterations, int rounds)
    {
        if (iterations == -1)
        {
            var here = new StringBuilder();
            here.Append("on the runtime thread (id ")
                .Append(Environment.CurrentManagedThreadId).Append(")\n");
            try { OneCall(here, tableIndex); }
            catch (Exception ex)
            {
                here.Append("STOPPED: ").Append(ex.GetType().Name)
                    .Append(": ").Append(ex.Message).Append('\n');
            }
            return here.ToString();
        }

        // Awaited, not handed over: the app's own exports do it this way, and
        // a Task that completes on a pool thread without the runtime thread
        // resuming it never reaches JavaScript at all.
        return await Task.Run(() =>
        {
            var report = new StringBuilder();
            report.Append("on a pool thread (id ")
                  .Append(Environment.CurrentManagedThreadId).Append(")\n");
            try
            {
                if (iterations <= 1) OneCall(report, tableIndex, call: iterations == 1);
                else Measure(report, tableIndex, iterations, Math.Max(1, rounds));
            }
            catch (Exception ex)
            {
                report.Append("STOPPED: ").Append(ex.GetType().Name)
                      .Append(": ").Append(ex.Message).Append('\n');
            }
            return report.ToString();
        }).ConfigureAwait(false);
    }

    /// <summary>Pins a mailbox and a register file, and optionally makes the
    /// one call the whole arc depends on.</summary>
    private static unsafe void OneCall(StringBuilder report, int index, bool call = true)
    {
        long[] mailbox = GC.AllocateArray<long>(WasmAbi.SlotCount, pinned: true);
        long[] registers = GC.AllocateArray<long>(8, pinned: true);
        int mailboxAt = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(mailbox, 0);
        int registersAt = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(registers, 0);
        mailbox[WasmAbi.RegistersBase] = registersAt;
        mailbox[WasmAbi.HeapWatermark] = long.MaxValue;
        registers[0] = Cell.Int(3).Data;
        report.Append("pinned: mailbox at ").Append(mailboxAt)
              .Append(", registers at ").Append(registersAt).Append('\n');
        if (!call) { report.Append("not calling\n"); return; }

        var fn = (delegate* unmanaged<int, int, int>)(void*)(nint)index;
        report.Append("about to call through the table index\n");
        int verdict = fn(mailboxAt, 0);
        Cell answer = new(registers[0]);
        report.Append("verdict ").Append((WasmVerdict)verdict)
              .Append(", X0 ").Append(answer.Tag).Append(' ')
              .Append(answer.AsInt).Append('\n');
    }

    /// <summary>The measuring, on a pool thread: the index came from the
    /// runtime thread, and whether it is callable from here is half of what
    /// the spike asks (the engine runs on pool threads).</summary>
    private static unsafe void Measure(
        StringBuilder report, int index, int iterations, int rounds)
    {
        long[] mailbox = GC.AllocateArray<long>(WasmAbi.SlotCount, pinned: true);
        long[] registers = GC.AllocateArray<long>(8, pinned: true);
        int mailboxAt = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(mailbox, 0);
        int registersAt = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(registers, 0);
        mailbox[WasmAbi.RegistersBase] = registersAt;
        mailbox[WasmAbi.HeapWatermark] = long.MaxValue;

        var call = (delegate* unmanaged<int, int, int>)(void*)(nint)index;

        registers[0] = Cell.Int(3).Data;
        int verdict = call(mailboxAt, 0);
        if (verdict != (int)WasmVerdict.Success || new Cell(registers[0]).AsInt != 0)
            throw new InvalidOperationException(
                $"the counter did not answer correctly: verdict {verdict}");

        double boundary = double.MaxValue;
        for (int r = 0; r < rounds; r++)
        {
            registers[0] = Cell.Int(0).Data;
            for (int i = 0; i < 10_000; i++) call(mailboxAt, 0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100_000; i++) call(mailboxAt, 0);
            sw.Stop();
            boundary = Math.Min(boundary, sw.Elapsed.TotalMilliseconds * 1e6 / 100_000);
        }
        report.Append("boundary: ").Append(boundary.ToString("F1"))
              .Append(" ns per entry\n");

        double wasmNs = double.MaxValue;
        for (int r = 0; r < rounds; r++)
        {
            registers[0] = Cell.Int(iterations).Data;
            var sw = Stopwatch.StartNew();
            int v = call(mailboxAt, 0);
            sw.Stop();
            if (v != (int)WasmVerdict.Success)
                throw new InvalidOperationException($"the counter bailed: {(WasmVerdict)v}");
            wasmNs = Math.Min(wasmNs, sw.Elapsed.TotalMilliseconds * 1e6 / iterations);
        }

        var engine = new PrologEngine();
        engine.ConsultString("loop(N) :- N > 0, N1 is N - 1, loop(N1).\nloop(0).\n");
        engine.Query("loop(2000).");
        double tier0Ns = double.MaxValue;
        for (int r = 0; r < rounds; r++)
        {
            var sw = Stopwatch.StartNew();
            bool ok = engine.Query($"loop({iterations}).").Success;
            sw.Stop();
            if (!ok) throw new InvalidOperationException("the engine's counter failed.");
            tier0Ns = Math.Min(tier0Ns, sw.Elapsed.TotalMilliseconds * 1e6 / iterations);
        }

        report.Append("\n  per counter iteration, ").Append(iterations)
              .Append(" x").Append(rounds).Append(" rounds\n");
        report.Append("  Tier-0 (interpreted here): ")
              .Append(tier0Ns.ToString("F2")).Append(" ns\n");
        report.Append("  wasm (native here):        ")
              .Append(wasmNs.ToString("F2")).Append(" ns\n");
        report.Append("  ratio: ").Append((tier0Ns / wasmNs).ToString("F1"))
              .Append("x   (the plan asks for 2.0x, and a boundary under 1000 ns)\n");
    }
}
