using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// <c>sort/2</c> and <c>msort/2</c>. Both walk a proper list, reorder its
/// elements by the standard order of terms, and unify the result with the
/// second argument; <c>sort/2</c> additionally removes adjacent duplicates,
/// <c>msort/2</c> keeps them. Comparison is delegated to
/// <see cref="StandardOrderComparator"/> so the behaviour matches the
/// existing <c>compare/3</c>, <c>@&lt;</c>, etc. exactly.
/// </summary>
public static class SortBuiltins
{
    /// <summary><c>sort(List, Sorted)</c> — sort in standard order, dropping
    /// adjacent duplicates.</summary>
    public static bool Sort(Activation engine) => SortImpl(engine, dedup: true);

    /// <summary><c>msort(List, Sorted)</c> — sort in standard order, keeping
    /// every element.</summary>
    public static bool Msort(Activation engine) => SortImpl(engine, dedup: false);

    /// <summary><c>keysort(+Pairs, -Sorted)</c> — stable-sort a
    /// list of <c>Key-Value</c> pairs by <c>Key</c> in the standard
    /// order of terms. Relative order is preserved for equal keys
    /// (stability matters: real-world programs that group by key
    /// rely on it). Each element must be a
    /// compound <c>'-'/2</c>; a non-pair element raises
    /// <c>type_error(pair, Element)</c>. ISO §8.4.4.</summary>
    public static bool Keysort(Activation engine)
    {
        var pairs = new List<(Cell Pair, Cell Key, int Index)>();
        Cell listStart = Resolve(engine, engine.GetRegister(0));
        Cell cursor = listStart;
        int index = 0;
        // §8.4.3/8.4.4: the list argument is checked as a whole first —
        // an unbound one is an instantiation_error, a bound non-list (or
        // improper tail) is type_error(list, L) with the WHOLE argument
        // as culprit — before any element's pair shape is looked at.
        CheckSortListArgument(engine, listStart);
        while (cursor.Tag == Tag.Lis)
        {
            int headIdx = cursor.AsHeapIndex;
            Cell pair = Resolve(engine, engine.GetHeap(headIdx));
            if (pair.Tag is Tag.Ref or Tag.AttVar)
                throw new PrologRuntimeException("instantiation_error");
            Cell key = ExtractPairKey(engine, pair);
            pairs.Add((pair, key, index++));
            cursor = Resolve(engine, engine.GetHeap(headIdx + 1));
        }

        // Sort by key, breaking ties by original index → stable.
        pairs.Sort((a, b) =>
        {
            int c = StandardOrderComparator.Compare(engine, a.Key, b.Key);
            return c != 0 ? c : a.Index - b.Index;
        });

        var sortedCells = new List<Cell>(pairs.Count);
        foreach (var p in pairs) sortedCells.Add(p.Pair);
        int listIdx = BuildList(engine, sortedCells);
        return engine.UnifyRegisterWithHeapAt(1, listIdx);
    }

    /// <summary>The sort family's list argument (§8.4.3.3): unbound (or
    /// with an unbound tail) is an instantiation_error; a bound
    /// non-list — including an improper tail — is type_error(list, L),
    /// the WHOLE argument being the culprit.</summary>
    private static void CheckSortListArgument(Activation engine, Cell listStart)
    {
        Cell cur = listStart;
        while (true)
        {
            if (cur.Tag is Tag.Ref or Tag.AttVar)
                throw new PrologRuntimeException("instantiation_error");
            if (cur.Tag == Tag.Atom && cur.AsAtomId == AtomTable.EmptyListId) return;
            if (cur.Tag != Tag.Lis)
                throw new PrologRuntimeException(
                    "type_error", "list", engine, listStart);
            cur = Resolve(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        }
    }

    /// <summary>Extracts the <c>K</c> from a <c>K-V</c> pair cell.
    /// A non-pair raises <c>type_error(pair, Element)</c> per ISO
    /// §8.4.4.</summary>
    private static Cell ExtractPairKey(Activation engine, Cell pair)
    {
        if (pair.Tag != Tag.Str)
            throw new PrologRuntimeException("type_error", "pair", engine, pair);
        int functorIdx = pair.AsHeapIndex;
        Cell functorCell = engine.GetHeap(functorIdx);
        if (functorCell.Tag != Tag.Functor)
            throw new PrologRuntimeException("type_error", "pair", engine, pair);
        var (atomId, arity) = FunctorTable.Lookup(functorCell.AsFunctorId);
        string name = AtomTable.GetById(atomId)?.Name ?? "";
        if (name != "-" || arity != 2)
            throw new PrologRuntimeException("type_error", "pair", engine, pair);
        return Resolve(engine, engine.GetHeap(functorIdx + 1));
    }

    private static bool SortImpl(Activation engine, bool dedup)
    {
        // Walk the input list collecting one cell per element. We keep the
        // *dereferenced* cell so an unbound element stays an unbound REF
        // pointing at its own var, and a bound-to-value element stays a
        // plain value cell — both work as elements of the result list and
        // both are compared correctly by StandardOrderComparator.
        var elements = new List<Cell>();
        Cell listStart = Resolve(engine, engine.GetRegister(0));
        Cell cursor = listStart;
        CheckSortListArgument(engine, listStart);
        while (cursor.Tag == Tag.Lis)
        {
            int headIdx = cursor.AsHeapIndex;
            elements.Add(Resolve(engine, engine.GetHeap(headIdx)));
            cursor = Resolve(engine, engine.GetHeap(headIdx + 1));
        }

        elements.Sort((a, b) => StandardOrderComparator.Compare(engine, a, b));

        if (dedup && elements.Count > 1)
        {
            // In-place dedup: write index advances only when the current
            // element differs from the previously-kept one. Avoids the
            // intermediate List allocation.
            int write = 1;
            for (int read = 1; read < elements.Count; read++)
            {
                if (StandardOrderComparator.Compare(engine, elements[read], elements[write - 1]) != 0)
                    elements[write++] = elements[read];
            }
            elements.RemoveRange(write, elements.Count - write);
        }

        int listIdx = BuildList(engine, elements);
        return engine.UnifyRegisterWithHeapAt(1, listIdx);
    }

    /// <summary>Builds a fresh cons-list on the heap whose head cells are
    /// the cells in <paramref name="elements"/>, terminated by <c>[]</c>.
    /// Layout matches the existing <see cref="AtomListBuiltins"/> helpers:
    /// 2N + 1 contiguous cells laid out as (Lis, head, Lis, head, …, nil).
    /// Returns the index of the first Lis cell, or of the lone nil cell
    /// when the list is empty.</summary>
    private static int BuildList(Activation engine, IReadOnlyList<Cell> elements)
    {
        if (elements.Count == 0)
        {
            int nilSlot = engine.AllocateHeap(1);
            engine.SetHeap(nilSlot, Cell.Atom(AtomTable.EmptyListId));
            return nilSlot;
        }

        int start = engine.AllocateHeap(2 * elements.Count + 1);
        for (int i = 0; i < elements.Count; i++)
        {
            int lisIdx = start + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            engine.SetHeap(headIdx, elements[i]);
        }
        engine.SetHeap(start + 2 * elements.Count, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    private static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}
