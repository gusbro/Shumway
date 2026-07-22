using System.Numerics;
using System.Runtime.CompilerServices;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Runtime evaluation stack for the arithmetic instruction set (ADR-018:
/// <c>a_eval_push</c> / <c>a_eval_bin</c> / <c>a_eval_un</c> / <c>a_eval_is</c>
/// / <c>a_eval_cmp</c>). A postfix sequence pushes operands and applies
/// operators here, leaving the WAM heap untouched.
///
/// <para><b>Integer fast lane with lazy escalation.</b> Each slot carries
/// either a raw <c>long</c> (<c>!_boxed</c>) or a <see cref="Number"/> (float /
/// bigint). An all-integer evaluation runs entirely on raw longs; a slot
/// escalates to <see cref="Number"/> only when it meets a float / bigint
/// operand or a fast op overflows the 60-bit range — per-value, never a global
/// mode.</para>
///
/// <para>Both tiers route through these static methods: the Tier-0 interpreter
/// dispatches the <c>a_eval_*</c> opcodes here, and the Tier-1 IL emit calls
/// them directly — which is why the stack lives here (reachable from an IL
/// delegate that only carries the <see cref="Activation"/>) rather than as an
/// interpreter field.</para>
///
/// <para><b>Thread-safety.</b> The backing arrays are <c>[ThreadStatic]</c> but
/// are not engine state: arithmetic is leaf (no Prolog goal runs between the
/// first push and the terminating is/cmp), so evaluations never nest or
/// interleave on one thread and the stack is empty between goals. The engine
/// stays thread-agile; a plain <c>static</c> would race two engines on two
/// threads.</para>
/// </summary>
public static class ArithEvalStack
{
    // Parallel slot arrays. _i[k] holds the raw long when !_boxed[k]; _n[k]
    // holds the Number when _boxed[k]. The fast int lane touches only _i / _b;
    // the fat _n array stays cold unless a float / bigint appears.
    [ThreadStatic] private static long[]? _i;
    [ThreadStatic] private static Number[]? _n;
    [ThreadStatic] private static bool[]? _b;
    [ThreadStatic] private static int _top;

    private static void EnsureInit()
    {
        if (_i is not null) return;
        _i = new long[32];
        _n = new Number[32];
        _b = new bool[32];
    }

    private static void Grow()
    {
        int cap = _i!.Length * 2;
        System.Array.Resize(ref _i, cap);
        System.Array.Resize(ref _n, cap);
        System.Array.Resize(ref _b, cap);
    }

    // Must be AggressiveInlining: this runs once per integer operand from the
    // Tier-1 delegates, and inlining lets the JIT CSE the [ThreadStatic] base
    // address across the caller's fast lane. The init/grow check collapses to
    // one predicted-not-taken branch (a null _i routes to PushIntSlow, which
    // subsumes EnsureInit).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PushIntLane(long v)
    {
        long[]? ia = _i;
        int top = _top;
        if (ia is null || top == ia.Length) { PushIntSlow(v); return; }
        ia[top] = v;
        _b![top] = false;
        _top = top + 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PushIntSlow(long v)
    {
        EnsureInit();
        if (_top == _i!.Length) Grow();
        _i![_top] = v;
        _b![_top] = false;
        _top++;
    }

    private static void PushBoxed(Number num)
    {
        EnsureInit();
        if (_top == _i!.Length) Grow();
        _n![_top] = num;
        _b![_top] = true;
        _top++;
    }

    /// <summary>Pushes an already-evaluated number; an integer-kind one drops
    /// into the fast lane, a float / bigint stays boxed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Push(Number num)
    {
        if (num.IsInt) PushIntLane(num.IntValue);
        else PushBoxed(num);
    }

    /// <summary>Pushes a 32-bit integer literal operand (a_eval_push kind 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PushInt(long value) => PushIntLane(value);

    // The int fast lane raises no Prolog error, so it takes no try/catch (which
    // would block inlining) — only the non-int Evaluate path can throw, and it
    // lives in the cold PushEvalSlow.
    /// <summary>Evaluates the X-register and pushes the result (a_eval_push kind 3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PushReg(Activation engine, int reg)
    {
        Cell c = engine.GetRegister(reg);
        if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        if (c.Tag == Tag.Int) { PushIntLane(c.AsInt); return; }
        PushEvalSlow(engine, c);
    }

    /// <summary>Evaluates the permanent (Y) slot and pushes the result
    /// (a_eval_push kind 4).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PushY(Activation engine, int slot)
    {
        Cell c = engine.GetY(slot);
        if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        if (c.Tag == Tag.Int) { PushIntLane(c.AsInt); return; }
        PushEvalSlow(engine, c);
    }

    // Non-int operand: a float / bigint cell, a compound sub-expression a var is
    // bound to, or an unbound var (→ instantiation_error). The general evaluator
    // can throw, so the try/catch (which would block inlining of the fast lane)
    // lives here. The cell is already deref'd by the caller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PushEvalSlow(Activation engine, Cell cell)
    {
        try { Push(ArithmeticEvaluator.Evaluate(engine, cell)); }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
    }

    /// <summary>Applies a binary operator to the top two stack entries
    /// (a_eval_bin), leaving the result on top. Stays on raw longs when both
    /// operands are in the int lane and the op is integer-closed within 60
    /// bits; otherwise escalates to the <see cref="Number"/> path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bin(int op)
    {
        int ai = _top - 2, bi = _top - 1;
        if (!_b![ai] && !_b[bi] && TryFastBin(op, _i![ai], _i[bi], out long r))
        {
            _i![ai] = r;          // result stays in the int lane (_b[ai] already false)
            _top--;
            return;
        }
        BinSlow(op, ai, bi);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BinSlow(int op, int ai, int bi)
    {
        Escalate(ai);
        Escalate(bi);
        try { _n![ai] = ArithmeticEvaluator.ApplyBin((ArithmeticEvaluator.BinOp)op, _n[ai], _n[bi]); }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
        _b![ai] = true;
        _top--;
    }

    /// <summary>Applies a unary operator to the top stack entry (a_eval_un).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Un(int op)
    {
        int ai = _top - 1;
        if (!_b![ai] && TryFastUn(op, _i![ai], out long r))
        {
            _i![ai] = r;
            return;
        }
        UnSlow(op, ai);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UnSlow(int op, int ai)
    {
        Escalate(ai);
        try { _n![ai] = ArithmeticEvaluator.ApplyUn((ArithmeticEvaluator.UnOp)op, _n[ai]); }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
    }

    /// <summary>Pops the result and unifies it with the X-register
    /// (a_eval_is kind 3). Returns the unification outcome.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsReg(Activation engine, int reg) => engine.UnifyRegisterWithCell(reg, PopCell(engine));

    /// <summary>Pops the result and unifies it with the permanent (Y) slot
    /// (a_eval_is kind 4). Returns the unification outcome.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPerm(Activation engine, int slot) => engine.UnifyPermanentWithCell(slot, PopCell(engine));

    /// <summary>Pops the result and stores it directly into the X-register
    /// (a_eval_is kind 5) — a first-occurrence target variable, bound by a plain
    /// store rather than unification (no unbound heap cell, no trail entry).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetReg(Activation engine, int reg) => engine.SetRegister(reg, PopCell(engine));

    /// <summary>Pops the result and stores it directly into the permanent (Y)
    /// slot (a_eval_is kind 6) — first-occurrence permanent target.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetPerm(Activation engine, int slot) => engine.SetY(slot, PopCell(engine));

    // Runs once per a_eval_is (every compiled is/2). A try/catch makes a method
    // uninlineable outright, so the int-lane pop (no Prolog error possible) is
    // the inlineable fast path; only the boxed Number.ToCell (bigint alloc /
    // float pair — can raise) keeps the try/catch, in the cold NoInlining half.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Cell PopCell(Activation engine)
    {
        int ai = --_top;
        if (!_b![ai]) return Cell.Int(_i![ai]);   // int lane always fits Cell.Int by invariant
        return PopCellBoxed(engine, ai);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Cell PopCellBoxed(Activation engine, int ai)
    {
        try { return _n![ai].ToCell(engine); }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
    }

    /// <summary>Pops the top two entries and applies an arithmetic comparison
    /// (a_eval_cmp). Returns whether the relation holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Cmp(int rel)
    {
        int ai = _top - 2, bi = _top - 1;
        _top -= 2;
        if (!_b![ai] && !_b[bi])
            return FastCmp(rel, _i![ai], _i[bi]);
        return CmpBoxed(rel, ai, bi);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CmpBoxed(int rel, int ai, int bi)
    {
        Escalate(ai);
        Escalate(bi);
        return ArithmeticEvaluator.ApplyRel((ArithmeticEvaluator.RelOp)rel, _n![ai], _n[bi]);
    }

    // ---- fused flat ops (a_int_bin / a_int_cmp) ----
    // These bypass the eval stack entirely: a single dispatch reads two leaf
    // operands, applies the op (int fast lane, escalating to Number for
    // float / bigint / overflow), and delivers the result — collapsing the
    // push/push/op/is RPN sequence into one call for the common flat case.
    // Operand kind: 0 int-literal, 3 X-reg, 4 Y-slot. Target kind: 3 unify-reg,
    // 4 unify-Y, 5 set-reg, 6 set-Y.

    /// <summary><c>T is A op B</c> over two simple leaf operands.</summary>
    // Two inline-int operands with the op staying in 60-bit long arithmetic
    // cannot raise a Prolog error, so the fast lane is try/catch-free (a
    // try/catch would block inlining of the whole method); the catch lives in
    // the cold slow path.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool FusedBin(Activation engine, int op,
        int aKind, int aVal, int bKind, int bVal, int tKind, int tVal)
    {
        if (TryReadInt(engine, aKind, aVal, out long ai)
            && TryReadInt(engine, bKind, bVal, out long bi)
            && TryFastBin(op, ai, bi, out long r))
            return Deliver(engine, tKind, tVal, Cell.Int(r));
        return FusedBinSlow(engine, op, aKind, aVal, bKind, bVal, tKind, tVal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool FusedBinSlow(Activation engine, int op,
        int aKind, int aVal, int bKind, int bVal, int tKind, int tVal)
    {
        Cell result;
        try
        {
            bool aInt = ReadOperand(engine, aKind, aVal, out long ai, out Number an);
            bool bInt = ReadOperand(engine, bKind, bVal, out long bi, out Number bn);
            if (aInt && bInt && TryFastBin(op, ai, bi, out long r))
                result = Cell.Int(r);
            else
                result = ArithmeticEvaluator.ApplyBin((ArithmeticEvaluator.BinOp)op,
                    aInt ? new Number(ai) : an, bInt ? new Number(bi) : bn).ToCell(engine);
        }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
        return Deliver(engine, tKind, tVal, result);
    }

    /// <summary><c>A cmp B</c> over two simple leaf operands.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool FusedCmp(Activation engine, int rel,
        int aKind, int aVal, int bKind, int bVal)
    {
        if (TryReadInt(engine, aKind, aVal, out long ai)
            && TryReadInt(engine, bKind, bVal, out long bi))
            return FastCmp(rel, ai, bi);
        return FusedCmpSlow(engine, rel, aKind, aVal, bKind, bVal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool FusedCmpSlow(Activation engine, int rel,
        int aKind, int aVal, int bKind, int bVal)
    {
        try
        {
            bool aInt = ReadOperand(engine, aKind, aVal, out long ai, out Number an);
            bool bInt = ReadOperand(engine, bKind, bVal, out long bi, out Number bn);
            if (aInt && bInt) return FastCmp(rel, ai, bi);
            return ArithmeticEvaluator.ApplyRel((ArithmeticEvaluator.RelOp)rel,
                aInt ? new Number(ai) : an, bInt ? new Number(bi) : bn);
        }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
    }

    // Non-throwing inline-int read for the fast lane: returns true + the long
    // only for an int literal (kind 0) or a register/Y slot already holding an
    // Int cell. Anything else (float, bigint, a compound to evaluate, an unbound
    // var) returns false, so the caller falls to the slow path which runs the
    // full ReadOperand (whose Evaluate raises instantiation_error / type_error)
    // under the try/catch.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadInt(Activation engine, int kind, int val, out long iVal)
    {
        if (kind == 0) { iVal = val; return true; }
        Cell c = kind == 4 ? engine.GetY(val) : engine.GetRegister(val);
        if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        if (c.Tag == Tag.Int) { iVal = c.AsInt; return true; }
        iVal = 0;
        return false;
    }

    // Reads a leaf operand. Returns true (int lane, iVal valid) for an integer
    // value; false (boxed, nVal valid) for a float / bigint. An int literal and
    // an int-valued register/Y take the fast lane; an unbound var raises
    // instantiation_error from Evaluate, exactly as is/2 does.
    private static bool ReadOperand(Activation engine, int kind, int val, out long iVal, out Number nVal)
    {
        if (kind == 0) { iVal = val; nVal = default; return true; }
        Cell c = kind == 4 ? engine.GetY(val) : engine.GetRegister(val);
        if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        if (c.Tag == Tag.Int) { iVal = c.AsInt; nVal = default; return true; }
        nVal = ArithmeticEvaluator.Evaluate(engine, c);
        if (nVal.IsInt) { iVal = nVal.IntValue; return true; }
        iVal = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Deliver(Activation engine, int tKind, int tVal, Cell result)
    {
        switch (tKind)
        {
            case 5: engine.SetRegister(tVal, result); return true;
            case 6: engine.SetY(tVal, result); return true;
            case 4: return engine.UnifyPermanentWithCell(tVal, result);
            default: return engine.UnifyRegisterWithCell(tVal, result);
        }
    }

    // Converts an int-lane slot to a boxed Number in place (no-op if already
    // boxed). The long is within 60 bits, so Number(long) yields an Int-kind
    // Number; subsequent Number ops promote to BigInt / float as needed.
    private static void Escalate(int k)
    {
        if (_b![k]) return;
        _n![k] = new Number(_i![k]);
        _b[k] = true;
    }

    // ---- raw-long fast paths (mirror ArithmeticEvaluator's int semantics) ----

    // AggressiveInlining matters: inside a big Tier-1 delegate the JIT's inline
    // budget is exhausted by the time it reaches this leaf, and without it this
    // survives as a real CALL in the integer hot loop. Two compares beat a call.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Fits60(long v) => v >= Cell.MinInt60 && v <= Cell.MaxInt60;

    /// <summary>Integer-closed binary ops on 60-bit longs. Returns false (→
    /// Number path) for a non-fast op, a zero divisor, or a result that
    /// overflows the 60-bit inline range (the Number path then promotes to
    /// BigInteger or raises the error, identically to <c>is/2</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryFastBin(int op, long a, long b, out long r)
    {
        switch ((ArithmeticEvaluator.BinOp)op)
        {
            case ArithmeticEvaluator.BinOp.Add: r = a + b; return Fits60(r);
            case ArithmeticEvaluator.BinOp.Sub: r = a - b; return Fits60(r);
            case ArithmeticEvaluator.BinOp.Mul:
                // Non-throwing 64-bit overflow check (a try/catch here would
                // block inlining of the fast lane): the inputs are 60-bit, so
                // a != 0 && (a*b)/a != b iff the product overflowed. Fits60 then
                // filters the 60-bit→BigInteger escalation. Both → slow path.
                r = unchecked(a * b);
                if (a != 0 && r / a != b) { r = 0; return false; }
                return Fits60(r);
            case ArithmeticEvaluator.BinOp.IntDiv:
                if (b == 0) { r = 0; return false; }
                r = a / b;                         // ISO //: truncate toward zero
                return true;
            case ArithmeticEvaluator.BinOp.Mod:
                if (b == 0) { r = 0; return false; }
                r = a % b;                         // ISO mod: sign of the divisor
                if (r != 0 && ((r ^ b) < 0)) r += b;
                return true;
            case ArithmeticEvaluator.BinOp.IntDivFloor:
                if (b == 0) { r = 0; return false; }
                r = a / b;                         // ISO div: floor division
                if (a % b != 0 && ((a ^ b) < 0)) r--;
                return true;
            default:
                r = 0;
                return false;
        }
    }

    private static bool TryFastUn(int op, long a, out long r)
    {
        switch ((ArithmeticEvaluator.UnOp)op)
        {
            case ArithmeticEvaluator.UnOp.Pos: r = a; return true;
            case ArithmeticEvaluator.UnOp.Neg: r = -a; return Fits60(r);
            case ArithmeticEvaluator.UnOp.Abs: r = a < 0 ? -a : a; return Fits60(r);
            case ArithmeticEvaluator.UnOp.Sign: r = a > 0 ? 1 : a < 0 ? -1 : 0; return true;
            case ArithmeticEvaluator.UnOp.BitNot: r = ~a; return Fits60(r);
            default: r = 0; return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FastCmp(int rel, long a, long b) => (ArithmeticEvaluator.RelOp)rel switch
    {
        ArithmeticEvaluator.RelOp.Eq => a == b,
        ArithmeticEvaluator.RelOp.Neq => a != b,
        ArithmeticEvaluator.RelOp.Lt => a < b,
        ArithmeticEvaluator.RelOp.Gt => a > b,
        ArithmeticEvaluator.RelOp.Le => a <= b,
        ArithmeticEvaluator.RelOp.Ge => a >= b,
        _ => false,
    };
}
