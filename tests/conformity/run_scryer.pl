% run_scryer.pl — Scryer Prolog driver for the conformity suites.
%
%   cd tests/conformity
%   scryer-prolog run_scryer.pl
%
% Engine glue: the non-ISO conveniences the common code allows itself
% (format/2,3, member/append/length/reverse via the cf_ helpers — nothing
% needed there) plus Scryer's libraries: format, dif, freeze; the fetch
% copies http_open's stream to a binary file byte by byte.

:- use_module(library(format)).
:- use_module(library(dif)).
:- use_module(library(freeze)).
:- use_module(library('http/http_open')).

conformity_engine(scryer).
conformity_skips([]).

% format-string adaptation: Scryer's format/2,3 wants a LIST, not an atom.
cf_format(F, A) :- atom_chars(F, C), format(C, A).
cf_format(S, F, A) :- atom_chars(F, C), format(S, C, A).

conformity_fetch(Url, File) :-
    http_open(Url, In, []),
    open(File, write, Out, [type(binary)]),
    conformity_copy_bytes(In, Out),
    close(Out),
    close(In).

conformity_copy_bytes(In, Out) :-
    get_char(In, C),
    ( C == end_of_file -> true
    ; char_code(C, Code),
      ( Code =< 255 -> put_byte(Out, Code) ; true ),
      conformity_copy_bytes(In, Out) ).

% Scryer accepts neither consult/1 nor include/1 as a DIRECTIVE — consult
% works as a plain GOAL, so the loading happens inside initialization.
:- initialization((
       consult('html_scan.pl'),
       consult('syntax_suite.pl'),
       consult('number_chars_suite.pl'),
       consult('variable_names_suite.pl'),
       consult('dif_suite.pl'),
       consult('conformity.pl'),
       conformity_main,
       halt
   )).
