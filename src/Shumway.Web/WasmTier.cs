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
/// <para>With threads on, every worker has its own function table -- only the
/// memory is shared -- so an index registered on one thread does not exist on
/// another, and calling through it traps the worker silently. The design that
/// follows from that: a thread REGISTERS the module bytes itself, through the
/// C shim's EM_JS (JavaScript in the calling thread's own realm), and gets an
/// index valid exactly where it will be used. No JavaScript instantiates
/// anything from the page; C# holds the module bytes and the whole path is
/// DllImport into spike.c.</para></summary>
internal static partial class WebShumwayApp
{
    /// <summary>This thread's table size (spike.c, EM_JS).</summary>
    [DllImport("spike")]
    private static extern int shumway_wasm_table_length();

    /// <summary>Instantiates the module bytes against this thread's realm and
    /// registers its entry in THIS thread's table. Returns the index, or -1.
    /// </summary>
    [DllImport("spike")]
    private static extern int shumway_wasm_register(int bytesPtr, int len);

    /// <summary>The crossing: one call_indirect through this thread's table.
    /// </summary>
    [DllImport("spike")]
    private static extern int shumway_wasm_call(int index, int mailbox, int cursor);

    private static int Register(StringBuilder report, string name, byte[] module)
    {
        byte[] pinned = GC.AllocateArray<byte>(module.Length, pinned: true);
        module.CopyTo(pinned, 0);
        int at = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0);
        int index = shumway_wasm_register(at, pinned.Length);
        report.Append(name).Append(": ").Append(module.Length)
              .Append(" bytes, registered at index ").Append(index)
              .Append(" on thread ").Append(Environment.CurrentManagedThreadId)
              .Append('\n');
        if (index < 0)
            throw new InvalidOperationException($"{name} did not register.");
        return index;
    }

    /// <summary>The whole spike: registration, the echo proof, the boundary
    /// and the counter, all on a pool thread -- where the engine runs.</summary>
    [JSExport]
    internal static async Task<string> WasmProbe(int iterations, int rounds)
    {
        var report = new StringBuilder();
        report.Append("table length on the runtime thread: ")
              .Append(shumway_wasm_table_length()).Append('\n');

        return await Task.Run(() =>
        {
            try
            {
                report.Append("table length on this pool thread: ")
                      .Append(shumway_wasm_table_length()).Append('\n');

                // The echo cannot loop, so an answer proves the whole path:
                // register in this thread's table, call through the shim.
                int echo = Register(report, "echo", EchoModule.ToBytes(shared: true));
                report.Append("echo answers: ")
                      .Append(shumway_wasm_call(echo, 0, 4242)).Append('\n');

                int counter = Register(report, "counter",
                    SpikeCounterModule.ToBytes(shared: true, cacheInLocal: false));
                Measure(report, counter, iterations, Math.Max(1, rounds));
            }
            catch (Exception ex)
            {
                report.Append("STOPPED: ").Append(ex.GetType().Name)
                      .Append(": ").Append(ex.Message).Append('\n');
            }
            return report.ToString();
        }).ConfigureAwait(false);
    }

    /// <summary>Whether the raw managed calli works once the index is valid
    /// for the calling thread -- kept apart because a hang here must not cost
    /// the measurements. If it does work, the first attempt's hang was never
    /// about calli at all: it was the cross-thread index.</summary>
    [JSExport]
    internal static async Task<string> WasmCalliCheck()
        => await Task.Run(() =>
        {
            var report = new StringBuilder();
            try
            {
                int echo = Register(report, "echo", EchoModule.ToBytes(shared: true));
                unsafe
                {
                    var fn = (delegate* unmanaged<int, int, int>)(void*)(nint)echo;
                    report.Append("calli answers: ").Append(fn(0, 4242)).Append('\n');
                }
            }
            catch (Exception ex)
            {
                report.Append("calli STOPPED: ").Append(ex.GetType().Name)
                      .Append(": ").Append(ex.Message).Append('\n');
            }
            return report.ToString();
        }).ConfigureAwait(false);

    /// <summary>This thread's view of an index (spike.c): -2 when the slot
    /// does not exist here.</summary>
    [DllImport("spike")]
    private static extern int shumway_wasm_probe_index(int index);

    /// <summary>Registers on one pool thread and asks OTHER pool threads what
    /// they see at that index -- without calling it, since calling through a
    /// foreign index traps the worker with nothing to catch. The engine is
    /// thread-agile, so the answer decides the product's shape: an index that
    /// crosses threads needs one registration per module, one that does not
    /// needs a per-thread cache.</summary>
    [JSExport]
    internal static async Task<string> WasmCrossThreadCheck()
        => await Task.Run(async () =>
        {
            var report = new StringBuilder();
            int index = Register(report, "echo", EchoModule.ToBytes(shared: true));
            int regThread = Environment.CurrentManagedThreadId;

            // Several probes held at a gate so they land on distinct threads;
            // a pool that serializes them anyway reports itself inconclusive.
            var gate = new bool[1];
            var probes = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                while (!Volatile.Read(ref gate[0]) && sw.ElapsedMilliseconds < 2000) { }
                return (Thread: Environment.CurrentManagedThreadId,
                        Sees: shumway_wasm_probe_index(index),
                        Length: shumway_wasm_table_length());
            })).ToArray();
            await Task.Delay(300).ConfigureAwait(false);
            Volatile.Write(ref gate[0], true);
            var results = await Task.WhenAll(probes).ConfigureAwait(false);

            bool foreignSeen = false;
            foreach (var r in results)
            {
                if (r.Thread == regThread) continue;
                foreignSeen = true;
                report.Append("thread ").Append(r.Thread)
                      .Append(" (table length ").Append(r.Length).Append("): ")
                      .Append(r.Sees switch
                      {
                          -2 => "the slot does not exist there",
                          0 => "the slot exists there but is empty",
                          1 => "the slot exists there and is occupied",
                          _ => "the probe itself failed",
                      }).Append('\n');
            }
            if (!foreignSeen)
                report.Append("every probe landed on the registering thread; inconclusive\n");
            return report.ToString();
        }).ConfigureAwait(false);

    private static void Measure(StringBuilder report, int index, int iterations, int rounds)
    {
        // The mailbox and the register file, pinned: their addresses go into
        // the mailbox, and they must not move under the wasm.
        long[] mailbox = GC.AllocateArray<long>(WasmAbi.SlotCount, pinned: true);
        long[] registers = GC.AllocateArray<long>(8, pinned: true);
        int mailboxAt = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(mailbox, 0);
        int registersAt = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(registers, 0);
        mailbox[WasmAbi.RegistersBase] = registersAt;
        mailbox[WasmAbi.HeapWatermark] = long.MaxValue;

        registers[0] = Cell.Int(3).Data;
        int verdict = shumway_wasm_call(index, mailboxAt, 0);
        if (verdict != (int)WasmVerdict.Success || new Cell(registers[0]).AsInt != 0)
            throw new InvalidOperationException(
                $"the counter did not answer correctly: verdict {verdict}, "
                + $"X0 {new Cell(registers[0]).AsInt}");
        report.Append("counter answers through the shim\n");

        // The boundary: in and out with the counter already at zero. This is
        // what the plan's 1000 ns ceiling is about -- a DllImport into the
        // shim plus one call_indirect, per entry.
        double boundary = double.MaxValue;
        for (int r = 0; r < rounds; r++)
        {
            registers[0] = Cell.Int(0).Data;
            for (int i = 0; i < 10_000; i++) shumway_wasm_call(index, mailboxAt, 0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100_000; i++) shumway_wasm_call(index, mailboxAt, 0);
            sw.Stop();
            boundary = Math.Min(boundary, sw.Elapsed.TotalMilliseconds * 1e6 / 100_000);
        }
        report.Append("boundary via the shim:  ").Append(boundary.ToString("F1"))
              .Append(" ns per entry\n");

        unsafe
        {
            var fn = (delegate* unmanaged<int, int, int>)(void*)(nint)index;
            double calliNs = double.MaxValue;
            for (int r = 0; r < rounds; r++)
            {
                registers[0] = Cell.Int(0).Data;
                for (int i = 0; i < 10_000; i++) fn(mailboxAt, 0);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 100_000; i++) fn(mailboxAt, 0);
                sw.Stop();
                calliNs = Math.Min(calliNs, sw.Elapsed.TotalMilliseconds * 1e6 / 100_000);
            }
            report.Append("boundary via calli:    ").Append(calliNs.ToString("F1"))
                  .Append(" ns per entry\n");
        }

        // The counter, in wasm and in the engine, same shape and same count.
        double wasmNs = double.MaxValue;
        for (int r = 0; r < rounds; r++)
        {
            registers[0] = Cell.Int(iterations).Data;
            var sw = Stopwatch.StartNew();
            int v = shumway_wasm_call(index, mailboxAt, 0);
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
