# ADR-015: Persistent Code Space and Live Dynamic-Predicate Dispatch

## Status

Accepted (Phase 8) — design agreed in review. Implementation is phased
across several chunks and not yet landed; this ADR is the contract those
chunks build to.

## Context

Today a query is compiled whole, every time. `PrologEngine.SetupQueryFromTerm`,
on every `Query` / `QueryAll`:

1. gathers **all** clauses — every module's static clauses, a *snapshot*
   of the runtime dynamic store (`_dynamicClauses`), and a synthetic
   `__query__` clause wrapping the goal;
2. runs the transforms (DCG, meta-call, mode, module-rewrite);
3. compiles them with `ModuleCompiler` into one `byte[]` program;
4. links it — every `Call` / `Execute` operand is patched to the callee's
   absolute address in that program.

Per-predicate *compilation* is cached (`_staticPredicateCache`,
`_dynamicPredicateCache`, `_precompiledClauseCache`), so the compiler is
not re-run for unchanged predicates. But the **assembly + linking** in
steps 3–4 is redone every query — O(program size) — and that is where two
problems live.

**Problem 1 — overhead.** Re-assembling and re-linking the entire program
on every query is wasted work. The bulk of a program is static and never
changes; only the `__query__` clause is genuinely query-specific.

**Problem 2 — the dynamic snapshot is frozen (the headline bug).** Step 1
snapshots `_dynamicClauses`. A direct call to a dynamic predicate runs
that snapshot's compiled bytecode at a link-time-fixed address. So a
modification made *during* the query is invisible to a later goal in the
same query:

```prolog
:- dynamic visto/1.
?- assertz(visto(a)), visto(a).      % ISO: true.  Shumway: false.
```

`assertz` updates the live `_dynamicClauses`, but the running query's
bytecode — and the address its `Call visto/1` was linked to — were frozen
at setup, when `visto/1` was empty. This violates the ISO **logical
update view** (a goal sees the database as of when *that goal* began).

`clause/2`, `retract/1`, `abolish/1` are *not* affected — they are
builtins that consult the live `_dynamicClauses` directly. Only a direct
call to a dynamic predicate (and a `findall/3` over one) sees the snapshot.

Two non-issues were ruled out by earlier Phase-8 work:

- The engine **does** have last-call optimisation (chunk 110): deep tail
  recursion runs in constant control stack.
- The deep-recursion overflow was the list-materialiser recursing per
  element, fixed in chunk 111.

So this is purely the snapshot-vs-live model. A correct fix is an
architecture change, which is why it gets an ADR rather than a patch:
it touches the compilation model, the query lifecycle, and — through the
finalizer path — the threading model (see ADR-001's single-threaded-engine
invariant).

## Decision

Move from per-query whole-program assembly to a **persistent unified code
space**, with **transient queries** layered on top, and make
dynamic-predicate visibility correct via a **generation counter** that
implements the logical update view.

### 1. Persistent code space

The engine owns one growable code area into which every predicate is
compiled and linked **once**. Predicate entry addresses are stable.
Inter-predicate `Call`s are linked within this space and stay valid for
the engine's life. `consult` of *new* predicates appends to it.

### 2. Transient queries

A query no longer re-assembles the program. It compiles only its own
`__query__<Id>` clause (`Id` a globally incrementing integer) as a small
standalone chunk; that chunk's handful of `Call` sites are patched to
addresses in the persistent space. The chunk is discarded when the query
ends. The persistent space is untouched by queries — only `assert` /
`retract` / `consult` modify it.

Per-query cost drops from O(program size) to O(query-goal size).

### 3. Generation counter — the logical update view

A per-engine counter is incremented by every `assertz` / `asserta` /
`retract` / `abolish`. Each dynamic clause carries two generations:
`born` (when asserted) and `died` (when retracted; ∞ while live).

A query captures the counter value `G` at start. A call to a dynamic
predicate iterates only clauses with `born ≤ G < died` — so it sees
exactly the database as of when it began, and a later goal in the same
query, being a *new* goal evaluated against the live store, captures a
*newer* `G` and does see an earlier `assertz`.

`retract` is **logical**: it only sets `died := counter`. **Physical**
removal is deferred until no live query's captured `G` still falls in the
clause's visible range — otherwise an in-flight query would lose a clause
it is entitled to see. (Standard "erased clause" + deferred GC, as in
XSB/SWI.)

### 4. Dynamic-predicate dispatch

Each dynamic predicate has a **stable entry trampoline** at a fixed
address. `assertz` compiles the new clause, appends it to the code space,
and relinks the predicate's `try/retry/trust` chain locally (O(1) — patch
the previous last clause). `retract` sets `died`. Because the *entry*
address never moves, callers' linked `Call`s never go stale.

#### 4.1 Concrete design — recompile-on-modify with an address redirect

After chunk B the program is `prefix | static region | query region`,
and a dynamic predicate is still snapshot-compiled into the per-query
region — correct as of query setup, stale after a mid-query `assertz`.
The implementation scoping settled on this design (leaner than the
two-segment sketch it replaces — no new code-addressing mechanism):

- **Growable program, with slack.** The query program buffer is
  allocated with spare capacity; the engine can append to it. The
  interpreter re-reads `code` from `engine.CurrentProgram` at the top of
  its dispatch loop, so an append (or, on slack exhaustion, a realloc)
  is picked up. Choice-point and `Cp` addresses are offsets — stable
  across an append.
- **Address redirect.** `engine` holds a map *setup-address →
  current-address*, empty for a query that never modifies the database
  (so the common path pays only one emptiness check per call). When
  `assertz` / `retract` / `abolish` touches a dynamic predicate `d/N`
  mid-query, it marks `d`'s setup entry address *stale* in the map —
  O(1), no recompilation yet.
- **Lazy recompile.** A `Call` / `Execute` whose target is redirected to
  the *stale* marker triggers a host recompile: `d/N`'s current clauses
  are compiled (unindexed — so no switch-table-table growth), linked at
  the program's end against the query's symbol map, appended, and the
  redirect updated to the real new address. So a bulk `assertz` loop
  stays O(1) per assert; a predicate is recompiled at most once per
  call that follows a batch of edits.
- **The logical update view falls out.** Old compiled bodies are never
  freed, so a call already backtracking through an old body keeps its
  clause set, while a *new* call redirects to the freshly compiled body
  — a goal sees the database as of when it began.
- **Clauses stay compiled, so cut is free.** A `!` in a dynamic clause
  body is an ordinary `cut` opcode. The rejected alternative — a builtin
  that interprets clause terms — cannot get cut right without
  re-implementing clause dispatch (a `!` reached through `call/1` is
  local to the call, not the predicate).
- **Literal pools.** A clause asserted mid-query may carry a new string
  / float / bigint literal; the interpreter re-reads the literal pools
  from the engine alongside `code`, since the persistent pools (chunk B)
  may have grown. Atoms and inline integers are unaffected (global atom
  table / in-cell payload).

This is the single largest chunk of the ADR — a growable program plus
the redirect/recompile machinery, ~5 interacting pieces with no
green-landable sub-increment — and is best taken as its own focused
effort with incremental build/test cycles.

### 5. Re-consult relink

Re-consulting a module recompiles and relocates its predicates; callers
of a changed predicate must be relinked. A `consult` that only *adds* new
predicates needs no relink. Linking thus moves from per-query to
per-consult — acceptable, as consult is a load-time operation.

### 6. Query lifecycle — `PrologQuery : IDisposable`

A query is a concrete `IDisposable`:

```csharp
using (var q = engine.OpenQuery("p(X)."))
    foreach (var s in q) { ... }
```

`using` gives a deterministic `Dispose` on the engine's thread, which
releases the query's generation pin (so deferred physical `retract` GC
can advance) and frees its transient chunk and choice points.

A query has no single natural "end": `QueryAll` is lazy and a consumer
may abandon the enumeration with choice points still live. The end-of-
query signal is therefore `Dispose` (which `foreach` calls automatically,
including on `break`/exception) or exhaustion.

A **finalizer** is the backstop for a leaked, never-disposed query, via
the standard `Dispose()` / `Dispose(bool)` / `GC.SuppressFinalize`
pattern — **with one mandatory rule**: the finalizer runs on the GC
finalizer thread, not the engine's thread, and the engine is
single-threaded (ADR-001). So:

- `Dispose(true)` (explicit / `using`, on the engine's thread) releases
  the generation pin and frees resources **directly**.
- `Dispose(false)` (finalizer thread) must **not** touch engine state.
  It only enqueues "query `Id` is dead" onto a thread-safe queue; the
  engine drains that queue at a **safe point** (e.g. the start of the
  next query) and releases the pins there — the same safe-point
  discipline the atom GC already uses (ADR-003).

The finalizer is a safety net, not the mechanism: it is non-deterministic,
so a leaked query holds its generation pin until the GC runs. `using` is
the expected path.

### 7. Incremental indexing

First-argument indexing for a dynamic predicate is maintained
**incrementally** on `assert` / `retract`, not recompiled per query —
so indexing learned in one query is reused by the next (ADR-007).

### 8. Code-space compaction — follow-up

`retract` / `abolish` / re-`consult` leave unreachable bytecode in the
persistent space. A compaction / code-GC pass is required for a
long-lived engine but is **deferred to a follow-up chunk**; until it
lands the code space grows monotonically.

## Consequences

- Per-query overhead drops from O(program) to O(query goal).
- The ISO logical update view is honoured; `assertz(d(1)), d(1)` works.
- New machinery: the persistent code space and its lifecycle, generation
  bookkeeping on dynamic clauses, the deferred-`retract` GC, the
  finalizer-enqueue / safe-point-drain path, incremental index updates.
- The single-threaded-engine invariant (ADR-001) is preserved — the
  finalizer never touches engine state.
- It is a large change; it is phased so each chunk lands on a green suite.

## Implementation phasing

Each is its own chunk, landing with the test suite green:

- **A — Generation counter.** ✅ *Done.* The monotonic per-engine counter
  (`PrologEngine.DbGeneration`), bumped by every `assertz` / `asserta` /
  `retract` / `abolish` through the single dynamic-store mutation
  chokepoint. A query captures it at start. The `born`/`died` clause
  stamps and logical (deferred) `retract` are deferred to chunk C, which
  consumes them in the dynamic-dispatch clause iteration — stamping
  clauses before anything reads the stamps would be unconsumed
  speculative state.
- **B — Persistent code space.** ✅ *Done.* Static predicates are linked
  once into a cached region; a query links only its transient region (the
  dynamic snapshot — until chunk C — plus `__query__` and its auxiliaries)
  against it.
  - *Done (chunk 115):* the `Linker` external-symbols path — a predicate
    set can be linked against an already-linked region's functor→address
    map, so its `Call`s into that region are patched to real addresses
    rather than the undefined sentinel.
  - *Done (chunk 116):* persistent literal pools. The string / float /
    bigint pools had to become engine-persistent with stable ids — a pool
    rebuilt per query would not let a cached static region's embedded pool
    ids survive to the next query. `ModuleCompiler.Compile` now takes an
    optional `LiteralPools`; passed pools accumulate (interning dedupes),
    so a literal keeps its id query to query. `null` keeps the original
    fresh-per-module behaviour.
  - *Done (chunk 117):* the `SetupQueryFromTerm` split. The compiled
    predicates are partitioned into the static region and the per-query
    region; the static region links once into `_staticLink` (nulled by
    `ConsultString` / a bundle load), and each query links its region
    against it with external symbols, then assembles `prefix | static |
    query` and merges the two regions' address maps, switch tables and
    `PredicatesByAddress`.
  - *Finding the ADR sketch missed:* a **static predicate may call a
    dynamic one**, whose address is only known per query (it lives in the
    transient region). The `Linker` now reports such sites
    (`LinkResult.UnresolvedSites`); the cached static region's bytecode is
    never mutated, but each query re-patches those sites in the assembled
    `program` once the dynamic addresses are known. (A fully stable
    dynamic entry address — chunk C's trampolines — will later let even
    those sites link once.)
- **C — Live dynamic dispatch.** `born`/`died` stamps on dynamic clauses
  with logical (deferred) `retract`; entry trampolines; `assertz` append +
  local relink; generation-filtered clause iteration. This is the chunk
  that fixes the headline bug.
- **D — Query lifecycle.** `PrologQuery : IDisposable`; finalizer that
  enqueues; safe-point drain releasing generation pins.
- **E — Code-space compaction (follow-up).** Reclaim unreachable bytecode.

## Quick reference

| Decision | See |
|----------|-----|
| Single-threaded engine, global tables | ADR-001 |
| Bytecode encoding (opcode sizes, operands) | ADR-006 |
| First-argument indexing | ADR-007 |
| Module visibility (static vs dynamic) | ADR-008 |
| Embedding API (`Query` / `QueryAll`) | ADR-010 |
