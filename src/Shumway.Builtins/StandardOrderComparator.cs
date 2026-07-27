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
    /// <summary>C#-recursion depth at which the ordered comparison stops
    /// recursing and hands the sub-term off to the iterative walk
    /// (<see cref="CompareCompoundsIterative"/>). Shallow terms — the sort hot
    /// path (pairs, small compounds) — stay on the fast recursive path and pay
    /// nothing; only a term deeper than this (a long list, deep nesting, or a
    /// cycle) escalates. Well below the C# stack-overflow point, so the switch
    /// always happens before a crash could.</summary>
    private const int RecursionLimit = 512;

    /// <summary>Beyond this many compound descents in the iterative walk it
    /// assumes it may be inside a cycle and engages the visited-pair set. Well
    /// above any realistic acyclic sub-term, so the common escalation (a long
    /// acyclic list) never allocates / probes the set.</summary>
    private const int CycleThreshold = 1 << 16;

    public static int Compare(Activation engine, Cell aCell, Cell bCell)
        => CompareRec(engine, aCell, bCell, 0);

    /// <summary>Recursive ordered comparison, threading the C#-recursion depth.
    /// Leaves compare inline; a compound recurses into its args — until
    /// <paramref name="depth"/> reaches <see cref="RecursionLimit"/>, where the
    /// remaining sub-term is compared by the non-recursive
    /// <see cref="CompareCompoundsIterative"/> so a long / deep / cyclic term
    /// can never overflow the C# stack.</summary>
    private static int CompareRec(Activation engine, Cell aCell, Cell bCell, int depth)
    {
        var (a, aAddr) = Resolve(engine, aCell);
        var (b, bAddr) = Resolve(engine, bCell);

        int aOrder = TypeOrder(a);
        int bOrder = TypeOrder(b);
        if (aOrder != bOrder) return aOrder.CompareTo(bOrder);

        switch (aOrder)
        {
            case 0: return aAddr.CompareTo(bAddr);         // unbound vars: by heap addr
            case 1: return CompareNumbers(engine, a, b);
            case 2: return CompareAtoms(a, b);
            case 3:
            {
                if (depth >= RecursionLimit)
                    return CompareCompoundsIterative(engine, a, b);
                var (aName, aArity, aArgsBase) = DescribeCompound(engine, a);
                var (bName, bArity, bArgsBase) = DescribeCompound(engine, b);
                if (aArity != bArity) return aArity.CompareTo(bArity);
                int nameCmp = string.CompareOrdinal(aName, bName);
                if (nameCmp != 0) return nameCmp;
                for (int i = 0; i < aArity; i++)
                {
                    int c = CompareRec(engine,
                        engine.GetHeap(aArgsBase + i),
                        engine.GetHeap(bArgsBase + i), depth + 1);
                    if (c != 0) return c;
                }
                return 0;
            }
            default: return 0;                             // PSTR etc.: tie
        }
    }

    /// <summary>Ordered comparison of two same-type compound cells, iterative
    /// (no per-node / per-element C# recursion). A LIFO work-stack of pending
    /// cell pairs is walked depth-first, left-to-right; the first non-zero
    /// leaf / arity / functor difference is the answer. Args are pushed in
    /// reverse so the leftmost is compared first. Past
    /// <see cref="CycleThreshold"/> descents a visited set of
    /// <c>(aAddr,bAddr)</c> structure-pairs is engaged: re-encountering a pair
    /// already in progress means "equal on this branch" (the co-inductive
    /// reading, consistent with <c>==/2</c>). The stack + set are pooled on the
    /// engine and cleared on entry; the walk is self-contained.</summary>
    private static int CompareCompoundsIterative(Activation engine, Cell aTop, Cell bTop)
    {
        List<Cell> stack = engine.CompareStack ??= new List<Cell>(64);
        stack.Clear();
        HashSet<long>? visited = null;
        int steps = 0;
        // The two tops are already resolved same-type compounds.
        stack.Add(aTop); stack.Add(bTop);
        while (stack.Count > 0)
        {
            Cell bc = stack[stack.Count - 1];
            Cell ac = stack[stack.Count - 2];
            stack.RemoveRange(stack.Count - 2, 2);
            var (a, aAddr) = Resolve(engine, ac);
            var (b, bAddr) = Resolve(engine, bc);

            int aOrder = TypeOrder(a), bOrder = TypeOrder(b);
            if (aOrder != bOrder) return aOrder.CompareTo(bOrder);
            switch (aOrder)
            {
                case 0: { int c = aAddr.CompareTo(bAddr); if (c != 0) return c; break; }
                case 1: { int c = CompareNumbers(engine, a, b); if (c != 0) return c; break; }
                case 2: { int c = CompareAtoms(a, b); if (c != 0) return c; break; }
                case 3:
                {
                    var (aName, aArity, aArgsBase) = DescribeCompound(engine, a);
                    var (bName, bArity, bArgsBase) = DescribeCompound(engine, b);
                    if (aArity != bArity) return aArity.CompareTo(bArity);
                    int nameCmp = string.CompareOrdinal(aName, bName);
                    if (nameCmp != 0) return nameCmp;

                    if (++steps == CycleThreshold)
                        (visited = engine.CompareVisited ??= new HashSet<long>()).Clear();
                    if (visited != null &&
                        !visited.Add(((long)a.AsHeapIndex << 32) | (uint)b.AsHeapIndex))
                        break;   // cycle: equal on this branch

                    // Push args in reverse so arg 0 is popped (compared) first.
                    for (int i = aArity - 1; i >= 0; i--)
                    {
                        stack.Add(engine.GetHeap(aArgsBase + i));
                        stack.Add(engine.GetHeap(bArgsBase + i));
                    }
                    break;
                }
                default: break;   // order 4 (PSTR etc.): tie, keep walking
            }
        }
        return 0;
    }

    private static int TypeOrder(Cell c) => c.Tag switch
    {
        // An attributed variable orders as a variable — by heap address,
        // alongside plain unbound REFs.
        Tag.Ref or Tag.AttVar => 0,
        Tag.Int or Tag.Float or Tag.BigInt or Tag.Rational => 1,
        Tag.Atom => 2,
        Tag.Str or Tag.Lis => 3,
        _ => 4,                                            // PSTR etc. — defer
    };

    private static int CompareNumbers(Activation engine, Cell a, Cell b)
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

    private static Number ToNumber(Activation engine, Cell c) => c.Tag switch
    {
        Tag.Int => new Number(c.AsInt),
        Tag.BigInt => new Number(engine.AsBigInt(c)),
        Tag.Rational => new Number(engine.AsRational(c)),
        Tag.Float => new Number(Cell.DecodeFloat(c, engine.GetHeap(c.FloatPairedIndex))),
        _ => throw new InvalidOperationException(
            $"StandardOrderComparator.ToNumber: cell has tag {c.Tag}, expected Int / BigInt / Float / Rational."),
    };

    private static int CompareAtoms(Cell a, Cell b)
    {
        string aName = AtomTable.GetById(a.AsAtomId)?.Name ?? "";
        string bName = AtomTable.GetById(b.AsAtomId)?.Name ?? "";
        return string.CompareOrdinal(aName, bName);
    }

    /// <summary>Returns (functor name, arity, base heap index for args[0]).
    /// For a STR cell the args begin one past the functor cell; for a LIS
    /// cell they're the head and tail at consecutive indices.</summary>
    private static (string Name, int Arity, int ArgsBase) DescribeCompound(Activation engine, Cell c)
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

    private static (Cell Cell, int Addr) Resolve(Activation engine, Cell c)
    {
        // A bare ATTVAR cell carries its home index as payload, so it
        // compares by that address — like any variable.
        if (c.Tag == Tag.AttVar) return (c, c.AsHeapIndex);
        if (c.Tag != Tag.Ref) return (c, -1);
        int addr = engine.Deref(c.AsHeapIndex);
        return (engine.GetHeap(addr), addr);
    }
}
