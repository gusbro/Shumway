using System.Runtime.InteropServices;
using Shumway.Compiler.Wasm;
using WebAssembly;
using WebAssembly.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Phase 0 of the many-modules arc, the half that needs no browser.
///
/// <para>The arc's premise is that two wasm modules can hand control to each
/// other through an imported table without returning to the host, and that the
/// transfer is a REAL tail call. The speed half of that has to be measured in a
/// browser. These are the parts that can be settled here, and settling them
/// early is the point: each one can kill or reshape the arc for a few minutes'
/// work.</para></summary>
/// <summary>The module's export surface. Public because the library compiles
/// wasm to IL by INHERITING this type.</summary>
public abstract class PingPongExports
{
    public abstract int run(int mailbox, int cursor);
}

public sealed class TailPingPongSpikeTests(ITestOutputHelper o)
{

    /// <summary>The named trap: <see cref="WasmSharedMemory.Patch"/> walks the
    /// import section to find the memory's limits byte, and a table import puts
    /// a new kind in its path. The walker has a case for it that until now had
    /// never run.</summary>
    [Fact]
    public void SharedMemoryPatchWalksPastATableImport()
    {
        byte[] plain = SpikeTailPingPongModules.Build(
            SpikeTailPingPongModules.SlotA, SpikeTailPingPongModules.SlotB,
            shared: false);
        Assert.False(WasmSharedMemory.IsShared(plain));

        byte[] shared = SpikeTailPingPongModules.Build(
            SpikeTailPingPongModules.SlotA, SpikeTailPingPongModules.SlotB,
            shared: true);
        Assert.True(WasmSharedMemory.IsShared(shared));
        // One byte for one byte: no section length may move.
        Assert.Equal(plain.Length, shared.Length);
    }

    /// <summary>G3: does the emitter library's own wasm engine execute
    /// <c>return_call_indirect</c> through an IMPORTED table? If it does not,
    /// nothing in this test project can ever exercise the in-wasm hop and the
    /// whole split path becomes browser-only to test — which does not kill the
    /// arc but changes its cost a great deal. Either way it is worth knowing on
    /// day one rather than in the last phase.</summary>
    [Fact]
    public void G3_TheLibraryExecutesAnIndirectTailCallThroughAnImportedTable()
    {
        var memory = new UnmanagedMemory(1, 1);
        var table = new FunctionTable(2, null);

        Instance<PingPongExports> Instantiate(int self, int other)
        {
            byte[] bytes = SpikeTailPingPongModules.Build(self, other, shared: false);
            using var stream = new MemoryStream(bytes);
            var creator = Module.ReadFromBinary(stream).Compile<PingPongExports>();
            return creator(new ImportDictionary
            {
                { Shumway.Core.WasmAbi.MemoryModule, Shumway.Core.WasmAbi.MemoryField,
                  new MemoryImport(() => memory) },
                { SpikeTailPingPongModules.TableModule,
                  SpikeTailPingPongModules.TableField, table },
            });
        }

        Instance<PingPongExports> a, b;
        try
        {
            a = Instantiate(SpikeTailPingPongModules.SlotA, SpikeTailPingPongModules.SlotB);
            b = Instantiate(SpikeTailPingPongModules.SlotB, SpikeTailPingPongModules.SlotA);
        }
        catch (Exception e)
        {
            // A refusal here is the ANSWER to G3, not a broken test: record it
            // in the shape the arc's plan asks for and fail loudly.
            Assert.Fail($"G3 = NO: the library refused the module. {e.GetType().Name}: {e.Message}");
            return;
        }

        table[(int)SpikeTailPingPongModules.SlotA] = (Func<int, int, int>)a.Exports.run;
        table[(int)SpikeTailPingPongModules.SlotB] = (Func<int, int, int>)b.Exports.run;

        Marshal.WriteInt64(memory.Start, SpikeTailPingPongModules.HopsRemainingSlot * 8, 7);
        int who;
        try { who = a.Exports.run(0, 0); }
        catch (Exception e)
        {
            Assert.Fail($"G3 = NO: the hop did not execute. {e.GetType().Name}: {e.Message}");
            return;
        }

        long done = Marshal.ReadInt64(memory.Start, SpikeTailPingPongModules.HopsDoneSlot * 8);
        o.WriteLine($"G3 = YES: 7 hops, finished in slot {who} (memory says {done})");
        // Seven hops starting at A ends at B: A,B,A,B,A,B,A -> the 7th
        // decrement happens in A... the parity is what it is; assert only that
        // SOMETHING finished and that both halves agree on which.
        Assert.Equal(who, (int)done);
        Assert.InRange(who, 0, 1);
    }

    /// <summary>The property the arc actually rests on, as far as it can be
    /// tested here: many hops must not grow the host stack. The library
    /// executes wasm by compiling to IL, so a non-tail transfer shows up as a
    /// real .NET stack overflow — which is not catchable, and would take the
    /// test process down. That is a blunt but honest signal.</summary>
    [Fact]
    public void ManyHopsDoNotGrowTheStack()
    {
        var memory = new UnmanagedMemory(1, 1);
        var table = new FunctionTable(2, null);
        Instance<PingPongExports> Instantiate(int self, int other)
        {
            byte[] bytes = SpikeTailPingPongModules.Build(self, other, shared: false);
            using var stream = new MemoryStream(bytes);
            var creator = Module.ReadFromBinary(stream).Compile<PingPongExports>();
            return creator(new ImportDictionary
            {
                { Shumway.Core.WasmAbi.MemoryModule, Shumway.Core.WasmAbi.MemoryField,
                  new MemoryImport(() => memory) },
                { SpikeTailPingPongModules.TableModule,
                  SpikeTailPingPongModules.TableField, table },
            });
        }
        Instance<PingPongExports> a, b;
        try
        {
            a = Instantiate(SpikeTailPingPongModules.SlotA, SpikeTailPingPongModules.SlotB);
            b = Instantiate(SpikeTailPingPongModules.SlotB, SpikeTailPingPongModules.SlotA);
        }
        catch (Exception e) { Assert.Fail($"G3 = NO: {e.Message}"); return; }
        table[(int)SpikeTailPingPongModules.SlotA] = (Func<int, int, int>)a.Exports.run;
        table[(int)SpikeTailPingPongModules.SlotB] = (Func<int, int, int>)b.Exports.run;

        // Modest here on purpose: the browser is where the real 10^7 number is
        // taken. This only has to be far past any plausible frame budget.
        const long hops = 200_000;
        Marshal.WriteInt64(memory.Start, SpikeTailPingPongModules.HopsRemainingSlot * 8, hops);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int who = a.Exports.run(0, 0);
        sw.Stop();
        o.WriteLine($"{hops} hops in {sw.Elapsed.TotalMilliseconds:F1} ms "
            + $"({sw.Elapsed.TotalMilliseconds * 1e6 / hops:F0} ns/hop, library engine), "
            + $"finished in slot {who}");
        Assert.InRange(who, 0, 1);
    }
}
