# Phase 8 — Closure

**Status**: complete.

**Tagged**: `phase-8` (this commit).

Phase 8 was the **engine-robustness** pass: the cluster of issues Phases
6–7 surfaced and parked on a backlog rather than patch ad hoc. The
headline item was *same-query dynamic-predicate visibility* — the gap
that made `assertz(d(1)), d(1)` fail and forced the tabling driver to
read its own answers through `clause/2`. Closing that gap turned into
its own architectural project, **ADR-015 (persistent code space and
live dynamic dispatch)**, which dominates Phase 8 (chunks 114–128). The
other backlog items — deep-recursion overflow, `between/3` in a
failure-driven loop, no `repeat/0` — resolved in chunks 110–113, two of
the three by *finding the real cause* and discovering the original
diagnosis was wrong.

---

## Deliverables checklist

Tracking the Phase 8 backlog from [`CLAUDE.md`](../../CLAUDE.md).

| Deliverable | Status | Implementing work |
|-------------|--------|-------------------|
| Deep-recursion stack overflow | ✓ resolved | chunks 110, 111 |
| `between/3` in a failure-driven loop | ✓ re-verified | chunk 112 |
| `repeat/0` builtin | ✓ | chunk 113 |
| Same-query dynamic-predicate visibility (ISO logical update view) | ✓ via ADR-015 | chunks 114–128 |

ADR-015 by chunk:

| Chunk | Step |
|-------|------|
| 114 | A — dynamic-database generation counter |
| 115–117 | B — persistent code space (linker external symbols; persistent literal pools; query-transient overlay) |
| 119 | E — amortised program-buffer growth (capacity doubling) |
| 120 | C step 1 — CP frame gets a `ViewGen` slot |
| 121 | C step 2 — `enter_dynamic` and `check_visible` opcodes |
| 122 | C step 3 — compiler emits the trampoline and per-clause guards |
| 123 | C step 4, sub-1 — per-clause `died` tracking; `retract` patches in place |
| 124 | C step 4, sub-2a — fail-stub emission in the prefix |
| 125 | C step 4, sub-2b — last clause is `retry_me_else <fail-stub>` (patchable) |
| 126 | C step 4, sub-2c — chain state tracks every `<next>` operand |
| 127 | C step 4, sub-2d — incremental `assertz` (append a chunk; patch the tail's `<next>`) |
| 128 | C step 4, sub-2e — incremental `asserta` (new chunk as head; in-place demote old head to `retry_me_else` + 4 `nop`) |

Chunk 118 ("recompile-on-modify redirect") shipped as a stepping-stone
and was retired wholesale by chunk 128; chunk D was obviated en route.

---

## By the numbers

- **19 chunks** (110–128) since the Phase-7 tag.
- **2017 passing tests, 0 failing, 0 skipped** across 5 projects
  (+71 over the Phase-7 tag's 1946):
  - `Shumway.Tests.Core` — 417 (+4)
  - `Shumway.Tests.Interpreter` — 105 (+7)
  - `Shumway.Tests.Compiler` — 232 (+10)
  - `Shumway.Tests.IsoConformance` — 61
  - `Shumway.Tests.Embedding` — 1202 (+50)
- **One new ADR** ([ADR-015](../architecture/adr/015-persistent-code-space.md)).
- **Three new opcodes** — `nop` (0x56, 1 byte), `enter_dynamic` (0x66,
  1 byte), `check_visible` (0x67, 17 bytes — two 8-byte operands).
- **CP frame grew by one slot** (`ViewGen`); ADR-005 updated.

---

## What Phase 8 added

### Chunks 110–113 — the rest of the backlog

**Deep-recursion overflow (110–111).** The original diagnosis — *no
last-call optimisation* — was wrong. Chunk 110 demonstrated directly
that a plain tail-recursive predicate runs 100 000+ calls deep in
constant control stack. Chunk 111 found the actual culprit:
`Materializer.MaterializeAsCell` and `TermReader.Materialize` — the
WAM-cell ↔ `Term`-AST converters — recursed once per list element, so a
long list overflowed the C# stack. Both now walk the list spine
iteratively; tabling rounds with thousands of `clause/2`-visible facts
and tail-recursions that build long lists run to 2500+ / 50 000+
without overflow.

**`between/3` in a failure-driven loop (112).** Re-verified directly:
`between(1, 500000, _), ( Step -> fail ; ! )` and the classic
`( between(...), fail ; true )` idiom both run in constant stack, and
side effects from inside the loop persist. The "hung / crashed"
behaviour the backlog recorded was the chunk-111 materialisation
overflow surfacing inside the loop body, not `between/3` itself.

**`repeat/0` (113).** A builtin: succeeds, and pushes a self-re-arming
choice point so it re-succeeds on every backtrack — the ISO constant-stack
failure-loop generator.

### Chunks 114–128 — ADR-015 (persistent code space and live dynamic dispatch)

The big one. The Phase-7 backlog summarised it as: a query compiles to
a fixed bytecode program at setup, so a direct call to a dynamic
predicate sees only the query-setup snapshot — `assertz(d(1)), d(1)`
fails. The fix is *not* local; the program memory model and the
dynamic-dispatch protocol both have to change. ADR-015 is that change.

**A — generation counter (114).** A monotonically-increasing `DbGeneration`
on the engine, bumped on every modification to a dynamic predicate; the
foundation of the logical update view.

**B — persistent code space (115–117).** Linker grows an external-symbols
path so a freshly-compiled query references the long-lived program by
address; literal pools persist across queries; the query gets a
transient overlay rather than its own program. After B, code space is
a single append-only buffer shared by the engine for its lifetime.

**E — amortised growth (119).** The shared program buffer grows by
capacity doubling, so the cumulative cost of appending `N` clauses is
O(N) rather than O(N²). (Chunk D — "pins" to keep callers' addresses
stable across moves — was obviated by the append-only design.)

**C — live dynamic dispatch (120–128).** The protocol change. Every
dynamic predicate's bytecode is now:

```
<fail-stub>:   trust_me; call_builtin fail/0       (10 bytes, once per predicate)
trampoline:    enter_dynamic; execute <chain-head> (6 bytes; chain-head is patchable)
chain:         try_me_else <next1>, <arity>        (9 bytes — head clause)
               check_visible <born>, <died>        (17 bytes — visibility guard)
               <body>
<next1>:       retry_me_else <next2>               (5 bytes + 4 nops = 9 bytes for asserta-compatible last clause)
               check_visible ...
               <body>
               ...
<tail>:        retry_me_else <fail-stub>           (5 bytes — patchable to <newtail-next> on assertz)
               ...
```

- `enter_dynamic` samples the engine's `DbGeneration` into a new
  `CurrentViewGen` and writes it into the choice-point frame so a
  retry replays the same logical update view. Captured at *goal entry*,
  not query setup — which is what makes the in-query assert visible.
- `check_visible <born> <died>` skips the clause body via `fail` when
  the captured `ViewGen` is outside `[born, died)`. Sentinel
  `died = long.MaxValue` is "alive"; sentinel `born = 0` is "always
  visible".
- **Retract** patches the clause's `<died>` operand in place to the
  current generation (O(1)); existing in-flight goals with a captured
  earlier `ViewGen` still see the clause.
- **Assertz** compiles one clause, builds `retry_me_else<fail-stub>;
  check_visible<gen> <∞>; body`, appends it, and patches the tail
  clause's `<next>` operand to point at the new chunk (O(1)).
- **Asserta** is the trick. The new clause is compiled with
  `try_me_else<old-head>`, appended, and the *old head*'s 9-byte
  `try_me_else` is demoted in place to `retry_me_else<sibling>` (5
  bytes) + 4 × `nop` (4 bytes) — same 9-byte footprint, `<next>` operand
  at the same offset. The trampoline's `<chain-head>` then points at the
  new clause. O(1), no recompile.
- **Abolish** patches every chain entry's `<died>` slot to the current
  generation and clears the chain state.

The combined effect: every classical dynamic-database operation is O(1),
no bytecode is wasted on a retracted clause (it stays in the chain;
the guard just filters it out), and the same-query visibility rule —
`assertz(d(1)), d(1)` succeeds; an enumerating `findall/3` walking the
predicate sees clauses asserted *before* the call and skips those
retracted *before* the call — is the natural consequence of the
generation-stamped guard.

The path got there through one false start: chunk 118 shipped a
*recompile-on-modify redirect* — leaner than the original ADR-015 chunk-C
sketch, but it accumulated dead bytecode every modify cycle. The user
pushed back ("ningún motor de prolog serio hace eso"), and the design
moved to canonical born/died timestamps at the bytecode level. Chunk
128 retired chunk 118's machinery wholesale.

---

## Architecture notes

- **Three opcodes, one ADR, one CP slot.** The dispatch change is
  contained: `nop` for padding, `enter_dynamic` to sample the
  generation, `check_visible` as the per-clause guard. Every other
  opcode is unchanged.
- **The bytecode for a dynamic predicate is now stable for the lifetime
  of the engine.** Every modification is an in-place patch (a `<died>`
  slot, a `<next>` operand, or the trampoline's `<chain-head>`) plus —
  for assertz/asserta — an append. No clause's address ever changes;
  the chain only gets longer.
- **`retract` no longer needs to be re-satisfiable via recompilation.**
  It writes one `<died>` operand and the next-round visibility filter
  takes care of the rest.
- **The diagnostic discipline paid off.** Two of the four backlog items
  resolved by *re-verification*: deep recursion was a list-materialisation
  bug, not a missing LCO; `between/3` worked all along. The right
  question — "demonstrate the failure on the simplest possible program"
  — would have closed both before they ever reached a backlog.

---

## What does *not* change

- **The trail format, the cell layout, the atom GC, the module model.**
  No invariant moved.
- **Static-predicate dispatch.** Static predicates do not get a
  trampoline or visibility guards; their bytecode emission is byte-for-byte
  the same as before Phase 8.
- **Tier-1 IL.** Promotion still applies to static predicates only;
  dynamic dispatch is bytecode-only by design.
- **The query lifecycle.** Embedding callers see no API change.
  `Query` / `QueryAll` work as before; the transient-query overlay is
  internal.

---

## Deferred — to Phase 9

Phase 8's backlog is closed. Items recorded along the way that did
*not* land:

- **Last-call optimisation deeper than the materialiser fix.** The
  iterative materialisation in chunk 111 raised the practical recursion
  limit by ~25×; the *interpreter* dispatch already has LCO. A
  pre-emptive sweep for other "recurses once per node" patterns (term
  copying, deep unification trace points) would push the ceiling
  further but no failing program demands it yet.
- **Clause GC for retracted clauses.** A retracted clause's chain
  entry stays in the bytecode forever (the guard filters it out);
  compacting the chain when many entries have died would reclaim
  bytecode and shorten dispatch. No workload measured so far makes
  this matter, but a long-lived engine that retracts millions of
  clauses would want it.
- **Indexed dispatch under the visibility guards.** First-argument
  indexing remains active for dynamic predicates with the chunk-67/68
  machinery; the per-clause `check_visible` guards live on the
  non-indexed fallback only. Behaviour is correct (the indexed entries
  carry sentinel `born=0, died=MaxValue`), but a predicate that mixes
  indexing with frequent in-query modification gets the indexed path's
  query-setup snapshot, not the live view. Re-uniting the two paths
  is its own design problem.

---

## What Phase 8 buys you

A program that asserts and retracts inside its own query — the
overwhelmingly-common pattern in real Prolog code — now behaves the
way ISO says it should: each modification is visible to subsequent
goals in the same query, every operation is O(1), and the engine's
program memory does not grow without bound under a steady-state
assertz/retract workload. The tabling driver's `clause/2` workaround
becomes optional; deep-recursive programs that overflowed at the
materialiser run to depth 50 000+; `repeat/0` is there.

The engine is no longer in workaround territory.

Phase 9 picks up from a green 2017-test suite, a closed backlog, and
an ADR ledger that gained exactly one entry (ADR-015) but moved no
older one.
