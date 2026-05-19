# Phase 1 — Closure

**Status**: complete.

**Tagged**: `phase-1` (this commit).

Phase 1 set out to deliver a functional Prolog implementation for .NET
that is embeddable, fast enough to be useful for grammar processing and
embedded rules engines, and faithful to the invariants laid out in the
ADRs. This document records what landed, with pointers to the chunks
that implemented each piece, what was deferred to Phase 2, and the
shape of the codebase at closure.

---

## Deliverables checklist

Tracking the Phase 1 list from [`CLAUDE.md`](../CLAUDE.md).

| Deliverable | Status | Implementing chunks |
|-------------|--------|---------------------|
| Interpreter (Tier 0) | ✓ | 5a–5e, 6 |
| WAM compiler (Prolog → bytecode) | ✓ | 7a–7c, 8a–8e, 9 |
| Atom GC, trail, heap, stack, unification | ✓ | 1, 2, 3a–3c, plus engine evolution through 5e |
| PSTR (partial strings) for grammar processing | ✓ | 4, 6 |
| Builtins: subset oriented to grammar processing | ✓ | 10a–10g, 17, 20, 23, 26, 30, 32, 34, 40, 54, 56, 59 |
| Module system with public/local visibility | ✓ | 19, 60 |
| Embedding API | ✓ | 9 (MVP), 21, 24, 39, 51, 53 |
| Bundler CLI (bytecode bundles) | ✓ | 22, 38, 45, 55 |
| IL compiler (Tier 1) with `DynamicMethod` + Sigil | ✓ | 25, 27, 29, 41, 42, 44, 47–50, 52, 66 |
| `:- mode` directive accepted (parsed, stored) | ✓ | 28 |
| First-argument indexing for static predicates | ✓ | 18 |

Originally-deferred items (chunks 61–63) all closed in deep-dive
revisits during chunks 64–66:

| Deferred item | Investigation | Closure |
|---------------|---------------|---------|
| Env trim runtime gate | 61, 64 (gate scaffolding) | 215ab7e (64, revisited) |
| Warren argument scheduler | 62, 65 (rule refinement + investigation) | 8bc9afa (65, deep dive) |
| IL non-leaf callee support via meta-CP | 63 (investigation) | ff8655d (66, deep dive) |

---

## By the numbers

- **91 commits** from initial scaffold to closure.
- **14 ADRs** capturing the load-bearing decisions:
  ADR-001..014 under `docs/architecture/adr/`.
- **1434 passing tests, 0 failing, 0 skipped** across 5 projects:
  - `Shumway.Tests.Core` — 413
  - `Shumway.Tests.Interpreter` — 98
  - `Shumway.Tests.Compiler` — 207
  - `Shumway.Tests.IsoConformance` — 61
  - `Shumway.Tests.Embedding` — 655
- **~120 predicates exposed to user code**: 73 standard builtins in
  C#, 33 meta-builtins for embedding, plus a Prolog-level prelude
  (`member/2`, `clause/2`, `current_predicate/1`, `length/2`,
  `sub_atom/5`, `maplist/{2,3,4}`, `foldl/{4,5}`, `aggregate_all/3`).
- **WAM opcode set** at 60+ instructions: ground get/put, choice
  points, cut variants, structure / list / pstr opcodes, call /
  execute / call_builtin with env-trimming operands, allocate /
  deallocate, get_level, and the Meta opcode for debug info.

---

## Architecture invariants — held

The non-negotiable invariants in `CLAUDE.md` all hold at closure:

- Engines are single-threaded internally and thread-agile (no
  `[ThreadStatic]`). Global tables (atoms, functors, code cache)
  are thread-safe.
- The heap is `Cell[]` of 8-byte blittable values with the 4-bit-tag
  / 60-bit-payload layout from ADR-002. Managed object references
  live in per-engine auxiliary tables, addressed by integer id.
- Atoms use the three-tier system (Permanent / Transient /
  TransientWeak) from ADR-003. The custom atom GC runs at safe
  points, never in the hot dispatch loop. Atom ids are stable
  across tier promotion.
- Modules: each Prolog source file is one module. Predicates are
  local by default; `:- public` exports to a flat global namespace.
  Static predicates are immutable. Dynamic predicates are declared
  with `:- dynamic` and modifiable at runtime. Public uniqueness
  is enforced.
- Bytecode opcode 0x00 is Invalid; 0xFE is Meta (with DbgInfo
  sub-byte); 0xFF reserved for Extension. All others use fixed-
  size encoding per the per-opcode table.
- Two separate trails: binding trail (`int[]`) and extra trail
  (`struct[]`). The HB check skips trailing for "young" variables.
  Young-to-old binding rule is enforced.
- Tier 0 (interpreter) is always available; Tier 1 (IL) is opt-in
  per static predicate, with promotion triggered by invocation
  count thresholds. The swap from interpreted to compiled is
  atomic. Compiled IL takes `Engine` as a parameter (engine-
  agnostic), shared across engines via a hash-indexed code cache.

---

## Deferred to Phase 2+

The Phase 1 IL subset covers the dominant rule shapes that the WAM
compiler emits but stops short of the aggressive optimisations the
roadmap reserves for Phase 2 and beyond:

| Feature | Phase |
|---------|-------|
| Multi-argument indexing | 2 |
| Indexing for dynamic predicates (with assertz/retract invalidation) | 2 |
| Compiled bundles (.dll) via `PersistedAssemblyBuilder` | 2 |
| Bundler API for .NET integration (beyond the CLI) | 2 |
| Aggressive IL inlining of small static callees | 2 |
| PSTR lazy concatenation | 2 |
| Mode inference (using `:- mode` metadata) | 3 |
| Specialized IL code generation per mode | 3 |
| Profile-guided IL optimisation | 3 |
| JIT indexing | 3 |
| Attributed variables (`attvar`) | 4 |
| CLP(FD), CLP(R) | 4 |
| Native AOT support for Tier 1 | 4 |
| Tabling | 4 |

The chunks that flagged future optimisation surfaces during Phase 1
(env-trim further tightening, Warren reordering of nested-compound
emissions, multi-arg indexing) leave a clean handoff: the existing
correctness contracts pin the shapes Phase 2 must continue to honour.

---

## What Phase 1 buys you

The library at this tag can:

- Consult and run Prolog source from .NET (string, file, or
  bundled .bin) under `PrologEngine.ConsultString` /
  `ConsultFile`.
- Answer `Query` and `QueryAll` against a multi-module program,
  enumerating solutions through normal Prolog backtracking.
- Promote hot static predicates to Tier 1 IL at runtime, with the
  non-leaf meta-CP machinery handling the cross-product
  enumeration over multi-clause callees inside an IL body.
- Bundle a Prolog program into a single binary blob (bytecode +
  precompiled clause cache + module / atom / functor metadata)
  via the `shumway-bundler` CLI, then load it back in a fraction
  of the time of source consult.
- Round-trip Prolog terms ↔ .NET values through the embedding API,
  call .NET from Prolog via foreign predicates, and call Prolog
  from .NET via the query / engine pool.
- Run DCGs end-to-end (translation, `phrase/2,3`, push-back,
  control structures) for grammar processing.
- Throw and catch ISO error terms; render and parse terms with
  operator-aware printing; serialise with `write_canonical`.

The Phase-2 work picks up from a green test suite, a clean ADR
ledger, and a roadmap whose deferred items are written down rather
than scattered across `TODO`s.
