% The engine sits at the prompt with this loaded, and a debugger attaches to it. That is
% the ordinary way to debug a program you did not launch from the IDE -- and it was broken:
% nothing had stopped, so no module existed for a breakpoint to bind against, and every
% breakpoint in the file was dark ("no symbols have been loaded for this document").
%
% Each goal leaves a mark in a file, so the smoke can tell a program that did not stop from
% one that never ran at all -- two failures that look identical from the IDE.

go :-
    step(1, A),
    step(A, B),
    result(B).

step(N, Out) :-
    Out is N * 2.

result(R) :-
    trace_line(answer(R)).

% The other way in, which needs nothing to have gone right first: the program asks the
% debugger to stop. With no debugger attached it does nothing.
marked :-
    X = 41,
    debugger_break,
    Y is X + 1,
    trace_line(marked(Y)).

trace_line(Term) :-
    open('C:/claude/Shumway/vs/smoke/attach-idle-trace.txt', append, S),
    write(S, Term), nl(S),
    close(S).
