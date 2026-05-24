using System.Collections.Generic;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Walks a runtime heap cell and rebuilds a static <see cref="Term"/> tree from
/// it. Used by <see cref="Solution"/> to expose the final state of query
/// variables back to .NET code in the same shape the parser produced.
///
/// <para>The materializer follows REFs to their targets, recursively expands
/// STR and LIS cells, and recognises PSTR headers. Truly unbound variables
/// surface as <see cref="VarTerm"/>s with synthetic names keyed off their heap
/// index (e.g. <c>_G42</c>), which both keeps the binding inspectable and
/// distinguishes different unbound variables from one another.</para>
///
/// <para>Chunk 148: cyclic structures (built by plain <c>=/2</c>'s
/// occurs-check-off binding, e.g. <c>X = f(X)</c>) used to overflow the C#
/// stack here. Cycle detection now substitutes a synthetic
/// <c>VarTerm("_C{addr}")</c> at the cycle-back point — preserving identity
/// across the multiple back-edges in a single materialise pass without
/// recursing into an infinite tree. The Phase-8 chunk-111 list-spine
/// iterative walk handled the long-list case; this handles the
/// genuinely-cyclic case.</para>
/// </summary>
public static class TermReader
{
    /// <summary>Materializes the term reachable from <paramref name="heapIdx"/>
    /// into an AST <see cref="Term"/>. Follows REF chains and recurses into
    /// compound / list structures; cycles are broken via a synthetic
    /// <c>VarTerm("_C{addr}")</c> placeholder.</summary>
    public static Term Materialize(Engine engine, int heapIdx) =>
        Materialize(engine, heapIdx, new HashSet<int>());

    private static Term Materialize(Engine engine, int heapIdx, HashSet<int> active)
    {
        int derefAddr = engine.Deref(heapIdx);
        Cell cell = engine.GetHeap(derefAddr);

        return cell.Tag switch
        {
            // An attributed variable is materialized as a plain unbound
            // variable — its attributes are engine-side metadata, not
            // part of the term's AST shape. (chunk 77)
            Tag.Ref or Tag.AttVar => new VarTerm($"_G{derefAddr}"),
            Tag.Atom => new AtomTerm(NameOfAtom(cell.AsAtomId)),
            Tag.Int => new IntTerm(cell.AsInt),
            Tag.BigInt => new BigIntTerm(engine.AsBigInt(cell)),
            Tag.Float => new FloatTerm(Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex))),
            Tag.Str => MaterializeStr(engine, cell, active),
            Tag.Lis => MaterializeLis(engine, cell, active),
            Tag.Pstr => new StringTerm(engine.AsPstrString(derefAddr)),
            // Foreign cells round-trip as `'$foreign'(N)` compounds — the
            // payload's identity (the engine's foreign table entry) is
            // exposed as the integer id. User code rarely inspects this
            // directly; it's mostly visible when a stream handle ends up
            // in a query's bindings.
            Tag.Foreign => new CompoundTerm("$foreign",
                new Term[] { new IntTerm(cell.AsForeignId) }),
            _ => throw new NotSupportedException(
                $"TermReader.Materialize does not yet handle the {cell.Tag} tag."),
        };
    }

    private static Term MaterializeStr(Engine engine, Cell strCell, HashSet<int> active)
    {
        int functorIdx = strCell.AsHeapIndex;
        // Chunk 148: if we're already inside materialising this exact
        // STR (same functor address), return the cycle marker.
        if (!active.Add(functorIdx))
            return new VarTerm($"_C{functorIdx}");
        try
        {
            Cell functorCell = engine.GetHeap(functorIdx);
            var (atomId, arity) = FunctorTable.Lookup(functorCell.AsFunctorId);
            string name = NameOfAtom(atomId);

            var args = new Term[arity];
            for (int i = 0; i < arity; i++)
                args[i] = Materialize(engine, functorIdx + 1 + i, active);
            return new CompoundTerm(name, args);
        }
        finally
        {
            active.Remove(functorIdx);
        }
    }

    private static Term MaterializeLis(Engine engine, Cell lisCell, HashSet<int> active)
    {
        // Walk the list spine iteratively: a recursive descent down the
        // tail would use one C# stack frame per element and overflow on a
        // long list. Only the (shallow) elements recurse.
        //
        // Chunk 148: a cons cell can cycle too (X = [a | X]). Each cons
        // address joins the active set as we walk; re-entry yields the
        // cycle marker. The spine addresses are tracked so a try/finally
        // removes them all once the walk is done.
        var heads = new List<Term>();
        var spineAddrs = new List<int>();
        Cell cur = lisCell;
        int tailIdx;
        try
        {
            while (true)
            {
                int headIdx = cur.AsHeapIndex;
                if (!active.Add(headIdx))
                {
                    // Cycle detected mid-spine: terminate the partial
                    // list with the cycle marker rather than recursing.
                    Term cycleResult = new VarTerm($"_C{headIdx}");
                    for (int i = heads.Count - 1; i >= 0; i--)
                        cycleResult = new CompoundTerm(".", new[] { heads[i], cycleResult });
                    return cycleResult;
                }
                spineAddrs.Add(headIdx);
                heads.Add(Materialize(engine, headIdx, active));
                tailIdx = headIdx + 1;
                Cell tailCell = engine.GetHeap(engine.Deref(tailIdx));
                if (tailCell.Tag != Tag.Lis) break;
                cur = tailCell;
            }
            Term result = Materialize(engine, tailIdx, active);
            for (int i = heads.Count - 1; i >= 0; i--)
                result = new CompoundTerm(".", new[] { heads[i], result });
            return result;
        }
        finally
        {
            foreach (int a in spineAddrs) active.Remove(a);
        }
    }

    private static string NameOfAtom(int id)
    {
        var atom = AtomTable.GetById(id);
        if (atom is null)
            throw new InvalidOperationException(
                $"Atom id {id} is not registered in the table.");
        return atom.Name;
    }
}
