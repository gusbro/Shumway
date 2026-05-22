# Phase 7 — Closure

**Status**: complete.

**Tagged**: `phase-7` (this commit).

Phase 7 turns Shumway from "a correct embeddable Prolog" into one a
real program can be dropped onto: generated reference documentation,
the common library predicates typical code expects, constraints over
the reals (CLP(R)), a Native AOT publish path, and a full tabling
implementation — memoisation, semi-naive evaluation, table invalidation
and well-founded negation. This document records what shipped and what
was deliberately left out.

---

## Deliverables checklist

Tracking the Phase 7 list from [`CLAUDE.md`](../CLAUDE.md).

| Deliverable | Status | Implementing work |
|-------------|--------|-------------------|
| Generated user-facing predicate documentation | ✓ | chunks 94–95 |
| Common library predicates (lists, atom/number, control / DB / I/O) | ✓ | chunks 96–98 |
| CLP(R) — linear equality core | ✓ | chunk 99 |
| CLP(R) — inequalities (Fourier–Motzkin) | ✓ | chunk 100 |
| CLP(R) — disequality and non-linear constraints | ✓ | chunk 101 |
| CLP(R) — constraint projection | ✓ | chunk 102 |
| Native AOT support | ✓ | chunk 103 |
| Tabling — `:- table`, memoisation, naive fixpoint | ✓ | chunk 104 |
| Tabling — O(n log n) fixpoint passes | ✓ | chunk 105 |
| Tabling — semi-naive evaluation | ✓ | chunk 106 |
| Tabling — table invalidation and tabled negation | ✓ | chunk 107 |
| Tabling — non-ground answers | ✓ | chunk 108 |
| Tabling — well-founded negation | ✓ | chunk 109 |

Engine fixes surfaced by the above:

| Fix | Implementing work |
|-----|-------------------|
| `==/2` and `\==/2` handle floats | chunk 97 |
| `retract/1` is re-satisfiable (ISO) | chunk 97 |
| Undefined-predicate call raises `existence_error` | (Phase 5, used throughout) |

---

## By the numbers

- **16 chunks** (94–109) since the Phase-6 tag.
- **1946 passing tests, 0 failing, 0 skipped** across 5 projects
  (+146 over the Phase-6 tag's 1800):
  - `Shumway.Tests.Core` — 413
  - `Shumway.Tests.Interpreter` — 98
  - `Shumway.Tests.Compiler` — 222
  - `Shumway.Tests.IsoConformance` — 61
  - `Shumway.Tests.Embedding` — 1152 (+146)
- **No new opcodes, no new cell tags, no ADR changes.** Everything in
  Phase 7 is library code, consult-time transforms or runtime guards
  built on the established invariants.

---

## What Phase 7 added

### Predicate documentation (chunks 94–95)

Doc metadata lives *next to each definition* — a category, a moded call
template and a summary, passed to `BuiltinsRegistry.Register` for C#
builtins and written as a structured `%! Template | Category | Summary`
comment in the Prolog library sources. `PredicateDoc.Generate()` walks
all three sources, groups by area and emits
[`docs/predicates.md`](predicates.md). A unit test regenerates and
fails if the committed file is stale (`SHUMWAY_REGEN_DOCS` rewrites it).

### Common library predicates (chunks 96–98)

So typical Prolog runs unchanged: list utilities (`select/3`,
`permutation/2`, `subtract/3`, `numlist/3`, `sum_list/2`, `include/3`,
`exclude/3`, `partition/4`, `predsort/3`, `pairs_keys_values/3`, …),
atom/number conversion (`atom_number/2`, `number_string/2`,
`atomic_list_concat/2,3`, `char_type/2`) and control / database / I/O
(`once/1`, `ignore/1`, `apply/2`, `findall/4`, `retractall/1`,
`listing/0,1`, `format_to_atom/3`). Most are pure Prolog in the
prelude; each carries doc metadata, so all appear in `predicates.md`.

### CLP(R) — constraints over the reals (chunks 99–102)

The opt-in `clpr` library (`engine.UseClpr()`), constraints written in
the `{Constraint}` wrapper:

- **Linear equality** — a Gaussian-elimination solver over attributed
  variables with lazy expansion (chunk 99).
- **Inequalities** — `<`, `>`, `=<`, `>=`, satisfiability tested by
  Fourier–Motzkin elimination on every post (chunk 100).
- **Disequality** (`=\=`) — fails only when the inequalities entail the
  linear form is pinned to zero; **non-linear constraints** — a product
  or quotient of non-constants is delayed and retried as variables
  determine (chunk 101).
- **Constraint projection** — `copy_term/3` collects the residual
  constraints on the copied term's variables as `{...}` goals (chunk 102).

CLP(R) and CLP(FD) cannot share an engine — both define a public
`verify_attributes/4`.

### Native AOT (chunk 103)

The Tier-0 bytecode interpreter is AOT-compatible; Tier-1 IL promotion
is runtime code generation, so it is cleanly skipped under AOT —
`IlPromotionStore` checks `RuntimeFeature.IsDynamicCodeSupported` and
never constructs the IL compiler. The REPL (`<PublishAot>true</PublishAot>`)
is the publish target: `dotnet publish` yields a self-contained native
`shumway`. See [`docs/native-aot.md`](native-aot.md).

### Tabling (chunks 104–109)

`:- table p/N` memoises a predicate so left-recursive and cyclic
definitions terminate. The consult-time transform splits each tabled
clause into base / recursive forms and routes calls through a driver;
the table lives in the runtime dynamic store, read with `clause/2`.

- **Core** (104) — naive global fixpoint; transitive closure and mutual
  recursion terminate.
- **O(n log n) passes** (105) — answers as a sorted per-subgoal list.
- **Semi-naive** (106) — each recursive clause's tabled literal becomes
  a `'$tbl_consume'` reading the producer's *delta*; the engine-backed
  `'$tbl_seen'` set gives O(1) duplicate detection. ~3.5× faster on a
  500-deep closure, widening with depth.
- **Invalidation + negation** (107) — `abolish_all_tables/0`,
  `abolish_table/1`; `\+` over a tabled goal.
- **Non-ground answers** (108) — variant tabling: the duplicate test
  canonicalises variables by first-occurrence index.
- **Well-founded negation** (109) — a program with tabled negation is
  evaluated by the alternating fixpoint, so negative cycles terminate
  and their atoms become *undefined*; `well_founded(Goal, Status)`
  reports `true` / `false` / `undefined`.

---

## Architecture notes

- **Everything is library code or a consult-time transform.** CLP(R)
  is a Prolog source string; tabling is a clause transform plus a
  prelude driver; AOT support is runtime guards. No opcode, cell tag
  or ADR moved.
- **Three small stateful builtins** back tabling — `'$tbl_seen'/1`
  (the O(1) answer-dedup set), `'$tbl_seen_clear'/0` and
  `'$tbl_solve_complete'/1` — each a thin wrapper over per-engine
  state on `PrologEngine`.
- **Well-founded negation subsumes stratified negation.** The chunk-107
  stratified mechanism was replaced wholesale by the chunk-109
  alternating fixpoint, which converges to the two-valued model for a
  stratified program.

---

## Deferred — to Phase 8

Phase 7 surfaced a cluster of engine-robustness issues, recorded in the
**Phase 8 — Engine robustness** backlog in [`CLAUDE.md`](../CLAUDE.md):

- **No last-call optimisation** — a deep tail-recursive predicate grows
  the control stack; this is the root cause of the tabling
  fixpoint-depth limit (a fixpoint deeper than ~1000 rounds overflows).
- **`between/3` misbehaves driving a failure-driven loop** (undiagnosed).
- **No `repeat` builtin.**
- **Same-query `assertz` is invisible to direct calls** — a query
  compiles a fixed snapshot; `clause/2` reads the live store (the
  workaround the tabling driver relies on).

Within tabling, well-founded negation assumes negated atoms are ground;
a richer treatment is optimisation, not new capability.

---

## What Phase 7 buys you

A program written for a mainstream Prolog now has a fair chance of
running on Shumway unchanged: the common library predicates are there,
and `docs/predicates.md` says what is available. Constraint code can
reach for finite domains (Phase 6) or the reals (CLP(R)). The engine
can be shipped as a single native executable with no .NET runtime
dependency. And tabling is a real implementation — termination on
left recursion and cycles, semi-naive evaluation, table invalidation,
non-ground answers, and negation under the well-founded semantics —
not a memoisation toy.

Phase 8 picks up from a green 1946-test suite and an unchanged ADR
ledger, with engine robustness — last-call optimisation chief among it
— as the agenda.
