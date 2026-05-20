using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// Pre-compilation AST pass that rewrites meta-call goals into ordinary
/// predicate calls plus a small batch of synthesised helper clauses. The
/// rewrite avoids needing a runtime meta-call opcode for the common cases
/// the pass currently handles.
///
/// <para><b>Negation as failure</b> (<c>\+ G</c> and the synonym
/// <c>not(G)</c>): rewritten using the classic two-clause helper:</para>
/// <code>
///   '$neg_N'(V1, ..., Vk) :- G, !, fail.
///   '$neg_N'(V1, ..., Vk).
/// </code>
/// <para>The compiler later compiles these two clauses with the existing
/// <c>try_me_else</c> / <c>trust_me</c> machinery and the
/// <c>neck_cut</c> / <c>fail</c> instructions. Calling the helper with the
/// surrounding clause's bindings produces exactly the desired semantics:
/// when <c>G</c> succeeds, the cut commits to clause 1 and <c>fail</c>
/// makes the helper fail; when <c>G</c> fails, the second clause makes the
/// helper succeed. Any bindings <c>G</c> made before failing are unwound by
/// the outer choice point's trail. The free variables <c>V1..Vk</c> are
/// the named (non-anonymous) variables that appear in <c>G</c>, in
/// first-occurrence order — they're the only channel through which
/// surrounding-scope bindings reach the helper.</para>
///
/// <para>Helper names use the prefix <c>$neg_</c> to keep them disjoint
/// from anything the user can write (the parser rejects atoms whose name
/// starts with <c>$</c> when unquoted).</para>
/// </summary>
public static class MetaTransform
{
    public static List<Clause> Apply(IEnumerable<Clause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        var result = new List<Clause>();
        var helpers = new List<Clause>();
        int counter = 0;

        foreach (var clause in clauses)
        {
            if (clause.Kind == ClauseKind.Rule
                && clause.Term is CompoundTerm ruleTerm
                && ruleTerm.Args.Length == 2)
            {
                Term head = ruleTerm.Args[0];
                Term body = ruleTerm.Args[1];
                Term newBody = TransformGoal(body, ref counter, helpers);
                Term newRuleTerm = new CompoundTerm(":-", new[] { head, newBody }) { Position = ruleTerm.Position };
                result.Add(new Clause(ClauseKind.Rule, newRuleTerm, clause.Position));
            }
            else
            {
                result.Add(clause);
            }
        }

        result.AddRange(helpers);
        return result;
    }

    private static Term TransformGoal(Term goal, ref int counter, List<Clause> helpers)
    {
        // Conjunction: recurse into both halves.
        if (goal is CompoundTerm { Functor: "," } conj && conj.Args.Length == 2)
        {
            Term lhs = TransformGoal(conj.Args[0], ref counter, helpers);
            Term rhs = TransformGoal(conj.Args[1], ref counter, helpers);
            return new CompoundTerm(",", new[] { lhs, rhs }) { Position = goal.Position };
        }

        // \+ G  or  not(G)  — synthesise the helper and emit a call to it.
        if (goal is CompoundTerm ct
            && ct.Args.Length == 1
            && (ct.Functor == "\\+" || ct.Functor == "not"))
        {
            return SynthesizeNegationHelper(ct.Args[0], ref counter, helpers);
        }

        // findall(Template, Goal, List) with a callable (non-variable)
        // Goal — rewrite to an in-engine collect loop (chunk 83):
        //   ( '$findall_push', Goal, '$findall_record'(Template), fail
        //   ; '$findall_collect'(List) )
        // Goal is spliced in as a body goal, so it compiles inline with
        // real choice points and runs in the live engine — no sub-engine,
        // and side effects (assertz) persist. The fail drives the
        // backtracking that enumerates every solution; the disjunction
        // then routes to '$findall_collect'. A bare-variable Goal is left
        // alone — it falls through to the runtime findall/3 builtin.
        if (goal is CompoundTerm fa
            && fa.Functor == "findall"
            && fa.Args.Length == 3
            && fa.Args[1] is not VarTerm)
        {
            Term collectLoop = new CompoundTerm(",", new[]
            {
                (Term)new AtomTerm("$findall_push"),
                new CompoundTerm(",", new[]
                {
                    fa.Args[1],
                    new CompoundTerm(",", new[]
                    {
                        (Term)new CompoundTerm("$findall_record", new[] { fa.Args[0] }),
                        new AtomTerm("fail"),
                    }),
                }),
            });
            Term rewritten = new CompoundTerm(";", new[]
            {
                collectLoop,
                (Term)new CompoundTerm("$findall_collect", new[] { fa.Args[2] }),
            }) { Position = goal.Position };
            return TransformGoal(rewritten, ref counter, helpers);
        }

        // Disjunction (A ; B) and if-then-else (A -> B ; C) — both compile
        // to a two-clause helper that the standard try_me_else / trust_me
        // dispatch then handles.
        if (goal is CompoundTerm disj && disj.Functor == ";" && disj.Args.Length == 2)
        {
            return SynthesizeDisjunctionHelper(disj.Args[0], disj.Args[1], ref counter, helpers);
        }

        return goal;
    }

    /// <summary>Rewrites <c>(A ; B)</c> and <c>(A -&gt; B ; C)</c> into a
    /// call to a freshly-synthesised two-clause helper. The classic
    /// Aït-Kaci translation: each branch becomes one clause of the helper,
    /// and the regular WAM choice-point dispatch makes the disjunction
    /// behave with the right backtracking semantics.</summary>
    private static Term SynthesizeDisjunctionHelper(
        Term left, Term right, ref int counter, List<Clause> helpers)
    {
        counter++;
        string helperName = $"$disj_{counter}";

        var freeVars = new List<string>();
        var seen = new HashSet<string>();
        CollectNamedVars(left, freeVars, seen);
        CollectNamedVars(right, freeVars, seen);

        // Recurse into both branches first so nested control structures
        // (further disjunctions, negations, etc.) get their own helpers.
        Term recursedLeft = TransformGoal(left, ref counter, helpers);
        Term recursedRight = TransformGoal(right, ref counter, helpers);

        Term BuildHelperHead() => freeVars.Count == 0
            ? (Term)new AtomTerm(helperName)
            : new CompoundTerm(helperName, freeVars.Select(n => (Term)new VarTerm(n)).ToArray());

        // If-then-else: (A -> B ; C) translates to two clauses with a
        // commit cut between A and B in the first clause.
        if (left is CompoundTerm ite && ite.Functor == "->" && ite.Args.Length == 2)
        {
            Term cond = TransformGoal(ite.Args[0], ref counter, helpers);
            Term then = TransformGoal(ite.Args[1], ref counter, helpers);
            // Clause 1: '$disj_N'(...) :- A, !, B.
            Term clause1Body = new CompoundTerm(",", new[]
            {
                cond,
                new CompoundTerm(",", new[] { (Term)new AtomTerm("!"), then })
            });
            helpers.Add(new Clause(
                ClauseKind.Rule,
                new CompoundTerm(":-", new[] { BuildHelperHead(), clause1Body }),
                left.Position));
            // Clause 2: '$disj_N'(...) :- C.
            helpers.Add(new Clause(
                ClauseKind.Rule,
                new CompoundTerm(":-", new[] { BuildHelperHead(), recursedRight }),
                right.Position));
            return BuildHelperHead();
        }

        // Plain disjunction.
        // Clause 1: '$disj_N'(...) :- A.
        helpers.Add(new Clause(
            ClauseKind.Rule,
            new CompoundTerm(":-", new[] { BuildHelperHead(), recursedLeft }),
            left.Position));
        // Clause 2: '$disj_N'(...) :- B.
        helpers.Add(new Clause(
            ClauseKind.Rule,
            new CompoundTerm(":-", new[] { BuildHelperHead(), recursedRight }),
            right.Position));
        return BuildHelperHead();
    }

    private static Term SynthesizeNegationHelper(
        Term innerGoal, ref int counter, List<Clause> helpers)
    {
        counter++;
        string helperName = $"$neg_{counter}";

        var freeVars = new List<string>();
        var seen = new HashSet<string>();
        CollectNamedVars(innerGoal, freeVars, seen);

        // Recurse into innerGoal too — a nested \+ inside the negated goal
        // should be transformed before being used as the helper's body.
        innerGoal = TransformGoal(innerGoal, ref counter, helpers);

        Term BuildHelperHead() => freeVars.Count == 0
            ? (Term)new AtomTerm(helperName)
            : new CompoundTerm(helperName, freeVars.Select(n => (Term)new VarTerm(n)).ToArray());

        // Clause 1: '$neg_N'(V1..) :- G, !, fail.
        Term clause1Body = new CompoundTerm(",", new[]
        {
            innerGoal,
            new CompoundTerm(",", new[]
            {
                (Term)new AtomTerm("!"),
                new AtomTerm("fail"),
            }),
        });
        helpers.Add(new Clause(
            ClauseKind.Rule,
            new CompoundTerm(":-", new[] { BuildHelperHead(), clause1Body }),
            innerGoal.Position));

        // Clause 2: '$neg_N'(V1..).   (a bare fact)
        helpers.Add(new Clause(ClauseKind.Fact, BuildHelperHead(), innerGoal.Position));

        // The call site uses the same names, so the outer clause's
        // variables flow through to the helper.
        return BuildHelperHead();
    }

    private static void CollectNamedVars(Term t, List<string> order, HashSet<string> seen)
    {
        switch (t)
        {
            case VarTerm v when v.Name != "_":
                if (seen.Add(v.Name)) order.Add(v.Name);
                break;
            case CompoundTerm c:
                foreach (var arg in c.Args) CollectNamedVars(arg, order, seen);
                break;
        }
    }
}
