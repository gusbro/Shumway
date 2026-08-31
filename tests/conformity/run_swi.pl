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
% length #30: the freeze-driven infinite list drives this engine into an
% uninterruptible kernel loop (a cyclic unification never reaches the
% safe point call_with_time_limit's alarm needs), so not even the timed
% call bounds it.
conformity_skips([length-30]).

% loops-sanctioned tests run bounded: call_with_time_limit's
% time_limit_exceeded IS the loops outcome.
conformity_timed_call(G, Ms, O) :-
    S is Ms / 1000,
    catch( ( call_with_time_limit(S, G) -> O = succeeds ; O = fails ),
           E,
           ( E == time_limit_exceeded -> O = timeout
           ; cf_error_class(E, O) ) ).

% CONFORMITY_DEEP=1: loops-or-resource tests run unbounded and must end
% in the resource error.
conformity_deep :- catch(getenv('CONFORMITY_DEEP', V), _, fail), V == '1'.

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
:- consult('quad_suites.pl').
:- consult('cleanup_suite.pl').
:- consult('conformity.pl').

:- initialization((conformity_main, halt)).
