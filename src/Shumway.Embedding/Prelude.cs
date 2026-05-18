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

        member(X, [X|_]).
        member(X, [_|T]) :- member(X, T).

        clause(H, B) :-
            nonvar(H),
            '$all_clauses_of'(H, Pairs),
            member(H-B, Pairs).

        current_predicate(I) :-
            '$check_predicate_indicator'(I),
            '$all_predicate_indicators'(All),
            member(I, All).

        '$check_predicate_indicator'(I) :- var(I), !.
        '$check_predicate_indicator'(_/_) :- !.
        '$check_predicate_indicator'(I) :-
            throw(error(type_error(predicate_indicator, I), _)).

        length(L, N) :-
            nonvar(L), !, '$list_length'(L, N).
        length(L, N) :-
            integer(N), !, '$make_var_list'(N, L).
        length(L, N) :- '$length_enum'(L, N, 0).

        '$length_enum'([], N, N).
        '$length_enum'([_|T], N, Acc) :-
            Acc1 is Acc + 1,
            '$length_enum'(T, N, Acc1).

        sub_atom(Atom, Before, Length, After, Sub) :-
            '$sub_atom_decompositions'(Atom, Decomps),
            member([Before, Length, After, Sub], Decomps).

        maplist(_, []).
        maplist(G, [X|Xs]) :- call(G, X), maplist(G, Xs).

        maplist(_, [], []).
        maplist(G, [X|Xs], [Y|Ys]) :- call(G, X, Y), maplist(G, Xs, Ys).

        maplist(_, [], [], []).
        maplist(G, [X|Xs], [Y|Ys], [Z|Zs]) :-
            call(G, X, Y, Z), maplist(G, Xs, Ys, Zs).

        foldl(_, [], Acc, Acc).
        foldl(G, [X|Xs], Acc, Out) :-
            call(G, X, Acc, Acc1),
            foldl(G, Xs, Acc1, Out).

        foldl(_, [], [], Acc, Acc).
        foldl(G, [X|Xs], [Y|Ys], Acc, Out) :-
            call(G, X, Y, Acc, Acc1),
            foldl(G, Xs, Ys, Acc1, Out).

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
        """;
}
