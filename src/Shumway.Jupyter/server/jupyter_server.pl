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
%   '$jupyter_answer_text'(+Term, +Names, +Priority, -Chars): Term as a top
%     level writes it in an answer, its variables named by Names;
%   '$jupyter_strict_syntax'(+New, -Old): whether a clause that does not
%     parse ends the load of its file.

:- module(jupyter_server, [jupyter_serve/0]).

:- use_module(jupyter_json).
:- use_module(jupyter_session).
:- use_module(jupyter).

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
known(jupyter_predicate_docs).
known(enable_logging).

% method(+Method, +Id, +Params, -Cell)
% A cell is read whole before any of it runs: a syntax error anywhere is the
% answer to the request. The page's library collections are put on the search
% path first, those another tab imported since the last cell included.
method(call, Id, Params, Cell) :-
    '$jupyter_refresh_libraries',
    (   Params = obj(Pairs), memberchk(code-str(Code), Pairs) ->
        user_file(Pairs, UserFile),
        catch(cell_content(Code, UserFile, Content), Error, true),
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
method(jupyter_predicate_docs, Id, _, none) :-
    '$jupyter_docs'(Docs),
    doc_strings(Docs, Pairs),
    send_result(Id, obj(Pairs)).
% The answer is whether a log file was created: this server keeps none.
method(enable_logging, Id, _, none) :-
    send_result(Id, false).

doc_strings([], []).
doc_strings([Key-Text|Docs], [Key-str(Text)|Pairs]) :-
    doc_strings(Docs, Pairs).

% The file of %%user: NAME.user.pl for the notebook NAME.ipynb the kernel
% names, so that notebooks in one directory keep apart; user.pl when it
% names none, as for a console.
user_file(Pairs, File) :-
    (   memberchk(notebook-str(Chars), Pairs), Chars \== [] ->
        atom_chars(Notebook, Chars),
        (   sub_atom(Notebook, Before, 6, 0, '.ipynb') ->
            sub_atom(Notebook, 0, Before, _, Name)
        ;   Name = Notebook
        ),
        atom_concat(Name, '.user.pl', File)
    ;   File = 'user.pl'
    ).

% cell_content(+Code, +UserFile, -Content): queries(Queries), or nothing
% when the cell has nothing to run.
cell_content(Code, UserFile, Content) :-
    (   magic(Code, Magic) -> magic_content(Magic, UserFile, Content)
    ;   line_magic(Code, Query) -> Content = queries([Query])
    ;   cell_queries(Code, Queries),
        Content = queries(Queries)
    ).

% ----- line magics -----

% A cell that is %NAME and nothing else, with a NAME below. Any other such
% cell is a comment.
line_magic(Code, query(Goal, Names)) :-
    reverse(Code, Reversed),
    ending(Reversed, ReversedName),
    reverse(ReversedName, ['%'|NameChars]),
    atom_chars(Name, NameChars),
    line_magic(Name, Goal, Names).

% Without the blanks and line ends it starts with.
ending([C|Cs], Rest) :-
    ( C == ' ' ; C == '\t' ; C == '\r' ; C == '\n' ),
    !,
    ending(Cs, Rest).
ending(Chars, Chars).

line_magic(bindings, jupyter:print_variable_bindings, []).
line_magic(queries, jupyter:print_queries, []).
line_magic(help, jupyter:help, []).
line_magic(pwd, jupyter:pwd, []).
line_magic(magic, jupyter:magic, []).
line_magic(versions, jupyter:versions, []).
line_magic(flags, jupyter:print_table(current_prolog_flag(Name, Value)), ['Name'=Name, 'Value'=Value]).

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

magic(user, [], Text, user(write, Text)).
magic('user+', [], Text, user(append, Text)).
magic(file, [C|Cs], Text, file(File, write, load, Text)) :- atom_chars(File, [C|Cs]).
magic('file+', [C|Cs], Text, file(File, append, load, Text)) :- atom_chars(File, [C|Cs]).
magic(load, [C|Cs], Text, file(File, write, load, Text)) :- atom_chars(File, [C|Cs]).
magic(save, [C|Cs], Text, file(File, write, save, Text)) :- atom_chars(File, [C|Cs]).
magic(highlight, [], _, highlight).
magic(tree, [], Text, tree(Text)).
magic(data, [], Text, data(Text)).
magic(table, [], Text, table(Text, print_table)).
magic(csv, [C|Cs], Text, table(Text, save(csv, File))) :- atom_chars(File, [C|Cs]).
magic(tsv, [C|Cs], Text, table(Text, save(tsv, File))) :- atom_chars(File, [C|Cs]).

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
magic_content(highlight, _, nothing).
magic_content(user(Mode, Text), File, Content) :-
    follow_rename(File),
    magic_content(file(File, Mode, load, Text), File, Content).
magic_content(file(File, Mode, Action, Text), _, Content) :-
    setup_call_cleanup(open(File, Mode, Stream),
                       format(Stream, "~s~n", [Text]),
                       close(Stream)),
    (   Action == load -> Content = queries([query(jupyter_server:load(File), [])])
    ;   Content = nothing
    ).
% Each term of the cell is drawn as a tree. A term ends with a full stop,
% which the last may leave out.
magic_content(tree(Text), _, queries(Trees)) :-
    reverse(Text, Backwards),
    ending(Backwards, Last),
    (   Last = ['.'|_] -> Terms = Text ; append(Text, ['.'], Terms) ),
    cell_queries(Terms, Queries),
    trees(Queries, Trees).
% The cell is one goal, whatever its lines, with a full stop the last line
% may leave out: the plot of show_data/1.
magic_content(data(Text), _, Content) :-
    reverse(Text, Backwards),
    ending(Backwards, Last),
    (   Last = ['.'|Rest] -> reverse(Rest, Goal) ; reverse(Last, Goal) ),
    append(Goal, [')', ')', '.'], Closed),
    atom_chars('jupyter:show_data((', Open),
    append(Open, Closed, Query),
    cell_queries(Query, Queries),
    Content = queries(Queries).
% Each query of the cell is shown as the table of its solutions.
magic_content(table(Text, How), _, queries(Tables)) :-
    cell_queries(Text, Queries),
    tables(Queries, How, Tables).

trees([], []).
trees([query(Term, Names)|Queries], [query(jupyter:show_term(Term), Names)|Trees]) :-
    trees(Queries, Trees).

tables([], _, []).
tables([query(Goal, Names)|Queries], How, [query(Table, Names)|Tables]) :-
    (   How = save(Format, File) -> Table = jupyter:print_and_save_table(Goal, Format, File)
    ;   Table = jupyter:print_table(Goal)
    ),
    tables(Queries, How, Tables).

% A notebook renamed since its last %%user cell takes its file along, so
% that %%user+ goes on from what the cells before it wrote.
follow_rename(File) :-
    (   nb_current(jupyter_user_file, Last), Last \== File,
        exists_file(Last), \+ exists_file(File) ->
        rename(Last, File)
    ;   true
    ),
    nb_setval(jupyter_user_file, File).

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
    ;   clause_term(Goal) ->
        Spent = Spent0, Next = continue,
        failed(Cell0, -4712, 'Exception', jupyter(clause_in_cell), Cell)
    ;   tracer_cell(Goal) ->
        Spent = Spent0, Next = continue,
        failed(Cell0, -4712, 'Exception', jupyter(tracer_in_cell(Goal)), Cell)
    ;   % As it was written: before $Var gives Var its value, and before
        % the query binds its variables.
        copy_term(Goal-Names, Written),
        catch(with_previous(Goal, Names, Resolved), Error, true),
        (   nonvar(Error) ->
            Spent = Spent0, Next = continue,
            failed(Cell0, -4712, 'Exception', Error, Cell)
        ;   helpers(Resolved, Names, Called),
            solve(Called, Names, Written, Alive, Spent0, Spent, Cell0, Cell, Next)
        )
    ).

% A helper that names the variables of its goal gets their names here, from
% the query it is written in: a goal that runs has none. It is found as a
% goal of the query itself, through its control constructs.
helpers(Goal, Names, Called) :-
    (   var(Goal) -> Called = Goal
    ;   control(Goal, Name, Left, Right) ->
        helpers(Left, Names, CalledLeft),
        helpers(Right, Names, CalledRight),
        Called =.. [Name, CalledLeft, CalledRight]
    ;   Goal = (\+ Inner) ->
        helpers(Inner, Names, CalledInner),
        Called = (\+ CalledInner)
    ;   own_predicate(Goal) -> Called = Goal
    ;   helper(Goal, Names, Named) -> Called = Named
    ;   graph_alias(Goal, Graph) -> Called = Graph
    ;   Goal = jupyter:Helper, nonvar(Helper), helper(Helper, Names, Named) -> Called = Named
    ;   Called = Goal
    ).

% show_term/1 and show_graph/2 of a query are the helpers', unless the
% program defines them.
graph_alias(show_term(Term), jupyter:show_term(Term)) :-
    \+ current_predicate(user:show_term/1).
graph_alias(show_graph(Nodes, Edges), jupyter:show_graph(Nodes, Edges)) :-
    \+ current_predicate(user:show_graph/2).

control((Left, Right), ',', Left, Right).
control((Left ; Right), ';', Left, Right).
control((Left -> Right), '->', Left, Right).
control((Left *-> Right), '*->', Left, Right).

helper(print_table(Goal), Names, jupyter:'$jupyter_table'(user:Goal, Named)) :-
    term_variables(Goal, Variables),
    names_of(Names, Variables, Named).
helper(print_and_save_table(Goal, Format, File), Names,
       jupyter:'$jupyter_saved_table'(user:Goal, Named, Format, File)) :-
    term_variables(Goal, Variables),
    names_of(Names, Variables, Named).
% The data is the variable named Data, or _Data; with neither, the query
% fails, as the reference's does.
helper(show_data(Goal), Names, Called) :-
    (   memberchk('Data'=Data, Names) -> Called = jupyter:'$jupyter_data'(user:Goal, Data)
    ;   memberchk('_Data'=Data, Names) -> Called = jupyter:'$jupyter_data'(user:Goal, Data)
    ;   Called = fail
    ).

% A helper's name the program defines itself is the program's, as in the
% reference.
own_predicate(show_data(_)) :-
    current_predicate(user:show_data/1).

% The names of the query that belong to Variables, in the query's order.
names_of([], _, []).
names_of([Name=Variable|Names], Variables, Named) :-
    (   occurs(Variables, Variable) -> Named = [Name=Variable|Rest]
    ;   Named = Rest
    ),
    names_of(Names, Variables, Rest).

% A term that can only be a clause. A fact cannot be told from a query, so
% a cell is never loaded as a program by guessing: this is an error that
% says where clauses go.
clause_term((_ :- _)).
clause_term((:- _)).
clause_term((_ --> _)).

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
% A tracer the query left on would trace the server from here on.
solve(Goal, Names, Written, Alive, Spent0, Spent, Cell0, Cell, Next) :-
    output(_),
    '$jupyter_extras'(_),
    nb_setval('$jupyter_cell', none),
    statistics(cputime, Started),
    (   catch(solution(Goal, Exit), Error, true),
        '$jupyter_tracing'(false),
        resumed(Cell0, Cell1, First),
        ran(First, Error, Cell1, Written, Started),
        solved(Error, Exit, Names, Alive, Spent0, Spent, Cell1, Cell, Next)
    ->  true
    ;   '$jupyter_tracing'(false),
        resumed(Cell0, Cell1, _),
        output(Output),
        error_object(-4711, 'Failure', [], Output, Failure),
        answered(Cell1, obj([status-str(error), error-Failure]), Cell),
        Spent = Spent0, Next = continue
    ).

% The cell as the retry that came back here left it, or as it was when the
% query started.
% First tells the query's first solution from one a retry asked for.
resumed(Cell0, Cell, First) :-
    nb_getval('$jupyter_cell', Left),
    (   Left == none -> Cell = Cell0, First = true
    ;   Cell = Left, First = false,
        nb_setval('$jupyter_cell', none)
    ).

% A query joins the session's history, as it was written, when it has its
% first solution.
ran(First, Error, cell(Id, _, _), Written, Started) :-
    (   First == true, var(Error) ->
        statistics(cputime, Now),
        Seconds is Now - Started,
        '$jupyter_ran'(Id, Written, Seconds)
    ;   true
    ).

output(Text) :-
    '$jupyter_take_output'(Text).

% Exit is bound when the goal left no choice point.
% The tracer goes off as the goal's last step, before what tells a
% deterministic exit runs and joins the trace.
solution(Goal, Exit) :-
    setup_call_cleanup(true, '$jupyter_untraced'(Goal), Exit = last).

'$jupyter_untraced'(Goal) :-
    user:Goal,
    '$jupyter_tracing'(false).

solved(Error, Exit, Names, Alive, Spent0, Spent, Cell0, Cell, Next) :-
    (   nonvar(Error) ->
        Spent = Spent0, Next = continue,
        failed(Cell0, -4712, 'Exception', Error, Cell)
    ;   output(Output),
        answer_terms(Names, Shown, Goals),
        answer_names(Shown, Goals, AnswerNames),
        bindings(Shown, AnswerNames, Bindings),
        residual_goals(Goals, AnswerNames, Residual),
        % What the helpers of module jupyter left for this answer.
        '$jupyter_extras'(Extras),
        append(Residual, Extras, More),
        answered(Cell0, obj([status-str(success), type-str(query),
                             bindings-obj(Bindings), output-str(Output)|More]), Cell1),
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
bindings(Shown, Names, Bindings) :-
    bindings(Shown, Names, [], Bindings).

bindings([], _, _, []).
bindings([Name=Value|More], Names, Seen, Bindings) :-
    (   hidden(Name) -> Bindings = Rest
    ;   var(Value) ->
        (   named(Seen, Value, Earlier) -> Bindings = [Name-str(Earlier)|Rest]
        ;   Bindings = Rest
        )
    ;   '$jupyter_answer_text'(Value, Names, 699, Text),
        Bindings = [Name-str(Text)|Rest]
    ),
    bindings(More, Names, [Name=Value|Seen], Rest).

hidden(Name) :- sub_atom(Name, 0, 1, _, '_').

% The first name the variable had in the query.
named([Name=Variable|Earlier], Value, Found) :-
    (   named(Earlier, Value, Found) -> true
    ;   Variable == Value -> Found = Name
    ).

% The values of the query's variables, and the goals that would put back
% the constraints left on them. With constraints the values are copies, which
% carry none, and the goals name the copies.
answer_terms(Names, Shown, Goals) :-
    values_of(Names, Values),
    term_attvars(Values, Constrained),
    (   Constrained == [] -> Shown = Names, Goals = []
    ;   copy_term(Values, Copies, Goals),
        named_copies(Names, Copies, Shown)
    ).

values_of([], []).
values_of([_=Value|Names], [Value|Values]) :- values_of(Names, Values).

named_copies([], [], []).
named_copies([Name=_|Names], [Copy|Copies], [Name=Copy|Shown]) :-
    named_copies(Names, Copies, Shown).

% The names an answer is written with: the query's own, for its variables
% that are still free, and _A, _B, ..., _Z, _A1, ... for the other variables
% it shows, in the order it shows them, past the names the query uses. The
% top level names them so.
answer_names(Shown, Goals, AnswerNames) :-
    free_names(Shown, Own),
    shown_values(Shown, Values),
    term_variables(Values-Goals, Variables),
    fresh_names(Variables, Shown, Own, 0, Fresh),
    append(Own, Fresh, AnswerNames).

free_names([], []).
free_names([Name=Value|Shown], Own) :-
    (   var(Value) -> Own = [Name=Value|Rest]
    ;   Own = Rest
    ),
    free_names(Shown, Rest).

% The values the bindings show, in their order.
shown_values([], []).
shown_values([Name=Value|Shown], Values) :-
    (   ( hidden(Name) ; var(Value) ) -> Values = Rest
    ;   Values = [Value|Rest]
    ),
    shown_values(Shown, Rest).

fresh_names([], _, _, _, []).
fresh_names([Variable|Variables], Shown, Own, I0, Fresh) :-
    (   named_variable(Own, Variable) -> I = I0, Fresh = Rest
    ;   unused_name(Shown, I0, Name, I),
        Fresh = [Name=Variable|Rest]
    ),
    fresh_names(Variables, Shown, Own, I, Rest).

named_variable([_=Named|Own], Variable) :-
    (   Named == Variable -> true
    ;   named_variable(Own, Variable)
    ).

% The name of position I0 and after in _A, ..., _Z, _A1, ..., _Z1, _A2, ...
% that the query does not use, and the position after it.
unused_name(Shown, I0, Name, I) :-
    Code is 0'A + I0 mod 26,
    char_code(Letter, Code),
    atom_concat('_', Letter, Stem),
    (   I0 < 26 -> Candidate = Stem
    ;   Round is I0 // 26,
        number_codes(Round, Digits),
        atom_codes(Suffix, Digits),
        atom_concat(Stem, Suffix, Candidate)
    ),
    I1 is I0 + 1,
    (   name_taken(Shown, Candidate) -> unused_name(Shown, I1, Name, I)
    ;   Name = Candidate, I = I1
    ).

name_taken([Taken=_|Shown], Name) :-
    (   Taken == Name -> true
    ;   name_taken(Shown, Name)
    ).

% The constraints left on the variables of the query: a key of the answer,
% or nothing when there are none.
residual_goals(Goals, Names, Residual) :-
    (   Goals == [] -> Residual = []
    ;   goal_texts(Goals, Names, Texts),
        Residual = [residual_goals-Texts]
    ).

goal_texts([], _, []).
goal_texts([Goal|Goals], Names, [str(Text)|Texts]) :-
    '$jupyter_answer_text'(Goal, Names, 999, Text),
    goal_texts(Goals, Names, Texts).

remember([]).
remember([Name=Value|Names]) :-
    (   nonvar(Value), \+ hidden(Name) -> '$jupyter_remember'(Name, Value)
    ;   true
    ),
    remember(Names).

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
    (   '$jupyter_value'(Name, Value) -> true
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

% tracer_cell(+Goal): a cell that is only trace, trace/1,2 or leash/1. The
% tracer prints every port and never stops at one, since a notebook cannot
% answer at a breakpoint: a cell traces a goal with jupyter:trace/1. A
% program's own trace/1, trace/2 or leash/1 runs as it is written.
tracer_cell(Goal) :-
    (   Goal == trace -> true
    ;   callable(Goal),
        functor(Goal, Name, Arity),
        tracer_predicate(Name, Arity),
        \+ current_predicate(user:Name/Arity)
    ).

tracer_predicate(trace, 1).
tracer_predicate(trace, 2).
tracer_predicate(leash, 1).

tracer_text(trace, Text) :- !,
    atom_chars('The tracer does not stay on after a query. To trace a goal, call jupyter:trace(Goal).', Text).
tracer_text(Goal, Text) :-
    functor(Goal, leash, 1), !,
    atom_chars('leash/1 does not apply in a notebook: the tracer prints every port and never stops at one. To trace a goal, call jupyter:trace(Goal).', Text).
tracer_text(Goal, Text) :-
    functor(Goal, Name, Arity),
    with_output_to(string(Text),
        format("~w/~w is not available in a notebook. To trace a goal, call jupyter:trace(Goal).", [Name, Arity])).

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
    ;   Ball == jupyter(clause_in_cell) ->
        atom_chars('A clause is not a query. To define predicates, start the cell with %%user.', Text)
    ;   Ball = jupyter(tracer_in_cell(Goal)) ->
        tracer_text(Goal, Text)
    ;   '$jupyter_error_text'(Ball, Text)
    ).
