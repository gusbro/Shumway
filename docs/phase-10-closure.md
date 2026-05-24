# Phase 10 — Closure

**Status**: complete.

**Tagged**: `phase-10` (this commit).

Phase 10 picked up the engine-robustness leftovers from Phases 8–9
and ran two large architectural threads alongside them: the
**persistent code space** that lets dynamic predicates live in a
buffer reused across queries (chunks 151a–b), and the
**extensible-indexed dispatch** that makes JIT-promoted hot dynamic
predicates honour the ISO logical-update view AND accept in-place
`assertz` / `asserta` / `retract` without rebuilding (chunks 154–
155g).

Two stages:

- **Stage A — user-facing fixes (chunks 144–149).** Each item is a
  bug the user found by running real programs through Shumway:
  richer error payloads, cycle-safe term materialisation, cut
  compaction not invalidating the catch frame's trail snapshot,
  parser `\+ (a, b)` adjacency.

- **Stage B — internals (chunks 150–155g).** Clause GC, persistent
  code space, indexed dispatch with visibility guards, in-place
  chain extension across every common mutation path. The biggest
  thread here is the chunk-155 series — `CompileIndexedDynamic`
  plus a runtime that walks every chain of an indexed predicate
  and patches in place for assertz / asserta / retract on most
  shapes, falling back to chunk-154's rebuild only for cases the
  MVP doesn't yet cover (asserta on the last layout shape; multi-
  arg indexed dynamics).

---

## Deliverables checklist

| Stage / chunk | Deliverable | Status |
|---|---|---|
| A1 (144) | `PrologRuntimeException` carries the offending `Term?` value so `type_error`, `domain_error` etc. include the actual offending term rather than a fresh anonymous variable | ✓ |
| A2 (145, 146) | GProlog / SWI compat predicates (`nb_setval` / `nb_getval`, `get_time`, `name/2`, `read_term_from_atom/3`, `assert/1`); cut-barrier > B no-op fix for `call((G,!,H))` inside meta-call; `TrySplitInfixUnary` op-split parser improvement | ✓ |
| A3 (147) | Cut compaction clips catch-frame trail snapshots so backtracking into the catch handler doesn't re-undo bindings beyond the cut point | ✓ |
| A4 (148) | Cycle-safe term materialiser (general compounds + lists), extending chunk-111's iterative spine walk with a visited-set guard so `X = f(X)` etc. don't overflow | ✓ |
| A5 (149) | Parser `\+ (a, b)` ambiguity — ISO §6.4.7 adjacency rule via `Token.HasLeadingWhitespace`, distinguishing `\+(a, b)` (binary, no leading whitespace) from `\+ (a, b)` (unary applied to parenthesised conjunction) | ✓ |
| B1 (150) | `garbage_collect_clauses/0,1` — clause GC for retracted entries; per-query free-list (`Engine.FreeChunks`) so the next `assertz` / `asserta` reuses the freed bytes | ✓ |
| B2 (151a) | `Shumway.Core.ProgramView` readonly struct — two-buffer view (persistent + per-query overlay) the interpreter dispatch reads through transparently | ✓ |
| B3 (151b) | Persistent dynamic code space: the dynamic region of the linked program lives in a buffer owned by `PrologEngine` across queries; `assertz` / `asserta` extend it in place via `engine.AppendCode`; per-query overlay sits at logical address `persistentLength + 64 MB` so mid-query growth never collides with the overlay's linked offsets | ✓ |
| B4 (152) | ISO §6.4.2 / §8.14.9–10 character conversion — `char_conversion/2` directive + builtin, `current_char_conversion/2` enumerator, lexer integration gated by `PrologFlags.CharConversionEnabled` | ✓ |
| B5 (153) | Verification of indexing-under-visibility-guards: under chunk-151b dynamic predicates always dispatch through the chain (which already emits `check_visible`), so the original concern is moot at this layer. Three tests pin assertz / retract-after-promotion correctness. | ✓ |
| B6 (154) | `CompileIndexed` emits `enter_dynamic` + per-clause `check_visible` for dynamic predicates so JIT-promoted hot dynamics honour the ISO logical-update view in the indexed path. Persistent-buffer rebuild on every mutation to an indexed predicate (and on cold→hot transitions) refreshes the dispatch to current clauses. | ✓ |
| B7 (155a) | New `CompileIndexedDynamic` compilation layout for single-arg indexed dynamic predicates: bucket chains use `try_me_else` / `retry_me_else` (patchable `<next>` operands ending at `fail_stub`), bodies live once and are reached via `execute`. The structural prerequisite for in-place extensibility. | ✓ |
| B8 (155b) | In-place same-key `assertz`: walk the bucket chain + var-fallthrough chain to find tail-next operands, append new body + chain entries at end of buffer, patch the prior tails. No persistent rebuild. | ✓ |
| B9 (155c) | In-place new-bucket-key `assertz`: build a fresh bucket chain (containing every var-arg clause's body + the new clause's body) at end of buffer, replace the sub-switch table with one extending `(key → chain-head)`, mirror into `_dynamicLink` so the next query setup carries the addition forward. `Engine.SwitchTables` is now a mutable `List<SwitchTable>`. | ✓ |
| B10 (155d) | In-place `retract`: walk every chain (every bucket reached through the atom / integer / structure sub-switches + list bucket + var fallthrough), patch the died slot of every chain entry whose `execute` targets the retired body. `FindBodyAddrForClauseIndex` counts only still-alive entries so previous retracts don't shift the index. | ✓ |
| B11 (155e) | In-place var-arg-at-0 `assertz`: extend every chain (var + list + every bucket reachable through the sub-switch tables) with a new entry referencing the shared body. `CollectAllChainTailNextOperands` deduplicates shared chains. | ✓ |
| B12 (155f) | In-place `asserta`: demote each affected chain's head in place (`try_me_else` → `retry_me_else` + 4 nops, same 9-byte footprint, preserving the `<next>` operand), append a new head chunk at the end of the buffer, redirect every pointer slot that referenced the old head — `switch_on_term` operands, sub-switch table entries, sub-switch default-cascade addresses. `ChainEntryHeaderSize` helper distinguishes a 9-byte demoted-head slot from a native 5-byte non-head. | ✓ |
| B13 (155g) | Multi-arg dynamic indexed predicates pin correctness via the chunk-154 rebuild-on-mutate fallback. 5 tests cover assertz / asserta / retract / mixed-mutation patterns across multi-arg dispatch. True in-place extensibility for multi-arg dispatch is deferred — recorded as future work. | ✓ |

---

## What chunk 155 actually means

The chunk-155 series is the largest architectural change in Phase
10. Background:

- Chunk 75 (Phase 3) added JIT indexing for dynamic predicates:
  cold ones use chain dispatch, hot ones recompile indexed.
- Chunk 151b (this phase) introduced the persistent code space:
  the linked dynamic-region bytecode lives across queries.

The combination surfaced a concrete bug: a JIT-promoted hot dynamic
predicate's *cached* CompiledPredicate would be indexed, but the
**live** dispatch in the persistent buffer was the cold-time chain
form. Mutations to the dynamic predicate weren't reaching the
indexed bytecode because the indexed cache was never linked into
persistent.

Chunk 154 made indexed dispatch actually take effect at runtime by
having a cold→hot transition (or any mutation to a hot indexed
predicate) invalidate persistent so the rebuild includes the
freshly-indexed compilation. The compilation also now emits
`enter_dynamic` + `check_visible` per clause so the indexed
dispatch honours the ISO logical-update view through the same
ADR-015 chunk-C mechanism the non-indexed chain path uses.

The user then pushed for true in-place extensibility — making
mutations to a hot indexed predicate NOT trigger a full re-link.
Chunks 155a-f deliver this for the single-arg case by:

1. **Layout (155a):** Replacing the contiguous `try` / `retry` /
   `trust` bucket chains with `try_me_else` / `retry_me_else` chains
   that end at `fail_stub`. Each chain entry references the clause
   body via `execute` so bodies live once and chains are
   independently extensible.

2. **Runtime helpers (155b–f):** A family of `Try*` methods on
   `PrologEngine` that detect the chunk-155a layout, walk the
   relevant chain(s), append new chunks at the end of the buffer
   (via `engine.AppendCode`), and patch in place — same
   chunk-127/128 pattern as the non-indexed chain, generalised
   to handle multiple chains per predicate.

3. **Switch table mutation (155c, 155f):** `Engine.SwitchTables`
   became a mutable `List<SwitchTable>`. The new-key assertz path
   adds entries with `SwitchTable.WithAdditionalEntry`; the asserta
   path redirects values to new head addresses via
   `SwitchTable.WithShiftedAddresses`-style replacement.
   `MirrorSwitchTableIntoDynamicLink` propagates each mutation into
   the cached `_dynamicLink` so the next query setup carries the
   change forward.

4. **Multi-arg fallback (155g):** Multi-arg dynamic predicates keep
   the chunk-154 contiguous layout — extending the chunk-155a model
   to nested `switch_on_arg` levels is a Phase 11 candidate.

---

## What is *not* in Phase 10

- **True in-place multi-arg indexed dispatch for dynamics.** A
  predicate where the compiler infers `indexableArgs.Count > 1`
  uses the chunk-154 layout (contiguous `try` / `retry` / `trust`
  with `switch_on_arg`) and rebuilds on mutation. The chunk-155a
  pattern needs nested switches and a chain-modification helper
  that walks multi-level dispatch.

- **Free-list compaction across realloc.** Chunk 150's free-list
  reuses freed chunks, but the persistent buffer can grow
  unboundedly under heavy mutation patterns. A future GC pass that
  compacts persistent (rebuild-without-dead-bytes) is recorded.

- **Asserta into chunk-128 paso-3 layout.** Asserta on a dynamic
  predicate compiled BEFORE the chunk-127 trampoline pattern
  landed (paso-3 `trust_me` tail) silently no-ops. Reached only by
  predicates compiled in a very specific pre-Phase-8 path; in
  practice the redirect-on-modify (chunk 118) covered the cases
  the engine actually hits.

---

## Test totals at closure

| Suite | Count |
|---|---|
| `Shumway.Tests.Interpreter` | 105 |
| `Shumway.Tests.Compiler` | 242 |
| `Shumway.Tests.IsoConformance` | 275 |
| `Shumway.Tests.Embedding` | 1380 |
| **Total** | **2002** |

All green at the closure tag.

---

## Roll forward to Phase 11

Likely Phase 11 themes:

- **True multi-arg extensible-indexed dispatch.** Extend chunks
  155a-f to handle nested `switch_on_arg` levels, lifting the
  chunk-155g rebuild fallback.

- **Persistent-buffer GC / compaction.** Reclaim bytes left over
  by repeated assertz / retract cycles when the free-list can't
  satisfy a request. Reuses chunk-150's machinery.

- **Sub-engine forking for `findall` etc.** The in-engine
  `findall` is great for single-thread but rebuilding a sub-engine
  view efficiently across many calls is open.
