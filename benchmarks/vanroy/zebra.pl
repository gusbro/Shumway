% Zebra puzzle — classic Van Roy / generate-and-test benchmark.
% Public domain.
%
% Measures: deep backtracking with constraint-like propagation.
% Who owns the zebra? Who drinks water?

zebra(Houses, Zebra, Water) :-
    Houses = [house(_, _, _, _, _), house(_, _, _, _, _),
              house(_, _, _, _, _), house(_, _, _, _, _),
              house(_, _, _, _, _)],
    member_(house(red, english, _, _, _), Houses),
    member_(house(_, spanish, dog, _, _), Houses),
    member_(house(green, _, _, coffee, _), Houses),
    member_(house(_, ukrainian, _, tea, _), Houses),
    right_of(house(green, _, _, _, _), house(ivory, _, _, _, _), Houses),
    member_(house(_, _, snails, _, winston), Houses),
    member_(house(yellow, _, _, _, kools), Houses),
    middle(house(_, _, _, milk, _), Houses),
    first(house(_, norwegian, _, _, _), Houses),
    next_to(house(_, _, _, _, chesterfield), house(_, _, fox, _, _), Houses),
    next_to(house(_, _, _, _, kools), house(_, _, horse, _, _), Houses),
    member_(house(_, _, _, orange_juice, lucky_strike), Houses),
    member_(house(_, japanese, _, _, parliaments), Houses),
    next_to(house(_, norwegian, _, _, _), house(blue, _, _, _, _), Houses),
    member_(house(_, Zebra, zebra, _, _), Houses),
    member_(house(_, Water, _, water, _), Houses).

member_(X, [X|_]).
member_(X, [_|T]) :- member_(X, T).

right_of(A, B, [B, A | _]).
right_of(A, B, [_|T]) :- right_of(A, B, T).

next_to(A, B, [A, B | _]).
next_to(A, B, [B, A | _]).
next_to(A, B, [_|T]) :- next_to(A, B, T).

first(X, [X|_]).
middle(X, [_, _, X, _, _]).

bench :- zebra(_, _, _), !.

bench(0) :- !.
bench(N) :- bench, N1 is N - 1, bench(N1).

% Cross-engine correctness check.
report :- zebra(_, Z, W), write(zebra-Z), nl, write(water-W), nl.
