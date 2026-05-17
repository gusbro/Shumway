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
    /// <summary><c>var(X)</c> — X is an unbound variable.</summary>
    public static bool IsVar(Engine engine) => Tag0(engine) == Tag.Ref;

    /// <summary><c>nonvar(X)</c> — X is bound to a non-variable term.</summary>
    public static bool IsNonVar(Engine engine) => Tag0(engine) != Tag.Ref;

    /// <summary><c>atom(X)</c> — X is bound to an atom (including <c>[]</c>
    /// and <c>{}</c>, which are atoms in ISO).</summary>
    public static bool IsAtom(Engine engine) => Tag0(engine) == Tag.Atom;

    /// <summary><c>integer(X)</c> — X is an integer cell.</summary>
    public static bool IsInteger(Engine engine) => Tag0(engine) == Tag.Int;

    /// <summary><c>float(X)</c> — X is a float cell.</summary>
    public static bool IsFloat(Engine engine) => Tag0(engine) == Tag.Float;

    /// <summary><c>number(X)</c> — X is either an integer or a float.</summary>
    public static bool IsNumber(Engine engine)
    {
        var t = Tag0(engine);
        return t == Tag.Int || t == Tag.Float;
    }

    /// <summary><c>atomic(X)</c> — X is a non-compound, non-variable term
    /// (atom, integer, float, string, PSTR).</summary>
    public static bool IsAtomic(Engine engine)
    {
        var t = Tag0(engine);
        return t is Tag.Atom or Tag.Int or Tag.Float or Tag.String or Tag.Pstr;
    }

    /// <summary><c>compound(X)</c> — X is a compound term (STR or non-empty
    /// LIS). An empty list (the atom <c>[]</c>) is NOT compound.</summary>
    public static bool IsCompound(Engine engine)
    {
        var t = Tag0(engine);
        return t is Tag.Str or Tag.Lis;
    }

    /// <summary><c>is_list(X)</c> — X is a proper list: a cons chain
    /// terminated by the empty-list atom. An unbound tail makes it a partial
    /// list — fails. An atom other than <c>[]</c> at the tail — fails.</summary>
    public static bool IsList(Engine engine)
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
    public static bool IsGround(Engine engine) =>
        IsGroundCell(engine, engine.GetRegister(0));

    private static bool IsGroundCell(Engine engine, Cell cell)
    {
        if (cell.Tag == Tag.Ref)
        {
            int addr = engine.Deref(cell.AsHeapIndex);
            cell = engine.GetHeap(addr);
            if (cell.Tag == Tag.Ref) return false;
        }
        switch (cell.Tag)
        {
            case Tag.Atom:
            case Tag.Int:
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

    private static Tag Tag0(Engine engine) => Resolve(engine, engine.GetRegister(0)).Tag;

    private static Cell Resolve(Engine engine, Cell cell)
    {
        if (cell.Tag != Tag.Ref) return cell;
        int addr = engine.Deref(cell.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}
