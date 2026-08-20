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

    /// <summary><c>string(X)</c> — X is a string cell (SWI). Shumway has a
    /// distinct string representation (Tag.String / PSTR); an atom is not a
    /// string.</summary>
    public static bool IsString(Activation engine)
    {
        var t = Tag0(engine);
        return t is Tag.String or Tag.Pstr;
    }

    /// <summary><c>'$is_partial_string'(X)</c> — X is a list of one-character
    /// atoms, complete or with an open tail. It is the fast path of
    /// <c>must_be(chars, X)</c> in the Scryer-dialect libraries.
    ///
    /// <para>It asks about the list's CONTENTS, not its storage (ADR-047):
    /// testing the tag instead made it true for a packed list of CODES, so
    /// <c>must_be(chars, "abc")</c> took the fast path and accepted a code
    /// list.</para></summary>
    public static bool IsPartialString(Activation engine)
    {
        Cell cur = ListCursor.Resolve(engine, engine.GetRegister(0));
        int guard = engine.HeapTop + 2;
        bool sawChar = false;
        while (guard-- > 0)
        {
            // An open tail is fine once some text has been read; a bare
            // unbound variable is not a partial string.
            if (cur.Tag is Tag.Ref or Tag.AttVar) return sawChar;
            if (ListCursor.IsNil(cur)) return true;
            if (!engine.TryUnconsListLike(cur, out Cell rawHead, out Cell tail))
                return false;
            Cell head = ListCursor.Resolve(engine, rawHead);
            if (head.Tag != Tag.Atom) return false;
            if ((AtomTable.GetById(head.AsAtomId)?.Name?.Length ?? 0) != 1) return false;
            sawChar = true;
            cur = ListCursor.Resolve(engine, tail);
        }
        return false;
    }

    /// <summary><c>atomic(X)</c> — X is a non-compound, non-variable term
    /// (atom, integer, bigint, rational, float, string). A packed list is a
    /// list (ADR-047), so it is NOT atomic; an empty one is the atom <c>[]</c>,
    /// which is, and <see cref="Tag0"/> has already collapsed it.</summary>
    public static bool IsAtomic(Activation engine)
    {
        var t = Tag0(engine);
        return t is Tag.Atom or Tag.Int or Tag.BigInt or Tag.Rational or Tag.Float or Tag.String;
    }

    /// <summary><c>compound(X)</c> — X is a compound term: a structure or a
    /// non-empty list, packed or not. An empty list (the atom <c>[]</c>) is NOT
    /// compound.</summary>
    public static bool IsCompound(Activation engine)
    {
        var t = Tag0(engine);
        return t is Tag.Str or Tag.Lis or Tag.Pstr;
    }

    /// <summary><c>callable(X)</c> — X is an atom or a compound term (ISO
    /// §8.3.6). A non-empty list is a compound, whether or not it is packed.
    /// An unbound variable or a number is not callable.</summary>
    public static bool IsCallable(Activation engine)
    {
        var t = Tag0(engine);
        return t is Tag.Atom or Tag.Str or Tag.Lis or Tag.Pstr;
    }

    /// <summary><c>is_list(X)</c> — X is a proper list: a cons chain
    /// terminated by the empty-list atom. An unbound tail makes it a partial
    /// list — fails. An atom other than <c>[]</c> at the tail — fails.</summary>
    public static bool IsList(Activation engine)
    {
        Cell cell = engine.GetRegister(0);
        while (true)
        {
            cell = ListCursor.Resolve(engine, cell);
            if (ListCursor.IsNil(cell)) return true;
            if (!engine.TryUnconsListLike(cell, out _, out Cell tail)) return false;
            cell = tail;
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
            case Tag.String:
                return true;
            // A packed list may be partial — its open tail is an unbound
            // variable, and a term holding one is not ground.
            case Tag.Pstr:
                return IsGroundCell(engine, engine.PstrFinalTailCell(cell));
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

    /// <summary><c>cyclic_term(X)</c> (SWI) — X contains a cycle (a rational /
    /// infinite term). The exact complement of <see cref="AcyclicTerm"/>.</summary>
    public static bool CyclicTerm(Activation engine) =>
        !IsAcyclicCell(engine, engine.GetRegister(0), new HashSet<int>());

    // SWI kernel type-check builtins ('$'-prefixed system predicates that
    // library(error)'s has_type/2 dispatches to). Bare-global internals.

    /// <summary><c>'$is_char'(X)</c> — X is a one-character atom.</summary>
    public static bool IsCharAtom(Activation engine)
    {
        Cell d = Resolve(engine, engine.GetRegister(0));
        return d.Tag == Tag.Atom && (AtomTable.GetById(d.AsAtomId)?.Name?.Length == 1);
    }

    /// <summary><c>'$is_char_code'(X)</c> — X is a character code (an integer in
    /// the Unicode range).</summary>
    public static bool IsCharCode(Activation engine)
    {
        Cell d = Resolve(engine, engine.GetRegister(0));
        return d.Tag == Tag.Int && d.AsInt >= 0 && d.AsInt <= 0x10FFFF;
    }

    /// <summary><c>'$is_char_list'(X, Len)</c> — X is a proper list of one-char
    /// atoms; Len unifies with its length.</summary>
    public static bool IsCharList(Activation engine) => IsTypedList(engine, chars: true);

    /// <summary><c>'$is_code_list'(X, Len)</c> — X is a proper list of character
    /// codes; Len unifies with its length.</summary>
    public static bool IsCodeList(Activation engine) => IsTypedList(engine, chars: false);

    /// <summary><c>'$skip_list'(-Length, ?List, -Tail)</c> — SWI's robust
    /// list-length primitive: counts the cons cells of List (a proper OR partial
    /// list), unifying Length with the count and Tail with the remainder — <c>[]</c>
    /// for a proper list, or the unbound variable / non-list atom that terminates
    /// a partial / improper one. Never fails on a bad list (unlike length/2).</summary>
    public static bool SkipList(Activation engine)
    {
        Cell cell = engine.GetRegister(1);
        long len = 0;
        while (true)
        {
            cell = Resolve(engine, cell);
            if (cell.Tag != Tag.Lis) break;
            len++;
            cell = engine.GetHeap(cell.AsHeapIndex + 1);
        }
        if (!engine.UnifyRegisterWithCell(0, Cell.Int(len))) return false;
        return engine.UnifyRegisterWithCell(2, cell);
    }

    private static bool IsTypedList(Activation engine, bool chars)
    {
        Cell cell = engine.GetRegister(0);
        long len = 0;
        while (true)
        {
            // The cursor, not `Tag.Lis`: these answer about the list's
            // contents, and a packed list is a list (ADR-047).
            cell = engine.NormalizeListCell(Resolve(engine, cell));
            if (cell.Tag == Tag.Atom && cell.AsAtomId == AtomTable.EmptyListId) break;
            if (!engine.TryUnconsListLike(cell, out Cell rawHead, out Cell tail)) return false;
            Cell head = Resolve(engine, rawHead);
            bool ok = chars
                ? head.Tag == Tag.Atom && (AtomTable.GetById(head.AsAtomId)?.Name?.Length == 1)
                : head.Tag == Tag.Int && head.AsInt >= 0 && head.AsInt <= 0x10FFFF;
            if (!ok) return false;
            len++;
            cell = tail;
        }
        return engine.UnifyRegisterWithCell(1, Cell.Int(len));
    }

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

    // Every type test in this file resolves through here, so collapsing an
    // empty packed segment to what it denotes (usually the atom `[]`) once is
    // what keeps `atomic("")`, `is_list("")` and the rest from having to know
    // a zero-length PSTR exists.
    private static Cell Resolve(Activation engine, Cell cell)
        => ListCursor.Resolve(engine, cell);
}
