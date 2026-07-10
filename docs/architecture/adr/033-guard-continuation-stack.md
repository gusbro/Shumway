# ADR-033: Guard continuation stack (shared fail-direct callee copies)

**Status:** Proposed — prototype in progress (user-driven design). Replaces
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

## Decision (v1 — same-method scope)

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
  shared callees, and the caps-raise-needs-IL-switch directive loses urgency.
- **Mutual recursion and arbitrary non-tail depth**: the continuation stack IS
  a call stack for these callees — `g3:cycle` ceases to be structural.
- **Cross-tail composes for free**: a tail `execute` to another fail-direct
  member is `br` to ITS copy entry, inheriting the caller's continuations
  (that is what continuations do).

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

## v2 (deferred): cross-method continuations

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
