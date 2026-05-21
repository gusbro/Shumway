# Phase 6 — Closure

**Status**: complete.

**Tagged**: `phase-6` (this commit).

Phase 6 delivers **constraint logic programming over finite domains** —
CLP(FD) — as an opt-in library, plus the one deferred soundness fix that
opened the phase. It began as "Constraints, AOT, tabling"; CLP(FD) grew
into a phase of its own, so Native AOT and tabling were split out to
Phase 7 and the phase rescoped to what actually landed. This document
records what shipped and what was deliberately left out.

---

## Deliverables checklist

Tracking the Phase 6 list from [`CLAUDE.md`](../CLAUDE.md).

| Deliverable | Status | Implementing work |
|-------------|--------|-------------------|
| `!` inside a runtime compound `call` goal commits soundly | ✓ | chunk 88 |
| CLP(FD) core — domains, `in`/`ins`, the six arithmetic constraints | ✓ | chunk 89 |
| CLP(FD) multiplication and labeling | ✓ | chunk 90 |
| CLP(FD) `all_different` and reification | ✓ | chunk 91 |
| CLP(FD) remaining arithmetic and `sum/3` | ✓ | chunk 92 |
| CLP(FD) refinements completing the library | ✓ | chunk 93 |

Done in the same window, surfaced by building CLP(FD):

| Deliverable | Status | Implementing work |
|-------------|--------|-------------------|
| Attvar+attvar merge defers to the `verify_attributes/4` hook | ✓ | engine fix in chunk 89 |

Moved out of the phase:

| Item | Disposition |
|------|-------------|
| Native AOT support | → Phase 7 |
| Tabling | → Phase 7 |
| CLP(R) | → Phase 7 (if needed) |

---

## By the numbers

- **6 substantive chunks** (88–93) since the Phase-5 tag, plus one
  roadmap-rescoping commit.
- **1800 passing tests, 0 failing, 0 skipped** across 5 projects
  (+111 over the Phase-5 tag's 1689):
  - `Shumway.Tests.Core` — 413
  - `Shumway.Tests.Interpreter` — 98
  - `Shumway.Tests.Compiler` — 222
  - `Shumway.Tests.IsoConformance` — 61
  - `Shumway.Tests.Embedding` — 1006 (+111: `Chunk88Tests` 12, plus the
    five CLP(FD) suites `Chunk89`–`Chunk93Tests` totalling 99)
- **No new opcodes, no new cell tags, no ADR changes.** Phase 6 stayed
  entirely inside the established invariants — CLP(FD) is ordinary
  Prolog built on the Phase-4 attributed-variable foundation.

---

## What Phase 6 added

### The `!`-inside-`call` soundness fix (chunk 88)

`call((a, !, b))` treated the cut as a no-op. That is *unsound*:
backtracking re-ran clauses ISO would have committed away, re-executing
their non-backtrackable side effects. `DispatchCall` now threads the
enclosing `call`'s cut barrier through the `$call_*` prelude helpers via
`'$call'/2`, so a `!` in a runtime `,`/`;`/`->` goal commits exactly as
far as the `call` — and no further.

### CLP(FD) — an opt-in constraint library

An embedder calls `engine.UseClpfd()` to consult the `clpfd` module;
engines that do not need constraints carry none of its weight. The
library is ordinary Prolog built on attributed variables: an FD variable
carries a `clpfd` attribute `fd(Domain, Propagators)` — its domain (a
sorted list of disjoint `L-H` intervals) and the suspended propagator
goals. Posting a constraint suspends propagators and runs them once;
narrowing a domain re-runs the watchers to a fixpoint; binding fires the
`verify_attributes/4` hook.

What landed across chunks 89–93:

- **Domains and membership** — `in`/`ins`, sorted interval-list domains
  with `inf`/`sup` bounds (chunk 89).
- **Arithmetic constraints** — `#=`, `#\=`, `#<`, `#>`, `#=<`, `#>=`
  over expressions built from `+`, `-`, `*`, `//`, `min`, `max`, `abs`
  and unary `-`, all with bounds propagation (chunks 89, 90, 92).
- **Labeling** — `label/1`, `labeling/2` (variable selection
  `leftmost`/`ff`, value order `up`/`down`) and `indomain/1`, running
  propagation between assignments (chunk 90).
- **Global constraints** — `all_different/1` (pairwise) and
  `all_distinct/1` (a single `$fd_alldiff` propagator with Hall-interval
  pruning) (chunks 91, 93).
- **Reification** — `#<==>`, `#==>`, `#<==` and the boolean connectives
  `#/\`, `#\/`, `#\`, each comparison reified through an
  entailment-checking `$fd_reif` propagator (chunk 91).
- **Aggregate constraints** — `sum/3` and `scalar_product/4` (chunks
  92, 93).

Every chunk shipped a dedicated test suite; CLP(FD) carries 99 tests
(`Chunk89`–`Chunk93Tests`) covering forward and backward propagation,
failure cases, labeling enumeration and ISO error terms.

### Attvar+attvar merge defers to the hook (engine fix, chunk 89)

CLP(FD) exposed a latent Phase-4 bug. Chunk 77's hookless attvar merge
rule required two unified attributed variables' shared-module attribute
values to *unify* — but two FD variables carry deliberately different
`fd(Domain, Propagators)` values, so `X = Y` failed before the
`verify_attributes/4` hook (which is what should intersect the domains)
could run. `Engine.MergeAttributes` now defers a shared module's merge
to the hook whenever a `verify_attributes/4` hook is defined; the
hookless "values must unify" rule still applies verbatim when no hook
exists, so every Phase-4 attvar test passes unchanged.

---

## Architecture notes

- **CLP(FD) is a library, not an engine layer.** It lives in
  `src/Shumway.Embedding/Clpfd.cs` as a Prolog source string consulted
  on demand. It introduced no opcode, no cell tag and no ADR change —
  the attributed-variable machinery from Phase 4 was sufficient.
- **The chunk-88 fix is a fix, not a redesign.** It threads an existing
  value (the cut barrier) through the `$call_*` helpers; no new opcode.
- **The chunk-89 engine change is a fix, not a redesign.** It narrows
  `MergeAttributes` to respect the hook model that Phase 4's chunks
  79–80 introduced — the hookless pre-unification was leftover behaviour
  that should have been retired then.

---

## Deferred — to Phase 7

- **CLP(R)** — constraints over the reals, if a use case calls for it.
- **Native AOT support.**
- **Tabling** — predicate memoisation.

Within CLP(FD) itself the library is feature-complete for finite-domain
work; further refinement (domain-consistent rather than bounds-consistent
propagators, a richer labeling search) would be optimisation, not new
capability.

---

## What Phase 6 buys you

Shumway can now solve finite-domain constraint problems. A program
calls `engine.UseClpfd()` and then states constraints —
`X in 1..9, all_distinct(Row), sum(Row, #=, 45)` — and `label/1`
searches for solutions, with propagation pruning the search between
assignments. Reification lets constraints be reasoned about as
0/1 values, so conditional and disjunctive models are expressible.

And, independent of CLP(FD), a `!` inside a `call`'d control construct
now commits soundly instead of silently doing nothing.

Phase 7 picks up from a green 1800-test suite and an unchanged ADR
ledger: CLP(R), Native AOT and tabling.
