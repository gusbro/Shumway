using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    // term_expansion / goal_expansion C# hooks — the "C# shim" path for
    // supporting foreign engines' libraries. A hook inspects a term (a clause or
    // directive, for term_expansion; a body goal, for goal_expansion) and returns
    // its replacement, or null to decline. First hook that returns non-null wins;
    // a term_expansion may return several terms (one clause → many) or an empty
    // list (drop the term). These run BEFORE the Prolog-level term_expansion/2 //
    // goal_expansion/2 predicates.
    private List<System.Func<Term, IReadOnlyList<Term>?>>? _termExpansions;
    private List<System.Func<Term, Term?>>? _goalExpansions;

    /// <summary>Registers a C# <c>term_expansion</c> hook (ADR-038 shim path): it
    /// receives each term read during a consult (a clause or a <c>:- Directive</c>)
    /// and returns the term(s) that replace it, or <c>null</c> to decline. An empty
    /// list drops the term. Hooks run in registration order; the first non-null
    /// result wins. Runs before any Prolog <c>term_expansion/2</c>.</summary>
    public void RegisterTermExpansion(System.Func<Term, IReadOnlyList<Term>?> hook)
    {
        System.ArgumentNullException.ThrowIfNull(hook);
        (_termExpansions ??= new()).Add(hook);
    }

    /// <summary>Registers a C# <c>goal_expansion</c> hook (ADR-038 shim path): it
    /// receives each body goal and returns its replacement goal, or <c>null</c> to
    /// decline. Hooks run in registration order; the first non-null result wins.
    /// Runs before any Prolog <c>goal_expansion/2</c>.</summary>
    public void RegisterGoalExpansion(System.Func<Term, Term?> hook)
    {
        System.ArgumentNullException.ThrowIfNull(hook);
        (_goalExpansions ??= new()).Add(hook);
    }

    internal bool HasTermExpansions => _termExpansions is { Count: > 0 };
    internal bool HasGoalExpansions => _goalExpansions is { Count: > 0 };

    // Applies the C# term_expansion hooks to one raw term. Returns null when no
    // hook fired (the caller keeps the original term unchanged); otherwise the
    // replacement list (possibly empty to drop the term).
    internal IReadOnlyList<Term>? ApplyCsTermExpansion(Term term)
    {
        if (_termExpansions is null) return null;
        foreach (var hook in _termExpansions)
        {
            var r = hook(term);
            if (r is not null) return r;
        }
        return null;
    }

    // Applies the C# goal_expansion hooks to one goal. Returns null when no hook
    // fired (keep the goal unchanged).
    internal Term? ApplyCsGoalExpansion(Term goal)
    {
        if (_goalExpansions is null) return null;
        foreach (var hook in _goalExpansions)
        {
            var r = hook(goal);
            if (r is not null) return r;
        }
        return null;
    }
}
