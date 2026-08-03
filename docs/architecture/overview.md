# Shumway — Architecture Overview

This document provides the high-level architecture of Shumway, the Prolog implementation for .NET. For specific decisions and their rationale, see the ADRs under `adr/`. For detailed subsystem designs, see `../design/`.

## System purpose and scope

Shumway implements a Prolog compiler and interpreter that runs natively on .NET. It is designed for embedding in .NET applications, with two primary use cases:

1. **Grammar processing**: DCGs and parsing of structured input, with input sizes potentially in the megabytes. Performance of list-of-characters operations is critical here.

2. **Embedded rules engines**: applications that need symbolic reasoning, decision rules, or logic programming as part of a larger .NET system. Frequent crossing of the C# ↔ Prolog boundary is expected.

The performance target is to be **comparable to or better than GNU Prolog** in real-world scenarios. In pure Prolog computation Shumway aims to be within a small factor of GNU Prolog (typically 1–2×); in interop-heavy workloads it is expected to outperform GNU Prolog significantly because Shumway avoids the cost of FFI marshalling.

## Architectural layers

Shumway is organized into seven major components, each with clearly defined responsibilities.

```
┌─────────────────────────────────────────────────────────────┐
│                   Embedding API (.NET public surface)        │
│  PrologEngine, Term, Query, Solution, EnginePool,            │
│  ForeignPredicate, struct↔term mapping (source generators)   │
└─────────────────────────────────────────────────────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                                           ▼
┌──────────────────┐                    ┌──────────────────────┐
│  WAM Compiler    │                    │  IL Compiler         │
│  Prolog → WAM    │                    │  WAM → IL (.NET)     │
│  bytecode        │                    │  Tier 1              │
└──────────────────┘                    └──────────────────────┘
        │                                           │
        ▼                                           │
┌──────────────────────────────────────────────────┐│
│              WAM Bytecode (binary encoding)       ││
│  Shared IR consumed by interpreter and IL compiler││
└──────────────────────────────────────────────────┘│
        │                                           │
        ▼                                           ▼
┌──────────────────┐                    ┌──────────────────────┐
│  Interpreter     │                    │  Compiled IL         │
│  Tier 0          │                    │  (DynamicMethod      │
│                  │                    │   or PersistedAsm)   │
└──────────────────┘                    └──────────────────────┘
        │                                           │
        └─────────────────────┬─────────────────────┘
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                       Engine Core                            │
│  Heap, stack, registers, trails, atom/functor tables,        │
│  unification, dereference, choice points, builtins           │
└─────────────────────────────────────────────────────────────┘
```

## Engine model

The engine is the central runtime unit in Shumway. It encapsulates the complete state needed to execute Prolog: heap, stack, registers, trails, predicate tables, and configuration.

**Key properties**:

- **Single-threaded internally**: no locks in the engine state. The caller serializes access.
- **Thread-agile**: no `[ThreadStatic]`. Engines can be used from any thread (one at a time). Async scenarios work naturally because the engine doesn't pin to a thread.
- **Multiple engines coexist**: each request, worker, or context can have its own engine. Engines are reasonably lightweight to construct.

**Shared global state** (across all engines):

- Atom table (atoms have global ids).
- Functor table (functor ids are global).
- Compiled IL code cache (indexed by bytecode hash; engine-agnostic IL).

These are thread-safe.

**Per-engine state**:

- Heap (`Cell[]`).
- Stack (`Cell[]`) — environments and choice points.
- Trails (binding trail `int[]` and extra trail `struct[]`).
- Registers (X1..Xn, A1..An).
- Predicate tables (one for module-local, one for the engine's view of public predicates).
- Auxiliary tables: bigints, strings, foreign objects.
- TransientWeak atom holds (for atoms retained from C#).

## Cell model

The heap is an array of 8-byte cells. Each cell encodes one Prolog value or a structural element.

```
Cell layout (64 bits):
  bits 63..60: tag (4 bits, 16 possible types)
  bits 59..0:  payload (60 bits)
```

Tags defined in v1:

| Tag | Mnemonic | Payload |
|-----|----------|---------|
| 0x0 | REF      | Heap index (unbound if points to self) |
| 0x1 | STR      | Heap index to FUNCTOR cell |
| 0x2 | LIS      | Heap index to head; head+1 is tail |
| 0x3 | FUNCTOR  | Functor table id |
| 0x4 | ATOM     | Atom id |
| 0x5 | INT      | Signed 60-bit inline integer |
| 0x6 | FLOAT    | 4 high bits + heap index to INT cell with 60 low bits |
| 0x7 | BIGINT   | Id in per-engine bigint table |
| 0x8 | STRING   | Id in per-engine string table (opaque, non-list) |
| 0x9 | FOREIGN  | Id in per-engine foreign object table |
| 0xA | ATTVAR   | Heap index to own home cell (attributed variables — CLP(FD)/CLP(R) build on these) |
| 0xB | PSTR     | Partial string (Scryer-style, UTF-16) |
| 0xC | PSTRBUF  | PSTR buffer cell (4 UTF-16 code units) |
| 0xD | RAWINT   | Non-heap-reference control word (env/CP fields) — lets the conservative heap-GC scan (ADR-016) distinguish control values from Refs |

The heap is fully blittable. The .NET GC never scans it for references. Shumway's
own **heap garbage collector** (ADR-016) — an order-preserving sliding mark-compact
collector with a conservative stack scan — reclaims it at safe points; a
watermark triggers it automatically and `garbage_collect/0` on demand.

## Atom system

Atoms are central to Prolog. Performance and memory behavior of the atom system define how Shumway scales over long-running sessions.

**Three tiers**:

1. **Permanent**: atoms from source code literals and built-ins. Strong references in a global list. Never collected.

2. **Transient**: atoms created at runtime (via `atom_concat`, `read_term`, `atom_codes`, etc.). Strong references in a global dictionary. **Collected by the custom atom GC** when no longer reachable from any engine's state.

3. **TransientWeak**: atoms that survived a custom GC because they were still referenced from C# (via `WeakReference`). No strong reference from the atom table. Promoted back to Transient if reused by an engine.

**Custom atom GC**: runs at safe points (between queries, or when transient table grows past a threshold). Marks atoms reachable from heaps, stacks, registers, predicate metadata, and from `WeakReference`s in the foreign-hold list. Sweep moves unmarked Transients to TransientWeak (or removes them if C# also let go).

The hot path of `put_atom` is just writing the id to a cell. No reference counting, no per-operation bookkeeping.

## Module system

Each Prolog source file is one module. Modules have a simple visibility model inspired by lexically scoped languages:

- **Local by default**: predicates are visible only within their module.
- **Public on declaration**: `:- public foo/N` exports the predicate to a flat global namespace.
- **Public uniqueness**: a `foo/N` declared public in two modules is a linker error.
- **Builtins are implicitly public** and cannot be overridden globally (but library builtins can be shadowed locally).

**Resolution**:

- Local references: resolved at compile time, hardcoded address in bytecode.
- Public references known at compile time: resolved at compile time.
- Public references unknown at compile time: deferred to **lazy linking** when all modules are loaded (or at bundle build time).
- Dynamic predicates: resolved at runtime via the predicate table.

**Static predicates are immutable**. `assertz`/`retract` on a static predicate is an error. This invariant enables aggressive optimization (IL compilation, indexing) without invalidation concerns.

**Dynamic predicates** are declared with `:- dynamic foo/N` (or auto-promoted on
first `assertz` under the default `implicit_dynamic` flag). They support
`assertz`/`retract` at runtime with the ISO logical update view (ADR-015:
persistent code space, per-clause born/died stamps, in-place O(clause) mutation).
They ARE indexed — first-argument and multi-argument switch dispatch with
in-place extensible chains (Phases 10–11) — and they even run as Tier-1 IL via
snapshot-plus-evict-on-mutation (ADR-023) with sound caller inlining (ADR-034).

## Bundle system

A **bundle** is a self-contained, pre-compiled package of Prolog modules. The Shumway bundler tool produces bundles from source files.

**Bundle contents**:

- Bytecode (binary, denselink-able).
- Atom and functor tables (snapshot).
- Auxiliary literals (bigints, strings).
- Module metadata.
- Optional debug info (configurable level).
- Optional persisted, pre-compiled IL (`shumway-link --with-compiled-il`;
  `--strip-wam` additionally drops the then-redundant WAM bodies).
- Optional librarian archive members (`shumway-lib` — the `ar`-style `.shum`
  library the linker pulls from on demand).

**Bundler reachability rules**:

1. Predicates reachable from entry points (via static call graph) are included.
2. **All `:- dynamic` predicates of modules that are included** are also added to the bundle, even if not reachable statically. Their static dependencies are then transitively included.
3. The process iterates until a fixed point is reached.

This dynamic-inclusion rule reflects that `assertz`/`retract` and meta-call typically operate on dynamic predicates that the static analysis cannot trace.

**Validation**:

- Public predicates must be globally unique.
- All references must be resolvable.
- Entry points must exist.

## Compilation strategy

Shumway uses two tiers:

**Tier 0 — WAM bytecode interpreter**:

- Always available.
- Used for all dynamic predicates.
- Used for static predicates until they are promoted.
- Used in builds that disable IL compilation (e.g., Native AOT).

**Tier 1 — IL compilation**:

- Two emission targets:
  - **Runtime**: `DynamicMethod` + Sigil. Promotion happens in a background thread when a predicate is identified as hot (via invocation count threshold).
  - **Build time**: `PersistedAssemblyBuilder`. `shumway-link --with-compiled-il` produces a bundle whose IL loads at startup — no runtime promotion wait.
- **Region compilation is the default** (Phase 29, `docs/design/il-region-compilation.md`): a predicate and its local-predicate closure compile into ONE IL method, intra-region calls becoming branches; the linker prunes now-unreachable standalone bodies.
- Code is **engine-agnostic**: every compiled method takes the activation as a parameter.
- Non-tail calls thread through the dispatcher via resume markers (Phase 16) — the C# stack stays O(1) in Prolog call depth.

**Static Tier-1 code does not invalidate** because static predicates are immutable. **Dynamic predicates also run as Tier-1 IL** via a static-style snapshot that is **evicted on mutation** (ADR-023), preserving the logical update view; a rule-bearing dynamic's snapshot may even inline into caller guards with a clause-entry staleness test (ADR-034).

### Determinism: declared modes vs inferred

Two determinism machineries coexist, deliberately, and they consume different
inputs:

- **Declared modes** (ADR-012) — `:- mode f(+, -) is det.` is *trusted*
  programmer metadata. Its consumers are the mode-aware specializations:
  the det/semidet **implicit cut** at clause end, and gates like the
  assert fast path (`ModeTable.AllModesDeterministic`). A wrong declaration
  produces wrong pruning; that is the contract of a declaration.
- **Inferred determinism** (ADR-029/030/031/033/034) is **mode-independent**
  by design: the redundant-cut fixpoint and the CP-free guard commit derive
  determinism from the compiled code alone (fail-direct bytecode shapes), so
  they are sound with no declarations present. This is also why
  first-argument-indexing-derived determinism is *excluded* from ADR-030's
  fixpoint: whether an index makes a call deterministic depends on the call's
  instantiation — a mode — and the inferred machinery refuses mode-dependent
  facts.

So `:- mode` enables ADR-012's specializations and nothing else; the ADR-029+
optimization arc reads no modes at all. The full rationale — why a declaration
may prune but never license removing a choice point, and where production
engines (Mercury, SWI, Ciao, GNU) draw the same line — is ADR-012's
"trust boundary" section.

## Bytecode

The bytecode is a binary format read by both the interpreter and the IL compiler.

- One byte per opcode, numbered **contiguously** so dispatch compiles to one dense jump table (see `Opcode.cs` for live values — numeric values are deliberately not cited in docs).
- Operands are unaligned ints, sizes determined by per-opcode table.
- Opcode 0x00 is reserved as Invalid (detects PC corruption).
- The `Meta` opcode (sub-byte distinguishes kinds; DbgInfo) and `ReservedExtension` sit at the end of the dense block; new opcodes append there.

The instruction set follows the WAM closely, with the addition of:
- The **arithmetic instruction set** (ADR-018): RPN `a_eval_*` over an eval stack plus the fused integer fast-lane `a_int_bin`/`a_int_cmp` — `is/2` and comparisons compile inline, zero heap.
- Inline nested compound build/match (ADR-019/020: `unify_structure`/`unify_list`, reserve-upfront non-last args).
- Indexing: `switch_on_term`/`switch_on_atom`/`switch_on_integer`/`switch_on_structure`, multi-argument `switch_on_*_arg`, and second-level sub-argument / structure-keyed variants (ADR-027/028).
- Dynamic dispatch: `enter_dynamic`/`check_visible` (the ADR-015 logical update view).
- Fusions: clause-prologue and epilogue superinstructions (`allocate_get_level`, `deallocate_proceed`, `cut;deallocate_proceed`, ADR-029), body `jump` for inline if-then-else (ADR-025).
- Tiered dispatch baked at link time: `call_il`/`execute_il`/`call_bytecode`/`execute_bytecode`, `call_builtin`/`execute_builtin`.
- Cut-related instructions (`neck_cut`, `get_level`, `cut`).
- Debugger support (ADR-035, emitted only under `compile_mode=debug`): `Break` in the reserved-extension slot, `debug_lastcall` (runtime-toggleable LCO), `debug_port`.

Debug information is stored in a side table, indexed from a Meta opcode operand. The compiler emits debug info at configurable levels (None / Basic / Full).

## Trail and backtracking

Shumway uses two trails for performance:

**BindingTrail** (`int[]`):
- Each entry is a heap index of a variable that was bound.
- Hot path: 4 bytes per entry, no allocation, no dispatch on type.
- Unwind: write `Cell.UnboundVar(idx)` to `_heap[idx]`.

**ExtraTrail** (`struct[]`):
- For non-binding reversible state (value changes, future attvar mods, mutable globals).
- Each entry carries type + heap index + old value + a marker into BindingTrail for ordering.
- Used much less frequently than BindingTrail.

**Choice points snapshot both tops** plus heap top, HB, and saved registers.

**Cut performs trail compaction**: entries beyond the new top that bind variables outside the parent's heap region are discarded. This keeps the trail compact even in cut-heavy code.

## PSTR — partial strings for grammar processing

PSTRs solve the fundamental performance problem of representing strings as `[H|T]` cons cells. A 1 MB input would otherwise require 2 million cells.

**Layout**:
- Header cell with tag PSTR, encoding length in code units and a buffer pointer.
- Buffer cells encode 4 UTF-16 code units each.
- Tail cell at the end: `[]` (complete), a variable (partial), another PSTR (lazy concatenation), or cons cells (after fallback).

**Decomposition is lazy**: `[H|T] = pstr("hello")` does not allocate cons cells. `T` is another PSTR header pointing to the buffer with an incremented offset.

**Default `double_quotes` is `codes`** (ISO standard). PSTRs are not created from source literals by default; they appear when reading from streams, from C# strings via the embedding API, or via explicit conversion builtins. This default can be changed per module with `:- set_prolog_flag(double_quotes, pstr)`.

**The choice of UTF-16** aligns with .NET strings. Conversion to/from C# `string` is essentially a memory copy. Surrogate pairs are handled correctly at decomposition time.

## Embedding API

The public API is the surface exposed to .NET application developers. It is shaped around the typical patterns of embedding:

- **Engine lifecycle**: construct, configure, dispose.
- **Source loading**: `Consult(path)`, `LoadBundle(path)`.
- **Term construction**: `MakeAtom`, `MakeInt`, `MakeCompound`, `ParseTerm`, builder for batch construction.
- **Term inspection**: `Kind`, `IsAtom`, `AsAtom`, `GetArg`, `EnumerateList`, `TryGet*` patterns.
- **Query execution**: `Query(text)` or `Query(term)` returns a `Query` object that yields `Solution`s.
- **Foreign predicates**: register C# delegates as Prolog predicates. Supports deterministic and non-deterministic patterns.
- **Struct ↔ term mapping**: source generators produce zero-overhead converters for types annotated with `[PrologTerm]`.
- **Engine pool**: utility for server scenarios (one engine per request from a pool).
- **Async API**: `IAsyncEnumerable<Solution>` with cancellation via safe points.

**Threading model from the client perspective**: engines are not thread-safe, but they are thread-agile. The client should rent an engine from a pool (or otherwise serialize access) per logical operation. Across operations, the engine may be on a different thread.

## Subsystems with their own living documentation

Shipped subsystems this overview does not detail (each has its own doc):

- **Source-level debugger** — Visual Studio 2026 (Concord) and VS Code (DAP),
  breakpoints/stepping/eval/Set-Next-Statement over the interpreter:
  `../guide/debugger.md`, `../guide/debugger-vscode.md`, ADR-035/036.
- **Embedded native C** (`:- c` prototypes + `{...}` blocks compiled to IL) and
  **generic term interop** (reftype cursors, the Arity materializer tier,
  `:- native` P/Invoke): `../guide/embedded-native-c.md`,
  `../guide/generic-term-interop.md`, ADR-022/024.
- **CLP(FD) and CLP(R)** — opt-in constraint libraries over attributed
  variables (`engine.UseClpfd()` / `UseClpr()`).
- **Tabling** — `:- table p/N`, semi-naive evaluation, well-founded negation.
- **Separate compilation** — `.pl → .shmo → .shum` via `shumway-compile` /
  `shumway-link` / `shumway-lib`, `--exe` / `--dll` emitters: `../guide/user-guide.md`.

## Quick map of files to read for specific topics

| To understand... | Read... |
|------------------|---------|
| Engine and global tables | ADR-001 |
| Cell encoding | ADR-002, design/cell-layout-detail.md |
| Atom GC | ADR-003 |
| Heap GC | ADR-016 |
| Trail | ADR-004 |
| Stack and choice points | ADR-005 |
| Bytecode format | ADR-006, design/wam-instruction-set.md |
| Indexing | ADR-007 (first-arg), ADR-027/028 (second-level + bucket) |
| Inline compounds / arithmetic | ADR-017 / ADR-018 |
| Modules | ADR-008 |
| Bundles / linker | ADR-009, ../guide/user-guide.md |
| Embedding API | ADR-010, design/api-reference.md |
| IL compiler | ADR-011, design/il-region-compilation.md |
| Dynamic predicates in IL | ADR-023, ADR-034 |
| Cut / CP-free guard commit | ADR-029..031, ADR-033 |
| Debugger | ADR-035 (VS), ADR-036 (VS Code) |
| PSTR | design/pstr-design.md |
| Builtins | [../guide/predicates.md](../guide/predicates.md) (generated, current) |

What counts as a major decision, and how decisions are recorded, is defined in
[`decision-policy.md`](decision-policy.md). The decisions themselves —
ADR-001 through ADR-041 — live under [`adr/`](adr/), each with a current
Status line; the maintainers' working decision → ADR table is in the
repository-root CLAUDE.md.

## Phase chronology

Development proceeded in numbered phases; ADR Status lines cite them
("implemented (Phase 25)"). Each phase has a closure summary under
[`../history/`](../history/) recording what shipped and the test gate at close.

| Phase | Closed | Theme |
|-------|--------|-------|
| [1](../history/phase-1-closure.md) | 2026-05-19 | Core engine, WAM compiler, embedding API |
| [2](../history/phase-2-closure.md) | 2026-05-19 | Production optimizations (indexing, bundles, inlining) |
| [3](../history/phase-3-closure.md) | 2026-05-20 | Mode inference, PGO, JIT indexing |
| [4](../history/phase-4-closure.md) | 2026-05-20 | Attributed variables, in-engine meta-call |
| [5](../history/phase-5-closure.md) | 2026-05-20 | Interactive top-level (REPL) |
| [6](../history/phase-6-closure.md) | 2026-05-21 | CLP(FD) |
| [7](../history/phase-7-closure.md) | 2026-05-22 | Predicate docs, CLP(R), Native AOT, tabling |
| [8](../history/phase-8-closure.md) | 2026-05-23 | Engine robustness, persistent code space |
| [9](../history/phase-9-closure.md) | 2026-05-24 | ISO conformance + error system |
| [10](../history/phase-10-closure.md) | 2026-05-24 | Robustness leftovers, in-place dynamic dispatch |
| [11](../history/phase-11-closure.md) | 2026-05-24 | Multi-arg in-place indexing |
| [12](../history/phase-12-closure.md) | 2026-05-24 | Auto-compaction, Tier-1 exclusions |
| [13](../history/phase-13-closure.md) | 2026-05-25 | Separate compilation + linker |
| [14](../history/phase-14-closure.md) | 2026-05-25 | Toolchain UX, `--exe` |
| 15 | — | Number never used |
| [16](../history/phase-16-closure.md) | 2026-05-26 | Tier-1 threaded dispatch (O(1) C# stack) |
| [17](../history/phase-17-closure.md) | 2026-05-27 | Cross-process persisted IL |
| [18](../history/phase-18-closure.md) | 2026-05-27 | Bundle ergonomics, IL fixes |
| [19](../history/phase-19-closure.md) | 2026-05-27 | IL meta-call dispatcher |
| [20](../history/phase-20-closure.md) | 2026-05-30 | Heap GC, Tier-1 completeness, dispatch perf |
| [21](../history/phase-21-closure.md) | 2026-05-31 | C# integration (typed conversion, foreigns) |
| [22](../history/phase-22-closure.md) | 2026-05-31 | Foreign-predicate toolchain |
| [23](../history/phase-23-closure.md) | 2026-06-01 | REPL UX, residual display, zero warnings |
| [24](../history/phase-24-closure.md) | 2026-06-01 | Arity-Prolog compatibility |
| 25 | 2026-06-03 | Benchmark harness, ADR-017/018 (no closure doc; record in [wam-vs-gprolog-blint.md](../history/wam-vs-gprolog-blint.md)) |
| [26](../history/phase-26-closure.md) | 2026-06-04 | WAM codegen quality vs GProlog |
| [27](../history/phase-27-closure.md) | 2026-06-05 | `--strip-wam`, non-last inline, cleanup |
| [28](../history/phase-28-closure.md) | 2026-06-08 | Real-program corpus, Tier-1 speed |
| [29](../history/phase-29-closure.md) | 2026-06-11 | Region compilation (default ON) |
| [30](../history/phase-30-closure.md) | 2026-06-26 | Arity round 2, native C, reftype interop |
| [31](../history/phase-31-closure.md) | 2026-06-29 | REPL editing, `--dll`, native-interop correctness |
| [32](../history/phase-32-closure.md) | 2026-06-30 | Materializer tier (ADR-024 completion) |
| [33](../history/phase-33-closure.md) | 2026-07-10 | Audit remediation, cut/tail-call arc (ADR-029..034) |
| [34](../history/phase-34-closure.md) | 2026-07-20 | Source-level debugger (VS + VS Code) |
| [35](../history/phase-35-closure.md) | 2026-07-25 | ISO conformance (Neumerkel), soft cut |
| [36](../history/phase-36-closure.md) | 2026-08-02 | Third-party ecosystem (libraries, dialects, ADR-041) |
