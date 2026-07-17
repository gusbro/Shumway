:- public main/0.
main :-
    repeat,
    between(1, 100000, N),
    tick(N, _),
    fail.

tick(N, Doubled) :-
    Doubled is N * 2,
    ( 0 =:= N mod 20000 -> report(N) ; true ).

report(N) :- write(reached(N)), nl.
