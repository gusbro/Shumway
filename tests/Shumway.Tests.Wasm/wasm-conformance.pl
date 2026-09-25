% Shared wasm-tier conformance corpus. Run by BOTH the desktop runner
% (WasmConformanceTests, under none/jit/all) and the browser (#wasmtests).
% The point is a differential: every case must give the SAME answer whether
% no predicate is on the tier, some are, or all are -- a case that passes on
% Tier-0 and fails promoted is a tier bug, which is how the tabling and cut
% bugs showed.
%
% A case is  wc_case(Name, Goal).  Goal must succeed (run once). "Must fail"
% is written  \+ Goal ; "must raise" is  catch(Goal, E, nonvar(E)). Keep each
% case deterministic and self-contained: they share one engine, so a case
% that leaves choice points or asserts across names can perturb its
% neighbours. The predicates UNDER test are ordinary clauses above -- those
% are what promote to the tier; the case just drives them.

:- use_module(library(clpfd)).
:- use_module(library(clpr)).
:- use_module(library(coroutining)).

% ---- predicates under test (these promote) ----

a(1). a(2). b(3). b(4).
conj(X)  :- ( a(X), ! ; b(X) ).
nocut(X) :- ( a(X) ; b(X) ).
deep(X)  :- ( a(X), ( X > 1, ! ; true ) ; b(X) ).
arrow(X) :- ( a(X) -> true ; X = none ).
outer(X, Y) :- a(X), conj(Y).

% meta-called goals the module open-codes: the goal is built at runtime so
% nothing about it is static.
mkeq(A, B, (A = B)).
mkid(A, B, (A == B)).
mkne(A, B, (A \== B)).
mktype(T, X, G) :- G =.. [T, X].
mkattr(X, M, V, get_attr(X, M, V)).
runq(G) :- call(G).

app([], L, L).
app([H|T], L, [H|R]) :- app(T, L, R).

nrev([], []).
nrev([H|T], R) :- nrev(T, RT), app(RT, [H], R).

tak(X, Y, Z, A) :-
    ( X =< Y -> A = Z
    ; X1 is X - 1, tak(X1, Y, Z, A1),
      Y1 is Y - 1, tak(Y1, Z, X, A2),
      Z1 is Z - 1, tak(Z1, X, Y, A3),
      tak(A1, A2, A3, A) ).

sumlist([], 0).
sumlist([H|T], S) :- sumlist(T, S0), S is S0 + H.

mul(A, B, P) :- P is A * B.       % overflow leaves the 60-bit cell

:- table fib/2.
fib(0, 0).
fib(1, 1).
fib(N, F) :- N > 1, A is N - 1, B is N - 2,
             fib(A, FA), fib(B, FB), F is FA + FB.

:- table tpath/2.
edge(p, q). edge(q, r). edge(r, p). edge(r, s).
tpath(X, Y) :- edge(X, Y).
tpath(X, Y) :- tpath(X, Z), edge(Z, Y).

frozen_probe(X, S) :- freeze(X, S = woke), X = 1.

% ---- the cases ----

% cut carried through a runtime meta-call (=$call'/2 territory)
wc_case(conj_cut,       (findall(X, conj(X), L), L == [1])).
wc_case(nocut_all,      (findall(X, nocut(X), L), L == [1,2,3,4])).
wc_case(deep_cut,       (findall(X, deep(X), L), L == [1,2])).
wc_case(arrow_then,     (findall(X, arrow(X), L), L == [1])).
wc_case(outer_keeps_cp, (findall(X-Y, outer(X, Y), L),
                         L == [1-1, 2-1])).

% =/2 meta-called: the module open-codes it, on the goal's own args
wc_case(eq_var_const,   (mkeq(X, one, G), runq(G), X == one)).
wc_case(eq_two_vars,    (mkeq(X, Y, G), runq(G), X == Y)).
wc_case(eq_compound,    (mkeq(X, f(1, 2), G), runq(G), X == f(1, 2))).
wc_case(eq_fail,        \+ (mkeq(a, b, G), runq(G))).

% ==/2 and \==/2 meta-called, deciding and declining shapes
wc_case(id_atoms_yes,   (mkid(foo, foo, G), runq(G))).
wc_case(id_atoms_no,    \+ (mkid(foo, bar, G), runq(G))).
wc_case(id_compound,    (mkid(f(1), f(1), G), runq(G))).
wc_case(id_float,       (mkid(1.5, 1.5, G), runq(G))).
wc_case(ne_yes,         (mkne(foo, bar, G), runq(G))).
wc_case(ne_no,          \+ (mkne(foo, foo, G), runq(G))).

% type tests meta-called, built with =..
wc_case(ty_var,         (mktype(var, _, G), runq(G))).
wc_case(ty_nonvar,      (mktype(nonvar, foo, G), runq(G))).
wc_case(ty_integer_big, (mktype(integer, 123456789012345678901234567890, G),
                         runq(G))).
wc_case(ty_atom_nil,    (mktype(atom, [], G), runq(G))).
wc_case(ty_compound,    (mktype(compound, [a], G), runq(G))).
wc_case(ty_compound_no, \+ (mktype(compound, foo, G), runq(G))).

% get_attr/3 meta-called: hit, miss, plain var, and the module-error order
wc_case(attr_hit,       (put_attr(X, m, v), mkattr(X, m, V, G), runq(G),
                         V == v)).
wc_case(attr_miss,      (put_attr(X, m, v), \+ (mkattr(X, other, _, G), runq(G)))).
wc_case(attr_plainvar,  \+ (mkattr(_, m, _, G), runq(G))).
wc_case(attr_mod_error, catch((mkattr(_, 42, _, G), runq(G)), E, nonvar(E))).

% arithmetic in the module, including the overflow escalation
wc_case(arith_sum,      (sumlist([1,2,3,4,5], S), S =:= 15)).
wc_case(arith_mul,      (mul(123456, 1000, P), P =:= 123456000)).

% recursion and backtracking on the tier
wc_case(app_forward,    (app([1,2], [3,4], L), L == [1,2,3,4])).
wc_case(app_split,      (findall(A-B, app(A, B, [1,2]), L),
                         L == [[]-[1,2], [1]-[2], [1,2]-[]])).
wc_case(nrev_10,        (numlist(1, 10, L), nrev(L, R),
                         R == [10,9,8,7,6,5,4,3,2,1])).
wc_case(tak_std,        (tak(18, 12, 6, A), A =:= 7)).

% attributed variables: freeze, dif, clpfd, clpr, on the tier
wc_case(freeze_wakes,   (frozen_probe(_, S), S == woke)).
wc_case(dif_ok,         (dif(A, B), A = 1, B = 2)).
wc_case(dif_veto,       \+ (dif(A, B), A = 1, B = 1)).
wc_case(clpfd_label,    (X in 1..3, X #> 1, X #< 3, label([X]), X == 2)).
wc_case(clpfd_alldiff,  (Vs = [P,Q], Vs ins 1..2, all_distinct(Vs),
                         P #< Q, label(Vs), P == 1, Q == 2)).
wc_case(clpr_solve,     ({A + B =:= 10, A - B =:= 2}, A =:= 6.0, B =:= 4.0)).

% tabling on the tier: the reported bug (fib(30) answering true unbound) and
% a ground call that must fail, plus left-recursion over a cycle
wc_case(fib_30,         (fib(30, N), N =:= 832040)).
wc_case(fib_ground_ok,  fib(10, 55)).
wc_case(fib_ground_no,  \+ fib(10, 54)).
wc_case(tpath_all,      (findall(Y, tpath(p, Y), L), sort(L, S),
                         S == [p, q, r, s])).
wc_case(arith_add_ovf,  (wc_add_ovf(X), X =:= 576460752303423488)).
wc_case(arith_mul_ovf,  (wc_mul_ovf(X), X =:= 1152921504606846974)).
wc_case(arith_sub_ovf,  (wc_sub_ovf(X), X =:= -576460752303423489)).

% Arithmetic that escalates out of the 60-bit inline integer lane. The tier
% cannot represent the result, so it steps aside and the engine finishes the
% work in bigger numbers. Held out of this corpus while a deopt on a
% predicate's FIRST call was being handed back to the tier as though it were a
% tail call, which spun instead of answering.
wc_add_ovf(X) :- X is 576460752303423487 + 1.
wc_mul_ovf(X) :- X is 576460752303423487 * 2.
wc_sub_ovf(X) :- X is -576460752303423488 - 1.

% ---- the harness (environment-agnostic) ----

wc_ok(G) :- catch(once(G), _E, fail).

% Names of the cases that did NOT hold. Empty list == everything passed.
wc_failed(Failed) :- findall(N, (wc_case(N, G), \+ wc_ok(G)), Failed).

% How many cases exist, for an anti-vacuity guard.
wc_count(Total) :- findall(x, wc_case(_, _), Xs), length(Xs, Total).

% The runner, in Prolog: consult this file and call wc_run(Failed). Failed is
% the list of case names that did not hold; [] is a clean sweep. save/0 after
% the corpus loads, restore/0 before each case, so tabling answers and any
% asserted state from one case do not leak into the next.
:- dynamic(wc_run/1).
wc_run(Failed) :-
    findall(N, (wc_case(N, G), \+ catch(once(G), _E, fail)), Failed).
