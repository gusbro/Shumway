namespace Shumway.Embedding;

/// <summary>The SWI compatibility shim — SWI system predicates that are not
/// standard/ISO and are provided only on demand: it loads AUTOMATICALLY the first
/// time an SWI-dialect module is loaded, and can also be loaded explicitly with
/// <c>use_module(library(swi))</c> (as SWI itself offers <c>library(sicstus)</c>).
/// A pure ISO program that never touches SWI never sees these predicates.
///
/// <para>The public predicates here are thin wrappers over always-available
/// C# helper builtins (<c>$nb_setarg</c>, <c>$same_term</c>,
/// <c>$copy_term_without_attr_vars</c>): the helpers are internal
/// (<c>$</c>-prefixed) so their global availability does not pollute the standard
/// namespace, while the SWI-named predicates only exist once the shim is
/// loaded.</para></summary>
internal static class SwiShim
{
    /// <summary>The module name a manual <c>use_module(library(swi))</c> resolves
    /// to (and the auto-load consults).</summary>
    public const string LibraryName = "swi";

    public const string Source = """
        % ----- destructive term modification (non-backtrackable) -----
        :- public nb_setarg/3.
        nb_setarg(Arg, Term, Value) :- '$nb_setarg'(Arg, Term, Value).
        :- public nb_linkarg/3.
        nb_linkarg(Arg, Term, Value) :- '$nb_setarg'(Arg, Term, Value).

        % ----- string / atom (case-insensitive substring) -----
        :- public sub_atom_icasechk/3.
        sub_atom_icasechk(Haystack, Before, Needle) :-
            '$sub_atom_icasechk'(Haystack, Before, Needle).

        % ----- code classification (the code counterpart of char_type/2) -----
        % The case-conversion types yield CODES (not chars, as char_type does);
        % everything else delegates to char_type on the corresponding character.
        :- public code_type/2.
        code_type(Code, Type) :- '$code_type_dispatch'(Type, Code).
        '$code_type_dispatch'(upper(Lower), Code) :- !, Code >= 0'A, Code =< 0'Z, Lower is Code + 32.
        '$code_type_dispatch'(lower(Upper), Code) :- !, Code >= 0'a, Code =< 0'z, Upper is Code - 32.
        % to_lower/to_upper are bidirectional in SWI: code_type(C, to_lower(X))
        % with C BOUND gives X = lowercase(C); with C UNBOUND and X bound it
        % binds C = lowercase(X) (url.pl's lwalpha lowercases that way).
        '$code_type_dispatch'(to_lower(Lower), Code) :- !,
            (   nonvar(Code) ->
                ( Code >= 0'A, Code =< 0'Z -> Lower is Code + 32 ; Lower = Code )
            ;   ( Lower >= 0'A, Lower =< 0'Z -> Code is Lower + 32 ; Code = Lower )
            ).
        '$code_type_dispatch'(to_upper(Upper), Code) :- !,
            (   nonvar(Code) ->
                ( Code >= 0'a, Code =< 0'z -> Upper is Code - 32 ; Upper = Code )
            ;   ( Upper >= 0'a, Upper =< 0'z -> Code is Upper - 32 ; Code = Upper )
            ).
        % SWI ctype names with no weight argument (dcg/basics tests digits via
        % code_type(C, digit)); our char_type only knows the digit(W) form.
        '$code_type_dispatch'(digit, Code) :- !, Code >= 0'0, Code =< 0'9.
        '$code_type_dispatch'(xdigit(W), Code) :- !,
            (   Code >= 0'0, Code =< 0'9 -> W is Code - 0'0
            ;   Code >= 0'a, Code =< 0'f -> W is Code - 0'a + 10
            ;   Code >= 0'A, Code =< 0'F,   W is Code - 0'A + 10
            ).
        '$code_type_dispatch'(csym, Code) :- !,
            (   Code >= 0'a, Code =< 0'z -> true
            ;   Code >= 0'A, Code =< 0'Z -> true
            ;   Code >= 0'0, Code =< 0'9 -> true
            ;   Code =:= 0'_
            ).
        '$code_type_dispatch'(csymf, Code) :- !,
            (   Code >= 0'a, Code =< 0'z -> true
            ;   Code >= 0'A, Code =< 0'Z -> true
            ;   Code =:= 0'_
            ).
        '$code_type_dispatch'(Type, Code) :- char_code(Char, Code), char_type(Char, Type).

        % ----- ansi terminal (colour ignored) -----
        % ansi_format(+Attributes, +Format, +Args): write the formatted text; the
        % colour/style attributes are ignored (no terminal styling here).
        :- public ansi_format/3.
        ansi_format(_Attributes, Format, Args) :- format(Format, Args).

        % ----- debugging (no-op) -----
        % We keep no interpreter backtrace to print here; succeed so libraries that
        % call it for diagnostics keep running.
        :- public backtrace/1.
        backtrace(_).

        % ----- kernel directives accepted as no-ops -----
        % '$clausable'(PI): SWI marks a static predicate as clause/2-inspectable
        % (library(error) does it for has_type/2). No such gate here.
        :- public '$clausable'/1.
        '$clausable'(_).
        % noprofile(PI): profiler exclusion hint (library(exceptions)). No profiler.
        :- public noprofile/1.
        noprofile(_).
        % '$hide'(PI): tracer exclusion hint. No SWI tracer.
        :- public '$hide'/1.
        '$hide'(_).
        % '$notransact'(PIs): transaction exclusion hint. No transactions.
        :- public '$notransact'/1.
        '$notransact'(_).
        % record.pl's expansion emits `(:- record('<compiled>'))` as an xref
        % marker; accept EXACTLY that atom so it no-ops instead of raising.
        % Any real (mis)use of record/1 as a goal still errors.
        :- public record/1.
        record('<compiled>').

        % create_prolog_flag(Flag, Value, Options): registers an application
        % flag (ansi_term's color_term, settings). Accepted, not stored — a
        % later current_prolog_flag on it fails (= flag unset), which the
        % callers treat as the conservative default.
        :- public create_prolog_flag/3.
        create_prolog_flag(_, _, _).

        % ----- message system (translate_message//1 + print_message_lines/3) -----
        % A pragmatic subset of SWI's $messages: translate a message term into a
        % list of message elements (Format-Args, nl, atoms), and render such a list
        % to a stream. Libraries call '$messages':translate_message(...) (module-
        % qualified, resolved to this bare-global definition) and print_message_lines.
        :- public translate_message/3.
        translate_message(Term) --> { var(Term) }, !, [ '~w'-[Term] ].
        translate_message(error(Formal, _Context)) --> !, translate_formal(Formal).
        translate_message(debug(Fmt, Args)) --> !, [ Fmt-Args ].
        translate_message(format(Fmt, Args)) --> !, [ Fmt-Args ].
        translate_message(Fmt-Args) --> { is_list(Args) }, !, [ Fmt-Args ].
        translate_message(Term) --> [ '~w'-[Term] ].

        translate_formal(type_error(Type, Culprit)) --> !,
            [ 'Type error: `~w'' expected, found `~w'''-[Type, Culprit] ].
        translate_formal(domain_error(Domain, Culprit)) --> !,
            [ 'Domain error: `~w'' expected, found `~w'''-[Domain, Culprit] ].
        translate_formal(existence_error(Type, Culprit)) --> !,
            [ 'Unknown ~w: ~w'-[Type, Culprit] ].
        translate_formal(instantiation_error) --> !,
            [ 'Arguments are not sufficiently instantiated'-[] ].
        translate_formal(permission_error(Op, Type, Culprit)) --> !,
            [ 'No permission to ~w ~w `~w'''-[Op, Type, Culprit] ].
        translate_formal(evaluation_error(What)) --> !,
            [ 'Arithmetic: evaluation error: `~w'''-[What] ].
        translate_formal(representation_error(What)) --> !,
            [ 'Cannot represent due to `~w'''-[What] ].
        translate_formal(syntax_error(What)) --> !,
            [ 'Syntax error: ~w'-[What] ].
        translate_formal(Formal) --> [ '~w'-[Formal] ].

        :- public print_message_lines/3.
        print_message_lines(Stream0, _Kind, Lines) :-
            ( Stream0 == current_output -> current_output(Stream)
            ; Stream0 == current_input -> current_input(Stream)
            ; Stream = Stream0
            ),
            '$pml'(Lines, Stream).
        '$pml'([], _).
        '$pml'([E|Es], S) :- '$pml_elem'(E, S), '$pml'(Es, S).
        '$pml_elem'(Fmt-Args, S) :- !, format(S, Fmt, Args).
        '$pml_elem'(nl, S) :- !, nl(S).
        '$pml_elem'(flush, S) :- !, flush_output(S).
        '$pml_elem'(ansi(_, Fmt, Args), S) :- !, format(S, Fmt, Args).
        '$pml_elem'(ansi(_, Fmt, Args, _), S) :- !, format(S, Fmt, Args).
        '$pml_elem'(url(URL), S) :- !, write(S, URL).
        '$pml_elem'(url(_, Label), S) :- !, write(S, Label).
        '$pml_elem'(at_same_line, _) :- !.
        '$pml_elem'(A, S) :- atom(A), !, write(S, A).
        '$pml_elem'(A, S) :- string(A), !, write(S, A).
        '$pml_elem'(_, _).

        % message_to_codes(+Kind, +Term, -Codes) — the codes of a translated message.
        :- public message_to_codes/3.
        message_to_codes(_Kind, Term, Codes) :-
            phrase(translate_message(Term), Lines),
            with_output_to(atom(A), print_message_lines(current_output, kind(_), Lines)),
            atom_codes(A, Codes).

        % ----- term copying / identity -----
        :- public copy_term_nat/2.
        copy_term_nat(Term, Copy) :- '$copy_term_without_attr_vars'(Term, Copy).
        :- public duplicate_term/2.
        duplicate_term(Term, Copy) :- copy_term(Term, Copy).
        :- public same_term/2.
        same_term(A, B) :- '$same_term'(A, B).

        % compound_name_arguments(?Compound, ?Name, ?Args): =.. restricted to
        % compounds (lazy_lists' iterator-macro expansion builds goals with it).
        :- public compound_name_arguments/3.
        compound_name_arguments(C, Name, Args) :- C =.. [Name | Args].

        % '$filled_array'(-Array, +Name, +Size, +Value): a compound Name/Size
        % with every argument unified with Value (nb_set's bucket arrays; Value
        % is typically one shared fresh variable used as the empty marker).
        :- public '$filled_array'/4.
        '$filled_array'(Array, Name, Size, Value) :-
            functor(Array, Name, Size),
            '$fill_args'(Size, Array, Value).
        '$fill_args'(0, _, _) :- !.
        '$fill_args'(I, Array, Value) :-
            arg(I, Array, Value),
            I1 is I - 1,
            '$fill_args'(I1, Array, Value).

        % variant_hash(+Term, -Hash): a hash equal for variant terms (nb_set's
        % bucket index). Canonicalise (copy + numbervars + written form), then
        % fold the codes. Quality is bucket-grade, not cryptographic.
        :- public variant_hash/2.
        variant_hash(Term, Hash) :-
            copy_term(Term, C),
            numbervars(C, 0, _),
            term_to_atom(C, A),
            atom_codes(A, Codes),
            '$vh_fold'(Codes, 5381, Hash).
        '$vh_fold'([], H, H).
        '$vh_fold'([C|Cs], H0, H) :-
            H1 is (H0 * 31 + C) mod 1152921504606846976,
            '$vh_fold'(Cs, H1, H).

        % ----- tries (minimal shim over the dynamic store) -----
        % SWI's solution_sequences (distinct/1,2) dedups via kernel tries. This
        % shim gives the variant-set behaviour those callers need: trie_insert/2
        % succeeds only the FIRST time a variant of Term is added. Keys are the
        % written form of the numbervar'd copy. Not backtrackable (like SWI's).
        :- dynamic('$shim_trie_kv'/2).
        :- public trie_new/1.
        trie_new('$shim_trie'(Ref)) :- gensym('$trie', Ref).
        :- public trie_insert/2.
        trie_insert('$shim_trie'(Ref), Term) :-
            copy_term(Term, C),
            numbervars(C, 0, _),
            term_to_atom(C, Key),
            ( '$shim_trie_kv'(Ref, Key) -> fail ; assertz('$shim_trie_kv'(Ref, Key)) ).
        :- public trie_destroy/1.
        trie_destroy('$shim_trie'(Ref)) :- retractall('$shim_trie_kv'(Ref, _)).

        % ----- library(arithmetic) native-override stubs -----
        % User-defined evaluable functions need SWI's global goal_expansion +
        % module introspection — unsupported. The declaration is accepted (and
        % ignored); runtime evaluation covers the BUILTIN evaluables only.
        :- public arithmetic_function/1.
        arithmetic_function(_).
        :- public arithmetic_expression_value/2.
        arithmetic_expression_value(QExpr, Value) :-
            ( QExpr = _:Expr -> true ; Expr = QExpr ),
            Value is Expr.

        % ----- arithmetic-function introspection -----
        % We do not support user-defined arithmetic functions, so this reports the
        % built-in evaluable functors only.
        :- public current_arithmetic_function/1.
        current_arithmetic_function(Head) :-
            ( nonvar(Head) ->
                functor(Head, Name, Arity),
                '$arith_function'(Name, Arity)
            ;   % enumeration mode (library(arithmetic) generates its eval
                % clauses from it): yield each evaluable as a most-general term
                '$arith_function'(Name, Arity),
                functor(Head, Name, Arity)
            ).
        '$arith_function'(+, 2). '$arith_function'(-, 2). '$arith_function'(*, 2).
        '$arith_function'(/, 2). '$arith_function'(//, 2). '$arith_function'(mod, 2).
        '$arith_function'(rem, 2). '$arith_function'(div, 2). '$arith_function'(gcd, 2).
        '$arith_function'(min, 2). '$arith_function'(max, 2). '$arith_function'(**, 2).
        '$arith_function'(^, 2). '$arith_function'(>>, 2). '$arith_function'(<<, 2).
        '$arith_function'(/\, 2). '$arith_function'(\/, 2). '$arith_function'(xor, 2).
        '$arith_function'(atan2, 2). '$arith_function'(atan, 2). '$arith_function'(copysign, 2).
        '$arith_function'(truncate, 1). '$arith_function'(round, 1). '$arith_function'(ceiling, 1).
        '$arith_function'(floor, 1). '$arith_function'(integer, 1). '$arith_function'(float, 1).
        '$arith_function'(float_integer_part, 1). '$arith_function'(float_fractional_part, 1).
        '$arith_function'(abs, 1). '$arith_function'(sign, 1). '$arith_function'(sqrt, 1).
        '$arith_function'(sin, 1). '$arith_function'(cos, 1). '$arith_function'(tan, 1).
        '$arith_function'(asin, 1). '$arith_function'(acos, 1). '$arith_function'(exp, 1).
        '$arith_function'(log, 1). '$arith_function'(-, 1). '$arith_function'(+, 1).
        '$arith_function'(\, 1). '$arith_function'(msb, 1).
        '$arith_function'(pi, 0). '$arith_function'(e, 0). '$arith_function'(epsilon, 0).
        """;
}
