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
                if (n.Value < Cell.MinInt60 || n.Value > Cell.MaxInt60)
                    throw new NotSupportedException(
                        $"Integer {n.Value} doesn't fit in a 60-bit inline cell. "
                        + "BigInt materialisation lands later.");
                return Cell.Int(n.Value);

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
                int pair = engine.AllocateHeap(2);
                // Materialise head / tail *after* reserving the pair cells so
                // the pair stays at a stable index even if the children
                // allocate more heap themselves.
                Cell head = MaterializeAsCell(engine, c.Args[0], varMap);
                Cell tail = MaterializeAsCell(engine, c.Args[1], varMap);
                engine.SetHeap(pair, head);
                engine.SetHeap(pair + 1, tail);
                return Cell.Lis(pair);
            }

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
