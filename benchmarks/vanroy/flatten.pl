% Van Roy benchmark — flatten a nested list of lists.
% Public domain.
%
% Measures: recursive descent + append.

my_flatten([], []) :- !.
my_flatten([H|T], R) :- !,
    my_flatten(H, FH),
    my_flatten(T, FT),
    conc(FH, FT, R).
my_flatten(X, [X]).

conc([], L, L).
conc([H|T], L, [H|R]) :- conc(T, L, R).

data([a, [b, [c, [d, [e, [f, [g, [h, [i, [j]]]]]]]]],
      [[k, [l, [m, [n, [o, [p, [q, [r, [s, [t]]]]]]]]]],
       [u, v, w, [x, [y, [z]]]]]]).

bench :- data(L), my_flatten(L, _).

bench(0) :- !.
bench(N) :- bench, N1 is N - 1, bench(N1).

% Cross-engine correctness check.
report :- data(L), my_flatten(L, R), write(R), nl.
