using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Native domain operations for CLP(FD). A domain lives in the engine's
/// foreign-object table as a <see cref="ClpfdDomain"/> and is named by a
/// <c>Foreign</c> cell; these builtins read/produce those cells, keeping
/// interval walking (the dominant cost of finite-domain solving) out of
/// interpreted Prolog. Bounds (min/max/cut points) round-trip as integers or
/// the atoms <c>inf</c>/<c>sup</c>; values are integers.
/// </summary>
public static class ClpfdDomainBuiltins
{
    private const long Inf = ClpfdDomain.Inf;
    private const long Sup = ClpfdDomain.Sup;
    private const long SizeInfinite = 1000000000;

    // Interned in Register(), not in field initializers: a beforefieldinit
    // cctor runs at an unspecified time that DIFFERS between runtimes (Mono
    // interns these mid-registration, CoreCLR later), which shuffles early
    // ids per platform — and the baked prelude wasm group validates ids.
    private static int InfAtom, SupAtom, MinusFunctor;

    // ADR-051 -- the heap form's names. Interned with the rest, for the same
    // reason: a beforefieldinit cctor runs at a time that differs per runtime
    // and the baked prelude wasm group validates ids.
    private static int DomAtom, EmptyDomAtom;

    // ---- argument helpers ----

    private static Cell Arg(Activation engine, int reg)
    {
        Cell c = engine.GetRegister(reg);
        return c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;
    }

    private static ClpfdDomain Dom(Activation engine, int reg) =>
        ReadDom(engine, Arg(engine, reg));

    private static bool WriteDom(Activation engine, int reg, ClpfdDomain d) =>
        engine.UnifyRegisterWithCell(reg, DomCell(engine, d));

    // ---- the heap form (ADR-051) ----
    //
    // '$fd_dom'(L1, H1, ..., Lk, Hk) for a domain of k intervals, and the atom
    // '$fd_dom_empty' for the empty one. Bounds are Int cells, or the atoms
    // inf and sup, which is what BoundCell already produced at the Prolog
    // boundary. A term rather than a managed object so a wasm module can read
    // it out of linear memory, and so backtracking reclaims it.
    //
    // Phase 0 keeps the ALGORITHMS on long[]: this converts at the boundary
    // and ClpfdDomain is untouched. The operations move onto cells next, and
    // the conversions disappear with them.

    /// <summary>The domain of k intervals as a heap term.</summary>
    private static Cell DomCell(Activation engine, ClpfdDomain d)
    {
        long[] iv = d.Bounds;
        if (iv.Length == 0) return Cell.Atom(EmptyDomAtom);
        int s = engine.AllocateHeap(1 + iv.Length);
        engine.SetHeap(s, Cell.Functor(DomFunctor(iv.Length)));
        for (int i = 0; i < iv.Length; i++)
            engine.SetHeap(s + 1 + i, BoundCell(iv[i]));
        return Cell.Str(s);
    }

    /// <summary>The domain a cell names. Anything that is not one of the two
    /// shapes is the type error the builtins have always raised.</summary>
    private static ClpfdDomain ReadDom(Activation engine, Cell c)
    {
        if (c.Tag == Tag.Atom && c.AsAtomId == EmptyDomAtom) return ClpfdDomain.Empty;
        if (c.Tag != Tag.Str) throw new PrologRuntimeException("type_error", "fd_domain");
        int s = c.AsHeapIndex;
        Cell f = engine.GetHeap(s);
        if (f.Tag != Tag.Functor)
            throw new PrologRuntimeException("type_error", "fd_domain");
        var (aid, arity) = FunctorTable.Lookup(f.AsFunctorId);
        if (aid != DomAtom || arity == 0 || (arity & 1) != 0)
            throw new PrologRuntimeException("type_error", "fd_domain");
        var iv = new long[arity];
        for (int i = 0; i < arity; i++)
        {
            Cell b = engine.GetHeap(s + 1 + i);
            if (b.Tag == Tag.Ref) b = engine.GetHeap(engine.Deref(b.AsHeapIndex));
            iv[i] = b.Tag switch
            {
                Tag.Int => b.AsInt,
                Tag.Atom when b.AsAtomId == InfAtom => Inf,
                Tag.Atom when b.AsAtomId == SupAtom => Sup,
                _ => throw new PrologRuntimeException("type_error", "fd_bound"),
            };
        }
        return ClpfdDomain.FromBounds(iv);
    }

    // One interned functor per interval count, not per domain. Grown on
    // demand: a program's widest fragmentation is what bounds the table.
    private static int[] _domFunctors = System.Array.Empty<int>();

    private static int DomFunctor(int arity)
    {
        var table = _domFunctors;
        if (arity < table.Length && table[arity] != 0) return table[arity];
        int fid = FunctorTable.Intern(DomAtom, arity);
        if (arity >= table.Length)
        {
            var grown = new int[System.Math.Max(arity + 1, 8)];
            System.Array.Copy(table, grown, table.Length);
            table = grown;
        }
        table[arity] = fid;
        _domFunctors = table;
        return fid;
    }

    private static long ReadBound(Activation engine, int reg)
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

    private static long ReadInt(Activation engine, int reg)
    {
        Cell c = Arg(engine, reg);
        if (c.Tag != Tag.Int) throw new PrologRuntimeException("type_error", "integer");
        return c.AsInt;
    }

    private static bool WriteBound(Activation engine, int reg, long v)
    {
        Cell cell = v == Inf ? Cell.Atom(InfAtom) : v == Sup ? Cell.Atom(SupAtom) : Cell.Int(v);
        return engine.UnifyRegisterWithCell(reg, cell);
    }

    // ---- builtins ----

    /// <summary>$dom_new(+Lo, +Hi, -Dom): the interval domain [Lo, Hi].</summary>
    public static bool New(Activation engine) =>
        WriteDom(engine, 2, ClpfdDomain.Interval(ReadBound(engine, 0), ReadBound(engine, 1)));

    /// <summary>$dom_universal(-Dom): [inf, sup].</summary>
    public static bool UniversalB(Activation engine) => WriteDom(engine, 0, ClpfdDomain.Universal);

    /// <summary>$dom_min(+Dom, -Min) / $dom_max(+Dom, -Max). Fail on empty.</summary>
    public static bool Min(Activation engine)
    {
        var d = Dom(engine, 0);
        return !d.IsEmpty && WriteBound(engine, 1, d.Min);
    }

    public static bool Max(Activation engine)
    {
        var d = Dom(engine, 0);
        return !d.IsEmpty && WriteBound(engine, 1, d.Max);
    }

    /// <summary>$dom_above(+Dom, +B, -Dom2): part of Dom at or below B.</summary>
    public static bool Above(Activation engine) =>
        WriteDom(engine, 2, Dom(engine, 0).Above(ReadBound(engine, 1)));

    /// <summary>$dom_below(+Dom, +B, -Dom2): part of Dom at or above B.</summary>
    public static bool Below(Activation engine) =>
        WriteDom(engine, 2, Dom(engine, 0).Below(ReadBound(engine, 1)));

    /// <summary>$dom_isect(+D1, +D2, -D3): intersection.</summary>
    public static bool Isect(Activation engine) =>
        WriteDom(engine, 2, Dom(engine, 0).Intersect(Dom(engine, 1)));

    /// <summary>$dom_union(+D1, +D2, -D3): union (merging adjacency).</summary>
    public static bool Union(Activation engine) =>
        WriteDom(engine, 2, Dom(engine, 0).Union(Dom(engine, 1)));

    /// <summary>$dom_del(+Dom, +V, -Dom2): remove the integer value V.
    ///
    /// <para>When V was not in Dom the domain is UNCHANGED, and then Dom2 is
    /// the incoming CELL and not a fresh one naming the same object. The two
    /// are equivalent to Prolog, and the difference is what makes the pair
    /// this is half of decidable without leaving a wasm module:
    /// clpfd_narrow's first test is '$dom_same'(New, Old), which holds
    /// exactly when nothing was removed, and identical cells can be compared
    /// where interval lists cannot. Measured in a browser on
    /// queens_fd(9): 190,026 $dom_del and 190,035 $dom_same, 82% of all the
    /// builtin exits in the run.</para>
    ///
    /// <para>It also stops the foreign table growing an entry per no-op.
    /// </para></summary>
    public static bool Del(Activation engine)
    {
        Cell incoming = Arg(engine, 0);
        var d = Dom(engine, 0);
        var without = d.Without(ReadInt(engine, 1));
        return ReferenceEquals(without, d)
            ? engine.UnifyRegisterWithCell(2, incoming)
            : WriteDom(engine, 2, without);
    }

    /// <summary>$dom_size(+Dom, -N): value count (or a big sentinel if infinite).</summary>
    public static bool Size(Activation engine) =>
        engine.UnifyRegisterWithCell(1, Cell.Int(Dom(engine, 0).Size(SizeInfinite)));

    /// <summary>$dom_contains(+Dom, +V): V is an integer in Dom.</summary>
    public static bool Contains(Activation engine) => Dom(engine, 0).Contains(ReadInt(engine, 1));

    /// <summary>$dom_empty(+Dom): Dom has no values.</summary>
    public static bool IsEmptyB(Activation engine) => Dom(engine, 0).IsEmpty;

    /// <summary>$dom_singleton(+Dom, -V): Dom is exactly {V}.</summary>
    public static bool Singleton(Activation engine)
    {
        var d = Dom(engine, 0);
        return d.TrySingleton(out long v) && engine.UnifyRegisterWithCell(1, Cell.Int(v));
    }

    /// <summary>$dom_same(+D1, +D2): the two domains are equal.</summary>
    public static bool Same(Activation engine) => Dom(engine, 0).SameAs(Dom(engine, 1));

    /// <summary>$dom_values(+Dom, -List): the values of a finite Dom, ascending.</summary>
    public static bool Values(Activation engine)
    {
        var d = Dom(engine, 0);
        var vals = new System.Collections.Generic.List<long>();
        foreach (long v in d.Values()) vals.Add(v);
        return engine.UnifyRegisterWithHeapAt(1,
            BuildList(engine, vals.Count, i => Cell.Int(vals[i])));
    }

    /// <summary>$dom_intervals(+Dom, -List): a list of L-H interval terms, for
    /// the residual-constraint projection.</summary>
    public static bool Intervals(Activation engine)
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
    private static int BuildList(Activation engine, int n, System.Func<int, Cell> elem)
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

    /// <summary>$fd_hall(+Vars, +Doms, -Applies): native Hall-interval pruning
    /// for all_distinct. Vars and Doms are parallel lists (Doms[i] is the domain
    /// object of Vars[i]). Fails on a pigeonhole violation (more variables than
    /// values in some interval). Otherwise unifies Applies with a list of
    /// <c>V-NewDom</c> pairs for every variable whose domain a saturated Hall
    /// interval shrank — the Prolog caller narrows each (re-propagating), so the
    /// O(n^3) interval search runs natively while narrowing stays in the engine.</summary>
    public static bool Hall(Activation engine)
    {
        var vars = ReadListCells(engine, 0);
        var domCells = ReadListCells(engine, 1);
        int n = vars.Count;
        var work = new ClpfdDomain[n];
        for (int i = 0; i < n; i++)
            work[i] = ReadDom(engine, domCells[i]);
        var orig = (ClpfdDomain[])work.Clone();

        // Candidate Hall bounds: the finite minima (lo) and maxima (hi).
        for (int li = 0; li < n; li++)
        {
            if (work[li].IsEmpty) return false;
            long lo = work[li].Min;
            if (lo == ClpfdDomain.Inf) continue;
            for (int hj = 0; hj < n; hj++)
            {
                long hi = work[hj].Max;
                if (hi == ClpfdDomain.Sup || lo > hi) continue;
                int count = 0;
                for (int k = 0; k < n; k++)
                    if (work[k].Within(lo, hi)) count++;
                long size = hi - lo + 1;
                if (count > size) return false;          // pigeonhole: unsatisfiable
                if (count == size)
                    for (int k = 0; k < n; k++)
                        if (!work[k].Within(lo, hi))
                            work[k] = work[k].RemoveInterval(lo, hi);
            }
        }

        // Emit V-NewDom for every variable whose domain changed.
        var changed = new System.Collections.Generic.List<int>();
        for (int i = 0; i < n; i++)
            if (!work[i].SameAs(orig[i])) changed.Add(i);

        return engine.UnifyRegisterWithHeapAt(2,
            BuildList(engine, changed.Count, j =>
            {
                int i = changed[j];
                // Reference the variable (a Ref to its home) rather than
                // embedding the deref'd AttVar/Int cell, so the Prolog caller's
                // clpfd_narrow sees the real variable.
                Cell vRef = vars[i].Tag is Tag.AttVar or Tag.Ref
                    ? Cell.Ref(vars[i].AsHeapIndex) : vars[i];
                int s = engine.AllocateHeap(3);
                engine.SetHeap(s, Cell.Functor(MinusFunctor));
                engine.SetHeap(s + 1, vRef);
                engine.SetHeap(s + 2, DomCell(engine, work[i]));
                return Cell.Str(s);
            }));
    }

    /// <summary>Reads a proper Prolog list at the register into its (deref'd)
    /// element cells.</summary>
    private static System.Collections.Generic.List<Cell> ReadListCells(Activation engine, int reg)
    {
        var items = new System.Collections.Generic.List<Cell>();
        Cell cur = Arg(engine, reg);
        while (cur.Tag == Tag.Lis)
        {
            int h = cur.AsHeapIndex;
            Cell head = engine.GetHeap(h);
            if (head.Tag == Tag.Ref) head = engine.GetHeap(engine.Deref(head.AsHeapIndex));
            items.Add(head);
            cur = engine.GetHeap(engine.Deref(h + 1));
        }
        return items;
    }

    public static void Register()
    {
        InfAtom = AtomTable.Intern("inf", permanent: true).Id;
        SupAtom = AtomTable.Intern("sup", permanent: true).Id;
        MinusFunctor = FunctorTable.Intern(AtomTable.Intern("-", permanent: true).Id, 2);
        DomAtom = AtomTable.Intern("$fd_dom", permanent: true).Id;
        EmptyDomAtom = AtomTable.Intern("$fd_dom_empty", permanent: true).Id;
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
        BuiltinsRegistry.Register("$fd_hall", 3, Hall);
    }
}
