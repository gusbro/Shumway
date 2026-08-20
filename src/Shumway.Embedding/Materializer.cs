using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Allocates a static <see cref="Term"/> tree onto a runtime
/// <see cref="Activation"/>'s heap. The mirror image of
/// <see cref="TermReader.Materialize(Activation, int)"/>: heap → Term goes one way,
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
    public static Cell MaterializeAsCell(Activation engine, Term term)
    {
        // the per-call variable-map Dictionary is pooled on the
        // engine (clear-on-use). Variable identity must NOT leak across
        // calls — the clear before each use preserves the "share within a
        // single call" contract exactly as a fresh dictionary did. The
        // depth counter guards re-entrancy: only the outermost call uses
        // the pooled instance; a nested one allocates fresh.
        engine.MaterializeDepth++;
        try
        {
            Dictionary<string, int> varMap;
            if (engine.MaterializeDepth == 1)
            {
                varMap = engine.MaterializeScratchMap ??= new Dictionary<string, int>();
                varMap.Clear();
            }
            else
            {
                varMap = new Dictionary<string, int>();
            }
            return MaterializeAsCell(engine, term, varMap);
        }
        finally
        {
            engine.MaterializeDepth--;
        }
    }

    /// <summary>ADR-035 — like <see cref="MaterializeAsCell(Activation, Term)"/>, but
    /// SHARING variables with pre-existing heap cells: <paramref name="sharedVars"/> maps a
    /// variable name to the heap address it must resolve to. The debugger's bind-into-frame
    /// commit seeds it with the suspended frame's own variables, so a term built here and
    /// unified against the frame creates REAL sharing rather than fresh copies. Variables
    /// the term introduces beyond the seeded ones allocate fresh cells and are added to the
    /// map — repeated calls against the same map preserve identity across a whole
    /// solution's worth of values.</summary>
    internal static Cell MaterializeAsCellSharing(
        Activation engine, Term term, Dictionary<string, int> sharedVars)
        => MaterializeAsCell(engine, term, sharedVars);

    private static Cell MaterializeAsCell(
        Activation engine, Term term, Dictionary<string, int> varMap)
    {
        switch (term)
        {
            case AtomTerm a:
                // read the node's lazily-cached id (seeded by
                // TermReader when the AST came off a heap) instead of a
                // by-name re-intern per visit. The intern, when it does
                // happen, is TRANSIENT — the old `permanent: true` pinned
                // every atom transiting a meta-builtin to the eternal tier,
                // defeating the three-tier atom GC (ADR-003). Safety: the
                // Transient tier holds a strong in-table reference; the only
                // collection mechanism is AtomTable.Sweep, whose contract
                // requires the caller's mark phase to include atom ids
                // reachable from engine heaps — exactly where this id goes.
                return Cell.Atom(a.ResolveAtomId());

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

            case RationalTerm rt:
                return engine.MakeRational(Rational.Create(rt.Num, rt.Den));

            case FloatTerm f:
                return Cell.Ref(engine.MakeFloat(f.Value));

            case StringTerm s:
                return Cell.Ref(engine.MakePstr(s.Content, TextKind.Codes));

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
            // domain object — preserves it instead of leaving a bare
            // `'$foreign'(N)` compound. Same engine, so the id is still valid.
            case CompoundTerm c when c.Functor == "$foreign" && c.Args.Length == 1
                                     && c.Args[0] is IntTerm fid:
                return Cell.Foreign((int)fid.Value);

            case CompoundTerm c:
            {
                // cached functor id (transient intern on first
                // use) — see the AtomTerm case above for the tier-safety
                // argument; the two string-keyed table probes per compound
                // visit collapse to a field read once the node is warm.
                int functorId = c.ResolveFunctorId();

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
