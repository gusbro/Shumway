using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Helper builtins backing the prelude's multi-solution predicates
/// (<c>length/2</c>, <c>sub_atom/5</c>, …). Each one materialises the
/// "all matches" set as a Prolog list so the prelude's wrapper can
/// then iterate via <c>member/2</c>, picking up backtracking via the
/// standard WAM choice-point machinery.
///
/// <para>Same pattern as <c>clause/2</c> and <c>current_predicate/1</c>:
/// keep the C# side purely enumerative and let Prolog do the iteration.
/// Costs more list allocation than a stateful builtin would, but each
/// piece is simple to reason about and the multi-solution semantics are
/// trivially correct.</para>
/// </summary>
public static class MultiSolutionHelpers
{
    /// <summary><c>'$get_cut_barrier'(K)</c>: unifies K with the
    /// clause's cut barrier (<see cref="Activation.B0"/>, the choice-point level the
    /// caller's Call/Execute established — what a neck cut commits to). Inserted
    /// by <c>MetaTransform</c> as the FIRST body goal of a clause that has a
    /// <c>!</c> inside a <c>;</c>/<c>-&gt;</c> branch: the branch lowers to a
    /// synthesised helper, and the captured barrier threads through to it so the
    /// branch cut commits the HOST clause (ISO 7.8.8 cut transparency in
    /// then/else and disjunction branches) instead of just the helper. The cut
    /// itself runs as <c>'$call'(!, K)</c> — the barrier-cut path.</summary>
    public static bool GetCutBarrier(Activation engine)
        => engine.UnifyRegisterWithCell(0, Cell.Int(engine.B0));

    /// <summary><c>'$soft_cut'(+Barrier)</c> — ADR-037. Neutralises the choice
    /// point at the choice-point pointer <c>Barrier</c> (captured by
    /// <c>'$choice_level'</c> at a soft-cut helper's clause-1 entry — i.e. the
    /// helper's <c>Else</c>-alternative CP), committing away <c>Else</c> once the
    /// condition has succeeded while leaving the condition's own choice points
    /// intact. This is the builtin form <c>MetaTransform</c> emits for a
    /// <c>( Cond *-&gt; Then ; Else )</c> that is NOT inline-eligible (a cut in a
    /// branch, nested control in a part); the inline-eligible case commits with the
    /// <c>soft_cut</c> opcode directly. See <see cref="Activation.SoftCut"/>.</summary>
    public static bool SoftCut1(Activation engine)
    {
        Cell c = engine.GetRegister(0);
        if (c.Tag == Tag.Ref)
            c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
        if (c.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "integer");
        engine.SoftCut((int)c.AsInt);
        return true;
    }

    /// <summary><c>'$list_length'(List, N)</c> — given a proper list,
    /// bind <c>N</c> to its length. Fails for partial / improper
    /// lists. Used by the prelude's <c>length/2</c> when the list is
    /// ground.</summary>
    public static bool ListLength(Activation engine)
    {
        Cell cur = ListCursor.Resolve(engine, engine.GetRegister(0));
        // Tortoise-and-hare over the SPINE: a cyclic list has no finite
        // length, and without the check this while loop is an uninterruptible
        // C# spin — `L = [a|L], length(L, 0)` hung the engine past every safe
        // point (Neumerkel's length case 27).
        Cell hare = cur;
        bool hareRuns = true;
        int count = 0;
        while (ListCursor.TryUncons(engine, cur, out _, out Cell tail))
        {
            count++;
            cur = ListCursor.Resolve(engine, tail);
            // Compare ONLY after the hare advanced BOTH steps: a hare that
            // ran off the end proves the spine finite, and comparing anyway
            // reports "cycle" when the tortoise catches it resting on nil —
            // which failed length/2 on every short proper list.
            if (hareRuns && ListCursor.TryUncons(engine, hare, out _, out Cell h1)
                && ListCursor.TryUncons(engine, ListCursor.Resolve(engine, h1), out _, out Cell h2))
            {
                hare = ListCursor.Resolve(engine, h2);
                if (cur.Equals(hare)) return false;   // the spine loops
            }
            else hareRuns = false;
        }
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
            return false;
        return engine.UnifyRegisterWithCell(1, Cell.Int(count));
    }

    /// <summary><c>'$cyclic_spine'(L)</c> — true when L's list SPINE loops
    /// (tortoise and hare; heads are not entered). The prelude's length/2
    /// fails such lists outright: walking them, in C# or in Prolog, never
    /// ends.</summary>
    public static bool CyclicSpine(Activation engine)
    {
        Cell cur = ListCursor.Resolve(engine, engine.GetRegister(0));
        Cell hare = cur;
        while (ListCursor.TryUncons(engine, cur, out _, out Cell tail))
        {
            cur = ListCursor.Resolve(engine, tail);
            // Same rule as ListLength: only a hare that advanced BOTH steps
            // may be compared — one that ran off the end proves finiteness.
            if (!ListCursor.TryUncons(engine, hare, out _, out Cell h1)
                || !ListCursor.TryUncons(engine, ListCursor.Resolve(engine, h1), out _, out Cell h2))
                return false;
            hare = ListCursor.Resolve(engine, h2);
            if (cur.Equals(hare)) return true;
        }
        return false;
    }

    /// <summary><c>'$make_var_list'(N, List)</c> — builds a fresh list
    /// of <c>N</c> unbound variables and unifies it with <c>List</c>.
    /// Used by the prelude's <c>length/2</c> when <c>N</c> is ground.
    /// </summary>
    public static bool MakeVarList(Activation engine)
    {
        Cell nCell = Resolve(engine, engine.GetRegister(0));
        // ISO precedence — instantiation_error before type_error.
        if (nCell.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        // A BIGINT count is a well-typed integer no list can ever have: the
        // heap cannot hold 2N+1 cells of it. Fail, like the length such a
        // list will never reach (Neumerkel's length case 31); the bare (int)
        // cast this replaces silently TRUNCATED a large long instead.
        if (nCell.Tag != Tag.Int)
        {
            if (nCell.Tag == Tag.BigInt) return false;
            throw new PrologRuntimeException("type_error", "integer");
        }
        long n = nCell.AsInt;
        if (n < 0) return false;
        if (n > int.MaxValue / 2 - 1) return false;   // 2N+1 cells cannot exist
        int listIdx = BuildFreshVarList(engine, (int)n);
        return engine.UnifyRegisterWithHeapAt(1, listIdx);
    }

    /// <summary><c>'$sub_atom_decompositions'(Atom, List)</c> — returns
    /// a list of every <c>[Before, Length, After, SubAtom]</c> 4-tuple
    /// such that <c>Atom = Prefix + SubAtom + Suffix</c> with the
    /// arithmetic constraints satisfied. The prelude's
    /// <c>sub_atom/5</c> calls <c>member/2</c> on the result so that
    /// every decomposition becomes a choice point.</summary>
    public static bool SubAtomDecompositions(Activation engine)
    {
        Cell atomCell = Resolve(engine, engine.GetRegister(0));
        // ISO precedence — instantiation_error before type_error.
        if (atomCell.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (atomCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        Atom? atomObj = AtomTable.GetById(atomCell.AsAtomId);
        string name = atomObj?.Name ?? "";
        // Offsets and lengths are CODE POINTS. For the (rare) non-BMP atom,
        // precompute each code point's starting unit index once so every
        // decomposition still slices in O(1).
        int[]? cpBounds = atomObj is { IsAllBmp: false }
            ? Utf16Text.CpBounds(name) : null;
        int len = cpBounds is null ? name.Length : cpBounds.Length - 1;

        // Build the list bottom-up so we can encode it without back-patching.
        // The decompositions: for each before ∈ [0..len], for each length ∈ [0..len-before],
        // emit [before, length, after, sub]. Total = (len+1)(len+2)/2 entries.
        int total = (len + 1) * (len + 2) / 2;

        // Preallocate the spine + element-list spines. Each decomposition
        // is a 4-element list, plus a 1-cell '.' header pointing to it,
        // plus the spine.
        // For simplicity, build via heap allocations one decomposition at
        // a time. Each decomposition: 4 cons cells + nil + reference, plus
        // a PSTR for the substring + an outer cons cell. We let MakePstr
        // + AllocateHeap handle the bookkeeping.
        //
        // Strategy: build into a List<int> of heap indices, then chain.
        // Last-cdr-first construction lets us emit a singly-linked Prolog
        // list cleanly.
        int tailIdx = engine.AllocateHeap(1);
        engine.SetHeap(tailIdx, Cell.Atom(AtomTable.EmptyListId));
        Cell tailCell = Cell.Atom(AtomTable.EmptyListId);

        // Enumerate in reverse order so the resulting list is in the
        // "natural" before-then-length-increasing order at the front.
        for (int before = len; before >= 0; before--)
        {
            for (int length = len - before; length >= 0; length--)
            {
                int after = len - before - length;
                string sub = cpBounds is null
                    ? name.Substring(before, length)
                    : name.Substring(cpBounds[before],
                        cpBounds[before + length] - cpBounds[before]);
                int subAtomId = AtomTable.Intern(sub, permanent: false).Id;

                // Build [Before, Length, After, Sub] as a cons chain.
                int spineEnd = engine.AllocateHeap(1);
                engine.SetHeap(spineEnd, Cell.Atom(AtomTable.EmptyListId));

                int subCons = engine.AllocateHeap(2);
                engine.SetHeap(subCons, Cell.Atom(subAtomId));
                engine.SetHeap(subCons + 1, Cell.Atom(AtomTable.EmptyListId));

                int afterCons = engine.AllocateHeap(2);
                engine.SetHeap(afterCons, Cell.Int(after));
                engine.SetHeap(afterCons + 1, Cell.Lis(subCons));

                int lengthCons = engine.AllocateHeap(2);
                engine.SetHeap(lengthCons, Cell.Int(length));
                engine.SetHeap(lengthCons + 1, Cell.Lis(afterCons));

                int beforeCons = engine.AllocateHeap(2);
                engine.SetHeap(beforeCons, Cell.Int(before));
                engine.SetHeap(beforeCons + 1, Cell.Lis(lengthCons));

                // Outer cons: [Decomp | Rest].
                int outer = engine.AllocateHeap(2);
                engine.SetHeap(outer, Cell.Lis(beforeCons));
                engine.SetHeap(outer + 1, tailCell);
                tailCell = Cell.Lis(outer);
            }
        }

        return engine.UnifyRegisterWithCell(1, tailCell);
    }

    /// <summary><c>'$sub_atom_enum'(Atom, Before, Length, After, Sub)</c> — the
    /// LAZY sub_atom/5 enumerator. Yields each <c>(Before, Length, After, Sub)</c>
    /// decomposition one at a time on backtracking via the shared
    /// <see cref="IndexEnumCursor"/>, instead of materialising all
    /// <c>(len+1)(len+2)/2</c> decompositions onto the heap up front (O(1) extra
    /// memory rather than O(n²)). Enumeration order is before-major, length
    /// ascending — identical to the eager list — so a bound argument filters via
    /// the per-decomposition unification.
    ///
    /// <para>TRAP: a cursor builtin like this MUST be seen as backtrackable by
    /// the Tier-1 IL emit, or it skips the resume-marker /
    /// <c>BuiltinReturnPc</c> setup and the cursor resumes at PC 0 (silent
    /// solution loss). <see cref="BacktrackableDetector"/> derives that flag
    /// from this method's IL (it calls <c>IndexEnumCursor.Start</c>), so it is
    /// correct under both tiers automatically.</para></summary>
    public static bool SubAtomEnum(Activation engine)
    {
        Cell atomCell = Resolve(engine, engine.GetRegister(0));
        if (atomCell.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        string name;
        if (atomCell.Tag == Tag.Atom) name = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
        else if (!SwiLenient.TryCoerce(engine, atomCell, out name))
            throw new PrologRuntimeException("type_error", "atom", engine, atomCell);
        // Offsets/lengths are CODE POINTS. BMP atoms (the per-atom shape,
        // computed at intern) keep the exact unit-based code below; a
        // non-BMP one gets a boundary table so slices stay O(1).
        int[]? cpb = null;
        {
            TextShape shape = atomCell.Tag == Tag.Atom
                ? AtomTable.GetById(atomCell.AsAtomId)?.Shape ?? TextShape.Bmp
                : Utf16Text.Classify(name);
            if (shape != TextShape.Bmp) cpb = Utf16Text.CpBounds(name);
        }
        int len = cpb is null ? name.Length : cpb.Length - 1;

        // Mode analysis: pre-filter the candidate set by every bound argument
        // so (in the common modes) a candidate is enumerated ONLY if it will
        // unify. The cursor drops its choice point exactly at the last real
        // solution, so a bound-mode call is deterministic like GNU/SWI —
        // without this, a sub_atom during a long-lived caller leaves a dead CP
        // that breaks callers' determinism (Logtalk lgtunit `deterministic/1`).
        Cell bCell = Resolve(engine, engine.GetRegister(1));
        Cell lCell = Resolve(engine, engine.GetRegister(2));
        Cell aCell = Resolve(engine, engine.GetRegister(3));
        Cell sCell = Resolve(engine, engine.GetRegister(4));
        // §8.16.5.3: each bound position argument must be an integer
        // (type_error) and non-negative (domain_error); a value merely
        // larger than the atom just has no solution.
        CheckIndexArg(engine, bCell);
        CheckIndexArg(engine, lCell);
        CheckIndexArg(engine, aCell);
        if (sCell.Tag is not (Tag.Ref or Tag.AttVar) && sCell.Tag != Tag.Atom
            && !SwiLenient.TryCoerce(engine, sCell, out _))
            throw new PrologRuntimeException("type_error", "atom", engine, sCell);
        int bFix = BoundIndex(bCell, len), lFix = BoundIndex(lCell, len), aFix = BoundIndex(aCell, len);
        if (bFix == -2 || lFix == -2 || aFix == -2) return false;

        int returnPc = engine.BuiltinReturnPc;
        bool TryUnify(Activation e, int b, int l)
        {
            if (!e.UnifyRegisterWithCell(1, Cell.Int(b))) return false;
            if (!e.UnifyRegisterWithCell(2, Cell.Int(l))) return false;
            if (!e.UnifyRegisterWithCell(3, Cell.Int(len - b - l))) return false;
            int u0 = cpb is null ? b : cpb[b];
            int u1 = cpb is null ? b + l : cpb[b + l];
            int subAtomId = AtomTable.Intern(
                name.Substring(u0, u1 - u0), permanent: false).Id;
            return e.UnifyRegisterWithCell(4, Cell.Atom(subAtomId));
        }

        // Sub bound to an atom: candidates are its occurrence positions.
        if (sCell.Tag == Tag.Atom)
        {
            string sub = AtomTable.GetById(sCell.AsAtomId)?.Name ?? "";
            int lsUnits = sub.Length;
            int ls = cpb is null ? lsUnits : Utf16Text.CodePointLength(sub);
            if (lFix >= 0 && lFix != ls) return false;
            // An occurrence must start AND end on a code-point boundary —
            // matching sub's units at cp position b spans exactly ls code
            // points iff the end lands on cpb[b + ls].
            bool OccursAt(int b)
            {
                int u0 = cpb is null ? b : cpb[b];
                if (u0 + lsUnits > name.Length) return false;
                if (cpb is not null && (b + ls > len || cpb[b + ls] != u0 + lsUnits))
                    return false;
                return string.CompareOrdinal(name, u0, sub, 0, lsUnits) == 0;
            }
            var occ = new List<int>();
            if (bFix >= 0)
            {
                if (bFix + ls <= len && OccursAt(bFix)) occ.Add(bFix);
            }
            else if (aFix >= 0)
            {
                int b = len - aFix - ls;
                if (b >= 0 && OccursAt(b)) occ.Add(b);
            }
            else
            {
                for (int b = 0; b + ls <= len; b++)
                    if (OccursAt(b)) occ.Add(b);
            }
            return IndexEnumCursor.Start(engine, occ.Count, arity: 5, returnPc,
                (e, i) => TryUnify(e, occ[i], ls));
        }

        // Two of Before/Length/After bound → the third is determined.
        int boundCount = (bFix >= 0 ? 1 : 0) + (lFix >= 0 ? 1 : 0) + (aFix >= 0 ? 1 : 0);
        if (boundCount >= 2)
        {
            int b1, l1;
            if (bFix >= 0 && lFix >= 0)
            {
                b1 = bFix; l1 = lFix;
                if (aFix >= 0 && bFix + lFix + aFix != len) return false;
            }
            else if (bFix >= 0) { b1 = bFix; l1 = len - bFix - aFix; }
            else { l1 = lFix; b1 = len - lFix - aFix; }
            if (b1 < 0 || l1 < 0 || b1 + l1 > len) return false;
            return IndexEnumCursor.Start(engine, 1, arity: 5, returnPc, (e, _) => TryUnify(e, b1, l1));
        }
        if (bFix >= 0)
            return IndexEnumCursor.Start(engine, len - bFix + 1, arity: 5, returnPc,
                (e, i) => TryUnify(e, bFix, i));
        if (lFix >= 0)
            return IndexEnumCursor.Start(engine, len - lFix + 1, arity: 5, returnPc,
                (e, i) => TryUnify(e, i, lFix));
        if (aFix >= 0)
            return IndexEnumCursor.Start(engine, len - aFix + 1, arity: 5, returnPc,
                (e, i) => TryUnify(e, i, len - aFix - i));

        // All free — full triangle. Sequential (before, length) state shared by
        // every tryAt call: the cursor invokes tryAt with i = 0,1,2,… strictly
        // in order, so advancing the pair keeps it in lock-step with i — O(1)
        // per decomposition, no heap list.
        int total = (len + 1) * (len + 2) / 2;   // Σ_{before=0..len} (len-before+1)
        int before = 0, length = 0;
        bool TryAt(Activation e, int i)
        {
            int b = before, l = length;
            if (length < len - before) length++;
            else { before++; length = 0; }
            return TryUnify(e, b, l);
        }
        return IndexEnumCursor.Start(engine, total, arity: 5, returnPc, TryAt);
    }

    /// <summary>Reads an argument of sub_atom's Before/Length/After as a
    /// candidate filter: the in-range integer value, −1 for an unbound
    /// variable, −2 when no decomposition can satisfy it (non-integer or out
    /// of 0..len — the caller fails, matching the unify-per-candidate
    /// behaviour it replaces).</summary>
    /// <summary>sub_atom/5's Before/Length/After arguments: a bound
    /// non-integer is type_error(integer, C); a negative integer is
    /// domain_error(not_less_than_zero, C).</summary>
    private static void CheckIndexArg(Activation engine, Cell c)
    {
        if (c.Tag is Tag.Ref or Tag.AttVar) return;
        if (c.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "integer", engine, c);
        if (c.AsInt < 0)
            throw new PrologRuntimeException(
                "domain_error", "not_less_than_zero", engine, c);
    }

    private static int BoundIndex(Cell c, int len)
    {
        if (c.Tag == Tag.Ref) return -1;
        if (c.Tag != Tag.Int) return -2;
        long v = c.AsInt;
        return (v < 0 || v > len) ? -2 : (int)v;
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

    private static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}
