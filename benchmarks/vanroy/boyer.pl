% Boyer-Moore-style term rewriter (small subset of the Van Roy
% benchmark). Public domain.
%
% Measures: term rewriting via =../2 + recursive descent + a
% rewrite-rule database. The full Boyer suite includes 100+ axioms;
% this trimmed version exercises the same dispatch shape with a
% smaller rule set so it runs in the seconds-per-1000 iters range
% across all three engines.

bench :- test_formula(F), rewrite(F, R), R == true.

% A formula that, under our rewrite rules, normalises to `true`.
%   and(true, not(false))            -> not(false) -> true
%   or(not(false), false)            -> not(false) -> true
%   implies(true, true)              -> true
test_formula(implies(and(true, not(false)), or(not(false), false))).

rewrite(A, A) :- atomic(A), !.
rewrite(Term, Result) :-
    Term =.. [F | Args],
    rewrite_args(Args, NArgs),
    NewTerm =.. [F | NArgs],
    ( axiom(NewTerm, Next) -> rewrite(Next, Result) ; Result = NewTerm ).

rewrite_args([], []).
rewrite_args([H|T], [HR|TR]) :- rewrite(H, HR), rewrite_args(T, TR).

% --- the axiom database ---
axiom(and(true,  X),    X).
axiom(and(X,     true), X).
axiom(and(false, _),    false).
axiom(and(_,     false),false).
axiom(or(true,   _),    true).
axiom(or(_,      true), true).
axiom(or(false,  X),    X).
axiom(or(X,      false),X).
axiom(not(true),        false).
axiom(not(false),       true).
axiom(not(not(X)),      X).
axiom(implies(true,  X), X).
axiom(implies(false, _), true).
axiom(implies(_, true),  true).
axiom(implies(X, false), not(X)).
axiom(implies(X, X),     true).

bench(0) :- !.
bench(N) :- bench, N1 is N - 1, bench(N1).

% Cross-engine correctness check.
report :- test_formula(F), rewrite(F, R), write(R), nl.
