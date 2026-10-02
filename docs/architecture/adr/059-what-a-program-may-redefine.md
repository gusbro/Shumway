# ADR-059: What a Program May Redefine

## Status

Accepted (2026-09-28). Amends [ADR-008](008-module-visibility.md) (a module may
no longer shadow an ISO builtin) and replaces the rule in
`ConsultPipeline.IsProtectedHead`.

## Context

Whether a clause may define a predicate the system already provides was
decided by the language the system's predicate is written in. A predicate
registered in C# (`BuiltinsRegistry`) was protected in the global module: a
clause for it raised `permission_error(modify, static_procedure, PI)` and was
dropped. A predicate the prelude defines in Prolog was not: the user's clauses
replaced it, silently. So `append/3`, moved to C# for speed, could not be
redefined, while `length/2`, written in Prolog, could; and moving a predicate
between the two for performance changed what programs may do.

Inside a named module every head but a control construct was allowed, and
shadowed the system's predicate for that module (ADR-008).

A redefinition, when it happened, was invisible to the introspection: after a
file defined `member/2`, the program ran the file's clauses, while
`listing(member/2)` said it was not defined, `predicate_property` reported
`built_in` and `current_predicate(member/2)` failed. ISO's `current_predicate/1`
enumerates the user-defined procedures, so that last one is a conformance
error.

ISO 13211-1 knows two kinds of system procedures, control constructs and
built-in predicates, and makes them static: adding a clause to one, from
Prolog text or with `assert`, is `permission_error(modify, static_procedure,
PI)`. That part is not ours to decide. The standard has no notion of a
library: `member/2`, `append/3` or `maplist/N` are not in it, and neither are
the predicates an implementation adds of its own; whether a program may
redefine those is the implementation's choice, and the rest of this ADR is
that choice.

## Decision

1. **Every system predicate has a category**, declared, not inferred from its
   implementation:

   | category | what it is |
   |---|---|
   | control | the control constructs: `,/2`, `;/2`, `->/2`, `*->/2`, `!/0`, `call/1`, `true/0`, `fail/0`, `catch/3`, `throw/1` |
   | ISO | the built-in predicates of ISO 13211-1 and its corrigenda (Cor.1, Cor.2, Cor.3), and `phrase/2,3` from 13211-3 (DCGs) |
   | engine | every other predicate the engine provides as part of the language: `format/2,3`, `between/3`, `succ/2`, `length/2`, `msort/2`, global variables, attributed variables, the `$`-internal ones, and so on |
   | library | the list and higher-order library: `append/3`, `member/2`, `memberchk/2`, `nth0/3`, `nth1/3`, `last/2`, `reverse/2`, `select/3`, `subtract/3`, `sum_list/2`, `maplist/N`, `foldl/N`, and the like |

   The category is declared with the rest of the predicate's metadata, in the
   one place the predicate reference is generated from: a `kind` argument of
   `BuiltinsRegistry.Register` for a predicate written in C#, a field of the
   `%! Template | Section | Kind | Summary` comment for one written in Prolog.
   Where the field is left out, the source decides: a C# builtin or a prelude
   predicate is engine, a predicate of a loadable library (`library(clpfd)`,
   `library(clpr)`, ...) is library. Every ISO builtin, every control
   construct and every library predicate of the prelude or of C# says so
   explicitly. The compiler-lowered connectives (`,/2`, `;/2`, `->/2`,
   `*->/2`, `!/0`), which have no entry of their own, are control. There is
   no separate list to keep in step.

2. **What a clause may define:**

   | category | in the global module (a file without `:- module`) | in a named module |
   |---|---|---|
   | control, ISO | error | error |
   | engine | error | local definition, silent |
   | library | redefinition, with a warning | local definition, silent |

   The first row is conformance, not policy: ISO forbids it, in any module.
   The error is `permission_error(modify, static_procedure, PI)`, reported
   through the warnings channel; the clause is dropped and the rest of the file
   loads, as today. The warning names the predicate and says the file's
   definition overrides the library's.

   A named module shadows for itself only: its clauses call its own
   definition, every other module keeps the system's. The warning is for the
   global module because a redefinition there changes what every file without
   a module sees; inside a module it is ordinary, and libraries brought in
   from elsewhere do it without meaning anything by it.

   A library loaded from a dialect-tagged directory (`-L scryer:DIR`, ADR-040)
   that defines a control construct or an ISO builtin is another system's
   implementation of the standard: Scryer's `dcgs` defines `phrase/2,3`, its
   `builtins` defines most of 13211-1. Its clauses for those predicates are
   dropped without a report, and the library and its importers run the
   engine's. The error stays for text without a dialect.

3. **The library keeps its own.** A library predicate that calls another one
   (`nonmember/2` calls `member/2`) calls the library's, whatever a program
   redefined. That is already the case and stays a requirement.

4. **The introspection describes what runs.** For a redefined predicate:
   - `listing/1` lists the program's clauses;
   - `predicate_property/2` does not report `built_in`; it reports what a
     predicate of the program reports (`static` or `dynamic`, `defined`,
     `number_of_clauses(N)`, `file(F)`) and a new property, `redefined`;
   - `current_predicate/1` succeeds;
   - `clause/2` behaves as for any static predicate of the program.

5. **`assertz/1` and friends are unchanged**: they refuse a system predicate of
   any category. Loading a definition and mutating a loaded one at run time
   are different acts.

6. **Dynamic declarations follow the same table**: `:- dynamic` for a control,
   ISO or (in the global module) engine predicate is the same error.

## Consequences

### Positive

- The rule is the same whatever language a predicate is written in, and moving
  one between C# and Prolog changes nothing a program can observe.
- A file without modules cannot change the meaning of anything the engine
  provides, and hears about it when it overrides the library.
- Libraries loaded from elsewhere can define their own `format/2` or
  `between/3` without warnings, and cannot redefine ISO.
- `current_predicate/1` conforms, and the tools show what the program runs.

### Negative

- A test checks that the ISO category covers 13211-1, its corrigenda
  and 13211-3 exactly: an ISO builtin left unmarked, or one of ours marked ISO, fails it.
- A module that shadowed an ISO builtin loaded before and now reports an
  error for that clause, as the standard requires.

## Validation

- A table test: for one predicate of each category, a clause in the global
  module and in a named module gives the error, the warning, or nothing, as
  the table says.
- The introspection after a redefinition: `listing`, `predicate_property`,
  `current_predicate`, `clause/2`.
- The library keeps its own: `nonmember/2` with `member/2` redefined.
- The generated predicate reference shows each predicate's category, and its
  staleness test covers it.
- The library suites (SWI, Scryer, Logtalk, clp(Z)) load as before, save for
  a clause that shadowed an ISO builtin, which is reported.
