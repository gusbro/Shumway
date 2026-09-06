using Shumway.Core;
using WebAssembly;
// Both namespaces have a Tag; the one that matters here is the cell's.
using Tag = Shumway.Core.Tag;
using WebAssembly.Instructions;

namespace Shumway.Compiler.Wasm;

/// <summary>The spike's one predicate, hand-built: the self-tail counter
///
/// <code>
/// loop(N) :- N &gt; 0, N1 is N - 1, loop(N1).
/// loop(0).
/// </code>
///
/// <para>It is the friendliest shape there is — no heap, no structures, no
/// builtins — and that is the point: it measures what crossing into wasm and
/// reaching the engine's memory COSTS, with nothing else in the way. If the
/// counter does not win here, nothing else will (the plan's Go criterion).
/// </para>
///
/// <para>What it exercises of the real backend: the open-coded deref of X0,
/// the small-integer tag test, i64 arithmetic on the payload, the self tail
/// call as a <c>loop</c> / <c>br</c> rather than a call, and the watermark and
/// flags check on the back edge that turns a long loop into a
/// <see cref="WasmVerdict.Safepoint"/> instead of an unbreakable one.</para>
///
/// <para>The whole resume state is the counter, and the counter lives in X0,
/// so a safepoint writes X0 back and re-entry reads it again: the cursor is
/// carried through the ABI but this predicate needs no other continuation.
/// </para></summary>
public static class SpikeCounterModule
{
    // Locals, after the two parameters.
    private const uint ParamMailbox = 0;
    private const uint ParamCursor = 1;
    private const uint LocalN = 2;          // i64: the counter, unboxed
    private const uint LocalRegs = 3;       // i32: the register file's address

    /// <summary>Builds the module. <paramref name="shared"/> asks for a shared
    /// memory import, which is what the browser needs (the runtime's memory is
    /// shared once threads are on) and what the desktop tests do not use.
    /// </summary>
    public static Module Build(bool shared = false)
    {
        var module = new Module();

        module.Types.Add(new WebAssemblyType
        {
            Parameters = [WebAssemblyValueType.Int32, WebAssemblyValueType.Int32],
            Returns = [WebAssemblyValueType.Int32],
        });

        // The memory is imported, never defined: it belongs to the runtime.
        module.Imports.Add(new Import.Memory
        {
            Module = WasmAbi.MemoryModule,
            Field = WasmAbi.MemoryField,
            Type = new Memory(1, MaximumPages),
        });

        module.Functions.Add(new Function { Type = 0 });
        module.Codes.Add(new FunctionBody
        {
            Locals =
            [
                new Local { Count = 1, Type = WebAssemblyValueType.Int64 },
                new Local { Count = 1, Type = WebAssemblyValueType.Int32 },
            ],
            Code = Body(),
        });
        module.Exports.Add(new Export
        {
            Kind = ExternalKind.Function,
            Index = 0,
            Name = WasmAbi.EntryExport,
        });

        if (shared) throw new NotSupportedException(
            "A shared memory import is emitted by patching the limits byte after "
            + "writing the module (the plan's D5); use WasmSharedMemory.Patch.");

        return module;
    }

    /// <summary>The module as bytes, ready to instantiate.</summary>
    public static byte[] ToBytes(bool shared = false)
    {
        using var stream = new MemoryStream();
        Build().WriteToBinary(stream);
        byte[] bytes = stream.ToArray();
        return shared ? WasmSharedMemory.Patch(bytes) : bytes;
    }

    /// <summary>65536 pages is 4 GiB, the whole 32-bit address space: the
    /// import must not be narrower than the memory the runtime hands over.
    /// </summary>
    private const uint MaximumPages = 65536;

    private static List<Instruction> Body()
    {
        var code = new List<Instruction>();

        // regs = (i32) mailbox[RegistersBase]
        code.Add(new LocalGet(ParamMailbox));
        code.Add(new Int64Load { Offset = WasmAbi.ByteOffset(WasmAbi.RegistersBase) });
        code.Add(new Int32WrapInt64());
        code.Add(new LocalSet(LocalRegs));

        // n = X0, still boxed
        code.Add(new LocalGet(LocalRegs));
        code.Add(new Int64Load { Offset = 0 });
        code.Add(new LocalTee(LocalN));

        // The tag test, open-coded: (cell >>> 60) & 0xF must be Tag.Int.
        // Anything else is a shape this predicate does not handle, and the
        // real backend would bail to the builtin path rather than fail.
        code.Add(new Int64Constant(Cell.TagShift));
        code.Add(new Int64ShiftRightUnsigned());
        code.Add(new Int64Constant(0xF));
        code.Add(new Int64And());
        code.Add(new Int64Constant((long)Tag.Int));
        code.Add(new Int64NotEqual());
        code.Add(new If());
        code.Add(new Int32Constant((int)WasmVerdict.Fail));
        code.Add(new Return());
        code.Add(new End());

        // n = payload, sign-extended: left 4 to push the tag out, arithmetic
        // right 4 to bring the sign back down.
        code.Add(new LocalGet(LocalN));
        code.Add(new Int64Constant(64 - Cell.TagShift));
        code.Add(new Int64ShiftLeft());
        code.Add(new Int64Constant(64 - Cell.TagShift));
        code.Add(new Int64ShiftRightSigned());
        code.Add(new LocalSet(LocalN));

        code.Add(new Block());              // depth 1: "done"
        code.Add(new Loop());               // depth 0: "again"

        // if n =< 0 -> leave the loop
        code.Add(new LocalGet(LocalN));
        code.Add(new Int64Constant(0));
        code.Add(new Int64LessThanOrEqualSigned());
        code.Add(new BranchIf(1));

        // n = n - 1
        code.Add(new LocalGet(LocalN));
        code.Add(new Int64Constant(1));
        code.Add(new Int64Subtract());
        code.Add(new LocalSet(LocalN));

        // The back edge is the safe point: flags pending, or the heap has
        // reached the watermark.
        code.Add(new LocalGet(ParamMailbox));
        code.Add(new Int64Load { Offset = WasmAbi.ByteOffset(WasmAbi.Flags) });
        code.Add(new Int64Constant(0));
        code.Add(new Int64NotEqual());
        code.Add(new LocalGet(ParamMailbox));
        code.Add(new Int64Load { Offset = WasmAbi.ByteOffset(WasmAbi.HeapTop) });
        code.Add(new LocalGet(ParamMailbox));
        code.Add(new Int64Load { Offset = WasmAbi.ByteOffset(WasmAbi.HeapWatermark) });
        code.Add(new Int64GreaterThanOrEqualSigned());
        code.Add(new Int32Or());
        code.Add(new If());
        code.AddRange(StoreCounterIntoX0());
        // The cursor says where to come back. This predicate keeps its whole
        // state in X0, so one resume point is all it has.
        code.Add(new LocalGet(ParamMailbox));
        code.Add(new Int64Constant(ResumeCursor));
        code.Add(new Int64Store { Offset = WasmAbi.ByteOffset(WasmAbi.Cursor) });
        code.Add(new Int32Constant((int)WasmVerdict.Safepoint));
        code.Add(new Return());
        code.Add(new End());

        code.Add(new Branch(0));            // the self tail call, as a back edge
        code.Add(new End());                // loop
        code.Add(new End());                // block

        code.AddRange(StoreCounterIntoX0());
        code.Add(new LocalGet(ParamMailbox));
        code.Add(new Int64Constant(0));
        code.Add(new Int64Store { Offset = WasmAbi.ByteOffset(WasmAbi.Cursor) });
        code.Add(new Int32Constant((int)WasmVerdict.Success));
        code.Add(new End());                // function

        return code;
    }

    /// <summary>The cursor a safepoint leaves behind. Re-entry re-reads X0,
    /// which is where the counter was left.</summary>
    public const long ResumeCursor = 1;

    /// <summary>X0 = the counter, boxed back into an integer cell.</summary>
    private static IEnumerable<Instruction> StoreCounterIntoX0()
    {
        yield return new LocalGet(LocalRegs);
        yield return new LocalGet(LocalN);
        yield return new Int64Constant(Cell.PayloadMask);
        yield return new Int64And();
        yield return new Int64Constant((long)Tag.Int << Cell.TagShift);
        yield return new Int64Or();
        yield return new Int64Store { Offset = 0 };
    }
}
