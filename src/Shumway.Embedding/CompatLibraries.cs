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

    // library(dcgs) — the generic DCG helpers. phrase/2,3 live in the prelude.
    private const string Dcgs = """
        :- public seq/3.
        seq([]) --> [].
        seq([X|Xs]) --> [X], seq(Xs).

        :- public '...'/2.
        '...' --> [].
        '...' --> [_], '...'.
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
