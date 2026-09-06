using System.Runtime.InteropServices;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using WebAssembly;
using WebAssembly.Runtime;

namespace Shumway.Tests.Wasm;

/// <summary>Runs a whole compiled PROGRAM -- several predicates, calling and
/// backtracking into each other -- on the desktop, with no engine and no
/// browser. The predicates are compiled by <see cref="WasmPredicateCompiler"/>
/// and executed by the emitter library's wasm-to-IL engine against one linear
/// memory holding heap, stack, registers and trail, laid out the way the
/// engine lays them out.
///
/// <para>The driver here is the interpreter's skeleton reduced to the verdict
/// protocol: Success consults CP (a harness-encoded marker, or the top
/// sentinel), SuccessTailCall dispatches Pc, Fail reads the top choice point
/// OUT OF THE MEMORY IMAGE -- its BP names the module and cursor whose
/// retry/trust does the restore -- and Deopt is an error, because nothing in
/// a test corpus is supposed to step aside.</para></summary>
public sealed class WasmProgramHarness : IDisposable, IWasmCompileEnv
{
    private const int MailboxAt = 1024;
    private const int RegistersAt = 2048;       // 64 registers
    private const int HeapAt = 4096;            // cells
    private const int HeapCells = 60_000;
    private const int StackAt = HeapAt + HeapCells * 8;
    private const int StackCells = 30_000;
    private const int TrailAt = StackAt + StackCells * 8;
    private const int TrailEntries = 20_000;

    // Harness encodings (IWasmCompileEnv): tagged so they can never collide
    // with each other or with a small cursor.
    private const int BpTag = 0x40000000;
    private const int MarkerTag = 0x20000000;
    private const int TopSentinel = -1;

    private readonly UnmanagedMemory _memory;
    private readonly List<(int FunctorId, Instance<WasmPredicateExports> Instance)> _preds = new();
    private readonly Dictionary<int, int> _predIndexByFunctor = new();
    private int _compilingIndex;

    public WasmProgramHarness(string source)
    {
        // Thirteen pages hold the whole image (heap 480 KB, stack 240 KB,
        // trail 80 KB, mailbox and registers below them).
        _memory = new UnmanagedMemory(13, 16);
        var clauses = new ClauseReader(source).ReadAll().ToList();
        var module = new ModuleCompiler().Compile(clauses);

        foreach (var p in module.Predicates)
            _predIndexByFunctor[p.FunctorId] = _predIndexByFunctor.Count;
        foreach (var p in module.Predicates)
        {
            _compilingIndex = _predIndexByFunctor[p.FunctorId];
            var entry = WasmPredicateCompiler.Compile(p, this);
            using var stream = new MemoryStream(entry.Module);
            var creator = Module.ReadFromBinary(stream).Compile<WasmPredicateExports>();
            var instance = creator(new ImportDictionary
            {
                { WasmAbi.MemoryModule, WasmAbi.MemoryField, new MemoryImport(() => _memory) },
            });
            _preds.Add((p.FunctorId, instance));
        }
    }

    // ---- IWasmCompileEnv: the harness's own constants ----

    int IWasmCompileEnv.EncodeBp(int cursor) => BpTag | (_compilingIndex << 16) | cursor;
    int IWasmCompileEnv.EncodeReturnMarker(int cursor) => MarkerTag | (_compilingIndex << 16) | cursor;
    int IWasmCompileEnv.EncodeCallTarget(int calleeFunctorId)
        => _predIndexByFunctor.TryGetValue(calleeFunctorId, out int idx)
            ? idx
            : throw new InvalidOperationException(
                  $"the corpus calls functor {calleeFunctorId}, which it does not define");
    int IWasmCompileEnv.EncodeDeoptPc(int bytecodePc) => bytecodePc;

    // ---- the memory image ----

    private long GetSlot(int slot)
        => Marshal.ReadInt64(_memory.Start, MailboxAt + slot * WasmAbi.SlotSize);
    private void SetSlot(int slot, long v)
        => Marshal.WriteInt64(_memory.Start, MailboxAt + slot * WasmAbi.SlotSize, v);

    public Cell ReadHeap(int index)
        => new(Marshal.ReadInt64(_memory.Start, HeapAt + index * 8));
    private Cell ReadStack(int index)
        => new(Marshal.ReadInt64(_memory.Start, StackAt + index * 8));
    public void SetRegister(int r, Cell c)
        => Marshal.WriteInt64(_memory.Start, RegistersAt + r * 8, c.Data);
    public Cell GetRegister(int r)
        => new(Marshal.ReadInt64(_memory.Start, RegistersAt + r * 8));

    /// <summary>A fresh unbound heap variable; returns its address.</summary>
    public int NewVariable()
    {
        int h = (int)GetSlot(WasmAbi.HeapTop);
        Marshal.WriteInt64(_memory.Start, HeapAt + h * 8, h);   // Ref(h) == h
        SetSlot(WasmAbi.HeapTop, h + 1);
        return h;
    }

    /// <summary>The value at a heap address, derefed.</summary>
    public Cell Deref(int addr)
    {
        Cell c = ReadHeap(addr);
        while (c.Tag == Shumway.Core.Tag.Ref)
        {
            Cell at = ReadHeap(c.AsHeapIndex);
            if (at.Data == c.Data) return at;    // unbound
            c = at;
        }
        return c;
    }

    private void ResetImage()
    {
        SetSlot(WasmAbi.HeapBase, HeapAt);
        SetSlot(WasmAbi.StackBase, StackAt);
        SetSlot(WasmAbi.RegistersBase, RegistersAt);
        SetSlot(WasmAbi.BindingTrailBase, TrailAt);
        SetSlot(WasmAbi.ExtraTrailBase, 0);
        SetSlot(WasmAbi.HeapTop, 0);
        SetSlot(WasmAbi.HeapWatermark, HeapCells - 64);
        SetSlot(WasmAbi.StackTop, 0);
        SetSlot(WasmAbi.ChoiceTop, -1);
        SetSlot(WasmAbi.HeapBacktrack, 0);
        SetSlot(WasmAbi.TrailTop, 0);
        SetSlot(WasmAbi.Flags, 0);
        SetSlot(WasmAbi.Pc, 0);
        SetSlot(WasmAbi.Cursor, 0);
        SetSlot(WasmAbi.EnvTop, -1);
        SetSlot(WasmAbi.ContinuationPc, TopSentinel);
        SetSlot(WasmAbi.StackLimit, StackCells - 64);
        SetSlot(WasmAbi.TrailLimit, TrailEntries - 16);
        SetSlot(WasmAbi.ExtraTrailTop, 0);
        SetSlot(WasmAbi.ViewGen, 7);
        SetSlot(WasmAbi.CutBarrier, -1);
    }

    // ---- the driver ----

    /// <summary>The heap home of the fresh variable passed as argument
    /// <paramref name="i"/> -- captured at Solve time, because the REGISTERS
    /// are working state: choice-point restores overwrite them, so reading a
    /// register after the run tells you about the last restore, not about the
    /// answer.</summary>
    public int ArgumentHome(int i) => _argHomes[i];
    private int[] _argHomes = [];

    /// <summary>The argument's value after a solution, derefed.</summary>
    public Cell Answer(int i) => Deref(ArgumentHome(i));

    /// <summary>Starts the goal <c>name(args...)</c>: sets up a fresh image,
    /// loads the registers, and drives to the first answer. An unbound
    /// argument is passed as <c>null</c> and read back through
    /// <see cref="Answer"/>.</summary>
    public bool Solve(string name, params long?[] args)
    {
        ResetImage();
        int fid = FunctorTable.Intern(
            AtomTable.Intern(name, permanent: true).Id, args.Length);
        _argHomes = new int[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is { } v) { SetRegister(i, Cell.Int(v)); _argHomes[i] = -1; }
            else
            {
                _argHomes[i] = NewVariable();
                SetRegister(i, Cell.Ref(_argHomes[i]));
            }
        }
        return Drive(_predIndexByFunctor[fid], 0);
    }

    /// <summary>Backtracks into the last answer for the next one.</summary>
    public bool NextSolution() => Backtrack();

    private bool Drive(int predIndex, int cursor)
    {
        while (true)
        {
            var verdict = (WasmVerdict)_preds[predIndex].Instance.Exports
                .run(MailboxAt, cursor);
            switch (verdict)
            {
                case WasmVerdict.Success:
                {
                    int cp = (int)GetSlot(WasmAbi.ContinuationPc);
                    if (cp == TopSentinel) return true;
                    if ((cp & MarkerTag) == 0)
                        throw new InvalidOperationException($"unmarked continuation {cp}");
                    predIndex = (cp >> 16) & 0x1FFF;
                    cursor = cp & 0xFFFF;
                    break;
                }
                case WasmVerdict.SuccessTailCall:
                    predIndex = (int)GetSlot(WasmAbi.Pc);
                    cursor = 0;
                    break;
                case WasmVerdict.Fail:
                    if (!TryPopForeign(out predIndex, out cursor)) return false;
                    break;
                case WasmVerdict.Deopt:
                    throw new InvalidOperationException(
                        $"deopt at bytecode {GetSlot(WasmAbi.Pc)} -- the corpus is "
                        + "supposed to stay on the fast path");
                default:
                    throw new InvalidOperationException($"verdict {verdict}");
            }
        }
    }

    /// <summary>The driver's half of backtracking: the failing module already
    /// handled its own choice points; what reaches here is a CP belonging to
    /// ANOTHER module (or none). The CP's BP names it.</summary>
    private bool Backtrack() => TryPopForeign(out int p, out int c) && Drive(p, c);

    private bool TryPopForeign(out int predIndex, out int cursor)
    {
        int b = (int)GetSlot(WasmAbi.ChoiceTop);
        if (b < 0) { predIndex = 0; cursor = 0; return false; }
        int arity = (int)ReadStack(b).Data;
        int bp = (int)ReadStack(b + 1 + arity + 3).Data;
        if ((bp & BpTag) == 0)
            throw new InvalidOperationException($"foreign BP {bp} is not harness-encoded");
        predIndex = (bp >> 16) & 0x1FFF;
        cursor = bp & 0xFFFF;
        return true;
    }

    public void Dispose()
    {
        foreach (var (_, inst) in _preds) inst.Dispose();
        _memory.Dispose();
    }
}
