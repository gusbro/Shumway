using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;

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
/// rewrites <c>findall/3</c>; <c>bagof/3</c>, <c>setof/3</c>,
/// <c>forall/2</c>; and <c>catch/3</c> when their goal
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
                // ISO 7.8.8 cut transparency. A `!` inside a
                // `;` / `->` then/else BRANCH must commit the HOST clause, but
                // the branch lowers to a synthesised helper whose own clause
                // dispatch the `!` would otherwise cut instead (the bug: d(X)
                // :- X -> !, true. left d's second clause reachable). When the
                // body has such a branch cut, capture the host's barrier into
                // a fresh variable as the FIRST body goal (CallBuiltin doesn't
                // touch B0, so it still holds the caller's Call-site value =
                // the neck barrier) and thread it into the helpers, where the
                // branch `!` becomes '$call'(!, K) — the barrier cut.
                Term newBody;
                if (HasTransparentBranchCut(body))
                {
                    DiagCutfix(head, ruleTerm);
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

    /// <param name="cutK">The host clause's captured cut-barrier
    /// variable, threaded through cut-TRANSPARENT positions only (conjunction,
    /// disjunction branches, if-then-else then/else). Null when the host body
    /// has no branch cut, or in cut-OPAQUE positions (an if-then-else
    /// condition, \+, call/N, findall/bagof/setof/forall/catch goals) — there
    /// a branch `!` keeps today's helper-local scope.</param>
    /// <summary>Diag-build-only (<c>-p:ShumwayDiag=true</c> +
    /// <c>SHUMWAY_CUTFIX_DIAG=1</c>): names each clause the branch-cut
    /// transparency capture fires on. Stripped from normal builds.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private static void DiagCutfix(Term head, CompoundTerm ruleTerm)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_CUTFIX_DIAG") == "1")
            System.Console.Error.WriteLine(
                $"[cutfix] {(head is CompoundTerm hc ? hc.Functor + "/" + hc.Args.Length : head is AtomTerm ha ? ha.Name + "/0" : "?")} at {ruleTerm.Position}");
    }

    /// <summary>A copy of <paramref name="term"/> carrying <paramref name="position"/>.
    ///
    /// <para>The control-construct rewrites (catch, \+, once/ignore, the disjunction helper)
    /// each replace a body goal with a call to a freshly synthesised helper — and a fresh
    /// <see cref="CompoundTerm"/> / <see cref="AtomTerm"/> has no source position. Spliced into
    /// the caller's body that way, the helper call had no debug site of its own and the debug
    /// compiler mapped it to the PREVIOUS goal's line: stepping onto the construct left the
    /// caret where it was, so the step looked like it had done nothing (ADR-035). The
    /// replacement must carry the position of the goal it replaces.</para></summary>
    private static Term WithPosition(Term term, SourcePosition position) => term switch
    {
        CompoundTerm c => new CompoundTerm(c.Functor, c.Args) { Position = position },
        AtomTerm a => new AtomTerm(a.Name) { Position = position },
        _ => term,
    };

    private static Term TransformGoal(Term goal, ref int counter, List<Clause> helpers,
        string? cutK = null)
    {
        // ISO §7.6.2: converting a control construct to a body fails when any
        // goal position inside it holds a number, and the conversion happens
        // BEFORE the body runs — `\+ (fail,1)` raises, it does not succeed on
        // `fail`. Emitted as a runtime throw/1 (not a compile-time C# throw)
        // so the error stays inside whatever catch/3 region encloses it, and
        // done HERE so the culprit is the construct as WRITTEN, before cut
        // barriers and catch markers are spliced in.
        if (IsControlConstruct(goal) && HasNumberInGoalPosition(goal))
        {
            // For \+/not the CONVERSION applies to the argument, so the
            // culprit is the inner construct — \+ (true;1) raises
            // type_error(callable, (true;1)), the ISO (and Scryer) shape.
            Term culprit = goal is CompoundTerm { Functor: "\\+" or "not", Args.Length: 1 } neg
                ? neg.Args[0] : goal;
            return WithPosition(BodyConversionThrow(culprit), goal.Position);
        }

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
        if (goal is CompoundTerm itoOnly
            && itoOnly.Functor is "->" or "*->"    // ADR-037 — standalone *-> too
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
        // call/1 which raises the proper ISO error.
        if (goal is CompoundTerm ct
            && ct.Args.Length == 1
            && (ct.Functor == "\\+" || ct.Functor == "not")
            && InlinableGoal(ct.Args[0]))
        {
            return WithPosition(
                SynthesizeNegationHelper(ct.Args[0], ref counter, helpers), goal.Position);
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

        // once(G) / ignore(G) with a syntactically-callable G.
        // Snips `[! G !]` desugar to once/1, so this is the hot
        // Arity construct: without the rewrite every snip built G as a heap
        // term and meta-dispatched through the prelude's once/1 + call/1 at
        // runtime. Rewrite to the negation-helper shape instead:
        //   '$once_N'(Vars) :- G, !.            (once)
        //   '$ign_N'(Vars)  :- G, !.  '$ign_N'(Vars).   (ignore)
        // G compiles as real inline WAM inside the helper; a `!` inside G cuts
        // to the helper's clause — exactly once/1's opaque cut barrier; and
        // bindings flow out through the helper head's shared variables. A
        // non-callable G (var, integer, …) falls through to the runtime
        // builtin for the proper ISO error.
        if (goal is CompoundTerm onceCt
            && (onceCt.Functor == "once" || onceCt.Functor == "ignore")
            && onceCt.Args.Length == 1
            && InlinableGoal(onceCt.Args[0]))
        {
            return WithPosition(
                SynthesizeOnceHelper(
                    onceCt.Args[0], ref counter, helpers, ignoreMode: onceCt.Functor == "ignore"),
                goal.Position);
        }

        // findall(Template, Goal, List) with a syntactically-callable
        // Goal — rewrite to an in-engine collect loop:
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
        // instantiation_error / type_error(callable, _).
        if (goal is CompoundTerm fa
            && fa.Functor == "findall"
            && fa.Args.Length == 3
            && InlinableGoal(fa.Args[1]))
        {
            Term spliced = GoalHasLocalCut(fa.Args[1])
                ? new CompoundTerm("call", new[] { fa.Args[1] })
                : fa.Args[1];
            Term collectLoop = new CompoundTerm(",", new[]
            {
                (Term)new AtomTerm("$findall_push"),
                new CompoundTerm(",", new[]
                {
                    spliced,
                    new CompoundTerm(",", new[]
                    {
                        (Term)new CompoundTerm("$findall_record_s", new[] { fa.Args[0] }),
                        new AtomTerm("fail"),
                    }),
                }),
            });
            Term rewritten = WithResultListCheck(fa.Args[2],
                new CompoundTerm(";", new[]
                {
                    collectLoop,
                    (Term)new CompoundTerm("$findall_collect", new[] { fa.Args[2] }),
                }), goal.Position);
            // ADR-035 — name the collect-loop's $disj helper for the meta-predicate
            // it stands for, so the debugger shows/stops on it as findall/3 (a real
            // user goal) rather than a transparent user ';'.
            _nextHelperKind = "findall";
            return TransformGoal(rewritten, ref counter, helpers);
        }

        // bagof/3 and setof/3 with a callable (non-variable) Goal — rewrite
        // to an in-engine collect loop that groups solutions by the goal's
        // witness variables. See RewriteBagof and the class
        // remarks. A bare-variable Goal falls through to the runtime builtin.
        if (goal is CompoundTerm bs
            && (bs.Functor == "bagof" || bs.Functor == "setof")
            && bs.Args.Length == 3
            && InlinableGoal(bs.Args[1]))
        {
            Term rewritten = WithResultListCheck(bs.Args[2], RewriteBagof(
                bs.Functor, bs.Args[0], bs.Args[1], bs.Args[2], ref counter),
                goal.Position);
            // ADR-035 — the inner collect-loop ';' is bagof/setof, not a user ';'.
            _nextHelperKind = bs.Functor;
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
            && InlinableGoal(fl.Args[0])
            && InlinableGoal(fl.Args[1]))
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
            // ADR-035 — the outer '\+' IS the forall/2 goal (its inner '\+ Action'
            // stays a plain $neg). Name it so the debugger shows/stops on forall/2.
            _nextHelperKind = "forall";
            return TransformGoal(rewritten, ref counter, helpers);
        }

        // catch(Goal, Catcher, Recovery) with a callable Goal and Recovery —
        // rewrite to an in-engine guarded call. See RewriteCatch.
        // A variable Goal or Recovery falls through to the runtime builtin.
        if (goal is CompoundTerm ca
            && ca.Functor == "catch"
            && ca.Args.Length == 3
            && InlinableGoal(ca.Args[0])
            && InlinableGoal(ca.Args[2]))
        {
            return WithPosition(
                RewriteCatch(ca.Args[0], ca.Args[1], ca.Args[2], ref counter, helpers),
                goal.Position);
        }

        // Static call/N rewrite. `call(Goal, X1, ..., Xn)`
        // where Goal is an atom or a non-control-construct compound rewrites
        // to a direct goal with the extra args appended. The compiler then
        // emits a Call / Execute to the resolved functor instead of
        // `CallBuiltin call/N`, dropping the dispatcher overhead AND making
        // the predicate Tier-1 IL eligible (the call/$call gate
        // skips it).
        //
        // Skipped when:
        //   - Goal is a variable — genuine runtime meta-call, needs the
        //     dispatcher.
        //   - Goal is a control construct (`,`/`;`/`->`/`*->`/`\+`/`not`/
        //     `!`/`catch`/`throw`) — the bytecode interpreter's
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
            && !IsMqualGoal(first)
            && IsStaticallyExtendable(first))
        {
            int extra = ct2.Args.Length - 1;
            if (first is AtomTerm a0)
            {
                Term direct = extra == 0
                    ? (Term)a0
                    : new CompoundTerm(a0.Name, ct2.Args.AsSpan(1).ToArray())
                        { Position = goal.Position };
                if (!NeedsRuntimeBodyConversion(direct, ct2))
                    return TransformGoal(direct, ref counter, helpers);
            }
            else if (first is CompoundTerm c0)
            {
                Term[] combined = new Term[c0.Args.Length + extra];
                System.Array.Copy(c0.Args, 0, combined, 0, c0.Args.Length);
                for (int i = 0; i < extra; i++)
                    combined[c0.Args.Length + i] = ct2.Args[1 + i];
                Term direct = extra == 0
                    ? (Term)c0
                    : new CompoundTerm(c0.Functor, combined) { Position = goal.Position };
                if (!NeedsRuntimeBodyConversion(direct, ct2))
                    return TransformGoal(direct, ref counter, helpers);
            }
        }

        // Disjunction (A ; B) and if-then-else (A -> B ; C).
        if (goal is CompoundTerm disj && disj.Functor == ";" && disj.Args.Length == 2)
        {
            // ADR-025 — when the inline lowering is enabled and every part is a
            // plain conjunction (no cuts / nested control / meta-goals), LEAVE
            // the construct intact: ClauseCompiler emits it in the host clause
            // (get_level; try_me_else ELSE; C; cut; T; jump END; ELSE: trust_me;
            // E) instead of a synthesized 2-clause helper reached by a Call.
            // Parts are plain by eligibility, so there is nothing to transform
            // inside them.
            // ADR-037: a soft-cut disjunction ALWAYS takes the inline path when
            // eligible — soft cut has no synthesized-helper form, its lowering
            // IS the inline get_level_b/soft_cut. So it inlines regardless of the
            // general (default-OFF) inline-ITE flag.
            if ((InlineIteEnabled || Shumway.Compiler.InlineIte.IsSoftCut(disj))
                && Shumway.Compiler.InlineIte.IsEligible(disj))
            {
                // Inlined, so no helper is synthesised here — drop any pending
                // aggregation kind rather than leak it onto a later ;/\+ (ADR-035).
                _nextHelperKind = null;
                return disj;
            }

            // Otherwise: a two-clause helper that the standard try_me_else /
            // trust_me dispatch then handles.
            return WithPosition(
                SynthesizeDisjunctionHelper(disj.Args[0], disj.Args[1], ref counter, helpers, cutK),
                goal.Position);
        }

        return goal;
    }

    /// <summary>Synthesized-helper NAMING context. The old per-Apply
    /// counter restarted at zero on every transform run, so a QUERY stub's
    /// synthesized <c>$disj_1</c> (e.g. a findall collect loop) could collide —
    /// same module mangling, same arity — with a CONSULTED clause's
    /// <c>$disj_1</c>: the query-region definition shadowed the consulted helper
    /// and the caller executed the WRONG body (surfaced as an
    /// instantiation_error inside <c>findall</c> over an if-then-else predicate;
    /// latent for a long time).
    ///
    /// <para>The fix is SCOPED naming, not a process-global sequence (that was
    /// tried first and mints unbounded fresh atoms — one per helper per query —
    /// growing the functor table past the resume-marker fid cap mid-suite):
    /// <list type="bullet">
    /// <item><see cref="HelperIdProvider"/> — consult/assert paths pass the
    /// ENGINE's monotonic sequence, so two consults into the same module never
    /// reuse a name; different engines may reuse names (their programs are
    /// separate), keeping the atom space bounded.</item>
    /// <item><see cref="HelperPrefix"/> — the query-stub path passes a reserved
    /// <c>$q</c> prefix with the per-Apply counter: query helper names are
    /// REUSED query-to-query (bounded atoms) and can never collide with
    /// consult-time names.</item>
    /// </list>
    /// Both default to the old per-Apply behavior for standalone tooling
    /// (disassembler, ShmoCompiler — where module mangling isolates names).</para></summary>
    [ThreadStatic] private static Func<int>? _helperIdProvider;
    [ThreadStatic] private static string? _helperPrefix;
    public static Func<int>? HelperIdProvider
    {
        get => _helperIdProvider;
        set => _helperIdProvider = value;
    }
    public static string? HelperPrefix
    {
        get => _helperPrefix;
        set => _helperPrefix = value;
    }

    /// <summary>ADR-035 (Camino B) — the meta-predicate a synthesised
    /// disjunction / negation helper actually STANDS FOR. findall/bagof/setof
    /// lower to a <c>;</c> collect loop and forall to a <c>\+</c>, all of which
    /// otherwise become an indistinguishable <c>$disj_N</c> / <c>$neg_N</c> —
    /// the same helpers a user-written <c>;</c> / <c>\+</c> produces. For the
    /// debugger those user constructs are TRANSPARENT control flow, but a
    /// findall IS a goal the user wrote and must stop / show as <c>findall/3</c>.
    /// So the rewrite sets this to the meta-predicate's kind right before it
    /// TransformGoal's the construct, and <see cref="HelperName"/> stamps it onto
    /// the OUTERMOST synthesised helper (the first one built — its HelperName runs
    /// before the recursion into the goal), then clears it so nested user
    /// constructs keep their plain <c>disj</c>/<c>neg</c> kind. Debug-recognised
    /// via <see cref="PrologEngine.DebugConstructName"/>; codegen-neutral bar the
    /// helper's atom name (pre-release, no format concern).</summary>
    [ThreadStatic] private static string? _nextHelperKind;
    public static string? NextHelperKind
    {
        get => _nextHelperKind;
        set => _nextHelperKind = value;
    }

    private static string HelperName(string kind, ref int counter)
    {
        // The aggregation rewrites tag only their OUTER ;/\+ — consumed once, so
        // the disjunction/negation helper that carries the meta-predicate's line
        // is named for it, and everything synthesised afterwards is plain again.
        if ((kind == "disj" || kind == "neg") && _nextHelperKind is { } k)
        {
            _nextHelperKind = null;
            kind = k;
        }
        int id = _helperIdProvider is { } p ? p() : ++counter;
        return $"{_helperPrefix}${kind}_{id}";
    }

    /// <summary>ADR-025 — per-thread toggle for the inline if-then-else lowering.
    /// [ThreadStatic] rather than a parameter because TransformGoal recursion has
    /// ~15 sites; a consult runs synchronously on one thread, and concurrent
    /// consults on other engines each see their own slot. Set (and restored) by
    /// <see cref="ClausePipeline.Apply"/>.</summary>
    [ThreadStatic] private static bool _inlineIteEnabled;
    public static bool InlineIteEnabled
    {
        get => _inlineIteEnabled;
        set => _inlineIteEnabled = value;
    }

    /// <summary>Does <paramref name="body"/> contain a <c>!</c> in a
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
                => (c.Args[0] is CompoundTerm { Functor: "->" or "*->", Args.Length: 2 } ite
                        ? InsideBranch(ite.Args[1])          // then (cond is opaque)
                        : InsideBranch(c.Args[0]))
                   || InsideBranch(c.Args[1]),
            CompoundTerm { Functor: "->" or "*->", Args.Length: 2 } c
                => InsideBranch(c.Args[1]),                  // then (cond is opaque)
            _ => false,
        };
        // At the TOP level of the body we are not inside a branch yet: descend
        // through conjunction; a ;/->/*-> here means its branches are branch
        // positions (handled by InsideBranch).
        return body switch
        {
            CompoundTerm { Functor: ",", Args.Length: 2 } c
                => HasTransparentBranchCut(c.Args[0]) || HasTransparentBranchCut(c.Args[1]),
            CompoundTerm { Functor: ";", Args.Length: 2 } c
                => (c.Args[0] is CompoundTerm { Functor: "->" or "*->", Args.Length: 2 } ite
                        ? InsideBranch(ite.Args[1])
                        : InsideBranch(c.Args[0]))
                   || InsideBranch(c.Args[1]),
            CompoundTerm { Functor: "->" or "*->", Args.Length: 2 } c
                => InsideBranch(c.Args[1]),
            _ => false,
        };
    }

    /// <summary>Rewrites every cut-transparent <c>!</c> in a branch term into
    /// <c>'$call'(!, K)</c> (the barrier cut). Traverses the SAME transparent
    /// positions as <see cref="HasTransparentBranchCut"/>: conjunction, both arms
    /// of a nested <c>;</c>, and the then of a nested <c>-&gt;</c> — but NOT a
    /// condition, <c>\+</c>, or meta-goal argument (cut-opaque, left untouched).
    ///
    /// <para>Descending into nested <c>;</c>/<c>-&gt;</c> is required for the
    /// barrier variable K to appear in the ENCLOSING helper's free variables (a
    /// nested <c>!</c> left as a bare <c>!</c> would be invisible to that helper's
    /// free-var collection, so K would not thread down and the inner helper would
    /// read a garbage barrier). The recursive transform of the rewritten term sees
    /// <c>'$call'(!, K)</c>, not a bare <c>!</c>, so it does not re-wrap it.</para></summary>
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
                return Rebuild2(c, ",",
                    ReplaceTransparentCuts(c.Args[0], cutK),
                    ReplaceTransparentCuts(c.Args[1], cutK));
            case CompoundTerm { Functor: ";", Args.Length: 2 } c:
            {
                // Left arm: a ( Cond -> Then ) / ( Cond *-> Then ) keeps Cond
                // opaque, Then transparent.
                Term newLeft = c.Args[0] is CompoundTerm { Functor: "->" or "*->", Args.Length: 2 } ite
                    ? Rebuild2(ite, ite.Functor, ite.Args[0], ReplaceTransparentCuts(ite.Args[1], cutK))
                    : ReplaceTransparentCuts(c.Args[0], cutK);
                return Rebuild2(c, ";", newLeft, ReplaceTransparentCuts(c.Args[1], cutK));
            }
            case CompoundTerm { Functor: "->" or "*->", Args.Length: 2 } c:
                // Standalone if-then / soft-cut: then transparent, cond opaque.
                return Rebuild2(c, c.Functor, c.Args[0], ReplaceTransparentCuts(c.Args[1], cutK));
            default:
                return branch;
        }
    }

    /// <summary>Rebuilds a 2-arg compound only if an argument actually changed,
    /// preserving the term's <see cref="Term.Position"/> and letting callers rely
    /// on reference identity when nothing was rewritten.</summary>
    private static Term Rebuild2(CompoundTerm original, string functor, Term a0, Term a1)
        => ReferenceEquals(a0, original.Args[0]) && ReferenceEquals(a1, original.Args[1])
            ? original
            : new CompoundTerm(functor, new[] { a0, a1 }) { Position = original.Position };

    /// <summary>True iff <paramref name="goal"/> is an atom / compound that
    /// the static call/N rewrite is allowed to extend. Excludes
    /// the control constructs whose <c>$call_*</c> routing in the bytecode
    /// interpreter gives a distinct cut-barrier semantics from
    /// the bare equivalent — rewriting <c>call(!)</c> to <c>!</c> would
    /// silently change the cut scope. The exclude set is the same one
    /// <c>DispatchCall</c> intercepts after functor lookup.</summary>
    /// <summary><c>'$mqual'(Module, Goal)</c> — a runtime-variable meta-goal
    /// tagged with its meta-caller's module by ModuleRewrite. It is an OPAQUE
    /// runtime marker: this transform must never inline it (unwrapping /
    /// module-relative resolution happens at the live-engine dispatch). Re-running
    /// the pipeline over an already-tagged clause (the prelude bake, --exe) would
    /// otherwise splice <c>$mqual</c> in as a real body goal → Call $mqual/2 →
    /// existence_error.</summary>
    private static bool IsMqualGoal(Term t) =>
        t is CompoundTerm c && c.Functor == "$mqual" && c.Args.Length == 2;

    /// <summary>A goal this transform may inline: syntactically callable AND not
    /// the opaque <c>$mqual</c> marker.</summary>
    /// <summary>A <c>!</c> anywhere cut-transparent in a findall/bagof
    /// GOAL argument (top level, <c>,</c>-chain, <c>;</c> arms, <c>-&gt;</c>
    /// thens). Splicing such a goal into the collect loop would let the cut
    /// reach the DRIVER's disjunction and kill the collect alternative —
    /// wrap it in call/1 instead so the cut stays local (§7.8.3).</summary>
    private static bool GoalHasLocalCut(Term t) => t switch
    {
        AtomTerm { Name: "!" } => true,
        CompoundTerm { Functor: "," or ";", Args.Length: 2 } c
            => GoalHasLocalCut(c.Args[0]) || GoalHasLocalCut(c.Args[1]),
        CompoundTerm { Functor: "->" or "*->", Args.Length: 2 } c
            => GoalHasLocalCut(c.Args[1]),
        _ => false,
    };

    private static bool InlinableGoal(Term t) =>
        (t is AtomTerm || t is CompoundTerm) && !IsMqualGoal(t);

    /// <summary>The closure of a <c>call/N</c> is checked for extendability
    /// as given, so <c>call(',', A, B)</c> passes as the atom <c>','</c>/0
    /// and the BUILT goal is a control construct. Inlining that skips the
    /// ISO §7.6.2 body conversion, so <c>call(',', fail, X)</c> with X = 3
    /// would fail on <c>fail</c> instead of raising
    /// <c>type_error(callable, (fail,3))</c>.
    /// <para>Only the goals that can actually hit the conversion are handed
    /// to the runtime dispatcher: an appended argument that is a variable or
    /// a number. Everything else keeps the inline form, which is both faster
    /// and the one that gives a metacalled <c>!</c> its own cut barrier.</para>
    /// </summary>
    /// <summary>§8.10: the collected-solutions argument of findall/bagof/setof
    /// has to be a partial list, checked BEFORE the goal runs — the inline
    /// rewrites bypass the prelude clause that would otherwise do it.</summary>
    private static Term WithResultListCheck(
        Term resultArg, Term body, Shumway.Compiler.Lexer.SourcePosition pos)
    {
        if (resultArg is VarTerm) return WithPosition(body, pos);
        return new CompoundTerm(",", new[]
        {
            (Term)new CompoundTerm("$check_partial_list", new[] { resultArg }),
            body,
        }) { Position = pos };
    }

    private static bool NeedsRuntimeBodyConversion(Term direct, CompoundTerm callGoal)
    {
        if (!IsControlConstruct(direct)) return false;
        for (int i = 1; i < callGoal.Args.Length; i++)
            if (callGoal.Args[i] is VarTerm or IntTerm or FloatTerm
                    or BigIntTerm or RationalTerm)
                return true;
        return false;
    }

    /// <summary>Descends the control-construct skeleton only: a number
    /// nested inside an ordinary goal's arguments (`p(1)`) is data, one in
    /// goal position (`(fail,1)`) is not convertible.</summary>
    private static bool HasNumberInGoalPosition(Term t)
    {
        if (t is CompoundTerm { Functor: ":" or "$mqual", Args.Length: 2 } q)
            t = q.Args[1];
        if (t is IntTerm or FloatTerm or BigIntTerm or RationalTerm) return true;
        if (t is CompoundTerm { Functor: "," or ";" or "->" or "*->", Args.Length: 2 } c)
            return HasNumberInGoalPosition(c.Args[0])
                || HasNumberInGoalPosition(c.Args[1]);
        if (t is CompoundTerm { Functor: "\\+" or "not", Args.Length: 1 } n)
            return HasNumberInGoalPosition(n.Args[0]);
        return false;
    }

    private static Term BodyConversionThrow(Term culprit) =>
        new CompoundTerm("throw", new Term[]
        {
            new CompoundTerm("error", new Term[]
            {
                new CompoundTerm("type_error",
                    new Term[] { new AtomTerm("callable"), culprit }),
                new VarTerm("_"),
            }),
        });

    private static bool IsControlConstruct(Term t) => t switch
    {
        CompoundTerm { Functor: "," or ";" or "->" or "*->", Args.Length: 2 } => true,
        CompoundTerm { Functor: "\\+" or "not" or "throw", Args.Length: 1 } => true,
        CompoundTerm { Functor: "catch", Args.Length: 3 } => true,
        AtomTerm { Name: "!" } => true,
        _ => false,
    };

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
            (":", 2) => false,      // module-qualified goal `M:G`. Extending it
                                    // statically would build (:)/N; the runtime
                                    // dispatcher distributes the extra args into
                                    // G inside the module qualification instead.
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
        string helperName = HelperName("disj", ref counter);

        // Branch cuts become '$call'(!, K) BEFORE free-variable
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

        // ADR-037 — soft cut: ( A *-> B ; C ) with A/B/C too rich for the inline
        // form (a cut in B/C, nested control, …). Two clauses, but the commit is a
        // SOFT cut: clause 1 captures the helper's Else-alternative CP with
        // '$choice_level'(K) at entry, runs A, then '$soft_cut'(K) neutralises that
        // ONE choice point — so C is pruned once A succeeds while A's own choice
        // points survive (B runs per solution of A). A cut in B/C stays transparent
        // to the host via the threaded cutK, exactly as in the -> case.
        if (branchLeft is CompoundTerm sc && sc.Functor == "*->" && sc.Args.Length == 2)
        {
            Term scCond = TransformGoal(sc.Args[0], ref counter, helpers, cutK: null);
            Term scThen = TransformGoal(sc.Args[1], ref counter, helpers, cutK);
            Term recursedRightSc = TransformGoal(right, ref counter, helpers, cutK);
            string kVar = $"$SoftB_{counter++}";
            // Clause 1: '$disj_N'(...) :- '$choice_level'(K), A, '$soft_cut'(K), B.
            Term scClause1 = new CompoundTerm(",", new[]
            {
                (Term)new CompoundTerm("$choice_level", new Term[] { new VarTerm(kVar) }),
                new CompoundTerm(",", new[]
                {
                    scCond,
                    new CompoundTerm(",", new[]
                    {
                        (Term)new CompoundTerm("$soft_cut", new Term[] { new VarTerm(kVar) }),
                        scThen,
                    })
                })
            });
            helpers.Add(new Clause(
                ClauseKind.Rule,
                new CompoundTerm(":-", new[] { BuildHelperHead(), scClause1 }),
                left.Position));
            // Clause 2: '$disj_N'(...) :- C.
            helpers.Add(new Clause(
                ClauseKind.Rule,
                new CompoundTerm(":-", new[] { BuildHelperHead(), recursedRightSc }),
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

    /// <summary>Synthesizes the once/ignore helper (see the
    /// TransformGoal case): <c>'$once_N'(V..) :- G, !.</c>, plus a bare-fact
    /// second clause for ignore. Mirrors <see cref="SynthesizeNegationHelper"/>:
    /// the free named variables flow through the helper head, so bindings G
    /// makes are visible to the caller after the commit.</summary>
    private static Term SynthesizeOnceHelper(
        Term innerGoal, ref int counter, List<Clause> helpers, bool ignoreMode)
    {
        string helperName = HelperName(ignoreMode ? "ign" : "once", ref counter);

        var freeVars = new List<string>();
        var seen = new HashSet<string>();
        CollectNamedVars(innerGoal, freeVars, seen);

        // Recurse into the inner goal — nested control constructs inside the
        // once'd goal are transformed before becoming the helper's body. No
        // cutK: once/1 is an opaque cut barrier.
        innerGoal = TransformGoal(innerGoal, ref counter, helpers);

        Term BuildHelperHead() => freeVars.Count == 0
            ? (Term)new AtomTerm(helperName)
            : new CompoundTerm(helperName, freeVars.Select(n => (Term)new VarTerm(n)).ToArray());

        // Clause 1: '$once_N'(V1..) :- G, !.
        Term clause1Body = new CompoundTerm(",", new[]
        {
            innerGoal,
            (Term)new AtomTerm("!"),
        });
        helpers.Add(new Clause(
            ClauseKind.Rule,
            new CompoundTerm(":-", new[] { BuildHelperHead(), clause1Body }),
            innerGoal.Position));

        // ignore/1 also succeeds when G fails: a bare-fact second clause.
        if (ignoreMode)
            helpers.Add(new Clause(ClauseKind.Fact, BuildHelperHead(), innerGoal.Position));

        return BuildHelperHead();
    }

    private static Term SynthesizeNegationHelper(
        Term innerGoal, ref int counter, List<Clause> helpers)
    {
        string helperName = HelperName("neg", ref counter);

        var freeVars = new List<string>();
        var seen = new HashSet<string>();
        CollectNamedVars(innerGoal, freeVars, seen);

        // §7.8.9: `\+ G` is `(call(G) -> fail ; true)`, so a cut inside G is
        // LOCAL to it. Spliced bare into the helper's first clause it would
        // cut the helper itself and take the second clause — the one that
        // makes the negation succeed — with it, so `\+ ((!, fail))` failed.
        if (GoalHasLocalCut(innerGoal))
            innerGoal = new CompoundTerm("call", new[] { innerGoal })
                { Position = innerGoal.Position };

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
    /// into the in-engine form
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

        // §7.6.2 on the goal the ^ wrappers were hiding: `setof(X, X^(true;4), L)`
        // must raise type_error(callable, (true;4)) before anything runs.
        if (IsControlConstruct(goal) && HasNumberInGoalPosition(goal))
            goal = BodyConversionThrow(goal);
        else if (goal is IntTerm or FloatTerm or BigIntTerm or RationalTerm)
            goal = BodyConversionThrow(goal);

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
        Term splicedGoal = GoalHasLocalCut(goal)
            ? new CompoundTerm("call", new[] { goal })
            : goal;
        Term collectLoop = new CompoundTerm(",", new[]
        {
            (Term)new AtomTerm("$findall_push"),
            new CompoundTerm(",", new[]
            {
                splicedGoal,
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
        }) { Position = position };
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
    /// in-engine form: a call to a synthesised goal helper
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
        // catchgoal/catchrec share one id.
        string goalName = HelperName("catchgoal", ref counter);
        string recName = goalName.Replace("$catchgoal_", "$catchrec_");

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
