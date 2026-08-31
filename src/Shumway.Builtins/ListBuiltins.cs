using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Additional list-manipulation built-ins: <c>nth0/3</c>, <c>nth1/3</c>,
/// <c>reverse/2</c>, <c>last/2</c>, <c>list_to_set/2</c>. Together with
/// the <c>length/2</c> / <c>append/3</c> / <c>sort/2</c> family this is
/// the bread-and-butter list toolkit user code reaches for.
///
/// <para><see cref="Member"/> is NOT registered — <c>member/2</c> lives
/// in the Prolog prelude so it enumerates solutions via standard
/// backtracking; the first-solution C# version here is unreachable from
/// Prolog source.</para>
/// </summary>
public static class ListBuiltins
{
    // ---------- member/2 ----------

    public static bool Member(Activation engine)
    {
        Cell elem = engine.GetRegister(0);
        Cell cur = Resolve(engine, engine.GetRegister(1));
        while (ListCursor.TryUncons(engine, cur, out Cell head, out Cell tail))
        {
            // Trial-unify with head. Save/restore on miss.
            int savedHeapTop = engine.HeapTop;
            int savedBindingTrail = engine.BindingTrailTop;
            int savedExtraTrail = engine.ExtraTrailTop;
            int savedHb = engine.Hb;
            engine.SetHb(engine.HeapTop);

            if (engine.UnifyRegisterWithCell(0, head))
            {
                engine.SetHb(savedHb);
                return true;
            }

            engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
            engine.SetHeapTop(savedHeapTop);
            engine.SetHb(savedHb);

            cur = Resolve(engine, tail);
        }
        return false;
    }

    // ---------- nth0/3 + nth1/3 ----------

    public static bool Nth0(Activation engine) => NthImpl(engine, oneBased: false);
    public static bool Nth1(Activation engine) => NthImpl(engine, oneBased: true);

    private static bool NthImpl(Activation engine, bool oneBased)
    {
        Cell n = Resolve(engine, engine.GetRegister(0));
        // A variable index enumerates every position on backtracking — the
        // SWI/SICStus library behaviour real programs rely on (e.g. iterating a
        // board with nth0(Row, Board, R)). A bound non-integer is a type error.
        if (n.Tag == Tag.Ref)
            return NthStep(engine, new NthCursor(oneBased, engine.BuiltinReturnPc), isResume: false);
        if (n.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "integer", engine, n);
        // Prolog prologue p.p.8: a negative index is a domain error, not a
        // failure; nth1's index 0 (target -1 after the shift) stays a plain
        // failure.
        if (n.AsInt < 0)
            throw new PrologRuntimeException(
                "domain_error", "not_less_than_zero", engine, n);
        long target = n.AsInt;
        if (oneBased) target--;
        if (target < 0) return false;

        Cell cur = Resolve(engine, engine.GetRegister(1));
        long i = 0;
        while (ListCursor.TryUncons(engine, cur, out Cell head, out Cell tail))
        {
            if (i == target)
                return engine.UnifyRegisterWithCell(2, head);
            cur = Resolve(engine, tail);
            i++;
        }
        // Prologue generate mode: an integer index against a PARTIAL list
        // (unbound tail) extends it — nth0(2, Es, E) gives Es = [_,_,E|_].
        // A closed or improper tail still just fails.
        if (cur.Tag != Tag.Ref) return false;
        Cell built = BuildOpenSkeleton(engine, target - i, out int elemSlot);
        return engine.UnifyHeapWithCell(cur.AsHeapIndex, built)
            && engine.UnifyRegisterWithCell(2, Cell.Ref(elemSlot));
    }

    /// <summary>Advances one list cell, EXTENDING a partial list by a fresh
    /// <c>[H|T]</c> cons when the walk reaches an unbound tail — the prologue
    /// generate mode, and what makes a variable-index enumeration over a
    /// partial list produce answers ad infinitum instead of stopping at the
    /// end. The extension binding is trailed like any other, so backtracking
    /// past the whole call undoes it. A bound non-list tail fails.</summary>
    private static bool UnconsOrExtend(Activation engine, ref Cell cur, out Cell head)
    {
        if (ListCursor.TryUncons(engine, cur, out head, out Cell tail))
        {
            cur = Resolve(engine, tail);
            return true;
        }
        if (cur.Tag != Tag.Ref) return false;
        int h = engine.AllocateHeap(1);
        engine.SetHeap(h, Cell.Ref(h));
        int t = engine.AllocateHeap(1);
        engine.SetHeap(t, Cell.Ref(t));
        int p = engine.AllocateHeap(2);
        engine.SetHeap(p, Cell.Ref(h));
        engine.SetHeap(p + 1, Cell.Ref(t));
        if (!engine.UnifyHeapWithCell(cur.AsHeapIndex, Cell.Lis(p))) return false;
        head = Cell.Ref(h);
        cur = Cell.Ref(t);
        return true;
    }

    /// <summary>Builds the open list skeleton <c>[_,...,_,E|_]</c> with
    /// <paramref name="before"/> fresh elements ahead of E, returning the
    /// list cell and E's heap slot. The tail stays a fresh variable.</summary>
    private static Cell BuildOpenSkeleton(Activation engine, long before, out int elemSlot)
    {
        int tailSlot = engine.AllocateHeap(1);
        engine.SetHeap(tailSlot, Cell.Ref(tailSlot));
        elemSlot = engine.AllocateHeap(1);
        engine.SetHeap(elemSlot, Cell.Ref(elemSlot));
        int pair = engine.AllocateHeap(2);
        engine.SetHeap(pair, Cell.Ref(elemSlot));
        engine.SetHeap(pair + 1, Cell.Ref(tailSlot));
        Cell list = Cell.Lis(pair);
        for (long k = 0; k < before; k++)
        {
            int h = engine.AllocateHeap(1);
            engine.SetHeap(h, Cell.Ref(h));
            int p2 = engine.AllocateHeap(2);
            engine.SetHeap(p2, Cell.Ref(h));
            engine.SetHeap(p2 + 1, list);
            list = Cell.Lis(p2);
        }
        return list;
    }

    /// <summary>Resume state for a variable-index <c>nth0</c>/<c>nth1</c>
    /// enumeration: the running position plus a cached resume delegate
    /// (allocated once per call, re-pushed unchanged on every backtrack —
    /// no per-position closure). The position is re-walked from register 1
    /// each step rather than caching a heap index, because a heap GC between
    /// backtracks can move the list cells.</summary>
    private sealed class NthCursor
    {
        public int Pos;
        public readonly bool OneBased;
        public readonly int ReturnPc;
        public readonly Func<Activation, int, bool> Resume;

        public NthCursor(bool oneBased, int returnPc)
        {
            OneBased = oneBased;
            ReturnPc = returnPc;
            Pos = 0;
            Resume = (e, _) => NthStep(e, this, isResume: true);
        }
    }

    private static bool NthStep(Activation engine, NthCursor c, bool isResume)
    {
        Cell cur = Resolve(engine, engine.GetRegister(1));
        for (int k = 0; k < c.Pos; k++)
        {
            if (!UnconsOrExtend(engine, ref cur, out _)) return false;
        }
        if (!UnconsOrExtend(engine, ref cur, out Cell head))
            return false;                              // improper tail

        int pos = c.Pos;
        c.Pos = pos + 1;
        engine.PushBuiltinChoicePoint(c.Resume, arity: 3);

        long idxVal = c.OneBased ? pos + 1 : pos;
        if (engine.UnifyRegisterWithCell(0, Cell.Int(idxVal))
            && engine.UnifyRegisterWithCell(2, head))
        {
            if (isResume) engine.ResumeAtReturnPc(c.ReturnPc);
            return true;
        }
        return false;                                   // → retries pos + 1
    }

    // ---------- reverse/2 ----------

    public static bool Reverse(Activation engine)
    {
        var heads = new List<Cell>();
        Cell cur = Resolve(engine, engine.GetRegister(0));
        while (ListCursor.TryUncons(engine, cur, out Cell head, out Cell tail))
        {
            heads.Add(head);
            cur = Resolve(engine, tail);
        }
        if (cur.Tag == Tag.Ref)
            // A partial list — the tail is unbound, so we can't
            // determine the length. ISO instantiation_error.
            throw new PrologRuntimeException("instantiation_error");
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId) return false;

        // Build reversed list.
        heads.Reverse();
        int listIdx = BuildList(engine, heads);
        return engine.UnifyRegisterWithHeapAt(1, listIdx);
    }

    // ---------- last/2 ----------

    public static bool Last(Activation engine)
    {
        Cell cur = Resolve(engine, engine.GetRegister(0));
        Cell last = default;
        bool any = false;
        while (ListCursor.TryUncons(engine, cur, out Cell head, out Cell tail))
        {
            last = head;
            any = true;
            cur = Resolve(engine, tail);
        }
        if (!any) return false;              // empty list — no last element
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId) return false;
        return engine.UnifyRegisterWithCell(1, last);
    }

    // ---------- list_to_set/2 ----------

    public static bool ListToSet(Activation engine)
    {
        // Preserve first occurrence order; drop subsequent structurally
        // equal duplicates.
        var seen = new List<Cell>();
        Cell cur = Resolve(engine, engine.GetRegister(0));
        while (ListCursor.TryUncons(engine, cur, out Cell rawHead, out Cell tail))
        {
            Cell head = Resolve(engine, rawHead);
            bool dup = false;
            foreach (Cell s in seen)
            {
                if (engine.AreStructurallyEqual(s, head))
                {
                    dup = true;
                    break;
                }
            }
            if (!dup) seen.Add(head);
            cur = Resolve(engine, tail);
        }
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId) return false;
        int listIdx = BuildList(engine, seen);
        return engine.UnifyRegisterWithHeapAt(1, listIdx);
    }

    // ---------- Shared helpers ----------

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

    // A list may be cons cells or packed, and both are the same term
    // (ADR-047), so every walk here peels with ListCursor rather than reading
    // Tag.Lis and the two slots behind it.
    private static Cell Resolve(Activation engine, Cell c)
        => ListCursor.Resolve(engine, c);
}
