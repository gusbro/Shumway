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
/// </summary>
public static class TermReader
{
    /// <summary>Materializes the term reachable from <paramref name="heapIdx"/>
    /// into an AST <see cref="Term"/>. Follows REF chains and recurses into
    /// compound / list structures.</summary>
    public static Term Materialize(Engine engine, int heapIdx)
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
            Tag.Str => MaterializeStr(engine, cell),
            Tag.Lis => MaterializeLis(engine, cell),
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

    private static Term MaterializeStr(Engine engine, Cell strCell)
    {
        int functorIdx = strCell.AsHeapIndex;
        Cell functorCell = engine.GetHeap(functorIdx);
        var (atomId, arity) = FunctorTable.Lookup(functorCell.AsFunctorId);
        string name = NameOfAtom(atomId);

        var args = new Term[arity];
        for (int i = 0; i < arity; i++)
            args[i] = Materialize(engine, functorIdx + 1 + i);
        return new CompoundTerm(name, args);
    }

    private static Term MaterializeLis(Engine engine, Cell lisCell)
    {
        // Walk the list spine iteratively: a recursive descent down the
        // tail would use one C# stack frame per element and overflow on a
        // long list. Only the (shallow) elements recurse.
        var heads = new List<Term>();
        Cell cur = lisCell;
        int tailIdx;
        while (true)
        {
            int headIdx = cur.AsHeapIndex;
            heads.Add(Materialize(engine, headIdx));
            tailIdx = headIdx + 1;
            Cell tailCell = engine.GetHeap(engine.Deref(tailIdx));
            if (tailCell.Tag != Tag.Lis) break;
            cur = tailCell;
        }
        Term result = Materialize(engine, tailIdx);
        for (int i = heads.Count - 1; i >= 0; i--)
            result = new CompoundTerm(".", new[] { heads[i], result });
        return result;
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
