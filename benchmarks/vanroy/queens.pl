% Van Roy benchmark — N-queens (default 8). Finds the first solution.
% Public domain.
%
% Measures: deep backtracking, deep arithmetic, list permutation.

queens(N, Qs) :-
    gen(1, N, Ns),
    queens_(Ns, [], Qs).

queens_([], Qs, Qs).
queens_(Unplaced, Placed, Qs) :-
    select_(Q, Unplaced, Rest),
    safe(Q, Placed, 1),
    queens_(Rest, [Q|Placed], Qs).

safe(_, [], _).
safe(Q, [Q0|Qs], D) :-
    Q =\= Q0 + D, Q =\= Q0 - D,
    D1 is D + 1,
    safe(Q, Qs, D1).

gen(N, N, [N]) :- !.
gen(I, N, [I|T]) :- I < N, I1 is I + 1, gen(I1, N, T).

select_(X, [X|Xs], Xs).
select_(X, [Y|Ys], [Y|Zs]) :- select_(X, Ys, Zs).

bench :- queens(8, _), !.

bench(0) :- !.
bench(N) :- bench, N1 is N - 1, bench(N1).
