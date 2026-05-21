namespace Shumway.Embedding;

/// <summary>
/// Internal Prolog prelude that <see cref="PrologEngine"/> consults at
/// construction time. Lives in a private module so its predicates are
/// declared <c>:- public</c> from the prelude's perspective but never
/// collide with user code, and are not subject to module-aware mangling
/// the same way user-defined locals are.
///
/// <para>The prelude is the home for predicates that benefit from
/// Prolog-level backtracking: <c>member/2</c>, <c>clause/2</c>, and
/// <c>current_predicate/1</c> all enumerate solutions, so they're far
/// nicer expressed as a pair of clauses than as a builtin that has to
/// fake backtracking. The Prolog-level definitions ride the standard
/// WAM choice-point machinery without any builtin-CP plumbing.</para>
///
/// <para>The C# side keeps a couple of "all matches" helper builtins
/// (<c>'$all_clauses_of'/2</c>, <c>'$all_predicate_indicators'/1</c>)
/// that bridge from the engine's clause and functor tables into a
/// Prolog list — Prolog's <c>member/2</c> then enumerates the list
/// with normal backtracking.</para>
/// </summary>
internal static class Prelude
{
    public const string ModuleName = "$prelude";

    public const string Source = """
        :- module('$prelude').
        :- public member/2.
        :- public clause/2.
        :- public current_predicate/1.
        :- public length/2.
        :- public sub_atom/5.
        :- public maplist/2.
        :- public maplist/3.
        :- public maplist/4.
        :- public foldl/4.
        :- public foldl/5.
        :- public aggregate_all/3.
        :- public copy_term/3.
        :- public select/3.
        :- public permutation/2.
        :- public memberchk/2.
        :- public subtract/3.
        :- public intersection/3.
        :- public union/3.
        :- public delete/3.
        :- public numlist/3.
        :- public sum_list/2.
        :- public max_list/2.
        :- public min_list/2.
        :- public max_member/2.
        :- public min_member/2.
        :- public include/3.
        :- public exclude/3.
        :- public partition/4.
        :- public pairs_keys_values/3.
        :- public predsort/3.
        :- public sort/4.
        :- public atomic_list_concat/2.
        :- public atomic_list_concat/3.
        :- public char_type/2.
        :- public '$call_conj'/3.
        :- public '$call_disj'/3.
        :- public '$call_arrow'/3.
        :- public '$call_neg'/1.
        :- dynamic attribute_goals/4.

        %! member(?Elem, ?List) | Lists | Succeeds when Elem is a member of List; enumerates members on backtracking.
        member(X, [X|_]).
        member(X, [_|T]) :- member(X, T).

        % Control constructs reached through a runtime call/1 goal (chunk
        % 86). A control construct written directly in a clause body never
        % gets here — the MetaTransform rewrites it at compile time; the
        % interpreter's call dispatch routes ,/2 ;/2 ->/2 \+/1 to these
        % plainly-named helpers (operator atoms are awkward to declare).
        % K is the cut barrier of the enclosing call (chunk 88): '$call'/2
        % re-enters call dispatch carrying it, so a `!` inside a runtime
        % compound goal commits exactly as far as the call — no further.
        % ,/2 ;/2 and the then/else of ->/2 are cut-transparent (they pass
        % K on); a ->/2 condition and \+/1 are opaque, so they use call/1.
        '$call_conj'(A, B, K) :- '$call'(A, K), '$call'(B, K).
        '$call_disj'((C -> T), E, K) :- !, ( call(C) -> '$call'(T, K) ; '$call'(E, K) ).
        '$call_disj'(A, _, K) :- '$call'(A, K).
        '$call_disj'(_, B, K) :- '$call'(B, K).
        '$call_arrow'(C, T, K) :- call(C), !, '$call'(T, K).
        '$call_neg'(G) :- ( call(G) -> fail ; true ).

        %! clause(+Head, ?Body) | Database | Enumerates the clauses (Head :- Body) of a predicate.
        clause(H, B) :-
            nonvar(H),
            '$all_clauses_of'(H, Pairs),
            member(H-B, Pairs).

        %! current_predicate(?PredicateIndicator) | Database | Enumerates the defined predicates as Name/Arity indicators.
        current_predicate(I) :-
            '$check_predicate_indicator'(I),
            '$all_predicate_indicators'(All),
            member(I, All).

        '$check_predicate_indicator'(I) :- var(I), !.
        '$check_predicate_indicator'(_/_) :- !.
        '$check_predicate_indicator'(I) :-
            throw(error(type_error(predicate_indicator, I), _)).

        %! length(?List, ?Length) | Lists | Relates a list to its length; enumerates lists of growing length when both arguments are unbound.
        length(L, N) :-
            nonvar(L), !, '$list_length'(L, N).
        length(L, N) :-
            integer(N), !, '$make_var_list'(N, L).
        length(L, N) :- '$length_enum'(L, N, 0).

        '$length_enum'([], N, N).
        '$length_enum'([_|T], N, Acc) :-
            Acc1 is Acc + 1,
            '$length_enum'(T, N, Acc1).

        %! sub_atom(+Atom, ?Before, ?Length, ?After, ?SubAtom) | Atoms & strings | Backtracks over every (Before, Length, After, SubAtom) decomposition of an atom.
        sub_atom(Atom, Before, Length, After, Sub) :-
            '$sub_atom_decompositions'(Atom, Decomps),
            member([Before, Length, After, Sub], Decomps).

        %! maplist(:Goal, ?List) | Lists | Succeeds if Goal holds for every element of List.
        maplist(_, []).
        maplist(G, [X|Xs]) :- call(G, X), maplist(G, Xs).

        %! maplist(:Goal, ?List1, ?List2) | Lists | Succeeds if Goal holds for corresponding elements of two lists.
        maplist(_, [], []).
        maplist(G, [X|Xs], [Y|Ys]) :- call(G, X, Y), maplist(G, Xs, Ys).

        %! maplist(:Goal, ?List1, ?List2, ?List3) | Lists | Succeeds if Goal holds for corresponding elements of three lists.
        maplist(_, [], [], []).
        maplist(G, [X|Xs], [Y|Ys], [Z|Zs]) :-
            call(G, X, Y, Z), maplist(G, Xs, Ys, Zs).

        %! foldl(:Goal, ?List, +V0, -V) | Lists | Folds Goal over a list, threading an accumulator from V0 to V.
        foldl(_, [], Acc, Acc).
        foldl(G, [X|Xs], Acc, Out) :-
            call(G, X, Acc, Acc1),
            foldl(G, Xs, Acc1, Out).

        %! foldl(:Goal, ?List1, ?List2, +V0, -V) | Lists | Folds Goal over two lists, threading an accumulator from V0 to V.
        foldl(_, [], [], Acc, Acc).
        foldl(G, [X|Xs], [Y|Ys], Acc, Out) :-
            call(G, X, Y, Acc, Acc1),
            foldl(G, Xs, Ys, Acc1, Out).

        %! aggregate_all(+Template, :Goal, -Result) | Findall & aggregation | Aggregates Goal's solutions with a count, sum, bag or set template.
        aggregate_all(count, Goal, Count) :-
            findall(t, Goal, L),
            length(L, Count).
        aggregate_all(sum(Expr), Goal, Sum) :-
            findall(Expr, Goal, L),
            '$sum_list'(L, 0, Sum).
        aggregate_all(bag(X), Goal, Bag) :- findall(X, Goal, Bag).
        aggregate_all(set(X), Goal, Set) :-
            findall(X, Goal, L),
            sort(L, Set).

        '$sum_list'([], Acc, Acc).
        '$sum_list'([H|T], Acc, Out) :-
            Acc1 is Acc + H,
            '$sum_list'(T, Acc1, Out).

        % Residual-constraint projection (chunk 81). copy_term/3 copies a
        % term and, for every attributed variable in it, collects the
        % goals each module's attribute_goals/4 hook produces — already
        % re-expressed over the copy's variables. '$copy_term_3_prep'/3
        % does the structural copy and hands back ag(Module, Attr, Var)
        % triples; attribute_goals/4 is pre-declared dynamic so user
        % clauses simply join it and a hook-less program still links.
        %! copy_term(+Term, -Copy, -Goals) | Term inspection & construction | Copies a term with fresh variables and collects the residual attribute goals.
        copy_term(Term, Copy, Goals) :-
            '$copy_term_3_prep'(Term, Copy, AttrInfo),
            '$attr_goals_of'(AttrInfo, Goals).

        '$attr_goals_of'([], []).
        '$attr_goals_of'([ag(M, A, V)|Rest], Goals) :-
            ( attribute_goals(M, A, V, G) -> true ; G = [] ),
            '$attr_goals_of'(Rest, RestGoals),
            append(G, RestGoals, Goals).

        % ===== common list-library predicates (chunk 96) =====

        %! select(?Elem, ?List, ?Rest) | Lists | Rest is List with one occurrence of Elem removed; backtracks over occurrences.
        select(X, [X|T], T).
        select(X, [H|T], [H|R]) :- select(X, T, R).

        %! permutation(?List, ?Permutation) | Lists | True when the two lists are permutations of each other; enumerates permutations.
        permutation([], []).
        permutation(L, [X|P]) :- select(X, L, R), permutation(R, P).

        %! memberchk(?Elem, +List) | Lists | Like member/2 but succeeds at most once — no backtracking over further matches.
        memberchk(X, [Y|T]) :- ( X = Y -> true ; memberchk(X, T) ).

        %! subtract(+Set, +Delete, -Rest) | Lists | Rest is Set without the elements that also occur in Delete.
        subtract([], _, []).
        subtract([H|T], D, R) :-
            ( memberchk(H, D) -> R = R1 ; R = [H|R1] ),
            subtract(T, D, R1).

        %! intersection(+Set1, +Set2, -Intersection) | Lists | Intersection holds the elements of Set1 that also occur in Set2.
        intersection([], _, []).
        intersection([H|T], S2, R) :-
            ( memberchk(H, S2) -> R = [H|R1] ; R = R1 ),
            intersection(T, S2, R1).

        %! union(+Set1, +Set2, -Union) | Lists | Union holds the elements of Set1 not in Set2, followed by all of Set2.
        union([], S2, S2).
        union([H|T], S2, R) :-
            ( memberchk(H, S2) -> R = R1 ; R = [H|R1] ),
            union(T, S2, R1).

        %! delete(+List, +Elem, -Rest) | Lists | Rest is List with every element that unifies with Elem removed.
        delete([], _, []).
        delete([H|T], X, R) :-
            ( H \= X -> R = [H|R1] ; R = R1 ),
            delete(T, X, R1).

        %! numlist(+Low, +High, -List) | Lists | List is the consecutive integers from Low to High inclusive.
        numlist(L, H, List) :-
            ( L =< H -> L1 is L + 1, List = [L|Rest], numlist(L1, H, Rest)
            ; List = []
            ).

        %! sum_list(+List, -Sum) | Lists | Sum is the sum of the numbers in List.
        sum_list(L, S) :- '$sum_list'(L, 0, S).

        %! max_list(+List, -Max) | Lists | Max is the largest number in the non-empty list.
        max_list([H|T], M) :- '$maxlist'(T, H, M).
        '$maxlist'([], M, M).
        '$maxlist'([H|T], A, M) :- ( H > A -> A1 = H ; A1 = A ), '$maxlist'(T, A1, M).

        %! min_list(+List, -Min) | Lists | Min is the smallest number in the non-empty list.
        min_list([H|T], M) :- '$minlist'(T, H, M).
        '$minlist'([], M, M).
        '$minlist'([H|T], A, M) :- ( H < A -> A1 = H ; A1 = A ), '$minlist'(T, A1, M).

        %! max_member(?Max, +List) | Lists | Max is the largest element of List in the standard order of terms.
        max_member(Max, [H|T]) :- '$maxmember'(T, H, Max).
        '$maxmember'([], M, M).
        '$maxmember'([H|T], A, M) :- ( H @> A -> A1 = H ; A1 = A ), '$maxmember'(T, A1, M).

        %! min_member(?Min, +List) | Lists | Min is the smallest element of List in the standard order of terms.
        min_member(Min, [H|T]) :- '$minmember'(T, H, Min).
        '$minmember'([], M, M).
        '$minmember'([H|T], A, M) :- ( H @< A -> A1 = H ; A1 = A ), '$minmember'(T, A1, M).

        %! include(:Goal, +List, -Included) | Lists | Included holds the elements of List for which Goal succeeds.
        include(_, [], []).
        include(G, [H|T], R) :-
            ( call(G, H) -> R = [H|R1] ; R = R1 ),
            include(G, T, R1).

        %! exclude(:Goal, +List, -Excluded) | Lists | Excluded holds the elements of List for which Goal fails.
        exclude(_, [], []).
        exclude(G, [H|T], R) :-
            ( call(G, H) -> R = R1 ; R = [H|R1] ),
            exclude(G, T, R1).

        %! partition(:Goal, +List, -Included, -Excluded) | Lists | Splits List by whether Goal succeeds on each element.
        partition(_, [], [], []).
        partition(G, [H|T], I, E) :-
            ( call(G, H) -> I = [H|I1], E = E1 ; I = I1, E = [H|E1] ),
            partition(G, T, I1, E1).

        %! pairs_keys_values(?Pairs, ?Keys, ?Values) | Lists | Relates a list of Key-Value pairs to its lists of keys and values.
        pairs_keys_values([], [], []).
        pairs_keys_values([K-V|Ps], [K|Ks], [V|Vs]) :-
            pairs_keys_values(Ps, Ks, Vs).

        %! predsort(:Pred, +List, -Sorted) | Lists | Sorts List by a three-way comparison predicate, dropping elements compared equal.
        predsort(P, List, Sorted) :- '$predsort_all'(List, P, [], Sorted).
        '$predsort_all'([], _, Acc, Acc).
        '$predsort_all'([H|T], P, Acc, Sorted) :-
            '$predsort_ins'(Acc, P, H, Acc1),
            '$predsort_all'(T, P, Acc1, Sorted).
        '$predsort_ins'([], _, X, [X]).
        '$predsort_ins'([Y|Ys], P, X, Out) :-
            call(P, Ord, X, Y),
            ( Ord == '<' -> Out = [X, Y|Ys]
            ; Ord == '=' -> Out = [Y|Ys]
            ; Out = [Y|Out1], '$predsort_ins'(Ys, P, X, Out1)
            ).

        %! sort(+Key, +Order, +List, -Sorted) | Lists | Sorts List by the given argument key (0 = whole term) and order (@<, @=<, @> or @>=).
        sort(Key, Order, List, Sorted) :-
            '$sort4_tag'(List, Key, 0, Tagged),
            msort(Tagged, Asc),
            ( ( Order == '@<' ; Order == '@>' ) -> '$sort4_dedup'(Asc, Uniq)
            ; Uniq = Asc
            ),
            ( ( Order == '@>' ; Order == '@>=' ) -> reverse(Uniq, Ordered)
            ; Ordered = Uniq
            ),
            '$sort4_elems'(Ordered, Sorted).
        '$sort4_tag'([], _, _, []).
        '$sort4_tag'([E|Es], Key, I, [k(K, I, E)|Ps]) :-
            ( Key =:= 0 -> K = E ; arg(Key, E, K) ),
            I1 is I + 1,
            '$sort4_tag'(Es, Key, I1, Ps).
        '$sort4_elems'([], []).
        '$sort4_elems'([k(_, _, E)|Ps], [E|Es]) :- '$sort4_elems'(Ps, Es).
        '$sort4_dedup'([], []).
        '$sort4_dedup'([k(K, I, E)|T], [k(K, I, E)|R]) :- '$sort4_skip'(T, K, R).
        '$sort4_skip'([], _, []).
        '$sort4_skip'([k(K2, I2, E2)|T], K, R) :-
            ( K2 == K -> '$sort4_skip'(T, K, R)
            ; R = [k(K2, I2, E2)|R1], '$sort4_skip'(T, K2, R1)
            ).

        % ===== atom / number conversion (chunk 97) =====
        % atom_number/2 and number_string/2 are C# builtins (parse-or-fail
        % via TryParse); atomic_list_concat/2,3 and char_type/2 are below.

        % render an atomic term (atom, number or string) as an atom.
        '$atomic_to_atom'(X, X) :- atom(X), !.
        '$atomic_to_atom'(X, A) :- number(X), !, number_codes(X, Cs), atom_codes(A, Cs).
        '$atomic_to_atom'(X, A) :- atom_string(A, X).

        %! atomic_list_concat(+List, -Atom) | Atoms & strings | Concatenates a list of atomic terms into a single atom.
        atomic_list_concat([], '').
        atomic_list_concat([X|Xs], Atom) :-
            '$atomic_to_atom'(X, AX),
            atomic_list_concat(Xs, Rest),
            atom_concat(AX, Rest, Atom).

        %! atomic_list_concat(?List, +Separator, ?Atom) | Atoms & strings | Joins a list of atomics with a separator, or splits an atom on the separator.
        atomic_list_concat(List, Sep, Atom) :-
            var(List), nonvar(Atom), Sep \== '', !,
            '$alc_split'(Atom, Sep, List).
        atomic_list_concat([], _, '').
        atomic_list_concat([X], _, Atom) :- !, '$atomic_to_atom'(X, Atom).
        atomic_list_concat([X, Y|Xs], Sep, Atom) :-
            '$atomic_to_atom'(X, AX),
            atomic_list_concat([Y|Xs], Sep, Rest),
            atom_concat(AX, Sep, P),
            atom_concat(P, Rest, Atom).

        % split Atom at each occurrence of the separator Sep.
        '$alc_split'(Atom, Sep, Parts) :-
            ( '$alc_first_sep'(Atom, Sep, B, A) ->
                sub_atom(Atom, 0, B, _, Head),
                sub_atom(Atom, _, A, 0, Tail),
                Parts = [Head|Rest],
                '$alc_split'(Tail, Sep, Rest)
            ; Parts = [Atom]
            ).
        % Before / After of the leftmost occurrence of Sep in Atom.
        '$alc_first_sep'(Atom, Sep, B, A) :-
            findall(B0, sub_atom(Atom, B0, _, _, Sep), Bs),
            Bs = [_|_],
            min_list(Bs, B),
            sub_atom(Atom, B, _, A, Sep).

        %! char_type(+Char, ?Type) | Atoms & strings | Tests or computes a character's type — alpha, alnum, digit(W), space, upper(L), to_lower(L), and so on (ASCII range).
        char_type(Char, Type) :- char_code(Char, Code), '$char_type'(Type, Code).

        '$char_type'(alpha, Code) :- '$ascii_alpha'(Code).
        '$char_type'(alnum, Code) :- ( '$ascii_alpha'(Code) -> true ; '$ascii_digit'(Code) ).
        '$char_type'(digit(W), Code) :- '$ascii_digit'(Code), W is Code - 48.
        '$char_type'(space, Code) :- '$ascii_space'(Code).
        '$char_type'(white, Code) :- ( Code =:= 32 -> true ; Code =:= 9 ).
        '$char_type'(end_of_line, Code) :- ( Code =:= 10 -> true ; Code =:= 13 ).
        '$char_type'(punct, Code) :-
            Code >= 33, Code =< 126,
            \+ '$ascii_alpha'(Code), \+ '$ascii_digit'(Code).
        '$char_type'(csym, Code) :-
            ( '$ascii_alpha'(Code) -> true ; '$ascii_digit'(Code) -> true ; Code =:= 95 ).
        '$char_type'(csymf, Code) :-
            ( '$ascii_alpha'(Code) -> true ; Code =:= 95 ).
        '$char_type'(upper(L), Code) :-
            Code >= 65, Code =< 90, LC is Code + 32, char_code(L, LC).
        '$char_type'(lower(U), Code) :-
            Code >= 97, Code =< 122, UC is Code - 32, char_code(U, UC).
        '$char_type'(to_lower(L), Code) :-
            ( Code >= 65, Code =< 90 -> LC is Code + 32 ; LC = Code ),
            char_code(L, LC).
        '$char_type'(to_upper(U), Code) :-
            ( Code >= 97, Code =< 122 -> UC is Code - 32 ; UC = Code ),
            char_code(U, UC).

        '$ascii_alpha'(C) :- C >= 65, C =< 90, !.
        '$ascii_alpha'(C) :- C >= 97, C =< 122.
        '$ascii_digit'(C) :- C >= 48, C =< 57.
        '$ascii_space'(32) :- !.
        '$ascii_space'(C) :- C >= 9, C =< 13.
        """;
}
