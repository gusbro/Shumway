% ADR-035 -- the Immediate window smoke. A clause to stand in (with a variable worth
% substituting), a helper worth calling from the Immediate window, and a dynamic
% predicate to prove the database is the live one.

:- dynamic seen/1.

double(A, B) :-
    B is A * 2.

step(N) :-
    helper(N).

helper(_).

run(N) :-
    step(N),
    tick.

tick.

go :-
    run(21).
