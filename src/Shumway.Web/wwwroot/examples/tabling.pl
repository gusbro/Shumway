% Tabling memoises a predicate and evaluates it to a fixpoint, so a definition
% that is natural but left-recursive terminates instead of looping.
% Try:  path(a, X).       then ;  for the rest
%       fib(30, F).       instant, where plain recursion would not be

:- table path/2.

edge(a, b).
edge(b, c).
edge(c, a).      % a cycle — plain SLD resolution would spin here
edge(c, d).

path(X, Y) :-
    edge(X, Y).
path(X, Y) :-
    path(X, Z),        % left-recursive, on purpose
    edge(Z, Y).

:- table fib/2.

fib(0, 0).
fib(1, 1).
fib(N, F) :-
    N > 1,
    A is N - 1,
    B is N - 2,
    fib(A, FA),
    fib(B, FB),
    F is FA + FB.
