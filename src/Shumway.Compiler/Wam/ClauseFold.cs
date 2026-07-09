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

    /// <summary>ADR-031 phase-2 sizing — what the FIRST clause's pre-cut guard is
    /// made of, deciding which CP-free emission tier can serve it.</summary>
    public enum GuardClass
    {
        /// <summary>Empty guard, or only integer comparisons on plain operands —
        /// the shipped phase-1 tier (<c>a_int_cmp; neck_cut</c>).</summary>
        CmpOnly,
        /// <summary>Comparisons over compound arithmetic expressions or float
        /// operands (the <c>a_eval</c> lane) — non-binding, whitelist widening
        /// (deferred case C).</summary>
        EvalCmp,
        /// <summary>Contains <c>=/2</c> — a binding unification; needs the trail/hb
        /// snapshot machinery (deferred case B).</summary>
        BindingUnify,
        /// <summary>Only identity/order tests (<c>==</c>, <c>\==</c>, <c>@&lt;</c>…)
        /// and/or type tests (<c>var</c>, <c>atom</c>, <c>number</c>…) plus
        /// comparisons — non-binding but compiled as framed builtin CALLS with
        /// register staging (deferred case E).</summary>
        TypeTestOrIdent,
        /// <summary>Contains <c>is/2</c> / <c>functor/arg/=..</c>-style det
        /// builtins alongside tests — deterministic and CP-free in principle, but
        /// register-writing (needs the register-save machinery).</summary>
        DetBuiltinMix,
        /// <summary>Contains a call to a user predicate (or an opaque goal) — the
        /// guard's failure propagates through the engine's backtracking, so a
        /// choice point is structurally required (only a det-fixpoint argument à la
        /// ADR-030 could ever elide it).</summary>
        UserCall,
        /// <summary>Control constructs (<c>;</c>, <c>-&gt;</c>, <c>\+</c>, …) or
        /// anything else — not CP-free-able without full generality.</summary>
        Other,
    }

    /// <summary>Classifies the first clause's pre-cut guard for phase-2 sizing.
    /// Call only on a group <see cref="Classify"/> accepted.</summary>
    public static GuardClass ClassifyGuard(IReadOnlyList<Clause> clauses)
    {
        var rule = (CompoundTerm)clauses[0].Term;      // Classify guaranteed Rule ":-"/2
        var goals = new List<Term>();
        FlattenConj(rule.Args[1], goals);
        bool sawEval = false, sawTypeIdent = false, sawBind = false;
        bool sawDetBuiltin = false, sawUserCall = false, sawOther = false;
        foreach (Term g in goals)
        {
            if (g is AtomTerm { Name: "!" }) break;    // guard ends at the cut
            switch (g)
            {
                case CompoundTerm { Args.Length: 2 } c when IsCmp(c.Functor):
                    if (!(IsPlainOperand(c.Args[0]) && IsPlainOperand(c.Args[1])))
                        sawEval = true;
                    break;
                case CompoundTerm { Functor: "=", Args.Length: 2 }:
                    sawBind = true;
                    break;
                case CompoundTerm { Args.Length: 2 } c when IsIdentTest(c.Functor):
                case AtomTerm a0 when IsTypeTestName(a0.Name, 0):
                    sawTypeIdent = true;
                    break;
                case CompoundTerm { Args.Length: 1 } t when IsTypeTestName(t.Functor, 1):
                    sawTypeIdent = true;
                    break;
                case CompoundTerm dg when IsDetBuiltinGoal(dg.Functor, dg.Args.Length):
                    sawDetBuiltin = true;
                    break;
                case CompoundTerm { Functor: ";" or "->" or "*->" or "\\+" }:
                case CompoundTerm { Functor: "not", Args.Length: 1 }:
                case VarTerm:
                    sawOther = true;
                    break;
                case AtomTerm or CompoundTerm:
                    sawUserCall = true;                // an atom/compound goal = a call
                    break;
                default:
                    sawOther = true;
                    break;
            }
        }
        if (sawOther) return GuardClass.Other;
        if (sawUserCall) return GuardClass.UserCall;
        if (sawDetBuiltin) return GuardClass.DetBuiltinMix;
        if (sawBind) return GuardClass.BindingUnify;
        if (sawTypeIdent) return GuardClass.TypeTestOrIdent;
        if (sawEval) return GuardClass.EvalCmp;
        return GuardClass.CmpOnly;
    }

    private static bool IsCmp(string f) =>
        f is "<" or ">" or "=<" or ">=" or "=:=" or "=\\=";

    private static bool IsIdentTest(string f) =>
        f is "==" or "\\==" or "@<" or "@>" or "@=<" or "@>=";

    private static bool IsTypeTestName(string n, int arity) =>
        arity == 1 && n is "var" or "nonvar" or "atom" or "atomic" or "number"
            or "integer" or "float" or "compound" or "callable" or "is_list";

    // Deterministic, non-CP builtins commonly used in guards: arithmetic
    // assignment and term inspection/construction. Register-writing (the result
    // lands in a register / builds cells), so a distinct CP-free tier from the
    // pure tests above.
    private static bool IsDetBuiltinGoal(string n, int arity) => (n, arity) switch
    {
        ("is", 2) => true,
        ("functor", 3) or ("arg", 3) or ("=..", 2) or ("copy_term", 2) => true,
        ("atom_length", 2) or ("atom_codes", 2) or ("atom_chars", 2) => true,
        ("compare", 3) => true,
        _ => false,
    };

    // A comparison operand the a_int_cmp fast lane takes directly: a variable or
    // an integer constant (an expression / float forces the a_eval lane).
    private static bool IsPlainOperand(Term t) => t is VarTerm or IntTerm;

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
