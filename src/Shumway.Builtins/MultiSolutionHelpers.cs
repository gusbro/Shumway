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

    /// <summary><c>'$list_length'(List, N)</c> — given a proper list,
    /// bind <c>N</c> to its length. Fails for partial / improper
    /// lists. Used by the prelude's <c>length/2</c> when the list is
    /// ground.</summary>
    public static bool ListLength(Activation engine)
    {
        Cell cur = Resolve(engine, engine.GetRegister(0));
        int count = 0;
        while (cur.Tag == Tag.Lis)
        {
            count++;
            cur = Resolve(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        }
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
            return false;
        return engine.UnifyRegisterWithCell(1, Cell.Int(count));
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
        if (nCell.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "integer");
        long n = nCell.AsInt;
        if (n < 0) return false;
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
        string name = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
        int len = name.Length;

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
                string sub = name.Substring(before, length);
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
        if (atomCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        string name = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
        int len = name.Length;
        int total = (len + 1) * (len + 2) / 2;   // Σ_{before=0..len} (len-before+1)

        // Sequential (before, length) state shared by every tryAt call: the
        // IndexEnumCursor driver invokes tryAt with i = 0,1,2,… strictly in
        // order, so advancing the pair by one per call keeps it in lock-step
        // with i — O(1) per decomposition, no heap list.
        int before = 0, length = 0;
        bool TryAt(Activation e, int i)
        {
            int b = before, l = length;
            if (length < len - before) length++;
            else { before++; length = 0; }
            int after = len - b - l;
            if (!e.UnifyRegisterWithCell(1, Cell.Int(b))) return false;
            if (!e.UnifyRegisterWithCell(2, Cell.Int(l))) return false;
            if (!e.UnifyRegisterWithCell(3, Cell.Int(after))) return false;
            int subAtomId = AtomTable.Intern(name.Substring(b, l), permanent: false).Id;
            return e.UnifyRegisterWithCell(4, Cell.Atom(subAtomId));
        }

        return IndexEnumCursor.Start(engine, total, arity: 5, engine.BuiltinReturnPc, TryAt);
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
