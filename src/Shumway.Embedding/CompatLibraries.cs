namespace Shumway.Embedding;

/// <summary>
/// Built-in compatibility libraries for Scryer / Trealla Prolog programs,
/// loaded on demand by <c>use_module(library(Name))</c>. Each is ordinary
/// Prolog consulted into the global (user) namespace so a program written for
/// Scryer/Trealla — which imports these stdlib modules — consults unchanged.
///
/// <para>Three of them supply predicates Shumway does not have in its prelude:
/// <c>dcgs</c> (<c>seq//1</c>, <c>...//0</c>; <c>phrase/2,3</c> are already in
/// the prelude), <c>format</c> (<c>format_//2</c>, the DCG-format subset used
/// by real programs), and <c>dif</c> (a non-coroutining <c>dif/2</c>
/// approximation — see the note on it below). The remainder — <c>lists</c>,
/// <c>charsio</c>, and friends — name predicates Shumway already provides in
/// its prelude / builtins, so importing them is a no-op that simply marks the
/// module available (a genuinely unknown library name still raises
/// <c>existence_error(library, Name)</c> so typos surface).</para>
///
/// <para>These deliberately assume the Scryer default <c>double_quotes = chars</c>
/// for the arguments they inspect (the format string, terminal lists). The
/// libraries themselves are written with explicit quoted character atoms
/// (<c>'~'</c>, <c>'s'</c>) so they parse identically under any
/// <c>double_quotes</c> flag; a program that relies on them still needs to run
/// under <c>chars</c>, exactly as it does on Scryer/Trealla.</para>
/// </summary>
internal static class CompatLibraries
{
    /// <summary>Resolves a <c>library(Name)</c> import. Returns <c>true</c> for
    /// a known compatibility library, with <paramref name="source"/> set to its
    /// Prolog source (empty when the library is a no-op covered by the
    /// prelude). Returns <c>false</c> for an unknown library name.</summary>
    public static bool TryGet(string name, out string source)
    {
        source = name switch
        {
            "dcgs"   => Dcgs,
            "format" => Format,
            "dif"    => Dif,
            "quads"  => Quads,
            "$project_atts" => ProjectAtts,
            "atts" => Atts,
            // Covered by Shumway's prelude / builtins — importing them is a
            // no-op that just marks the module available. `loader` is Scryer's
            // bootstrap module; the one predicate real libraries import from it
            // (strip_module/3 — dcgs.pl) is already in the prelude.
            "lists" or "charsio" or "error" or "iso_ext" or "between"
              or "apply" or "pio" or "si" or "debug" or "pairs"
              or "ordsets" or "assoc" or "dcg" or "dcg_basics" or "loader" => "",
            _ => null!,
        };
        return source is not null;
    }

    /// <summary>The quads library source, for the predicate-reference
    /// generator: its exports carry <c>%!</c> doc comments like the always-on
    /// libraries do.</summary>
    internal static string QuadsSource => Quads;

    // library(dcgs) — the generic DCG helpers. seq//1 and '...'//0 moved into
    // the prelude (they are built-in now), joining phrase/2,3; the library
    // stays loadable so a `:- use_module(library(dcgs)).` in existing sources
    // resolves and is a no-op.
    private const string Dcgs = """
        % seq//1, '...'//0 and phrase/2,3 are built-in; nothing to load.
        """;

    // library(quads) — Neumerkel's machine-readable test transcripts (issue
    // #69; length_quad.pl, phrase_quad.pl). A quad file is not a program:
    //     <id> ?- <goal>.
    //     <expected> | <alternative> | ... .
    // Importing this module activates `?-` (xfx) and `|` (xfy) for the
    // importer (module-scoped ops, ADR-046 — the default table is untouched),
    // and a user:term_expansion/2 pair turns each quad into inert facts here,
    // so `use_module(library(quads)), consult('length_quad.pl'), run_quads.`
    // is the whole workflow — the Trealla shape the issue asks for. The
    // classifier and checker mirror tests/conformity/quad_suites.pl (the
    // certified harness): each expected alternative maps to a CLASS
    // (succeeds / fails / error(Kind) / loops / lenient) and the goal's
    // outcome must match one. `loops` runs under time_out/3: still going at
    // 15 s counts as looping.
    private const string Quads = """
        :- module(quads, [run_quads/0, run_quads/1, clear_quads/0,
                          op(1200, xfx, ?-), op(1200, fx, ?-),
                          op(1100, xfy, '|')]).

        % The published suites lean on freeze/2 and dif/2; without this a
        % freeze goal is an existence_error and its quad fails instead of
        % running (length 29-31 caught it in the browser).
        :- use_module(library(coroutining)).

        % A query is recognised by its principal functor alone, and it has
        % two: `Id ?- Goal` and, for a test that needs no name, `?- Goal`.
        :- op(1200, xfx, ?-).
        :- op(1200, fx, ?-).
        :- op(1100, xfy, '|').

        :- dynamic('$quad'/4).          % '$quad'(Seq, Id, Goal, Alternatives)
        :- dynamic('$quad_open'/2).     % File, Seq — the quad being described
        :- dynamic('$quad_seq'/1).
        :- dynamic('$quad_dropped'/2).  % Id, Why
        :- dynamic('$quad_tmp_seq'/1).
        :- dynamic('$quad_src'/2).         % Seq, File
        :- dynamic('$quad_names'/2).       % Seq, the goal's shown variable names
        :- dynamic('$quad_run'/3).         % Seq, Goal, tuple of those variables
        :- dynamic('$quad_uncompared'/1).  % Id whose answers could not be compared

        % ---- consult-time capture -----------------------------------------
        % A QUERY opens a test and is recognised by its principal functor
        % alone: `Id ?- Goal` or `?- Goal`. Every sentence after it, up to
        % the next query, DESCRIBES that test's answers. All of them expand
        % to nothing, so the transcript consults without ever defining ;/2 or
        % =/2, and a description is never handed to the compiler — which used
        % to reject it as a clause for ,/2 with a message about static
        % procedures that named neither the quad nor the line it came from.
        % The open slot is keyed by file, so a transcript cannot swallow a
        % clause of a later consult.
        %
        % The id is any GROUND term, not just a name or a number: a suite may
        % key a test by whatever identifies it, including a comma term whose
        % second half names the clause of the standard under test. A test
        % that needs no name is written `?- Goal` and reported by its
        % position in the file.
        user:term_expansion((Id ?- Goal), []) :-
            ground(Id), !,
            quads_open(Id, Goal).
        user:term_expansion((?- Goal), []) :-
            !,
            quads_open(anon, Goal).
        user:term_expansion(Block, []) :-
            quads_open_quad(N), !,
            quads_describe(N, Block).

        quads_load_file(F) :-
            ( prolog_load_context(file, F0) -> F = F0 ; F = user ).

        quads_open_quad(N) :- quads_load_file(F), '$quad_open'(F, N), !.

        % Recorded the moment it opens, with no alternatives yet: a query
        % whose descriptions are missing or unreadable is still a quad and is
        % reported as one rather than vanishing.
        quads_open(IdSpec, Goal) :-
            quads_load_file(F),
            ( retract('$quad_seq'(N0)) -> true ; N0 = 0 ),
            N is N0 + 1,
            assertz('$quad_seq'(N)),
            ( IdSpec == anon -> Id = N ; Id = IdSpec ),
            assertz('$quad'(N, Id, Goal, [])),
            assertz('$quad_src'(N, F)),
            retractall('$quad_open'(F, _)),
            assertz('$quad_open'(F, N)).

        % Each description sentence adds its alternatives to the open quad.
        % No names here: the hook is handed a term whose variables have
        % already lost them (see the recovery pass below).
        quads_describe(N, Block) :-
            quads_alts(Block, Alts),
            retract('$quad'(N, Id, Goal, Have)),
            quads_parse_alts(Alts, Id, none, Have, More),
            assertz('$quad'(N, Id, Goal, More)).

        % ---- recovering the variable names --------------------------------
        % A query and the sentences describing its answers are separate
        % terms, so the L of one is not the L of the other: only their NAMES
        % relate them, and the names are in the source. Hence a second read
        % of the file, for the names alone -- without them `?- length(L,0).`
        % described as `L = [].` can only be checked as far as "the goal
        % succeeds", which is no check of the answer at all.
        quads_recover_all :-
            findall(F, ( '$quad_src'(_, F), F \== user ), Fs0),
            quads_dedup(Fs0, Fs),
            quads_recover_files(Fs).

        quads_dedup([], []).
        quads_dedup([X|Xs], Out) :-
            ( quads_memberchk(X, Xs) -> Out = R ; Out = [X|R] ),
            quads_dedup(Xs, R).

        % A source that cannot be read again leaves the quad as the consult
        % captured it: checked, but not down to its answers.
        quads_recover_files([]).
        quads_recover_files([F|Fs]) :-
            ( catch(quads_recover_file(F), _, fail) -> true ; true ),
            quads_recover_files(Fs).

        quads_recover_file(F) :-
            findall(N, '$quad_src'(N, F), Ns),
            setup_call_cleanup(open(F, read, S),
                               quads_reread(S, none, Ns),
                               close(S)).

        quads_reread(S, Cur, Ns) :-
            read_term(S, T, [variable_names(Vs)]),
            (   T == end_of_file
            ->  true
            ;   quads_query_goal(T, G)
            ->  (   Ns = [N|Rest]
                ->  quads_recover_quad(N, G, Vs), quads_reread(S, N, Rest)
                ;   quads_reread(S, none, Ns)
                )
            ;   Cur \== none
            ->  quads_recover_desc(Cur, T, Vs), quads_reread(S, Cur, Ns)
            ;   quads_reread(S, Cur, Ns)
            ).

        quads_query_goal(T, G) :- nonvar(T), T = (Id ?- G0), ground(Id), !, G = G0.
        quads_query_goal(T, G) :- nonvar(T), T = (?- G).

        % The goal and the tuple of its shown variables are stored as ONE
        % fact: asserting them apart would copy them apart, and the tuple is
        % only useful while it shares the goal's variables.
        quads_recover_quad(N, Goal, GNames) :-
            quads_shown_names(GNames, Names, Vars),
            Tuple =.. [t|Vars],
            retract('$quad'(N, Id, _, _)),
            assertz('$quad'(N, Id, Goal, [])),
            retractall('$quad_names'(N, _)),
            retractall('$quad_run'(N, _, _)),
            assertz('$quad_names'(N, Names)),
            assertz('$quad_run'(N, Goal, Tuple)),
            retractall('$quad_dropped'(Id, _)),
            retractall('$quad_uncompared'(Id)).

        quads_recover_desc(N, Block, DNames) :-
            '$quad_names'(N, Names),
            quads_alts(Block, Alts),
            retract('$quad'(N, Id, Goal, Have)),
            quads_parse_alts(Alts, Id, ctx(Names, DNames), Have, More),
            assertz('$quad'(N, Id, Goal, More)).

        quads_shown_names([], [], []).
        quads_shown_names([Name=V|Ps], Names, Vars) :-
            (   quads_hidden_name(Name)
            ->  Names = Ns, Vars = Vs
            ;   Names = [Name|Ns], Vars = [V|Vs]
            ),
            quads_shown_names(Ps, Ns, Vs).

        % A name that starts with an underscore is not part of an answer: a
        % top level does not show it, so a transcript does not describe it.
        quads_hidden_name(Name) :- atom_chars(Name, ['_'|_]).

        quads_alts(B, Alts) :-
            ( B = '|'(A, Rest) -> Alts = [A|More], quads_alts(Rest, More)
            ; Alts = [B] ).

        quads_parse_alts([], _, _, Acc, Acc).
        quads_parse_alts([A|As], Id, Ctx, Acc, Out) :-
            (   quads_alt(A, Ctx, Alt)
            ->  ( Ctx == none, quads_has_binding(A)
                -> quads_note_uncompared(Id) ; true ),
                ( quads_memberchk(Alt, Acc) -> Acc1 = Acc
                ; quads_append(Acc, [Alt], Acc1) )
            ;   % Unreadable, or written in a vocabulary this harness does
                % not know. Reported against its quad — the one thing the
                % old silent catch-all could not do.
                quads_note_dropped(Id, A),
                Acc1 = Acc
            ),
            quads_parse_alts(As, Id, Ctx, Acc1, Out).

        quads_note_dropped(Id, A) :-
            ( '$quad_dropped'(Id, A) -> true ; assertz('$quad_dropped'(Id, A)) ).

        quads_note_uncompared(Id) :-
            ( '$quad_uncompared'(Id) -> true ; assertz('$quad_uncompared'(Id)) ).

        % ---- reading one alternative --------------------------------------
        % An alternative is a comma chain of DESCRIPTORS and an outcome, and
        % may end in the marker `unexpected`, which says the alternative is a
        % wrong answer: a system producing it does not pass. Descriptors say
        % how the goal is run or what else it does; the outcome is what gets
        % checked.
        quads_alt(A, Ctx, alt(Class, In, Pk, Out, Sanctioned)) :-
            quads_conj_list(A, Es0),
            quads_take_marker(Es0, Es1, Sanctioned),
            quads_take_descriptors(Es1, Es2, none, In, none, Pk, none, Out),
            (   Es2 == []
            ->  % Descriptors only: the goal has to run, and succeeding is
                % all that is claimed.
                Class = succeeds
            ;   quads_conj_from(Es2, Body),
                quads_alt_class(Body, Ctx, Class)
            ).

        quads_conj_list((A, B), [A|R]) :- !, quads_conj_list(B, R).
        quads_conj_list(A, [A]).
        quads_conj_from([X], X) :- !.
        quads_conj_from([X|Xs], (X, R)) :- quads_conj_from(Xs, R).

        quads_take_marker(Es, Rest, Sanctioned) :-
            (   quads_append(Rest0, [unexpected], Es)
            ->  Sanctioned = false, Rest = Rest0
            ;   Sanctioned = true, Rest = Es
            ).

        % inputs(Text) and peeks(Text) together say what the goal reads: it
        % must CONSUME inputs and leave peeks unread. The two are supplied as
        % one input, which is what makes the pair checkable — reading `1.`
        % off `1.` alone cannot tell that the number ended, and writing the
        % peek down separately is how that is said.
        quads_take_descriptors([], [], In, In, Pk, Pk, Out, Out).
        quads_take_descriptors([E|Es], Rest, In0, In, Pk0, Pk, Out0, Out) :-
            (   E == sto
            ->  Rest = Rest1, In1 = In0, Pk1 = Pk0, Out1 = Out0
            ;   nonvar(E), E = inputs(T)
            ->  Rest = Rest1, In1 = T, Pk1 = Pk0, Out1 = Out0
            ;   nonvar(E), E = peeks(T)
            ->  Rest = Rest1, In1 = In0, Pk1 = T, Out1 = Out0
            ;   nonvar(E), E = outputs(T)
            ->  Rest = Rest1, In1 = In0, Pk1 = Pk0, Out1 = T
            ;   Rest = [E|Rest1], In1 = In0, Pk1 = Pk0, Out1 = Out0
            ),
            quads_take_descriptors(Es, Rest1, In1, In, Pk1, Pk, Out1, Out).

        quads_append([], L, L).
        quads_append([X|Xs], L, [X|R]) :- quads_append(Xs, L, R).

        % `sto,` prefixes mark subject-to-occurs-check runs; the class is the
        % same either way (the engine's default is rational trees, like the
        % systems the page's sto column tracks).
        quads_alt_class((sto, R), Ctx, C) :- !, quads_alt_class(R, Ctx, C).
        % `outputs(Text), Outcome` says the goal writes Text and THEN
        % behaves as Outcome. The outcome is what this harness observes, so
        % it classifies by it; the text itself is not compared, and a bare
        % outputs/1 with no outcome after it is nothing this can check.
        quads_alt_class((outputs(_), R), Ctx, C) :- !, quads_alt_class(R, Ctx, C).
        quads_alt_class(false, _, fails) :- !.
        quads_alt_class(true, _, succeeds) :- !.
        quads_alt_class(loops, _, loops) :- !.
        % The class carries the error TERM, not just its kind: a description
        % naming one culprit while the goal reports another describes a
        % different system, and comparing only the kind let that pass.
        quads_alt_class(E, _, error(E)) :-
            nonvar(E), functor(E, W, _), quads_error_word(W), !.
        quads_alt_class(throw(T), _, thrown(T)) :- !.
        % An answer display is the answers themselves, in order: with the
        % names recovered they are compared one by one against what the goal
        % actually answers. `;` separates SUCCESSIVE answers here -- the bar
        % is what separates alternative sanctioned behaviours.
        quads_alt_class(A, ctx(Names, DNames), answers(Exps, Open)) :-
            quads_has_binding(A), !,
            quads_answer_list(A, Seq, Open),
            quads_expected_list(Seq, DNames, Names, Exps).
        % Without the names nothing links the description's L to the goal's,
        % so all that can be checked is that the goal succeeds. Noted, so the
        % report does not pass this off as a comparison.
        quads_alt_class(A, none, succeeds) :- quads_has_binding(A), !.
        % An answer sequence cut short with `...` claims only that the goal
        % succeeds and goes on succeeding; the shown answers are not
        % compared, so there is nothing narrower to check.
        quads_alt_class(A, _, lenient) :- quads_has_ellipsis(A), !.
        % Anything else is a description this harness cannot read. It does
        % NOT become a class that matches whatever happens: that is how a
        % test written in a vocabulary we do not know reported a pass while
        % checking nothing at all. Failing here sends it to the report.

        quads_has_ellipsis((A, B)) :- !,
            ( quads_has_ellipsis(A) -> true ; quads_has_ellipsis(B) ).
        quads_has_ellipsis((A ; B)) :- !,
            ( quads_has_ellipsis(A) -> true ; quads_has_ellipsis(B) ).
        quads_has_ellipsis('...').

        % An answer display: any =/2 in the ,/;-chain (`L = [], N = 0 ; ...`).
        quads_has_binding((A, B)) :- !,
            ( quads_has_binding(A) -> true ; quads_has_binding(B) ).
        quads_has_binding((A ; B)) :- !,
            ( quads_has_binding(A) -> true ; quads_has_binding(B) ).
        quads_has_binding(_ = _).

        % A trailing `...` says the answers go on; the ones written down
        % still have to be the first ones, and nothing is claimed past them.
        quads_answer_list(A, Seq, Open) :-
            quads_disj_list(A, Es),
            (   quads_append(Es0, ['...'], Es)
            ->  Open = true, Seq = Es0
            ;   Open = false, Seq = Es
            ),
            Seq \== [].

        quads_disj_list((A ; B), [A|R]) :- !, quads_disj_list(B, R).
        quads_disj_list(A, [A]).

        quads_expected_list([], _, _, []).
        quads_expected_list([A|As], DNames, Names, [E|Es]) :-
            quads_expected(A, DNames, Names, E),
            quads_expected_list(As, DNames, Names, Es).

        % One described answer, as the goal's own variables would show it.
        % The equations run on a COPY of the description, so one answer's
        % bindings cannot reach the next; a variable the description does not
        % mention stays unbound, which is what a top level showing nothing
        % for it means.
        quads_expected(A, DNames, Names, Exp) :-
            copy_term(A-DNames, A1-DN1),
            quads_conj_list(A1, Es0),
            quads_take_descriptors(Es0, Es1, none, _, none, _, none, _),
            quads_all_equations(Es1),
            quads_call_all(Es1),
            quads_tuple_args(Names, DN1, Args),
            Exp =.. [t|Args].

        quads_all_equations([]).
        quads_all_equations([E|Es]) :- nonvar(E), E = (_ = _), quads_all_equations(Es).
        quads_call_all([]).
        quads_call_all([E|Es]) :- call(E), quads_call_all(Es).

        quads_tuple_args([], _, []).
        quads_tuple_args([N|Ns], DN, [V|Vs]) :-
            ( quads_name_var(DN, N, V0) -> V = V0 ; true ),
            quads_tuple_args(Ns, DN, Vs).
        quads_name_var([N0=V0|Ps], N, V) :-
            ( N0 == N -> V = V0 ; quads_name_var(Ps, N, V) ).

        quads_error_word(instantiation_error).
        quads_error_word(type_error).
        quads_error_word(domain_error).
        quads_error_word(existence_error).
        quads_error_word(permission_error).
        quads_error_word(representation_error).
        quads_error_word(evaluation_error).
        quads_error_word(resource_error).
        quads_error_word(syntax_error).

        % ---- running ------------------------------------------------------

        %! run_quads | Quad tests | Runs every loaded quad test and prints quads: Passed/Total, listing the failing ids and any test whose expected block could not be classified.
        run_quads :- quads_run_matching(_).
        %! run_quads(+Id) | Quad tests | Runs the single quad test with the given id and reports it the same way.
        run_quads(Id) :- quads_run_matching(Id).

        quads_run_matching(Filter) :-
            quads_recover_all,
            findall(q(N, Id, R, K), ( '$quad'(N, Id, G, K),
                                      ( Filter = Id -> true ; var(Filter) ),
                                      quads_runnable(N, G, R) ),
                    Qs),
            quads_run_list(Qs, 0, Pass, 0, Total, [], FailsR),
            quads_reverse(FailsR, Fails),
            format('quads: ~w/~w~n', [Pass, Total]),
            (   Fails == [] -> true
            ;   quads_length(Fails, NF),
                format('  failing (~w): ~w~n', [NF, Fails])
            ),
            quads_report_dropped,
            quads_report_uncompared.

        % Every answer description this harness could not read, named with
        % the quad it belongs to. A test whose descriptions were ALL
        % unreadable is called out separately: it counts in the total and can
        % only fail, and saying so is the difference between a report and a
        % number that looks fine.
        quads_report_dropped :-
            (   findall(Id-A, '$quad_dropped'(Id, A), Ds), Ds \== []
            ->  quads_length(Ds, ND),
                format('  not understood (~w):~n', [ND]),
                quads_report_each(Ds)
            ;   true
            ).
        quads_report_each([]).
        quads_report_each([Id-A|R]) :-
            format('    ~w: ~q~n', [Id, A]),
            quads_report_each(R).

        % A quad whose answers were written down but could not be compared,
        % because its source was not there to take the names from. It was
        % still run; only the answer substitutions went unchecked.
        quads_report_uncompared :-
            (   findall(Id, '$quad_uncompared'(Id), Us), Us \== []
            ->  quads_length(Us, NU),
                format('  answers not compared (~w): ~w~n', [NU, Us])
            ;   true
            ).

        % The goal as it will be run, with the tuple of its shown variables
        % when the names were recovered.
        quads_runnable(N, _, run(G, T)) :- '$quad_run'(N, G, T), !.
        quads_runnable(_, G, run(G, none)).

        quads_run_list([], P, P, T, T, F, F).
        quads_run_list([q(_, Id, G, K)|Qs], P0, P, T0, T, F0, F) :-
            T1 is T0 + 1,
            ( quads_check(K, G) -> P1 is P0 + 1, F1 = F0
            ; P1 = P0, F1 = [Id|F0] ),
            quads_run_list(Qs, P1, P, T1, T, F1, F).

        % The test passes when the run matches SOME sanctioned alternative.
        % An alternative marked `unexpected` is not sanctioned: it is written
        % down precisely because producing it is wrong, so it never makes a
        % test pass, and a quad whose alternatives are all unexpected can
        % only fail.
        quads_check(Alts, G) :-
            quads_sanctioned(Alts, Ok),
            Ok \== [],
            quads_group_inputs(Ok, Groups),
            quads_any_group_matches(Groups, G).

        quads_sanctioned([], []).
        quads_sanctioned([alt(C, In, Pk, Ot, S)|As], Out) :-
            ( S == true -> Out = [alt(C, In, Pk, Ot)|R] ; Out = R ),
            quads_sanctioned(As, R).

        % Alternatives that read the same input are decided by ONE run of the
        % goal; the reading is the expensive part and a `loops` alternative
        % costs the whole time limit.
        quads_group_inputs([], []).
        quads_group_inputs([alt(C, In, Pk, Ot)|As], Groups) :-
            quads_group_inputs(As, G0),
            quads_add_to_group(G0, In, want(C, Pk, Ot), Groups).
        quads_add_to_group([], In, W, [group(In, [W])]).
        quads_add_to_group([group(In0, Ws)|Gs], In, W, Out) :-
            (   In0 == In
            ->  Out = [group(In0, [W|Ws])|Gs]
            ;   Out = [group(In0, Ws)|Rest],
                quads_add_to_group(Gs, In, W, Rest)
            ).

        quads_any_group_matches([group(In, Ws)|Gs], G) :-
            (   quads_run_group(In, Ws, G)
            ->  true
            ;   quads_any_group_matches(Gs, G)
            ).

        quads_run_group(In, Ws, G) :-
            quads_run_watched(In, Ws, G, O, Left, Written),
            quads_want_matches(Ws, O, Left, Written).

        quads_want_matches([want(C, Pk, Ot)|Ws], O, Left, Written) :-
            (   quads_match(C, O),
                quads_peek_matches(Pk, Left),
                quads_output_matches(Ot, Written)
            ->  true
            ;   quads_want_matches(Ws, O, Left, Written)
            ).

        % No peek was written down, so nothing is claimed about what is left.
        quads_peek_matches(none, _) :- !.
        quads_peek_matches(Pk, Left) :- quads_text_chars(Pk, Cs), Cs == Left.

        % Likewise for what the goal WRITES. An `outputs` that is written
        % down is compared: a description claiming the goal prints one thing
        % while it prints another describes a different system, and used to
        % pass here because the text was taken on trust.
        quads_output_matches(none, _) :- !.
        quads_output_matches(Ot, Written) :- quads_text_chars(Ot, Cs), Cs == Written.

        quads_match(succeeds, succeeds).
        quads_match(fails, fails).
        % When the answers were collected, having some IS succeeding and
        % having none IS failing, so a group that mixes an answer display
        % with a plain true/false description is decided by one run.
        quads_match(succeeds, answers([_|_])).
        quads_match(fails, answers([])).
        quads_match(answers(Exps, Open), answers(As)) :-
            quads_answers_match(Exps, Open, As).
        % An ISO error is described by its formal alone, so that is what is
        % compared, up to a renaming of the variables in it. The context slot
        % is the implementation's to fill.
        quads_match(error(E), raised(error(F, _))) :- !, quads_term_matches(E, F).
        quads_match(thrown(T), raised(B)) :- !, quads_term_matches(T, B).
        quads_match(loops, timeout).
        quads_match(lenient, _).

        % The described answers must be the ones the goal gives, in order.
        % A sequence left open with `...` claims only its own prefix; a
        % closed one claims there are no further answers, so one extra
        % answer refutes it.
        quads_answers_match(Exps, Open, As) :-
            (   Open == true
            ->  quads_prefix_matches(Exps, As)
            ;   quads_same_length(Exps, As),
                quads_prefix_matches(Exps, As)
            ).
        quads_prefix_matches([], _).
        quads_prefix_matches([E|Es], [A|As]) :-
            quads_term_matches(E, A),
            quads_prefix_matches(Es, As).
        quads_same_length([], []).
        quads_same_length([_|Xs], [_|Ys]) :- quads_same_length(Xs, Ys).

        % `...` inside a described term stands for a part that was not
        % written down, so it matches whatever is in that position and the
        % rest still has to agree. Masking the actual term where the
        % description elides it, and comparing the two afterwards, keeps the
        % comparison a VARIANT one: the variables a description names are
        % variables in the answer, not values.
        %
        % The walk stops as soon as one side stops being compound, so it
        % terminates even against a rational tree: an answer is collected
        % through findall/3, which snapshots a cyclic solution, and a
        % description is as finite as the source it was read from.
        quads_term_matches(D, A) :- quads_mask(D, A, M), D =@= M.

        quads_mask(D, A, M) :-
            (   D == '...' -> M = '...'
            ;   var(D) -> M = A
            ;   var(A) -> M = A
            ;   compound(D), compound(A),
                functor(D, F, N), functor(A, F, N)
            ->  D =.. [F|Ds], A =.. [F|As],
                quads_mask_list(Ds, As, Ms), M =.. [F|Ms]
            ;   M = A
            ).
        quads_mask_list([], [], []).
        quads_mask_list([D|Ds], [A|As], [M|Ms]) :-
            quads_mask(D, A, M), quads_mask_list(Ds, As, Ms).

        % A test that sanctions looping runs under a 15-second limit — no
        % harness can observe an infinite loop directly, so still-running IS
        % the loops outcome (the certified conformity harness draws the same
        % line). Everything else runs unbounded.
        % Reading and writing are watched only when the description says
        % something about them: a quad that mentions neither runs exactly as
        % before, on the real streams.
        % quads_run_reading/5 reifies the run: it binds the outcome instead
        % of failing or throwing, which is what lets the whole thing sit
        % inside with_output_to/2 and still report what happened.
        quads_run_watched(In, Ws, G, O, Left, Written) :-
            quads_wants_output(Ws),
            !,
            with_output_to(atom(A), quads_run_reading(In, Ws, G, O, Left)),
            atom_chars(A, Written).
        quads_run_watched(In, Ws, G, O, Left, none) :-
            quads_run_reading(In, Ws, G, O, Left).

        quads_wants_output([want(_, _, Ot)|Ws]) :-
            ( Ot == none -> quads_wants_output(Ws) ; true ).

        quads_run_reading(none, Ws, G, O, []) :- !,
            quads_outcome(G, Ws, O).
        quads_run_reading(In, Ws, G, O, Left) :-
            % inputs ++ peeks IS the text the goal reads from: the goal has
            % to consume the first part and leave the second, and what it
            % left is read back here rather than assumed.
            quads_wanted_peek(Ws, Pk),
            quads_text_chars(In, InCs),
            quads_text_chars(Pk, PkCs),
            quads_append(InCs, PkCs, AllCs),
            quads_input_file(Path),
            setup_call_cleanup(
                quads_open_input(Path, AllCs, Stream, Saved),
                ( quads_outcome(G, Ws, O), quads_left(Left) ),
                quads_close_input(Path, Stream, Saved)).

        quads_wanted_peek([want(_, Pk, _)|Ws], Out) :-
            ( Pk == none -> quads_wanted_peek(Ws, Out) ; Out = Pk ).
        quads_wanted_peek([], none).


        quads_outcome(run(G, T), Ws, O) :-
            (   quads_group_wants_loops(Ws)
            ->  catch(quads_timed_outcome(G, O), E, quads_error_outcome(E, O))
            ;   quads_answers_wanted(Ws, T, Max)
            ->  catch(( findall(T, call_with_limit(Max, G), As), O = answers(As) ),
                      E, quads_error_outcome(E, O))
            ;   catch(( call(G) -> O = succeeds ; O = fails ), E,
                      quads_error_outcome(E, O))
            ).

        % Collecting answers is only for a group that describes them, and one
        % answer past the longest description: a goal that answers more times
        % than the transcript says has to be caught doing it.
        quads_answers_wanted(Ws, T, Max) :-
            T \== none,
            quads_longest_answer(Ws, 0, Len),
            Len > 0,
            Max is Len + 1.
        quads_longest_answer([], L, L).
        quads_longest_answer([want(C, _, _)|Ws], L0, L) :-
            (   nonvar(C), C = answers(Exps, _), quads_length(Exps, N), N > L0
            ->  quads_longest_answer(Ws, N, L)
            ;   quads_longest_answer(Ws, L0, L)
            ).

        quads_group_wants_loops([want(loops, _, _)|_]) :- !.
        quads_group_wants_loops([_|Ws]) :- quads_group_wants_loops(Ws).

        % What the goal did NOT consume, as a character list.
        quads_left(Left) :-
            (   peek_char(C), C \== end_of_file
            ->  get_char(_), quads_left(Rest), Left = [C|Rest]
            ;   Left = []
            ).

        quads_open_input(Path, Chars, Stream, Saved) :-
            current_input(Saved),
            setup_call_cleanup(open(Path, write, W),
                               quads_put_chars(W, Chars),
                               close(W)),
            open(Path, read, Stream),
            set_input(Stream).
        quads_close_input(Path, Stream, Saved) :-
            set_input(Saved),
            catch(close(Stream), _, true),
            catch(delete_file(Path), _, true).
        quads_put_chars(_, []).
        quads_put_chars(W, [C|Cs]) :- put_char(W, C), quads_put_chars(W, Cs).

        % A scratch file needs a name nothing else will pick. A fixed one in
        % the working directory looked harmless and was not: the test suite
        % runs several engines at once from the same directory, and they
        % raced over it. Process id plus a per-call counter, in the system
        % temp directory.
        quads_input_file(Path) :-
            ( catch(current_prolog_flag(pid, P), _, fail) -> true ; P = 0 ),
            ( retract('$quad_tmp_seq'(N0)) -> true ; N0 = 0 ),
            N is N0 + 1,
            assertz('$quad_tmp_seq'(N)),
            quads_temp_dir(D),
            atomic_list_concat([D, '/shumway_quads_', P, '_', N, '.tmp'], Path).

        quads_temp_dir(D) :-
            (   catch(getenv('TMPDIR', D0), _, fail) -> D = D0
            ;   catch(getenv('TEMP', D0), _, fail) -> D = D0
            ;   catch(getenv('TMP', D0), _, fail) -> D = D0
            ;   D = '.'
            ).

        % A text descriptor is written as a double-quoted string, so what it
        % is at runtime follows the double_quotes flag: chars, codes, or an
        % atom. All three have to answer the same question here.
        quads_text_chars(none, []) :- !.
        quads_text_chars(T, Cs) :- atom(T), !, atom_chars(T, Cs).
        quads_text_chars(T, Cs) :- quads_codes_to_chars(T, Cs), !.
        quads_text_chars(T, T).
        quads_codes_to_chars([], []).
        quads_codes_to_chars([C|Cs], [Ch|Chs]) :-
            integer(C), char_code(Ch, C), quads_codes_to_chars(Cs, Chs).

        quads_timed_outcome(G, O) :-
            (   time_out(call(G), 15000, R)
            ->  ( R == time_out -> O = timeout ; O = succeeds )
            ;   O = fails
            ).

        % The whole ball, as thrown. What of it counts is the matcher's
        % business, not this one's.
        quads_error_outcome(B, raised(B)).

        %! clear_quads | Quad tests | Forgets every loaded quad test; the next consult starts a fresh set.
        clear_quads :-
            retractall('$quad'(_, _, _, _)),
            retractall('$quad_open'(_, _)),
            retractall('$quad_seq'(_)),
            retractall('$quad_dropped'(_, _)),
            retractall('$quad_src'(_, _)),
            retractall('$quad_names'(_, _)),
            retractall('$quad_run'(_, _, _)),
            retractall('$quad_uncompared'(_)).

        quads_memberchk(X, [Y|T]) :- ( X == Y -> true ; quads_memberchk(X, T) ).
        quads_reverse(L, R) :- quads_rev_(L, [], R).
        quads_rev_([], A, A).
        quads_rev_([X|T], A, R) :- quads_rev_(T, [X|A], R).
        quads_length(L, N) :- quads_len_(L, 0, N).
        quads_len_([], N, N).
        quads_len_([_|T], N0, N) :- N1 is N0 + 1, quads_len_(T, N1, N).
        """;

    // library(dif) — a non-coroutining approximation. When the arguments are
    // decidably unequal it succeeds; when identical it fails; otherwise (they
    // could still unify, e.g. an unbound var vs a value) it optimistically
    // succeeds. The true dif/2 would delay; a program that later forces such a
    // pair equal would observe the difference. Sufficient for the common
    // "these are already bound / will never be unified" usage.
    // The real, SUSPENDING dif/2 lives in the coroutining library. This
    // entry used to be a decide-once stub — ( X \= Y -> true ; ... ) —
    // which silently FORGOT an undecided disequality: dif(A, B) with both
    // unbound succeeded and never failed anything later (Trealla
    // test0400/0402/0210 caught it over rational trees, where the real
    // dif already behaves).
    private const string Dif = """
        :- use_module(library(coroutining)).
        """;

    // library(atts) — the SICStus attributed-variable API (put_atts/get_atts
    // + the `:- attribute Spec` declaration) over the engine's native
    // per-module attribute-list primitives ('$put_to_attr_list' & co, the
    // ones the Scryer clpz certification exercised). The module a
    // put_atts/get_atts call belongs to is the module of the clause being
    // COMPILED — baked in by goal_expansion via prolog_load_context, which is
    // how Scryer's own atts.pl does it. Client modules define their
    // verify_attributes/3 hook; the engine's per-module dispatch (ADR-040)
    // finds it without registration.
    private const string Atts = """
        % get_attr/put_attr/del_attr are EXPORT-QUALIFIED (ADR-038): an
        % importer of library(atts) sees these hProlog-COMPAT wrappers —
        % get_attr(V, M, Val) is get_atts(V, Access) with Access =.. [M, Val],
        % the bridge Triska's solvers rely on (clpz's fd_get stores via
        % put_atts and reads via get_attr expecting the SAME value) — while
        % everyone else keeps the engine's raw-value builtins.
        :- module(atts, [get_attr/3, put_attr/3, del_attr/2,
                         put_atts/2, get_atts/2,
                         '$atts_put'/3, '$atts_get'/3]).
        :- multifile goal_expansion/2.
        :- multifile term_expansion/2.

        % The declaration op, in the USER layer (ADR-046 escape hatch): the
        % CLIENT's file is what parses `:- attribute frozen/1.`, so the op
        % must be visible outside this module's own layer.
        :- op(1199, fx, user:attribute).

        % `:- attribute f/1, g/2.` declares which attribute functors belong
        % to the declaring module. The engine keys attributes by (module,
        % functor) dynamically, so the declaration is metadata — accepted,
        % dropped.
        term_expansion((:- attribute(_)), []).

        goal_expansion(put_atts(V, Spec), '$atts_put'(V, M, Spec)) :-
            prolog_load_context(module, M).
        goal_expansion(get_atts(V, Spec), '$atts_get'(V, M, Spec)) :-
            prolog_load_context(module, M).

        %! put_atts(-Var, +AccessSpec) | Attributed variables | SICStus atts: sets attributes of Var per AccessSpec: +Attr (or bare Attr) adds/replaces, -Attr removes, a list applies each in order. The attribute's MODULE is the calling module, resolved at compile time.
        put_atts(V, Spec) :- '$atts_put'(V, user, Spec).

        %! get_atts(-Var, ?AccessSpec) | Attributed variables | SICStus atts: queries attributes of Var: +Attr (or bare Attr) unifies with the attribute, -Attr succeeds iff absent, an unbound AccessSpec returns the full list.
        get_atts(V, Spec) :- '$atts_get'(V, user, Spec).

        get_attr(V, M, Value) :-
            var(V), atom(M),
            Access =.. [M, Value],
            '$atts_get'(V, M, +Access).
        put_attr(V, M, Value) :-
            atom(M),
            Access =.. [M, Value],
            '$atts_put'(V, M, +Access).
        del_attr(V, M) :-
            ( var(V), atom(M) ->
                Access =.. [M, _],
                '$atts_put'(V, M, -Access)
            ; true
            ).

        '$atts_put'(_, _, Spec) :-
            var(Spec), !, throw(error(instantiation_error, put_atts/2)).
        '$atts_put'(V, M, [S|Ss]) :- !,
            '$atts_put'(V, M, S),
            ( Ss == [] -> true ; '$atts_put'(V, M, Ss) ).
        '$atts_put'(_, _, []) :- !.
        '$atts_put'(V, M, +A) :- !, '$put_to_attr_list'(V, M, A).
        '$atts_put'(V, M, -A) :- !, '$del_from_attr_list'(V, M, A).
        '$atts_put'(V, M, A) :- '$put_to_attr_list'(V, M, A).

        '$atts_get'(V, M, Spec) :-
            ( var(Spec) -> get_attr(V, M, Spec)
            ; '$atts_get_spec'(Spec, V, M)
            ).
        '$atts_get_spec'([], _, _) :- !.
        '$atts_get_spec'([S|Ss], V, M) :- !,
            '$atts_get_spec'(S, V, M), '$atts_get_spec'(Ss, V, M).
        '$atts_get_spec'(+A, V, M) :- !, '$get_from_attr_list'(V, M, A).
        '$atts_get_spec'(-A, V, M) :- !,
            functor(A, F, N), functor(Probe, F, N),
            \+ '$get_from_attr_list'(V, M, Probe).
        '$atts_get_spec'(A, V, M) :- '$get_from_attr_list'(V, M, A).
        """;

    // library('$project_atts') — Scryer's attribute-projection bootstrap
    // module. Real libraries reach it module-qualified
    // (`'$project_atts':term_residual_goals(Term, Rs)` — iso_ext.pl's
    // copy_term/3); a bare-global definition resolves that call through the
    // M:G fallback chain. Our implementation over the engine's attvar
    // machinery: collect Term's attributed variables, and for each variable ×
    // module project via the module's own attribute_goals//1 when it defines
    // one (the Scryer/SICStus convention — freeze, clpz), else fall back to
    // raw re-runnable put_atts/put_attr goals. A projection hook that
    // CONSUMES attributes as it emits (freeze's `put_atts(V, -frozen(_))`)
    // is safe: callers run this inside findall/3, whose backtracking undoes
    // the trailed attribute mutations.
    private const string ProjectAtts = """
        :- public term_residual_goals/2.
        term_residual_goals(Term, Goals) :-
            '$term_attributed_variables'(Term, Vs),
            '$prj_vars'(Vs, Goals, []).

        '$prj_vars'([], Gs, Gs).
        '$prj_vars'([V|Vs], Gs0, Gs) :-
            (   var(V)
            ->  '$attr_modules'(V, Ms), '$prj_mods'(Ms, V, Gs0, Gs1)
            ;   Gs1 = Gs0
            ),
            '$prj_vars'(Vs, Gs1, Gs).

        '$prj_mods'([], _, Gs, Gs).
        '$prj_mods'([M|Ms], V, Gs0, Gs) :-
            '$prj_one'(M, V, Gs0, Gs1),
            '$prj_mods'(Ms, V, Gs1, Gs).

        '$prj_one'(M, V, Gs0, Gs) :-
            (   catch(call(M:attribute_goals(V, Gs0, Gs)), _, fail)
            ->  true
            ;   get_attr(V, M, As)
            ->  '$prj_attr'(As, M, V, Gs0, Gs)
            ;   Gs0 = Gs
            ).

        '$prj_attr'(As, M, V, Gs0, Gs) :-
            (   is_list(As)
            ->  '$prj_raw'(As, M, V, Gs0, Gs)
            ;   Gs0 = [put_attr(V, M, As)|Gs]
            ).

        '$prj_raw'([], _, _, Gs, Gs).
        '$prj_raw'([A|As], M, V, [put_atts(V, M, A)|Gs1], Gs) :-
            '$prj_raw'(As, M, V, Gs1, Gs).
        """;

    // library(format) — the DCG-format non-terminal format_//2. Supports the
    // directives real programs use: ~s (char/code list, spliced verbatim),
    // ~d (integer), ~a (atom), ~w / ~q (write / writeq via
    // write_term_to_chars), ~n (newline), ~~ (literal tilde); any other
    // character is emitted literally. Self-contained (does not depend on
    // library(dcgs) load order).
    private const string Format = """
        :- public format_/4.
        format_([], _) --> [].
        format_(['~', 's' | Fs], [A | As]) --> !, '$fmt_seq'(A), format_(Fs, As).
        format_(['~', 'd' | Fs], [A | As]) --> !,
            { number_chars(A, Cs) }, '$fmt_seq'(Cs), format_(Fs, As).
        format_(['~', 'a' | Fs], [A | As]) --> !,
            { atom_chars(A, Cs) }, '$fmt_seq'(Cs), format_(Fs, As).
        format_(['~', 'w' | Fs], [A | As]) --> !,
            { write_term_to_chars(A, [], Cs) }, '$fmt_seq'(Cs), format_(Fs, As).
        format_(['~', 'q' | Fs], [A | As]) --> !,
            { write_term_to_chars(A, [quoted(true)], Cs) }, '$fmt_seq'(Cs), format_(Fs, As).
        format_(['~', 'n' | Fs], As) --> !, ['\n'], format_(Fs, As).
        format_(['~', '~' | Fs], As) --> !, ['~'], format_(Fs, As).
        format_([C | Fs], As) --> [C], format_(Fs, As).

        '$fmt_seq'([]) --> [].
        '$fmt_seq'([C | Cs]) --> [C], '$fmt_seq'(Cs).
        """;
}
