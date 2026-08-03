# Phase 29 — Closure

**Status**: complete.

**Tagged**: `phase-29`.

Phase 29 opened as *Tier-1 IL inlining of RULES over real programs* (the
chunk-364 survey: Blint has 3 fact call sites vs 229 rule sites) and became the
phase where the user's **region compilation** model replaced the duplication
inliner, matured through every member shape, shipped as the **default**, and
dragged a long tail of correctness and runtime work along with it: the dead-region
prune for bundles, the WAM strip, the link-time meta-wrapper unfold, the ISO
`unknown` flag, dynamic-predicate mutation costs, and the ADR-021 verdict that
closed the register-allocator question with data. Sixty chunks (365–424, plus
letter chunks and ADR-021).

| # | Chunks | Theme | What it adds |
|---|--------|-------|--------------|
| 1 | 365–368 | rule inliner (cases 1–2) | single-clause leaf + non-leaf rule inlining with mid-body cut scoping and tail un-tailing — correct but gated; coverage capped by the metaCp-only caller shape |
| 2 | 369–381 | **region compilation** | the redesign that superseded the inliner: each local predicate's body emitted ONCE inside the caller's IL method, intra-region calls a `br`. Discovery → cursor planner → method emit → multi-clause CPs → cut barriers → cross-region trampolines → INDEXED members. Blint 24 regions byte-identical |
| 3 | 382–387 | region measurement + tooling | honest A/B (neutral on parsing-bound Blint); `SHUMWAY_IL_DUMP`; `[region-skip]` diagnostic; member-exclusion path 1 (24→55 regions); `shumway-compile --dump-wam/--dump-il/--regions`; user-guide |
| 4 | 388–394 | dead-region prune (Stage 9) | reachability analysis over br-absorption; linker seed set; fid bridge; region-mode persisted bundles; the APPLIED prune; cost-based root selection (Blint IL bundle 1834→914 KB) |
| 5 | 395–402 | WAM strip arc | strip attempted, reverted, root-caused (analysis↔compile membership divergence + meta-call-by-fid), prune moved into `CompileEntryToIl` over the exact calleeMap, member-entry cursors making every absorbed member fid-resolvable — `--region-prune --strip-wam` sound end-to-end (Blint exe runs, −20% bundle) |
| 6 | 403–410 | tombstones + the 404 lesson | dead-entry skip (no backtrack per tombstone); the in-place unlink (404) that corrupted Blint's unget buffer and its full post-mortem + revert (410); the mini.pl oracle workflow |
| 7 | 405, ADR-021 | register-allocator verdict | the Y-survey quantified Class B at ~1.5% on real code and unsound — **arc rejected with data**; successor candidates ranked |
| 8 | 406–408, 411 | meta-call + LTO | meta-dispatch sizing; `MetaWrapperUnfold` (Blint meta-dispatches 90 370→64); ISO 7.8.8 branch-cut transparency fix (`'$get_cut_barrier'`); `.shmo` clause-terms channel + cross-module unfold (the LTO architecture) |
| 9 | 412–414 | pre-release hygiene | `.shmo`/`.shum` format freeze (exact-version readers, no compat); ALL env-var diagnostics behind `[Conditional("SHUMWAY_DIAG")]` — zero trace in release builds |
| 10 | 415 | superinstructions | unify-family + Y-move run fusion in the interpreter (~24% of Blint dispatches leave the main switch; deterministically equivalent) |
| 11 | 416–417 | meta-call cache + `unknown` | shared per-engine meta-call route cache (both tiers); the ISO `unknown` flag wired through every dispatch point (default `error`); the assertz pre-scan observability bug fixed (catch+assertz now matches SWI) |
| 12 | 418–419 | **regions default ON** | the chunk-418 validation refuted the "CP save/restore" premise and found the real lever — the `(C->T;E)` lowering (2 trampolines + broken self-loop) that regions fix (~2× ITE-recursion, qsort −22%, boyer −15%, corpus output-identical, one-shot neutral). Runtime default ON; bundles region-compile iff pruning; linker prunes by default (`--no-region-prune` to opt out); CLI help rewritten for a general audience |
| 13 | 420–423 | dynamic-mutation costs | dead-chain reclaim by dead count alone (the dead<live gate pinned ~100 tombstones on live-heavy predicates); threshold swept to 4 (Blint opcodes −4.9% deterministic); retract/1 zero-allocation mismatch pre-filter + lazy tail snapshot (−70% alloc); GlobalVarStore atom-id keys; the 404 corruption distilled into a churn regression suite |
| 14 | 424 | region coverage complete | backtrackable builtins + meta-calls INSIDE members via `BuiltinResume` plan cursors (chunk-218/182 markers with region identity) — Blint 0 region-skips; every member shape now absorbs |

## End state

- **Region compilation is the shipped Tier-1 model**: default ON at runtime
  (`SHUMWAY_REGION=0` opts out), bundles region-compile + prune by default,
  every member shape handled (single-clause, chains, indexed, cut,
  cross-region, backtrackable builtins, meta-calls), absorbed members
  fid-resolvable, WAM strippable. Blint: 52 region roots, 0 skips,
  byte-identical through REPL / bundle / `--strip-wam` / `--exe`.
- **Performance**: Blint Tier-0 opcodes 29.85M → 27.78M across the phase;
  call-bound vanroy shapes −9..22% under regions; ITE-recursion ~2×; the
  remaining profile is genuine work (char-list unification, required parser
  nondeterminism).
- **Correctness**: ISO branch-cut transparency; `unknown` flag; catch+assertz
  matches SWI; the 404 unlink fully understood (two distinct defects) and its
  failure shapes pinned by tests; logical-update-view churn suite.
- **Hygiene**: formats frozen pre-release; diagnostics compiled out of normal
  builds; CLI help speaks to users, not to us.

## The lessons the phase kept re-teaching

1. **Validate the real artifact with the real entry point** (the chunk-398
   strip broke only under Blint's `main`, not the test entry).
2. **A control must predate every suspect change**, and internal convergence
   is not correctness — only the external oracle is (the 404/409 bisect).
3. **Check the access pattern before building machinery** (mid-query indexing
   died in ten minutes: the hot predicates use unbound keys).
4. **Distrust nearest-name attribution for appended-code pcs** (twice).
5. **Wall-clock on this machine is drift-dominated**: deterministic counters
   (opcodes, allocs, profile pair tables) are the trustworthy metric;
   interleaved min-of-N both orders is the floor for wall claims.
6. **When a "fix" sits behind a broader catch-all it can be dead code** —
   confirm the new path FIRES (region-emit diag), not just that outputs match.

## Deferred / next

- Region budget tuning + the Stage-7 64KB method guard (perf-only now).
- The general single-clause partial-deduction unfold over the `.shmo`
  clause-terms channel (waits for the multi-module-programs phase).
- Tier-1 CP cost: closed as refuted; superseded by regions.
- Phase 30 (next): **Arity/Prolog32 compatibility, round 2** — driven by the
  `C:\Arity` reference material and real Arity programs.
