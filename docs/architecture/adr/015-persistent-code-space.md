# ADR-015: Persistent Code Space and Live Dynamic-Predicate Dispatch

## Status

Shipped ([Phase 8](../../history/phase-8-closure.md)).

Implemented in chunks 114–128 (the closure doc maps them):

- A (114) generation counter, B (115–117) persistent code space,
- C (118) initial recompile-on-modify dispatch — the headline bug fix,
- E (119) amortised program growth,
- C-bytecode-level (120–128) the canonical engine-style dispatch:
  CP-frame ViewGen + new opcodes `enter_dynamic` / `check_visible` +
  born/died stamps + trampoline + `Nop` padding + O(clause)
  asserta/assertz/retract/abolish.

The final landing follows §3 (born/died generation-filtered) and §4
(entry trampolines, incremental chain) — the design originally
sketched and then deferred. The §4.1 recompile-on-modify approach
(chunk 118) shipped first as a stepping stone and is now gone; what
runs in production is the canonical design. Chunk D (the
`PrologQuery : IDisposable` lifecycle for generation pins) stays
dropped — there are no pins to release because retracted clauses are
filtered by `check_visible`, not deferred for physical GC.

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

`retract` is **logical**: it only sets `died := counter`.

> **As built (see phasing D):** there is **no** deferred physical-removal
> GC. The retracted clause's `died` slot is patched in the bytecode in
> place and the next `check_visible` filters it out; the clause's bytecode
> stays put. The running program buffer is append-only, so an in-flight
> backtracking call still sees its own snapshot without any generation pin
> to hold the clause alive. The "erased clause + deferred GC" design below
> was obviated.

### 4. Dynamic-predicate dispatch

Each dynamic predicate has a **stable entry trampoline** at a fixed
address. `assertz` compiles the new clause, appends it to the code space,
and relinks the predicate's `try/retry/trust` chain locally (O(1) — patch
the previous last clause). `retract` sets `died`. Because the *entry*
address never moves, callers' linked `Call`s never go stale.

#### 4.1 Original sketch — recompile-on-modify with an address redirect (SUPERSEDED)

> **Not what shipped.** This recompile-on-modify redirect was the chunk-118
> stepping stone; it landed, fixed the headline bug, and was then **removed
> in full** (chunks 120–128) — `_dynamicRedirects`, `MarkDynamicStale`,
> `ResolveDynamicTarget`, `DynamicRecompiler`, `RecompileDynamicPredicate`
> are all gone from the code. The shipped mechanism is the canonical
> engine-style dispatch — `enter_dynamic` / `check_visible` opcodes plus
> in-place `assertz` / `asserta` / `retract` bytecode patching against a
> stable entry trampoline (`DynamicCodePatcher`). See §4 above and phasing
> chunk C for the as-built design. The sketch is kept here as the recorded
> road not taken.

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

### 6. Query lifecycle — `PrologQuery : IDisposable` (DROPPED)

> **Not built.** This whole lifecycle existed only to release a query's
> *generation pin* so deferred physical `retract` could reclaim a clause.
> The canonical implementation keeps no generation pins and defers no
> physical removal (see §3 and phasing D), so there is nothing to dispose:
> a `PrologQuery : IDisposable` would be an empty-`Dispose` wrapper, and
> `PrologQuery` / `OpenQuery` / the finalizer path do not exist in the code.
> The sketch below is retained as the road not taken.

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

Indexing for a dynamic predicate is maintained **incrementally** on
`assert` / `retract`, so indexing survives across queries (ADR-007). As
built this went further than the first-argument sketch: the dynamic
dispatch is **multi-argument extensible-indexed**
(`PredicateCompiler.CompileIndexedDynamic` — every bucket chain at every
level is extensible and patched in place), and there is a **JIT-indexing**
path (`JitIndexProfile`) where a cold predicate runs a plain
`try_me_else` chain and the first query after a mutation recompiles it
with full multi-arg indexing.

### 8. Code-space compaction — not needed (superseded framing)

> **Superseded (see phasing E).** The original worry was that `retract` /
> `abolish` / re-`consult` leave unreachable bytecode requiring a moving
> compaction / code-GC pass. That was the wrong framing: incremental
> in-place patching (chunks 127–128) does not append superseded bodies in
> the first place, and the per-query buffer is ordinary managed state the
> GC reclaims when the query ends. `compact_dynamic_buffer/0` exists as an
> explicit reclaim hook, but no moving code-GC is required.

## Consequences

- Per-query overhead drops from O(program) to O(query goal).
- The ISO logical update view is honoured; `assertz(d(1)), d(1)` works.
- New machinery (as built): the persistent code space and its cached
  static region, generation bookkeeping on dynamic clauses
  (`born`/`died` + the `enter_dynamic`/`check_visible` opcodes), in-place
  `assertz`/`asserta`/`retract` bytecode patching, and incremental
  multi-arg index updates. (The originally-sketched deferred-`retract` GC
  and query finalizer path were dropped — see §3, §6, phasing D.)
- The single-threaded-engine invariant (ADR-001) is preserved.
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
- **C — Live dynamic dispatch.** ✅ *Done* — implemented in two phases.
  - *First landing (chunk 118)*: a stepping-stone recompile-on-modify
    redirect map. The program buffer grew via `Engine.AppendCode`, the
    interpreter re-read `code` each dispatch-loop iteration, and a
    setup-address → current-address redirect on `Engine` had `assertz`
    / `retract` / `abolish` mark the predicate stale; the next call
    triggered a whole-predicate recompile. Fixed the headline bug; was
    correct but coarse (O(clause-count) per assert, dead bodies in the
    buffer per re-call). **No longer present** — superseded by the
    canonical implementation below.
  - *Final landing (chunks 120–128)*: the canonical engine-style
    dispatch, matching the §3/§4 sketch.
    - **Chunk 120** — choice-point frame gets a `ViewGen` slot
      (ADR-005 updated). `PushChoicePoint` captures
      `Engine.CurrentViewGen`; `RetryMeElse` / `TrustMe` restore it.
      Uniform across all CPs (zero for static predicates).
    - **Chunk 121** — two new opcodes (ADR-006 updated):
      `enter_dynamic` (1 byte) samples `DbGeneration` into
      `CurrentViewGen` at every dynamic predicate's entry;
      `check_visible <born:long> <died:long>` (17 bytes) backtracks if
      the captured view-gen lies outside `[born, died)`. The two
      `LongValue` operands are the encoding.s first 64-bit operands — the
      generation counter needs more than 32 bits for a long-running
      engine. The interpreter reads `DbGeneration` through
      `Engine.DbGenerationProvider` (a `Func<long>`) so
      `Shumway.Interpreter` stays independent of the embedding layer.
    - **Chunk 122** — the compiler emits the new opcodes for dynamic
      predicates: `enter_dynamic` at the entry and `check_visible`
      before each clause body. Single-clause and chain layouts both
      handled; with always-visible sentinel values (`born=0`,
      `died=MaxValue`), behaviour is unchanged.
    - **Chunk 123** — per-clause chain state on `PrologEngine`
      (`_dynChains`). After every query setup the walker pairs each
      `check_visible` opcode it finds with the corresponding clause in
      `_dynamicClauses`. `retract` patches the clause's `died` slot in
      place (`BytecodeIO.WriteInt64`, 8 bytes) — the next
      `check_visible` filters it out.
    - **Chunks 124–125** — fail-stub at offset 10 of the prefix
      (`call_builtin fail/0`, preceded by `trust_me` to pop the chain
      CP — without that, `retry_me_else <fail-stub>` would retain the
      CP and loop forever). The compiler emits `retry_me_else
      <fail-stub>` as the last clause's chain instruction (not
      `trust_me`) so its operand is patchable in place when assertz
      appends a new clause.
    - **Chunk 126** — chain state also tracks each clause's
      chain-instruction `<next>` operand, the 4-byte address an
      assertz will patch.
    - **Chunk 127** — `assertz` is now incremental: compile one
      clause via `ClauseCompiler`, build a chunk (`retry_me_else
      <fail-stub>; check_visible <born=gen> <died=∞>; <body>`),
      append it, patch the previous tail's `<next>` operand in place.
      Per-assert cost: O(clause size).
    - **Chunk 128** — `asserta` is incremental too. The trampoline
      pattern: every dynamic predicate's compiled bytecode begins with
      `enter_dynamic; execute <chain-head>` (6 bytes). Asserta patches
      the trampoline's `execute` operand to install a new head; the
      previous head's `try_me_else` (9 bytes) is demoted in place to
      `retry_me_else <same-next>` (5 bytes) + 4 `Nop` opcodes — same
      9-byte footprint, the address operand at bytes 1–4 stays valid
      (`retry_me_else`'s `<next>` is at the same offset as
      `try_me_else`'s). A new `Opcode.Nop` (0x56, 1 byte, just
      `AdvancePc(1)`) makes the demotion uniform. The chunk-118
      redirect machinery — `_dynamicRedirects`, `MarkDynamicStale`,
      `ResolveDynamicTarget`, `DynamicRecompiler`,
      `RecompileDynamicPredicate`, `MarkDynamicModified` and the
      check in `Call` / `Execute` opcodes — is **all removed**.
      `Engine.RefreshLiteralPoolsCallback` lets the incremental assert
      paths update the interpreter's literal pool snapshot when a new
      clause interns a new literal.

  End state: every dynamic-predicate operation is O(clause size) or
  better — `assertz` and `asserta` compile one clause and patch a few
  bytes; `retract` patches 8 bytes; `abolish` patches 8 bytes per live
  clause. Cut is a normal `cut` opcode (clauses stay compiled).
  Logical update view holds via born/died — an in-progress call's
  captured view-gen is below an assertz/retract's gen, so it sees the
  database as of when its goal began. The trampoline + Nop-padding
  approach lets both directions of insertion stay in-place patchable
  with no opcode-size mismatch and no extra CP overhead at runtime.
- **D — Query lifecycle — obviated, dropped.** D existed to release a
  query's *generation pin* on `Dispose` so deferred physical `retract`
  could reclaim a clause once no live query could still see it. The
  canonical implementation (chunks 120–128) keeps **no generation
  pins** and defers **no** physical removal: a retracted clause has its
  `died` slot patched in the bytecode and the next `check_visible`
  filters it out; the clause's bytecode stays in place (the running
  program is append-only, so an in-progress backtracking call still
  sees its own snapshot). A per-query `Engine` — program buffer,
  choice points, heap — is ordinary managed state the GC reclaims when
  the enumeration ends or is abandoned. There is no pin to release, no
  unmanaged resource, no leak; a `PrologQuery : IDisposable` would
  therefore be an empty-`Dispose` wrapper. Should a future change
  reintroduce deferred physical removal (e.g. a moving code GC), the
  lifecycle returns with it.
- **E — Program-growth cost — addressed (chunk 119, refined by 127–128).**
  Chunk 119 measured the pathological pattern (one query asserting-then-
  calling a dynamic predicate in a loop, forcing a whole-predicate
  recompile each iteration): the chunk-118 redirect's `Engine.AppendCode`
  re-copied the whole growing buffer every append — O(n³) overall
  (n=1500: 11.5 s, 12.7 GB allocated). Capacity doubling made the
  append amortised O(1); n=1500 fell to 3.9 s / 1.5 GB. The residual was
  O(n²), dominated by recompiling the whole predicate on each
  modification. Chunks 127–128 removed even that: `assertz` and
  `asserta` now compile only the new clause, append a chunk of size
  O(clause), and patch the chain in place. The buffer growth per
  modification is O(clause size) — the canonical minimum, not the
  pathological one. A moving "compaction" GC was the wrong framing all
  along: incremental append prevents the superseded bodies rather than
  collecting them, and relocating live code past in-flight choice
  points would be far harder. (Across queries there is no leak
  regardless — the per-query buffer is reclaimed by the GC when the
  query ends.)

## Quick reference

| Decision | See |
|----------|-----|
| Single-threaded engine, global tables | ADR-001 |
| Bytecode encoding (opcode sizes, operands) | ADR-006 |
| First-argument indexing | ADR-007 |
| Module visibility (static vs dynamic) | ADR-008 |
| Embedding API (`Query` / `QueryAll`) | ADR-010 |
