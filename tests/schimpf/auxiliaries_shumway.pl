% Auxiliary predicates the Schimpf suite assumes (see its README) —
% Shumway definitions. The suite's own auxiliaries.pl is ECLiPSe-oriented
% and is not loaded; this file replaces it on the command line.

% compile/load a source file
iso_test_ensure_loaded(File) :-
    ensure_loaded(File).

% return operating system name as 'win' or 'unix'
iso_test_os(OS) :-
    (   catch(getenv('OS', V), _, fail), V == 'Windows_NT'
    ->  OS = win
    ;   OS = unix
    ).

% create a non-repositionable I/O stream
iso_test_non_repositionable_stream(S) :-
    current_input(S).

% test whether two terms are variants of each other
iso_test_variant(X, Y) :-
    subsumes_term(X, Y),
    subsumes_term(Y, X).

% test whether two lists have the same set of members
iso_test_same_members(Xs, Ys) :-
    sort(Xs, SXs),
    sort(Ys, SYs),
    SXs == SYs.
