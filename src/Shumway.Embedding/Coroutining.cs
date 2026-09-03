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
            ; '$when_attach'(Condition, trigger(Condition, Goal, _Fired, _Alive))
            ).

        % Same trap one level down: a variable SUB-condition would unify with
        % nonvar(_) and silently become a condition the caller never wrote.
        % Too little instantiation to decide, so it reports as such.
        '$when_valid'(C) :- var(C), !, throw(error(instantiation_error, when/2)).
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
        %   already fired, or a retired incarnation -> nothing;
        %   now holds       -> claim the flag and run Goal once;
        %   still undecided -> retire THIS incarnation and re-attach to the
        %                      variables that remain (a partial binding can
        %                      expose new ones, e.g. under ground/1), reusing
        %                      Fired so the run stays unique.
        %
        % TWO flags, as dif/2 has: Fired is shared by every incarnation and
        % keeps Goal to one run; Alive belongs to one incarnation and retires
        % it. Without Alive each re-suspension left the previous records live,
        % and the top level showed one residual per record -- three copies of
        % the same constraint for a condition that had been re-suspended twice.
        '$when_fire'(trigger(Condition, Goal, Fired, Alive)) :-
            ( Fired == fired -> true
            ; Alive == dead -> true
            ; '$when_holds'(Condition) -> Fired = fired, call(Goal)
            ; Alive = dead,
              '$when_attach'(Condition, trigger(Condition, Goal, Fired, _))
            ).

        %! dif(?X, ?Y) | Coroutining | Constrains X and Y to be different: fails when they become identical, succeeds once they cannot unify.
        dif(X, Y) :-
            '$dif_check'(X, Y, Out, Canon),
            ( Out == none -> true
            ; '$dif_canon'(X, Y, Canon, CX, CY),
              ( '$dif_live_already'(Out, CX, CY) -> true
              ; '$dif_suspend'(CX, CY, Out)
              )
            ).

        % ===== canonical form, and not posting what is already posted =====
        % The UNIFIER of the two terms is what a dif really constrains: the
        % terms are equal exactly when every one of its pairs holds, so the
        % disequality is the negation of that conjunction — a DISJUNCTION of
        % pair-disequalities. Two consequences, and both are why this is here:
        %
        %   one pair  -> the disjunction is a single disequality, so the whole
        %                constraint IS that pair. dif(-X, -Y), dif(f(X,Y),
        %                f(Y,X)) and dif(X, Y) all unify by {X = Y} and are one
        %                and the same constraint; storing the pair makes them
        %                literally so, which is both smaller to re-check on
        %                every wake and what the top level then shows.
        %   many pairs -> a real disjunction (dif(f(X,Y), f(A,B)) is X\=A OR
        %                Y\=B), which does not reduce; the terms stay as
        %                written.
        %
        % Having a canonical form makes the duplicate check a comparison. A dif
        % already suspended and still live subsumes the one being posted, so the
        % new one is simply not posted. Keeping the OLDER is not a preference:
        % its lifetime contains the newer one's, so discarding it and keeping
        % the newcomer would drop the constraint entirely on backtracking to
        % between the two — answers, not just output, would be wrong.
        % The pair comes from the SAME trial unification that decided whether
        % this dif suspends at all — '$dif_check' hands it back rather than
        % making us unify a second time to find out. All that is left here is
        % the orientation: var-var by standard order, so dif(X,Y) and dif(Y,X)
        % land on the same pair; a non-var side always sits second.
        '$dif_canon'(X, Y, Canon, CX, CY) :-
            (   Canon = (A - B) ->
                (   var(B), B @< A -> CX = B, CY = A
                ;   CX = A, CY = B
                )
            ;   CX = X, CY = Y
            ).

        % Is an equivalent constraint already watching these variables? Only
        % the first is examined: equivalent constraints unify the same way, so
        % they suspend on the same variables in the same order.
        '$dif_live_already'([V|_], A, B) :-
            var(V),
            get_attr(V, coroutining, frozen(G)),
            '$dif_conj_holds'(G, A, B).
        '$dif_conj_holds'((P, Q), A, B) :-
            !,
            ( '$dif_conj_holds'(P, A, B) -> true ; '$dif_conj_holds'(Q, A, B) ).
        '$dif_conj_holds'('$dif_wake'(dif_c(X, Y, Alive)), A, B) :-
            Alive \== dead,
            ( X == A, Y == B -> true ; X == B, Y == A ).

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
        %     the variables that remain — in canonical form, since a binding
        %     may have reduced what is left to a single pair.
        '$dif_wake'(dif_c(X, Y, Alive)) :-
            ( Alive == dead -> true
            ; '$dif_check'(X, Y, Out, Canon),
              ( Out == none -> Alive = dead
              ; Alive = dead,
                '$dif_canon'(X, Y, Canon, CX, CY),
                '$dif_suspend'(CX, CY, Out)
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
                    '$co_merge'(G2, G, Merged),
                    Goals = [put_attr(Value, coroutining, frozen(Merged)),
                             '$co_alias_check'(Merged)]
                ; Goals = [put_attr(Value, coroutining, frozen(G)),
                           '$co_alias_check'(G)]
                )
            ; Goals = [G]
            ).

        % A dif/when constraint watching BOTH variables sits in each one's
        % goal list, so concatenating them leaves the survivor carrying it
        % twice and the top level showing it twice; the two records are
        % literally the same term, so == decides it.
        %
        % ONLY those records. A frozen GOAL is not shared between variables:
        % each freeze/2 call demands one run, so dropping the second of two
        % equal goals loses a side effect the program asked for
        % (freeze(X,write(a)), freeze(Y,write(a)), X = Y, X = 1 must print
        % twice). A goal is arbitrary — including another freeze, or a
        % variable bound to one later — so nothing about its shape can say
        % whether it was recorded once or twice.
        '$co_merge'(Old, G, Merged) :- var(G), !, Merged = (Old, G).
        '$co_merge'(Old, (A, B), Merged) :-
            !,
            '$co_merge'(Old, A, Mid),
            '$co_merge'(Mid, B, Merged).
        '$co_merge'(Old, G, Merged) :-
            (   '$co_shared_record'(G), '$co_has'(Old, G)
            ->  Merged = Old
            ;   Merged = (Old, G)
            ).

        % The two records one constraint leaves in two variables.
        '$co_shared_record'('$dif_wake'(_)).
        '$co_shared_record'('$when_fire'(_)).

        '$co_has'(G0, G) :- var(G0), !, G0 == G.
        '$co_has'((A, B), G) :- !, ( '$co_has'(A, G) -> true ; '$co_has'(B, G) ).
        '$co_has'(G0, G) :- G0 == G.

        % Runs after the aliasing is committed, which is the only place a
        % constraint over TWO variables can see that they became one.
        %
        % dif/2: fails iff some live suspension's arguments became identical.
        % when/2: an aliasing can DECIDE ?=(X, Y), since the two are now the
        % same term, and nothing else would ever look -- a binding to a VALUE
        % is what releases a frozen goal, and this was not one. So
        % `when(?=(P,Q), G), P = Q` left G suspended for good.
        % Anything still undecided stays covered by the migrated suspension.
        '$co_alias_check'(G) :- var(G), !.
        '$co_alias_check'((A, B)) :-
            !, '$co_alias_check'(A), '$co_alias_check'(B).
        '$co_alias_check'('$dif_wake'(dif_c(X, Y, Alive))) :-
            !, ( Alive == dead -> true ; X \== Y ).
        '$co_alias_check'('$when_fire'(Trigger)) :-
            !, '$when_fire'(Trigger).
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

        % A goal that is still a VARIABLE (freeze(X, G) with G unbound, which
        % is legal — it raises only when it runs) must not reach the
        % conjunction pattern below: unifying it there BINDS it to (A, B)
        % with two fresh variables, which both corrupts the suspension and
        % recurses forever on the fresh parts. Guard every walker.
        co_project(G, V, Goals, Tail) :-
            var(G), !, Goals = [freeze(V, G)|Tail].
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
        co_project('$when_fire'(trigger(Cond, Goal, Fired, Alive)), V, Goals, Tail) :-
            !,
            ( Fired \== fired, Alive \== dead, '$co_owner'(Cond, V)
              -> Goals = [when(Cond, Goal)|Tail]
            ; Goals = Tail
            ).
        co_project(G, V, [freeze(V, G)|Tail], Tail).

        % V owns a constraint iff it is the first variable of the watched
        % terms — the once-only emission rule (as CLP(FD) does for propagators).
        '$co_owner'(Term, V) :- term_variables(Term, [First|_]), First == V.
        """;
}
