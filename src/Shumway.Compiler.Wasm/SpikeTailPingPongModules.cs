using WebAssembly;
using WebAssembly.Instructions;

using Shumway.Core;

namespace Shumway.Compiler.Wasm;

/// <summary>The Go/No-Go spike for the many-modules arc: two modules that hand
/// control to each other with <c>return_call_indirect</c> through a table they
/// both import, never returning to the host.
///
/// <para>What it has to prove is a PROPERTY, not a speed: that the transfer is
/// a real tail call. A Prolog program makes millions of calls, and in the WAM a
/// call IS a jump — the continuation lives in CP, not on the host stack. If a
/// browser quietly compiles <c>return_call_indirect</c> as an ordinary call,
/// the stack grows per hop and the whole design is dead, with no fallback.
/// Millions of hops with a bounded stack is the only evidence for that.</para>
///
/// <para>The shape is deliberately the production one: the export is
/// <c>run(mailbox, cursor) -&gt; i32</c>, the memory import is the engine's, and
/// the hop reads its own state out of linear memory — so a Go here is evidence
/// about the code the emitter will actually produce, not about a toy.</para>
/// </summary>
public static class SpikeTailPingPongModules
{
    /// <summary>Where the ping-pong keeps its counter, in cells of the shared
    /// memory. Arbitrary but fixed: the harness reads the same place.</summary>
    public const int HopsRemainingSlot = 0;
    public const int HopsDoneSlot = 1;

    /// <summary>Where the host writes each half's function-table index, in
    /// cells. A module reads the OTHER half's index from here rather than
    /// having it baked in, because the index is whatever addFunction hands out
    /// at run time and the two halves refer to each other. That indirection is
    /// also what production needs, so the spike exercises the real shape.
    /// </summary>
    public const int IndexOfASlot = 2;
    public const int IndexOfBSlot = 3;

    /// <summary>Identifies a half in the report; not a table index.</summary>
    public const int HalfA = 0;
    public const int HalfB = 1;

    public const string TableModule = "env";
    public const string TableField = "__indirect_function_table";

    /// <summary>One half of the pair. <paramref name="selfHalf"/> identifies
    /// this one in the report; <paramref name="otherIndexSlot"/> is the memory
    /// cell where the host leaves the other half's table index.
    ///
    /// <para>The body: decrement the counter; when it reaches zero, count
    /// itself done and RETURN to the host with the slot's id (so the harness
    /// can tell which half finished); otherwise tail-call the other half. The
    /// only non-tail exit is the last one.</para></summary>
    public static byte[] Build(int selfHalf, int otherIndexSlot, bool shared)
    {
        var module = new Module();
        // Type 0 is the production signature, on purpose: return_call_indirect
        // requires the callee's type to match the caller's RESULT type, and
        // using the real one means a Go here transfers to the real emitter.
        module.Types.Add(new WebAssemblyType
        {
            Parameters = [WebAssemblyValueType.Int32, WebAssemblyValueType.Int32],
            Returns = [WebAssemblyValueType.Int32],
        });
        // The memory import goes FIRST: WasmSharedMemory.Patch walks the
        // import section for it, and while that walk does skip a table import
        // correctly, keeping the order it has always seen is the cheap way to
        // stay out of its way.
        module.Imports.Add(new Import.Memory
        {
            Module = WasmAbi.MemoryModule,
            Field = WasmAbi.MemoryField,
            Type = new Memory(1, 65536),
        });
        // Minimum 2, no maximum: the host may grow the table, and a module
        // reaches slots added AFTER it was instantiated because a table
        // import is by reference. That is G4.
        module.Imports.Add(new Import.Table(TableModule, TableField, 2, null));
        module.Functions.Add(new Function { Type = 0 });
        module.Exports.Add(new Export
        {
            Kind = ExternalKind.Function, Index = 0, Name = WasmAbi.EntryExport,
        });

        var code = new List<Instruction>();
        // EVERY address is relative to the base the host passes in local 0.
        // Absolute offsets would work against a private image and corrupt the
        // runtime's own linear memory in a browser, where address 0 belongs to
        // someone else -- and would read garbage back, which is exactly how
        // this was caught.
        void Base() => code.Add(new LocalGet(0));

        // remaining = mem[HopsRemaining] - 1
        Base();
        Base();
        code.Add(new Int64Load { Offset = HopsRemainingSlot * 8 });
        code.Add(new Int64Constant(1));
        code.Add(new Int64Subtract());
        code.Add(new Int64Store { Offset = HopsRemainingSlot * 8 });

        // if (remaining <= 0) { mem[HopsDone] = half; return half; }
        Base();
        code.Add(new Int64Load { Offset = HopsRemainingSlot * 8 });
        code.Add(new Int64Constant(0));
        code.Add(new Int64LessThanOrEqualSigned());
        code.Add(new If());
        Base();
        code.Add(new Int64Constant(selfHalf));
        code.Add(new Int64Store { Offset = HopsDoneSlot * 8 });
        code.Add(new Int32Constant(selfHalf));
        code.Add(new Return());
        code.Add(new End());

        // ...otherwise hand control to the other half and DO NOT come back.
        code.Add(new LocalGet(0));                  // mailbox, passed along
        code.Add(new LocalGet(1));                  // cursor, passed along
        // The other half's table index, read at run time.
        Base();
        code.Add(new Int64Load { Offset = (uint)(otherIndexSlot * 8) });
        code.Add(new Int32WrapInt64());
        code.Add(new ReturnCallIndirect(0));        // type 0, table 0
        // Unreachable in practice; wasm still wants the block to type-check.
        code.Add(new Int32Constant(-1));
        code.Add(new End());

        module.Codes.Add(new FunctionBody { Locals = [], Code = code });

        using var stream = new MemoryStream();
        module.WriteToBinary(stream);
        byte[] bytes = stream.ToArray();
        return shared ? WasmSharedMemory.Patch(bytes) : bytes;
    }
}
