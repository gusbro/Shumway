% What a notebook session remembers between queries (ADR-067): the latest
% value each variable was given, the queries that ran, and the additional
% data a helper leaves for the answer of the query that is running.
%
% All of it lives in global variables of the engine. The server's loop is
% one query for as long as the session lives, and a dynamic predicate that
% is read and changed at every step of it costs far more than a global.
% Each entry has a key of its own, so remembering one more copies only that
% one.

:- module(jupyter_session, ['$jupyter_remember'/2, '$jupyter_value'/2, '$jupyter_values'/1,
                            '$jupyter_ran'/3, '$jupyter_queries'/1,
                            '$jupyter_extra'/2, '$jupyter_extra_item'/2, '$jupyter_extras'/1]).

global(Key, Value, Default) :-
    catch(nb_getval(Key, Value), error(_, _), Value = Default).

% ----- values -----

% '$jupyter_remember'(+Name, +Value): Value is the latest value of the
% variable Name.
'$jupyter_remember'(Name, Value) :-
    value_key(Name, Key),
    (   catch(nb_getval(Key, _), error(_, _), fail) -> true
    ;   global('$jupyter_names', Names, []),
        nb_setval('$jupyter_names', [Name|Names])
    ),
    nb_setval(Key, Value).

% '$jupyter_value'(+Name, -Value): fails when Name was never given one.
'$jupyter_value'(Name, Value) :-
    value_key(Name, Key),
    catch(nb_getval(Key, Value), error(_, _), fail).

% '$jupyter_values'(-Pairs): Name-Value for every variable, in the order
% they were first given a value.
'$jupyter_values'(Pairs) :-
    global('$jupyter_names', Latest, []),
    reverse(Latest, Names),
    values(Names, Pairs).

values([], []).
values([Name|Names], [Name-Value|Pairs]) :-
    '$jupyter_value'(Name, Value),
    values(Names, Pairs).

value_key(Name, Key) :-
    atom_concat('$jupyter_value ', Name, Key).

% ----- queries -----

% '$jupyter_ran'(+Id, +Text, +Seconds): a query of the request Id, with the
% text it was written as, had a solution after Seconds.
'$jupyter_ran'(Id, Text, Seconds) :-
    global('$jupyter_ran', Count, 0),
    Number is Count + 1,
    query_key(Number, Key),
    nb_setval(Key, query(Id, Text, Seconds)),
    nb_setval('$jupyter_ran', Number).

% '$jupyter_queries'(-Queries): query(Id, Text, Seconds) for each, first to
% last.
'$jupyter_queries'(Queries) :-
    global('$jupyter_ran', Count, 0),
    queries(1, Count, Queries).

queries(Number, Count, Queries) :-
    (   Number > Count -> Queries = []
    ;   query_key(Number, Key),
        nb_getval(Key, Query),
        Queries = [Query|More],
        Next is Number + 1,
        queries(Next, Count, More)
    ).

query_key(Number, Key) :-
    number_chars(Number, Digits),
    atom_chars(Key, ['$', j, u, p, y, t, e, r, '_', q, u, e, r, y, ' '|Digits]).

% ----- additional data -----

% '$jupyter_extra'(+Key, +Json): Key of the answer of the query that is
% running. A later value for the same key replaces the earlier one.
'$jupyter_extra'(Key, Json) :-
    global('$jupyter_extras', Pairs, []),
    without(Pairs, Key, Others),
    nb_setval('$jupyter_extras', [Key-Json|Others]).

% '$jupyter_extra_item'(+Key, +Json): one more item of Key, a list, in the
% answer of the query that is running. The items stay in the order given.
'$jupyter_extra_item'(Key, Json) :-
    global('$jupyter_extras', Pairs, []),
    (   memberchk(Key-Items, Pairs) -> append(Items, [Json], All)
    ;   All = [Json]
    ),
    without(Pairs, Key, Others),
    nb_setval('$jupyter_extras', [Key-All|Others]).

without([], _, []).
without([Key-Json|Pairs], Wanted, Others) :-
    (   Key == Wanted -> Others = Rest
    ;   Others = [Key-Json|Rest]
    ),
    without(Pairs, Wanted, Rest).

% '$jupyter_extras'(-Pairs): the additional data left since the last call,
% first to last, as Key-Json.
'$jupyter_extras'(Pairs) :-
    global('$jupyter_extras', Latest, []),
    nb_setval('$jupyter_extras', []),
    reverse(Latest, Pairs).
