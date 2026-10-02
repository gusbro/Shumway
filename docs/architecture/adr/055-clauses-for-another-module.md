# ADR-055: Clauses for Another Module (`M:Head :- Body`)

## Status

Accepted (2026-09-26). Extends [ADR-008](008-module-visibility.md) and
[ADR-038](038-library-loading.md).

## Context

A source file may define a clause for a predicate of another module by
qualifying its head: `user:portray(X) :- ...`, `prolog:message(E) --> ...`,
`other:foo(1).`. The consult honoured this for the two expansion hooks only
(`user:term_expansion/2`, `user:goal_expansion/2`): it stripped the
qualifier and kept the clause in the file's module. Every other qualified
head was left as written, so the clause defined `(:)/2`, and `foo/1` did not
exist: `user:fact(5).` raised an existence error on `fact(5)`, and so did
`src:own(7).` written in `src` itself.

There is no standard to follow here: ISO 13211-2 (modules) has no
implementation anyone uses. For module semantics this engine follows
SICStus and Scryer. Each point below was checked against Scryer; SICStus is
not at hand, and the points cite its manual.

## Decision

A clause whose head is `M:Head`, with `M` an atom, read in a source whose
module is `S`:

1. **Defines `Head` in module `M`.** When `M` is `S`, it is an ordinary
   clause of `S`. Nested qualification (`a:b:h`) takes the innermost module.
   A module that does not exist yet is created, empty, with the clause in it
   (Scryer does the same).
2. **Runs its body in `S`.** The body's goals resolve in the module the
   clause was written in, not in `M`: `user:hello :- helper.` in `src` calls
   `src`'s `helper/0`, even when `user` has one of its own.
3. **Representation.** The clause goes into `M`'s manifest, so its head is
   named by `M`'s own rules (a local of `M`, a public, an export) and
   `clause(M:Head, B)` finds it there. Its body is stored qualified with `S`,
   distributed over the control constructs (`,`, `;`, `->`, `*->`, the cut
   left as it is) and into the goal arguments of the meta-predicates
   (ADR-056). A builtin is not qualified itself, only its goal arguments.
   `ModuleRewrite` already resolves a written `S:Goal` statically in `S`, so
   no new resolution rule exists: the clause compiles as if each goal had
   been written qualified. `clause/2` shows the distributed form
   (`src:helper`, `(src:a, src:b)`), which is the same body.
4. **Dynamic and multifile predicates are global** (ADR-008; `:- multifile`
   makes a predicate dynamic here), so a qualified head of one loses its
   qualifier and the clause joins the dynamic store as any other clause of
   it, its body qualified with `S` as above. Clauses from several sources
   accumulate there.
5. **Grammar rules** are translated first and then treated as the clause
   they define.
6. **One source defines a predicate** (SICStus). The consult remembers which
   clauses each source contributed to other modules (the source is its
   module for a module file, the file or the string buffer otherwise). A
   static predicate is defined by one source: when another source defines it
   in `M`, the earlier clauses are replaced, with a warning. That holds
   between two modules writing `M:Head`, between a module and `M`'s own
   file (whichever loads later redefines), and between a module writing
   `user:Head` and a module-less source defining the same predicate.
   Declaring the predicate multifile (`:- multifile(user:f/1)`) is how
   several sources contribute clauses to it, as in SICStus; Scryer rejects
   that declaration and so cannot. A module file loaded again withdraws
   its previous contributions before adding the new ones, and so does a
   reconsulted module-less source. A module replaced by a reload of its own
   file keeps what other sources contributed to it, except the predicates
   the reload now defines itself.
7. The expansion hooks keep their own path (stripped, kept in `S`), which
   ADR-038's early activation and the in-file hook pass depend on.

## Consequences

- `user:portray/1`, `user:file_search_path/2`, `prolog:message//1` and a
  library defining clauses for its client modules load as they do elsewhere.
- Extending another module's predicate from outside (a solver's rule table)
  needs that predicate declared multifile, by the module and by the
  extending source. clp(Z) does not declare `run_propagator//2` multifile,
  so the custom propagator its documentation shows replaces all of clp(Z)'s
  own, here as in Scryer.
- Two module-less files defining the same predicate still both contribute:
  that is this engine's rule for plain consults, and rule 6 only governs
  definitions made through `M:Head`.
- A clause body that crosses modules is compiled with one qualified goal per
  call; each resolves statically when the target module is loaded, and
  through the runtime `':'/2` otherwise, as a written `M:Goal` does.
- `listing/1` and `clause/2` show the body with its goals qualified.
- **Separate compilation** needs `shumway-compile --consult`, which runs the
  consult. What a module gives `user` is emitted in that module's object as
  public predicates: in a linked program `user:Head` means "visible to the
  whole program", which is what a public is, and the body keeps running in
  the module. A multifile or dynamic predicate travels as the dynamic seeds
  of its declaring module, all sources' clauses included. A module the
  consult created (`other` above) becomes an object of its own. Without
  `--consult`, a file with a qualified clause head is refused with that
  advice.
- Compiled code calling `other:foo(2)`, where `foo/1` was defined into
  `other` and is not public there, links: a written `M:Goal` reaches any
  predicate `M` defines (ADR-056).

## Tests

`ClausesForAnotherModuleTests`: the points above, including reload (no
duplicates, a removed definition disappears, a replaced module keeps what
others added), redefinition between modules, between a module and the
target's own file and between a module and a user source, multifile
accumulation, dynamic, grammar rules, meta-arguments and the cut.
