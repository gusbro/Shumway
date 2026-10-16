# CLP(FD): the length of a list as a constraint variable

**Status**: IMPLEMENTED (2026-10-15). An extension of `library(clpfd)`; no ADR
changes.

## Problem

The solver has no notion of the length of a list. `length(L, N)` with `N`
unbound enumerates lengths, and `same_length/2` walks both lists at once;
neither is suspended until more is known, and neither can be combined with
arithmetic constraints. The goal is to be able to write `@A #= @B`, `@L #> N`
or `3 * @L #=< @M + 1`, with the lengths propagated like any other integer in
both directions: more elements in the list raise the lower bound of the
length, and a bound on the length extends or closes the list.

An experiment with `when/2` (a `same_length/2` suspended until either list is
bound) covers only the suspension: it is a single predicate, it does not
combine with other constraints, and it does not propagate bounds.

## Idea

`@L` is an expression of `library(clpfd)`: an integer `Len` such that
`length(L, Len)`, propagated incrementally. Everything the solver already has
(linear constraints, inequalities, reification, `labeling/2`) works on `Len`
unchanged, because `Len` is an ordinary FD variable. What is new is the link
between `Len` and the list, which is stored in two places:

- **on the open tail of the list**, an attribute `spine(Len, Off)`, meaning that
  `Len` is `Off` plus the length of this tail;
- **on `Len`**, a propagator `'$fd_length'(L, Len)`, suspended like any other.

Neither side holds a reference to the other, so neither can become stale. The
attribute moves with the tail as the list grows; the propagator finds the
current tail by walking `L` from the start when it runs.

## The operator

`:- op(150, fx, @)` is declared by `library(clpfd)` beside its other operators.
At 150 it binds tighter than every arithmetic operator, so `@L + 1`,
`2 * @L #= @M - 1` and `@[a,b|T] #>= 2` read as intended. `@` is not an operator
of ISO Prolog; the comparison atoms `@<`, `@=<`, `@>`, `@>=` are their own
tokens and are not affected.

## Posting `@L`

`clpfd_expr/2` gains a clause for `@L`:

1. `'$skip_list'(N, L, Tail)` walks the known spine: `N` cells, `Tail` is `[]`,
   an unbound variable, a non-list term, or a list cell when the spine is
   cyclic.
2. `Tail == []`: the list is proper; `@L` is the integer `N`. Nothing is posted.
3. `Tail` is an unbound variable with no attribute: `Len` is a fresh FD
   variable with domain `N..sup`; `Tail` gets `spine(Len, N)`; `Len` gets
   `'$fd_length'(L, Len)`.
4. `Tail` already carries `spine(Len1, Off1)` (a second `@` over the same list,
   or over a list sharing its tail): no second attribute and no second
   propagator. `@L` is `Len1` itself when `N = Off1`, else the expression
   `Len1 + (N - Off1)`, posted through `clpfd_expr/2` like any sum.
5. Anything else is not a list: a non-list tail, a cyclic spine, or a tail that
   is an FD variable (`fd(_, _)`, so an integer). `type_error(list, L)`.

`@L` requires the spine of `L` to be acyclic, and checks it as the list grows:
each binding of the attributed tail walks the cells it adds, with cycle
detection in linear time, and a cycle raises `type_error(list, L)` right there,
before the next goal runs, rather than when something walks the list later.
Only the spine is checked; an element may be cyclic, so `L = [L]` has length 1.

## Propagation from the list to the length

Binding the tail fires `verify_attributes/4` on `spine(Len, Off)` with the
value it is being bound to. The hook is `clpfd`'s own (the engine resolves the
hook by the attribute module, ADR-040), so a second clause of the existing
predicate covers the second attribute shape.

| the tail is bound to | what the hook does |
|---|---|
| a list `[_,…\|T2]` | walks it with `'$skip_list'`: `K` new cells, tail `T2`. `T2 == []`: `Len = Off + K`. `T2` unbound: moves to `spine(Len, Off + K)` on `T2` and narrows `Len` to `Off + K .. sup`. `T2` a `spine(Len2, Off2)` tail: links `Len + Off2 #= Len2 + Off + K` and leaves that attribute where it is. Anything else: `type_error(list, …)`. |
| `[]` | `Len = Off` |
| an unbound variable with no attribute | the attribute moves to it |
| a variable with `spine(Len2, Off2)` | the two lists share a tail from here: `Len - Off #= Len2 - Off2`; one attribute stays |
| a variable with `fd(_, _)` | the tail would be an integer: `type_error(list, …)` |
| anything else | `type_error(list, …)` |

The walk covers only the cells added by this binding: a tail bound to a list of
`K` cells costs `K` steps, and a list built one cell at a time costs one step
per cell. The move is a `put_attr`, which the trail records; backtracking over
the binding restores the attribute where it was and the domain `Len` had.

`@L #= A` with `L` and `A` unbound, then `L = [a|_]`: the hook walks one cell,
moves `spine(A, 1)` onto the new tail, and narrows `A` to `1..sup`. `L` is no
longer a variable, but nothing depends on that: the attribute is on the tail
that is still open.

## Propagation from the length to the list

`'$fd_length'(L, Len)` runs when `Len`'s domain changes, like every propagator
suspended on an FD variable. It recomputes from its arguments:

1. `'$skip_list'(N, L, Tail)`.
2. `Tail == []`: `Len = N`.
3. `Tail` unbound, `Len` an integer `V`: `V >= N` or fail; the tail becomes a
   list of `V - N` fresh variables ending in `[]`.
4. `Tail` unbound, `Len` with domain `Lo..Hi`: `Len` is narrowed to `N..sup`
   first. Then `Hi == N`: `Tail = []`. Else `Lo > N`: `Tail = [_,…|T2]` with
   `Lo - N` cells, in one unification.
5. Anything else (the list became cyclic or improper through some other
   binding): `type_error(list, L)`.

The extension in 3 and 4 binds the tail, which runs the spine hook, which
moves the attribute and narrows `Len` to a bound it already has. An unchanged
domain is a no-op in `clpfd_narrow/2`, so the propagators of `Len` do not run
again and the two sides do not trigger each other repeatedly.

The walk from the start of `L` costs the length of the known prefix at every
run. The walk is a native builtin and runs once per narrowing of `Len`. An
alternative, anchoring the propagator on the current tail through a trailed
chain of moves, is worth measuring only if this cost shows up in practice. In
every other respect the propagator follows the convention of `clpfd.pl`: it is
stateless and recomputes everything from its arguments.

## Examples

```prolog
?- @L #= A, L = [a|_].
L = [a|_A], A in 1..sup, @[a|_A] #= A.

?- A in -10..10, @L #= A.
A in 0..10, @L #= A.

?- @L #> N, N #> 5.
L = [_A,_B,_C,_D,_E,_F,_G|_H], N in 6..sup, ...

?- @L #= 3.
L = [_A,_B,_C].

?- @A #= @B, A = [x,y], B = [_|T].
A = [x,y], B = [_A,_B], T = [_B].

?- @L #= N, L = [a|L].
error(type_error(list, …), _)
```

`@A #= @B` is a suspended `same_length/2`. It is documented as an example in
the guide; there is no library predicate for it.

## Projection

The answer shows the link through `Len`'s propagator:
`clpfd_prop_to_goal('$fd_length'(L, Len), (@L #= Len))`, with `L` printed as
it is at that point (`@[a|_A] #= A` above). The spine attribute itself projects
to nothing: each propagator is printed once, through the variable that owns it,
and the owner here is `Len`, not the tail. `copy_term/3` uses the same
`attribute_goals/4`. Applied to the tail alone it still reports the link,
because it follows the length variable stored in the tail's attribute.

## What changes in `clpfd.pl`

- The operator declaration.
- `clpfd_expr/2`: the `@L` clause (before the catch-all that raises
  `type_error(fd_expression, E)`).
- `verify_attributes/4`: a clause for `spine(Len, Off)`.
- `attribute_goals/4` (through `clpfd_attr_goals/3`): `spine(_, _)` projects to
  `[]`.
- `'$fd_length'/2`, the propagator, and `clpfd_prop_to_goal/2` for it.
- `clpfd_makevar/1` and `fd_var/1` test for the `fd(_, _)` shape instead of
  for any `clpfd` attribute, so a tail used as an integer is
  `type_error(integer, T)` and `fd_var(T)` fails. The other six `get_attr`
  sites already match `fd(...)`.

Nothing changes in the engine, the domain builtins or the wasm tier: the
library is baked into its bundle as before, and the tier's inline attribute
operations read the attribute cell without inspecting the term.

## Labeling

`@L` names one variable, the length of `L`, so `label/1` and `labeling/2`
accept it among the variables to label: `clpfd_labeling/4` replaces each `@L`
by that variable through `clpfd_expr/2`, which gives the variable the list
already has or an integer for a proper list. Each value tried for the length
closes the list. `length/2` and `same_length/2` are unchanged: `length(L, N)`
with `N` unbound still enumerates, whether or not `N` is an FD variable.
Making `length/2` post `@L #= N` when `N` already has a domain is a possible
later step, not part of this design.

## Tests

- `ClpfdListLengthTests` (Embedding): each posting shape (proper list, open
  list, variable, second `@` over the same tail); both directions of
  propagation, including the move on a partial binding and the growth of a
  list one cell at a time; two lists sharing a tail; the `type_error` cases;
  the projections above; labeling the length; backtracking over a growth step
  restoring the attribute and the domain; `@A #= @B` as `same_length/2` with
  either list known first.
- Wasm: three `@` cases in the differential shapes of
  `AttVarWasmDifferentialTests`, so the hook and the propagator run on the tier
  and agree with the interpreter.
- Against `length/2`: for a known prefix and a domain, labeling the length
  gives every length `length/2` gives, in the same order, and each answer is a
  proper list of that length.
