using WebAssembly;
using WebAssembly.Instructions;

using Shumway.Core;

namespace Shumway.Compiler.Wasm;

/// <summary>A module that does nothing at all: it returns its second argument
/// and touches neither memory nor the mailbox.
///
/// <para>It exists to tell two failures apart, which from the outside look
/// alike. If a call through the table index never comes back, either the CALL
/// is the problem (the plan's D1: a function pointer whose value is a table
/// index, invoked from C#) or the CALLEE is (a counter that read its mailbox
/// wrong and is counting down from a number no clock will outlive). This one
/// cannot loop, so if it does not return, the call is what does not work.
/// </para></summary>
public static class EchoModule
{
    /// <summary>Builds it. The signature is the ABI's, so the same call site
    /// exercises the same path.</summary>
    public static byte[] ToBytes(bool shared = false)
    {
        var module = new Module();
        module.Types.Add(new WebAssemblyType
        {
            Parameters = [WebAssemblyValueType.Int32, WebAssemblyValueType.Int32],
            Returns = [WebAssemblyValueType.Int32],
        });
        // The memory import is kept even though nothing is read: the module
        // under test has one, and instantiating both the same way keeps the
        // comparison to the one thing being compared.
        module.Imports.Add(new Import.Memory
        {
            Module = WasmAbi.MemoryModule,
            Field = WasmAbi.MemoryField,
            Type = new Memory(1, 65536),
        });
        module.Functions.Add(new Function { Type = 0 });
        module.Codes.Add(new FunctionBody
        {
            Code = [new LocalGet(1), new End()],
        });
        module.Exports.Add(new Export
        {
            Kind = ExternalKind.Function,
            Index = 0,
            Name = WasmAbi.EntryExport,
        });

        using var stream = new MemoryStream();
        module.WriteToBinary(stream);
        byte[] bytes = stream.ToArray();
        return shared ? WasmSharedMemory.Patch(bytes) : bytes;
    }
}
