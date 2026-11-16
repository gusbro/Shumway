% The helpers a notebook's author calls (ADR-067 D9): what the reference
% offers as jupyter::Name is jupyter:Name here.
%
% A helper that shows something other than text leaves it for the server as
% additional data of the query's answer: a key of the protocol and its JSON
% value. The server takes them when the query has a solution.

:- module(jupyter, [print_table/1, print_table/2, print_and_save_table/3,
                    '$jupyter_table'/2, '$jupyter_saved_table'/4,
                    '$jupyter_extras'/1]).

:- meta_predicate(print_table(0)).
:- meta_predicate(print_and_save_table(0, *, *)).
:- meta_predicate('$jupyter_table'(0, *)).
:- meta_predicate('$jupyter_saved_table'(0, *, *, *)).

%! print_table(:Goal) | Notebook | Shows a table with a row for each solution of Goal and a column for each of its variables. A variable whose name starts with an underscore gets no column. Called from a program, where the variables have no names, the columns are named A, B and so on.
print_table(Goal) :-
    term_variables(Goal, Variables),
    lettered(Variables, 0, Names),
    '$jupyter_table'(Goal, Names).

%! print_table(+Rows, +Names) | Notebook | Shows a table of Rows, a list of lists of values, under the column names of Names. An empty Names names the columns A, B and so on.
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
    extra(print_table, obj(['ValuesLists'-Texts, 'VariableNames'-Strings])).

%! print_and_save_table(:Goal, +Format, +File) | Notebook | As print_table/1, and writes the table to File as well, a line for the column names and one for each row. Format is csv, with commas between the values, or tsv, with tabs.
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
    extra(print_and_save_table,
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

% ----- additional data -----

% extra(+Key, +Json): Key of the answer of the query that is running. A
% later value for the same key replaces the earlier one.
extra(Key, Json) :-
    extras(Pairs),
    without(Pairs, Key, Others),
    nb_setval('$jupyter_extras', [Key-Json|Others]).

extras(Pairs) :-
    catch(nb_getval('$jupyter_extras', Pairs), error(_, _), Pairs = []).

without([], _, []).
without([Key-Json|Pairs], Wanted, Others) :-
    (   Key == Wanted -> Others = Rest
    ;   Others = [Key-Json|Rest]
    ),
    without(Pairs, Wanted, Rest).

% '$jupyter_extras'(-Pairs): the additional data left since the last call,
% first to last, as Key-Json.
'$jupyter_extras'(Pairs) :-
    extras(Latest),
    nb_setval('$jupyter_extras', []),
    reverse(Latest, Pairs).
