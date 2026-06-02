% Van Roy benchmark — naive reverse of a 30-element list.
% Source: Peter Van Roy's PhD dissertation (UC Berkeley, 1990) /
% Aquarius Prolog distribution. Public domain.
%
% Measures: unification + recursive list traversal. O(n^2) via append.

nrev([], []).
nrev([H|T], R) :- nrev(T, RT), conc(RT, [H], R).

conc([], L, L).
conc([H|T], L, [H|R]) :- conc(T, L, R).

data([1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,
      16,17,18,19,20,21,22,23,24,25,26,27,28,29,30]).

bench :- data(L), nrev(L, _).

bench(0) :- !.
bench(N) :- bench, N1 is N - 1, bench(N1).
