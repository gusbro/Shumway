using System.Numerics;
using System.Runtime.CompilerServices;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Native bound-arithmetic primitives for the CLP(FD) library.
/// A "bound" is an integer or one of the atoms <c>inf</c> (−∞) / <c>sup</c>
/// (+∞), represented as a plain <c>long</c> with <see cref="long.MinValue"/> =
/// inf and <see cref="long.MaxValue"/> = sup, so comparisons and arithmetic are
/// native rather than chains of interpreted <c>==/2</c> tests (which dominated
/// finite-domain solving). In particular <c>clpfd_ble</c> collapses to a single
/// <c>&lt;=</c> because the sentinels order correctly against every integer.
///
/// <para>The arithmetic is exact: a bound computed from two inline integers
/// need not be one (the product of two maxima), and it is an integer of
/// whatever size it takes. What a bound past the inline range does to a
/// domain is decided where the domain is cut with it
/// (<see cref="ClpfdDomainBuiltins"/>); wrapped here, it would prune with a
/// wrong bound.</para>
///
/// <para>These are registered under the same names the clpfd module calls, so
/// removing the Prolog clauses makes the module-local calls fall through to
/// these builtins (the standard local → builtin resolution order).</para>
/// </summary>
public static class FdBoundBuiltins
{
    private const long Inf = long.MinValue;
    private const long Sup = long.MaxValue;

    // Interned in Register(), not in field initializers — see the note in
    // ClpfdDomainBuiltins: beforefieldinit timing differs per runtime.
    private static int InfAtom, SupAtom;

    private static Cell Arg(Activation engine, int reg)
    {
        Cell c = engine.GetRegister(reg);
        return c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;
    }

    /// <summary>Reads argument <paramref name="reg"/> as a bound that fits a
    /// long: an inline integer, or the atoms inf/sup mapped to the sentinels.
    /// False for an integer past the inline range, which takes the path of
    /// <see cref="Wide"/>.</summary>
    private static bool TryReadBound(Activation engine, int reg, out long v)
    {
        Cell c = Arg(engine, reg);
        if (c.Tag == Tag.Int) { v = c.AsInt; return true; }
        if (c.Tag == Tag.Atom && c.AsAtomId == InfAtom) { v = Inf; return true; }
        if (c.Tag == Tag.Atom && c.AsAtomId == SupAtom) { v = Sup; return true; }
        v = 0;
        if (c.Tag != Tag.BigInt) throw new PrologRuntimeException("type_error", "fd_bound");
        return false;
    }

    private static bool TryReadInt(Activation engine, int reg, out long v)
    {
        Cell c = Arg(engine, reg);
        if (c.Tag == Tag.Int) { v = c.AsInt; return true; }
        v = 0;
        if (c.Tag != Tag.BigInt) throw new PrologRuntimeException("type_error", "integer");
        return false;
    }

    // An integer that is not a sentinel, whatever a long can hold: the sum of
    // two inline integers does not fit an Int cell, and a product may be
    // exactly long.MinValue, which is a number here and not inf.
    private static bool WriteFinite(Activation engine, int reg, long v) =>
        v >= Cell.MinInt60 && v <= Cell.MaxInt60
            ? engine.UnifyRegisterWithCell(reg, Cell.Int(v))
            : WritePastInline(engine, reg, v);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool WritePastInline(Activation engine, int reg, long v) =>
        engine.UnifyRegisterWithCell(reg, engine.MakeBigInt(v));

    private static bool WriteInfinite(Activation engine, int reg, long sentinel) =>
        engine.UnifyRegisterWithCell(reg, Cell.Atom(sentinel == Inf ? InfAtom : SupAtom));

    // ---- a bound of any size ----
    //
    // The methods that handle one are kept out of line: a BigInteger local
    // has its method zero the frame on entry, and the builtins above them
    // run on inline integers at every propagation.

    /// <summary>A bound when an operand or the result is past the inline
    /// range: <see cref="Infinite"/> is -1 for inf, +1 for sup and 0 for the
    /// integer <see cref="Value"/>.</summary>
    private readonly struct Wide
    {
        public readonly int Infinite;
        public readonly BigInteger Value;

        private Wide(int infinite, BigInteger value) { Infinite = infinite; Value = value; }

        public static Wide NegInf => new(-1, BigInteger.Zero);
        public static Wide PosInf => new(+1, BigInteger.Zero);
        public static Wide Of(BigInteger v) => new(0, v);

        public Wide Negated => Infinite != 0 ? new(-Infinite, BigInteger.Zero) : new(0, -Value);

        // Scaled or divided by K, an infinity keeps its side for a positive K.
        public Wide Scaled(bool positive) => positive ? this : Negated;

        public int CompareTo(Wide other) =>
            Infinite != other.Infinite ? Infinite.CompareTo(other.Infinite)
            : Infinite != 0 ? 0
            : Value.CompareTo(other.Value);
    }

    private static Wide ReadWide(Activation engine, int reg)
    {
        Cell c = Arg(engine, reg);
        return c.Tag switch
        {
            Tag.Int => Wide.Of(c.AsInt),
            Tag.BigInt => Wide.Of(engine.AsBigInt(c)),
            Tag.Atom when c.AsAtomId == InfAtom => Wide.NegInf,
            Tag.Atom when c.AsAtomId == SupAtom => Wide.PosInf,
            _ => throw new PrologRuntimeException("type_error", "fd_bound"),
        };
    }

    private static BigInteger ReadWideInt(Activation engine, int reg)
    {
        Cell c = Arg(engine, reg);
        return c.Tag switch
        {
            Tag.Int => c.AsInt,
            Tag.BigInt => engine.AsBigInt(c),
            _ => throw new PrologRuntimeException("type_error", "integer"),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool WriteWide(Activation engine, int reg, Wide w) =>
        engine.UnifyRegisterWithCell(reg,
            w.Infinite < 0 ? Cell.Atom(InfAtom)
            : w.Infinite > 0 ? Cell.Atom(SupAtom)
            : engine.MakeBigInt(w.Value));

    // ---- comparisons ----

    private static int Compare(Activation engine) =>
        TryReadBound(engine, 0, out long a) && TryReadBound(engine, 1, out long b)
            ? a.CompareTo(b)
            : CompareWide(engine);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CompareWide(Activation engine) =>
        ReadWide(engine, 0).CompareTo(ReadWide(engine, 1));

    /// <summary>clpfd_ble(A, B): A ≤ B in the inf &lt; ints &lt; sup order.</summary>
    public static bool Ble(Activation engine) => Compare(engine) <= 0;

    /// <summary>clpfd_blt(A, B): A &lt; B.</summary>
    public static bool Blt(Activation engine) => Compare(engine) < 0;

    /// <summary>clpfd_bmin(A, B, -M): M = min(A, B).</summary>
    public static bool Bmin(Activation engine) =>
        engine.UnifyRegisterWithCell(2, Arg(engine, Compare(engine) <= 0 ? 0 : 1));

    /// <summary>clpfd_bmax(A, B, -M): M = max(A, B).</summary>
    public static bool Bmax(Activation engine) =>
        engine.UnifyRegisterWithCell(2, Arg(engine, Compare(engine) >= 0 ? 0 : 1));

    // ---- additive bound arithmetic (mins never carry sup, maxes never inf) ----

    // A + B, or A - B when subtract. The absorbing infinity wins over the
    // other one, and two inline integers cannot overflow a long.
    private static bool Add(Activation engine, bool subtract, long absorbing)
    {
        if (!TryReadBound(engine, 0, out long a) || !TryReadBound(engine, 1, out long b))
            return AddWide(engine, subtract, absorbing);
        bool finiteB = b != Inf && b != Sup;
        if (a != Inf && a != Sup && finiteB)
            return WriteFinite(engine, 2, subtract ? a - b : a + b);
        if (subtract && !finiteB) b = b == Inf ? Sup : Inf;
        if (a == absorbing || b == absorbing) return WriteInfinite(engine, 2, absorbing);
        return WriteInfinite(engine, 2, a == Inf || a == Sup ? a : b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AddWide(Activation engine, bool subtract, long absorbing)
    {
        Wide x = ReadWide(engine, 0), y = ReadWide(engine, 1);
        if (subtract) y = y.Negated;
        int absorbs = absorbing == Inf ? -1 : +1;
        if (x.Infinite == absorbs || y.Infinite == absorbs)
            return WriteWide(engine, 2, absorbs < 0 ? Wide.NegInf : Wide.PosInf);
        if (x.Infinite != 0) return WriteWide(engine, 2, x);
        if (y.Infinite != 0) return WriteWide(engine, 2, y);
        return WriteWide(engine, 2, Wide.Of(x.Value + y.Value));
    }

    /// <summary>clpfd_add_lo(A, B, -R): lower bound of A + B (inf-absorbing).</summary>
    public static bool AddLo(Activation engine) => Add(engine, subtract: false, Inf);

    /// <summary>clpfd_add_hi(A, B, -R): upper bound of A + B (sup-absorbing).</summary>
    public static bool AddHi(Activation engine) => Add(engine, subtract: false, Sup);

    /// <summary>clpfd_sub_lo(A, B, -R): lower bound of A − B.</summary>
    public static bool SubLo(Activation engine) => Add(engine, subtract: true, Inf);

    /// <summary>clpfd_sub_hi(A, B, -R): upper bound of A − B.</summary>
    public static bool SubHi(Activation engine) => Add(engine, subtract: true, Sup);

    /// <summary>clpfd_bneg(X, -Y): Y = −X (inf ↔ sup).</summary>
    public static bool Bneg(Activation engine)
    {
        if (!TryReadBound(engine, 0, out long x)) return BnegWide(engine);
        return x == Inf ? WriteInfinite(engine, 1, Sup)
             : x == Sup ? WriteInfinite(engine, 1, Inf)
             : WriteFinite(engine, 1, -x);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BnegWide(Activation engine) =>
        WriteWide(engine, 1, ReadWide(engine, 0).Negated);

    // ---- scaling by an integer constant ----

    private const long Fits32 = 1L << 31;

    /// <summary>clpfd_bmul(B, K, -R): B × the nonzero integer constant K.</summary>
    public static bool Bmul(Activation engine)
    {
        if (!TryReadBound(engine, 0, out long b) || !TryReadInt(engine, 1, out long k))
            return BmulWide(engine);
        if (b == Inf || b == Sup)
            return WriteInfinite(engine, 2, k > 0 ? b : b == Inf ? Sup : Inf);
        long r = unchecked(b * k);
        // Two 32-bit factors cannot overflow; otherwise the product did not
        // wrap exactly when dividing it gives the factor back.
        if ((b > -Fits32 && b < Fits32 && k > -Fits32 && k < Fits32) || b == 0 || r / b == k)
            return WriteFinite(engine, 2, r);
        return BmulWide(engine);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BmulWide(Activation engine)
    {
        Wide b = ReadWide(engine, 0);
        BigInteger k = ReadWideInt(engine, 1);
        return WriteWide(engine, 2, b.Infinite != 0 ? b.Scaled(k.Sign > 0) : Wide.Of(b.Value * k));
    }

    /// <summary>clpfd_bfloordiv(C, K, -R): ⌊C / K⌋ for nonzero integer K.</summary>
    public static bool Bfloordiv(Activation engine) => Divide(engine, ceiling: false);

    /// <summary>clpfd_bceildiv(C, K, -R): ⌈C / K⌉ for nonzero integer K.</summary>
    public static bool Bceildiv(Activation engine) => Divide(engine, ceiling: true);

    private static bool Divide(Activation engine, bool ceiling)
    {
        if (!TryReadBound(engine, 0, out long c) || !TryReadInt(engine, 1, out long k))
            return DivideWide(engine, ceiling);
        if (k == 0) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
        if (c == Inf || c == Sup)
            return WriteInfinite(engine, 2, k > 0 ? c : c == Inf ? Sup : Inf);
        // The least inline integer over -1 is one past the greatest: a long
        // holds it, an Int cell does not.
        return WriteFinite(engine, 2, ceiling ? CeilDiv(c, k) : FloorDiv(c, k));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool DivideWide(Activation engine, bool ceiling)
    {
        Wide c = ReadWide(engine, 0);
        BigInteger k = ReadWideInt(engine, 1);
        if (k.IsZero) throw new PrologRuntimeException("evaluation_error", "zero_divisor");
        if (c.Infinite != 0) return WriteWide(engine, 2, c.Scaled(k.Sign > 0));
        BigInteger q = BigInteger.DivRem(c.Value, k, out BigInteger rem);
        if (!rem.IsZero)
        {
            // DivRem truncates toward zero: below the quotient when the signs
            // agree, above it when they differ.
            bool sameSign = (c.Value.Sign < 0) == (k.Sign < 0);
            if (ceiling && sameSign) q += BigInteger.One;
            else if (!ceiling && !sameSign) q -= BigInteger.One;
        }
        return WriteWide(engine, 2, Wide.Of(q));
    }

    private static long FloorDiv(long a, long b)
    {
        long q = a / b, r = a % b;
        if (r != 0 && ((a ^ b) < 0)) q--;
        return q;
    }

    private static long CeilDiv(long a, long b)
    {
        long q = a / b, r = a % b;
        if (r != 0 && ((a ^ b) > 0)) q++;
        return q;
    }

    /// <summary>Registers the bound primitives. Called once at builtin setup.</summary>
    public static void Register()
    {
        InfAtom = AtomTable.Intern("inf", permanent: true).Id;
        SupAtom = AtomTable.Intern("sup", permanent: true).Id;
        BuiltinsRegistry.Register("clpfd_ble", 2, Ble);
        BuiltinsRegistry.Register("clpfd_blt", 2, Blt);
        BuiltinsRegistry.Register("clpfd_bmin", 3, Bmin);
        BuiltinsRegistry.Register("clpfd_bmax", 3, Bmax);
        BuiltinsRegistry.Register("clpfd_add_lo", 3, AddLo);
        BuiltinsRegistry.Register("clpfd_add_hi", 3, AddHi);
        BuiltinsRegistry.Register("clpfd_sub_lo", 3, SubLo);
        BuiltinsRegistry.Register("clpfd_sub_hi", 3, SubHi);
        BuiltinsRegistry.Register("clpfd_bneg", 2, Bneg);
        BuiltinsRegistry.Register("clpfd_bmul", 3, Bmul);
        BuiltinsRegistry.Register("clpfd_bfloordiv", 3, Bfloordiv);
        BuiltinsRegistry.Register("clpfd_bceildiv", 3, Bceildiv);
    }
}
