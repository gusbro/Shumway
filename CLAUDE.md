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
└── Shumway.Builtins/       # ISO-conformant builtin implementations

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

**Phase 1 (v1) — Core functional Prolog with embedding**
- Interpreter (Tier 0).
- WAM compiler (Prolog → bytecode).
- Atom GC, trail, heap, stack, unification.
- PSTR (partial strings) for grammar processing.
- Builtins: subset oriented to grammar processing (~150 builtins).
- Module system with public/local visibility.
- Embedding API.
- Bundler CLI (bytecode bundles).
- IL compiler (Tier 1) with `DynamicMethod` + Sigil.
- `:- mode` directive accepted (parsed, stored as metadata; not used by compiler).
- First-argument indexing for static predicates.

**Phase 2 — Production-grade optimizations**
- Multi-argument indexing.
- Indexing for dynamic predicates (with invalidation on modify).
- Compiled bundles (.dll) via `PersistedAssemblyBuilder`.
- Bundler API for .NET integration (in addition to CLI).
- More aggressive IL inlining.
- PSTR concatenation lazy (instead of eager).

**Phase 3 — Advanced optimizations**
- Mode inference (using `:- mode` directives).
- Specialized code generation per mode.
- Profile-guided optimization (PGO) of IL code.
- JIT indexing.

**Phase 4 — Extended features**
- Attributed variables (attvars).
- CLP(FD), CLP(R) if needed.
- Native AOT support.
- Tabling.

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
| PSTR design | docs/design/pstr-design.md |
| Debug info | docs/design/debug-info.md |
| Builtins catalog | docs/design/builtins-catalog.md |
| WAM instruction set | docs/design/wam-instruction-set.md |
