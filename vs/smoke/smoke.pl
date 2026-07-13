% ADR-035 smoke program.
%
% A failure-driven loop that never ends, so the debugger always has something to catch
% in the act -- and whose stack stays shallow while it runs. That second property is not
% incidental: a debug session turns last-call optimisation OFF (a frame the machine has
% reclaimed is a frame nobody can show), so an ordinary tail-recursive loop would grow
% one frame per iteration and the debugger would be asked to render a hundred thousand
% of them. repeat/between/fail builds no environment chain at all.
%
% The breakpoint the smoke script sets is the LAST line of this file: the body of
% tick/2, where N is bound and Doubled is not -- so the Locals window has something to
% be right or wrong about.

main :-
    repeat,
    between(1, 100000, N),
    tick(N, _),
    fail.

tick(N, Doubled) :-
    Doubled is N * 2.
