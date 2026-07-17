:- public main/0.
main :-
    repeat,
    between(1, 100000, N),
    tick(N, _),
    fail.

tick(N, Doubled) :-
    Doubled is N * 2.
