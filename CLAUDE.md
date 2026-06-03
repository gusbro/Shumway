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
- **Dynamic predicates may be declared explicitly** with `:- dynamic foo/N`, or — when the `implicit_dynamic` prolog_flag is `true` (the default since Phase 19+) — auto-promoted on first `assertz`/`asserta` of an undefined predicate. Matches SWI / SICStus / GNU default behaviour. Setting `:- set_prolog_flag(implicit_dynamic, false).` reverts to ISO-strict mode where assertz on an undeclared predicate raises `permission_error(modify, static_procedure, _)`. Auto-promotion never applies to predicates with existing static clauses or to registered builtins.
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

**Phase 7 — Predicate documentation, CLP(R), AOT, tabling** — ✅ **Complete** (tagged `phase-7`; closure summary in [`docs/phase-7-closure.md`](docs/phase-7-closure.md)).
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
  Chunk 108 handles **non-ground answers**: a tabled answer may contain
  unbound variables, and the duplicate test (`'$tbl_seen'`) canonicalises
  variables by first-occurrence index, so variant answers (`p(X)` and
  `p(Y)`) deduplicate to one — variant tabling. Without this a non-ground
  answer re-derived with a fresh variable each round never deduplicates and
  the fixpoint loops forever.
  Chunk 107 adds **table invalidation** — `abolish_all_tables/0` and
  `abolish_table/1` (by `Name/Arity`) discard cached answers so a later
  query recomputes against the current program — and **tabled negation**:
  the transform rewrites `\+ G` / `not(G)` over a tabled goal to
  `'$tbl_negate'(G)`.
  Chunk 109 makes that negation **well-founded**. A program with tabled
  negation is evaluated by the *alternating fixpoint*: `W(K)` is one tabled
  least-fixpoint in which `\+ a` succeeds iff `a ∉ K`; iterating `W` from
  the empty set gives an increasing chain (limit `U`, the well-founded
  *true* atoms) and a decreasing chain (limit `O`), with `U ⊆ O` and
  `O \ U` the *undefined* atoms. So a negative cycle now **terminates** —
  `p :- \+ p` makes `p` undefined; the win/lose/draw game gives draws as
  undefined — where plain SLD would loop. A tabled query yields the true
  answers; `well_founded(Goal, Status)` reports `true` / `false` /
  `undefined`. (Negated atoms are assumed ground.) This subsumes the
  chunk-107 stratified mechanism — for a stratified program the
  alternation converges to the two-valued model.
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

**Phase 8 — Engine robustness** — ✅ **Complete** (tagged `phase-8`; closure summary in [`docs/phase-8-closure.md`](docs/phase-8-closure.md)).

Problems surfaced while building Phases 6–7, recorded here for a dedicated
pass rather than patched ad hoc.

- ✅ **Deep-recursion stack overflow — resolved (chunks 110–111).** Chunk
  110 established the engine *does* have last-call optimisation — a plain
  tail-recursive predicate runs 100 000+ calls deep in constant control
  stack (`Chunk110Tests`); the original "no LCO" diagnosis was wrong.
  Chunk 111 found and fixed the actual cause: `Materializer.MaterializeAsCell`
  and `TermReader.Materialize` — the WAM-cell ↔ `Term`-AST converters —
  recursed once per list element, so a long list (a tabled predicate's
  thousands of `clause/2`-visible facts, or a tail recursion accumulating a
  list) overflowed the C# stack. Both now walk the list spine iteratively;
  the tabling fixpoint and deep list-building recursions that overflowed at
  ~1500–2000 run to 2500+ / 50 000+ (`Chunk111Tests`).
- ✅ **`between/3` in a failure-driven loop — resolved (chunk 112).**
  Re-verified directly: `between(1, 500000, _), ( Step -> fail ; ! )` and
  the `( between(...), fail ; true )` idiom run in constant stack, and
  side effects from inside the loop persist (`Chunk112Tests`). The original
  "hung / crashed" was the chunk-111 list-materialisation overflow inside
  the loop body (the tabling round's `clause/2`), not `between/3`.
- ✅ **`repeat/0` — done (chunk 113).** A builtin: succeeds, and pushes a
  self-re-arming choice point so it re-succeeds on every backtrack — the ISO
  constant-stack failure-loop generator.
- ✅ **Same-query dynamic-predicate visibility (ISO logical update view) —
  resolved (chunks 114–128, [ADR-015](docs/architecture/adr/015-persistent-code-space.md)).**
  A direct call to a dynamic predicate (and a `findall/3` over one) now
  sees a change made earlier in the same query — `assertz(d(1)), d(1)`
  succeeds. The canonical engine-style dispatch:
    - **A (114)** generation counter on `PrologEngine`.
    - **B (115–117)** persistent code space — static linked once, queries
      link only their transient region against it.
    - **C-bytecode-level (120–128)** dynamic dispatch matching what
      mature Prolog engines do. CP frame gains a `ViewGen` slot saved /
      restored alongside the rest of state. Two new opcodes
      (`enter_dynamic` samples `DbGeneration` into `CurrentViewGen`;
      `check_visible <born:long> <died:long>` filters per clause). Each
      dynamic predicate compiles to a 6-byte trampoline
      (`enter_dynamic; execute <chain-head>`) followed by a
      `try_me_else` / `retry_me_else` chain with the last clause
      pointing at a `call_builtin fail/0` fail-stub. `assertz` and
      `asserta` are both O(clause): compile one clause via
      `ClauseCompiler`, append a chunk, patch the appropriate operand
      in place. Asserta demotes the previous head's `try_me_else`
      (9 bytes) to `retry_me_else <same-next>` + 4 × `Nop` (also
      9 bytes — same address operand at the same offset). `retract`
      and `abolish` patch the `died` slot in place.
    - **D** stays dropped — no generation pins to release.
    - **E (119)** capacity-doubling `AppendCode`; the incremental
      assertz/asserta (127–128) made the residual O(n²) growth a
      non-issue too. Per-modification growth is O(clause size).
  The chunk-118 stepping-stone (recompile-on-modify redirect) is gone;
  what runs is the canonical design. Cut works as a normal `cut`
  opcode because clauses stay compiled. The logical update view holds
  through born/died: an in-progress call's captured view-gen sits
  below any mid-query assertz/retract, so it sees the database as of
  when its goal began. ADR-015 is complete.

**Phase 9 — ISO conformance & error system** — ✅ **Complete** (tagged `phase-9`; closure summary in [`docs/phase-9-closure.md`](docs/phase-9-closure.md)).

Brought Shumway's error reporting in line with what other ISO Prologs
emit, and widened `Shumway.Tests.IsoConformance` from a 61-test
sampler (5 files) to a 268-test one-file-per-§8-chapter suite (16
files). 24 new ISO-named builtins implemented along the way.

- ✓ **Stage A — error-system completion** (chunks 129–131e). The four
  missing `IsoError` kinds (`representation_error`, `syntax_error`,
  `resource_error`, `system_error/0,1`); the offending builtin's
  `Name/Arity` stamped onto `PrologRuntimeException` at every
  `CallBuiltin` dispatch site so the `error/2` Context slot survives
  sub-engine teardown; ~60 `InvalidOperationException` sites across
  7 source files converted to catchable `PrologRuntimeException` with
  ISO precedence honoured (`instantiation_error` before `type_error`
  before `domain_error` before `existence_error` before
  `permission_error`).
- ✓ **Stage B — conformance widening** (chunks 132–143). One chunk
  per ISO §8 chapter. Every chapter has its own conformance file. The
  discipline that emerged: when a conformance test surfaces a missing
  predicate, *implement it* rather than just record the gap. Real
  predicates added: `unify_with_occurs_check/2` (§8.2.2),
  `current_op/3` (§8.17.3), all of §8.11 stream control
  (`current_input/1`, `current_output/1`, `set_input/1`,
  `set_output/1`, `open/4`, `flush_output/0,1`, `at_end_of_stream/0,1`,
  `current_stream/3`, `stream_property/2`, `set_stream_position/2`),
  all of §8.12 character I/O (`get_char/1`, `peek_char/1`,
  `put_char/1,2`, `get_code/1,2`, `peek_code/1,2`, `put_code/1,2`),
  all of §8.13 byte I/O (`get_byte/1,2`, `peek_byte/1,2`,
  `put_byte/1,2`) including binary streams, the missing §8.14 term
  I/O (`read/1,2`, `writeq/1,2`, `write_canonical/2`,
  `write_term/3`).
- ✓ **Stream subsystem** (chunk 140 a/b/c/d). New `StreamHandle` and
  `StreamRegistry` types in `Shumway.Core` give every engine a real
  per-instance registry: handles carry mode / filename / alias /
  binary-vs-text kind, the registry owns the alias map and the
  current-input / current-output cursors. `==/2` extended to handle
  Foreign, BigInt, String and PSTR cells (was throwing
  `NotSupportedException`).
- → `char_conversion/2` / `current_char_conversion/2` (§8.14.9-10),
  the cyclic-term materialiser overflow, and the parser's `\+ (a, b)`
  ambiguity are recorded inline next to the tests that surface them;
  queued for Phase 10+.

**Phase 10 — Engine robustness leftovers** — ✅ **Complete** (tagged `phase-10`; closure summary in [`docs/phase-10-closure.md`](docs/phase-10-closure.md)).

User-facing fixes first (chunks 144–149: richer error payload,
cyclic-term safe walking, cut-vs-catch trail snapshots, parser
adjacency rule), then internals — clause GC (150), persistent
dynamic code space (151a–b), character conversion (152), and the
chunk-155 series that delivers true in-place `assertz` / `asserta`
/ `retract` for JIT-promoted hot dynamic predicates with extensible
indexed dispatch:

- ✓ **155a** — `CompileIndexedDynamic` compilation layout: bucket
  chains use `try_me_else` / `retry_me_else` with patchable
  `<next>` operands; bodies live once and are reached via
  `execute`. The structural prerequisite for in-place extensibility.
- ✓ **155b** — in-place same-key `assertz`: walk bucket + var
  chains, append new chunks, patch tails.
- ✓ **155c** — in-place new-bucket-key `assertz`: create a fresh
  bucket chain (with merged var-arg clauses), extend the sub-switch
  table, mirror into `_dynamicLink` for cross-query persistence.
  `Engine.SwitchTables` became a mutable list.
- ✓ **155d** — in-place `retract`: walk every chain, patch died
  slot of every chain entry whose `execute` targets the retired
  body. Counts only alive entries when mapping clause index to
  body address.
- ✓ **155e** — in-place var-arg-at-0 `assertz`: extend every chain
  (var + list + every bucket reachable via sub-switches).
- ✓ **155f** — in-place `asserta`: demote each chain's head in
  place (`try_me_else` 9 bytes → `retry_me_else` + 4 nops, same
  footprint), append new head, redirect every pointer slot
  (switch_on_term operands, sub-switch table values, defaults)
  from old to new. `ChainEntryHeaderSize` helper distinguishes a
  9-byte demoted-head slot from a native 5-byte non-head.
- ✓ **155g** — multi-arg dynamic indexed predicates pin
  correctness via the chunk-154 rebuild-on-mutate fallback.
- → True in-place multi-arg extensible-indexed dispatch deferred
  to Phase 11.

**Phase 11 — Multi-arg in-place indexing + persistent compaction** — ✅ **Complete** (tagged `phase-11`; closure summary in [`docs/phase-11-closure.md`](docs/phase-11-closure.md)).

Two chunks closing Phase 10's deferred items:

- ✓ **156** — multi-arg in-place extensible-indexed dispatch.
  `CompileIndexedDynamic` generalised to take `perArgInfo` +
  `indexableArgs` and emit nested switch dispatch
  (`switch_on_term` for arg 0, `switch_on_arg` for arg 1+) with
  extensible chains at every level. The runtime chain-modification
  helpers (assertz / asserta / retract / var-arg / new-key) walk
  multi-level structure via a single recursive enumerator
  `EnumerateChainHeadsRecursive`. `IsExtensibleIndexedLayout`
  follows the `switch_on_arg` cascade to recognise multi-arg
  layouts; `FindFinalVarChainHead` resolves through the cascade
  to the actual final chain head.

- ✓ **157** — `compact_dynamic_buffer/0` builtin. Routes through
  the existing `InvalidatePersistent` (chunk 151b) to reclaim
  memory consumed by in-place chain entries and clause bodies
  that became unreachable after a long run of mutations.
  Trade-off: one re-link of the dynamic region on the next
  query; subsequent queries start fresh at append-only growth.
  Recommended use is periodic, between top-level queries.

**Phase 14 — Compiler / linker UX polish + `--exe`** — ✅ **Complete** (tagged `phase-14`; closure summary in [`docs/phase-14-closure.md`](docs/phase-14-closure.md)).

Polish around the Phase 13 separate-compilation workflow plus a
real `--exe` path. Eight chunks:

- ✓ **168** — `shumway-compile` multi-file input + per-file
  "compiling X -> Y" progress; `-o` becomes the output
  directory under multi-input.
- ✓ **169** — `--debug` / `--release` flags (default release).
  Mode is persisted via a new build-mode byte in the .shmo V2
  format. V1 still readable.
- ✓ **170** — `--verbose` lists every `:- public` and
  `:- dynamic` indicator per compiled file.
- ✓ **171** — Parser error recovery. C-compiler-style
  `ShmoCompiler.TryCompileSource` accumulates parse +
  directive errors (up to 100), resyncing to the next clause
  terminator between attempts. CLI prints
  `file:line:col: error: msg` for each.
- ✓ **172** — `shumway-link --strip` removes embedded source
  from each bundle entry. Bytecode preserved. (Chunk-172's
  `stripped_bundle` warning is gone since chunks 178/179
  delivered the source-less `LoadEntryFromBytecode` path.
  Release `.shmo` is always source-stripped, so every linked
  bundle takes that path. Chunk 209 made it actually correct
  for real programs — see the chunk-209 note under Phase 19+.)
- ✓ **173** — `shumway-link --map <path>` writes a C-toolchain
  -style audit file: per-module sizes, public/dynamic
  predicate lists, reached / dropped modules, totals.
- ✓ **174** — `shumway-link --exe <path>` produces a single-
  file native executable for the current platform. Embeds the
  bundle as a manifest resource; runs the `--goal` at startup;
  exits 0/1/2. Implementation shells out to `dotnet publish`
  with `PublishSingleFile=true`. `--self-contained` switches
  from framework-dependent (~5-10 MB) to fully standalone
  (~70 MB). `--goal` accepts both `main` and `main.`; the head
  pred becomes an implicit entry-point.
- ✓ **175** — Closure summary + tag.

Bonus parser fixes during the phase (surfaced compiling
Blint.pl): the `prefix_op/N` predicate-indicator ambiguity
disambiguates to the indicator when followed by `/ <integer>`;
`:- dynamic a/0, b/1, c/2.` (GNU comma-separated form) is now
accepted alongside the single-indicator and list forms.

**Phase 13 — Separate compilation + linker + user docs** — ✅ **Complete** (tagged `phase-13`; closure summary in [`docs/phase-13-closure.md`](docs/phase-13-closure.md)).

Introduces the `.pl → .shmo → .shum` separate-compilation
workflow and the user-facing documentation that ties the whole
tool family together. Eight chunks:

- ✓ **160** — `.shmo` V1 file format. Magic `SHMO` + `uint32`
  version + module name + source + WAM bytecode + defined-set
  with `PredicateVisibility` (Local / Public / Dynamic) +
  `ensure_linked` set + per-predicate call graph + qualified
  refs. `ShmoFormat` / `ShmoObject` / `ShmoReader` / `ShmoWriter`.
- ✓ **161** — `shumway-compile` CLI (`.pl → .shmo`). New
  `Shumway.Compile` project. `ShmoCompiler` reads
  `:- module/1` / `:- public/1` / `:- dynamic/1`, applies
  `DcgTransform`, walks each clause body extracting call edges
  (descending through `,`/`;`/`->`/`*->`/`\+`/`not`/`call/1`,
  emitting `Module:Goal` as `QualifiedPredicateRef`, skipping
  cuts).
- ✓ **162** — `:- ensure_linked/1` directive. GNU-Prolog-style
  reachability hint for predicates invoked only via runtime
  meta-call. Recorded into `ShmoObject.EnsureLinked`; the linker
  treats every indicator as an additional root. Added
  `ensure_linked` as an `fx 1150` prefix operator alongside
  `dynamic`/`public`.
- ✓ **163** — `ShmoLinker`. Takes a set of `ShmoObject`s plus
  entry points. Builds the global namespace (with
  `duplicate_public` collision detection), compiles the prelude
  on the fly and snapshots `BuiltinsRegistry` + `MetaBuiltins` as
  the always-available filter, walks reachability from
  entry-points + `ensure_linked` + qualified refs resolving each
  edge in order (module-local / global public / global dynamic /
  builtin / prelude), emits `missing_predicate` diagnostics
  (error, or warning under `AllowUndefined`), drops unreachable
  modules with an `unreachable_module` warning, and serialises a
  `Bundle`.
- ✓ **164** — `shumway-link` CLI. New `Shumway.Link` project.
  `--entry pred/N` is repeatable AND accepts a comma-separated
  list per flag; both combine. `--allow-undefined` downgrades
  missing-predicate errors to warnings.
- ✓ **165** — Linker async + source/file conveniences mirroring
  the chunk-72 Bundler shape: `LinkAsync(LinkConfig,
  CancellationToken)`, `LinkFromFiles(paths, entries, ...)`,
  `LinkFromSources(...)`.
- ✓ **166** — `docs/user-guide.md`. Comprehensive walkthrough:
  what ships in each project, building from source, running the
  REPL, embedding the engine (PrologEngine / Solution / Term /
  CLP opt-in / LoadBundle), the full separate-compilation flow
  with diagrams, module directives reference, a worked
  grandparent example including a deliberate failure case, and
  a pointer to `native-aot.md`.
- ✓ **167** — Closure summary + tag.

**Phase 12 — Auto-compaction + Tier-1 IL revisit** — ✅ **Complete** (tagged `phase-12`; closure summary in [`docs/phase-12-closure.md`](docs/phase-12-closure.md)).

Two chunks closing out the auto-compaction and IL-promotion
questions from Phase 11's deferred list:

- ✓ **158** — auto-compaction watermark + `compact_dynamic_buffer/1`.
  `PrologEngine._persistentMutationsSinceCompact` counter bumped
  by every dynamic-store mutation through
  `InvalidateDynamicCache`. `SetupQueryFromTerm` auto-invalidates
  the persistent buffer once the counter crosses
  `PrologEngine.CompactWatermark` (default 1000); the rebuild in
  the same setup picks up the trim. `compact_dynamic_buffer/1`
  is the per-predicate API surface — currently delegates to the
  full rebuild as a forward-compatibility hint.

- ✓ **159** — explicit Tier-1 IL exclusion for dynamic predicates.
  `IlPromotionStore.IsExcludedByLayout` marks predicates whose
  bytecode opens with `enter_dynamic` as unpromotable on the
  first invocation. Formalises the architectural invariant —
  chunks-155+/156 mutation-driven dispatch must stay on Tier 0
  because a cached IL delegate wouldn't observe mid-life
  `retract` / `assertz` — and avoids redundant `TryDescribe*`
  attempts that were already rejecting the shape.

**Phase 24 — Arity-Prolog compatibility primitives** — ✅ **Complete** (tagged `phase-24`; closure summary in [`docs/phase-24-closure.md`](docs/phase-24-closure.md)).

Ten chunks (263–274 with 269/270 dropped) bringing Arity-Prolog source-compat: snips `[! G !]`, save_state/restore_state, `:- visible` directive alias, recorded database, Edinburgh-style I/O, file_list, file-system ops, pseudo-random, expand_term/2, string_term/string_termq/string_search, and a few smaller pieces. Selection driven by Arity's actual predicate listing (`C:\Arity\doc\ARITY.HLP.txt`), not generic Prolog folklore.

- **Chunk 263 — snips `[! G !]`**. Parser-level desugar to `once((G))`. Internal backtracking is permitted; successful exit prunes the snip's choice points. Cut inside a snip scoped to the snip boundary via `once/1`'s call barrier.
- **Chunk 264 — `save_state/1,2`, `restore_state/1`**. Snapshots engine state (consult history + dynamic clauses) to a V6 .shum bundle (new snapshot trailer). Full mode resets + replays; dynamic-only mode merges. New `BundleSnapshot` type + public `PrologEngine.SaveState/RestoreState[FromBytes]`.
- **Chunk 265 — `:- visible foo/N`**. Same semantics as `:- dynamic`. Added to OperatorTable as `fx 1150`; `TryReadDynamicDirective` accepts both functor names; ShmoCompiler mirrors.
- **Chunk 266 — recorded database**. `RecordedDatabase` class — keys are arbitrary terms, stable monotonic integer refs that are never reused. Full builtin family: `recorda/3`, `recordz/3`, `recorded/3` (backtrackable), `erase/1`, `eraseall/1`, `instance/2`, `key_count/2`, `keys/1`, `ref/1`, `replace/2`, `nref/2`, `pref/2`, `record_after/3`, `record_before/3`.
- **Chunk 267 — Edinburgh-style I/O**. `see/1`, `seen/0`, `seeing/1`, `tell/1`, `told/0`, `telling/1`, `get/1,2`, `get0/1,2`, `put/1,2`, `skip/1,2`, `tab/2`. Layer over the chunk-140 stream registry.
- **Chunk 268 — `string_term/2`, `string_termq/2`, `string_search/3`**. write- and writeq-style bidirectional atom↔term conversion (Arity uses "string" to mean atom); backtrackable substring search with overlapping matches. (Broader chunk 268 scoping discarded `ifthen/2`/`ifthenelse/3` from the prelude because Blint and other Arity programs redefine `ifthen/2` as a user predicate — providing one in the prelude collides via `ValidatePublicUniqueness`.)
- **Chunk 271 — file-system ops**. `mkdir/1`, `rmdir/1`, `delete/1`, `rename/2`, `directory/6` (backtrackable enumeration with Arity-style mode bitfield), `exists_file/1`, `exists_directory/1`, `chdir/1` (1-arg prelude alias of `working_directory/2`).
- **Chunk 272 — pseudo-random**. `randomize(+Seed)`, `random(-X)` (float [0,1)), `random_between(+L,+H,-X)` (int [L,H] inclusive, SWI semantics). Per-engine `System.Random`. The `is/2` arithmetic-function form `X is random(N)` not included (cross-project hook deferred).
- **Chunk 273 — `expand_term/2`**. Exposes the same `DcgTransform` consult applies internally. DCG rules expand; other terms pass through.
- **Chunk 274 — `file_list/1,2`**. Plain-text database dump, re-consultable. `file_list(+File, +Spec)` accepts a `Name/Arity` or a list of them. Emits `:- dynamic` directives for dynamic predicates so re-consult preserves the declaration.

~70 new tests, full suite at phase close ~3066 tests with 0 failures. Bundle format bumped V5→V6 (backward-compatible).

**Phase 23 — REPL UX polish, listing, residual constraint display, warnings cleanup** — ✅ **Complete** (tagged `phase-23`; closure summary in [`docs/phase-23-closure.md`](docs/phase-23-closure.md)).

Fourteen chunks (249–262). Originally scoped as engine robustness focused on a `retract/1` Blint bug recorded in memory; verification showed the bug had been fixed incidentally during chunks 235-248 (memory updated). With the obvious correctness target gone, pivoted to REPL UX — and expanded as each landed chunk surfaced something else worth fixing while the surface was still fresh.

REPL editing (249–253): custom Console.ReadKey line editor with cursor movement, Up/Down history navigation, in-progress draft preservation, Emacs-style Ctrl-A/E/U/K; persistent `~/.shumway_history`; horizontal scroll for queries wider than the terminal. Tab completion against builtins + every user-known predicate, multi-column listing sized to terminal width, capped at 200 results.

REPL display (251–252, 262): error rendering distinguishes `ShumwayPrologException` / `PrologRuntimeException` / other; both Prolog families surface `LastErrorStackTraceWithPositions` with `file:line:col`. `Solution.ToString(int width)` pretty-prints bindings, breaking long compounds/lists. Residual constraint display: wraps each query with `copy_term/3`, so `?- A #> 5, A #< 10.` prints `A in 6..9.` instead of leaving a bare unbound. CLP(FD)'s `clpfd_attr_goals/3` projects propagators (`$fd_lt` → `X #< Y`, `$fd_plus` → `A + B #= C`, `$fd_alldiff` → `all_distinct`, etc.) once each via an owner-first-var rule; binary cmps against integer constants are dropped because the resulting domain already captures them.

Listing (254–258): `listing/1` walks AST clauses directly (preserves source variable names), falls back to `PrecompiledStaticPredicates` for source-stripped bundles, demangles `<module>$` prefix for local predicates, and emits diagnostics (`no predicate matches X`, `X/N not defined`). New `portray_clause/1,2` builtin: SWI/SICStus head + indented body; width-aware multi-line layout with `,`-chains always breaking and args aligned past the open paren.

Tooling cleanup (259–261): deleted obsolete `shumway-bundler` (~847 lines, superseded by compile + link). `shumway-link` gained `-x`-style short flags for every `--xxx` option and complete help text. Zero out compilation warnings (was ~196, now 0) — source generator's `[PrologPredicate]` bridge gets `engine.Host!` / `call!.GetEnumerator()`; engine paths fix `_persistentProgram!` / `CurrentProgram!`; test files migrate `(IntTerm)s["X"]` casts to `!`-suffixed form and `Assert.Equal(1, x.Count())` to `Assert.Single`.

New public surfaces (262): `PrologEngine.ParseGoal(string)` returns `(Term, IReadOnlyList<string>)` for top-level wrap construction; `PrologEngine.Operators` exposes the runtime operator table so library-defined operators (CLP(FD)'s `in`, `..`, `#=`, ...) render in operator form; `AstTermRenderer.Render(Term, int, OperatorTable)` overload threads the caller's table; `use_module/1` Prolog-level library loader (`library(clpfd)` / `library(clpr)` plus atom-as-consult/1).

~60 new tests, 0 failures, no engine invariants modified.

**Phase 22 — Foreign-predicate toolchain (mode-aware sigs + --foreign-dll across compile/link/run)** — ✅ **Complete** (tagged `phase-22`; closure summary in [`docs/phase-22-closure.md`](docs/phase-22-closure.md)).

Three chunks (246–248) taking chunk-237/242's `[PrologPredicate]` from "works in-process" to "works through the full shumway-compile / shumway-link / shumway-exe pipeline":

- **Chunk 246 — mode-aware `[PrologPredicate]`**. Standard C# parameter modifiers map to Prolog modes: plain → `+`, `out T` → `-`, `ref T?` → `?`. Generator emits per-mode decode (`FromTerm<T>` with explicit `instantiation_error` for `+`; declare-then-unify-after for `-`; nullable-or-`FromTerm` for `?`). Validation: out/ref are incompatible with non-bool / non-void return and with non-determinism. Subtle generator bug surfaced: a `?:` ternary in the `?` decode typed `default` as `int` (boxing to `Nullable<int>(0)` instead of `null`) — fixed with explicit if/else.
- **Chunk 247 — `--foreign-dll` linker support + runtime auto-load + bundle V5 trailer**. `shumway-link --foreign-dll <path>` reflects the DLL, registers every `[PrologPredicate]` indicator as resolved during reachability, records the assembly filename in the bundle. `PrologEngine.LoadBundle` auto-registers each, probing adjacent to the .shum then `AppContext.BaseDirectory` then the default `Assembly.Load`. `--exe` copies foreign DLLs next to the produced executable.
- **Chunk 248 — `Opcode.ExecuteBuiltin` + full linker rewrite**. The compiler doesn't need to know about foreigns at compile time. Linker rewrites both Call→CallBuiltin (chunk 247, in-place 9-byte swap) and Execute→ExecuteBuiltin (chunk 248, in-place 5-byte swap via the new opcode). ExecuteBuiltin is the tail-call counterpart of CallBuiltin: same 5-byte width as Execute/ExecuteIl/ExecuteBytecode; dispatches the builtin then `Pc = Cp` to return. `BuiltinReturnPc = Cp` so backtrackable builtins resume at the caller's continuation rather than looping.

End state: the compiler emits unresolved external references with generic `Call`/`Execute`; the linker decides how to materialise each (native address, foreign builtin id). Standard separation-of-concerns the user flagged on review of chunk 247.

**Phase 21 — C# integration (ADR-010 embedding API surface)** — ✅ **Complete** (tagged `phase-21`; closure summary in [`docs/phase-21-closure.md`](docs/phase-21-closure.md)).

Eleven chunks (235–245) delivering the bulk of ADR-010's embedding API. Three threads:

- **Loading + lifecycle** (chunks 235–236). `PrologEngine.ConsultFile(path)` (extension-routed: `.shum` → `LoadBundle`, else `ConsultString`) plus the matching `consult/1` / `reconsult/1` ISO builtins. Chunk 236 fixed `reconsult/1` to follow classical GProlog / SICStus semantics: abolish the predicates defined in the file before loading, leave others alone (the chunk-235 alias-of-consult was a bug surfaced in review).
- **Foreign predicates** (chunks 237, 242, 244, 245). `[PrologPredicate("name/arity", NonDeterministic = false)]` attribute + `engine.RegisterPredicates(instance | typeof | <T>())` with conflict detection. Typed signatures (any of `void`, `bool`, `T`, `IEnumerable<T>`-when-non-det) get a generated `_{Method}_PrologBridge(Engine)` that decodes typed inputs via `FromTerm<T>` and encodes the return via `ToTerm<T>`. Non-determinism uses `Engine.PushBuiltinChoicePoint` (chunk-56 IL CP mechanism) — no new CP type. Deterministic `Dispose` on `!` / `->` via a new `IlChoicePointEntry.OnPrune` callback invoked in `Engine.Cut` (chunk 245).
- **Term conversion** (chunks 238–241, 243). Four-tier dispatcher: user converters → built-in scalars → composites → convention. `engine.ToTerm<T>` / `FromTerm<T>` / `RegisterConverter<T>`, `Solution.Get<T>` / `TryGet<T>`, `engine.Query<T>(text [, varName])` / `QueryFirst<T>`. Built-in scalars (int/long/...BigInteger/string/bool/char), composites (`List<T>`, `T[]`, `Tuple<,>`, `KeyValuePair<,>`, `Nullable<T>`, `Dictionary<K,V>`), and the convention tier discovers source-generated `ToPrologTerm` / `FromPrologTerm` methods via reflection (cached per type). `[PrologTerm]` source generator in new `Shumway.SourceGen` project (netstandard2.0); `[PrologTermIgnore]` opts a field out (chunk 243). Nested types emit their full `partial Outer { partial Inner { ... } }` hierarchy.

End-state lets a typical embedding look like `engine.RegisterPredicates(new Service()); foreach (var p in engine.Query<Person>("query(P).", "P")) ...` with zero manual register / heap manipulation.

Deferred: `EnginePool`, async `IAsyncEnumerable<Solution>` query API (cooperative cancellation via safe points), SWI-style `ForeignContext.IsFirstCall` / `State` for predicates that don't fit the `IEnumerable<T>` generator mold, mode declarations for multi-output foreigns.

**Phase 20 — Heap GC + Tier-1 IL completeness + dispatch perf + user-IL bundle** — ✅ **Complete** (tagged `phase-20`; closure summary in [`docs/phase-20-closure.md`](docs/phase-20-closure.md)).

Largest phase to date by chunk count (210–234, with the ADR-016 GC series interleaving its own 210–217 sub-numbering). Four threads:

- **ADR-016 heap GC** (chunks 210–217, ADR-016 series). Order-preserving sliding mark-compact collector + watermark + `garbage_collect/0`. Conservative stack scan (covers env Y-slots, CP-protected slots, query vars, global vars, retract/1 CPs); precise enough that the tabling fixpoint stays sound. `Tag.RawInt` (Phase 20 chunk 213) tags every CP / env control word so the conservative scan distinguishes them from heap refs.
- **Tier-1 IL completeness** (chunks 215–218). Deep cut (`GetLevel`+`Cut`) IL-emittable; full `switch_on_term`/`switch_on_arg` indexed dispatch in Tier-1 with O(1) key lookup + bucket backtracking, including in persisted-IL bundles (functor-id-keyed, lazy rebuild from linked code at first call); backtrackable builtins under Tier-1 via `Engine.BuiltinReturnPc`. No more IL exclusions for any ISO-shaped predicate.
- **Opcode dispatch fast paths** (chunks 220–227). Peephole / fusion (chunks 220–221: `AllocateGetLevel`, `DeallocateProceed`, `{Try,Retry,Trust}MeElse`+`CheckVisible`) + chunk-222's lock-free transient atom Intern + 256-char atom cache. Then **Stage B** — three new opcodes (`CallIl`, `CallBytecode`, `ExecuteIl`, `ExecuteBytecode`) that bake the dispatch decision into the bytecode at link time, eliminating the `OnDispatch` interface call + dict probe per Call/Execute. `PrologEngine.InstallCallIlRewrites` classifies each call site against `IlPromotion.IsPermanentlyBytecodeOnly`. Dynamic predicates stay on the original Call/Execute path because `JitIndexProfile` lives inside `OnDispatch` (chunk-227 fix). Blint Tier-0 exec wall-clock: 3988 ms → 3227 ms (-19% pure exec, excluding startup, 11-run median).
- **User-IL bundle correctness + IL machinery cleanup** (chunks 230–234). Discovered `BundleWriter.CompileEntryToIl` never IL-compiled user predicates — source-stripped `.shmo`s left an empty engine for PersistedIlBuilder to read. Chunk 230 routes source-less entries through `engine.LoadBundle` + a new `PrecompiledStaticPredicates` view. With user IL actually active, exposed a 30% regression vs Tier-0 WAM: chunks 231–234 closed the gap to 5%. Highlights: `_ilCpInfo` Dictionary → stack-array (`Engine.Cut` 5.31% → <0.74%; chunk 231); `AtomTable._lock` → `System.Threading.Lock` (`Monitor.Enter_Slowpath` -37%; chunk 232); IL indexed-dispatch cache moved off `ConditionalWeakTable<Engine, ConcurrentDictionary>` onto Engine directly (chunk 233); `[MethodImpl(AggressiveInlining)]` on `Cell` factories + `Span<Cell>` over the contiguous CP control-word block (chunk 234).

Other Phase 20 work: chunk 213 lock-free `AtomTable.Intern` permanent fast path + GC watermark tune (chunk 214); REPL `SHUMWAY_TIMING=1` per-phase wall-clock breakdown that unblocked the Stage B / chunks 230–234 measurement work; REPL polish (operator-form binding display, platform-correct EOF hint, top-level determinism detection); `term_to_atom/2` + SWI-compatible operator rendering; deep cut barrier fix; dispatch chain compaction (-44% opcodes on chain-heavy retract workloads).

Deferred to a future phase: Stage B.4 (runtime promotion mutation Call → CallIl); IL inlining of `ResolveEntryCursor` decode; WAM stripping for bundled-IL predicates (~40% bundle-size win for IL-heavy programs); background-thread `Assembly.Load` for Tier-1 startup.

**Phase 19 — IL meta-call dispatcher** — ✅ **Complete** (tagged `phase-19`; closure summary in [`docs/phase-19-closure.md`](docs/phase-19-closure.md)).

Closes the last gap in Tier-1 IL coverage — `call/N` and `'$call'/2` are now IL-emittable, removing the chunk-201 gate. `IlMetaCallHelper.Dispatch` mirrors the bytecode interpreter's `DispatchCall` (chunks 86, 88): derefs the runtime goal, routes control constructs to `$call_*` helpers, intercepts `!`/`true`/`fail` inline, recurses for `call(call(...))`. The emit threads the dispatch through chunk-182 with a last-call optimisation — when the CallBuiltin is followed by Proceed, Cp is left alone so the called goal's proceed jumps straight back to the outer caller (the original cut at Cp = resume_marker trapped in an infinite loop).

Plus the `\$call/2` barrier reader (`IlMetaCallHelper.ReadIntRegister`), 10 dedicated Phase19MetaCallTests, and the `MetaTransform` static `call/N` rewrite (chunk 205) that turned compile-time-known `call(foo(X))` into a direct `foo(X)`. Together they drop Blint's call-gate exclusion count from 11 to 0.

Blint Tier-1 IL: 7.9s vs Tier-0 9.2s (3-run median).

**Phase 19+ — implicit_dynamic, runtime-bound assertz, dynamic-clause bundles** — incremental follow-ups after `phase-19`:

- ✓ **206** — `implicit_dynamic` prolog_flag (default `true`). `assertz`/`asserta`/`assert` on an undefined predicate auto-promotes it to dynamic (SWI / SICStus / GNU behaviour); `set_prolog_flag(implicit_dynamic, false)` restores ISO-strict `permission_error`. Consult-time pre-scan (`CollectImplicitDynamics`) pre-declares literal-head assertz targets so the linker emits a real trampoline.
- ✓ **207** — runtime-bound `assertz(X)` / `call(pepe(Y))` in the same query. `MaterializeDynamicTrampoline` emits a chunk-127 trampoline mid-query for a freshly auto-promoted functor; `ResolveTargetMaybeAutoPromoted` resolves an `IsUnresolved` Call sentinel through `CurrentFunctorAddresses` — but only to an `enter_dynamic` trampoline, so module-local predicates stay invisible from outside their module.
- ✓ **208** — bundle/linker UX: `--exe` no longer requires `-o`; help text and the stale chunk-172 `--strip` caveat corrected. (The LoadBundle fast-path *widening* part of 208 was reverted — it surfaced the chunk-209 bug below.)
- ✓ **209** — **`:- dynamic foo/N.` predicates with source clauses now dispatch from a bundle.** Root cause: ShmoCompiler compiled a dynamic predicate's clauses as *static* bytecode, but `LoadEntryFromBytecode` registered the functor dynamic + empty `_dynamicClauses`, so dispatch hit an empty trampoline (Blint's `:- dynamic main/0.` returned `false`). Fix has four parts: (1) new `TermCodec` serialises clause terms to a compact binary form; (2) ShmoCompiler peels dynamic-head clauses out of the static bytecode into a `.shmo`/`.shum` **DynamicSeeds** trailer (`.shmo` V3, `.shum` V4) — `LoadEntryFromBytecode` rehydrates them into `_dynamicClauses` exactly as ConsultString does; (3) `CollectCalls` now descends into `catch`/`findall`/`bagof`/`setof`/`forall`/`once`/`ignore` goal args so callees inside a protected goal stay reachable; (4) ShmoObject's `ModuleName` is `DefaultModuleName` ("user") when the source has no `:- module/1` directive (was the file name) — and bundle-local fids feed `userLocalsCache` — so the dynamic-clause `ModuleRewrite` mangles body calls consistently with the static bytecode. Blint now runs end-to-end from both a `.shum` bundle and a `--strip --exe` native executable.

**Phase 18 — Bundle ergonomics + IL correctness + Tier-1 perf** — ✅ **Complete** (tagged `phase-18`; closure summary in [`docs/phase-18-closure.md`](docs/phase-18-closure.md)).

Four issues that Phase 17 surfaced (or the user flagged) while running Blint end-to-end via `shumway-link --with-compiled-il`:

- ✓ **200** — Linker accepts local entry-point predicates. No more `:- public pred/N.` requirement on the source. Transparent promotion via `:- public` source-augment + ShmoCompiler recompile; explicit ambiguity error when multiple modules define the same local.
- ✓ **201** — Two IL emit bugs surfaced by Phase 17 (now that persisted IL actually executes cross-process). `TryDescribeIndexedAtomPredicate` rejected predicates with mixed list-pattern + atom-headed clauses; `IsClauseBodyOpcode` gated `call/N` and `'$call'/2` CallBuiltin sites that need the bytecode interpreter's runtime goal dispatch.
- ✓ **202** — Tier-1 dispatch fast path. `OnDispatch` was allocating a fresh wrapper closure per hit (hundreds of thousands of GC allocations on Blint); cached by address. Skipped `RecordCall` on already-promoted predicates.
- ✓ **203** — Closure + tag.

Blint via persisted IL bundle now runs in 8.4s vs Tier-0 bundle 9.1s — IL is the fastest configuration and produces the correct answer end-to-end.

**Phase 17 — Cross-process persisted Tier-1 IL** — ✅ **Complete** (tagged `phase-17`; closure summary in [`docs/phase-17-closure.md`](docs/phase-17-closure.md)).

Persisted Tier-1 IL bundles produced by `shumway-link --with-compiled-il` now run **correctly in a fresh process**. Pre-Phase 17 the IL baked each functor/atom id as an inline `ldc.i4` constant; the build process accumulated `AtomTable` / `FunctorTable` interns the run process doesn't, so those integers pointed at the wrong functors at runtime (symptom on Blint: ~10× faster than Tier-0 but wrong answer). Phase 17 makes persisted IL **name-relative**: emit writes sentinel constants and a side-channel patch table; `LoadBundle` rewrites each sentinel's four bytes to the runtime id before `Assembly.Load`. Per-dispatch overhead is zero — the JIT sees normal inline immediates.

- ✓ **193** — `IlPatchSite` / `IlPersistedEntry` types + codecs.
- ✓ **194** — `IlPredicateCompiler` persist-mode emit helpers (`EmitAtomId` / `EmitFunctorId` / `EmitResumeMarker`).
- ✓ **195** — `PersistedIlBuilder.LocatePatchSites` post-Save PE scan.
- ✓ **196** — Bundle V3 format with per-entry patch + entries tables.
- ✓ **197** — `PrologEngine.LoadBundle.ApplyIlPatches` + runtime-fid delegate registration.
- ✓ **198** — Test harness (`PePatchPrototype`, `PePatchEndToEnd`).
- ✓ **199** — Closure + tag.

**Phase 16 — Tier-1 IL threading** — ✅ **Complete** (tagged `phase-16`; closure summary in [`docs/phase-16-closure.md`](docs/phase-16-closure.md)).

The chunk-50 Tier-1 IL Call site used to recurse into the bytecode
interpreter via `IlSubroutineRunner` → `RunSubroutine` →
`Dispatch`, creating a fresh C# stack frame per non-tail Prolog
call. Chunk 174 fixed a resulting Y-slot corruption bug
(backtracking inside the recursive frame could cascade past the
IL caller's CPs) with a floor pin, semantically correct but
exposing a ~7× slowdown on Blint and still leaving the C# stack
unbounded on deep chains.

Phase 16 redesigns Tier-1 dispatch as threaded continuation: the
IL caller sets `Cp = resumeMarker`, `Pc = callee`,
`IlTailCallPending = true`, and *returns* to the outer Dispatch
loop. When the callee Proceeds, `Pc = Cp = marker`; the bytecode
interpreter decodes the marker (chunk 181's encoding scheme) and
re-invokes the IL delegate at the matching forward-resume
cursor. The C# stack stays O(1) regardless of Prolog call depth.
Backtracking is the natural CP cascade — the callee's
`try_me_else` CPs carry the caller's marker as their saved Cp.

- ✓ **181** — Resume-marker encoding on Engine (high-range int
  with `EncodeResumeMarker(functorId, cursor)`); dispatcher
  recognition at the top of `BytecodeInterpreter.Dispatch`;
  `ITier1Dispatcher.ResolveByFunctorId` extension.

- ✓ **182** — IL non-tail Call emit switched to threaded
  pattern. The chunk-66 meta-CP push is gone — backtracking
  semantics fall out of the natural CP cascade. Cursor switch
  at the delegate's top collapses to a direct branch to the
  post-Call body. 3 architectural tests in `Chunk182Tests`:
  deep call chain (5000 levels stays in O(1) C# stack),
  backtracking across IL/bytecode boundary, mixed Tier-1
  promote=32 correctness.

- ✓ **183** — Delete `RunSubroutine` (chunk-50) + the chunk-174
  floor pin + `RunBacktrackWithFloor` (chunk-174) +
  `IlSubroutineRunner` / `BacktrackRunner` / `SetBacktrackFloor`
  / `SetE` engine callbacks (chunks 50/66/174). Net delete:
  223 lines.

- ✓ **184/185** — Tests + closure (merged).

Architectural goal met: deep call chains no longer blow C#'s
stack. The remaining ~8× Blint slowdown under `promote=32` is
*not* the floor pin — it's elsewhere (likely the per-Call
`OnDispatch` dictionary lookup or the linear cursor-switch in
the IL delegate's top). Follow-ups in `docs/phase-16-closure.md`.

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
| Persistent code space & live dynamic dispatch | ADR-015 |
| Heap garbage collection | ADR-016 |
| Inline compound references (2-cell cons) + cell-based unify | ADR-017 (phases 1 & 2 done) |
| Arithmetic instruction set (RPN eval stack) | ADR-018 (proposed) |
| PSTR design | docs/design/pstr-design.md |
| Debug info | docs/design/debug-info.md |
| Builtins catalog | docs/design/builtins-catalog.md |
| WAM instruction set | docs/design/wam-instruction-set.md |
