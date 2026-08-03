# Phase 2 — Closure

**Status**: complete.

**Tagged**: `phase-2` (this commit).

Phase 2 is the production-grade optimisation pass over the functional
core that Phase 1 delivered. It does not add user-visible language
features; every chunk makes an existing capability faster, cheaper, or
easier to integrate into a build pipeline. This document records what
landed, with pointers to the chunks that implemented each piece, what
was deferred to Phase 3, and the shape of the codebase at closure.

---

## Deliverables checklist

Tracking the Phase 2 list from [`CLAUDE.md`](../../CLAUDE.md).

| Deliverable | Status | Implementing chunk |
|-------------|--------|--------------------|
| Multi-argument indexing | ✓ | 67 |
| Indexing for dynamic predicates (with invalidation on modify) | ✓ | 68 |
| More aggressive IL inlining | ✓ | 69 |
| PSTR concatenation lazy (instead of eager) | ✓ | 70 |
| Compiled bundles (.dll) via `PersistedAssemblyBuilder` | ✓ | 71 (+ follow-up) |
| Bundler API for .NET integration (in addition to CLI) | ✓ | 72 |

---

## By the numbers

- **7 commits** from the Phase-1 tag to closure (chunks 67–72; chunk
  71 landed in two commits — an MVP plus a full-subset follow-up).
- **1508 passing tests, 0 failing, 0 skipped** across 5 projects
  (+74 over the Phase-1 tag's 1434):
  - `Shumway.Tests.Core` — 413
  - `Shumway.Tests.Interpreter` — 98
  - `Shumway.Tests.Compiler` — 214 (+7: multi-arg indexing bytecode pins)
  - `Shumway.Tests.IsoConformance` — 61
  - `Shumway.Tests.Embedding` — 722 (+67: chunks 67–72 end-to-end)
- **4 new WAM opcodes** (0x74–0x77): `switch_on_arg`,
  `switch_on_atom_arg`, `switch_on_integer_arg`,
  `switch_on_structure_arg` — the multi-arg indexing dispatch family.

---

## What Phase 2 added

### Multi-argument indexing (chunk 67)

ADR-007's Phase-1 model only discriminated on the first argument.
Chunk 67 adds the sequential fallback the ADR anticipated: when A1 is
a variable in the call, the dispatch chain consults A2, then A3, and
so on before landing in the full `try`/`retry`/`trust` chain. Four
new opcodes carry an explicit argument index; `PredicateCompiler`
builds one `ArgLevel` per indexable position and chains their
var-fallthroughs head-to-tail.

### Dynamic predicate indexing (chunk 68)

The WAM compiler always indexed dynamic clauses the same way as
static ones — but every query re-rewrote and re-compiled the whole
clause set, so a 1000-fact dynamic predicate paid an O(N) compile
cost per call. Chunk 68 adds a per-engine `DynamicPredicateCache`
that's populated lazily and invalidated on `assertz` / `asserta` /
`retract` / `abolish`. It also fixed two latent bugs: the module
rewriter mangled dynamic call sites that happened to be module
locals, and source-declared clauses for a `:- dynamic` predicate
were invisible to `retract/2`.

### IL inlining of leaf callees (chunk 69)

At each non-tail `Call` and tail-call `Execute` site the IL compiler
checks whether the callee is a small static leaf (single clause, head
match + proceed). If so, the callee's body opcodes are emitted
directly into the caller's IL stream, skipping the
`IlCallHelper.Run` / `IlExecuteHelper.Resolve` thunk — a managed
call, a Pc-set, and a bytecode-interpreter re-entry per call site.

### Lazy PSTR concatenation (chunk 70)

`string_concat/3` with two PSTR arguments now builds the result by
copying only the left side into a fresh buffer and chaining the tail
cell to the right side's existing header — no allocation for the
right operand's buffer. The chunk-6 PSTR design reserved the
`Pstr`-tagged tail slot for exactly this; chunk 70 makes the chain
get built and the read paths (`AsPstrString`, `GetPstrChainLength`)
follow it.

### Compiled IL bundles (chunk 71)

The bundler can emit a persisted .NET assembly — built with
`PersistedAssemblyBuilder` — holding the Tier-1 IL for every
IL-eligible predicate. `LoadBundle` loads the assembly and binds each
method as a `PredicateDelegate`, skipping the runtime Sigil emission
entirely. The follow-up commit extended the persistable subset from
single-clause leaves to the full IL surface: multi-clause try-me-else
chains, indexed-atom dispatch, and single-clause meta-CP shapes.
Self-referential IL choice points route through a static
`PredicateDelegate[]` field on the emitted type — a self-contained
indirection that doesn't collide across bundles.

### Bundler API (chunk 72)

ADR-009 sketched a typed `Bundler` / `BundleConfig` / `BundleResult`
surface for MSBuild / CI integration. Chunk 72 lands it: `Build` and
`BuildAsync` take a config and return a structured result with
diagnostics, the in-memory bundle, the serialised bytes, and a
summary report. The `shumway-bundler` CLI is now a thin wrapper
around `Bundler.Build`.

---

## Architecture notes

- **Bundle format** stays at version 1. There is no released runtime
  to maintain compatibility with, so the chunk-71 compiled-IL fields
  slotted into the existing layout without a version bump — the
  decision is to defer versioning until a release exists.
- **No new cell tags, no trail-format change, no threading-model
  change.** Phase 2 stayed inside the Phase-1 invariants; the only
  ADR-relevant change was the four new indexing opcodes (ADR-007's
  own Phase-2 roadmap anticipated them).
- The IL compiler's runtime (`DynamicMethod`) and build-time
  (`PersistedAssemblyBuilder`) paths now share every emit body —
  `EmitClauseBody`, `EmitSingleClauseMetaCpBody`,
  `EmitTryMeElseChainBody`, `EmitIndexedAtomBody` — parameterised on
  a `SelfDelegateEmitter` that abstracts how a predicate names its
  own delegate. The two paths emit byte-identical IL.

---

## Deferred to Phase 3+

Per the `CLAUDE.md` roadmap, the next phase is advanced
optimisation:

| Feature | Phase |
|---------|-------|
| Mode inference (using `:- mode` directives) | 3 |
| Specialized code generation per mode | 3 |
| Profile-guided optimisation (PGO) of IL code | 3 |
| JIT indexing | 3 |
| Attributed variables (`attvar`) | 4 |
| CLP(FD), CLP(R) | 4 |
| Native AOT support for Tier 1 | 4 |
| Tabling | 4 |

Two Phase-2 chunks left a documented, intentional limitation rather
than a loose end:

- **Lazy PSTR concat is single-step.** A concat over an
  already-lazy result re-copies the left side; a true rope
  representation that stays lazy across arbitrarily many concats is
  deferred. The chunk-70 commit and `pstr-design.md` both record
  this.
- **Multi-arg indexing is sequential fallback, not nested.** When A1
  is bound, the dispatch doesn't drill into A2 within the A1 bucket
  (nested / deep indexing). ADR-007 lists deep indexing as a
  separate, later refinement.

Both are deliberate cut points, not unfinished work — the behaviour
is correct, just not maximally optimised.

---

## What Phase 2 buys you

A program that ran correctly at the Phase-1 tag still runs correctly
at `phase-2`, and:

- Predicates that discriminate on a non-first argument now skip the
  irrelevant clauses instead of trying them all.
- Dynamic predicates with large fact sets stop paying an O(N)
  recompile on every query.
- Hot static leaf predicates called from IL bodies execute inline,
  with no thunk and no bytecode-interpreter re-entry.
- Grammar pipelines that concatenate PSTRs avoid allocating the
  right operand's buffer on each join.
- A deployed application can ship a bundle with pre-compiled IL,
  so the engine binds native-JIT-ready delegates at load time
  instead of running Sigil per predicate.
- Build systems can produce bundles programmatically through a
  typed API with structured diagnostics, no shelling out to the
  CLI.

Phase 3 picks up from a green 1508-test suite, an unchanged ADR
ledger, and the two intentional cut points above written down
rather than scattered.
