using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Helper builtins backing the prelude's multi-solution predicates
/// (<c>length/2</c>, <c>sub_atom/5</c>, …). Each one materialises the
/// "all matches" set as a Prolog list so the prelude's wrapper can
/// then iterate via <c>member/2</c>, picking up backtracking via the
/// standard WAM choice-point machinery.
///
/// <para>The pattern matches what we did in chunk 40 for
/// <c>clause/2</c> and <c>current_predicate/1</c>: keep the C# side
/// purely enumerative and let Prolog do the iteration. Costs more
/// list allocation than a stateful builtin would, but each piece is
/// simple to reason about and the multi-solution semantics are
/// trivially correct.</para>
/// </summary>
public static class MultiSolutionHelpers
{
    /// <summary><c>'$list_length'(List, N)</c> — given a proper list,
    /// bind <c>N</c> to its length. Fails for partial / improper
    /// lists. Used by the prelude's <c>length/2</c> when the list is
    /// ground.</summary>
    public static bool ListLength(Engine engine)
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
    public static bool MakeVarList(Engine engine)
    {
        Cell nCell = Resolve(engine, engine.GetRegister(0));
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
    public static bool SubAtomDecompositions(Engine engine)
    {
        Cell atomCell = Resolve(engine, engine.GetRegister(0));
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

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        int addr = engine.Deref(c.AsHeapIndex);
        return engine.GetHeap(addr);
    }
}
