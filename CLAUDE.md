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

**Performance target**: comparable to or better than GNU Prolog in real-world scenarios. On interop-heavy workloads (where the cost of crossing the C# ↔ Prolog boundary matters more than raw Prolog throughput) this is **measured, not aspirational**: the zero-copy path wins 3–180× over P/Invoke-embedded GNU Prolog — [`docs/benchmarks/cross-engine-comparison.md`](docs/benchmarks/cross-engine-comparison.md) is the source of truth for all performance claims; when this file and that one disagree, that one wins.

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

The full catalog lives in [`docs/architecture/invariants.md`](docs/architecture/invariants.md) —
**read it before changing engine internals**. These are hard constraints; if a change requires
breaking one, stop and write/amend an ADR before proceeding. The headline rules:

- Activations (per-query WAM machines) are single-threaded internally and thread-agile; global
  tables are thread-safe and shared.
- The heap is a `Cell[]` of 8-byte blittable values (4-bit tag + payload) — never a managed
  reference inside a cell; managed payloads live in per-activation side tables.
- Atom ids are global, stable ints; the atom GC runs only at safe points.
- One file = one module; statics are immutable; publics are globally unique;
  `:- module/2` is the sole trigger for scoped qualification.
- Opcode 0x00 = Invalid; opcodes stay contiguous (dense jump table); fixed-size encoding.
- Two trails, HB check, young-to-old binding; `assertz`/`retract` are NOT trailed — extra
  backtracking re-runs side effects and is a correctness bug.
- Dynamic predicates execute on Tier 0 (the ADR-023/034 snapshot model is the one sanctioned
  exception); compiled IL is engine-agnostic; promotion swaps are atomic.
- Logical update view: a call sees the database as of when its goal began (ViewGen + born/died).
- Zero build warnings, enforced mechanically.

---

## Repository Layout

```
src/
├── Shumway.Core/           # Engine (Activation), heap, stack, trail, unification
├── Shumway.Compiler/       # Prolog → WAM compilation (parser, lexer, clause pipeline)
├── Shumway.Interpreter/    # Tier 0: WAM bytecode interpreter
├── Shumway.Compiler.Il/    # Tier 1: WAM → IL compilation
├── Shumway.Builtins/       # ISO-conformant builtin implementations
├── Shumway.Embedding/      # Public API for .NET embedding (PrologEngine)
├── Shumway.TopLevel/       # Shared top-level session/formatting (REPL + web)
├── Shumway.Repl/           # `shumway` — the interactive top-level
├── Shumway.Compile/        # `shumway-compile` — .pl → .shmo
├── Shumway.Link/           # `shumway-link` — .shmo → .shum / --exe / --dll
├── Shumway.Lib/            # `shumway-lib` — .shmo archives (ar-style)
├── Shumway.Disasm/         # `shumway-disasm` — WAM disassembler / audit
├── Shumway.Dap/            # `shumway-dap` — VS Code debug adapter
├── Shumway.Web/            # WebShumway (browser-wasm static site)
└── Shumway.SourceGen/      # [PrologTerm] / [PrologPredicate] source generators

tests/                      # The six-project gate (see CONTRIBUTING.md) +
│                           # Benchmarks, Smoke.Net48, conformity/
docs/
├── README.md               # Index of all documentation
├── guide/                  # User-facing guides + compatibility status
├── architecture/
│   ├── overview.md
│   ├── invariants.md       # The consolidated invariant catalog
│   ├── decision-policy.md  # What is a major decision; where decisions live
│   └── adr/                # Architecture Decision Records
├── design/                 # Detailed subsystem designs
├── benchmarks/             # Current cross-engine baselines
└── history/                # Phase closures, audits, past comparisons
```

The NuGet package id is `Shumway` for the main embedding library (not yet
published; the first tagged release will).

---

## Build and Test Commands

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build

# Run all tests
dotnet test

# Routine gate: skip the handful of Category=Slow pins (Donald leftmost labeling,
# the 2500-round tabled fixpoint — ~1 min). Run the FULL suite (no filter) before
# closing a phase.
dotnet test tests/Shumway.Tests.Embedding/ --filter "Category!=Slow"

# FASTER Embedding gate (~2.5 min vs ~13): one build + 4 concurrent test
# PROCESSES over disjoint class-prefix filters. Process-level parallelism is
# safe here by construction (each process gets its own AtomTable/FunctorTable
# statics — the reason in-process xUnit parallelism is disabled). This script
# is the sanctioned exception to the one-`dotnet test`-at-a-time rule.
powershell -File tests/test-embedding-parallel.ps1          # routine (Category!=Slow)
powershell -File tests/test-embedding-parallel.ps1 -Full    # pre-phase-close

# Run ISO conformance suite specifically
dotnet test tests/Shumway.Tests.IsoConformance/

# Run benchmarks
dotnet run -c Release --project tests/Shumway.Tests.Benchmarks/

# Publish the toolchain CLIs
dotnet publish src/Shumway.Compile/ -c Release
dotnet publish src/Shumway.Link/ -c Release

# Run the interactive top-level (REPL); any files listed are consulted at startup
dotnet run --project src/Shumway.Repl/ -- [file.pl ...]

# Publish the REPL as a self-contained Native AOT executable
# (see docs/guide/native-aot.md — Windows needs the Visual C++ build tools)
dotnet publish src/Shumway.Repl/ -r win-x64 -c Release
```

---

## Coding Conventions

- **Zero warnings — invariant.** The build compiles with no warnings, enforced mechanically by `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in the root `Directory.Build.props` (covers `src/` and `tests/`; the `vs/` debugger projects have their own props). A warning fails the build. Fix it at the source; never suppress wholesale. A genuinely unavoidable, understood case is silenced narrowly and locally (a targeted `#pragma warning disable <code>` with a comment, or per-project `NoWarn`), never by relaxing the invariant.
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

The authoritative policy lives in
[`docs/architecture/decision-policy.md`](docs/architecture/decision-policy.md).
In short: a new cell tag, trail-format change, new top-level opcode, atom-GC
strategy change, module-resolution change, backtracking/choice-point model
change, new external dependency, threading-model change, or breaking anything
in `docs/architecture/invariants.md` → **stop and propose an ADR before
implementing**.

---

## Phase Roadmap

Shumway is designed in phases; all of 1–39 are ✅ complete and tagged
(`phase-N`). **The canonical record of each phase is its closure doc in
[`docs/history/`](docs/history/)** — what shipped, the decisions, the gate at
close. This table is the index, one line of essence per phase; do not grow
entries here, grow the closure docs.

| Phase | Theme — essence | Closure |
|---|---|---|
| 1 | Core engine: Tier-0 WAM interpreter + compiler, heap/trail/atom GC, PSTR, modules, embedding API, bundler, Tier-1 IL (DynamicMethod + Sigil), first-arg indexing | [doc](docs/history/phase-1-closure.md) |
| 2 | Production optimizations: multi-arg indexing, dynamic-predicate index cache, compiled `.dll` bundles, IL leaf inlining, lazy PSTR concat | [doc](docs/history/phase-2-closure.md) |
| 3 | Mode inference (consumes `:- mode`), det/semidet specialization, PGO, JIT indexing for dynamics | [doc](docs/history/phase-3-closure.md) |
| 4 | Attributed variables (ATTVAR tag, put/get/del_attr, attr_unify_hook, residual projection) + in-engine meta-call (findall/bagof/setof/catch/call/N in the live engine) | [doc](docs/history/phase-4-closure.md) |
| 5 | Interactive top-level (`src/Shumway.Repl/`); undefined predicates raise catchable existence_error | [doc](docs/history/phase-5-closure.md) |
| 6 | CLP(FD) over sorted interval lists on `verify_attributes/4` (arith constraints, labeling, all_distinct + Hall pruning, reification, scalar_product); also fixed `!` inside runtime compound call | [doc](docs/history/phase-6-closure.md) |
| 7 | Generated predicate docs (`predicates.md` + staleness test), common library predicates, CLP(R) (Gaussian + Fourier–Motzkin, projection), Native AOT (Tier-0), tabling (semi-naive, variant answers, well-founded negation) | [doc](docs/history/phase-7-closure.md) |
| 8 | Engine robustness: iterative materializers (deep lists), `repeat/0`, ISO logical update view via persistent code space + `enter_dynamic`/`check_visible` (ADR-015), O(clause) assertz/asserta | [doc](docs/history/phase-8-closure.md) |
| 9 | ISO error system completed (all IsoError kinds, Name/Arity context) + conformance suite widened to one-file-per-§8-chapter; stream subsystem (StreamHandle/StreamRegistry), §8.11–8.14 builtins | [doc](docs/history/phase-9-closure.md) |
| 10 | Robustness leftovers: richer errors, cyclic-term safety, cut-vs-catch, parser adjacency, clause GC, char_conversion, in-place assertz/asserta/retract for JIT-promoted dynamics (155a–g) | [doc](docs/history/phase-10-closure.md) |
| 11 | Multi-arg in-place extensible indexed dispatch (nested switch chains) + `compact_dynamic_buffer/0` | [doc](docs/history/phase-11-closure.md) |
| 12 | Auto-compaction watermark + explicit Tier-1 exclusion for dynamic predicates | [doc](docs/history/phase-12-closure.md) |
| 13 | Separate compilation: `.shmo` format, `shumway-compile`, `:- ensure_linked`, `ShmoLinker` + `shumway-link`, user guide | [doc](docs/history/phase-13-closure.md) |
| 14 | Toolchain UX: multi-file compile, `--debug/--release`, `--verbose`, parser error recovery, `--strip`, `--map`, `--exe` native executables | [doc](docs/history/phase-14-closure.md) |
| 15 | — number never used | — |
| 16 | Tier-1 threaded dispatch: resume markers replace recursive `RunSubroutine`; O(1) C# stack at any Prolog depth | [doc](docs/history/phase-16-closure.md) |
| 17 | Cross-process persisted IL: name-relative sentinels + patch tables applied in `LoadBundle` | [doc](docs/history/phase-17-closure.md) |
| 18 | Linker accepts local entry points; IL emit fixes; Tier-1 dispatch fast path (no per-hit closure) | [doc](docs/history/phase-18-closure.md) |
| 19 | IL meta-call dispatcher: `call/N` / `'$call'/2` emit as IL with LCO; static `call/N` rewrite | [doc](docs/history/phase-19-closure.md) |
| 19+ | Incremental: `implicit_dynamic` flag; runtime-bound assertz + mid-query trampolines; **chunk 209**: `:- dynamic` predicates with source clauses dispatch from bundles via the DynamicSeeds trailer (TermCodec; `.shmo` V3/`.shum` V4), CollectCalls descends into protected goals, default module naming unified | [doc](docs/history/phase-19-closure.md) |
| 20 | ADR-016 heap GC (sliding mark-compact, conservative scan, `Tag.RawInt`); Tier-1 completeness (deep cut, indexed dispatch, backtrackable builtins); opcode fusion + `CallIl`/`ExecuteIl` linked dispatch; user-IL bundle correctness | [doc](docs/history/phase-20-closure.md) |
| 21 | C# integration (ADR-010): ConsultFile/consult/reconsult, `[PrologPredicate]` foreigns (typed, non-det, prune-aware), 4-tier term conversion + `[PrologTerm]` source generator | [doc](docs/history/phase-21-closure.md) |
| 22 | Foreign-predicate toolchain: mode-aware signatures, `--foreign-dll` through link/load/exe, `ExecuteBuiltin` opcode — compiler emits generic calls, linker materialises | [doc](docs/history/phase-22-closure.md) |
| 23 | REPL editing/history/completion, error rendering with positions, residual-constraint display, `listing/1` + `portray_clause`, zero warnings (~196 → 0), `use_module/1` | [doc](docs/history/phase-23-closure.md) |
| 24 | Arity-Prolog compatibility: snips, save/restore_state, recorded DB, Edinburgh I/O, file ops, random, expand_term, string_term | [doc](docs/history/phase-24-closure.md) |
| 25 | Benchmark harness (Van Roy, `--alloc` deterministic metric) + ADR-017 inline 2-cell compounds + ADR-018 arithmetic instruction set + `shumway-disasm`. Measurement discipline: back-to-back A/B only | [record](docs/history/wam-vs-gprolog-blint.md) |
| 26 | WAM codegen quality vs GProlog on Blint: −12% instructions, inline `=/2`, canonical ClausePipeline, neck-cut transparency, constant folding, ADR-019 inline nested build, CSE | [doc](docs/history/phase-26-closure.md) |
| 27 | `--strip-wam` (IL-only bundles, `IlIndexGraph`), ADR-020 non-last nested inline, EnginePool + async queries; deferred ISO items verified done | [doc](docs/history/phase-27-closure.md) |
| 28 | Real-program corpus (GProlog oracle) + Tier-1 speed arc: lazy Y-slots (~4.6×), fact inliner, native clpfd domain layer | [doc](docs/history/phase-28-closure.md) |
| 29 | Region compilation default ON (body-once IL regions, `(C->T;E)` lowering ~2×), dead-region prune, ADR-021 register allocator REJECTED, link-time MetaWrapperUnfold, `unknown` flag | [doc](docs/history/phase-29-closure.md) |
| 30 | Arity round 2: ADR-022 embedded native C (`:- c`/`{…}` → IL), ADR-023 dynamics in Tier-1 (snapshot + evict), ADR-024 reftype cursor tier, `shumway-lib` librarian, `.shum` archives as linker libraries, float/literal correctness arc | [doc](docs/history/phase-30-closure.md) |
| 31 | REPL wrapping + ESC-cancel (BacktrackSafePoint), `--dll` class libraries, persistent scalar `:- c` globals, five-project gate discipline | [doc](docs/history/phase-31-closure.md) |
| 32 | ADR-024 materializer tier complete: `:- native` P/Invoke + managed snapshots, `t_reftype` graphs (own layout, x86-stable), native allocator mode, out-scalars, char* family, `--native-dll` through `--exe`/`--dll`, process-wide library lifetime | [doc](docs/history/phase-32-closure.md) |
| 33 | Largest phase (138 commits): audit backlog 66/66, ADR-025 inline ITE, ADR-027/028 second-level + bucket indexing, cut/tail-call arc ADR-029–034 (CP-free guard commit default ON incl. indexed buckets; stable-dynamic inlining), Logtalk/Djota real-program rounds | [doc](docs/history/phase-33-closure.md) |
| 34 | Source-level debugger: ADR-035 VS/Concord + ADR-036 VS Code/DAP over one engine debug core; conditional bps, live eval, Set Next Statement, `:- disable_debug` | [doc](docs/history/phase-34-closure.md) |
| 35 | Neumerkel-driven ISO reader/writer conformance, ADR-037 soft cut end to end (+ latent `->` fix), module-local meta-calls (`$mqual`), REPL fresh-line + default Tier-1 | [doc](docs/history/phase-35-closure.md) |
| 36 | Third-party ecosystem (145 commits): ADR-038 library loading + scoped `:- module/2`, ADR-039 rationals, ADR-040 multi-dialect shims, ADR-041 dispatch-time clause selection; SWI/Scryer/Logtalk library campaigns; debugger residual-constraints round | [doc](docs/history/phase-36-closure.md) |
| 37 | Documentation truth (guide/history split, invariants.md, ADR audit), MIT licensing + adapter rewrite, `--consult` incremental toolchain, bundle helper-id collision fixes, `SolveOnce` + first interop benchmark vs GNU (zero-copy wins 3–180×) | [doc](docs/history/phase-37-closure.md) |
| 38 | WebShumway (ADR-042): the full engine on browser-wasm as a static site — Tier-0 via `RuntimeCaps`, threads + COOP/COEP, MEMFS↔OPFS, `Shumway.TopLevel` extraction, libraries as dialect-tagged collections | [doc](docs/history/phase-38-closure.md) |
| 39 | ADR-043 .NET Framework 4.8 opt-in multi-target (net48 + 32-bit, Tier-1 unchanged, persisted IL native on Framework), 3-lane CI, debugger VSIX 0.30 round | [doc](docs/history/phase-39-closure.md) |
| 40 | Version 1.0 + the ecosystem campaigns: ADR-044/045 host-boundary conventions, ADR-046 module-scoped ops, ADR-047 packed-string-is-a-list (default `chars`, lazy `phrase_from_file`), full Neumerkel suites (365/365), Logtalk libraries 100% + ISO battery 3,219/70, Trealla corpus + Triska clpz/clpb certified from their tree, SSU aligned with SWI, WebShumway debug mode, bounded memory (dead-CP reclamation + attr-log hygiene, ~14% faster) | [doc](docs/history/phase-40-closure.md) |


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
| The consolidated invariant catalog | docs/architecture/invariants.md |
| Cell layout (8 bytes, tag + payload) | ADR-002 |
| Atom three-tier system | ADR-003 |
| Two separate trails | ADR-004 |
| Stack layout | ADR-005 |
| Bytecode encoding | ADR-006 |
| First-argument indexing v1 | ADR-007 |
| Module visibility model | ADR-008 |
| Bundler design (CLI retired; format lives on) | ADR-009 |
| Embedding API | ADR-010 |
| IL compiler architecture | ADR-011 |
| Mode inference | ADR-012 |
| BigInt literal opcodes | ADR-013 |
| IL choice points (multi-clause ABI) | ADR-014 |
| Persistent code space & live dynamic dispatch | ADR-015 |
| Heap garbage collection | ADR-016 |
| Inline compound references (2-cell cons) | ADR-017 |
| Arithmetic instruction set (RPN eval stack) | ADR-018 |
| Inline nested compound build — last-arg | ADR-019 |
| Inline nested compound build — non-last | ADR-020 |
| Register allocator — rejected | ADR-021 |
| Embedded native C blocks (`:- c` / `{...}`) | ADR-022 |
| Dynamic predicates in Tier-1 IL (snapshot + evict) | ADR-023 |
| Generic term interop (reftype cursor + materializer) | ADR-024 |
| Body `jump` opcode + inline if-then-else | ADR-025 |
| Variable-width choice points — rejected | ADR-026 |
| Second-level (sub-argument) indexing | ADR-027 |
| Sibling-arg + structure-keyed bucket indexing | ADR-028 |
| Clause-epilogue peephole fusion | ADR-029 |
| Redundant-cut elimination (det fixpoint) | ADR-030 |
| Delayed choice point (CP-free guard commit) | ADR-031 |
| Guard fail-continuation — soft-rejected | ADR-032 |
| Guard continuation stack — prototype (opt-in) | ADR-033 |
| Stable-dynamic inlining | ADR-034 |
| Source-level debugger (VS / Concord) | ADR-035 |
| VS Code debugger frontend (DAP) | ADR-036 |
| Soft cut (`*->`) opcode + inline lowering | ADR-037 |
| Library loading + scoped `:- module/2` | ADR-038 |
| Rational numbers | ADR-039 |
| Multi-dialect library shims, per-module attr hook | ADR-040 |
| Dispatch-time dynamic clause selection | ADR-041 |
| WebShumway — the engine in a browser | ADR-042 |
| .NET Framework 4.8 target (opt-in) | ADR-043 |
| Canonical `/` path separator | ADR-044 |
| Text-mode CR-LF → `\n` translation | ADR-045 |
| Module-scoped operator tables | ADR-046 |
| A packed string is a list | ADR-047 |
| A Prolog character is a code point | ADR-048 |
| PSTR design | docs/design/pstr-design.md |
| Debug info | docs/design/debug-info.md |
| WAM instruction set | docs/design/wam-instruction-set.md |
