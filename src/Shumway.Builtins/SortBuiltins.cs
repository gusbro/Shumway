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
    public static bool Sort(Engine engine) => SortImpl(engine, dedup: true);

    /// <summary><c>msort(List, Sorted)</c> — sort in standard order, keeping
    /// every element.</summary>
    public static bool Msort(Engine engine) => SortImpl(engine, dedup: false);

    private static bool SortImpl(Engine engine, bool dedup)
    {
        // Walk the input list collecting one cell per element. We keep the
        // *dereferenced* cell so an unbound element stays an unbound REF
        // pointing at its own var, and a bound-to-value element stays a
        // plain value cell — both work as elements of the result list and
        // both are compared correctly by StandardOrderComparator.
        var elements = new List<Cell>();
        Cell cursor = Resolve(engine, engine.GetRegister(0));
        while (cursor.Tag == Tag.Lis)
        {
            int headIdx = cursor.AsHeapIndex;
            elements.Add(Resolve(engine, engine.GetHeap(headIdx)));
            cursor = Resolve(engine, engine.GetHeap(headIdx + 1));
        }
        if (cursor.Tag == Tag.Ref)
            throw new InvalidOperationException(
                "sort/2: first argument must be a proper list (got a partial list).");
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            return false;   // improper / non-list — fail rather than throw

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
    private static int BuildList(Engine engine, IReadOnlyList<Cell> elements)
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

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}
