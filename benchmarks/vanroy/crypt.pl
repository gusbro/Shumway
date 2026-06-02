% Van Roy benchmark — cryptarithmetic.
% Public domain.
%
% Finds digits 0..9 such that the puzzle:
%
%        O N E
%  +     O N E
%  ---------
%      T W O
%
% (extended to handle full carry chains via generate-and-test) holds.
% Smaller and faster than sendmore, used by the GNU bench suite.

crypt([O, N, E, T, W]) :-
    digit(O), O > 0,
    digit(N), N \== O,
    digit(E), E \== O, E \== N,
    digit(T), T > 0, T \== O, T \== N, T \== E,
    digit(W), W \== O, W \== N, W \== E, W \== T,
    100*O + 10*N + E
  + 100*O + 10*N + E
  =:= 100*T + 10*W + O.

digit(0). digit(1). digit(2). digit(3). digit(4).
digit(5). digit(6). digit(7). digit(8). digit(9).

bench :- crypt(_), !.

bench(0) :- !.
bench(N) :- bench, N1 is N - 1, bench(N1).
