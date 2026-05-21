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
/// <para>Chunk 89 delivers the core plus the six arithmetic constraints
/// <c>#=</c>, <c>#\=</c>, <c>#&lt;</c>, <c>#&gt;</c>, <c>#=&lt;</c>,
/// <c>#&gt;=</c> over additive expressions (<c>+</c>, <c>-</c>, unary
/// <c>-</c>). Chunk 90 adds multiplication (<c>*</c>) with bounds
/// consistency and the labeling predicates <c>label/1</c>,
/// <c>labeling/2</c> (options <c>leftmost</c>/<c>ff</c> and
/// <c>up</c>/<c>down</c>) and <c>indomain/1</c>. Chunk 91 adds the
/// <c>all_different/1</c> / <c>all_distinct/1</c> global constraint and
/// reification: <c>#&lt;==&gt;</c>, <c>#==&gt;</c>, <c>#&lt;==</c> and
/// the boolean connectives <c>#/\</c>, <c>#\/</c>, <c>#\</c>.</para>
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
        :- public '$fd_set'/3.
        :- public '$fd_reif'/4.
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

        % the prefix-negation operator is declared after the public block:
        % once `#\` is a prefix operator, the quoted atom in `'#\\'/1`
        % above would be misparsed as an operator awaiting an argument.
        :- op(710, fy, #\).

        % ===== bound order: inf < every integer < sup =====
        clpfd_ble(A, B) :-
            ( A == inf -> true
            ; B == sup -> true
            ; A == sup -> fail
            ; B == inf -> fail
            ; A =< B
            ).
        clpfd_blt(A, B) :- A \== B, clpfd_ble(A, B).
        clpfd_bmin(A, B, M) :- ( clpfd_ble(A, B) -> M = A ; M = B ).
        clpfd_bmax(A, B, M) :- ( clpfd_ble(A, B) -> M = B ; M = A ).

        % bound arithmetic — mins never carry sup, maxes never carry inf,
        % so the two indeterminate combinations never arise here.
        clpfd_add_lo(A, B, R) :- ( ( A == inf ; B == inf ) -> R = inf ; R is A + B ).
        clpfd_add_hi(A, B, R) :- ( ( A == sup ; B == sup ) -> R = sup ; R is A + B ).
        clpfd_sub_lo(A, B, R) :- ( A == inf -> R = inf ; B == sup -> R = inf ; R is A - B ).
        clpfd_sub_hi(A, B, R) :- ( A == sup -> R = sup ; B == inf -> R = sup ; R is A - B ).

        % negate a bound
        clpfd_bneg(inf, sup) :- !.
        clpfd_bneg(sup, inf) :- !.
        clpfd_bneg(X, Y) :- Y is -X.

        % multiply a bound by a nonzero integer constant K
        clpfd_bmul(B, K, R) :-
            ( B == inf -> ( K > 0 -> R = inf ; R = sup )
            ; B == sup -> ( K > 0 -> R = sup ; R = inf )
            ; R is B * K
            ).

        % floor / ceil of a bound divided by a nonzero integer constant K.
        % `mod` is floored (sign of divisor), so C - (C mod K) is the exact
        % multiple of K at or below C, making the // division exact.
        clpfd_bfloordiv(C, K, R) :-
            ( C == inf -> ( K > 0 -> R = inf ; R = sup )
            ; C == sup -> ( K > 0 -> R = sup ; R = inf )
            ; M is C mod K, R is (C - M) // K
            ).
        clpfd_bceildiv(C, K, R) :-
            ( C == inf -> ( K > 0 -> R = inf ; R = sup )
            ; C == sup -> ( K > 0 -> R = sup ; R = inf )
            ; NC is -C, M is NC mod K, R0 is (NC - M) // K, R is -R0
            ).

        % ===== domains: sorted lists of disjoint L-H intervals =====
        clpfd_universal([inf-sup]).

        clpfd_iv(L, H, IV) :- ( clpfd_ble(L, H) -> IV = [L-H] ; IV = [] ).

        clpfd_dom_min([L-_|_], L).
        clpfd_dom_max([_-H], H) :- !.
        clpfd_dom_max([_|T], H) :- clpfd_dom_max(T, H).

        clpfd_in_dom(V, [L-H|T]) :-
            ( clpfd_ble(L, V), clpfd_ble(V, H) -> true ; clpfd_in_dom(V, T) ).

        % keep only the part of the domain at or below bound B
        clpfd_dom_above([], _, []).
        clpfd_dom_above([L-H|T], B, Out) :-
            ( clpfd_ble(L, B) ->
                clpfd_bmin(H, B, H2),
                Out = [L-H2|Rest],
                ( clpfd_blt(B, H) -> Rest = [] ; clpfd_dom_above(T, B, Rest) )
            ; Out = []
            ).

        % keep only the part of the domain at or above bound B
        clpfd_dom_below([], _, []).
        clpfd_dom_below([L-H|T], B, Out) :-
            ( clpfd_blt(H, B) -> clpfd_dom_below(T, B, Out)
            ; clpfd_ble(B, L) -> Out = [L-H|Rest], clpfd_dom_below(T, B, Rest)
            ; Out = [B-H|Rest], clpfd_dom_below(T, B, Rest)
            ).

        % remove the single integer value V
        clpfd_dom_del([], _, []).
        clpfd_dom_del([L-H|T], V, Out) :-
            ( clpfd_ble(L, V), clpfd_ble(V, H) ->
                V1 is V - 1, V2 is V + 1,
                ( clpfd_ble(L, V1) -> Lo = [L-V1] ; Lo = [] ),
                ( clpfd_ble(V2, H) -> Hi = [V2-H] ; Hi = [] ),
                clpfd_app(Lo, Hi, Frag),
                clpfd_app(Frag, T, Out)
            ; Out = [L-H|Rest], clpfd_dom_del(T, V, Rest)
            ).

        % intersection of two sorted disjoint interval lists
        clpfd_dom_isect([], _, []).
        clpfd_dom_isect([_|_], [], []).
        clpfd_dom_isect([A-B|R1], [C-D|R2], Out) :-
            ( clpfd_blt(B, C) -> clpfd_dom_isect(R1, [C-D|R2], Out)
            ; clpfd_blt(D, A) -> clpfd_dom_isect([A-B|R1], R2, Out)
            ; clpfd_bmax(A, C, L), clpfd_bmin(B, D, H),
              Out = [L-H|Rest],
              ( clpfd_blt(B, D) -> clpfd_dom_isect(R1, [C-D|R2], Rest)
              ; clpfd_dom_isect([A-B|R1], R2, Rest)
              )
            ).

        clpfd_app([], L, L).
        clpfd_app([H|T], L, [H|R]) :- clpfd_app(T, L, R).

        % render a domain as an `in` domain expression for projection
        clpfd_dom_expr([L-H], L..H) :- !.
        clpfd_dom_expr([L-H|T], (L..H \/ Rest)) :- clpfd_dom_expr(T, Rest).

        % number of integers a domain admits; an unbounded interval counts
        % as a large constant — enough to deprioritise it under first-fail.
        clpfd_dom_size([], 0).
        clpfd_dom_size([L-H|T], N) :-
            ( integer(L), integer(H) -> S is H - L + 1 ; S = 1000000000 ),
            clpfd_dom_size(T, N0), N is N0 + S.

        % ===== FD variables =====
        % the domain of X: a singleton for an integer, the attribute's
        % domain for an FD variable, the universal domain otherwise.
        clpfd_dom_of(X, D) :-
            ( integer(X) -> D = [X-X]
            ; get_attr(X, clpfd, fd(D0, _)) -> D = D0
            ; clpfd_universal(D)
            ).

        % ensure X is usable as an FD term: a plain variable becomes an FD
        % variable with the universal domain; an integer or existing FD
        % variable is left as is.
        clpfd_makevar(X) :-
            ( integer(X) -> true
            ; get_attr(X, clpfd, _) -> true
            ; var(X) -> clpfd_universal(U), put_attr(X, clpfd, fd(U, []))
            ; throw(error(type_error(integer, X), _))
            ).

        % narrow X's domain to NewDom: empty fails, a singleton binds X,
        % an unchanged domain is a no-op, otherwise store it and re-run the
        % suspended propagators to a fixpoint.
        clpfd_narrow(X, NewDom) :-
            ( integer(X) -> NewDom \== []
            ; get_attr(X, clpfd, fd(OldDom, Props)) ->
                ( NewDom == OldDom -> true
                ; NewDom == [] -> fail
                ; NewDom = [K-K] -> X = K
                ; put_attr(X, clpfd, fd(NewDom, Props)), clpfd_run(Props)
                )
            ; NewDom == [] -> fail
            ; NewDom = [K-K] -> X = K
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
            ( Dom = [K-K] -> V = K
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
                    Dom3 \== [],
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
        attribute_goals(clpfd, Attr, V, Goals) :- clpfd_attr_goals(Attr, V, Goals).
        clpfd_attr_goals(fd(Dom, _), V, [V in Expr]) :- clpfd_dom_expr(Dom, Expr).

        % ===== in / ins =====
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
        clpfd_expr(- A, V) :- !, clpfd_expr(0 - A, V).
        clpfd_expr(E, _) :-
            throw(error(type_error(fd_expression, E), _)).

        % ===== the six arithmetic constraints =====
        '#='(L, R)  :- clpfd_expr(L, X), clpfd_expr(R, Y), X = Y.
        '#\\='(L, R) :- clpfd_expr(L, X), clpfd_expr(R, Y), clpfd_post('$fd_neq'(X, Y), [X, Y]).
        '#<'(L, R)  :- clpfd_expr(L, X), clpfd_expr(R, Y), clpfd_post('$fd_lt'(X, Y), [X, Y]).
        '#=<'(L, R) :- clpfd_expr(L, X), clpfd_expr(R, Y), clpfd_post('$fd_le'(X, Y), [X, Y]).
        '#>'(L, R)  :- R #< L.
        '#>='(L, R) :- R #=< L.

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
            ( integer(A), integer(B) -> P is A * B, clpfd_narrow(C, [P-P])
            ; integer(A) -> clpfd_times_one(A, B, C)
            ; integer(B) -> clpfd_times_one(B, A, C)
            ; clpfd_times_gen(A, B, C)
            ).

        % K * Y = C with K an integer constant.
        clpfd_times_one(0, _, C) :- !, clpfd_narrow(C, [0-0]).
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

        % ===== labeling =====
        % label/1 and labeling/2 assign each variable a value from its
        % domain, backtracking over the choices; propagation runs between
        % assignments and prunes the remaining search.
        label(Vars) :- labeling([], Vars).

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
        clpfd_pick(V, down) :- clpfd_dom_of(V, D),
                               clpfd_rev(D, [], R), clpfd_indomain_down(V, R).

        indomain(X) :-
            ( integer(X) -> true
            ; clpfd_dom_of(X, D), clpfd_indomain_up(X, D)
            ).

        clpfd_indomain_up(_, []) :- fail.
        clpfd_indomain_up(V, [L-H|T]) :-
            ( L == inf -> throw(error(instantiation_error, _)) ; true ),
            ( clpfd_enum_up(V, L, H)
            ; clpfd_indomain_up(V, T)
            ).
        clpfd_enum_up(V, L, H) :-
            ( H == sup -> throw(error(instantiation_error, _))
            ; L =< H -> ( V = L ; L1 is L + 1, clpfd_enum_up(V, L1, H) )
            ; fail
            ).

        clpfd_indomain_down(_, []) :- fail.
        clpfd_indomain_down(V, [L-H|T]) :-
            ( H == sup -> throw(error(instantiation_error, _)) ; true ),
            ( clpfd_enum_down(V, H, L)
            ; clpfd_indomain_down(V, T)
            ).
        clpfd_enum_down(V, H, L) :-
            ( L == inf -> throw(error(instantiation_error, _))
            ; L =< H -> ( V = H ; H1 is H - 1, clpfd_enum_down(V, H1, L) )
            ; fail
            ).

        clpfd_rev([], A, A).
        clpfd_rev([X|Xs], A, R) :- clpfd_rev(Xs, [X|A], R).

        % ===== all_different / all_distinct =====
        % every pair of list elements is constrained unequal; whenever a
        % variable grounds, '#\='/2 prunes its value from the others.
        all_different([]).
        all_different([X|Xs]) :- clpfd_diff_all(Xs, X), all_different(Xs).
        all_distinct(L) :- all_different(L).

        clpfd_diff_all([], _).
        clpfd_diff_all([Y|Ys], X) :- X #\= Y, clpfd_diff_all(Ys, X).

        % ===== reification =====
        % B #<==> C : the 0/1 variable B is 1 exactly when constraint C
        % holds. #==>/#<== are (reverse) implication, #/\ / #\/ / #\ the
        % boolean connectives. Each is reified to a 0/1 variable and the
        % connective posted as an arithmetic constraint over those.
        '#<==>'(B, C) :- clpfd_reify(C, B).
        '#==>'(C1, C2) :-
            clpfd_reify(C1, B1), clpfd_reify(C2, B2), B1 #=< B2.
        '#<=='(C1, C2) :-
            clpfd_reify(C1, B1), clpfd_reify(C2, B2), B2 #=< B1.
        '#/\\'(C1, C2) :- clpfd_reify(C1, 1), clpfd_reify(C2, 1).
        '#\\/'(C1, C2) :-
            clpfd_reify(C1, B1), clpfd_reify(C2, B2), B1 + B2 #>= 1.
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
            ( clpfd_dom_isect(DX, DY, I), I == [] -> E = false
            ; DX = [V-V], DY = [V-V] -> E = true
            ; E = unknown
            ).
        clpfd_entail_('#\\=', DX, DY, _, _, _, _, E) :-
            ( clpfd_dom_isect(DX, DY, I), I == [] -> E = true
            ; DX = [V-V], DY = [V-V] -> E = false
            ; E = unknown
            ).
        """;
}
