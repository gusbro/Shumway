% ADR-035 -- a program that RUNS for a while, so Break All has something to interrupt.
% The user's case is Blint: a real program, a goal that takes seconds. This is the same
% shape in four lines -- a long computation that passes ports the whole way.

spin(0) :- !.
spin(N) :-
    N1 is N - 1,
    spin(N1).

work(0) :- !.
work(N) :-
    spin(2000),
    N1 is N - 1,
    work(N1).

go :-
    work(20000).

% A STACK WITH REAL DATA ON IT -- the shape that broke. Blint pauses 200+ frames deep with
% variables holding the file it is reading, and the whole stop has to cross a fixed-size
% buffer. It did not fit; the snapshot was truncated but claimed the full count; the
% debugger walked the missing frames through the tail of an older stop, read rubbish as a
% variable count, and died of it inside the stop handler -- so the pause was never
% completed and Visual Studio waited for it for ever, while the program ran happily on.
big(0, []) :- !.
big(N, [N|T]) :-
    N1 is N - 1,
    big(N1, T).

deep(0, _) :- !.
deep(N, Data) :-
    spin(1000),
    N1 is N - 1,
    deep(N1, Data).

heavy :-
    big(500, Data),
    deep(2000, Data).
