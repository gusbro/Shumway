using System.Collections.Generic;
using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;
using BinOp = Shumway.Builtins.ArithmeticEvaluator.BinOp;
using RelOp = Shumway.Builtins.ArithmeticEvaluator.RelOp;
using UnOp = Shumway.Builtins.ArithmeticEvaluator.UnOp;

namespace Shumway.Compiler.Il;

// An a_eval sequence evaluated on method locals, as the wasm tier evaluates it
// on module locals (EmitAEvalPush and friends): the RPN stack is resolved at
// compile time, so the integer lane needs no ArithEvalStack call and no
// [ThreadStatic] access. In a large region the JIT runs out of inlining budget
// and every ArithEvalStack op became a call plus a thread-static helper call.
// A non-Int operand, an op without an integer form, or a result outside 60
// bits runs the whole sequence again through ArithEvalStack; nothing in a
// sequence has an effect before its is/cmp, so the rerun is invisible.
public sealed partial class IlPredicateCompiler
{
    private static readonly FieldInfo CellDataField =
        typeof(Cell).GetField(nameof(Cell.Data))!;
    private static readonly ConstructorInfo CellCtor =
        typeof(Cell).GetConstructor(new[] { typeof(long) })!;
    private static readonly MethodInfo EngineUnifyPermanentMethod =
        typeof(Activation).GetMethod(nameof(Activation.UnifyPermanentWithCell),
            new[] { typeof(int), typeof(Cell) })!;

    /// <summary>Whether a_int_bin and a_int_cmp run on method locals as the
    /// a_eval sequences do (<see cref="TryEmitFusedBinLane"/>), with FusedBin
    /// and FusedCmp as the slow lane. An off switch for measuring it.</summary>
    internal static bool InlineFusedArith { get; set; } =
        Environment.GetEnvironmentVariable("SHUMWAY_IL_INLINE_FUSED") != "0";

    /// <summary>a_int_bin and a_int_cmp sites emitted on method locals, over
    /// every method compiled in this process.</summary>
    public static int InlineFusedSites;

    /// <summary>Emits the a_eval sequence starting at <paramref name="start"/>
    /// on method locals, with the ArithEvalStack form as its slow path.
    /// Declines (returns false, emits nothing) unless the sequence is well
    /// formed from here and every op has an integer form; a push in the middle
    /// of a sequence never is, since the operands before it are missing.</summary>
    private static bool TryEmitIntArithSequence(IlEmit emit,
        byte[] code, int start, IlLabel failLabel, out int end)
    {
        end = start;
        int pc = start, depth = 0, maxDepth = 0;
        // The constants of the RPN stack: a literal zero divisor is the
        // evaluator's error, not a lane's, and the sequence declines it.
        var consts = new List<long?>();
        while (true)
        {
            if (pc >= code.Length) return false;
            var op = (Opcode)code[pc];
            if (op == Opcode.AEvalPush)
            {
                int kind = BytecodeIO.ReadInt32(code, pc + 1);
                if (kind is not (0 or 3 or 4)) return false;
                consts.Add(kind == 0 ? BytecodeIO.ReadInt32(code, pc + 5) : null);
                depth++;
                if (depth > maxDepth) maxDepth = depth;
            }
            else if (op == Opcode.AEvalBin)
            {
                var bin = (BinOp)BytecodeIO.ReadInt32(code, pc + 1);
                if (!ArithBounds.IsLaneBinOp(bin)) return false;
                if (depth < 2) return false;
                long? ac = consts[^2], bc = consts[^1];
                if (bin is BinOp.IntDiv or BinOp.Mod && bc == 0) return false;
                consts.RemoveRange(consts.Count - 2, 2);
                consts.Add(ac is { } av && bc is { } bv ? FoldInt(bin, av, bv) : null);
                depth--;
            }
            else if (op == Opcode.AEvalUn)
            {
                var un = (UnOp)BytecodeIO.ReadInt32(code, pc + 1);
                if (!ArithBounds.IsLaneUnOp(un)) return false;
                if (depth < 1) return false;
                if (un != UnOp.Pos) consts[^1] = consts[^1] is { } c ? FoldUn(un, c) : null;
            }
            else if (op == Opcode.AEvalCmp)
            {
                if (depth != 2) return false;
                break;
            }
            else if (op == Opcode.AEvalIs)
            {
                if (depth != 1) return false;
                if (BytecodeIO.ReadInt32(code, pc + 1) is not (3 or 4 or 5 or 6)) return false;
                break;
            }
            else return false;
            pc += OpcodeTable.Get(op).Size;
        }
        int last = pc;
        end = last + OpcodeTable.Get((Opcode)code[last]).Size;

        // With every operand within `width` bits no value the sequence computes
        // leaves 64 bits: one check per operand replaces one per operation, and
        // a comparison compares exact values. 0: a check per operation.
        int width = OperandWidth(code, start, last, out bool resultFits);
        var slow = emit.DefineLabel($"iar_slow_{NextLabelSeq()}");
        var done = emit.DefineLabel($"iar_done_{NextLabelSeq()}");
        if (WakePoints)
            EmitArithWakePoint(emit, failLabel, start, ArithGoalOperands(code, start, out int live), live);
        else if (!SkipWakeTest())
            EmitArithWakeFlush(emit, failLabel);
        var data = emit.DeclareLocal<long>();
        // The RPN stack, resolved at compile time: a constant, an operand's
        // local (read once per sequence: nothing in a sequence binds), or a
        // temporary holding an intermediate result.
        var stack = new List<ArithValue>(maxDepth);
        var operands = new Dictionary<(int, int), IlLocal>();
        var freeTemps = new Stack<IlLocal>();
        IlLocal Temp() => freeTemps.Count > 0 ? freeTemps.Pop() : emit.DeclareLocal<long>();
        void Release(ArithValue v) { if (v.IsTemp) freeTemps.Push(v.Local!); }
        void Load(ArithValue v)
        {
            if (v.Local is { } l) emit.LoadLocal(l);
            else emit.LoadConstant(v.Constant);
        }

        for (pc = start; pc <= last; pc += OpcodeTable.Get((Opcode)code[pc]).Size)
        {
            var op = (Opcode)code[pc];
            int a1 = BytecodeIO.ReadInt32(code, pc + 1);
            switch (op)
            {
                case Opcode.AEvalPush:
                {
                    int val = BytecodeIO.ReadInt32(code, pc + 5);
                    if (a1 == 0)
                    {
                        stack.Add(ArithValue.Of(val));
                        break;
                    }
                    if (!operands.TryGetValue((a1, val), out var loc))
                    {
                        loc = emit.DeclareLocal<long>();
                        EmitReadIntOperand(emit, a1, val, loc, data, slow);
                        if (width is > 0 and < Cell.TagShift) EmitBranchUnlessFitsBits(emit, loc, width, slow);
                        operands[(a1, val)] = loc;
                    }
                    stack.Add(new ArithValue(loc, 0, IsTemp: false));
                    break;
                }
                case Opcode.AEvalBin:
                {
                    var b = stack[^1];
                    var a = stack[^2];
                    stack.RemoveRange(stack.Count - 2, 2);
                    stack.Add(EmitLaneBinary(emit, (BinOp)a1, a, b, width, slow, Load, Temp, Release));
                    break;
                }
                case Opcode.AEvalUn:
                {
                    var un = (UnOp)a1;
                    if (un == UnOp.Pos) break;
                    var a = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    if (a.Local is null && FoldUn(un, a.Constant) is { } folded)
                    {
                        stack.Add(ArithValue.Of(folded));
                        break;
                    }
                    var r = Temp();
                    EmitLaneUnOp(emit, un, a, r, Load);
                    Release(a);
                    // A negation or an absolute value can leave 60 bits: -(-2^59).
                    if (un is UnOp.Neg or UnOp.Abs && width == 0) EmitBranchUnlessFitsBits(emit, r, 60, slow);
                    stack.Add(new ArithValue(r, 0, IsTemp: true));
                    break;
                }
                case Opcode.AEvalCmp:
                    EmitLaneCompare(emit, (RelOp)a1, stack[0], stack[1], failLabel, Load);
                    break;
                case Opcode.AEvalIs:
                {
                    var value = stack[0];
                    if (width > 0 && !resultFits && value.Local is { } result)
                        EmitBranchUnlessFitsBits(emit, result, Cell.TagShift, slow);
                    EmitDeliverInt(emit, a1, BytecodeIO.ReadInt32(code, pc + 5), value, failLabel, Load);
                    break;
                }
            }
        }
        emit.Branch(done);

        emit.MarkLabel(slow);
        // ADR-061: in a hot method the slow lane is the cold method's, from
        // the boundary before the sequence: nothing in it has an effect yet.
        if (EmitColdExit(emit))
        {
            emit.MarkLabel(done);
            return true;
        }
        // The fast lane fired the pending wakes before its first read, and
        // reading operands binds nothing: no wake check here.
        for (pc = start; pc <= last; pc += OpcodeTable.Get((Opcode)code[pc]).Size)
            EmitStackArithOp(emit, code, pc, failLabel, wakesFlushed: true);
        emit.MarkLabel(done);
        return true;
    }

    /// <summary>The fused <c>T is A op B</c> on method locals: the operands
    /// read as the sequence lane reads them, the operation with its checks,
    /// the result delivered by the target's kind. A non-integer operand, a
    /// zero divisor or a result past 60 bits runs FusedBin through a call
    /// that inlines nothing (FusedBinCold): as an inline, FusedBin cost its
    /// site some forty of the method's locals. False when the operator has
    /// no integer form or the method holds no register file.</summary>
    private static bool TryEmitFusedBinLane(IlEmit emit, byte[] code, int pc, IlLabel failLabel)
    {
        int packed = BytecodeIO.ReadInt32(code, pc + 1);
        var bin = (BinOp)((packed >> 24) & 0xFF);
        int aKind = packed & 0xFF, bKind = (packed >> 8) & 0xFF, tKind = (packed >> 16) & 0xFF;
        int aVal = BytecodeIO.ReadInt32(code, pc + 5);
        int bVal = BytecodeIO.ReadInt32(code, pc + 9);
        int tVal = BytecodeIO.ReadInt32(code, pc + 13);
        if (!InlineFusedArith || !ArithBounds.IsLaneBinOp(bin) || RegisterFileOf(emit) is not { } rf) return false;
        if (aKind is not (0 or 3 or 4) || bKind is not (0 or 3 or 4) || tKind is not (3 or 4 or 5 or 6)) return false;
        // A literal zero divisor is the evaluator's error, not a lane.
        if (bin is BinOp.IntDiv or BinOp.Mod && bKind == 0 && bVal == 0) return false;
        System.Threading.Interlocked.Increment(ref InlineFusedSites);
        int n = NextLabelSeq();
        var slow = emit.DefineLabel($"fbl_slow_{n}");
        var done = emit.DefineLabel($"fbl_done_{n}");
        var data = rf.Temp(typeof(long), KU + 5);
        var temps = new Stack<IlLocal>();
        temps.Push(rf.Temp(typeof(long), KU + 4));
        temps.Push(rf.Temp(typeof(long), KU + 3));
        temps.Push(rf.Temp(typeof(long), KU + 2));
        IlLocal Temp() => temps.Pop();
        void Release(ArithValue v) { if (v.IsTemp) temps.Push(v.Local!); }
        void Load(ArithValue v)
        {
            if (v.Local is { } l) emit.LoadLocal(l);
            else emit.LoadConstant(v.Constant);
        }
        var a = EmitFusedOperand(emit, aKind, aVal, Temp, data, slow);
        var b = aKind != 0 && aKind == bKind && aVal == bVal ? a : EmitFusedOperand(emit, bKind, bVal, Temp, data, slow);
        var r = EmitLaneBinary(emit, bin, a, b, 0, slow, Load, Temp, Release);
        EmitDeliverInt(emit, tKind, tVal, r, failLabel, Load);
        emit.Branch(done);
        emit.MarkLabel(slow);
        emit.LoadArgument(0);
        emit.LoadConstant((int)bin);
        emit.LoadConstant(aKind);
        emit.LoadConstant(aVal);
        emit.LoadConstant(bKind);
        emit.LoadConstant(bVal);
        emit.LoadConstant(tKind);
        emit.LoadConstant(tVal);
        EmitHelperCall(emit, ArithFusedBinColdMethod);
        emit.BranchIfFalse(failLabel);
        emit.MarkLabel(done);
        return true;
    }

    /// <summary>The fused <c>A rel B</c> on method locals, FusedCmp through
    /// FusedCmpCold as the slow lane (see <see cref="TryEmitFusedBinLane"/>).</summary>
    private static bool TryEmitFusedCmpLane(IlEmit emit, byte[] code, int pc, IlLabel failLabel)
    {
        int packed = BytecodeIO.ReadInt32(code, pc + 1);
        var rel = (RelOp)((packed >> 16) & 0xFF);
        int aKind = packed & 0xFF, bKind = (packed >> 8) & 0xFF;
        int aVal = BytecodeIO.ReadInt32(code, pc + 5);
        int bVal = BytecodeIO.ReadInt32(code, pc + 9);
        if (!InlineFusedArith || RegisterFileOf(emit) is not { } rf) return false;
        if (aKind is not (0 or 3 or 4) || bKind is not (0 or 3 or 4)) return false;
        System.Threading.Interlocked.Increment(ref InlineFusedSites);
        int n = NextLabelSeq();
        var slow = emit.DefineLabel($"fcl_slow_{n}");
        var done = emit.DefineLabel($"fcl_done_{n}");
        var data = rf.Temp(typeof(long), KU + 5);
        var temps = new Stack<IlLocal>();
        temps.Push(rf.Temp(typeof(long), KU + 3));
        temps.Push(rf.Temp(typeof(long), KU + 2));
        IlLocal Temp() => temps.Pop();
        void Load(ArithValue v)
        {
            if (v.Local is { } l) emit.LoadLocal(l);
            else emit.LoadConstant(v.Constant);
        }
        var a = EmitFusedOperand(emit, aKind, aVal, Temp, data, slow);
        var b = aKind != 0 && aKind == bKind && aVal == bVal ? a : EmitFusedOperand(emit, bKind, bVal, Temp, data, slow);
        EmitLaneCompare(emit, rel, a, b, failLabel, Load);
        emit.Branch(done);
        emit.MarkLabel(slow);
        emit.LoadArgument(0);
        emit.LoadConstant((int)rel);
        emit.LoadConstant(aKind);
        emit.LoadConstant(aVal);
        emit.LoadConstant(bKind);
        emit.LoadConstant(bVal);
        EmitHelperCall(emit, ArithFusedCmpColdMethod);
        emit.BranchIfFalse(failLabel);
        emit.MarkLabel(done);
        return true;
    }

    /// <summary>A fused operand as a lane value: a literal as a constant, a
    /// register or Y slot read into a temporary (<see cref="EmitReadIntOperand"/>).</summary>
    private static ArithValue EmitFusedOperand(IlEmit emit, int kind, int val,
        Func<IlLocal> temp, IlLocal data, IlLabel slow)
    {
        if (kind == 0) return ArithValue.Of(val);
        var loc = temp();
        EmitReadIntOperand(emit, kind, val, loc, data, slow);
        return new ArithValue(loc, 0, IsTemp: false);
    }

    /// <summary>a op b into a temporary of the lane. Two constants fold; +, -
    /// and * at <paramref name="width"/> 0 check their operands (a product)
    /// and their result against 60 bits, and at a width the sequence bounds
    /// check nothing; the other operators are bounded by their operands. The
    /// operands are released before the result's temporary is taken.</summary>
    private static ArithValue EmitLaneBinary(IlEmit emit, BinOp bin, ArithValue a, ArithValue b, int width,
        IlLabel slow, Action<ArithValue> load, Func<IlLocal> temp, Action<ArithValue> release)
    {
        if (a.Local is null && b.Local is null && FoldInt(bin, a.Constant, b.Constant) is { } folded)
            return ArithValue.Of(folded);
        if (bin is not (BinOp.Add or BinOp.Sub or BinOp.Mul))
        {
            // Bounded by the operands, so no fit check at a width the sequence
            // bounds; at width 0 a quotient is checked, since -(2^59) // -1
            // is the one that leaves 60 bits.
            var r2 = temp();
            EmitLaneBinOp(emit, bin, a, b, r2, slow, load);
            if (bin == BinOp.IntDiv && width == 0) EmitBranchUnlessFitsBits(emit, r2, 60, slow);
            release(a);
            release(b);
            return new ArithValue(r2, 0, IsTemp: true);
        }
        if (bin == BinOp.Mul && width == 0)
        {
            // The product must not wrap 64 bits. Factors within 31
            // bits keep it within 62; a constant factor c bounds the
            // other to 64 - bitlen(|c|) bits (none for |c| < 16, as
            // operands are 60-bit).
            if (a.Local is null) EmitFitsForConstantFactor(emit, b.Local!, a.Constant, slow);
            else if (b.Local is null) EmitFitsForConstantFactor(emit, a.Local!, b.Constant, slow);
            else
            {
                EmitBranchUnlessFitsBits(emit, a.Local!, 31, slow);
                EmitBranchUnlessFitsBits(emit, b.Local!, 31, slow);
            }
        }
        load(a);
        load(b);
        if (bin == BinOp.Add) emit.Add();
        else if (bin == BinOp.Sub) emit.Subtract();
        else emit.Multiply();
        release(a);
        release(b);
        var r = temp();
        emit.StoreLocal(r);
        if (width == 0) EmitBranchUnlessFitsBits(emit, r, 60, slow);
        return new ArithValue(r, 0, IsTemp: true);
    }

    /// <summary>a rel b of the lane, to <paramref name="failLabel"/> when false.</summary>
    private static void EmitLaneCompare(IlEmit emit, RelOp rel, ArithValue a, ArithValue b,
        IlLabel failLabel, Action<ArithValue> load)
    {
        load(a);
        load(b);
        switch (rel)
        {
            case RelOp.Eq: emit.UnsignedBranchIfNotEqual(failLabel); break;
            case RelOp.Neq: emit.BranchIfEqual(failLabel); break;
            case RelOp.Lt: emit.BranchIfGreaterOrEqual(failLabel); break;
            case RelOp.Gt: emit.BranchIfLessOrEqual(failLabel); break;
            case RelOp.Le: emit.BranchIfGreater(failLabel); break;
            default: emit.BranchIfLess(failLabel); break;
        }
    }

    /// <summary>The lane's integer <paramref name="value"/> as the is/2 target
    /// of <paramref name="kind"/>: stored into a first-occurrence register (5)
    /// or Y slot (6), unified with a register (3) or a Y slot (4).</summary>
    private static void EmitDeliverInt(IlEmit emit, int kind, int target, ArithValue value,
        IlLabel failLabel, Action<ArithValue> load)
    {
        void PushResult()
        {
            emit.LoadConstant((long)Tag.Int << Cell.TagShift);
            load(value);
            emit.LoadConstant(Cell.PayloadMask);
            emit.And();
            emit.Or();
            emit.NewObject(CellCtor);
        }
        if (kind == 5) EmitSetRegisterConst(emit, target, PushResult);
        else if (kind == 6) EmitStoreY(emit, target, PushResult);
        else
        {
            emit.LoadArgument(0);
            emit.LoadConstant(target);
            PushResult();
            EmitHelperCall(emit, kind == 4 ? EngineUnifyPermanentMethod : EngineUnifyMethod);
            emit.BranchIfFalse(failLabel);
        }
    }

    /// <summary>An entry of the compile-time RPN stack: a constant, or a local
    /// (an operand's, or a temporary's that the stack owns).</summary>
    private readonly record struct ArithValue(IlLocal? Local, long Constant, bool IsTemp)
    {
        public static ArithValue Of(long constant) => new(null, constant, false);
    }

    /// <summary>ArithBounds over the sequence's bytecode.</summary>
    private static int OperandWidth(byte[] code, int start, int last, out bool resultFits)
    {
        var seq = new List<ArithBounds.Step>();
        for (int pc = start; pc <= last; pc += OpcodeTable.Get((Opcode)code[pc]).Size)
        {
            var op = (Opcode)code[pc];
            int a2 = op is Opcode.AEvalPush or Opcode.AEvalIs ? BytecodeIO.ReadInt32(code, pc + 5) : 0;
            seq.Add(new ArithBounds.Step(op, BytecodeIO.ReadInt32(code, pc + 1), a2));
        }
        return ArithBounds.OperandWidth(seq, out resultFits);
    }

    /// <summary>a op b on two constants when the result is a 60-bit integer;
    /// null for a zero divisor, which the slow path raises.</summary>
    private static long? FoldInt(BinOp op, long a, long b)
    {
        long r;
        try
        {
            switch (op)
            {
                case BinOp.Add: r = checked(a + b); break;
                case BinOp.Sub: r = checked(a - b); break;
                case BinOp.Mul: r = checked(a * b); break;
                case BinOp.IntDiv: if (b == 0) return null; r = a / b; break;
                case BinOp.Mod:
                    if (b == 0) return null;
                    r = a % b;
                    if (r != 0 && (r ^ b) < 0) r += b;
                    break;
                case BinOp.Min: r = System.Math.Min(a, b); break;
                default: r = System.Math.Max(a, b); break;
            }
        }
        catch (System.OverflowException) { return null; }
        long lim = 1L << (Cell.TagShift - 1);
        return r >= -lim && r < lim ? r : null;
    }

    /// <summary>op a on a constant when the result is a 60-bit integer.</summary>
    private static long? FoldUn(UnOp op, long a)
    {
        long r = op switch
        {
            UnOp.Neg => -a,
            UnOp.Abs => a < 0 ? -a : a,
            UnOp.Sign => a > 0 ? 1 : a < 0 ? -1 : 0,
            _ => ~a,   // BitNot
        };
        long lim = 1L << (Cell.TagShift - 1);
        return r >= -lim && r < lim ? r : null;
    }

    /// <summary>a op b for the lane's operators beyond +, - and *, into
    /// <paramref name="r"/>: //, mod, min and max, whose results the
    /// operands bound. A zero divisor goes to <paramref name="slow"/>, where
    /// the evaluator raises; the same ISO rules as TryFastBin.</summary>
    private static void EmitLaneBinOp(IlEmit emit, BinOp op, ArithValue a, ArithValue b,
        IlLocal r, IlLabel slow, Action<ArithValue> load)
    {
        switch (op)
        {
            case BinOp.IntDiv:
            case BinOp.Mod:
            {
                // A literal divisor is never zero here: the lanes decline one.
                if (b.Local is not null)
                {
                    load(b);
                    emit.BranchIfFalse(slow);
                }
                load(a);
                load(b);
                if (op == BinOp.IntDiv)
                {
                    emit.Divide();                      // truncates toward zero, as ISO //
                    emit.StoreLocal(r);
                    break;
                }
                emit.Remainder();                        // the sign of the dividend
                emit.StoreLocal(r);
                // ISO mod takes the sign of the divisor: add it back when they differ.
                var done = emit.DefineLabel($"mod_done_{NextLabelSeq()}");
                emit.LoadLocal(r);
                emit.BranchIfFalse(done);
                emit.LoadLocal(r);
                load(b);
                emit.Xor();
                emit.LoadConstant(0L);
                emit.BranchIfGreaterOrEqual(done);
                emit.LoadLocal(r);
                load(b);
                emit.Add();
                emit.StoreLocal(r);
                emit.MarkLabel(done);
                break;
            }
            default:
            {
                // min / max: the operand the comparison picks.
                var takeB = emit.DefineLabel($"minmax_b_{NextLabelSeq()}");
                var done = emit.DefineLabel($"minmax_done_{NextLabelSeq()}");
                load(a);
                load(b);
                if (op == BinOp.Min) emit.BranchIfGreater(takeB);
                else emit.BranchIfLess(takeB);
                load(a);
                emit.StoreLocal(r);
                emit.Branch(done);
                emit.MarkLabel(takeB);
                load(b);
                emit.StoreLocal(r);
                emit.MarkLabel(done);
                break;
            }
        }
    }

    /// <summary>op a for the lane's unary operators, into <paramref name="r"/>.</summary>
    private static void EmitLaneUnOp(IlEmit emit, UnOp op, ArithValue a, IlLocal r, Action<ArithValue> load)
    {
        switch (op)
        {
            case UnOp.Neg:
                load(a);
                emit.Negate();
                emit.StoreLocal(r);
                break;
            case UnOp.Abs:
            {
                var done = emit.DefineLabel($"abs_done_{NextLabelSeq()}");
                load(a);
                emit.StoreLocal(r);
                emit.LoadLocal(r);
                emit.LoadConstant(0L);
                emit.BranchIfGreaterOrEqual(done);
                emit.LoadLocal(r);
                emit.Negate();
                emit.StoreLocal(r);
                emit.MarkLabel(done);
                break;
            }
            case UnOp.Sign:
                // (a > 0) - (a < 0)
                load(a);
                emit.LoadConstant(0L);
                emit.CompareGreaterThan();
                load(a);
                emit.LoadConstant(0L);
                emit.CompareLessThan();
                emit.Subtract();
                emit.Convert<long>();
                emit.StoreLocal(r);
                break;
            default:   // BitNot
                load(a);
                emit.Not();
                emit.StoreLocal(r);
                break;
        }
    }

    /// <summary>Branches to <paramref name="slow"/> unless <paramref name="v"/>
    /// times the constant <paramref name="c"/> stays within 64 bits.</summary>
    private static void EmitFitsForConstantFactor(IlEmit emit,
        IlLocal v, long c, IlLabel slow)
    {
        ulong m = c == long.MinValue ? 1UL << 63 : (ulong)System.Math.Abs(c);
        int bitlen = 0;
        while (m != 0) { bitlen++; m >>= 1; }
        int bits = 64 - bitlen;
        if (bits >= Cell.TagShift) return;   // a 60-bit operand cannot wrap
        EmitBranchUnlessFitsBits(emit, v, bits, slow);
    }

    /// <summary>Reads an operand (kind 0 int literal, 3 X register, 4 Y slot)
    /// into <paramref name="dest"/> as a 60-bit integer, or branches to
    /// <paramref name="slow"/> when it is not an Int cell after deref.</summary>
    private static void EmitReadIntOperand(IlEmit emit,
        int kind, int val, IlLocal dest, IlLocal data, IlLabel slow)
    {
        if (kind == 0)
        {
            emit.LoadConstant((long)val);
            emit.StoreLocal(dest);
            return;
        }
        var notRef = emit.DefineLabel($"iar_notref_{NextLabelSeq()}");
        if (kind == 4) EmitLoadY(emit, val);
        else
        {
            emit.LoadArgument(0);
            emit.LoadConstant(val);
            EmitHelperCall(emit, EngineGetRegisterMethod);
        }
        emit.LoadField(CellDataField);
        emit.StoreLocal(data);
        // Tag.Ref is 0: any other tag needs no deref.
        emit.LoadLocal(data);
        emit.LoadConstant(Cell.TagShift);
        emit.UnsignedShiftRight();
        emit.LoadConstant(0L);
        emit.UnsignedBranchIfNotEqual(notRef);
        // One step: the cell the reference names. An Int there is the value;
        // anything else (an unbound variable, a further reference) is the
        // slow lane's, which dereferences fully.
        EmitLoadEngineField(emit, EngHeap);
        emit.LoadLocal(data);
        emit.Convert<int>();
        emit.LoadElement<Cell>();
        emit.LoadField(CellDataField);
        emit.StoreLocal(data);
        emit.MarkLabel(notRef);
        emit.LoadLocal(data);
        emit.LoadConstant(Cell.TagShift);
        emit.UnsignedShiftRight();
        emit.LoadConstant((long)Tag.Int);
        emit.UnsignedBranchIfNotEqual(slow);
        // Sign-extend the 60-bit payload.
        emit.LoadLocal(data);
        emit.LoadConstant(64 - Cell.TagShift);
        emit.ShiftLeft();
        emit.LoadConstant(64 - Cell.TagShift);
        emit.ShiftRight();
        emit.StoreLocal(dest);
    }

    /// <summary>Branches to <paramref name="slow"/> unless the long in
    /// <paramref name="v"/> is representable in <paramref name="bits"/> signed
    /// bits.</summary>
    private static void EmitBranchUnlessFitsBits(IlEmit emit,
        IlLocal v, int bits, IlLabel slow)
    {
        emit.LoadLocal(v);
        emit.LoadConstant(64 - bits);
        emit.ShiftLeft();
        emit.LoadConstant(64 - bits);
        emit.ShiftRight();
        emit.LoadLocal(v);
        emit.UnsignedBranchIfNotEqual(slow);
    }

    /// <summary>One a_eval op through ArithEvalStack: the form every sequence
    /// had before, and the slow path of the integer lane.</summary>
    private static void EmitStackArithOp(IlEmit emit,
        byte[] code, int pc, IlLabel failLabel, bool wakesFlushed = false)
    {
        var op = (Opcode)code[pc];
        switch (op)
        {
            case Opcode.AEvalPush:
            {
                // ADR-049: fire a pending wake before an operand variable is
                // read, but only at the start of the expression (empty eval
                // stack), since the drain runs nested arithmetic on this same
                // static stack; with continuation methods, an interrupt that
                // re-runs the goal from that start.
                if (!wakesFlushed && WakePoints)
                {
                    if (_arithDepth == 0)
                        EmitArithWakePoint(emit, failLabel, pc, ArithGoalOperands(code, pc, out int live), live);
                }
                else if (!wakesFlushed) EmitArithWakeFlush(emit, failLabel);
                _arithDepth++;
                int kind = BytecodeIO.ReadInt32(code, pc + 1);
                int operand = BytecodeIO.ReadInt32(code, pc + 5);
                if (kind == 0)
                {
                    emit.LoadConstant((long)operand);
                    EmitHelperCall(emit, ArithPushIntMethod);
                }
                else
                {
                    emit.LoadArgument(0);
                    emit.LoadConstant(operand);
                    EmitHelperCall(emit, kind == 4 ? ArithPushYMethod : ArithPushRegMethod);
                }
                break;
            }
            case Opcode.AEvalBin:
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 1));
                emit.LoadArgument(0);
                EmitHelperCall(emit, PreferRationalsGetter);
                EmitHelperCall(emit, ArithBinMethod);
                _arithDepth--;
                break;
            case Opcode.AEvalUn:
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 1));
                EmitHelperCall(emit, ArithUnMethod);
                break;
            case Opcode.AEvalIs:
            {
                _arithDepth = 0;
                int kind = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 5));
                switch (kind)
                {
                    case 5: EmitHelperCall(emit, ArithSetRegMethod); break;     // first-occurrence X
                    case 6: EmitHelperCall(emit, ArithSetPermMethod); break;    // first-occurrence Y
                    case 4:                                          // bound Y
                        EmitHelperCall(emit, ArithIsPermMethod);
                        emit.BranchIfFalse(failLabel);
                        break;
                    default:                                         // bound X
                        EmitHelperCall(emit, ArithIsRegMethod);
                        emit.BranchIfFalse(failLabel);
                        break;
                }
                break;
            }
            case Opcode.AEvalCmp:
                _arithDepth = 0;
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 1));
                EmitHelperCall(emit, ArithCmpMethod);
                emit.BranchIfFalse(failLabel);
                break;
            default:
                throw new System.InvalidOperationException($"not an a_eval op: {op}");
        }
    }
}
