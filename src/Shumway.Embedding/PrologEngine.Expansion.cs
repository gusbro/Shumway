using Shumway.Compiler.Ast;
using Shumway.Core;

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

    private static readonly int TermExpansionFid =
        FunctorTable.Intern(AtomTable.Intern("term_expansion", permanent: true).Id, 2);

    internal bool HasPrologTermExpansion => HasPredicate(TermExpansionFid);

    // The Prolog term_expansion/2 hook: call the user predicate
    // `term_expansion(Input, Expanded)` in the live engine (works mid-consult) and
    // return its expansion. A term_expansion result that is a PROPER LIST is a list
    // of clauses (SWI/Scryer); anything else is a single clause; `[]` drops the
    // term. Returns false when term_expansion/2 is undefined or fails (no
    // expansion). Only consulted when HasPrologTermExpansion is true, so a program
    // that defines no hook pays nothing.
    internal bool TryPrologTermExpansion(Term input, out List<Term> output)
    {
        output = new List<Term>();
        var inputVars = new HashSet<string>();
        CollectVarNames(input, inputVars);
        var expandedVar = new VarTerm("$TE_Expanded");
        var goal = new CompoundTerm("term_expansion", new Term[] { input, expandedVar });
        foreach (var sol in QueryAll(goal))
        {
            Term? expanded = sol["$TE_Expanded"];
            if (expanded is null) return false;
            FlattenExpansion(RelinkInputVars(expanded, inputVars, sol), output);
            return true;
        }
        return false;
    }

    // Running an expansion through QueryAll materialises the input's variables and
    // reads the output back with fresh heap-address names (_G<addr>) — losing the
    // sharing between the input's vars and the clause around it. This restores it:
    // read each input var back too (same heap address → same _G<addr> name) and
    // rename that name in the output to the input var's ORIGINAL name, so the
    // expansion shares variables with the rest of the clause again.
    private static Term RelinkInputVars(Term output, HashSet<string> inputVars, Solution sol)
    {
        Dictionary<string, Term>? rename = null;
        foreach (string name in inputVars)
        {
            if (sol[name] is VarTerm rb && rb.Name != name)
                (rename ??= new())[rb.Name] = new VarTerm(name);
        }
        return rename is null ? output : SubstituteVars(output, rename);
    }

    private static void CollectVarNames(Term t, HashSet<string> into)
    {
        switch (t)
        {
            case VarTerm v: into.Add(v.Name); break;
            case CompoundTerm c:
                foreach (var a in c.Args) CollectVarNames(a, into);
                break;
        }
    }

    private static Term SubstituteVars(Term t, Dictionary<string, Term> map)
    {
        switch (t)
        {
            case VarTerm v: return map.TryGetValue(v.Name, out var r) ? r : t;
            case CompoundTerm c:
            {
                Term[]? args = null;
                for (int i = 0; i < c.Args.Length; i++)
                {
                    Term s = SubstituteVars(c.Args[i], map);
                    if (!ReferenceEquals(s, c.Args[i])) (args ??= (Term[])c.Args.Clone())[i] = s;
                }
                return args is null ? t : new CompoundTerm(c.Functor, args) { Position = c.Position };
            }
            default: return t;
        }
    }

    private static readonly int GoalExpansionFid =
        FunctorTable.Intern(AtomTable.Intern("goal_expansion", permanent: true).Id, 2);

    internal bool HasPrologGoalExpansion => HasPredicate(GoalExpansionFid);

    // Apply goal_expansion to every body goal of a clause (or a directive's goal).
    // A fact has no body and is returned unchanged.
    internal Clause ExpandClauseGoals(Clause clause)
    {
        switch (clause.Term)
        {
            case CompoundTerm { Functor: ":-", Args: [var head, var body] } rule:
            {
                Term nb = ExpandGoalTree(body);
                return ReferenceEquals(nb, body) ? clause
                    : new Clause(clause.Kind,
                        new CompoundTerm(":-", new[] { head, nb }) { Position = rule.Position },
                        clause.Position);
            }
            case CompoundTerm { Functor: ":-", Args: [var dbody] } dir:
            {
                Term nb = ExpandGoalTree(dbody);
                return ReferenceEquals(nb, dbody) ? clause
                    : new Clause(clause.Kind,
                        new CompoundTerm(":-", new[] { nb }) { Position = dir.Position },
                        clause.Position);
            }
            default:
                return clause;   // fact
        }
    }

    // Recurse through the cut-transparent control constructs and apply
    // goal_expansion (C# then Prolog) to each plain body goal.
    private Term ExpandGoalTree(Term goal)
    {
        if (goal is CompoundTerm c && IsGoalControl(c.Functor, c.Args.Length))
        {
            Term[]? args = null;
            for (int i = 0; i < c.Args.Length; i++)
            {
                Term ex = ExpandGoalTree(c.Args[i]);
                if (!ReferenceEquals(ex, c.Args[i]))
                    (args ??= (Term[])c.Args.Clone())[i] = ex;
            }
            return args is null ? goal
                : new CompoundTerm(c.Functor, args) { Position = c.Position };
        }
        // Plain goal — expand (bounded fixpoint so g→g' →g'' converges).
        Term g = goal;
        for (int i = 0; i < 8; i++)
        {
            Term? next = ApplyCsGoalExpansion(g)
                ?? (HasPrologGoalExpansion && TryPrologGoalExpansion(g, out var pe) ? pe : null);
            if (next is null || next.Equals(g)) break;
            g = next;
        }
        return ReferenceEquals(g, goal) ? goal : g;
    }

    // The control constructs whose arguments are themselves goals (walked, not
    // expanded as a unit). call/1 recurses into its goal too.
    private static bool IsGoalControl(string functor, int arity) => (functor, arity) switch
    {
        (",", 2) or (";", 2) or ("->", 2) or ("*->", 2)
            or ("\\+", 1) or ("not", 1) or ("call", 1) => true,
        _ => false,
    };

    // The Prolog goal_expansion/2 hook, mirroring TryPrologTermExpansion but for a
    // single replacement goal.
    private bool TryPrologGoalExpansion(Term input, out Term expanded)
    {
        var inputVars = new HashSet<string>();
        CollectVarNames(input, inputVars);
        var v = new VarTerm("$GE_Expanded");
        var goal = new CompoundTerm("goal_expansion", new Term[] { input, v });
        foreach (var sol in QueryAll(goal))
        {
            Term? e = sol["$GE_Expanded"];
            if (e is null) break;
            expanded = RelinkInputVars(e, inputVars, sol);
            return true;
        }
        expanded = input;
        return false;
    }

    // A proper list [a,b,…] → its elements (each a clause); [] → nothing (drop);
    // anything else → itself (one clause).
    private static void FlattenExpansion(Term t, List<Term> output)
    {
        if (t is AtomTerm { Name: "[]" }) return;
        if (t is CompoundTerm { Functor: ".", Args.Length: 2 })
        {
            Term cursor = t;
            var items = new List<Term>();
            while (cursor is CompoundTerm { Functor: ".", Args: [var head, var tail] })
            {
                items.Add(head);
                cursor = tail;
            }
            if (cursor is AtomTerm { Name: "[]" }) { output.AddRange(items); return; }
            // Improper list — treat the whole term as one clause.
        }
        output.Add(t);
    }
}
