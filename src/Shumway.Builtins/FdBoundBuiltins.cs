using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Native bound-arithmetic primitives for the CLP(FD) library (Phase 28).
/// A "bound" is an integer or one of the atoms <c>inf</c> (−∞) / <c>sup</c>
/// (+∞). The clpfd library used to implement these in Prolog — a long chain of
/// <c>A == inf</c> / <c>B == sup</c> tests per operation — which profiling
/// showed dominated finite-domain solving (≈1.36M <c>==/2</c> calls, the single
/// biggest cost on the alpha benchmark). Here a bound is a plain <c>long</c>
/// with <see cref="long.MinValue"/> = inf and <see cref="long.MaxValue"/> = sup,
/// so the comparisons and arithmetic are native. The semantics match the
/// retired Prolog definitions exactly (in particular <c>clpfd_ble</c> collapses
/// to a single <c>&lt;=</c> because the sentinels order correctly against every
/// integer).
///
/// <para>These are registered under the same names the clpfd module calls, so
/// removing the Prolog clauses makes the module-local calls fall through to
/// these builtins (the standard local → builtin resolution order).</para>
/// </summary>
public static class FdBoundBuiltins
{
    private const long Inf = long.MinValue;
    private const long Sup = long.MaxValue;

    private static readonly int InfAtom = AtomTable.Intern("inf", permanent: true).Id;
    private static readonly int SupAtom = AtomTable.Intern("sup", permanent: true).Id;

    /// <summary>Reads argument <paramref name="reg"/> as a bound: an integer, or
    /// the atoms inf/sup mapped to the long sentinels.</summary>
    private static long ReadBound(Engine engine, int reg)
    {
        Cell c = engine.GetRegister(reg);
        if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        return c.Tag switch
        {
            Tag.Int => c.AsInt,
            Tag.Atom when c.AsAtomId == InfAtom => Inf,
            Tag.Atom when c.AsAtomId == SupAtom => Sup,
            _ => throw new PrologRuntimeException("type_error", "fd_bound"),
        };
    }

    private static long ReadInt(Engine engine, int reg)
    {
        Cell c = engine.GetRegister(reg);
        if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        if (c.Tag != Tag.Int) throw new PrologRuntimeException("type_error", "integer");
        return c.AsInt;
    }

    private static bool WriteBound(Engine engine, int reg, long v)
    {
        Cell cell = v switch
        {
            Inf => Cell.Atom(InfAtom),
            Sup => Cell.Atom(SupAtom),
            _ => Cell.Int(v),
        };
        return engine.UnifyRegisterWithCell(reg, cell);
    }

    // ---- comparisons ----

    /// <summary>clpfd_ble(A, B): A ≤ B in the inf &lt; ints &lt; sup order.</summary>
    public static bool Ble(Engine engine) => ReadBound(engine, 0) <= ReadBound(engine, 1);

    /// <summary>clpfd_blt(A, B): A &lt; B.</summary>
    public static bool Blt(Engine engine) => ReadBound(engine, 0) < ReadBound(engine, 1);

    /// <summary>clpfd_bmin(A, B, -M): M = min(A, B).</summary>
    public static bool Bmin(Engine engine)
    {
        long a = ReadBound(engine, 0), b = ReadBound(engine, 1);
        return WriteBound(engine, 2, a <= b ? a : b);
    }

    /// <summary>clpfd_bmax(A, B, -M): M = max(A, B).</summary>
    public static bool Bmax(Engine engine)
    {
        long a = ReadBound(engine, 0), b = ReadBound(engine, 1);
        return WriteBound(engine, 2, a >= b ? a : b);
    }

    // ---- additive bound arithmetic (mins never carry sup, maxes never inf) ----

    /// <summary>clpfd_add_lo(A, B, -R): lower bound of A + B (inf-absorbing).</summary>
    public static bool AddLo(Engine engine)
    {
        long a = ReadBound(engine, 0), b = ReadBound(engine, 1);
        return WriteBound(engine, 2, (a == Inf || b == Inf) ? Inf : a + b);
    }

    /// <summary>clpfd_add_hi(A, B, -R): upper bound of A + B (sup-absorbing).</summary>
    public static bool AddHi(Engine engine)
    {
        long a = ReadBound(engine, 0), b = ReadBound(engine, 1);
        return WriteBound(engine, 2, (a == Sup || b == Sup) ? Sup : a + b);
    }

    /// <summary>clpfd_sub_lo(A, B, -R): lower bound of A − B.</summary>
    public static bool SubLo(Engine engine)
    {
        long a = ReadBound(engine, 0), b = ReadBound(engine, 1);
        return WriteBound(engine, 2, (a == Inf || b == Sup) ? Inf : a - b);
    }

    /// <summary>clpfd_sub_hi(A, B, -R): upper bound of A − B.</summary>
    public static bool SubHi(Engine engine)
    {
        long a = ReadBound(engine, 0), b = ReadBound(engine, 1);
        return WriteBound(engine, 2, (a == Sup || b == Inf) ? Sup : a - b);
    }

    /// <summary>clpfd_bneg(X, -Y): Y = −X (inf ↔ sup).</summary>
    public static bool Bneg(Engine engine)
    {
        long x = ReadBound(engine, 0);
        return WriteBound(engine, 1, x == Inf ? Sup : x == Sup ? Inf : -x);
    }

    /// <summary>clpfd_bmul(B, K, -R): B × the nonzero integer constant K.</summary>
    public static bool Bmul(Engine engine)
    {
        long b = ReadBound(engine, 0), k = ReadInt(engine, 1);
        long r = b == Inf ? (k > 0 ? Inf : Sup)
               : b == Sup ? (k > 0 ? Sup : Inf)
               : b * k;
        return WriteBound(engine, 2, r);
    }

    /// <summary>clpfd_bfloordiv(C, K, -R): ⌊C / K⌋ for nonzero integer K.</summary>
    public static bool Bfloordiv(Engine engine)
    {
        long c = ReadBound(engine, 0), k = ReadInt(engine, 1);
        long r = c == Inf ? (k > 0 ? Inf : Sup)
               : c == Sup ? (k > 0 ? Sup : Inf)
               : FloorDiv(c, k);
        return WriteBound(engine, 2, r);
    }

    /// <summary>clpfd_bceildiv(C, K, -R): ⌈C / K⌉ for nonzero integer K.</summary>
    public static bool Bceildiv(Engine engine)
    {
        long c = ReadBound(engine, 0), k = ReadInt(engine, 1);
        long r = c == Inf ? (k > 0 ? Inf : Sup)
               : c == Sup ? (k > 0 ? Sup : Inf)
               : CeilDiv(c, k);
        return WriteBound(engine, 2, r);
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
