% number_chars_suite.pl — the Neumerkel number_chars/2 suite: 67 tests.
% Pipeline: the fetched number_chars_cont page -> artifacts/nc_tests.tsv
% (extract) -> artifacts/nc_facts.pl (generate: binding expecteds compile
% into the goal — `X = 1.2` becomes `(Q), (X == 1.2)`) -> run. STRICT ISO
% Prolog + format/2,3; shared helpers in html_scan.pl.
%
%% entry: nc_extract/0    page HTML -> artifacts/nc_tests.tsv
%% entry: nc_generate/0   tsv -> artifacts/nc_facts.pl
%% entry: nc_run/0        run the 67, report via cf_report

:- dynamic(nc_test/3).

% ======================================================================
% EXTRACT — number_chars_cont page -> nc_tests.tsv
% ======================================================================

nc_extract :-
    cf_read_file_bytes('artifacts/number_chars_cont.html', H),
    scan_rows(H, Rows),
    open('artifacts/nc_tests.tsv', write, S),
    nc_x_rows(Rows, S, 0, Count),
    close(S),
    cf_format('number_chars extract: ~w tests~n', [Count]).

nc_x_rows([], _, C, C).
nc_x_rows([R|Rs], S, C0, C) :-
    ( nc_x_row(R, NC, Q0)
      -> nc_x_exp(R, E0),
         scan_trim(E0, E),
         nc_x_query(Q0, Q),
         nc_x_class(E, Cls),
         nc_x_emit(S, NC, Cls, E, Q),
         C1 is C0 + 1
      ;  C1 = C0 ),
    nc_x_rows(Rs, S, C1, C).

nc_x_row(Row, NC, Q) :-
    scan_after([0'<,0't,0'd,0'>,0'<,0'a,0' ,0'n,0'a,0'm,0'e,0'=], Row, R1),
    scan_digits1(R1, R2, NC),
    R2 = [0'>|R3],
    scan_digits1(R3, R4, _),
    cf_append([0'<,0'/,0'a,0'>,0'<,0't,0'd,0'>], R5, R4),
    scan_upto_nl(R5, Q), !.

nc_x_exp(Row, E) :-
    scan_after([0'<,0't,0'd,0' ,0'c,0'l,0'a,0's,0's,0'=,0'c,0'o,0'd,0'x,0'>,
                0'<,0'!,0'-,0'-], Row, R1),
    nc_codx_end(R1, R2), !,
    scan_upto_nl_or_lt(R2, E).
nc_x_exp(_, []).
nc_codx_end([0'-, 0'-, 0'> | R], R) :- !.
nc_codx_end([C|T], R) :- C =\= 0'>, nc_codx_end(T, R).

nc_x_query(Q0, Q) :-
    scan_replace_all(Q0, [0'<,0'b,0'r,0'>], [10], Q1),
    scan_strip_comments(Q1, Q2),
    scan_strip_tags(Q2, Q3),
    scan_deent(Q3, Q).

nc_x_class(E, Cls) :-
    ( nc_is(E, 'true.')  -> Cls = succeeds
    ; nc_is(E, 'false.') -> Cls = fails
    ; nc_pre(E, 't._e.') -> Cls = type_error
    ; ( nc_pre(E, inst) ; nc_pre(E, 'i._e') ) -> Cls = instantiation_error
    ; ( nc_pre(E, 'sy._e')
      ; atom_codes(syntax, P), scan_contains(P, E) ) -> Cls = syntax_error
    ; nc_pre(E, 'rep._e') -> Cls = representation_error
    ; Cls = value ).
nc_is(E, A) :- atom_codes(A, C), E == C.
nc_pre(E, A) :- atom_codes(A, C), cf_append(C, _, E).

nc_x_emit(S, NC, Cls, E, Q) :-
    cf_put_codes(S, NC), put_code(S, 9),
    atom_codes(Cls, CC), cf_put_codes(S, CC), put_code(S, 9),
    cf_put_codes(S, E), put_code(S, 9),
    scan_put_csv(S, Q),
    nl(S).

% ======================================================================
% GENERATE — nc_tests.tsv -> nc_facts.pl
% ======================================================================
% The facts are written as SOURCE TEXT (the goals embed "..." strings that
% must read under double_quotes=chars — the loader flips the flag while
% reading them back).

nc_generate :-
    open('artifacts/nc_tests.tsv', read, In),
    open('artifacts/nc_facts.pl', write, Out),
    nc_g_lines(In, Out, 0, Count),
    close(Out),
    close(In),
    cf_format('number_chars generate: ~w facts~n', [Count]).

nc_g_lines(In, Out, C0, C) :-
    scan_read_line(In, L),
    ( L == end_of_file -> C = C0
    ; ( nc_g_split(L, NumC, _ClsC, ExpC, CodesC),
        nc_g_row(Out, NumC, ExpC, CodesC)
        -> C1 is C0 + 1
        ;  C1 = C0 ),
      nc_g_lines(In, Out, C1, C) ).

nc_g_split(L, A, B, C, D) :-
    scan_split_first(L, 9, A, R1),
    scan_split_first(R1, 9, B, R2),
    scan_split_first(R2, 9, C, D).

nc_g_row(Out, NumC, Exp, CodesC) :-
    nc_g_codes(CodesC, Q0),
    nc_g_clean(Q0, Q),
    nc_g_classify(Exp, Q, ChkText, Goal),
    cf_put_codes(Out, [0'n, 0'c, 0'_, 0't, 0'e, 0's, 0't, 0'(]),
    cf_put_codes(Out, NumC),
    cf_put_codes(Out, [0',, 0' ]),
    cf_put_codes(Out, ChkText),
    cf_put_codes(Out, [0',, 0' , 0'(]),
    cf_put_codes(Out, Goal),
    cf_put_codes(Out, [0'), 0'), 0'.]),
    nl(Out).

nc_g_codes([], []) :- !.
nc_g_codes(F, [N|Ns]) :-
    ( scan_split_first(F, 0',, Piece, Rest)
      -> number_codes(N, Piece), nc_g_codes(Rest, Ns)
      ;  number_codes(N, F), Ns = [] ).

% strip a leading `?- `, trailing whitespace, and the trailing dot.
nc_g_clean(Q0, Q) :-
    ( scan_ws_run(Q0, T0), cf_append([0'?, 0'-], T1, T0)
      -> scan_ws_run(T1, Q1) ; Q1 = Q0 ),
    scan_trim_trailing(Q1, Q2),
    ( cf_append(Q, [0'.], Q2) -> true ; Q = Q2 ).

% ---- expected classification (fails = no fact emitted = skipped) -----

nc_g_classify(Exp, Q, ChkText, Q) :-
    nc_is(Exp, 'true.'), !, atom_codes(succeeds, ChkText).
nc_g_classify(Exp, Q, ChkText, Q) :-
    nc_is(Exp, 'false.'), !, atom_codes(fails, ChkText).
nc_g_classify(Exp, Q, ChkText, Q) :-
    nc_error_word(W),
    atom_codes(W, WC),
    cf_append(WC, After, Exp),
    nc_boundary(After), !,
    atom_codes(W, WN),
    nc_appall([[0'e,0'r,0'r,0'o,0'r,0'(], WN, [0')]], ChkText).
nc_g_classify(Exp, Q, ChkText, Q) :-
    ( nc_pre(Exp, inst) ; nc_pre(Exp, 'i._e') ), !,
    atom_codes('error(instantiation_error)', ChkText).
nc_g_classify(Exp, Q, ChkText, Q) :-
    ( nc_pre(Exp, 'sy._e') ; nc_pre(Exp, syntax) ), !,
    atom_codes('error(syntax_error)', ChkText).
% ^Var\s*=\s*Number\s*\.?\s*$ -> succeeds, goal (Q), (Var == Number)
nc_g_classify(Exp, Q, ChkText, Goal) :-
    nc_var(Exp, R1, V),
    scan_ws_run(R1, R2), R2 = [0'=|R3], scan_ws_run(R3, R4),
    nc_number(R4, R5, Val),
    nc_tail_end(R5), !,
    atom_codes(succeeds, ChkText),
    nc_eq_goal(Q, V, Val, Goal).
% ^Var\s*=\s*'chars'(\s*,\s*Var2\s*=\s*'chars')?\s*\.?\s*$
nc_g_classify(Exp, Q, ChkText, Goal) :-
    nc_var(Exp, R1, V1),
    scan_ws_run(R1, R2), R2 = [0'=|R3], scan_ws_run(R3, R4),
    nc_quoted(R4, R5, A1),
    ( scan_ws_run(R5, R6), R6 = [0',|R7], scan_ws_run(R7, R8),
      nc_var(R8, R9, V2),
      scan_ws_run(R9, R10), R10 = [0'=|R11], scan_ws_run(R11, R12),
      nc_quoted(R12, R13, A2),
      nc_tail_end(R13)
      -> nc_eq_goal(Q, V1, A1, G1),
         nc_eq_pair(V2, A2, P2),
         nc_appall([G1, [0',, 0' ], P2], Goal)
    ; nc_tail_end(R5),
      nc_eq_goal(Q, V1, A1, Goal) ), !,
    atom_codes(succeeds, ChkText).
nc_g_classify(Exp, Q, ChkText, Q) :-
    ( Exp == []
    ; scan_contains([0' ,0'o,0'r,0' ], Exp)
    ; atom_codes(maybe, M), scan_contains(M, Exp) ), !,
    % unspecified / multiple-acceptable: must TERMINATE; any outcome OK.
    atom_codes(lenient, ChkText).

nc_eq_goal(Q, V, Val, Goal) :-
    nc_eq_pair(V, Val, P),
    nc_appall([[0'(], Q, [0'), 0',, 0' ], P], Goal).
nc_eq_pair(V, Val, Out) :-
    nc_appall([[0'(], V, [0' , 0'=, 0'=, 0' ], Val, [0')]], Out).

nc_error_word(type_error). nc_error_word(domain_error).
nc_error_word(evaluation_error). nc_error_word(representation_error).
nc_error_word(instantiation_error). nc_error_word(permission_error).
nc_error_word(existence_error).

nc_boundary([]).
nc_boundary([C|_]) :- \+ scan_word_char(C).

nc_var([C|T], Rest, [C|W]) :-
    C >= 0'A, C =< 0'Z,
    nc_word_run(T, Rest, W).
nc_word_run([C|T], Rest, [C|W]) :-
    scan_word_char(C), !, nc_word_run(T, Rest, W).
nc_word_run(L, L, []).

nc_number(L, Rest, Num) :-
    ( L = [0'-|L1] -> Pre = [0'-] ; L1 = L, Pre = [] ),
    scan_digits1(L1, L2, D1),
    ( L2 = [0'.|L3], scan_digits1(L3, L4, D2)
      -> ( nc_exp_part(L4, L5, E)
           -> nc_appall([Pre, D1, [0'.], D2, E], Num), Rest = L5
           ;  nc_appall([Pre, D1, [0'.], D2], Num), Rest = L4 )
      ;  cf_append(Pre, D1, Num), Rest = L2 ).
nc_exp_part([E0|L1], Rest, [E0|E]) :-
    ( E0 =:= 0'e ; E0 =:= 0'E ),
    ( L1 = [S|L2], ( S =:= 0'+ ; S =:= 0'- ) -> E = [S|Ds], L3 = L2
    ; E = Ds, L3 = L1 ),
    scan_digits1(L3, Rest, Ds).

nc_quoted([Q0|T], Rest, [Q0|Cs]) :-
    Q0 =:= 0''',
    nc_qbody(T, Rest, Cs).
nc_qbody([Q0|T], T, [Q0]) :- Q0 =:= 0''', !.
nc_qbody([C|T], Rest, [C|Cs]) :- nc_qbody(T, Rest, Cs).

nc_tail_end(L) :-
    scan_ws_run(L, L1),
    ( L1 = [0'.|L2] -> scan_ws_run(L2, []) ; L1 = [] ).

nc_appall([], []).
nc_appall([L|Ls], Out) :- cf_append(L, Rest, Out), nc_appall(Ls, Rest).

% ======================================================================
% LOAD + RUN
% ======================================================================
% The facts' "..." literals must read as CHAR LISTS; the flag flips around
% the load and is restored to the ISO default (codes) after.

nc_load_facts :-
    retractall(nc_test(_, _, _)),
    set_prolog_flag(double_quotes, chars),
    open('artifacts/nc_facts.pl', read, S),
    ( catch(nc_load_(S), E, true) -> true ; true ),
    close(S),
    set_prolog_flag(double_quotes, codes),
    ( var(E) -> true ; throw(E) ).
nc_load_(S) :-
    read_term(S, T, []),
    ( T == end_of_file -> true
    ; assertz(T), nc_load_(S) ).

nc_outcome(G, O) :-
    catch( ( call(G) -> O = succeeds ; O = fails ),
           Err, cf_error_class(Err, O) ).

nc_check(succeeds, G) :- nc_outcome(G, succeeds).
nc_check(fails, G)    :- nc_outcome(G, fails).
nc_check(error(C), G) :- nc_outcome(G, error(C)).
nc_check(lenient, G)  :- nc_outcome(G, _).

nc_run :-
    nc_load_facts,
    findall(N-K-G, nc_test(N, K, G), Ts0),
    cf_apply_skips(number_chars, Ts0, Ts, Skipped),
    cf_length(Ts, Total),
    nc_run_list(Ts, 0, Pass, [], FailsR),
    cf_reverse(FailsR, Fails),
    cf_length(Fails, NF),
    cf_report('number_chars:    ~w/~w~n', [Pass, Total]),
    ( NF =:= 0 -> true
    ; cf_report('  failing (~w): ~w~n', [NF, Fails]) ),
    cf_report_skips(Skipped).

nc_run_list([], P, P, F, F).
nc_run_list([N-K-G|Ts], P0, P, F0, F) :-
    ( catch(nc_check(K, G), _, fail)
      -> P1 is P0 + 1, F1 = F0
      ;  P1 = P0, F1 = [N|F0] ),
    nc_run_list(Ts, P1, P, F1, F).
