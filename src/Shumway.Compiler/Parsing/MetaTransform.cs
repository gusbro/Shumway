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
///
/// <para><b>All-solutions and control predicates.</b> The same pass also
/// rewrites <c>findall/3</c> (chunk 83); <c>bagof/3</c>, <c>setof/3</c>,
/// <c>forall/2</c> (chunk 84); and <c>catch/3</c> (chunk 85) when their goal
/// argument is callable at compile time, so they run in the live engine
/// instead of an isolated sub-engine. <c>forall(C, A)</c> becomes
/// <c>\+ (C, \+ A)</c>; the all-solutions predicates become a fail-driven
/// collect loop over a per-engine solution buffer. <c>bagof/3</c> and
/// <c>setof/3</c> additionally pair each solution with a witness term — the
/// variables free in the goal but not in the template and not bound by a
/// <c>^/2</c> wrapper — and backtrack the grouped result over
/// <c>member/2</c>. <c>catch/3</c> becomes a guarded goal helper plus a
/// recovery helper, bracketed by <c>'$catch_begin'</c> / <c>'$catch_end'</c>
/// so the engine's throw handler can roll back to the catch and run the
/// recovery. A goal still a variable at compile time is left for the
/// runtime builtin.</para>
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
                // Chunk 408 — ISO 7.8.8 cut transparency. A `!` inside a
                // `;` / `->` then/else BRANCH must commit the HOST clause, but
                // the branch lowers to a synthesised helper whose own clause
                // dispatch the `!` would otherwise cut instead (the bug: d(X)
                // :- X -> !, true. left d's second clause reachable). When the
                // body has such a branch cut, capture the host's barrier into
                // a fresh variable as the FIRST body goal (CallBuiltin doesn't
                // touch B0, so it still holds the caller's Call-site value =
                // the neck barrier) and thread it into the helpers, where the
                // branch `!` becomes '$call'(!, K) — the chunk-88 barrier cut.
                Term newBody;
                if (HasTransparentBranchCut(body))
                {
                    if (System.Environment.GetEnvironmentVariable("SHUMWAY_CUTFIX_DIAG") == "1")
                        System.Console.Error.WriteLine(
                            $"[cutfix] {(head is CompoundTerm hc ? hc.Functor + "/" + hc.Args.Length : head is AtomTerm ha ? ha.Name + "/0" : "?")} at {ruleTerm.Position}");
                    counter++;
                    string cutK = $"$CutB_{counter}";
                    Term transformed = TransformGoal(body, ref counter, helpers, cutK);
                    newBody = new CompoundTerm(",", new[]
                    {
                        (Term)new CompoundTerm("$get_cut_barrier",
                            new Term[] { new VarTerm(cutK) }),
                        transformed,
                    }) { Position = ruleTerm.Position };
                }
                else
                {
                    newBody = TransformGoal(body, ref counter, helpers, cutK: null);
                }
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

    /// <param name="cutK">Chunk 408 — the host clause's captured cut-barrier
    /// variable, threaded through cut-TRANSPARENT positions only (conjunction,
    /// disjunction branches, if-then-else then/else). Null when the host body
    /// has no branch cut, or in cut-OPAQUE positions (an if-then-else
    /// condition, \+, call/N, findall/bagof/setof/forall/catch goals) — there
    /// a branch `!` keeps today's helper-local scope.</param>
    private static Term TransformGoal(Term goal, ref int counter, List<Clause> helpers,
        string? cutK = null)
    {
        // Conjunction: recurse into both halves.
        if (goal is CompoundTerm { Functor: "," } conj && conj.Args.Length == 2)
        {
            Term lhs = TransformGoal(conj.Args[0], ref counter, helpers, cutK);
            Term rhs = TransformGoal(conj.Args[1], ref counter, helpers, cutK);
            return new CompoundTerm(",", new[] { lhs, rhs }) { Position = goal.Position };
        }

        // Standalone if-then `(A -> B)` without an else branch. ISO
        // §7.8.7 defines it as equivalent to `(A -> B ; fail)`, so
        // rewrite it that way and recurse — the disjunction case
        // below synthesises the helper. Without this rewrite the
        // compiler treats `->/2` as a plain procedure and emits a
        // call to it, which raises existence_error/2 at runtime
        // (the engine doesn't ship `->`/2 as a builtin).
        if (goal is CompoundTerm itoOnly && itoOnly.Functor == "->"
            && itoOnly.Args.Length == 2)
        {
            Term withFallback = new CompoundTerm(";", new[]
            {
                (Term)itoOnly,
                new AtomTerm("fail"),
            }) { Position = goal.Position };
            return TransformGoal(withFallback, ref counter, helpers, cutK);
        }

        // \+ G  or  not(G)  with a syntactically-callable inner goal —
        // synthesise the helper and emit a call to it. A non-callable
        // inner goal (var, integer, …) falls through to a runtime
        // call/1 which raises the proper ISO error (chunk 136).
        if (goal is CompoundTerm ct
            && ct.Args.Length == 1
            && (ct.Functor == "\\+" || ct.Functor == "not")
            && (ct.Args[0] is AtomTerm || ct.Args[0] is CompoundTerm))
        {
            return SynthesizeNegationHelper(ct.Args[0], ref counter, helpers);
        }
        // \+ G with a var / non-callable G — rewrite to
        // ( call(G) -> fail ; true ). call/1's runtime dispatch
        // performs the ISO error checks (var → instantiation_error,
        // non-callable → type_error(callable, _)). Recurse so the
        // resulting disjunction goes through its own helper synthesis.
        if (goal is CompoundTerm ctf
            && ctf.Args.Length == 1
            && (ctf.Functor == "\\+" || ctf.Functor == "not"))
        {
            Term rewritten = new CompoundTerm(";", new Term[]
            {
                new CompoundTerm("->", new Term[]
                {
                    new CompoundTerm("call", new Term[] { ctf.Args[0] }),
                    new AtomTerm("fail"),
                }),
                new AtomTerm("true"),
            }) { Position = goal.Position };
            return TransformGoal(rewritten, ref counter, helpers);
        }

        // findall(Template, Goal, List) with a syntactically-callable
        // Goal — rewrite to an in-engine collect loop (chunk 83):
        //   ( '$findall_push', Goal, '$findall_record'(Template), fail
        //   ; '$findall_collect'(List) )
        // Goal is spliced in as a body goal, so it compiles inline with
        // real choice points and runs in the live engine — no sub-engine,
        // and side effects (assertz) persist. The fail drives the
        // backtracking that enumerates every solution; the disjunction
        // then routes to '$findall_collect'. A Goal that isn't
        // syntactically callable (a var, an integer, a string, …) is
        // left alone — it falls through to the runtime findall/3
        // builtin, which raises the appropriate ISO
        // instantiation_error / type_error(callable, _) (chunk 135).
        if (goal is CompoundTerm fa
            && fa.Functor == "findall"
            && fa.Args.Length == 3
            && (fa.Args[1] is AtomTerm || fa.Args[1] is CompoundTerm))
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

        // bagof/3 and setof/3 with a callable (non-variable) Goal — rewrite
        // to an in-engine collect loop that groups solutions by the goal's
        // witness variables (chunk 84). See RewriteBagof and the class
        // remarks. A bare-variable Goal falls through to the runtime builtin.
        if (goal is CompoundTerm bs
            && (bs.Functor == "bagof" || bs.Functor == "setof")
            && bs.Args.Length == 3
            && (bs.Args[1] is AtomTerm || bs.Args[1] is CompoundTerm))
        {
            Term rewritten = RewriteBagof(
                bs.Functor, bs.Args[0], bs.Args[1], bs.Args[2], ref counter);
            return TransformGoal(rewritten, ref counter, helpers);
        }

        // forall(Cond, Action) with callable arguments — the textbook
        // \+ (Cond, \+ Action). Cond and Action are spliced as ordinary body
        // goals, so they enumerate with real choice points in the live
        // engine; the negation pair makes forall succeed exactly when no
        // solution of Cond falsifies Action.
        if (goal is CompoundTerm fl
            && fl.Functor == "forall"
            && fl.Args.Length == 2
            && fl.Args[0] is not VarTerm
            && fl.Args[1] is not VarTerm)
        {
            Term inner = new CompoundTerm(",", new[]
            {
                fl.Args[0],
                new CompoundTerm("\\+", new[] { fl.Args[1] }),
            });
            Term rewritten = new CompoundTerm("\\+", new[] { inner })
            {
                Position = goal.Position,
            };
            return TransformGoal(rewritten, ref counter, helpers);
        }

        // catch(Goal, Catcher, Recovery) with a callable Goal and Recovery —
        // rewrite to an in-engine guarded call (chunk 85). See RewriteCatch.
        // A variable Goal or Recovery falls through to the runtime builtin.
        if (goal is CompoundTerm ca
            && ca.Functor == "catch"
            && ca.Args.Length == 3
            && ca.Args[0] is not VarTerm
            && ca.Args[2] is not VarTerm)
        {
            return RewriteCatch(ca.Args[0], ca.Args[1], ca.Args[2], ref counter, helpers);
        }

        // Phase 19 chunk 205 — static call/N rewrite. `call(Goal, X1, ..., Xn)`
        // where Goal is an atom or a non-control-construct compound rewrites
        // to a direct goal with the extra args appended. The compiler then
        // emits a Call / Execute to the resolved functor instead of
        // `CallBuiltin call/N`, dropping the dispatcher overhead AND making
        // the predicate Tier-1 IL eligible (the chunk-201 call/$call gate
        // skips it).
        //
        // Skipped when:
        //   - Goal is a variable — genuine runtime meta-call, needs the
        //     dispatcher.
        //   - Goal is a control construct (`,`/`;`/`->`/`*->`/`\+`/`not`/
        //     `!`/`catch`/`throw`) — the bytecode interpreter's chunk-88
        //     cut-barrier threading routes these through `$call_*` helpers
        //     with a distinct semantics for `!` (commits to the call's
        //     barrier, not the enclosing predicate's). The rewrite would
        //     change that.
        //   - Goal is itself `call/N` — re-enter the transform recursively
        //     after one unwrap, so `call(call(foo, A), B)` flattens cleanly.
        if (goal is CompoundTerm ct2
            && ct2.Functor == "call"
            && ct2.Args.Length >= 1
            && ct2.Args[0] is Term first
            && first is not VarTerm
            && IsStaticallyExtendable(first))
        {
            int extra = ct2.Args.Length - 1;
            if (first is AtomTerm a0)
            {
                Term direct = extra == 0
                    ? (Term)a0
                    : new CompoundTerm(a0.Name, ct2.Args.AsSpan(1).ToArray())
                        { Position = goal.Position };
                return TransformGoal(direct, ref counter, helpers);
            }
            if (first is CompoundTerm c0)
            {
                Term[] combined = new Term[c0.Args.Length + extra];
                System.Array.Copy(c0.Args, 0, combined, 0, c0.Args.Length);
                for (int i = 0; i < extra; i++)
                    combined[c0.Args.Length + i] = ct2.Args[1 + i];
                Term direct = extra == 0
                    ? (Term)c0
                    : new CompoundTerm(c0.Functor, combined) { Position = goal.Position };
                return TransformGoal(direct, ref counter, helpers);
            }
        }

        // Disjunction (A ; B) and if-then-else (A -> B ; C) — both compile
        // to a two-clause helper that the standard try_me_else / trust_me
        // dispatch then handles.
        if (goal is CompoundTerm disj && disj.Functor == ";" && disj.Args.Length == 2)
        {
            return SynthesizeDisjunctionHelper(disj.Args[0], disj.Args[1], ref counter, helpers, cutK);
        }

        return goal;
    }

    /// <summary>Chunk 408 — does <paramref name="body"/> contain a <c>!</c> in a
    /// cut-TRANSPARENT branch position (inside a <c>;</c> branch or an
    /// if-then-else then/else, possibly nested)? Such a body needs the host
    /// barrier captured and threaded. A top-level <c>!</c> (plain conjunction)
    /// compiles as a normal clause cut and does not count; cut-opaque positions
    /// (a condition, <c>\+</c>, meta-goal arguments) do not count.</summary>
    private static bool HasTransparentBranchCut(Term body)
    {
        static bool InsideBranch(Term t) => t switch
        {
            AtomTerm { Name: "!" } => true,
            CompoundTerm { Functor: ",", Args.Length: 2 } c
                => InsideBranch(c.Args[0]) || InsideBranch(c.Args[1]),
            CompoundTerm { Functor: ";", Args.Length: 2 } c
                => (c.Args[0] is CompoundTerm { Functor: "->", Args.Length: 2 } ite
                        ? InsideBranch(ite.Args[1])          // then (cond is opaque)
                        : InsideBranch(c.Args[0]))
                   || InsideBranch(c.Args[1]),
            CompoundTerm { Functor: "->", Args.Length: 2 } c
                => InsideBranch(c.Args[1]),                  // then (cond is opaque)
            _ => false,
        };
        // At the TOP level of the body we are not inside a branch yet: descend
        // through conjunction; a ;/-> here means its branches are branch
        // positions (handled by InsideBranch).
        return body switch
        {
            CompoundTerm { Functor: ",", Args.Length: 2 } c
                => HasTransparentBranchCut(c.Args[0]) || HasTransparentBranchCut(c.Args[1]),
            CompoundTerm { Functor: ";", Args.Length: 2 } c
                => (c.Args[0] is CompoundTerm { Functor: "->", Args.Length: 2 } ite
                        ? InsideBranch(ite.Args[1])
                        : InsideBranch(c.Args[0]))
                   || InsideBranch(c.Args[1]),
            CompoundTerm { Functor: "->", Args.Length: 2 } c
                => InsideBranch(c.Args[1]),
            _ => false,
        };
    }

    /// <summary>Chunk 408 — rewrites every cut-transparent <c>!</c> at the
    /// conjunction level of a branch term into <c>'$call'(!, K)</c> (the
    /// chunk-88 barrier cut). Nested <c>;</c>/<c>-&gt;</c> inside the branch
    /// are left for the recursive transform (their own helper synthesis
    /// replaces THEIR branch cuts with the same K); cut-opaque subterms are
    /// untouched.</summary>
    private static Term ReplaceTransparentCuts(Term branch, string cutK)
    {
        switch (branch)
        {
            case AtomTerm { Name: "!" }:
                return new CompoundTerm("$call", new Term[]
                {
                    new AtomTerm("!"),
                    new VarTerm(cutK),
                });
            case CompoundTerm { Functor: ",", Args.Length: 2 } c:
            {
                Term l = ReplaceTransparentCuts(c.Args[0], cutK);
                Term r = ReplaceTransparentCuts(c.Args[1], cutK);
                if (ReferenceEquals(l, c.Args[0]) && ReferenceEquals(r, c.Args[1]))
                    return branch;
                return new CompoundTerm(",", new[] { l, r }) { Position = c.Position };
            }
            default:
                return branch;
        }
    }

    /// <summary>True iff <paramref name="goal"/> is an atom / compound that
    /// the chunk-205 static call/N rewrite is allowed to extend. Excludes
    /// the control constructs whose <c>$call_*</c> routing in the bytecode
    /// interpreter (chunk 88) gives a distinct cut-barrier semantics from
    /// the bare equivalent — rewriting <c>call(!)</c> to <c>!</c> would
    /// silently change the cut scope. The exclude set is the same one
    /// <c>DispatchCall</c> intercepts after functor lookup.</summary>
    private static bool IsStaticallyExtendable(Term t)
    {
        string name;
        int arity;
        if (t is AtomTerm a) { name = a.Name; arity = 0; }
        else if (t is CompoundTerm c) { name = c.Functor; arity = c.Args.Length; }
        else return false;
        return (name, arity) switch
        {
            (",", 2) => false,
            (";", 2) => false,
            ("->", 2) => false,
            ("*->", 2) => false,
            ("\\+", 1) => false,
            ("not", 1) => false,
            ("!", 0) => false,
            ("catch", 3) => false,
            ("throw", 1) => false,
            ("call", _) => false,  // recursive call(call(...)) — let the
                                    // builtin runtime path handle it; one
                                    // unwrap per layer would still emit
                                    // CallBuiltin call/N anyway since the
                                    // first arg stays a `call/N` compound
                                    // until it's recursed past.
            _ => true,
        };
    }

    /// <summary>Rewrites <c>(A ; B)</c> and <c>(A -&gt; B ; C)</c> into a
    /// call to a freshly-synthesised two-clause helper. The classic
    /// Aït-Kaci translation: each branch becomes one clause of the helper,
    /// and the regular WAM choice-point dispatch makes the disjunction
    /// behave with the right backtracking semantics.</summary>
    private static Term SynthesizeDisjunctionHelper(
        Term left, Term right, ref int counter, List<Clause> helpers,
        string? cutK = null)
    {
        counter++;
        string helperName = $"$disj_{counter}";

        // Chunk 408 — branch cuts become '$call'(!, K) BEFORE free-variable
        // collection, so the captured-barrier variable K rides into the
        // helper's head like any other free variable. Branch positions only;
        // an if-then-else condition stays opaque (its cuts keep helper scope).
        Term branchLeft = left;
        if (cutK is not null)
        {
            branchLeft = left is CompoundTerm { Functor: "->", Args.Length: 2 } ite0
                ? new CompoundTerm("->", new[]
                  {
                      ite0.Args[0],
                      ReplaceTransparentCuts(ite0.Args[1], cutK),
                  }) { Position = ite0.Position }
                : ReplaceTransparentCuts(left, cutK);
            right = ReplaceTransparentCuts(right, cutK);
        }

        var freeVars = new List<string>();
        var seen = new HashSet<string>();
        CollectNamedVars(branchLeft, freeVars, seen);
        CollectNamedVars(right, freeVars, seen);

        Term BuildHelperHead() => freeVars.Count == 0
            ? (Term)new AtomTerm(helperName)
            : new CompoundTerm(helperName, freeVars.Select(n => (Term)new VarTerm(n)).ToArray());

        // If-then-else: (A -> B ; C) translates to two clauses with a
        // commit cut between A and B in the first clause. The cond
        // and then come from `left.Args[0]` / `left.Args[1]` —
        // recursing through TransformGoal on `left` itself would
        // bounce off the standalone `(A -> B)` rewrite above and
        // ping-pong infinitely.
        if (branchLeft is CompoundTerm ite && ite.Functor == "->" && ite.Args.Length == 2)
        {
            Term cond = TransformGoal(ite.Args[0], ref counter, helpers, cutK: null);
            Term then = TransformGoal(ite.Args[1], ref counter, helpers, cutK);
            Term recursedRightIte = TransformGoal(right, ref counter, helpers, cutK);
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
                new CompoundTerm(":-", new[] { BuildHelperHead(), recursedRightIte }),
                right.Position));
            return BuildHelperHead();
        }

        // Plain disjunction.
        Term recursedLeft = TransformGoal(branchLeft, ref counter, helpers, cutK);
        Term recursedRight = TransformGoal(right, ref counter, helpers, cutK);
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

    /// <summary>Rewrites <c>bagof(T, Goal, B)</c> / <c>setof(T, Goal, B)</c>
    /// into the chunk-84 in-engine form
    /// <code>
    ///   ( '$findall_push', Goal', '$findall_record'(Wt-T), fail
    ///   ; '$bagof_collect'(Groups) ),
    ///   member(Wt-B, Groups)
    /// </code>
    /// where <c>Wt</c> is the witness term — <c>'$w'(W1..Wk)</c> over the
    /// variables free in <c>Goal</c> but not in <c>T</c> and not bound by a
    /// <c>^/2</c> existential wrapper, or the atom <c>'$w'</c> when there are
    /// none. <c>Goal'</c> is <c>Goal</c> with its <c>^</c> wrappers removed
    /// and its anonymous variables named, since an anonymous variable not in
    /// <c>T</c> is a witness exactly like a named one.
    ///
    /// <para>The collect loop runs <c>Goal'</c> in the live engine and the
    /// trailing <c>fail</c> enumerates it; <c>'$bagof_collect'</c> groups the
    /// buffered <c>Wt-T</c> pairs by witness; <c>member/2</c> then backtracks
    /// over the groups, binding the witness variables and the result.</para></summary>
    private static Term RewriteBagof(
        string functor, Term template, Term goal, Term bag, ref int counter)
    {
        var position = goal.Position;

        // Strip ^/2 existential wrappers; collect the quantified variables.
        var existential = new HashSet<string>();
        while (goal is CompoundTerm caret && caret.Functor == "^" && caret.Args.Length == 2)
        {
            CollectNamedVars(caret.Args[0], new List<string>(), existential);
            goal = caret.Args[1];
        }

        // Name anonymous variables so they can be collected as witnesses.
        goal = NameAnonymousVars(goal, ref counter);

        // Witness = vars(goal) \ vars(template) \ existential, in
        // first-occurrence order. A variable local to a nested all-solutions
        // call (the template of an inner findall/bagof/setof) is counted here
        // too, but harmlessly: such a variable is unbound once the nested call
        // returns, so every solution's witness shares it as a free variable
        // and the canonical-form grouping folds those snapshots together.
        var templateVars = new HashSet<string>();
        CollectNamedVars(template, new List<string>(), templateVars);
        var goalVars = new List<string>();
        CollectNamedVars(goal, goalVars, new HashSet<string>());
        var witnessVars = new List<string>();
        foreach (string v in goalVars)
        {
            if (!templateVars.Contains(v) && !existential.Contains(v))
                witnessVars.Add(v);
        }

        Term Witness() => witnessVars.Count == 0
            ? new AtomTerm("$w")
            : new CompoundTerm("$w", witnessVars.Select(n => (Term)new VarTerm(n)).ToArray());

        var groups = new VarTerm("$BG" + counter++);
        string collector = functor == "setof" ? "$setof_collect" : "$bagof_collect";

        // '$findall_push', Goal', '$findall_record'(Wt-T), fail
        Term collectLoop = new CompoundTerm(",", new[]
        {
            (Term)new AtomTerm("$findall_push"),
            new CompoundTerm(",", new[]
            {
                goal,
                new CompoundTerm(",", new[]
                {
                    (Term)new CompoundTerm("$findall_record", new[]
                    {
                        (Term)new CompoundTerm("-", new[] { Witness(), template }),
                    }),
                    new AtomTerm("fail"),
                }),
            }),
        });

        // ( collectLoop ; '$<bag|set>of_collect'(Groups) )
        Term disjunction = new CompoundTerm(";", new[]
        {
            collectLoop,
            (Term)new CompoundTerm(collector, new Term[] { groups }),
        }) { Position = position };

        // ( disjunction , member(Wt-B, Groups) )
        return new CompoundTerm(",", new[]
        {
            disjunction,
            new CompoundTerm("member", new Term[]
            {
                new CompoundTerm("-", new[] { Witness(), bag }),
                groups,
            }),
        });
    }

    /// <summary>Returns a copy of <paramref name="term"/> with every anonymous
    /// variable (<c>_</c>) replaced by a freshly-named one. bagof/3 and
    /// setof/3 treat an anonymous variable in the goal as an ordinary witness
    /// variable, so it has to carry a name to be collected as one.</summary>
    private static Term NameAnonymousVars(Term term, ref int counter)
    {
        switch (term)
        {
            case VarTerm v when v.Name == "_":
                return new VarTerm("$A" + counter++);
            case CompoundTerm c:
                var args = new Term[c.Args.Length];
                for (int i = 0; i < c.Args.Length; i++)
                    args[i] = NameAnonymousVars(c.Args[i], ref counter);
                return new CompoundTerm(c.Functor, args) { Position = c.Position };
            default:
                return term;
        }
    }

    /// <summary>Rewrites <c>catch(Goal, Catcher, Recovery)</c> into the
    /// chunk-85 in-engine form: a call to a synthesised goal helper
    /// <code>
    ///   '$catchgoal_N'(AllVars) :-
    ///       '$catch_begin'(Catcher, '$catchrec_N'(RecVars)),
    ///       Goal', '$catch_end'.
    /// </code>
    /// plus a recovery helper <c>'$catchrec_N'(RecVars) :- Recovery'.</c>
    ///
    /// <para>Goal' is compiled inline in the goal helper, so it runs in the
    /// live engine with full backtracking. <c>'$catch_begin'</c> pushes a
    /// catch frame snapshotting the machine; on a matching <c>throw/1</c>
    /// the engine rolls back to the frame and runs the recovery helper.
    /// <c>'$catch_end'</c> deactivates the frame once the goal succeeds.
    /// The goal helper takes every variable of the whole <c>catch/3</c> so
    /// surrounding bindings flow in; the recovery helper takes only the
    /// recovery goal's variables.</para></summary>
    private static Term RewriteCatch(
        Term goal, Term catcher, Term recovery, ref int counter, List<Clause> helpers)
    {
        counter++;
        string goalName = $"$catchgoal_{counter}";
        string recName = $"$catchrec_{counter}";

        var allVars = new List<string>();
        var allSeen = new HashSet<string>();
        CollectNamedVars(goal, allVars, allSeen);
        CollectNamedVars(catcher, allVars, allSeen);
        CollectNamedVars(recovery, allVars, allSeen);
        var recVars = new List<string>();
        CollectNamedVars(recovery, recVars, new HashSet<string>());

        Term transformedGoal = TransformGoal(goal, ref counter, helpers);
        Term transformedRecovery = TransformGoal(recovery, ref counter, helpers);

        static Term Invoke(string name, List<string> vars) => vars.Count == 0
            ? new AtomTerm(name)
            : new CompoundTerm(name, vars.Select(n => (Term)new VarTerm(n)).ToArray());

        // '$catchgoal_N'(AllVars) :-
        //   '$catch_begin'(Catcher, '$catchrec_N'(RecVars)), Goal', '$catch_end'.
        Term goalBody = new CompoundTerm(",", new Term[]
        {
            new CompoundTerm("$catch_begin", new Term[]
            {
                catcher,
                Invoke(recName, recVars),
            }),
            new CompoundTerm(",", new Term[]
            {
                transformedGoal,
                new AtomTerm("$catch_end"),
            }),
        });
        helpers.Add(new Clause(
            ClauseKind.Rule,
            new CompoundTerm(":-", new Term[] { Invoke(goalName, allVars), goalBody }),
            goal.Position));

        // '$catchrec_N'(RecVars) :- Recovery'.
        helpers.Add(new Clause(
            ClauseKind.Rule,
            new CompoundTerm(":-", new Term[]
            {
                Invoke(recName, recVars),
                transformedRecovery,
            }),
            recovery.Position));

        return Invoke(goalName, allVars);
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
