% Probes of the current behaviour, run before and after each change.
:- dynamic(cnt/1).
cnt(0).
bump :- retract(cnt(N)), N1 is N + 1, assertz(cnt(N1)).

by_key(O, _-K1, _-K2) :- bump, compare(O, K1, K2).
always_foo(foo, _, _).
unbound_order(_, _, _).
nondet_cmp(<, _, _).
nondet_cmp(>, _, _).
throwing_cmp(_, _, _) :- throw(oops).
ascending_cmp(O, A, B) :- bump, compare(O, A, B).

p(X) => X = ssu.
q(1). q(2). q(a). q(f(b)).
:- dynamic(d/2).
d(1, a). d(2, b). d(k, c). d([x], d). d(f(1), e). d(_, anything).
g --> [hello].

show(G) :- ( catch(G, E, (write(caught(E)), nl, fail)) -> write(yes(G)), nl ; write(no(G)), nl ).

main :-
    show((atomic_list_concat([a, 1, 2.5, -3, 'B c', [], 1.0e10], A1), write(A1), nl)),
    show((atomic_list_concat([], A2), write(A2), nl)),
    show(atomic_list_concat([a, b], ab)),
    show(atomic_list_concat([a, b], abc)),
    show(atomic_list_concat([a, b], 1)),
    show(atomic_list_concat([a, _], _)),
    show(atomic_list_concat([a|_], _)),
    show(atomic_list_concat(_, _)),
    show(atomic_list_concat(foo, _)),
    show(atomic_list_concat([f(x)], _)),
    show(atomic_list_concat(["ab"], _)),
    show((atomic_list_concat([a, b, c], '-', A3), write(A3), nl)),
    show((atomic_list_concat([a, b, c], 1, A4), write(A4), nl)),
    show((atomic_list_concat([a, b, c], '', A5), write(A5), nl)),
    show((atomic_list_concat([], '-', A6), write(A6), nl)),
    show((atomic_list_concat([a], '-', A7), write(A7), nl)),
    show(atomic_list_concat([a, b], _, _)),
    show(atomic_list_concat([a, b], f(x), _)),
    show(atomic_list_concat([a, b], '-', 1)),
    show(atomic_list_concat([a, b], '-', 'a-b')),
    show(atomic_list_concat([a, b], '-', 'a+b')),
    show(atomic_list_concat([a|_], '-', 'a-b')),
    show((atomic_list_concat(L1, '-', 'a-b-c'), write(L1), nl)),
    show((atomic_list_concat(L2, '-', '-a--b-'), write(L2), nl)),
    show((atomic_list_concat(L3b, '-', ''), write(L3b), nl)),
    show((atomic_list_concat(L4, '-', abc), write(L4), nl)),
    show((atomic_list_concat(L5, aa, aaa), write(L5), nl)),
    show((atomic_list_concat(L6, ', ', 'x, y'), write(L6), nl)),
    show((atomic_list_concat(L7, '-', 123))),
    show((atomic_list_concat(L8, 1, 'a1b'), write(L8), nl)),
    show((atomic_list_concat(L9, 1, 'a-b'), write(L9), nl)),
    show((atomic_list_concat(_, '', 'a-b'))),
    show((atomic_list_concat(_, _, 'a-b'))),
    show((atomic_list_concat(L10, '-', 'a-b'), write(L10), nl)),
    show((atomic_list_concat(L11, '😀', 'a😀b'), write(L11), nl)),
    show((atomic_list_concat(L12, '-', f(x)))),
    % clause/2
    show((findall(H-B, clause(q(H), B), QL), write(QL), nl)),
    show((findall(B, clause(q(a), B), QL2), write(QL2), nl)),
    show((findall(H-B, clause(d(H, _), B), DL), write(DL), nl)),
    show((findall(B, clause(d(k, B), true), DL2), write(DL2), nl)),
    show((findall(B, clause(d([_], B), true), DL3), write(DL3), nl)),
    show((findall(B, clause(d(f(_), B), true), DL4), write(DL4), nl)),
    show((findall(B, clause(d(1.0, B), true), DL5), write(DL5), nl)),
    show((findall(H-B, clause(p(H), B), PL), write(PL), nl)),
    show((findall(H-B, clause('=>'(H, B), _), PL2), write(PL2), nl)),
    show((findall(X-B, clause(g(X, _), B), GL), write(GL), nl)),
    show((findall(B, clause((g --> B), _), GL2), write(GL2), nl)),
    show(clause(_, _)),
    show(clause(1, _)),
    show(clause(q(_), 1)),
    show(clause(atom(_), _)),
    show(clause(length(_, _), _)),
    show(clause(';'(_, _), _)),
    show(clause(nonexistent(_), _)),
    show(clause(foo:q(_), _)),
    show(clause(user:q(X1), B1)), write(X1-B1), nl,
    true,
    true,
    show((assertz(d(new, z)), clause(d(new, Z), true), write(Z), nl)),
    show((clause(d(K, _), true), K == 2, assertz(d(2, late)), fail ; findall(K2, clause(d(2, K2), true), K2s), write(K2s), nl)),
    show((findall(K3, (clause(d(K3, _), true), (K3 == 1 -> retract(d(2, b)) ; true)), K3s), write(K3s), nl)),
    show((findall(B, clause(cnt(_), B), CL), write(CL), nl)),
    true.

limit3(G) :- findall(G, limit_n(3, G), Gs), member(G, Gs).
limit_n(N, G) :- flag(lim, _, 0), call(G), flag(lim, C, C + 1), ( C + 1 >= N -> ! ; true ).
