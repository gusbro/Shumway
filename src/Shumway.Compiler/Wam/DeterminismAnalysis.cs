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
/// CP on success) when its dispatch is deterministic — single clause, OR every
/// clause except the last that <em>can succeed</em> commits via a top-level cut
/// (the last clause is reached only via <c>trust</c>; a clause with a top-level
/// <c>fail</c>/<c>false</c> never yields, so it is exempt) — AND every goal after
/// the last cut in each can-succeed clause body itself leaves no CP. First-argument
/// key exclusivity is deliberately NOT used (it is mode-dependent). Goals are
/// classified against a conservative builtin whitelist, the det control constructs,
/// and — recursively — the det set being computed, via a <em>greatest</em> fixpoint
/// (assume all eligible predicates det, remove the provably non-det) so a predicate
/// whose determinism depends on its own (self / mutual recursion) is proven det.
/// Unknown/cross-module callees are treated as non-det, so the analysis only ever
/// <em>under</em>-claims determinism (sound).</para>
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
                if (!PredIsDet(flat[ind], detPreds, defined))
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
                && TryElideTrailingCut(c, analysis.Classify, out Clause? rewritten))
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
    /// is not a <c>…, !.</c> rule or its prefix is not provably det (per
    /// <paramref name="classify"/> — module-local or whole-program).</summary>
    private static bool TryElideTrailingCut(
        Clause c, System.Func<Term, GoalKind> classify, out Clause? rewritten)
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
            if (!LeavesNoCp(classify(goals[j]))) return false;

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

    // --- ADR-030 linker closure: the WHOLE-PROGRAM variant. ---

    /// <summary>Whole-program determinism (the ADR-030 linker closure). The same
    /// greatest-fixpoint model as <see cref="Build"/>, but over EVERY module's
    /// clauses at once: a goal resolves module-locally first, then to the global
    /// PUBLIC definition — so a cross-module callee is no longer opaque and the
    /// <see cref="GoalKind.CrossModule"/> blocker disappears for anything the
    /// program actually defines. Qualified indicators are
    /// <c>module + '' + name/arity</c>.</summary>
    public sealed class WholeProgram
    {
        private readonly HashSet<string> _detPreds;
        private readonly Dictionary<string, HashSet<string>> _localDefs;
        private readonly IReadOnlyDictionary<string, string> _publicOwner;

        private WholeProgram(
            HashSet<string> detPreds,
            Dictionary<string, HashSet<string>> localDefs,
            IReadOnlyDictionary<string, string> publicOwner)
        {
            _detPreds = detPreds;
            _localDefs = localDefs;
            _publicOwner = publicOwner;
        }

        internal static string Qualify(string module, string ind) => module + "" + ind;

        /// <summary>Resolves a bare <c>name/arity</c> goal indicator as seen from
        /// <paramref name="module"/>: the module's own definition wins, else the
        /// global public owner; null when the program does not define it.</summary>
        public string? Resolve(string module, string ind)
        {
            if (_localDefs.TryGetValue(module, out var defs) && defs.Contains(ind))
                return Qualify(module, ind);
            if (_publicOwner.TryGetValue(ind, out string? owner))
                return Qualify(owner, ind);
            return null;
        }

        /// <summary>True iff the predicate (qualified per <see cref="Resolve"/>)
        /// was proven deterministic by the whole-program fixpoint.</summary>
        public bool IsDet(string module, string ind)
            => Resolve(module, ind) is string q && _detPreds.Contains(q);

        /// <summary>Classifies a body goal as seen from <paramref name="module"/>.</summary>
        public GoalKind ClassifyIn(string module, Term goal)
            => ClassifyCore(goal, _detPreds, ind => Resolve(module, ind));

        /// <summary><paramref name="modules"/>: each module's static clauses +
        /// an optional per-clause eligibility gate (dynamic predicates must be
        /// excluded by the caller). <paramref name="publicOwner"/> maps a bare
        /// public indicator to its defining module.</summary>
        public static WholeProgram Build(
            IReadOnlyList<(string Module, IReadOnlyList<Clause> Clauses,
                System.Func<Clause, bool>? IsEligible)> modules,
            IReadOnlyDictionary<string, string> publicOwner)
        {
            var order = new List<string>();                       // qualified inds
            var flat = new Dictionary<string, List<List<Term>>>();
            var eligible = new Dictionary<string, bool>();
            var moduleOf = new Dictionary<string, string>();
            var localDefs = new Dictionary<string, HashSet<string>>();

            foreach (var (module, clauses, isEligible) in modules)
            {
                var defs = new HashSet<string>();
                localDefs[module] = defs;
                foreach (Clause c in clauses)
                {
                    if (c.Kind == ClauseKind.Directive) continue;
                    string ind = HeadIndicator(c);
                    defs.Add(ind);
                    string q = Qualify(module, ind);
                    if (!flat.TryGetValue(q, out var perClause))
                    {
                        flat[q] = perClause = new List<List<Term>>();
                        order.Add(q);
                        eligible[q] = isEligible?.Invoke(c) ?? true;
                        moduleOf[q] = module;
                    }
                    var gs = new List<Term>();
                    Term? body = ClauseBody(c);
                    if (body is not null) FlattenConj(body, gs);
                    perClause.Add(gs);
                }
            }

            var result = new WholeProgram(new HashSet<string>(), localDefs, publicOwner);
            var detPreds = result._detPreds;
            foreach (string q in order) if (eligible[q]) detPreds.Add(q);
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (string q in order)
                {
                    if (!detPreds.Contains(q)) continue;
                    string module = moduleOf[q];
                    if (!PredIsDetCore(flat[q], detPreds, ind => result.Resolve(module, ind)))
                    { detPreds.Remove(q); changed = true; }
                }
            }
            return result;
        }

        /// <summary>The ADR-030 rewrite with whole-program knowledge: drops the
        /// redundant trailing cut from <paramref name="module"/>'s eligible last
        /// clauses whose prefixes are det under the whole-program fixpoint.
        /// <paramref name="elided"/> reports how many clauses changed.</summary>
        public List<Clause> EliminateRedundantTrailingCuts(
            string module, IReadOnlyList<Clause> clauses,
            System.Func<Clause, bool>? isEligible, out int elided)
        {
            elided = 0;
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
                    && TryElideTrailingCut(c, g => ClassifyIn(module, g), out Clause? rewritten))
                {
                    result.Add(rewritten!);
                    elided++;
                }
                else
                {
                    result.Add(c);
                }
            }
            return result;
        }
    }

    // --- The det model (shared with PredicateDisassembler.CensusDet and the
    //     whole-program linker closure). ---

    internal static GoalKind Classify(
        Term g, HashSet<string> detPreds, HashSet<string> definedInModule)
        => ClassifyCore(g, detPreds,
            ind => definedInModule.Contains(ind) ? ind : null);

    private static GoalKind ClassifyCore(
        Term g, HashSet<string> detPreds, System.Func<string, string?> resolve)
    {
        if (IsInlineLike(g)) return GoalKind.Inline;
        string? ind = GoalIndicator(g);
        if (ind is null) return GoalKind.Nondet;                      // var goal / call(_)
        if (ind is ";/2" or "->/2" or "*->/2") return GoalKind.Nondet; // scoping we don't model
        if (DetControl.Contains(ind)) return GoalKind.DetControl;
        if (KnownDetBuiltins.Contains(ind)) return GoalKind.DetBuiltin;
        string? q = resolve(ind);
        if (q is null) return GoalKind.CrossModule;
        return detPreds.Contains(q) ? GoalKind.DetUserPred : GoalKind.Nondet;
    }

    private static bool PredIsDet(List<List<Term>> flat,
        HashSet<string> detPreds, HashSet<string> definedInModule)
        => PredIsDetCore(flat, detPreds,
            ind => definedInModule.Contains(ind) ? ind : null);

    private static bool PredIsDetCore(List<List<Term>> flat,
        HashSet<string> detPreds, System.Func<string, string?> resolve)
    {
        if (!DispatchDet(flat)) return false;
        for (int i = 0; i < flat.Count; i++)
        {
            var goals = flat[i];
            if (!CanSucceed(goals)) continue;   // a clause that never yields leaves no CP on success
            int lastCut = -1;
            for (int j = 0; j < goals.Count; j++)
                if (goals[j] is AtomTerm { Name: "!" }) lastCut = j;
            for (int j = lastCut + 1; j < goals.Count; j++)
                if (!LeavesNoCp(ClassifyCore(goals[j], detPreds, resolve)))
                    return false;
        }
        return true;
    }

    // Deterministic dispatch, mode-AGNOSTICALLY (we do not know the call's
    // instantiation): single clause (no clause-alternative CP is ever created),
    // OR every clause EXCEPT the last that CAN SUCCEED commits via a top-level
    // cut. The last clause needs no cut — it is reached only via `trust`, with
    // the clause-selection CP already consumed. A clause that can never succeed
    // (a top-level `fail`/`false` conjunct) never yields, so it leaves no CP on
    // success and is exempt too (`p(X):-q(X),fail. p(X):-q(X),!.` is det — clause
    // 1 always fails, clause 2's trailing cut commits). Whichever surviving
    // clause yields a solution must have run its top-level cut (a top-level cut
    // is on every success path of its clause), pruning the rest; so whichever
    // clause yields leaves no clause CP. The per-clause post-cut body-det check
    // in PredIsDet (also skipping can't-succeed clauses) covers what each clause
    // leaves after its cut — the two together are sound.
    //
    // First-argument mutual exclusivity is DELIBERATELY NOT used: it only makes
    // dispatch deterministic when the call supplies a ground first argument
    // (`q(a). q(b).` still leaves a CP under `q(X)`), so relying on it would be
    // unsound for a partially-instantiated call. This rule instead keys off the
    // cuts, whose commit is decided by runtime success, not the call mode.
    private static bool DispatchDet(List<List<Term>> flat)
    {
        if (flat.Count == 1) return true;
        for (int i = 0; i < flat.Count - 1; i++)
            if (CanSucceed(flat[i]) && !HasTopLevelCut(flat[i])) return false;
        return true;
    }

    // A top-level cut anywhere in the clause's conjunction commits it: the clause
    // succeeds only if every top-level conjunct (incl. the cut) ran.
    private static bool HasTopLevelCut(List<Term> goals)
    {
        foreach (Term g in goals) if (g is AtomTerm { Name: "!" }) return true;
        return false;
    }

    // A clause cannot succeed if any top-level conjunct is `fail`/`false` — the
    // whole conjunction then fails (a `fail` inside a disjunction is one `;/2`
    // goal, not a top-level conjunct, so it is correctly not detected here).
    private static bool CanSucceed(List<Term> goals)
    {
        foreach (Term g in goals) if (g is AtomTerm { Name: "fail" or "false" }) return false;
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
