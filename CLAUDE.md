# Shumway — Project Guidance for Claude Code

**Shumway** is a Prolog compiler and interpreter for the .NET platform.

This file is read at the start of every Claude Code session in this repository. It contains non-negotiable invariants, architectural constraints, and conventions. **Read this fully before making any code changes.**

For broader context, see `docs/architecture/overview.md`. For specific design decisions, see the ADRs under `docs/architecture/adr/`. For detailed designs, see `docs/design/`.

---

## Project Goal

Shumway implements a **Prolog compiler and interpreter that runs on .NET**, intended for embedding in .NET applications. The primary use cases are:

- **Grammar processing** (DCGs, parsing of structured input).
- **Embedded rules engines** in .NET applications.
- **Symbolic reasoning** within larger .NET systems.

**Performance target**: comparable to or better than GNU Prolog in real-world scenarios. Specifically, Shumway is expected to **outperform** GNU Prolog in interop-heavy workloads (where the cost of crossing the C# ↔ Prolog boundary matters more than raw Prolog throughput).

---

## Technology Stack

| Component | Choice |
|-----------|--------|
| Runtime target | .NET 10+ (minimum .NET 9) |
| Language | C# 12+ |
| IL emission (runtime) | `System.Reflection.Emit.DynamicMethod` + Sigil (MS-PL license) |
| IL emission (build-time bundles) | `PersistedAssemblyBuilder` (official .NET API, no external deps) |
| Testing | xUnit |
| Benchmarking | BenchmarkDotNet |
| Source generation (struct↔term mapping) | Roslyn source generators |

**License compatibility**: all dependencies must be permissive (MIT, MS-PL, Apache, BSD). No GPL.

---

## Non-Negotiable Invariants

These are hard constraints. If a change requires breaking one of them, **stop and consult before proceeding**.

### Memory and concurrency

- **Engines are single-threaded internally.** No locks inside the engine state. The caller guarantees that only one thread accesses a given engine at a time.
- **Engines are thread-agile.** No `[ThreadStatic]` state. An engine can be used from different threads as long as access is serialized.
- **Global tables (atom table, functor table, code cache) are thread-safe.** Use `ConcurrentDictionary` or fine-grained locks. Multiple engines may share these tables.
- **The heap is a `Cell[]` of 8-byte blittable values.** Never put managed object references inside cells. References to managed objects (BigInteger, string, foreign object) live in per-engine auxiliary tables, accessed by integer id from the cell.
- **Cells are 8 bytes: 4 bits tag + 60 bits payload.** See ADR-002 for the exact layout. Do not change this layout.

### Atom management

- **Atoms have global integer ids.** Comparison of atoms is comparison of ints.
- **Three-tier atom table**: Permanent (eternal strong refs), Transient (strong refs in the table itself, cleaned by custom GC), TransientWeak (no strong refs, kept alive only by C# retention via `WeakReference`).
- **The custom atom GC runs at safe points**, not in the hot path. Hot path is just write the id to a cell.
- **Atom ids are stable for the lifetime of the atom.** Even after promotion between tiers, the id does not change.

### Modules and visibility

- **Each Prolog source file is one module.**
- **Predicates are local by default.** `:- public foo/N` exports them to a flat global namespace.
- **Static predicates are immutable.** Once compiled, they cannot be modified. `assertz`/`retract` on a static predicate is an error.
- **Dynamic predicates are declared explicitly** with `:- dynamic foo/N`. They can be modified at runtime.
- **Public predicates are globally unique.** Two modules cannot both declare `foo/N` as public.

### Bytecode

- **Bytecode opcode 0x00 is reserved as Invalid.** Encountering it during dispatch indicates corruption — fail loudly.
- **Opcode 0xFE is the Meta opcode** with a sub-byte for kind (currently only DbgInfo).
- **Opcode 0xFF is reserved as Extension** for a future escape mechanism. Do not use in v1.
- **All other opcodes (0x01–0xFD) follow fixed-size encoding** with operands as unaligned ints. Sizes are determined by a per-opcode table.

### Trail and backtracking

- **Two separate trails**: `BindingTrail` (int[] for variable bindings, the hot path) and `ExtraTrail` (struct[] for other reversible state).
- **HB check** prevents trailing of bindings to "young" variables (those created after the most recent choice point).
- **Young-to-old binding rule**: when unifying two unbound variables, always bind the younger (higher heap index) to the older.
- **`assertz`, `retract`, and modifications to global state are not trailed.** They are permanent.

### Compilation strategy

- **Tier 0**: WAM bytecode interpreter. Always available. Used for all dynamic predicates.
- **Tier 1**: IL-compiled code. For static predicates that are hot or pre-compiled in bundles.
- **Promotion is automatic** based on invocation count. Promotion happens in a background thread; the swap from interpreted to compiled is atomic.
- **Compiled IL is engine-agnostic.** It takes `Engine` as a parameter. The global code cache (indexed by bytecode hash) is shared across engines.

---

## Repository Layout

```
src/
├── Shumway.Core/           # Engine, heap, stack, trail, unification
├── Shumway.Compiler/       # Prolog → WAM compilation
├── Shumway.Interpreter/    # Tier 0: WAM bytecode interpreter
├── Shumway.Compiler.Il/    # Tier 1: WAM → IL compilation
├── Shumway.Bundler/        # CLI tool + library for bundling
├── Shumway.Embedding/      # Public API for .NET embedding
├── Shumway.Builtins/       # ISO-conformant builtin implementations
└── Shumway.Repl/           # Interactive top-level (REPL) console app

tests/
├── Shumway.Tests.Core/
├── Shumway.Tests.Compiler/
├── Shumway.Tests.IsoConformance/
└── Shumway.Tests.Benchmarks/

docs/
├── architecture/
│   ├── overview.md
│   └── adr/                # Architecture Decision Records
└── design/                 # Detailed subsystem designs
```

The NuGet package id is `Shumway` for the main embedding library. CLI tool is `shumway-bundler`.

---

## Build and Test Commands

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build

# Run all tests
dotnet test

# Run ISO conformance suite specifically
dotnet test tests/Shumway.Tests.IsoConformance/

# Run benchmarks
dotnet run -c Release --project tests/Shumway.Tests.Benchmarks/

# Build the bundler CLI tool
dotnet publish src/Shumway.Bundler/ -c Release

# Run the interactive top-level (REPL); any files listed are consulted at startup
dotnet run --project src/Shumway.Repl/ -- [file.pl ...]

# Publish the REPL as a self-contained Native AOT executable
# (see docs/native-aot.md — Windows needs the Visual C++ build tools)
dotnet publish src/Shumway.Repl/ -r win-x64 -c Release
```

---

## Coding Conventions

- **Naming**: standard .NET conventions (PascalCase types/methods, camelCase locals, `_camelCase` private fields).
- **Avoid LINQ in hot paths.** It allocates. Use plain loops with explicit indices in the interpreter dispatch, unification, trail unwind, etc.
- **Use `Span<T>` and `ref struct` where appropriate** for zero-allocation slices.
- **Prefer `struct` for small immutable types** (Cell, FunctorId, AtomId).
- **Document `unsafe` blocks** with a comment explaining why it's needed.
- **Avoid `async` in the interpreter core.** The interpreter runs synchronously within a thread. Async APIs at the embedding layer use safe-point cancellation, not async/await internally.
- **No `[ThreadStatic]` for engine state.** Engines must remain thread-agile.

---

## Testing Discipline

- **Every WAM instruction must have unit tests** covering its semantics.
- **Every builtin must have ISO conformance tests** when applicable.
- **The atom GC must have tests** covering: simple sweep, C# retention via WeakReference, promotion paths.
- **Backtracking and cut behavior** require dedicated test suites (cut interactions are easy to get wrong).
- **Benchmarks against GNU Prolog** are not unit tests, but should be kept current as part of CI.

---

## What Counts as a Major Decision

If any of the following come up, **stop and propose an ADR before implementing**:

- Adding a new cell tag.
- Changing the trail format.
- Adding a new top-level opcode.
- Changing the atom GC strategy.
- Changing the module resolution mechanism.
- Introducing a new external dependency.
- Changing the threading model.

These are areas where coherence across the codebase is critical and ad-hoc changes break invariants in non-obvious places.

---

## Phase Roadmap

Shumway is designed in phases. Be explicit about what phase a change targets.

**Phase 1 (v1) — Core functional Prolog with embedding** — ✅ **Complete** (tagged `phase-1`; closure summary in [`docs/phase-1-closure.md`](docs/phase-1-closure.md)).
- ✓ Interpreter (Tier 0).
- ✓ WAM compiler (Prolog → bytecode).
- ✓ Atom GC, trail, heap, stack, unification.
- ✓ PSTR (partial strings) for grammar processing.
- ✓ Builtins: subset oriented to grammar processing (~120 predicates delivered: 73 standard + 33 meta + 12 prelude).
- ✓ Module system with public/local visibility.
- ✓ Embedding API.
- ✓ Bundler CLI (bytecode bundles).
- ✓ IL compiler (Tier 1) with `DynamicMethod` + Sigil. Non-leaf callees handled via per-call-site meta-CP.
- ✓ `:- mode` directive accepted (parsed, stored as metadata; not used by compiler).
- ✓ First-argument indexing for static predicates.
- ✓ Per-call Warren argument scheduler (cycle-aware, replaces conservative head-var preservation).
- ✓ Per-call environment trimming (live-Y analysis on every Call / CallBuiltin).

**Phase 2 — Production-grade optimizations** — ✅ **Complete** (tagged `phase-2`; closure summary in [`docs/phase-2-closure.md`](docs/phase-2-closure.md)).
- ✓ Multi-argument indexing (sequential fallback; chunk 67).
- ✓ Indexing for dynamic predicates (cross-query cache, invalidation on modify; chunk 68).
- ✓ Compiled bundles (.dll) via `PersistedAssemblyBuilder` (chunk 71).
- ✓ Bundler API for .NET integration (in addition to CLI; chunk 72).
- ✓ More aggressive IL inlining (leaf-callee inlining; chunk 69).
- ✓ PSTR concatenation lazy (single-step; chunk 70).

**Phase 3 — Advanced optimizations** — ✅ **Complete** (tagged `phase-3`; closure summary in [`docs/phase-3-closure.md`](docs/phase-3-closure.md)).
- ✓ Mode inference (consumes `:- mode` directives; chunk 73).
- ✓ Specialized code generation per mode (det/semidet implicit cut; chunk 74).
- ✓ Profile-guided optimization (PGO) of IL code (two-phase instrumented→optimised; chunk 76).
- ✓ JIT indexing (deferred dynamic-predicate switch tables; chunk 75).

**Phase 4 — Extended features** — ✅ **Complete** (tagged `phase-4`; closure summary in [`docs/phase-4-closure.md`](docs/phase-4-closure.md)).
- ✓ Attributed variables (attvars): the ATTVAR cell tag, the `put_attr`/`get_attr`/`del_attr` family, `attvar/1`, the `attr_unify_hook` unification hook, and residual-goal projection (chunks 77–81).
- ✓ In-engine meta-call (added to this phase mid-stream): `findall/3`, `bagof/3`, `setof/3`, `forall/2`, `catch/3` and `call/1..7` now run in the live engine rather than an isolated sub-engine — side effects persist and there is no per-call sub-engine cost. `bagof`/`setof` do real witness grouping; `catch` and `call/N` are fully backtrackable per ISO (chunks 82–86).
- → CLP, Native AOT and tabling were moved to Phase 6.

**Phase 5 — Interactive top-level** — ✅ **Complete** (tagged `phase-5`; closure summary in [`docs/phase-5-closure.md`](docs/phase-5-closure.md)).
- ✓ `src/Shumway.Repl/` — a console-app project (the `shumway` executable) with a basic Prolog top-level: it consults files named on the command line, reads queries, prints each solution with `;` to search for the next, and exits on `halt.` or end of input. A thin client over the `PrologEngine` embedding API, for interactively exercising Shumway (chunk 87).
- ✓ Undefined-predicate calls raise a catchable ISO `existence_error(procedure, Name/Arity)` when reached, instead of an uncatchable link-time failure — a correctness fix the REPL surfaced.

**Phase 6 — Constraint logic programming over finite domains** — ✅ **Complete** (tagged `phase-6`; closure summary in [`docs/phase-6-closure.md`](docs/phase-6-closure.md)).
- ✓ Fixed `!` inside a runtime compound `call` goal (chunk 88). `call((a,!,b))` treated the cut as a no-op — *unsound*: backtracking re-ran clauses ISO would have cut away, re-executing their side effects. `DispatchCall` now threads the enclosing call's cut barrier through the `$call_*` helpers via `'$call'/2`, so a `!` in a runtime `,`/`;`/`->` goal commits exactly as far as the call — and no further.
- ✓ CLP(FD) core — opt-in library (`engine.UseClpfd()`, module `clpfd`) over sorted
  interval-list finite domains: `in`/`ins`, the six arithmetic constraints
  (`#=` `#\=` `#<` `#>` `#=<` `#>=`) over additive expressions, with bounds
  propagation, built on the `verify_attributes/4` hook (chunk 89). Chunk 89 also
  fixed a Phase-4 attvar bug: chunk-77's hookless "shared-module values must
  unify" merge rule ran even when a `verify_attributes/4` hook was defined,
  failing an attvar+attvar unification before the hook could run — fatal for a
  constraint library whose two variables carry deliberately different domains.
  `MergeAttributes` now defers a shared module's merge to the hook when one
  exists; the hookless rule still applies verbatim when no hook is defined.
- ✓ CLP(FD) multiplication and labeling (chunk 90): the `*` expression posts a
  bounds-consistent product propagator (exact two-way scaling when a factor is
  an integer constant; corner-product narrowing of the product otherwise);
  `label/1`, `labeling/2` (options `leftmost`/`ff` and `up`/`down`) and
  `indomain/1` enumerate domain values, running propagation between assignments.
- ✓ CLP(FD) `all_different`/`all_distinct` and reification (chunk 91):
  `all_different/1` posts pairwise disequality; reification ties a constraint
  to a 0/1 variable — `#<==>`, `#==>`, `#<==` and the boolean connectives
  `#/\`, `#\/`, `#\`, each comparison reified through an entailment-checking
  `$fd_reif` propagator.
- ✓ CLP(FD) remaining arithmetic and `sum/3` (chunk 92): the expression
  functions `min`, `max`, `abs` and truncating integer division `//` (with a
  positive integer divisor), and `sum(List, Rel, Total)` for the six relations.
- ✓ CLP(FD) refinements completing the library (chunk 93): `all_distinct/1`
  gains Hall-interval pruning (a single `$fd_alldiff` propagator — an interval
  holding exactly as many variables as values removes that range from the
  others, more variables than values fails); `scalar_product/4`; and `//`
  with a variable divisor (forward-bounds the quotient when the divisor's
  domain is wholly positive).
- → CLP(R), Native AOT and tabling moved to Phase 7.

**Phase 7 — Predicate documentation, CLP(R), AOT, tabling**
- ✓ Generated user-facing predicate documentation (chunks 94, 95). Predicate
  doc metadata lives *next to each definition* — a category, a moded call
  template and a summary passed to `BuiltinsRegistry.Register` for C#
  builtins, a structured `%! Template | Category | Summary` comment in the
  Prolog library sources (prelude, CLP(FD)). The template names every
  parameter with its mode (e.g. `between(+Low, +High, ?X)`).
  `PredicateDoc.Generate()` walks all three sources, groups by area, and
  emits `docs/predicates.md`. A unit test regenerates and fails if the
  committed file is stale; re-running the suite with the `SHUMWAY_REGEN_DOCS`
  environment variable set rewrites it. (The hand-written
  `docs/design/builtins-catalog.md` remains as a design-level catalogue for
  now.)
- ✓ Common library predicates, so typical Prolog programs run unchanged
  (chunks 96–98):
  - list utilities (chunk 96): `select/3`, `permutation/2`, `memberchk/2`,
    `subtract/3`, `intersection/3`, `union/3`, `delete/3`, `numlist/3`,
    `sum_list/2`, `max_list/2`, `min_list/2`, `max_member/2`, `min_member/2`,
    `include/3`, `exclude/3`, `partition/4`, `sort/4`, `predsort/3`,
    `pairs_keys_values/3`;
  - atom/number conversion (chunk 97): `atom_number/2`, `number_string/2`,
    `atomic_list_concat/2`, `atomic_list_concat/3`, `char_type/2`;
  - control, database & I/O (chunk 98): `once/1`, `ignore/1`, `tab/1`,
    `apply/2`, `findall/4`, `retractall/1`, `listing/0`, `listing/1`,
    `format_to_atom/3`.
  Most are pure Prolog in the prelude; `atom_number/2` and `number_string/2`
  are C# builtins (parse-or-fail). Each carries doc metadata, so all land in
  `docs/predicates.md` automatically. Two engine fixes fell out of this work:
  `==/2` and `\==/2` now handle floats, and `retract/1` is re-satisfiable
  (ISO requires it to enumerate matching clauses on backtracking).
- ✓ CLP(R) — constraints over the reals. Chunk 99 delivers the linear-equality
  core: the opt-in `clpr` library (`engine.UseClpr()`), the `{Constraint}`
  wrapper, and a Gaussian-elimination solver built on attributed variables
  with lazy expansion — each posted equality is normalised against the
  current solution, an inconsistent one fails, a free variable is pivoted
  out otherwise, and determined variables are bound. Chunk 100 adds the
  inequalities `<`, `>`, `=<`, `>=`: each is stored on the variables it
  mentions, and every post gathers the connected component of inequalities,
  re-expands it through the current equality solution, and tests
  satisfiability by Fourier–Motzkin elimination — so an unsatisfiable system
  (even one of purely multi-variable inequalities) fails on the spot.
  Chunk 101 adds disequality (`=\=` — it fails only when the inequalities
  entail its linear form is pinned to zero, decided with two more FM checks)
  and non-linear constraints (a product or quotient of non-constants is
  delayed and retried whenever a variable it mentions is determined, posted
  for real once it turns linear; a residual non-linear constraint that never
  resolves is left as a conditional answer). Chunk 102 adds constraint
  projection: `copy_term/3` collects the residual constraints on the copied
  term's variables, re-expressed over the copy as `{...}` goals (each shared
  constraint emitted once, by the variable that owns it). (CLP(R) and CLP(FD)
  cannot share an engine — both define a public `verify_attributes/4`.)
- ✓ Native AOT support (chunk 103). The Tier-0 bytecode interpreter is
  AOT-compatible; Tier-1 IL promotion is runtime code generation, so it is
  cleanly skipped under AOT — `IlPromotionStore` checks
  `RuntimeFeature.IsDynamicCodeSupported`, never constructs the IL compiler
  (its reflection-laden type initialiser is never reached), and the
  persisted-IL bundle path in `LoadBundle` falls back to the entry's
  bytecode. The REPL (`src/Shumway.Repl/`, `<PublishAot>true</PublishAot>`)
  is the publish target: `dotnet publish` produces a self-contained native
  `shumway` executable running the full engine, interpreter-only. See
  [`docs/native-aot.md`](docs/native-aot.md) (incl. the Windows native-link
  toolchain requirement).
- ✓ Tabling (chunk 104). `:- table p/N` memoises a predicate. At consult time
  its clauses are re-headed to `'$tabled$p'/N` and a driver clause routes
  every call through `'$table_call'`, which memoises answers and drives a
  *global naive fixpoint* — so left-recursive and cyclic definitions
  (transitive closure, mutual recursion) that loop under plain SLD resolution
  now terminate. The answer/subgoal table lives in the runtime dynamic store
  and is read with `clause/2`, since a direct call to a dynamic predicate
  sees only the query-setup snapshot — `clause/2` consults the live store, so
  a write made earlier in the same query is visible, and running the tabled
  goal via `call/1` keeps `findall` in-engine so its `assertz`es persist.
  Documented limitations: definite programs only (no tabled negation);
  answers assumed ground; the table persists per engine, so retracting a
  clause after a tabled query can leave a stale answer.
  Chunk 105 made a fixpoint pass O(n log n) rather than O(n²) by keeping
  each subgoal's answers as one sorted, duplicate-free list. Chunk 106 goes
  to genuine *semi-naive* evaluation: the consult-time transform splits each
  tabled clause into base clauses (`'$tbase$p'`) and recursive clauses
  (`'$trec$p'`), and a recursive clause's single tabled body literal becomes
  a `'$tbl_consume'` call that yields only the producer's *delta* — the
  answers it gained in the previous round — so a round re-derives only what
  is newly possible, not the whole relation. A clause with two-plus tabled
  literals, or a tabled call nested in a control construct, is re-run every
  round undifferentiated (correct, just not accelerated). The per-answer
  duplicate test is the engine-backed `'$tbl_seen'` set, O(1) — the dynamic
  store copies on every assert, so a list-per-subgoal answer table would be
  O(n) per round and mask the win; answers, deltas and subgoals are instead
  individual asserted facts. Measured ~3.5× faster on a 500-deep transitive
  closure, widening with depth. Remaining limitation: the fixpoint loop
  recurses once per round and Shumway has no last-call optimisation, so a
  fixpoint deeper than ~1000 rounds (a very long recursive chain) overflows
  the control stack.

**Phase 8 — Engine robustness (backlog)**

Problems surfaced while building Phases 6–7, recorded here for a dedicated
pass rather than patched ad hoc. The first four are interrelated — all about
deep loops overflowing the control stack.

- **Last-call optimisation (LCO).** The interpreter does not reclaim a
  clause's environment on a last call, so a deep tail-recursive predicate
  grows the control stack one frame per call. A left-recursive predicate is
  expected to overflow (that is what tabling is for), but a *properly
  tail-recursive* loop overflows too — surfaced by tabling's semi-naive
  fixpoint loop (`'$table_seminaive'`), which recurses once per round and
  overflows past ~1000 rounds. Restructuring the loop as a plain syntactic
  last call did not help, confirming there is no LCO. This is the root
  engine fix and the highest-value item.
- **`between/3` in a failure-driven loop.** Replacing the fixpoint loop with
  `between(1, BigN, _), ( Step -> fail ; ! )` — the textbook constant-stack
  loop idiom — hung / crashed. `between/3` works for ordinary enumeration;
  its behaviour *driving a failure-driven loop* was never diagnosed.
  Investigate (suspected: it is not a constant-stack choice-point generator,
  or backtracking through it interacts badly with the tabling asserts).
- **`repeat`.** There is no `repeat` builtin — the ISO constant-stack
  choice-point generator (`repeat/0`; a bounded `repeat/1` is also useful).
  A correct `repeat` lets the tabling fixpoint loop — and any other deep
  loop — be written failure-driven and constant-stack.
- **Tabling fixpoint depth.** Consequence of the three above: the semi-naive
  fixpoint overflows on a fixpoint deeper than ~1000 rounds. Resolved for
  free once LCO lands, or once the loop can be made failure-driven.
- **Same-query `assertz` visibility (ISO logical update view).** A query is
  compiled to a fixed program at setup, so a direct call to a dynamic
  predicate sees only that snapshot — `assertz(d(1)), d(X)` does not see
  `d(1)` within the same query, though ISO's logical update view says a
  goal sees the database as of when the goal began. `clause/2` *does*
  consult the live store — that is the workaround the chunk-104 tabling
  driver relies on. A real fix makes a compiled call to a dynamic predicate
  re-resolve against the live `_dynamicClauses` store.

---

## Communication and Iteration

When proposing changes:

1. **Read the relevant ADR(s) first.** If your change conflicts with an ADR, mention it explicitly.
2. **Reference invariants by name** when explaining trade-offs.
3. **Show what tests will validate the change.**
4. **Distinguish between "fix" (correct existing implementation), "extension" (new capability within current design), and "redesign" (changes an ADR).**

---

## Quick Reference: Key Decisions

| Decision | See |
|----------|-----|
| Cell layout (8 bytes, tag + payload) | ADR-002 |
| Atom three-tier system | ADR-003 |
| Two separate trails | ADR-004 |
| Stack layout | ADR-005 |
| Bytecode encoding | ADR-006 |
| First-argument indexing v1 | ADR-007 |
| Module visibility model | ADR-008 |
| Bundler design | ADR-009 |
| Embedding API | ADR-010 |
| IL compiler architecture | ADR-011 |
| Mode inference roadmap | ADR-012 |
| BigInt literal opcodes | ADR-013 |
| IL choice points (multi-clause ABI) | ADR-014 |
| PSTR design | docs/design/pstr-design.md |
| Debug info | docs/design/debug-info.md |
| Builtins catalog | docs/design/builtins-catalog.md |
| WAM instruction set | docs/design/wam-instruction-set.md |
