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

    /// <summary><c>append(L1, L2, L3)</c>. Phase 1 supports the (+, +, ?)
    /// mode: L1 and L2 are proper lists (or just L1 a proper list with L2
    /// any cell), result is L1 ++ L2 unified with L3. The (?, ?, +)
    /// non-deterministic split mode is deferred until the embedding API
    /// supports multi-solution enumeration.</summary>
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
            throw new InvalidOperationException(
                "append/3: only the (+, +, ?) mode is supported in Phase 1.");
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

    /// <summary><c>atom_concat(A, B, C)</c> in (+, +, ?) mode — the
    /// non-deterministic split mode (?, ?, +) is deferred.</summary>
    public static bool AtomConcat(Engine engine)
    {
        Cell aCell = Resolve(engine, engine.GetRegister(0));
        Cell bCell = Resolve(engine, engine.GetRegister(1));
        if (aCell.Tag != Tag.Atom || bCell.Tag != Tag.Atom)
            throw new InvalidOperationException(
                "atom_concat/3: Phase 1 only supports the (+, +, ?) mode.");

        string aName = AtomTable.GetById(aCell.AsAtomId)?.Name ?? "";
        string bName = AtomTable.GetById(bCell.AsAtomId)?.Name ?? "";
        int newAtomId = AtomTable.Intern(aName + bName, permanent: false).Id;
        return engine.UnifyRegisterWithCell(2, Cell.Atom(newAtomId));
    }

    // ---------- Helpers ----------

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}
