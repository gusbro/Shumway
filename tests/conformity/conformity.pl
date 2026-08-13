% conformity.pl — the engine-agnostic MAIN of the Neumerkel conformity
% suites. A per-engine DRIVER (run_shumway.pl, run_gprolog.pl,
% run_scryer.pl, run_swi.pl) loads this file and the suite files, provides
% the two hooks, and calls conformity_main/0.
%
% Driver hooks (see README.md):
%   conformity_engine(-Name)         an atom naming the engine for the report
%   conformity_fetch(+URL, +File)    downloads URL's raw bytes to File
%   conformity_skips(-List)          Suite-Id pairs this engine cannot RUN
%                                    (e.g. [number_chars-46]: the cyclic-list
%                                    test segfaults GNU Prolog — a crash
%                                    cannot be caught, so it is excluded and
%                                    reported as skipped)
%
% Pipeline: fetch the four pages (skipped per page when the file already
% exists in artifacts/ — pre-fetch by hand there if the engine has no HTTP
% glue) -> extract -> generate -> run. Results go to the screen AND to
% artifacts/results.txt.
%
%% entry: conformity_main/0


% ---- pages ---------------------------------------------------------------

cf_base('https://www.complang.tuwien.ac.at/ulrich/iso-prolog/').

cf_page(conformity_testing, 'artifacts/conformity.html').
cf_page(number_chars_cont,  'artifacts/number_chars_cont.html').
cf_page(variable_names,     'artifacts/variable_names.html').
cf_page(dif,                'artifacts/dif.html').

conformity_fetch_pages :-
    cf_base(B),
    cf_forall(cf_page(Name, File), conformity_fetch_page(B, Name, File)).

conformity_fetch_page(_, _, File) :-
    cf_file_exists(File), !.
conformity_fetch_page(B, Name, File) :-
    atom_concat(B, Name, Url),
    cf_format('fetching ~w~n', [Url]),
    catch(conformity_fetch(Url, File), E,
          ( cf_format('  FETCH FAILED (~q) — place the page at ~w by hand~n',
                   [E, File]),
            fail )).

% ---- report: screen + artifacts/results.txt ------------------------------

% Streams are NOT kept in the database (a stream term does not survive an
% assertz/retrieve round trip on every engine): the results file is opened
% in append mode per line. A handful of lines — cost is nil.
cf_report_open :-
    open('artifacts/results.txt', write, S),
    close(S).

cf_report_close.

cf_report(Fmt, Args) :-
    cf_format(Fmt, Args),
    open('artifacts/results.txt', append, S),
    cf_format(S, Fmt, Args),
    close(S).

% key-count pairs from cf_count, one per line (generator statistics).
cf_report_counts(Keys) :-
    cf_count(Keys, Pairs),
    cf_forall(cf_member(K-N, Pairs), cf_format('  ~w: ~w~n', [K, N])).

% Splits a suite's test list into runnable tests and the driver-declared
% skips.
cf_apply_skips(Suite, Ts, Runnable, SkippedIds) :-
    conformity_skips(Skips),
    cf_split_skips(Ts, Suite, Skips, Runnable, SkippedIds).

cf_report_skips([]) :- !.
cf_report_skips(Ids) :-
    cf_report('  skipped on this engine (crash): ~w~n', [Ids]).
cf_split_skips([], _, _, [], []).
cf_split_skips([N-K-G|Ts], Suite, Skips, Run, Skipped) :-
    ( cf_member(Suite-N, Skips)
      -> Run = Run1, Skipped = [N|Skipped1]
      ;  Run = [N-K-G|Run1], Skipped = Skipped1 ),
    cf_split_skips(Ts, Suite, Skips, Run1, Skipped1).

% ---- main ----------------------------------------------------------------

conformity_main :-
    conformity_engine(Engine),
    cf_report_open,
    cf_report('Neumerkel ISO conformity suites — engine: ~w~n', [Engine]),
    ( conformity_fetch_pages
      -> conformity_run_suites
      ;  cf_report('missing pages — nothing run~n', []) ),
    cf_report_close.

conformity_run_suites :-
    syntax_extract,
    syntax_generate,
    nc_extract,
    nc_generate,
    vn_generate,
    dif_generate,
    cf_report('----------------------------------------~n', []),
    syntax_run,
    nc_run,
    vn_run,
    % dif/2 is not ISO: probe for it; without it (GNU Prolog) the suite is
    % reported skipped rather than failed en masse.
    ( catch(( dif(a, b) -> true ; true ), _, fail)
      -> dif_run
      ;  cf_report('dif:             skipped (no dif/2 on this engine)~n', []) ),
    cf_report('----------------------------------------~n', []).
