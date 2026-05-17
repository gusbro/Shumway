using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Meta-predicate builtins (predicates that call other predicates as goals).
/// They live in the Embedding layer rather than <c>Shumway.Builtins</c>
/// because their implementations spawn sub-<see cref="PrologEngine"/>s, which
/// Builtins can't reference without creating a circular dependency. Registered
/// from <see cref="PrologEngine"/>'s constructor through
/// <see cref="EnsureRegistered"/>.
/// </summary>
public static class MetaBuiltins
{
    private static int _initialized;

    public static void EnsureRegistered()
    {
        if (System.Threading.Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        BuiltinsRegistry.Register("findall", 3, Findall);
        BuiltinsRegistry.Register("bagof",   3, Bagof);
        BuiltinsRegistry.Register("setof",   3, Setof);
        BuiltinsRegistry.Register("copy_term", 2, CopyTerm);
    }

    /// <summary><c>copy_term(Term, Copy)</c> — unifies <c>Copy</c> with a
    /// fresh-variable copy of <c>Term</c>. Bound subterms are preserved by
    /// value; unbound variables become brand-new unbound variables in the
    /// copy, with sharing preserved (multiple occurrences of the same input
    /// var map to one new var).
    ///
    /// <para>Implementation: <see cref="TermReader.Materialize"/> walks the
    /// input and turns truly-unbound REFs into <see cref="VarTerm"/>s with
    /// synthetic <c>_GN</c> names keyed by heap address; the immediately
    /// following <see cref="Materializer.MaterializeAsCell"/> call uses a
    /// fresh var-name → heap-index map, so each <c>_GN</c> resolves to a new
    /// unbound — and shared occurrences in the AST keep sharing.</para></summary>
    public static bool CopyTerm(Engine engine)
    {
        Term original = MaterializeRegister(engine, 0);
        Cell copyCell = Materializer.MaterializeAsCell(engine, original);
        return engine.UnifyRegisterWithCell(1, copyCell);
    }

    /// <summary><c>findall(Template, Goal, List)</c> — runs <c>Goal</c> in a
    /// fresh peer engine, captures the value of <c>Template</c> at every
    /// solution, and unifies <c>List</c> with the resulting list (empty when
    /// no solution exists).
    ///
    /// <para>The sub-engine approach sidesteps choice-point stack manipulation
    /// on the calling engine. The trade-off is that <c>Template</c> and
    /// <c>Goal</c> have to round-trip through the AST <see cref="Term"/>
    /// representation — variable identity is preserved via the synthetic
    /// <c>_GN</c> names <see cref="TermReader"/> assigns, which is why the
    /// substitution step at the end works.</para></summary>
    public static bool Findall(Engine engine)
    {
        var results = CollectSolutions(engine, stripExistentials: false);
        return BindList(engine, results);
    }

    /// <summary><c>bagof(Template, Goal, Bag)</c> — like <c>findall/3</c> but
    /// <em>fails</em> when <c>Goal</c> has no solutions instead of returning
    /// <c>[]</c>. ISO bagof also splits the solution stream by free-variable
    /// groupings; Phase 1 doesn't do that yet, so this implementation is
    /// effectively "findall + fail-on-empty". The <c>Var^Goal</c> existential
    /// quantifier is recognised and stripped (every var is implicitly
    /// existential without grouping, so it's a no-op).</summary>
    public static bool Bagof(Engine engine)
    {
        var results = CollectSolutions(engine, stripExistentials: true);
        if (results.Count == 0) return false;
        return BindList(engine, results);
    }

    /// <summary><c>setof(Template, Goal, Set)</c> — like <c>bagof/3</c> but
    /// the result is sorted in standard order and duplicate terms are
    /// removed. Like bagof, fails when no solutions exist. The sort runs
    /// on the AST level via <see cref="TermStandardOrder.Compare"/> so the
    /// outcome only depends on solution content, not on which heap
    /// addresses the sub-engine happened to allocate.</summary>
    public static bool Setof(Engine engine)
    {
        var results = CollectSolutions(engine, stripExistentials: true);
        if (results.Count == 0) return false;

        results.Sort(TermStandardOrder.Compare);

        // Dedup adjacent equals in place.
        int write = 1;
        for (int read = 1; read < results.Count; read++)
        {
            if (TermStandardOrder.Compare(results[read], results[write - 1]) != 0)
                results[write++] = results[read];
        }
        if (write < results.Count) results.RemoveRange(write, results.Count - write);

        return BindList(engine, results);
    }

    /// <summary>Shared workhorse for findall/bagof/setof: reads Template and
    /// Goal, optionally strips <c>^/2</c> existential wrappers from the
    /// goal, runs it in a peer engine, and projects each solution's
    /// bindings through Template. The result list is built by the
    /// per-builtin tail logic (which decides what to do on empty).</summary>
    private static List<Term> CollectSolutions(Engine engine, bool stripExistentials)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "Collection meta-builtins require the engine to be hosted by "
                + "a PrologEngine. Engine.Host is "
                + (engine.Host?.GetType().Name ?? "null") + ".");

        Term template = MaterializeRegister(engine, 0);
        Term goal = MaterializeRegister(engine, 1);
        if (stripExistentials) goal = StripExistentials(goal);

        var sub = host.CreateSubEngine();
        var results = new List<Term>();
        foreach (Solution sol in sub.QueryAll(goal))
            results.Add(Substitute(template, sol.Bindings));
        return results;
    }

    /// <summary>Builds a Prolog list from the collected results and unifies
    /// it with the caller's third argument.</summary>
    private static bool BindList(Engine engine, IReadOnlyList<Term> results)
    {
        Term listTerm = new AtomTerm("[]");
        for (int i = results.Count - 1; i >= 0; i--)
            listTerm = new CompoundTerm(".", new[] { results[i], listTerm });

        Cell listCell = Materializer.MaterializeAsCell(engine, listTerm);
        return engine.UnifyRegisterWithCell(2, listCell);
    }

    /// <summary>Strips any leading <c>^/2</c> existential wrappers off a goal.
    /// <c>X^Y^Goal</c> reduces to <c>Goal</c>; the stripped variables become
    /// ordinary free variables of the inner goal. Without solution grouping
    /// this is purely a no-op syntactically — every variable is already
    /// existential — but stripping makes ISO-compliant user code work.</summary>
    private static Term StripExistentials(Term goal)
    {
        while (goal is CompoundTerm c && c.Functor == "^" && c.Args.Length == 2)
            goal = c.Args[1];
        return goal;
    }

    /// <summary>Reads the term currently bound in <c>X[<paramref name="regIdx"/>]</c>
    /// as an AST <see cref="Term"/>. Wraps the register's cell on the heap
    /// briefly so the existing <see cref="TermReader.Materialize"/> can do its
    /// REF-chasing work uniformly — for atomic registers this costs one
    /// throwaway heap cell.</summary>
    private static Term MaterializeRegister(Engine engine, int regIdx)
    {
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, engine.GetRegister(regIdx));
        return TermReader.Materialize(engine, slot);
    }

    /// <summary>Walks <paramref name="term"/> and replaces every
    /// <see cref="VarTerm"/> whose name appears in
    /// <paramref name="bindings"/> with its bound value. Used by
    /// <see cref="Findall"/> to project the sub-engine's solution bindings
    /// through the user-supplied template.</summary>
    private static Term Substitute(Term term, IReadOnlyDictionary<string, Term> bindings)
    {
        switch (term)
        {
            case VarTerm v when bindings.TryGetValue(v.Name, out Term? bound):
                // Recurse: a binding might itself contain variables that we
                // need to further substitute (the sub-engine reports
                // dereferenced terms, but a residual unbound var has its
                // _GN name preserved and shouldn't be re-walked endlessly).
                return Substitute(bound, bindings);
            case CompoundTerm c:
                var newArgs = new Term[c.Args.Length];
                for (int i = 0; i < c.Args.Length; i++)
                    newArgs[i] = Substitute(c.Args[i], bindings);
                return new CompoundTerm(c.Functor, newArgs);
            default:
                return term;
        }
    }
}
