namespace Shumway.Embedding;

/// <summary>
/// The CLP(FD) library — constraint logic programming over finite
/// integer domains. It is an <em>opt-in</em> module: an embedder calls
/// <see cref="PrologEngine.UseClpfd"/> to consult it, so engines that do
/// not need constraints carry none of its weight.
///
/// <para>The library is ordinary Prolog built on the Phase-4 attributed
/// variable foundation. An FD variable carries a <c>clpfd</c> attribute
/// <c>fd(Domain, Propagators)</c> — its current domain (a sorted list of
/// disjoint <c>L-H</c> intervals, endpoints integers or <c>inf</c>/<c>sup</c>)
/// and the list of suspended propagator goals. Posting a constraint
/// suspends propagators on the involved variables and runs them once;
/// narrowing a domain re-runs the watchers to a fixpoint. Binding an FD
/// variable to an integer fires the <c>verify_attributes/4</c> hook,
/// which checks domain membership and re-runs the propagators.</para>
///
/// <para>The library delivers the core plus the six arithmetic constraints
/// <c>#=</c>, <c>#\=</c>, <c>#&lt;</c>, <c>#&gt;</c>, <c>#=&lt;</c>,
/// <c>#&gt;=</c> over additive expressions (<c>+</c>, <c>-</c>, unary
/// <c>-</c>); multiplication (<c>*</c>) with bounds
/// consistency and the labeling predicates <c>label/1</c>,
/// <c>labeling/2</c> (options <c>leftmost</c>/<c>ff</c> and
/// <c>up</c>/<c>down</c>) and <c>indomain/1</c>; the
/// <c>all_different/1</c> / <c>all_distinct/1</c> global constraint and
/// reification: <c>#&lt;==&gt;</c>, <c>#==&gt;</c>, <c>#&lt;==</c> and
/// the boolean connectives <c>#/\</c>, <c>#\/</c>, <c>#\</c>;
/// the remaining arithmetic expression functions <c>min</c>,
/// <c>max</c>, <c>abs</c> and <c>//</c>, and the <c>sum/3</c>
/// constraint; and <c>all_distinct/1</c> with
/// Hall-interval pruning, <c>scalar_product/4</c> is added, and
/// <c>//</c> accepts a variable divisor.</para>
/// </summary>
internal static class Clpfd
{
    public const string ModuleName = "clpfd";

    public const string Source = """
        :- module(clpfd).

        :- op(450, xfx, ..).
        :- op(700, xfx, #=).
        :- op(700, xfx, #\=).
        :- op(700, xfx, #<).
        :- op(700, xfx, #>).
        :- op(700, xfx, #=<).
        :- op(700, xfx, #>=).
        :- op(700, xfx, in).
        :- op(700, xfx, ins).
        :- op(720, yfx, #/\).
        :- op(740, yfx, #\/).
        :- op(760, yfx, #<==>).
        :- op(760, yfx, #==>).
        :- op(760, yfx, #<==).
        % GNU-Prolog spells the reification operators with a single arrow,
        % and uses ## for boolean exclusive-or.
        :- op(760, yfx, #<=>).
        :- op(760, yfx, #=>).
        :- op(760, yfx, #<=).
        :- op(720, yfx, ##).

        :- public '#='/2.
        :- public '#\\='/2.
        :- public '#<'/2.
        :- public '#>'/2.
        :- public '#=<'/2.
        :- public '#>='/2.
        :- public 'in'/2.
        :- public 'ins'/2.
        :- public '$fd_lt'/2.
        :- public '$fd_le'/2.
        :- public '$fd_neq'/2.
        :- public '$fd_plus'/3.
        :- public '$fd_times'/3.
        :- public '$fd_min'/3.
        :- public '$fd_max'/3.
        :- public '$fd_abs'/2.
        :- public '$fd_idiv'/3.
        :- public '$fd_linear'/4.
        :- public '$fd_set'/3.
        :- public '$fd_reif'/4.
        :- public '$fd_alldiff'/1.
        :- public sum/3.
        :- public scalar_product/4.
        :- public verify_attributes/4.
        :- public clpfd_attr_goals/3.
        :- public label/1.
        :- public labeling/2.
        :- public indomain/1.
        :- public all_different/1.
        :- public all_distinct/1.
        :- public '#<==>'/2.
        :- public '#==>'/2.
        :- public '#<=='/2.
        :- public '#/\\'/2.
        :- public '#\\/'/2.
        :- public '#\\'/1.

        % GNU-Prolog FD compatibility shim: the ExamplesFD corpus
        % uses GProlog's fd_* primitives, which map onto the SWI/SICStus-style
        % clpfd above. Aliases so those programs run unchanged.
        :- public fd_domain/3.
        :- public fd_labeling/1.
        :- public fd_labelingff/1.
        :- public fd_all_different/1.
        :- public fd_set_vector_max/1.
        :- public fd_atmost/3.
        :- public fd_exactly/3.
        :- public fd_only_one/1.
        :- public fd_at_most_one/1.
        :- public '#<=>'/2.
        :- public '#=>'/2.
        :- public '#<='/2.
        :- public '##'/2.

        % the prefix-negation operator is declared after the public block:
        % once `#\` is a prefix operator, the quoted atom in `'#\\'/1`
        % above would be misparsed as an operator awaiting an argument.
        :- op(710, fy, #\).

        % ===== bound order: inf < every integer < sup =====
        % clpfd_ble/blt/bmin/bmax, clpfd_add_lo/hi, clpfd_sub_lo/hi, clpfd_bneg,
        % clpfd_bmul, clpfd_bfloordiv, clpfd_bceildiv are now native builtins
        % (FdBoundBuiltins): a bound is a plain long with inf/sup as
        % the long sentinels, so the chain of `A == inf` / `B == sup` tests that
        % dominated FD solving (≈1.36M ==/2 calls on alpha) becomes one native
        % comparison. Removing the Prolog clauses lets the module-local calls
        % fall through to the builtins.

        % truncating (toward zero) division of a bound by a positive K.
        clpfd_btruncdiv(C, K, R) :-
            ( C == inf -> R = inf
            ; C == sup -> R = sup
            ; R is C // K
            ).

        % ===== domains: opaque C# interval objects =====
        % A domain is now a single immutable C# object (a Foreign cell), not a
        % Prolog interval list. The dom_* helpers are thin wrappers over the
        % native $dom_* builtins, so every propagator that calls them is
        % unchanged; only the few predicates that destructured the interval list
        % (narrow, $fd_set, labeling enumeration, reification, projection) are
        % rewritten below. Profiling showed the interpreted interval walking
        % dominated FD solving.
        clpfd_universal(D)        :- '$dom_universal'(D).
        clpfd_iv(L, H, IV)        :- '$dom_new'(L, H, IV).
        clpfd_dom_min(D, L)       :- '$dom_min'(D, L).
        clpfd_dom_max(D, H)       :- '$dom_max'(D, H).
        clpfd_in_dom(V, D)        :- '$dom_contains'(D, V).
        clpfd_dom_above(D, B, Out):- '$dom_above'(D, B, Out).
        clpfd_dom_below(D, B, Out):- '$dom_below'(D, B, Out).
        clpfd_dom_del(D, V, Out)  :- '$dom_del'(D, V, Out).
        clpfd_dom_isect(A, B, Out):- '$dom_isect'(A, B, Out).
        clpfd_dom_size(D, N)      :- '$dom_size'(D, N).

        % generic list append — still used for propagator (Props) lists.
        clpfd_app([], L, L).
        clpfd_app([H|T], L, [H|R]) :- clpfd_app(T, L, R).

        % render a domain as an `in` expression for residual-constraint display.
        clpfd_dom_expr(D, Expr) :- '$dom_intervals'(D, IVs), clpfd_iv_expr(IVs, Expr).
        clpfd_iv_expr([L-H], L..H) :- !.
        clpfd_iv_expr([L-H|T], (L..H \/ Rest)) :- clpfd_iv_expr(T, Rest).

        % ===== FD variables =====
        % the domain of X: a singleton for an integer, the attribute's domain
        % for an FD variable, the universal domain otherwise.
        clpfd_dom_of(X, D) :-
            ( integer(X) -> '$dom_new'(X, X, D)
            ; get_attr(X, clpfd, fd(D0, _)) -> D = D0
            ; '$dom_universal'(D)
            ).

        clpfd_makevar(X) :-
            ( integer(X) -> true
            ; get_attr(X, clpfd, _) -> true
            ; var(X) -> '$dom_universal'(U), put_attr(X, clpfd, fd(U, []))
            ; throw(error(type_error(integer, X), _))
            ).

        % narrow X's domain to NewDom (a domain object): empty fails, a singleton
        % binds X, an unchanged domain is a no-op, else store + re-run propagators.
        clpfd_narrow(X, NewDom) :-
            ( integer(X) -> '$dom_contains'(NewDom, X)
            ; get_attr(X, clpfd, fd(OldDom, Props)) ->
                ( '$dom_same'(NewDom, OldDom) -> true
                ; '$dom_empty'(NewDom) -> fail
                ; '$dom_singleton'(NewDom, K) -> X = K
                ; put_attr(X, clpfd, fd(NewDom, Props)), clpfd_run(Props)
                )
            ; '$dom_empty'(NewDom) -> fail
            ; '$dom_singleton'(NewDom, K) -> X = K
            ; put_attr(X, clpfd, fd(NewDom, []))
            ).

        clpfd_narrow_bounds(X, Lo, Hi) :-
            clpfd_dom_of(X, D),
            clpfd_dom_above(D, Hi, D1),
            clpfd_dom_below(D1, Lo, D2),
            clpfd_narrow(X, D2).

        clpfd_run([]).
        clpfd_run([P|Ps]) :- call(P), clpfd_run(Ps).

        % suspend a propagator on every FD variable it watches, then run it.
        clpfd_post(Prop, Vars) :- clpfd_watch(Vars, Prop), call(Prop).
        clpfd_watch([], _).
        clpfd_watch([V|Vs], Prop) :-
            ( get_attr(V, clpfd, fd(D, Ps)) -> put_attr(V, clpfd, fd(D, [Prop|Ps]))
            ; true
            ),
            clpfd_watch(Vs, Prop).

        % move/merge a clpfd attribute onto V and re-propagate (used by the
        % verify_attributes hook when an FD variable is aliased to another).
        '$fd_set'(V, Dom, Props) :-
            ( '$dom_singleton'(Dom, K) -> V = K
            ; put_attr(V, clpfd, fd(Dom, Props)), clpfd_run(Props)
            ).

        % ===== the verify_attributes hook =====
        % fired when an FD variable is bound. An integer must lie in the
        % domain; aliasing to another FD variable intersects the domains.
        verify_attributes(clpfd, fd(Dom, Props), Value, Goals) :-
            ( integer(Value) ->
                clpfd_in_dom(Value, Dom),
                Goals = Props
            ; var(Value) ->
                ( get_attr(Value, clpfd, fd(Dom2, Props2)) ->
                    clpfd_dom_isect(Dom, Dom2, Dom3),
                    \+ '$dom_empty'(Dom3),
                    clpfd_app(Props, Props2, AllProps),
                    Goals = ['$fd_set'(Value, Dom3, AllProps)]
                ; Goals = ['$fd_set'(Value, Dom, Props)]
                )
            ; fail
            ).

        % ===== projection: a constrained variable prints as `V in Dom` =====
        % attribute_goals/4 is dynamic (pre-declared by the prelude); a
        % dynamic clause's body is not module-mangled, so it delegates to
        % the public clpfd_attr_goals/3, whose body resolves clpfd locals.
        %
        % We emit the domain (`V in L..H`) plus each suspended propagator
        % translated to its user-facing form (`X #< Y`, `A + B #= C`, ...).
        % A propagator is stored on every variable it watches, so projecting
        % the same one through every variable would duplicate it; the
        % "owner-first-var" rule emits a propagator exactly once — through
        % whichever of its variable args appears first.
        attribute_goals(clpfd, Attr, V, Goals) :- clpfd_attr_goals(Attr, V, Goals).
        clpfd_attr_goals(fd(Dom, Props), V, Goals) :-
            clpfd_dom_expr(Dom, Expr),
            clpfd_props_owned_by(Props, V, PropGoals),
            Goals = [V in Expr | PropGoals].

        % Emit those propagators whose first variable argument == V and
        % whose user-facing form isn't already subsumed by the domain
        % (clpfd_prop_to_goal/2 fails for those — e.g. `$fd_lt(X, 10)`
        % only narrows X's domain, which is already projected).
        clpfd_props_owned_by([], _, []).
        clpfd_props_owned_by([P|Ps], V, Goals) :-
            ( clpfd_prop_owner(P, V), clpfd_prop_to_goal(P, G) ->
                Goals = [G|Rest], clpfd_props_owned_by(Ps, V, Rest)
            ; clpfd_props_owned_by(Ps, V, Goals)
            ).

        clpfd_prop_owner(P, V) :-
            P =.. [_|Args],
            clpfd_first_var(Args, FV),
            FV == V.

        clpfd_first_var([A|_], A) :- var(A), !.
        clpfd_first_var([A|_], V) :- nonvar(A), is_list(A), !, clpfd_first_var(A, V).
        clpfd_first_var([_|R], V) :- clpfd_first_var(R, V).

        % Translate each internal propagator back to its source-level
        % form. A binary comparison against a ground integer is fully
        % captured by the variable's domain projection — we drop the
        % propagator in that case by demanding both args be vars
        % (matching SWI's top-level: `?- A #> 5, A #< 10.` prints just
        % `A in 6..9.`, no `5 #< A, A #< 10` residue).
        clpfd_prop_to_goal('$fd_lt'(X, Y),    (X #< Y))   :- var(X), var(Y).
        clpfd_prop_to_goal('$fd_le'(X, Y),    (X #=< Y))  :- var(X), var(Y).
        clpfd_prop_to_goal('$fd_neq'(X, Y),   (X #\= Y))  :- var(X), var(Y).
        clpfd_prop_to_goal('$fd_plus'(A,B,C), (A + B #= C)).
        clpfd_prop_to_goal('$fd_times'(A,B,C),(A * B #= C)).
        clpfd_prop_to_goal('$fd_min'(A,B,C),  (min(A,B) #= C)).
        clpfd_prop_to_goal('$fd_max'(A,B,C),  (max(A,B) #= C)).
        clpfd_prop_to_goal('$fd_abs'(A,C),    (abs(A) #= C)).
        clpfd_prop_to_goal('$fd_idiv'(A,B,C), (A // B #= C)).
        clpfd_prop_to_goal('$fd_alldiff'(Vs), all_distinct(Vs)).

        % ===== in / ins =====
        %! in(?Var, +Domain) | CLP(FD) — domains | Constrains a variable to a finite domain (e.g. X in 1..9).
        %! ins(?Vars, +Domain) | CLP(FD) — domains | Constrains every variable in a list to a finite domain.
        'in'(X, Spec) :-
            ( integer(Spec) -> X #= Spec
            ; Spec = L..H ->
                clpfd_makevar(X),
                clpfd_dom_of(X, D),
                clpfd_iv(L, H, IV),
                clpfd_dom_isect(D, IV, D2),
                clpfd_narrow(X, D2)
            ; throw(error(type_error(fd_domain, Spec), _))
            ).

        'ins'([], _).
        'ins'([X|Xs], Spec) :- 'in'(X, Spec), 'ins'(Xs, Spec).

        % ===== arithmetic expressions reduced to an FD term =====
        clpfd_expr(E, E) :- integer(E), !.
        clpfd_expr(E, V) :- var(E), !, clpfd_makevar(E), V = E.
        clpfd_expr(A + B, V) :- !,
            clpfd_expr(A, VA), clpfd_expr(B, VB),
            clpfd_makevar(V),
            clpfd_post('$fd_plus'(VA, VB, V), [VA, VB, V]).
        clpfd_expr(A - B, V) :- !,
            clpfd_expr(A, VA), clpfd_expr(B, VB),
            clpfd_makevar(V),
            clpfd_post('$fd_plus'(V, VB, VA), [V, VB, VA]).
        clpfd_expr(A * B, V) :- !,
            clpfd_expr(A, VA), clpfd_expr(B, VB),
            clpfd_makevar(V),
            clpfd_post('$fd_times'(VA, VB, V), [VA, VB, V]).
        clpfd_expr(min(A, B), V) :- !,
            clpfd_expr(A, VA), clpfd_expr(B, VB),
            clpfd_makevar(V),
            clpfd_post('$fd_min'(VA, VB, V), [VA, VB, V]).
        clpfd_expr(max(A, B), V) :- !,
            clpfd_expr(A, VA), clpfd_expr(B, VB),
            clpfd_makevar(V),
            clpfd_post('$fd_max'(VA, VB, V), [VA, VB, V]).
        clpfd_expr(abs(A), V) :- !,
            clpfd_expr(A, VA),
            clpfd_makevar(V),
            clpfd_post('$fd_abs'(VA, V), [VA, V]).
        clpfd_expr(A // B, V) :- !,
            clpfd_expr(A, VA), clpfd_expr(B, VB),
            clpfd_makevar(V),
            clpfd_post('$fd_idiv'(VA, VB, V), [VA, VB, V]).
        clpfd_expr(- A, V) :- !, clpfd_expr(0 - A, V).
        % A ** N (non-negative integer constant N) — GNU-Prolog FD allows power;
        % expand to repeated $fd_times (X**2 -> X*X), N=0 -> 1, N=1 -> X.
        clpfd_expr(A ** N, V) :- integer(N), N >= 0, !,
            clpfd_expr(A, VA), clpfd_pow(VA, N, V).
        clpfd_expr(E, _) :-
            throw(error(type_error(fd_expression, E), _)).

        clpfd_pow(_, 0, 1) :- !.
        clpfd_pow(VA, 1, VA) :- !.
        clpfd_pow(VA, N, V) :- N > 1, N1 is N - 1,
            clpfd_pow(VA, N1, V0),
            clpfd_makevar(V),
            clpfd_post('$fd_times'(VA, V0, V), [VA, V0, V]).

        % ===== the six arithmetic constraints =====
        %! #=(?Expr1, ?Expr2) | CLP(FD) — arithmetic constraints | The two integer expressions are equal.
        %! #\=(?Expr1, ?Expr2) | CLP(FD) — arithmetic constraints | The two integer expressions are different.
        %! #<(?Expr1, ?Expr2) | CLP(FD) — arithmetic constraints | The first integer expression is strictly less than the second.
        %! #>(?Expr1, ?Expr2) | CLP(FD) — arithmetic constraints | The first integer expression is strictly greater than the second.
        %! #=<(?Expr1, ?Expr2) | CLP(FD) — arithmetic constraints | The first integer expression is at most the second.
        %! #>=(?Expr1, ?Expr2) | CLP(FD) — arithmetic constraints | The first integer expression is at least the second.
        % Each comparison first tries to read its two expressions as ONE linear
        % form sum(Ci*Vi) Rel K (clpfd_norm), and if so posts a single global
        % bounds-consistency propagator ($fd_linear) instead of decomposing the
        % expression into a tree of binary $fd_plus / $fd_times. The global form
        % is far stronger on scaled sums and repeated variables (crypt-
        % arithmetic), because it combines a variable's coefficients
        % (e.g. donald's D appears three times) and prunes each variable against
        % every other at once. clpfd_norm FAILS on a non-linear expression
        % (var*var, //, min/max/abs, **), so the fallback clause keeps the
        % decomposition for those.
        '#='(L, R)  :- clpfd_norm(L, R, Terms, Const), clpfd_worth_linear(Terms), !, RHS is -Const, clpfd_post_lin(Terms, =, RHS).
        '#='(L, R)  :- clpfd_expr(L, X), clpfd_expr(R, Y), X = Y.
        '#\\='(L, R) :- clpfd_expr(L, X), clpfd_expr(R, Y), clpfd_post('$fd_neq'(X, Y), [X, Y]).
        '#<'(L, R)  :- clpfd_norm(L, R, Terms, Const), clpfd_worth_linear(Terms), !, RHS is -Const - 1, clpfd_post_lin(Terms, =<, RHS).
        '#<'(L, R)  :- clpfd_expr(L, X), clpfd_expr(R, Y), clpfd_post('$fd_lt'(X, Y), [X, Y]).
        '#=<'(L, R) :- clpfd_norm(L, R, Terms, Const), clpfd_worth_linear(Terms), !, RHS is -Const, clpfd_post_lin(Terms, =<, RHS).
        '#=<'(L, R) :- clpfd_expr(L, X), clpfd_expr(R, Y), clpfd_post('$fd_le'(X, Y), [X, Y]).
        '#>'(L, R)  :- R #< L.
        '#>='(L, R) :- R #=< L.

        % ===== linear normalisation: read L - R as sum(Ci*Vi) + Const =====
        % clpfd_lin(Expr, K, T0, T, C0, C): accumulate K*Expr into the term list
        % (a [Coeff-Var] per variable occurrence) and the running constant.
        % Fails on any non-linear sub-term so the caller can fall back.
        clpfd_lin(E, K, T0, T, C0, C) :- integer(E), !, C is C0 + K * E, T = T0.
        clpfd_lin(E, K, T0, T, C0, C) :- var(E), !, T = [K-E | T0], C = C0.
        clpfd_lin(A + B, K, T0, T, C0, C) :- !,
            clpfd_lin(A, K, T0, T1, C0, C1), clpfd_lin(B, K, T1, T, C1, C).
        clpfd_lin(A - B, K, T0, T, C0, C) :- !,
            clpfd_lin(A, K, T0, T1, C0, C1), K1 is -K, clpfd_lin(B, K1, T1, T, C1, C).
        clpfd_lin(- A, K, T0, T, C0, C) :- !, K1 is -K, clpfd_lin(A, K1, T0, T, C0, C).
        clpfd_lin(A * B, K, T0, T, C0, C) :- integer(A), !, K1 is K * A, clpfd_lin(B, K1, T0, T, C0, C).
        clpfd_lin(A * B, K, T0, T, C0, C) :- integer(B), !, K1 is K * B, clpfd_lin(A, K1, T0, T, C0, C).

        clpfd_norm(L, R, Terms, Const) :-
            clpfd_lin(L, 1, [], T1, 0, C1),
            clpfd_lin(R, -1, T1, T2, C1, Const),
            clpfd_combine(T2, Terms).

        % The global propagator earns its O(n^2) cost only when the binary
        % +/* decomposition would lose precision: a constraint with at least
        % two terms AND a scaled coefficient (|C| >= 2). Crypt-arithmetic always
        % qualifies (powers of ten, and a variable repeated across columns whose
        % combined coefficient exceeds one). Plain unit-coefficient sums
        % (A + B #= C, X #= Y) stay on the existing decomposition, preserving its
        % aliasing and residual-goal projection.
        clpfd_worth_linear(Terms) :- Terms = [_, _ | _], clpfd_any_scaled(Terms).
        clpfd_any_scaled([C-_ | _]) :- ( C >= 2 ; C =< -2 ), !.
        clpfd_any_scaled([_ | T]) :- clpfd_any_scaled(T).

        % combine repeated variables (sum coefficients) and drop zero coeffs.
        clpfd_combine([], []).
        clpfd_combine([K-V | Rest], Out) :-
            clpfd_collect(Rest, V, K, KTotal, Rest1),
            ( KTotal =:= 0 -> Out = Out1 ; Out = [KTotal-V | Out1] ),
            clpfd_combine(Rest1, Out1).
        clpfd_collect([], _, K, K, []).
        clpfd_collect([K2-V2 | T], V, K, KT, Rest) :-
            ( V == V2 -> K1 is K + K2, clpfd_collect(T, V, K1, KT, Rest)
            ; Rest = [K2-V2 | Rest1], clpfd_collect(T, V, K, KT, Rest1)
            ).

        % post the normalised constraint. Special cases keep the cheap shapes:
        % no variables → a constant check; a pure Var = Var → aliasing (as the
        % decomposition did). Everything else gets the global propagator.
        clpfd_post_lin([], =,  RHS) :- !, RHS =:= 0.
        clpfd_post_lin([], =<, RHS) :- !, 0 =< RHS.
        clpfd_post_lin([1-X, -1-Y], =, 0) :- !, clpfd_makevar(X), clpfd_makevar(Y), X = Y.
        clpfd_post_lin([-1-X, 1-Y], =, 0) :- !, clpfd_makevar(X), clpfd_makevar(Y), X = Y.
        clpfd_post_lin(Terms, Rel, RHS) :-
            clpfd_unzip(Terms, Coeffs, Vars),
            clpfd_makevars(Vars),
            clpfd_post('$fd_linear'(Coeffs, Vars, Rel, RHS), Vars).

        clpfd_unzip([], [], []).
        clpfd_unzip([C-V | T], [C | Cs], [V | Vs]) :- clpfd_unzip(T, Cs, Vs).

        % ===== the global linear propagator: sum(Ci*Vi) Rel RHS =====
        % Bounds consistency. SMin/SMax are the sum's reachable bounds; each
        % variable is then pruned to the interval the constraint allows given
        % every other variable's current domain. Rel is `=` or `=<` (the other
        % relations reduce to these at post time). Re-fires through clpfd_run on
        % every watched-variable narrowing, so a single post reaches a fixpoint.
        '$fd_linear'(Coeffs, Vars, Rel, RHS) :-
            clpfd_lin_sum(Coeffs, Vars, 0, SMin, 0, SMax),
            clpfd_lin_check(Rel, SMin, SMax, RHS),
            ( integer(SMin), integer(SMax) ->
                % All term bounds finite (every variable has a bounded domain —
                % the case for all crypt-arithmetic). The rest of the sum for a
                % term is then the exact integer SMin/SMax minus that term's own
                % contribution: O(1) per variable, O(n) per propagation. This is
                % safe from the value-coincidence pitfall of an identity-based
                % skip because it subtracts THIS term's contribution
                % arithmetically, not every term that happens to share its value.
                clpfd_lin_prune_fin(Coeffs, Vars, Rel, RHS, SMin, SMax)
            ; % Some bound is inf/sup (an unbounded variable). Fall back to the
              % O(n^2) prefix+suffix rest, which subtraction can't express
              % (inf - inf is undefined).
              clpfd_lin_prune(Coeffs, Vars, [], [], Rel, RHS)
            ).

        clpfd_lin_prune_fin([], [], _, _, _, _).
        clpfd_lin_prune_fin([C | Cs], [V | Vs], Rel, RHS, SMin, SMax) :-
            clpfd_term_bounds(C, V, TLo, THi),
            RestLo is SMin - TLo,
            RestHi is SMax - THi,
            clpfd_lin_contrib(Rel, RHS, RestLo, RestHi, CLo, CHi),
            clpfd_div_bounds(C, CLo, CHi, VLo, VHi),
            clpfd_narrow_bounds(V, VLo, VHi),
            clpfd_lin_prune_fin(Cs, Vs, Rel, RHS, SMin, SMax).

        clpfd_lin_sum([], [], Lo, Lo, Hi, Hi).
        clpfd_lin_sum([C | Cs], [V | Vs], Lo0, Lo, Hi0, Hi) :-
            clpfd_term_bounds(C, V, TLo, THi),
            clpfd_add_lo(Lo0, TLo, Lo1),
            clpfd_add_hi(Hi0, THi, Hi1),
            clpfd_lin_sum(Cs, Vs, Lo1, Lo, Hi1, Hi).

        % contribution bounds [TLo, THi] of the term C*V over V's domain.
        clpfd_term_bounds(C, V, TLo, THi) :-
            clpfd_dom_of(V, D), clpfd_dom_min(D, Vmin), clpfd_dom_max(D, Vmax),
            ( C > 0 -> clpfd_bmul(Vmin, C, TLo), clpfd_bmul(Vmax, C, THi)
            ;          clpfd_bmul(Vmax, C, TLo), clpfd_bmul(Vmin, C, THi)
            ).

        clpfd_lin_check(=,  SMin, SMax, RHS) :- clpfd_ble(SMin, RHS), clpfd_ble(RHS, SMax).
        clpfd_lin_check(=<, SMin, _,    RHS) :- clpfd_ble(SMin, RHS).

        % Prune each variable in turn. For the variable at the current position
        % the implied bound comes from the REST of the sum (every OTHER term),
        % computed directly as prefix + suffix — NOT by subtracting this term
        % from the total (the total is often infinite precisely because this
        % var is still unbounded, e.g. the result var of X + Y #= Z) and NOT by
        % matching variable identity (two distinct vars can be bound to the same
        % value, so `==` would wrongly skip both). The prefix (DoneC/DoneV,
        % already processed) and suffix (Cs/Vs) together are exactly the other
        % terms. O(n) per variable, O(n^2) per propagation — fine for the small
        % systems this targets, and it handles infinities cleanly (a second
        % unbounded term simply leaves the rest open).
        clpfd_lin_prune([], [], _, _, _, _).
        clpfd_lin_prune([C | Cs], [V | Vs], DoneC, DoneV, Rel, RHS) :-
            clpfd_lin_sum(DoneC, DoneV, 0, PLo, 0, PHi),
            clpfd_lin_sum(Cs, Vs, PLo, RestLo, PHi, RestHi),
            clpfd_lin_contrib(Rel, RHS, RestLo, RestHi, CLo, CHi),
            clpfd_div_bounds(C, CLo, CHi, VLo, VHi),
            clpfd_narrow_bounds(V, VLo, VHi),
            clpfd_lin_prune(Cs, Vs, [C | DoneC], [V | DoneV], Rel, RHS).

        % bounds [CLo, CHi] on this term's contribution C*V, given the rest of
        % the sum lies in [RestLo, RestHi]. For `=` the contribution is pinned
        % from both sides (C*V = RHS - rest); for `=<` only from above.
        clpfd_lin_contrib(=, RHS, RestLo, RestHi, CLo, CHi) :-
            clpfd_sub_lo(RHS, RestHi, CLo),
            clpfd_sub_hi(RHS, RestLo, CHi).
        clpfd_lin_contrib(=<, RHS, RestLo, _, inf, CHi) :-
            clpfd_sub_hi(RHS, RestLo, CHi).

        % divide the contribution interval by the coefficient to bound V.
        clpfd_div_bounds(C, CLo, CHi, VLo, VHi) :-
            ( C > 0 -> clpfd_bceildiv(CLo, C, VLo), clpfd_bfloordiv(CHi, C, VHi)
            ;          clpfd_bceildiv(CHi, C, VLo), clpfd_bfloordiv(CLo, C, VHi)
            ).

        % ===== propagators =====
        % X =< Y
        '$fd_le'(X, Y) :-
            clpfd_dom_of(X, DX), clpfd_dom_of(Y, DY),
            clpfd_dom_max(DY, MaxY), clpfd_dom_min(DX, MinX),
            clpfd_dom_above(DX, MaxY, DX2), clpfd_narrow(X, DX2),
            clpfd_dom_below(DY, MinX, DY2), clpfd_narrow(Y, DY2).

        % X < Y
        '$fd_lt'(X, Y) :-
            clpfd_dom_of(X, DX), clpfd_dom_of(Y, DY),
            clpfd_dom_max(DY, MaxY), clpfd_dom_min(DX, MinX),
            ( integer(MaxY) -> UB is MaxY - 1 ; UB = MaxY ),
            ( integer(MinX) -> LB is MinX + 1 ; LB = MinX ),
            clpfd_dom_above(DX, UB, DX2), clpfd_narrow(X, DX2),
            clpfd_dom_below(DY, LB, DY2), clpfd_narrow(Y, DY2).

        % X =\= Y — narrows only once a side is ground; stays suspended
        % otherwise, re-firing when narrowing binds one of them.
        '$fd_neq'(X, Y) :-
            ( integer(X), integer(Y) -> X =\= Y
            ; integer(X) -> clpfd_dom_of(Y, DY), clpfd_dom_del(DY, X, DY2), clpfd_narrow(Y, DY2)
            ; integer(Y) -> clpfd_dom_of(X, DX), clpfd_dom_del(DX, Y, DX2), clpfd_narrow(X, DX2)
            ; true
            ).

        % A + B = C — bounds propagation in all three directions.
        '$fd_plus'(A, B, C) :-
            clpfd_dom_of(A, DA), clpfd_dom_of(B, DB), clpfd_dom_of(C, DC),
            clpfd_dom_min(DA, AMin), clpfd_dom_max(DA, AMax),
            clpfd_dom_min(DB, BMin), clpfd_dom_max(DB, BMax),
            clpfd_dom_min(DC, CMin), clpfd_dom_max(DC, CMax),
            clpfd_add_lo(AMin, BMin, CLo), clpfd_add_hi(AMax, BMax, CHi),
            clpfd_narrow_bounds(C, CLo, CHi),
            clpfd_sub_lo(CMin, BMax, ALo), clpfd_sub_hi(CMax, BMin, AHi),
            clpfd_narrow_bounds(A, ALo, AHi),
            clpfd_sub_lo(CMin, AMax, BLo), clpfd_sub_hi(CMax, AMin, BHi),
            clpfd_narrow_bounds(B, BLo, BHi).

        % A * B = C — bounds consistency. A factor that is a (nonzero)
        % integer constant gives exact two-way scaling; with both factors
        % still variable only the product C is narrowed, from the four
        % corner products, and only while every endpoint is finite.
        '$fd_times'(A, B, C) :-
            ( integer(A), integer(B) -> P is A * B, '$dom_new'(P, P, DP), clpfd_narrow(C, DP)
            ; integer(A) -> clpfd_times_one(A, B, C)
            ; integer(B) -> clpfd_times_one(B, A, C)
            ; clpfd_times_gen(A, B, C)
            ).

        % K * Y = C with K an integer constant.
        clpfd_times_one(0, _, C) :- !, '$dom_new'(0, 0, D0), clpfd_narrow(C, D0).
        clpfd_times_one(K, Y, C) :-
            clpfd_dom_of(Y, DY),
            clpfd_dom_min(DY, YMin), clpfd_dom_max(DY, YMax),
            ( K > 0 -> clpfd_bmul(YMin, K, CLo), clpfd_bmul(YMax, K, CHi)
            ;          clpfd_bmul(YMax, K, CLo), clpfd_bmul(YMin, K, CHi)
            ),
            clpfd_narrow_bounds(C, CLo, CHi),
            clpfd_dom_of(C, DC),
            clpfd_dom_min(DC, CMin), clpfd_dom_max(DC, CMax),
            ( K > 0 -> clpfd_bceildiv(CMin, K, YLo), clpfd_bfloordiv(CMax, K, YHi)
            ;          clpfd_bceildiv(CMax, K, YLo), clpfd_bfloordiv(CMin, K, YHi)
            ),
            clpfd_narrow_bounds(Y, YLo, YHi).

        clpfd_times_gen(A, B, C) :-
            clpfd_dom_of(A, DA), clpfd_dom_of(B, DB),
            clpfd_dom_min(DA, AMin), clpfd_dom_max(DA, AMax),
            clpfd_dom_min(DB, BMin), clpfd_dom_max(DB, BMax),
            ( integer(AMin), integer(AMax), integer(BMin), integer(BMax) ->
                P1 is AMin * BMin, P2 is AMin * BMax,
                P3 is AMax * BMin, P4 is AMax * BMax,
                CLo is min(min(P1, P2), min(P3, P4)),
                CHi is max(max(P1, P2), max(P3, P4)),
                clpfd_narrow_bounds(C, CLo, CHi)
            ; true
            ).

        % C = min(A, B) — C tracks the smaller max/min of the two; since
        % C is below both operands, each operand's lower bound rises to C.
        '$fd_min'(A, B, C) :-
            clpfd_dom_of(A, DA), clpfd_dom_of(B, DB),
            clpfd_dom_min(DA, AMin), clpfd_dom_max(DA, AMax),
            clpfd_dom_min(DB, BMin), clpfd_dom_max(DB, BMax),
            clpfd_bmin(AMin, BMin, CLo), clpfd_bmin(AMax, BMax, CHi),
            clpfd_narrow_bounds(C, CLo, CHi),
            clpfd_dom_of(C, DC), clpfd_dom_min(DC, CMin),
            clpfd_narrow_bounds(A, CMin, sup),
            clpfd_narrow_bounds(B, CMin, sup).

        % C = max(A, B) — dual of min: each operand's upper bound drops to C.
        '$fd_max'(A, B, C) :-
            clpfd_dom_of(A, DA), clpfd_dom_of(B, DB),
            clpfd_dom_min(DA, AMin), clpfd_dom_max(DA, AMax),
            clpfd_dom_min(DB, BMin), clpfd_dom_max(DB, BMax),
            clpfd_bmax(AMin, BMin, CLo), clpfd_bmax(AMax, BMax, CHi),
            clpfd_narrow_bounds(C, CLo, CHi),
            clpfd_dom_of(C, DC), clpfd_dom_max(DC, CMax),
            clpfd_narrow_bounds(A, inf, CMax),
            clpfd_narrow_bounds(B, inf, CMax).

        % C = abs(A) — C is non-negative; A is confined to [-Cmax, Cmax].
        '$fd_abs'(A, C) :-
            clpfd_dom_of(A, DA), clpfd_dom_min(DA, AMin), clpfd_dom_max(DA, AMax),
            ( clpfd_ble(0, AMin) -> CLo = AMin, CHi = AMax
            ; clpfd_ble(AMax, 0) -> clpfd_bneg(AMax, CLo), clpfd_bneg(AMin, CHi)
            ; CLo = 0, clpfd_bneg(AMin, NA), clpfd_bmax(NA, AMax, CHi)
            ),
            clpfd_narrow_bounds(C, CLo, CHi),
            clpfd_dom_of(C, DC), clpfd_dom_max(DC, CMax),
            clpfd_bneg(CMax, NCMax),
            clpfd_narrow_bounds(A, NCMax, CMax).

        % V = A // B (truncating). A known positive integer divisor gives
        % two-way propagation; a variable divisor whose domain is wholly
        % positive gives a forward bound on V (truncating division is
        % monotone in each argument, so the extreme quotients lie at the
        % operand-domain corners).
        '$fd_idiv'(A, B, V) :-
            ( integer(B) ->
                ( B =:= 0 -> throw(error(evaluation_error(zero_divisor), _))
                ; B > 0 -> clpfd_idiv_pos(A, B, V)
                ; throw(error(type_error(fd_positive_divisor, B), _))
                )
            ; clpfd_idiv_var(A, B, V)
            ).

        clpfd_idiv_var(A, B, V) :-
            clpfd_dom_of(B, DB), clpfd_dom_min(DB, BMin), clpfd_dom_max(DB, BMax),
            clpfd_dom_of(A, DA), clpfd_dom_min(DA, AMin), clpfd_dom_max(DA, AMax),
            ( integer(BMin), BMin >= 1, integer(BMax),
              integer(AMin), integer(AMax) ->
                T1 is AMin // BMin, T2 is AMin // BMax,
                T3 is AMax // BMin, T4 is AMax // BMax,
                VLo is min(min(T1, T2), min(T3, T4)),
                VHi is max(max(T1, T2), max(T3, T4)),
                clpfd_narrow_bounds(V, VLo, VHi)
            ; true
            ).

        clpfd_idiv_pos(A, K, V) :-
            clpfd_dom_of(A, DA), clpfd_dom_min(DA, AMin), clpfd_dom_max(DA, AMax),
            clpfd_btruncdiv(AMin, K, VLo), clpfd_btruncdiv(AMax, K, VHi),
            clpfd_narrow_bounds(V, VLo, VHi),
            clpfd_dom_of(V, DV), clpfd_dom_min(DV, VMin), clpfd_dom_max(DV, VMax),
            K1 is K - 1,
            clpfd_bmul(VMin, K, P1), clpfd_sub_lo(P1, K1, ALo),
            clpfd_bmul(VMax, K, P2), clpfd_add_hi(P2, K1, AHi),
            clpfd_narrow_bounds(A, ALo, AHi).

        % ===== sum/3 =====
        % sum(List, Rel, Total): Total stands in relation Rel to the sum
        % of List. Rel is one of the six arithmetic-constraint operators.
        %! sum(+Vars, +Rel, ?Total) | CLP(FD) — global constraints | Total stands in relation Rel to the sum of the list of variables.
        sum(List, Rel, Total) :-
            clpfd_sum_expr(List, 0, Expr),
            ( clpfd_rel_ok(Rel) -> clpfd_apply_rel(Rel, Expr, Total)
            ; throw(error(domain_error(clpfd_relation, Rel), _))
            ).

        clpfd_sum_expr([], Acc, Acc).
        clpfd_sum_expr([X|Xs], Acc, S) :- clpfd_sum_expr(Xs, Acc + X, S).

        clpfd_rel_ok('#=').  clpfd_rel_ok('#\\=').  clpfd_rel_ok('#<').
        clpfd_rel_ok('#>').  clpfd_rel_ok('#=<').   clpfd_rel_ok('#>=').

        clpfd_apply_rel('#=',   E, T) :- E #= T.
        clpfd_apply_rel('#\\=', E, T) :- E #\= T.
        clpfd_apply_rel('#<',   E, T) :- E #< T.
        clpfd_apply_rel('#>',   E, T) :- E #> T.
        clpfd_apply_rel('#=<',  E, T) :- E #=< T.
        clpfd_apply_rel('#>=',  E, T) :- E #>= T.

        % ===== scalar_product/4 =====
        % scalar_product(Coeffs, Vars, Rel, Total): Total stands in
        % relation Rel to the dot product of the equal-length lists
        % Coeffs and Vars.
        %! scalar_product(+Coeffs, +Vars, +Rel, ?Total) | CLP(FD) — global constraints | Total stands in relation Rel to the dot product of the coefficient and variable lists.
        scalar_product(Coeffs, Vars, Rel, Total) :-
            ( clpfd_rel_ok(Rel) -> true
            ; throw(error(domain_error(clpfd_relation, Rel), _))
            ),
            clpfd_sp_expr(Coeffs, Vars, 0, Expr),
            clpfd_apply_rel(Rel, Expr, Total).

        clpfd_sp_expr([], [], Acc, Acc) :- !.
        clpfd_sp_expr([C|Cs], [V|Vs], Acc, S) :- !,
            clpfd_sp_expr(Cs, Vs, Acc + C * V, S).
        clpfd_sp_expr(_, _, _, _) :-
            throw(error(type_error(clpfd_scalar_product_lengths, _), _)).

        % ===== labeling =====
        % label/1 and labeling/2 assign each variable a value from its
        % domain, backtracking over the choices; propagation runs between
        % assignments and prunes the remaining search.
        %! label(+Vars) | CLP(FD) — labeling | Assigns each variable in the list a value from its domain, searching by backtracking.
        label(Vars) :- labeling([], Vars).

        %! labeling(+Options, +Vars) | CLP(FD) — labeling | Like label/1 with options for variable selection (leftmost, ff) and value order (up, down).
        labeling(Options, Vars) :-
            clpfd_label_opts(Options, Sel, Ord),
            clpfd_label(Vars, Sel, Ord).

        % option list -> variable-selection and value-ordering strategy.
        clpfd_label_opts([], leftmost, up).
        clpfd_label_opts([O|Os], Sel, Ord) :-
            clpfd_label_opts(Os, Sel0, Ord0),
            ( O == leftmost -> Sel = leftmost, Ord = Ord0
            ; O == ff       -> Sel = ff,       Ord = Ord0
            ; O == up       -> Ord = up,       Sel = Sel0
            ; O == down     -> Ord = down,     Sel = Sel0
            ; throw(error(domain_error(labeling_option, O), _))
            ).

        clpfd_label([], _, _).
        clpfd_label([V|Vs], Sel, Ord) :-
            ( integer(V) -> clpfd_label(Vs, Sel, Ord)
            ; Sel == ff  -> clpfd_pick_ff([V|Vs], Ord, Rest),
                            clpfd_label(Rest, Sel, Ord)
            ; clpfd_pick(V, Ord),
              clpfd_label(Vs, Sel, Ord)
            ).

        % first-fail: label the unbound variable with the smallest domain.
        clpfd_pick_ff(Vars, Ord, Rest) :-
            clpfd_choose_ff(Vars, Best),
            clpfd_del1(Vars, Best, Rest),
            clpfd_pick(Best, Ord).

        clpfd_choose_ff([V|Vs], Best) :-
            ( integer(V) -> clpfd_choose_ff(Vs, Best)
            ; clpfd_var_size(V, S), clpfd_choose_ff_(Vs, V, S, Best)
            ).
        clpfd_choose_ff_([], Best, _, Best).
        clpfd_choose_ff_([V|Vs], CurBest, CurS, Best) :-
            ( integer(V) -> clpfd_choose_ff_(Vs, CurBest, CurS, Best)
            ; clpfd_var_size(V, S),
              ( S < CurS -> clpfd_choose_ff_(Vs, V, S, Best)
              ;             clpfd_choose_ff_(Vs, CurBest, CurS, Best)
              )
            ).
        clpfd_var_size(V, N) :- clpfd_dom_of(V, D), clpfd_dom_size(D, N).

        clpfd_del1([], _, []).
        clpfd_del1([X|Xs], Y, Rest) :-
            ( X == Y -> Rest = Xs
            ; Rest = [X|Rest1], clpfd_del1(Xs, Y, Rest1)
            ).

        % bind V to a value of its domain, on backtracking the next one.
        clpfd_pick(V, up)   :- clpfd_dom_of(V, D), clpfd_indomain_up(V, D).
        clpfd_pick(V, down) :- clpfd_dom_of(V, D), clpfd_indomain_down(V, D).

        %! indomain(?Var) | CLP(FD) — labeling | Binds one variable to each value of its domain in turn, on backtracking.
        indomain(X) :-
            ( integer(X) -> true
            ; clpfd_dom_of(X, D), clpfd_indomain_up(X, D)
            ).

        % Enumerate the domain's values (ascending for up, descending for down).
        % An unbounded domain cannot be labelled — raise instantiation_error, as
        % the interval-walking version did when it met inf/sup.
        clpfd_indomain_up(V, D) :-
            clpfd_dom_finite(D),
            '$dom_values'(D, Vals), member(V, Vals).
        clpfd_indomain_down(V, D) :-
            clpfd_dom_finite(D),
            '$dom_values'(D, Vals), clpfd_rev(Vals, [], R), member(V, R).
        clpfd_dom_finite(D) :-
            '$dom_min'(D, Mn), '$dom_max'(D, Mx),
            ( ( Mn == inf ; Mx == sup ) -> throw(error(instantiation_error, _)) ; true ).

        clpfd_rev([], A, A).
        clpfd_rev([X|Xs], A, R) :- clpfd_rev(Xs, [X|A], R).

        % ===== all_different / all_distinct =====
        % all_different posts pairwise disequality: whenever a variable
        % grounds, '#\='/2 prunes its value from the others.
        %! all_different(?Vars) | CLP(FD) — global constraints | Every element of the list takes a distinct value (pairwise).
        all_different([]).
        all_different([X|Xs]) :- clpfd_diff_all(Xs, X), all_different(Xs).

        clpfd_diff_all([], _).
        clpfd_diff_all([Y|Ys], X) :- X #\= Y, clpfd_diff_all(Ys, X).

        % all_distinct is stronger: a single $fd_alldiff propagator does
        % Hall-interval pruning. An interval [Lo,Hi] holding exactly as
        % many variables (whose domains it contains) as it has values is
        % a tight Hall interval — those variables consume every value, so
        % [Lo,Hi] is removed from all the others; more variables than
        % values fails immediately.
        %! all_distinct(?Vars) | CLP(FD) — global constraints | Every element of the list takes a distinct value, with Hall-interval pruning.
        all_distinct(List) :-
            clpfd_makevars(List),
            clpfd_post('$fd_alldiff'(List), List).

        clpfd_makevars([]).
        clpfd_makevars([X|Xs]) :- clpfd_makevar(X), clpfd_makevars(Xs).

        % all_distinct's Hall-interval pruning. The O(n^3) interval search runs
        % natively ($fd_hall): it reads the variables' current domains
        % and returns the shrunk domain for every variable a saturated Hall
        % interval pruned (or fails on a pigeonhole violation). Narrowing — and
        % the re-propagation it drives — stays in the engine: clpfd_narrow each
        % returned V-NewDom. The interpreted-Prolog version of this loop was far
        % too slow (it made alpha first-fail ~8x slower).
        '$fd_alldiff'(Vars) :-
            clpfd_doms(Vars, Doms),
            '$fd_hall'(Vars, Doms, Applies),
            clpfd_apply_doms(Applies).

        clpfd_doms([], []).
        clpfd_doms([V|Vs], [D|Ds]) :- clpfd_dom_of(V, D), clpfd_doms(Vs, Ds).

        clpfd_apply_doms([]).
        clpfd_apply_doms([V-D | T]) :- clpfd_narrow(V, D), clpfd_apply_doms(T).

        % ===== reification =====
        % B #<==> C : the 0/1 variable B is 1 exactly when constraint C
        % holds. #==>/#<== are (reverse) implication, #/\ / #\/ / #\ the
        % boolean connectives. Each is reified to a 0/1 variable and the
        % connective posted as an arithmetic constraint over those.
        %! #<==>(?Bool, +Constraint) | CLP(FD) — reification | Bool is 1 exactly when the constraint holds, 0 otherwise.
        '#<==>'(B, C) :- clpfd_reify(C, B).
        %! #==>(+Constraint1, +Constraint2) | CLP(FD) — reification | Constraint1 implies Constraint2.
        '#==>'(C1, C2) :-
            clpfd_reify(C1, B1), clpfd_reify(C2, B2), B1 #=< B2.
        %! #<==(+Constraint1, +Constraint2) | CLP(FD) — reification | Constraint2 implies Constraint1.
        '#<=='(C1, C2) :-
            clpfd_reify(C1, B1), clpfd_reify(C2, B2), B2 #=< B1.
        % GNU-Prolog single-arrow reification operators (aliases) + ## (xor).
        '#<=>'(X, Y) :- '#<==>'(X, Y).
        '#=>'(X, Y) :- '#==>'(X, Y).
        '#<='(X, Y) :- '#<=='(X, Y).
        '##'(X, Y) :- X #\= Y.
        %! #/\(+Constraint1, +Constraint2) | CLP(FD) — reification | Both constraints hold (conjunction).
        '#/\\'(C1, C2) :- clpfd_reify(C1, 1), clpfd_reify(C2, 1).
        %! #\/(+Constraint1, +Constraint2) | CLP(FD) — reification | At least one constraint holds (disjunction).
        '#\\/'(C1, C2) :-
            clpfd_reify(C1, B1), clpfd_reify(C2, B2), B1 + B2 #>= 1.
        %! #\(+Constraint) | CLP(FD) — reification | The constraint does not hold (negation).
        '#\\'(C) :- clpfd_reify(C, 0).

        % reify constraint expression C to the 0/1 variable B.
        clpfd_reify(C, B) :-
            ( clpfd_reif_cmp(C, Kind, L, R) ->
                B in 0..1,
                clpfd_expr(L, X), clpfd_expr(R, Y),
                clpfd_post('$fd_reif'(B, Kind, X, Y), [B, X, Y])
            ; C = (C1 #/\ C2) ->
                clpfd_reify(C1, B1), clpfd_reify(C2, B2), B #= B1 * B2
            ; C = (C1 #\/ C2) ->
                clpfd_reify(C1, B1), clpfd_reify(C2, B2),
                B #= B1 + B2 - B1 * B2
            ; C = (#\ C1) ->
                clpfd_reify(C1, B1), B #= 1 - B1
            ; C = (C1 #==> C2) ->
                clpfd_reify(C1, B1), clpfd_reify(C2, B2),
                B #= 1 - B1 + B1 * B2
            ; C = (C1 #<== C2) ->
                clpfd_reify(C2, B1), clpfd_reify(C1, B2),
                B #= 1 - B1 + B1 * B2
            ; C == true -> B #= 1
            ; C == false -> B #= 0
            ; throw(error(type_error(clpfd_reifiable, C), _))
            ).

        clpfd_reif_cmp((L #= R),   '#=',   L, R).
        clpfd_reif_cmp((L #\= R),  '#\\=', L, R).
        clpfd_reif_cmp((L #< R),   '#<',   L, R).
        clpfd_reif_cmp((L #> R),   '#>',   L, R).
        clpfd_reif_cmp((L #=< R),  '#=<',  L, R).
        clpfd_reif_cmp((L #>= R),  '#>=',  L, R).
        % GNU-Prolog ## = boolean exclusive-or; on 0/1 vars that is inequality.
        clpfd_reif_cmp((L ## R),   '#\\=', L, R).

        % B #<==> Kind(X,Y). Once B is decided the constraint (or its
        % negation) is enforced; while B is open, an entailed constraint
        % sets B = 1 and a disentailed one sets B = 0.
        '$fd_reif'(B, Kind, X, Y) :-
            ( B == 1 -> clpfd_kind_run(Kind, X, Y)
            ; B == 0 -> clpfd_neg(Kind, NKind), clpfd_kind_run(NKind, X, Y)
            ; clpfd_entail(Kind, X, Y, E),
              ( E == true  -> B = 1
              ; E == false -> B = 0
              ; true
              )
            ).

        clpfd_kind_run('#<',   X, Y) :- '$fd_lt'(X, Y).
        clpfd_kind_run('#=<',  X, Y) :- '$fd_le'(X, Y).
        clpfd_kind_run('#>',   X, Y) :- '$fd_lt'(Y, X).
        clpfd_kind_run('#>=',  X, Y) :- '$fd_le'(Y, X).
        clpfd_kind_run('#=',   X, Y) :- X = Y.
        clpfd_kind_run('#\\=', X, Y) :- '$fd_neq'(X, Y).

        clpfd_neg('#=',   '#\\=').
        clpfd_neg('#\\=', '#=').
        clpfd_neg('#<',   '#>=').
        clpfd_neg('#>=',  '#<').
        clpfd_neg('#=<',  '#>').
        clpfd_neg('#>',   '#=<').

        % E = true / false / unknown: whether Kind(X,Y) is entailed,
        % disentailed, or undecided by the current domains.
        clpfd_entail(Kind, X, Y, E) :-
            clpfd_dom_of(X, DX), clpfd_dom_of(Y, DY),
            clpfd_dom_min(DX, XL), clpfd_dom_max(DX, XH),
            clpfd_dom_min(DY, YL), clpfd_dom_max(DY, YH),
            clpfd_entail_(Kind, DX, DY, XL, XH, YL, YH, E).

        clpfd_entail_('#<', _, _, XL, XH, YL, YH, E) :-
            ( clpfd_blt(XH, YL) -> E = true
            ; clpfd_ble(YH, XL) -> E = false
            ; E = unknown
            ).
        clpfd_entail_('#=<', _, _, XL, XH, YL, YH, E) :-
            ( clpfd_ble(XH, YL) -> E = true
            ; clpfd_blt(YH, XL) -> E = false
            ; E = unknown
            ).
        clpfd_entail_('#>', DX, DY, XL, XH, YL, YH, E) :-
            clpfd_entail_('#<', DY, DX, YL, YH, XL, XH, E).
        clpfd_entail_('#>=', DX, DY, XL, XH, YL, YH, E) :-
            clpfd_entail_('#=<', DY, DX, YL, YH, XL, XH, E).
        clpfd_entail_('#=', DX, DY, _, _, _, _, E) :-
            ( clpfd_dom_isect(DX, DY, I), '$dom_empty'(I) -> E = false
            ; '$dom_singleton'(DX, V), '$dom_singleton'(DY, V) -> E = true
            ; E = unknown
            ).
        clpfd_entail_('#\\=', DX, DY, _, _, _, _, E) :-
            ( clpfd_dom_isect(DX, DY, I), '$dom_empty'(I) -> E = true
            ; '$dom_singleton'(DX, V), '$dom_singleton'(DY, V) -> E = false
            ; E = unknown
            ).

        % ===== GNU-Prolog FD compatibility shim =====
        % Aliases mapping GProlog's fd_* primitives onto the clpfd above, so the
        % ExamplesFD corpus runs unchanged. fd_domain/labeling/all_different are
        % direct renames; fd_atmost/exactly/only_one/at_most_one are reified
        % counts; fd_set_vector_max is a GProlog internal sizing hint (no-op).
        fd_domain(Vars, Lo, Hi) :-
            ( is_list(Vars) -> Vars ins Lo..Hi ; Vars in Lo..Hi ).
        fd_labeling(Vars) :-
            ( is_list(Vars) -> label(Vars) ; label([Vars]) ).
        fd_labelingff(Vars) :-
            ( is_list(Vars) -> labeling([ff], Vars) ; labeling([ff], [Vars]) ).
        % GProlog's fd_all_different maps to pairwise all_different, not the
        % stronger all_distinct (native Hall). Even with native Hall,
        % its O(n^3) re-fire on every domain change costs more than pairwise's
        % fire-on-grounding on the crypt-arithmetic corpus (alpha ff ~3s vs ~7s),
        % and the extra pruning does NOT make alpha's leftmost labelling feasible
        % (that is node-count-bound under interpreted control, not propagation-
        % bound). Users who want the strong global constraint call all_distinct/1
        % directly.
        fd_all_different(Vars) :- all_different(Vars).
        fd_set_vector_max(_).
        fd_atmost(N, Vars, V) :- '$fd_count_eq'(Vars, V, C), C #=< N.
        fd_exactly(N, Vars, V) :- '$fd_count_eq'(Vars, V, C), C #= N.
        fd_only_one(Bs) :- sum(Bs, #=, 1).
        fd_at_most_one(Bs) :- sum(Bs, #=<, 1).
        '$fd_count_eq'([], _, 0).
        '$fd_count_eq'([X|Xs], V, C) :-
            B #<==> (X #= V), '$fd_count_eq'(Xs, V, C0), C #= C0 + B.
        """;
}
