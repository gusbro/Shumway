% Goals that wait. A coroutined goal is posted now and runs later, when the
% variable it is watching becomes bound. It is how you say "this must hold"
% before you know enough to check it.
%
% Try:  safe_divide(10, D, R), D = 2.       the division waits for D
%       distinct(X, Y), X = a, Y = b.       dif/2 holds, both ways round
%       distinct(X, Y), X = a, Y = a.       and this one fails
%       positive(N), N = 5.
%       positive(N), N = -5.                the check fires on binding
%       freeze(X, format("X became ~w~n", [X])), X = hello.
%       at_most(2, L), L = [_,_].        a length limit, elements unbound
%       at_most(2, L), L = [a|T], T = [b,c].     one too many
%       dif(A, B).                          an answer that is a constraint

:- use_module(library(coroutining)).

% dif/2 is not "not equal now", it is "never equal". Post it on two
% variables and it survives until they are bound, whichever order that
% happens in. Compare with A \== B, which answers about this instant only.
distinct(X, Y) :-
    dif(X, Y).

% freeze/2 delays a goal on a single variable. Nothing runs until D is bound,
% then the guard runs before the division does, so the error is impossible
% rather than caught.
safe_divide(N, D, R) :-
    freeze(D, D =\= 0),
    freeze(D, R is N / D).

% when/2 waits for a condition rather than a single variable: ground(N),
% nonvar(N), ?=(X, Y) (their equality is decided either way), and the
% conjunctions and disjunctions of those.
positive(N) :-
    when(ground(N), N > 0).

% A length limit that constrains rather than generates. What decides a list's
% length is its spine, so that is what this watches: freeze/2 wakes on each
% cons cell as the list grows, and the limit holds for a partial list as well
% as for one whose elements are never bound.
at_most(N, List) :-
    N >= 0,
    freeze(List, at_most_cell(List, N)).
at_most_cell([], _).
at_most_cell([_|T], N) :-
    N > 0,
    N1 is N - 1,
    at_most(N1, T).

% Producer and consumer in one query. The consumer is posted first and waits;
% the producer fills the list; each element wakes its own goal as it arrives.
% Try:  watch([A,B,C]), A = 1, B = 2, C = 3.
watch([]).
watch([X|Xs]) :-
    freeze(X, format("got ~w~n", [X])),
    watch(Xs).
