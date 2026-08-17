% dif_suite.pl — the Neumerkel dif/2 suite: 26 tests. Pipeline: the fetched
% dif page -> artifacts/dif_facts.pl (generate) -> run. STRICT ISO Prolog +
% format/2,3; shared helpers in html_scan.pl.
%
% dif/2 itself is NOT ISO: the driver loads whatever provides it (Shumway
% library(coroutining), Scryer library(dif), SWI builtin) and the
% orchestrator probes for it — on an engine without dif (GNU Prolog) the
% whole suite is reported as skipped.
%
%% entry: dif_generate/0   page HTML -> artifacts/dif_facts.pl
%% entry: dif_run/0        run the 26, report via cf_report

:- dynamic(dif_test/3).

% ======================================================================
% GENERATE — dif page -> dif_facts.pl
% ======================================================================

dif_generate :-
    cf_read_file_bytes('artifacts/dif.html', H),
    scan_rows(H, Rows),
    open('artifacts/dif_facts.pl', write, Out),
    dif_g_rows(Rows, Out, 0, Count),
    close(Out),
    cf_format('dif generate: ~w facts~n', [Count]).

dif_g_rows([], _, C, C).
dif_g_rows([R|Rs], Out, C0, C) :-
    ( dif_row(R, NC, Q0)
      -> dif_query(Q0, Q1),
         dif_exp(R, E),
         dif_gen_query(Q1, Q),
         ( dif_classify(E, ChkText)
           -> cf_put_codes(Out, [0'd, 0'i, 0'f, 0'_, 0't, 0'e, 0's, 0't, 0'(]),
              cf_put_codes(Out, NC),
              cf_put_codes(Out, [0',, 0' ]),
              cf_put_codes(Out, ChkText),
              cf_put_codes(Out, [0',, 0' , 0'(]),
              cf_put_codes(Out, Q),
              cf_put_codes(Out, [0'), 0'), 0'.]),
              nl(Out),
              C1 is C0 + 1
           ;  C1 = C0 )
      ;  C1 = C0 ),
    dif_g_rows(Rs, Out, C1, C).

% <td><a name=(\d+)>\d+</a>\s*<td[^>]*>\s*([^\n]*)  — leftmost
dif_row(Row, NC, Q) :-
    scan_after([0'<,0't,0'd,0'>,0'<,0'a,0' ,0'n,0'a,0'm,0'e,0'=], Row, R1),
    scan_digits1(R1, R2, NC),
    R2 = [0'>|R3],
    scan_digits1(R3, R4, _),
    cf_append([0'<,0'/,0'a,0'>], R5, R4),
    scan_ws_run(R5, R6),
    cf_append([0'<,0't,0'd], R7, R6),
    scan_upto_gt(R7, R8),
    scan_ws_run(R8, R9),
    scan_upto_nl(R9, Q), !.

% <td class=ad[^>]*>\s*([^\n<]*)  — '' when absent
dif_exp(Row, E) :-
    scan_after([0'<,0't,0'd,0' ,0'c,0'l,0'a,0's,0's,0'=,0'a,0'd], Row, R1),
    scan_upto_gt(R1, R2),
    scan_ws_run(R2, R3),
    scan_upto_nl_or_lt(R3, E0), !,
    scan_trim(E0, E).
dif_exp(_, []).

dif_query(Q0, Q) :-
    scan_strip_comments(Q0, Q1),
    scan_strip_tags(Q1, Q2),
    scan_deent(Q2, Q3),
    scan_trim(Q3, Q).

% `?- ` prefix, trailing whitespace, trailing dot.
dif_gen_query(Q0, Q) :-
    ( scan_ws_run(Q0, T0), cf_append([0'?, 0'-], T1, T0)
      -> scan_ws_run(T1, Q1) ; Q1 = Q0 ),
    scan_trim_trailing(Q1, Q2),
    ( cf_append(Q, [0'.], Q2) -> true ; Q = Q2 ).

dif_classify(E, ChkText) :-
    ( scan_contains([0'|], E)
      -> atom_codes(lenient, ChkText)      % multiple acceptable answers
    ; atom_codes(true, T), cf_append(T, _, E)
      -> atom_codes(succeeds, ChkText)
    ; atom_codes(false, F), cf_append(F, _, E)
      -> atom_codes(fails, ChkText)
    ; scan_contains([0'=], E)
      -> atom_codes(succeeds, ChkText)
    ; fail ).

% ======================================================================
% LOAD + RUN
% ======================================================================

dif_load_facts :-
    retractall(dif_test(_, _, _)),
    open('artifacts/dif_facts.pl', read, S),
    dif_load_(S),
    close(S).
dif_load_(S) :-
    read_term(S, T, []),
    ( T == end_of_file -> true
    ; assertz(T), dif_load_(S) ).

dif_outcome(G, O) :-
    catch(( call(G) -> O = succeeds ; O = fails ), _, O = err).

dif_check(succeeds, G) :- dif_outcome(G, succeeds).
dif_check(fails, G)    :- dif_outcome(G, fails).
% lenient: must TERMINATE with one of the valid answers — here the valid
% set is {succeeds, fails}; an error does not conform.
dif_check(lenient, G)  :- dif_outcome(G, O), ( O == succeeds ; O == fails ).

dif_run :-
    dif_load_facts,
    findall(N-K-G, dif_test(N, K, G), Ts0),
    cf_apply_skips(dif, Ts0, Ts, Skipped),
    cf_length(Ts, Total),
    dif_run_list(Ts, 0, Pass, [], FailsR),
    cf_reverse(FailsR, Fails),
    cf_length(Fails, NF),
    cf_report('dif:             ~w/~w~n', [Pass, Total]),
    ( NF =:= 0 -> true
    ; cf_report('  failing (~w): ~w~n', [NF, Fails]) ),
    cf_report_skips(Skipped).

dif_run_list([], P, P, F, F).
dif_run_list([N-K-G|Ts], P0, P, F0, F) :-
    ( catch(dif_check(K, G), _, fail)
      -> P1 is P0 + 1, F1 = F0
      ;  P1 = P0, F1 = [N|F0] ),
    dif_run_list(Ts, P1, P, F1, F).
