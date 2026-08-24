using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// ISO "standard order of terms" comparison. Returns -1 / 0 / +1 for the
/// usual <c>&lt; / = / &gt;</c> outcome, with the ordering:
///
/// <list type="number">
/// <item>Variables (compared by heap address — older / lower comes first)</item>
/// <item>Numbers (ISO §7.2.1: ALL floats before all integers;
/// by value within a type)</item>
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
                var (aName, aArity) = DescribeCompound(engine, a);
                var (bName, bArity) = DescribeCompound(engine, b);
                if (aArity != bArity) return aArity.CompareTo(bArity);
                int nameCmp = CompareNames(aName, bName);
                if (nameCmp != 0) return nameCmp;
                for (int i = 0; i < aArity; i++)
                {
                    int c = CompareRec(engine,
                        ArgAt(engine, a, i), ArgAt(engine, b, i), depth + 1);
                    if (c != 0) return c;
                }
                return 0;
            }
            default:
                throw new NotSupportedException(
                    $"standard order: order class {aOrder} has no comparison.");
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
                    var (aName, aArity) = DescribeCompound(engine, a);
                    var (bName, bArity) = DescribeCompound(engine, b);
                    if (aArity != bArity) return aArity.CompareTo(bArity);
                    int nameCmp = CompareNames(aName, bName);
                    if (nameCmp != 0) return nameCmp;

                    if (++steps == CycleThreshold)
                        (visited = engine.CompareVisited ??= new HashSet<long>()).Clear();
                    if (visited != null &&
                        !visited.Add(((long)a.AsHeapIndex << 32) | (uint)b.AsHeapIndex))
                        break;   // cycle: equal on this branch

                    // Push args in reverse so arg 0 is popped (compared) first.
                    for (int i = aArity - 1; i >= 0; i--)
                    {
                        stack.Add(ArgAt(engine, a, i));
                        stack.Add(ArgAt(engine, b, i));
                    }
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"standard order: order class {aOrder} has no comparison.");
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
        // A PSTR is the list it represents, so it orders with the compounds.
        // The empty PSTR never reaches here — Resolve collapses it to its tail.
        Tag.Str or Tag.Lis or Tag.Pstr => 3,
        _ => throw new NotSupportedException(
            $"standard order: tag {c.Tag} has no order class."),
    };

    private static int CompareNumbers(Activation engine, Cell a, Cell b)
    {
        Number na = ToNumber(engine, a);
        Number nb = ToNumber(engine, b);
        // ISO §7.2.1: a Float ALWAYS precedes an Integer, whatever the
        // values — msort([3, 1.5, 2, 0.5, 1]) is [0.5, 1.5, 1, 2, 3], not
        // numeric order (verified against GNU; SICStus and Scryer agree).
        // Only within one type does the value decide. Rationals are exact,
        // so they sort with the integers.
        if (na.IsFloat != nb.IsFloat) return na.IsFloat ? -1 : 1;
        return Number.Compare(na, nb);
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

    /// <summary>Functor-name comparison in code-point order. Same-functor
    /// compounds share the interned name instance, so the common case is the
    /// reference check.</summary>
    private static int CompareNames(string aName, string bName)
        => ReferenceEquals(aName, bName)
            ? 0
            : Utf16Text.CompareCodePointOrder(aName, bName);

    private static int CompareAtoms(Cell a, Cell b)
    {
        Atom? aAtom = AtomTable.GetById(a.AsAtomId);
        Atom? bAtom = AtomTable.GetById(b.AsAtomId);
        string aName = aAtom?.Name ?? "";
        string bName = bAtom?.Name ?? "";
        // The standard order is by CODE POINT. Unit-wise ordinal order
        // agrees for all-BMP names (the near-universal case, O(1) via the
        // intern-time shape flag); a surrogate on either side takes the
        // remapped comparison so astral atoms sort above U+E000–U+FFFF.
        if (aAtom is { IsAllBmp: true } && bAtom is { IsAllBmp: true })
            return string.CompareOrdinal(aName, bName);
        return Utf16Text.CompareCodePointOrder(aName, bName);
    }

    /// <summary>Returns (functor name, arity, base heap index for args[0]).
    /// For a STR cell the args begin one past the functor cell; for a LIS
    /// cell they're the head and tail at consecutive indices.</summary>
    /// <summary>Name and arity of a compound for ordering purposes. A list —
    /// cons cell or PSTR alike — is <c>'.'/2</c>. Arguments are read with
    /// <see cref="ArgAt"/> rather than from a base address, because a PSTR's
    /// head and tail are computed, not stored at consecutive slots.</summary>
    private static (string Name, int Arity) DescribeCompound(Activation engine, Cell c)
    {
        if (Activation.IsListLike(c)) return (".", 2);

        // Tag.Str.
        Cell functorCell = engine.GetHeap(c.AsHeapIndex);
        var (atomId, arity) = FunctorTable.Lookup(functorCell.AsFunctorId);
        return (AtomTable.GetById(atomId)?.Name ?? "", arity);
    }

    /// <summary>Argument <paramref name="i"/> of a compound cell.</summary>
    private static Cell ArgAt(Activation engine, Cell c, int i)
    {
        if (Activation.IsListLike(c))
        {
            engine.TryUnconsListLike(c, out Cell head, out Cell tail);
            return i == 0 ? head : tail;
        }
        return engine.GetHeap(c.AsHeapIndex + 1 + i);
    }

    private static (Cell Cell, int Addr) Resolve(Activation engine, Cell c)
    {
        // A bare ATTVAR cell carries its home index as payload, so it
        // compares by that address — like any variable.
        if (c.Tag == Tag.AttVar) return (c, c.AsHeapIndex);
        if (c.Tag != Tag.Ref) return (engine.NormalizeListCell(c), -1);
        int addr = engine.Deref(c.AsHeapIndex);
        Cell target = engine.GetHeap(addr);
        // An empty PSTR is its tail (`[]`, or the variable it is open on), so
        // it must not reach TypeOrder as a third kind of thing.
        if (target.Tag == Tag.Pstr)
        {
            Cell norm = engine.NormalizeListCell(target);
            if (norm.Tag is Tag.Ref or Tag.AttVar) return (norm, norm.AsHeapIndex);
            return (norm, addr);
        }
        return (target, addr);
    }
}
