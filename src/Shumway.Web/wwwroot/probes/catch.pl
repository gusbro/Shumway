% What catch/3 costs the tier in the shape clpz's with_local_attributes/3
% has: the guarded goal always ends in throw/1, which undoes the changes it
% made and hands the result back through the ball. Each goal does the same
% work; what differs is how it gets there. Run at #wasmprobe=catch&n=20000.
%
%   ideal      the work, the copy and the unify, written out in the body
%   callconj   the same, with the work reached by call/1 of a conjunction
%   catchonly  the same, inside a catch/3 that nothing throws to
%   throwlit   the with_local_attributes shape with the goal written in it
%   wla        the with_local_attributes shape, the goal passed as an argument
%
%probe ideal: lp_ideal({N}).
%probe callconj: lp_callconj({N}).
%probe catchonly: lp_catchonly({N}).
%probe throwlit: lp_throwlit({N}).
%probe wla: lp_wla({N}).

:- meta_predicate(wla(?, 0, ?)).

wla(Vars, Goal, Result) :-
    catch((Goal, copy_term_nat(Vars-Result, Copy), throw(local_attributes(Copy))),
          local_attributes(Vars-Result), true).

pairs([], _, []).
pairs([V|Vs], I, [V-I|Ps]) :- I1 is I+1, pairs(Vs, I1, Ps).

goals([], []).
goals([V-I|Ps], [neq(V, I)|Gs]) :- goals(Ps, Gs).

lp_ideal(0) :- !.
lp_ideal(N) :-
    Vs = [_,_,_,_,_,_,_,_,_],
    pairs(Vs, 1, Ps), goals(Ps, Gs0),
    copy_term_nat(Vs-Gs0, Copy), Copy = Vs-_,
    N1 is N-1, lp_ideal(N1).

lp_callconj(0) :- !.
lp_callconj(N) :-
    Vs = [_,_,_,_,_,_,_,_,_],
    G = (pairs(Vs, 1, Ps), goals(Ps, Gs0)),
    call(G),
    copy_term_nat(Vs-Gs0, Copy), Copy = Vs-_,
    N1 is N-1, lp_callconj(N1).

lp_catchonly(0) :- !.
lp_catchonly(N) :-
    Vs = [_,_,_,_,_,_,_,_,_],
    catch((pairs(Vs, 1, Ps), goals(Ps, Gs0)), _, true),
    copy_term_nat(Vs-Gs0, Copy), Copy = Vs-_,
    N1 is N-1, lp_catchonly(N1).

lp_throwlit(0) :- !.
lp_throwlit(N) :-
    Vs = [_,_,_,_,_,_,_,_,_],
    catch((pairs(Vs, 1, Ps), goals(Ps, Gs0),
           copy_term_nat(Vs-Gs0, Copy), throw(local_attributes(Copy))),
          local_attributes(Vs-_), true),
    N1 is N-1, lp_throwlit(N1).

lp_wla(0) :- !.
lp_wla(N) :-
    Vs = [_,_,_,_,_,_,_,_,_],
    wla(Vs, (pairs(Vs, 1, Ps), goals(Ps, Gs)), Gs),
    N1 is N-1, lp_wla(N1).
