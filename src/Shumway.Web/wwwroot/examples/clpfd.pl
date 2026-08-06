% Constraints over finite domains: state the relationships, let the solver
% narrow the possibilities.
%
% The library is opt-in, and the program says so itself — the directive below
% runs as this file is consulted, which is what puts `ins`, `#=` and the rest
% in the operator table before the clauses that use them are read.
%
% Try:  puzzle(Digits).           SEND + MORE = MONEY
%       queens_fd(8, Qs), label(Qs).
%       X #> 3, X #< 7.           an answer that is still a constraint

:- use_module(library(clpfd)).

puzzle([S,E,N,D,M,O,R,Y]) :-
    Vars = [S,E,N,D,M,O,R,Y],
    Vars ins 0..9,
    all_different(Vars),
    S #\= 0, M #\= 0,
                 1000*S + 100*E + 10*N + D
    +            1000*M + 100*O + 10*R + E
    #= 10000*M + 1000*O + 100*N + 10*E + Y,
    label(Vars).

% N queens again — this time the diagonals are constraints, not a test.
queens_fd(N, Qs) :-
    length(Qs, N),
    Qs ins 1..N,
    all_different(Qs),
    diagonals(Qs).

diagonals([]).
diagonals([Q|Qs]) :- no_diag(Q, Qs, 1), diagonals(Qs).

no_diag(_, [], _).
no_diag(Q, [R|Rs], D) :-
    Q #\= R + D,
    Q #\= R - D,
    D1 is D + 1,
    no_diag(Q, Rs, D1).
