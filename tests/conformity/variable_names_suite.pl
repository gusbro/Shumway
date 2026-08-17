% variable_names_suite.pl — the Neumerkel variable_names/1 suite: 63 tests.
% Pipeline: the fetched variable_names page -> artifacts/vn_facts.pl
% (generate: /**/ back-references resolve INLINE against the last non-/**/
% query; non-runnable shapes — stdin reads, multi-clause rows, $VAR display
% quirks — are skipped) -> run. STRICT ISO Prolog + format/2,3; shared
% helpers in html_scan.pl. Test #28 mentions freeze/2: engines without it
% raise an existence error, which the test's lenient class accepts.
%
%% entry: vn_generate/0   page HTML -> artifacts/vn_facts.pl
%% entry: vn_run/0        run the 63, report via cf_report

:- dynamic(vn_test/3).

% ======================================================================
% GENERATE — variable_names page -> vn_facts.pl
% ======================================================================

vn_generate :-
    cf_read_file_bytes('artifacts/variable_names.html', H),
    scan_rows(H, Rows),
    vn_collect(Rows, [], TR),
    cf_reverse(TR, Tests),
    open('artifacts/vn_facts.pl', write, Out),
    vn_emit_tests(Tests, Out, 0, Count),
    close(Out),
    cf_format('variable_names generate: ~w facts~n', [Count]).

vn_collect(Rows, Acc, Out) :- vn_collect_(Rows, [], Acc, Out).
vn_collect_([], _, Acc, Acc).
vn_collect_([R|Rs], Base0, Acc0, Out) :-
    ( vn_row(R, NC, Q0)
      -> vn_query(Q0, Q1),
         vn_exp(R, E0), scan_trim(E0, E),
         ( scan_contains([0'/,0'*,0'*,0'/], Q1)
           -> scan_replace_all(Q1, [0'/,0'*,0'*,0'/], Base0, Q),
              Base1 = Base0
           ;  Q = Q1, Base1 = Q1 ),
         Acc1 = [t(NC, E, Q)|Acc0]
      ;  Acc1 = Acc0, Base1 = Base0 ),
    vn_collect_(Rs, Base1, Acc1, Out).

% <td><a name=(\d+)>\d+</a><td...>([^\n]*)  — strictly adjacent
vn_row(Row, NC, Q) :-
    scan_after([0'<,0't,0'd,0'>,0'<,0'a,0' ,0'n,0'a,0'm,0'e,0'=], Row, R1),
    scan_digits1(R1, R2, NC),
    R2 = [0'>|R3],
    scan_digits1(R3, R4, _),
    cf_append([0'<,0'/,0'a,0'>,0'<,0't,0'd], R5, R4),
    scan_upto_gt(R5, R6),
    scan_upto_nl(R6, Q), !.

vn_exp(Row, E) :-
    scan_after([0'<,0't,0'd,0' ,0'c,0'l,0'a,0's,0's,0'=,0'c,0'o,0'd,0'x,0'>,
                0'<,0'!,0'-,0'-], Row, R1),
    vn_codx_end(R1, R2), !,
    scan_upto_nl_or_lt(R2, E).
vn_exp(_, []).
vn_codx_end([0'-, 0'-, 0'> | R], R) :- !.
vn_codx_end([C|T], R) :- C =\= 0'>, vn_codx_end(T, R).

% <br> -> SPACE here (single-line goals).
vn_query(Q0, Q) :-
    scan_replace_all(Q0, [0'<,0'b,0'r,0'>], [0' ], Q1),
    scan_strip_comments(Q1, Q2),
    scan_strip_tags(Q2, Q3),
    scan_deent(Q3, Q4),
    scan_trim(Q4, Q).

vn_emit_tests([], _, C, C).
vn_emit_tests([t(NC, E, Q0)|Ts], Out, C0, C) :-
    scan_trim_trailing(Q0, Q1),
    ( cf_append(Q, [0'.], Q1) -> true ; Q = Q1 ),
    ( vn_skip(Q)
      -> C1 = C0
    ; vn_classify(E, ChkText)
      -> cf_put_codes(Out, [0'v, 0'n, 0'_, 0't, 0'e, 0's, 0't, 0'(]),
         cf_put_codes(Out, NC),
         cf_put_codes(Out, [0',, 0' ]),
         cf_put_codes(Out, ChkText),
         cf_put_codes(Out, [0',, 0' , 0'(]),
         cf_put_codes(Out, Q),
         cf_put_codes(Out, [0'), 0'), 0'.]),
         nl(Out),
         C1 is C0 + 1
    ; C1 = C0 ),
    vn_emit_tests(Ts, Out, C1, C).

% stdin reads (block), multi-clause rows (`. ` mid-text), $VAR quirks.
vn_skip(Q) :-
    ( scan_contains([0'r,0'e,0'a,0'd,0'_,0't,0'e,0'r,0'm,0'(], Q) -> true
    ; scan_contains([0'r,0'e,0'a,0'd,0'(], Q) -> true
    ; vn_dot_ws_nonws(Q) -> true
    ; scan_contains([0'$,0'V,0'A,0'R], Q) ).
vn_dot_ws_nonws([0'., W, N | _]) :- scan_ws(W), \+ scan_ws(N), !.
vn_dot_ws_nonws([_|T]) :- vn_dot_ws_nonws(T).

vn_classify(E, ChkText) :-
    ( vn_pre(E, 'i._e')
      -> atom_codes('error(instantiation_error)', ChkText)
    ; vn_pre(E, 'd._e')
      -> atom_codes('error(domain_error)', ChkText)
    ; vn_pre(E, 't._e')
      -> atom_codes('error(type_error)', ChkText)
    ; ( vn_pre(E, 'sy._e') ; vn_pre(E, syntax) )
      -> atom_codes('error(syntax_error)', ChkText)
    ; ( vn_pre(E, 'r._e')
      ; atom_codes(repr, P), scan_contains(P, E) )
      -> atom_codes('error(representation_error)', ChkText)
    ; vn_is(E, 'true.')
      -> atom_codes(succeeds, ChkText)
    ; vn_is(E, succeeds)          % the Codex writes a bare "succeeds" too
      -> atom_codes(succeeds, ChkText)
    ; atom_codes(waits, W), scan_contains(W, E)
      -> fail                     % needs EOF/more input: skipped
    ; ( E == []
      ; atom_codes('Impdep', I), scan_contains(I, E)
      ; scan_contains([0' ,0'o,0'r,0' ], E)
      ; scan_contains([0'.,0'.], E)
      ; scan_contains([0'e,0'.,0'g,0'.], E) )
      -> atom_codes(lenient, ChkText)
    ; % exact output: output([<codes of E>])
      vn_codes_csv(E, CsvC),
      atom_codes('output([', A), atom_codes('])', B),
      vn_appall([A, CsvC, B], ChkText) ).

vn_is(E, A) :- atom_codes(A, C), E == C.
vn_pre(E, A) :- atom_codes(A, C), cf_append(C, _, E).

vn_codes_csv([], []).
vn_codes_csv([C], D) :- !, number_codes(C, D).
vn_codes_csv([C|T], Out) :-
    number_codes(C, D),
    cf_append(D, [0',|Rest], Out),
    vn_codes_csv(T, Rest).

vn_appall([], []).
vn_appall([L|Ls], Out) :- cf_append(L, Rest, Out), vn_appall(Ls, Rest).

% ======================================================================
% LOAD + RUN
% ======================================================================

vn_load_facts :-
    retractall(vn_test(_, _, _)),
    open('artifacts/vn_facts.pl', read, S),
    vn_load_(S),
    close(S).
vn_load_(S) :-
    read_term(S, T, []),
    ( T == end_of_file -> true
    ; assertz(T), vn_load_(S) ).

% Every check runs under cf_capture so a write_term goal's output does not
% leak to the console (only the output class compares it).
vn_check(error(C), G) :-
    cf_capture(G, Outcome, _),
    Outcome = error(Cc), Cc == C.
vn_check(succeeds, G) :-
    cf_capture(G, succeeds, _).
% lenient: the goal must TERMINATE; any outcome (success, failure, error).
vn_check(lenient, G) :-
    cf_capture(G, _, _).
vn_check(output(Exp), G) :-
    cf_capture(G, Outcome, Out),
    Outcome == succeeds,
    Out == Exp.

vn_run :-
    vn_load_facts,
    findall(N-K-G, vn_test(N, K, G), Ts0),
    cf_apply_skips(variable_names, Ts0, Ts, Skipped),
    cf_length(Ts, Total),
    vn_run_list(Ts, 0, Pass, [], FailsR),
    cf_reverse(FailsR, Fails),
    cf_length(Fails, NF),
    cf_report('variable_names:  ~w/~w~n', [Pass, Total]),
    ( NF =:= 0 -> true
    ; cf_report('  failing (~w): ~w~n', [NF, Fails]) ),
    cf_report_skips(Skipped).

vn_run_list([], P, P, F, F).
vn_run_list([N-K-G|Ts], P0, P, F0, F) :-
    ( catch(vn_check(K, G), _, fail)
      -> P1 is P0 + 1, F1 = F0
      ;  P1 = P0, F1 = [N|F0] ),
    vn_run_list(Ts, P1, P, F1, F).
