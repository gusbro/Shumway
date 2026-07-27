using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The ISO type-testing predicates. Each one inspects its single argument
/// (after dereferencing variable indirection) and returns true iff the
/// argument has the requested shape. None of them perform unification or
/// mutate state.
/// </summary>
public static class TypeBuiltins
{
    /// <summary><c>var(X)</c> — X is an unbound variable. An attributed
    /// variable counts as unbound — it has no value yet,
    /// only attributes.</summary>
    public static bool IsVar(Activation engine)
    {
        var t = Tag0(engine);
        return t == Tag.Ref || t == Tag.AttVar;
    }

    /// <summary><c>nonvar(X)</c> — X is bound to a non-variable term.
    /// An attributed variable is still a variable, so <c>nonvar</c>
    /// rejects it.</summary>
    public static bool IsNonVar(Activation engine)
    {
        var t = Tag0(engine);
        return t != Tag.Ref && t != Tag.AttVar;
    }

    /// <summary><c>attvar(X)</c> — X is an attributed variable.
    /// False for plain unbound variables and for any bound term.</summary>
    public static bool IsAttVar(Activation engine) => Tag0(engine) == Tag.AttVar;

    /// <summary><c>atom(X)</c> — X is bound to an atom (including <c>[]</c>
    /// and <c>{}</c>, which are atoms in ISO).</summary>
    public static bool IsAtom(Activation engine) => Tag0(engine) == Tag.Atom;

    /// <summary><c>integer(X)</c> — X is an integer cell. Both inline ints
    /// and BigInteger cells satisfy the predicate.</summary>
    public static bool IsInteger(Activation engine)
    {
        var t = Tag0(engine);
        return t == Tag.Int || t == Tag.BigInt;
    }

    /// <summary><c>float(X)</c> — X is a float cell.</summary>
    public static bool IsFloat(Activation engine) => Tag0(engine) == Tag.Float;

    /// <summary><c>rational(X)</c> — X is a rational (ADR-039). An integer is a
    /// rational with denominator 1, so integers satisfy it too (SWI/Scryer).</summary>
    public static bool IsRational(Activation engine)
    {
        var t = Tag0(engine);
        return t is Tag.Rational or Tag.Int or Tag.BigInt;
    }

    /// <summary><c>number(X)</c> — X is an integer (inline or big), a float,
    /// or a rational.</summary>
    public static bool IsNumber(Activation engine)
    {
        var t = Tag0(engine);
        return t is Tag.Int or Tag.BigInt or Tag.Float or Tag.Rational;
    }

    /// <summary><c>atomic(X)</c> — X is a non-compound, non-variable term
    /// (atom, integer, bigint, rational, float, string, PSTR).</summary>
    public static bool IsAtomic(Activation engine)
    {
        var t = Tag0(engine);
        return t is Tag.Atom or Tag.Int or Tag.BigInt or Tag.Rational or Tag.Float or Tag.String or Tag.Pstr;
    }

    /// <summary><c>compound(X)</c> — X is a compound term (STR or non-empty
    /// LIS). An empty list (the atom <c>[]</c>) is NOT compound.</summary>
    public static bool IsCompound(Activation engine)
    {
        var t = Tag0(engine);
        return t is Tag.Str or Tag.Lis;
    }

    /// <summary><c>callable(X)</c> — X is an atom or a compound term (ISO
    /// §8.3.6). An unbound variable, number, string or PSTR is not callable.</summary>
    public static bool IsCallable(Activation engine)
    {
        var t = Tag0(engine);
        return t is Tag.Atom or Tag.Str or Tag.Lis;
    }

    /// <summary><c>is_list(X)</c> — X is a proper list: a cons chain
    /// terminated by the empty-list atom. An unbound tail makes it a partial
    /// list — fails. An atom other than <c>[]</c> at the tail — fails.</summary>
    public static bool IsList(Activation engine)
    {
        Cell cell = engine.GetRegister(0);
        while (true)
        {
            cell = Resolve(engine, cell);
            switch (cell.Tag)
            {
                case Tag.Atom when cell.AsAtomId == AtomTable.EmptyListId:
                    return true;
                case Tag.Lis:
                    int headIdx = cell.AsHeapIndex;
                    cell = engine.GetHeap(headIdx + 1);
                    continue;
                default:
                    return false;
            }
        }
    }

    /// <summary><c>ground(X)</c> — X contains no unbound variables. Walks
    /// the heap representation recursively; on the first dereferenced
    /// REF still pointing at itself the predicate fails.</summary>
    public static bool IsGround(Activation engine) =>
        IsGroundCell(engine, engine.GetRegister(0));

    private static bool IsGroundCell(Activation engine, Cell cell)
    {
        if (cell.Tag == Tag.Ref)
        {
            int addr = engine.Deref(cell.AsHeapIndex);
            cell = engine.GetHeap(addr);
            if (cell.Tag == Tag.Ref) return false;
        }
        switch (cell.Tag)
        {
            // An attributed variable is an UNBOUND variable (freeze/dif/clpfd
            // attach attributes to it) — a term holding one is not ground.
            case Tag.AttVar:
                return false;
            case Tag.Atom:
            case Tag.Int:
            case Tag.BigInt:
            case Tag.Rational:
            case Tag.Float:
            case Tag.Pstr:
            case Tag.String:
                return true;
            case Tag.Str:
                int functorIdx = cell.AsHeapIndex;
                var (_, arity) = FunctorTable.Lookup(
                    engine.GetHeap(functorIdx).AsFunctorId);
                for (int i = 0; i < arity; i++)
                    if (!IsGroundCell(engine, engine.GetHeap(functorIdx + 1 + i)))
                        return false;
                return true;
            case Tag.Lis:
                int headIdx = cell.AsHeapIndex;
                return IsGroundCell(engine, engine.GetHeap(headIdx))
                    && IsGroundCell(engine, engine.GetHeap(headIdx + 1));
            default:
                return true;
        }
    }

    /// <summary><c>acyclic_term(X)</c> — X contains no cycle (is a finite
    /// tree, not a rational/cyclic term). Walks the heap term tracking the
    /// set of compound anchors on the current DFS path; a reference back to
    /// one of them is a cycle. Shared (DAG) subterms are fine — each anchor
    /// is removed from the path set once its subtree is fully checked.</summary>
    public static bool AcyclicTerm(Activation engine) =>
        IsAcyclicCell(engine, engine.GetRegister(0), new HashSet<int>());

    private static bool IsAcyclicCell(Activation engine, Cell cell, HashSet<int> onPath)
    {
        if (cell.Tag == Tag.Ref)
        {
            int addr = engine.Deref(cell.AsHeapIndex);
            cell = engine.GetHeap(addr);
            if (cell.Tag == Tag.Ref) return true;   // unbound var: acyclic
        }
        switch (cell.Tag)
        {
            case Tag.Str:
            {
                int functorIdx = cell.AsHeapIndex;
                if (!onPath.Add(functorIdx)) return false;   // back-edge → cyclic
                var (_, arity) = FunctorTable.Lookup(
                    engine.GetHeap(functorIdx).AsFunctorId);
                for (int i = 0; i < arity; i++)
                    if (!IsAcyclicCell(engine, engine.GetHeap(functorIdx + 1 + i), onPath))
                        return false;
                onPath.Remove(functorIdx);
                return true;
            }
            case Tag.Lis:
            {
                int headIdx = cell.AsHeapIndex;
                if (!onPath.Add(headIdx)) return false;
                bool ok = IsAcyclicCell(engine, engine.GetHeap(headIdx), onPath)
                       && IsAcyclicCell(engine, engine.GetHeap(headIdx + 1), onPath);
                onPath.Remove(headIdx);
                return ok;
            }
            default:
                return true;   // atomic
        }
    }

    private static Tag Tag0(Activation engine) => Resolve(engine, engine.GetRegister(0)).Tag;

    private static Cell Resolve(Activation engine, Cell cell)
    {
        if (cell.Tag != Tag.Ref) return cell;
        int addr = engine.Deref(cell.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}
