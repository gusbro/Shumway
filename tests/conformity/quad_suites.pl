% quad_suites.pl — the Neumerkel suites distributed as MACHINE-READABLE
% quad files (length_quad.pl, phrase_quad.pl): a shared parser plus the
% length/2 and phrase/2,3 suite entries. STRICT ISO Prolog + format/2,3;
% shared helpers in html_scan.pl.
%
% Quad format, per test:
%     <id> ?- <goal>.
%           <expected>
%        |  <alternative expected>.
% An id is a run of word chars (1, 39, a1, c7, f3). Expected alternatives
% are separated by lines whose first non-blank char is `|`; `;`-lines are
% answer-sequence continuations of the SAME alternative. An `sto,` prefix
% (subject to occurs-check) and `% ...` comment tails are stripped before
% classification. A `loops.` alternative is checked through the driver's
% conformity_timed_call hook: still running at 15 s counts as looping.
% The driver's conformity_deep flag upgrades loops-or-resource tests to
% an UNBOUNDED run that must end in the resource error (see qd_check).
%
%% entry: length_generate/0   artifacts/length_quad.pl -> artifacts/length_facts.pl
%% entry: length_run/0        run, report via cf_report
%% entry: phrase_generate/0   artifacts/phrase_quad.pl -> artifacts/phrase_facts.pl
%% entry: phrase_run/0        run, report via cf_report

:- dynamic(qd_test/4).

% ======================================================================
% suite entries
% ======================================================================

length_generate :-
    qd_generate(length, 'artifacts/length_quad.pl',
                'artifacts/length_facts.pl').
length_run :-
    qd_run(length, 'length:          ', 'artifacts/length_facts.pl').

phrase_generate :-
    qd_generate(phrase, 'artifacts/phrase_quad.pl',
                'artifacts/phrase_facts.pl').
phrase_run :-
    qd_run(phrase, 'phrase:          ', 'artifacts/phrase_facts.pl').

% ======================================================================
% GENERATE — quad file -> facts file
% ======================================================================

qd_generate(Suite, QuadFile, FactsFile) :-
    open(QuadFile, read, In),
    qd_read_lines(In, Lines),
    close(In),
    qd_tests(Lines, Tests),
    open(FactsFile, write, Out),
    qd_emit(Tests, Suite, Out, 0, Count, 0, Dropped),
    close(Out),
    cf_format('~w generate: ~w facts (~w unverifiable dropped)~n',
              [Suite, Count, Dropped]).

qd_read_lines(S, Lines) :-
    scan_read_line(S, L),
    ( L == end_of_file -> Lines = []
    ; Lines = [L|T], qd_read_lines(S, T) ).

% ---- grouping into t(IdCodes, GoalCodes, ExpectedLines) --------------

qd_tests([], []).
qd_tests([L|Ls], Out) :-
    ( qd_test_start(L, Id, Goal)
      -> qd_expected(Ls, Exp, Rest),
         Out = [t(Id, Goal, Exp)|Out1],
         qd_tests(Rest, Out1)
      ;  qd_tests(Ls, Out) ).

qd_expected([], [], []).
qd_expected([L|Ls], Exp, Rest) :-
    ( qd_test_start(L, _, _)
      -> Exp = [], Rest = [L|Ls]
    ; scan_trim(L, [])
      -> qd_expected(Ls, Exp, Rest)
    ;  Exp = [L|Exp1], qd_expected(Ls, Exp1, Rest) ).

% <ws> Id <ws> ?- <ws> Goal .
qd_test_start(Line, Id, Goal) :-
    scan_ws_run(Line, L1),
    qd_word(L1, L2, Id),
    scan_ws_run(L2, L3),
    cf_append([0'?, 0'-], L4, L3),
    scan_ws_run(L4, G0),
    scan_trim_trailing(G0, G1),
    cf_append(Goal, [0'.], G1),
    Goal = [_|_].

qd_word([C|T], Rest, [C|W]) :-
    scan_word_char(C),
    qd_word_run(T, Rest, W).
qd_word_run([C|T], Rest, [C|W]) :-
    scan_word_char(C), !, qd_word_run(T, Rest, W).
qd_word_run(L, L, []).

% ---- expected lines -> alternative class list ------------------------

% Lines fold into alternatives: a `|`-prefixed line opens a new one;
% anything else (including `;` answer continuations) extends the current.
qd_alts([], []).
qd_alts([L|Ls], [A|As]) :-
    scan_trim(L, L1),
    qd_alt_more(Ls, L1, A, Rest),
    qd_alts_rest(Rest, As).
qd_alts_rest([], []).
qd_alts_rest([L|Ls], [A|As]) :-
    scan_trim(L, L1),
    ( L1 = [0'||T] -> scan_ws_run(T, L2) ; L2 = L1 ),
    qd_alt_more(Ls, L2, A, Rest),
    qd_alts_rest(Rest, As).
qd_alt_more([L|Ls], Acc, A, Rest) :-
    scan_trim(L, L1),
    \+ L1 = [0'||_], !,
    cf_append(Acc, [0' |L1], Acc1),
    qd_alt_more(Ls, Acc1, A, Rest).
qd_alt_more(Ls, A, A, Ls).

qd_classes(Exp, Classes) :-
    qd_alts(Exp, Alts),
    qd_classify_alts(Alts, [], R),
    cf_reverse(R, Classes).

qd_classify_alts([], Acc, Acc).
qd_classify_alts([A|As], Acc, Out) :-
    ( qd_alt_class(A, C), \+ cf_member(C, Acc)
      -> Acc1 = [C|Acc]
      ;  Acc1 = Acc ),
    qd_classify_alts(As, Acc1, Out).

qd_alt_class(Alt0, Class) :-
    qd_strip_comment(Alt0, A1),
    qd_strip_sto(A1, A2),
    scan_trim(A2, A3),
    ( cf_append(A4, [0'.], A3) -> true ; A4 = A3 ),
    scan_trim_trailing(A4, A5),
    qd_classify(A5, Class).

qd_strip_comment(L, Out) :-
    ( scan_split_first(L, 0'%, Pre, _) -> Out = Pre ; Out = L ).

qd_strip_sto(L, Out) :-
    ( cf_append([0's, 0't, 0'o, 0',], T, L)
      -> scan_ws_run(T, T1), qd_strip_sto(T1, Out)
      ;  Out = L ).

qd_classify([], _) :- !, fail.
qd_classify(A, error(W)) :-
    qd_error_word(W),
    atom_codes(W, WC),
    cf_append(WC, After, A),
    ( After = [] ; After = [C|_], \+ scan_word_char(C) ), !.
qd_classify(A, error(other)) :-
    cf_append([0't, 0'h, 0'r, 0'o, 0'w, 0'(], _, A), !.
qd_classify(A, fails) :- qd_is(A, false), !.
qd_classify(A, succeeds) :- qd_is(A, true), !.
qd_classify(A, loops) :- qd_is(A, loops), !.
% an answer display (`L = [a], N = 1 ; ...`) — the test succeeds.
qd_classify(A, succeeds) :- scan_contains([0'=], A), !.
qd_classify(_, lenient).

qd_is(A, Atom) :- atom_codes(Atom, C), A == C.

qd_error_word(instantiation_error).
qd_error_word(type_error).
qd_error_word(domain_error).
qd_error_word(existence_error).
qd_error_word(permission_error).
qd_error_word(representation_error).
qd_error_word(evaluation_error).
qd_error_word(resource_error).
qd_error_word(syntax_error).

% ---- emission --------------------------------------------------------

qd_emit([], _, _, C, C, D, D).
qd_emit([t(Id, Goal, Exp)|Ts], Suite, Out, C0, C, D0, D) :-
    qd_classes(Exp, Classes),
    ( Classes = []
      -> C1 = C0, D1 is D0 + 1
      ;  qd_emit_one(Suite, Id, Classes, Goal, Out),
         C1 is C0 + 1, D1 = D0 ),
    qd_emit(Ts, Suite, Out, C1, C, D1, D).

qd_emit_one(Suite, Id, Classes, Goal, Out) :-
    atom_codes(Suite, SC),
    cf_put_codes(Out, [0'q, 0'd, 0'_, 0't, 0'e, 0's, 0't, 0'(]),
    cf_put_codes(Out, SC),
    cf_put_codes(Out, [0',, 0' ]),
    cf_put_codes(Out, Id),
    cf_put_codes(Out, [0',, 0' , 0'[]),
    qd_put_classes(Out, Classes),
    cf_put_codes(Out, [0'], 0',, 0' , 0'(]),
    cf_put_codes(Out, Goal),
    cf_put_codes(Out, [0'), 0'), 0'.]),
    nl(Out).

qd_put_classes(_, []).
qd_put_classes(Out, [C]) :- !, qd_put_class(Out, C).
qd_put_classes(Out, [C|T]) :-
    qd_put_class(Out, C), put_code(Out, 0',),
    qd_put_classes(Out, T).
qd_put_class(Out, error(W)) :- !,
    atom_codes(W, WC),
    cf_put_codes(Out, [0'e, 0'r, 0'r, 0'o, 0'r, 0'(]),
    cf_put_codes(Out, WC),
    put_code(Out, 0')).
qd_put_class(Out, C) :-
    atom_codes(C, CC), cf_put_codes(Out, CC).

% ======================================================================
% LOAD + RUN
% ======================================================================

qd_load_facts(Suite, FactsFile) :-
    retractall(qd_test(Suite, _, _, _)),
    open(FactsFile, read, S),
    qd_load_(S),
    close(S).
qd_load_(S) :-
    read_term(S, T, []),
    ( T == end_of_file -> true
    ; assertz(T), qd_load_(S) ).

% Output is captured (phrase tests write nothing, but a cleanup-style
% goal could); the outcome alone is compared against the class list.
qd_outcome(G, O) :-
    cf_capture(G, O, _).

% A test with `loops` among its sanctioned outcomes runs under the
% driver's conformity_timed_call: still running at 15 s IS the loops
% outcome (no harness can observe an infinite loop directly). Under the
% driver's conformity_deep flag, a test whose PREFERRED outcome (the
% page lists alternatives in preference order, and the class list keeps
% it) is resource_error runs UNBOUNDED instead — the enumeration must
% actually END in the (catchable) resource error. A loops-preferred
% test (the freeze-driven #30) stays time-bounded even under deep: its
% loop only meets a resource wall after grinding attribute wakeups for
% hours.
qd_check(Classes, G) :-
    ( cf_member(loops, Classes)
      -> ( conformity_deep, Classes = [error(resource_error)|_]
           -> qd_outcome(G, O)
           ;  conformity_timed_call(G, 15000, O) )
      ;  qd_outcome(G, O) ),
    cf_member(C, Classes),
    qd_match(C, O), !.

qd_match(succeeds, succeeds).
qd_match(fails, fails).
qd_match(error(W), error(W)).
qd_match(loops, timeout).
qd_match(lenient, _).

qd_run(Suite, Label, FactsFile) :-
    qd_load_facts(Suite, FactsFile),
    findall(N-K-G, qd_test(Suite, N, K, G), Ts0),
    cf_apply_skips(Suite, Ts0, Ts, Skipped),
    cf_length(Ts, Total),
    qd_run_list(Ts, 0, Pass, [], FailsR),
    cf_reverse(FailsR, Fails),
    cf_length(Fails, NF),
    cf_report('~w~w/~w~n', [Label, Pass, Total]),
    ( NF =:= 0 -> true
    ; cf_report('  failing (~w): ~w~n', [NF, Fails]) ),
    cf_report_skips(Skipped).

qd_run_list([], P, P, F, F).
qd_run_list([N-K-G|Ts], P0, P, F0, F) :-
    ( catch(qd_check(K, G), _, fail)
      -> P1 is P0 + 1, F1 = F0
      ;  P1 = P0, F1 = [N|F0] ),
    qd_run_list(Ts, P1, P, F1, F).
