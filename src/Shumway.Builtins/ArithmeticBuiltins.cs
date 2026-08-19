using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The ISO arithmetic predicates: <c>is/2</c> for assignment and the six
/// comparison operators (<c>=:=</c>, <c>=\=</c>, <c>&lt;</c>, <c>&gt;</c>,
/// <c>=&lt;</c>, <c>&gt;=</c>) that take two arithmetic expressions. The
/// expressions themselves are evaluated by <see cref="ArithmeticEvaluator"/>.
/// </summary>
public static class ArithmeticBuiltins
{
    /// <summary><c>X is Expr</c> — evaluates <c>Expr</c> and unifies the
    /// result with <c>X</c>. The result is either an Int cell (integer
    /// result) or a heap-allocated Float (when promotion was needed).
    ///
    /// <para>Fast path: a 2-arg compound whose functor is +, -, *, // or mod
    /// with both args (after deref) <see cref="Tag.Int"/> is evaluated directly
    /// in a long, skipping <see cref="Number"/> boxing. Overflow falls back to
    /// the slow evaluator (which promotes to BigInt).</para></summary>
    public static bool Is(Activation engine)
    {
        Cell rhs = engine.GetRegister(1);
        if (rhs.Tag == Tag.Ref) rhs = engine.GetHeap(engine.Deref(rhs.AsHeapIndex));
        if (rhs.Tag == Tag.Str && TryFastIntBinary(engine, rhs, out long fast))
            return engine.UnifyRegisterWithCell(0, Cell.Int(fast));
        if (rhs.Tag == Tag.Int)
            return engine.UnifyRegisterWithCell(0, rhs);   // `X is 7`.

        Number result = ArithmeticEvaluator.Evaluate(engine, rhs);
        Cell cell = result.ToCell(engine);
        return engine.UnifyRegisterWithCell(0, cell);
    }

    public static bool ArithEqual(Activation engine) =>
        TryFastIntCompare(engine, out int cmp) ? cmp == 0
        : Number.Compare(EvaluateA(engine), EvaluateB(engine)) == 0;

    public static bool ArithNotEqual(Activation engine) =>
        TryFastIntCompare(engine, out int cmp) ? cmp != 0
        : Number.Compare(EvaluateA(engine), EvaluateB(engine)) != 0;

    public static bool ArithLess(Activation engine) =>
        TryFastIntCompare(engine, out int cmp) ? cmp < 0
        : Number.Compare(EvaluateA(engine), EvaluateB(engine)) < 0;

    public static bool ArithGreater(Activation engine) =>
        TryFastIntCompare(engine, out int cmp) ? cmp > 0
        : Number.Compare(EvaluateA(engine), EvaluateB(engine)) > 0;

    public static bool ArithLessOrEqual(Activation engine) =>
        TryFastIntCompare(engine, out int cmp) ? cmp <= 0
        : Number.Compare(EvaluateA(engine), EvaluateB(engine)) <= 0;

    public static bool ArithGreaterOrEqual(Activation engine) =>
        TryFastIntCompare(engine, out int cmp) ? cmp >= 0
        : Number.Compare(EvaluateA(engine), EvaluateB(engine)) >= 0;

    // Cached functor ids for the common arithmetic ops. FunctorTable
    // is process-global and ids never recycle, so a per-process lazy
    // cache is safe.
    private static int _addFid, _subFid, _mulFid, _intDivFid, _modFid;
    private static int _addAid, _subAid, _mulAid, _intDivAid, _modAid;
    private static bool _fidsInit;
    private static void InitFids()
    {
        _addAid    = AtomTable.Intern("+",   permanent: true).Id;
        _subAid    = AtomTable.Intern("-",   permanent: true).Id;
        _mulAid    = AtomTable.Intern("*",   permanent: true).Id;
        _intDivAid = AtomTable.Intern("//",  permanent: true).Id;
        _modAid    = AtomTable.Intern("mod", permanent: true).Id;
        _addFid    = FunctorTable.Intern(_addAid,    2);
        _subFid    = FunctorTable.Intern(_subAid,    2);
        _mulFid    = FunctorTable.Intern(_mulAid,    2);
        _intDivFid = FunctorTable.Intern(_intDivAid, 2);
        _modFid    = FunctorTable.Intern(_modAid,    2);
        _fidsInit  = true;
    }

    // Compiled arithmetic uses the a_eval_* instruction set (ADR-018); this
    // builtin path is only reached when arithmetic is NOT compiled — a runtime
    // meta-call such as `call(X is Y)`, or a variable goal — hence the fast
    // path below still matters.

    // Checked integer arithmetic for the hot ops on already-deref'd Int
    // operands; returns false on overflow / out-of-60-bit-range / div-by-zero
    // / non-fast op, so the caller falls to the BigInt-promoting slow path.
    private static bool TryFastIntBinary(Activation engine, Cell strCell, out long result)
    {
        result = 0;
        if (!_fidsInit) InitFids();
        int functorIdx = strCell.AsHeapIndex;
        int fid = engine.GetHeap(functorIdx).AsFunctorId;
        if (fid != _addFid && fid != _subFid && fid != _mulFid
            && fid != _intDivFid && fid != _modFid)
            return false;
        Cell a = engine.GetHeap(functorIdx + 1);
        if (a.Tag == Tag.Ref) a = engine.GetHeap(engine.Deref(a.AsHeapIndex));
        if (a.Tag != Tag.Int) return false;
        Cell b = engine.GetHeap(functorIdx + 2);
        if (b.Tag == Tag.Ref) b = engine.GetHeap(engine.Deref(b.AsHeapIndex));
        if (b.Tag != Tag.Int) return false;

        long av = a.AsInt, bv = b.AsInt;
        try
        {
            checked
            {
                if (fid == _addFid)    result = av + bv;
                else if (fid == _subFid) result = av - bv;
                else if (fid == _mulFid) result = av * bv;
                else if (fid == _intDivFid)
                {
                    if (bv == 0) return false;   // let slow path raise
                    result = av / bv;
                }
                else if (fid == _modFid)
                {
                    if (bv == 0) return false;
                    long r = av % bv;
                    if ((r != 0) && ((r < 0) != (bv < 0))) r += bv;  // ISO mod
                    result = r;
                }
                else return false;
            }
            // A long can hold values the 60-bit inline Int cell cannot (e.g.
            // 1000000000 * 1000000000 = 1e18 fits long but not Int60); if the
            // result is out of inline range, fall to the BigInt-promoting slow
            // path rather than letting Cell.Int throw.
            return result >= Cell.MinInt60 && result <= Cell.MaxInt60;
        }
        catch (OverflowException) { return false; }  // fall through to BigInt
    }

    // Fast int-int comparison: skip Number boxing when both operands
    // are concrete ints (possibly behind one level of indirection).
    // Returns true on success with `cmp` ∈ {-1, 0, 1}.
    private static bool TryFastIntCompare(Activation engine, out int cmp)
    {
        cmp = 0;
        Cell a = engine.GetRegister(0);
        if (a.Tag == Tag.Ref) a = engine.GetHeap(engine.Deref(a.AsHeapIndex));
        if (a.Tag == Tag.Str && TryFastIntBinary(engine, a, out long av)) { }
        else if (a.Tag == Tag.Int) av = a.AsInt;
        else return false;
        Cell b = engine.GetRegister(1);
        if (b.Tag == Tag.Ref) b = engine.GetHeap(engine.Deref(b.AsHeapIndex));
        if (b.Tag == Tag.Str && TryFastIntBinary(engine, b, out long bv)) { }
        else if (b.Tag == Tag.Int) bv = b.AsInt;
        else return false;
        cmp = av.CompareTo(bv);
        return true;
    }

    /// <summary><c>between(Low, High, X)</c> — integer range. <c>Low</c>
    /// and <c>High</c> must be ground integers. With <c>X</c> ground the
    /// builtin verifies <c>Low ≤ X ≤ High</c>. With <c>X</c> unbound it
    /// binds <c>X</c> to <c>Low</c> and pushes a runtime choice point —
    /// each backtrack advances to <c>Low + 1</c>, <c>Low + 2</c>, etc.,
    /// until <c>High</c> is reached.</summary>
    public static bool Between(Activation engine)
    {
        Cell lo = Resolve(engine, engine.GetRegister(0));
        Cell hi = Resolve(engine, engine.GetRegister(1));
        Cell x = Resolve(engine, engine.GetRegister(2));
        // Bound-but-wrong beats unbound: between(a, 2, _) is
        // type_error(integer, a), not an instantiation_error.
        foreach (Cell c in stackalloc Cell[] { lo, hi })
            if (c.Tag is not (Tag.Ref or Tag.AttVar) && c.Tag is not (Tag.Int or Tag.BigInt))
                throw new PrologRuntimeException("type_error", "integer", engine, c);
        if (x.Tag is not (Tag.Ref or Tag.AttVar) && x.Tag is not (Tag.Int or Tag.BigInt))
            throw new PrologRuntimeException("type_error", "integer", engine, x);
        if (lo.Tag != Tag.Int || hi.Tag != Tag.Int)
            throw new PrologRuntimeException(
                "instantiation_error",
                "between/3 requires Low and High to be ground integers");
        long loVal = lo.AsInt;
        long hiVal = hi.AsInt;
        if (loVal > hiVal) return false;

        if (x.Tag == Tag.Int)
        {
            long xVal = x.AsInt;
            return xVal >= loVal && xVal <= hiVal;
        }
        if (x.Tag == Tag.Ref)
        {
            // Enumerate loVal..hiVal. The resume state lives in ONE per-CALL
            // cursor object + a cached delegate re-pushed unchanged on every
            // backtrack — a long range costs O(1) managed allocation, not one
            // closure per value (which made failure-driven loops churn Gen0).
            if (loVal < hiVal)
            {
                var cursor = new BetweenCursor(loVal, hiVal, engine.BuiltinReturnPc);
                // arity: 3 — the CP must save/restore between's three argument
                // registers (X0..X2). The resume writes the result into X2, but
                // a following body goal whose builtin call takes >= 3 args
                // clobbers X2; without restoring it the resume's
                // UnifyRegisterWithCell(2, ...) would corrupt the enumeration.
                engine.PushBuiltinChoicePoint(cursor.Resume, arity: 3);
            }
            return engine.UnifyRegisterWithCell(2, Cell.Int(loVal));
        }
        return false;
    }

    /// <summary>Resume state for a non-deterministic <c>between/3</c>
    /// enumeration: the running position plus a cached resume delegate,
    /// re-pushed unchanged on each backtrack so advancing allocates nothing
    /// per step.</summary>
    private sealed class BetweenCursor
    {
        private long _current;
        private readonly long _hi;
        private readonly int _returnPc;
        public readonly Func<Activation, int, bool> Resume;

        public BetweenCursor(long start, long hi, int returnPc)
        {
            _current = start;
            _hi = hi;
            _returnPc = returnPc;
            Resume = Step;
        }

        private bool Step(Activation engine, int _)
        {
            long next = ++_current;   // this backtrack yields the next value
            if (next < _hi)
                engine.PushBuiltinChoicePoint(Resume, arity: 3);
            if (!engine.UnifyRegisterWithCell(2, Cell.Int(next))) return false;
            engine.ResumeAtReturnPc(_returnPc);
            return true;
        }
    }

    /// <summary><c>succ(X, Y)</c> — successor of a non-negative integer.
    /// Either <c>X</c> or <c>Y</c> must be ground. With <c>X</c> ground
    /// (non-negative) the result is <c>Y = X + 1</c>; with <c>Y</c> ground
    /// (positive) the result is <c>X = Y - 1</c>.
    ///
    /// <para>ISO errors: a negative input raises
    /// <c>domain_error(not_less_than_zero, X)</c>; both args unbound
    /// raises <c>instantiation_error</c>; a non-integer raises
    /// <c>type_error(integer, _)</c>.</para></summary>
    public static bool Succ(Activation engine)
    {
        Cell xc = Resolve(engine, engine.GetRegister(0));
        Cell yc = Resolve(engine, engine.GetRegister(1));

        // Type/domain checks run on EVERY bound argument before either
        // direction is attempted, and carry the offending value.
        // Unbounded integers count: succ/2 relates bignums too.
        static System.Numerics.BigInteger? CheckArg(Activation e, Cell c)
        {
            if (c.Tag is Tag.Ref or Tag.AttVar) return null;
            System.Numerics.BigInteger v;
            if (c.Tag == Tag.Int) v = c.AsInt;
            else if (c.Tag == Tag.BigInt) v = e.AsBigInt(c);
            else throw new PrologRuntimeException("type_error", "integer", e, c);
            if (v.Sign < 0)
                throw new PrologRuntimeException(
                    "domain_error", "not_less_than_zero", e, c);
            return v;
        }
        System.Numerics.BigInteger? x = CheckArg(engine, xc);
        System.Numerics.BigInteger? y = CheckArg(engine, yc);
        if (x is { } xv)
            return engine.UnifyRegisterWithCell(1, new Number(xv + 1).ToCell(engine));
        if (y is { } yv)
        {
            if (yv.IsZero) return false;   // succ(_, 0) has no solution
            return engine.UnifyRegisterWithCell(0, new Number(yv - 1).ToCell(engine));
        }
        throw new PrologRuntimeException("instantiation_error");
    }

    /// <summary><c>plus(X, Y, Z)</c> — integer addition relation with
    /// any one of the three arguments allowed to be free. With X+Y
    /// bound it computes Z; with X+Z bound it computes Y = Z-X; with
    /// Y+Z bound it computes X = Z-Y.</summary>
    public static bool Plus(Activation engine)
    {
        Cell xc = Resolve(engine, engine.GetRegister(0));
        Cell yc = Resolve(engine, engine.GetRegister(1));
        Cell zc = Resolve(engine, engine.GetRegister(2));

        // A BOUND non-integer is a type error regardless of how many
        // arguments are known — the type check precedes the mode check.
        // Bignums are integers here: plus/3 relates unbounded values.
        static System.Numerics.BigInteger? Read(Activation e, Cell c)
        {
            if (c.Tag is Tag.Ref or Tag.AttVar) return null;
            if (c.Tag == Tag.Int) return c.AsInt;
            if (c.Tag == Tag.BigInt) return e.AsBigInt(c);
            throw new PrologRuntimeException("type_error", "integer", e, c);
        }
        System.Numerics.BigInteger? x = Read(engine, xc);
        System.Numerics.BigInteger? y = Read(engine, yc);
        System.Numerics.BigInteger? z = Read(engine, zc);
        int boundCount = (x is null ? 0 : 1) + (y is null ? 0 : 1) + (z is null ? 0 : 1);
        if (boundCount < 2)
            throw new PrologRuntimeException("instantiation_error");

        if (x is { } xv && y is { } yv)
            return engine.UnifyRegisterWithCell(2, new Number(xv + yv).ToCell(engine));
        if (x is { } xv2 && z is { } zv2)
            return engine.UnifyRegisterWithCell(1, new Number(zv2 - xv2).ToCell(engine));
        return engine.UnifyRegisterWithCell(
            0, new Number(z!.Value - y!.Value).ToCell(engine));
    }

    private static Number EvaluateA(Activation engine) =>
        ArithmeticEvaluator.Evaluate(engine, engine.GetRegister(0));

    private static Number EvaluateB(Activation engine) =>
        ArithmeticEvaluator.Evaluate(engine, engine.GetRegister(1));

    private static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
