namespace Shumway.Embedding;

/// <summary>
/// The CLP(R) library — constraint logic programming over the reals. It
/// is an <em>opt-in</em> module: an embedder calls
/// <see cref="PrologEngine.UseClpr"/> to consult it.
///
/// <para>Chunk 99 delivers the linear-equality core. Constraints are
/// posted with the <c>{Constraint, ...}</c> wrapper; each constraint is
/// an equality (<c>=:=</c> or <c>=</c>) between linear arithmetic
/// expressions over <c>+</c>, <c>-</c>, <c>*</c> and <c>/</c> by
/// constants. A Gaussian-elimination solver keeps the accumulated
/// equations in solved form: each posted equation is normalised against
/// the current solution, an inconsistent one (<c>c = 0</c> with
/// <c>c &#8800; 0</c>) fails, and otherwise a free variable is pivoted
/// out and recorded — as an attributed-variable attribute — as a linear
/// form over the remaining free variables. Variables determined to a
/// single value are bound to it.</para>
///
/// <para>Inequalities, non-linear constraints and constraint projection
/// are later chunks. CLP(R) and CLP(FD) cannot yet be loaded into the
/// same engine — both define a public <c>verify_attributes/4</c>.</para>
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

        % merge two coefficient lists, combining like variables and
        % dropping any term whose coefficient cancels to zero.
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

        % the linear form of a variable: a parametric (free) variable
        % contributes itself; a dependent variable expands to its solved
        % form; a plain variable is adopted as a fresh parametric one.
        clpr_var_lf(V, LF) :-
            ( get_attr(V, clpr, A) ->
                ( A = dep(Form) -> clpr_expand(Form, LF)
                ; LF = lin(0, [V-1])
                )
            ; put_attr(V, clpr, par), LF = lin(0, [V-1])
            ).
        clpr_expand(lin(C, Terms), LF) :-
            clpr_expand_terms(Terms, lin(C, []), LF).
        clpr_expand_terms([], Acc, Acc).
        clpr_expand_terms([V-Coeff|R], Acc, LF) :-
            clpr_norm(V, VLF),
            clpr_scale(Coeff, VLF, Scaled),
            clpr_add(Acc, Scaled, Acc1),
            clpr_expand_terms(R, Acc1, LF).

        % ===== the solver =====
        % solve `LinearForm = 0`. With no variables it is a consistency
        % check; otherwise a free variable is pivoted out and recorded as
        % dependent on the rest.
        clpr_solve(lin(C, [])) :- !, clpr_zero(C).
        clpr_solve(lin(C, [P-Cp|Rest])) :-
            NegInv is -1 / Cp,
            C2 is C * NegInv,
            clpr_scale_terms(Rest, NegInv, RestForm),
            put_attr(P, clpr, dep(lin(C2, RestForm))).

        clpr_zero(C) :- A is abs(C), A < 0.000000001.

        clpr_eq(E1, E2) :-
            clpr_norm(E1, L1), clpr_norm(E2, L2),
            clpr_sub(L1, L2, D),
            clpr_solve(D).

        % ===== the {}/1 constraint wrapper =====
        %! {}(+Constraints) | CLP(R) | Posts linear-equality constraints over the reals, written {C1, C2, ...} with =:= or =.
        '{}'(Constraints) :-
            clpr_post(Constraints),
            clpr_cvars(Constraints, [], Vars),
            clpr_settle(Vars).

        clpr_post((A, B)) :- !, clpr_post(A), clpr_post(B).
        clpr_post(E1 =:= E2) :- !, clpr_eq(E1, E2).
        clpr_post(E1 = E2) :- !, clpr_eq(E1, E2).
        clpr_post(C) :- throw(error(type_error(clpr_constraint, C), _)).

        % collect the variables occurring in a constraint conjunction
        clpr_cvars((A, B), Acc, Out) :- !,
            clpr_cvars(A, Acc, A1), clpr_cvars(B, A1, Out).
        clpr_cvars(E1 =:= E2, Acc, Out) :- !,
            clpr_evars(E1, Acc, A1), clpr_evars(E2, A1, Out).
        clpr_cvars(E1 = E2, Acc, Out) :- !,
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

        clpr_memq(X, [Y|_]) :- X == Y, !.
        clpr_memq(X, [_|T]) :- clpr_memq(X, T).

        % bind every variable whose solved form is now a constant, to a
        % fixpoint — binding one variable can determine others.
        clpr_settle(Vars) :-
            ( clpr_bind_one(Vars) -> clpr_settle(Vars) ; true ).
        clpr_bind_one([V|Vs]) :-
            ( var(V), clpr_norm(V, lin(C, [])) -> V = C
            ; clpr_bind_one(Vs)
            ).

        % ===== the verify_attributes hook =====
        % binding a CLP(R) variable is a fresh constraint. A free variable
        % bound to a value needs nothing — lazy expansion already reads
        % through it; a dependent variable bound to V means its solved
        % form must equal V, which is posted as a constraint.
        verify_attributes(clpr, AttrValue, Value, Goals) :-
            ( number(Value) -> true ; var(Value) ),
            ( AttrValue = dep(Form) -> Goals = ['$clpr_dep_eq'(Form, Value)]
            ; Goals = []
            ).
        '$clpr_dep_eq'(Form, Value) :-
            clpr_expand(Form, FLF),
            clpr_norm(Value, VLF),
            clpr_sub(FLF, VLF, D),
            clpr_solve(D).
        """;
}
