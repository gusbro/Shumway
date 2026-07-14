% ADR-035 -- a file the engine consults from its TOP LEVEL, after a debugger has already
% attached. Which files a program is made of is not settled when it starts, and the debugger
% has to learn this one from the engine (it cannot be on the command line: that is the point).

later_spin(0) :- !.
later_spin(N) :-
    N1 is N - 1,
    later_spin(N1).

later_work(0) :- !.
later_work(N) :-
    later_spin(2000),
    N1 is N - 1,
    later_work(N1).

later_go :-
    later_work(20000).
