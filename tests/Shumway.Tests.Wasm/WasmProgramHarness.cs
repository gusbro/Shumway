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
    // The functor-arity mirror the general unifier reads (i32 per functor id).
    private const int ArityAt = TrailAt + TrailEntries * 4;

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
        // The registry fills on first engine construction; the harness has no
        // engine, and an empty registry would make every builtin look like an
        // ordinary missing predicate.
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();
        Shumway.Embedding.MetaBuiltins.EnsureRegistered();
        // Sixteen pages hold the whole image (heap 480 KB, stack 240 KB,
        // trail 80 KB, the functor-arity mirror, mailbox and registers).
        _memory = new UnmanagedMemory(16, 16);
        var clauses = new ClauseReader(source).ReadAll().ToList();
        var module = new ModuleCompiler().Compile(clauses);

        foreach (var p in module.Predicates)
            _predIndexByFunctor[p.FunctorId] = _predIndexByFunctor.Count;
        foreach (var p in module.Predicates)
        {
            _compilingIndex = _predIndexByFunctor[p.FunctorId];
            var entry = WasmPredicateCompiler.Compile(p, this,
                floatLiterals: module.FloatLiterals);
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

    bool IWasmCompileEnv.TryGetBuiltin(int calleeFunctorId, out int builtinId)
        => Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(calleeFunctorId, out builtinId);

    // Everything the harness emulates is direct.
    bool IWasmCompileEnv.IsDirectBuiltin(int builtinId) => true;

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
        SetSlot(WasmAbi.WriteMode, 0);
        SetSlot(WasmAbi.UnifyPointer, 0);
        SetSlot(WasmAbi.FunctorArityBase, ArityAt);
        SyncArityTable();
    }

    // ---- building and reading terms in the image ----

    private void WriteHeap(int index, Cell c)
        => Marshal.WriteInt64(_memory.Start, HeapAt + index * 8, c.Data);

    /// <summary>Mirrors every functor's arity into the image. Re-run per
    /// query setup: term builders may intern functors after construction.</summary>
    private void SyncArityTable()
    {
        int count = FunctorTable.Count;
        if (ArityAt + count * 4 > 16 * 65536)
            throw new InvalidOperationException($"arity mirror overflows the image ({count} functors)");
        for (int fid = 0; fid < count; fid++)
            Marshal.WriteInt32(_memory.Start, ArityAt + fid * 4,
                FunctorTable.TryLookup(fid, out var fe) ? fe.Arity : 0);
    }

    private int AllocHeap(int cells)
    {
        int h = (int)GetSlot(WasmAbi.HeapTop);
        SetSlot(WasmAbi.HeapTop, h + cells);
        return h;
    }

    /// <summary>An integer list, built the way the engine builds one:
    /// two-cell conses (ADR-017), nil-terminated.</summary>
    public Cell MakeIntList(params long[] items)
    {
        Cell tail = Cell.Atom(AtomTable.EmptyListId);
        for (int i = items.Length - 1; i >= 0; i--)
        {
            int pair = AllocHeap(2);
            WriteHeap(pair, Cell.Int(items[i]));
            WriteHeap(pair + 1, tail);
            tail = Cell.Lis(pair);
        }
        return tail;
    }

    public Cell MakeAtom(string name)
        => Cell.Atom(AtomTable.Intern(name, permanent: true).Id);

    public Cell CellInt(long v) => Cell.Int(v);

    /// <summary>A partial list <c>[items... | tail]</c>.</summary>
    public Cell MakePartialList(long[] items, Cell tail)
    {
        Cell t = tail;
        for (int i = items.Length - 1; i >= 0; i--)
        {
            int pair = AllocHeap(2);
            WriteHeap(pair, Cell.Int(items[i]));
            WriteHeap(pair + 1, t);
            t = Cell.Lis(pair);
        }
        return t;
    }

    /// <summary>An Int argument for SolveWith (a nullable-friendly name).</summary>
    public Cell MakeIntNull(long v) => Cell.Int(v);

    /// <summary>A structure in the image: functor cell plus args, the
    /// ADR-017 inline layout.</summary>
    public Cell MakeStruct(string name, params Cell[] args)
    {
        int fid = FunctorTable.Intern(
            AtomTable.Intern(name, permanent: true).Id, args.Length);
        int f = AllocHeap(args.Length + 1);
        WriteHeap(f, Cell.Functor(fid));
        for (int i = 0; i < args.Length; i++) WriteHeap(f + 1 + i, args[i]);
        return Cell.Str(f);
    }

    /// <summary>A term rendered from the image, for comparing answers:
    /// canonical-ish, no operators, unbound as <c>_</c>.</summary>
    public string Render(Cell c)
    {
        c = DerefCell(c);
        switch (c.Tag)
        {
            case Shumway.Core.Tag.Int: return c.AsInt.ToString();
            case Shumway.Core.Tag.Atom:
                return AtomTable.GetById(c.AsAtomId)?.Name ?? "<atom?>";
            case Shumway.Core.Tag.Ref: return "_";
            case Shumway.Core.Tag.Lis:
            {
                var sb = new System.Text.StringBuilder("[");
                Cell cur = c;
                bool first = true;
                while (true)
                {
                    cur = DerefCell(cur);
                    if (cur.Tag == Shumway.Core.Tag.Lis)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(Render(ReadHeap(cur.AsHeapIndex)));
                        cur = ReadHeap(cur.AsHeapIndex + 1);
                        continue;
                    }
                    if (cur.Tag == Shumway.Core.Tag.Atom
                        && cur.AsAtomId == AtomTable.EmptyListId) break;
                    sb.Append('|').Append(Render(cur));
                    break;
                }
                return sb.Append(']').ToString();
            }
            case Shumway.Core.Tag.Str:
            {
                int f = c.AsHeapIndex;
                var (atomId, arity) = FunctorTable.Lookup(ReadHeap(f).AsFunctorId);
                var sb = new System.Text.StringBuilder(
                    AtomTable.GetById(atomId)?.Name ?? "<f?>");
                sb.Append('(');
                for (int i = 0; i < arity; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Render(ReadHeap(f + 1 + i)));
                }
                return sb.Append(')').ToString();
            }
            default: return $"<{c.Tag}>";
        }
    }

    private Cell DerefCell(Cell c)
    {
        while (c.Tag == Shumway.Core.Tag.Ref)
        {
            Cell at = ReadHeap(c.AsHeapIndex);
            if (at.Data == c.Data) return at;
            c = at;
        }
        return c;
    }

    /// <summary>Solve with arbitrary argument cells; <c>null</c> is a fresh
    /// variable whose home <see cref="Answer"/> reads back. Cells must have
    /// been built through this harness (they live in the image).</summary>
    public bool SolveWith(string name, params Cell?[] args)
    {
        int fid = FunctorTable.Intern(
            AtomTable.Intern(name, permanent: true).Id, args.Length);
        _argHomes = new int[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is { } c) { SetRegister(i, c); _argHomes[i] = -1; }
            else
            {
                _argHomes[i] = NewVariable();
                SetRegister(i, Cell.Ref(_argHomes[i]));
            }
        }
        return Drive(_predIndexByFunctor[fid], 0);
    }

    /// <summary>Fresh image, no goal yet: for building argument terms before
    /// <see cref="SolveWith"/>.</summary>
    public void Fresh() => ResetImage();

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

    /// <summary>Decodes a float answer from its two heap cells.</summary>
    public double AnswerFloat(int i)
    {
        Cell header = Answer(i);
        if (header.Tag != Shumway.Core.Tag.Float)
            throw new InvalidOperationException($"not a float: {header.Tag}");
        long paired = Marshal.ReadInt64(
            _memory.Start, HeapAt + header.FloatPairedIndex * 8);
        return Cell.DecodeFloat(header, new Cell(paired));
    }

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
                case WasmVerdict.BuiltinRequest:
                {
                    int builtinId = (int)GetSlot(WasmAbi.BuiltinId);
                    int retCursor = (int)GetSlot(WasmAbi.Cursor);
                    if (!RunBuiltin(builtinId))
                    {
                        if (!TryPopForeign(out predIndex, out cursor)) return false;
                        break;
                    }
                    if (retCursor == -1)
                    {
                        // A tail-position builtin: proceed, exactly as a
                        // Success would.
                        int cp = (int)GetSlot(WasmAbi.ContinuationPc);
                        if (cp == TopSentinel) return true;
                        if ((cp & MarkerTag) == 0)
                            throw new InvalidOperationException($"unmarked continuation {cp}");
                        predIndex = (cp >> 16) & 0x1FFF;
                        cursor = cp & 0xFFFF;
                        break;
                    }
                    cursor = retCursor;             // same predicate, resumed
                    break;
                }
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

    /// <summary>A handful of builtins, emulated over the image: enough for
    /// the corpus (type tests and =/2). The REAL integration runs the real
    /// registry against the real engine; what this exercises is the wasm side
    /// of the request protocol, which is identical.</summary>
    private bool RunBuiltin(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        Cell a0 = DerefCell(GetRegister(0));
        switch (entry.Name, entry.Arity)
        {
            case ("true", 0): return true;
            case ("fail", 0) or ("false", 0): return false;
            case ("var", 1): return a0.Tag is Shumway.Core.Tag.Ref or Shumway.Core.Tag.AttVar;
            case ("nonvar", 1): return a0.Tag is not (Shumway.Core.Tag.Ref or Shumway.Core.Tag.AttVar);
            case ("integer", 1): return a0.Tag is Shumway.Core.Tag.Int or Shumway.Core.Tag.BigInt;
            case ("atom", 1): return a0.Tag == Shumway.Core.Tag.Atom;
            case ("number", 1): return a0.Tag is Shumway.Core.Tag.Int or Shumway.Core.Tag.BigInt
                                              or Shumway.Core.Tag.Float or Shumway.Core.Tag.Rational;
            case ("atomic", 1): return a0.Tag is Shumway.Core.Tag.Atom or Shumway.Core.Tag.Int
                                              or Shumway.Core.Tag.BigInt or Shumway.Core.Tag.Float
                                              or Shumway.Core.Tag.Rational;
            case ("=", 2):
            {
                Cell b = DerefCell(GetRegister(1));
                if (a0.Data == b.Data) return true;
                if (a0.Tag == Shumway.Core.Tag.Ref) { BindImage(a0.AsHeapIndex, b); return true; }
                if (b.Tag == Shumway.Core.Tag.Ref) { BindImage(b.AsHeapIndex, a0); return true; }
                if (a0.Tag is Shumway.Core.Tag.Int or Shumway.Core.Tag.Atom
                    && b.Tag is Shumway.Core.Tag.Int or Shumway.Core.Tag.Atom) return false;
                throw new InvalidOperationException("=/2 on shapes the harness does not emulate");
            }
            default:
                throw new InvalidOperationException(
                    $"the harness cannot emulate builtin {entry.Name}/{entry.Arity}");
        }
    }

    /// <summary>Binds an unbound heap cell in the image, with the engine's
    /// trail rule (below HB gets trailed).</summary>
    private void BindImage(int addr, Cell value)
    {
        WriteHeap(addr, value);
        if (addr < (int)GetSlot(WasmAbi.HeapBacktrack))
        {
            int tr = (int)GetSlot(WasmAbi.TrailTop);
            Marshal.WriteInt32(_memory.Start, TrailAt + tr * 4, addr);
            SetSlot(WasmAbi.TrailTop, tr + 1);
        }
    }

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
