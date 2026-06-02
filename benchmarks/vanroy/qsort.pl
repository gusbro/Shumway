% Van Roy benchmark — quicksort of a 50-element list.
% Public domain.
%
% Measures: arithmetic comparison, partitioning, recursive descent
% with two recursive calls per level.

qsort([], R, R).
qsort([X|Tail], R, Cont) :-
    partition(Tail, X, Small, Big),
    qsort(Small, R, [X|R1]),
    qsort(Big, R1, Cont).

partition([], _, [], []).
partition([X|L], Y, [X|L1], L2) :- X =< Y, !, partition(L, Y, L1, L2).
partition([X|L], Y, L1, [X|L2]) :- partition(L, Y, L1, L2).

data([27,74,17,33,94,18,46,83,65,2,32,53,28,85,99,47,
      28,82,6,11,55,29,39,81,90,37,10,0,66,51,7,21,
      85,27,31,63,75,4,95,99,11,28,61,74,18,92,40,53,
      59,8]).

bench :- data(L), qsort(L, _, []).

bench(0) :- !.
bench(N) :- bench, N1 is N - 1, bench(N1).
