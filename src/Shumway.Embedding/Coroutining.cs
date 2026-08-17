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
        :- public when/2.

        % Surfaced by predicate_property/2 as meta_predicate(T) — how an
        % embedding layer (Logtalk's compiler) learns the goal argument must
        % be wrapped for its calling context before handing it over.
        :- meta_predicate(freeze(*, 0)).
        :- meta_predicate(when(*, 0)).
        :- public verify_attributes/4.
        :- multifile verify_attributes/4.
        :- public coroutining_attr_goals/3.
        % public because the hook (a dynamic clause, body not module-mangled)
        % references it in its residual Goals — like clpfd's '$fd_set'/3.
        :- public '$co_alias_check'/1.
        :- public '$when_fire'/1.
        :- public '$dif_wake'/1.

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

        %! when(+Condition, :Goal) | Coroutining | Runs Goal as soon as Condition becomes true. Condition is nonvar(X), ground(X), ?=(X,Y), or a (,)/(;) of these.
        when(Condition, Goal) :-
            ( var(Condition) -> throw(error(instantiation_error, when/2))
            ; '$when_valid'(Condition) -> true
            ; throw(error(domain_error(when_condition, Condition), when/2))
            ),
            ( '$when_holds'(Condition) -> call(Goal)
            ; '$when_attach'(Condition, trigger(Condition, Goal, _Fired))
            ).

        '$when_valid'(nonvar(_)).
        '$when_valid'(ground(_)).
        '$when_valid'(?=(_, _)).
        '$when_valid'((A, B)) :- '$when_valid'(A), '$when_valid'(B).
        '$when_valid'((A ; B)) :- '$when_valid'(A), '$when_valid'(B).

        % Does the condition hold right now?
        '$when_holds'(nonvar(X)) :- nonvar(X).
        '$when_holds'(ground(X)) :- ground(X).
        '$when_holds'(?=(X, Y)) :- ?=(X, Y).
        '$when_holds'((A, B)) :- '$when_holds'(A), '$when_holds'(B).
        '$when_holds'((A ; B)) :- ( '$when_holds'(A) -> true ; '$when_holds'(B) ).

        % Watch every variable the condition mentions. A single shared Fired
        % flag in the trigger keeps Goal to one run even though several
        % variables (and re-attachments) carry a copy of the trigger.
        '$when_attach'(Condition, Trigger) :-
            term_variables(Condition, Vars),
            '$when_watch'(Vars, Trigger).
        '$when_watch'([], _).
        '$when_watch'([V|Vs], Trigger) :-
            freeze(V, '$when_fire'(Trigger)),
            '$when_watch'(Vs, Trigger).

        % A watched variable was bound. Re-check:
        %   already fired   -> nothing;
        %   now holds       -> claim the flag and run Goal once;
        %   still undecided -> re-attach to the variables that remain (a
        %                      partial binding can expose new ones, e.g. under
        %                      ground/1), reusing Fired so the run stays unique.
        '$when_fire'(trigger(Condition, Goal, Fired)) :-
            ( Fired == fired -> true
            ; '$when_holds'(Condition) -> Fired = fired, call(Goal)
            ; '$when_attach'(Condition, trigger(Condition, Goal, Fired))
            ).

        %! dif(?X, ?Y) | Coroutining | Constrains X and Y to be different: fails when they become identical, succeeds once they cannot unify.
        dif(X, Y) :-
            '$dif_check'(X, Y, Out),
            ( Out == none -> true
            ; '$dif_suspend'(X, Y, Out)
            ).

        % Post ONE constraint incarnation, watching every variable of the
        % current unifier. The incarnation carries a shared Alive flag; a
        % re-suspension (below) retires the previous incarnation by binding
        % its flag to `dead`, so the same dif is not re-accumulated on every
        % variable at every wake — only the latest incarnation stays live.
        '$dif_suspend'(X, Y, Vars) :-
            '$dif_freeze'(Vars, dif_c(X, Y, _Alive)).
        '$dif_freeze'([], _).
        '$dif_freeze'([V|Vs], C) :- freeze(V, '$dif_wake'(C)), '$dif_freeze'(Vs, C).

        % A watched variable was bound. Re-check the disequality:
        %   retired incarnation -> nothing;
        %   the terms can never unify -> the dif holds for good, retire it;
        %   the terms are now identical -> '$dif_check' FAILS, so we fail (the
        %     binding that woke us fails, as dif demands);
        %   still undecided -> retire this incarnation and post a fresh one on
        %     the variables that remain.
        '$dif_wake'(dif_c(X, Y, Alive)) :-
            ( Alive == dead -> true
            ; '$dif_check'(X, Y, Out),
              ( Out == none -> Alive = dead
              ; Alive = dead, '$dif_suspend'(X, Y, Out)
              )
            ).

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

        % Fails iff some live dif/2 suspension's arguments became identical.
        % Anything still non-identical stays covered by the migrated
        % suspension; non-dif frozen goals are untouched by aliasing.
        '$co_alias_check'((A, B)) :-
            !, '$co_alias_check'(A), '$co_alias_check'(B).
        '$co_alias_check'('$dif_wake'(dif_c(X, Y, Alive))) :-
            !, ( Alive == dead -> true ; X \== Y ).
        '$co_alias_check'(_).

        % ===== projection: residual constraints for the top level =====
        % attribute_goals/4 is dynamic (pre-declared by the prelude); a
        % dynamic clause's body is not module-mangled, so it delegates to
        % the public coroutining_attr_goals/3, whose body resolves locals.
        % A dif/when suspension reads back as its user-facing constraint,
        % emitted ONCE — from its owner variable (the first variable of the
        % watched terms), skipping any retired incarnation — so a constraint
        % watching several variables (or superseded by re-suspension) is not
        % shown many times. A plain freeze goal projects as freeze(V, G).
        attribute_goals(coroutining, Attr, V, Goals) :-
            coroutining_attr_goals(Attr, V, Goals).
        coroutining_attr_goals(frozen(G), V, Goals) :-
            co_project(G, V, Goals, []).

        co_project((A, B), V, Goals, Tail) :-
            !,
            co_project(A, V, Goals, Mid),
            co_project(B, V, Mid, Tail).
        co_project('$dif_wake'(dif_c(X, Y, Alive)), V, Goals, Tail) :-
            !,
            ( Alive \== dead, '$co_owner'((X, Y), V)
              -> Goals = [dif(X, Y)|Tail]
            ; Goals = Tail
            ).
        co_project('$when_fire'(trigger(Cond, Goal, Fired)), V, Goals, Tail) :-
            !,
            ( Fired \== fired, '$co_owner'(Cond, V)
              -> Goals = [when(Cond, Goal)|Tail]
            ; Goals = Tail
            ).
        co_project(G, V, [freeze(V, G)|Tail], Tail).

        % V owns a constraint iff it is the first variable of the watched
        % terms — the once-only emission rule (as CLP(FD) does for propagators).
        '$co_owner'(Term, V) :- term_variables(Term, [First|_]), First == V.
        """;
}
