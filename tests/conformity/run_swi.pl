% run_swi.pl — SWI-Prolog driver for the conformity suites.
%
%   cd tests/conformity
%   swipl run_swi.pl
%
% Engine glue: http_download/2 over library(http/http_open) (binary copy —
% the pages are ISO-8859-1 and must round-trip byte-exactly); the ISO
% default double_quotes=codes (SWI's default is string, which would break
% every "..." test); dif/2 and freeze/2 are builtins.

:- set_prolog_flag(double_quotes, codes).
:- use_module(library(http/http_open)).

conformity_engine(swi).
conformity_skips([]).

% format-string adaptation: this engine's format/2,3 accepts an atom.
cf_format(F, A) :- format(F, A).
cf_format(S, F, A) :- format(S, F, A).

conformity_fetch(Url, File) :-
    setup_call_cleanup(
        http_open(Url, In, []),
        setup_call_cleanup(
            open(File, write, Out, [type(binary)]),
            ( set_stream(In, encoding(octet)),
              copy_stream_data(In, Out) ),
            close(Out)),
        close(In)).

:- consult('html_scan.pl').
:- consult('syntax_suite.pl').
:- consult('number_chars_suite.pl').
:- consult('variable_names_suite.pl').
:- consult('dif_suite.pl').
:- consult('conformity.pl').

:- initialization((conformity_main, halt)).
