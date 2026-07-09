using System.Collections.Generic;
using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Wam;

/// <summary>
/// ADR-031 — recognises the multi-clause <c>p :- Guard, !, Body.  p :- Rest.</c>
/// shape that can be folded to <c>p :- ( Guard -&gt; Body ; Rest )</c> so the
/// committing guard never pushes the clause-selection choice point the cut would
/// otherwise tear down. This file is the shared recogniser used both by the
/// <c>--foldcensus</c> sizing pass and (later, if the prototype clears its A/B
/// gate) the transform itself.
///
/// <para>The fold is sound regardless of the guard's determinism — <c>-&gt;</c>
/// gives exactly the once-commit the <c>!</c> gave, and on guard failure it
/// backtracks (undoing the guard's bindings) before <c>Rest</c>, matching
/// clause-tried-next semantics. The constraint is purely about the HEADS:
/// clauses separated by first-argument indexing already get deterministic
/// dispatch (the cut is redundant there — ADR-030 territory), so only
/// non-discriminating var-argument heads are worth folding.</para>
/// </summary>
public static class ClauseFold
{
    public enum FoldKind
    {
        /// <summary>Not a fold candidate (single clause, no leading guarded cut,
        /// multiple/deep cuts, or a structured head that indexing separates).</summary>
        None,
        /// <summary>Every clause head is all-distinct variables in the SAME
        /// positional pattern (`p(X,Y)` throughout) — folds with a plain variable
        /// rename, no head-argument unification threaded into the body.</summary>
        TrivialVarHeads,
        /// <summary>All head arguments are variables, but the pattern varies across
        /// clauses (a repeated var like `max(X,Y,X)`, or different shapes) — folds
        /// only by threading the per-clause head unifications into each branch.</summary>
        ThreadedVarHeads,
    }

    /// <summary>Classifies a predicate's clause group (all same functor/arity, in
    /// source order) for ADR-031 folding.</summary>
    public static FoldKind Classify(IReadOnlyList<Clause> clauses)
    {
        if (clauses.Count < 2) return FoldKind.None;

        // Clause 1 must be `Head :- Guard, !, Body` with exactly one top-level cut
        // that is not the last goal-shape we care about... actually any single
        // top-level cut with a (possibly empty) guard before it qualifies; the cut
        // commits clause selection.
        if (!FirstClauseGuardedCut(clauses[0])) return FoldKind.None;

        // No later clause may carry a top-level cut of its own (that is a
        // different, multi-commit shape we do not model here).
        for (int i = 1; i < clauses.Count; i++)
            if (HasTopLevelCut(clauses[i])) return FoldKind.None;

        // Head classification across ALL clauses.
        bool allTrivial = true;
        string? pattern = null;
        foreach (Clause c in clauses)
        {
            Term head = Head(c);
            if (head is AtomTerm) continue;            // arity 0 — vacuously var-headed
            if (head is not CompoundTerm hc) return FoldKind.None;
            if (!AllArgsVars(hc)) return FoldKind.None;  // a structured arg → indexing separates
            string pat = VarPattern(hc);
            if (pattern is null) pattern = pat;
            else if (pat != pattern) allTrivial = false;
        }
        return allTrivial ? FoldKind.TrivialVarHeads : FoldKind.ThreadedVarHeads;
    }

    // Clause 1 is a rule whose flattened body has EXACTLY ONE top-level cut, with
    // that cut not being the whole body (there is a guard and/or a body around it).
    private static bool FirstClauseGuardedCut(Clause c)
    {
        if (c.Kind != ClauseKind.Rule
            || c.Term is not CompoundTerm { Functor: ":-", Args.Length: 2 } rule)
            return false;
        var goals = new List<Term>();
        FlattenConj(rule.Args[1], goals);
        int cuts = 0, cutPos = -1;
        for (int i = 0; i < goals.Count; i++)
            if (goals[i] is AtomTerm { Name: "!" }) { cuts++; cutPos = i; }
        if (cuts != 1) return false;                 // zero, or multiple cuts → skip
        // A trailing cut (`Guard, !.`) is ADR-030's last-clause case only when this
        // is the last clause; as clause 1 of a multi-clause pred it still commits
        // clause selection, so `Guard, !` (body empty) folds to `(Guard -> true ; Rest)`.
        return cutPos >= 0;
    }

    private static bool HasTopLevelCut(Clause c)
    {
        if (c.Kind != ClauseKind.Rule
            || c.Term is not CompoundTerm { Functor: ":-", Args.Length: 2 } rule)
            return false;
        var goals = new List<Term>();
        FlattenConj(rule.Args[1], goals);
        foreach (Term g in goals) if (g is AtomTerm { Name: "!" }) return true;
        return false;
    }

    private static bool AllArgsVars(CompoundTerm head)
    {
        foreach (Term a in head.Args) if (a is not VarTerm) return false;
        return true;
    }

    // A canonical string for a head's variable pattern by first-occurrence index,
    // so `p(X,Y)` and `p(A,B)` share a pattern but `max(X,Y,X)` differs from
    // `max(X,Y,Z)` (the repeated first var shows as a back-reference).
    private static string VarPattern(CompoundTerm head)
    {
        var seen = new Dictionary<string, int>();
        var sb = new System.Text.StringBuilder();
        foreach (Term a in head.Args)
        {
            string name = ((VarTerm)a).Name;
            if (name == "_") { sb.Append("_;"); continue; }   // anonymous — always fresh
            if (!seen.TryGetValue(name, out int idx)) { idx = seen.Count; seen[name] = idx; }
            sb.Append(idx).Append(';');
        }
        return sb.ToString();
    }

    private static Term Head(Clause c) =>
        c.Kind == ClauseKind.Rule && c.Term is CompoundTerm { Args.Length: 2 } r ? r.Args[0] : c.Term;

    private static void FlattenConj(Term body, List<Term> outGoals)
    {
        while (body is CompoundTerm { Functor: ",", Args.Length: 2 } c)
        {
            FlattenConj(c.Args[0], outGoals);
            body = c.Args[1];
        }
        outGoals.Add(body);
    }
}
