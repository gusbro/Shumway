% JSON for the notebook server (ADR-067 D8): one value per line, read from
% and written to a stream.
%
% A JSON value as a term:
%
%   object   obj(Pairs): a list of Key-Value, Key an atom
%   array    a list
%   string   str(Text)
%   number   a number
%   true, false, null
%
% A string is always wrapped. The atom [] is the empty list, so a bare
% atom for a string would turn the text "[]" into an empty array.
%
% Text is a list of characters when read. When written it is a list of
% characters, a number, or an atom for a constant of the program. Text that
% was computed is passed as characters: the atom [] writes as empty text.

:- module(jupyter_json, [jupyter_json_read/2, jupyter_json_write/2]).

%! jupyter_json_read(+Stream, -Value) | Notebook server | Reads a line of Stream and unifies Value with the JSON value it holds, or with end_of_file at the end of the stream. A line that is not one JSON value is a syntax error, and the line is consumed.
jupyter_json_read(Stream, Value) :-
    read_line(Stream, Line),
    (   Line == end_of_file -> Value = end_of_file
    ;   phrase(document(Read), Line) -> Value = Read
    ;   throw(error(syntax_error(json), jupyter_json_read/2))
    ).

read_line(Stream, Line) :-
    get_char(Stream, C),
    (   C == end_of_file -> Line = end_of_file
    ;   line_rest(C, Stream, Line)
    ).

line_rest(end_of_file, _, []) :- !.
line_rest('\n', _, []) :- !.
line_rest(C, Stream, [C|Cs]) :-
    get_char(Stream, Next),
    line_rest(Next, Stream, Cs).

space(' ').
space('\t').
space('\r').
space('\n').

document(Value) --> spaces, value(Value), spaces.

value(str(Text)) --> ['"'], !, text(Text).
value(obj(Pairs)) --> ['{'], !, spaces, members(Pairs).
value(Items) --> ['['], !, spaces, items(Items).
value(true) --> [t, r, u, e], !.
value(false) --> [f, a, l, s, e], !.
value(null) --> [n, u, l, l], !.
value(Number) --> numeral(Chars), { Chars = [_|_], numeral_number(Chars, Number) }.

members([]) --> ['}'], !.
members([Key-Value|Pairs]) --> member(Key, Value), more_members(Pairs).

more_members([]) --> ['}'], !.
more_members([Key-Value|Pairs]) --> [','], spaces, member(Key, Value), more_members(Pairs).

member(Key, Value) -->
    ['"'], text(Chars), { atom_chars(Key, Chars) },
    spaces, [':'], spaces, value(Value), spaces.

items([]) --> [']'], !.
items([Value|Values]) --> value(Value), spaces, more_items(Values).

more_items([]) --> [']'], !.
more_items([Value|Values]) --> [','], spaces, value(Value), spaces, more_items(Values).

spaces --> [C], { space(C) }, !, spaces.
spaces --> [].

% The characters of a string, after its opening quote and through its
% closing one.
text([]) --> ['"'], !.
text([C|Cs]) --> ['\\'], !, escaped(C), text(Cs).
text([C|Cs]) --> [C], text(Cs).

escaped('"') --> ['"'], !.
escaped('\\') --> ['\\'], !.
escaped('/') --> ['/'], !.
escaped('\b') --> [b], !.
escaped('\f') --> [f], !.
escaped('\n') --> [n], !.
escaped('\r') --> [r], !.
escaped('\t') --> [t], !.
escaped(C) --> [u], hex4(High), low_surrogate(High, Code), { char_code(C, Code) }.

% A code point above the basic plane travels as two escapes. Half of a pair
% on its own is not a character: it reads as U+FFFD.
low_surrogate(High, Code) -->
    { High >= 0xD800, High =< 0xDBFF },
    ['\\', u], hex4(Low),
    { Low >= 0xDC00, Low =< 0xDFFF },
    !,
    { Code is 0x10000 + (High - 0xD800) * 0x400 + (Low - 0xDC00) }.
low_surrogate(Half, 0xFFFD) --> { Half >= 0xD800, Half =< 0xDFFF }, !.
low_surrogate(Code, Code) --> [].

hex4(Value) -->
    hex(A), hex(B), hex(C), hex(D),
    { Value is ((A * 16 + B) * 16 + C) * 16 + D }.

hex(Value) --> [C], { hex_value(C, Value) }.

hex_value(C, Value) :-
    char_code(C, Code),
    (   Code >= 0'0, Code =< 0'9 -> Value is Code - 0'0
    ;   Code >= 0'a, Code =< 0'f -> Value is Code - 0'a + 10
    ;   Code >= 0'A, Code =< 0'F -> Value is Code - 0'A + 10
    ).

numeral([C|Cs]) --> [C], { numeral_char(C) }, !, numeral(Cs).
numeral([]) --> [].

numeral_char(C) :- char_code(C, Code), Code >= 0'0, Code =< 0'9, !.
numeral_char('-').
numeral_char('+').
numeral_char('.').
numeral_char(e).
numeral_char('E').

% JSON writes 1e5 where Prolog wants 1.0e5.
numeral_number(Chars, Number) :-
    catch(number_chars(Read, Chars), error(_, _), fail),
    !,
    Number = Read.
numeral_number(Chars, Number) :-
    append(Mantissa, [E|Exponent], Chars),
    ( E == e ; E == 'E' ),
    \+ memberchk('.', Mantissa),
    !,
    append(Mantissa, ['.', '0', e|Exponent], Dotted),
    catch(number_chars(Number, Dotted), error(_, _), fail).

%! jupyter_json_write(+Stream, +Value) | Notebook server | Writes Value to Stream as one line of JSON and flushes the stream. A term that is not a JSON value is a type error.
% The line is built first: a value that cannot be written leaves nothing on
% the stream.
jupyter_json_write(Stream, Value) :-
    with_output_to(string(Line), write_value(Value)),
    format(Stream, "~s~n", [Line]),
    flush_output(Stream).

write_value(Value) :-
    var(Value),
    !,
    throw(error(instantiation_error, jupyter_json_write/2)).
write_value(str(Text)) :-
    !,
    text_chars(Text, Chars),
    put_char('"'),
    write_text(Chars),
    put_char('"').
write_value(obj(Pairs)) :-
    !,
    put_char('{'),
    write_members(Pairs),
    put_char('}').
write_value([]) :-
    !,
    put_char('['),
    put_char(']').
write_value([Value|Values]) :-
    !,
    put_char('['),
    write_value(Value),
    write_more_items(Values),
    put_char(']').
write_value(Number) :-
    number(Number),
    !,
    write(Number).
write_value(Literal) :-
    ( Literal == true ; Literal == false ; Literal == null ),
    !,
    write(Literal).
write_value(Value) :-
    throw(error(type_error(json, Value), jupyter_json_write/2)).

write_members([]).
write_members([Key-Value|Pairs]) :-
    write_member(Key, Value),
    write_more_members(Pairs).

write_more_members([]).
write_more_members([Key-Value|Pairs]) :-
    put_char(','),
    write_member(Key, Value),
    write_more_members(Pairs).

write_member(Key, Value) :-
    write_value(str(Key)),
    put_char(':'),
    write_value(Value).

write_more_items([]).
write_more_items([Value|Values]) :-
    put_char(','),
    write_value(Value),
    write_more_items(Values).

text_chars(Text, Chars) :-
    (   var(Text) -> throw(error(instantiation_error, jupyter_json_write/2))
    ;   Text == [] -> Chars = []
    ;   Text = [_|_] -> Chars = Text
    ;   atom(Text) -> atom_chars(Text, Chars)
    ;   number(Text) -> number_chars(Text, Chars)
    ;   throw(error(type_error(text, Text), jupyter_json_write/2))
    ).

write_text([]).
write_text([C|Cs]) :-
    write_char(C),
    write_text(Cs).

write_char('"') :- !, put_char('\\'), put_char('"').
write_char('\\') :- !, put_char('\\'), put_char('\\').
write_char('\n') :- !, put_char('\\'), put_char(n).
write_char('\r') :- !, put_char('\\'), put_char(r).
write_char('\t') :- !, put_char('\\'), put_char(t).
write_char(C) :-
    char_code(C, Code),
    (   Code < 0x20 -> write_control(Code)
    ;   put_char(C)
    ).

write_control(Code) :-
    High is Code // 16,
    Low is Code mod 16,
    put_char('\\'),
    put_char(u),
    put_char('0'),
    put_char('0'),
    hex_digit(High, H), put_char(H),
    hex_digit(Low, L), put_char(L).

hex_digit(Value, Digit) :-
    (   Value < 10 -> Code is 0'0 + Value
    ;   Code is 0'a + Value - 10
    ),
    char_code(Digit, Code).
