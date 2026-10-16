# A builtin continues after its wakeups

**Status**: IMPLEMENTED (2026-10-16). An extension of the ADR-049 wake batch;
no ADR changes.

## Problem

A builtin written in C# that enumerates solutions over a partial list,
`nth0/3` and `nth1/3` with a free index and the split mode of `append/3`,
grows the list one cell per step: it binds the open tail to a fresh cons,
arms the choice point for the next step and yields. When the tail is an
attributed variable the binding wakes its hooks, and under ADR-049 the hooks
run once the builtin has returned, at the next goal boundary, above the
choice point the builtin armed. A retry undoes the hooks' work and keeps
the binding, so the attributes fall out of step with the list.

A Prolog definition has no such problem. In

```prolog
nth_gen(_, Elem, Elem, Base, Base).
nth_gen([H|Tail], Elem, _, N, Base) :- succ(N, M), nth_gen(Tail, Elem, H, M, Base).
```

the second clause binds the tail in its head, the hooks run at the body's
first goal boundary, and only then does the recursive call push the choice
point of the next level. Binding and hooks sit below that choice point and
survive its retries, and a hook that rejects the binding fails the clause,
which has no alternative left: the enumeration ends.

PR #149 made the C# builtins bind the tail after the choice point and
rebuild the whole extension on each retry. The answers are right, but a
rejected extension does not end anything: the builtin cannot tell a hook's
rejection from the failure of a later goal, so it keeps proposing longer
lists. `@A #=< 6, nth0(X, A, a)` finds six positions and never stops, where
the Prolog definition stops after the sixth.

## Idea

The builtin cannot run the hooks in the middle of a step, but it does not
have to: it can leave the rest of the step to the hooks' own batch. After
binding the tail, if the binding queued wakeups, the builtin queues one more
item behind them, a goal that carries the whole state of the next level in
its arguments, and returns without yielding. The next goal boundary runs the
batch: the hooks first, then the goal, which pushes the choice point of the
next level and yields the solution. The order of events is exactly the
clause's: binding, hooks, choice point, yield.

```prolog
'$nth_gen'(Tail, Elem, H, N, Index)    % H is the element at position N
'$append_gen'(Tail, L2, Rest)          % the two-clause split from Tail and Rest
```

When the binding queued nothing (a plain variable, or an attributed one with
no hook) the builtin goes on in line, as before: the choice point first, then
the yield. The deferred path costs nothing to programs without hooks.

## Why this is right

- **A rejected extension ends the enumeration.** The step binds the tail
  with the choice point of its own level already trusted, as the second
  clause is the last one. A hook that fails backtracks past the whole
  enumeration, which is what the clause does.
- **The yield comes before any cut.** A cut is a goal boundary (the
  interpreter and the IL both flush the batch before committing), so the
  goal has pushed its choice point by the time the cut runs, and the cut
  removes it as it removes any other.
- **Backtracking drops the goal with the binding.** A batch item names the
  home of the binding it belongs to, and a backtrack that undoes the binding
  drops the item (`DropDeadWakeups`). The goal names the tail it bound.
- **A hook's alternatives re-run the goal.** The driver runs the batch as
  ordinary code; backtracking into a hook's alternative continues with the
  rest of the batch, so the goal yields again from the same position, which
  is what the clause body does after the head's hooks resume.
- **The state lives in the term and in the choice point.** The goal's
  arguments are heap cells the collector relocates; the choice point saves
  them as argument registers, plus the return address as a raw integer. No
  C# object holds a heap index across a retry.

## What changes

In the engine, one method: `Activation.QueueWakeGoal(goalIdx, home)` adds a
`'$lazy'` item to the pending wakeups, the kind whose value is the goal
itself. The driver already runs such items with `'$wake_call'/1`, the
fallback drain already meta-calls them, the flush gate already keeps them
when no hook is linked, and the heap collector already relocates their
indices.

In `nth0/3` and `nth1/3`: the walk over the known cells of the list keeps
its linear retry in place. At an unbound tail, attributed or not, the step
binds one cons; with wakeups pending it queues `'$nth_gen'/5` and returns,
otherwise it arms the next frame and yields in line. `'$nth_gen'/5` is the
same step as a builtin of its own, entered by the driver: it pushes the
frame of its level and yields, and its retry binds the next cons and
decides the same way.

In `append/3`: the split cursor binds the rest of L1 in its second clause
and, with wakeups pending, queues `'$append_gen'/3` instead of running the
first clause of the next level in line. `'$append_gen'/3` starts the cursor
over from the tail and the rest of L3.

The wasm tier opens only the deterministic mode of `append/3` in line; an
open tail steps out to the host, and the host builtins are the ones above.
The IL tier calls the same builtins and flushes the batch at the same
boundaries.

## Tests

`AttributedTailGrowthTests`: the answers of PR #149 unchanged; and
termination where the length is bounded before the enumeration,
`@A #=< 6, findall(X, nth0(X, A, a), Xs)` gives the six positions and ends,
a frozen goal that rejects every extension ends the enumeration at the
known cells, the `append/3` split under a bounded length stops at the last
split the bound admits, and a cut right after the enumeration commits to
the first position.
