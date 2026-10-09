% Reified conditions: a condition answers with a truth value, true or
% false, instead of succeeding or failing, so a choice between two
% branches is made without a choice point when the arguments already
% decide it, and with both branches, each constrained, when they do not.
% Opt-in: PrologEngine.UseReif() or use_module(library(reif)) loads the
% bundle baked from this file at build time.
%
% A reified condition is a closure called with one more argument, T. It
% binds T to true or false; when its arguments do not decide it, it
% answers true first and false second, each with the constraint that
% makes it so (X = Y, or dif(X, Y)). A T that is already bound decides
% which one, without a choice point.

:- module(reif).

:- use_module(library(coroutining)).

:- public if_/3.
:- public (=)/3.
:- public dif/3.
:- public (',')/3.
:- public (;)/3.
:- public cond_t/3.
:- public memberd_t/3.
:- public tmember/2.
:- public tmember_t/3.
:- public tfilter/3.
:- public tpartition/4.

:- meta_predicate(if_(1, 0, 0)).
:- meta_predicate(','(1, 1, *)).
:- meta_predicate(;(1, 1, *)).
:- meta_predicate(cond_t(1, 0, *)).
:- meta_predicate(tmember(2, *)).
:- meta_predicate(tmember_t(2, *, *)).
:- meta_predicate(tfilter(2, *, *)).
:- meta_predicate(tpartition(2, *, *, *)).

%! if_(:If_1, :Then_0, :Else_0) | Reified conditions | Calls If_1 with one more argument T, then Then_0 when T is true and Else_0 when it is false. A T left unbound is an instantiation error, anything but true or false a type error.
if_(If_1, Then_0, Else_0) :-
    call(If_1, T),
    reif_branch(T, Then_0, Else_0).

reif_branch(T, Then_0, Else_0) :-
    (   T == true -> call(Then_0)
    ;   T == false -> call(Else_0)
    ;   var(T) -> throw(error(instantiation_error, if_/3))
    ;   throw(error(type_error(boolean, T), if_/3))
    ).

%! =(?X, ?Y, ?T) | Reified conditions | T is true when X and Y are equal and false when they are different; when they are neither yet, true with X = Y, then false with dif(X, Y).
=(X, Y, T) :-
    (   X == Y -> T = true
    ;   \+ X = Y -> T = false
    ;   T == true -> X = Y
    ;   T == false -> dif(X, Y)
    ;   var(T) -> ( T = true, X = Y ; T = false, dif(X, Y) )
    ).

%! dif(?X, ?Y, ?T) | Reified conditions | T is true when X and Y are different and false when they are equal; when they are neither yet, false with X = Y, then true with dif(X, Y).
dif(X, Y, T) :-
    (   X == Y -> T = false
    ;   \+ X = Y -> T = true
    ;   T == false -> X = Y
    ;   T == true -> dif(X, Y)
    ;   var(T) -> ( T = false, X = Y ; T = true, dif(X, Y) )
    ).

%! ','(:A_1, :B_1, ?T) | Reified conditions | T is true when both reified conditions are true.
','(A_1, B_1, T) :-
    if_(A_1, call(B_1, T), T = false).

%! ;(:A_1, :B_1, ?T) | Reified conditions | T is true when either reified condition is true.
;(A_1, B_1, T) :-
    if_(A_1, T = true, call(B_1, T)).

%! cond_t(:If_1, :Then_0, ?T) | Reified conditions | T is true, and Then_0 has run, when If_1 is true; false when it is false.
cond_t(If_1, Then_0, T) :-
    if_(If_1, ( call(Then_0), T = true ), T = false).

%! memberd_t(?X, ?Xs, ?T) | Reified conditions | T is true when X is an element of the list Xs, false when it is none; each element is tried once, as =/3.
memberd_t(X, Xs, T) :-
    reif_memberd(Xs, X, T).

reif_memberd([], _, false).
reif_memberd([E|Es], X, T) :-
    if_(=(E, X), T = true, reif_memberd(Es, X, T)).

%! tmember(:C_2, ?Xs) | Reified conditions | Some element of Xs satisfies the reified condition C_2.
tmember(C_2, [E|Es]) :-
    if_(call(C_2, E), true, tmember(C_2, Es)).

%! tmember_t(:C_2, ?Xs, ?T) | Reified conditions | T is true when some element of Xs satisfies the reified condition C_2, false when none does.
tmember_t(_, [], false).
tmember_t(C_2, [E|Es], T) :-
    if_(call(C_2, E), T = true, tmember_t(C_2, Es, T)).

%! tfilter(:C_2, ?Xs, ?Ys) | Reified conditions | Ys holds the elements of Xs that satisfy the reified condition C_2, in order.
tfilter(_, [], []).
tfilter(C_2, [E|Es], Ys0) :-
    if_(call(C_2, E), Ys0 = [E|Ys], Ys0 = Ys),
    tfilter(C_2, Es, Ys).

%! tpartition(:C_2, ?Xs, ?Ts, ?Fs) | Reified conditions | Ts holds the elements of Xs that satisfy the reified condition C_2 and Fs the others, each in order.
tpartition(_, [], [], []).
tpartition(C_2, [E|Es], Ts0, Fs0) :-
    if_(call(C_2, E), ( Ts0 = [E|Ts], Fs0 = Fs ), ( Ts0 = Ts, Fs0 = [E|Fs] )),
    tpartition(C_2, Es, Ts, Fs).
