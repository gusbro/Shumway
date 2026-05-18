using System.Text;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Atom and list manipulation. The Phase 1 implementation covers the
/// deterministic modes that show up in everyday code; non-deterministic
/// modes (<c>length</c> with both args unbound, <c>append</c> with the
/// result given and the splits enumerated) need backtracking support
/// the embedding API doesn't yet expose, so they're rejected with a
/// clear instantiation error.
/// </summary>
public static class AtomListBuiltins
{
    // ---------- length/2 ----------

    /// <summary><c>length(List, N)</c>. Supported modes:
    /// <list type="bullet">
    /// <item>(+, ?): List is a proper list — count it and unify N.</item>
    /// <item>(-, +): List is unbound, N is a non-negative integer — bind
    ///   List to a fresh list of N anonymous variables.</item>
    /// </list></summary>
    public static bool Length(Engine engine)
    {
        Cell listCell = Resolve(engine, engine.GetRegister(0));
        Cell nCell = Resolve(engine, engine.GetRegister(1));

        if (listCell.Tag != Tag.Ref)
        {
            int count = 0;
            Cell cursor = listCell;
            while (cursor.Tag == Tag.Lis)
            {
                count++;
                cursor = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex + 1));
            }
            if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
                return false;   // partial / improper list
            return engine.UnifyRegisterWithCell(1, Cell.Int(count));
        }

        if (nCell.Tag == Tag.Int)
        {
            long n = nCell.AsInt;
            if (n < 0) return false;
            int listHeapIdx = BuildFreshVarList(engine, (int)n);
            return engine.UnifyRegisterWithHeapAt(0, listHeapIdx);
        }

        throw new InvalidOperationException(
            "length/2: at least one of List, N must be sufficiently instantiated.");
    }

    private static int BuildFreshVarList(Engine engine, int count)
    {
        if (count == 0)
        {
            int nilSlot = engine.AllocateHeap(1);
            engine.SetHeap(nilSlot, Cell.Atom(AtomTable.EmptyListId));
            return nilSlot;
        }

        int start = engine.AllocateHeap(2 * count + 1);
        for (int i = 0; i < count; i++)
        {
            int lisIdx = start + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            engine.SetHeap(headIdx, Cell.UnboundVar(headIdx));
        }
        engine.SetHeap(start + 2 * count, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    // ---------- append/3 ----------

    /// <summary><c>append(L1, L2, L3)</c>. Supported modes:
    /// <list type="bullet">
    /// <item>(+, +, ?): L1 (and ideally L2) ground — result is L1 ++ L2
    ///   unified with L3.</item>
    /// <item>(?, ?, +): L1/L2 unbound, L3 ground — enumerate every
    ///   prefix/suffix split via a runtime CP (chunk 56). Each backtrack
    ///   advances the split point by one element.</item>
    /// </list></summary>
    public static bool Append(Engine engine)
    {
        // Walk L1 collecting head cells. Then build new cons cells with
        // L2 as the final tail.
        var heads = new List<Cell>();
        Cell cursor = Resolve(engine, engine.GetRegister(0));
        while (cursor.Tag == Tag.Lis)
        {
            int headIdx = cursor.AsHeapIndex;
            heads.Add(engine.GetHeap(headIdx));
            cursor = Resolve(engine, engine.GetHeap(headIdx + 1));
        }
        if (cursor.Tag == Tag.Ref)
            return AppendSplit(engine, engine.P + 9);
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            return false;   // improper L1

        Cell l2 = engine.GetRegister(1);

        if (heads.Count == 0)
            return engine.UnifyRegisters(2, 1);

        // Allocate 2N + 1 cells: N pairs of (LIS, head) + 1 tail slot.
        int start = engine.AllocateHeap(2 * heads.Count + 1);
        for (int i = 0; i < heads.Count; i++)
        {
            int lisIdx = start + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            engine.SetHeap(headIdx, heads[i]);
        }
        engine.SetHeap(start + 2 * heads.Count, l2);

        return engine.UnifyRegisterWithHeapAt(2, start);
    }

    /// <summary>Non-deterministic <c>append/3</c> path: L1 isn't bound, so
    /// we drive the split off L3. Collect L3's elements, then enumerate
    /// every split point 0..N. The CP machinery (<see cref="Engine.PushBuiltinChoicePoint"/>)
    /// makes each backtrack try the next split.</summary>
    private static bool AppendSplit(Engine engine, int returnPc)
    {
        // L3 must be ground enough to walk — collect its elements.
        var elems = new List<Cell>();
        Cell cursor = Resolve(engine, engine.GetRegister(2));
        while (cursor.Tag == Tag.Lis)
        {
            int headIdx = cursor.AsHeapIndex;
            elems.Add(engine.GetHeap(headIdx));
            cursor = Resolve(engine, engine.GetHeap(headIdx + 1));
        }
        if (cursor.Tag == Tag.Ref)
            throw new InvalidOperationException(
                "append/3: at least one of L1, L3 must be sufficiently instantiated.");
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            return false;   // improper L3

        return AppendSplitAttempt(engine, elems, splitIdx: 0, returnPc, isResume: false);
    }

    private static bool AppendSplitAttempt(
        Engine engine, IReadOnlyList<Cell> elems, int splitIdx, int returnPc, bool isResume)
    {
        int n = elems.Count;
        if (splitIdx > n) return false;

        // Push a CP for the next split point first (unless we're at the
        // last one), so a backtrack into us retries with splitIdx + 1.
        if (splitIdx < n)
        {
            int nextSplit = splitIdx + 1;
            Func<Engine, int, bool> resume = (e, _) =>
                AppendSplitAttempt(e, elems, nextSplit, returnPc, isResume: true);
            engine.PushBuiltinChoicePoint(resume, arity: 0);
        }

        // L1 = elems[0..splitIdx], L2 = elems[splitIdx..n].
        int l1Heap = BuildListFromCells(engine, elems, 0, splitIdx);
        int l2Heap = BuildListFromCells(engine, elems, splitIdx, n);
        if (!engine.UnifyRegisterWithHeapAt(0, l1Heap)) return false;
        if (!engine.UnifyRegisterWithHeapAt(1, l2Heap)) return false;
        if (isResume) engine.ResumeAtReturnPc(returnPc);
        return true;
    }

    private static int BuildListFromCells(
        Engine engine, IReadOnlyList<Cell> elems, int start, int end)
    {
        int count = end - start;
        if (count == 0)
        {
            int nilSlot = engine.AllocateHeap(1);
            engine.SetHeap(nilSlot, Cell.Atom(AtomTable.EmptyListId));
            return nilSlot;
        }
        int baseIdx = engine.AllocateHeap(2 * count + 1);
        for (int i = 0; i < count; i++)
        {
            int lisIdx = baseIdx + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            engine.SetHeap(headIdx, elems[start + i]);
        }
        engine.SetHeap(baseIdx + 2 * count, Cell.Atom(AtomTable.EmptyListId));
        return baseIdx;
    }

    // ---------- atom_codes/2 ----------

    /// <summary><c>atom_codes(Atom, Codes)</c>. Modes:
    /// <list type="bullet">
    /// <item>(+, ?): build the codes list from the atom's name.</item>
    /// <item>(-, +): build the atom by interning the string from
    ///   a list of integer character codes.</item>
    /// </list></summary>
    public static bool AtomCodes(Engine engine)
    {
        Cell atomCell = Resolve(engine, engine.GetRegister(0));
        Cell codesCell = Resolve(engine, engine.GetRegister(1));

        if (atomCell.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
            int listIdx = BuildIntCodesList(engine, name);
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }

        if (atomCell.Tag == Tag.Ref)
        {
            string name = ReadCodesString(engine, codesCell);
            int atomId = AtomTable.Intern(name, permanent: false).Id;
            return engine.UnifyRegisterWithCell(0, Cell.Atom(atomId));
        }

        throw new InvalidOperationException(
            $"atom_codes/2: first argument must be an atom or an unbound variable; got tag {atomCell.Tag}.");
    }

    private static int BuildIntCodesList(Engine engine, string s)
    {
        if (s.Length == 0)
        {
            int nilSlot = engine.AllocateHeap(1);
            engine.SetHeap(nilSlot, Cell.Atom(AtomTable.EmptyListId));
            return nilSlot;
        }

        int start = engine.AllocateHeap(2 * s.Length + 1);
        for (int i = 0; i < s.Length; i++)
        {
            int lisIdx = start + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            engine.SetHeap(headIdx, Cell.Int(s[i]));
        }
        engine.SetHeap(start + 2 * s.Length, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    private static string ReadCodesString(Engine engine, Cell codesCell)
    {
        var sb = new StringBuilder();
        Cell cursor = Resolve(engine, codesCell);
        while (cursor.Tag == Tag.Lis)
        {
            Cell head = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex));
            if (head.Tag != Tag.Int)
                throw new InvalidOperationException(
                    $"atom_codes/2: list element must be an integer code; got tag {head.Tag}.");
            sb.Append((char)head.AsInt);
            cursor = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex + 1));
        }
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            throw new InvalidOperationException(
                "atom_codes/2: Codes must be a proper list of integers.");
        return sb.ToString();
    }

    // ---------- atom_concat/3 ----------

    /// <summary><c>atom_concat(A, B, C)</c>. Supported modes:
    /// <list type="bullet">
    /// <item>(+, +, ?): A and B are atoms; C is their concatenation.</item>
    /// <item>(?, ?, +): A and/or B unbound, C is a ground atom —
    ///   enumerate every <c>(prefix, suffix)</c> split of C's name via a
    ///   runtime CP (chunk 56). Each backtrack moves the split one
    ///   character to the right.</item>
    /// </list></summary>
    public static bool AtomConcat(Engine engine)
    {
        Cell aCell = Resolve(engine, engine.GetRegister(0));
        Cell bCell = Resolve(engine, engine.GetRegister(1));

        if (aCell.Tag == Tag.Atom && bCell.Tag == Tag.Atom)
        {
            string aName = AtomTable.GetById(aCell.AsAtomId)?.Name ?? "";
            string bName = AtomTable.GetById(bCell.AsAtomId)?.Name ?? "";
            int newAtomId = AtomTable.Intern(aName + bName, permanent: false).Id;
            return engine.UnifyRegisterWithCell(2, Cell.Atom(newAtomId));
        }

        Cell cCell = Resolve(engine, engine.GetRegister(2));
        if (cCell.Tag != Tag.Atom)
            throw new InvalidOperationException(
                "atom_concat/3: at least one of A+B or C must be ground.");

        string cName = AtomTable.GetById(cCell.AsAtomId)?.Name ?? "";
        int returnPc = engine.P + 9;
        return AtomConcatSplitAttempt(engine, cName, splitIdx: 0, returnPc, isResume: false);
    }

    private static bool AtomConcatSplitAttempt(
        Engine engine, string cName, int splitIdx, int returnPc, bool isResume)
    {
        if (splitIdx > cName.Length) return false;

        if (splitIdx < cName.Length)
        {
            int nextSplit = splitIdx + 1;
            Func<Engine, int, bool> resume = (e, _) =>
                AtomConcatSplitAttempt(e, cName, nextSplit, returnPc, isResume: true);
            engine.PushBuiltinChoicePoint(resume, arity: 0);
        }

        string a = cName.Substring(0, splitIdx);
        string b = cName.Substring(splitIdx);
        int aAtomId = AtomTable.Intern(a, permanent: false).Id;
        int bAtomId = AtomTable.Intern(b, permanent: false).Id;
        if (!engine.UnifyRegisterWithCell(0, Cell.Atom(aAtomId))) return false;
        if (!engine.UnifyRegisterWithCell(1, Cell.Atom(bAtomId))) return false;
        if (isResume) engine.ResumeAtReturnPc(returnPc);
        return true;
    }

    // ---------- Helpers ----------

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}
