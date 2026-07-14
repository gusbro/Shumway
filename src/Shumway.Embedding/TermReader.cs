using System.Collections.Generic;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Walks a runtime heap cell and rebuilds a static <see cref="Term"/> tree from
/// it. Used by <see cref="Solution"/> to expose the final state of query
/// variables back to .NET code in the same shape the parser produced.
///
/// <para>The materializer follows REFs to their targets, expands STR and LIS
/// cells, and recognises PSTR headers. Truly unbound variables surface as
/// <see cref="VarTerm"/>s with synthetic names keyed off their heap index
/// (e.g. <c>_G42</c>), which both keeps the binding inspectable and
/// distinguishes different unbound variables from one another.</para>
///
/// <para>Chunk 148: cyclic structures (built by plain <c>=/2</c>'s
/// occurs-check-off binding, e.g. <c>X = f(X)</c>) used to overflow the C#
/// stack here. Cycle detection substitutes a synthetic
/// <c>VarTerm("_C{addr}")</c> at the cycle-back point — preserving identity
/// across the multiple back-edges in a single materialise pass without
/// recursing into an infinite tree.</para>
///
/// <para>Phase 33 I13: the whole walk is now iterative. Chunk 111 made the
/// list <em>spine</em> iterative, but a deeply-nested <em>non-list</em> term —
/// <c>s(s(…))</c> or a long left-associative <c>a+b+c+…</c> — still recursed
/// one C# frame per compound level and overflowed the host uncatchably at
/// materialise time (before the clause even reached the compiler, which the
/// I12 walks had already hardened). The walk below is an explicit-stack
/// post-order tree traversal: children are expanded before their parent is
/// assembled, so C# stack depth is O(1) regardless of term shape or depth.
/// Cycle detection is unchanged in meaning — the active set holds exactly the
/// addresses on the current root→node path, added when a compound is expanded
/// and removed when it is assembled (matching the old recursive try/finally
/// scoping, so a shared-but-acyclic sub-term — a DAG — still materialises
/// twice rather than being mistaken for a cycle).</para>
/// </summary>
public static class TermReader
{
    // --- per-thread scratch pool -------------------------------------------
    //
    // The walk needs a work stack (pending frames), a result stack (built
    // child Terms awaiting their parent) and the cycle-detection path set.
    // These are transient per-walk scratch — NOT engine state — so pooling
    // them per-thread keeps the engine thread-agile (a walk is synchronous and
    // single-threaded; the buffers simply follow the executing thread) while
    // avoiding a fresh allocation per findall solution (the chunk-432 intent,
    // now covering the work/result stacks too). A re-entrant walk on the same
    // thread — should one ever occur — takes the busy flag and allocates fresh,
    // so the pooled buffers are never aliased.
    [ThreadStatic] private static List<Frame>? _tlWork;
    [ThreadStatic] private static List<Term>? _tlResults;
    [ThreadStatic] private static HashSet<int>? _tlActive;
    [ThreadStatic] private static bool _tlBusy;

    /// <summary>A pending unit of work in the iterative walk.</summary>
    private readonly struct Frame
    {
        // 0 = Expand a heap index into a Term.
        // 1 = Assemble a compound (STR/FUNCTOR) from Arity results.
        // 2 = Assemble a cons cell from 2 results (head, tail).
        public readonly int Kind;
        // Expand: the heap index to materialize.
        // Assemble: the functor/cons address to drop from the active path set.
        public readonly int A;
        public readonly int Arity;       // Assemble-compound: argument count.
        public readonly string? Name;    // Assemble-compound: functor name.
        public readonly int FunctorId;   // Assemble: cached functor id (chunk 431).

        private Frame(int kind, int a, int arity, string? name, int functorId)
        {
            Kind = kind; A = a; Arity = arity; Name = name; FunctorId = functorId;
        }

        public static Frame Expand(int heapIdx) => new(0, heapIdx, 0, null, 0);
        public static Frame BuildStr(string name, int arity, int functorId, int exitAddr)
            => new(1, exitAddr, arity, name, functorId);
        public static Frame BuildCons(int consFid, int exitAddr)
            => new(2, exitAddr, 0, null, consFid);
    }

    /// <summary>Materializes the term reachable from <paramref name="heapIdx"/>
    /// into an AST <see cref="Term"/>. Follows REF chains and expands compound /
    /// list structures iteratively; cycles are broken via a synthetic
    /// <c>VarTerm("_C{addr}")</c> placeholder.</summary>
    public static Term Materialize(Activation engine, int heapIdx)
    {
        bool pooled = !_tlBusy;
        List<Frame> work;
        List<Term> results;
        HashSet<int> active;
        if (pooled)
        {
            _tlBusy = true;
            work = _tlWork ??= new List<Frame>(64); work.Clear();
            results = _tlResults ??= new List<Term>(64); results.Clear();
            active = _tlActive ??= new HashSet<int>(); active.Clear();
        }
        else
        {
            work = new List<Frame>(16);
            results = new List<Term>(16);
            active = new HashSet<int>();
        }

        try
        {
            int consFid = -1;   // cons functor id, interned lazily on first list
            work.Add(Frame.Expand(heapIdx));
            while (work.Count > 0)
            {
                Frame f = work[work.Count - 1];
                work.RemoveAt(work.Count - 1);
                switch (f.Kind)
                {
                    case 0:   // Expand
                        Expand(engine, f.A, work, results, active, ref consFid);
                        break;

                    case 1:   // Assemble compound from Arity results
                    {
                        var args = new Term[f.Arity];
                        // Args were expanded left-to-right, so arg 0 is deepest
                        // in the result stack and arg (Arity-1) is on top.
                        for (int i = f.Arity - 1; i >= 0; i--)
                        {
                            args[i] = results[results.Count - 1];
                            results.RemoveAt(results.Count - 1);
                        }
                        active.Remove(f.A);
                        results.Add(new CompoundTerm(f.Name!, args, f.FunctorId));
                        break;
                    }

                    default:  // case 2: Assemble cons (head, tail)
                    {
                        Term tail = results[results.Count - 1];
                        results.RemoveAt(results.Count - 1);
                        Term head = results[results.Count - 1];
                        results.RemoveAt(results.Count - 1);
                        active.Remove(f.A);
                        results.Add(new CompoundTerm(".", new[] { head, tail }, f.FunctorId));
                        break;
                    }
                }
            }
            return results[0];
        }
        finally
        {
            if (pooled) _tlBusy = false;
        }
    }

    /// <summary>Dereferences <paramref name="heapIdx"/> and either pushes its
    /// finished leaf <see cref="Term"/> onto <paramref name="results"/>, or —
    /// for a compound / list — records an assemble frame plus an expand frame
    /// per child (in reverse, so the leftmost child is expanded first and its
    /// result lands deepest). All C# stack growth is thereby replaced by growth
    /// of the explicit <paramref name="work"/> stack.</summary>
    private static void Expand(Activation engine, int heapIdx,
        List<Frame> work, List<Term> results, HashSet<int> active, ref int consFid)
    {
        int derefAddr = engine.Deref(heapIdx);
        Cell cell = engine.GetHeap(derefAddr);

        switch (cell.Tag)
        {
            // An attributed variable materializes as a plain unbound variable —
            // its attributes are engine-side metadata, not part of the AST
            // shape. (chunk 77)
            case Tag.Ref:
            case Tag.AttVar:
                results.Add(new VarTerm($"_G{derefAddr}"));
                break;

            // chunk 431: seed the node's lazily-cached atom id — we have it in
            // hand here, so downstream consumers (Materializer, retract's
            // DefiniteMismatch, assert's head-functor extraction) skip the
            // by-name re-intern entirely.
            case Tag.Atom:
                results.Add(new AtomTerm(NameOfAtom(cell.AsAtomId), cell.AsAtomId));
                break;

            case Tag.Int:
                results.Add(new IntTerm(cell.AsInt));
                break;

            case Tag.BigInt:
                results.Add(new BigIntTerm(engine.AsBigInt(cell)));
                break;

            case Tag.Float:
                results.Add(new FloatTerm(
                    Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex))));
                break;

            case Tag.Pstr:
                results.Add(new StringTerm(engine.AsPstrString(derefAddr)));
                break;

            // A STRING cell is a string too — the whole string, held in the engine's table,
            // rather than the PSTR's heap-resident run of characters. The engine can make one
            // (Activation.MakeString) and compare it (==/2 handles the tag), and the
            // materializer could not read it back: a cell the engine can build and cannot show
            // is a NotSupportedException waiting for the first program that builds one.
            case Tag.String:
                results.Add(new StringTerm(engine.AsString(cell)));
                break;

            // Foreign cells round-trip as `'$foreign'(N)` compounds — the
            // payload's identity (the engine's foreign table entry) is exposed
            // as the integer id. Mostly visible when a stream handle ends up in
            // a query's bindings.
            case Tag.Foreign:
                results.Add(new CompoundTerm("$foreign",
                    new Term[] { new IntTerm(cell.AsForeignId) }));
                break;

            // A STR ref points at the functor cell; a bare FUNCTOR cell reached
            // as a value (ADR-017 inline builds whose ref was elided) is the
            // head of the compound rooted right here.
            case Tag.Str:
            case Tag.Functor:
            {
                int functorIdx = cell.Tag == Tag.Str ? cell.AsHeapIndex : derefAddr;
                // Chunk 148: if this exact compound address is already on the
                // active path, we've cycled — emit the marker instead of
                // recursing forever.
                if (!active.Add(functorIdx))
                {
                    results.Add(new VarTerm($"_C{functorIdx}"));
                    break;
                }
                Cell functorCell = engine.GetHeap(functorIdx);
                var (atomId, arity) = FunctorTable.Lookup(functorCell.AsFunctorId);
                string name = NameOfAtom(atomId);
                work.Add(Frame.BuildStr(name, arity, functorCell.AsFunctorId, functorIdx));
                // Push args in reverse so arg 0 is popped/expanded first.
                for (int i = arity - 1; i >= 0; i--)
                    work.Add(Frame.Expand(functorIdx + 1 + i));
                break;
            }

            // A cons cell is compound with implicit functor "./2". Each cons
            // address joins the active set as it is expanded and is removed when
            // its BuildCons frame runs — so a cyclic list (X = [a | X]) yields
            // the cycle marker for its tail rather than looping. A long list no
            // longer needs the chunk-111 bespoke spine loop: the tail is just
            // another Expand frame on the (heap-allocated) work stack.
            case Tag.Lis:
            {
                int h = cell.AsHeapIndex;
                if (!active.Add(h))
                {
                    results.Add(new VarTerm($"_C{h}"));
                    break;
                }
                if (consFid < 0)
                    consFid = FunctorTable.Intern(AtomTable.ConsFunctorId, 2);
                work.Add(Frame.BuildCons(consFid, h));
                work.Add(Frame.Expand(h + 1));   // tail
                work.Add(Frame.Expand(h));       // head
                break;
            }

            default:
                throw new NotSupportedException(
                    $"TermReader.Materialize does not yet handle the {cell.Tag} tag.");
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
