# Phase 4 — Closure

**Status**: complete.

**Tagged**: `phase-4` — on chunk 86 (`0ad5543`), the last Phase 4
commit. Unlike phases 1–3, the tag is not on this closure commit: the
Phase 5 top-level (chunk 87) had already landed, so `phase-4` is placed
to mark the true end of Phase 4 work.

Phase 4 is the extended-features phase. It set out to deliver attributed
variables and the constraint / AOT / tabling work the earlier phases had
deferred. What it actually delivered is attributed variables in full —
and, mid-stream, a rework of the entire meta-call family (`findall`,
`bagof`, `setof`, `forall`, `catch`, `call/N`) to run in the live engine
instead of an isolated sub-engine.

The meta-call rework was not on the original roadmap. It became
unavoidable once the attributed-variable hooks needed to observe the
*live* constraint store — a hook running against a copied heap cannot
implement a real constraint solver — and, having started, it was the
natural moment to retire the sub-engine path everywhere. It is also the
substrate the deferred constraint libraries need, so the scope change
brought Phase 6 closer rather than competing with it. CLP, Native AOT
and tabling move to Phase 6. This document records what landed, with
pointers to the chunks, and what carries forward.

---

## Deliverables checklist

Tracking the Phase 4 list from [`CLAUDE.md`](../../CLAUDE.md) as it stood at
the start of the phase.

| Deliverable | Status | Notes |
|-------------|--------|-------|
| Attributed variables (`attvar`) | ✓ delivered | chunks 77–81 |
| CLP(FD), CLP(R) | → Phase 6 | needs the in-engine meta-call substrate, now in place |
| Native AOT support | → Phase 6 | scope unchanged |
| Tabling | → Phase 6 | scope unchanged |

Added to the phase mid-stream and delivered:

| Deliverable | Status | Implementing chunk |
|-------------|--------|--------------------|
| Compiled-static-predicate cache | ✓ | 82 |
| In-engine `findall/3` | ✓ | 83 |
| In-engine `bagof/3`, `setof/3`, `forall/2` (full ISO witness grouping) | ✓ | 84 |
| In-engine `catch/3` (fully backtrackable) | ✓ | 85 |
| In-engine `call/1..7` (fully backtrackable) | ✓ | 86 |

---

## By the numbers

- **13 commits** from the Phase-3 tag to chunk 86: chunks 77–86 (chunk
  80 landed in two commits), plus a two-commit env-trimming bug fix
  (`d8026e6` and its regression tests `74391fb`) that the all-solutions
  work surfaced.
- **1683 passing tests, 0 failing, 0 skipped** across 5 projects
  (+118 over the Phase-3 tag's 1565). Every Phase-4 test is an
  end-to-end test, so all +118 land in `Shumway.Tests.Embedding`:
  - `Shumway.Tests.Core` — 413
  - `Shumway.Tests.Interpreter` — 98
  - `Shumway.Tests.Compiler` — 222
  - `Shumway.Tests.IsoConformance` — 61
  - `Shumway.Tests.Embedding` — 889 (+118: the Chunk77–Chunk86 suites
    plus two env-trim regression tests)
- **One cell tag activated**: ATTVAR (0xA). It was *reserved* in
  ADR-002 from the original cell-layout design; chunk 77 fills the
  reserved slot. No new tag was added, so no new ADR — ADR-002 and the
  overview tables were updated in place.
- **Two new `TrailType` kinds**: `AttrModify` (attribute mutations)
  and `CatchFrame` (catch-frame push / deactivate). These ride the
  extra trail's existing kind-discriminated `struct[]` — the
  trail *format* is unchanged (ADR-004's designed extension point).
- **No new opcodes.** The in-engine meta-call is entirely compile-time
  transforms reusing the existing disjunction-helper machinery; the
  chunk-86 `call` dispatch is an interpreter intercept at the existing
  `CallBuiltin` opcode.
- **New builtins / hooks**: `put_attr/3`, `get_attr/3`, `del_attr/2`,
  `attvar/1`, `copy_term/3`; the `verify_attributes/4` unification
  hook and the `attribute_goals/4` residual-projection hook.

---

## What Phase 4 added

### Attributed variables (chunks 77–81)

**Foundation (chunk 77).** An attvar is an unbound variable that also
carries a set of `(module, value)` attribute pairs. The ATTVAR cell tag
(0xA) goes live; its payload is the heap index of the variable's own
home cell — a self-reference like a REF, but tagged so `Deref` stops at
it. Attribute storage is a per-engine table keyed by the home index;
every mutation is trailed via `TrailType.AttrModify`, so `put_attr` /
`del_attr` and overwrites revert on backtracking. `UnifyAttVar` handles
attvar + plain-var, attvar + value and attvar + attvar (a shared
module's values must unify, or the whole unification fails). At this
chunk an attvar is hook-less: unifying one simply binds it.

**The unification hook (chunks 78 → 79).** Chunk 78 first wired an
SWI-style `attr_unify_hook/3`; chunk 79 replaced it — before any release
— with the Scryer / SICStus `verify_attributes(Module, AttrValue, Value,
Goals)` model. `verify_attributes` is the more fundamental primitive: a
hook *returns goals to run* rather than doing the work inline, it
composes cleanly across modules, and it is the substrate a CLP library
is built on. A single global predicate dispatches on the module atom —
Shumway's flat-namespace adaptation of the implicit module argument. No
hook defined ⇒ every wakeup is a silent no-op, so the hook-less chunk-77
foundation is preserved exactly.

**In-engine wakeups (chunk 80).** Chunks 78–79 ran the hook in an
isolated peer sub-engine, so the hook and its goals saw a *copy* of the
heap — fatal for a constraint library, whose propagation goals
introspect the live store of *other* variables. Chunk 80 replaced the
sub-engine runner with an in-engine meta-call (`RunGoalInEngine` /
`MetaCallInEngine`): hooks and their returned goals run against the live
heap, trail and attribute table. This is where the in-engine meta-call
work began — the rest of the arc generalised it.

**Residual-constraint projection (chunk 81).** `copy_term/3` and the
`attribute_goals/4` hook: `copy_term(Term, Copy, Goals)` copies a term
with fresh variables and collects, per attributed variable, the goals
each module's `attribute_goals` hook produces — already expressed over
`Copy`'s variables. This turns a constrained variable's state back into
goals, for a top level to print or to round-trip a constraint.

### Compiled-static-predicate cache (chunk 82)

Every query used to recompile the whole program — every static
predicate plus the ~30-clause prelude — from AST through WAM bytecode,
then relink it. The skip-compile cache (chunk-55 bundles, chunk-68
dynamic predicates) never covered static predicates, so a pure-static
program paid an O(program) recompile on every `Query` / `findall` /
`call`. Chunk 82 adds a per-engine cache of each static predicate's
compiled bytecode — static predicates are immutable between consults, so
it is reused on every subsequent query and invalidated wholesale by
`ConsultString`. Measured ~10× drop on the query-heavy suites. This is
also what makes the in-engine meta-call cheap: a meta-called goal
compiles inline with its enclosing clause, once, and is then cached.

### The in-engine meta-call (chunks 83–86)

Each of `findall`, `bagof`, `setof`, `forall`, `catch` and `call` used
to spawn an isolated peer sub-engine for the goal — re-parsing the
prelude, copying every module, recompiling and relinking the whole
program — and any side effect the goal performed was discarded with the
sub-engine. No industrial Prolog runs meta-calls this way. Chunks 83–86
rewrite them as compile-time transforms whose goal is spliced in as an
ordinary body goal and runs in the one live engine.

- **`findall/3` (chunk 83).** `MetaTransform` rewrites a callable-goal
  `findall` into a fail-driven disjunction; the goal compiles inline
  with real choice points, and solutions are buffered as AST terms off
  the WAM heap so the enumerating backtrack cannot unwind them.
  `assertz` / `retract` inside a `findall` goal now persist.
- **`bagof/3`, `setof/3`, `forall/2` (chunk 84).** Full ISO. `bagof` /
  `setof` previously did *no* witness grouping at all — "findall +
  fail-on-empty", a documented Phase-1 gap. Chunk 84 computes the
  witness at compile time and groups solutions by witness variant in
  standard order; the algorithm follows SWI's `boot/bags.pl`.
  `forall(C, A)` becomes `\+ (C, \+ A)` with both goals spliced inline.
- **`catch/3` (chunk 85).** Fully backtrackable ISO `catch`. A
  reversible catch-frame stack lives on the `Engine`, with push /
  deactivate recorded via `TrailType.CatchFrame`; `'$catch_begin'`
  snapshots the machine and lowers the heap boundary so a caught throw
  rolls back every binding the guarded goal made. `throw/1` stays a
  .NET exception, matched top-down against the active catch frames.
- **`call/1..7` (chunk 86).** Fully backtrackable. The interpreter
  intercepts `call/N` at the `CallBuiltin` opcode and tail-jumps to the
  goal's predicate, so the goal runs with real choice points and the
  call's continuation flows on success; control constructs in a runtime
  goal route through plainly-named prelude helpers. The old
  once-semantics — a `call` silently truncating a goal's backtracking —
  is not ISO and is gone.

### A pre-existing bug, fixed (env trimming)

The all-solutions work surfaced a latent Phase-1 bug. `ClauseCompiler`
emitted a live-permanents trim operand on *every* `CallBuiltin`,
including a clause's last goal — but a last goal's environment is the
caller's (a single-goal clause allocates no frame) or is deallocated
immediately after the call, so trimming there corrupted the caller's
live `Y` slots. The `findall` / `bagof` / `setof` helper clauses end in
builtin goals and hit it hard. Fix (`d8026e6`): a last-goal
`CallBuiltin` carries `-1`, a no-trim sentinel — the parallel to
`Execute`, which never trimmed. Regression tests followed in `74391fb`.

---

## Architecture notes

- **ATTVAR was a reserved tag, not a new one.** ADR-002 reserved tag
  0xA from the start; chunk 77 activated it and updated ADR-002 in
  place. Filling a reserved slot is not the "adding a new cell tag"
  decision `CLAUDE.md` gates behind a fresh ADR, so none was written.
- **The trail format did not change.** `AttrModify` and `CatchFrame`
  are new `TrailType` discriminator values on the existing extra-trail
  `struct[]`. ADR-004 designed the extra trail precisely so new
  reversible state slots in without a format change.
- **The in-engine meta-call added no opcodes.** Chunks 83–85 are
  `MetaTransform` rewrites that reuse the disjunction-helper machinery;
  chunk 86 is an interpreter-level intercept at the existing
  `CallBuiltin` opcode. The WAM instruction set is unchanged from
  Phase 3.
- **A new resolution contract.** A predicate reached by the in-engine
  meta-call — `verify_attributes/4`, or a predicate named in a hook's
  returned goal or a runtime `call` goal — must keep a bare,
  link-rooted functor, which `:- public` or `:- dynamic` both provide
  (`ModuleRewrite` never mangles either). This is the same
  flat-namespace adaptation the explicit-module hook argument already
  made; a real module system carries it implicitly.
- **The hook model changed once, pre-release.** `attr_unify_hook/3`
  (chunk 78) was removed entirely in chunk 79 in favour of
  `verify_attributes/4`. Nothing released depended on it.

---

## Deferred to Phase 5 and Phase 6

Phase 5 — the interactive top-level — is **already delivered**: chunk 87
added `src/Shumway.Repl/`, the `shumway` executable, a basic Prolog REPL
over the `PrologEngine` embedding API. Phase 6 picks up the rest:

| Feature | Phase |
|---------|-------|
| Fix `!` inside a runtime compound `call` goal | 6 — **first item** |
| CLP(FD), CLP(R) | 6 |
| Native AOT support | 6 |
| Tabling | 6 |

Phase 4 also left a set of deliberate, documented limitations — correct
as shipped, with a clear extension path:

- **`!` inside a runtime compound `call` goal is a no-op.**
  `call((a, !, b))` does not commit; the `!` reached as a bare goal
  does nothing. This is **not** cosmetic over-generosity — it is
  *unsound*. Backtracking re-enters clauses ISO would have cut away and
  re-runs their code; a re-run `retract` / `abolish` / `assertz` is not
  backtrackable (not trailed, ADR-004), so the database is left in a
  state ISO would never produce. This is why it is Phase 6's first item.
- **Attvar hooks fire at Tier-0 goal boundaries.** A unification inside
  a Tier-1 (IL-compiled) predicate flushes its wakeups at the next
  Tier-0 boundary; per-IL-instruction wakeup checks are a follow-up.
- **`verify_attributes` returned goals run with once-semantics**, and
  `;/2`, `->/2`, `\+/1` *inside a returned goal* are not yet handled —
  plain calls and conjunctions cover clpz-style propagation.
- **A clause containing `call/N` stays in Tier 0.** The IL
  builtin-invoke path would bypass the chunk-86 dispatch, so
  `IlPredicateCompiler` rejects such a clause.
- **The catch-frame stack grows within a query** for a deterministic
  loop running many `catch/3` calls without backtracking; it is
  reclaimed on backtracking and at query boundaries. Aggressive
  in-query reclamation is a follow-up.
- **A meta-call goal still a variable at compile time** falls through
  to the original runtime `findall` / `bagof` / `setof` / `forall` /
  `catch` / `call` builtins (sub-engine, unchanged) — the transforms
  fire only for a goal known callable at compile time.

These are intentional cut points, not unfinished work — except the
first, which is a genuine soundness bug and is scheduled accordingly.

---

## What Phase 4 buys you

A program correct at the `phase-3` tag is still correct at `phase-4`,
and:

- A program can attach attributes to variables, define a
  `verify_attributes/4` hook to constrain unification, and project
  residual constraints back to goals with `copy_term/3` — the
  foundation a CLP library is built on.
- `findall`, `bagof`, `setof`, `forall`, `catch` and `call` run the
  goal in the live engine: side effects (`assertz` / `retract`)
  persist, and there is no per-call sub-engine spawn, prelude re-parse
  or whole-program recompile.
- `bagof/3` and `setof/3` do real ISO witness grouping, not the
  Phase-1 "findall + fail-on-empty" stand-in.
- `catch/3` and `call/N` are fully backtrackable per ISO — a `call` no
  longer silently truncates a goal's backtracking, and a guarded goal
  enumerates all its solutions.
- A pure-static program no longer pays an O(program) recompile per
  query.

Phase 5's interactive top-level is already in. Phase 6 picks up from a
green 1683-test suite, an ADR ledger extended only within its designed
envelopes (ADR-002's reserved tag, ADR-004's trail-kind extension), and
the limitations above written down — the unsound `!`-in-`call` case
first in line.
