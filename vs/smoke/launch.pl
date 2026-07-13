% ADR-035 D4 -- the launch smoke's program.
%
% Unlike smoke.pl (which loops forever so a Break All has something to catch), this one
% RUNS ON ITS OWN and finishes: the launch command starts shumway.exe on it, the
% :- initialization goal runs as soon as the file is consulted, and the breakpoint in
% tick/2 must be hit without anyone attaching by hand. That is the whole point of the
% F5 path -- the session exists before the first goal does.
%
% It also leaves a TRAIL on disk. A program launched by the IDE has a console nobody is
% reading: without the trail there is no telling "the breakpoint never bound" from "the
% program never ran", and those are different bugs.

:- initialization(main).

main :-
    trail(write, started),
    between(1, 5, N),
    tick(N, Doubled),
    trail(append, tick(N, Doubled)),
    fail.
main :-
    trail(append, done),
    halt.

trail(Mode, Term) :-
    open('C:/claude/Shumway/vs/smoke/launch-trace.txt', Mode, S),
    write(S, Term), nl(S),
    close(S).

tick(N, Doubled) :-
    Doubled is N * 2.
