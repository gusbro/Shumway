% Continuation functions against the budget layout (ADR-061 stage 5): one
% goal per kind of transfer between functions. Each loop scales with n.
% Run at #wasmprobe=transfers&n=4000000&rounds=4&cps=ab.
%
%probe call+return: cps_count({N}).
%probe nrev: cps_nrev({N}).
%probe tak: cps_tak({N}).
%probe queens: cps_queens({N}).
%probe backtrack: cps_back({N}).

cps_step(N, M) :- M is N - 1.
cps_count(0) :- !.
cps_count(N) :- cps_step(N, M), cps_count(M).

cps_app([], L, L).
cps_app([H|T], L, [H|R]) :- cps_app(T, L, R).
cps_rev([], []).
cps_rev([H|T], R) :- cps_rev(T, RT), cps_app(RT, [H], R).
cps_nrev(N) :- K is N // 500, cps_range(1, 30, L), cps_nrev_loop(K, L).
cps_nrev_loop(0, _) :- !.
cps_nrev_loop(K, L) :- cps_rev(L, _), K1 is K - 1, cps_nrev_loop(K1, L).

cps_tak(X, Y, Z, A) :- X =< Y, !, Z = A.
cps_tak(X, Y, Z, A) :-
    X1 is X - 1, cps_tak(X1, Y, Z, A1),
    Y1 is Y - 1, cps_tak(Y1, Z, X, A2),
    Z1 is Z - 1, cps_tak(Z1, X, Y, A3),
    cps_tak(A1, A2, A3, A).
cps_tak(N) :- K is N // 100000, cps_tak_loop(K).
cps_tak_loop(0) :- !.
cps_tak_loop(K) :- cps_tak(18, 12, 6, _), K1 is K - 1, cps_tak_loop(K1).

cps_sel(X, [X|T], T).
cps_sel(X, [H|T], [H|R]) :- cps_sel(X, T, R).
cps_perm([], []).
cps_perm(L, [H|T]) :- cps_sel(H, L, R), cps_perm(R, T).
cps_safe([]).
cps_safe([Q|Qs]) :- cps_noatt(Q, Qs, 1), cps_safe(Qs).
cps_noatt(_, [], _).
cps_noatt(Q, [Q1|Qs], D) :-
    Q =\= Q1 + D, Q =\= Q1 - D, D1 is D + 1, cps_noatt(Q, Qs, D1).
cps_queens_all(N) :- cps_range(1, N, L), cps_perm(L, Qs), cps_safe(Qs), fail.
cps_queens_all(_).
cps_queens(N) :- K is N // 200000, cps_queens_loop(K).
cps_queens_loop(0) :- !.
cps_queens_loop(K) :- cps_queens_all(7), K1 is K - 1, cps_queens_loop(K1).

cps_mem(X, [X|_]).
cps_mem(X, [_|T]) :- cps_mem(X, T).
cps_back(N) :- M is N // 10, cps_range(1, M, L), cps_back_loop(10, L, M).
cps_back_loop(0, _, _) :- !.
cps_back_loop(K, L, M) :- cps_mem(X, L), X >= M, !, K1 is K - 1, cps_back_loop(K1, L, M).

cps_range(N, N, [N]) :- !.
cps_range(I, N, [I|T]) :- I1 is I + 1, cps_range(I1, N, T).
