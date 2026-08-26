% N queens by generate-and-test over permutations.
% Try:  queens(8, Qs).           then ;  to see other solutions
%
% Qs is a list of column positions, one per row: the Ith element is the
% column of the queen in row I. A permutation already rules out two queens
% sharing a row or a column, so only the diagonals are left to check.

queens(N, Qs) :-
    numlist(1, N, Ns),
    permutation(Ns, Qs),
    safe(Qs).

safe([]).
safe([Q|Qs]) :-
    no_attack(Q, Qs, 1),
    safe(Qs).

no_attack(_, [], _).
no_attack(Q, [R|Rs], D) :-
    Q =\= R + D,
    Q =\= R - D,
    D1 is D + 1,
    no_attack(Q, Rs, D1).
