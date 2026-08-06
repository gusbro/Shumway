% The zebra puzzle: five houses, and constraints enough to pin every fact.
% Try:  houses(Hs), member(house(_,_,_,_,zebra), Hs).
%
% Each house is house(Colour, Nation, Pet, Drink, Smoke)... here reduced to
% the classic five-attribute form. Plain Prolog, no constraint library: the
% search is the point.

right_of(X, Y, [Y,X|_]).
right_of(X, Y, [_|T]) :- right_of(X, Y, T).

next_to(X, Y, L) :- right_of(X, Y, L).
next_to(X, Y, L) :- right_of(Y, X, L).

houses(Hs) :-
    Hs = [house(_,norwegian,_,_,_), _, house(_,_,_,milk,_), _, _],
    member(house(red,english,_,_,_), Hs),
    right_of(house(green,_,_,_,_), house(ivory,_,_,_,_), Hs),
    next_to(house(_,norwegian,_,_,_), house(blue,_,_,_,_), Hs),
    member(house(_,spanish,dog,_,_), Hs),
    member(house(green,_,_,coffee,_), Hs),
    member(house(_,ukrainian,_,tea,_), Hs),
    member(house(_,_,snails,_,oldgold), Hs),
    member(house(yellow,_,_,_,kools), Hs),
    next_to(house(_,_,_,_,chesterfield), house(_,_,fox,_,_), Hs),
    next_to(house(_,_,_,_,kools), house(_,_,horse,_,_), Hs),
    member(house(_,_,_,juice,luckystrike), Hs),
    member(house(_,japanese,_,_,parliament), Hs),
    member(house(_,_,zebra,_,_), Hs),
    member(house(_,_,_,water,_), Hs).
