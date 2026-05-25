# Phase 12 — Closure

**Status**: complete.

**Tagged**: `phase-12` (this commit).

Phase 12 picked the two items from Phase 11's deferred list that
were ready to land: automatic / per-predicate compaction of the
persistent dynamic-code buffer, and an explicit revisit of how
Tier-1 IL promotion interacts with the chunks-155+/156
mutation-driven dispatch.

Two chunks:

- **Chunk 158 — Auto-compaction watermark + `compact_dynamic_buffer/1`.**
  The persistent buffer now tracks a mutation counter that auto-
  triggers a full compaction at the next query setup once the
  count crosses `PrologEngine.CompactWatermark` (default 1000).
  `compact_dynamic_buffer/1` is the per-predicate API surface — a
  forward-compatibility hint, currently delegating to the same
  full rebuild because the persistent buffer holds every dynamic
  predicate interleaved.

- **Chunk 159 — Explicit Tier-1 IL exclusion for dynamic
  predicates.** Bytecode prefixed with `enter_dynamic` (the
  chunk-155+/156 signature) is now marked unpromotable up-front by
  `IlPromotionStore.IsExcludedByLayout`. Avoids redundant
  `TryDescribe*` attempts on shapes the IL compiler never matched
  anyway, and formalises the architectural invariant: dynamic
  predicates' dispatch is mutation-driven and must stay on Tier 0.
  Static predicates keep their existing IL paths.

---

## Deliverables checklist

| Chunk | Deliverable | Status |
|---|---|---|
| 158a | `PrologEngine._persistentMutationsSinceCompact` counter, bumped by `InvalidateDynamicCache` on every dynamic-store mutation. | ✓ |
| 158b | `PrologEngine.CompactWatermark` property (default 1000). At `SetupQueryFromTerm` start, if the counter has crossed the watermark, the persistent buffer is auto-invalidated — the rebuild that follows in the same setup picks up the trim. Setting to `long.MaxValue` disables auto-compaction. | ✓ |
| 158c | `compact_dynamic_buffer/1` builtin. Takes `+Name/Arity`, validates it (instantiation / type_error / permission_error for static), then delegates to the same full rebuild as the 0-arg form. | ✓ |
| 158d | 9 tests in `Chunk158Tests` pin: counter increments on each mutation; auto-compact fires at threshold; high threshold disables; explicit `/0` resets the counter; `/1` works on dynamic predicates; `/1` raises `permission_error` on static, `instantiation_error` on unbound, `type_error` on non-indicator; in-place mutations resume after an auto-compact. | ✓ |
| 159a | `IlPromotionStore.IsExcludedByLayout(predicate)` — returns true when `predicate.Bytecode[0] == EnterDynamic`. Called by both `RecordInvocation` and `Warm` alongside the existing `IsExcludedFromPromotion` (`__query__/N`) gate. | ✓ |
| 159b | 4 tests in `Chunk159Tests` pin: hot dynamic predicate is unpromotable; static predicate is not unpromotable; dynamic mutations after the IL threshold is crossed still produce correct answers (Tier 0 keeps doing its job); `__query__/N` stays excluded. | ✓ |

---

## What chunk 158 actually means

Phase 11's chunk 157 added `compact_dynamic_buffer/0` as the
manual reclamation hatch. Useful, but the user has to remember to
call it — easy to forget on long-lived engines that accumulate
megabytes of dead bytecode from many `assertz` / `retract` cycles.

Chunk 158 adds the automatic path: every mutation funneled through
`InvalidateDynamicCache` bumps a per-engine counter; at the next
query's `SetupQueryFromTerm` (a safe point — no in-flight choice
points hold addresses into the buffer yet), if the count has
crossed the watermark, the buffer is invalidated and the rebuild
runs as part of the same setup. The counter resets at compaction.

Default watermark is 1000 mutations. Hosts can tune via
`PrologEngine.CompactWatermark`:

- Memory-tight environments lower it (e.g. 100) to keep the
  buffer small at the cost of more re-link work.
- Throughput-tight environments raise it (e.g. 100000) or set
  `long.MaxValue` to disable auto-compaction, manually calling
  `compact_dynamic_buffer/0` between batches instead.

The `/1` form is a forward-compatibility seam: today it validates
the predicate indicator and then falls through to the full rebuild
(every dynamic predicate's bytecode is interleaved in one buffer,
so independent per-predicate reclamation would need partial-relink
support not yet implemented).

---

## What chunk 159 actually means

The chunk-75 JIT indexing + chunk-156 multi-arg in-place dispatch
gives dynamic predicates a layout that the chunk-75 IL promotion
path can't model. Specifically:

- `IlPredicateCompiler.TryDescribeIndexedAtomPredicate` expects
  `SwitchOnTerm` at offset 0; chunk-155+/156 layouts have
  `EnterDynamic` at offset 0 (the var label cascades through
  potentially several `SwitchOnArg` levels before reaching a
  chain head).

- `IlPredicateCompiler.TryDescribeTryMeElseChain` expects
  `TryMeElse` at offset 0 with bodies contiguous after each
  chain instruction; chunk-155+/156 layouts use `Execute` from
  chain entries to shared bodies elsewhere in the buffer, and
  each entry has a `CheckVisible` prefix that the IL compiler
  doesn't model.

- Even if the IL compiler could be extended to handle the
  layout, a cached IL delegate wouldn't see mid-life mutations
  (`retract` patching a clause's died slot, `assertz` appending
  a new chain entry). The dispatch would silently run the stale
  cached compilation.

The existing detectors were already rejecting the layout — they
just walked the bytecode looking for opcodes that weren't there,
then marked the predicate unpromotable. Chunk 159 formalises this:
when `Bytecode[0] == EnterDynamic`, mark unpromotable on the
first invocation, no `TryDescribe*` attempts. Static predicates
(no `enter_dynamic` prefix) keep their IL paths unchanged.

The rejection is the right architectural call: dynamic-predicate
dispatch is mutation-driven (chunks 155b-f / 156's in-place
extension hooks) and must run on Tier 0 where every dispatch
re-reads the chain. Tier 1 IL is a fit for static-predicate
shapes whose bytecode doesn't change.

---

## What is *not* in Phase 12

- **True per-predicate compaction.** `compact_dynamic_buffer/1`
  is currently a hint — it delegates to the full-rebuild path.
  An independent per-predicate variant would need partial-relink
  support: recompile one predicate, append its new bytecode to
  the buffer, retarget the trampoline at the old entry. The
  underlying chunk-118 redirect machinery was abandoned earlier;
  a future phase could revisit.

- **Auto-compaction by byte waste, not mutation count.**
  Mutation count is a rough proxy. A predicate with 100 large-
  body retract cycles wastes far more than 100 small-body
  retracts. A future phase could track actual waste bytes and
  trigger by `(waste / live) > threshold`.

- **IL compilation of cold-chain dynamic predicates.** Before a
  dynamic predicate becomes hot, its dispatch is the chunk-127
  trampoline + chain — still prefixed with `enter_dynamic`, so
  chunk 159 also excludes it. A future phase could add an IL
  shape that handles the chain layout with a call-out per
  `check_visible`, accepting the dispatch-overhead cost in
  exchange for IL bodies.

- **Sub-engine forking for `findall` etc.** Listed as a
  candidate; deferred again. Open candidate for Phase 13.

---

## Test totals at closure

| Suite | Count |
|---|---|
| `Shumway.Tests.Interpreter` | 105 |
| `Shumway.Tests.Compiler` | 242 |
| `Shumway.Tests.IsoConformance` | 275 |
| `Shumway.Tests.Embedding` | 1405 |
| **Total** | **2027** |

All green at the closure tag.

---

## Roll forward to Phase 13+

Open candidates:

- **Sub-engine forking for `findall` / `bagof` / `setof`.**
  Cheaper sub-engine state cloning for high-volume meta-call
  workloads.

- **Per-predicate compaction with partial relink.** True
  `compact_dynamic_buffer/1` that only rebuilds one predicate's
  bytecode.

- **Byte-waste-driven auto-compaction.** Track actual waste
  bytes per mutation and trigger by ratio instead of by count.

- **IL for cold-chain dynamics.** A new IL shape that handles
  the chunk-127 trampoline + chain layout via per-entry
  `check_visible` call-outs.
