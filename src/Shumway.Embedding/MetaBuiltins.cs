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
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "findall/3 requires the engine to be hosted by a PrologEngine. "
                + "Engine.Host is "
                + (engine.Host?.GetType().Name ?? "null") + ".");

        Term template = MaterializeRegister(engine, 0);
        Term goal = MaterializeRegister(engine, 1);

        var sub = host.CreateSubEngine();
        var results = new List<Term>();
        foreach (Solution sol in sub.QueryAll(goal))
            results.Add(Substitute(template, sol.Bindings));

        // Build the result list bottom-up: [] for the empty case, otherwise
        // a chain of ./2 cons cells.
        Term listTerm = new AtomTerm("[]");
        for (int i = results.Count - 1; i >= 0; i--)
            listTerm = new CompoundTerm(".", new[] { results[i], listTerm });

        Cell listCell = Materializer.MaterializeAsCell(engine, listTerm);
        return engine.UnifyRegisterWithCell(2, listCell);
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
