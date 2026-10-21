using System;
using System.Collections.Generic;
using System.Reflection;
using Shumway.Core;

namespace Shumway.Compiler.Il;

// ADR-063, heads: the read-only form of a matching stream under an image.
// After get_blit the argument is bound, and after a part's skip the part's
// cell is whatever the heap holds: the stream never builds. Its
// instructions are emitted for read mode alone, the unify pointer in a
// local and no mode test; a nested cell that comes unbound is bound to a
// copy of its block of the image, which the stream then reads. The form
// spends basic blocks where the helpers spent calls, and the planner
// counts them (IlJitLimits.BasicBlocks).
// The emitter maps the instructions to the image's cells by walking both
// in the order the streams run (ReadPlan); a unit the walk cannot follow
// keeps the helpers.
public sealed partial class IlPredicateCompiler
{
    /// <summary>Whether the streams under an image are emitted in the
    /// read-only form. An off switch for measuring it.</summary>
    internal static bool ReadOnlyStreams { get; set; } =
        Environment.GetEnvironmentVariable("SHUMWAY_IL_READ_STREAM") != "0";

    /// <summary>The units emitted in the read-only form, over the process.</summary>
    internal static int ReadStreamUnits;

    private static readonly MethodInfo IlReadNestedHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.ReadNested))!;
    private static readonly MethodInfo IlReadNestedGlobalHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.ReadNestedGlobal))!;
    private static readonly MethodInfo IlReadNestedListHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.ReadNestedList))!;
    private static readonly MethodInfo IlReadNestedListGlobalHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.ReadNestedListGlobal))!;
    private static readonly MethodInfo EngineUnifyVariableYAtMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyVariableYAt))!;
    private static readonly MethodInfo EngineUnifyPermanentWithHeapAtMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyPermanentWithHeapAt), new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo EngineEnsureRegistersMethod =
        typeof(Activation).GetMethod(nameof(Activation.EnsureRegisters))!;

    /// <summary>One stream under an image: the term's register, the image
    /// and the block the stream matches, the instructions' range, and the
    /// locals the form keeps while it runs.</summary>
    private sealed class ReadUnit
    {
        public int PrefixPc;
        public int ImageId;
        public TermImage Image = null!;
        public int Offset;
        public bool List;
        public int Reg;
        public int StartPc;
        public int EndPc;
        /// <summary>The highest X register the unit reads or writes, -1 for none.</summary>
        public int MaxX = -1;
        public IlLabel? End;
        public IlLocal Regs = null!, Heap = null!, S = null!, Bits = null!, P = null!;
    }

    /// <summary>The units of one head and the nested blocks of their inline
    /// chains, by the pc of the prefix and of the unify_structure or
    /// unify_list.</summary>
    private sealed class ReadPlan
    {
        public byte[] Code = null!;
        public object? Matcher;
        public int From, To;
        public readonly Dictionary<int, ReadUnit> Units = new();
        public readonly Dictionary<int, (int Offset, bool List)> Chains = new();
    }

    [ThreadStatic] private static ReadPlan? _readPlan;
    [ThreadStatic] private static ReadUnit? _readUnit;

    private static bool ReadFormAvailable(IlEmit emit)
        => ReadOnlyStreams && !CpsHotExit && _ilImagePool is not null && RegisterFileOf(emit) is not null;

    // The plan of the head that holds pc: computed from the first prefix met
    // in a head and kept while the emission stays in its range and the
    // matcher's sites are the same (a compaction replaces them).
    private static ReadPlan PlanAt(byte[] code, int pc)
    {
        var plan = _readPlan;
        var matcher = IlRegionCost.MatcherSites(code);
        if (plan is null || !ReferenceEquals(plan.Code, code) || !ReferenceEquals(plan.Matcher, matcher)
            || pc < plan.From || pc >= plan.To)
            _readPlan = plan = PlanReadStreams(code, pc, matcher);
        return plan;
    }

    // The walk, over the code from the first prefix to the end: a unit
    // begins at a prefix (get_blit for an argument, unify_blit off the
    // matcher or skip_if_copied for a part whose temporary an earlier unit
    // assigned) and runs to the prefix's target; inside it every
    // instruction takes the image's next argument cell, a unify_structure
    // or unify_list the block that cell names. A unit whose instructions do
    // not follow the image is dropped, and keeps the helpers. An
    // instruction of a body ends a head: the next clause's temporaries are
    // its own.
    private static ReadPlan PlanReadStreams(byte[] code, int from, ISet<int> matcher)
    {
        var plan = new ReadPlan { Code = code, From = from, Matcher = matcher, To = code.Length };
        var pool = _ilImagePool!;
        var partOf = new Dictionary<int, (int ImageId, int Offset, bool List)>();
        ReadUnit? unit = null;
        int cursor = 0;
        int pc = from;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Meta) { pc += 6; continue; }
            int size = OpcodeTable.Get(op).Size;
            if (!IsHeadStreamOp(op))
            {
                if (unit is not null) unit = Drop(plan, unit);
                partOf.Clear();
                pc += size;
                continue;
            }
            if (unit is not null && pc >= unit.EndPc) unit = null;
            switch (op)
            {
                case Opcode.GetBlit:
                {
                    int imageId = BytecodeIO.ReadInt32(code, pc + 1);
                    int reg = BytecodeIO.ReadInt32(code, pc + 5);
                    int target = BytecodeIO.ReadInt32(code, pc + 9);
                    unit = Begin(plan, pool, pc, imageId, 0, reg, pc + size, target, list: null);
                    break;
                }
                case Opcode.UnifyBlit:
                {
                    int imageId = BytecodeIO.ReadInt32(code, pc + 1);
                    int reg = BytecodeIO.ReadInt32(code, pc + 5);
                    int target = BytecodeIO.ReadInt32(code, pc + 9);
                    unit = matcher.Contains(pc) ? null
                        : Begin(plan, pool, pc, imageId, 0, reg, pc + size, target, list: null);
                    break;
                }
                case Opcode.SkipIfCopied:
                {
                    int reg = BytecodeIO.ReadInt32(code, pc + 1);
                    int target = BytecodeIO.ReadInt32(code, pc + 5);
                    unit = partOf.TryGetValue(reg, out var part)
                        ? Begin(plan, pool, pc, part.ImageId, part.Offset, reg, pc + size, target, part.List)
                        : null;
                    break;
                }
                case Opcode.GetStructure when unit is not null:
                {
                    int functorId = BytecodeIO.ReadInt32(code, pc + 1);
                    int reg = BytecodeIO.ReadInt32(code, pc + 5);
                    var cells = unit.Image.Cells;
                    if (pc != unit.StartPc || reg != unit.Reg || unit.List
                        || cells[unit.Offset].Tag != Tag.Functor || cells[unit.Offset].AsFunctorId != functorId)
                    {
                        unit = Drop(plan, unit);
                        break;
                    }
                    cursor = unit.Offset + 1;
                    break;
                }
                case Opcode.GetList when unit is not null:
                {
                    int reg = BytecodeIO.ReadInt32(code, pc + 1);
                    if (pc != unit.StartPc || reg != unit.Reg || !unit.List)
                    {
                        unit = Drop(plan, unit);
                        break;
                    }
                    cursor = unit.Offset;
                    break;
                }
                case Opcode.UnifyVariableX when unit is not null:
                {
                    int slot = BytecodeIO.ReadInt32(code, pc + 1);
                    Cell cell = unit.Image.Cells[cursor];
                    if (cell.Tag is Tag.Str or Tag.Lis)
                        partOf[slot] = (unit.ImageId, cell.AsHeapIndex, cell.Tag == Tag.Lis);
                    if (slot > unit.MaxX) unit.MaxX = slot;
                    cursor++;
                    break;
                }
                case Opcode.UnifyValueX when unit is not null:
                {
                    int slot = BytecodeIO.ReadInt32(code, pc + 1);
                    if (slot > unit.MaxX) unit.MaxX = slot;
                    cursor++;
                    break;
                }
                case Opcode.UnifyVariableY when unit is not null:
                case Opcode.UnifyValueY when unit is not null:
                case Opcode.UnifyAtom when unit is not null:
                case Opcode.UnifyInteger when unit is not null:
                case Opcode.UnifyNil when unit is not null:
                    cursor++;
                    break;
                case Opcode.UnifyVoid when unit is not null:
                    cursor += BytecodeIO.ReadInt32(code, pc + 1);
                    break;
                case Opcode.UnifyStructure when unit is not null:
                {
                    int functorId = BytecodeIO.ReadInt32(code, pc + 1);
                    var cells = unit.Image.Cells;
                    Cell cell = cells[cursor];
                    if (cell.Tag != Tag.Str || cells[cell.AsHeapIndex].AsFunctorId != functorId)
                    {
                        unit = Drop(plan, unit);
                        break;
                    }
                    plan.Chains[pc] = (cell.AsHeapIndex, false);
                    cursor = cell.AsHeapIndex + 1;
                    break;
                }
                case Opcode.UnifyList when unit is not null:
                {
                    Cell cell = unit.Image.Cells[cursor];
                    if (cell.Tag != Tag.Lis)
                    {
                        unit = Drop(plan, unit);
                        break;
                    }
                    plan.Chains[pc] = (cell.AsHeapIndex, true);
                    cursor = cell.AsHeapIndex;
                    break;
                }
                default:
                    if (unit is not null) unit = Drop(plan, unit);
                    break;
            }
            pc += size;
        }
        return plan;
    }

    // The instructions of a head: the walk keeps a clause's temporaries
    // while it sees them, and drops them at the first of a body.
    private static bool IsHeadStreamOp(Opcode op) => op is
        Opcode.GetAtom or Opcode.GetInteger or Opcode.GetFloat or Opcode.GetNil or Opcode.GetConstant
        or Opcode.GetVariableX or Opcode.GetVariableY or Opcode.GetValueX or Opcode.GetValueY
        or Opcode.GetBigInt or Opcode.GetRational or Opcode.GetPstr
        or Opcode.GetStructure or Opcode.GetList
        or Opcode.UnifyAtom or Opcode.UnifyInteger or Opcode.UnifyNil or Opcode.UnifyConstant
        or Opcode.UnifyVariableX or Opcode.UnifyVariableY or Opcode.UnifyValueX or Opcode.UnifyValueY
        or Opcode.UnifyVoid or Opcode.UnifyFloat or Opcode.UnifyBigInt or Opcode.UnifyRational
        or Opcode.UnifyStructure or Opcode.UnifyList
        or Opcode.GetBlit or Opcode.UnifyBlit or Opcode.SkipIfCopied;

    private static ReadUnit? Begin(ReadPlan plan, IReadOnlyList<TermImage> pool, int prefixPc,
        int imageId, int offset, int reg, int start, int end, bool? list)
    {
        if ((uint)imageId >= (uint)pool.Count) return null;
        var image = pool[imageId];
        var unit = new ReadUnit
        {
            PrefixPc = prefixPc, ImageId = imageId, Image = image, Offset = offset,
            List = list ?? image.Root == Tag.Lis, Reg = reg, StartPc = start, EndPc = end,
        };
        plan.Units[prefixPc] = unit;
        return unit;
    }

    private static ReadUnit? Drop(ReadPlan plan, ReadUnit unit)
    {
        plan.Units.Remove(unit.PrefixPc);
        var chains = new List<int>();
        foreach (int at in plan.Chains.Keys)
            if (at >= unit.StartPc && at < unit.EndPc) chains.Add(at);
        foreach (int at in chains) plan.Chains.Remove(at);
        return null;
    }

    /// <summary>At a prefix: the unit that follows, when the plan has one
    /// and the form applies. The prefix's own test is the caller's; the
    /// unit ends at <paramref name="end"/>, the prefix's target.</summary>
    private static bool TryBeginReadUnit(IlEmit emit, byte[] code, int pc, IlLabel end)
    {
        if (!ReadFormAvailable(emit)) return false;
        var plan = PlanAt(code, pc);
        if (!plan.Units.TryGetValue(pc, out var unit)) return false;
        var rf = RegisterFileOf(emit)!;
        unit.End = end;
        unit.Regs = rf.Temp(typeof(Cell[]), KU);
        unit.Heap = rf.Temp(typeof(Cell[]), KU + 1);
        unit.Bits = rf.Temp(typeof(long), KU + 3);
        unit.S = rf.Temp(typeof(int), KU + 3);
        unit.P = rf.Temp(typeof(int), KU + 4);
        _readUnit = unit;
        System.Threading.Interlocked.Increment(ref ReadStreamUnits);
        return true;
    }

    /// <summary>A part's prefix: the temporary holding the mark goes to
    /// the unit's end, the part being on the heap already.</summary>
    private static void EmitReadMarkTest(IlEmit emit)
    {
        var unit = _readUnit!;
        EmitLoadEngineField(emit, EngRegisters);
        emit.StoreLocal(unit.Regs);
        emit.LoadLocal(unit.Regs);
        emit.LoadConstant(unit.Reg);
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        emit.LoadConstant(Activation.CopiedMark.Data);
        emit.BranchIfEqual(unit.End!);
    }

    /// <summary>The instruction at <paramref name="pc"/> of the unit being
    /// emitted, in the read-only form; false for one the plan did not
    /// foresee.</summary>
    private static bool TryEmitReadOp(IlEmit emit, byte[] code, int pc, Opcode op, IlLabel failLabel)
    {
        var unit = _readUnit!;
        var rf = RegisterFileOf(emit)!;
        switch (op)
        {
            case Opcode.GetStructure:
                if (pc != unit.StartPc) return false;
                EmitReadPrologue(emit, unit, BytecodeIO.ReadInt32(code, pc + 1), failLabel);
                return true;
            case Opcode.GetList:
                if (pc != unit.StartPc) return false;
                EmitReadPrologue(emit, unit, -1, failLabel);
                return true;
            case Opcode.UnifyVariableX:
                EmitRegisterFromHeap(emit, unit.Regs, unit.Heap, BytecodeIO.ReadInt32(code, pc + 1), unit.S, 0, rf);
                EmitReadAdvance(emit, unit, 1);
                return true;
            case Opcode.UnifyVariableY:
                emit.LoadArgument(0);
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 1));
                emit.LoadLocal(unit.S);
                EmitHelperCall(emit, EngineUnifyVariableYAtMethod);
                EmitReadAdvance(emit, unit, 1);
                return true;
            case Opcode.UnifyValueX:
            case Opcode.UnifyValueY:
                emit.LoadArgument(0);
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 1));
                emit.LoadLocal(unit.S);
                EmitHelperCall(emit, op == Opcode.UnifyValueX
                    ? EngineUnifyRegisterWithHeapAtMethod : EngineUnifyPermanentWithHeapAtMethod);
                emit.BranchIfFalse(failLabel);
                EmitReadReload(emit, unit);
                EmitReadAdvance(emit, unit, 1);
                return true;
            case Opcode.UnifyAtom:
            {
                int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                EmitReadConstant(emit, unit, failLabel,
                    () =>
                    {
                        emit.LoadConstant((long)Tag.Atom << Cell.TagShift);
                        EmitAtomId(emit, atomId);
                        emit.Convert<uint>();
                        emit.Convert<ulong>();
                        emit.Or();
                    },
                    () =>
                    {
                        EmitAtomId(emit, atomId);
                        EmitHelperCall(emit, CellAtomMethod);
                    });
                return true;
            }
            case Opcode.UnifyInteger:
            {
                long value = BytecodeIO.ReadInt32(code, pc + 1);
                EmitReadConstant(emit, unit, failLabel,
                    () => emit.LoadConstant(Cell.Int(value).Data),
                    () =>
                    {
                        emit.LoadConstant(value);
                        EmitHelperCall(emit, CellIntMethod);
                    });
                return true;
            }
            case Opcode.UnifyNil:
                EmitReadConstant(emit, unit, failLabel,
                    () => emit.LoadConstant(Cell.Atom(AtomTable.EmptyListId).Data),
                    () =>
                    {
                        emit.LoadConstant(AtomTable.EmptyListId);
                        EmitHelperCall(emit, CellAtomMethod);
                    });
                return true;
            case Opcode.UnifyVoid:
                EmitReadAdvance(emit, unit, BytecodeIO.ReadInt32(code, pc + 1));
                return true;
            case Opcode.UnifyStructure:
            {
                if (!_readPlan!.Chains.TryGetValue(pc, out var chain)) return false;
                EmitReadCell(emit, unit, BytecodeIO.ReadInt32(code, pc + 1), chain.Offset, chain.List, failLabel);
                return true;
            }
            case Opcode.UnifyList:
            {
                if (!_readPlan!.Chains.TryGetValue(pc, out var chain)) return false;
                EmitReadCell(emit, unit, -1, chain.Offset, chain.List, failLabel);
                return true;
            }
            default:
                return false;
        }
    }

    // The unit's first instruction: the arrays in their locals, the register
    // bank wide enough for the unit's temporaries, then the term's cell as
    // the block the unit matches.
    private static void EmitReadPrologue(IlEmit emit, ReadUnit unit, int functorId, IlLabel failLabel)
    {
        EmitReadReload(emit, unit);
        if (unit.MaxX >= 0)
        {
            var wide = emit.DefineLabel($"rs_wide_{NextLabelSeq()}");
            emit.LoadConstant(unit.MaxX);
            emit.LoadLocal(unit.Regs);
            emit.LoadLength<Cell>();
            emit.Convert<int>();
            emit.BranchIfLess(wide);
            emit.LoadArgument(0);
            emit.LoadConstant(unit.MaxX + 1);
            EmitHelperCall(emit, EngineEnsureRegistersMethod);
            EmitLoadEngineField(emit, EngRegisters);
            emit.StoreLocal(unit.Regs);
            emit.MarkLabel(wide);
        }
        emit.LoadLocal(unit.Regs);
        emit.LoadConstant(unit.Reg);
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        emit.StoreLocal(unit.Bits);
        EmitReadBlock(emit, unit, functorId, unit.Offset, unit.List, failLabel);
    }

    // A nested cell of the chain: the cell at the pointer as the block it names.
    private static void EmitReadCell(IlEmit emit, ReadUnit unit, int functorId, int offset, bool list, IlLabel failLabel)
    {
        emit.LoadLocal(unit.Heap);
        emit.LoadLocal(unit.S);
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        emit.StoreLocal(unit.Bits);
        EmitReadBlock(emit, unit, functorId, offset, list, failLabel);
    }

    // The cell in Bits as the block at offset: a structure of the functor
    // (a list) sets the pointer past its functor (at its pair) inline;
    // anything else goes to the helper, which follows a reference, matches
    // a bound cell, binds an unbound one to a copy of the block the stream
    // then reads, and answers the pointer, or -1 to fail. Two labels and
    // four branches: the form's blocks count against the JIT's limit.
    private static void EmitReadBlock(IlEmit emit, ReadUnit unit, int functorId, int offset, bool list, IlLabel failLabel)
    {
        var other = emit.DefineLabel($"rs_other_{NextLabelSeq()}");
        var done = emit.DefineLabel($"rs_done_{NextLabelSeq()}");
        EmitTagIs(emit, unit.Bits, list ? Tag.Lis : Tag.Str);
        emit.BranchIfFalse(other);
        if (!list)
        {
            emit.LoadLocal(unit.Bits);
            emit.Convert<int>();
            emit.StoreLocal(unit.P);
            emit.LoadLocal(unit.Heap);
            emit.LoadLocal(unit.P);
            emit.LoadElement<Cell>();
            emit.LoadField(CellDataField);
            emit.LoadConstant((long)Tag.Functor << Cell.TagShift);
            EmitFunctorId(emit, functorId);
            emit.Convert<uint>();
            emit.Convert<ulong>();
            emit.Or();
            emit.CompareEqual();
            emit.BranchIfFalse(failLabel);
            emit.LoadLocal(unit.P);
            emit.LoadConstant(1);
            emit.Add();
            emit.StoreLocal(unit.S);
        }
        else
        {
            emit.LoadLocal(unit.Bits);
            emit.Convert<int>();
            emit.StoreLocal(unit.S);
        }
        emit.Branch(done);

        emit.MarkLabel(other);
        emit.LoadArgument(0);
        emit.LoadLocal(unit.Bits);
        if (!list) EmitFunctorId(emit, functorId);
        if (_persistPatches is null)
        {
            emit.LoadConstant(unit.ImageId);
            emit.LoadConstant(offset);
            EmitHelperCall(emit, list ? IlReadNestedListHelperMethod : IlReadNestedHelperMethod);
        }
        else
        {
            EmitImageId(emit, unit.ImageId);
            emit.LoadConstant(offset);
            EmitHelperCall(emit, list ? IlReadNestedListGlobalHelperMethod : IlReadNestedGlobalHelperMethod);
        }
        emit.StoreLocal(unit.S);
        EmitReadReload(emit, unit);
        emit.LoadLocal(unit.S);
        emit.LoadConstant(0);
        emit.BranchIfLess(failLabel);
        emit.MarkLabel(done);
    }

    // A constant's cell: equal bits go on; otherwise the helper unifies (an
    // unbound cell takes the constant, an attributed one queues its hooks, a
    // packed string's end is a nil).
    private static void EmitReadConstant(IlEmit emit, ReadUnit unit, IlLabel failLabel, Action pushBits, Action pushCell)
    {
        var same = emit.DefineLabel($"rs_same_{NextLabelSeq()}");
        emit.LoadLocal(unit.Heap);
        emit.LoadLocal(unit.S);
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        pushBits();
        emit.BranchIfEqual(same);
        emit.LoadArgument(0);
        emit.LoadLocal(unit.S);
        pushCell();
        EmitHelperCall(emit, EngineUnifyHeapWithCellMethod);
        emit.BranchIfFalse(failLabel);
        EmitReadReload(emit, unit);
        emit.MarkLabel(same);
        EmitReadAdvance(emit, unit, 1);
    }

    // A unit the plan foresaw and the emission could not follow: a bug in
    // one or the other; the message carries both.
    private static string DescribeReadFailure(byte[] code, int pc, Opcode op)
    {
        var unit = _readUnit!;
        var plan = _readPlan;
        var sb = new System.Text.StringBuilder();
        sb.Append($"The read-only stream has no form for {op} at pc={pc}: unit prefix={unit.PrefixPc} start={unit.StartPc} end={unit.EndPc} image={unit.ImageId} offset={unit.Offset} list={unit.List}");
        if (plan is not null)
        {
            sb.Append($"; plan [{plan.From}, {plan.To}) units=");
            sb.Append(string.Join(",", plan.Units.Keys));
            sb.Append(" chains=");
            foreach (var kv in plan.Chains) sb.Append($"{kv.Key}:{kv.Value.Offset}{(kv.Value.List ? "L" : "S")},");
        }
        sb.Append("; ops=");
        for (int p = unit.PrefixPc; p < unit.EndPc && p < code.Length;)
        {
            var o = (Opcode)code[p];
            sb.Append($"{p}:{o} ");
            p += o == Opcode.Meta ? 6 : OpcodeTable.Get(o).Size;
        }
        return sb.ToString();
    }

    private static void EmitReadAdvance(IlEmit emit, ReadUnit unit, int cells)
    {
        emit.LoadLocal(unit.S);
        emit.LoadConstant(cells);
        emit.Add();
        emit.StoreLocal(unit.S);
    }

    // After a helper that may grow the heap or the register bank.
    private static void EmitReadReload(IlEmit emit, ReadUnit unit)
    {
        EmitLoadEngineField(emit, EngRegisters);
        emit.StoreLocal(unit.Regs);
        EmitLoadEngineField(emit, EngHeap);
        emit.StoreLocal(unit.Heap);
    }
}
