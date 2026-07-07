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
        :- public forall/2.
        :- public catch/3.
        :- public '$catch_run'/1.
        :- public copy_term/3.
        :- public subsumes_term/2.
        :- public select/3.
        :- public permutation/2.
        :- public memberchk/2.
        :- public subtract/3.
        :- public intersection/3.
        :- public union/3.
        :- public delete/3.
        :- public numlist/3.
        :- public sum_list/2.
        :- public sumlist/2.
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
        :- public once/1.
        :- public ignore/1.
        :- public chdir/1.
        :- public append/2.
        :- public ':'/2.
        :- public phrase/2.
        :- public phrase/3.
        :- public display/1.
        :- public display/2.
        :- public recorda/2.
        :- public recordz/2.
        :- public recorded/2.
        :- public tab/1.
        :- public apply/2.
        :- public findall/4.
        :- public retractall/1.
        :- public listing/0.
        :- public listing/1.
        :- public format/1.
        :- public format_to_atom/3.
        :- public '$call_conj'/3.
        :- public '$call_disj'/3.
        :- public '$call_arrow'/3.
        :- public '$call_neg'/1.
        :- dynamic attribute_goals/4.
        :- dynamic '$tbl_running'/0.
        :- dynamic '$tbl_subgoal'/4.
        :- dynamic '$tbl_ans'/2.
        :- dynamic '$tbl_delta'/2.
        :- dynamic '$tbl_newd'/2.
        :- dynamic '$tbl_fresh'/1.
        :- dynamic '$tbl_mode'/1.
        :- dynamic '$tbl_neg_cache'/2.
        :- dynamic '$wfs_mode'/0.
        :- dynamic '$wfs_active'/0.
        :- dynamic '$wfs_k'/1.
        :- public '$table_call'/3.
        :- public '$tbl_dispatch'/3.
        :- public '$tbl_consume'/3.
        :- public '$tbl_negate'/1.
        :- public abolish_all_tables/0.
        :- public abolish_table/1.
        :- public well_founded/2.

        %! member(?Elem, ?List) | Lists | Succeeds when Elem is a member of List; enumerates members on backtracking.
        % First-argument-indexed "look-ahead" form (the GNU Prolog
        % library shape). '$member3' dispatches on its first argument —
        % the list tail — so when the tail is [] only the var-headed
        % first clause is in the index bucket and no choice point is
        % left. That makes the LAST element of a fixed list deterministic
        % (no residual CP), so the top-level finishes without a spurious
        % ';' prompt, exactly like gprolog. The naive
        % member(X,[X|_]) / member(X,[_|T]) form leaves a CP on every
        % element because both clauses' list argument is just [_|_].
        member(X, [Y|T]) :- '$member3'(T, X, Y).
        '$member3'(_, X, X).
        '$member3'([Y|T], X, _) :- '$member3'(T, X, Y).

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

        %! forall(:Condition, :Action) | Control | Succeeds if Action holds for every solution of Condition.
        % \+ (Condition, \+ Action). Condition and Action run in the LIVE engine
        % via call/1 (so their side effects are visible to the caller). A
        % statically-callable forall/2 is rewritten inline to this same negation
        % pair by MetaTransform; this clause is the runtime fallback for a
        % variable Condition/Action (it must NOT use an isolated sub-engine —
        % that would hide the called goals' assert/retract).
        forall(Cond, Action) :- \+ ( call(Cond), \+ call(Action) ).

        %! catch(:Goal, ?Catcher, :Recovery) | Control | Runs Goal; if it throws a ball unifying Catcher, runs Recovery instead.
        % Runs in the LIVE engine using the chunk-85 catch-frame machinery
        % ('$catch_begin' pushes a frame carrying Catcher + a recovery goal;
        % the engine's throw handler unwinds to it and dispatches the recovery).
        % A statically-callable catch/3 is rewritten inline by MetaTransform to
        % the same shape with generated helpers; this clause is the runtime
        % fallback for a variable Goal/Recovery. '$catch_run'/1 wraps the
        % (arbitrary, possibly control-construct) Recovery so the recovery
        % dispatch resolves a real predicate address and call/1 then runs it.
        catch(Goal, Catcher, Recovery) :-
            '$catch_begin'(Catcher, '$catch_run'(Recovery)),
            call(Goal),
            '$catch_end'.
        '$catch_run'(Recovery) :- call(Recovery).

        %! clause(+Head, ?Body) | Database | Enumerates the clauses (Head :- Body) of a predicate.
        % '$clause_enum' yields matching clauses lazily (a backtrackable
        % builtin): only the candidate being tried is materialised on the heap,
        % instead of building the whole O(#clauses) Head-Body pair list up front
        % for member/2 to walk. The Head-Body pair is built here so its
        % variables are the caller's.
        clause(H, B) :-
            nonvar(H),
            '$clause_enum'(H, H-B).

        %! current_predicate(?PredicateIndicator) | Database | Enumerates the defined predicates as Name/Arity indicators.
        % '$current_predicate_enum' yields indicators lazily (a backtrackable
        % builtin), so the full O(n) indicator list is no longer built on the
        % heap before member/2 walks it.
        current_predicate(I) :-
            '$check_predicate_indicator'(I),
            '$current_predicate_enum'(I).

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
        % '$sub_atom_enum' yields each decomposition lazily (a backtrackable
        % builtin), so a long atom no longer materialises all O(n^2)
        % decompositions onto the heap before member/2 walks them. (The earlier
        % revert to the eager form was due to a missing IsBacktrackable flag that
        % broke it under Tier-1 IL — now fixed.)
        sub_atom(Atom, Before, Length, After, Sub) :-
            '$sub_atom_enum'(Atom, Before, Length, After, Sub).

        %! subsumes_term(@General, @Specific) | Term inspection & construction | Succeeds if General subsumes Specific (Specific is an instance of General) without binding any variable of either term.
        % ISO §8.2.4. A pure test: the double negation undoes the trial
        % unification's bindings. After General = Specific, Specific's
        % variables must be unchanged (still the same distinct unbound vars),
        % i.e. only General's variables were bound — otherwise General does
        % not subsume Specific.
        subsumes_term(General, Specific) :-
            \+ \+ (
                term_variables(Specific, Vars),
                General = Specific,
                term_variables(Vars, Vars2),
                Vars == Vars2
            ).

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

        %! sumlist(+List, -Sum) | Lists | Sum is the sum of the numbers in List (alias of sum_list/2).
        sumlist(L, S) :- '$sum_list'(L, 0, S).

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
        '$char_type'(decimal_digit, Code) :- '$ascii_digit'(Code).
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

        % ===== control, database & inspection (chunk 98) =====

        %! once(:Goal) | Control | Succeeds at most once — commits to the first solution of Goal.
        once(Goal) :- call(Goal), !.

        %! ignore(:Goal) | Control | Runs Goal, succeeding whether or not Goal does.
        ignore(Goal) :- ( call(Goal) -> true ; true ).

        %! chdir(?Path) | Input / output | Arity-Prolog 1-arg form of working_directory/2. With Path unbound, returns the current directory; with Path bound, changes to it.
        chdir(Path) :- var(Path), !, working_directory(Path, Path).
        chdir(Path) :- working_directory(_, Path).

        %! append(+ListOfLists, -List) | Lists | Concatenates a list of lists (SWI library form).
        append([], []).
        append([L|Ls], As) :- append(L, Ws, As), append(Ls, Ws).

        %! :(+Module, :Goal) | Control | Runtime module-qualified call. Shumway's public predicates share one flat global namespace, so a qualified call to an exported predicate is module-transparent — it simply calls Goal.
        ':'(_Module, Goal) :- call(Goal).

        %! phrase(:Body, ?List) | Grammar | phrase(Body, List, []) — succeeds when the DCG Body derives List.
        phrase(Body, List) :- phrase(Body, List, []).
        %! phrase(:Body, ?List, ?Rest) | Grammar | Runtime DCG driver: succeeds when Body derives the difference List/Rest. Statically-known bodies are expanded at compile time; this interpreter handles a variable/list Body and control constructs at runtime.
        phrase(Body, S0, S) :- '$phrase'(Body, S0, S).

        '$phrase'(V, _, _) :- var(V), !, throw(error(instantiation_error, phrase/3)).
        '$phrase'([], S0, S) :- !, S0 = S.
        '$phrase'([H|T], S0, S) :- !, append([H|T], S, S0).
        '$phrase'(!, S0, S) :- !, S0 = S.
        '$phrase'((A, B), S0, S) :- !, '$phrase'(A, S0, S1), '$phrase'(B, S1, S).
        '$phrase'((A ; B), S0, S) :- !, ( '$phrase'(A, S0, S) ; '$phrase'(B, S0, S) ).
        '$phrase'((A | B), S0, S) :- !, ( '$phrase'(A, S0, S) ; '$phrase'(B, S0, S) ).
        '$phrase'((A -> B), S0, S) :- !, ( '$phrase'(A, S0, S1) -> '$phrase'(B, S1, S) ).
        '$phrase'({G}, S0, S) :- !, call(G), S0 = S.
        '$phrase'(\+ A, S0, S) :- !, \+ '$phrase'(A, S0, _), S0 = S.
        '$phrase'(call(G), S0, S) :- !, call(G, S0, S).
        '$phrase'(G, S0, S) :- call(G, S0, S).

        %! display(+Term) | Input / output | Edinburgh display/1: writes Term to current output ignoring operator definitions, unquoted.
        display(X) :- write_term(X, [ignore_ops(true)]).
        %! display(+Stream, +Term) | Input / output | Edinburgh display/2: writes Term to Stream ignoring operator definitions, unquoted.
        display(S, X) :- write_term(S, X, [ignore_ops(true)]).

        %! recorda(+Key, +Term) | Database | SWI 2-arg form of recorda/3 (reference discarded).
        recorda(K, V) :- recorda(K, V, _).
        %! recordz(+Key, +Term) | Database | SWI 2-arg form of recordz/3 (reference discarded).
        recordz(K, V) :- recordz(K, V, _).
        %! recorded(+Key, ?Term) | Database | SWI 2-arg form of recorded/3 (reference discarded); backtracks over matches.
        recorded(K, V) :- recorded(K, V, _).

        %! apply(:Goal, +ExtraArgs) | Control | Calls Goal with the list of extra arguments appended.
        apply(Goal, Extra) :-
            Goal =.. List0,
            append(List0, Extra, List),
            Call =.. List,
            call(Call).

        %! tab(+N) | Input / output | Writes N spaces to the current output stream.
        tab(N) :- ( N =< 0 -> true ; write(' '), N1 is N - 1, tab(N1) ).

        %! findall(?Template, :Goal, -List, ?Tail) | Findall & aggregation | Like findall/3 but the result is a difference list ending in Tail.
        findall(Template, Goal, List, Tail) :-
            findall(Template, Goal, List0),
            append(List0, Tail, List).

        %! retractall(+Head) | Database | Removes every clause whose head unifies with Head.
        % retract/1 is re-satisfiable, so a failure-driven loop retracts
        % every match; the `fail` undoes each solution's bindings, keeping
        % Head general. Facts are retracted by the head form, rules by the
        % (Head :- Body) form.
        % retractall on an UNDEFINED predicate is a silent no-op (SWI / SICStus);
        % on a STATIC one it raises permission_error. '$retractall_modifiable'
        % succeeds only for a dynamic predicate, so the retract loop runs just
        % then; undefined makes it fail into the `true` arm.
        retractall(Head) :-
            ( '$retractall_modifiable'(Head)
            -> ( retract(Head), fail ; true ),
               ( retract((Head :- _)), fail ; true )
            ; true ).

        %! listing | Database | Lists the clauses of every user-defined predicate — consulted or asserted, never builtins or library predicates.
        listing :-
            '$listable_predicates'(All),
            '$listing_all'(All).

        %! listing(+Spec) | Database | Lists the clauses of the user-defined predicate named by Spec (Name or Name/Arity).
        % Chunk 256: when no predicate matches, print a comment so
        % the user sees feedback instead of a silent `true.`
        listing(Name/Arity) :-
            !,
            '$listable_predicates'(All),
            ( member(pi(Name, Arity, Dyn), All) ->
                '$listing_pred'(Name, Arity, Dyn)
            ;
                write('% '), write(Name), write('/'), write(Arity),
                write(' not defined'), nl
            ).
        listing(Name) :-
            '$listable_predicates'(All),
            '$listing_named'(All, Name, false, Found),
            ( Found == true -> true
            ;
                write('% no predicate matches '), write(Name), nl
            ).

        '$listing_all'([]).
        '$listing_all'([pi(Name, Arity, Dyn)|Rest]) :-
            '$listing_pred'(Name, Arity, Dyn),
            '$listing_all'(Rest).

        '$listing_named'([], _, Found, Found).
        '$listing_named'([pi(Name, Arity, Dyn)|Rest], Want, FoundIn, FoundOut) :-
            ( Name == Want ->
                '$listing_pred'(Name, Arity, Dyn),
                Found1 = true
            ;
                Found1 = FoundIn
            ),
            '$listing_named'(Rest, Want, Found1, FoundOut).

        % print one predicate: a `:- dynamic` header for a dynamic
        % predicate, then a clause per line, then a blank separator
        % line. Chunk 254 — the actual clause printing routes through
        % the engine's '$listing_pred_source'/2 which walks the AST
        % directly so variable names from the source survive
        % (clause/2 + write/1 lost them through the heap round-trip).
        '$listing_pred'(Name, Arity, Dyn) :-
            ( Dyn == true ->
                write(':- dynamic '), write(Name), write('/'), write(Arity),
                write('.'), nl
            ; true
            ),
            '$listing_pred_source'(Name, Arity),
            nl.

        %! format(+Format) | Input / output | Like format/2 with no arguments.
        format(Format) :- format(Format, []).

        %! format_to_atom(-Atom, +Format, +Args) | Input / output | Like format/2 but captures the formatted output into an atom.
        format_to_atom(Atom, Format, Args) :-
            with_output_to(atom(Atom), format(Format, Args)).

        % ===== tabling (chunks 104-106) =====
        % A `:- table p/N` predicate is transformed at consult time. Its
        % clauses are split: base clauses (no tabled body call) become
        % '$tbase$p'/N, recursive clauses become '$trec$p'/N with the
        % single tabled body literal turned into a '$tbl_consume' call. A
        % driver clause routes p through '$table_call'.
        %
        % '$table_call' drives a *semi-naive* fixpoint: a subgoal's base
        % answers are its first delta, and each round re-derives only what
        % a producer's delta makes newly possible ('$tbl_consume' yields a
        % producer's delta, not its whole answer set). A clause with two
        % or more tabled literals, or a tabled call nested in a control
        % construct, is left undifferentiated and re-run every round (it
        % stays correct, just not accelerated).
        %
        % The table is the runtime dynamic store: answers, deltas and
        % subgoals are individual asserted facts, so every update is O(1)
        % (a list-per-subgoal would copy O(n) on every assert). It is read
        % with clause/2 — a direct call to a dynamic predicate sees only
        % the query-setup snapshot, whereas clause/2 consults the live
        % store, so writes made earlier in the same query are visible.
        % Duplicate answers are filtered by the engine-backed '$tbl_seen'
        % set, an O(1) test — the semi-naive fixpoint would be quadratic
        % if it instead scanned the asserted answers.
        %
        % The '$tbl_running' flag is raised before registering, because
        % registration computes base answers and a base clause of a
        % *complex* predicate can call a tabled predicate — that nested
        % call must take the consumer path, not start a second fixpoint.
        '$table_call'(Goal, BaseRun, RecRun) :-
            '$table_key'(Goal, Key),
            ( '$tbl_is_running' ->
                '$table_register'(Key, Goal, BaseRun, RecRun),
                '$table_emit'(Key, Goal)
            ; assertz('$tbl_running'),
              '$table_register'(Key, Goal, BaseRun, RecRun),
              '$table_seminaive',
              retract('$tbl_running'),
              '$table_emit'(Key, Goal)
            ).

        % canonical (ground) key — variant subgoals share one entry.
        '$table_key'(Goal, Key) :- copy_term(Goal, Key), numbervars(Key, 0, _).

        '$tbl_is_running' :- clause('$tbl_running', true), !.

        % register a subgoal. A new one has its base clauses evaluated at
        % once — base clauses make no tabled call, so this is self-
        % contained — and the result becomes both its answers and its
        % first pending delta.
        '$table_register'(Key, Goal, BaseRun, RecRun) :-
            ( clause('$tbl_subgoal'(Key, _, _, _), true) -> true
            ; assertz('$tbl_subgoal'(Key, Goal, BaseRun, RecRun)),
              assertz('$tbl_fresh'(Key)),
              findall(Goal, call(BaseRun), Raw),
              '$table_absorb'(Key, Raw)
            ).

        % file each derived answer that is new for this subgoal — '$tbl_seen'
        % both tests and records — as both an answer and a pending delta.
        '$table_absorb'(_, []).
        '$table_absorb'(Key, [A|As]) :-
            ( '$tbl_seen'(Key - A)
              -> assertz('$tbl_ans'(Key, A)), assertz('$tbl_newd'(Key, A))
              ;  true
            ),
            '$table_absorb'(Key, As).

        '$table_emit'(Key, Goal) :- clause('$tbl_ans'(Key, Goal), true).

        % the differentiated tabled call. It normally yields the producer
        % subgoal's delta (the answers gained in the previous round); but
        % when the *consuming* subgoal is running its first round (mode
        % full) it yields the producer's whole answer set — a freshly
        % discovered consumer must catch up on deltas emitted before it
        % existed.
        '$tbl_consume'(Goal, BaseRun, RecRun) :-
            '$table_key'(Goal, Key),
            '$table_register'(Key, Goal, BaseRun, RecRun),
            ( clause('$tbl_mode'(full), true)
              -> clause('$tbl_ans'(Key, Goal), true)
              ;  clause('$tbl_delta'(Key, Goal), true)
            ).

        % semi-naive fixpoint: each iteration commits the pending deltas
        % and re-runs every subgoal's recursive clauses against them.
        % Running '$trec' is also what discovers new subgoals (its
        % '$tbl_consume' calls register them), so a round runs even when
        % every delta is empty; the loop ends when a round adds no answer
        % and discovers no new subgoal. The loop recurses once per round,
        % so a fixpoint deeper than the control stack (very long recursive
        % chains) overflows — see the chunk-106 notes.
        '$table_seminaive' :-
            '$table_count'(Before),
            '$table_commit',
            '$table_round',
            '$table_count'(After),
            ( ( '$table_progress' ; After > Before ) -> '$table_seminaive'
            ; true
            ).
        '$table_count'(N) :-
            findall(x, clause('$tbl_subgoal'(_, _, _, _), true), L), length(L, N).
        '$table_progress' :- clause('$tbl_newd'(_, _), true), !.

        % the pending deltas ('$tbl_newd') become the deltas the next round
        % consumes; old deltas are discarded.
        '$table_commit' :-
            retractall('$tbl_delta'(_, _)),
            findall(K - A, clause('$tbl_newd'(K, A), true), Pairs),
            retractall('$tbl_newd'(_, _)),
            '$table_install'(Pairs).
        '$table_install'([]).
        '$table_install'([K - A|Rest]) :-
            assertz('$tbl_delta'(K, A)),
            '$table_install'(Rest).

        '$table_round' :-
            findall(K, clause('$tbl_subgoal'(K, _, _, _), true), Keys),
            '$table_round_each'(Keys).
        '$table_round_each'([]).
        '$table_round_each'([K|Ks]) :-
            clause('$tbl_subgoal'(K, G, _, Rec), true),
            ( retract('$tbl_fresh'(K)) -> '$tbl_set_mode'(full)
            ; '$tbl_set_mode'(delta)
            ),
            findall(G, call(Rec), Raw),
            '$table_absorb'(K, Raw),
            '$table_round_each'(Ks).

        '$tbl_set_mode'(M) :-
            ( retract('$tbl_mode'(_)) -> true ; true ),
            assertz('$tbl_mode'(M)).

        % ----- tabled negation: well-founded semantics -----
        % `\+ G` over a tabled goal is rewritten at consult time to
        % '$tbl_negate'(G). A monotone fixpoint cannot read a negated
        % subgoal incrementally, so a program with tabled negation is
        % evaluated by the *alternating fixpoint*. W(K) is one tabled
        % least-fixpoint in which `\+ a` succeeds iff a is not in the
        % assumption set K; iterating W from the empty set yields an
        % increasing chain (limit U — the well-founded *true* atoms) and a
        % decreasing chain (limit O), with U subset-of O. O minus U is the
        % *undefined* atoms. This terminates on negative cycles — e.g.
        % `p :- \+ p` makes p undefined — where plain SLD would loop.
        %
        % A tabled call routes through '$tbl_dispatch': inside a W-run
        % ('$wfs_active') it is a plain '$table_call'; at the top level of
        % a program that uses tabled negation ('$wfs_mode', a fact the
        % consult-time transform adds) it runs the alternating fixpoint.
        '$tbl_dispatch'(Goal, Base, Rec) :-
            ( clause('$wfs_active', true) -> '$table_call'(Goal, Base, Rec)
            ; clause('$wfs_mode', true)   -> '$wfs_query'(Goal)
            ; '$table_call'(Goal, Base, Rec)
            ).

        % during a W-run `\+ G` is decided against the assumption K — but
        % G is still run first (for its side effect of registering its
        % subgoal) so its atoms are part of the model the fixpoint builds.
        '$tbl_negate'(Goal) :-
            ( call(Goal), fail ; true ),
            \+ clause('$wfs_k'(Goal), true).

        '$wfs_query'(Goal) :-
            assertz('$wfs_active'),
            '$wfs_solve'(Goal, U, _),
            retractall('$wfs_active'),
            member(Goal, U).

        %! well_founded(+Goal, -Status) | Database | The well-founded truth value of a tabled Goal — true, false or undefined.
        well_founded(Goal, Status) :-
            assertz('$wfs_active'),
            '$wfs_solve'(Goal, U, O),
            retractall('$wfs_active'),
            ( '$wfs_memq'(Goal, U) -> Status = true
            ; '$wfs_memq'(Goal, O) -> Status = undefined
            ; Status = false
            ).

        % the alternating fixpoint: U = well-founded true atoms, O the
        % over-estimate; O minus U are undefined.
        '$wfs_solve'(Goal, U, O) :-
            '$wfs_eval'(Goal, [], K1),
            '$wfs_iterate'(Goal, [], K1, U, O).

        % A = K(n-2), B = K(n-1); compute K(n) = W(B). Once K(n) = K(n-2)
        % the sequence has entered its period-2 cycle and {B, K(n)} are
        % the two limits — the smaller is U, the larger O.
        '$wfs_iterate'(Goal, A, B, U, O) :-
            '$wfs_eval'(Goal, B, Kn),
            ( Kn == A
              -> ( '$wfs_subset'(Kn, B) -> U = Kn, O = B ; U = B, O = Kn )
              ;  '$wfs_iterate'(Goal, B, Kn, U, O)
            ).

        % W(K): one tabled least-fixpoint with `\+` resolved against K;
        % yields the sorted set of every atom derived.
        '$wfs_eval'(Goal, K, Atoms) :-
            abolish_all_tables,
            retractall('$wfs_k'(_)),
            '$wfs_install'(K),
            ( call(Goal), fail ; true ),   % run for side effect; do not bind Goal
            findall(A, clause('$tbl_ans'(_, A), true), Raw),
            sort(Raw, Atoms).
        '$wfs_install'([]).
        '$wfs_install'([A|As]) :- assertz('$wfs_k'(A)), '$wfs_install'(As).

        '$wfs_subset'([], _).
        '$wfs_subset'([X|Xs], Ys) :- '$wfs_memq'(X, Ys), '$wfs_subset'(Xs, Ys).
        '$wfs_memq'(X, [Y|Ys]) :- ( X == Y -> true ; '$wfs_memq'(X, Ys) ).

        % ----- table invalidation -----
        %! abolish_all_tables | Database | Discards every tabled answer; later queries recompute against the current program.
        abolish_all_tables :-
            retractall('$tbl_subgoal'(_, _, _, _)),
            retractall('$tbl_ans'(_, _)),
            retractall('$tbl_delta'(_, _)),
            retractall('$tbl_newd'(_, _)),
            retractall('$tbl_fresh'(_)),
            retractall('$tbl_mode'(_)),
            retractall('$tbl_running'),
            retractall('$tbl_neg_cache'(_, _)),
            '$tbl_seen_clear'.

        %! abolish_table(+PredicateIndicator) | Database | Discards the tabled answers of one predicate, given as Name/Arity.
        abolish_table(Name/Arity) :-
            functor(Template, Name, Arity),
            findall(K,
                ( clause('$tbl_subgoal'(K, G, _, _), true), \+ \+ G = Template ),
                Keys),
            '$tbl_drop_each'(Keys),
            retractall('$tbl_neg_cache'(_, _)),
            '$tbl_seen_clear'.
        '$tbl_drop_each'([]).
        '$tbl_drop_each'([K|Ks]) :-
            retractall('$tbl_subgoal'(K, _, _, _)),
            retractall('$tbl_ans'(K, _)),
            retractall('$tbl_delta'(K, _)),
            retractall('$tbl_newd'(K, _)),
            retractall('$tbl_fresh'(K)),
            '$tbl_drop_each'(Ks).
        """;
}
