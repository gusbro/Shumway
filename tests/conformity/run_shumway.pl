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

% Bounded run for tests whose sanctioned outcomes include `loops`:
% timeout at Ms IS the loops outcome.
conformity_timed_call(G, Ms, O) :-
    catch( ( time_out(G, Ms, R)
             -> ( R == time_out -> O = timeout ; O = succeeds )
             ;  O = fails ),
           Err, cf_error_class(Err, O) ).

% CONFORMITY_DEEP=1: loops-or-resource tests run UNBOUNDED and must end
% in the resource_error. With this engine's default unlimited heap that
% means filling RAM — constrain the process first for a quick proof,
% e.g. DOTNET_GCHeapHardLimit=0x20000000 (512 MB).
conformity_deep :- catch(getenv('CONFORMITY_DEEP', V), _, fail), V == '1'.

% format-string adaptation: this engine's format/2,3 accepts an atom.
cf_format(F, A) :- format(F, A).
cf_format(S, F, A) :- format(S, F, A).

conformity_fetch(Url, File) :- http_download(Url, File).

:- consult('html_scan.pl').
:- consult('syntax_suite.pl').
:- consult('number_chars_suite.pl').
:- consult('variable_names_suite.pl').
:- consult('dif_suite.pl').
:- consult('quad_suites.pl').
:- consult('cleanup_suite.pl').
:- consult('conformity.pl').

:- initialization((conformity_main, halt)).
