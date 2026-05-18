using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// ISO "standard order of terms" comparison. Returns -1 / 0 / +1 for the
/// usual <c>&lt; / = / &gt;</c> outcome, with the ordering:
///
/// <list type="number">
/// <item>Variables (compared by heap address — older / lower comes first)</item>
/// <item>Numbers (numeric, with float &lt; integer on tie)</item>
/// <item>Atoms (alphabetical by name)</item>
/// <item>Strings (alphabetical, treated as opaque)</item>
/// <item>Compound terms (by arity, then functor name, then args left-to-right)</item>
/// </list>
///
/// <para>An empty list <c>[]</c> is an atom (not compound). A cons cell
/// (Tag.Lis) is compound with implicit functor <c>"."/2</c>.</para>
/// </summary>
public static class StandardOrderComparator
{
    public static int Compare(Engine engine, Cell aCell, Cell bCell)
    {
        var (a, aAddr) = Resolve(engine, aCell);
        var (b, bAddr) = Resolve(engine, bCell);

        int aOrder = TypeOrder(a);
        int bOrder = TypeOrder(b);
        if (aOrder != bOrder) return aOrder.CompareTo(bOrder);

        return aOrder switch
        {
            0 => aAddr.CompareTo(bAddr),                   // unbound vars: by heap addr
            1 => CompareNumbers(engine, a, b),
            2 => CompareAtoms(a, b),
            3 => CompareCompounds(engine, a, b),
            _ => 0,
        };
    }

    private static int TypeOrder(Cell c) => c.Tag switch
    {
        Tag.Ref => 0,
        Tag.Int or Tag.Float or Tag.BigInt => 1,
        Tag.Atom => 2,
        Tag.Str or Tag.Lis => 3,
        _ => 4,                                            // PSTR etc. — defer
    };

    private static int CompareNumbers(Engine engine, Cell a, Cell b)
    {
        Number na = ToNumber(engine, a);
        Number nb = ToNumber(engine, b);
        int cmp = Number.Compare(na, nb);
        if (cmp != 0) return cmp;
        // Tie-break by type: float < integer in ISO standard order.
        if (na.IsFloat && !nb.IsFloat) return -1;
        if (!na.IsFloat && nb.IsFloat) return 1;
        return 0;
    }

    private static Number ToNumber(Engine engine, Cell c) => c.Tag switch
    {
        Tag.Int => new Number(c.AsInt),
        Tag.BigInt => new Number(engine.AsBigInt(c)),
        Tag.Float => new Number(Cell.DecodeFloat(c, engine.GetHeap(c.FloatPairedIndex))),
        _ => throw new InvalidOperationException(
            $"StandardOrderComparator.ToNumber: cell has tag {c.Tag}, expected Int / BigInt / Float."),
    };

    private static int CompareAtoms(Cell a, Cell b)
    {
        string aName = AtomTable.GetById(a.AsAtomId)?.Name ?? "";
        string bName = AtomTable.GetById(b.AsAtomId)?.Name ?? "";
        return string.CompareOrdinal(aName, bName);
    }

    private static int CompareCompounds(Engine engine, Cell a, Cell b)
    {
        // Resolve both into (functor name, arity, argument cells).
        var (aName, aArity, aArgsBase) = DescribeCompound(engine, a);
        var (bName, bArity, bArgsBase) = DescribeCompound(engine, b);

        if (aArity != bArity) return aArity.CompareTo(bArity);

        int nameCmp = string.CompareOrdinal(aName, bName);
        if (nameCmp != 0) return nameCmp;

        // Same functor name + arity — compare args left to right.
        for (int i = 0; i < aArity; i++)
        {
            int cmp = Compare(engine,
                engine.GetHeap(aArgsBase + i),
                engine.GetHeap(bArgsBase + i));
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    /// <summary>Returns (functor name, arity, base heap index for args[0]).
    /// For a STR cell the args begin one past the functor cell; for a LIS
    /// cell they're the head and tail at consecutive indices.</summary>
    private static (string Name, int Arity, int ArgsBase) DescribeCompound(Engine engine, Cell c)
    {
        if (c.Tag == Tag.Lis)
            return (".", 2, c.AsHeapIndex);

        // Tag.Str.
        int functorIdx = c.AsHeapIndex;
        Cell functorCell = engine.GetHeap(functorIdx);
        var (atomId, arity) = FunctorTable.Lookup(functorCell.AsFunctorId);
        string name = AtomTable.GetById(atomId)?.Name ?? "";
        return (name, arity, functorIdx + 1);
    }

    private static (Cell Cell, int Addr) Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return (c, -1);
        int addr = engine.Deref(c.AsHeapIndex);
        return (engine.GetHeap(addr), addr);
    }
}
