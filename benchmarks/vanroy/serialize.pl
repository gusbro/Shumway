% Warren's serialize benchmark — assigns sequence numbers to each
% distinct list element such that equal elements get the same number,
% and the numbering follows in-order traversal of a BST keyed on the
% value. Public domain (cleaned-up form of Warren's original Aquarius
% test).
%
% Measures: term construction, tree traversal, conditional unification.

serialize(List, Numbers) :-
    pair_lists(List, Numbers, Pairs),
    build_tree(Pairs, void, Tree),
    number_tree(Tree, 1, _).

pair_lists([], [], []).
pair_lists([X|Xs], [Y|Ys], [X-Y|Ps]) :- pair_lists(Xs, Ys, Ps).

build_tree([], T, T).
build_tree([K-V|Rest], T0, T) :- insert_(T0, K, V, T1), build_tree(Rest, T1, T).

insert_(void, K, V, tree(K, V, void, void)).
insert_(tree(K, V0, L, R), K, V, tree(K, V0, L, R)) :- !, V = V0.
insert_(tree(K0, V0, L, R), K, V, tree(K0, V0, L1, R)) :-
    K @< K0, !, insert_(L, K, V, L1).
insert_(tree(K0, V0, L, R), K, V, tree(K0, V0, L, R1)) :-
    insert_(R, K, V, R1).

number_tree(void, N, N).
number_tree(tree(_, V, L, R), N0, N) :-
    number_tree(L, N0, N1),
    V = N1,
    N2 is N1 + 1,
    number_tree(R, N2, N).

% "ABLE WAS I ERE I SAW ELBA" as explicit codes (avoids the
% double_quotes flag differences across engines).
data([65,66,76,69,32,87,65,83,32,73,32,69,82,69,32,
      73,32,83,65,87,32,69,76,66,65]).

bench :- data(S), serialize(S, _).

bench(0) :- !.
bench(N) :- bench, N1 is N - 1, bench(N1).

% Cross-engine correctness check.
report :- data(S), serialize(S, R), write(R), nl.
