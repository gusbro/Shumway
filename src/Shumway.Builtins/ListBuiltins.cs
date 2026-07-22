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
        for (int k = 0; k < c.Pos && cur.Tag == Tag.Lis; k++)
            cur = Resolve(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        if (cur.Tag != Tag.Lis) return false;          // past the list end

        int headIdx = cur.AsHeapIndex;
        int pos = c.Pos;
        c.Pos = pos + 1;
        engine.PushBuiltinChoicePoint(c.Resume, arity: 3);

        long idxVal = c.OneBased ? pos + 1 : pos;
        if (engine.UnifyRegisterWithCell(0, Cell.Int(idxVal))
            && engine.UnifyRegisterWithHeapAt(2, headIdx))
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
        while (cur.Tag == Tag.Lis)
        {
            int headIdx = cur.AsHeapIndex;
            heads.Add(engine.GetHeap(headIdx));
            cur = Resolve(engine, engine.GetHeap(headIdx + 1));
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

    public static bool ListToSet(Activation engine)
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
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
