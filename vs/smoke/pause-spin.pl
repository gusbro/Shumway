% ADR-035 -- a program that RUNS for a while, so Break All has something to interrupt.
% The user's case is Blint: a real program, a goal that takes seconds. This is the same
% shape in four lines -- a long computation that passes ports the whole way.

spin(0) :- !.
spin(N) :-
    N1 is N - 1,
    spin(N1).

work(0) :- !.
work(N) :-
    spin(2000),
    N1 is N - 1,
    work(N1).

go :-
    work(20000).
