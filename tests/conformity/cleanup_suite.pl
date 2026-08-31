% cleanup_suite.pl — the setup_call_cleanup/3 examples from Neumerkel's
% post-N215 draft page. Unlike the table pages this one is a PROSE
% document: the runnable material is its example blocks — a goal at
% column 0 ending in `.`, followed by an indented outcome description
% ("Fails.", "Instantiation error.", "Succeeds, unifying ...",
% "Either: ... Or: ..."). STRICT ISO Prolog + format/2,3; shared helpers
% in html_scan.pl.
%
% Classification is deliberately coarse: the descriptions are prose, and
% many outcomes are implementation-dependent pairs — those run as
% `lenient` (must terminate; any outcome). Goals that open files are
% skipped. The pipeline still fetches everything from the live page: the
% repository carries none of the page's content.
%
%% entry: cleanup_generate/0   artifacts/cleanup.html -> artifacts/cleanup_facts.pl
%% entry: cleanup_run/0        run, report via cf_report

:- dynamic(cu_test/3).

% ======================================================================
% GENERATE — cleanup page -> cleanup_facts.pl
% ======================================================================

cleanup_generate :-
    cf_read_file_bytes('artifacts/cleanup.html', H),
    scan_strip_comments(H, H1),
    scan_strip_tags(H1, H2),
    scan_deent(H2, Text),
    cu_lines(Text, Lines),
    cu_tests(Lines, Tests),
    open('artifacts/cleanup_facts.pl', write, Out),
    cu_emit(Tests, Out, 1, 0, Count),
    close(Out),
    cf_format('cleanup generate: ~w facts~n', [Count]).

cu_lines([], []).
cu_lines(Cs, [L|Ls]) :-
    scan_upto_nl(Cs, L),
    cu_after_nl(Cs, Rest),
    cu_lines(Rest, Ls).
cu_after_nl([], []) :- !.
cu_after_nl([10|R], R) :- !.
cu_after_nl([_|T], R) :- cu_after_nl(T, R).

% A goal line sits at column 0, contains setup_call_cleanup, ends in `.`,
% and READS as a term — the parse gate keeps prose out.
cu_tests([], []).
cu_tests([L|Ls], Out) :-
    ( cu_goal_line(L, Goal)
      -> cu_desc(Ls, Desc, Rest),
         Out = [t(Goal, Desc)|Out1],
         cu_tests(Rest, Out1)
      ;  cu_tests(Ls, Out) ).

cu_goal_line(L, Goal) :-
    L = [C|_], \+ scan_ws(C),
    scan_contains([0's,0'e,0't,0'u,0'p,0'_,0'c,0'a,0'l,0'l,0'_,
                   0'c,0'l,0'e,0'a,0'n,0'u,0'p], L),
    scan_trim_trailing(L, L1),
    cf_append(Goal, [0'.], L1),
    cf_read_codes_term(Goal, _, _).

cu_desc([], [], []).
cu_desc([L|Ls], Desc, Rest) :-
    ( L = [C|_], scan_ws(C), \+ scan_trim(L, [])
      -> scan_trim(L, L1),
         Desc = [L1|Desc1],
         cu_desc(Ls, Desc1, Rest)
      ;  Desc = [], Rest = [L|Ls] ).

% ---- classification --------------------------------------------------

% First description line decides; "Either"/unrecognized run lenient.
cu_class([], _) :- fail.
cu_class([D|_], Class) :-
    ( cu_pre(D, 'Fails') -> Class = fails
    ; cu_pre(D, 'Instantiation error') -> Class = 'error(instantiation_error)'
    ; cu_pre(D, 'Type error') -> Class = 'error(type_error)'
    ; cu_pre(D, 'System error') -> Class = 'error(other)'
    ; cu_pre(D, 'Succeeds') -> Class = succeeds
    ; Class = lenient ).
cu_pre(L, A) :- atom_codes(A, C), cf_append(C, _, L).

cu_emit([], _, _, C, C).
cu_emit([t(Goal, Desc)|Ts], Out, N0, C0, C) :-
    ( cu_skip(Goal)
      -> N1 = N0, C1 = C0
    ; cu_class(Desc, Class)
      -> atom_codes(Class, ClsC),
         number_codes(N0, NC),
         cf_put_codes(Out, [0'c, 0'u, 0'_, 0't, 0'e, 0's, 0't, 0'(]),
         cf_put_codes(Out, NC),
         cf_put_codes(Out, [0',, 0' ]),
         cf_put_codes(Out, ClsC),
         cf_put_codes(Out, [0',, 0' , 0'(]),
         cf_put_codes(Out, Goal),
         cf_put_codes(Out, [0'), 0'), 0'.]),
         nl(Out),
         N1 is N0 + 1, C1 is C0 + 1
    ;  N1 = N0, C1 = C0 ),
    cu_emit(Ts, Out, N1, C1, C).

% File-touching examples need fixture files the page does not ship.
cu_skip(Goal) :- scan_contains([0'o, 0'p, 0'e, 0'n, 0'(], Goal).

% ======================================================================
% LOAD + RUN
% ======================================================================

cleanup_load_facts :-
    retractall(cu_test(_, _, _)),
    open('artifacts/cleanup_facts.pl', read, S),
    cleanup_load_(S),
    close(S).
cleanup_load_(S) :-
    read_term(S, T, []),
    ( T == end_of_file -> true
    ; assertz(T), cleanup_load_(S) ).

% Runs under cf_capture: several examples write/1 their cleanup argument.
cu_check(K, G) :-
    cf_capture(G, O, _),
    cu_match(K, O), !.
cu_match(succeeds, succeeds).
cu_match(fails, fails).
cu_match(error(W), error(W)).
cu_match(lenient, _).

cleanup_run :-
    cleanup_load_facts,
    findall(N-K-G, cu_test(N, K, G), Ts0),
    cf_apply_skips(cleanup, Ts0, Ts, Skipped),
    cf_length(Ts, Total),
    cu_run_list(Ts, 0, Pass, [], FailsR),
    cf_reverse(FailsR, Fails),
    cf_length(Fails, NF),
    cf_report('cleanup:         ~w/~w~n', [Pass, Total]),
    ( NF =:= 0 -> true
    ; cf_report('  failing (~w): ~w~n', [NF, Fails]) ),
    cf_report_skips(Skipped).

cu_run_list([], P, P, F, F).
cu_run_list([N-K-G|Ts], P0, P, F0, F) :-
    ( catch(cu_check(K, G), _, fail)
      -> P1 is P0 + 1, F1 = F0
      ;  P1 = P0, F1 = [N|F0] ),
    cu_run_list(Ts, P1, P, F1, F).
