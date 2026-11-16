% The helpers a notebook's author calls (ADR-067 D9): what the reference
% offers as jupyter::Name is jupyter:Name here.
%
% A helper that shows something other than text leaves it for the server as
% additional data of the query's answer ('$jupyter_extra'/2).
%
% doc/3 is the one description of each helper: help/0 prints it, and the
% kernel asks for it to show beside the code.

:- module(jupyter, [print_table/1, print_table/2, print_and_save_table/3,
                    print_variable_bindings/0, print_queries/0, print_queries/1,
                    print_query_time/0, pwd/0, cd/1,
                    help/0, magic/0, version/0, version/4, versions/0,
                    '$jupyter_table'/2, '$jupyter_saved_table'/4, '$jupyter_docs'/1]).

:- use_module(jupyter_session).

:- meta_predicate(print_table(0)).
:- meta_predicate(print_and_save_table(0, *, *)).
:- meta_predicate('$jupyter_table'(0, *)).
:- meta_predicate('$jupyter_saved_table'(0, *, *, *)).

% ----- tables -----

print_table(Goal) :-
    term_variables(Goal, Variables),
    lettered(Variables, 0, Names),
    '$jupyter_table'(Goal, Names).

print_table(Rows, Names) :-
    must_be_rows(Rows),
    (   Names == [], Rows = [Row|_] ->
        lettered(Row, 0, Lettered),
        headers(Lettered, Headers)
    ;   Headers = Names
    ),
    rows_texts(Rows, Headers, Texts),
    table(Headers, Texts).

% '$jupyter_table'(:Goal, +Names): the table of Goal's solutions, the
% columns being the variables of Names, a list of Name=Variable.
'$jupyter_table'(Goal, Names) :-
    shown(Names, Headers, Variables),
    findall(Variables, Goal, Rows),
    rows_texts(Rows, Headers, Texts),
    table(Headers, Texts).

table(Headers, Texts) :-
    header_strings(Headers, Strings),
    '$jupyter_extra'(print_table, obj(['ValuesLists'-Texts, 'VariableNames'-Strings])).

print_and_save_table(Goal, Format, File) :-
    term_variables(Goal, Variables),
    lettered(Variables, 0, Names),
    '$jupyter_saved_table'(Goal, Names, Format, File).

% '$jupyter_saved_table'(:Goal, +Names, +Format, +File): as
% '$jupyter_table'/2, and the table goes to File.
'$jupyter_saved_table'(Goal, Names, Format, File) :-
    separator(Format, Separator),
    shown(Names, Headers, Variables),
    findall(Variables, Goal, Rows),
    rows_texts(Rows, Headers, Texts),
    header_strings(Headers, Strings),
    setup_call_cleanup(open(File, write, Stream),
                       write_lines([Strings|Texts], Separator, Stream),
                       close(Stream)),
    '$jupyter_extra'(print_and_save_table,
                     obj(['ValuesLists'-Texts, 'VariableNames'-Strings,
                          'Format'-str(Format), 'File'-str(File)])).

separator(Format, Separator) :-
    (   var(Format) -> throw(error(instantiation_error, print_and_save_table/3))
    ;   Format == csv -> Separator = (',')
    ;   Format == tsv -> Separator = '\t'
    ;   throw(error(domain_error(table_format, Format), print_and_save_table/3))
    ).

write_lines([], _, _).
write_lines([Line|Lines], Separator, Stream) :-
    write_fields(Line, Separator, Stream),
    nl(Stream),
    write_lines(Lines, Separator, Stream).

write_fields([], _, _).
write_fields([str(Text)], Separator, Stream) :-
    !,
    write_field(Text, Separator, Stream).
write_fields([str(Text)|Fields], Separator, Stream) :-
    write_field(Text, Separator, Stream),
    put_char(Stream, Separator),
    write_fields(Fields, Separator, Stream).

% A value with the separator, a quote or a line end in it goes between
% quotes, with its own quotes doubled.
write_field(Text, Separator, Stream) :-
    (   atom(Text) -> atom_chars(Text, Chars) ; Chars = Text ),
    (   member(C, Chars), ( C == Separator ; C == '"' ; C == '\n' ; C == '\r' ) ->
        put_char(Stream, '"'),
        write_quoted(Chars, Stream),
        put_char(Stream, '"')
    ;   format(Stream, "~s", [Chars])
    ).

write_quoted([], _).
write_quoted([C|Cs], Stream) :-
    (   C == '"' -> put_char(Stream, '"'), put_char(Stream, '"')
    ;   put_char(Stream, C)
    ),
    write_quoted(Cs, Stream).

shown([], [], []).
shown([Name=Variable|Names], Headers, Variables) :-
    (   sub_atom(Name, 0, 1, _, '_') -> Headers = Headers1, Variables = Variables1
    ;   Headers = [Name|Headers1], Variables = [Variable|Variables1]
    ),
    shown(Names, Headers1, Variables1).

headers([], []).
headers([Name=_|Names], [Name|Headers]) :- headers(Names, Headers).

header_strings([], []).
header_strings([Header|Headers], [str(Header)|Strings]) :-
    header_strings(Headers, Strings).

% A, B, ... Z, then V26, V27, ...
lettered([], _, []).
lettered([Variable|Variables], N, [Name=Variable|Names]) :-
    (   N < 26 -> Code is 0'A + N, char_code(Name, Code)
    ;   number_chars(N, Digits), atom_chars(Name, ['V'|Digits])
    ),
    N1 is N + 1,
    lettered(Variables, N1, Names).

rows_texts([], _, []).
rows_texts([Row|Rows], Headers, [Texts|More]) :-
    row_texts(Row, Headers, Texts),
    rows_texts(Rows, Headers, More).

% A value still unbound in a solution shows the name of its column.
row_texts([], _, []).
row_texts([Value|Values], Headers, [str(Text)|Texts]) :-
    (   var(Value), Headers = [Header|_] -> atom_chars(Header, Text)
    ;   with_output_to(string(Text), write_term(Value, [quoted(true)]))
    ),
    (   Headers = [_|Rest] -> true ; Rest = [] ),
    row_texts(Values, Rest, Texts).

must_be_rows(Rows) :-
    (   var(Rows) -> throw(error(instantiation_error, print_table/2))
    ;   rows(Rows) -> true
    ;   throw(error(type_error(list_of_lists, Rows), print_table/2))
    ).

rows(Rows) :- Rows == [], !.
rows(Rows) :-
    nonvar(Rows),
    Rows = [Row|More],
    is_list(Row),
    rows(More).

% ----- what the session remembers -----

print_variable_bindings :-
    '$jupyter_values'(Pairs),
    print_values(Pairs).

print_values([]).
print_values([Name-Value|Pairs]) :-
    format("$~w = ", [Name]),
    write_term(Value, [quoted(true)]),
    nl,
    print_values(Pairs).

print_queries :-
    '$jupyter_queries'(Queries),
    print_texts(Queries).

print_queries(Ids) :-
    (   is_list(Ids) -> true
    ;   throw(error(type_error(list, Ids), print_queries/1))
    ),
    '$jupyter_queries'(Queries),
    of_requests(Queries, Ids, Wanted),
    print_texts(Wanted).

of_requests([], _, []).
of_requests([query(Id, Text, Seconds)|Queries], Ids, Wanted) :-
    (   member(Wanted0, Ids), Wanted0 == Id -> Wanted = [query(Id, Text, Seconds)|Rest]
    ;   Wanted = Rest
    ),
    of_requests(Queries, Ids, Rest).

% One query to a line, as a program that runs them all.
print_texts([]).
print_texts([query(_, Text, _)]) :-
    !,
    format("~s.~n", [Text]).
print_texts([query(_, Text, _)|Queries]) :-
    format("~s,~n", [Text]),
    print_texts(Queries).

print_query_time :-
    '$jupyter_queries'(Queries),
    (   last_of(Queries, query(_, Text, Seconds)) ->
        format("Query:   ~s~nRuntime: ~w s~n", [Text, Seconds])
    ;   format("No query has run yet.~n", [])
    ).

last_of([Query], Query) :- !.
last_of([_|Queries], Query) :- last_of(Queries, Query).

% ----- tracing -----

% trace(+Goal): the ports of Goal as it runs. Goal runs once, and trace/1
% succeeds whether Goal did or not, as the reference's does.
% The tracer goes on inside the catch/3 and under a name it does not show:
% the trace is of Goal, not of what runs it.
trace(Goal) :-
    (   catch('$jupyter_traced'(Goal), Ball, ( '$jupyter_tracing'(false), throw(Ball) )) -> true
    ;   true
    ),
    '$jupyter_tracing'(false).

'$jupyter_traced'(Goal) :-
    '$jupyter_tracing'(true),
    user:Goal.

% ----- completion -----

% update_completion_data: the names a kernel completes with, as additional
% data of the answer. Each is a head with its arguments lettered: the
% helpers of this module, the predicates the engine documents and the ones
% the session defined.
update_completion_data :-
    findall(jupyter:Name/Arity, doc(Name/Arity, _, _), Helpers),
    '$jupyter_documented'(Documented),
    findall(Name/Arity, session_predicate(Name, Arity), Defined),
    append(Documented, Defined, Unsorted),
    sort(Unsorted, Indicators),
    append(Helpers, Indicators, All),
    head_texts(All, Texts),
    '$jupyter_extra'(predicate_atoms, Texts).

% The engine lists under user the predicates of every loaded module, the
% private ones too, and has no property that tells them apart. What another
% module has is left out, and with it that module's exports.
session_predicate(Name, Arity) :-
    current_predicate(user:Name/Arity),
    \+ sub_atom(Name, 0, 1, _, '$'),
    \+ ( current_module(Module), Module \== user, current_predicate(Module:Name/Arity) ).

head_texts([], []).
head_texts([Indicator|Indicators], [str(Text)|Texts]) :-
    head(Indicator, Head, Arguments),
    lettered(Arguments, 0, Names),
    with_output_to(string(Text), write_term(Head, [quoted(true), variable_names(Names)])),
    head_texts(Indicators, Texts).

head(Indicator, Head, Arguments) :-
    (   Indicator = Module:Name/Arity -> Head = Module:Plain
    ;   Indicator = Name/Arity, Head = Plain
    ),
    functor(Plain, Name, Arity),
    Plain =.. [_|Arguments].

% ----- the session's directory -----

pwd :-
    working_directory(Directory, Directory),
    format("Working directory: ~q~n", [Directory]).

cd(Directory) :-
    working_directory(_, Directory).

% ----- versions -----

version :-
    version(Major, Minor, Patch, _),
    format("Shumway notebook server ~w.~w.~w~n", [Major, Minor, Patch]).

% The server is part of the engine: its version is the engine's, and Status
% is the commit the engine was built from, or release.
version(Major, Minor, Patch, Status) :-
    current_prolog_flag(version_data, Data),
    Data =.. [_, Major, Minor, Patch, Details],
    (   member(git-Commit, Details) -> Status = Commit
    ;   Status = release
    ).

versions :-
    version(Major, Minor, Patch, _),
    format("Shumway Prolog ~w.~w.~w~n", [Major, Minor, Patch]),
    version.

% ----- help -----

help :-
    findall(Text, doc_text(_, Text), Texts),
    print_docs(Texts).

print_docs([]).
print_docs([Text]) :-
    !,
    format("~s~n", [Text]).
print_docs([Text|Texts]) :-
    format("~s~n~n--------------------------------------------------------------------------------~n~n", [Text]),
    print_docs(Texts).

% '$jupyter_docs'(-Pairs): Key-Text for each helper, Key being
% 'jupyter:Name/Arity'.
'$jupyter_docs'(Pairs) :-
    findall(Key-Text, ( doc_text(Name/Arity, Text), doc_key(Name, Arity, Key) ), Pairs).

doc_key(Name, Arity, Key) :-
    with_output_to(string(Chars), format("jupyter:~w/~w", [Name, Arity])),
    atom_chars(Key, Chars).

doc_text(Indicator, Text) :-
    doc(Indicator, Usage, Lines),
    with_output_to(string(Text), ( format("~w~n", [Usage]), print_indented(Lines) )).

print_indented([]).
print_indented([Line|Lines]) :-
    format("~n    ~w", [Line]),
    print_indented(Lines).

doc(halt/0, 'jupyter:halt or halt', [
    'Stops the session.',
    'The next cell starts a new one, with nothing of what this one defined.',
    'Must be the whole query.']).
doc(retry/0, 'jupyter:retry or retry', [
    'Asks the latest query that can still give one for its next solution.',
    'Must be the whole query.']).
doc(help/0, 'jupyter:help', [
    'Prints this description of the predicates of module jupyter.']).
doc(magic/0, 'jupyter:magic', [
    'Prints the description of the cell and line magics.']).
doc(version/0, 'jupyter:version', [
    'Prints the version of the notebook server.']).
doc(version/4, 'jupyter:version(-Major, -Minor, -Patch, -Status)', [
    'The version of the notebook server, which is the engine''s.',
    'Status is the commit the engine was built from, or release.']).
doc(versions/0, 'jupyter:versions', [
    'Prints the versions of the engine and of the notebook server.']).
doc(pwd/0, 'jupyter:pwd', [
    'Prints the working directory of the session.']).
doc(cd/1, 'jupyter:cd(+Directory)', [
    'Changes the working directory of the session.']).
doc(trace/1, 'jupyter:trace(+Goal)', [
    'Prints the calls, exits, retries and failures of Goal as it runs.',
    'Goal runs once, and the query goes on whether it succeeded or failed.']).
doc(update_completion_data/0, 'jupyter:update_completion_data', [
    'Gives the kernel the names it completes with.',
    'Call it after loading code, to complete the names of its predicates.']).
doc(print_variable_bindings/0, 'jupyter:print_variable_bindings', [
    'Prints the latest value each variable of an earlier query was given.',
    'In a query, $Var stands for the latest value of Var.']).
doc(print_queries/0, 'jupyter:print_queries or print_queries', [
    'Prints the queries that had a solution, in the order they ran.']).
doc(print_queries/1, 'jupyter:print_queries(+Ids) or print_queries(+Ids)', [
    'Prints the queries of the cells whose execution counts are in Ids.']).
doc(print_query_time/0, 'jupyter:print_query_time', [
    'Prints the latest query that had a solution and the CPU time it took.']).
doc(print_table/1, 'jupyter:print_table(+Goal) or print_table(+Goal)', [
    'Shows a table with a row for each solution of Goal and a column for',
    'each of its variables. A variable whose name starts with an underscore',
    'gets no column. Called from a program, where variables have no names,',
    'the columns are named A, B and so on.']).
doc(print_table/2, 'jupyter:print_table(+Rows, +Names)', [
    'Shows a table of Rows, a list of lists of values, under the column',
    'names of Names. An empty Names names the columns A, B and so on.']).
doc(print_and_save_table/3, 'jupyter:print_and_save_table(+Goal, +Format, +File) or print_and_save_table(+Goal, +Format, +File)', [
    'As print_table/1, and writes the table to File as well: a line for the',
    'column names and one for each row. Format is csv, with commas between',
    'the values, or tsv, with tabs.']).

magic :-
    forall(magic_line(Line), format("~w~n", [Line])).

magic_line('Cell magic, on the first line of a cell:').
magic_line('').
magic_line('    %%user').
magic_line('        Writes the rest of the cell to user.pl and loads it').
magic_line('    %%user+').
magic_line('        Appends the rest of the cell to user.pl and loads it').
magic_line('    %%file FILE').
magic_line('        Writes the rest of the cell to FILE and loads it').
magic_line('    %%file+ FILE').
magic_line('        Appends the rest of the cell to FILE and loads it').
magic_line('    %%load FILE').
magic_line('        The same as %%file FILE').
magic_line('    %%save FILE').
magic_line('        Writes the rest of the cell to FILE').
magic_line('').
magic_line('    %%table').
magic_line('        Shows each query of the cell as the table of its solutions').
magic_line('    %%csv FILE').
magic_line('        As %%table, and writes the table to FILE with commas between the values').
magic_line('    %%tsv FILE').
magic_line('        As %%table, and writes the table to FILE with tabs between the values').
magic_line('').
magic_line('    %%highlight').
magic_line('        Shows the cell as code, without running it').
magic_line('').
magic_line('Line magic, as the whole cell:').
magic_line('').
magic_line('    %bindings').
magic_line('        Prints the latest value of each variable of earlier queries').
magic_line('    %queries').
magic_line('        Prints the queries that had a solution').
magic_line('    %pwd').
magic_line('        Prints the working directory of the session').
magic_line('    %help').
magic_line('        Prints the description of the predicates of module jupyter').
magic_line('    %versions').
magic_line('        Prints the versions of the engine and of the notebook server').
magic_line('    %flags').
magic_line('        Shows a table of the Prolog flags and their values').
magic_line('    %magic').
magic_line('        Prints this description').
