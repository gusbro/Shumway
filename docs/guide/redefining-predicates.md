# Redefining predicates

A program can define a predicate with the same name and arity as one Shumway
provides. What is allowed depends on the kind of predicate and on whether the
clauses are in a module. Design: [ADR-059](../architecture/adr/059-what-a-program-may-redefine.md).

## Kinds of system predicates

| kind | examples | outside a module | inside a module |
|---|---|---|---|
| control construct | `,/2`, `;/2`, `->/2`, `*->/2`, `!/0`, `call/1`, `true/0`, `fail/0`, `catch/3`, `throw/1` | error | error |
| ISO builtin | `atom_length/2`, `write/1`, `functor/3`, `sort/2`, `assertz/1`, `call/2` to `call/8`, `phrase/3` | error | error |
| engine builtin | `format/2`, `between/3`, `succ/2`, `length/2`, `msort/2`, `nb_getval/2` | error | allowed |
| library predicate | `append/3`, `member/2`, `memberchk/2`, `reverse/2`, `nth0/3`, `maplist/3`, `foldl/4` | allowed, with a warning | allowed |

The ISO standard makes control constructs and ISO builtins static, so no
program can add clauses to them. The kind of each predicate is in the Kind
column of [predicates.md](predicates.md).

## Outside a module

Predicates in a file without a `:- module` directive belong to the global
module. Redefining a library predicate there prints a warning:

```prolog
% lists.pl
member(X, [X|_]) :- !.
member(X, [_|T]) :- member(X, T).
```

```
?- [lists].
% member/2: the definition in lists.pl overrides the library predicate
```

Every call to `member/2` from the global module then runs these clauses.

A clause for any other kind of system predicate is an error. The clause is
dropped and the rest of the file loads:

```
error: no permission to modify static procedure (between)/3 - clause ignored
```

## Inside a module

A module can define its own engine builtins and library predicates, without a
warning. The definition applies to that module only; other modules keep the
system's.

```prolog
:- module(report, [show/1]).

format(Fmt, Args) :- ...          % report's own format/2

show(X) :- format("~w~n", [X]).   % calls report's format/2
```

## Libraries from other systems

A library loaded from a directory with a dialect prefix (`-L scryer:DIR`,
`-L swi:DIR`, `-L trealla:DIR`) may define control constructs or ISO builtins
of its own, as Scryer's `dcgs` does for `phrase/2` and `phrase/3`. Those
clauses are dropped without an error, and the library uses Shumway's
definitions. See [Loading third-party Prolog libraries](user-guide.md#loading-third-party-prolog-libraries).

## Library predicates keep the library's definitions

Library predicates call the library's versions of each other. With `member/2`
redefined as above, `nonmember/2`, which uses `member/2`, is unaffected.

## Introspection after a redefinition

| query | result |
|---|---|
| `listing(member/2)` | the program's clauses |
| `predicate_property(member(_, _), P)` | `static`, `defined`, `number_of_clauses(N)`, `file(F)` and `redefined`; not `built_in` |
| `current_predicate(member/2)` | succeeds |
| `clause(member(X, L), Body)` | as for any static predicate of the program |

## Run-time changes

`assertz/1`, `asserta/1` and `retract/1` raise `permission_error(modify,
static_procedure, PI)` for every system predicate, whatever its kind. So does
`:- dynamic` for a predicate the file could not define.
