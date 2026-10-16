% Constraints over finite domains: state the relationships, let the solver
% narrow the possibilities.
%
% The library is opt-in, and the program says so itself — the directive below
% runs as this file is consulted, which is what puts `ins`, `#=` and the rest
% in the operator table before the clauses that use them are read.
%
% Try:  puzzle(Digits).           SEND + MORE = MONEY
%       queens_fd(8, Qs).
%       X #> 3, X #< 7.           an answer that is still a constraint
%       @A #= @B, A = [1,2,3].    the length of a list is a constraint too
%       equal_parts([a,b,c,d,e,f], N, Parts).
%       nonogram_row(10, [3,2], Row).

:- use_module(library(clpfd)).

puzzle([S,E,N,D,M,O,R,Y]) :-
    Vars = [S,E,N,D,M,O,R,Y],
    Vars ins 0..9,
    all_different(Vars),
    S #\= 0,
    M #\= 0,
                 1000*S + 100*E + 10*N + D
    +            1000*M + 100*O + 10*R + E
    #= 10000*M + 1000*O + 100*N + 10*E + Y,
    label(Vars).

% N queens again — this time the diagonals are constraints, not a test.
% The labeling at the end turns the narrowed domains into concrete answers;
% drop it to see the residual constraints instead.
queens_fd(N, Qs) :-
    length(Qs, N),
    Qs ins 1..N,
    all_different(Qs),
    diagonals(Qs),
    label(Qs).

diagonals([]).
diagonals([Q|Qs]) :-
    no_diag(Q, Qs, 1),
    diagonals(Qs).

no_diag(_, [], _).
no_diag(Q, [R|Rs], D) :-
    Q #\= R + D,
    Q #\= R - D,
    D1 is D + 1,
    no_diag(Q, Rs, D1).

% The length of a list is an integer the solver knows about: @L. A list that
% gets more elements raises the lower bound of its length; a length that is
% fixed closes its list. `@A #= @B` keeps two lists the same length, whichever
% is known first.

% Splits a list into K parts of the same length. Arithmetic alone decides the
% length of each part (K times it is the length of the list), the parts take
% that shape, and append/2 only fills them in. With K unbound, labeling it
% enumerates the ways to split the list.
%
%   equal_parts([a,b,c,d,e,f], 3, Parts).   Parts = [[a,b],[c,d],[e,f]]
%   equal_parts([a,b,c,d,e,f], N, Parts).   N = 1, 2, 3 and 6
%   equal_parts([a,b,c,d,e], 2, Parts).     false
equal_parts(List, K, Parts) :-
    @List #= K * Len,
    K #>= 1, Len #>= 1,
    label([K]),
    length(Parts, K),
    maplist(part_length(Len), Parts),
    append(Parts, List).

part_length(Len, Part) :- @Part #= Len.

% One row of a nonogram ("paint by numbers"): N cells, with blocks of filled
% cells of the given lengths, in that order, and at least one empty cell
% between two blocks. The row is a gap, a block, a gap, ..., a block, a gap,
% and the outer gaps may be empty. Only the lengths are constrained; the
% segments are built after the solver has chosen the gaps.
%
%   nonogram_row(5, [2], Row).       "##...", ".##..", "..##.", "...##"
%   nonogram_row(5, [2,2], Row).     "##.##"
%   nonogram_row(10, [3,2], Row).    15 placements
nonogram_row(N, Blocks, Row) :-
    layout(Blocks, 0, Segments, Gaps),
    sum_list(Blocks, Filled),
    sum(Gaps, #=, N - Filled),
    label(Gaps),
    maplist(fill, Segments),
    maplist(arg(1), Segments, Parts),
    append(Parts, Row).

% Min is the least length of the gap before the next block: 0 for the first
% gap, 1 after a block. The gap after the last block may be empty.
layout([], _, [gap(G)], [Len]) :-
    @G #= Len, Len #>= 0.
layout([B|Bs], Min, [gap(G), block(Bl)|Segments], [Len|Gaps]) :-
    @G #= Len, Len #>= Min,
    @Bl #= B,
    layout(Bs, 1, Segments, Gaps).

fill(gap(G)) :- maplist(=('.'), G).
fill(block(B)) :- maplist(=('#'), B).
