# Phase 3 — Closure

**Status**: complete.

**Tagged**: `phase-3` (this commit).

Phase 3 is the advanced-optimisation phase: it teaches the compiler to
exploit information it previously only stored or ignored. Mode
declarations stop being inert metadata; runtime call patterns start
steering both indexing and IL dispatch. As with Phase 2, no
user-visible language feature is added — every chunk makes correct
programs faster. This document records what landed, with pointers to
the chunks, and what carries forward to Phase 4.

---

## Deliverables checklist

Tracking the Phase 3 list from [`CLAUDE.md`](../CLAUDE.md).

| Deliverable | Status | Implementing chunk |
|-------------|--------|--------------------|
| Mode inference (using `:- mode` directives) | ✓ | 73 |
| Specialized code generation per mode | ✓ | 74 |
| JIT indexing | ✓ | 75 |
| Profile-guided optimization (PGO) of IL code | ✓ | 76 |

---

## By the numbers

- **4 commits**, chunks 73–76, from the Phase-2 tag to closure.
- **1565 passing tests, 0 failing, 0 skipped** across 5 projects
  (+57 over the Phase-2 tag's 1508):
  - `Shumway.Tests.Core` — 413
  - `Shumway.Tests.Interpreter` — 98
  - `Shumway.Tests.Compiler` — 222 (+8: IL profile-counter unit tests)
  - `Shumway.Tests.IsoConformance` — 61
  - `Shumway.Tests.Embedding` — 771 (+49: chunks 73–76 end-to-end)
- **New compiler subsystem**: `Shumway.Compiler.Modes` — the mode
  data model, directive parser, mode table, and specialisation
  transform.

---

## What Phase 3 added

### Mode-analysis foundation (chunk 73)

ADR-012's `:- mode` directive was parsed and stored as raw strings
since chunk 28 — inert metadata. Chunk 73 builds the real model:
`ModeIndicator` (`+` / `-` / `?`), `Determinism`
(det / semidet / multi / nondet), a typed `ModeDeclaration`, the
`is det` annotation, multiple declarations per predicate, and a
queryable `ModeTable` with a semantic validation pass. This is the
vocabulary the rest of Phase 3 reads.

### Specialized code generation per mode (chunk 74)

The first mode-aware code-gen pass. When every declared mode of a
predicate is deterministic, `ModeSpecializationTransform` appends an
implicit trailing cut to each clause — a predicate the user
declared det / semidet leaves no dangling choice point and
backtracking never re-enters it. A predicate with any multi / nondet
mode keeps full backtracking; the cut would be unsafe. The trailing
cut (rather than a head-commit) is the correct realisation: det /
semidet promise *at most one solution*, not mutually-exclusive
heads, so body failure in one clause must still fall through to the
next.

### JIT indexing (chunk 75)

A dynamic predicate now compiles to a plain `try_me_else` chain —
cheap to build, O(N) dispatch — until its runtime call count
crosses a threshold, at which point the next query recompiles it
with full multi-arg indexing (switch tables, O(1) dispatch). A
`JitIndexProfile` counts calls on the existing dispatch hook; the
chunk-68 dynamic cache holds the compile at the right indexing
level. A dynamic predicate that's rarely called, or churning under
heavy assertz / retract, never pays the switch-table build cost.

### Profile-guided optimization of IL code (chunk 76)

A two-phase PGO loop. A multi-clause predicate that promotes to
Tier-1 IL is first compiled in an *instrumented* form whose
indexed-atom ground dispatch records which atom matched. Once the
profile has enough samples, a query-setup pass recompiles the
predicate *optimised* — the ground-dispatch cmp chain reordered so
the most-frequently-matched atom is checked first — and drops the
instrumentation. The ground dispatch is a pure lookup, so the
reorder is always semantics-preserving; the var-dispatch
enumeration path keeps source order.

---

## Architecture notes

- **No new cell tags, no trail-format change, no bytecode-encoding
  change, no threading-model change.** Phase 3 stayed entirely
  inside the established invariants. The mode work added a new
  compiler namespace (`Shumway.Compiler.Modes`) but no runtime
  representation change.
- Mode specialisation (chunk 74) reuses the existing cut compilation
  end-to-end — the synthesised trailing cut is an ordinary cut as
  far as the WAM and IL compilers are concerned.
- JIT indexing (chunk 75) and the dynamic-predicate cache (chunk 68)
  compose: the cache stores the compile at whatever indexing level
  the JIT profile last dictated, and a cold→hot flip drops the stale
  entry.
- PGO (chunk 76) reuses the Tier-0 → Tier-1 promotion machinery as
  its instrumentation trigger; the phase-2 recompile rides the same
  delegate-swap the promotion store already does.

---

## Deferred to Phase 4

Per the `CLAUDE.md` roadmap, the next phase is extended features:

| Feature | Phase |
|---------|-------|
| Attributed variables (`attvar`) | 4 |
| CLP(FD), CLP(R) | 4 |
| Native AOT support | 4 |
| Tabling | 4 |

Phase 3 also left two deliberate, documented scoping decisions —
correct as shipped, with a clear extension path:

- **Mode inference is declaration-driven, not usage-inferred.**
  ADR-012 always placed usage-pattern inference after the
  declaration-based exploitation; chunk 73 consumes `:- mode`
  declarations, and inferring modes from clause bodies / call sites
  remains a later refinement.
- **PGO reorders the indexed-atom ground dispatch only.** The
  two-phase framework (instrumented → optimised, with
  `IlProfileCounters` and the promotion-store phase tracking) is
  general; chunk 76 wires one optimisation through it. Reordering
  the try-me-else chain — safe only for det / semidet predicates,
  and entangled with chunk 74's cut — is a natural follow-up that
  the framework already supports.

Both are intentional cut points, not unfinished work.

---

## What Phase 3 buys you

A program correct at the `phase-2` tag is still correct at
`phase-3`, and:

- A predicate the user declared `det` / `semidet` runs without
  leaving choice points — no trail growth, no stack growth, no
  spurious re-entry on backtracking.
- A dynamic predicate only pays for switch-table construction once
  it has proven itself hot; the common "consult, query a handful of
  times" shape stays on the cheap unindexed chain.
- A hot indexed-atom predicate's dispatch reorders itself around the
  atoms the program actually looks up, so the common case is the
  first comparison.

Phase 4 picks up from a green 1565-test suite, an unchanged ADR
ledger, and the two intentional cut points above written down.
