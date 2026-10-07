using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Walking a list from a builtin. A list may be stored as cons cells or packed
/// (<see cref="Tag.Pstr"/>) and the two are the same term (ADR-047), so a
/// builtin that reads <c>Tag.Lis</c> and the two heap slots behind it answers
/// correctly for one and silently walks zero elements for the other.
/// </summary>
internal static class ListCursor
{
    /// <summary>Dereferences, then collapses an empty packed segment to what it
    /// denotes — usually the atom <c>[]</c>. Every cell entering a spine walk
    /// goes through here so no caller has to know a zero-length PSTR exists.</summary>
    public static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag == Tag.Ref)
            c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        return engine.NormalizeListCell(c);
    }

    /// <summary>Peels one element, whatever the storage. The head and tail are
    /// values, not heap addresses: a packed list's are computed.</summary>
    public static bool TryUncons(Activation engine, Cell c, out Cell head, out Cell tail)
        => engine.TryUnconsListLike(Resolve(engine, c), out head, out tail);

    public static bool IsNil(Cell c)
        => c.Tag == Tag.Atom && c.AsAtomId == AtomTable.EmptyListId;

    /// <summary>Walks the spine, heads not entered, and returns where it
    /// stopped: <c>[]</c> for a proper list, the unbound tail of a partial
    /// one, the non-list term ending an improper one, or a list cell inside
    /// the cycle of a cyclic spine (the only end <see cref="TryUncons"/>
    /// accepts). <paramref name="length"/> is the number of cells walked,
    /// which for a cyclic spine is not a length. Brent's detection: one step
    /// per cell, the cycle found within twice its length past its entry.</summary>
    public static Cell SkipSpine(Activation engine, Cell list, out long length)
    {
        Cell cur = Resolve(engine, list);
        Cell saved = cur;
        long power = 1, lam = 0;
        length = 0;
        while (TryUncons(engine, cur, out _, out Cell tail))
        {
            cur = Resolve(engine, tail);
            length++;
            if (cur.Equals(saved)) return cur;
            if (++lam == power)
            {
                saved = cur;
                power <<= 1;
                lam = 0;
            }
        }
        return cur;
    }

    /// <summary>True when the spine loops back on itself.</summary>
    public static bool IsCyclic(Activation engine, Cell list)
        => TryUncons(engine, SkipSpine(engine, list, out _), out _, out _);

    /// <summary>True unless the cell is a partial list — one whose spine or any
    /// element is still unbound. The text builtins use it to choose direction:
    /// a proper ground list is checked against the atom's text, anything else
    /// is the generate direction and has to unify instead
    /// (<c>atom_codes(A, [X])</c> after <c>atom_codes(A, [0'x])</c> binds X).</summary>
    public static bool IsProperListCell(Activation engine, Cell c)
    {
        Cell cur = Resolve(engine, c);
        int guard = engine.HeapTop + 2;
        while (guard-- > 0 && TryUncons(engine, cur, out Cell head, out Cell tail))
        {
            if (Resolve(engine, head).Tag is Tag.Ref or Tag.AttVar) return false;
            cur = Resolve(engine, tail);
        }
        // Guard exhausted: a spine longer than the heap is cyclic (no proper
        // list has more conses than cells), and a cyclic list is not proper.
        if (guard < 0) return false;
        return cur.Tag is not (Tag.Ref or Tag.AttVar);
    }
}
