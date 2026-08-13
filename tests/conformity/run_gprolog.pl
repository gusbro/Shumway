% run_gprolog.pl — GNU Prolog driver for the conformity suites.
%
%   cd tests/conformity
%   gprolog --consult-file run_gprolog.pl
%
% (On Windows gprolog is a GUI-subsystem binary: run it from a real console;
% piping its stdio can pop the GUI top level.)
%
% Engine glue: GNU Prolog has no HTTP library — the fetch shells out to
% curl via system/1 (or pre-fetch the pages into artifacts/ by hand; the
% fetch stage skips files that already exist). dif/2 does not exist here:
% the orchestrator probes for it and reports that suite as skipped.

conformity_engine(gprolog).
% number_chars #46 walks a CYCLIC char list — GNU Prolog segfaults on it
% (an uncatchable crash), so it is excluded rather than run.
conformity_skips([number_chars-46]).

% format-string adaptation: this engine's format/2,3 accepts an atom.
cf_format(F, A) :- format(F, A).
cf_format(S, F, A) :- format(S, F, A).

conformity_fetch(Url, File) :-
    atom_concat('curl -s -o ', File, C1),
    atom_concat(C1, ' ', C2),
    atom_concat(C2, Url, Cmd),
    system(Cmd).

% GNU Prolog takes no consult/1 DIRECTIVE — include/1 (ISO §7.4.2.7) does
% the textual job.
:- include('html_scan.pl').
:- include('syntax_suite.pl').
:- include('number_chars_suite.pl').
:- include('variable_names_suite.pl').
:- include('dif_suite.pl').
:- include('conformity.pl').

:- initialization((conformity_main, halt)).
