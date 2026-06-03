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
    /// <para>Fast path (chunk 284): a 2-arg compound whose functor is
    /// +, -, *, // or mod and both args (after deref) are <see cref="Tag.Int"/>
    /// is evaluated directly in a long without going through the
    /// <see cref="Number"/> boxing path or the string-keyed dispatch.
    /// Overflow falls back to the slow evaluator (which promotes to
    /// BigInt). Targets tight-loop arithmetic in tak / queens / crypt
    /// where each iteration does e.g. `N1 is N - 1`.</para></summary>
    public static bool Is(Engine engine)
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

    public static bool ArithEqual(Engine engine) =>
        TryFastIntCompare(engine, out int cmp) ? cmp == 0
        : Number.Compare(EvaluateA(engine), EvaluateB(engine)) == 0;

    public static bool ArithNotEqual(Engine engine) =>
        TryFastIntCompare(engine, out int cmp) ? cmp != 0
        : Number.Compare(EvaluateA(engine), EvaluateB(engine)) != 0;

    public static bool ArithLess(Engine engine) =>
        TryFastIntCompare(engine, out int cmp) ? cmp < 0
        : Number.Compare(EvaluateA(engine), EvaluateB(engine)) < 0;

    public static bool ArithGreater(Engine engine) =>
        TryFastIntCompare(engine, out int cmp) ? cmp > 0
        : Number.Compare(EvaluateA(engine), EvaluateB(engine)) > 0;

    public static bool ArithLessOrEqual(Engine engine) =>
        TryFastIntCompare(engine, out int cmp) ? cmp <= 0
        : Number.Compare(EvaluateA(engine), EvaluateB(engine)) <= 0;

    public static bool ArithGreaterOrEqual(Engine engine) =>
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

    // ADR-018 retired the goal-rewriting `$arith2` / `$arith1` builtins: an
    // `X is A op B` goal now compiles directly to the a_eval_* instruction set
    // (RPN over the Number eval stack), with no synthetic variable and no
    // expression term on the heap. The TryFastIntBinary fast path below still
    // serves the `is/2` *builtin* — reached when arithmetic is not compiled to
    // a_eval_* (a runtime meta-call such as `call(X is Y)`, or a variable goal).

    // Checked integer arithmetic for the hot ops on already-deref'd Int
    // operands; returns false on overflow / out-of-60-bit-range / div-by-zero
    // / non-fast op, so the caller falls to the BigInt-promoting slow path.
    private static bool TryFastIntBinary(Engine engine, Cell strCell, out long result)
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
    private static bool TryFastIntCompare(Engine engine, out int cmp)
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
    /// binds <c>X</c> to <c>Low</c> and pushes a runtime choice point
    /// for the next integer (chunk 59) — each backtrack advances to
    /// <c>Low + 1</c>, <c>Low + 2</c>, etc., until <c>High</c> is reached.</summary>
    public static bool Between(Engine engine)
    {
        Cell lo = Resolve(engine, engine.GetRegister(0));
        Cell hi = Resolve(engine, engine.GetRegister(1));
        Cell x = Resolve(engine, engine.GetRegister(2));
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
            int returnPc = engine.BuiltinReturnPc;
            return BetweenStep(engine, current: loVal, hiVal, returnPc, isResume: false);
        }
        return false;
    }

    private static bool BetweenStep(
        Engine engine, long current, long hi, int returnPc, bool isResume)
    {
        if (current > hi) return false;
        if (current < hi)
        {
            long next = current + 1;
            Func<Engine, int, bool> resume = (e, _) =>
                BetweenStep(e, next, hi, returnPc, isResume: true);
            // arity: 3 — the CP must save/restore between's three argument
            // registers (X0..X2). The resume writes the result into X2, but a
            // following body goal whose builtin call takes >= 3 args clobbers
            // X2 (and beyond); without restoring it, the resume's
            // UnifyRegisterWithCell(2, ...) would operate on a corrupt
            // register and the enumeration breaks after one or two values.
            engine.PushBuiltinChoicePoint(resume, arity: 3);
        }
        if (!engine.UnifyRegisterWithCell(2, Cell.Int(current))) return false;
        if (isResume) engine.ResumeAtReturnPc(returnPc);
        return true;
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
    public static bool Succ(Engine engine)
    {
        Cell xc = Resolve(engine, engine.GetRegister(0));
        Cell yc = Resolve(engine, engine.GetRegister(1));

        if (xc.Tag == Tag.Int)
        {
            long xv = xc.AsInt;
            if (xv < 0)
                throw new PrologRuntimeException("domain_error", "not_less_than_zero");
            return engine.UnifyRegisterWithCell(1, Cell.Int(xv + 1));
        }
        if (yc.Tag == Tag.Int)
        {
            long yv = yc.AsInt;
            if (yv < 0)
                throw new PrologRuntimeException("domain_error", "not_less_than_zero");
            if (yv == 0) return false;   // succ(_, 0) has no solution
            return engine.UnifyRegisterWithCell(0, Cell.Int(yv - 1));
        }
        // Neither is an integer — instantiation_error if both var,
        // type_error(integer) when at least one is bound to a non-int.
        if (xc.Tag == Tag.Ref && yc.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        throw new PrologRuntimeException("type_error", "integer");
    }

    /// <summary><c>plus(X, Y, Z)</c> — integer addition relation with
    /// any one of the three arguments allowed to be free. With X+Y
    /// bound it computes Z; with X+Z bound it computes Y = Z-X; with
    /// Y+Z bound it computes X = Z-Y. (Chunk 54.)</summary>
    public static bool Plus(Engine engine)
    {
        Cell xc = Resolve(engine, engine.GetRegister(0));
        Cell yc = Resolve(engine, engine.GetRegister(1));
        Cell zc = Resolve(engine, engine.GetRegister(2));

        bool xBound = xc.Tag == Tag.Int;
        bool yBound = yc.Tag == Tag.Int;
        bool zBound = zc.Tag == Tag.Int;
        int boundCount = (xBound ? 1 : 0) + (yBound ? 1 : 0) + (zBound ? 1 : 0);
        if (boundCount < 2)
            throw new PrologRuntimeException("instantiation_error");

        if (xBound && yBound)
        {
            long sum = checked(xc.AsInt + yc.AsInt);
            return engine.UnifyRegisterWithCell(2, Cell.Int(sum));
        }
        if (xBound && zBound)
        {
            long y = checked(zc.AsInt - xc.AsInt);
            return engine.UnifyRegisterWithCell(1, Cell.Int(y));
        }
        // yBound && zBound
        {
            long x = checked(zc.AsInt - yc.AsInt);
            return engine.UnifyRegisterWithCell(0, Cell.Int(x));
        }
    }

    private static Number EvaluateA(Engine engine) =>
        ArithmeticEvaluator.Evaluate(engine, engine.GetRegister(0));

    private static Number EvaluateB(Engine engine) =>
        ArithmeticEvaluator.Evaluate(engine, engine.GetRegister(1));

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
