using System.Numerics;
using System.Runtime.CompilerServices;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// ADR-018 — the runtime evaluation stack for the arithmetic instruction set
/// (<c>a_eval_push</c> / <c>a_eval_bin</c> / <c>a_eval_un</c> / <c>a_eval_is</c>
/// / <c>a_eval_cmp</c>). A postfix arithmetic sequence pushes operands and
/// applies operators against this stack, leaving the WAM heap untouched — no
/// synthetic variables, no expression term.
///
/// <para><b>Integer fast lane with lazy escalation.</b> Prolog arithmetic is
/// dynamically typed (a value may be a 60-bit <c>int</c>, a <see cref="BigInteger"/>
/// after overflow, or a <c>double</c>), so the universal carrier is the fat
/// <see cref="Number"/> struct. But the overwhelmingly common case — small
/// integers (counters, indices, <c>X-1</c>) — never needs it. Each stack slot
/// therefore carries either a <i>raw <c>long</c></i> (the fast int lane, slot
/// flagged <c>!_boxed</c>) or a <see cref="Number"/> (the slow lane, for
/// float / bigint). An all-integer evaluation runs entirely on raw longs and
/// allocates / copies no <see cref="Number"/>. A slot <i>escalates</i> to a
/// <see cref="Number"/> only when it meets a float / bigint operand or a fast
/// op overflows the 60-bit range — per-value, never a global "mode". This is
/// the same raw-long shortcut the retired <c>$arith2</c> builtin used, now
/// inside the RPN machine so it covers nested integer expressions too. Float /
/// bigint arithmetic stays on the <see cref="Number"/> path (genuinely heavier
/// values — unavoidable, and rare in practice).</para>
///
/// <para>Both execution tiers route through these static methods: the Tier-0
/// interpreter dispatches the <c>a_eval_*</c> opcodes here, and the Tier-1 IL
/// emit (<c>IlPredicateCompiler</c>) emits direct calls to them. The Tier-1
/// path is why the stack lives here (reachable from an IL delegate that only
/// carries the <see cref="Engine"/>) rather than as an interpreter field.</para>
///
/// <para><b>Thread-safety / engine-agility.</b> The backing arrays are
/// <c>[ThreadStatic]</c>, but they are <i>not</i> engine state — they carry no
/// engine identity and are always fully drained within a single arithmetic
/// evaluation. Arithmetic is leaf: no Prolog goal executes between the first
/// <c>a_eval_push</c> and the terminating <c>a_eval_is</c>/<c>a_eval_cmp</c>,
/// so evaluations never nest or interleave on one thread, and the stack is
/// empty between goals. The engine therefore stays thread-agile (none of
/// <i>its</i> state is thread-static) — this is transient per-thread scratch,
/// which the invariant's "no [ThreadStatic] for engine state" rule is not
/// about. A plain <c>static</c> would race two engines arithmeticking on two
/// threads at once.</para>
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

    private static void PushIntLane(long v)
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
    public static void Push(Number num)
    {
        if (num.IsInt) PushIntLane(num.IntValue);
        else PushBoxed(num);
    }

    /// <summary>Pushes a 32-bit integer literal operand (a_eval_push kind 0).</summary>
    public static void PushInt(long value) => PushIntLane(value);

    // Chunk 355: the integer fast lane (a register/Y slot holding an inline Int)
    // raises no Prolog error, so it takes no try/catch — only the non-int
    // Evaluate path can throw, and it lives in the cold PushEvalSlow.
    // AggressiveInlining lets the JIT fold the fast lane into the Tier-1 IL
    // delegate (mirrors chunk 354 for the eval-stack RPN path that crypt-style
    // compound expressions — `C is A*B+Carry` — use).
    /// <summary>Evaluates the X-register and pushes the result (a_eval_push kind 3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PushReg(Engine engine, int reg)
    {
        Cell c = engine.GetRegister(reg);
        if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        if (c.Tag == Tag.Int) { PushIntLane(c.AsInt); return; }
        PushEvalSlow(engine, c);
    }

    /// <summary>Evaluates the permanent (Y) slot and pushes the result
    /// (a_eval_push kind 4).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PushY(Engine engine, int slot)
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
    private static void PushEvalSlow(Engine engine, Cell cell)
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
    public static bool IsReg(Engine engine, int reg) => engine.UnifyRegisterWithCell(reg, PopCell(engine));

    /// <summary>Pops the result and unifies it with the permanent (Y) slot
    /// (a_eval_is kind 4). Returns the unification outcome.</summary>
    public static bool IsPerm(Engine engine, int slot) => engine.UnifyPermanentWithCell(slot, PopCell(engine));

    /// <summary>Pops the result and stores it directly into the X-register
    /// (a_eval_is kind 5) — a first-occurrence target variable, bound by a plain
    /// store rather than unification (no unbound heap cell, no trail entry).</summary>
    public static void SetReg(Engine engine, int reg) => engine.SetRegister(reg, PopCell(engine));

    /// <summary>Pops the result and stores it directly into the permanent (Y)
    /// slot (a_eval_is kind 6) — first-occurrence permanent target.</summary>
    public static void SetPerm(Engine engine, int slot) => engine.SetY(slot, PopCell(engine));

    private static Cell PopCell(Engine engine)
    {
        int ai = --_top;
        if (!_b![ai]) return Cell.Int(_i![ai]);   // int lane always fits Cell.Int by invariant
        try { return _n![ai].ToCell(engine); }
        catch (PrologRuntimeException re) { re.StampBuiltin("is", 2); throw; }
    }

    /// <summary>Pops the top two entries and applies an arithmetic comparison
    /// (a_eval_cmp). Returns whether the relation holds.</summary>
    public static bool Cmp(int rel)
    {
        int ai = _top - 2, bi = _top - 1;
        _top -= 2;
        if (!_b![ai] && !_b[bi])
            return FastCmp(rel, _i![ai], _i[bi]);
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
    // Chunk 354: integer fast lane is try/catch-free and AggressiveInlining, so
    // the JIT inlines it into the Tier-1 IL delegate. Both operands read as
    // inline ints and the op staying in 60-bit long arithmetic cannot raise a
    // Prolog error, so the try/catch (which blocks inlining of the whole method
    // and limits optimisation) moves to the cold slow path. Integer-heavy code
    // (cx, crypt) never leaves the fast lane.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool FusedBin(Engine engine, int op,
        int aKind, int aVal, int bKind, int bVal, int tKind, int tVal)
    {
        if (TryReadInt(engine, aKind, aVal, out long ai)
            && TryReadInt(engine, bKind, bVal, out long bi)
            && TryFastBin(op, ai, bi, out long r))
            return Deliver(engine, tKind, tVal, Cell.Int(r));
        return FusedBinSlow(engine, op, aKind, aVal, bKind, bVal, tKind, tVal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool FusedBinSlow(Engine engine, int op,
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
    public static bool FusedCmp(Engine engine, int rel,
        int aKind, int aVal, int bKind, int bVal)
    {
        if (TryReadInt(engine, aKind, aVal, out long ai)
            && TryReadInt(engine, bKind, bVal, out long bi))
            return FastCmp(rel, ai, bi);
        return FusedCmpSlow(engine, rel, aKind, aVal, bKind, bVal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool FusedCmpSlow(Engine engine, int rel,
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
    private static bool TryReadInt(Engine engine, int kind, int val, out long iVal)
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
    private static bool ReadOperand(Engine engine, int kind, int val, out long iVal, out Number nVal)
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
    private static bool Deliver(Engine engine, int tKind, int tVal, Cell result)
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
