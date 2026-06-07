using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Native domain operations for CLP(FD) (Phase 28). A domain lives in the
/// engine's foreign-object table as a <see cref="ClpfdDomain"/> and is named by
/// a <c>Foreign</c> cell; these builtins read/produce those cells. They replace
/// the interpreted-Prolog interval-list walking that profiling showed dominated
/// finite-domain solving. Bounds (min/max/cut points) round-trip as integers or
/// the atoms <c>inf</c>/<c>sup</c>; values are integers.
/// </summary>
public static class ClpfdDomainBuiltins
{
    private const long Inf = ClpfdDomain.Inf;
    private const long Sup = ClpfdDomain.Sup;
    private const long SizeInfinite = 1000000000;

    private static readonly int InfAtom = AtomTable.Intern("inf", permanent: true).Id;
    private static readonly int SupAtom = AtomTable.Intern("sup", permanent: true).Id;
    private static readonly int MinusFunctor =
        FunctorTable.Intern(AtomTable.Intern("-", permanent: true).Id, 2);

    // ---- argument helpers ----

    private static Cell Arg(Engine engine, int reg)
    {
        Cell c = engine.GetRegister(reg);
        return c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;
    }

    private static ClpfdDomain Dom(Engine engine, int reg) =>
        engine.AsForeign<ClpfdDomain>(Arg(engine, reg))
            ?? throw new PrologRuntimeException("type_error", "fd_domain");

    private static bool WriteDom(Engine engine, int reg, ClpfdDomain d) =>
        engine.UnifyRegisterWithCell(reg, engine.MakeForeign(d));

    private static long ReadBound(Engine engine, int reg)
    {
        Cell c = Arg(engine, reg);
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
        Cell c = Arg(engine, reg);
        if (c.Tag != Tag.Int) throw new PrologRuntimeException("type_error", "integer");
        return c.AsInt;
    }

    private static bool WriteBound(Engine engine, int reg, long v)
    {
        Cell cell = v == Inf ? Cell.Atom(InfAtom) : v == Sup ? Cell.Atom(SupAtom) : Cell.Int(v);
        return engine.UnifyRegisterWithCell(reg, cell);
    }

    // ---- builtins ----

    /// <summary>$dom_new(+Lo, +Hi, -Dom): the interval domain [Lo, Hi].</summary>
    public static bool New(Engine engine) =>
        WriteDom(engine, 2, ClpfdDomain.Interval(ReadBound(engine, 0), ReadBound(engine, 1)));

    /// <summary>$dom_universal(-Dom): [inf, sup].</summary>
    public static bool UniversalB(Engine engine) => WriteDom(engine, 0, ClpfdDomain.Universal);

    /// <summary>$dom_min(+Dom, -Min) / $dom_max(+Dom, -Max). Fail on empty.</summary>
    public static bool Min(Engine engine)
    {
        var d = Dom(engine, 0);
        return !d.IsEmpty && WriteBound(engine, 1, d.Min);
    }

    public static bool Max(Engine engine)
    {
        var d = Dom(engine, 0);
        return !d.IsEmpty && WriteBound(engine, 1, d.Max);
    }

    /// <summary>$dom_above(+Dom, +B, -Dom2): part of Dom at or below B.</summary>
    public static bool Above(Engine engine) =>
        WriteDom(engine, 2, Dom(engine, 0).Above(ReadBound(engine, 1)));

    /// <summary>$dom_below(+Dom, +B, -Dom2): part of Dom at or above B.</summary>
    public static bool Below(Engine engine) =>
        WriteDom(engine, 2, Dom(engine, 0).Below(ReadBound(engine, 1)));

    /// <summary>$dom_isect(+D1, +D2, -D3): intersection.</summary>
    public static bool Isect(Engine engine) =>
        WriteDom(engine, 2, Dom(engine, 0).Intersect(Dom(engine, 1)));

    /// <summary>$dom_union(+D1, +D2, -D3): union (merging adjacency).</summary>
    public static bool Union(Engine engine) =>
        WriteDom(engine, 2, Dom(engine, 0).Union(Dom(engine, 1)));

    /// <summary>$dom_del(+Dom, +V, -Dom2): remove the integer value V.</summary>
    public static bool Del(Engine engine) =>
        WriteDom(engine, 2, Dom(engine, 0).Without(ReadInt(engine, 1)));

    /// <summary>$dom_size(+Dom, -N): value count (or a big sentinel if infinite).</summary>
    public static bool Size(Engine engine) =>
        engine.UnifyRegisterWithCell(1, Cell.Int(Dom(engine, 0).Size(SizeInfinite)));

    /// <summary>$dom_contains(+Dom, +V): V is an integer in Dom.</summary>
    public static bool Contains(Engine engine) => Dom(engine, 0).Contains(ReadInt(engine, 1));

    /// <summary>$dom_empty(+Dom): Dom has no values.</summary>
    public static bool IsEmptyB(Engine engine) => Dom(engine, 0).IsEmpty;

    /// <summary>$dom_singleton(+Dom, -V): Dom is exactly {V}.</summary>
    public static bool Singleton(Engine engine)
    {
        var d = Dom(engine, 0);
        return d.TrySingleton(out long v) && engine.UnifyRegisterWithCell(1, Cell.Int(v));
    }

    /// <summary>$dom_same(+D1, +D2): the two domains are equal.</summary>
    public static bool Same(Engine engine) => Dom(engine, 0).SameAs(Dom(engine, 1));

    /// <summary>$dom_values(+Dom, -List): the values of a finite Dom, ascending.</summary>
    public static bool Values(Engine engine)
    {
        var d = Dom(engine, 0);
        var vals = new System.Collections.Generic.List<long>();
        foreach (long v in d.Values()) vals.Add(v);
        return engine.UnifyRegisterWithHeapAt(1,
            BuildList(engine, vals.Count, i => Cell.Int(vals[i])));
    }

    /// <summary>$dom_intervals(+Dom, -List): a list of L-H interval terms, for
    /// the residual-constraint projection.</summary>
    public static bool Intervals(Engine engine)
    {
        var ivs = Dom(engine, 0).Intervals();
        return engine.UnifyRegisterWithHeapAt(1,
            BuildList(engine, ivs.Count, i =>
            {
                int s = engine.AllocateHeap(3);
                engine.SetHeap(s, Cell.Functor(MinusFunctor));
                engine.SetHeap(s + 1, BoundCell(ivs[i].Lo));
                engine.SetHeap(s + 2, BoundCell(ivs[i].Hi));
                return Cell.Str(s);
            }));
    }

    private static Cell BoundCell(long v) =>
        v == Inf ? Cell.Atom(InfAtom) : v == Sup ? Cell.Atom(SupAtom) : Cell.Int(v);

    /// <summary>Builds a proper list of <paramref name="n"/> elements (the i-th
    /// from <paramref name="elem"/>) and returns the heap index of its head.
    /// <paramref name="elem"/> may itself allocate heap, so each element is
    /// materialised before its cons cell is laid down.</summary>
    private static int BuildList(Engine engine, int n, System.Func<int, Cell> elem)
    {
        if (n == 0)
        {
            int e = engine.AllocateHeap(1);
            engine.SetHeap(e, Cell.Atom(AtomTable.EmptyListId));
            return e;
        }
        // Materialise the element cells first (they may allocate), then the spine.
        var cells = new Cell[n];
        for (int i = 0; i < n; i++) cells[i] = elem(i);
        int start = engine.AllocateHeap(2 * n + 1);
        for (int i = 0; i < n; i++)
        {
            int lisIdx = start + 2 * i;
            engine.SetHeap(lisIdx, Cell.Lis(lisIdx + 1));
            engine.SetHeap(lisIdx + 1, cells[i]);
        }
        engine.SetHeap(start + 2 * n, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    public static void Register()
    {
        BuiltinsRegistry.Register("$dom_new", 3, New);
        BuiltinsRegistry.Register("$dom_universal", 1, UniversalB);
        BuiltinsRegistry.Register("$dom_min", 2, Min);
        BuiltinsRegistry.Register("$dom_max", 2, Max);
        BuiltinsRegistry.Register("$dom_above", 3, Above);
        BuiltinsRegistry.Register("$dom_below", 3, Below);
        BuiltinsRegistry.Register("$dom_isect", 3, Isect);
        BuiltinsRegistry.Register("$dom_union", 3, Union);
        BuiltinsRegistry.Register("$dom_del", 3, Del);
        BuiltinsRegistry.Register("$dom_size", 2, Size);
        BuiltinsRegistry.Register("$dom_contains", 2, Contains);
        BuiltinsRegistry.Register("$dom_empty", 1, IsEmptyB);
        BuiltinsRegistry.Register("$dom_singleton", 2, Singleton);
        BuiltinsRegistry.Register("$dom_same", 2, Same);
        BuiltinsRegistry.Register("$dom_values", 2, Values);
        BuiltinsRegistry.Register("$dom_intervals", 2, Intervals);
    }
}
