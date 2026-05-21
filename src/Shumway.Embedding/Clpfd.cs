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
/// <c>-</c>). Multiplication, labeling, global constraints and
/// reification are later chunks.</para>
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
        :- public '$fd_set'/3.
        :- public verify_attributes/4.
        :- public clpfd_attr_goals/3.

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
        """;
}
