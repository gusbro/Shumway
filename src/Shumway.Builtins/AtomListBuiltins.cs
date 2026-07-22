using System.Text;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Atom and list manipulation builtins: <c>length/2</c>, <c>append/3</c>
/// (including the non-deterministic split modes), <c>atom_codes/2</c> and
/// <c>atom_concat/3</c> (including split enumeration).
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
    public static bool Length(Activation engine)
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

        // ISO precedence: both var → instantiation_error; N at the wrong
        // type → type_error(integer, _).
        if (listCell.Tag == Tag.Ref && nCell.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        throw new PrologRuntimeException("type_error", "integer");
    }

    private static int BuildFreshVarList(Activation engine, int count)
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
    ///   prefix/suffix split via a runtime CP. Each backtrack advances
    ///   the split point by one element.</item>
    /// </list></summary>
    public static bool Append(Activation engine)
    {
        // Two-pass det path: walk L1's spine once to count (and classify
        // the tail), reserve the result cells in one allocation, then walk
        // again filling — no intermediate buffer.
        int count = 0;
        Cell cursor = Resolve(engine, engine.GetRegister(0));
        while (cursor.Tag == Tag.Lis)
        {
            count++;
            cursor = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex + 1));
        }
        if (cursor.Tag == Tag.Ref)
            return AppendSplit(engine, engine.BuiltinReturnPc);
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            return false;   // improper L1

        Cell l2 = engine.GetRegister(1);

        if (count == 0)
            return engine.UnifyRegisters(2, 1);

        // Allocate 2N + 1 cells: N pairs of (LIS, head) + 1 tail slot.
        // (ADR-017 layout: pair i's LIS cell at start + 2i points at its
        // head cell, start + 2i + 1; the cell after the head — the next
        // pair's LIS slot, or the final extra slot — is the tail.)
        int start = engine.AllocateHeap(2 * count + 1);
        cursor = Resolve(engine, engine.GetRegister(0));
        for (int i = 0; i < count; i++)
        {
            int srcHeadIdx = cursor.AsHeapIndex;
            int lisIdx = start + 2 * i;
            engine.SetHeap(lisIdx, Cell.Lis(lisIdx + 1));
            engine.SetHeap(lisIdx + 1, engine.GetHeap(srcHeadIdx));
            cursor = Resolve(engine, engine.GetHeap(srcHeadIdx + 1));
        }
        engine.SetHeap(start + 2 * count, l2);

        return engine.UnifyRegisterWithHeapAt(2, start);
    }

    /// <summary>Non-deterministic <c>append/3</c> path: L1 isn't bound, so
    /// we drive the split off L3. Collect L3's elements, then enumerate
    /// every split point 0..N. The CP machinery (<see cref="Activation.PushBuiltinChoicePoint"/>)
    /// makes each backtrack try the next split.</summary>
    private static bool AppendSplit(Activation engine, int returnPc)
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
            // L3 is a partial list while L1 is open too — nothing closed to
            // drive the split off. Do NOT raise instantiation_error: the PURE
            // append/3 enumerates solutions by unification, and the classic
            // difference-list idiom `append(Open, [], Open)` (closing an open
            // list's tail hole) must succeed at the first solution. Enumerate
            // k = 0, 1, 2, …: L1 unifies with a k-element fresh-var list and
            // L3 with those same vars prefixed onto L2 — unification against
            // the callers' partial lists prunes each attempt. Unbounded on
            // backtracking, exactly like SWI's append(X, Y, Z).
            return new AppendOpenCursor(returnPc).Start(engine);

        // `cursor` is L3's final tail: [] for a proper list, or some other
        // term (atom / compound) for an improper list. ISO append/3 splits an
        // improper list too — every suffix L2 simply carries that tail
        // (append([3], fac, [3|fac]) etc.), so we thread it through to the L2
        // build instead of rejecting it. For a proper list this tail is [],
        // so the behaviour is unchanged.
        return new AppendSplitCursor(elems, cursor, returnPc).Start(engine);
    }

    /// <summary>Resume state for the non-deterministic <c>append/3</c> split:
    /// the collected L3 elements, the (possibly improper) tail, and the
    /// running split index, plus a cached resume delegate — allocated once
    /// per call and re-pushed unchanged on each backtrack, no per-split
    /// closure.</summary>
    private sealed class AppendSplitCursor
    {
        private readonly IReadOnlyList<Cell> _elems;
        private readonly Cell _suffixTail;
        private readonly int _returnPc;
        private int _splitIdx;
        public readonly Func<Activation, int, bool> Resume;

        public AppendSplitCursor(IReadOnlyList<Cell> elems, Cell suffixTail, int returnPc)
        {
            _elems = elems;
            _suffixTail = suffixTail;
            _returnPc = returnPc;
            _splitIdx = 0;
            Resume = (e, _) => Attempt(e, isResume: true);
        }

        public bool Start(Activation engine) => Attempt(engine, isResume: false);

        private bool Attempt(Activation engine, bool isResume)
        {
            int n = _elems.Count;
            int splitIdx = _splitIdx;
            if (splitIdx > n) return false;

            // Push a CP for the next split point first (unless we're at the
            // last one), so a backtrack into us retries with splitIdx + 1.
            // arity 3: the CP must restore append/3's argument registers, else
            // a following body goal whose builtin call takes >= (resultReg+1)
            // args clobbers X0/X1 and the enumeration breaks on backtrack.
            if (splitIdx < n)
            {
                _splitIdx = splitIdx + 1;
                engine.PushBuiltinChoicePoint(Resume, arity: 3);
            }

            // L1 = elems[0..splitIdx] (always proper); L2 = elems[splitIdx..n]
            // with L3's tail ([] for a proper L3, the improper tail otherwise).
            int l1Heap = BuildListFromCells(engine, _elems, 0, splitIdx, Cell.Atom(AtomTable.EmptyListId));
            int l2Heap = BuildListFromCells(engine, _elems, splitIdx, n, _suffixTail);
            if (!engine.UnifyRegisterWithHeapAt(0, l1Heap)) return false;
            if (!engine.UnifyRegisterWithHeapAt(1, l2Heap)) return false;
            if (isResume) engine.ResumeAtReturnPc(_returnPc);
            return true;
        }
    }

    /// <summary>Resume state for the fully-open <c>append/3</c> mode (both L1
    /// and L3 have unbound tails — see the call site in
    /// <see cref="AppendSplit"/>). Attempt k unifies L1 with a k-element
    /// fresh-var list and L3 with those same k vars prefixed onto L2; the
    /// engine's builtin-CP trail unwind resets the bindings between attempts.
    /// The enumeration is unbounded (pure-append semantics — SWI behaves the
    /// same on <c>append(X, Y, Z)</c>); callers commit with a cut.</summary>
    private sealed class AppendOpenCursor
    {
        private readonly int _returnPc;
        private int _k;
        public readonly Func<Activation, int, bool> Resume;

        public AppendOpenCursor(int returnPc)
        {
            _returnPc = returnPc;
            _k = 0;
            Resume = (e, _) => Attempt(e, isResume: true);
        }

        public bool Start(Activation engine) => Attempt(engine, isResume: false);

        private bool Attempt(Activation engine, bool isResume)
        {
            int k = _k;
            // Always re-arm for k+1 — the solution set is unbounded.
            _k = k + 1;
            engine.PushBuiltinChoicePoint(Resume, arity: 3);

            // L1 = [V1..Vk] (fresh vars, closed). Unify FIRST so the shared
            // var cells pick up L1's actual elements before L3 sees them.
            int l1Heap = BuildFreshVarList(engine, k);
            if (!engine.UnifyRegisterWithHeapAt(0, l1Heap)) return false;

            // L3 = [V1..Vk | L2] — the SAME var cells (now possibly bound),
            // tail = L2's current cell.
            Cell l2 = engine.GetRegister(1);
            int l3Heap;
            if (k == 0)
            {
                l3Heap = engine.AllocateHeap(1);
                engine.SetHeap(l3Heap, l2);
            }
            else
            {
                // Mirror BuildFreshVarList's layout: pair i's LIS at base+2i
                // points at its head (a Ref to L1's var cell); the slot after
                // the last pair carries the tail.
                l3Heap = engine.AllocateHeap(2 * k + 1);
                for (int i = 0; i < k; i++)
                {
                    int lisIdx = l3Heap + 2 * i;
                    int headIdx = lisIdx + 1;
                    engine.SetHeap(lisIdx, Cell.Lis(headIdx));
                    // L1's list layout: element i's head cell at l1Heap + 2i + 1.
                    engine.SetHeap(headIdx, Cell.Ref(l1Heap + 2 * i + 1));
                }
                engine.SetHeap(l3Heap + 2 * k, l2);
            }
            if (!engine.UnifyRegisterWithHeapAt(2, l3Heap)) return false;
            if (isResume) engine.ResumeAtReturnPc(_returnPc);
            return true;
        }
    }

    private static int BuildListFromCells(
        Activation engine, IReadOnlyList<Cell> elems, int start, int end, Cell finalTail)
    {
        int count = end - start;
        if (count == 0)
        {
            int tailSlot = engine.AllocateHeap(1);
            engine.SetHeap(tailSlot, finalTail);
            return tailSlot;
        }
        int baseIdx = engine.AllocateHeap(2 * count + 1);
        for (int i = 0; i < count; i++)
        {
            int lisIdx = baseIdx + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            engine.SetHeap(headIdx, elems[start + i]);
        }
        engine.SetHeap(baseIdx + 2 * count, finalTail);
        return baseIdx;
    }

    // ---------- atom_codes/2 ----------

    /// <summary><c>atom_codes(Atom, Codes)</c>. Modes:
    /// <list type="bullet">
    /// <item>(+, ?): build the codes list from the atom's name.</item>
    /// <item>(-, +): build the atom by interning the string from
    ///   a list of integer character codes.</item>
    /// </list></summary>
    public static bool AtomCodes(Activation engine)
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

        // First arg bound to something other than an atom.
        throw new PrologRuntimeException("type_error", "atom");
    }

    private static int BuildIntCodesList(Activation engine, string s)
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

    private static string ReadCodesString(Activation engine, Cell codesCell)
    {
        var sb = new StringBuilder();
        Cell cursor = Resolve(engine, codesCell);
        if (cursor.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        while (true)
        {
            // A PSTR is a code list; consume its text and continue at its
            // tail.
            if (cursor.Tag == Tag.Pstr)
            {
                sb.Append(engine.ReadPstrChain(cursor, out cursor));
                continue;
            }
            if (cursor.Tag != Tag.Lis) break;
            Cell head = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex));
            if (head.Tag == Tag.Ref)
                throw new PrologRuntimeException("instantiation_error");
            if (head.Tag != Tag.Int)
                throw new PrologRuntimeException("type_error", "character_code");
            sb.Append((char)head.AsInt);
            cursor = Resolve(engine, engine.GetHeap(cursor.AsHeapIndex + 1));
        }
        if (cursor.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("type_error", "list");
        return sb.ToString();
    }

    // ---------- atom_concat/3 ----------

    /// <summary><c>atom_concat(A, B, C)</c>. Supported modes:
    /// <list type="bullet">
    /// <item>(+, +, ?): A and B are atoms; C is their concatenation.</item>
    /// <item>(?, ?, +): A and/or B unbound, C is a ground atom —
    ///   enumerate every <c>(prefix, suffix)</c> split of C's name via a
    ///   runtime CP. Each backtrack moves the split one character to the
    ///   right.</item>
    /// </list></summary>
    public static bool AtomConcat(Activation engine)
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
        {
            // ISO §8.16.2: if C is var, neither direction can drive
            // synthesis unless BOTH A and B are atoms. If C is var and
            // either A or B is var, raise instantiation_error. If C
            // is bound to a non-atom, raise type_error(atom, C).
            if (cCell.Tag == Tag.Ref
                && (aCell.Tag == Tag.Ref || bCell.Tag == Tag.Ref))
            {
                Shumway.Core.Diagnostics.ChoicePointTrace.DumpAtSite(
                    engine, "atom_concat/3 instantiation_error");
                throw new PrologRuntimeException("instantiation_error");
            }
            Shumway.Core.Diagnostics.ChoicePointTrace.DumpAtSite(
                engine, "atom_concat/3 type_error(atom)");
            throw new PrologRuntimeException("type_error", "atom");
        }

        string cName = AtomTable.GetById(cCell.AsAtomId)?.Name ?? "";
        int returnPc = engine.BuiltinReturnPc;
        return new AtomConcatSplitCursor(cName, returnPc).Start(engine);
    }

    /// <summary>Resume state for the non-deterministic <c>atom_concat/3</c>
    /// split: the atom being split and the running split index, plus a cached
    /// resume delegate — allocated once per call, re-pushed unchanged on each
    /// backtrack (no per-split closure).</summary>
    private sealed class AtomConcatSplitCursor
    {
        private readonly string _cName;
        private readonly int _returnPc;
        private int _splitIdx;
        public readonly Func<Activation, int, bool> Resume;

        public AtomConcatSplitCursor(string cName, int returnPc)
        {
            _cName = cName;
            _returnPc = returnPc;
            _splitIdx = 0;
            Resume = (e, _) => Attempt(e, isResume: true);
        }

        public bool Start(Activation engine) => Attempt(engine, isResume: false);

        private bool Attempt(Activation engine, bool isResume)
        {
            int splitIdx = _splitIdx;
            if (splitIdx > _cName.Length) return false;

            if (splitIdx < _cName.Length)
            {
                _splitIdx = splitIdx + 1;
                engine.PushBuiltinChoicePoint(Resume, arity: 3);  // restore atom_concat/3 args
            }

            string a = _cName.Substring(0, splitIdx);
            string b = _cName.Substring(splitIdx);
            int aAtomId = AtomTable.Intern(a, permanent: false).Id;
            int bAtomId = AtomTable.Intern(b, permanent: false).Id;
            if (!engine.UnifyRegisterWithCell(0, Cell.Atom(aAtomId))) return false;
            if (!engine.UnifyRegisterWithCell(1, Cell.Atom(bAtomId))) return false;
            if (isResume) engine.ResumeAtReturnPc(_returnPc);
            return true;
        }
    }

    // ---------- Helpers ----------

    private static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}
