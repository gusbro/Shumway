namespace Shumway.Embedding;

/// <summary>
/// The CLP(R) library — constraint logic programming over the reals. It
/// is an <em>opt-in</em> module: an embedder calls
/// <see cref="PrologEngine.UseClpr"/> to consult it.
///
/// <para>Constraints are posted with the <c>{Constraint, ...}</c>
/// wrapper. Equalities (<c>=:=</c> / <c>=</c>) feed a Gaussian-elimination
/// solver: a dependent variable carries its solved linear form as a
/// <c>clpr</c> attribute, and expansion through the solution is lazy.
/// Inequalities (<c>&lt;</c>, <c>&gt;</c>, <c>=&lt;</c>, <c>&gt;=</c>) are
/// stored on the variables they mention; on every post the connected
/// component of inequalities is gathered, re-expanded through the current
/// equality solution, and checked for satisfiability by Fourier–Motzkin
/// elimination — so an unsatisfiable system fails as soon as the
/// offending constraint is posted. Variables determined to a single
/// value are bound to it.</para>
///
/// <para>Disequality (<c>=\=</c>), non-linear constraints and constraint
/// projection are later chunks. CLP(R) and CLP(FD) cannot share an engine
/// — both define a public <c>verify_attributes/4</c>.</para>
/// </summary>
internal static class Clpr
{
    public const string ModuleName = "clpr";

    public const string Source = """
        :- module(clpr).

        :- public '{}'/1.
        :- public verify_attributes/4.
        :- public '$clpr_dep_eq'/2.

        % ===== linear forms: lin(Constant, [Var-Coeff, ...]) =====
        % the coefficient list never carries a zero coefficient.

        clpr_add(lin(C1, T1), lin(C2, T2), lin(C3, T3)) :-
            C3 is C1 + C2,
            clpr_merge(T1, T2, T3).

        clpr_scale(K, lin(C, T), lin(C2, T2)) :-
            ( K =:= 0 -> C2 = 0, T2 = []
            ; C2 is C * K, clpr_scale_terms(T, K, T2)
            ).
        clpr_scale_terms([], _, []).
        clpr_scale_terms([V-C|R], K, [V-C2|R2]) :-
            C2 is C * K, clpr_scale_terms(R, K, R2).

        clpr_sub(A, B, R) :- clpr_scale(-1, B, NB), clpr_add(A, NB, R).

        clpr_merge([], T, T).
        clpr_merge([V-C|R], T, Out) :-
            clpr_addterm(V, C, T, T1),
            clpr_merge(R, T1, Out).
        clpr_addterm(V, C, [], Out) :- ( C =:= 0 -> Out = [] ; Out = [V-C] ).
        clpr_addterm(V, C, [V2-C2|R], Out) :-
            ( V == V2 ->
                C3 is C + C2,
                ( C3 =:= 0 -> Out = R ; Out = [V2-C3|R] )
            ; Out = [V2-C2|R1], clpr_addterm(V, C, R, R1)
            ).

        clpr_coeff([], _, 0).
        clpr_coeff([V2-C|R], V, Out) :-
            ( V == V2 -> Out = C ; clpr_coeff(R, V, Out) ).

        % ===== normalise an arithmetic expression to a linear form =====
        clpr_norm(E, lin(E, [])) :- number(E), !.
        clpr_norm(E, LF) :- var(E), !, clpr_var_lf(E, LF).
        clpr_norm(A + B, LF) :- !,
            clpr_norm(A, LA), clpr_norm(B, LB), clpr_add(LA, LB, LF).
        clpr_norm(A - B, LF) :- !,
            clpr_norm(A, LA), clpr_norm(B, LB), clpr_sub(LA, LB, LF).
        clpr_norm(- A, LF) :- !, clpr_norm(A, LA), clpr_scale(-1, LA, LF).
        clpr_norm(A * B, LF) :- !,
            clpr_norm(A, LA), clpr_norm(B, LB),
            ( LA = lin(K, []) -> clpr_scale(K, LB, LF)
            ; LB = lin(K, []) -> clpr_scale(K, LA, LF)
            ; throw(error(type_error(clpr_linear, A * B), _))
            ).
        clpr_norm(A / B, LF) :- !,
            clpr_norm(A, LA), clpr_norm(B, LB),
            ( LB = lin(K, []), K =\= 0 ->
                Inv is 1 / K, clpr_scale(Inv, LA, LF)
            ; throw(error(type_error(clpr_linear, A / B), _))
            ).
        clpr_norm(E, _) :- throw(error(type_error(clpr_expression, E), _)).

        % the linear form of a variable. A CLP(R) variable's attribute is
        % par(Ineqs) when free or dep(Form, Ineqs) when solved; Ineqs is
        % the list of inequalities the variable participates in.
        clpr_var_lf(V, LF) :-
            ( get_attr(V, clpr, A) ->
                ( A = dep(Form, _) -> clpr_expand(Form, LF)
                ; LF = lin(0, [V-1])
                )
            ; put_attr(V, clpr, par([])), LF = lin(0, [V-1])
            ).
        clpr_expand(lin(C, Terms), LF) :-
            clpr_expand_terms(Terms, lin(C, []), LF).
        clpr_expand_terms([], Acc, Acc).
        clpr_expand_terms([V-Coeff|R], Acc, LF) :-
            clpr_norm(V, VLF),
            clpr_scale(Coeff, VLF, Scaled),
            clpr_add(Acc, Scaled, Acc1),
            clpr_expand_terms(R, Acc1, LF).

        clpr_var_ineqs(V, Ineqs) :-
            ( get_attr(V, clpr, A) ->
                ( A = par(I) -> Ineqs = I
                ; A = dep(_, I) -> Ineqs = I
                ; Ineqs = []
                )
            ; Ineqs = []
            ).

        % ===== the equality solver =====
        % solve `LinearForm = 0`: no variables is a consistency check,
        % otherwise a free variable is pivoted out and recorded dependent.
        clpr_solve(lin(C, [])) :- !, clpr_zero(C).
        clpr_solve(lin(C, [P-Cp|Rest])) :-
            NegInv is -1 / Cp,
            C2 is C * NegInv,
            clpr_scale_terms(Rest, NegInv, RestForm),
            clpr_var_ineqs(P, PIneqs),
            put_attr(P, clpr, dep(lin(C2, RestForm), PIneqs)),
            clpr_check([P]).

        clpr_zero(C) :- A is abs(C), A < 0.000000001.

        clpr_eq(E1, E2) :-
            clpr_norm(E1, L1), clpr_norm(E2, L2),
            clpr_sub(L1, L2, D),
            clpr_solve(D).

        % ===== the inequality store =====
        % an inequality is iq(LinearForm, Strict): LinearForm >= 0 when
        % Strict = 0, LinearForm > 0 when Strict = 1.
        clpr_post_ineq(Expr, Strict) :-
            clpr_norm(Expr, lin(C, Terms)),
            Iq = iq(lin(C, Terms), Strict),
            clpr_attach(Terms, Iq),
            clpr_term_vars(Terms, Vars),
            clpr_check(Vars).

        clpr_attach([], _).
        clpr_attach([V-_|R], Iq) :-
            clpr_add_ineq(V, Iq),
            clpr_attach(R, Iq).
        clpr_add_ineq(V, Iq) :-
            get_attr(V, clpr, A),
            ( A = par(I) -> put_attr(V, clpr, par([Iq|I]))
            ; A = dep(F, I) -> put_attr(V, clpr, dep(F, [Iq|I]))
            ).

        clpr_term_vars([], []).
        clpr_term_vars([V-_|R], [V|Rest]) :- clpr_term_vars(R, Rest).

        % gather the connected component of inequalities reachable from
        % the seed variables, re-expand them through the current equality
        % solution, and test the result for satisfiability.
        clpr_check(Seed) :-
            clpr_gather(Seed, [], [], Raw),
            clpr_dedup(Raw, Ineqs),
            clpr_reexpand(Ineqs, Expanded),
            clpr_fm_sat(Expanded).

        clpr_gather([], _, Acc, Acc).
        clpr_gather([V|Vs], Seen, Acc, Out) :-
            ( var(V), \+ clpr_memq(V, Seen) ->
                clpr_var_ineqs(V, VIneqs),
                clpr_ineqs_vars(VIneqs, MoreVars),
                append(VIneqs, Acc, Acc1),
                append(MoreVars, Vs, Vs1),
                clpr_gather(Vs1, [V|Seen], Acc1, Out)
            ; clpr_gather(Vs, Seen, Acc, Out)
            ).

        clpr_ineqs_vars([], []).
        clpr_ineqs_vars([iq(lin(_, Terms), _)|R], Vars) :-
            clpr_term_vars(Terms, V1),
            clpr_ineqs_vars(R, V2),
            append(V1, V2, Vars).

        clpr_memq(X, [Y|_]) :- X == Y, !.
        clpr_memq(X, [_|T]) :- clpr_memq(X, T).

        clpr_dedup([], []).
        clpr_dedup([X|R], Out) :-
            ( clpr_memq(X, R) -> clpr_dedup(R, Out)
            ; Out = [X|O1], clpr_dedup(R, O1)
            ).

        clpr_reexpand([], []).
        clpr_reexpand([iq(Lin, S)|R], [iq(Lin2, S)|R2]) :-
            clpr_expand(Lin, Lin2),
            clpr_reexpand(R, R2).

        % ===== Fourier-Motzkin satisfiability =====
        clpr_fm_sat(Ineqs) :-
            clpr_ineqs_vars(Ineqs, Raw),
            clpr_dedup(Raw, Vars),
            clpr_fm(Vars, Ineqs).

        clpr_fm([], Ineqs) :- !, clpr_fm_ground(Ineqs).
        clpr_fm([V|Vs], Ineqs) :-
            clpr_partition(Ineqs, V, Pos, Neg, Zero),
            clpr_combine_all(Pos, Neg, V, Comb),
            append(Comb, Zero, Next),
            clpr_fm(Vs, Next).

        clpr_fm_ground([]).
        clpr_fm_ground([iq(lin(C, _), S)|R]) :-
            ( S =:= 0 -> C >= -0.000000001
            ; C > 0.000000001
            ),
            clpr_fm_ground(R).

        clpr_partition([], _, [], [], []).
        clpr_partition([Iq|R], V, Pos, Neg, Zero) :-
            clpr_partition(R, V, P1, N1, Z1),
            Iq = iq(lin(_, Terms), _),
            clpr_coeff(Terms, V, Cv),
            ( Cv > 0 -> Pos = [Iq|P1], Neg = N1, Zero = Z1
            ; Cv < 0 -> Pos = P1, Neg = [Iq|N1], Zero = Z1
            ; Pos = P1, Neg = N1, Zero = [Iq|Z1]
            ).

        clpr_combine_all([], _, _, []).
        clpr_combine_all([P|Ps], Neg, V, Out) :-
            clpr_combine_one(P, Neg, V, O1),
            clpr_combine_all(Ps, Neg, V, O2),
            append(O1, O2, Out).
        clpr_combine_one(_, [], _, []).
        clpr_combine_one(P, [N|Ns], V, [C|Rest]) :-
            clpr_combine(P, N, V, C),
            clpr_combine_one(P, Ns, V, Rest).

        % combine a `+V` inequality with a `-V` one into a non-negative
        % linear combination from which V has cancelled.
        clpr_combine(iq(LinP, Sp), iq(LinN, Sn), V, iq(LinC, Sc)) :-
            LinP = lin(_, Tp), clpr_coeff(Tp, V, A),
            LinN = lin(_, Tn), clpr_coeff(Tn, V, D),
            NegD is -D,
            clpr_scale(NegD, LinP, LP2),
            clpr_scale(A, LinN, LN2),
            clpr_add(LP2, LN2, LinC),
            ( ( Sp =:= 1 ; Sn =:= 1 ) -> Sc = 1 ; Sc = 0 ).

        % ===== the {}/1 constraint wrapper =====
        %! {}(+Constraints) | CLP(R) | Posts linear equality and inequality constraints over the reals, written {C1, C2, ...}.
        '{}'(Constraints) :-
            clpr_post(Constraints),
            clpr_cvars(Constraints, [], Vars),
            clpr_settle(Vars).

        clpr_post((A, B)) :- !, clpr_post(A), clpr_post(B).
        clpr_post(E1 =:= E2) :- !, clpr_eq(E1, E2).
        clpr_post(E1 = E2) :- !, clpr_eq(E1, E2).
        clpr_post(E1 < E2) :- !, clpr_post_ineq(E2 - E1, 1).
        clpr_post(E1 > E2) :- !, clpr_post_ineq(E1 - E2, 1).
        clpr_post(E1 =< E2) :- !, clpr_post_ineq(E2 - E1, 0).
        clpr_post(E1 >= E2) :- !, clpr_post_ineq(E1 - E2, 0).
        clpr_post(C) :- throw(error(type_error(clpr_constraint, C), _)).

        clpr_cvars((A, B), Acc, Out) :- !,
            clpr_cvars(A, Acc, A1), clpr_cvars(B, A1, Out).
        clpr_cvars(E1 =:= E2, Acc, Out) :- !,
            clpr_evars(E1, Acc, A1), clpr_evars(E2, A1, Out).
        clpr_cvars(E1 = E2, Acc, Out) :- !,
            clpr_evars(E1, Acc, A1), clpr_evars(E2, A1, Out).
        clpr_cvars(E1 < E2, Acc, Out) :- !,
            clpr_evars(E1, Acc, A1), clpr_evars(E2, A1, Out).
        clpr_cvars(E1 > E2, Acc, Out) :- !,
            clpr_evars(E1, Acc, A1), clpr_evars(E2, A1, Out).
        clpr_cvars(E1 =< E2, Acc, Out) :- !,
            clpr_evars(E1, Acc, A1), clpr_evars(E2, A1, Out).
        clpr_cvars(E1 >= E2, Acc, Out) :- !,
            clpr_evars(E1, Acc, A1), clpr_evars(E2, A1, Out).
        clpr_cvars(_, Acc, Acc).

        clpr_evars(E, Acc, Acc) :- number(E), !.
        clpr_evars(E, Acc, Out) :- var(E), !,
            ( clpr_memq(E, Acc) -> Out = Acc ; Out = [E|Acc] ).
        clpr_evars(A + B, Acc, Out) :- !,
            clpr_evars(A, Acc, A1), clpr_evars(B, A1, Out).
        clpr_evars(A - B, Acc, Out) :- !,
            clpr_evars(A, Acc, A1), clpr_evars(B, A1, Out).
        clpr_evars(- A, Acc, Out) :- !, clpr_evars(A, Acc, Out).
        clpr_evars(A * B, Acc, Out) :- !,
            clpr_evars(A, Acc, A1), clpr_evars(B, A1, Out).
        clpr_evars(A / B, Acc, Out) :- !,
            clpr_evars(A, Acc, A1), clpr_evars(B, A1, Out).
        clpr_evars(_, Acc, Acc).

        % bind every variable whose solved form is now a constant, to a
        % fixpoint — binding one variable can determine others.
        clpr_settle(Vars) :-
            ( clpr_bind_one(Vars) -> clpr_settle(Vars) ; true ).
        clpr_bind_one([V|Vs]) :-
            ( var(V), clpr_norm(V, lin(C, [])) -> V = C
            ; clpr_bind_one(Vs)
            ).

        % ===== the verify_attributes hook =====
        verify_attributes(clpr, AttrValue, Value, Goals) :-
            ( number(Value) -> true ; var(Value) ),
            ( AttrValue = dep(Form, _) -> Goals = ['$clpr_dep_eq'(Form, Value)]
            ; Goals = []
            ).
        '$clpr_dep_eq'(Form, Value) :-
            clpr_expand(Form, FLF),
            clpr_norm(Value, VLF),
            clpr_sub(FLF, VLF, D),
            clpr_solve(D).
        """;
}
