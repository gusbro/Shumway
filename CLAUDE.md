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

- **Naming (renamed 2026-07-11):** `Shumway.Core.Activation` (née `Engine`) is the per-query WAM machine — heap, stacks, trails, registers, choice points — born at every `SetupQueryFromTerm` and alive exactly as long as its solution enumeration. Several activations can coexist over one database (a suspended `QueryAll` plus a nested query). The durable Prolog instance (dynamic store, compiled code space, consult history) is `Shumway.Embedding.PrologEngine`. Historical docs/comments that say "engine" for the per-query machine mean Activation.
- **Activations are single-threaded internally.** No locks inside the activation state. The caller guarantees that only one thread accesses a given activation at a time.
- **Activations are thread-agile.** No `[ThreadStatic]` state. An activation can be used from different threads as long as access is serialized.
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
- **Opcodes are numbered CONTIGUOUSLY** (chunk 429) so the interpreter's switch compiles to one dense jump table. The Meta opcode (sub-byte for kind, currently only DbgInfo) sits at the end of the dense dispatch block, ReservedExtension right after it; new opcodes are added at the end of the dense block (see Opcode.cs for live values — do NOT cite numeric values in docs).
- **All dispatched opcodes follow fixed-size encoding** with operands as unaligned ints. Sizes are determined by a per-opcode table.

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

### Comment policy (2026-07 cleanup — binding for all new code)

A comment exists to state something the code cannot show: an **invariant**, a
**constraint**, a **non-obvious trick**, or a **trap** ("don't simplify this to
X — it breaks Y because Z"). One to three lines. Everything else is noise:

- **No chunk numbers.** They reference a work log, not the code. History lives
  in git (every chunk was a commit), the phase closure docs, and the ADRs.
- **No historical narrative** ("this used to be a Dictionary until we measured
  …"). If the old design is a trap someone might reintroduce, state the trap in
  one line ("not a Dictionary: probe cost dominated dispatch"); drop the story.
- **No restating the code**, no measurement archaeology (profile percentages,
  dates, who found it), no war stories in doc-comments.
- **ADR references are fine** (`// ADR-031: …`) — ADRs are living repo docs.
  Prefer them over prose when the rationale has an ADR.
- XML doc-comments on public API: what it does and its contract, brief. The
  design essay belongs in an ADR or docs/design, not the doc-comment.

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

**Phase 35 — ISO conformance (Neumerkel) + soft cut (ADR-037) + module-local meta-calls + REPL polish** — ✅ **Complete** (tagged `phase-35`; closure summary in [`docs/phase-35-closure.md`](docs/phase-35-closure.md)). 61 commits. (1) ISO reader/writer conformance driven by Neumerkel's suites — writeq/write_term token-adjacency + operator/list parenthesisation, number_chars/number_codes §8.16.8 via a term-reader fallback, reader edge cases (radix lowercase-only, line-continuation, control chars, §6.3.1.3 bare operator-atom), directives-as-goals (§7.4.2), coroutining `when`/`?=`/`unifiable` + `dif/2`; scores number_chars 67/67, variable_names 63/63, dif 26/26, syntax 201/202 (only #106 diverges). (2) **ADR-037 soft cut `*->/2` end to end** — `soft_cut` opcode (mirrors GProlog's, verified by pl2wam disasm) with Tier-0 + Tier-1 IL, inline + non-eligible (synthesized soft-cut helper + runtime `$call_disj`/`$call_softarrow`), `time/1` determinate; fixed a nested-branch-cut crash and a **latent `->` bug** (runtime-built `( true -> a ; b )` ran both branches — `DistributeMqual`/`WrapGoal`). (3) Module-local predicates meta-called by name in linked bundles resolve via `$mqual` module-relative tagging (interpreter + IL); findall/bagof/setof variable-goal fallbacks run live. (4) REPL: fresh-line answers via output-column tracking (redirection-safe), default Tier-1 IL auto-promote at threshold 32. Closing ISO §7.8/§8 audit: every ISO construct/builtin executes; `*->` was the only "parses but doesn't run" gap. Gate: Core 444 / Interpreter 105 / Compiler 360 / ISO 298 / Embedding 3424.

**Phase 34 — Source-level debugger: Visual Studio + VS Code (ADR-035 / ADR-036)** — ✅ **Complete** (tagged `phase-34`; closure summary in [`docs/phase-34-closure.md`](docs/phase-34-closure.md)).

One engine-side debug core, two full IDE frontends, every deployment shape (REPL
`--debug`, embedded `EnableDebugging`, linked `--exe --debug`). **ADR-035** (VS 2026
via Concord): port-based stepping, conditional breakpoints evaluated engine-side,
live-engine evaluation + bind-into-frame, destructive Watch edits, Set Next Statement
(forward/backward/cross-frame/sibling-clause), `:- disable_debug.` semi-native
predicates, lazy arm-on-attach, func-eval on Release engines. **ADR-036** (VS Code via
DAP, cross-platform): in-process DAP server over the same session (stop = semaphore, NO
func-eval), both endpoints coexisting with single-driver arbitration, zero-JS
declarative extension + `shumway-dap` C# adapter (`runInTerminal` launch, `--dap-wait`
hold-the-door), Debug Console = Immediate, `setVariable`, Jump to Cursor, logpoints,
`--dap-port` baked into executables with `SHUMWAY_DAP_PORT` precedence. The launch race
surfaced and fixed a real arm-vs-consult data race in the core (consult under the
debug-arm gate). Verified IDE-less by xUnit DAP clients over real sockets in the normal
gate. Pending: a Linux end-to-end smoke when a box exists (everything is xplat by
construction); `docs/debugger.md` + `docs/debugger-vscode.md` are the user guides.

**Phase 33 — Audit remediation + real-program rounds + the cut/tail-call arc** — ✅ **Complete** (tagged `phase-33`; closure summary in [`docs/phase-33-closure.md`](docs/phase-33-closure.md)).

Opened as audit remediation round 1 — five waves attacking the six-way audit
of 2026-06-30 (backlog closed 65/66 in
[`docs/phase-33-backlog.md`](docs/phase-33-backlog.md); the one open item is
the dump-armed intermittent native AV, whose likeliest cause —
`_emitOwnerFid` plain-static under concurrent compiles — was found and
fixed) — and grew into the largest phase to date (138 commits):

- ✓ **Waves 1–5**: correctness-critical E-series; interop hot path; WAM
  codegen (once/snips, neck-cut, assert fast-path, Tier-0 ITE, DCG
  disjunction, `execute_builtin` fusion); IL dispatch/promotion (runtime
  Call→CallIl, background promotion, churn re-arm, baked index graphs);
  LTO/startup/size (`--prune-prelude` −94%, bundle compression, persisted-IL
  cache). Plus the user-directed profile-driven IL round 2.
- ✓ **Real-program rounds**: Logtalk 3.101.0 (random 457/457, term_io 87/87,
  types 148/149, many testers 100%; benchmark parity-or-better vs GProlog on
  every shape except nrev); Djota 32/32 (six standard-DCG fixes + fail-fast
  DCG lowering, −15.3% heap/render); the GProlog-doc ISO predicate audit
  (all gaps fixed).
- ✓ **ADR-027/028**: second-level (sub-argument) indexing — bounded 2-hop
  paths (list head / struct sub-arg / token stream) — and sibling-arg +
  structure-keyed indexing inside value buckets, all three tiers.
- ✓ **The cut/tail-call arc (ADRs 029–034)**, census-driven end to end:
  029 epilogue fusion; 030 redundant-cut elision (det greatest fixpoint,
  intra-module + linker whole-program closure); **031 CP-free guard commit
  default ON through every tier** (A/B/G/G2/G3, staging + callee-cut
  widenings, lazy CP under wakeups) **including INDEXED BUCKETS** (the lazy
  bucket CP via the per-member `idxnext` local — test/ 724→2,796 accepted,
  testGen/ 601→3,014, measured 1.58× end-to-end on dispatch-then-validate);
  032 soft-rejected; 033 guard continuation stack prototype (shared copies,
  cross-tail LCO composition, deep-G3 tail cycles via the pure-tail-segment
  rule); **034 sound stable-dynamic inlining default ON** (fixed the shipped
  LUV bug — snapshots inlined into caller IL with no eviction path — plus
  the Call→CallIl hardening and per-query `IlByFunctorId` staleness
  variants; empty-dynamic-as-fail measured then rejected on runtime-cost
  grounds). Two latent soundness fixes shipped along the way (lazy-CP
  clobbered-register patch via `SetTopCpArgRegister`; the mixed-cycle rule).

**Phase 32 — ADR-024 materializer ↔ dematerializer tier** — ✅ **Complete** (tagged `phase-32`; closure summary in [`docs/phase-32-closure.md`](docs/phase-32-closure.md)).

Attacks ADR-024's deferred TODO: whole-term interop for the case the cursor tier
doesn't cover — when C# is only a **trampoline to a native C function** (P/Invoke,
can't touch the Shumway heap) or a .NET method that wants a struct **snapshot**.
Driven by the GX `testProc` corpus (`C:\temp\testProc\*.c`, Prolog under `#ifdef
GXPROLOG`, C under `#else`): the uniform pattern is `fill_par(Term,&parNref)` →
`ret='native_fn'(...,parNref)` → `reftype_term(Term,&parNref)`.

**Settled design (with the user):**
- A **`:- native fn/N`** directive marks a function as native C (P/Invoke) vs .NET —
  the call site picks the mechanism. Materialize/Dematerialize **wrap the `:- native`
  call**; `fill_par`/`reftype_term` stay the cursor builtins (Phase 30). At the call,
  each `:- c`-prototyped `reftype` arg is materialized to a blittable native
  `t_reftype`, the pointer passed, then dematerialized back (native C may build/modify
  it — e.g. `i_nextinfo`→`menu_to_list` builds a list).
- The native DLL is named by engine config / a CLI flag (`--native-dll` /
  `engine.UseNativeLibrary(...)`); `fn` resolves by name.
- The **managed snapshot** path is the same core to a managed `Reftype`, triggered by
  a .NET interop method whose parameter is `Reftype`/`ref Reftype` (not `TermSlot`).
- `t_reftype` layout fixed **identical to Arity** (`int64 ntype; int64 nelem;
  t_reftype** pars; union crep`), blittable; ntype 3 (atom) and 4 (string) both →
  atom on dematerialize.

- **Núcleo — managed snapshot (done).** `Shumway.Embedding.Reftype` — the managed
  snapshot + `Reftype.Codes` ntype contract; `Materialize(Term)→Reftype` (recursive
  over functor args) and `Dematerialize(Reftype)→Term` (atom/string→atom, undef→fresh
  var). 10 round-trip tests over every ntype incl. nested functor and a "native C
  built the struct" case.
- **Blittable native-memory form (done).** `Shumway.Embedding.NativeReftype` —
  `Materialize(Term, Encoding?)→IntPtr` builds the real 32-byte `t_reftype` graph
  (`AllocHGlobal`; cint 32-bit; pars = `t_reftype*` array),
  `Dematerialize(IntPtr, Encoding?)→Term`, and `Free` walks + releases it (Arity
  `freepar`). `char*` text uses a configurable `Encoding` — default **UTF-8**, set
  per engine via `PrologEngine.NativeTextEncoding` (UTF-8/Latin1/codepage; byte-
  oriented). 14 tests: round-trips, the Arity field-offset layout, native-
  modification-in-place, deep-graph free, and the encoding (byte-level UTF-8 vs
  Latin1, engine default + config).
- **`:- native` directive + managed-snapshot backend (done).** `:- native fn/N`
  (a new `fx 1150` prefix operator) marks fn as a materializer-protocol function;
  registered in `PrologEngine._nativeFunctions` (`IsNativeFunction`). At a block
  interop call to a `:- native` fn, a `Reftype` parameter receives a **materialized
  managed snapshot** of the reftype global's term (`slot.Materialize()` →
  `Reftype.Materialize`), and the mutated snapshot is dematerialized back into the
  slot after the call (`slot.SetValue(Reftype.Dematerialize(...))`) — wired in
  `NativeBlockRunner.CallInterop` (the interpreter; the delegate/IL backends bail to
  it on a `Reftype` param). 3 end-to-end tests: `go(10,Out)` → `result(11)` via
  fill_par → materialize → C# mutates the snapshot → dematerialize → reftype_term.
- **P/Invoke backend (done).** A `:- native` function that does **not** resolve to a
  C# interop method is a real native C function exported by a registered library
  (`engine.UseNativeLibrary(path)`). Resolution is **cached per functor** (C# method
  vs native export → `NativeResolution`), so a call resolves once and dispatches
  directly thereafter. `NativeCall` derives the marshalling signature from the
  `:- c` prototype (reftype → native `t_reftype*`; int/short/long/double by value)
  and invokes by pointer via a cached **cdecl `calli`** (`DynamicMethod`, JIT-only).
  At the call: each reftype arg is materialized to native memory (`NativeReftype`),
  passed, then dematerialized back into its slot and freed. First cut: the native
  function may modify the struct's scalar fields **in place** (allocating sub-nodes
  needs a shared allocator — follow-up); char*/out-scalar pointer params deferred
  (loud error). The consult-time block validation exempts `:- native` names
  (`IsNativeFunctionName`). Tests: 4 mechanism (signature + calli over a native
  `t_reftype` via a C# fn-pointer, CI-safe) + 1 real-DLL end-to-end (compiles via
  `$SHUMWAY_NATIVE_CC` or a PATH compiler `cl`/`cc`/`gcc`/`clang` — no hardcoded
  paths, cross-platform; warns + skips with no toolchain).
- **"C builds a list" — native-allocator mode (done).** A native function that
  **allocates** sub-nodes (builds a list/term into the struct) can't have its graph
  freed by `FreeHGlobal` (mixed allocators). Fix, as in Arity: when the native
  library exports the reftype allocator API (`newreftype`/`freepar`/`getargp`/
  `setcflt`), Shumway materializes and frees **through it** (`NativeReftypeAllocator`)
  so the whole graph lives in the library's heap. `UseNativeLibrary` auto-detects it;
  `PInvokeCall` uses it when present (else the HGlobal in-place path). Dematerialize
  reads the C-built graph unchanged. Test (real DLL): `build_list` allocates a cons
  list via `newreftype`, Shumway dematerializes `[1,2,3]` and `freepar`s it.
- **Out-scalar pointer params (done).** `fn(..., &local)` — a native function writes
  a scalar through a pointer (the corpus `i_form_exp(.., &type, ..)` /
  `i_obj_id_native(.., &id)` pattern). `NativeCall.Kind` now distinguishes
  `Scalar`/`Reftype`/`OutScalar`; a `short*`/`int*`/`long*`/`double*` param (incl.
  via typedef like `pshort`) maps to `OutScalar` with its element type. At the call,
  `PInvokeCall` allocates a native scalar (seeded from the block-local), passes the
  pointer, then reads it back into the local — so a following `X is local` sees what
  the function wrote. Test (real DLL): `set_out(In, &oi, &os)` → `calc(5,Ri,Rs)` →
  `Ri==50, Rs==6` (int* + short*).
- **`--native-dll` CLI flag (done).** `shumway-link --native-dll <path>` records a
  native C library (DLL/.so/.dylib) in the bundle (`Bundle.NativeLibraries`,
  serialized in both .shum writers + reader); `LoadBundle` auto-loads each via
  `UseNativeLibrary` (probed next to the bundle / executable), and `--exe` copies
  them alongside — mirrors `--foreign-dll`, no reflection (native functions are
  declared via `:- native` + `:- c`). Tests: serialization round-trip (CI-safe) + a
  source-stripped-bundle auto-load end-to-end (no `UseNativeLibrary` call).
- **`:- native` + prototypes serialized for source-stripped bundles (done).** The
  `:- native fn/N` indicators and the raw `:- c` declaration text now travel in the
  `.shmo` (`ShmoObject.NativeFunctions`/`NativeDecls`) and `.shum`
  (`BundleEntry`, both writers + reader, via `BundleWriter.WriteNativeInterop`), and
  `LoadBundle` restores `_nativeFunctions` + re-parses the prototypes
  (`RegisterNativePrototypes`). So a source-stripped Release bundle / `--exe`
  resolves `:- native` (managed snapshot *and* P/Invoke) with no source — verified
  by a stripped-bundle P/Invoke test. (NB: a format change → rebuild the CLI + the
  Release REPL the cross-process tests use.)
- **char\* params (done).** A `:- native` function's `char*` parameter marshals a
  Prolog string into NUL-terminated native memory (via the engine's
  `NativeTextEncoding`, default UTF-8), passed and freed (`NativeCall.Kind.StringIn`).
  A `char*` **return** flows to the block as a raw pointer integer (the inference types
  a char*-returning call as a pointer, not a string), so the corpus pattern
  `{ Ptr is 'tbl_name'(M,T) }, Ptr \= 0, make_prolog_string(Ptr, Name)` works:
  `make_prolog_string` reads the NUL-terminated native string from an integer source.
  Tested over a real DLL incl. byte-exact UTF-8. (`char**` / out-string still deferred.)
- **IL emit for the materializer tier (done).** Both `:- native` backends now compile
  to IL instead of bailing the block to the interpreter. The **managed snapshot**
  (Reftype-param) path emits inline — materialize → call → write-back — as an
  `Expression.Block` keyed off the `Reftype` parameter type. The **P/Invoke** path
  (Option 1, since Expression trees can't emit cdecl `calli`) routes through
  `NativeBlockRunner.PInvokeFromIl` — the same marshalling over pre-evaluated boxed
  args, returning a boxed long/double the emitted IL unboxes; `EmitNativeCall` boxes
  scalar/string args, names reftype globals, and invokes it. The win is structural —
  scalar work *around* a native call now runs as IL. Both proven via `CompiledCount`.
- **out-scalar + `char**` out-string (done).** An out-scalar (`short*`/`int*`/…
  `&local`) and a `char**` out-string (`&local`, the native side writes a borrowed
  `char*`) both marshal in the interpreter *and* under IL — the IL emit threads the
  read-back values through an array the emitted method stores into its block-locals
  (`PInvokeFromIl`'s `outScalars` channel), so a native call with these params
  compiles instead of bailing. Memory ownership documented in
  [generic-term-interop §10](docs/generic-term-interop.md): Shumway owns + frees the
  out-scalar slot and the `char**` cell (call-scoped); the pointed-to / returned
  `char*` is **borrowed** (native-owned, copied out, never freed) — a `malloc`'d
  return would leak, by design (caller-owns would need an explicit paired-free
  annotation).
- **`--exe` + `--dll` native run-through (done).** Validated the whole chain in a
  shipped binary: a source-stripped Release bundle with `:- native bump_native/1`
  over a real `nrt.dll`, linked `shumway-link --native-dll nrt.dll`. The native lib
  is copied next to the output for **both** emitters — `ExecutableEmitter` already
  did it; `LibraryEmitter` was missing it (the `--dll` path dropped native DLLs) and
  now copies them too (CLI passes `nativeDllPaths`). Verified: `--exe` →
  `app.exe` prints `42` (P/Invoke bump 41→42, no source, DLL auto-loaded), exit 0;
  `--dll` → a consumer app calling `Bundle.CreateEngine()` runs `go(41,Out)` → `42`
  with `nrt.dll` copied to a separate output dir and auto-loaded by `LoadBundle`.
- **Native-library lifetime + thread-safety (done).** A native library is now loaded
  **once per path for the process** (a static `_loadedNativeLibraries` table guarded
  by a lock) and shared across engines, instead of one `NativeLibrary.Load` per engine
  — the old per-engine load leaked an OS refcount per engine under churn. The mapping
  is never freed (lives to process exit). Documented the contract in
  [generic-term-interop §10e](docs/generic-term-interop.md): `:- native` calls are
  **not serialized** (a parallel multi-engine caller needs a reentrant library;
  borrowed static-buffer returns race), and native global state is process-global and
  **not** reset between engines. Test: two engines loading the same path trigger one
  real load and both resolve + call.
- **Phase 32 ready to close** — the materializer tier is functionally complete
  (scalar / reftype / char\* in / char\* return / out-scalar / char\*\* out-string,
  interpreter + IL) with the ownership + lifetime/thread-safety model documented and
  the deployment chain (`--native-dll` → `--exe` / `--dll`) verified end-to-end.

**Phase 31 — REPL line-editing + `--dll` + native-interop correctness** — ✅ **Complete** (tagged `phase-31`; closure summary in [`docs/phase-31-closure.md`](docs/phase-31-closure.md)).

Opened with two user-named themes (REPL line editing; a linker `--dll`) and grew,
through review, into a native-interop correctness arc plus a test-discipline fix.
Highlights: `--dll` loadable class library (factory `<Ns>.<Class>.CreateEngine()`);
REPL multi-row wrapping + flicker fix + ESC-cancel (reaching `between/fail` /
`repeat/fail` via a counter-throttled `TryBacktrack` IL-CP safe point); fixed 7
long-stale Interpreter tests (ADR-017 / Phase-28) and recorded the **five-project
gate** discipline; a four-item `embedded-native-c.md` stale-doc audit; **persistent
scalar `:- c` globals** (Arity static-storage, all three native backends + bundle +
`--exe`, write-through) with **undeclared-global → consult error** and `extern` as
the cross-module declaration. Full gate at close: Embedding 2572 / Compiler 302 /
Core 432 / Interpreter 105 / ISO 277.

- **`--dll` — loadable .NET class library.** `shumway-link
  --dll <path>` emits a .NET class-library DLL embedding the `.shum` bundle plus a
  generated factory (`<Namespace>.<Class>.CreateEngine()`) returning a
  ready-to-query `PrologEngine` (via `PrologEngine.FromBundle`, baked-prelude warm
  path). For a .NET app that *uses* Shumway to evaluate goals, not an `--exe` whose
  whole point is one startup goal. `Shumway.Embedding.LibraryEmitter.Emit(...)` is
  the API the CLI wraps: writes a temp classlib project (csproj refs the Shumway
  runtime DLLs, embeds `bundle.shum` as manifest resource `shumway.bundle`),
  `dotnet build -c Release`, copies the DLL + every Shumway dependency DLL (+
  foreign DLLs) next to the output. Namespace defaults to the sanitised DLL
  filename (`Greeter.dll` → `Greeter`), class defaults to `Bundle`; both
  overridable with `--dll-namespace` / `--dll-class`. `--dll` and `--exe` are
  mutually exclusive; `--dll` needs a reachability root (`--entry`/`--goal`) like
  any link. Verified end-to-end: a consumer .NET app referenced the generated
  `Greeter.dll`, called `Greeter.Bundle.CreateEngine()`, ran `greet(X)`, got
  `hello`/`world`. Documented in `docs/user-guide.md` step 3b. 16 LibraryEmitterTests
  (namespace/identifier inference — the full build path is manually verified, too
  heavy for the unit suite, matching the `--exe` precedent). Gate Embedding 2558.

- **REPL line editing — long-line wrapping (done, pending commit).** User
  confirmed the pain is **líneas largas**: a query wider than the terminal
  horizontally-scrolled on one row (chunk-253 `ComputeVisibleWindow`), hiding the
  line's start while editing its end. Replaced with real multi-row wrapping: a
  per-`ReadLine` `LineView` repaints `prompt + buffer` from a captured origin row
  on every edit, lets the console wrap naturally, then positions the hardware
  cursor at the logical edit point *across rows*. Scroll detected via
  `Console.BufferHeight` (not post-write `CursorTop` — sidesteps the deferred-wrap
  phantom-column ambiguity); origin shifted by the overflow so cursor math stays
  aligned. All console ops guarded → non-interactive host degrades cleanly. Pure
  `CellRowCol` helper replaces `ComputeVisibleWindow`; Chunk253Tests retargeted (6).
  Interactive path is manual-smoke-only (headless input takes the `ReadLine`
  fallback); REPL verified to still run end-to-end. Follow-up: per-keystroke
  flicker (cursor visibly jumping to column 0 during repaint) killed by hiding
  the cursor across the repaint (`Console.CursorVisible=false`/`true`, guarded).

- **REPL ESC-cancel of a long query (done, pending commit).** Like SWI: press
  `Esc` and a long search aborts. A background watcher thread polls the console
  while the query runs on the main thread; ESC fires a `CancellationTokenSource`
  the engine observes at its next safe point (checked at *every* safe point —
  `Engine.MaybeCollectHeap` — so even heap-light loops cancel) and throws
  `OperationCanceledException`, caught in `RunQuery` → `% Execution aborted.`. New
  `PrologEngine.QueryAll(Term, CancellationToken)` overload (mirrors the existing
  string one) because the REPL wraps the parsed goal in `copy_term/3`. Each query
  builds a fresh `Engine`, so the never-cleared `_cancelRequested` flag isn't
  sticky. Watcher drops non-ESC keys and is joined before the main thread reads
  keys again; redirected input skips watching. 2 Term-overload cancellation tests;
  gate Embedding 2562. **Follow-up — backtrackable-builtin loops.** `between(0,
  BIG, X), fail` / `repeat, fail` re-satisfy through a builtin choice point and
  never cross a call-boundary `MaybeCollectHeap`, so ESC couldn't reach them. Added
  `Engine.BacktrackSafePoint()` — a counter-throttled cancel poll (non-volatile
  decrement + predicted-not-taken branch; volatile flag read every 4096 calls) —
  called in `TryBacktrack`'s **IL-choice-point branch** (the resume path those
  builtins take via `PushBuiltinChoicePoint`). Clause-backtracking loops re-satisfy
  via `Call` and were already cancellable at the call-boundary safe point, so they
  never reach this and pay nothing — back-to-back Van Roy: zebra 0.6% (within
  noise), queens faster. Now both abort in ~100ms. (Also fixed 7 long-stale
  Interpreter tests this surfaced — 6 ADR-017 inline STR/LIS-in-register, 1
  Phase-28 deallocate frame reclaim — that had been failing on baseline since
  Phase 25/28 because the Interpreter suite was omitted from the routine gate.
  Full 5-project gate now green: Core 432 / Interpreter 105 / Embedding 2564 /
  Compiler 302 / ISO 277.)

**Phase 30 — Arity/Prolog32 compatibility, round 2** — ✅ **Complete** (tagged `phase-30`; closure summary in [`docs/phase-30-closure.md`](docs/phase-30-closure.md)).

Widened the Phase-24 Arity source-compat work, driven by the reference material
at `C:\Arity` and real Arity programs (`C:\temp\test` 245, `testGen` 311,
`testProcDotNet` 31). Grew past that into an efficiency audit, the `shumway-lib`
librarian (4th CLI), three ADRs delivered end-to-end — **022** embedded native C
(`:- c`/`{…}` → IL, runtime + persisted), **023** dynamic predicates in Tier-1 IL
(snapshot + evict + prime + persisted bake), **024** generic term interop (reftype
cursor + Arity `*_c` layer + string holders, full IL) — and a runtime-correctness
arc the real-program validation surfaced: float literals in IL across all paths,
source-less-bundle literal remapping, the two decode sites unified, and the PSTR
`==` infinite-recursion fix. The `:- visible` directive was corrected to
exported-mutable (not static). Chunks 425–444 plus the native-C / reftype / float /
literal arcs. Gate at close: Embedding 2542 / Compiler 302 / Core 432 / ISO 277.

- **Chunk 442 — `shumway-lib` librarian (new CLI)**. A fourth CLI tool
  (`src/Shumway.Lib/`, assembly `shumway-lib`) that packages chosen `.shmo`
  objects into a runnable `.shum` *without* the linker's reachability analysis
  / dead-module pruning — every object you add is kept. Commands: `create`,
  `add`/`r`, `delete`/`d`, `list`/`t`, `extract`/`x` (wildcards + `-C` out-dir),
  the usual `ar`-style surface. The `.shum` format gains a first-class **archive
  section** (`Bundle.ArchiveMembers` — each a verbatim `.shmo` image keyed by
  module name; written by both `.shum` writers, read by `BundleReader`; no
  version bump, pre-release layout-change-free): the linker stores its modules
  as post-link `BundleEntry`s, the librarian stores them as raw `.shmo`s, and
  `LoadBundle` derives a runnable entry from each member at load — so an archive
  is directly runnable with **zero duplication** (the `ar`/`.a` model) and
  `extract` reproduces the input byte-for-byte. Cross-module public calls
  resolve at load with no link step (verified debug + source-stripped release).
  `Shumway.Embedding.Librarian` is the tested API the CLI wraps. 11
  LibrarianTests; gate suites Embedding 2357 / Core 432 / Compiler 284 / ISO
  277, all green. (Also fixed a latent `_dynamicLink` CS8602 in the query-setup
  linker that any compilation perturbation could surface.)

- **Chunk 443 — `shumway-link` accepts `.shum` libraries (C-archive
  semantics)**. The linker now takes `.shum` inputs as *libraries* alongside
  `.shmo` *objects* (routed by extension). Objects always link; a library's
  members are pulled in **only on demand** to satisfy a reference the explicit
  objects (plus builtins / prelude) leave unresolved — searched **FIFO** (first
  providing library wins; explicit objects win over any library), pulled at
  module granularity (like a C linker pulling a whole `.o`), **transitively** to
  a fixpoint. Members no reference reaches are not linked. A `.shum` library
  must be a `shumway-lib` archive (it carries its `.shmo`s); a linked bundle has
  none → clean error. Implementation: `LinkConfig.Libraries` (`LinkLibrary` =
  named member list) + a `PullLibraryMembers` pre-pass that resolves
  reachability with FIFO pull and returns explicit ∪ pulled. It runs **before**
  the chunk-411 cross-module LTO unfold, so the full set is optimized together
  (resolve-then-optimize, the real-LTO-linker order — pulled members get the
  same cross-module unfold as explicit ones, including library wrappers, since
  each `.shmo` carries its `ClauseTerms`). Under-pull is impossible for the
  edges the main walk follows; anything genuinely unprovided still surfaces as
  `missing_predicate`. 6 LinkLibraryTests; gate Embedding 2363 / Core 432 /
  Compiler 284 / ISO 277, all green.

- **Chunk 444 — chunk-443 follow-ups (`--map` + foreign-pred interaction)**.
  (1) `--map` now lists pulled library modules: `LinkResult.LinkedObjects`
  exposes the resolved set (explicit ∪ pulled, pre-LTO form) and the CLI feeds
  it to `ShmoBundleMap` instead of just the explicit objects (identical map for
  non-library links). (2) `--foreign-dll` predicates are now honored by the
  library pull pre-pass: foreign-assembly reflection moved to a single up-front
  `ReflectForeignAssemblies` helper (run once, reused by the builtin snapshot),
  and its indicators feed the selection's "already available" set — so a
  library member is never pulled to satisfy a reference a foreign predicate
  provides (foreign wins, like a builtin). 8 LinkLibraryTests (2 new); gate
  Embedding 2365 / Core 432 / Compiler 284 / ISO 277, all green.

**Phase 29 — region compilation shipped + engine correctness/runtime arc** — ✅ **Complete** (tagged `phase-29`; closure summary in [`docs/phase-29-closure.md`](docs/phase-29-closure.md)).

Opened as Tier-1 rule inlining (chunk-364 survey); the user's REGION COMPILATION
model (each local predicate's body emitted once inside the caller's IL method,
intra-region calls a `br`) superseded the duplication inliner and shipped as the
DEFAULT. Sixty chunks (365–424). Highlights: regions through every member shape
(single-clause / chains / indexed / cut / cross-region / backtrackable builtins /
meta-calls via `BuiltinResume` cursors — Blint 0 region-skips); the dead-region
prune + WAM strip making `--strip-wam` bundles sound end-to-end; regions
**default ON** after the chunk-418 validation found the real lever (the
`(C->T;E)` lowering: ~2× on ITE-recursion, qsort −22%, boyer −15%, corpus
output-identical); **ADR-021** rejecting the register-allocator arc with survey
data; the link-time `MetaWrapperUnfold` (Blint meta-dispatches −99.9%) + the ISO
branch-cut transparency fix; the ISO `unknown` flag wired through dispatch; the
dynamic-mutation cost arc (tombstone reclaim by dead count, threshold 4 — Blint
opcodes −4.9% deterministic; retract −70% alloc); interpreter superinstruction
runs; format freeze + `[Conditional("SHUMWAY_DIAG")]` diagnostics. The chunk-404
unlink corruption was fully post-mortemed (two distinct defects) and its shapes
pinned by a churn regression suite.

**Phase 28 — real-program validation corpus + Tier-1 IL runtime speed** — ✅ **Complete** (tagged `phase-28`; closure summary in [`docs/phase-28-closure.md`](docs/phase-28-closure.md)).

Began as a GProlog-oracle validation corpus (real third-party Prolog, diffed
against GNU Prolog) and became a sustained Tier-1 IL runtime-speed arc — the
horizon being that a shipped program runs as Tier-1 IL packed in a bundle.
Thirty-eight chunks (327–364). Highlights: corpus-surfaced engine fixes
(`append/3` improper-list split; cut flushing attribute wakeups; `CompactTrails`
dropping live `AttrModify`/`BigIntAlloc` entries); a **native C# clpfd domain
layer** (~3.5–4×); WAM void-batching; and the Tier-1 speed arc — **lazy Y-slot
allocation** (tight-loop heap GC was ~90%; ~4.6×), env-frame reclaim,
try/catch-free arithmetic fast lanes, self-tail-recursion as an in-method loop,
and the **local-predicate fact inliner** (default ON) whose 362→363 story —
replacing a misguided size budget with an O(1) cursor jump table — is the
phase's measurement lesson. The chunk-364 survey scopes Phase 29 (rule
inlining). Carried discipline: trust the structural argument over thermal-noisy
wall-clock; measure interleaved min-of-N.

**Phase 27 — Tier-1 IL bundle slimming + non-last nested inline + cleanup** — ✅ **Complete** (tagged `phase-27`; closure summary in [`docs/phase-27-closure.md`](docs/phase-27-closure.md)).

A mixed phase, four themes in order 1,3,4,2 (chunks 316–326 plus letter chunks
B/C). **Theme 1 — `--strip-wam`**: a Tier-1 IL bundle drops its now-redundant WAM
bodies. IL→IL calls dispatch by functor id via `IlByFunctorId` (316/319);
indexed dispatch runs on a WAM-independent node graph (`IlIndexGraph`, 320)
persisted in the bundle (`IndexGraphCodec`, B) so indexed predicates strip too;
runtime meta-calls to a stripped predicate resolve via a resume-marker alias in
`CurrentFunctorAddresses` (C). Blint bundle −20%; runs cross-process + as `--exe`.
(Size note: a stripped IL bundle is smaller than `--with-compiled-il` but larger
than WAM-only — IL is more verbose than the WAM it replaces.) **Theme 3 —
ADR-020**: inline non-last nested compound build in body args (reserve-upfront
`put_structure_r`/`put_list_r` with the arity baked, + a write-pointer frame
stack); Blint total WAM 15087→14039 (−7%), `get_structure` −68%, `get_list`
−80%, beating GProlog (which BFSs these). Head matching deliberately kept BFS
(measured ~71-instruction ceiling vs the read/write-mode-flip risk). **Theme 4**:
the three deferred ISO/parser items (`char_conversion/2`, cyclic-term overflow,
parser `\+ (a,b)`) were all already fixed in Phase 10 (chunks 148/149/152);
chunk 322 verified + added direct coverage. **Bonus 323**: a user question about
`(\+)/2` surfaced that `existence_error(procedure, PI)` built `PI` as an atom
`"name/arity"` not the compound `'/'(Name, Arity)` — so a specific catcher never
unified; fixed (operator-form rendering had hidden it; `functor/3` revealed it).
**Theme 2 — embedding leftovers**: `EnginePool` (324, bounded thread-agile engine
reuse) + async/cancellable query API (325/326: `QueryAsync` →
`IAsyncEnumerable<Solution>`, `QueryAll(string, ct)`, cancellation checked at the
GC watermark so the per-goal path stays free — heap-bounded loops uncancellable
by design). The other two Phase-21 items were already done in Phase 22 (modes
ch246, `IEnumerable<T>` non-det foreigns ch244).

**Phase 26 — WAM codegen quality (Blint vs GProlog)** — ✅ **Complete** (tagged `phase-26`; closure summary in [`docs/phase-26-closure.md`](docs/phase-26-closure.md)).

Drove a predicate-by-predicate comparison of our WAM against GNU Prolog's
`pl2wam` on Blint (a real ~2570-line program;
[`docs/wam-vs-gprolog-blint.md`](docs/wam-vs-gprolog-blint.md)). End state: over
the 89 Blint predicates `pl2wam` compiles, Shumway emits **3319 non-index WAM
instructions vs GProlog's 3769 (−12%)** — ahead of or at parity on every shape,
and beating GProlog on arithmetic, clause-prologue fusion and CSE. Nine chunks
(307–315): inline `=/2` (307); one canonical `ClausePipeline` so the
disassembler shows exactly what executes (308 — it had been running only
`DcgTransform`, masking the lowered control-construct helpers and misleading a
multi-session debugging effort); neck-cut chunk-transparency + the Warren
scheduler targeting the post-cut call, which is what actually broke the earlier
chunk-model refinement (309); arithmetic constant folding `A is 1*2` →
`get_integer 2` (310); compact `a_int_*` encoding 29→17 / 21→13 (311); the four
Blint gaps A–D — neck-cut frame elision + direct arg extraction (312), `D`
verified as a non-issue since permanents are heap-allocated (313), inline nested
compound build/match `unify_structure`/`unify_list` (314, **ADR-019** — Blint
`get_list`+`get_structure` −51%), and CSE sharing a repeated head-arg compound
via `unify_value` (315, beats GProlog). The session also refuted the long-held
premise behind the chunk-model arc: GProlog compiles `is`/`=<` as *calls*, so it
does NOT keep cross-arithmetic vars in X registers — our conservative model
already matched it and our inline `a_int_*` beats it (see
`chunk-model-refinement-failed`).

**Phase 25 — Benchmark harness + ADR-017/018 (representation + arithmetic)** — ✅ **Complete** (tagged `phase-25`; state captured in `docs/wam-vs-gprolog-blint.md` and the Phase 26 closure).

Performance phase preceding the codegen work. Deliverables: the Van Roy
multi-engine benchmark harness (`--alloc` deterministic cell metric + hyperfine
cross-engine wall-clock); **ADR-017** inline 2-cell cons / structure
representation (Lis/Str tag inline in the referring slot, no on-heap header) with
cell-based unification; **ADR-018** arithmetic instruction set (`a_eval_*` RPN
over a per-thread eval stack + the fused `a_int_bin`/`a_int_cmp` integer
fast-lane, zero heap, both tiers) replacing the goal-rewriting `$arith2`; the
`compile_mode` prolog flag (release omits per-clause `meta dbg_info`);
argument-register preferencing; and the `shumway-disasm` tool. KEY measurement
discipline established here and carried forward: trust the deterministic
`--alloc` metric; never compare wall-clock against a different-session baseline
(this laptop has ~40% thermal variance — a byte-identical `nreverse` swings that
much between back-to-back runs).

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
| Inline nested compound build — last-arg | ADR-019 |
| Inline nested compound build — non-last (reserve-upfront + write-pointer stack) | ADR-020 |
| Register allocator — REJECTED with survey data (Class-B ceiling 1.5% on real code, unsound) | ADR-021 |
| Embedded native C blocks (`:- c` / `{...}`) — IL lowering to a foreign interop class | ADR-022 (proposed) |
| Dynamic predicates in Tier-1 IL — snapshot + evict-on-mutation | ADR-023 (proposed) |
| Generic Prolog-term interop (reftype tier) — zero-copy TermRef cursor + named intrinsics | ADR-024 (proposed) |
| Body `jump` opcode + inline deterministic if-then-else at Tier-0 | ADR-025 (proposed) |
| Variable-width choice points — REJECTED with measured ceiling (≤1% on max-CP synthetic, below noise; soundness blueprint preserved) | ADR-026 |
| Second-level (sub-argument) indexing — `switch_on_{atom,integer}_sub`, bounded 2-hop path (list head / struct sub-arg / token stream) | ADR-027 |
| Sibling-arg + structure-keyed indexing inside value buckets — nested `BucketSwitch` reusing `switch_on_*_arg` + new `switch_on_structure_sub` | ADR-028 |
| Clause-epilogue peephole fusion — `cut;deallocate_proceed` shipped (Tier-0 dispatch; IL reads `BytecodeUnfused`); `deallocate;execute` + neck-cut variants deferred; `call;cut` non-fusable | ADR-029 |
| Redundant-cut elimination via a determinism fixpoint — intra-module elision SHIPPED (default ON; `DeterminismAnalysis` drops a det-prefix last-clause trailing cut → clean tail call); first-arg indexing det is mode-dependent → excluded; linker whole-program closure deferred | ADR-030 |
| Delayed choice point — Tier-1 CP-free guard commit SHIPPED default ON, tiers A (inline cmp, 2.6×) + B (binding guards, snapshot/restore, ~1.8×) + G (guard CALLS: leaf inlining ~2×; G2 fail-direct multi-clause/self-tail-recursive callees inlined as sequential chains + in-place loop, ~10%; det-builtin/is guards too); lazy CP materialisation under pending wakeups. The clause→ITE fold was a no-op; "det" strengthened to bytecode-level "CP-free" (fail-direct). **INDEXED BUCKETS SHIPPED default ON** (`SHUMWAY_CPFREE_IDXBUCKET=0` off): lazy bucket CP — the node stores the next node's cursor in a per-member `idxnext` local (−1 = tail) and branches to the clause's ONE shared guard block; guard fail = switch on the local; rare paths materialize the CP from it. test/ 724→2,796 accepted, testGen/ 601→3,014 (5×), bundles +9-12%. Plus the latent case-B/G lazy-CP fix (CP saved clobbered arg registers; now patched to entry via `SetTopCpArgRegister`) | ADR-031 |
| Dynamic guard fail-continuation (engine continuation stack) — SOFT-REJECTED (revisable once CpFreeStats over real corpus quantifies the residual) with ceiling analysis: the fail path intrinsically round-trips the engine (callee CPs are real), so the win is only the commit-side push+cut (~30-40ns) on clauses doing substantial work, at the cost of a per-TryBacktrack check + catch/wakeup/GC/cut interplay. The 81.8% "CrossModule" census class resolves at promotion time (whole-program calleeMap) — the shipped static tiers already reach it. Alternatives: raise fail-direct caps, callee cuts, control shapes, true-G3 nesting | ADR-032 |
| Guard continuation stack — ONE shared fail-direct callee copy per IL method + engine int stack of packed (ok,fail) continuation cursors, dispatched via a method-end continuation switch (same-method v1; TryBacktrack untouched — deliberately NOT ADR-032). Prototype SHIPPED opt-in (`SHUMWAY_CPFREE_CONT=1`): runtime parity with duplication, catch/3 rebalances the stack; cross-tail composition SHIPPED (LCO `br` into the target's shared copy, last-or-cut-committed position rule + det folding) | ADR-033 (prototype) |
| Stable-dynamic inlining — SHIPPED default ON (soundness fix + fast path): a rule-bearing dynamic's ADR-023 snapshot may be inlined into caller guards ONLY with a clause-entry staleness test (`Engine.IsDynMutated`) + un-inlined fallback (plain CP + live by-fid call + jump into the shared post-commit body); fact-only dynamics never caller-inlined; DB-mutation builtins never combined with embedded snapshots. Fixed the shipped LUV bug (stale snapshot baked into caller IL — 423/724 of test/'s accepted guards) + two dispatch variants (Call→CallIl hardening of evictable delegates; stale per-query IlByFunctorId slot on mid-query evict). **Empty-dynamic-as-fail: measured (+69/+111% static acceptance) then REJECTED and removed** — in real programs the assert happens, so the steady state is the plain path PLUS a per-entry probe (net runtime cost); the corpus counts were inflated by GX host-interface placeholders (`i_*`) that production links declare as FOREIGN, whose det-ness the guard machinery already derives via `BacktrackableDetector`. Plus the mixed-cycle fix (KEPT): tail back-edges accepted only over pure-tail cycle segments (non-tail-edge count per visiting entry) — entry-point-independent describe | ADR-034 |
| Source-level debugger — SHIPPED (arc closed 2026-07-20): VS 2026 via Concord (NOT AD7/DAP), interpreter-aware: stack filter recomposes Prolog frames from Activation env-chain state; engine-side DebugService (xplat) with pinned-memory channel primary / func-eval secondary; port-based stepping (redo/fail break the frame model); `Break` opcode in the ReservedExtension slot + runtime-toggleable-LCO `debug_lastcall`; `:- disable_debug.` = semi-native predicates (release codegen under debug, collapsed opaque frames, prelude implicit); conditional bps, live-engine eval + bind-into-frame, destructive Watch edit, Set Next Statement (no-replay: trail-everything + port marks, cross-frame + sibling-clause), lazy arm-on-attach; opt-in `vs\` build (Linux untouched); licensing MIT/Apache-2.0/VS-SDK-EULA verified | ADR-035 |
| VS Code debugger frontend — SHIPPED (arc closed, phase-34): DAP served IN-PROCESS by the engine (TCP loopback; external-driver seam on ChannelDebugSession; stop blocks on a semaphore; NO func-eval anywhere); both endpoints (Concord channel + DAP port) in one `--debug` build, single-driver arbitration; zero-JS declarative VS Code extension + `shumway-dap` C# adapter CLI (stdio↔TCP, runInTerminal launch, `--dap-wait` hold-the-door); hand-rolled AOT-safe DAP; Debug Console = Immediate, setVariable, Jump to Cursor, logpoints; `--dap-port` bakeable (`SHUMWAY_DAP_PORT` precedence); consult serialized under the debug-arm gate (arm-vs-consult race); known limits: no mixed-runtime stack in one session (compound-session recipe), no cross-runtime stepping, breakpoints silent during console evals; Linux E2E smoke pending a box | ADR-036 |
| `soft_cut` opcode + inline `( Cond *-> Then ; Else )` — PROPOSED: `*->` was parsed + recognised by every analysis but never lowered to execution (existence_error at runtime); the fix mirrors GProlog's dedicated `soft_cut(y(0))` opcode (capture B AFTER `try_me_else` via `get_level_b`, commit with `soft_cut` not `cut`). `soft_cut` neutralises the single ELSE choice point (a middle CP) by patching its BP to a dead sentinel — no CP frame-layout change; `Cond`'s CPs above it survive (non-determinism preserved). Reuses ADR-025 inline-ITE machinery; runtime meta-call arm + IL describe/emit included. Flag source-lowering alternative rejected on WAM-parity grounds | ADR-037 |
| PSTR design | docs/design/pstr-design.md |
| Debug info | docs/design/debug-info.md |
| Builtins catalog | docs/design/builtins-catalog.md |
| WAM instruction set | docs/design/wam-instruction-set.md |
