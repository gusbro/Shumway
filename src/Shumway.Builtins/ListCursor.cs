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
        return cur.Tag is not (Tag.Ref or Tag.AttVar);
    }
}
