// Phase D0 spike debuggee (ADR-035): simulates the shape of Shumway's Tier-0
// engine so the Concord spike legs can run without touching the real engine.
//  - BytecodeInterpreter.Dispatch: the persistent, recognizable interpreter frame
//    (the stack filter replaces frames from THIS module).
//  - SpikeDebugHelper: the debug-cooperation surface — pinned channel buffer
//    (leg 1: ReadMemory/WriteMemory), no-inline Notify (leg 2: hidden CLR
//    breakpoint), Ping (leg 1: func-eval round trip).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SpikeDebuggee;

public static class Program
{
    public static void Main()
    {
        Console.WriteLine($"SpikeDebuggee pid={Environment.ProcessId}");
        Console.WriteLine($"channel address=0x{SpikeDebugHelper.Attach():X}  (magic 'SHDB' + tick counter at +8)");
        Console.WriteLine("Attach the VS managed debugger and Break All to see the [Prolog] frames.");
        new BytecodeInterpreter().Dispatch();
    }
}

public sealed class BytecodeInterpreter
{
    public void Dispatch()
    {
        // Managed busy loop — deliberately NO Thread.Sleep on this thread: a
        // thread stopped inside native code can't be func-eval'd, and the real
        // engine's Dispatch is a managed loop too. One tick ≈ 250 ms.
        long ticks = 0;
        long spin = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            spin++;
            if ((spin & 0x3FFF) == 0 && sw.ElapsedMilliseconds >= 250)
            {
                sw.Restart();
                ticks++;
                SpikeDebugHelper.WriteSnapshot(ticks);
                if (ticks % 8 == 0)
                    SpikeDebugHelper.Notify(1); // simulated Prolog-breakpoint stop signal (leg 2)
            }
        }
    }
}

public static class SpikeDebugHelper
{
    public const uint Magic = 0x53484442; // "SHDB" little-endian at offset 0

    private static byte[]? _channel;
    private static GCHandle _pin;
    private static long _address;
    private static int _lastReason;

    /// <summary>
    /// Idempotent bootstrap: allocates and pins the channel buffer, returns its
    /// address. In the real engine this is func-eval'd once at debugger attach.
    /// Layout: [0..3] magic, [8..15] tick counter (written by the interpreter loop),
    /// [16..] command region the debugger writes with WriteMemory.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Attach()
    {
        if (_channel is null)
        {
            _channel = new byte[4096];
            _pin = GCHandle.Alloc(_channel, GCHandleType.Pinned);
            _address = _pin.AddrOfPinnedObject().ToInt64();
            BitConverter.GetBytes(Magic).CopyTo(_channel, 0);
        }
        return _address;
    }

    /// <summary>
    /// The hidden-breakpoint target (leg 2). The debugger plants a CLR breakpoint
    /// here; the engine calls it after pre-serializing a stop snapshot.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Notify(int reason)
    {
        Volatile.Write(ref _lastReason, reason);
    }

    /// <summary>Func-eval round-trip probe (leg 1).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Ping() => "shumway-spike-pong";

    public static void WriteSnapshot(long ticks)
    {
        if (_channel is null)
            Attach();
        BitConverter.GetBytes(ticks).CopyTo(_channel!, 8);

        // Echo the first command byte the debugger wrote (leg 1 WriteMemory check):
        // command region starts at +16; the engine copies it to +24 so the debugger
        // can verify the round trip with a subsequent ReadMemory.
        _channel![24] = _channel[16];
    }
}
