using System.Collections.Generic;
using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Wam;

/// <summary>
/// ADR-030 — the intra-module determinism model and the redundant-trailing-cut
/// clause rewrite it drives. This is the single source of truth for "what leaves
/// no choice point"; <see cref="PredicateDisassembler"/>'s <c>--detcensus</c>
/// classification delegates to it so the census and the shipped elision can
/// never diverge.
///
/// <para><b>Determinism fixpoint.</b> A user predicate is <em>det</em> (leaves no
/// CP on success) when its dispatch is deterministic (single clause, all clauses
/// commit via a top-level cut, or first-argument keys are mutually exclusive)
/// AND every goal after the last cut in each clause body itself leaves no CP.
/// Goals are classified against a conservative builtin whitelist, the det control
/// constructs, and — recursively — the det set being computed. The least fixpoint
/// starts empty and adds predicates until stable; unknown/cross-module callees are
/// treated as non-det, so the analysis only ever <em>under</em>-claims determinism
/// (sound).</para>
///
/// <para><b>Redundant-cut elimination.</b> A predicate's <em>last</em> clause is
/// always reached with its clause-alternative choice point already consumed (the
/// dispatch chain's <c>trust</c>/<c>trust_me</c> pops it, and earlier clauses'
/// body CPs were unwound on backtrack into it). So a trailing top-level <c>!</c>
/// in the last clause can only prune choice points created by that clause's own
/// prefix goals. When every prefix goal is det, the cut prunes nothing and is
/// removed — semantically identical (same solutions, same side effects), and it
/// turns <c>Head :- …, call, !.</c> into a clean tail call eligible for LCO /
/// Tier-1 self-tail loops. Running extra clauses would be unsound
/// (<c>extra-backtracking-not-sound</c>); requiring prefix-det is exactly what
/// prevents it.</para>
/// </summary>
public sealed class DeterminismAnalysis
{
    /// <summary>How a body goal behaves w.r.t. leaving a choice point.</summary>
    public enum GoalKind
    {
        /// <summary>Inline goal (cut / true / fail / <c>is</c>/<c>=</c>/rel-op) —
        /// never pushes a CP and never needs a frame.</summary>
        Inline,
        /// <summary>A whitelisted builtin that leaves no CP in any mode.</summary>
        DetBuiltin,
        /// <summary>A user predicate proven det by the fixpoint.</summary>
        DetUserPred,
        /// <summary>A control construct that wraps its goal and never leaks the
        /// inner CP (<c>\+</c>, <c>once</c>, <c>findall</c>, …).</summary>
        DetControl,
        /// <summary>A predicate not defined in this module — the linker's
        /// whole-program closure could resolve it, but intra-module it is opaque
        /// and conservatively non-det.</summary>
        CrossModule,
        /// <summary>Anything that may leave a choice point (a non-det user
        /// predicate, a backtracking builtin, a disjunction, a var goal).</summary>
        Nondet,
    }

    // Builtins that leave NO choice point in any mode (whitelist; everything else
    // is treated as nondet — sound/conservative). NB: atom_concat/3, sub_atom/5,
    // member/2, between/3, … are deliberately absent — they backtrack.
    private static readonly HashSet<string> KnownDetBuiltins = new()
    {
        "true/0","fail/0","false/0","!/0","halt/0","halt/1",
        "is/2","=:=/2","=\\=/2","</2",">/2","=</2",">=/2",
        "=/2","==/2","\\==/2","\\=/2","@</2","@>/2","@=</2","@>=/2","compare/3",
        "var/1","nonvar/1","atom/1","atomic/1","number/1","integer/1","float/1",
        "compound/1","callable/1","is_list/1","ground/1",
        "functor/3","arg/3","=../2","copy_term/2",
        "atom_length/2","char_code/2","atom_number/2","number_codes/2","number_chars/2",
        "atom_codes/2","atom_chars/2","upcase_atom/2","downcase_atom/2","term_to_atom/2",
        "nl/0","nl/1","write/1","write/2","writeln/1","print/1","writeq/1","write_canonical/1",
        "tab/1","put_char/1","format/1","format/2","format/3",
        "assert/1","assertz/1","asserta/1","retractall/1",
        "nb_setval/2","nb_getval/2","b_setval/2","b_getval/2",
        "g_assign/2","g_read/2","g_assignb/2",
    };

    // Control constructs that leave no CP on success regardless of their goal
    // argument (they wrap and never leak the inner CP).
    private static readonly HashSet<string> DetControl = new()
    { "\\+/1","not/1","once/1","forall/2","findall/3","findall/4","ignore/1" };

    private readonly HashSet<string> _detPreds;
    private readonly HashSet<string> _definedInModule;

    private DeterminismAnalysis(HashSet<string> detPreds, HashSet<string> definedInModule)
    {
        _detPreds = detPreds;
        _definedInModule = definedInModule;
    }

    /// <summary>True iff <paramref name="indicator"/> (<c>name/arity</c>) is a
    /// module predicate the fixpoint proved deterministic.</summary>
    public bool IsDet(string indicator) => _detPreds.Contains(indicator);

    /// <summary>Classifies a single body goal against the computed det set.</summary>
    public GoalKind Classify(Term goal) => Classify(goal, _detPreds, _definedInModule);

    /// <summary>True iff a goal of this kind leaves no choice point on success.</summary>
    public static bool LeavesNoCp(GoalKind k) =>
        k is GoalKind.Inline or GoalKind.DetBuiltin or GoalKind.DetUserPred or GoalKind.DetControl;

    /// <summary>Runs the least-fixpoint determinism analysis over
    /// <paramref name="clauses"/>. <paramref name="isEligible"/> (optional) gates
    /// which predicates may be proven det: a predicate whose clauses are all
    /// ineligible (e.g. dynamic — its clause set changes at runtime) is never
    /// added to the det set, so callers see it as non-det.</summary>
    public static DeterminismAnalysis Build(
        IReadOnlyList<Clause> clauses, System.Func<Clause, bool>? isEligible = null)
    {
        System.ArgumentNullException.ThrowIfNull(clauses);
        var order = new List<string>();
        var groups = new Dictionary<string, List<Clause>>();
        var eligible = new Dictionary<string, bool>();
        foreach (Clause c in clauses)
        {
            if (c.Kind == ClauseKind.Directive) continue;
            string ind = HeadIndicator(c);
            if (!groups.TryGetValue(ind, out var list))
            {
                groups[ind] = list = new List<Clause>();
                order.Add(ind);
                eligible[ind] = isEligible?.Invoke(c) ?? true;
            }
            list.Add(c);
        }

        var defined = new HashSet<string>(order);

        // Pre-flatten each clause's body goals once.
        var flat = new Dictionary<string, List<List<Term>>>();
        foreach (string ind in order)
        {
            var perClause = new List<List<Term>>();
            foreach (Clause c in groups[ind])
            {
                var gs = new List<Term>();
                Term? body = ClauseBody(c);
                if (body is not null) FlattenConj(body, gs);
                perClause.Add(gs);
            }
            flat[ind] = perClause;
        }

        // GREATEST fixpoint: optimistically assume every eligible predicate is
        // det, then remove any that is provably non-det (bad dispatch, or a body
        // goal that leaves a CP) — where a recursive / mutually-recursive call is
        // classified against the *current* assumption. This proves a predicate
        // whose determinism depends on its own det-ness (self-recursion) det,
        // which a least-fixpoint-from-empty cannot bootstrap. Sound: a minimal
        // successful derivation of `p` leaving a residual CP would need a
        // strictly-smaller sub-derivation (a recursive call) also leaving one —
        // impossible under single-clause / all-but-last-commit dispatch when the
        // non-recursive goals are det. Ineligible (e.g. dynamic) predicates never
        // enter the set. Removal is monotone, so in-place (Gauss–Seidel) update
        // converges to the same greatest set.
        var detPreds = new HashSet<string>();
        foreach (string ind in order) if (eligible[ind]) detPreds.Add(ind);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string ind in order)
            {
                if (!detPreds.Contains(ind)) continue;
                if (!PredIsDet(groups[ind], flat[ind], detPreds, defined))
                { detPreds.Remove(ind); changed = true; }
            }
        }

        return new DeterminismAnalysis(detPreds, defined);
    }

    /// <summary>ADR-030 — returns a clause list in the same order as
    /// <paramref name="clauses"/> with the redundant trailing top-level <c>!</c>
    /// dropped from every eligible predicate's last clause whose prefix goals are
    /// all det. A clause whose body becomes empty is returned as a fact. All other
    /// clauses are returned unchanged (same references). The analysis is computed
    /// internally over the same <paramref name="isEligible"/> gate.</summary>
    public static List<Clause> EliminateRedundantTrailingCuts(
        IReadOnlyList<Clause> clauses, System.Func<Clause, bool>? isEligible = null)
    {
        System.ArgumentNullException.ThrowIfNull(clauses);
        var analysis = Build(clauses, isEligible);

        // Find, per eligible predicate, the index of its LAST clause in the
        // original list (last textual occurrence of the indicator).
        var lastClauseIndex = new Dictionary<string, int>();
        var eligibleInd = new HashSet<string>();
        for (int i = 0; i < clauses.Count; i++)
        {
            Clause c = clauses[i];
            if (c.Kind == ClauseKind.Directive) continue;
            string ind = HeadIndicator(c);
            lastClauseIndex[ind] = i;
            if (isEligible?.Invoke(c) ?? true) eligibleInd.Add(ind);
        }

        var result = new List<Clause>(clauses.Count);
        for (int i = 0; i < clauses.Count; i++)
        {
            Clause c = clauses[i];
            if (c.Kind != ClauseKind.Directive
                && lastClauseIndex.TryGetValue(HeadIndicator(c), out int last) && last == i
                && eligibleInd.Contains(HeadIndicator(c))
                && TryElideTrailingCut(c, analysis, out Clause? rewritten))
            {
                result.Add(rewritten!);
            }
            else
            {
                result.Add(c);
            }
        }
        return result;
    }

    /// <summary>Attempts to drop the redundant trailing cut from a single clause
    /// (assumed to be its predicate's last clause). Returns false when the clause
    /// is not a <c>…, !.</c> rule or its prefix is not provably det.</summary>
    private static bool TryElideTrailingCut(
        Clause c, DeterminismAnalysis analysis, out Clause? rewritten)
    {
        rewritten = null;
        if (c.Kind != ClauseKind.Rule
            || c.Term is not CompoundTerm { Functor: ":-", Args.Length: 2 } rule)
            return false;

        Term head = rule.Args[0];
        var goals = new List<Term>();
        FlattenConj(rule.Args[1], goals);
        if (goals.Count == 0 || goals[^1] is not AtomTerm { Name: "!" }) return false;

        // Every goal before the trailing cut must leave no choice point.
        for (int j = 0; j < goals.Count - 1; j++)
            if (!LeavesNoCp(analysis.Classify(goals[j]))) return false;

        // Rebuild the body without the trailing cut. Empty ⇒ the clause is a fact.
        var kept = goals.GetRange(0, goals.Count - 1);
        if (kept.Count == 0)
        {
            rewritten = new Clause(ClauseKind.Fact, head, c.Position);
            return true;
        }
        Term newBody = kept[^1];
        for (int j = kept.Count - 2; j >= 0; j--)
            newBody = new CompoundTerm(",", new[] { kept[j], newBody });
        rewritten = new Clause(ClauseKind.Rule,
            new CompoundTerm(":-", new[] { head, newBody }), c.Position);
        return true;
    }

    // --- The det model (shared with PredicateDisassembler.CensusDet). ---

    internal static GoalKind Classify(
        Term g, HashSet<string> detPreds, HashSet<string> definedInModule)
    {
        if (IsInlineLike(g)) return GoalKind.Inline;
        string? ind = GoalIndicator(g);
        if (ind is null) return GoalKind.Nondet;                      // var goal / call(_)
        if (ind is ";/2" or "->/2" or "*->/2") return GoalKind.Nondet; // scoping we don't model
        if (DetControl.Contains(ind)) return GoalKind.DetControl;
        if (KnownDetBuiltins.Contains(ind)) return GoalKind.DetBuiltin;
        if (definedInModule.Contains(ind))
            return detPreds.Contains(ind) ? GoalKind.DetUserPred : GoalKind.Nondet;
        return GoalKind.CrossModule;
    }

    private static bool PredIsDet(List<Clause> cls, List<List<Term>> flat,
        HashSet<string> detPreds, HashSet<string> definedInModule)
    {
        if (!DispatchDet(cls)) return false;
        for (int i = 0; i < flat.Count; i++)
        {
            var goals = flat[i];
            int lastCut = -1;
            for (int j = 0; j < goals.Count; j++)
                if (goals[j] is AtomTerm { Name: "!" }) lastCut = j;
            for (int j = lastCut + 1; j < goals.Count; j++)
                if (!LeavesNoCp(Classify(goals[j], detPreds, definedInModule)))
                    return false;
        }
        return true;
    }

    // Deterministic dispatch, mode-AGNOSTICALLY (we do not know the call's
    // instantiation): single clause (no clause-alternative CP is ever created),
    // OR every clause EXCEPT the last commits via a top-level cut. The last
    // clause needs no cut — it is reached only via `trust`, with the clause-
    // selection CP already consumed. Whichever earlier clause yields a solution
    // must have run its top-level cut (a top-level cut is on every success path
    // of its clause), pruning the rest; whichever clause yields therefore leaves
    // no clause CP. The per-clause post-cut body-det check in PredIsDet covers
    // what each clause leaves after its cut, including the last clause's cut-free
    // body — so the two together are sound.
    //
    // First-argument mutual exclusivity is DELIBERATELY NOT used: it only makes
    // dispatch deterministic when the call supplies a ground first argument
    // (`q(a). q(b).` still leaves a CP under `q(X)`), so relying on it would be
    // unsound for a partially-instantiated call. This rule instead keys off the
    // cuts, whose commit is decided by runtime success, not the call mode.
    private static bool DispatchDet(List<Clause> cls)
    {
        if (cls.Count == 1) return true;
        for (int i = 0; i < cls.Count - 1; i++)
            if (!BodyCommits(cls[i])) return false;
        return true;
    }

    // The goals that never push a CP AND never need a frame (matches the
    // compiler's IsInlineBodyGoal): cut / true / fail, is, =, the six rel-ops.
    private static bool IsInlineLike(Term g) => g switch
    {
        AtomTerm { Name: "!" or "true" or "fail" or "false" } => true,
        CompoundTerm { Functor: "is" or "=", Args.Length: 2 } => true,
        CompoundTerm { Args.Length: 2 } c
            when Shumway.Builtins.ArithmeticEvaluator.TryRelOp(c.Functor, out _) => true,
        _ => false,
    };

    private static bool BodyCommits(Clause c)
    {
        if (c.Kind != ClauseKind.Rule || c.Term is not CompoundTerm r || r.Args.Length != 2)
            return false;
        return TopLevelCut(r.Args[1]);

        static bool TopLevelCut(Term t) => t switch
        {
            AtomTerm a => a.Name == "!",
            CompoundTerm c when c.Functor == "," && c.Args.Length == 2 =>
                TopLevelCut(c.Args[0]) || TopLevelCut(c.Args[1]),
            _ => false,
        };
    }

    private static string? GoalIndicator(Term g) => g switch
    {
        AtomTerm a => a.Name + "/0",
        CompoundTerm c => c.Functor + "/" + c.Args.Length,
        _ => null,
    };

    private static Term ClauseHead(Clause c) =>
        c.Kind == ClauseKind.Rule && c.Term is CompoundTerm { Args.Length: 2 } r ? r.Args[0] : c.Term;

    private static Term? ClauseBody(Clause c) =>
        c.Kind == ClauseKind.Rule && c.Term is CompoundTerm { Args.Length: 2 } r ? r.Args[1] : null;

    /// <summary>The <c>name/arity</c> indicator of a clause's head.</summary>
    public static string HeadIndicator(Clause c)
    {
        Term head = ClauseHead(c);
        return head switch
        {
            AtomTerm a => a.Name + "/0",
            CompoundTerm ct => ct.Functor + "/" + ct.Args.Length,
            _ => "?/0",
        };
    }

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
