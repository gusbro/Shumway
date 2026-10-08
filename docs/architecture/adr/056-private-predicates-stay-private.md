# ADR-056: A Module's Private Predicates Stay Private

## Status

Accepted (2026-09-26). Amends [ADR-008](008-module-visibility.md) and
[ADR-038](038-library-loading.md). Withdraws the consult-direct fallback of
commit 39a0f863 (August 2026).

## Context

A predicate that a `:- module` file does not export is private to that
module. The linker has always enforced it: a bare call to another module's
private is a `missing_predicate` error. The interactive engine did not.

In August 2026 the engine gained a fallback for the top level: when a bare
goal was about to raise `existence_error`, it was resolved to the one
directly consulted module that defined the name as a private, and to an
ambiguity error when two did. The intent was the top level, and every test
of it called from a query. The implementation sat in the dispatch sites,
where every unresolved bare call ends, so it answered for any caller: a
clause of another module, a clause of a file without a module directive, a
runtime-built goal. A private predicate became reachable from everywhere
as soon as its module was consulted directly, and the same program behaved
differently in the REPL and in a linked executable.

No reference system does this. Scryer raises `existence_error` for a bare
call to another module's private, from the top level and from another
module alike, and reaches it only through `M:Goal`; SICStus documents the
same rule.

## Decision

1. **The fallback is removed.** A private predicate is called by its bare
   name only from its own module's clauses. The top level, another module
   and a file without a module directive reach it only by qualifying:
   `m:p(X)`, `call(m:p(X))`, `G = m:p(X), call(G)`.
2. Nothing else changes. A file without a module directive consulted
   interactively defines predicates of `user`. Dynamic predicates stay
   global (ADR-008). A use_module import and the auto-import of a directly
   consulted module's exports into `user` work as before.
3. The name of a private cannot become a global dynamic: `assertz` on it
   from outside the module raises `permission_error(modify,
   static_procedure, _)`. A global dynamic of that name would take it over
   inside the module, since dynamic predicates are global.
4. **No bare alias for a private.** The address map runtime meta-calls
   use gave every module-local predicate an alias under its bare name, so
   `G = p(X), call(G)` reached a private from anywhere. Only two kinds of
   local keep one: those of `user`, which is the global module, and the
   helpers the compiler generates, whose callers name them unqualified.
5. **A meta-argument carries its module.** What the aliases really served
   was a closure a module hands to a library predicate: `maplist(ok, L)`
   calls `ok` from maplist's clauses, which cannot see the module's
   private `ok`. The module now goes with the closure, as in SICStus and
   Scryer:
   - At compile time, a meta-argument of a call in module `m` becomes
     `m:Arg`. The meta-arguments are those of the callee's
     `meta_predicate` declaration, and for the prelude's higher-order
     predicates that declare none (`maplist`, `foldl`, `include`,
     `exclude`, `partition`, `predsort`, `phrase`, `time`, and a few
     more), a fixed table, kept out of `predicate_property/2`. An argument
     that resolves the same from any module stays as written: one naming
     a global or builtin predicate that is neither a control construct
     nor a meta-predicate. In `user`, whose own predicates are global,
     that leaves variables and imports to qualify, and a control construct
     only through a sub-goal that needs it: wrapped anyway, the module
     showed in an ISO error's culprit (`call(',', fail, 3)` reported
     `(user:fail, user:3)`). A call to the module's
     own predicate is not qualified: its clauses resolve in the same
     module, and whatever they pass on to another module is qualified
     there, at the crossing. Qualifying it would wrap a library's
     recursion (`maplist` calling itself) once per step.
   - At run time, a goal dispatched with a module (a variable goal a
     clause of `m` meta-calls, or `m:Goal`) passes the module to its
     meta-arguments the same way. `user` passes none: its predicates are
     global.
   - `M:Goal`, at compile time or at run time, makes `M` the context of
     Goal's meta-arguments, so `hof:apply_to(ok, X)` looks for `ok` in
     `hof`, as in Scryer.
   - The goals a `verify_attributes/3` hook returns run in the
     attribute's module, as in SICStus and Scryer.
   - A builtin that judges a meta-argument looks past its qualifier:
     `setup_call_cleanup/3` raises `instantiation_error` for `M:X` with
     `X` unbound, and `bagof/3` and `setof/3` find the `^` inside
     `M:(V^Goal)`.
   - `phrase(m:Body, L)` translates `Body` and runs it in `m`.
6. `:- dynamic` and `:- visible` predicates are global and unaffected.
7. **The linker agrees.** A written `M:Goal` in compiled code links when `M`
   defines the predicate, public or not, as it runs in the REPL; it used to
   require a public. The predicate it reaches is called by name from outside
   its module, so link-time optimization counts it among the externally
   reachable seeds: region pruning keeps its standalone form instead of
   treating it as reachable only from its module's own code.

## Consequences

- The REPL, the embedding API and a linked executable resolve a bare call
  the same way.
- A program or test that called a private from the top level has to
  qualify the call or export the predicate.
- A higher-order predicate a library defines without a `meta_predicate`
  declaration cannot call a module's private closure; it could only
  through the aliases. SICStus and Scryer have the same requirement: only
  a `meta_predicate` declaration carries the caller's module.
- A goal frozen in `user` is stored `user:G`, since it runs there, and the
  top level shows it without the module, as Scryer's does.
- A qualified closure is a small term built per call: on the clp(Z)
  benchmark heap cells grow 1 to 5 percent, inferences and time are
  unchanged within noise. The Van Roy set allocates exactly what it did.

## Tests

`ModulePrivatePredicateTests`: the top level, another module and a file
without a module directive all get `existence_error`, for a direct call
and for a goal built at run time; qualifying reaches the private; two
modules keep their own private of the same name; a use_module
dependency's private stays private; dynamics stay global; the private's
name cannot become a global dynamic. A module's private closures reach
`maplist`, `include`, `foldl`, `predsort` and a declared meta-predicate,
directly and through a goal built at run time; a frozen goal and a hook's
goals run in their module; `phrase` runs a qualified body there; `user`'s
predicates stay reachable from library meta-calls; `M:Goal` gives `M` the
context. `QualifiedPrivateCallLinkTests`: a written `M:Goal` to a private
links and runs, with and without WAM, and the private is among the
externally reachable seeds. Red counter-proofs: restoring every alias fails
the runtime-goal tests, dropping the meta-argument specs fails the closure
tests, and dropping the qualified targets from the seeds fails the seed
test.
