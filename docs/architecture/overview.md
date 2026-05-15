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
| 0xA | ATTVAR   | (reserved, not implemented in v1) |
| 0xB | PSTR     | Partial string (Scryer-style, UTF-16) |

The heap is fully blittable. The .NET GC never scans it for references.

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

**Dynamic predicates** are declared with `:- dynamic foo/N`. They support `assertz`/`retract` at runtime. They are not indexed in v1 (planned for v2).

## Bundle system

A **bundle** is a self-contained, pre-compiled package of Prolog modules. The Shumway bundler tool produces bundles from source files.

**Bundle contents**:

- Bytecode (binary, denselink-able).
- Atom and functor tables (snapshot).
- Auxiliary literals (bigints, strings).
- Module metadata.
- Optional debug info (configurable level).
- Optional pre-compiled IL (`.dll`, in phase 2).

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

- For static predicates.
- Two emission targets:
  - **Runtime**: `DynamicMethod` + Sigil. Promotion happens in a background thread when a predicate is identified as hot (via invocation count threshold).
  - **Build time**: `PersistedAssemblyBuilder`. The bundler can produce a `.dll` that the engine loads at startup. Useful for large programs where waiting for runtime promotion would be too slow.
- Code is **engine-agnostic**: every compiled method takes `Engine` as its first parameter.
- A **global code cache** keyed by bytecode hash allows compiled code to be shared across engines that have loaded the same predicate.

**Tier 1 code does not invalidate** because static predicates are immutable. Dynamic predicates are never compiled to Tier 1.

## Bytecode

The bytecode is a binary format read by both the interpreter and the IL compiler.

- One byte per opcode (0x00–0xFF range).
- Operands are unaligned ints, sizes determined by per-opcode table.
- Opcode 0x00 is reserved as Invalid (detects PC corruption).
- Opcode 0xFE is the Meta opcode (sub-byte distinguishes Meta kinds; v1 has DbgInfo only).
- Opcode 0xFF is reserved for future Extension escape.

The instruction set follows the WAM clearly, with the addition of:
- Consolidations of the most frequent patterns (e.g., `get_constant_a1`, `put_constant_a1`).
- Direct opcodes for hot builtins (`=/2`, `is/2`, comparisons).
- Indexing instructions (`switch_on_term`, `switch_on_atom`, `switch_on_integer`, `switch_on_structure`).
- PSTR-specific instructions (`get_pstr`, `put_pstr`, `unify_pstr`).
- Cut-related instructions (`neck_cut`, `get_level`, `cut`).

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

## Quick map of files to read for specific topics

| To understand... | Read... |
|------------------|---------|
| Engine and global tables | ADR-001 |
| Cell encoding | ADR-002, design/cell-layout-detail.md |
| Atom GC | ADR-003 |
| Trail | ADR-004 |
| Stack and choice points | ADR-005 |
| Bytecode format | ADR-006, design/wam-instruction-set.md |
| Indexing | ADR-007 |
| Modules | ADR-008 |
| Bundler | ADR-009 |
| Embedding API | ADR-010, design/api-reference.md |
| IL compiler | ADR-011 |
| Mode inference | ADR-012 |
| PSTR | design/pstr-design.md |
| Debug info | design/debug-info.md |
| Builtins | design/builtins-catalog.md |
