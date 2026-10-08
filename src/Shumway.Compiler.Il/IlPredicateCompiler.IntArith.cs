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
        while (true)
        {
            if (pc >= code.Length) return false;
            var op = (Opcode)code[pc];
            if (op == Opcode.AEvalPush)
            {
                if (BytecodeIO.ReadInt32(code, pc + 1) is not (0 or 3 or 4)) return false;
                depth++;
                if (depth > maxDepth) maxDepth = depth;
            }
            else if (op == Opcode.AEvalBin)
            {
                if ((BinOp)BytecodeIO.ReadInt32(code, pc + 1)
                    is not (BinOp.Add or BinOp.Sub or BinOp.Mul)) return false;
                if (depth < 2) return false;
                depth--;
            }
            else if (op == Opcode.AEvalUn)
            {
                if ((UnOp)BytecodeIO.ReadInt32(code, pc + 1)
                    is not (UnOp.Neg or UnOp.Pos)) return false;
                if (depth < 1) return false;
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
        else
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
                    var bin = (BinOp)a1;
                    if (a.Local is null && b.Local is null && FoldInt(bin, a.Constant, b.Constant) is { } folded)
                    {
                        stack.Add(ArithValue.Of(folded));
                        break;
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
                    Load(a);
                    Load(b);
                    if (bin == BinOp.Add) emit.Add();
                    else if (bin == BinOp.Sub) emit.Subtract();
                    else emit.Multiply();
                    Release(a);
                    Release(b);
                    var r = Temp();
                    emit.StoreLocal(r);
                    if (width == 0) EmitBranchUnlessFitsBits(emit, r, 60, slow);
                    stack.Add(new ArithValue(r, 0, IsTemp: true));
                    break;
                }
                case Opcode.AEvalUn:
                    if ((UnOp)a1 == UnOp.Neg)
                    {
                        var a = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        if (a.Local is null && FoldInt(BinOp.Sub, 0, a.Constant) is { } negated)
                        {
                            stack.Add(ArithValue.Of(negated));
                            break;
                        }
                        Load(a);
                        emit.Negate();
                        Release(a);
                        var r = Temp();
                        emit.StoreLocal(r);
                        if (width == 0) EmitBranchUnlessFitsBits(emit, r, 60, slow);
                        stack.Add(new ArithValue(r, 0, IsTemp: true));
                    }
                    break;
                case Opcode.AEvalCmp:
                    Load(stack[0]);
                    Load(stack[1]);
                    switch ((RelOp)a1)
                    {
                        case RelOp.Eq: emit.UnsignedBranchIfNotEqual(failLabel); break;
                        case RelOp.Neq: emit.BranchIfEqual(failLabel); break;
                        case RelOp.Lt: emit.BranchIfGreaterOrEqual(failLabel); break;
                        case RelOp.Gt: emit.BranchIfLessOrEqual(failLabel); break;
                        case RelOp.Le: emit.BranchIfGreater(failLabel); break;
                        default: emit.BranchIfLess(failLabel); break;
                    }
                    break;
                case Opcode.AEvalIs:
                {
                    int target = BytecodeIO.ReadInt32(code, pc + 5);
                    var value = stack[0];
                    if (width > 0 && !resultFits && value.Local is { } result)
                        EmitBranchUnlessFitsBits(emit, result, Cell.TagShift, slow);
                    void PushResult()
                    {
                        emit.LoadConstant((long)Tag.Int << Cell.TagShift);
                        Load(value);
                        emit.LoadConstant(Cell.PayloadMask);
                        emit.And();
                        emit.Or();
                        emit.NewObject(CellCtor);
                    }
                    if (a1 == 5) EmitSetRegisterConst(emit, target, PushResult);
                    else if (a1 == 6) EmitStoreY(emit, target, PushResult);
                    else
                    {
                        emit.LoadArgument(0);
                        emit.LoadConstant(target);
                        PushResult();
                        EmitHelperCall(emit, a1 == 4 ? EngineUnifyPermanentMethod : EngineUnifyMethod);
                        emit.BranchIfFalse(failLabel);
                    }
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

    /// <summary>a op b on two constants when the result is a 60-bit integer.</summary>
    private static long? FoldInt(BinOp op, long a, long b)
    {
        long r;
        try
        {
            r = op switch
            {
                BinOp.Add => checked(a + b),
                BinOp.Sub => checked(a - b),
                _ => checked(a * b),
            };
        }
        catch (System.OverflowException) { return null; }
        long lim = 1L << (Cell.TagShift - 1);
        return r >= -lim && r < lim ? r : null;
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
