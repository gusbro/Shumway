namespace Shumway.Embedding;

/// <summary>
/// The coroutining library — goal suspension on variable binding. It is an
/// <em>opt-in</em> module: an embedder calls
/// <see cref="PrologEngine.UseCoroutining"/> (or a program loads
/// <c>use_module(library(coroutining))</c>).
///
/// <para><c>freeze/2</c> delays a goal until its variable is bound;
/// <c>frozen/2</c> reads the delayed goals back; <c>dif/2</c> is a sound
/// disequality constraint: it fails as soon as its arguments become
/// identical, succeeds outright once they cannot unify, and stays
/// suspended in between. Built on the same attributed-variable
/// <c>verify_attributes/4</c> hook as CLP(FD)/CLP(R) — the hook is
/// <c>:- multifile</c>, so coroutining coexists with either (or both)
/// constraint libraries on one engine.</para>
/// </summary>
internal static class Coroutining
{
    public const string ModuleName = "coroutining";

    public const string Source = """
        :- module(coroutining).

        :- public freeze/2.
        :- public frozen/2.
        :- public dif/2.
        :- public verify_attributes/4.
        :- multifile verify_attributes/4.
        :- public coroutining_attr_goals/3.
        % public because the hook (a dynamic clause, body not module-mangled)
        % references it in its residual Goals — like clpfd's '$fd_set'/3.
        :- public '$co_alias_check'/1.

        % The attribute value is frozen(Goal) where Goal is one goal or a
        % (G1, G2) conjunction, oldest first — waking runs them in the
        % order they were frozen.

        %! freeze(?Var, :Goal) | Coroutining | Delays Goal until Var is bound; runs it at once when Var is already bound.
        freeze(X, Goal) :-
            ( var(X) ->
                ( get_attr(X, coroutining, frozen(G0)) ->
                    put_attr(X, coroutining, frozen((G0, Goal)))
                ; put_attr(X, coroutining, frozen(Goal))
                )
            ; call(Goal)
            ).

        %! frozen(?Var, -Goal) | Coroutining | Unifies Goal with the conjunction of goals delayed on Var (true when none).
        frozen(X, G) :-
            ( var(X), get_attr(X, coroutining, frozen(G0)) -> G = G0
            ; G = true
            ).

        %! dif(?X, ?Y) | Coroutining | Constrains X and Y to be different: fails when they become identical, succeeds once they cannot unify.
        dif(X, Y) :-
            '$dif_check'(X, Y, Out),
            ( Out == none -> true
            ; co_suspend(Out, dif(X, Y))
            ).

        % Suspend Goal on every variable the trial unification bound —
        % binding ANY of them can change the disequality's status, and
        % re-running dif/2 then either fails, succeeds for good, or
        % re-suspends on the new frontier.
        co_suspend([], _).
        co_suspend([V|Vs], Goal) :- freeze(V, Goal), co_suspend(Vs, Goal).

        % ===== the verify_attributes hook =====
        % Fired when a variable carrying frozen goals is bound. Aliasing to
        % another variable migrates the goals onto the survivor (appending
        % to any it already has — its own goals stay older, so they run
        % first); binding to a value releases the goals, which the engine
        % runs at the next goal boundary, after the binding.
        %
        % Aliasing must also re-examine the dif suspensions in BOTH
        % variables' goals: the alias may have made a dif's two arguments
        % identical, which has to fail right now — a merged suspension
        % would only notice at the next binding, if one ever comes.
        verify_attributes(coroutining, frozen(G), Value, Goals) :-
            ( var(Value) ->
                ( get_attr(Value, coroutining, frozen(G2)) ->
                    Goals = [put_attr(Value, coroutining, frozen((G2, G))),
                             '$co_alias_check'((G2, G))]
                ; Goals = [put_attr(Value, coroutining, frozen(G)),
                           '$co_alias_check'(G)]
                )
            ; Goals = [G]
            ).

        % Fails iff some dif/2 suspension's arguments became identical.
        % Anything still non-identical stays covered by the migrated
        % suspension; non-dif frozen goals are untouched by aliasing.
        '$co_alias_check'((A, B)) :-
            !, '$co_alias_check'(A), '$co_alias_check'(B).
        '$co_alias_check'(dif(X, Y)) :- !, X \== Y.
        '$co_alias_check'(_).

        % ===== projection: residual constraints for the top level =====
        % attribute_goals/4 is dynamic (pre-declared by the prelude); a
        % dynamic clause's body is not module-mangled, so it delegates to
        % the public coroutining_attr_goals/3, whose body resolves locals.
        % Each frozen conjunct projects as freeze(V, G) — except a dif/2
        % suspension, which reads back as the dif constraint itself.
        attribute_goals(coroutining, Attr, V, Goals) :-
            coroutining_attr_goals(Attr, V, Goals).
        coroutining_attr_goals(frozen(G), V, Goals) :-
            co_project(G, V, Goals, []).

        co_project((A, B), V, Goals, Tail) :-
            !,
            co_project(A, V, Goals, Mid),
            co_project(B, V, Mid, Tail).
        co_project(dif(X, Y), _, [dif(X, Y)|Tail], Tail) :- !.
        co_project(G, V, [freeze(V, G)|Tail], Tail).
        """;
}
