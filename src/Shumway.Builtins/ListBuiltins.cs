using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Additional list-manipulation built-ins: <c>member/2</c>,
/// <c>nth0/3</c>, <c>nth1/3</c>, <c>reverse/2</c>, <c>last/2</c>,
/// <c>list_to_set/2</c>. Together with the earlier
/// <c>length/2</c> / <c>append/3</c> / <c>sort/2</c> family this is
/// the bread-and-butter list toolkit user code reaches for.
///
/// <para>Phase-1 semantics: every operation returns the first
/// solution. The fully non-deterministic <c>member/2</c> that
/// enumerates every match lands once call/N gets choice-point
/// integration; for now <c>member(X, [a, b, c])</c> binds <c>X</c>
/// to <c>a</c> and stops.</para>
/// </summary>
public static class ListBuiltins
{
    // ---------- member/2 ----------

    public static bool Member(Engine engine)
    {
        Cell elem = engine.GetRegister(0);
        Cell cur = Resolve(engine, engine.GetRegister(1));
        while (cur.Tag == Tag.Lis)
        {
            int headIdx = cur.AsHeapIndex;
            // Trial-unify with head. Save/restore on miss.
            int savedHeapTop = engine.HeapTop;
            int savedBindingTrail = engine.BindingTrailTop;
            int savedExtraTrail = engine.ExtraTrailTop;
            int savedHb = engine.Hb;
            engine.SetHb(engine.HeapTop);

            if (engine.UnifyRegisterWithHeapAt(0, headIdx))
            {
                engine.SetHb(savedHb);
                return true;
            }

            engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
            engine.SetHeapTop(savedHeapTop);
            engine.SetHb(savedHb);

            cur = Resolve(engine, engine.GetHeap(headIdx + 1));
        }
        return false;
    }

    // ---------- nth0/3 + nth1/3 ----------

    public static bool Nth0(Engine engine) => NthImpl(engine, oneBased: false);
    public static bool Nth1(Engine engine) => NthImpl(engine, oneBased: true);

    private static bool NthImpl(Engine engine, bool oneBased)
    {
        Cell n = Resolve(engine, engine.GetRegister(0));
        // A variable index enumerates every position on backtracking — the
        // SWI/SICStus library behaviour real programs rely on (e.g. iterating a
        // board with nth0(Row, Board, R)). A bound non-integer is a type error.
        if (n.Tag == Tag.Ref)
            return NthEnumerate(engine, oneBased, engine.BuiltinReturnPc, pos: 0, isResume: false);
        if (n.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "integer");
        long target = n.AsInt;
        if (oneBased) target--;
        if (target < 0) return false;

        Cell cur = Resolve(engine, engine.GetRegister(1));
        long i = 0;
        while (cur.Tag == Tag.Lis)
        {
            int headIdx = cur.AsHeapIndex;
            if (i == target)
                return engine.UnifyRegisterWithHeapAt(2, headIdx);
            cur = Resolve(engine, engine.GetHeap(headIdx + 1));
            i++;
        }
        return false;
    }

    /// <summary>Variable-index <c>nth0</c>/<c>nth1</c>: yield the element at
    /// position <paramref name="pos"/>, then push a choice point so a backtrack
    /// retries position <paramref name="pos"/>+1 — mirroring the Prolog
    /// <c>nth0(I,[E|_],E) ; nth0(I,[_|T],E)</c> enumeration. A failed unification
    /// at this position (a bound Elem that doesn't match) falls straight through
    /// into that choice point.</summary>
    private static bool NthEnumerate(Engine engine, bool oneBased, int returnPc, int pos, bool isResume)
    {
        Cell cur = Resolve(engine, engine.GetRegister(1));
        for (int k = 0; k < pos && cur.Tag == Tag.Lis; k++)
            cur = Resolve(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        if (cur.Tag != Tag.Lis) return false;          // past the list end

        int headIdx = cur.AsHeapIndex;
        Func<Engine, int, bool> resume =
            (e, _) => NthEnumerate(e, oneBased, returnPc, pos + 1, isResume: true);
        engine.PushBuiltinChoicePoint(resume, arity: 3);

        long idxVal = oneBased ? pos + 1 : pos;
        if (engine.UnifyRegisterWithCell(0, Cell.Int(idxVal))
            && engine.UnifyRegisterWithHeapAt(2, headIdx))
        {
            if (isResume) engine.ResumeAtReturnPc(returnPc);
            return true;
        }
        return false;                                   // → retries pos + 1
    }

    // ---------- reverse/2 ----------

    public static bool Reverse(Engine engine)
    {
        var heads = new List<Cell>();
        Cell cur = Resolve(engine, engine.GetRegister(0));
        while (cur.Tag == Tag.Lis)
        {
            int headIdx = cur.AsHeapIndex;
            heads.Add(engine.GetHeap(headIdx));
            cur = Resolve(engine, engine.GetHeap(headIdx + 1));
        }
        if (cur.Tag == Tag.Ref)
            // Chunk 131c: a partial list — the tail is unbound, so we
            // can't determine the length. ISO instantiation_error.
            throw new PrologRuntimeException("instantiation_error");
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId) return false;

        // Build reversed list.
        heads.Reverse();
        int listIdx = BuildList(engine, heads);
        return engine.UnifyRegisterWithHeapAt(1, listIdx);
    }

    // ---------- last/2 ----------

    public static bool Last(Engine engine)
    {
        Cell cur = Resolve(engine, engine.GetRegister(0));
        int lastHeadIdx = -1;
        while (cur.Tag == Tag.Lis)
        {
            int headIdx = cur.AsHeapIndex;
            lastHeadIdx = headIdx;
            cur = Resolve(engine, engine.GetHeap(headIdx + 1));
        }
        if (lastHeadIdx < 0) return false;   // empty list — no last element
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId) return false;
        return engine.UnifyRegisterWithHeapAt(1, lastHeadIdx);
    }

    // ---------- list_to_set/2 ----------

    public static bool ListToSet(Engine engine)
    {
        // Preserve first occurrence order; drop subsequent structurally
        // equal duplicates.
        var seen = new List<Cell>();
        Cell cur = Resolve(engine, engine.GetRegister(0));
        while (cur.Tag == Tag.Lis)
        {
            int headIdx = cur.AsHeapIndex;
            Cell head = Resolve(engine, engine.GetHeap(headIdx));
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
            cur = Resolve(engine, engine.GetHeap(headIdx + 1));
        }
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId) return false;
        int listIdx = BuildList(engine, seen);
        return engine.UnifyRegisterWithHeapAt(1, listIdx);
    }

    // ---------- Shared helpers ----------

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
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
