using System.Collections.Generic;
using Shumway.Core;
using BinOp = Shumway.Builtins.ArithmeticEvaluator.BinOp;
using UnOp = Shumway.Builtins.ArithmeticEvaluator.UnOp;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Interval bounds of an integer a_eval sequence, for the compiled tiers'
/// integer lanes: with every operand within the width it returns, no value the
/// sequence computes leaves 64 bits, so a check per operand replaces one per
/// operation and a comparison compares exact values.
/// </summary>
public static class ArithBounds
{
    /// <summary>One a_eval instruction: its opcode and its first two operands.</summary>
    public readonly struct Step
    {
        public readonly Opcode Op;
        public readonly int A1, A2;

        public Step(Opcode op, int a1, int a2)
        {
            Op = op;
            A1 = a1;
            A2 = a2;
        }
    }

    /// <summary>The widest operand width, from 60 down to 32 bits, under which
    /// no value of <paramref name="seq"/> leaves 2^62 in magnitude; 0 when there
    /// is none, or when the sequence has a float or bignum literal or an
    /// operator other than +, -, * and unary - and +. <paramref name="resultFits"/>:
    /// is/2's result then stays within 2^58, a 60-bit integer.</summary>
    public static int OperandWidth(IReadOnlyList<Step> seq, out bool resultFits)
    {
        resultFits = false;
        if (!IsIntegerSequence(seq)) return 0;
        int best = 0;
        // Narrower operands never widen a bound, so the widths that hold are a
        // prefix of 32..60.
        for (int lo = 32, hi = Cell.TagShift; lo <= hi;)
        {
            int w = (lo + hi) / 2;
            if (BoundsHold(seq, w, out bool fits))
            {
                best = w;
                resultFits = fits;
                lo = w + 1;
            }
            else hi = w - 1;
        }
        return best;
    }

    private static bool IsIntegerSequence(IReadOnlyList<Step> seq)
    {
        if (seq.Count == 0) return false;
        int depth = 0;
        for (int i = 0; i < seq.Count; i++)
        {
            var s = seq[i];
            bool last = i == seq.Count - 1;
            switch (s.Op)
            {
                case Opcode.AEvalPush:
                    if (s.A1 is not (0 or 3 or 4)) return false;
                    depth++;
                    break;
                case Opcode.AEvalBin:
                    if ((BinOp)s.A1 is not (BinOp.Add or BinOp.Sub or BinOp.Mul) || depth < 2) return false;
                    depth--;
                    break;
                case Opcode.AEvalUn:
                    if ((UnOp)s.A1 is not (UnOp.Neg or UnOp.Pos) || depth < 1) return false;
                    break;
                case Opcode.AEvalIs:
                    if (!last || depth != 1) return false;
                    break;
                case Opcode.AEvalCmp:
                    if (!last || depth != 2) return false;
                    break;
                default:
                    return false;
            }
        }
        return seq[seq.Count - 1].Op is Opcode.AEvalIs or Opcode.AEvalCmp;
    }

    /// <summary>Interval arithmetic over the sequence with every operand within
    /// <paramref name="w"/> bits. The bounds are doubles: the limits, 2^62 and
    /// 2^58, keep a factor of two below 2^63 and 2^59 to cover their rounding,
    /// so a bound that holds here holds exactly.</summary>
    private static bool BoundsHold(IReadOnlyList<Step> seq, int w, out bool resultFits)
    {
        const double Limit = 4611686018427387904d;          // 2^62
        const double ResultLimit = 288230376151711744d;     // 2^58
        double operand = System.Math.Pow(2, w - 1);
        var stack = new List<(double Lo, double Hi)>();
        resultFits = false;
        foreach (var s in seq)
        {
            (double Lo, double Hi) r;
            if (s.Op == Opcode.AEvalPush)
            {
                double c = s.A2;
                r = s.A1 == 0 ? (c, c) : (-operand, operand);
            }
            else if (s.Op == Opcode.AEvalBin)
            {
                var b = stack[stack.Count - 1];
                var a = stack[stack.Count - 2];
                stack.RemoveRange(stack.Count - 2, 2);
                switch ((BinOp)s.A1)
                {
                    case BinOp.Add: r = (a.Lo + b.Lo, a.Hi + b.Hi); break;
                    case BinOp.Sub: r = (a.Lo - b.Hi, a.Hi - b.Lo); break;
                    default:
                        double p1 = a.Lo * b.Lo, p2 = a.Lo * b.Hi, p3 = a.Hi * b.Lo, p4 = a.Hi * b.Hi;
                        r = (System.Math.Min(System.Math.Min(p1, p2), System.Math.Min(p3, p4)),
                             System.Math.Max(System.Math.Max(p1, p2), System.Math.Max(p3, p4)));
                        break;
                }
            }
            else if (s.Op == Opcode.AEvalUn)
            {
                if ((UnOp)s.A1 != UnOp.Neg) continue;
                var a = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                r = (-a.Hi, -a.Lo);
            }
            else
            {
                if (s.Op == Opcode.AEvalIs)
                    resultFits = stack[0].Lo > -ResultLimit && stack[0].Hi < ResultLimit;
                return true;
            }
            if (r.Lo < -Limit || r.Hi > Limit) return false;
            stack.Add(r);
        }
        return true;
    }
}
