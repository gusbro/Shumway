using System.Collections.Generic;
using System.Reflection;
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
    private static bool TryEmitIntArithSequence(Sigil.Emit<PredicateDelegate> emit,
        byte[] code, int start, Sigil.Label failLabel, out int end)
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

        var slow = emit.DefineLabel($"iar_slow_{NextLabelSeq()}");
        var done = emit.DefineLabel($"iar_done_{NextLabelSeq()}");
        EmitArithWakeFlush(emit, failLabel);
        var slots = new List<Sigil.Local>(maxDepth);
        for (int i = 0; i < maxDepth; i++) slots.Add(emit.DeclareLocal<long>());
        var data = emit.DeclareLocal<long>();

        depth = 0;
        for (pc = start; pc <= last; pc += OpcodeTable.Get((Opcode)code[pc]).Size)
        {
            var op = (Opcode)code[pc];
            int a1 = BytecodeIO.ReadInt32(code, pc + 1);
            switch (op)
            {
                case Opcode.AEvalPush:
                    EmitReadIntOperand(emit, a1, BytecodeIO.ReadInt32(code, pc + 5),
                        slots[depth], data, slow);
                    depth++;
                    break;
                case Opcode.AEvalBin:
                {
                    var a = slots[depth - 2];
                    var b = slots[depth - 1];
                    if ((BinOp)a1 == BinOp.Mul)
                    {
                        // Factors within 31 bits keep the product within 62,
                        // so the 64-bit multiply cannot wrap.
                        EmitBranchUnlessFitsBits(emit, a, 31, slow);
                        EmitBranchUnlessFitsBits(emit, b, 31, slow);
                    }
                    emit.LoadLocal(a);
                    emit.LoadLocal(b);
                    if ((BinOp)a1 == BinOp.Add) emit.Add();
                    else if ((BinOp)a1 == BinOp.Sub) emit.Subtract();
                    else emit.Multiply();
                    emit.StoreLocal(a);
                    EmitBranchUnlessFitsBits(emit, a, 60, slow);
                    depth--;
                    break;
                }
                case Opcode.AEvalUn:
                    if ((UnOp)a1 == UnOp.Neg)
                    {
                        var a = slots[depth - 1];
                        emit.LoadLocal(a);
                        emit.Negate();
                        emit.StoreLocal(a);
                        EmitBranchUnlessFitsBits(emit, a, 60, slow);
                    }
                    break;
                case Opcode.AEvalCmp:
                    emit.LoadLocal(slots[0]);
                    emit.LoadLocal(slots[1]);
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
                    emit.LoadArgument(0);
                    emit.LoadConstant(target);
                    emit.LoadConstant((long)Tag.Int << Cell.TagShift);
                    emit.LoadLocal(slots[0]);
                    emit.LoadConstant(Cell.PayloadMask);
                    emit.And();
                    emit.Or();
                    emit.NewObject(CellCtor);
                    switch (a1)
                    {
                        case 5: EmitHelperCall(emit, EngineSetRegisterMethod); break;
                        case 6: EmitHelperCall(emit, EngineSetYMethod); break;
                        case 4:
                            EmitHelperCall(emit, EngineUnifyPermanentMethod);
                            emit.BranchIfFalse(failLabel);
                            break;
                        default:
                            EmitHelperCall(emit, EngineUnifyMethod);
                            emit.BranchIfFalse(failLabel);
                            break;
                    }
                    break;
                }
            }
        }
        emit.Branch(done);

        emit.MarkLabel(slow);
        for (pc = start; pc <= last; pc += OpcodeTable.Get((Opcode)code[pc]).Size)
            EmitStackArithOp(emit, code, pc, failLabel);
        emit.MarkLabel(done);
        return true;
    }

    /// <summary>Reads an operand (kind 0 int literal, 3 X register, 4 Y slot)
    /// into <paramref name="dest"/> as a 60-bit integer, or branches to
    /// <paramref name="slow"/> when it is not an Int cell after deref.</summary>
    private static void EmitReadIntOperand(Sigil.Emit<PredicateDelegate> emit,
        int kind, int val, Sigil.Local dest, Sigil.Local data, Sigil.Label slow)
    {
        if (kind == 0)
        {
            emit.LoadConstant((long)val);
            emit.StoreLocal(dest);
            return;
        }
        var notRef = emit.DefineLabel($"iar_notref_{NextLabelSeq()}");
        emit.LoadArgument(0);
        emit.LoadConstant(val);
        EmitHelperCall(emit, kind == 4 ? EngineGetYMethod : EngineGetRegisterMethod);
        emit.LoadField(CellDataField);
        emit.StoreLocal(data);
        // Tag.Ref is 0: any other tag needs no deref.
        emit.LoadLocal(data);
        emit.LoadConstant(Cell.TagShift);
        emit.UnsignedShiftRight();
        emit.LoadConstant(0L);
        emit.UnsignedBranchIfNotEqual(notRef);
        emit.LoadArgument(0);
        emit.LoadArgument(0);
        emit.LoadLocal(data);
        emit.Convert<int>();
        EmitHelperCall(emit, EngineDerefMethod);
        EmitHelperCall(emit, EngineGetHeapMethod);
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
    private static void EmitBranchUnlessFitsBits(Sigil.Emit<PredicateDelegate> emit,
        Sigil.Local v, int bits, Sigil.Label slow)
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
    private static void EmitStackArithOp(Sigil.Emit<PredicateDelegate> emit,
        byte[] code, int pc, Sigil.Label failLabel)
    {
        var op = (Opcode)code[pc];
        switch (op)
        {
            case Opcode.AEvalPush:
            {
                // ADR-049: fire a pending wake before an operand variable is
                // read, but only at the start of the expression (empty eval
                // stack), since the drain runs nested arithmetic on this same
                // static stack.
                EmitArithWakeFlush(emit, failLabel);
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
                break;
            case Opcode.AEvalUn:
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 1));
                EmitHelperCall(emit, ArithUnMethod);
                break;
            case Opcode.AEvalIs:
            {
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
                emit.LoadConstant(BytecodeIO.ReadInt32(code, pc + 1));
                EmitHelperCall(emit, ArithCmpMethod);
                emit.BranchIfFalse(failLabel);
                break;
            default:
                throw new System.InvalidOperationException($"not an a_eval op: {op}");
        }
    }
}
