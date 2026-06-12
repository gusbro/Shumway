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
    public static Term Materialize(Engine engine, int heapIdx)
    {
        // chunk 432: the cycle-detection HashSet used to be allocated per
        // call — one per findall solution. It is now (a) created lazily,
        // only when the walk first meets a compound / list (atoms, ints
        // and unbound vars never touch it), and (b) pooled on the engine,
        // clear-on-use. The depth counter guards re-entrancy: only the
        // outermost walk may use the pooled instance; a nested walk
        // allocates fresh.
        engine.TermWalkDepth++;
        try
        {
            HashSet<int>? active = null;
            return Materialize(engine, heapIdx, ref active);
        }
        finally
        {
            engine.TermWalkDepth--;
        }
    }

    /// <summary>Lazily provides the cycle-detection set for the current walk
    /// (chunk 432) — pooled at depth 1, freshly allocated when nested.</summary>
    private static HashSet<int> EnsureActive(Engine engine, ref HashSet<int>? active)
    {
        if (active is null)
        {
            if (engine.TermWalkDepth == 1)
            {
                active = engine.TermWalkScratchSet ??= new HashSet<int>();
                active.Clear();
            }
            else
            {
                active = new HashSet<int>();
            }
        }
        return active;
    }

    private static Term Materialize(Engine engine, int heapIdx, ref HashSet<int>? active)
    {
        int derefAddr = engine.Deref(heapIdx);
        Cell cell = engine.GetHeap(derefAddr);

        return cell.Tag switch
        {
            // An attributed variable is materialized as a plain unbound
            // variable — its attributes are engine-side metadata, not
            // part of the term's AST shape. (chunk 77)
            Tag.Ref or Tag.AttVar => new VarTerm($"_G{derefAddr}"),
            // chunk 431: seed the AST node's lazily-cached atom id — we
            // have it in hand here, so downstream consumers (Materializer,
            // retract's DefiniteMismatch, assert's head-functor extraction)
            // skip the by-name re-intern entirely.
            Tag.Atom => new AtomTerm(NameOfAtom(cell.AsAtomId), cell.AsAtomId),
            Tag.Int => new IntTerm(cell.AsInt),
            Tag.BigInt => new BigIntTerm(engine.AsBigInt(cell)),
            Tag.Float => new FloatTerm(Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex))),
            Tag.Str => MaterializeCompoundAt(engine, cell.AsHeapIndex, ref active),
            // A bare FUNCTOR cell reached as a value is the head of the compound
            // that starts at this address (functor + args). ADR-017 normally
            // wraps it in a STR ref, but some paths (e.g. a reserved/inline
            // build whose ref was elided) land directly on the functor; treat it
            // as the compound rooted here.
            Tag.Functor => MaterializeCompoundAt(engine, derefAddr, ref active),
            Tag.Lis => MaterializeLis(engine, cell, ref active),
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

    private static Term MaterializeCompoundAt(Engine engine, int functorIdx, ref HashSet<int>? activeRef)
    {
        // chunk 432: first compound met on this walk creates (or rents) the
        // cycle set.
        HashSet<int> active = EnsureActive(engine, ref activeRef);
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
                args[i] = Materialize(engine, functorIdx + 1 + i, ref activeRef);
            // chunk 431: seed the node's cached functor id from the cell.
            return new CompoundTerm(name, args, functorCell.AsFunctorId);
        }
        finally
        {
            active.Remove(functorIdx);
        }
    }

    private static Term MaterializeLis(Engine engine, Cell lisCell, ref HashSet<int>? activeRef)
    {
        // chunk 432: lazy/pooled cycle set — see EnsureActive.
        HashSet<int> active = EnsureActive(engine, ref activeRef);
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
        // chunk 431: intern './2' once per spine walk and seed every cons
        // node's cached functor id, so a later Materializer/assert pass over
        // the list never re-interns the cons functor by name per element.
        int consFid = FunctorTable.Intern(AtomTable.ConsFunctorId, 2);
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
                        cycleResult = new CompoundTerm(".", new[] { heads[i], cycleResult }, consFid);
                    return cycleResult;
                }
                spineAddrs.Add(headIdx);
                heads.Add(Materialize(engine, headIdx, ref activeRef));
                tailIdx = headIdx + 1;
                Cell tailCell = engine.GetHeap(engine.Deref(tailIdx));
                if (tailCell.Tag != Tag.Lis) break;
                cur = tailCell;
            }
            Term result = Materialize(engine, tailIdx, ref activeRef);
            for (int i = heads.Count - 1; i >= 0; i--)
                result = new CompoundTerm(".", new[] { heads[i], result }, consFid);
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
