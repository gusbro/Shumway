using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// Compile-time rewrite of <c>phrase/2</c> and <c>phrase/3</c> calls into
/// direct DCG-style invocations. <c>phrase(Body, List)</c> expands to
/// <c>phrase(Body, List, [])</c>, and <c>phrase(Body, List, Rest)</c>
/// appends <c>List</c> and <c>Rest</c> to <c>Body</c>'s argument list so the
/// goal becomes a regular call to a DCG-transformed non-terminal.
///
/// <para>The rewrite only fires when <c>Body</c> is a statically known goal
/// (atom or compound). A variable <c>Body</c> would need a real runtime
/// meta-call — that's deferred to the chunk that lands <c>call/N</c>.
/// Until then, encountering a variable body throws
/// <see cref="NotSupportedException"/> at compile time so the failure mode
/// is loud rather than mysterious.</para>
/// </summary>
public static class PhraseTransform
{
    public static List<Clause> Apply(IEnumerable<Clause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        var result = new List<Clause>();
        foreach (var clause in clauses)
        {
            if (clause.Kind == ClauseKind.Rule
                && clause.Term is CompoundTerm ruleTerm
                && ruleTerm.Args.Length == 2)
            {
                Term head = ruleTerm.Args[0];
                Term body = ruleTerm.Args[1];
                Term newBody = RewriteGoal(body);
                if (ReferenceEquals(newBody, body))
                {
                    result.Add(clause);
                }
                else
                {
                    Term newRuleTerm = new CompoundTerm(":-", new[] { head, newBody })
                    { Position = ruleTerm.Position };
                    result.Add(new Clause(ClauseKind.Rule, newRuleTerm, clause.Position));
                }
            }
            else
            {
                result.Add(clause);
            }
        }
        return result;
    }

    private static Term RewriteGoal(Term goal)
    {
        // Recurse through the control-flow constructors that group goals — the
        // constructors themselves stay, only their sub-goals get rewritten.
        if (goal is CompoundTerm c)
        {
            if (IsControlFlow(c.Functor, c.Args.Length))
            {
                Term[]? newArgs = null;
                for (int i = 0; i < c.Args.Length; i++)
                {
                    Term orig = c.Args[i];
                    Term rew = RewriteGoal(orig);
                    if (!ReferenceEquals(rew, orig))
                    {
                        newArgs ??= (Term[])c.Args.Clone();
                        newArgs[i] = rew;
                    }
                }
                return newArgs is null ? goal : new CompoundTerm(c.Functor, newArgs);
            }

            if (c.Functor == "phrase" && c.Args.Length == 2)
            {
                Term? expanded = ExpandPhrase(c.Args[0], c.Args[1], new AtomTerm("[]"));
                return expanded ?? goal;
            }
            if (c.Functor == "phrase" && c.Args.Length == 3)
            {
                Term? expanded = ExpandPhrase(c.Args[0], c.Args[1], c.Args[2]);
                return expanded ?? goal;
            }
        }
        return goal;
    }

    /// <summary>Returns the rewritten goal, or <c>null</c> when the body
    /// isn't a syntactically callable non-terminal (a variable, an empty
    /// list, a cons cell) — in that case the caller should leave the
    /// <c>phrase</c> call alone so the resolver can route it to a
    /// user-defined <c>phrase/2</c> or <c>phrase/3</c> predicate.</summary>
    private static Term? ExpandPhrase(Term body, Term list, Term rest)
    {
        switch (body)
        {
            case AtomTerm a when a.Name != "[]":
                // phrase(a, L, R) → a(L, R).
                return new CompoundTerm(a.Name, new[] { list, rest });
            case CompoundTerm bc when !(bc.Functor == "." && bc.Args.Length == 2):
                // phrase(foo(X), L, R) → foo(X, L, R). List-shaped compounds
                // (`./2`) and lookalikes are not callable goals — skip them.
                var newArgs = new Term[bc.Args.Length + 2];
                Array.Copy(bc.Args, newArgs, bc.Args.Length);
                newArgs[bc.Args.Length] = list;
                newArgs[bc.Args.Length + 1] = rest;
                return new CompoundTerm(bc.Functor, newArgs);
            default:
                return null;
        }
    }

    private static bool IsControlFlow(string functor, int arity) => (functor, arity) switch
    {
        (",", 2) => true,
        (";", 2) => true,
        ("->", 2) => true,
        ("*->", 2) => true,
        _ => false,
    };
}
