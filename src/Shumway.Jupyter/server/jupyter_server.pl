% The notebook server (ADR-067): a JSON-RPC loop over two streams of the
% session, jupyter_requests and jupyter_responses, one message per line.
%
% A cell is a list of queries. A query that succeeded with choice points
% left stays alive under a loop of its own, which reads the requests that
% follow: `retry` fails that loop, and the query gives its next solution.
% The queries alive at any time are a stack, and `retry` reaches the latest.
%
% The cell being answered travels through the loop as an argument:
% cell(Id, Queries, Results), with the queries still to run and the answers
% so far, latest first; none between requests. A retry is the one moment it
% must survive backtracking, and it goes through a global variable then.
% Nothing here is a dynamic predicate: the loop is one query for as long as
% the session lives.
%
% The host gives the session:
%   '$jupyter_attach': the streams jupyter_requests and jupyter_responses;
%   '$jupyter_take_output'(-Chars): what the session wrote since the last
%     call, to standard output or standard error, less one final line end;
%   '$jupyter_open_text'(+Chars, -Stream): an input stream over a text;
%   '$jupyter_error_text'(+Ball, -Chars): the line a top level shows for
%     an error;
%   '$jupyter_strict_syntax'(+New, -Old): whether a clause that does not
%     parse ends the load of its file.

:- module(jupyter_server, [jupyter_serve/0]).

:- use_module(jupyter_json).

%! jupyter_serve | Notebook server | Answers the requests of jupyter_requests on jupyter_responses until the stream ends or a cell calls halt.
jupyter_serve :-
    '$jupyter_attach',
    % Cells are read with the operators of user: $Var needs one there.
    user:op(1, fx, $),
    level(0, 0, none, _).

% level(+Alive, +Spent, +Cell, -Exit)
% One loop of the server. Alive counts the queries alive under it. Spent
% counts the queries that ran in this loop and left no choice point: no loop
% was started for them, and a retry finds each one exhausted.
% Fails when a retry is for the query this loop runs under.
level(Alive, Spent0, Cell0, Exit) :-
    step(Cell0, Alive, Spent0, Spent, Cell, Next),
    (   Next == continue -> level(Alive, Spent, Cell, Exit)
    ;   Next == retry -> fail
    ;   Exit = Next
    ).

% No clause of step/6 may leave a choice point: a retry would stop there.
step(Cell0, Alive, Spent0, Spent, Cell, Next) :-
    (   Cell0 == none ->
        Spent = Spent0,
        read_request(Request),
        answer(Request, Cell, Next)
    ;   Cell0 = cell(Id, [], Results) ->
        Spent = Spent0, Cell = none, Next = continue,
        send_results(Id, Results)
    ;   Cell0 = cell(Id, [Query|Queries], Results),
        run(Query, Alive, Spent0, Spent, cell(Id, Queries, Results), Cell, Next)
    ).

% ----- requests -----

read_request(Request) :-
    catch(jupyter_json_read(jupyter_requests, Request),
          error(syntax_error(json), _),
          Request = malformed).

% answer(+Request, -Cell, -Next)
answer(Request, Cell, Next) :-
    (   Request == end_of_file -> Cell = none, Next = stop
    ;   Request == malformed ->
        Cell = none, Next = continue,
        send_error(null, -32700, 'Invalid JSON was received by the server.', [])
    ;   Request = obj(Pairs), memberchk(method-str(Chars), Pairs) ->
        Next = continue,
        atom_chars(Method, Chars),
        (   memberchk(id-Id, Pairs) -> true ; Id = null ),
        (   memberchk(params-Params, Pairs) -> true ; Params = null ),
        (   known(Method) -> method(Method, Id, Params, Cell)
        ;   Cell = none,
            send_error(Id, -32601, 'The method does not exist / is not available.', Chars)
        )
    ;   Cell = none, Next = continue,
        send_error(null, -32600, 'The JSON sent is not a valid Request object.', [])
    ).

known(call).
known(backend).
known(version).
known(protocol).

% method(+Method, +Id, +Params, -Cell)
% A cell is read whole before any of it runs: a syntax error anywhere is the
% answer to the request.
method(call, Id, Params, Cell) :-
    (   Params = obj(Pairs), memberchk(code-str(Code), Pairs) ->
        catch(cell_content(Code, Content), Error, true),
        (   nonvar(Error) ->
            Cell = none,
            error_text(Error, Text),
            send_error(Id, -4712, 'Exception', Text)
        ;   Content = queries(Queries) -> Cell = cell(Id, Queries, [])
        ;   Cell = none,
            send_result(Id, obj([]))
        )
    ;   Cell = none,
        send_error(Id, -32602, 'Invalid method parameter(s).', [])
    ).
method(backend, Id, _, none) :-
    current_prolog_flag(dialect, Dialect),
    send_result(Id, str(Dialect)).
method(version, Id, _, none) :-
    current_prolog_flag(version_data, Data),
    Data =.. [_, Major, Minor, Patch|_],
    with_output_to(string(Version), format("~w.~w.~w", [Major, Minor, Patch])),
    send_result(Id, str(Version)).
method(protocol, Id, _, none) :-
    send_result(Id, obj([version-1, implementation-str(shumway)])).

% cell_content(+Code, -Content): queries(Queries), or nothing when the
% cell has nothing to run.
cell_content(Code, Content) :-
    (   magic(Code, Magic) -> magic_content(Magic, Content)
    ;   cell_queries(Code, Queries),
        Content = queries(Queries)
    ).

% ----- cell magics -----

% A cell whose first line is %%NAME, or %%NAME ARGUMENT, with a NAME below.
% Any other first line is a comment of an ordinary cell.
magic(Code, Magic) :-
    once(append(['%', '%'|Header], ['\n'|Text], Code)),
    (   append(NameChars, [' '|Rest], Header) -> trimmed(Rest, Argument)
    ;   NameChars = Header, Argument = []
    ),
    atom_chars(Name, NameChars),
    magic(Name, Argument, Text, Magic).

magic(user, [], Text, file('user.pl', write, load, Text)).
magic('user+', [], Text, file('user.pl', append, load, Text)).
magic(file, [C|Cs], Text, file(File, write, load, Text)) :- atom_chars(File, [C|Cs]).
magic('file+', [C|Cs], Text, file(File, append, load, Text)) :- atom_chars(File, [C|Cs]).
magic(load, [C|Cs], Text, file(File, write, load, Text)) :- atom_chars(File, [C|Cs]).
magic(save, [C|Cs], Text, file(File, write, save, Text)) :- atom_chars(File, [C|Cs]).
magic(highlight, [], _, highlight).

% Without the spaces around it.
trimmed(Chars, Trimmed) :-
    leading(Chars, Rest),
    reverse(Rest, Reversed),
    leading(Reversed, ReversedTrimmed),
    reverse(ReversedTrimmed, Trimmed).

leading([C|Cs], Rest) :-
    ( C == ' ' ; C == '\t' ; C == '\r' ),
    !,
    leading(Cs, Rest).
leading(Chars, Chars).

% The text goes to its file. A file that is loaded is a query of the cell:
% its answer carries what loading wrote and the error it raised.
magic_content(highlight, nothing).
magic_content(file(File, Mode, Action, Text), Content) :-
    setup_call_cleanup(open(File, Mode, Stream),
                       format(Stream, "~s~n", [Text]),
                       close(Stream)),
    (   Action == load -> Content = queries([query(jupyter_server:load(File), [])])
    ;   Content = nothing
    ).

% A cell with a clause that does not parse is an error of the cell: nothing
% of it is loaded, where a load from a program reports the clause and goes on.
load(File) :-
    setup_call_cleanup('$jupyter_strict_syntax'(true, Old),
                       user:reconsult(File),
                       '$jupyter_strict_syntax'(Old, _)).

cell_queries(Code, Queries) :-
    catch(text_queries(Code, Read), error(syntax_error(Why), Where), true),
    (   var(Why) -> Queries = Read
    ;   without_full_stop(Code, Stopped) -> Queries = Stopped
    ;   throw(error(syntax_error(Why), Where))
    ).

% A last query may come without its full stop.
without_full_stop(Code, Queries) :-
    append(Code, ['\n', '.'], Stopped),
    catch(text_queries(Stopped, Queries), error(syntax_error(_), _), fail).

text_queries(Code, Queries) :-
    '$jupyter_open_text'(Code, Stream),
    catch(read_queries(Stream, Queries), Error, true),
    close(Stream),
    (   nonvar(Error) -> throw(Error) ; true ).

read_queries(Stream, Queries) :-
    read_term(Stream, Term, [variable_names(Names)]),
    (   Term == end_of_file -> Queries = []
    ;   Queries = [query(Term, Names)|More],
        read_queries(Stream, More)
    ).

% ----- running a query -----

% run(+Query, +Alive, +Spent0, -Spent, +Cell0, -Cell, -Next)
run(query(Goal, Names), Alive, Spent0, Spent, Cell0, Cell, Next) :-
    (   retry_goal(Goal) -> retry(Alive, Spent0, Spent, Cell0, Cell, Next)
    ;   halt_goal(Goal) ->
        Spent = Spent0, Cell = none, Next = stop,
        answered(Cell0, obj([status-str(halt)]), cell(Id, _, Results)),
        send_results(Id, Results)
    ;   catch(with_previous(Goal, Names, Resolved), Error, true),
        (   nonvar(Error) ->
            Spent = Spent0, Next = continue,
            failed(Cell0, -4712, 'Exception', Error, Cell)
        ;   solve(Resolved, Names, Alive, Spent0, Spent, Cell0, Cell, Next)
        )
    ).

retry_goal(Goal) :- Goal == retry.
retry_goal(Goal) :- Goal == jupyter:retry.

halt_goal(Goal) :- Goal == halt.
halt_goal(Goal) :- Goal == jupyter:halt.

retry(Alive, Spent0, Spent, Cell0, Cell, Next) :-
    (   Spent0 > 0 ->
        Spent is Spent0 - 1, Next = continue,
        error_object(-4711, 'Failure', [], [], Error),
        answered(Cell0, obj([status-str(error), error-Error]), Cell)
    ;   Alive =:= 0 ->
        Spent = Spent0, Next = continue,
        error_object(-4713, 'No active call', [], [], Error),
        answered(Cell0, obj([status-str(error), error-Error]), Cell)
    ;   % The query that is retried goes on with this cell.
        Spent = Spent0, Cell = Cell0, Next = retry,
        nb_setval('$jupyter_cell', Cell0)
    ).

% The query's solutions, one for each time control comes back here. After a
% solution the server goes on inside this clause, so the query's choice
% points stay; a retry fails back into them.
solve(Goal, Names, Alive, Spent0, Spent, Cell0, Cell, Next) :-
    output(_),
    nb_setval('$jupyter_cell', none),
    (   catch(solution(Goal, Exit), Error, true),
        resumed(Cell0, Cell1),
        solved(Error, Exit, Names, Alive, Spent0, Spent, Cell1, Cell, Next)
    ->  true
    ;   resumed(Cell0, Cell1),
        output(Output),
        error_object(-4711, 'Failure', [], Output, Failure),
        answered(Cell1, obj([status-str(error), error-Failure]), Cell),
        Spent = Spent0, Next = continue
    ).

% The cell as the retry that came back here left it, or as it was when the
% query started.
resumed(Cell0, Cell) :-
    nb_getval('$jupyter_cell', Left),
    (   Left == none -> Cell = Cell0
    ;   Cell = Left,
        nb_setval('$jupyter_cell', none)
    ).

output(Text) :-
    '$jupyter_take_output'(Text).

% Exit is bound when the goal left no choice point.
solution(Goal, Exit) :-
    setup_call_cleanup(true, user:Goal, Exit = last).

solved(Error, Exit, Names, Alive, Spent0, Spent, Cell0, Cell, Next) :-
    (   nonvar(Error) ->
        Spent = Spent0, Next = continue,
        failed(Cell0, -4712, 'Exception', Error, Cell)
    ;   output(Output),
        bindings(Names, Bindings),
        answered(Cell0, obj([status-str(success), type-str(query),
                             bindings-obj(Bindings), output-str(Output)]), Cell1),
        remember(Names),
        (   Exit == last -> Spent is Spent0 + 1, Cell = Cell1, Next = continue
        ;   Spent = Spent0, Cell = none,
            Deeper is Alive + 1,
            level(Deeper, 0, Cell1, Next)
        )
    ).

failed(Cell0, Code, Message, Ball, Cell) :-
    output(Output),
    error_text(Ball, Text),
    error_object(Code, Message, Text, Output, Error),
    answered(Cell0, obj([status-str(error), error-Error]), Cell).

answered(cell(Id, Queries, Results), Result, cell(Id, Queries, [Result|Results])).

% ----- bindings -----

% A variable of the query is shown with its value. One left unbound is shown
% only when it is the same variable as an earlier one, as Name = Earlier.
bindings(Names, Bindings) :-
    bindings(Names, Names, [], Bindings).

bindings([], _, _, []).
bindings([Name=Value|More], Names, Seen, Bindings) :-
    (   hidden(Name) -> Bindings = Rest
    ;   var(Value) ->
        (   named(Seen, Value, Earlier) -> Bindings = [Name-str(Earlier)|Rest]
        ;   Bindings = Rest
        )
    ;   with_output_to(string(Text), write_term(Value, [quoted(true), variable_names(Names)])),
        Bindings = [Name-str(Text)|Rest]
    ),
    bindings(More, Names, [Name=Value|Seen], Rest).

hidden(Name) :- sub_atom(Name, 0, 1, _, '_').

% The first name the variable had in the query.
named([Name=Variable|Earlier], Value, Found) :-
    (   named(Earlier, Value, Found) -> true
    ;   Variable == Value -> Found = Name
    ).

% The value a query gave a variable is kept under a global variable of the
% engine, one for each name.
remember([]).
remember([Name=Value|Names]) :-
    (   nonvar(Value), \+ hidden(Name) ->
        value_key(Name, Key),
        nb_setval(Key, Value)
    ;   true
    ),
    remember(Names).

value_key(Name, Key) :-
    atom_concat('$jupyter_value ', Name, Key).

% $Var stands for the latest value an earlier query gave Var. When the query
% has no other use of Var, Var itself takes that value, and so is shown.
with_previous(Goal, Names, Resolved) :-
    replace(Goal, Names, Resolved, [], Uses),
    own_variables(Uses, Resolved).

replace(Term, Names, Resolved, Uses0, Uses) :-
    (   var(Term) -> Resolved = Term, Uses = Uses0
    ;   Term = $(Variable), var(Variable) ->
        (   used(Uses0, Variable, Value) -> Uses = Uses0
        ;   previous_value(Variable, Names, Value),
            Uses = [Variable-Value|Uses0]
        ),
        Resolved = Value
    ;   compound(Term) ->
        Term =.. [Name|Arguments],
        replace_all(Arguments, Names, ResolvedArguments, Uses0, Uses),
        Resolved =.. [Name|ResolvedArguments]
    ;   Resolved = Term, Uses = Uses0
    ).

replace_all([], _, [], Uses, Uses).
replace_all([Term|Terms], Names, [Resolved|ResolvedTerms], Uses0, Uses) :-
    replace(Term, Names, Resolved, Uses0, Uses1),
    replace_all(Terms, Names, ResolvedTerms, Uses1, Uses).

used([Variable-Value|Uses], Wanted, Found) :-
    (   Variable == Wanted -> Found = Value
    ;   used(Uses, Wanted, Found)
    ).

previous_value(Variable, Names, Value) :-
    name_of(Names, Variable, Name),
    value_key(Name, Key),
    (   catch(nb_getval(Key, Value), error(_, _), fail) -> true
    ;   throw(jupyter(no_previous_value(Name)))
    ).

name_of([Name=Variable|Names], Wanted, Found) :-
    (   Variable == Wanted -> Found = Name
    ;   name_of(Names, Wanted, Found)
    ).

own_variables([], _).
own_variables([Variable-Value|Uses], Resolved) :-
    term_variables(Resolved, Variables),
    (   occurs(Variables, Variable) -> true
    ;   Variable = Value
    ),
    own_variables(Uses, Resolved).

occurs([Variable|Variables], Wanted) :-
    (   Variable == Wanted -> true
    ;   occurs(Variables, Wanted)
    ).

% ----- responses -----

% send_results(+Id, +Results): the answers of a cell, latest first.
send_results(Id, Results) :-
    (   Results == [] -> send_result(Id, str([]))
    ;   reverse(Results, InOrder),
        keyed(InOrder, 1, Pairs),
        send_result(Id, obj(Pairs))
    ).

keyed([], _, []).
keyed([Result|Results], N, [Key-Result|Pairs]) :-
    number_chars(N, Digits),
    atom_chars(Key, Digits),
    N1 is N + 1,
    keyed(Results, N1, Pairs).

send_result(Id, Result) :-
    jupyter_json_write(jupyter_responses,
                       obj([jsonrpc-str('2.0'), id-Id, result-Result])).

send_error(Id, Code, Message, Text) :-
    error_object(Code, Message, Text, [], Error),
    jupyter_json_write(jupyter_responses,
                       obj([jsonrpc-str('2.0'), id-Id, error-Error])).

error_object(Code, Message, Text, Output, obj([code-Code, message-str(Message), data-obj(Data)])) :-
    (   Output == [] -> Data = [prolog_message-str(Text)]
    ;   Data = [prolog_message-str(Text), output-str(Output)]
    ).

% The text of an error, as the top-level prints it.
error_text(Ball, Text) :-
    (   Ball = jupyter(no_previous_value(Name)) ->
        with_output_to(string(Text), format("$~w has no value from an earlier query", [Name]))
    ;   '$jupyter_error_text'(Ball, Text)
    ).
