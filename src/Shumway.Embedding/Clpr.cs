namespace Shumway.Embedding;

/// <summary>
/// The CLP(R) library — constraint logic programming over the reals. It
/// is an <em>opt-in</em> module: an embedder calls
/// <see cref="PrologEngine.UseClpr"/> to consult it.
///
/// <para>Constraints are posted with the <c>{Constraint, ...}</c>
/// wrapper. Equalities (<c>=:=</c>, <c>=</c>) feed a Gaussian-elimination
/// solver; inequalities (<c>&lt;</c>, <c>&gt;</c>, <c>=&lt;</c>,
/// <c>&gt;=</c>) are checked for joint satisfiability by Fourier–Motzkin
/// elimination on every post. A disequality (<c>=\=</c>) is kept and
/// fails only when the inequalities entail that its linear form is pinned
/// to zero. A non-linear constraint (a product or quotient of two
/// non-constants) is delayed and retried whenever a variable it mentions
/// is determined — once it turns linear it is posted for real.
/// Variables determined to a single value are bound to it.</para>
///
/// <para>CLP(R) and CLP(FD) can share an engine: both declare their
/// <c>verify_attributes/4</c> hook <c>:- multifile</c>, and the hook's first
/// argument (the attribute module) dispatches each wakeup to the right
/// library. Constraints must stay per-library — a variable carrying BOTH
/// clpfd and clpr attributes is not supported.</para>
/// </summary>
internal static class Clpr
{
    public const string ModuleName = "clpr";

    public const string Source = """
        :- module(clpr).

        :- public '{}'/1.
        :- public verify_attributes/4.
        :- multifile verify_attributes/4.
        :- public '$clpr_dep_eq'/2.
        :- public clpr_attr_goals/3.

        % ===== linear forms: lin(Constant, [Var-Coeff, ...]) =====
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

        % ===== normalise an expression to a linear form =====
        % a product or quotient of two non-constants is non-linear: the
        % clause fails (the caller delays the whole constraint) rather
        % than throwing.
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
            ; fail
            ).
        clpr_norm(A / B, LF) :- !,
            clpr_norm(A, LA), clpr_norm(B, LB),
            ( LB = lin(K, []) ->
                ( K =\= 0 -> Inv is 1 / K, clpr_scale(Inv, LA, LF)
                ; throw(error(evaluation_error(zero_divisor), _))
                )
            ; fail
            ).
        clpr_norm(E, _) :- throw(error(type_error(clpr_expression, E), _)).

        % a CLP(R) variable's attribute is par(Cons) when free or
        % dep(Form, Cons) when solved; Cons is the list of inequality,
        % disequality and delayed non-linear constraints it participates in.
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

        clpr_var_cons(V, Cons) :-
            ( get_attr(V, clpr, A) ->
                ( A = par(I) -> Cons = I
                ; A = dep(_, I) -> Cons = I
                ; Cons = []
                )
            ; Cons = []
            ).

        % ===== the equality solver =====
        clpr_solve(lin(C, [])) :- !, clpr_zero(C).
        clpr_solve(lin(C, [P-Cp|Rest])) :-
            NegInv is -1 / Cp,
            C2 is C * NegInv,
            clpr_scale_terms(Rest, NegInv, RestForm),
            clpr_var_cons(P, PCons),
            put_attr(P, clpr, dep(lin(C2, RestForm), PCons)),
            % P's form is written in terms of the variables in RestForm, and
            % nothing else records that it is. Without the back-link, solving
            % one of THEM later leaves P determined but unbound: {A+B =:= 10}
            % and then {B =:= 4} would answer with a residual for A rather
            % than 6.0.
            clpr_link(RestForm, P),
            clpr_check([P]),
            clpr_dependents([P], [], Affected),
            clpr_settle(Affected).

        % de(P) reads "P's form mentions me". A back-pointer, not a
        % constraint: clpr_split steps over it and it projects as nothing.
        clpr_link([], _).
        clpr_link([V-_|R], P) :-
            clpr_var_cons(V, Cons),
            ( clpr_memq(de(P), Cons) -> true ; clpr_attach([V-0], de(P)) ),
            clpr_link(R, P).

        % Every variable that depends, transitively, on one of Seed, Seed
        % included: the set a newly determined value can settle.
        clpr_dependents([], Seen, Seen).
        clpr_dependents([V|Vs], Seen, Out) :-
            ( var(V), \+ clpr_memq(V, Seen) ->
                clpr_var_cons(V, Cons),
                clpr_dep_targets(Cons, Ds),
                append(Ds, Vs, Vs1),
                clpr_dependents(Vs1, [V|Seen], Out)
            ; clpr_dependents(Vs, Seen, Out)
            ).
        clpr_dep_targets([], []).
        clpr_dep_targets([de(P)|R], [P|Ps]) :- !, clpr_dep_targets(R, Ps).
        clpr_dep_targets([_|R], Ps) :- clpr_dep_targets(R, Ps).

        clpr_zero(C) :- A is abs(C), A < 0.000000001.

        % ===== posting constraints =====
        clpr_post((A, B)) :- !, clpr_post(A), clpr_post(B).
        clpr_post(C) :- C = (E1 =:= E2), !, clpr_post_eq(E1, E2, C).
        clpr_post(C) :- C = (E1 = E2),   !, clpr_post_eq(E1, E2, C).
        clpr_post(C) :- C = (E1 < E2),   !, clpr_post_iq(E2 - E1, 1, C).
        clpr_post(C) :- C = (E1 > E2),   !, clpr_post_iq(E1 - E2, 1, C).
        clpr_post(C) :- C = (E1 =< E2),  !, clpr_post_iq(E2 - E1, 0, C).
        clpr_post(C) :- C = (E1 >= E2),  !, clpr_post_iq(E1 - E2, 0, C).
        clpr_post(C) :- C = (E1 =\= E2), !, clpr_post_dq(E1 - E2, C).
        clpr_post(C) :- throw(error(type_error(clpr_constraint, C), _)).

        clpr_post_eq(E1, E2, Orig) :-
            ( clpr_norm(E1, L1), clpr_norm(E2, L2) ->
                clpr_sub(L1, L2, D), clpr_solve(D)
            ; clpr_delay(Orig)
            ).

        clpr_post_iq(Expr, Strict, Orig) :-
            ( clpr_norm(Expr, lin(C, Terms)) ->
                Iq = iq(lin(C, Terms), Strict),
                clpr_attach(Terms, Iq),
                clpr_term_vars(Terms, Vars),
                clpr_check(Vars)
            ; clpr_delay(Orig)
            ).

        clpr_post_dq(Expr, Orig) :-
            ( clpr_norm(Expr, lin(C, Terms)) ->
                ( Terms == [] -> \+ clpr_zero(C)
                ; clpr_attach(Terms, dq(lin(C, Terms))),
                  clpr_term_vars(Terms, Vars),
                  clpr_check(Vars)
                )
            ; clpr_delay(Orig)
            ).

        % delay a non-linear constraint: store it on its variables so a
        % later determination triggers a retry.
        clpr_delay(Orig) :-
            clpr_cvars(Orig, [], Vars),
            clpr_store_nl(Vars, nl(Orig)).
        clpr_store_nl([], _).
        clpr_store_nl([V|Vs], Entry) :-
            ( get_attr(V, clpr, A) -> true
            ; A = par([]), put_attr(V, clpr, A)
            ),
            ( A = par(I) ->
                ( clpr_memq(Entry, I) -> true
                ; put_attr(V, clpr, par([Entry|I]))
                )
            ; A = dep(F, I) ->
                ( clpr_memq(Entry, I) -> true
                ; put_attr(V, clpr, dep(F, [Entry|I]))
                )
            ),
            clpr_store_nl(Vs, Entry).

        clpr_attach([], _).
        clpr_attach([V-_|R], Con) :-
            get_attr(V, clpr, A),
            ( A = par(I) -> put_attr(V, clpr, par([Con|I]))
            ; A = dep(F, I) -> put_attr(V, clpr, dep(F, [Con|I]))
            ),
            clpr_attach(R, Con).

        clpr_term_vars([], []).
        clpr_term_vars([V-_|R], [V|Rest]) :- clpr_term_vars(R, Rest).

        % ===== the satisfiability check =====
        % gather the connected component of constraints reachable from the
        % seed variables, then test it: inequalities by Fourier-Motzkin,
        % disequalities by an entailment check, non-linear ones by retry.
        clpr_check(Seed) :-
            clpr_gather(Seed, [], [], Raw),
            clpr_dedup(Raw, Cons),
            clpr_split(Cons, Iqs, Dqs, Nls),
            clpr_reexpand_iqs(Iqs, EIqs),
            clpr_fm_sat(EIqs),
            clpr_check_dqs(Dqs, EIqs),
            clpr_retry_nls(Nls).

        clpr_gather([], _, Acc, Acc).
        clpr_gather([V|Vs], Seen, Acc, Out) :-
            ( var(V), \+ clpr_memq(V, Seen) ->
                clpr_var_cons(V, VCons),
                clpr_cons_vars(VCons, MoreVars),
                append(VCons, Acc, Acc1),
                append(MoreVars, Vs, Vs1),
                clpr_gather(Vs1, [V|Seen], Acc1, Out)
            ; clpr_gather(Vs, Seen, Acc, Out)
            ).

        clpr_cons_vars([], []).
        clpr_cons_vars([Con|R], Vars) :-
            clpr_con_vars(Con, V1),
            clpr_cons_vars(R, V2),
            append(V1, V2, Vars).
        clpr_con_vars(iq(lin(_, Terms), _), Vs) :- !, clpr_term_vars(Terms, Vs).
        clpr_con_vars(dq(lin(_, Terms)), Vs) :- !, clpr_term_vars(Terms, Vs).
        clpr_con_vars(de(P), [P]) :- !.
        clpr_con_vars(nl(C), Vs) :- clpr_cvars(C, [], Vs).

        clpr_memq(X, [Y|_]) :- X == Y, !.
        clpr_memq(X, [_|T]) :- clpr_memq(X, T).

        clpr_dedup([], []).
        clpr_dedup([X|R], Out) :-
            ( clpr_memq(X, R) -> clpr_dedup(R, Out)
            ; Out = [X|O1], clpr_dedup(R, O1)
            ).

        clpr_split([], [], [], []).
        clpr_split([iq(L, S)|R], [iq(L, S)|I], D, N) :- !, clpr_split(R, I, D, N).
        clpr_split([dq(L)|R], I, [dq(L)|D], N) :- !, clpr_split(R, I, D, N).
        clpr_split([de(_)|R], I, D, N) :- !, clpr_split(R, I, D, N).
        clpr_split([nl(C)|R], I, D, [nl(C)|N]) :- clpr_split(R, I, D, N).

        clpr_reexpand_iqs([], []).
        clpr_reexpand_iqs([iq(L, S)|R], [iq(L2, S)|R2]) :-
            clpr_expand(L, L2),
            clpr_reexpand_iqs(R, R2).

        % a disequality lf =\= 0 fails only when the inequalities pin lf to
        % zero — i.e. neither lf > 0 nor lf < 0 is feasible with them.
        clpr_check_dqs([], _).
        clpr_check_dqs([dq(L)|R], Iqs) :-
            clpr_expand(L, EL),
            clpr_check_dq(EL, Iqs),
            clpr_check_dqs(R, Iqs).
        clpr_check_dq(lin(C, []), _) :- !, \+ clpr_zero(C).
        clpr_check_dq(lin(C, Terms), Iqs) :-
            clpr_scale(-1, lin(C, Terms), Neg),
            ( clpr_fm_sat([iq(lin(C, Terms), 1)|Iqs]) -> true
            ; clpr_fm_sat([iq(Neg, 1)|Iqs])
            ).

        % retry each delayed non-linear constraint: if it is linear now,
        % drop the delayed copy and post it for real.
        clpr_retry_nls([]).
        clpr_retry_nls([nl(C)|R]) :-
            ( clpr_is_linear_now(C) ->
                clpr_cvars(C, [], Vars),
                clpr_remove(Vars, nl(C)),
                clpr_post(C)
            ; true
            ),
            clpr_retry_nls(R).
        clpr_is_linear_now(E1 =:= E2)  :- !, clpr_norm(E1, _), clpr_norm(E2, _).
        clpr_is_linear_now(E1 = E2)    :- !, clpr_norm(E1, _), clpr_norm(E2, _).
        clpr_is_linear_now(E1 < E2)    :- !, clpr_norm(E1, _), clpr_norm(E2, _).
        clpr_is_linear_now(E1 > E2)    :- !, clpr_norm(E1, _), clpr_norm(E2, _).
        clpr_is_linear_now(E1 =< E2)   :- !, clpr_norm(E1, _), clpr_norm(E2, _).
        clpr_is_linear_now(E1 >= E2)   :- !, clpr_norm(E1, _), clpr_norm(E2, _).
        clpr_is_linear_now(E1 =\= E2)  :- !, clpr_norm(E1, _), clpr_norm(E2, _).

        clpr_remove([], _).
        clpr_remove([V|Vs], Entry) :-
            ( get_attr(V, clpr, A) ->
                ( A = par(I) -> clpr_del(I, Entry, I2), put_attr(V, clpr, par(I2))
                ; A = dep(F, I) -> clpr_del(I, Entry, I2), put_attr(V, clpr, dep(F, I2))
                ; true
                )
            ; true
            ),
            clpr_remove(Vs, Entry).
        clpr_del([], _, []).
        clpr_del([X|R], E, Out) :-
            ( X == E -> clpr_del(R, E, Out)
            ; Out = [X|O1], clpr_del(R, E, O1)
            ).

        % ===== Fourier-Motzkin satisfiability =====
        clpr_fm_sat(Ineqs) :-
            clpr_cons_vars(Ineqs, Raw),
            clpr_dedup(Raw, Vars),
            clpr_fm(Vars, Ineqs).

        % The same elimination, kept rather than decided: what remains after
        % dropping these variables is the projection onto the others.
        clpr_fm_project([], Ineqs, Ineqs).
        clpr_fm_project([V|Vs], Ineqs, Out) :-
            clpr_partition(Ineqs, V, Pos, Neg, Zero),
            clpr_combine_all(Pos, Neg, V, Comb),
            append(Comb, Zero, Next),
            clpr_fm_project(Vs, Next, Out).

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
        clpr_combine(iq(LinP, Sp), iq(LinN, Sn), V, iq(LinC, Sc)) :-
            LinP = lin(_, Tp), clpr_coeff(Tp, V, A),
            LinN = lin(_, Tn), clpr_coeff(Tn, V, D),
            NegD is -D,
            clpr_scale(NegD, LinP, LP2),
            clpr_scale(A, LinN, LN2),
            clpr_add(LP2, LN2, LinC),
            ( ( Sp =:= 1 ; Sn =:= 1 ) -> Sc = 1 ; Sc = 0 ).

        % ===== the {}/1 constraint wrapper =====
        %! {}(+Constraints) | CLP(R) | Posts equality, inequality, disequality and (delayed) non-linear constraints over the reals.
        '{}'(Constraints) :-
            clpr_post(Constraints),
            clpr_cvars(Constraints, [], Vars),
            clpr_settle(Vars).

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
        clpr_cvars(E1 =\= E2, Acc, Out) :- !,
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

        % ===== asking the store questions (SWI / SICStus surface) =====

        %! entailed(+Constraint) | CLP(R) | True when the store already implies Constraint, without adding it. Asks whether the negation is unsatisfiable, so the store is left exactly as it was.
        :- public entailed/1.
        entailed((A, B)) :- !, entailed(A), entailed(B).
        entailed(C) :-
            clpr_negate(C, N),
            \+ '{}'(N).

        clpr_negate(A =:= B, A =\= B) :- !.
        clpr_negate(A =\= B, A =:= B) :- !.
        clpr_negate(A  <  B, A >= B) :- !.
        clpr_negate(A  >  B, A =< B) :- !.
        clpr_negate(A =<  B, A  > B) :- !.
        clpr_negate(A >=  B, A  < B) :- !.
        clpr_negate(A  =  B, A =\= B) :- !.
        clpr_negate(C, _) :- throw(error(type_error(clpr_constraint, C), _)).

        %! inf(+Expr, -Inf) | CLP(R) | The infimum of Expr under the current store: the greatest lower bound the constraints imply. Fails when Expr is unbounded below.
        :- public inf/2.
        inf(Expr, Inf) :- clpr_optimum(Expr, min, Inf).

        %! sup(+Expr, -Sup) | CLP(R) | The supremum of Expr under the current store: the least upper bound the constraints imply. Fails when Expr is unbounded above.
        :- public sup/2.
        sup(Expr, Sup) :- clpr_optimum(Expr, max, Sup).

        %! minimize(+Expr) | CLP(R) | Pins Expr to its infimum, adding that equation to the store. Fails when Expr is unbounded below.
        :- public minimize/1.
        minimize(Expr) :- inf(Expr, I), '{}'(Expr =:= I).

        %! maximize(+Expr) | CLP(R) | Pins Expr to its supremum, adding that equation to the store. Fails when Expr is unbounded above.
        :- public maximize/1.
        maximize(Expr) :- sup(Expr, S), '{}'(Expr =:= S).

        % Optimising a linear objective with the machinery already here:
        % introduce Z =:= Expr as two inequalities, eliminate every OTHER
        % variable by Fourier-Motzkin, and read the bounds off what is left,
        % which mentions Z alone. Equalities need no special handling: the
        % expansion below substitutes each dependent variable's form first.
        clpr_optimum(Expr, Dir, Value) :-
            clpr_norm(Expr, ELF0),
            clpr_expand(ELF0, lin(EC, ETerms)),
            ( ETerms == [] -> Value = EC          % already a number
            ; clpr_term_vars(ETerms, EVars),
              clpr_gather(EVars, [], [], Raw),
              clpr_dedup(Raw, Cons),
              clpr_split(Cons, Iqs, _, _),
              clpr_reexpand_iqs(Iqs, EIqs),
              % Z - Expr >= 0 and Expr - Z >= 0, i.e. Z =:= Expr.
              clpr_sub(lin(0, [Z-1]), lin(EC, ETerms), Up),
              clpr_sub(lin(EC, ETerms), lin(0, [Z-1]), Down),
              All = [iq(Up, 0), iq(Down, 0)|EIqs],
              clpr_cons_vars(All, RawVars),
              clpr_dedup(RawVars, AllVars),
              clpr_del(AllVars, Z, ElimVars),
              % clpr_fm/2 DECIDES; this keeps what the elimination leaves.
              clpr_fm_project(ElimVars, All, Left),
              clpr_zbounds(Left, Z, none, none, Lo, Hi),
              ( Dir == min -> Lo = bound(Value) ; Hi = bound(Value) )
            ).

        % After the elimination every inequality is C + A*Z >= 0 (or > 0):
        % a positive A is a lower bound on Z, a negative one an upper bound.
        % The forms here are already expanded (the objective was, and the
        % inequalities were re-expanded before the elimination). Expanding
        % again would call clpr_norm on Z and ATTACH an attribute to it: a
        % constraint-store entry on a variable that dies with this call.
        clpr_zbounds([], _, Lo, Hi, Lo, Hi).
        clpr_zbounds([iq(lin(C, Terms), _)|R], Z, Lo0, Hi0, Lo, Hi) :-
            clpr_coeff(Terms, Z, A),
            ( A > 0.000000001 ->
                B is -C / A, clpr_tighter(Lo0, B, max, Lo1), Hi1 = Hi0
            ; A < -0.000000001 ->
                B is -C / A, clpr_tighter(Hi0, B, min, Hi1), Lo1 = Lo0
            ; Lo1 = Lo0, Hi1 = Hi0
            ),
            clpr_zbounds(R, Z, Lo1, Hi1, Lo, Hi).

        clpr_tighter(none, B, _, bound(B)).
        clpr_tighter(bound(Old), B, Dir, bound(New)) :-
            ( Dir == max -> ( B > Old -> New = B ; New = Old )
            ; ( B < Old -> New = B ; New = Old )
            ).

        %! dump(+Vars, +Names, -Constraints) | CLP(R) | The residual constraints on Vars, written over Names instead of the variables themselves. The store is not changed: this reports it.
        :- public dump/3.
        dump(Vars, Names, Constraints) :-
            copy_term(Vars, Copy, Goals),
            Copy = Names,
            Constraints = Goals.

        % ===== constraint projection =====
        % copy_term/3 collects, for every constrained variable of the
        % copied term, the residual constraints re-expressed over the
        % copy. attribute_goals/4 is dynamic (pre-declared by the prelude);
        % a dynamic clause's body is not module-mangled, so it delegates to
        % the public clpr_attr_goals/3, whose body resolves clpr locals.
        attribute_goals(clpr, Attr, V, Goals) :- clpr_attr_goals(Attr, V, Goals).

        % a solved variable projects as `{V =:= Expr}`; every constraint
        % is projected once, by the variable that owns it (the first in
        % its term list), so a shared constraint is not duplicated.
        clpr_attr_goals(dep(Form, Cons), V, Goals) :- !,
            clpr_lin_expr(Form, Expr),
            clpr_owned_goals(Cons, V, CG),
            Goals = [{V =:= Expr}|CG].
        clpr_attr_goals(par(Cons), V, Goals) :- !,
            clpr_owned_goals(Cons, V, Goals).
        clpr_attr_goals(_, _, []).

        clpr_owned_goals([], _, []).
        clpr_owned_goals([Con|R], V, Goals) :-
            ( clpr_owns(Con, V) -> clpr_render(Con, G), Goals = [G|Rest]
            ; Goals = Rest
            ),
            clpr_owned_goals(R, V, Rest).

        clpr_owns(iq(lin(_, [W-_|_]), _), V) :- W == V.
        clpr_owns(dq(lin(_, [W-_|_])), V) :- W == V.
        clpr_owns(nl(C), V) :- clpr_cvars(C, [], [W|_]), W == V.

        clpr_render(iq(Lin, 0), {Expr >= 0}) :- clpr_lin_expr(Lin, Expr).
        clpr_render(iq(Lin, 1), {Expr > 0}) :- clpr_lin_expr(Lin, Expr).
        clpr_render(dq(Lin), {Expr =\= 0}) :- clpr_lin_expr(Lin, Expr).
        clpr_render(nl(C), {C}).

        % render a linear form as an arithmetic expression.
        clpr_lin_expr(lin(C, []), C) :- !.
        clpr_lin_expr(lin(0, [T|Ts]), Expr) :- !, clpr_terms_expr([T|Ts], Expr).
        clpr_lin_expr(lin(C, [T|Ts]), C + Rest) :- clpr_terms_expr([T|Ts], Rest).
        clpr_terms_expr([W-K], TE) :- !, clpr_term_expr(W, K, TE).
        clpr_terms_expr([W-K|R], TE + Rest) :-
            clpr_term_expr(W, K, TE), clpr_terms_expr(R, Rest).
        clpr_term_expr(W, 1, W) :- !.
        clpr_term_expr(W, K, K * W).
        """;
}
