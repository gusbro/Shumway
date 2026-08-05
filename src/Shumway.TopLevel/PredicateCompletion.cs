using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.TopLevel;

/// <summary>
/// Name completion over everything the engine can currently call: the
/// process-wide builtin registry plus, per module, the clauses it holds, its
/// public functors, its dynamic functors, and any precompiled-bundle
/// predicates. Feeds a REPL's Tab key and an editor's autocomplete alike.
/// </summary>
public static class PredicateCompletion
{
    /// <summary>How many names to return at most. A prefix of one or two
    /// characters matches a great many predicates; past a screenful the list
    /// stops being a completion and starts being a wall.</summary>
    public const int Cap = 200;

    /// <summary>Returns the sorted, deduplicated predicate names that start with
    /// <paramref name="prefix"/>, capped at <see cref="Cap"/>.</summary>
    public static IReadOnlyList<string> Matching(PrologEngine? engine, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<string>();
        void Offer(string name)
        {
            if (results.Count >= Cap) return;
            if (string.IsNullOrEmpty(name)) return;
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return;
            if (seen.Add(name)) results.Add(name);
        }

        foreach (var b in Shumway.Builtins.BuiltinsRegistry.AllEntries())
            Offer(b.Name);

        if (engine is not null)
        {
            foreach (var (_, manifest) in engine.Modules)
            {
                foreach (var clause in manifest.Clauses)
                {
                    string? n = ClauseHeadName(clause);
                    if (n is not null) Offer(n);
                }
                foreach (int fid in manifest.PublicFunctors)
                    Offer(NameOfFunctor(fid));
                foreach (int fid in manifest.DynamicFunctors)
                    Offer(NameOfFunctor(fid));
            }
            foreach (var (fid, _) in engine.PrecompiledStaticPredicates)
                Offer(NameOfFunctor(fid));
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static string? ClauseHeadName(Clause clause)
    {
        Term head = clause.Term;
        if ((clause.Kind == ClauseKind.Rule || clause.Kind == ClauseKind.DcgRule)
            && head is CompoundTerm wrap && wrap.Args.Length == 2)
            head = wrap.Args[0];
        return head switch
        {
            AtomTerm a => a.Name,
            CompoundTerm c => c.Functor,
            _ => null,
        };
    }

    private static string NameOfFunctor(int fid)
    {
        var (atomId, _) = FunctorTable.Lookup(fid);
        return AtomTable.GetById(atomId)?.Name ?? "";
    }
}
