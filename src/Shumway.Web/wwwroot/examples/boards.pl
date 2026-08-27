% Two puzzles that live on a board, and a way to look at the answer. A list
% of numbers is the right shape for a solver and the wrong shape for a person,
% so each one comes with a predicate that draws it.
%
% Try:  queens(8, Qs).                  then ;  to see other solutions
%       queens_show(8, Qs).             the same answer, drawn
%       queens_show(6, _).
%       puzzle_show(easy).              the givens, before solving
%       sudoku_show(easy).
%       sudoku_show(hard).
%       puzzle(easy, Rows), sudoku(Rows), label_all(Rows).

:- use_module(library(clpfd)).

% ===== N queens, by generate and test =====
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

% Solve, then draw. Backtracking into it draws the next solution.
queens_show(N, Qs) :-
    queens(N, Qs),
    queens_board(N, Qs).

queens_board(N, Qs) :-
    edge(N, '┌', '┬', '┐'),
    queens_rows(Qs, N),
    edge(N, '└', '┴', '┘').

queens_rows([], _).
queens_rows([Q|Qs], N) :-
    write('│'),
    queens_cells(1, N, Q),
    nl,
    ( Qs == [] -> true ; edge(N, '├', '┼', '┤') ),
    queens_rows(Qs, N).

queens_cells(C, N, _) :-
    C > N,
    !.
queens_cells(C, N, Q) :-
    ( C =:= Q -> write(' ♛ ') ; write('   ') ),
    write('│'),
    C1 is C + 1,
    queens_cells(C1, N, Q).

% One horizontal rule: the corners and junctions differ, the cells do not.
edge(N, Left, Join, Right) :-
    write(Left),
    edge_cells(N, Join),
    write(Right),
    nl.

edge_cells(1, _) :-
    !,
    write('───').
edge_cells(N, Join) :-
    write('───'),
    write(Join),
    N1 is N - 1,
    edge_cells(N1, Join).

% ===== Sudoku, as constraints =====
% Nothing here searches. Every rule is stated once as "these nine are all
% different", and the solver narrows the domains; label_all/1 only turns
% whatever is left into concrete answers.

sudoku(Rows) :-
    length(Rows, 9),
    square(Rows),
    cells(Rows, Cells),
    Cells ins 1..9,
    all_distinct_each(Rows),
    transpose(Rows, Columns),
    all_distinct_each(Columns),
    boxes(Rows).

% A board is square when every row is as long as the list of rows.
square([]).
square(Rows) :-
    rows_square(Rows, Rows).

rows_square([], _).
rows_square([Row|Rows], Board) :-
    same_length(Row, Board),
    rows_square(Rows, Board).

% Two lists of the same length, whatever is in them. It builds whichever
% side is unbound, so it works as a check and as a generator.
same_length([], []).
same_length([_|As], [_|Bs]) :-
    same_length(As, Bs).

% Columns as rows. Peel one element off every row at once: the heads you
% peeled ARE a column, and the tails are the rest of the board.
transpose([], []).
transpose([[]|_], []) :-
    !.
transpose(Rows, [Column|Columns]) :-
    heads_and_tails(Rows, Column, Tails),
    transpose(Tails, Columns).

heads_and_tails([], [], []).
heads_and_tails([[H|T]|Rows], [H|Heads], [T|Tails]) :-
    heads_and_tails(Rows, Heads, Tails).

cells([], []).
cells([Row|Rows], Cells) :-
    cells(Rows, Rest),
    append(Row, Rest, Cells).

all_distinct_each([]).
all_distinct_each([Group|Groups]) :-
    all_distinct(Group),
    all_distinct_each(Groups).

% The nine 3x3 boxes: three rows at a time, three columns at a time. This
% peels the variables off the rows rather than collecting them, because
% findall/3 would hand all_distinct/1 copies and constrain those instead.
boxes([]).
boxes([A, B, C|Rows]) :-
    box_row(A, B, C),
    boxes(Rows).

box_row([], [], []).
box_row([A1,A2,A3|As], [B1,B2,B3|Bs], [C1,C2,C3|Cs]) :-
    all_distinct([A1,A2,A3,B1,B2,B3,C1,C2,C3]),
    box_row(As, Bs, Cs).

label_all([]).
label_all([Row|Rows]) :-
    label(Row),
    label_all(Rows).

sudoku_show(Name) :-
    puzzle(Name, Rows),
    sudoku(Rows),
    label_all(Rows),
    sudoku_board(Rows).

% The givens on their own. The same drawing serves both: a cell that is
% still a variable prints as a dot, which is what a blank is.
puzzle_show(Name) :-
    puzzle(Name, Rows),
    sudoku_board(Rows).

sudoku_board(Rows) :-
    box_edge('┌', '┬', '┐'),
    sudoku_rows(Rows, 1),
    box_edge('└', '┴', '┘').

sudoku_rows([], _).
sudoku_rows([Row|Rows], N) :-
    write('│'),
    sudoku_cells(Row, 1),
    nl,
    ( 0 is N mod 3, Rows \== [] -> box_edge('├', '┼', '┤') ; true ),
    N1 is N + 1,
    sudoku_rows(Rows, N1).

sudoku_cells([], _).
sudoku_cells([V|Vs], C) :-
    write(' '),
    ( integer(V) -> write(V) ; write('·') ),
    ( 0 is C mod 3 -> write(' │') ; true ),
    C1 is C + 1,
    sudoku_cells(Vs, C1).

box_edge(Left, Join, Right) :-
    write(Left),
    write('───────'),
    write(Join),
    write('───────'),
    write(Join),
    write('───────'),
    write(Right),
    nl.

% Blanks are unbound variables, which is what the solver wants anyway.
puzzle(easy,
    [[5,3,_, _,7,_, _,_,_],
     [6,_,_, 1,9,5, _,_,_],
     [_,9,8, _,_,_, _,6,_],

     [8,_,_, _,6,_, _,_,3],
     [4,_,_, 8,_,3, _,_,1],
     [7,_,_, _,2,_, _,_,6],

     [_,6,_, _,_,_, 2,8,_],
     [_,_,_, 4,1,9, _,_,5],
     [_,_,_, _,8,_, _,7,9]]).

% Seventeen givens, the fewest a sudoku can have and still be a puzzle.
% Propagation alone does not finish this one; the labeling earns its keep.
puzzle(hard,
    [[_,_,_, _,_,_, _,1,_],
     [4,_,_, _,_,_, _,_,_],
     [_,2,_, _,_,_, _,_,_],

     [_,_,_, _,5,_, 4,_,7],
     [_,_,8, _,_,_, 3,_,_],
     [_,_,1, _,9,_, _,_,_],

     [3,_,_, 4,_,_, 2,_,_],
     [_,5,_, 1,_,_, _,_,_],
     [_,_,_, 8,_,6, _,_,_]]).
