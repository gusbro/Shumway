namespace Shumway.Embedding;

/// <summary>
/// Internal Prolog prelude that <see cref="PrologEngine"/> consults at
/// construction time. Lives in a private module so its predicates are
/// declared <c>:- public</c> from the prelude's perspective but never
/// collide with user code, and are not subject to module-aware mangling
/// the same way user-defined locals are.
///
/// <para>The prelude is the home for predicates that benefit from
/// Prolog-level backtracking: <c>member/2</c>, <c>clause/2</c>, and
/// <c>current_predicate/1</c> all enumerate solutions, so they're far
/// nicer expressed as a pair of clauses than as a builtin that has to
/// fake backtracking. The Prolog-level definitions ride the standard
/// WAM choice-point machinery without any builtin-CP plumbing.</para>
///
/// <para>The C# side keeps a couple of "all matches" helper builtins
/// (<c>'$all_clauses_of'/2</c>, <c>'$all_predicate_indicators'/1</c>)
/// that bridge from the engine's clause and functor tables into a
/// Prolog list — Prolog's <c>member/2</c> then enumerates the list
/// with normal backtracking.</para>
/// </summary>
internal static class Prelude
{
    public const string ModuleName = "$prelude";

    public const string Source = """
        :- module('$prelude').
        :- public member/2.
        :- public clause/2.
        :- public current_predicate/1.
        :- public '$cp_ctx'/2.
        :- public length/2.
        :- public sub_atom/5.
        :- public maplist/2.
        :- public maplist/3.
        :- public maplist/4.
        :- public foldl/4.
        :- public foldl/5.
        :- public aggregate_all/3.
        :- public forall/2.
        :- public if/3.
        :- public ifthen/2.
        :- public ifthenelse/3.
        :- public evaluable_property/2.
        :- public clause/3.
        :- public call_nth/2.
        :- public catch/3.
        :- public '$catch_run'/1.
        :- public copy_term/3.
        :- public subsumes_term/2.
        :- public call_det/2.
        :- public select/3.
        :- public permutation/2.
        :- public memberchk/2.
        :- public nonmember/2.
        :- public subtract/3.
        :- public intersection/3.
        :- public union/3.
        :- public delete/3.
        :- public numlist/3.
        :- public sum_list/2.
        :- public sumlist/2.
        :- public max_list/2.
        :- public min_list/2.
        :- public max_member/2.
        :- public min_member/2.
        :- public include/3.
        :- public exclude/3.
        :- public partition/4.
        :- public pairs_keys_values/3.
        :- public map_list_to_pairs/3.
        :- public can_be/2.
        :- public atom_si/1.
        :- public atomic_si/1.
        :- public integer_si/1.
        :- public character_si/1.
        :- public list_si/1.
        :- public chars_si/1.
        :- meta_predicate(map_list_to_pairs(2, *, *)).
        :- public predsort/3.
        :- public sort/4.
        :- public atomic_concat/3.
        :- public atomic_list_concat/2.
        :- public atomic_list_concat/3.
        :- public char_type/2.
        :- public false/0.
        :- public once/1.
        :- public ignore/1.
        :- public time_out/3.
        :- meta_predicate(time_out(0, *, *)).
        % The ISO/de-facto meta-predicates and control constructs, so
        % predicate_property(G, meta_predicate(T)) reports the argument
        % modes a portable program (and Logtalk's compiler) reads.
        :- meta_predicate(findall(*, 0, *)).
        :- meta_predicate(findall(*, 0, *, *)).
        :- meta_predicate(bagof(*, ^, *)).
        :- meta_predicate(setof(*, ^, *)).
        :- meta_predicate(forall(0, 0)).
        :- meta_predicate(aggregate_all(*, 0, *)).
        :- meta_predicate(catch(0, *, 0)).
        :- meta_predicate(\+(0)).
        :- meta_predicate(once(0)).
        :- meta_predicate(ignore(0)).
        :- meta_predicate(call(0)).
        :- meta_predicate(call(1, *)).
        :- meta_predicate(call(2, *, *)).
        :- meta_predicate(call(3, *, *, *)).
        :- meta_predicate(call(4, *, *, *, *)).
        :- meta_predicate(call(5, *, *, *, *, *)).
        :- meta_predicate(call(6, *, *, *, *, *, *)).
        :- meta_predicate(call(7, *, *, *, *, *, *, *)).
        :- meta_predicate(call_nth(0, *)).
        :- meta_predicate(setup_call_cleanup(0, 0, 0)).
        :- meta_predicate(call_cleanup(0, 0)).
        :- meta_predicate(if(0, 0, 0)).
        :- meta_predicate(ifthen(0, 0)).
        :- meta_predicate(ifthenelse(0, 0, 0)).
        :- meta_predicate(apply(1, *)).
        % Control constructs: callable, and their arguments are goals.
        % (','/2 and friends are seeded in C# — PrologEngine's
        % SeedControlMetaTemplates — since a ','-term in the directive is
        % the conjunction-of-specs form and cannot name ','/2 itself.)
        :- public call_residue_vars/2.
        :- public time/1.
        :- public chdir/1.
        :- public append/2.
        :- public flatten/2.
        :- public call_with_limit/2.
        :- public offset/2.
        :- public copy_term_nat/2.
        :- public variant/2.
        :- public term_singletons/2.
        :- public bb_put/2.
        :- public bb_get/2.
        :- public bb_update/3.
        :- public bb_delete/2.
        :- public bb_b_put/2.
        :- public read_term_from_chars/3.
        :- public write_term_to_chars/3.
        :- meta_predicate(limit(*, 0)).
        :- meta_predicate(offset(*, 0)).
        :- public ':'/2.
        :- public phrase/2.
        :- public phrase/3.
        :- public phrase_from_stream/2.
        :- public phrase_from_stream/3.
        :- public phrase_from_file/2.
        :- public phrase_from_file/3.
        % public like coroutining's '$co_alias_check'/1: the goal a wakeup
        % meta-calls resolves globally, not module-locally.
        :- public '$lazy_text_step'/4.
        :- meta_predicate(phrase_from_stream(2, *)).
        :- meta_predicate(phrase_from_stream(2, *, *)).
        :- meta_predicate(phrase_from_file(2, *)).
        :- meta_predicate(phrase_from_file(2, *, *)).
        :- public display/1.
        :- public display/2.
        :- public recorda/2.
        :- public recordz/2.
        :- public recorded/2.
        :- public tab/1.
        :- public apply/2.
        :- public findall/3.
        :- public bagof/3.
        :- public setof/3.
        :- public findall/4.
        :- public retractall/1.
        :- public listing/0.
        :- public listing/1.
        :- public format/1.
        :- public format_to_atom/3.
        :- public with_output_to/2.
        :- public '$wot_recover'/2.
        :- public '$call_conj'/3.
        :- public '$call_disj'/3.
        :- public '$call_arrow'/3.
        :- public '$call_softarrow'/3.
        :- public '$call_neg'/1.
        % The portray/1 hook: user code adds clauses to it from anywhere, so
        % it is multifile and dynamic before anyone declares it (SWI, SICStus).
        :- multifile portray/1.
        :- dynamic portray/1.
        :- dynamic attribute_goals/4.
        :- dynamic file_search_path/2.
        :- dynamic library_directory/1.
        :- dynamic '$tbl_running'/0.
        :- dynamic '$tbl_subgoal'/4.
        :- dynamic '$tbl_ans'/2.
        :- dynamic '$tbl_delta'/2.
        :- dynamic '$tbl_newd'/2.
        :- dynamic '$tbl_fresh'/1.
        :- dynamic '$tbl_mode'/1.
        :- dynamic '$tbl_neg_cache'/2.
        :- dynamic '$wfs_mode'/0.
        :- dynamic '$wfs_active'/0.
        :- dynamic '$wfs_k'/1.
        :- public '$table_call'/3.
        :- public '$tbl_dispatch'/3.
        :- public '$tbl_consume'/3.
        :- public '$tbl_negate'/1.
        :- public '$get_attr_list'/2.
        :- public '$term_attributed_variables'/2.
        :- public put_atts/3.
        :- public get_atts/3.
        :- public strip_module/3.
        :- public '$skip_max_list'/4.
        :- public '$absent_attr'/3.
        :- public abolish_all_tables/0.
        :- public abolish_table/1.
        :- public well_founded/2.

        %! member(?Elem, ?List) | Lists | Succeeds when Elem is a member of List; enumerates members on backtracking.
        % First-argument-indexed "look-ahead" form (the GNU Prolog
        % library shape). '$member3' dispatches on its first argument —
        % the list tail — so when the tail is [] only the var-headed
        % first clause is in the index bucket and no choice point is
        % left. That makes the LAST element of a fixed list deterministic
        % (no residual CP), so the top-level finishes without a spurious
        % ';' prompt, exactly like gprolog. The naive
        % member(X,[X|_]) / member(X,[_|T]) form leaves a CP on every
        % element because both clauses' list argument is just [_|_].
        member(X, [Y|T]) :- '$member3'(T, X, Y).
        '$member3'(_, X, X).
        '$member3'([Y|T], X, _) :- '$member3'(T, X, Y).

        % Control constructs reached through a runtime call/1 goal (chunk
        % 86). A control construct written directly in a clause body never
        % gets here — the MetaTransform rewrites it at compile time; the
        % interpreter's call dispatch routes ,/2 ;/2 ->/2 \+/1 to these
        % plainly-named helpers (operator atoms are awkward to declare).
        % K is the cut barrier of the enclosing call: '$call'/2
        % re-enters call dispatch carrying it, so a `!` inside a runtime
        % compound goal commits exactly as far as the call — no further.
        % ,/2 ;/2 and the then/else of ->/2 are cut-transparent (they pass
        % K on); a ->/2 condition and \+/1 are opaque, so they use call/1.
        '$call_conj'(A, B, K) :- '$call'(A, K), '$call'(B, K).
        '$call_disj'((C -> T), E, K) :- !, ( call(C) -> '$call'(T, K) ; '$call'(E, K) ).
        % ADR-037 — a runtime-built ( C *-> T ; E ). The module distribution
        % (DistributeMqual) keeps the *-> structural so this clause matches; the
        % soft cut is written with the builtins directly ('$choice_level'(B) at
        % branch-1 entry names the Else CP, '$soft_cut'(B) neutralises it once C
        % succeeds — Else pruned, C's non-determinism preserved, T/E transparent
        % via K).
        '$call_disj'((C *-> T), E, K) :- !,
            ( '$choice_level'(B), call(C), '$soft_cut'(B), '$call'(T, K)
            ; '$call'(E, K) ).
        '$call_disj'(A, _, K) :- '$call'(A, K).
        '$call_disj'(_, B, K) :- '$call'(B, K).
        '$call_arrow'(C, T, K) :- call(C), !, '$call'(T, K).
        % ADR-037 — bare ( C *-> T ) with no else. Without an else there is nothing
        % to prune, so soft cut degenerates to conjunction: it is $call_arrow WITHOUT
        % the commit, so C's non-determinism drives T (each solution of C runs T).
        '$call_softarrow'(C, T, K) :- call(C), '$call'(T, K).
        '$call_neg'(G) :- ( call(G) -> fail ; true ).

        %! forall(:Condition, :Action) | Control | Succeeds if Action holds for every solution of Condition.
        % \+ (Condition, \+ Action). Condition and Action run in the LIVE engine
        % via call/1 (so their side effects are visible to the caller). A
        % statically-callable forall/2 is rewritten inline to this same negation
        % pair by MetaTransform; this clause is the runtime fallback for a
        % variable Condition/Action (it must NOT use an isolated sub-engine —
        % that would hide the called goals' assert/retract).
        forall(Cond, Action) :- \+ ( call(Cond), \+ call(Action) ).

        %! if(:Condition, :Then, :Else) | Control | SICStus soft-cut if/3: runs Then for EVERY solution of Condition; Else only if Condition never succeeded.
        if(C, T, E) :- ( C *-> T ; E ).

        %! ifthen(:Condition, :Then) | Control | Arity form: runs Then if Condition succeeds (committing to its first solution); SUCCEEDS without running Then when Condition fails — unlike (Condition -> Then), which fails.
        ifthen(P, Q) :- ( P -> Q ; true ).

        %! ifthenelse(:Condition, :Then, :Else) | Control | Arity form of if-then-else: Then over the first solution of Condition, Else when Condition fails.
        ifthenelse(P, Q, R) :- ( P -> Q ; R ).

        %! call_nth(:Goal, ?N) | Control | True when Goal has an Nth solution: with N bound, commits to that solution; with N unbound, enumerates solutions numbering each.
        call_nth(Goal, N) :-
            (   var(Goal) -> throw(error(instantiation_error, call_nth/2))
            ;   \+ callable(Goal) ->
                throw(error(type_error(callable, Goal), call_nth/2))
            ;   var(N) -> true
            ;   integer(N) ->
                (   N < 0 ->
                    throw(error(domain_error(not_less_than_zero, N), call_nth/2))
                ;   true
                )
            ;   throw(error(type_error(integer, N), call_nth/2))
            ),
            (   integer(N), N =:= 0 -> fail
            ;   gensym('$call_nth', Key),
                set_flag(Key, 0),
                call(Goal),
                get_flag(Key, C0),
                C1 is C0 + 1,
                set_flag(Key, C1),
                % The counter is a FLAG (not trailed), so it keeps counting
                % across Goal's backtracking — that is the whole point.
                (   integer(N) -> ( C1 =:= N -> ! ; fail )
                ;   N = C1
                )
            ).

        %! clause(?Head, ?Body, ?Ref) | Database | clause/2 with a clause reference: fetches by Ref when bound, else enumerates Head's clauses binding Ref (de facto standard).
        clause(Head, Body, Ref) :-
            (   nonvar(Ref) -> '$clause_ref_fetch'(Ref, Head, Body)
            ;   '$clause_refs_of'(Head, Refs),
                member(Ref, Refs),
                '$clause_ref_fetch'(Ref, Head, Body)
            ).

        %! evaluable_property(+Callable, ?Property) | Arithmetic | Properties of an arithmetic function: built_in, static, template(Callable, ReturnType).
        evaluable_property(E, P) :-
            (   var(E) -> throw(error(instantiation_error, evaluable_property/2))
            ;   \+ callable(E) ->
                throw(error(type_error(callable, E), evaluable_property/2))
            ;   true
            ),
            (   var(P) -> true
            ;   P = template(_, _) -> true
            ;   ( P == built_in ; P == static ; P == (dynamic) ; P == foreign ) -> true
            ;   throw(error(domain_error(evaluable_property, P), evaluable_property/2))
            ),
            functor(E, N, A),
            '$is_evaluable'(N, A),
            '$evaluable_property1'(N, A, P).

        '$evaluable_property1'(_, _, built_in).
        '$evaluable_property1'(_, _, static).
        '$evaluable_property1'(N, A, template(T, R)) :-
            (   A =:= 0 -> T = N, ( N == pi -> R = float ; N == e -> R = float ; R = number )
            ;   functor(T, N, A), '$fill_number_args'(A, T), R = number
            ).

        '$fill_number_args'(0, _) :- !.
        '$fill_number_args'(I, T) :-
            arg(I, T, number), I1 is I - 1, '$fill_number_args'(I1, T).

        %! findall(?Template, :Goal, -List) | Findall & aggregation | Collects an instance of Template for every solution of Goal into a list.
        % Runs Goal in the LIVE engine via call/1 and the in-engine collect
        % primitives ('$findall_push' opens a solution frame, '$findall_record_s'
        % snapshots Template at each solution, '$findall_collect' closes the frame
        % and unifies List). Mirrors the inline loop MetaTransform emits for a
        % statically-callable findall/3; this clause is the runtime fallback for a
        % variable Goal. It must NOT use an isolated sub-engine — that lacked the
        % parent's bundle-precompiled predicates and hid the goal's side effects.
        findall(Template, Goal, List) :-
            '$check_partial_list'(List),
            ( '$findall_push', call(Goal), '$findall_record_s'(Template), fail
            ; '$findall_collect'(List) ).

        %! bagof(?Template, :Goal, -List) | Findall & aggregation | Collects Goal's solutions; fails when there are none.
        %! setof(?Template, :Goal, -List) | Findall & aggregation | Like bagof/3 but the result list is sorted and duplicate-free.
        % Runtime (variable-goal) fallback for bagof/3 and setof/3, in the LIVE
        % engine with FULL WITNESS GROUPING — the free variables of Goal (not in
        % Template, not ^-quantified) partition the solutions; each distinct
        % witness (variant equality, first-occurrence order) yields one bag on
        % backtracking with the free variables bound to it (ISO §8.10.2).
        % The no-grouping shortcut this replaces collapsed every group into one
        % bag whenever bagof was reached as a runtime goal (Logtalk's
        % union_find::disjoint_sets returned a single merged set).
        % '$bagof_parts' preserves the '$mqual' module tag so the collected goal
        % still resolves in the meta-caller's module; the quantified variables
        % are collected from INSIDE the tag as well.
        bagof(Template, Goal, Bag) :-
            '$check_partial_list'(Bag),
            '$bagof_parts'(Goal, Inner, QVars),
            term_variables(Inner, GoalVars),
            term_variables(t(Template, QVars), BoundVars),
            '$bagof_witness'(GoalVars, BoundVars, Witness),
            (   Witness == [] ->
                findall(Template, Inner, Bag),
                Bag \= []
            ;   findall(Witness-Template, Inner, Pairs),
                Pairs \= [],
                '$bagof_groups'(Pairs, Witness, Bag)
            ).
        setof(Template, Goal, Set) :-
            '$check_partial_list'(Set),
            bagof(Template, Goal, Bag),
            sort(Bag, Set).
        '$bagof_parts'('$mqual'(M, G), '$mqual'(M, S), Q) :- !, '$bagof_strip'(G, S, Q).
        '$bagof_parts'(G, S, Q) :- '$bagof_strip'(G, S, Q).
        '$bagof_strip'(V, S, Q) :- nonvar(V), V = Vs ^ G, !, Q = [Vs|Q1], '$bagof_strip'(G, S, Q1).
        '$bagof_strip'(G, G, []).
        '$bagof_witness'([], _, []).
        '$bagof_witness'([V|Vs], Bound, W) :-
            (   '$var_memberchk'(V, Bound) -> '$bagof_witness'(Vs, Bound, W)
            ;   W = [V|W1], '$bagof_witness'(Vs, Bound, W1)
            ).
        '$var_memberchk'(V, [X|Xs]) :- ( V == X -> true ; '$var_memberchk'(V, Xs) ).
        % One group per distinct witness, in first-occurrence order; RE-SATISFIABLE
        % (bagof enumerates groups on backtracking). Variant witnesses share a
        % group and unify, binding the caller's free variables.
        '$bagof_groups'([W0-T0|Rest], Witness, Bag) :-
            '$bagof_take'(Rest, W0, Ts, Others),
            (   Witness = W0, Bag = [T0|Ts]
            ;   '$bagof_groups'(Others, Witness, Bag)
            ).
        '$bagof_take'([], _, [], []).
        '$bagof_take'([W-T|Ps], W0, Ts, Others) :-
            (   subsumes_term(W, W0), subsumes_term(W0, W) ->
                W = W0, Ts = [T|Ts1], '$bagof_take'(Ps, W0, Ts1, Others)
            ;   Others = [W-T|O1], '$bagof_take'(Ps, W0, Ts, O1)
            ).

        %! catch(:Goal, ?Catcher, :Recovery) | Control | Runs Goal; if it throws a ball unifying Catcher, runs Recovery instead.
        % Runs in the LIVE engine using the catch-frame machinery
        % ('$catch_begin' pushes a frame carrying Catcher + a recovery goal;
        % the engine's throw handler unwinds to it and dispatches the recovery).
        % A statically-callable catch/3 is rewritten inline by MetaTransform to
        % the same shape with generated helpers; this clause is the runtime
        % fallback for a variable Goal/Recovery. '$catch_run'/1 wraps the
        % (arbitrary, possibly control-construct) Recovery so the recovery
        % dispatch resolves a real predicate address and call/1 then runs it.
        catch(Goal, Catcher, Recovery) :-
            '$catch_begin'(Catcher, '$catch_run'(Recovery)),
            call(Goal),
            '$catch_end'.
        '$catch_run'(Recovery) :- call(Recovery).

        %! clause(+Head, ?Body) | Database | Enumerates the clauses (Head :- Body) of a predicate; Module:Head reads from that module's viewpoint.
        % '$clause_enum' yields matching clauses lazily (a backtrackable
        % builtin): only the candidate being tried is materialised on the heap,
        % instead of building the whole O(#clauses) Head-Body pair list up front
        % for member/2 to walk. The Head-Body pair is built here so its
        % variables are the caller's. The qualified form resolves the head
        % from M's viewpoint: a dynamic is flat-global (the qualifier peels),
        % M's own definition reads M's clauses only, an import reads its
        % source's; anything else fails.
        clause(H0, B) :-
            nonvar(H0), H0 = ':'(_, _), !,
            '$strip_module'(H0, M, H),
            (   var(M) -> throw(error(instantiation_error, _))
            ;   \+ atom(M) -> throw(error(type_error(atom, M), _))
            ;   true
            ),
            nonvar(H),
            '$module_clause_enum'(M, H, H-B).
        clause(H, B) :-
            (   var(H) -> throw(error(instantiation_error, _))
            ;   \+ callable(H) -> throw(error(type_error(callable, H), _))
            ;   nonvar(B), \+ callable(B) ->
                    throw(error(type_error(callable, B), _))
            ;   predicate_property(H, built_in) ->
                    functor(H, N, A),
                    throw(error(permission_error(
                        access, private_procedure, N/A), _))
            ;   true
            ),
            '$clause_enum'(H, H-B).

        %! current_predicate(?PredicateIndicator) | Database | Enumerates the defined predicates as Name/Arity indicators; Module:Name/Arity enumerates a module's own.
        % '$current_predicate_enum' yields indicators lazily (a backtrackable
        % builtin), so the full O(n) indicator list is no longer built on the
        % heap before member/2 walks it. The qualified form M:PI answers for
        % the predicates DEFINED in module M (imports are not definitions);
        % an unbound M backtracks over the modules, SWI-style.
        current_predicate(Spec) :-
            (   nonvar(Spec), '$qualified_indicator'(Spec, M, I) ->
                (   nonvar(M), \+ atom(M) ->
                    throw(error(type_error(atom, M), _))
                ;   true
                ),
                '$module_predicate_enum'(M, I)
            ;   '$check_predicate_indicator'(Spec),
                '$current_predicate_enum'(Spec)
            ).

        % The two spellings of a qualified indicator. The operator-natural
        % M:F/A parses as (M:F)/A — ':' (200) binds tighter than '/' (400) —
        % so the colon sits INSIDE the indicator; M:(F/A) is the explicit
        % whole-indicator qualification. Fails for an unqualified spec.
        '$qualified_indicator'(':'(Q0, R0), M, I) :- !,
            '$strip_module'(':'(Q0, R0), M, I),
            '$check_qualified_indicator'(I, ':'(Q0, R0)).
        '$qualified_indicator'('/'(Q, A), M, '/'(N, A)) :-
            nonvar(Q), Q = ':'(_, _),
            '$strip_module'(Q, M, N),
            (   atom(N) -> true
            ;   var(N)  -> true
            ;   throw(error(type_error(predicate_indicator, '/'(Q, A)), _))
            ).

        % Peels module qualifications to the INNERMOST pair: M1:M2:X is
        % M2's X (the SWI reading). Shared by every M:X-aware builtin.
        '$strip_module'(':'(M0, R), M, I) :-
            (   nonvar(R), R = ':'(_, _) -> '$strip_module'(R, M, I)
            ;   M = M0, I = R
            ).

        % The compile-time context for an in-module current_predicate/1 call
        % (ModuleRewrite injects the textual module, the $mqual idea): an
        % explicitly qualified spec keeps its own module; a plain one answers
        % for the module's OWN definitions plus the global view.
        '$cp_ctx'(M, Spec) :-
            (   nonvar(Spec), '$qualified_indicator'(Spec, _, _) ->
                current_predicate(Spec)
            ;   '$check_predicate_indicator'(Spec),
                '$ctx_predicate_enum'(M, Spec)
            ).

        '$check_predicate_indicator'(I) :- var(I), !.
        % §8.8.2.3: inside Name/Arity, a BOUND Name must be an atom and a
        % BOUND Arity a non-negative integer — 0/dog, 3/3, f/f and f/(-1)
        % are all type_error(predicate_indicator, Culprit), with the whole
        % indicator as the culprit (GNU and SWI agree).
        '$check_predicate_indicator'(I) :-
            I = (N/A), !,
            (   nonvar(N), \+ atom(N) ->
                throw(error(type_error(predicate_indicator, I), _))
            ;   nonvar(A), \+ integer(A) ->
                throw(error(type_error(predicate_indicator, I), _))
            ;   integer(A), A < 0 ->
                throw(error(domain_error(not_less_than_zero, A), _))
            ;   true
            ).
        '$check_predicate_indicator'(I) :-
            throw(error(type_error(predicate_indicator, I), _)).

        % Inside a qualification the culprit of a malformed indicator is the
        % WHOLE qualified term (SWI: type_error(predicate_indicator, m:bad)).
        '$check_qualified_indicator'(I, _) :- var(I), !.
        '$check_qualified_indicator'(_/_, _) :- !.
        '$check_qualified_indicator'(_, Spec) :-
            throw(error(type_error(predicate_indicator, Spec), _)).

        %! length(?List, ?Length) | Lists | Relates a list to its length; enumerates lists of growing length when both arguments are unbound.
        % Proper lists take the native '$list_length' fast path; everything
        % else (partial list, improper term, bad Length) walks with the
        % original term kept for the type_error(list, Culprit).
        length(L, N) :-
            integer(N), N < 0, !,
            throw(error(domain_error(not_less_than_zero, N), length/2)).
        length(L, N) :-
            nonvar(L), '$list_length'(L, M), !,
            (   integer(N) -> N = M
            ;   var(N) -> N = M
            ;   throw(error(type_error(integer, N), length/2))
            ).
        length(L, N) :- '$length_walk'(L, L, N, 0).

        '$length_walk'(L, Orig, N, Acc) :-
            (   var(L) ->
                (   integer(N) -> M is N - Acc, M >= 0, '$make_var_list'(M, L)
                    % Length identical to the open tail (length(L,L),
                    % length([a|X],X)): every enumeration candidate binds the
                    % tail to a k-skeleton and then fails unifying that LIST
                    % with the integer k as output — false is the limit the
                    % enumeration never reaches. (SWI fails too; Scryer and
                    % Trealla throw resource_error(finite_memory) instead —
                    % accepted divergence, their test1095.)
                ;   L == N -> fail
                ;   var(N) -> '$length_enum'(L, N, Acc)
                ;   throw(error(type_error(integer, N), length/2))
                )
            ;   L = [_|T] -> Acc1 is Acc + 1, '$length_walk'(T, Orig, N, Acc1)
            ;   throw(error(type_error(list, Orig), length/2))
            ).

        '$length_enum'([], N, N).
        '$length_enum'([_|T], N, Acc) :-
            Acc1 is Acc + 1,
            '$length_enum'(T, N, Acc1).

        %! sub_atom(+Atom, ?Before, ?Length, ?After, ?SubAtom) | Atoms & strings | Backtracks over every (Before, Length, After, SubAtom) decomposition of an atom.
        % '$sub_atom_enum' yields each decomposition lazily (a backtrackable
        % builtin), so a long atom no longer materialises all O(n^2)
        % decompositions onto the heap before member/2 walks them. (The earlier
        % revert to the eager form was due to a missing IsBacktrackable flag that
        % broke it under Tier-1 IL — now fixed.)
        sub_atom(Atom, Before, Length, After, Sub) :-
            '$sub_atom_enum'(Atom, Before, Length, After, Sub).

        %! sub_string(+String, ?Before, ?Length, ?After, ?SubString) | Atoms & strings | Backtracks over every substring decomposition of String; the parts are strings (SWI).
        % The string counterpart of sub_atom/5: enumerate over the text (a string
        % arg is converted to an atom first) and hand each substring back as a
        % string.
        :- public sub_string/5.
        sub_string(String, Before, Length, After, Sub) :-
            ( string(String) -> atom_string(A, String) ; A = String ),
            sub_atom(A, Before, Length, After, SubA),
            atom_string(SubA, Sub).

        %! subsumes_term(@General, @Specific) | Term inspection & construction | Succeeds if General subsumes Specific (Specific is an instance of General) without binding any variable of either term.
        % ISO §8.2.4. A pure test: the double negation undoes the trial
        % unification's bindings. After General = Specific, Specific's
        % variables must be unchanged (still the same distinct unbound vars),
        % i.e. only General's variables were bound — otherwise General does
        % not subsume Specific.
        subsumes_term(General, Specific) :-
            \+ \+ (
                term_variables(Specific, Vars),
                General = Specific,
                term_variables(Vars, Vars2),
                Vars == Vars2
            ).

        %! call_det(:Goal, -Deterministic) | Control | Calls Goal once and unifies Deterministic with true if Goal succeeded without leaving a choice point, false otherwise.
        % The determinism-check primitive several SWI-family / GNU Prologs
        % expose; lgtunit's deterministic/1,2 dispatch to it. Samples the
        % engine choice-point pointer B before and after the goal: a goal
        % that leaves a choice point raises B, a deterministic one does not.
        % The final cut commits to the first solution (call_det is semidet)
        % and discards any choice point the goal left.
        call_det(Goal, Deterministic) :-
            '$choice_level'(B0),
            call(Goal),
            '$choice_level'(B1),
            ( B1 > B0 -> Deterministic = false ; Deterministic = true ),
            !.

        %! setup_call_cleanup(:Setup, :Goal, :Cleanup) | Control | Runs Setup once, then Goal, running Cleanup exactly once when Goal completes: deterministic success, failure, exhaustion, error, external cut, or query teardown.
        % Cleanup runs with once/1 semantics — its choice points are destroyed and
        % its success/failure ignored, but an exception it raises propagates (that
        % is exactly ignore/1). It fires exactly once, guarded by the retract of
        % the '$cleanup_pending'/2 fact that stably stores the goal:
        %   - deterministic success: fired synchronously, then the cut keeps the
        %     call deterministic (determinism sampled via '$choice_level', as
        %     call_det/2 does);
        %   - failure / backtracking-exhaustion: the fallback clause fires it;
        %   - error in Goal: the catch recovery fires it, then re-raises;
        %   - a caller cutting past a NON-deterministic Goal's leftover choice
        %     points, an exception unwinding from BELOW, or the query being torn
        %     down: the engine enqueues the handler (Activation cleanup registry)
        %     and the interpreter runs '$drain_cleanups'/0 at its next safe point.
        % The synchronous paths '$scc_forget' the handler first, so only a
        % genuinely-abandoned scope ever fires asynchronously. Uses
        % '$catch_begin'/'$catch_end' directly (an inline catch(...) in a prelude
        % clause lowers to a '$catchrec_N' helper whose per-Apply counter collides
        % with the query's own — no compiled address); the recovery is the
        % stable-address public '$scc_recover'/2.
        :- dynamic('$cleanup_pending'/2).
        :- public setup_call_cleanup/3.
        setup_call_cleanup(Setup, Goal, Cleanup) :-
            once(Setup),
            % Cleanup must be callable NOW (WG17): an unbound Cleanup is an
            % instantiation_error even if Goal would bind it later; the check
            % runs after Setup so setup_call_cleanup(X=true, true, X) is fine.
            (   var(Cleanup) ->
                throw(error(instantiation_error, setup_call_cleanup/3))
            ;   callable(Cleanup) -> true
            ;   throw(error(type_error(callable, Cleanup), setup_call_cleanup/3))
            ),
            '$scc_register'(Ref, Cleanup),
            assertz('$cleanup_pending'(Ref, Cleanup)),
            '$catch_begin'(Error, '$scc_recover'(Ref, Error)),
            '$scc'(Goal, Ref, Cleanup),
            '$catch_end'.

        '$scc'(Goal, Ref, Cleanup) :-
            '$choice_level'(B0),
            call(Goal),
            '$choice_level'(B1),
            ( B1 =< B0 -> '$scc_fire_live'(Ref, Cleanup), ! ; true ).
        '$scc'(_, Ref, Cleanup) :-
            '$scc_fire_live'(Ref, Cleanup),
            fail.

        % Public so the catch-frame recovery dispatch ('$catch_begin') can
        % resolve its address by functor, as '$catch_run'/1 is.
        :- public '$scc_recover'/2.
        '$scc_recover'(Ref, Error) :-
            '$scc_fire'(Ref),
            throw(Error).

        % Exactly-once: the retract is the atomic guard. Forget the handler first
        % so a concurrent engine teardown can't also enqueue it, then run Cleanup
        % with once/1 semantics (ignore/1: CPs destroyed, success/failure ignored,
        % exception propagated).
        '$scc_fire'(Ref) :-
            '$scc_forget'(Ref),
            ( retract('$cleanup_pending'(Ref, Cleanup)) -> ignore(Cleanup) ; true ).

        % Sync-path variant: runs the LIVE Cleanup term, NOT the retracted copy —
        % assertz renamed its variables, and SWI's determinism-detection idiom
        % setup_call_cleanup(true, G, Det=true) needs Cleanup's bindings to reach
        % the caller. The retract stays as the exactly-once guard (if the engine
        % already fired the handler asynchronously, it fails and Cleanup is
        % skipped); the copy itself is discarded. Async paths (external cut,
        % teardown, error unwind) still run the copy — their bindings are undone
        % by the unwind anyway.
        '$scc_fire_live'(Ref, Cleanup) :-
            '$scc_forget'(Ref),
            ( retract('$cleanup_pending'(Ref, _)) -> ignore(Cleanup) ; true ).

        % Run by the interpreter at a safe point when the engine enqueued cleanups
        % from a teardown path. A Cleanup exception propagates out of the drain as
        % a normal exception.
        :- public '$drain_cleanups'/0.
        % A drained handler fired ASYNCHRONOUSLY (exception unwind, external
        % cut, teardown). An exception the Cleanup itself throws here is
        % DROPPED: when the trigger was an error unwind the original ball
        % has already won (SWI/WG17 first-exception-wins — a late throw
        % would surface as a phantom second error after the catch ran).
        % Async fire runs the LIVE Cleanup term (handler slot) — its
        % bindings reach the caller (scc(true, scc(...), Y=3), ! leaves
        % Y=3). The retract stays the exactly-once guard. The catch goal is
        % built at runtime so MetaTransform takes the runtime catch/3
        % clause (see '$module_attr_goals' — an inlined static catch has no
        % baked '$catchrec' address and silently fails); its ball is
        % DROPPED: on an error unwind the original ball already won
        % (first-exception-wins), a late throw would surface as a phantom
        % second error after the catch ran.
        % Live is the handler's live cell for a CUT fire (heap intact,
        % bindings reach the caller) or the '$scc_use_copy' sentinel for an
        % EXCEPTION/teardown fire (heap truncated below the catcher — the
        % live cell may point at reclaimed memory; run the stable copy).
        '$drain_cleanups' :-
            ( '$pop_pending_cleanup'(Ref, Live)
            -> '$scc_forget'(Ref),
               ( retract('$cleanup_pending'(Ref, Copy))
               -> ( Live == '$scc_use_copy' -> F = ignore(Copy) ; F = ignore(Live) ),
                  catch(F, _, true)
               ; true ),
               '$drain_cleanups'
            ; true ).

        %! call_cleanup(:Goal, :Cleanup) | Control | setup_call_cleanup/3 with no setup: Cleanup runs exactly once when Goal completes.
        :- public call_cleanup/2.
        call_cleanup(Goal, Cleanup) :- setup_call_cleanup(true, Goal, Cleanup).

        %! gensym(+Base, -Unique) | Atoms & strings | Generates a fresh atom Base1, Base2, … from a per-Base counter that survives backtracking.
        % Base + a monotonically increasing sequence number. The counter is a
        % flag/3 (not backtracked, so a failure-driven loop keeps advancing).
        % The number is converted to an atom BEFORE concatenation — atom_concat/3
        % is ISO-strict (rejects a numeric argument, as GProlog does).
        :- dynamic('$gensym_base'/1).
        :- public gensym/2.
        gensym(Base, Unique) :-
            must_be(atom, Base),
            ( '$gensym_base'(Base) -> true ; assertz('$gensym_base'(Base)) ),
            flag('$gensym'(Base), N0, N0 + 1),
            N is N0 + 1,
            number_codes(N, Codes),
            atom_codes(Suffix, Codes),
            atom_concat(Base, Suffix, Unique).

        %! reset_gensym | Atoms & strings | Resets every gensym/2 counter to 0.
        :- public reset_gensym/0.
        reset_gensym :- forall('$gensym_base'(Base), set_flag('$gensym'(Base), 0)).

        %! reset_gensym(+Base) | Atoms & strings | Resets the gensym/2 counter for Base to 0.
        :- public reset_gensym/1.
        reset_gensym(Base) :- set_flag('$gensym'(Base), 0).

        %! maplist(:Goal, ?List) | Lists | Succeeds if Goal holds for every element of List.
        maplist(_, []).
        maplist(G, [X|Xs]) :- call(G, X), maplist(G, Xs).

        %! maplist(:Goal, ?List1, ?List2) | Lists | Succeeds if Goal holds for corresponding elements of two lists.
        maplist(_, [], []).
        maplist(G, [X|Xs], [Y|Ys]) :- call(G, X, Y), maplist(G, Xs, Ys).

        %! maplist(:Goal, ?List1, ?List2, ?List3) | Lists | Succeeds if Goal holds for corresponding elements of three lists.
        maplist(_, [], [], []).
        maplist(G, [X|Xs], [Y|Ys], [Z|Zs]) :-
            call(G, X, Y, Z), maplist(G, Xs, Ys, Zs).

        %! foldl(:Goal, ?List, +V0, -V) | Lists | Folds Goal over a list, threading an accumulator from V0 to V.
        foldl(_, [], Acc, Acc).
        foldl(G, [X|Xs], Acc, Out) :-
            call(G, X, Acc, Acc1),
            foldl(G, Xs, Acc1, Out).

        %! foldl(:Goal, ?List1, ?List2, +V0, -V) | Lists | Folds Goal over two lists, threading an accumulator from V0 to V.
        foldl(_, [], [], Acc, Acc).
        foldl(G, [X|Xs], [Y|Ys], Acc, Out) :-
            call(G, X, Y, Acc, Acc1),
            foldl(G, Xs, Ys, Acc1, Out).

        %! aggregate_all(+Template, :Goal, -Result) | Findall & aggregation | Aggregates Goal's solutions with a count, sum, bag or set template.
        aggregate_all(count, Goal, Count) :-
            findall(t, Goal, L),
            length(L, Count).
        aggregate_all(sum(Expr), Goal, Sum) :-
            findall(Expr, Goal, L),
            '$sum_list'(L, 0, Sum).
        aggregate_all(bag(X), Goal, Bag) :- findall(X, Goal, Bag).
        aggregate_all(set(X), Goal, Set) :-
            findall(X, Goal, L),
            sort(L, Set).

        '$sum_list'([], Acc, Acc).
        '$sum_list'([H|T], Acc, Out) :-
            Acc1 is Acc + H,
            '$sum_list'(T, Acc1, Out).

        % Residual-constraint projection. copy_term/3 copies a
        % term and, for every attributed variable in it, collects the
        % goals each module's attribute_goals/4 hook produces — already
        % re-expressed over the copy's variables. '$copy_term_3_prep'/3
        % does the structural copy and hands back ag(Module, Attr, Var)
        % triples; attribute_goals/4 is pre-declared dynamic so user
        % clauses simply join it and a hook-less program still links.
        %! copy_term(+Term, -Copy, -Goals) | Term inspection & construction | Copies a term with fresh variables and collects the residual attribute goals.
        % Scryer-style projection in three phases. (1) re-attach EVERY copied
        % attribute value to its copy variable first — a module's
        % attribute_goals//1 may read a SIBLING variable's attribute (clpz's
        % rel_tuple reads the relation variable's clpz_relation), so all
        % attachments must exist before any hook runs. (2) run the hooks.
        % (3) strip whatever attachments the hooks left, so a hookless
        % module's attribute never leaks onto the copy.
        copy_term(Term, Copy, Goals) :-
            '$copy_term_3_prep'(Term, Copy, AttrInfo),
            '$attr_reattach'(AttrInfo),
            '$attr_goals_of'(AttrInfo, Goals),
            '$attr_strip'(AttrInfo).

        '$attr_reattach'([]).
        '$attr_reattach'([ag(M, A, V)|R]) :- put_attr(V, M, A), '$attr_reattach'(R).

        % ADR-035 — debugger entry points over the same projection phases.
        % The debugger TRANSPLANTS a suspended activation's attributed
        % variables into an evaluation activation as ag(M, A, V) triples
        % (fresh V per source variable, C#-side); '$dbg_residuals' turns
        % them into the residual goals a stop's Constraints view shows, and
        % '$dbg_attach' alone makes an Immediate-window goal's frame
        % variables carry their real constraints.
        :- public '$dbg_residuals'/2.
        '$dbg_residuals'(AttrInfo, Goals) :-
            '$dbg_fix_foreign'(AttrInfo),
            '$attr_reattach'(AttrInfo),
            '$attr_goals_of'(AttrInfo, Goals),
            '$attr_strip'(AttrInfo).

        :- public '$dbg_attach'/1.
        '$dbg_attach'(AttrInfo) :-
            '$dbg_fix_foreign'(AttrInfo),
            '$attr_reattach'(AttrInfo).

        % A hook may have bound a helper variable (clpz marks propagator
        % states processed) or already stripped its own module — both fine.
        '$attr_strip'([]).
        '$attr_strip'([ag(M, _, V)|R]) :-
            ( var(V) -> del_attr(V, M) ; true ),
            '$attr_strip'(R).

        '$attr_goals_of'([], []).
        '$attr_goals_of'([ag(M, A, V)|Rest], Goals) :-
            ( attribute_goals(M, A, V, G) -> true
            ; '$module_attr_goals'(M, V, G) -> true
            ; G = []
            ),
            '$attr_goals_of'(Rest, RestGoals),
            append(G, RestGoals, Goals).

        % Scryer/SWI projection protocol: the attribute module defines a
        % module-local attribute_goals//1 that reads the attributes off the
        % variable itself and strips them as it projects (clpz ends in
        % del_attr — it expects to run on a copy; the attachment happened in
        % '$attr_reattach'). The catch goal is built at runtime (Goal is a
        % variable at the call site) so MetaTransform takes the runtime
        % catch/3 clause instead of inlining catch helpers — the baked
        % prelude carries no addresses for clause-generated '$catchrec'
        % helpers.
        '$module_attr_goals'(M, V, G) :-
            var(V),
            Goal = call(M:attribute_goals(V, G, [])),
            catch(Goal, error(existence_error(_, _), _), fail).

        % ===== variant (structural) equivalence =====
        % A =@= B iff A and B are equal up to a consistent renaming of their
        % variables. Number a fresh copy of each (variables become '$VAR'(N) in
        % first-occurrence order); the terms are variants iff the numbered copies
        % are identical and used the same number of variables.
        :- public (=@=)/2.
        :- public (\=@=)/2.
        %! =@=(@Term1, @Term2) | Term ordering | Term1 and Term2 are variants (structurally equal up to variable renaming).
        A =@= B :-
            copy_term(A, A1), numbervars(A1, 0, N),
            copy_term(B, B1), numbervars(B1, 0, M),
            N == M, A1 == B1.
        %! \=@=(@Term1, @Term2) | Term ordering | Term1 and Term2 are NOT variants.
        A \=@= B :- \+ (A =@= B).

        % ===== \= over the three-state trial core =====
        :- public (\=)/2.
        %! \=(?Term1, ?Term2) | Unification & comparison | Succeeds if the two terms do not unify. Attributed-variable hooks run: freeze fires during the trial, dif can veto it.
        % The native core only TRIAL-unifies (rollback, hooks never run). When
        % the trial bound an attvar the verdict is unreliable — a hook could
        % veto the unification (dif) or must observably fire (freeze) — so the
        % m verdict re-decides through a real negated unification.
        X \= Y :-
            '$not_unifiable3'(X, Y, R),
            (   R == t -> true
            ;   R == m -> \+ X = Y
            ;   fail
            ).

        % ===== single-threaded mutex + message queues (SWI compat) =====
        % Shumway is single-threaded, so a mutex is a no-op and with_mutex/2 just
        % runs the goal, committing to its first solution (as SWI does). A message
        % queue is a FIFO buffer backed by dynamic facts — assertz appends,
        % retract removes the oldest match. thread_get_message/2 FAILS on an empty
        % queue (there is no other thread to wait for) rather than blocking.
        :- public with_mutex/2.
        %! with_mutex(+Mutex, :Goal) | Threads | Runs Goal (once). Single-threaded: the mutex is a no-op.
        with_mutex(_Mutex, Goal) :- once(Goal).
        :- public mutex_create/1.
        mutex_create(_).
        :- public mutex_create/2.
        mutex_create(_, _).
        :- public mutex_lock/1.
        mutex_lock(_).
        :- public mutex_unlock/1.
        mutex_unlock(_).
        :- public mutex_destroy/1.
        mutex_destroy(_).

        :- dynamic('$mq_msg'/2).
        :- dynamic('$mq_ctr'/1).
        :- public message_queue_create/1.
        %! message_queue_create(?Queue) | Threads | Creates (or names) a FIFO message queue. Single-threaded buffer.
        message_queue_create(Q) :- ( var(Q) -> '$mq_fresh_id'(Q) ; true ).
        :- public message_queue_create/2.
        message_queue_create(Q, _Options) :- message_queue_create(Q).
        '$mq_fresh_id'(Q) :-
            ( retract('$mq_ctr'(N0)) -> N is N0 + 1 ; N = 1 ),
            assertz('$mq_ctr'(N)),
            number_codes(N, Cs), atom_codes(A, Cs), atom_concat('$mq_q_', A, Q).
        :- public thread_send_message/2.
        %! thread_send_message(+Queue, +Message) | Threads | Appends Message to the queue (FIFO).
        thread_send_message(Q, M) :- assertz('$mq_msg'(Q, M)).
        :- public thread_send_message/3.
        thread_send_message(Q, M, _Opts) :- assertz('$mq_msg'(Q, M)).
        :- public thread_get_message/2.
        %! thread_get_message(+Queue, ?Message) | Threads | Removes the oldest matching message; FAILS if none (single-threaded, no blocking).
        thread_get_message(Q, M) :- retract('$mq_msg'(Q, M)).
        :- public thread_peek_message/2.
        thread_peek_message(Q, M) :- '$mq_msg'(Q, M), !.
        :- public message_queue_destroy/1.
        message_queue_destroy(Q) :- retractall('$mq_msg'(Q, _)).

        % ===== must_be/2, print_message/2 (SWI/SICStus compat) =====
        :- public must_be/2.
        %! must_be(+Type, @Value) | Type checking | Throws instantiation_error if Value is unbound (unless Type is var), or type_error(Type, Value) if it is not of Type.
        must_be(Type, X) :-
            ( var(X), Type \== var -> throw(error(instantiation_error, must_be/2))
            ; '$must_be_ok'(Type, X) -> true
            ; throw(error(type_error(Type, X), must_be/2)) ).
        '$must_be_ok'(integer, X) :- integer(X).
        '$must_be_ok'(atom, X) :- atom(X).
        '$must_be_ok'(atomic, X) :- atomic(X).
        '$must_be_ok'(number, X) :- number(X).
        '$must_be_ok'(callable, X) :- callable(X).
        '$must_be_ok'(list, X) :- is_list(X).
        '$must_be_ok'(boolean, X) :- ( X == true ; X == false ).
        '$must_be_ok'(var, X) :- var(X).
        '$must_be_ok'(nonvar, X) :- nonvar(X).
        '$must_be_ok'(ground, X) :- ground(X).
        '$must_be_ok'(positive_integer, X) :- integer(X), X > 0.
        '$must_be_ok'(nonneg, X) :- integer(X), X >= 0.
        '$must_be_ok'(float, X) :- float(X).
        '$must_be_ok'(string, X) :- string(X).
        '$must_be_ok'(text, X) :- ( atom(X) ; string(X) ).
        '$must_be_ok'(acyclic, X) :- acyclic_term(X).
        '$must_be_ok'(cyclic, X) :- cyclic_term(X).
        '$must_be_ok'(compound, X) :- compound(X).
        '$must_be_ok'(oneof(L), X) :- memberchk(X, L).

        :- public print_message/2.
        %! print_message(+Kind, +Message) | Messages | Prints a message of the given kind (error/warning/informational/silent) to user_error. A best-effort renderer (no message//1 hooks).
        print_message(silent, _) :- !.
        print_message(Kind, format(F, A)) :- !,
            format(user_error, '~w: ', [Kind]), format(user_error, F, A), nl(user_error).
        print_message(Kind, Message) :- format(user_error, '~w: ~q~n', [Kind, Message]).

        % ===== common list-library predicates =====

        %! select(?Elem, ?List, ?Rest) | Lists | Rest is List with one occurrence of Elem removed; backtracks over occurrences.
        select(X, [X|T], T).
        select(X, [H|T], [H|R]) :- select(X, T, R).

        %! permutation(?List, ?Permutation) | Lists | True when the two lists are permutations of each other; enumerates permutations.
        permutation([], []).
        permutation(L, [X|P]) :- select(X, L, R), permutation(R, P).

        %! memberchk(?Elem, +List) | Lists | Like member/2 but succeeds at most once — no backtracking over further matches.
        memberchk(X, [Y|T]) :- ( X = Y -> true ; memberchk(X, T) ).

        %! nonmember(?Elem, +List) | Lists | True when Elem does not unify with any element of List.
        nonmember(X, L) :- \+ member(X, L).

        %! subtract(+Set, +Delete, -Rest) | Lists | Rest is Set without the elements that also occur in Delete.
        subtract([], _, []).
        subtract([H|T], D, R) :-
            ( memberchk(H, D) -> R = R1 ; R = [H|R1] ),
            subtract(T, D, R1).

        %! intersection(+Set1, +Set2, -Intersection) | Lists | Intersection holds the elements of Set1 that also occur in Set2.
        intersection([], _, []).
        intersection([H|T], S2, R) :-
            ( memberchk(H, S2) -> R = [H|R1] ; R = R1 ),
            intersection(T, S2, R1).

        %! union(+Set1, +Set2, -Union) | Lists | Union holds the elements of Set1 not in Set2, followed by all of Set2.
        union([], S2, S2).
        union([H|T], S2, R) :-
            ( memberchk(H, S2) -> R = R1 ; R = [H|R1] ),
            union(T, S2, R1).

        %! delete(+List, +Elem, -Rest) | Lists | Rest is List with every element that unifies with Elem removed.
        delete([], _, []).
        delete([H|T], X, R) :-
            ( H \= X -> R = [H|R1] ; R = R1 ),
            delete(T, X, R1).

        %! numlist(+Low, +High, -List) | Lists | List is the consecutive integers from Low to High inclusive.
        numlist(L, H, List) :-
            ( L =< H -> L1 is L + 1, List = [L|Rest], numlist(L1, H, Rest)
            ; List = []
            ).

        %! sum_list(+List, -Sum) | Lists | Sum is the sum of the numbers in List.
        sum_list(L, S) :- '$sum_list'(L, 0, S).

        %! sumlist(+List, -Sum) | Lists | Sum is the sum of the numbers in List (alias of sum_list/2).
        sumlist(L, S) :- '$sum_list'(L, 0, S).

        %! max_list(+List, -Max) | Lists | Max is the largest number in the non-empty list.
        max_list([H|T], M) :- '$maxlist'(T, H, M).
        '$maxlist'([], M, M).
        '$maxlist'([H|T], A, M) :- ( H > A -> A1 = H ; A1 = A ), '$maxlist'(T, A1, M).

        %! min_list(+List, -Min) | Lists | Min is the smallest number in the non-empty list.
        min_list([H|T], M) :- '$minlist'(T, H, M).
        '$minlist'([], M, M).
        '$minlist'([H|T], A, M) :- ( H < A -> A1 = H ; A1 = A ), '$minlist'(T, A1, M).

        %! max_member(?Max, +List) | Lists | Max is the largest element of List in the standard order of terms.
        max_member(Max, [H|T]) :- '$maxmember'(T, H, Max).
        '$maxmember'([], M, M).
        '$maxmember'([H|T], A, M) :- ( H @> A -> A1 = H ; A1 = A ), '$maxmember'(T, A1, M).

        %! min_member(?Min, +List) | Lists | Min is the smallest element of List in the standard order of terms.
        min_member(Min, [H|T]) :- '$minmember'(T, H, Min).
        '$minmember'([], M, M).
        '$minmember'([H|T], A, M) :- ( H @< A -> A1 = H ; A1 = A ), '$minmember'(T, A1, M).

        %! include(:Goal, +List, -Included) | Lists | Included holds the elements of List for which Goal succeeds.
        include(_, [], []).
        include(G, [H|T], R) :-
            ( call(G, H) -> R = [H|R1] ; R = R1 ),
            include(G, T, R1).

        %! exclude(:Goal, +List, -Excluded) | Lists | Excluded holds the elements of List for which Goal fails.
        exclude(_, [], []).
        exclude(G, [H|T], R) :-
            ( call(G, H) -> R = R1 ; R = [H|R1] ),
            exclude(G, T, R1).

        %! partition(:Goal, +List, -Included, -Excluded) | Lists | Splits List by whether Goal succeeds on each element.
        partition(_, [], [], []).
        partition(G, [H|T], I, E) :-
            ( call(G, H) -> I = [H|I1], E = E1 ; I = I1, E = [H|E1] ),
            partition(G, T, I1, E1).

        %! pairs_keys_values(?Pairs, ?Keys, ?Values) | Lists | Relates a list of Key-Value pairs to its lists of keys and values.
        pairs_keys_values([], [], []).
        pairs_keys_values([K-V|Ps], [K|Ks], [V|Vs]) :-
            pairs_keys_values(Ps, Ks, Vs).

        %! map_list_to_pairs(:Key, +List, -KeyedPairs) | Lists | For each element E of List, KeyedPairs holds K-E where call(Key, E, K) computes the key (SWI library(pairs) form).
        map_list_to_pairs(_, [], []).
        map_list_to_pairs(F, [X|Xs], [K-X|Ps]) :-
            call(F, X, K),
            map_list_to_pairs(F, Xs, Ps).

        %! can_be(+Type, @Term) | Type checking | Scryer library(si) form: like must_be/2 but an unbound Term (or one whose subterms are yet unbound enough) is still admissible — only a term already incompatible with Type raises.
        can_be(Type, Term) :-
            ( var(Term) -> must_be_type_ok(Type)
            ; must_be(Type, Term)
            ).
        must_be_type_ok(Type) :-
            ( var(Type) -> throw(error(instantiation_error, can_be/2))
            ; true
            ).

        % The library(si) family (Scryer/Trealla): sound type tests — an
        % unbound (or not-yet-listlike) term is an instantiation_error, a
        % wrong one a type_error; plain success otherwise. "si" reads both
        % as Prolog "si" (if) and "sufficiently instantiated".
        %! atom_si(@Term) | Type checking | Sound atom test: instantiation_error when unbound, type_error(atom, Term) when bound to a non-atom.
        atom_si(X) :- ( var(X) -> throw(error(instantiation_error, atom_si/1)) ; atom(X) -> true ; throw(error(type_error(atom, X), atom_si/1)) ).
        %! atomic_si(@Term) | Type checking | Sound atomic test (si family).
        atomic_si(X) :- ( var(X) -> throw(error(instantiation_error, atomic_si/1)) ; atomic(X) -> true ; throw(error(type_error(atomic, X), atomic_si/1)) ).
        %! integer_si(@Term) | Type checking | Sound integer test (si family).
        integer_si(X) :- ( var(X) -> throw(error(instantiation_error, integer_si/1)) ; integer(X) -> true ; throw(error(type_error(integer, X), integer_si/1)) ).
        %! character_si(@Term) | Type checking | Sound one-char-atom test (si family).
        character_si(X) :- ( var(X) -> throw(error(instantiation_error, character_si/1)) ; atom(X), atom_length(X, 1) -> true ; throw(error(type_error(character, X), character_si/1)) ).
        %! list_si(@Term) | Type checking | Sound proper-list test: instantiation_error while the tail is unbound, type_error(list, Term) on a non-list tail.
        list_si(L) :- '$list_si'(L, L).
        '$list_si'(V, _) :- var(V), !, throw(error(instantiation_error, list_si/1)).
        '$list_si'([], _) :- !.
        '$list_si'([_|T], W) :- !, '$list_si'(T, W).
        '$list_si'(_, W) :- throw(error(type_error(list, W), list_si/1)).
        %! chars_si(@Term) | Type checking | Sound list-of-characters test (si family).
        chars_si(Cs) :- '$chars_si'(Cs, Cs).
        '$chars_si'(V, _) :- var(V), !, throw(error(instantiation_error, chars_si/1)).
        '$chars_si'([], _) :- !.
        '$chars_si'([C|T], W) :- !, character_si(C), '$chars_si'(T, W).
        '$chars_si'(_, W) :- throw(error(type_error(list, W), chars_si/1)).

        %! pairs_keys(+Pairs, -Keys) | Lists | The keys of a list of Key-Value pairs.
        :- public pairs_keys/2.
        pairs_keys(Pairs, Keys) :- pairs_keys_values(Pairs, Keys, _).

        %! pairs_values(+Pairs, -Values) | Lists | The values of a list of Key-Value pairs.
        :- public pairs_values/2.
        pairs_values(Pairs, Values) :- pairs_keys_values(Pairs, _, Values).

        %! predsort(:Pred, +List, -Sorted) | Lists | Sorts List by a three-way comparison predicate, dropping elements compared equal.
        predsort(P, List, Sorted) :- '$predsort_all'(List, P, [], Sorted).
        '$predsort_all'([], _, Acc, Acc).
        '$predsort_all'([H|T], P, Acc, Sorted) :-
            '$predsort_ins'(Acc, P, H, Acc1),
            '$predsort_all'(T, P, Acc1, Sorted).
        '$predsort_ins'([], _, X, [X]).
        '$predsort_ins'([Y|Ys], P, X, Out) :-
            call(P, Ord, X, Y),
            ( Ord == ('<') -> Out = [X, Y|Ys]
            ; Ord == ('=') -> Out = [Y|Ys]
            ; Out = [Y|Out1], '$predsort_ins'(Ys, P, X, Out1)
            ).

        %! sort(+Key, +Order, +List, -Sorted) | Lists | Sorts List by the given argument key (0 = whole term) and order (@<, @=<, @> or @>=).
        sort(Key, Order, List, Sorted) :-
            '$sort4_tag'(List, Key, 0, Tagged),
            msort(Tagged, Asc),
            ( ( Order == ('@<') ; Order == ('@>') ) -> '$sort4_dedup'(Asc, Uniq)
            ; Uniq = Asc
            ),
            ( ( Order == ('@>') ; Order == ('@>=') ) -> reverse(Uniq, Ordered)
            ; Ordered = Uniq
            ),
            '$sort4_elems'(Ordered, Sorted).
        '$sort4_tag'([], _, _, []).
        '$sort4_tag'([E|Es], Key, I, [k(K, I, E)|Ps]) :-
            ( Key =:= 0 -> K = E ; arg(Key, E, K) ),
            I1 is I + 1,
            '$sort4_tag'(Es, Key, I1, Ps).
        '$sort4_elems'([], []).
        '$sort4_elems'([k(_, _, E)|Ps], [E|Es]) :- '$sort4_elems'(Ps, Es).
        '$sort4_dedup'([], []).
        '$sort4_dedup'([k(K, I, E)|T], [k(K, I, E)|R]) :- '$sort4_skip'(T, K, R).
        '$sort4_skip'([], _, []).
        '$sort4_skip'([k(K2, I2, E2)|T], K, R) :-
            ( K2 == K -> '$sort4_skip'(T, K, R)
            ; R = [k(K2, I2, E2)|R1], '$sort4_skip'(T, K2, R1)
            ).

        % ===== atom / number conversion =====
        % atom_number/2 and number_string/2 are C# builtins (parse-or-fail
        % via TryParse); atomic_list_concat/2,3 and char_type/2 are below.

        % render an atomic term (atom, number or string) as an atom.
        '$atomic_to_atom'(X, X) :- atom(X), !.
        '$atomic_to_atom'(X, A) :- number(X), !, number_codes(X, Cs), atom_codes(A, Cs).
        '$atomic_to_atom'(X, A) :- atom_string(A, X).

        %! atomic_list_concat(+List, -Atom) | Atoms & strings | Concatenates a list of atomic terms into a single atom.
        atomic_list_concat(List, Atom) :-
            '$alc_check_list'(List),
            (   var(Atom) -> true
            ;   atom(Atom) -> true
            ;   throw(error(type_error(atom, Atom), atomic_list_concat/2))
            ),
            '$alc_concat'(List, Atom).

        '$alc_concat'([], '').
        '$alc_concat'([X|Xs], Atom) :-
            '$atomic_to_atom'(X, AX),
            '$alc_concat'(Xs, Rest),
            atom_concat(AX, Rest, Atom).

        %! atomic_concat(+Atomic1, +Atomic2, -Atom) | Atoms & strings | Concatenates two atomic terms into a single atom.
        atomic_concat(A, B, C) :- atomic_list_concat([A, B], C).

        %! atomic_list_concat(?List, +Separator, ?Atom) | Atoms & strings | Joins a list of atomics with a separator, or splits an atom on the separator.
        atomic_list_concat(List, Sep, Atom) :-
            var(List), nonvar(Atom), nonvar(Sep), Sep \== '', !,
            '$alc_split'(Atom, Sep, List).
        atomic_list_concat(List, Sep, Atom) :-
            % Join direction: the list must be proper with atomic elements
            % and the separator an atom — all checked before concatenating,
            % so a bad argument errors rather than half-building an atom.
            '$alc_check_list'(List),
            (   var(Sep) -> throw(error(instantiation_error, atomic_list_concat/3))
            ;   atomic(Sep) -> true
            ;   throw(error(type_error(atomic, Sep), atomic_list_concat/3))
            ),
            (   var(Atom) -> true
            ;   atom(Atom) -> true
            ;   throw(error(type_error(atom, Atom), atomic_list_concat/3))
            ),
            '$atomic_to_atom'(Sep, SepAtom),
            '$alc_join'(List, SepAtom, Atom).

        '$alc_check_list'(L) :-
            (   var(L) -> throw(error(instantiation_error, atomic_list_concat/3))
            ;   L == [] -> true
            ;   L = [X|Xs] ->
                (   var(X) -> throw(error(instantiation_error, atomic_list_concat/3))
                ;   atomic(X) -> true
                ;   throw(error(type_error(atomic, X), atomic_list_concat/3))
                ),
                '$alc_check_list'(Xs)
            ;   throw(error(type_error(list, L), atomic_list_concat/3))
            ).

        '$alc_join'([], _, '').
        '$alc_join'([X], _, Atom) :- !, '$atomic_to_atom'(X, Atom).
        '$alc_join'([X, Y|Xs], Sep, Atom) :-
            '$atomic_to_atom'(X, AX),
            '$alc_join'([Y|Xs], Sep, Rest),
            atom_concat(AX, Sep, P),
            atom_concat(P, Rest, Atom).

        % split Atom at each occurrence of the separator Sep.
        '$alc_split'(Atom, Sep, Parts) :-
            ( '$alc_first_sep'(Atom, Sep, B, A) ->
                sub_atom(Atom, 0, B, _, Head),
                sub_atom(Atom, _, A, 0, Tail),
                Parts = [Head|Rest],
                '$alc_split'(Tail, Sep, Rest)
            ; Parts = [Atom]
            ).
        % Before / After of the leftmost occurrence of Sep in Atom.
        '$alc_first_sep'(Atom, Sep, B, A) :-
            findall(B0, sub_atom(Atom, B0, _, _, Sep), Bs),
            Bs = [_|_],
            min_list(Bs, B),
            sub_atom(Atom, B, _, A, Sep).

        %! char_type(+Char, ?Type) | Atoms & strings | Tests or computes a character's type — alpha, alnum, digit(W), space, upper(L), to_lower(L), and so on (ASCII range).
        char_type(Char, Type) :- char_code(Char, Code), '$char_type'(Type, Code).

        '$char_type'(alpha, Code) :- '$ascii_alpha'(Code).
        '$char_type'(alnum, Code) :- ( '$ascii_alpha'(Code) -> true ; '$ascii_digit'(Code) ).
        '$char_type'(digit(W), Code) :- '$ascii_digit'(Code), W is Code - 48.
        '$char_type'(decimal_digit, Code) :- '$ascii_digit'(Code).
        '$char_type'(space, Code) :- '$ascii_space'(Code).
        '$char_type'(white, Code) :- ( Code =:= 32 -> true ; Code =:= 9 ).
        '$char_type'(end_of_line, Code) :- ( Code =:= 10 -> true ; Code =:= 13 ).
        '$char_type'(punct, Code) :-
            Code >= 33, Code =< 126,
            \+ '$ascii_alpha'(Code), \+ '$ascii_digit'(Code).
        '$char_type'(csym, Code) :-
            ( '$ascii_alpha'(Code) -> true ; '$ascii_digit'(Code) -> true ; Code =:= 95 ).
        '$char_type'(csymf, Code) :-
            ( '$ascii_alpha'(Code) -> true ; Code =:= 95 ).
        '$char_type'(upper(L), Code) :-
            Code >= 65, Code =< 90, LC is Code + 32, char_code(L, LC).
        '$char_type'(lower(U), Code) :-
            Code >= 97, Code =< 122, UC is Code - 32, char_code(U, UC).
        '$char_type'(to_lower(L), Code) :-
            ( Code >= 65, Code =< 90 -> LC is Code + 32 ; LC = Code ),
            char_code(L, LC).
        '$char_type'(to_upper(U), Code) :-
            ( Code >= 97, Code =< 122 -> UC is Code - 32 ; UC = Code ),
            char_code(U, UC).

        '$ascii_alpha'(C) :- C >= 65, C =< 90, !.
        '$ascii_alpha'(C) :- C >= 97, C =< 122.
        '$ascii_digit'(C) :- C >= 48, C =< 57.
        '$ascii_space'(32) :- !.
        '$ascii_space'(C) :- C >= 9, C =< 13.

        % ===== control, database & inspection =====

        %! false | Control | Always fails — ISO synonym of fail/0.
        false :- fail.

        %! once(:Goal) | Control | Succeeds at most once — commits to the first solution of Goal.
        once(Goal) :- call(Goal), !.

        %! ignore(:Goal) | Control | Runs Goal, succeeding whether or not Goal does.
        ignore(Goal) :- ( call(Goal) -> true ; true ).

        %! time_out(:Goal, +MilliSeconds, -Result) | Control | Runs Goal under a time limit (SICStus-compatible). Result is success, or time_out if the limit expired. NON-DETERMINISTIC: Goal keeps its solutions, and re-entering it on backtracking RESTARTS the clock, so the limit bounds each solution rather than the whole enumeration. The limit is enforced at the engine's safe points, so a goal that neither calls nor allocates can outlive it; ordinary Prolog, including a failure-driven loop like (repeat, fail), is interrupted.
        time_out(Goal, MilliSeconds, Result) :-
            Seconds is MilliSeconds / 1000,
            '$catch_begin'(Ball, '$time_out_recover'(Ball, Result)),
            '$time_out_run'(Goal, Seconds),
            '$catch_end',
            (   var(Result) ->
                Result = success
            ;   true
            ).

        '$time_out_run'(Goal, Seconds) :-
            '$timeout_start'(Seconds),
            call(Goal),
            '$timeout_stop'(Seconds).

        % Stable-address public, like '$scc_recover'/2 and '$wot_recover'/2: a
        % catch/3 recovery in a PRELUDE clause is resolved by functor id, and a
        % module-local one has no compiled address there.
        :- public '$time_out_recover'/2.
        '$time_out_recover'(Ball, Result) :-
            '$timeout_pop',
            (   '$timeout_ball'(Ball) ->
                Result = time_out
            ;   throw(Ball)
            ).

        % Starting and stopping the clock are BACKTRACKABLE, which is what makes
        % the restart happen: leaving Goal stops it, re-entering Goal on redo
        % starts a fresh one, and exhausting Goal unwinds the whole thing.
        '$timeout_start'(Seconds) :- '$timeout_push'(Seconds).
        '$timeout_start'(_) :- '$timeout_pop', fail.

        '$timeout_stop'(_) :- '$timeout_pop'.
        '$timeout_stop'(Seconds) :- '$timeout_push'(Seconds), fail.

        % The engine throws a bare '$timeout_expired' — it does not know which
        % goal it interrupted. Matched under both spellings because a catcher
        % may wrap a ball in error/2 on the way out.
        '$timeout_ball'('$timeout_expired').
        '$timeout_ball'(error('$timeout_expired', _)).

        %! call_residue_vars(:Goal, -Vars) | Attributed variables | Runs Goal, then unifies Vars with the attributed variables created during Goal that are still constrained (carry residual attributes). Needs an attribute library (e.g. use_module(library(coroutining)) for dif/2) to produce any.
        call_residue_vars(Goal, Vars) :-
            '$attv_snapshot'(S),
            call(Goal),
            '$attv_new_since'(S, Vars).

        %! time(:Goal) | Control | Calls Goal like call/1 and prints a per-answer resource report (SWI-style): inferences (Tier-0 goal dispatches), elapsed seconds, heap cells allocated, and Lips. Non-determinism is preserved - each further answer prints the cost since the previous one, and exhausting Goal prints a final report before failing. Under Tier-1 IL promotion the inference count undercounts (intra-region calls are raw branches); the REPL's default Tier-0 execution reports exact numbers.
        time(Goal) :-
            '$time_start'(Mark),
            (   call(Goal) *->
                '$time_report'(Mark)
            ;   '$time_report'(Mark),
                fail
            ).

        %! chdir(?Path) | Input / output | Arity-Prolog 1-arg form of working_directory/2. With Path unbound, returns the current directory; with Path bound, changes to it.
        chdir(Path) :- var(Path), !, working_directory(Path, Path).
        chdir(Path) :- working_directory(_, Path).

        %! append(+ListOfLists, -List) | Lists | Concatenates a list of lists (SWI library form).
        append([], []).
        append([L|Ls], As) :- append(L, Ws, As), append(Ls, Ws).

        %! flatten(+Nested, -Flat) | Lists | Flattens nested lists into a single list; a non-list element (or variable) becomes an element of Flat.
        flatten(Nested, Flat) :- '$flatten'(Nested, [], Flat0), !, Flat = Flat0.
        '$flatten'(V, T, [V|T]) :- var(V), !.
        '$flatten'([], T, T) :- !.
        '$flatten'([H|R], T, F) :- !, '$flatten'(R, T, F1), '$flatten'(H, F1, F).
        '$flatten'(X, T, [X|T]).

        %! call_with_limit(+N, :Goal) | Control | Solutions of Goal, at most the first N. Fails when N < 1.
        % Dialect shims map their names onto this (SWI solution_sequences'
        % and Trealla's limit/2).
        call_with_limit(N, Goal) :-
            ( integer(N) -> true
            ; throw(error(type_error(integer, N), call_with_limit/2)) ),
            N >= 1,
            call_nth(Goal, Nth),
            ( Nth =:= N -> ! ; true ).

        %! offset(+N, :Goal) | Control | Solutions of Goal after skipping the first N.
        offset(N, Goal) :-
            ( integer(N) -> true
            ; throw(error(type_error(integer, N), offset/2)) ),
            call_nth(Goal, Nth),
            Nth > N.

        %! variant(@Term1, @Term2) | Term inspection & construction | True when the terms are structural variants: equal up to a consistent renaming of variables (mutual subsumption).
        variant(A, B) :- subsumes_term(A, B), subsumes_term(B, A).

        %! term_singletons(@Term, -Singletons) | Term inspection & construction | Unifies Singletons with the variables occurring exactly once in Term, in order of first appearance.
        term_singletons(T, Singles) :-
            '$tsing_occurrences'(T, [], RevOcc),
            '$tsing_keep_single'(RevOcc, RevOcc, [], Singles).
        '$tsing_occurrences'(T, Acc0, Acc) :-
            (   var(T) -> Acc = [T|Acc0]
            ;   '$tsing_args'(T, 1, Acc0, Acc)
            ).
        '$tsing_args'(T, I, Acc0, Acc) :-
            functor(T, _, N),
            (   I > N -> Acc = Acc0
            ;   arg(I, T, A),
                '$tsing_occurrences'(A, Acc0, Acc1),
                I1 is I + 1,
                '$tsing_args'(T, I1, Acc1, Acc)
            ).
        % Occurrences arrive newest-first; keeping while walking that order
        % and prepending restores first-appearance order in one pass.
        '$tsing_keep_single'([], _, Singles, Singles).
        '$tsing_keep_single'([V|Vs], All, Acc, Singles) :-
            (   '$tsing_count'(All, V, 0, 1) ->
                '$tsing_keep_single'(Vs, All, [V|Acc], Singles)
            ;   '$tsing_keep_single'(Vs, All, Acc, Singles)
            ).
        '$tsing_count'([], _, N, N).
        '$tsing_count'([X|Xs], V, N0, N) :-
            (   X == V -> N1 is N0 + 1 ; N1 = N0 ),
            N1 =< 1,
            '$tsing_count'(Xs, V, N1, N).

        %! copy_term_nat(?Term, -Copy) | Term inspection & construction | copy_term/2 ignoring attributes (SWI/Trealla).
        copy_term_nat(Term, Copy) :- '$copy_term_without_attr_vars'(Term, Copy).

        %! bb_put(+Key, +Value) | Global variables | Blackboard store (SICStus/Trealla): non-backtrackable global assignment.
        % Attributed variables survive the blackboard (the SICStus/Trealla
        % contract): a value carrying attvars is stored RESIDUALIZED — the
        % attribute-free copy plus its copy_term/3 projection goals — and
        % every bb_get re-copies and re-runs the goals, so each read is an
        % independent fresh constraint set and the original variables are
        % untouched. A plain value skips the walk entirely and is stored raw
        % ('$bb_attr'/2 as user data would be misread — accepted edge).
        bb_put(Key, Value) :- '$bb_wrap'(Value, W), nb_setval(Key, W).

        '$bb_wrap'(Value, W) :-
            (   term_attvars(Value, [_|_]) ->
                copy_term(Value, Copy, Gs),
                W = '$bb_attr'(Copy, Gs)
            ;   W = Value
            ).

        %! bb_get(+Key, -Value) | Global variables | Reads a blackboard entry; FAILS when Key is unset (unlike nb_getval/2, which throws).
        bb_get(Key, Value) :-
            catch(nb_getval(Key, V0), _, fail),
            V0 \== '$bb_absent',
            '$bb_unwrap'(V0, Value).

        '$bb_unwrap'(V0, Value) :-
            (   nonvar(V0), V0 = '$bb_attr'(Copy, Gs) ->
                copy_term(Copy-Gs, Value-Gs1),
                '$bb_recall'(Gs1)
            ;   Value = V0
            ).

        '$bb_recall'([]).
        '$bb_recall'([G|Gs]) :- call(G), '$bb_recall'(Gs).

        %! bb_update(+Key, ?Old, +New) | Global variables | Unifies Old with the current value and replaces it with New; fails (leaving the entry unchanged) when Old does not match.
        bb_update(Key, Old, New) :- bb_get(Key, Old), bb_put(Key, New).

        %! bb_delete(+Key, -Value) | Global variables | Unifies Value with the current value and removes the entry.
        bb_delete(Key, Value) :- bb_get(Key, Value), nb_setval(Key, '$bb_absent').

        %! bb_b_put(+Key, +Value) | Global variables | Backtrackable blackboard assignment: the previous value is restored on backtracking.
        bb_b_put(Key, Value) :- '$bb_wrap'(Value, W), b_setval(Key, W).

        %! consult_text(+Text) | Database | Consults Text (an atom or a chars/codes list) as Prolog source — the in-language form of the embedding API's ConsultString. A module loaded this way keeps its exports scoped (no auto-import into user).
        :- public consult_text/1.
        consult_text(Text) :-
            (   var(Text) -> throw(error(instantiation_error, consult_text/1))
            ;   atom(Text) -> A = Text
            ;   Text = [C|_], integer(C) -> atom_codes(A, Text)
            ;   atom_chars(A, Text)
            ),
            '$load_text'(A, []).

        %! read_term_from_chars(+Chars, -Term, +Options) | Input / output | Reads a term from a character list, honouring read_term/2 options.
        read_term_from_chars(Chars, Term, Options) :-
            atom_chars(Atom, Chars),
            read_term_from_atom(Atom, Term, Options).

        %! write_term_to_chars(+Term, +Options, -Chars) | Input / output | Writes a term to a character list with write_term/2's options.
        write_term_to_chars(Term, Options, Chars) :-
            with_output_to(atom(Atom), write_term(Term, Options)),
            atom_chars(Atom, Chars).

        %! :(+Module, :Goal) | Control | Runtime module-qualified call: resolves Goal relative to Module (module-local first, then imports, then the global namespace / builtins). ADR-038 — an export-qualified module's own version of a builtin-named predicate (Scryer iso_ext's copy_term/3) must win for M:Goal.
        ':'(Module, Goal) :- call(Module:Goal).

        %! phrase(:Body, ?List) | Grammar | phrase(Body, List, []) — succeeds when the DCG Body derives List.
        phrase(Body, List) :- phrase(Body, List, []).
        %! phrase(:Body, ?List, ?Rest) | Grammar | Runtime DCG driver: succeeds when Body derives the difference List/Rest. Statically-known bodies are expanded at compile time; this interpreter handles a variable/list Body and control constructs at runtime.
        % SS7.6.2 for DCG bodies: a number anywhere in the control
        % skeleton makes the WHOLE body non-translatable, checked BEFORE
        % anything runs — phrase(({fail}, 1), _) raises, fail never runs.
        phrase(Body, S0, S) :- '$dcg_body_check'(Body), '$phrase'(Body, S0, S).

        '$dcg_body_check'(B) :- var(B), !.
        '$dcg_body_check'((A, B)) :- !, '$dcg_body_check'(A), '$dcg_body_check'(B).
        '$dcg_body_check'((A ; B)) :- !, '$dcg_body_check'(A), '$dcg_body_check'(B).
        '$dcg_body_check'((A -> B)) :- !, '$dcg_body_check'(A), '$dcg_body_check'(B).
        '$dcg_body_check'('|'(A, B)) :- !, '$dcg_body_check'(A), '$dcg_body_check'(B).
        '$dcg_body_check'(N) :-
            number(N), !, throw(error(type_error(callable, N), phrase/3)).
        '$dcg_body_check'(_).

        '$phrase'(V, _, _) :- var(V), !, throw(error(instantiation_error, phrase/3)).
        '$phrase'([], S0, S) :- !, S0 = S.
        '$phrase'([H|T], S0, S) :- !, append([H|T], S, S0).
        '$phrase'(!, S0, S) :- !, S0 = S.
        '$phrase'((A, B), S0, S) :- !, '$phrase'(A, S0, S1), '$phrase'(B, S1, S).
        '$phrase'((A ; B), S0, S) :- !, ( '$phrase'(A, S0, S) ; '$phrase'(B, S0, S) ).
        % '|'(A,B) written canonically: `|` is only an operator inside a DCG
        % rule body (strict ISO has no bar operator), and this is a plain
        % clause matching the alternation a DCG body term carries at runtime.
        '$phrase'('|'(A, B), S0, S) :- !, ( '$phrase'(A, S0, S) ; '$phrase'(B, S0, S) ).
        '$phrase'((A -> B), S0, S) :- !, ( '$phrase'(A, S0, S1) -> '$phrase'(B, S1, S) ).
        '$phrase'({G}, S0, S) :- !, call(G), S0 = S.
        '$phrase'(\+ A, S0, S) :- !, \+ '$phrase'(A, S0, _), S0 = S.
        '$phrase'(call(G), S0, S) :- !, call(G, S0, S).
        '$phrase'(G, S0, S) :- call(G, S0, S).

        %! phrase_from_stream(:Body, +Stream) | Grammar | Runs the DCG Body over Stream's text, read lazily in windows.
        phrase_from_stream(Body, Stream) :-
            phrase_from_stream(Body, Stream, chars).

        %! phrase_from_stream(:Body, +Stream, +Kind) | Grammar | As phrase_from_stream/2, with Kind (chars or codes) choosing the list's elements.
        phrase_from_stream(Body, Stream, Kind) :-
            '$lazy_text'(Stream, Kind, 0, Ls),
            phrase(Body, Ls).

        %! phrase_from_file(:Body, +File) | Grammar | Runs the DCG Body over File's text, read lazily; the file is closed on the way out.
        phrase_from_file(Body, File) :-
            phrase_from_file(Body, File, []).

        %! phrase_from_file(:Body, +File, +Options) | Grammar | As phrase_from_file/2; Options are open/4's, plus text_kind(chars) or text_kind(codes).
        phrase_from_file(Body, File, Options) :-
            '$lazy_kind'(Options, Kind, OpenOptions),
            setup_call_cleanup(open(File, read, Stream, OpenOptions),
                               phrase_from_stream(Body, Stream, Kind),
                               close(Stream)).

        % The delayed goal runs AFTER the binding that woke it, so Ls already
        % holds whatever the grammar unified it with. That is why the step
        % UNIFIES Ls with the window rather than binding it: the grammar's
        % [H|T] meets the packed window and peels one element out of it.
        %
        % The offset is carried explicitly because reading is a side effect
        % backtracking cannot undo: a grammar that tries one clause, fails and
        % tries the next wakes the SAME cell twice, and a plain read would hand
        % it the next characters the second time — quietly parsing an input the
        % file does not contain. '$lazy_window'/6 is idempotent per offset.
        '$lazy_text'(Stream, Kind, Offset, Ls) :-
            '$lazy_freeze'(Ls, '$lazy_text_step'(Stream, Kind, Offset, Ls)).

        '$lazy_text_step'(Stream, Kind, Offset, Ls) :-
            '$lazy_window'(Stream, Offset, 4096, Kind, Window, Length),
            ( Length =:= 0 ->
                Ls = []
            ; Next is Offset + Length,
              '$lazy_text'(Stream, Kind, Next, Ls0),
              partial_string(Window, Ls, Ls0)
            ).

        '$lazy_kind'([], chars, []).
        '$lazy_kind'([text_kind(K)|T], K, Rest) :- !, '$lazy_kind'(T, _, Rest).
        '$lazy_kind'([O|T], K, [O|Rest]) :- '$lazy_kind'(T, K, Rest).

        %! display(+Term) | Input / output | Edinburgh display/1: writes Term to current output ignoring operator definitions, unquoted.
        display(X) :- write_term(X, [ignore_ops(true)]).
        %! display(+Stream, +Term) | Input / output | Edinburgh display/2: writes Term to Stream ignoring operator definitions, unquoted.
        display(S, X) :- write_term(S, X, [ignore_ops(true)]).

        %! recorda(+Key, +Term) | Database | SWI 2-arg form of recorda/3 (reference discarded).
        recorda(K, V) :- recorda(K, V, _).
        %! recordz(+Key, +Term) | Database | SWI 2-arg form of recordz/3 (reference discarded).
        recordz(K, V) :- recordz(K, V, _).
        %! recorded(+Key, ?Term) | Database | SWI 2-arg form of recorded/3 (reference discarded); backtracks over matches.
        recorded(K, V) :- recorded(K, V, _).

        %! apply(:Goal, +ExtraArgs) | Control | Calls Goal with the list of extra arguments appended.
        apply(Goal, Extra) :-
            Goal =.. List0,
            append(List0, Extra, List),
            Call =.. List,
            call(Call).

        %! tab(+N) | Input / output | Writes N spaces to the current output stream.
        tab(N) :- ( N =< 0 -> true ; write(' '), N1 is N - 1, tab(N1) ).

        %! findall(?Template, :Goal, -List, ?Tail) | Findall & aggregation | Like findall/3 but the result is a difference list ending in Tail.
        findall(Template, Goal, List, Tail) :-
            '$check_partial_list'(List),
            '$check_partial_list'(Tail),
            findall(Template, Goal, List0),
            append(List0, Tail, List).


        %! retractall(+Head) | Database | Removes every clause whose head unifies with Head.
        % retract/1 is re-satisfiable, so a failure-driven loop retracts
        % every match; the `fail` undoes each solution's bindings, keeping
        % Head general. Facts are retracted by the head form, rules by the
        % (Head :- Body) form.
        % retractall on an UNDEFINED predicate is a silent no-op (SWI / SICStus);
        % on a STATIC one it raises permission_error. '$retractall_modifiable'
        % succeeds only for a dynamic predicate, so the retract loop runs just
        % then; undefined makes it fail into the `true` arm.
        % The qualified form peels first: dynamics are flat-global, so M: on
        % a database operation validates the module slot and drops.
        retractall(H0) :-
            nonvar(H0), H0 = ':'(_, _), !,
            '$strip_module'(H0, M, H),
            (   var(M) -> throw(error(instantiation_error, _))
            ;   \+ atom(M) -> throw(error(type_error(atom, M), _))
            ;   true
            ),
            retractall(H).
        retractall(Head) :-
            ( '$retractall_modifiable'(Head)
            -> ( retract(Head), fail ; true ),
               ( retract((Head :- _)), fail ; true )
            ; true ).

        %! listing | Database | Lists the clauses of every user-defined predicate — consulted or asserted, never builtins or library predicates.
        listing :-
            '$listable_predicates'(All),
            '$listing_all'(All).

        %! listing(+Spec) | Database | Lists the clauses of the user-defined predicate named by Spec (Name, Name/Arity, or Module:Spec).
        % when no predicate matches, print a comment so
        % the user sees feedback instead of a silent `true.`
        % The qualified form M:Spec must come first: M:Name/Arity parses as
        % (M:Name)/Arity, which the plain Name/Arity clause would swallow.
        % It lists what M itself defines (the current_predicate(M:PI) set).
        listing(Spec) :-
            nonvar(Spec), '$listing_qualified'(Spec, M, Name, Arity), !,
            (   var(M) -> throw(error(instantiation_error, _))
            ;   \+ atom(M) -> throw(error(type_error(atom, M), _))
            ;   true
            ),
            findall(qpi(N, A),
                    ( '$module_predicate_enum'(M, N/A),
                      N = Name,
                      ( var(Arity) -> true ; A = Arity ) ),
                    PIs),
            (   PIs == [] ->
                write('% nothing to list for '), write(M), write(':'),
                write(Name),
                ( integer(Arity) -> write('/'), write(Arity) ; true ), nl
            ;   '$listing_qpis'(PIs)
            ).
        listing(Name/Arity) :-
            !,
            '$listable_predicates'(All),
            ( member(pi(Name, Arity, Dyn), All) ->
                '$listing_pred'(Name, Arity, Dyn)
            ;
                write('% '), write(Name), write('/'), write(Arity),
                write(' not defined'), nl
            ).
        listing(Name) :-
            '$listable_predicates'(All),
            '$listing_named'(All, Name, false, Found),
            ( Found == true -> true
            ;
                write('% no predicate matches '), write(Name), nl
            ).

        % The two spellings of a qualified listing spec: (M:Name)/Arity — the
        % operator parse of M:Name/Arity — and M:Name / M:(Name/Arity).
        % Fails for an unqualified spec (the plain clauses then apply).
        '$listing_qualified'(':'(Q, R), M, Name, Arity) :- !,
            '$strip_module'(':'(Q, R), M, I),
            (   var(I)  -> Name = I
            ;   I = N/A -> Name = N, Arity = A
            ;   atom(I) -> Name = I
            ;   throw(error(type_error(predicate_indicator, ':'(Q, R)), _))
            ).
        '$listing_qualified'('/'(Q, Arity), M, Name, Arity) :-
            nonvar(Q), Q = ':'(_, _),
            '$strip_module'(Q, M, Name).

        '$listing_qpis'([]).
        '$listing_qpis'([qpi(N, A)|Rest]) :-
            '$listable_predicates'(All),
            (   member(pi(N, A, Dyn), All) -> '$listing_pred'(N, A, Dyn)
            ;   true
            ),
            '$listing_qpis'(Rest).

        '$listing_all'([]).
        '$listing_all'([pi(Name, Arity, Dyn)|Rest]) :-
            '$listing_pred'(Name, Arity, Dyn),
            '$listing_all'(Rest).

        '$listing_named'([], _, Found, Found).
        '$listing_named'([pi(Name, Arity, Dyn)|Rest], Want, FoundIn, FoundOut) :-
            ( Name == Want ->
                '$listing_pred'(Name, Arity, Dyn),
                Found1 = true
            ;
                Found1 = FoundIn
            ),
            '$listing_named'(Rest, Want, Found1, FoundOut).

        % print one predicate: a `:- dynamic` header for a dynamic
        % predicate, then a clause per line, then a blank separator
        % line. the actual clause printing routes through
        % the engine's '$listing_pred_source'/2 which walks the AST
        % directly so variable names from the source survive
        % (clause/2 + write/1 lost them through the heap round-trip).
        '$listing_pred'(Name, Arity, Dyn) :-
            ( Dyn == true ->
                write(':- dynamic '), write(Name), write('/'), write(Arity),
                write('.'), nl
            ; true
            ),
            '$listing_pred_source'(Name, Arity),
            nl.

        %! format(+Format) | Input / output | Like format/2 with no arguments.
        format(Format) :- format(Format, []).

        %! format_to_atom(-Atom, +Format, +Args) | Input / output | Like format/2 but captures the formatted output into an atom.
        format_to_atom(Atom, Format, Args) :-
            with_output_to(atom(Atom), format(Format, Args)).

        %! with_output_to(+Sink, :Goal) | Input / output | Runs Goal once, capturing its output into the atom(A) or string(S) sink.
        % The goal runs in the LIVE engine (its op/3 / assertz side effects
        % survive — a sub-engine would swallow them); the capture is exposed
        % whether the goal succeeded, failed or raised (the SWI convention),
        % which is why every arm passes through '$wot_end'. Uses
        % '$catch_begin'/'$catch_end' with a stable-address public recovery —
        % an inline catch(...) in a prelude clause has no compiled address
        % (see setup_call_cleanup above).
        with_output_to(Sink, Goal) :-
            '$wot_begin'(Sink),
            '$catch_begin'(E, '$wot_recover'(Sink, E)),
            ( call(Goal) -> R = t ; R = f ),
            '$catch_end',
            '$wot_end'(Sink),
            R == t.
        '$wot_recover'(Sink, E) :- '$wot_end'(Sink), throw(E).

        % ===== tabling =====
        % A `:- table p/N` predicate is transformed at consult time. Its
        % clauses are split: base clauses (no tabled body call) become
        % '$tbase$p'/N, recursive clauses become '$trec$p'/N with the
        % single tabled body literal turned into a '$tbl_consume' call. A
        % driver clause routes p through '$table_call'.
        %
        % '$table_call' drives a *semi-naive* fixpoint: a subgoal's base
        % answers are its first delta, and each round re-derives only what
        % a producer's delta makes newly possible ('$tbl_consume' yields a
        % producer's delta, not its whole answer set). A clause with two
        % or more tabled literals, or a tabled call nested in a control
        % construct, is left undifferentiated and re-run every round (it
        % stays correct, just not accelerated).
        %
        % The table is the runtime dynamic store: answers, deltas and
        % subgoals are individual asserted facts, so every update is O(1)
        % (a list-per-subgoal would copy O(n) on every assert). It is read
        % with clause/2 — a direct call to a dynamic predicate sees only
        % the query-setup snapshot, whereas clause/2 consults the live
        % store, so writes made earlier in the same query are visible.
        % Duplicate answers are filtered by the engine-backed '$tbl_seen'
        % set, an O(1) test — the semi-naive fixpoint would be quadratic
        % if it instead scanned the asserted answers.
        %
        % The '$tbl_running' flag is raised before registering, because
        % registration computes base answers and a base clause of a
        % *complex* predicate can call a tabled predicate — that nested
        % call must take the consumer path, not start a second fixpoint.
        '$table_call'(Goal, BaseRun, RecRun) :-
            '$table_key'(Goal, Key),
            ( '$tbl_is_running' ->
                '$table_register'(Key, Goal, BaseRun, RecRun),
                '$table_emit'(Key, Goal)
            ; assertz('$tbl_running'),
              '$table_register'(Key, Goal, BaseRun, RecRun),
              '$table_seminaive',
              retract('$tbl_running'),
              '$table_emit'(Key, Goal)
            ).

        % canonical (ground) key — variant subgoals share one entry.
        '$table_key'(Goal, Key) :- copy_term(Goal, Key), numbervars(Key, 0, _).

        '$tbl_is_running' :- clause('$tbl_running', true), !.

        % register a subgoal. A new one has its base clauses evaluated at
        % once — base clauses make no tabled call, so this is self-
        % contained — and the result becomes both its answers and its
        % first pending delta.
        '$table_register'(Key, Goal, BaseRun, RecRun) :-
            ( clause('$tbl_subgoal'(Key, _, _, _), true) -> true
            ; assertz('$tbl_subgoal'(Key, Goal, BaseRun, RecRun)),
              assertz('$tbl_fresh'(Key)),
              findall(Goal, call(BaseRun), Raw),
              '$table_absorb'(Key, Raw)
            ).

        % file each derived answer that is new for this subgoal — '$tbl_seen'
        % both tests and records — as both an answer and a pending delta.
        '$table_absorb'(_, []).
        '$table_absorb'(Key, [A|As]) :-
            ( '$tbl_seen'(Key - A)
              -> assertz('$tbl_ans'(Key, A)), assertz('$tbl_newd'(Key, A))
              ;  true
            ),
            '$table_absorb'(Key, As).

        '$table_emit'(Key, Goal) :- clause('$tbl_ans'(Key, Goal), true).

        % the differentiated tabled call. It normally yields the producer
        % subgoal's delta (the answers gained in the previous round); but
        % when the *consuming* subgoal is running its first round (mode
        % full) it yields the producer's whole answer set — a freshly
        % discovered consumer must catch up on deltas emitted before it
        % existed.
        '$tbl_consume'(Goal, BaseRun, RecRun) :-
            '$table_key'(Goal, Key),
            '$table_register'(Key, Goal, BaseRun, RecRun),
            ( clause('$tbl_mode'(full), true)
              -> clause('$tbl_ans'(Key, Goal), true)
              ;  clause('$tbl_delta'(Key, Goal), true)
            ).

        % semi-naive fixpoint: each iteration commits the pending deltas
        % and re-runs every subgoal's recursive clauses against them.
        % Running '$trec' is also what discovers new subgoals (its
        % '$tbl_consume' calls register them), so a round runs even when
        % every delta is empty; the loop ends when a round adds no answer
        % and discovers no new subgoal. The loop recurses once per round,
        % so a fixpoint deeper than the control stack (very long recursive
        % chains) overflows — see the notes.
        '$table_seminaive' :-
            '$table_count'(Before),
            '$table_commit',
            '$table_round',
            '$table_count'(After),
            ( ( '$table_progress' ; After > Before ) -> '$table_seminaive'
            ; true
            ).
        '$table_count'(N) :-
            findall(x, clause('$tbl_subgoal'(_, _, _, _), true), L), length(L, N).
        '$table_progress' :- clause('$tbl_newd'(_, _), true), !.

        % the pending deltas ('$tbl_newd') become the deltas the next round
        % consumes; old deltas are discarded.
        '$table_commit' :-
            retractall('$tbl_delta'(_, _)),
            findall(K - A, clause('$tbl_newd'(K, A), true), Pairs),
            retractall('$tbl_newd'(_, _)),
            '$table_install'(Pairs).
        '$table_install'([]).
        '$table_install'([K - A|Rest]) :-
            assertz('$tbl_delta'(K, A)),
            '$table_install'(Rest).

        '$table_round' :-
            findall(K, clause('$tbl_subgoal'(K, _, _, _), true), Keys),
            '$table_round_each'(Keys).
        '$table_round_each'([]).
        '$table_round_each'([K|Ks]) :-
            clause('$tbl_subgoal'(K, G, _, Rec), true),
            ( retract('$tbl_fresh'(K)) -> '$tbl_set_mode'(full)
            ; '$tbl_set_mode'(delta)
            ),
            findall(G, call(Rec), Raw),
            '$table_absorb'(K, Raw),
            '$table_round_each'(Ks).

        '$tbl_set_mode'(M) :-
            ( retract('$tbl_mode'(_)) -> true ; true ),
            assertz('$tbl_mode'(M)).

        % ----- tabled negation: well-founded semantics -----
        % `\+ G` over a tabled goal is rewritten at consult time to
        % '$tbl_negate'(G). A monotone fixpoint cannot read a negated
        % subgoal incrementally, so a program with tabled negation is
        % evaluated by the *alternating fixpoint*. W(K) is one tabled
        % least-fixpoint in which `\+ a` succeeds iff a is not in the
        % assumption set K; iterating W from the empty set yields an
        % increasing chain (limit U — the well-founded *true* atoms) and a
        % decreasing chain (limit O), with U subset-of O. O minus U is the
        % *undefined* atoms. This terminates on negative cycles — e.g.
        % `p :- \+ p` makes p undefined — where plain SLD would loop.
        %
        % A tabled call routes through '$tbl_dispatch': inside a W-run
        % ('$wfs_active') it is a plain '$table_call'; at the top level of
        % a program that uses tabled negation ('$wfs_mode', a fact the
        % consult-time transform adds) it runs the alternating fixpoint.
        '$tbl_dispatch'(Goal, Base, Rec) :-
            ( clause('$wfs_active', true) -> '$table_call'(Goal, Base, Rec)
            ; clause('$wfs_mode', true)   -> '$wfs_query'(Goal)
            ; '$table_call'(Goal, Base, Rec)
            ).

        % during a W-run `\+ G` is decided against the assumption K — but
        % G is still run first (for its side effect of registering its
        % subgoal) so its atoms are part of the model the fixpoint builds.
        '$tbl_negate'(Goal) :-
            ( call(Goal), fail ; true ),
            \+ clause('$wfs_k'(Goal), true).

        '$wfs_query'(Goal) :-
            assertz('$wfs_active'),
            '$wfs_solve'(Goal, U, _),
            retractall('$wfs_active'),
            member(Goal, U).

        %! well_founded(+Goal, -Status) | Database | The well-founded truth value of a tabled Goal — true, false or undefined.
        well_founded(Goal, Status) :-
            assertz('$wfs_active'),
            '$wfs_solve'(Goal, U, O),
            retractall('$wfs_active'),
            ( '$wfs_memq'(Goal, U) -> Status = true
            ; '$wfs_memq'(Goal, O) -> Status = undefined
            ; Status = false
            ).

        % the alternating fixpoint: U = well-founded true atoms, O the
        % over-estimate; O minus U are undefined.
        '$wfs_solve'(Goal, U, O) :-
            '$wfs_eval'(Goal, [], K1),
            '$wfs_iterate'(Goal, [], K1, U, O).

        % A = K(n-2), B = K(n-1); compute K(n) = W(B). Once K(n) = K(n-2)
        % the sequence has entered its period-2 cycle and {B, K(n)} are
        % the two limits — the smaller is U, the larger O.
        '$wfs_iterate'(Goal, A, B, U, O) :-
            '$wfs_eval'(Goal, B, Kn),
            ( Kn == A
              -> ( '$wfs_subset'(Kn, B) -> U = Kn, O = B ; U = B, O = Kn )
              ;  '$wfs_iterate'(Goal, B, Kn, U, O)
            ).

        % W(K): one tabled least-fixpoint with `\+` resolved against K;
        % yields the sorted set of every atom derived.
        '$wfs_eval'(Goal, K, Atoms) :-
            abolish_all_tables,
            retractall('$wfs_k'(_)),
            '$wfs_install'(K),
            ( call(Goal), fail ; true ),   % run for side effect; do not bind Goal
            findall(A, clause('$tbl_ans'(_, A), true), Raw),
            sort(Raw, Atoms).
        '$wfs_install'([]).
        '$wfs_install'([A|As]) :- assertz('$wfs_k'(A)), '$wfs_install'(As).

        '$wfs_subset'([], _).
        '$wfs_subset'([X|Xs], Ys) :- '$wfs_memq'(X, Ys), '$wfs_subset'(Xs, Ys).
        '$wfs_memq'(X, [Y|Ys]) :- ( X == Y -> true ; '$wfs_memq'(X, Ys) ).

        % ----- table invalidation -----
        %! abolish_all_tables | Database | Discards every tabled answer; later queries recompute against the current program.
        abolish_all_tables :-
            retractall('$tbl_subgoal'(_, _, _, _)),
            retractall('$tbl_ans'(_, _)),
            retractall('$tbl_delta'(_, _)),
            retractall('$tbl_newd'(_, _)),
            retractall('$tbl_fresh'(_)),
            retractall('$tbl_mode'(_)),
            retractall('$tbl_running'),
            retractall('$tbl_neg_cache'(_, _)),
            '$tbl_seen_clear'.

        %! abolish_table(+PredicateIndicator) | Database | Discards the tabled answers of one predicate, given as Name/Arity.
        abolish_table(Name/Arity) :-
            functor(Template, Name, Arity),
            findall(K,
                ( clause('$tbl_subgoal'(K, G, _, _), true), \+ \+ G = Template ),
                Keys),
            '$tbl_drop_each'(Keys),
            retractall('$tbl_neg_cache'(_, _)),
            '$tbl_seen_clear'.
        '$tbl_drop_each'([]).
        '$tbl_drop_each'([K|Ks]) :-
            retractall('$tbl_subgoal'(K, _, _, _)),
            retractall('$tbl_ans'(K, _)),
            retractall('$tbl_delta'(K, _)),
            retractall('$tbl_newd'(K, _)),
            retractall('$tbl_fresh'(K)),
            '$tbl_drop_each'(Ks).

        % SICStus/Scryer library(atts) storage primitives — the Prolog half of the
        % put_atts/get_atts shim (the C# half is '$attr_modules'/2). Each module M
        % keeps a LIST of its attribute terms on the variable via put_attr/get_attr;
        % '$get_attr_list' flattens every module's list into [M:Attr, ...].
        % '$put_to_attr_list'/'$get_from_attr_list'/'$del_from_attr_list' are
        % C# builtins (AttvarBuiltins) — the Prolog walks were the hottest
        % predicates of a clpz solve.
        '$term_attributed_variables'(T, Vs) :- term_attvars(T, Vs).
        '$get_attr_list'(V, Ls) :- '$attr_modules'(V, Ms), '$attr_collect'(Ms, V, Ls).
        '$attr_collect'([], _, []).
        '$attr_collect'([M|Ms], V, Ls) :-
            ( get_attr(V, M, As) -> '$attr_pairs'(As, M, Ls, Ls1) ; Ls = Ls1 ),
            '$attr_collect'(Ms, V, Ls1).
        '$attr_pairs'([], _, Ls, Ls).
        '$attr_pairs'([A|As], M, [M:A|Ls0], Ls) :- '$attr_pairs'(As, M, Ls0, Ls).

        % Direct put_atts/3 & get_atts/3 (explicit module) — the usable SICStus/
        % Scryer attribute API without loading library(atts). The +Attr / -Attr /
        % bare-Attr modes match atts: +Attr and bare-Attr set/add, -Attr removes
        % (put) or checks-absent (get). (Scryer's put_atts/2 / get_atts/2 are the
        % module-implicit forms library(atts) generates per :- attribute; the
        % 3-arg forms are what its goal_expansion lowers a call to.)
        put_atts(V, M, +Attr) :- !, '$put_to_attr_list'(V, M, Attr).
        put_atts(V, M, -Attr) :- !, '$del_from_attr_list'(V, M, Attr).
        put_atts(V, M, Attr)  :- '$put_to_attr_list'(V, M, Attr).
        get_atts(V, M, +Attr) :- !, '$get_from_attr_list'(V, M, Attr).
        get_atts(V, M, -Attr) :- !, '$absent_attr'(V, M, Attr).
        get_atts(V, M, Attr)  :- '$get_from_attr_list'(V, M, Attr).
        '$absent_attr'(V, M, Attr) :-
            ( '$get_from_attr_list'(V, M, Attr) -> false ; true ).

        % strip_module(+MG, -Module, -Goal): remove a Module:Goal qualifier.
        % Scryer's library(loader) exports it (a compiled-in built-in, so no file
        % to resolve); dcgs.pl's goal_expansion and other libraries call it. A bare
        % goal keeps its default module (user). Single level, which is all real code
        % writes.
        strip_module(MG, M, G) :-
            ( nonvar(MG), MG = M0:G0 -> M = M0, G = G0
            ; G = MG, ( var(M) -> M = user ; true ) ).

        % '$skip_max_list'(?Length, ?Max, ?List, ?Tail): walk List's spine,
        % stopping after Max elements (when Max is a bound integer) or at the
        % first non-cons tail. Length is the number walked, Tail the term reached.
        % A Scryer/SWI built-in that library(error)'s must_be(list,_) and others
        % rely on for proper/partial-list checking.
        '$skip_max_list'(Length, Max, List, Tail) :-
            '$skip_max_list_'(List, Max, 0, Length, Tail).
        '$skip_max_list_'(List, Max, N, N, List) :- integer(Max), N >= Max, !.
        '$skip_max_list_'([H|T], Max, N0, N, Tail) :-
            !, N1 is N0 + 1, '$skip_max_list_'(T, Max, N1, N, Tail).
        '$skip_max_list_'(List, _, N, N, List).
        """;
}
