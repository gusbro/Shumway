using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Allocates a static <see cref="Term"/> tree onto a runtime
/// <see cref="Engine"/>'s heap. The mirror image of
/// <see cref="TermReader.Materialize(Engine, int)"/>: heap → Term goes one way,
/// this goes the other.
///
/// <para>Used by meta-builtins (<c>findall/3</c>) that run a goal in a fresh
/// sub-engine, collect the resulting bindings as AST <see cref="Term"/>s, and
/// then need to plant the collected list back into the parent engine's heap
/// before unifying it with the caller's output variable.</para>
///
/// <para><see cref="VarTerm"/>s share their heap cell within a single call:
/// two <c>VarTerm("X")</c>s in the same term tree resolve to the same fresh
/// unbound. This is what preserves variable identity when the same term is
/// re-materialised in a different engine — e.g. when <c>findall/3</c> re-runs
/// the same goal under sub-engine bindings.</para>
/// </summary>
public static class Materializer
{
    /// <summary>Allocates <paramref name="term"/> on the engine's heap and
    /// returns a <see cref="Cell"/> suitable for placing into a register or
    /// arg slot. Atomic terms return value cells (<c>Atom</c>, <c>Int</c>);
    /// floats, strings, lists, compounds, and variables return <c>Ref</c>
    /// or <c>Lis</c> cells pointing at freshly-allocated heap regions.</summary>
    public static Cell MaterializeAsCell(Engine engine, Term term)
        => MaterializeAsCell(engine, term, new Dictionary<string, int>());

    private static Cell MaterializeAsCell(
        Engine engine, Term term, Dictionary<string, int> varMap)
    {
        switch (term)
        {
            case AtomTerm a:
                return Cell.Atom(AtomTable.Intern(a.Name, permanent: true).Id);

            case IntTerm n:
                // 60-bit inline range: anything wider hops to the BigInteger
                // side table so the cell still fits in 8 bytes.
                if (n.Value < Cell.MinInt60 || n.Value > Cell.MaxInt60)
                    return engine.MakeBigInt(new System.Numerics.BigInteger(n.Value));
                return Cell.Int(n.Value);

            case BigIntTerm bn:
                // BigIntegers that happen to fit in the inline range collapse
                // to Tag.Int — keeps the hot path tag-uniform for small values
                // produced by arithmetic that *could* have stayed inline.
                if (bn.Value >= Cell.MinInt60 && bn.Value <= Cell.MaxInt60)
                    return Cell.Int((long)bn.Value);
                return engine.MakeBigInt(bn.Value);

            case FloatTerm f:
                return Cell.Ref(engine.MakeFloat(f.Value));

            case StringTerm s:
                return Cell.Ref(engine.MakePstr(s.Content));

            case VarTerm v:
                if (v.Name == "_" || !varMap.TryGetValue(v.Name, out int existingIdx))
                {
                    int freshIdx = engine.AllocateHeapUnbound();
                    if (v.Name != "_") varMap[v.Name] = freshIdx;
                    return Cell.Ref(freshIdx);
                }
                return Cell.Ref(existingIdx);

            case CompoundTerm c when c.Functor == "." && c.Args.Length == 2:
            {
                // Walk the list spine iteratively: a recursive descent down
                // the tail would use one C# stack frame per element and
                // overflow on a long list. Only the (shallow) elements and
                // the final tail recurse.
                var heads = new List<Term>();
                Term cursor = term;
                while (cursor is CompoundTerm cc
                       && cc.Functor == "." && cc.Args.Length == 2)
                {
                    heads.Add(cc.Args[0]);
                    cursor = cc.Args[1];
                }
                int count = heads.Count;
                // Reserve every pair cell up front so the indices are stable
                // while the elements (which may extend the heap) materialise.
                int firstPair = engine.AllocateHeap(2 * count);
                Cell tailCell = MaterializeAsCell(engine, cursor, varMap);
                var headCells = new Cell[count];
                for (int i = 0; i < count; i++)
                    headCells[i] = MaterializeAsCell(engine, heads[i], varMap);
                for (int i = 0; i < count; i++)
                {
                    int pair = firstPair + 2 * i;
                    engine.SetHeap(pair, headCells[i]);
                    engine.SetHeap(pair + 1, i + 1 < count
                        ? Cell.Lis(firstPair + 2 * (i + 1))
                        : tailCell);
                }
                return Cell.Lis(firstPair);
            }

            // A foreign object round-trips through the AST as `'$foreign'(N)`
            // (TermReader renders Tag.Foreign that way). Re-materialise it back
            // to the same Foreign cell so copy_term/3 over an attributed
            // variable whose attribute holds a foreign value — e.g. a clpfd
            // domain object (Phase 28) — preserves it instead of leaving a bare
            // `'$foreign'(N)` compound. Same engine, so the id is still valid.
            case CompoundTerm c when c.Functor == "$foreign" && c.Args.Length == 1
                                     && c.Args[0] is IntTerm fid:
                return Cell.Foreign((int)fid.Value);

            case CompoundTerm c:
            {
                int atomId = AtomTable.Intern(c.Functor, permanent: true).Id;
                int functorId = FunctorTable.Intern(atomId, c.Args.Length);

                // Reserve the STR + Functor + arg cells up front; recurse for
                // each arg afterwards so children can freely extend the heap.
                int strBase = engine.AllocateHeap(2 + c.Args.Length);
                engine.SetHeap(strBase, Cell.Str(strBase + 1));
                engine.SetHeap(strBase + 1, Cell.Functor(functorId));
                for (int i = 0; i < c.Args.Length; i++)
                {
                    Cell argCell = MaterializeAsCell(engine, c.Args[i], varMap);
                    engine.SetHeap(strBase + 2 + i, argCell);
                }
                return Cell.Ref(strBase);
            }

            default:
                throw new NotSupportedException(
                    $"Materializer doesn't yet handle {term.GetType().Name}.");
        }
    }
}
