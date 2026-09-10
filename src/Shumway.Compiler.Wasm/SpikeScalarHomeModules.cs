using WebAssembly;
using WebAssembly.Instructions;

using Shumway.Core;

namespace Shumway.Compiler.Wasm;

/// <summary>Where should the WAM's scalars live?
///
/// <para>Today they live in LOCALS, loaded from the mailbox on entry and
/// written back on exit. That prologue and epilogue are most of a small
/// module's fixed cost — about 1,198 bytes of the ~3,234 byte floor — and they
/// are also what a crossing has to pay: every hop between partitions, and every
/// hop between modules once modules can reach each other, spills fifteen
/// scalars and reloads them.</para>
///
/// <para>Imported mutable globals would remove the prologue rather than share
/// it: nothing to load, nothing to spill, and a module reached by a tail call
/// finds the state already there. The question is what an access costs. A local
/// is a register; an imported global may or may not be, and these are the
/// hottest reads and writes in the engine — H moves on every allocation.</para>
///
/// <para>So: the same counting loop, four ways, differing only in where the
/// counter lives.</para></summary>
public static class SpikeScalarHomeModules
{
    public enum Home
    {
        /// <summary>A local. The floor: this is a register.</summary>
        Local,
        /// <summary>A global the module declares itself.</summary>
        OwnGlobal,
        /// <summary>A global imported from elsewhere — what sharing state
        /// across modules would actually use.</summary>
        ImportedGlobal,
        /// <summary>Linear memory at a mailbox offset: what an access costs if
        /// the scalar is NOT cached in a local, which is the shape the code
        /// would have without a prologue.</summary>
        Memory,
    }

    public const string GlobalModule = "state";
    public const string GlobalField = "h";

    /// <summary>Where the loop leaves its result, so the host can check the
    /// work was not optimised away.</summary>
    public const int ResultSlot = 5;

    public static byte[] Build(Home home, bool shared)
    {
        var module = new Module();
        module.Types.Add(new WebAssemblyType
        {
            Parameters = [WebAssemblyValueType.Int32, WebAssemblyValueType.Int32],
            Returns = [WebAssemblyValueType.Int32],
        });
        module.Imports.Add(new Import.Memory
        {
            Module = WasmAbi.MemoryModule,
            Field = WasmAbi.MemoryField,
            Type = new Memory(1, 65536),
        });
        if (home == Home.ImportedGlobal)
            module.Imports.Add(new Import.Global(GlobalModule, GlobalField,
                WebAssemblyValueType.Int64) { IsMutable = true });
        if (home == Home.OwnGlobal)
            module.Globals.Add(new Global
            {
                ContentType = WebAssemblyValueType.Int64,
                IsMutable = true,
                InitializerExpression = [new Int64Constant(0), new End()],
            });

        module.Functions.Add(new Function { Type = 0 });
        module.Exports.Add(new Export
        {
            Kind = ExternalKind.Function, Index = 0, Name = WasmAbi.EntryExport,
        });

        // locals: 0,1 are the params; 2 = the counter, 3 = the scalar when it
        // lives in a local.
        var code = new List<Instruction>();
        // counter = iterations, read from the mailbox so the host sets it.
        code.Add(new LocalGet(0));
        code.Add(new Int64Load { Offset = 0 });
        code.Add(new LocalSet(2));

        code.Add(new Block(BlockType.Empty));
        code.Add(new Loop(BlockType.Empty));
        // if (counter == 0) break
        code.Add(new LocalGet(2));
        code.Add(new Int64Constant(0));
        code.Add(new Int64Equal());
        code.Add(new BranchIf(1));

        // The one line under test: bump the scalar where it lives.
        switch (home)
        {
            case Home.Local:
                code.Add(new LocalGet(3));
                code.Add(new Int64Constant(1));
                code.Add(new Int64Add());
                code.Add(new LocalSet(3));
                break;
            case Home.OwnGlobal:
            case Home.ImportedGlobal:
                code.Add(new GlobalGet(0));
                code.Add(new Int64Constant(1));
                code.Add(new Int64Add());
                code.Add(new GlobalSet(0));
                break;
            case Home.Memory:
                code.Add(new LocalGet(0));
                code.Add(new LocalGet(0));
                code.Add(new Int64Load { Offset = 8 });
                code.Add(new Int64Constant(1));
                code.Add(new Int64Add());
                code.Add(new Int64Store { Offset = 8 });
                break;
        }

        code.Add(new LocalGet(2));
        code.Add(new Int64Constant(1));
        code.Add(new Int64Subtract());
        code.Add(new LocalSet(2));
        code.Add(new Branch(0));
        code.Add(new End());        // loop
        code.Add(new End());        // block

        // Publish the result so nothing can be elided as dead.
        code.Add(new LocalGet(0));
        switch (home)
        {
            case Home.Local: code.Add(new LocalGet(3)); break;
            case Home.OwnGlobal:
            case Home.ImportedGlobal: code.Add(new GlobalGet(0)); break;
            case Home.Memory:
                code.Add(new LocalGet(0));
                code.Add(new Int64Load { Offset = 8 });
                break;
        }
        code.Add(new Int64Store { Offset = ResultSlot * 8 });

        code.Add(new Int32Constant(0));
        code.Add(new End());        // function

        module.Codes.Add(new FunctionBody
        {
            Locals = [new Local { Count = 2, Type = WebAssemblyValueType.Int64 }],
            Code = code,
        });

        using var stream = new MemoryStream();
        module.WriteToBinary(stream);
        byte[] bytes = stream.ToArray();
        return shared ? WasmSharedMemory.Patch(bytes) : bytes;
    }
}
