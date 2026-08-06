% Relations and recursion — the shape most Prolog starts with.
% Try:  ancestor(ana, Who).      then press ;  for the next answer
%       ancestor(Who, tomas).

parent(ana,   beto).
parent(ana,   clara).
parent(beto,  diego).
parent(clara, elena).
parent(diego, tomas).

ancestor(X, Y) :- parent(X, Y).
ancestor(X, Z) :- parent(X, Y), ancestor(Y, Z).

% Everyone reachable from X, collected in one go.
descendants(X, L) :- findall(Y, ancestor(X, Y), L).
