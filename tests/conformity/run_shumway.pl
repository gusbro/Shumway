% run_shumway.pl — Shumway driver for the conformity suites.
%
%   cd tests/conformity
%   dotnet run --project ../../src/Shumway.Repl -c Release -- run_shumway.pl
%
% Engine glue: http_download/2 is a Shumway builtin; dif/2 and freeze/2
% come from library(coroutining).

:- use_module(library(coroutining)).

conformity_engine(shumway).
conformity_skips([]).

% format-string adaptation: this engine's format/2,3 accepts an atom.
cf_format(F, A) :- format(F, A).
cf_format(S, F, A) :- format(S, F, A).

conformity_fetch(Url, File) :- http_download(Url, File).

:- consult('html_scan.pl').
:- consult('syntax_suite.pl').
:- consult('number_chars_suite.pl').
:- consult('variable_names_suite.pl').
:- consult('dif_suite.pl').
:- consult('conformity.pl').

:- initialization((conformity_main, halt)).
