:- use_module(library(clpfd)).

main :-
    loop(0).

loop(N) :-
    X in 1..9,
    X #< Y,
    Y in 3..7,
    mark(X, Y, N),
    N1 is N + 1,
    loop(N1).

mark(_, _, _).
