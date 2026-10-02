% The cost of ONE kind of exit from a wasm module per iteration, Tier-0
% against the tier: each loop does one thing a promoted predicate may have
% to leave the module for, so its counters and its time price that exit.
% Run at #wasmprobe=exits&n=100000.
%
%uses scryer
%probe plain: mb_plain({N}).
%probe b_getval: b_setval(mbk, 1), mb_bget({N}).
%probe acyclic: mb_acyc({N}).
%probe sort: mb_sort({N}).
%probe attvar =/2: mb_attv_run({N}).
%probe meta call: mb_meta_run({N}).
%probe struct =/2: mb_eq({N}).
%probe fetch global: b_setval(mbk, 1), mb_fetch({N}).
%probe dynamic call: mb_dyn({N}).
%probe attvar wake: mb_wake_run({N}).
%probe bb_get shim: bb_b_put(mbk2, 1), mb_bb({N}).
%probe bb put+get: mb_bbw({N}).

:- use_module(library(iso_ext)).
:- use_module(library(coroutining)).

mb_plain(0) :- !.
mb_plain(N) :- N1 is N-1, mb_plain(N1).

mb_bget(0) :- !.
mb_bget(N) :- b_getval(mbk, V), V == 1, N1 is N-1, mb_bget(N1).

mb_acyc(0) :- !.
mb_acyc(N) :- acyclic_term(f(N)), N1 is N-1, mb_acyc(N1).

mb_sort(0) :- !.
mb_sort(N) :- sort([3,1,2], _), N1 is N-1, mb_sort(N1).

mb_attv(0, _) :- !.
mb_attv(N, A) :- Y = A, Y == A, N1 is N-1, mb_attv(N1, A).
mb_attv_run(N) :- put_attr(A, mbattr, 1), mb_attv(N, A).

mb_meta(0, _) :- !.
mb_meta(N, G) :- call(G), N1 is N-1, mb_meta(N1, G).
mb_meta_run(N) :- mb_meta(N, true).

mb_eq(0) :- !.
mb_eq(N) :- X = f(N, a), X = f(_, a), N1 is N-1, mb_eq(N1).

mb_fetch(0) :- !.
mb_fetch(N) :- '$fetch_global_var'(mbk, V), V == 1, N1 is N-1, mb_fetch(N1).

:- dynamic(dn/1).
dn(1).
mb_dyn(0) :- !.
mb_dyn(N) :- dn(X), X == 1, N1 is N-1, mb_dyn(N1).

mb_wake(0, _) :- !.
mb_wake(N, A) :- \+ \+ (A = b), N1 is N-1, mb_wake(N1, A).
mb_wake_run(N) :- freeze(A, true), mb_wake(N, A).

mb_bb(0) :- !.
mb_bb(N) :- bb_get(mbk2, V), V == 1, N1 is N-1, mb_bb(N1).

mb_bbw(0) :- !.
mb_bbw(N) :- bb_b_put(mbk3, N), bb_get(mbk3, V), V == N, N1 is N-1, mb_bbw(N1).
