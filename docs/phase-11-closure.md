# Phase 11 — Closure

**Status**: complete.

**Tagged**: `phase-11` (this commit).

Phase 11 was the focused follow-up on the Phase 10 deferred items.
Two chunks:

- **Chunk 156 — Multi-arg in-place extensible-indexed dispatch.**
  Lifts the chunk-155g rebuild fallback. Multi-arg dynamic indexed
  predicates now use the chunk-155-style extensible layout at every
  level of switch dispatch, and the runtime chain-modification
  helpers walk multi-level structure via a recursive enumerator.
  assertz / asserta / retract / var-arg-at-0 all run in-place
  instead of rebuilding the predicate.

- **Chunk 157 — `compact_dynamic_buffer/0` builtin.** Exposes the
  existing `InvalidatePersistent` (chunk 151b) as a user-callable
  builtin. Reclaims memory consumed by in-place chain entries and
  clause bodies that became unreachable after a long run of
  assertz / retract cycles, by forcing the next query to rebuild
  the persistent buffer from current `_dynamicClauses`.

---

## Deliverables checklist

| Chunk | Deliverable | Status |
|---|---|---|
| 156a | `CompileIndexedDynamic` generalised to take `perArgInfo` + `indexableArgs` (was arg-0-only). Per-level bucket structure with var-clauses-at-each-level merged into every concrete bucket. Top-level switch is `switch_on_term` for level 0, `switch_on_arg` for subsequent levels. Sub-switches use the `_Arg` variants for non-zero levels. | ✓ |
| 156b | `IsExtensibleIndexedLayout` walks through `switch_on_arg` cascade to recognise multi-arg layouts (chain head can sit several levels deep behind the cascade). | ✓ |
| 156c | `FindFinalVarChainHead` — new helper. `TryAppendToIndexedDynamic` and `TryPrependToIndexedDynamic` use it instead of reading `predAddr+2` directly, so the var-chain extension targets the actual final chain (not the level-1 `switch_on_arg`). | ✓ |
| 156d | `EnumerateChainHeadsRecursive` — replaces the manual cascade-walking loops in `CollectAllChainTailNextOperands`, `CollectAllChainHeadsForRedirect`, and `TryPatchDiedInAllIndexedChains` with a single recursive enumerator that descends through every level switch + sub-switch table. Handles `SwitchOnTerm`, `SwitchOnArg`, the three sub-switch opcodes, their `_Arg` variants, and chain heads (`TryMeElse` + demoted `RetryMeElse` + Nop). | ✓ |
| 156e | `RedirectChainHeads` (chunk 155f asserta) becomes recursive — every switch operand and every switch-table value/default across every level gets the (oldHead → newHead) rewrite. | ✓ |
| 156f | `FindBucketChainHead` / `TryLocateSubSwitchForArg` stop the level-0 cascade at `SwitchOnArg` (the level boundary) instead of returning `-1` on first non-recognised opcode. | ✓ |
| 156g | 7 tests in `Chunk156Tests` pin the multi-arg in-place paths: cached form has the right opcodes (`TryMeElse` + `SwitchOnArg` + `Execute`, no `Try`); same-key assertz; new-key assertz; retract patches all reachable chains; asserta demotes across levels; var-arg-at-0 extends every chain across every level; mixed mutations stay consistent. | ✓ |
| 156h | `Chunk155gTests` updated from "rebuild fallback" pinning to chunk-156 layout pinning (TryMeElse + SwitchOnArg + Execute). | ✓ |
| 157a | `compact_dynamic_buffer/0` builtin registered. `MetaBuiltins.CompactDynamicBuffer` routes through new `PrologEngine.CompactDynamicCodeBuffer()` which delegates to the existing `InvalidatePersistent`. | ✓ |
| 157b | 5 tests in `Chunk157Tests` pin correctness: post-compaction dispatch matches pre-compaction across simple and heavy-churn workloads; further in-place mutations after compact work normally; no-op on fresh engine succeeds; multi-arg dynamic predicates are also handled correctly. | ✓ |

---

## What chunk 156 actually means

Phase 10's chunk 155 series delivered in-place mutation for
single-arg indexed dynamic predicates. Multi-arg (`indexableArgs.Count
> 1`) kept the chunk-154 rebuild fallback as a deliberate scope cut.

Phase 11 chunk 156 lifts that cut by:

1. **Compilation:** The chunk-155a layout already had bodies-via-
   `execute` + extensible chains. Chunk 156 generalises this to
   multi-level switch dispatch — `switch_on_term` for arg 0,
   `switch_on_arg` for arg 1 and beyond, each with its own sub-
   switch tables and bucket chains. Each bucket chain at every
   level uses `try_me_else` / `retry_me_else` with shared bodies.

2. **Runtime helpers:** Every chain-modification helper that
   needed to walk the dispatch graph (find all chain heads, find
   tail-next operands, redirect references, patch died slots) was
   refactored to use a single recursive enumerator
   `EnumerateChainHeadsRecursive`. It descends through every
   switch type and follows every sub-switch table value + default,
   stopping only at chain heads (`TryMeElse`, or `RetryMeElse` +
   Nop padding from a chunk-155f demotion) and unreachable
   addresses.

3. **Cascade boundary:** Within level 0, the const cascade
   (atom → integer → structure) is still bounded; `FindBucketChainHead`
   and `TryLocateSubSwitchForArg` stop at `SwitchOnArg` because
   arg-0's bucket only lives in level 0. The cross-level traversal
   only happens in the helpers that need to touch every chain
   (var-arg-at-0 extension, asserta head demotion, retract died-
   slot patching).

The cascade now correctly handles:
- A clause whose args at all indexable positions are concrete
  (extends only level 0's bucket(arg0_key) + final var chain).
- A clause whose arg at level L is var but earlier ones are
  concrete (only extends level 0 bucket(arg0_key) + final var
  chain — doesn't touch higher-level buckets because dispatch to
  those is via the var cascade, not via this clause's specific
  arg-0).
- A clause with var at all indexable positions (extends every
  bucket at every level + final var chain).
- A clause that introduces a brand-new key at level 0 (creates a
  new bucket chain + extends the switch table).

---

## What chunk 157 actually means

Chunk-155b through 156's in-place mutations are append-only — the
persistent buffer grows monotonically as `engine.AppendCode`
allocates new chain chunks and clause bodies. The chunk-150
free-list reuses chunks released by `garbage_collect_clauses`, but
chunks consumed by in-place mutations that are now unreachable
(e.g. a `retract`'d clause's body, the OLD chain head bytes after
an `asserta` demotion + replacement) accumulate.

Chunk 157's `compact_dynamic_buffer/0` is the explicit reclamation
hatch. Calling it invalidates the persistent buffer; the next
query's setup rebuilds the dynamic region from current
`_dynamicClauses`, producing fresh bytecode with no orphaned
chunks. The cost is one re-link of the dynamic region; the chunk-
155b-f in-place paths then start fresh at append-only growth.

Recommended use: invoke periodically (e.g. after a large mutation
batch, or as part of a per-N-mutations housekeeping callback), not
per-mutation.

---

## What is *not* in Phase 11

- **Automatic / heuristic compaction.** `compact_dynamic_buffer/0`
  is explicit. A future phase could add a watermark (`if buffer
  waste > threshold, auto-compact at next quiescent point`).

- **Per-predicate compaction.** The current builtin invalidates
  the entire dynamic region. A finer-grained version (recompile
  one predicate, leave others alone) would need partial-relink
  support.

- **Sub-engine forking for `findall` etc.** Listed as a Phase 11
  candidate in the Phase-10 closure; deferred again. The in-engine
  `findall` works correctly; the optimisation is about reusing
  state across many sub-calls. Not a correctness gap.

---

## Test totals at closure

| Suite | Count |
|---|---|
| `Shumway.Tests.Interpreter` | 105 |
| `Shumway.Tests.Compiler` | 242 |
| `Shumway.Tests.IsoConformance` | 275 |
| `Shumway.Tests.Embedding` | 1392 |
| **Total** | **2014** |

All green at the closure tag.

---

## Roll forward to Phase 12+

Open candidates:

- **Automatic compaction watermark.** Track persistent-buffer waste
  (bytes appended but unreachable) and trigger compaction
  automatically at safe points.

- **Per-predicate compaction.** A version of
  `compact_dynamic_buffer/1` that only rebuilds one predicate.

- **Sub-engine forking for `findall` etc.** Cheaper sub-engine
  state cloning for high-volume meta-call workloads.

- **JIT-promoted IL compilation** (revisit chunk 75 / Tier-1 paths
  in light of chunk-156's extensible layout).
