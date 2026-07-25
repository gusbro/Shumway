% lists_ext — extra list utilities that Shumway does NOT already provide as
% builtins or prelude predicates. An export-qualified module (ADR-038): every
% predicate is mangled lists_ext$name; the exports below are what
% `use_module(library(lists_ext))` imports. Ships in Shumway's default library
% path (the lib/ folder beside the executable).

:- module(lists_ext,
    [take/3, drop/3, split_at/4, zip/3, unzip/3, intersperse/3, flatten/2]).

%% take(+N, +List, -Prefix)
%  Prefix is the first N elements of List (all of List if it is shorter).
take(0, _, []) :- !.
take(_, [], []) :- !.
take(N, [X|Xs], [X|Ys]) :- N > 0, N1 is N - 1, take(N1, Xs, Ys).

%% drop(+N, +List, -Suffix)
%  Suffix is List with its first N elements removed.
drop(0, L, L) :- !.
drop(_, [], []) :- !.
drop(N, [_|Xs], Ys) :- N > 0, N1 is N - 1, drop(N1, Xs, Ys).

%% split_at(+N, +List, -Prefix, -Suffix)
%  Prefix ++ Suffix = List, with Prefix of length min(N, length(List)).
split_at(N, List, Prefix, Suffix) :- take(N, List, Prefix), drop(N, List, Suffix).

%% zip(?As, ?Bs, ?Pairs)
%  Pairs is the list of A-B key-value pairs from the parallel lists As and Bs.
zip([], [], []).
zip([A|As], [B|Bs], [A-B|Ps]) :- zip(As, Bs, Ps).

%% unzip(?Pairs, ?As, ?Bs)
%  The inverse of zip/3: split a list of A-B pairs into its two columns.
unzip([], [], []).
unzip([A-B|Ps], [A|As], [B|Bs]) :- unzip(Ps, As, Bs).

%% intersperse(+Sep, +List, -Out)
%  Out is List with Sep inserted between each pair of adjacent elements.
intersperse(_, [], []) :- !.
intersperse(_, [X], [X]) :- !.
intersperse(S, [X|Xs], [X,S|Ys]) :- intersperse(S, Xs, Ys).

%% flatten(+Nested, -Flat)
%  Flat is the list of leaves of the arbitrarily nested list Nested.
flatten(List, Flat) :- flatten(List, [], Flat0), !, Flat = Flat0.

flatten(Var, Tl, [Var|Tl]) :- var(Var), !.
flatten([], Tl, Tl) :- !.
flatten([Hd|Tl], Tail, List) :- !, flatten(Hd, FlatHeadTail, List), flatten(Tl, Tail, FlatHeadTail).
flatten(NonList, Tl, [NonList|Tl]).
