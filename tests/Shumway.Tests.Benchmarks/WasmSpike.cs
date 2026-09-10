using System.Diagnostics;
using System.Runtime.InteropServices;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using WebAssembly;
using WebAssembly.Runtime;

/// <summary>The desktop half of the wasm Tier-1 spike's measurements
/// (docs/design/wasm-tier1-plan.md, phase 0).
///
/// <para>What this can and cannot say. It runs the hand-built counter module
/// through the emitter library's own engine, which compiles wasm to .NET IL
/// and JITs it — NOT a browser's wasm engine. So it measures the quality of
/// the CODE the backend would generate, against the same counter run by our
/// two tiers on the same machine in the same process. It does not measure
/// what the browser will do with that code, and it cannot: the Go criterion
/// is a browser number, and the boundary here is a JIT-compiled delegate call
/// rather than the `calli` into a wasm table that D1 rests on.</para>
///
/// <para>Read it as: is the code we would emit in the right league at all, and
/// how much does an entry cost when nothing else is in the way.</para></summary>
public static class WasmSpike
{
    private const int MailboxAt = 0;
    private const int RegistersAt = 256;
    private const int Rounds = 5;

    public abstract class Entry
    {
        public abstract int run(int mailbox, int cursor);
    }

    public static void Run(string[] args)
    {
        long iterations = ArgValue(args, "--n", 3_000_000);
        long entries = ArgValue(args, "--entries", 1_000_000);
        int rounds = (int)ArgValue(args, "--rounds", Rounds);

        Console.WriteLine("wasm Tier-1 spike — desktop measurements");
        Console.WriteLine($"  counter iterations: {iterations:N0}   boundary entries: {entries:N0}"
                          + $"   rounds: {rounds}");
        Console.WriteLine("  (the wasm runs through the emitter library's wasm-to-IL engine,");
        Console.WriteLine("   so this is about the generated code, not about a browser)");
        Console.WriteLine();

        // Each measurement warms its own subject and then times ROUNDS runs of
        // it, reporting the FASTEST: a wall clock on a shared machine only
        // ever adds, so the minimum is the sample least polluted by whatever
        // else the box was doing. The spread is printed beside it, because a
        // wide one says the number is worth less.
        var tier0 = Fastest(rounds, PrologRunner(iterations, threshold: 0));
        var tier1 = Fastest(rounds, PrologRunner(iterations, threshold: 1));
        var tier0i = Fastest(rounds, PrologRunner(iterations, threshold: 0, indexed: true));
        var tier1i = Fastest(rounds, PrologRunner(iterations, threshold: 1, indexed: true));
        var wasmLocal = Fastest(rounds, WasmRunner(iterations, cacheInLocal: true));
        var wasmMemory = Fastest(rounds, WasmRunner(iterations, cacheInLocal: false));
        var boundary = Fastest(rounds, BoundaryRunner(entries));

        Console.WriteLine($"  {"per counter iteration",-30}{"ns",9}{"spread",10}{"vs Tier-0",12}");
        Row("Tier-0 (bytecode)", tier0, tier0.Best);
        Row("Tier-1 (IL)", tier1, tier0.Best);
        Row("Tier-0, indexed counter", tier0i, tier0.Best);
        Row("Tier-1, indexed counter", tier1i, tier0.Best);
        Row("wasm, counter in memory", wasmMemory, tier0.Best);
        Row("wasm, counter in a local", wasmLocal, tier0.Best);
        Console.WriteLine();
        Console.WriteLine($"  boundary, one entry and out: {boundary.Best:F1} ns"
                          + $"  (spread {Spread(boundary)})");
        Console.WriteLine("    the plan's ceiling is 1000 ns, but for the browser's");
        Console.WriteLine("    calli into a wasm table; this is a JIT-compiled delegate.");
    }

    private static void Row(string name, Sample s, double baseline)
        => Console.WriteLine($"  {name,-30}{s.Best,9:F2}{Spread(s),10}{baseline / s.Best,12:F2}x");

    private static string Spread(Sample s)
        => s.Best <= 0 ? "-" : $"+{(s.Worst - s.Best) / s.Best * 100,3:F0}%";

    private readonly record struct Sample(double Best, double Worst);

    private static Sample Fastest(int rounds, Func<double> timed)
    {
        double best = double.MaxValue, worst = 0;
        for (int i = 0; i < rounds; i++)
        {
            double ns = timed();
            if (ns < best) best = ns;
            if (ns > worst) worst = ns;
        }
        return new Sample(best, worst);
    }

    // ---- the wasm side --------------------------------------------------

    private sealed class Instantiated : IDisposable
    {
        public readonly UnmanagedMemory Memory;
        public readonly Instance<Entry> Instance;

        public Instantiated(bool cacheInLocal = true)
        {
            Memory = new UnmanagedMemory(1, 1);
            using var stream = new MemoryStream(
                SpikeCounterModule.ToBytes(cacheInLocal: cacheInLocal));
            var creator = Module.ReadFromBinary(stream).Compile<Entry>();
            Instance = creator(new ImportDictionary
            {
                { WasmAbi.MemoryModule, WasmAbi.MemoryField, new MemoryImport(() => Memory) },
            });
            SetSlot(WasmAbi.RegistersBase, RegistersAt);
            SetSlot(WasmAbi.HeapWatermark, long.MaxValue);
        }

        public void SetSlot(int slot, long value)
            => Marshal.WriteInt64(Memory.Start, MailboxAt + slot * WasmAbi.SlotSize, value);

        public void SetX0(Cell c) => Marshal.WriteInt64(Memory.Start, RegistersAt, c.Data);
        public Cell GetX0() => new(Marshal.ReadInt64(Memory.Start, RegistersAt));

        public void Dispose() { Instance.Dispose(); Memory.Dispose(); }
    }

    /// <summary>Cost of crossing in and out with no work to do: X0 is already
    /// zero, so the loop is not entered. The module is instantiated and warmed
    /// once, outside the timing.</summary>
    private static Func<double> BoundaryRunner(long entries)
    {
        var w = new Instantiated();
        var run = w.Instance.Exports;
        w.SetX0(Cell.Int(0));
        for (int i = 0; i < 100_000; i++) run.run(MailboxAt, 0);

        return () =>
        {
            var sw = Stopwatch.StartNew();
            for (long i = 0; i < entries; i++) run.run(MailboxAt, 0);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1e6 / entries;
        };
    }

    /// <summary>Cost per round of the counter loop, entered once.</summary>
    private static Func<double> WasmRunner(long iterations, bool cacheInLocal)
    {
        var w = new Instantiated(cacheInLocal);
        var run = w.Instance.Exports;
        for (int i = 0; i < 3; i++)
        {
            w.SetX0(Cell.Int(200_000));
            run.run(MailboxAt, 0);
        }

        return () =>
        {
            w.SetX0(Cell.Int(iterations));
            var sw = Stopwatch.StartNew();
            int verdict = run.run(MailboxAt, 0);
            sw.Stop();
            if (verdict != (int)WasmVerdict.Success || w.GetX0().AsInt != 0)
                throw new InvalidOperationException(
                    $"the counter did not finish: verdict {verdict}, X0 {w.GetX0().AsInt}");
            return sw.Elapsed.TotalMilliseconds * 1e6 / iterations;
        };
    }

    // ---- our own two tiers ----------------------------------------------

    /// <summary>The plan's program, as the plan writes it. The head of the
    /// first clause is a variable, so indexing cannot rule the second one out
    /// and the engine carries a choice point per round -- work the wasm module
    /// does not do, which is why the deterministic form below is measured
    /// beside it.</summary>
    private const string CounterProgram = """
        loop(N) :- N > 0, N1 is N - 1, loop(N1).
        loop(0).
        """;

    /// <summary>The same counter written so that first-argument indexing
    /// decides it: nothing to backtrack into, which is the shape a compiled
    /// loop is a fair comparison for.</summary>
    private const string IndexedCounterProgram = """
        loopi(0).
        loopi(N) :- N > 0, N1 is N - 1, loopi(N1).
        """;

    /// <summary>One engine, consulted and warmed until the tier under test is
    /// the one running: at threshold 1 the first call promotes, so the compile
    /// happens in the warm-up rather than inside a measurement.</summary>
    private static Func<double> PrologRunner(long iterations, int threshold, bool indexed = false)
    {
        string name = indexed ? "loopi" : "loop";
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = threshold;
        engine.ConsultString(indexed ? IndexedCounterProgram : CounterProgram);
        for (int i = 0; i < 3; i++)
            if (!engine.Query($"{name}(200000).").Success)
                throw new InvalidOperationException("the warm-up counter failed.");

        return () =>
        {
            var sw = Stopwatch.StartNew();
            bool ok = engine.Query($"{name}({iterations}).").Success;
            sw.Stop();
            if (!ok) throw new InvalidOperationException("the counter failed.");
            return sw.Elapsed.TotalMilliseconds * 1e6 / iterations;
        };
    }

    // ---- plumbing --------------------------------------------------------

    private static double Median(int rounds, Func<double> measure)
    {
        var samples = new List<double>(rounds);
        for (int i = 0; i < rounds; i++) samples.Add(measure());
        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static long ArgValue(string[] args, string name, long fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && long.TryParse(args[i + 1], out long v)
            ? v : fallback;
    }
}
