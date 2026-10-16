using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Additional list-manipulation built-ins: <c>nth0/3</c>, <c>nth1/3</c>,
/// <c>reverse/2</c>, <c>last/2</c>, <c>list_to_set/2</c>. Together with
/// the <c>length/2</c> / <c>append/3</c> / <c>sort/2</c> family this is
/// the bread-and-butter list toolkit user code reaches for.
///
/// <para><see cref="Member"/> is not registered — <c>member/2</c> lives
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
        // An attributed index is a variable too: each position binds it and
        // its constraints decide.
        if (n.Tag is Tag.Ref or Tag.AttVar)
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
        // Prologue generate mode: an integer index against a partial list
        // (unbound tail, attributed or not) extends it — nth0(2, Es, E) gives
        // Es = [_,_,E|_]. A closed or improper tail still just fails.
        if (cur.Tag is not (Tag.Ref or Tag.AttVar)) return false;
        Cell built = BuildOpenSkeleton(engine, target - i, out int elemSlot);
        return engine.UnifyHeapWithCell(cur.AsHeapIndex, built)
            && engine.UnifyRegisterWithCell(2, Cell.Ref(elemSlot));
    }

    /// <summary>Extends a partial list by a fresh <c>[H|T]</c> cons at its
    /// unbound tail <paramref name="cur"/> — the prologue generate mode, and
    /// what makes a variable-index enumeration over a partial list produce
    /// answers ad infinitum instead of stopping at the end. The extension
    /// binding is trailed like any other, so backtracking past the whole
    /// call undoes it. An attributed tail binds through the attribute
    /// protocol, which queues its hooks.</summary>
    private static void Extend(Activation engine, ref Cell cur, out Cell head)
    {
        int h = engine.AllocateHeap(1);
        engine.SetHeap(h, Cell.Ref(h));
        int t = engine.AllocateHeap(1);
        engine.SetHeap(t, Cell.Ref(t));
        int p = engine.AllocateHeap(2);
        engine.SetHeap(p, Cell.Ref(h));
        engine.SetHeap(p + 1, Cell.Ref(t));
        if (cur.Tag == Tag.Ref) engine.Bind(cur.AsHeapIndex, Cell.Lis(p));
        else engine.UnifyHeapWithCell(cur.AsHeapIndex, Cell.Lis(p));
        head = Cell.Ref(h);
        cur = Cell.Ref(t);
    }

    // ---------- '$nth_gen'/5 ----------
    // The step of a free-index nth0/nth1 past an unbound tail, as a builtin the
    // wake driver enters once the hooks of the tail's binding have run
    // (docs/design/builtin-continues-after-wakeups.md): H is the element at
    // position Idx of the list whose rest is Tail. X0 = Tail, X1 = Elem,
    // X2 = H, X3 = Idx, X4 = Index; the frame also saves X5, the return
    // address, as a raw integer, so no C# object outlives a retry.

    private static readonly int NthGenFunctorId =
        FunctorTable.Intern(AtomTable.Intern("$nth_gen", permanent: true).Id, 5);

    /// <summary>Queues <c>'$nth_gen'(Tail, Elem, H, Idx, Index)</c> behind
    /// the wakeups the binding of the tail queued. The item belongs to that
    /// binding, at <paramref name="home"/>.</summary>
    private static void QueueNthGen(Activation engine, Cell tail, Cell elem,
                                    Cell head, long idx, Cell index, int home)
    {
        int f = engine.AllocateHeap(6);
        engine.SetHeap(f, Cell.Functor(NthGenFunctorId));
        engine.SetHeap(f + 1, tail);
        engine.SetHeap(f + 2, elem);
        engine.SetHeap(f + 3, head);
        engine.SetHeap(f + 4, Cell.Int(idx));
        engine.SetHeap(f + 5, index);
        int g = engine.AllocateHeap(1);
        engine.SetHeap(g, Cell.Str(f));
        engine.QueueWakeGoal(g, home);
    }

    public static bool NthGen(Activation engine)
    {
        engine.SetRegister(5, Cell.RawInt(engine.BuiltinReturnPc));
        engine.ArmBuiltinChoicePoint(NthGenResume, arity: 6, more: true, isResume: false);
        return NthGenYield(engine);
    }

    private static readonly Func<Activation, int, bool> NthGenResume = (e, _) => NthGenNext(e);

    // Index = Idx, Elem = H. A miss returns false into the armed frame, which
    // retries at the next position.
    private static bool NthGenYield(Activation engine)
        => engine.UnifyRegisterWithCell(4, Resolve(engine, engine.GetRegister(3)))
           && engine.UnifyRegisterWithCell(1, engine.GetRegister(2));

    /// <summary>The retry, with X0..X5 restored: the second clause of the
    /// enumeration, and the last, so the frame is trusted before the tail is
    /// bound to the next cons. With wakeups pending the next level is queued
    /// behind them; otherwise it is armed and yields in line.</summary>
    private static bool NthGenNext(Activation engine)
    {
        engine.TrustBuiltinChoicePoint();
        int returnPc = (int)engine.GetRegister(5).Data;
        long idx = Resolve(engine, engine.GetRegister(3)).AsInt + 1;
        Cell cur = Resolve(engine, engine.GetRegister(0));
        Cell head;
        if (ListCursor.TryUncons(engine, cur, out head, out Cell tail))
        {
            cur = Resolve(engine, tail);
        }
        else if (cur.Tag is Tag.Ref or Tag.AttVar)
        {
            int home = cur.AsHeapIndex;
            Extend(engine, ref cur, out head);
            if (engine.HasPendingWakeups)
            {
                QueueNthGen(engine, cur, engine.GetRegister(1), head, idx,
                    engine.GetRegister(4), home);
                engine.ResumeAtReturnPc(returnPc);
                return true;
            }
        }
        else
        {
            return false;                       // a closed or improper tail
        }
        engine.SetRegister(0, cur);
        engine.SetRegister(2, head);
        engine.SetRegister(3, Cell.Int(idx));
        engine.ArmBuiltinChoicePoint(NthGenResume, arity: 6, more: true, isResume: false);
        if (!NthGenYield(engine)) return false;
        engine.ResumeAtReturnPc(returnPc);
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
    /// no per-position closure). The rest of the list is not kept here: a
    /// heap GC between backtracks moves the list cells (see NthStep).</summary>
    private sealed class NthCursor
    {
        public long Pos;
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
        // The rest of the list rides in the choice point as a fourth saved
        // register, which the heap GC relocates, so a step goes on from where
        // the previous one stopped: walking from the list's start each time
        // makes the enumeration quadratic. Restoring it clobbers X3, which is
        // dead after the call: a variable used past a body goal lives in a
        // Y slot.
        Cell cur = Resolve(engine, engine.GetRegister(isResume ? 3 : 1));
        Cell head;
        if (ListCursor.TryUncons(engine, cur, out head, out Cell tail))
        {
            cur = Resolve(engine, tail);
            engine.ArmBuiltinChoicePoint(c.Resume, arity: 4, more: true, isResume);
        }
        else if (cur.Tag is Tag.Ref or Tag.AttVar)
        {
            // A partial list grows a cell here, before the choice point: the
            // frame being retried saved a heap top below it, so that frame is
            // trusted and a new one pushed above the new cell. An attributed
            // tail wakes its hooks, which run at the next goal boundary, above
            // any frame armed here; the step then goes on from the batch, once
            // they have run (docs/design/builtin-continues-after-wakeups.md).
            if (isResume) engine.TrustBuiltinChoicePoint();
            int home = cur.AsHeapIndex;
            Extend(engine, ref cur, out head);
            if (engine.HasPendingWakeups)
            {
                QueueNthGen(engine, cur, engine.GetRegister(2), head,
                    c.OneBased ? c.Pos + 1 : c.Pos, engine.GetRegister(0), home);
                if (isResume) engine.ResumeAtReturnPc(c.ReturnPc);
                return true;
            }
            engine.ArmBuiltinChoicePoint(c.Resume, arity: 4, more: true, isResume: false);
        }
        else
        {
            // A closed or improper tail ends the enumeration. The frame goes
            // before the failure: left armed, the engine retries it without end.
            if (isResume) engine.TrustBuiltinChoicePoint();
            return false;
        }

        long pos = c.Pos;
        c.Pos = pos + 1;
        // An attributed variable kept outside its home is a reference to it.
        engine.SetTopCpArgRegister(3, cur.Tag == Tag.AttVar ? Cell.Ref(cur.AsHeapIndex) : cur);

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
        var guard = new SpineGuard(cur);
        while (ListCursor.TryUncons(engine, cur, out Cell head, out Cell tail))
        {
            heads.Add(head);
            cur = Resolve(engine, tail);
            if (guard.Loops(cur)) throw ListCursor.InfiniteList();
        }
        if (cur.Tag is Tag.Ref or Tag.AttVar)
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
        var guard = new SpineGuard(cur);
        while (ListCursor.TryUncons(engine, cur, out Cell head, out Cell tail))
        {
            last = head;
            any = true;
            cur = Resolve(engine, tail);
            if (guard.Loops(cur)) throw ListCursor.InfiniteList();
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
        var guard = new SpineGuard(cur);
        bool cyclic = false;
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
            // A cyclic spine repeats what it has shown: its elements are a
            // finite set, every one of them seen by the time the walk is
            // back on a cell it has been on.
            if (guard.Loops(cur)) { cyclic = true; break; }
        }
        if (!cyclic && (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)) return false;
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
