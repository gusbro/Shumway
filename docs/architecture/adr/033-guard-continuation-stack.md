# ADR-033: Guard continuation stack (shared fail-direct callee copies)

## Status

Prototype (opt-in: `SHUMWAY_CPFREE_CONT=1`) ([Phase 33](../../history/phase-33-closure.md)).

Includes cross-tail composition; not default-on. Replaces
per-call-site DUPLICATION of fail-direct callees (ADR-031 G/G2/G3) with ONE
shared "optimized copy" per callee per IL method, routed through a small
engine-level continuation stack. NOT a backtracking-model change: unlike the
soft-rejected ADR-032, `TryBacktrack` is untouched — the stack is only pushed
and popped at statically-emitted sites in fail-direct code, which by
construction never enters the engine's choice-point machinery.

## Context

ADR-031's CP-free guard machinery makes a callee's failure a direct IL branch
to the guard's per-site restore stub. Because a region member's shared block
has ONE fail label (return false → `TryBacktrack`), the only static way to
own the fail routing was to DUPLICATE the callee's code at every guard call
site — bounded by caps (≤ 4 clauses / ≤ 512 bytes / 1536 total) and
structurally unable to handle mutual recursion (infinite nesting) or code-size
-heavy callees. The whole-program links of the two real Arity apps show the
residual: `g3:cross-tail` 666/1 859, `g3:cycle`, `g3:inner-caps`, `g3:budget`.

## Decision (first cut — same-method scope)

The CLR has no computed goto across methods, but WITHIN a method the IL
`switch` is exactly that — and the region method is already a cursor-switch.
So, per IL method:

- **One optimized copy per fail-direct callee** (in addition to the normal
  region member block, which keeps serving CP-based callers): every failure
  path branches to a shared `contFail` epilogue; success branches to a shared
  `contOk` epilogue.
- **A guard call site** pushes one packed int — `(okCursor << 16) | failCursor`
  — onto the engine's guard-continuation stack and `br`s to the copy's entry.
  `okCursor` indexes the label just after the site; `failCursor` indexes the
  guard's restore stub (both registered in a per-method continuation label
  table).
- **The epilogues** pop the packed entry and `switch` over the corresponding
  half — a dedicated continuation-dispatch switch emitted at the method end,
  when every continuation label exists.

Because fail-direct code never leaves the method invocation (all failures are
static branches), the guard's IL locals (trail-mark snapshot, saved argument
registers) are STILL LIVE when the fail continuation dispatches — the exact
property whose absence killed the pure-locals design and forced ADR-032
toward engine frames.

### What this unlocks over duplication

- **No per-site code growth**: O(1) copy per callee per method — the caps
  (`FailDirectMaxClauses/MaxBytes/MaxTotalBytes`) stop being load-bearing for
  shared callees, and the caps-raise-needs-IL-switch requirement loses urgency.
- **Mutual recursion and arbitrary non-tail depth**: the continuation stack IS
  a call stack for these callees — `g3:cycle` ceases to be structural.
- **Cross-tail composes for free**: a tail `execute` to another fail-direct
  member is `br` to ITS copy entry, inheriting the caller's continuations
  (that is what continuations do).

### Cross-tail (implemented)

A fail-direct clause ending in `execute <other-fid>` is accepted when the
continuation mode is on and the target itself describes fail-direct:
`FailDirectClause.CrossTailFid/CrossTailDet` carry the target; the terminator
emits `br` to the target's shared copy (`GetOrAddGuardContCopy`), inheriting
the caller's ok/fail continuations — LCO composition, no push. Soundness
mirrors self-tail: a cross-tail clause must be the LAST clause or
cut-committed (`selftail-pos` reject otherwise), because inheriting the fail
continuation forfeits the caller's remaining alternatives. And the target's
multiplicity folds into the caller's det (`FailDirectCalleeIsDet` returns
false when any clause cross-tails a non-det target): committing the caller's
clause selection does NOT commit the target's alternatives, so a multi-
solution target keeps the caller out of mid-guard positions.

**Measured (whole-program links, fresh binaries, 2026-07-10):** test/(te/4)
724 → 733 accepted (tierG2 459 → 468), bundle +0.4%; testGen/(generate/3)
601 → 650 (tierG2 375 → 424), bundle +0.27%. The `g3:cross-tail` sightings
(666 / 1 859) mostly FUNNEL into deeper reject reasons once the target is
actually described — `g3:inner-calls` +299, `g3:inner-dynamic` +227 on test/
— i.e. the typical tail-called target itself calls other predicates or is
dynamic, so it isn't fail-direct either. The conversion yield is the funnel's
bottom, not the sightings count. Raising it means attacking `inner-calls`
(deeper G3 through the shared copies — the stack already permits arbitrary
depth) and `inner-dynamic` (needs the ADR-023 caller-eviction cascade).

### Costs and kept hybrid

- Push + pop ≈ 4 field operations per guard call — the duplication path is a
  pure fall-through, so SMALL det callees stay on duplication (faster); the
  stack targets what duplication cannot reach (large / shared / recursive /
  cross-tail). Selection: `CpFreeGuardContinuations` (env
  `SHUMWAY_CPFREE_CONT`) during the prototype; a size/shape heuristic once
  measured.
- **Soundness surfaces** (part of the prototype, each small): `catch/3` must
  snapshot the continuation-stack top in its frame and restore it on unwind
  (an exception mid-callee leaves stale entries); query setup resets the top.
  The stack holds ints (cursors) — no heap-GC roots.

### Deep G3, first cut (implemented): tail cycles + fresh per-copy budget

The call-stack model has three cost tiers, and the third is deliberately not
built:

1. **Tail calls (self/cross-tail): push nothing** — `br` into the target's
   copy, LCO. Mutual TAIL recursion (the even/odd idiom) is a cross-tail
   CYCLE: the describe's visiting-set rejection is lifted for the `Execute`
   edge under continuations (greatest-fixpoint reading: accept the edge; if
   any participant's own walk fails, its describe rejects and the acceptance
   collapses). Sound with plain per-copy IL locals BECAUSE the cross-tail
   position rule (last-clause-or-cut-committed) forfeits the abandoned
   activation's alternatives — its entry marks are dead when the next
   activation of the same copy overwrites them. The cycle edge's det is
   conservatively FALSE, so a cyclic callee never sits mid-guard.
2. **Acyclic non-tail calls: one packed int per active call.** The cumulative
   `FailDirectMaxTotalBytes` budget is lifted under continuations — each
   callee is ONE shared copy per method, not per-site duplication — replaced
   by a FRESH per-copy budget (the per-callee caps still bound every copy).
   Per-activation state (entry marks, saved registers) stays in the copy's IL
   locals, sound because an acyclic copy graph never re-enters a copy while
   it is active.
3. **Non-tail cycles: NOT built (first cut).** A re-entered copy's IL locals would
   clobber the outer activation's entry marks; a later goal failing in the
   outer alternative would then under-restore. Sound support needs real
   frames (marks + registers pushed per activation). The residual is now
   measured directly (`g3:cycle-nontail`): test/ 14, testGen/ 19 — it does
   not currently justify the frame machinery.

**Multiplicity fix (latent bug of the cross-tail commit):** the
`ClauseCount == 1 → trivially det` shortcuts (recognizer multi-mid rule,
inner rule, cross-tail target det) bypassed `FailDirectCalleeIsDet` — but a
single-clause wrapper that cross-tails a NONDET target inherits its
multiplicity. All three sites now go through `FailDirectCalleeIsDet` (which
follows `CrossTailDet`); pinned by
`Adr033_SingleClauseWrapper_InheritsCrossTailMultiplicity`.

**Measured (whole-program links, 2026-07-10):** accepted UNCHANGED (test/
733, testGen/ 650 under CONT) — the freed deep chains re-block on the true
funnel bottoms: EMPTY dynamics (`g3:inner-dynamic-facts` 2 329 +
`op:EnterDynamic-facts` 1 452 on testGen — assert targets, inherent until an
ADR-023 caller-eviction cascade) and real semantics (`multi-mid` 461,
`nondet-mid` 214). `g3:budget` 40→25 (test/). The value of the deep-G3 first cut is the
CAPABILITY — mutual tail recursion and caps-free deep chains compose
correctly through the copies (proven by the unit suite) — not corpus counts
on these two apps.

## Deferred: cross-method continuations

A callee in a DIFFERENT region needs continuation entries that survive the
method exit: (delegate, cursor) dispatch via the existing resume-marker
machinery PLUS the restore state (the guard's snapshot no longer lives in
reachable locals) — converging toward a mini choice point held outside the
B-chain. Heavier; only worth revisiting if same-method coverage leaves a
measured residual.

## Verification plan

- Semantic differential: the ADR-031 test suite green under
  `CpFreeGuardContinuations` (same answers, same solution counts).
- Exception safety: an ISO error / cancellation thrown mid-callee, caught by
  `catch/3`, leaves the stack balanced (nested catch + retry).
- A/B (ABBA min-of-N, same binary): shared-copy+stack vs duplication on the
  classify / guard-call loops; code-size comparison on the two real Arity app
  bundles.
- Full five-project gate.

## Related

ADR-031 (the CP-free tiers this extends); ADR-032 (soft-rejected engine
continuation frames — the TryBacktrack-integrated variant this deliberately
is not); Phase 29 regions (the method whose cursor switch this reuses).
