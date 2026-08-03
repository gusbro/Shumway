# Phase 18 — Closure

**Status**: complete.

**Tagged**: `phase-18` (this commit).

Phase 18 closes four user-visible issues that Phase 17 (cross-process
persisted Tier-1 IL) surfaced or that the user flagged while testing
end-to-end with Blint. The headline outcome: `shumway-link
--with-compiled-il -o Blint.shum Blint.shmo` now produces a bundle
that loads in a fresh process and runs faster than the equivalent
Tier-0 bundle. The persisted IL is the actual code that executes,
and it produces the correct answer.

## Issues

| # | Symptom | Fix |
|---|---------|-----|
| 1 | Bundle path returned wrong answer for Blint (`false.` instead of `Lint errors found / true.`) | Resolved colaterally by chunk 200 (entry-point promotion forced a `:- public` augment + bytecode recompile that bypassed the broken path) |
| 2 | Linker required `:- public pred/N` for every `--entry` predicate | Chunk 200 — accept local entries, transparent promotion (single match), explicit ambiguity error (multiple matches), `entry_not_found` (zero matches) |
| 3 | Persisted IL produced silent wrong answers for predicates with mixed list-pattern / atom-headed clauses; `instantiation_error` for predicates calling `call/N` or `'$call'/2` | Chunk 201 — `TryDescribeIndexedAtomPredicate` rejects mixed shapes; `IsClauseBodyOpcode` rejects clause bodies holding `call/N` or `'$call'/2` CallBuiltin sites |
| 4 | Tier-1 IL Blint ran 30% slower than Tier-0 (12.7s vs 9.7s) | Chunk 202 — cache `OnDispatch` results by address (was allocating a fresh closure per call); skip `RecordCall` on already-promoted predicates (moot decision, just wasted dict ops) |

## Chunks

- **200** — Linker accepts local entry-point predicates with
  transparent promotion. Recompiles the entry's module source
  through `ShmoCompiler.CompileSource` (full DCG / Meta / Phrase
  transform pipeline) after prepending `:- public pred/N.` —
  `BundleWriter.CompileEntryToBytes` would have
  `NotSupportedException`'d on any DCG rule. Limitation:
  source-stripped (`--release`) bundles can't apply the augment
  (no source to recompile); follow-up.

- **201** — Two `IlPredicateCompiler` recogniser bugs surfaced by
  Phase 17 now that persisted IL actually executes:
    1. `TryDescribeIndexedAtomPredicate` accepted predicates with
       mixed list-pattern + atom-headed clauses but emitted only
       the atom-dispatch — list inputs fell through to fail.
       Reject the shape (`table.Count != ClauseCount`), let
       `TryDescribeSwitchedChain` handle the full var-dispatch
       chain.
    2. `IsClauseBodyOpcode` accepted CallBuiltin to `call/N` and
       `'$call'/2`, which need the bytecode interpreter's
       cut-barrier dispatch (chunks 86, 88) — IL invoked their
       Impl directly and `'$call'/2` threw "must be dispatched
       by the interpreter". Mirror `CanCompileSingleClause`'s
       gate so chain-shaped predicates honour the same rule.

- **202** — Tier-1 dispatch fast path. Cache `OnDispatch` results
  by address (was allocating a fresh wrapper closure per hit;
  hundreds of thousands per Blint run). Skip
  `_jitProfile.RecordCall` on already-promoted predicates (the
  indexing recompile decision is moot once IL is running).
  Per-call cost drops to one dictionary probe + delegate invoke
  on the hot path.

- **203** — This closure + tag.

## Measurements (Blint cross-process, 3-run median)

| Configuration | Time | Correct? |
|---------------|------|----------|
| Direct REPL consult (Tier-0) | 15s | ✓ |
| Bundle Tier-0 | 9.1s | ✓ |
| **Bundle Tier-1 persisted IL** | **8.4s** | ✓ |
| Runtime IL (`SHUMWAY_IL_PROMOTE=32`) | 32s | ✓ |

Pre-Phase-18: bundle Tier-0 worked but Blint had no `:- public main/1`
so it couldn't even be linked. Bundle Tier-1 persisted IL ran fast
(3.3s) but produced the wrong answer (`false.`). After Phase 18,
both bundle paths work end-to-end and the IL path is the fastest
configuration overall.

## Out of scope

- **Source-stripped local-entry support.** `--release` or `--strip`
  drops the per-entry source. Chunk 200's promotion path can't
  apply the `:- public` augment without source. Either rewrite the
  pre-compiled bytecode at link time, or add an engine-level
  alias mechanism. Tracked as a follow-up.

- **The 5 Sigil-verifier "Unreachable code detected" predicates**
  (`$prelude$$listing_all/1` etc.) that
  `PersistedIlBuilder.Build`'s per-pred try/catch already skips.
  Same set the runtime DynamicMethod path rejects.

- **The 3 pre-existing Chunk45 `LoadBundle_PreWarm*` test failures**
  inherited from chunk 192's `IlPromotion.Warm` gating on
  `Threshold > 0`. The tests assume Warm fires unconditionally;
  chunk 192 made that opt-in. Tests should be updated to set
  `Threshold > 0` before asserting promotion happened.
