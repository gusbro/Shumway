% html_scan.pl — shared scanning and utility predicates for the conformity
% suites. STRICT ISO Prolog (plus format/2,3, which every target engine
% provides): binary file reads (a page's ISO-8859-1 bytes ARE Latin-1 code
% points), HTML row/field scanning, and small self-contained replacements
% for the non-ISO conveniences (forall, msort, numbervars, output capture).
%
%% entry: cf_read_file_bytes(+File, -Codes)
%% entry: cf_capture(+Goal, -Outcome, -OutputCodes)
% All predicates here are prefixed cf_ / scan_ so they collide with nothing
% on any engine.

% ---- files ---------------------------------------------------------------

% The page bytes, read through a BINARY stream: byte values are exactly the
% Latin-1 code points, on every engine, with no encoding negotiation.
cf_read_file_bytes(F, Codes) :-
    open(F, read, S, [type(binary)]),
    cf_read_bytes_(S, Codes),
    close(S).
cf_read_bytes_(S, L) :-
    get_byte(S, B),
    ( B =:= -1 -> L = [] ; L = [B|T], cf_read_bytes_(S, T) ).

% A text file's codes (for reading back our own capture/temp files, which we
% also wrote as text — self-consistent per engine).
cf_read_file_codes(F, Codes) :-
    open(F, read, S),
    cf_read_codes_(S, Codes),
    close(S).
cf_read_codes_(S, L) :-
    get_code(S, C),
    ( C =:= -1 -> L = [] ; L = [C|T], cf_read_codes_(S, T) ).

% Write codes to a text file (replaces tell/told).
cf_write_file_codes(F, Codes) :-
    open(F, write, S),
    cf_put_codes(S, Codes),
    close(S).

cf_put_codes(_, []).
cf_put_codes(S, [C|T]) :- put_code(S, C), cf_put_codes(S, T).

cf_file_exists(F) :-
    catch(open(F, read, S), _, fail),
    close(S).

% ---- output capture (replaces with_output_to; ISO streams only) ----------

% Runs Goal once with current output redirected to a temp file; Outcome is
% succeeds / fails / error(Class); OutputCodes is what it wrote. The goal
% runs in the CURRENT engine (op/3, assertz side effects survive).
cf_capture(Goal, Outcome, OutputCodes) :-
    current_output(Old),
    open('artifacts/capture.tmp', write, S),
    set_output(S),
    % The outcome lands in a FRESH variable: output must be restored and the
    % stream closed before any unification a caller-bound Outcome could fail.
    ( catch(Goal, Err, true)
      -> ( var(Err) -> O = succeeds ; cf_error_class(Err, O) )
      ;  O = fails ),
    set_output(Old),
    close(S),
    cf_read_file_codes('artifacts/capture.tmp', OutputCodes),
    Outcome = O.

cf_error_class(Err, error(C)) :-
    nonvar(Err), Err = error(F, _), nonvar(F), !,
    functor(F, C, _).
cf_error_class(_, error(other)).

% ---- term round-trips (replaces read_term_from_atom) ---------------------

% Reads ONE term (and its variable_names) from a codes list, under the
% CURRENT operator table and flags, via a temp file. A terminating ` .` is
% appended unless the (trimmed) text already ends with a dot — the space
% keeps a graphic-atom tail (`*`) from fusing with the terminator.
cf_read_codes_term(Codes, Term, VarNames) :-
    scan_trim_trailing(Codes, C1),
    ( cf_append(_, [0'.], C1) -> C2 = C1
    ; cf_append(C1, [0' , 0'.], C2) ),
    cf_write_file_codes('artifacts/readback.tmp', C2),
    open('artifacts/readback.tmp', read, S),
    ( catch(read_term(S, Term, [variable_names(VarNames)]), _, fail)
      -> close(S)
      ;  close(S), fail ).

% ---- small ISO-only replacements -----------------------------------------

cf_forall(Cond, Action) :- \+ (Cond, \+ Action).

% Own numbervars ('$VAR'-binding walk; arg/3 recursion, no =..).
cf_numbervars(T, N0, N) :-
    ( var(T) -> T = '$VAR'(N0), N is N0 + 1
    ; compound(T) ->
        functor(T, _, A),
        cf_numbervars_args(1, A, T, N0, N)
    ; N = N0 ).
cf_numbervars_args(I, A, T, N0, N) :-
    ( I > A -> N = N0
    ; arg(I, T, X),
      cf_numbervars(X, N0, N1),
      I1 is I + 1,
      cf_numbervars_args(I1, A, T, N1, N) ).

% Variant equality: same shape with variables in the same positions.
cf_variant(A, B) :-
    \+ \+ ( cf_numbervars(A, 0, N),
            cf_numbervars(B, 0, M),
            N == M, A == B ).

% Occurrence counting for the stats report (replaces sort/4): keeps first-
% appearance order.
cf_count([], []).
cf_count([K|T], Out) :-
    cf_count(T, Out0),
    cf_bump(Out0, K, Out).
cf_bump([], K, [K-1]).
cf_bump([K-N|T], K, [K-N1|T]) :- !, N1 is N + 1.
cf_bump([P|T], K, [P|T1]) :- cf_bump(T, K, T1).

% ---- generic codes scanning ----------------------------------------------

scan_contains(Sub, L) :- cf_append(_, T, L), cf_append(Sub, _, T), !.

cf_append([], L, L).
cf_append([H|T], L, [H|R]) :- cf_append(T, L, R).

cf_member(X, [X|_]).
cf_member(X, [_|T]) :- cf_member(X, T).

cf_reverse(L, R) :- cf_reverse_(L, [], R).
cf_reverse_([], A, A).
cf_reverse_([H|T], A, R) :- cf_reverse_(T, [H|A], R).

cf_length(L, N) :- cf_length_(L, 0, N).
cf_length_([], N, N).
cf_length_([_|T], N0, N) :- N1 is N0 + 1, cf_length_(T, N1, N).

scan_ws(9). scan_ws(10). scan_ws(11). scan_ws(12). scan_ws(13). scan_ws(32).
scan_ws_run([C|T], R) :- scan_ws(C), !, scan_ws_run(T, R).
scan_ws_run(L, L).

scan_trim(L, Out) :- scan_ws_run(L, L1), scan_trim_trailing(L1, Out).
scan_trim_trailing(L, Out) :-
    cf_reverse(L, R), scan_ws_run(R, R1), cf_reverse(R1, Out).

scan_word_char(C) :- C >= 0'a, C =< 0'z, !.
scan_word_char(C) :- C >= 0'A, C =< 0'Z, !.
scan_word_char(C) :- C >= 0'0, C =< 0'9, !.
scan_word_char(0'_).

scan_digits1([C|T], Rest, [C|Ds]) :-
    C >= 0'0, C =< 0'9,
    scan_digit_run(T, Rest, Ds).
scan_digit_run([C|T], Rest, [C|Ds]) :-
    C >= 0'0, C =< 0'9, !,
    scan_digit_run(T, Rest, Ds).
scan_digit_run(L, L, []).

% Leftmost occurrence of Pat; nondeterministic over later ones.
scan_after(Pat, L, R) :- cf_append(_, T, L), cf_append(Pat, R, T).

scan_replace_all([], _, _, []) :- !.
scan_replace_all(L, Pat, Rep, Out) :-
    ( cf_append(Pat, Rest, L)
      -> cf_append(Rep, Out1, Out), scan_replace_all(Rest, Pat, Rep, Out1)
      ;  L = [C|T], Out = [C|Out1], scan_replace_all(T, Pat, Rep, Out1) ).

% Remove the FIRST occurrence only.
scan_remove_first(L, Pat, Out) :-
    cf_append(Pre, Rest0, L), cf_append(Pat, Rest, Rest0), !,
    cf_append(Pre, Rest, Out).

scan_split_first(L, Sep, Pre, Post) :- cf_append(Pre, [Sep|Post], L), !.

scan_upto_gt([0'>|R], R) :- !.
scan_upto_gt([C|T], R) :- C =\= 0'>, scan_upto_gt(T, R).

scan_upto_nl([], []) :- !.
scan_upto_nl([10|_], []) :- !.
scan_upto_nl([C|T], [C|R]) :- scan_upto_nl(T, R).

scan_upto_nl_or_lt([], []) :- !.
scan_upto_nl_or_lt([10|_], []) :- !.
scan_upto_nl_or_lt([0'<|_], []) :- !.
scan_upto_nl_or_lt([C|T], [C|R]) :- scan_upto_nl_or_lt(T, R).

% ---- HTML ----------------------------------------------------------------

% Split on `<tr` followed by a non-word char — LINEAR scan (an append-based
% leftmost search is quadratic on the 240KB page).
scan_rows(L, Out) :- scan_rows_(L, [], Out).
scan_rows_([], AccR, [Chunk]) :- cf_reverse(AccR, Chunk).
scan_rows_([0'<, 0't, 0'r | R], AccR, [Chunk|Rows]) :-
    \+ ( R = [C|_], scan_word_char(C) ), !,
    cf_reverse(AccR, Chunk),
    scan_rows_(R, [], Rows).
scan_rows_([C|T], AccR, Out) :- scan_rows_(T, [C|AccR], Out).

% <!-- ... --> removal: non-greedy, never across a newline.
scan_strip_comments(L, Out) :-
    ( cf_append([0'<, 0'!, 0'-, 0'-], R1, L), scan_comment_end(R1, R2)
      -> scan_strip_comments(R2, Out)
      ;  ( L = [C|T] -> Out = [C|Out1], scan_strip_comments(T, Out1)
         ; Out = [] ) ).
scan_comment_end([0'-, 0'-, 0'> | R], R) :- !.
scan_comment_end([C|T], R) :- C =\= 10, scan_comment_end(T, R).

% </?[a-zA-Z][^>]*> removal.
scan_strip_tags(L, Out) :-
    ( scan_tag_at(L, R) -> scan_strip_tags(R, Out)
    ; L = [C|T] -> Out = [C|Out1], scan_strip_tags(T, Out1)
    ; Out = [] ).
scan_tag_at([0'< | R0], R) :-
    ( R0 = [0'/ | R1] -> true ; R1 = R0 ),
    R1 = [A|R2], scan_letter(A),
    scan_upto_gt(R2, R).
scan_letter(C) :- C >= 0'a, C =< 0'z, !.
scan_letter(C) :- C >= 0'A, C =< 0'Z.

% &lt; &gt; &quot; &apos; &nbsp; &amp;  (a caller passes the flags it needs).
scan_deent(L0, L) :-
    scan_replace_all(L0, [0'&, 0'l, 0't, 0';], [0'<], L1),
    scan_replace_all(L1, [0'&, 0'g, 0't, 0';], [0'>], L2),
    scan_replace_all(L2, [0'&, 0'q, 0'u, 0'o, 0't, 0';], [0'"], L3),
    scan_replace_all(L3, [0'&, 0'a, 0'p, 0'o, 0's, 0';], [0'''], L4),
    scan_replace_all(L4, [0'&, 0'n, 0'b, 0's, 0'p, 0';], [0' ], L5),
    scan_replace_all(L5, [0'&, 0'a, 0'm, 0'p, 0';], [0'&], L).

% Emit codes as a comma-separated decimal list (the tsv query field).
scan_put_csv(_, []).
scan_put_csv(S, [C]) :- !, number_codes(C, D), cf_put_codes(S, D).
scan_put_csv(S, [C|T]) :-
    number_codes(C, D), cf_put_codes(S, D), put_code(S, 0',),
    scan_put_csv(S, T).

% One line from a text stream (trailing \r stripped); end_of_file at EOF.
scan_read_line(S, L) :-
    get_code(S, C0),
    ( C0 =:= -1 -> L = end_of_file
    ; scan_read_line_(S, C0, L0),
      ( cf_append(L1, [13], L0) -> L = L1 ; L = L0 ) ).
scan_read_line_(_, -1, []) :- !.
scan_read_line_(_, 10, []) :- !.
scan_read_line_(S, C, [C|T]) :- get_code(S, C2), scan_read_line_(S, C2, T).
