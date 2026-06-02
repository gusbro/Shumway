% Classic cryptarithmetic: SEND + MORE = MONEY.
% Pure backtracking, no CLP — Van Roy / standard form.
% Public domain.
%
% Measures: deep generate-and-test backtracking + arithmetic.

sendmore([S,E,N,D,M,O,R,Y]) :-
    digit(M), M > 0,
    digit(S), S > 0, S \== M,
    digit(O), O \== M, O \== S,
    digit(E), E \== M, E \== S, E \== O,
    digit(N), N \== M, N \== S, N \== O, N \== E,
    digit(R), R \== M, R \== S, R \== O, R \== E, R \== N,
    digit(D), D \== M, D \== S, D \== O, D \== E, D \== N, D \== R,
    digit(Y), Y \== M, Y \== S, Y \== O, Y \== E, Y \== N, Y \== R, Y \== D,
                1000*S + 100*E + 10*N + D
              + 1000*M + 100*O + 10*R + E
       =:= 10000*M + 1000*O + 100*N + 10*E + Y.

digit(0). digit(1). digit(2). digit(3). digit(4).
digit(5). digit(6). digit(7). digit(8). digit(9).

bench :- sendmore(_), !.

bench(0) :- !.
bench(N) :- bench, N1 is N - 1, bench(N1).

% Cross-engine correctness check.
report :- sendmore(L), write(L), nl.
