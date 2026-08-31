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
    /// <summary>Builds a fresh list of <paramref name="count"/> unbound
    /// variables on the heap and returns its start index (ADR-017 layout).
    /// Used by append/3's enumeration cursor.</summary>
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
        Cell cursor = ListCursor.Resolve(engine, engine.GetRegister(0));
        while (ListCursor.TryUncons(engine, cursor, out _, out Cell countTail))
        {
            count++;
            cursor = ListCursor.Resolve(engine, countTail);
        }
        if (cursor.Tag is Tag.Ref or Tag.AttVar)
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
        cursor = ListCursor.Resolve(engine, engine.GetRegister(0));
        for (int i = 0; i < count; i++)
        {
            ListCursor.TryUncons(engine, cursor, out Cell head, out Cell tail);
            int lisIdx = start + 2 * i;
            engine.SetHeap(lisIdx, Cell.Lis(lisIdx + 1));
            engine.SetHeap(lisIdx + 1, head);
            cursor = ListCursor.Resolve(engine, tail);
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
        Cell cursor = ListCursor.Resolve(engine, engine.GetRegister(2));
        while (ListCursor.TryUncons(engine, cursor, out Cell el, out Cell elTail))
        {
            elems.Add(el);
            cursor = ListCursor.Resolve(engine, elTail);
        }
        if (cursor.Tag is Tag.Ref or Tag.AttVar)
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

        // Mode-directed: a proper-list L2 pins the split point (the
        // append(-, +, +) suffix idiom), so the single candidate is checked
        // without a choice point — the cursor would leave a dead CP after the
        // match while the remaining splits can only fail.
        int m = 0;
        Cell c2 = ListCursor.Resolve(engine, engine.GetRegister(1));
        while (ListCursor.TryUncons(engine, c2, out _, out Cell c2Tail))
        {
            m++;
            c2 = ListCursor.Resolve(engine, c2Tail);
        }
        if (c2.Tag == Tag.Atom && c2.AsAtomId == AtomTable.EmptyListId)
        {
            if (m > elems.Count) return false;
            int split = elems.Count - m;
            // L2 is BUILT here rather than shared with L3's spine, which is
            // what the enumerating path below does. Sharing means walking to
            // the split point, and this mode's common shape is a long prefix
            // with a short L2 (`append(_, [Last], L)`): the walk would cost a
            // step per element to save building the handful that L2 has.
            int l1Heap = BuildListFromCells(engine, elems, 0, split, Cell.Atom(AtomTable.EmptyListId));
            int l2Heap = BuildListFromCells(engine, elems, split, elems.Count, cursor);
            return engine.UnifyRegisterWithHeapAt(0, l1Heap)
                && engine.UnifyRegisterWithHeapAt(1, l2Heap);
        }

        return new AppendSplitCursor(elems, CollectSuffixes(engine, elems.Count), returnPc)
            .Start(engine);
    }

    /// <summary>The suffix at every split point, for the ENUMERATING path.
    ///
    /// <para>Every split hands L2 a suffix of L3, and that suffix ALREADY
    /// exists inside L3's spine, so there is nothing to build for it: sharing
    /// it is what the two-clause Prolog <c>append/3</c> does when it reaches
    /// <c>append([], L, L)</c>. One walk of the spine, and each of the n + 1
    /// solutions reads one entry instead of building a list, which is what
    /// takes the enumeration from two lists per solution down to one.</para>
    ///
    /// <para>Only worth it when there ARE n + 1 solutions to amortise it over:
    /// the deterministic split builds its single L2 instead.</para></summary>
    private static List<Cell> CollectSuffixes(Activation engine, int count)
    {
        var suffixes = new List<Cell>(count + 1);
        Cell cursor = ListCursor.Resolve(engine, engine.GetRegister(2));
        for (int i = 0; ; i++)
        {
            suffixes.Add(cursor);
            if (i == count) return suffixes;
            ListCursor.TryUncons(engine, cursor, out _, out Cell tail);
            cursor = ListCursor.Resolve(engine, tail);
        }
    }

    /// <summary>Resume state for the non-deterministic <c>append/3</c> split:
    /// the collected L3 elements, the suffix each split point yields, and the
    /// running split index, plus a cached resume delegate — allocated once
    /// per call and re-pushed unchanged on each backtrack, no per-split
    /// closure.</summary>
    private sealed class AppendSplitCursor
    {
        private readonly IReadOnlyList<Cell> _elems;
        private readonly IReadOnlyList<Cell> _suffixes;
        private readonly int _returnPc;
        private int _splitIdx;
        public readonly Func<Activation, int, bool> Resume;

        public AppendSplitCursor(
            IReadOnlyList<Cell> elems, IReadOnlyList<Cell> suffixes, int returnPc)
        {
            _elems = elems;
            _suffixes = suffixes;
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

            // L1 = elems[0..splitIdx], built fresh because it is a new list.
            // L2 is L3's own suffix from splitIdx on, so it is handed over
            // rather than rebuilt.
            int l1Heap = BuildListFromCells(engine, _elems, 0, splitIdx, Cell.Atom(AtomTable.EmptyListId));
            if (!engine.UnifyRegisterWithHeapAt(0, l1Heap)) return false;
            if (!engine.UnifyRegisterWithCell(1, _suffixes[splitIdx])) return false;
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
            // With BOTH arguments bound the list is still type-checked
            // (§8.16.5.3): atom_codes(abc, [a,b,c]) is
            // type_error(integer, a), not a silent failure.
            // Only a PROPER list is validated-and-compared: a partial
            // one (atom_codes(abc, [0'a|T])) must still unify.
            if (ListCursor.IsProperListCell(engine, codesCell))
                return ReadCodesString(engine, codesCell) == name;
            int listIdx = BuildIntCodesList(engine, name);
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }

        if (atomCell.Tag is Tag.Ref or Tag.AttVar)
        {
            string name = ReadCodesString(engine, codesCell);
            int atomId = AtomTable.Intern(name, permanent: false).Id;
            return engine.UnifyRegisterWithCell(0, Cell.Atom(atomId));
        }

        // First arg bound to something other than an atom. SWI coerces a
        // number/string to its text and yields its codes.
        if (SwiLenient.TryCoerce(engine, atomCell, out string coerced))
            return engine.UnifyRegisterWithHeapAt(1, BuildIntCodesList(engine, coerced));
        throw new PrologRuntimeException("type_error", "atom", engine, atomCell);
    }

    private static int BuildIntCodesList(Activation engine, string s)
        => engine.MakeTextList(s, TextKind.Codes);

    private static string ReadCodesString(Activation engine, Cell codesCell)
    {
        var sb = new StringBuilder();
        Cell listStart = Resolve(engine, codesCell);
        Cell cursor = listStart;
        if (cursor.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        // A bound non-list is type_error(list, L) before any element.
        if (cursor.Tag is not (Tag.Lis or Tag.Pstr)
            && !(cursor.Tag == Tag.Atom && cursor.AsAtomId == AtomTable.EmptyListId))
            throw new PrologRuntimeException("type_error", "list", engine, listStart);
        while (true)
        {
            // A packed run of CODES is consumed in bulk; a chars run falls
            // through to the element loop and raises the ISO element error
            // from its own head's tag (ADR-047).
            if (cursor.Tag == Tag.Pstr && cursor.AsPstrKind == TextKind.Codes
                && cursor.AsPstrLength > 0)
            {
                sb.Append(engine.ReadPstrChain(cursor, out cursor));
                continue;
            }
            if (!ListCursor.TryUncons(engine, cursor, out Cell rawHead, out Cell rTail)) break;
            Cell head = Resolve(engine, rawHead);
            if (head.Tag is Tag.Ref or Tag.AttVar)
                throw new PrologRuntimeException("instantiation_error");
            if (head.Tag != Tag.Int)
                throw new PrologRuntimeException(
                    "type_error", "integer", engine, head);
            // Any Unicode scalar value, same contract as char_code/2;
            // an astral code appends its surrogate pair.
            if (!Utf16Text.IsScalarValue(head.AsInt))
                throw new PrologRuntimeException(
                    "representation_error", "character_code");
            Utf16Text.AppendCodePoint(sb, (int)head.AsInt);
            cursor = ListCursor.Resolve(engine, rTail);
        }
        if (cursor.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        if (cursor.Tag != Tag.Atom || cursor.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("type_error", "list", engine, listStart);
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

        // Both arguments instantiated but not both atoms (a number/string in
        // concat mode): ISO §8.16.2 raises type_error(atom). SWI instead coerces
        // any atomic to text. Honour that ONLY when the caller lives in an SWI
        // module — and only here, on the path that was going to raise anyway, so
        // the strict case pays nothing.
        if (SwiLenient.IsBoundAtomic(aCell) && SwiLenient.IsBoundAtomic(bCell)
            && SwiLenient.CallerIsSwi(engine)
            && SwiLenient.TryAtomicText(engine, aCell, out string at)
            && SwiLenient.TryAtomicText(engine, bCell, out string bt))
        {
            int id = AtomTable.Intern(at + bt, permanent: false).Id;
            return engine.UnifyRegisterWithCell(2, Cell.Atom(id));
        }

        Cell cCell = Resolve(engine, engine.GetRegister(2));
        // §8.16.2.3: a BOUND non-atom in A or B is type_error(atom, X)
        // with that argument as culprit, before the C-driven split.
        foreach (Cell abc in stackalloc Cell[] { aCell, bCell })
            if (abc.Tag is not (Tag.Ref or Tag.AttVar) && abc.Tag != Tag.Atom
                && !SwiLenient.IsBoundAtomic(abc))
                throw new PrologRuntimeException("type_error", "atom", engine, abc);
        if (cCell.Tag != Tag.Atom)
        {
            // ISO §8.16.2: if C is var, neither direction can drive
            // synthesis unless BOTH A and B are atoms. If C is var and
            // either A or B is var, raise instantiation_error. If C
            // is bound to a non-atom, raise type_error(atom, C).
            if (cCell.Tag is Tag.Ref or Tag.AttVar
                && (aCell.Tag is Tag.Ref or Tag.AttVar || bCell.Tag is Tag.Ref or Tag.AttVar))
            {
                Shumway.Core.Diagnostics.ChoicePointTrace.DumpAtSite(
                    engine, "atom_concat/3 instantiation_error");
                throw new PrologRuntimeException("instantiation_error");
            }
            Shumway.Core.Diagnostics.ChoicePointTrace.DumpAtSite(
                engine, "atom_concat/3 type_error(atom)");
            // The culprit is the first argument that is bound but not an
            // atom — A, then B, then C.
            Cell culprit =
                aCell.Tag is not (Tag.Ref or Tag.AttVar) && aCell.Tag != Tag.Atom ? aCell
                : bCell.Tag is not (Tag.Ref or Tag.AttVar) && bCell.Tag != Tag.Atom ? bCell
                : cCell;
            throw new PrologRuntimeException("type_error", "atom", engine, culprit);
        }

        string cName = AtomTable.GetById(cCell.AsAtomId)?.Name ?? "";

        // Mode-directed split: a bound A or B pins the split point, so the
        // single candidate is checked directly — no choice point. Without
        // this the cursor walked every split unifying against the bound
        // argument and left a dead CP after the match — phantom
        // nondeterminism in callers that never cut (Logtalk mime_types/os).
        if (aCell.Tag is not (Tag.Ref or Tag.AttVar))
        {
            if (aCell.Tag != Tag.Atom) return false;
            string aName = AtomTable.GetById(aCell.AsAtomId)?.Name ?? "";
            if (!cName.StartsWith(aName, StringComparison.Ordinal)) return false;
            int bId = AtomTable.Intern(cName.Substring(aName.Length), permanent: false).Id;
            return engine.UnifyRegisterWithCell(1, Cell.Atom(bId));
        }
        if (bCell.Tag is not (Tag.Ref or Tag.AttVar))
        {
            if (bCell.Tag != Tag.Atom) return false;
            string bName = AtomTable.GetById(bCell.AsAtomId)?.Name ?? "";
            if (!cName.EndsWith(bName, StringComparison.Ordinal)) return false;
            int aId = AtomTable.Intern(
                cName.Substring(0, cName.Length - bName.Length), permanent: false).Id;
            return engine.UnifyRegisterWithCell(0, Cell.Atom(aId));
        }

        // Same mode analysis one step further: A and B ALIASED (the same
        // unbound variable, `atom_concat(X, X, aaaa)`) pins the split just as
        // firmly as a bound argument does — only the even split can match, and
        // only when the two halves are equal. One candidate, checked directly,
        // no choice point.
        if (aCell.Tag is Tag.Ref or Tag.AttVar && bCell.Tag is Tag.Ref or Tag.AttVar
            && aCell.AsHeapIndex == bCell.AsHeapIndex)
        {
            if ((cName.Length & 1) != 0) return false;
            int half = cName.Length / 2;
            if (string.CompareOrdinal(cName, 0, cName, half, half) != 0) return false;
            int halfId = AtomTable.Intern(cName[..half], permanent: false).Id;
            return engine.UnifyRegisterWithCell(0, Cell.Atom(halfId));
        }

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
            // A split point inside a surrogate pair is not a CHARACTER
            // boundary: cutting there manufactured two lone-surrogate atoms
            // ('😀x' used to enumerate 4 splits instead of 3).
            while (splitIdx > 0 && splitIdx < _cName.Length
                   && char.IsLowSurrogate(_cName[splitIdx])
                   && char.IsHighSurrogate(_cName[splitIdx - 1]))
                splitIdx++;
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
        if (c.Tag is not (Tag.Ref or Tag.AttVar)) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}
