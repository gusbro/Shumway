% ADR-035 D4 E2E -- arity-compat module with interop.
%
% The point of the whole debugger, in one file:
%
%   * a MODULE with LOCAL predicates (mangled internally to module$name -- a breakpoint has
%     to bind in one, and the call stack has to show the name the user wrote, not the mangled
%     one);
%   * a call out to a C# FOREIGN predicate (scale/2), which itself P/Invokes into C.
%
% Stopping in step/2 must show a Prolog stack of the user's own predicates; stopping inside
% the C# must show that stack UNDERNEATH the C# frames -- one mixed stack, which is the thing
% no amount of unit testing can prove.

:- module(interop).

% The entry point has to be reachable from outside the module: an initialization goal is run
% in the global namespace, and a local main is not a name it can see. Everything BELOW stays
% local -- which is the part the debugger has to get right (local predicates are mangled to
% module$name internally, and the call stack must show what the user wrote).
:- public main/0.

:- initialization(main).

main :-
    run(4, Result),
    trail(Result),
    halt.

% Local (not :- public): reached only from within this module, and mangled as such.
run(N, Result) :-
    step(N, Doubled),
    step(Doubled, Result).

step(N, Scaled) :-
    scale(N, Scaled).

trail(Result) :-
    open('C:/claude/Shumway/vs/smoke/interop/interop-trace.txt', write, S),
    write(S, result(Result)), nl(S),
    close(S).
