# ADR-031: Delayed choice point via clause-to-if-then-else folding

**Status:** **Accepted — phase 1 (CP-free neck-cut guard commit) SHIPPED,
default ON.** The delayed choice point landed as a **Tier-1 codegen recogniser**
(not the AST fold — see Investigation): a non-last chain clause of the shape
`Head :- IntCmpGuard, !, Body.` is emitted WITHOUT its entry
`PushIlChoicePoint`; guard failure is a **direct IL branch** to the next
clause's label, and the commit's cut tears down nothing. **Measured: the
guard-fail recursive hot loop (`loop(N):-N=<0,!. loop(N):-M is N-1,loop(M).`,
30 M iterations, Tier-1 promoted, ABBA min-of-N same session) runs 2.6× faster
(≈133 ns → ≈46 ns per iteration)**; boyer flat (within noise), qsort/tak ≈3%
in favour — no regression anywhere. Full five-project gate green: Embedding
2902 / Compiler 351 / Core 436 / Interpreter 105 / ISO 277.

## Phase 1 implementation (2026-07-09)

- **Recogniser** `IlPredicateCompiler.TryGetCpFreeNeckCutGuard`: the clause
  byte range is `[a_int_cmp | Meta]* ; neck_cut ; …` — a frameless guard of
  **non-binding, non-allocating, register-preserving** integer comparisons
  committing via a neck cut. Those three properties are what make the direct
  fail-branch sound: the guard mutates NO engine state (no bindings → nothing
  to untrail; no allocation → no heap reset; no register writes → the next
  clause sees the entry arguments), so failing into the next clause needs no
  restore at all, and the skipped choice point's job is fully covered.
- **Emission** (both `EmitTryMeElseChainBody` and the region
  `EmitRegionMultiClauseMember` — runtime promotion AND persisted bundles):
  the clause is emitted as three `EmitClauseBody` slices — guard prefix with
  `failLabel := next clause's label`, then `EmitCpFreeGuardCommit`, then the
  post-cut body with the normal fail label. No changes to `EmitClauseBody`
  itself and none to the Engine.
- **The wakeup caveat → the choice point is materialised LAZILY.** The
  standard emit flushes attribute wakeups before a cut, and a failing hook
  must have the clause CP to backtrack into. `EmitCpFreeGuardCommit` checks
  `Engine.HasPendingWakeups` (one field read): fast path (every non-attvar
  program) → just `engine.NeckCut()` (a runtime no-op unless self-tail-loop
  body CPs exist, where it must prune exactly as today); rare path → **push
  the skipped CP here**, then flush + cut exactly as the standard emit. The
  lazy push is state-identical to an entry push because the guard changed
  nothing. This is the "delayed choice point" in its purest form.
- **Gate lever:** `IlPredicateCompiler.CpFreeGuardCommit`
  (`SHUMWAY_CPFREE_GUARD=0` disables) — the A/B was run with the same binary.
- **Tier-0 unchanged** (measured flat, as expected — the CP cost being
  removed is Tier-1's push + `TryBacktrack`/`PopIlChoicePointAndRestore`
  round-trip per guard failure).
- **Case B SHIPPED (2026-07-09) — binding guards (tier B).** The recogniser
  accepts the head-unification / `=/2` op family in the guard prefix
  (`get_atom`/`get_integer`/`get_value_x`/`get_structure`/`get_list`/
  `unify_*`…, plus the register-writing moves `get_variable_x`/`put_value_x`/
  `unify_variable_x` gated on target ≥ arity so the entry argument registers
  survive). These BIND and allocate, so the clause carries a 4-int snapshot:
  entry emits `Engine.BeginIlGuard()` (sets `HB := heapTop`, so every guard
  binding to a pre-existing variable is trailed — closing the untrailed-young-
  binding hole) plus the two trail tops and the heap top into IL locals; guard
  failure lands on a restore stub — `Engine.FailIlGuard` (untrail to the marks,
  heap reset, HB restore, pending-wakeup clear) — before branching to the next
  clause; the commit restores HB via `Engine.CommitIlGuard`. The rare
  pending-wakeups path materialises the lazy CP via
  `Engine.PushIlChoicePointWithMarks`, which overwrites the four restore slots
  with the CLAUSE-ENTRY marks — so a failing attribute hook backtracks into a
  CP that undoes the guard's own bindings (verified by a clpfd test: an
  attvar bound out-of-domain in the guard fails the hook at the commit flush
  and falls to the next clause with the domain intact). Four small Engine
  additions (`BeginIlGuard`/`CommitIlGuard`/`FailIlGuard`/
  `PushIlChoicePointWithMarks`), no invariant changes. **Measured (same
  binary, `SHUMWAY_CPFREE_GUARD` A/B, ABBA min-of-N, Tier-1 promoted): the
  binding-guard recursive loop (`bloop(N):-N=0,!. bloop(N):-M is N-1,
  bloop(M).`, 30 M iterations) runs ≈1.8× faster (min 2112 ms vs 3613 ms);
  qsort min-of-12 1013 vs 1079, nreverse 944 vs 1036 (≈6–9% in favour); boyer/
  tak parity.** Gate green: Embedding 2914 / Compiler 351 / Core 436 /
  Interpreter 105 / ISO 277. Corpus sizing (`--foldcensus` guard classes):
  tier A+B cover the comparison (0.3%) + binding-unify (6.8%) fold-candidate
  guards.
- **Deferred — the real phase 2 is case G (user-call guards, 91.9% of fold
  candidates):** `p(X) :- check(X), !, …` — the guard call's failure propagates
  through the engine's backtracking, so CP-free needs the callee's failure to
  return to the call site (fail-continuation threading in region emit), gated
  on the callee being proven det by ADR-030's `DeterminismAnalysis` (det-set
  plumbing from compile to promotion), and reusing tier B's snapshot/restore.
  Prototype-gated like this phase. Remaining smaller candidates: a_eval
  comparison guards (1 corpus pred); indexed-dispatch bucket chains
  (`try`/`retry` nodes — needs tier B's machinery at the indexed emit sites);
  type-test builtin guards (34 preds — framed `call var/1` clauses needing
  frame teardown + register restore on the fail path).

## Original investigation (the fold that was NOT the answer)

Recognise the multi-clause `p :- Guard, !, Body.  p :- Rest.` shape
and fold it to `p :- (Guard -> Body ; Rest)`, routed to the **CP-free** region-
helper lowering, so a committing guard **never pushes** the clause-selection
choice point the cut would otherwise tear down. Targets hot Tier-1 recursion
(`!, tailCall`).

## Investigation (2026-07-09)

- **Sizing (`--foldcensus`, `ClauseFold` recogniser).** Over the 556-file Arity
  corpus: **3 581 fold candidates = 11.6% of 30 820 predicates** (7 183 clauses),
  of which **3 321 (93%) have trivial var-heads** (fold with a plain variable
  rename, no head-argument threading) and 260 need threading (`max(X,Y,X)`-style
  repeated-var heads). Size is real.
- **The fold-through-existing-machinery is a structural no-op — proven by
  disassembly.** With `EnableInlineIte = false` (the default), MetaTransform
  lowers `(Guard -> Body ; Rest)` to a helper `$disj :- Guard, !, Body. $disj :-
  Rest.` The disassembly of the folded form is:
  - `p/1` → `execute $disj_1` (a 5-byte trampoline), plus
  - `$disj_1/1` → **byte-for-byte identical** to the original multi-clause `p/1`:
    `try_me_else ELSE; allocate_get_level; …; call; cut; …; ELSE: trust_me; …`.

  So routing the fold through the existing helper path just **moves the same
  `try_me_else`+`cut` into a helper and adds a call indirection** — the CP is
  still pushed and torn down. There is **no CP-free region-helper lowering for
  this shape today**; the ADR's premise ("route to the CP-free lowering")
  assumed one exists, but it does not. The inline path (`EnableInlineIte = true`)
  is the one ADR-025 measured *losing* (+17% boyer) precisely because it too
  pushes an arity-0 IL CP.
- **Consequence — the fold is unnecessary; the lever is codegen.** Since the ITE
  helper is byte-identical to the multi-clause form, a CP-free emission for the
  `Guard, !, Body / Rest` shape would apply **directly** to the multi-clause form
  (recognise it in `ClauseCompiler` / the region IL emit and redirect the guard's
  fail label to the else clause instead of emitting `try_me_else` + `cut`). The
  AST-fold step is a red herring; the whole win is the new CP-free codegen, which
  is exactly ADR-025's deferred step-3 follow-up. That is a Tier-1 region-emit
  change (a **major decision** per CLAUDE.md), high-risk (ADR-025's measurement
  warns the naive version regresses), and gated on a back-to-back A/B beating the
  plain `try_me_else`-chain-plus-cut.

**Decision pending:** invest in the CP-free guard-commit codegen recogniser
(the real ADR-031), or defer with this finding recorded. The `ClauseFold`
recogniser + `--foldcensus` sizing tooling are committed regardless.

---

The decisive risk — the inline ITE lowering was measured to *lose* in Tier-1 — is
addressed by mandating the CP-free lowering and validating with a benchmark
before commit.

## Context

For `p :- Guard, !, Body.  p :- Rest.`, WAM pushes the clause-2 choice point at
clause 1's `try_me_else` — **before** `Guard` runs — and the `!` removes it once
`Guard` succeeds. So every committing call pays a `PushChoicePoint` +
`Cut` (Tier-0) or `PushIlChoicePoint` + `engine.Cut` (Tier-1) for a choice point
that never survives. This is the classic *shallow-backtracking* / *delayed choice
point* opportunity.

`!, tailCall` (a cut immediately guarding the final recursive call) is the base
of deterministic recursion in the Arity corpus: **6 626 clauses = 36.2% of all
tail-call clauses, 9.2% of all clauses** (ADR-029 census). It runs hot, so it
promotes to Tier-1 — which is where the CP push/teardown cost actually lands.

This is the complement of ADR-030: there the cut sits in the *last/only* clause
(no clause CP → redundant → elide); here the cut sits in a *non-last* clause and
genuinely commits clause selection — the CP is real, so we must restructure so it
is never pushed rather than remove the cut.

## Decision

Fold the shape at compile time:

```
p(H1) :- Guard, !, Body.
p(H2) :- Rest.
              ⟶       p(A) :- ( Guard' -> Body' ; Rest' ).
p(Hk) :- ...
```

and compile the resulting `(Guard -> Body ; Rest)` through the **CP-free
region-helper** lowering (ADR-025's measured finding: the region lowering of the
helper form is **already CP-free for the deterministic commit**, whereas the
*inline* ITE form pays a real `PushIlChoicePoint` + `Cut`). The guard becomes a
conditional whose failure branches to the else **without** a clause CP ever being
pushed; on guard success the commit is structural (no cut opcode at all).

### Soundness of the fold

`p :- Guard, !, Body.  p :- Rest.` is **semantically** `(Guard -> Body ; Rest)`
**regardless of `Guard`'s determinism**: the `->` provides exactly the
once-commit the `!` gave (commit to `Guard`'s first solution and to this clause),
and on `Guard` failure the `->` backtracks — undoing `Guard`'s bindings — before
running `Rest`, matching the clause-tried-next semantics. So the transform needs
**no** determinism analysis (unlike ADR-030).

Constraints:
- **Head folding.** Clauses whose heads are distinct variable-argument patterns
  (`p(X):-…` / `p(Y):-…`) fold trivially into one head `p(A)` with the args
  threaded into the body. Heads with *different structure*
  (`p(f(X)):-…` / `p(g(Y)):-…`) are already separated by first-argument indexing
  and the cut is not doing cross-clause selection there — such predicates are
  **not** folded (indexing already gives the deterministic dispatch).
- **Shape.** Only the leading `Guard, !, Body` + trailing else(s) shape is folded.
  A `!` deeper in the body, multiple cuts, or a first clause without a leading
  cut-committed guard are left as-is.
- The fold is applied where it strictly helps: a predicate whose clause selection
  is *already* deterministic via indexing gains nothing and is skipped.

## Risk and why it is prototype-gated

ADR-025 stage (d) **measured the inline ITE lowering LOSING in Tier-1** (`boyer`
+17% min wall-clock, interleaved ABBA) precisely because the inline form pushes an
arity-0 IL choice point and pays `PushIlChoicePoint` + `Cut`, while the region
helper is CP-free. So the win of this ADR exists **only** through the CP-free
lowering; naively feeding the fold to the inline path would reproduce that
regression. ADR-025's own follow-up — "teach the IL emit to skip the ITE CP when
the condition is guard-only (fail-label redirection to ELSE)" — is the mechanism
that makes the folded form CP-free at Tier-1.

Therefore this ADR is **prototype-first**: implement the fold, route to (or
build) the guard-only-CP-free lowering, and A/B on a cut-guarded recursive
benchmark **back-to-back same session** ([[wallclock-ab-must-be-back-to-back]])
before committing the default. If the CP-free lowering does not beat the plain
`try_me_else`-chain-plus-cut on Tier-1, the ADR is rejected with the measurement
recorded (the ADR-021 / ADR-026 discipline: measured ceiling, not speculation).

## Corpus impact

`!, tailCall` = 6 626 clauses (ADR-029 census). The **foldable** subset is the
multi-clause form (a `Guard,!,tail` first clause with a following else clause) —
a census refinement (`--census` extension, or a dedicated pass) will size it
before implementation; the single-clause `!, tailCall` cases are ADR-030
territory (redundant-cut elision) not clause-folding.

## Implementation plan (prototype order)

1. A recogniser for the `Guard, !, Body / Rest` multi-clause shape (var-arg heads;
   skip indexing-separated / deep-cut / multi-cut shapes).
2. The fold to `(Guard -> Body ; Rest)` with head-arg threading, feeding the
   existing `MetaTransform` / ADR-025 ITE lowering — but forced onto the **region
   / CP-free** path.
3. The guard-only-CP-skip in the IL emit (ADR-025 follow-up): when the ITE
   condition is a deterministic guard, redirect its fail label to ELSE instead of
   pushing an IL choice point.
4. A/B measurement (Tier-1 promoted, ABBA, min-of-N) on a cut-guarded recursive
   benchmark; deterministic CP-count drop as the structural metric.
5. Decide the default from the measurement.

## Verification

- Semantic differential: the folded predicate yields identical solutions and
  backtracking to the original clause form (including guard-failure → else, and
  guard with multiple solutions committing to the first).
- Deterministic CP-count: the folded form pushes **zero** clause-selection CPs on
  a committing call (the structural proof of the win).
- Tier-1 wall-clock A/B beating the plain chain-plus-cut — the go/no-go.
- Full five-project gate.

## Alternatives considered

- **Delay the CP in the WAM chain directly** (emit the guard, then push the
  clause CP only on guard success): this *is* the ITE lowering expressed in WAM;
  folding to `(->;)` reuses the existing, tested ITE machinery rather than a new
  chain-emit mode.
- **Do nothing at Tier-1, rely on indexing**: where the guard is a first-arg
  test, ADR-028 indexing already gives deterministic dispatch (no CP, cut
  redundant → ADR-030). This ADR targets the residual: guards that are *not*
  first-arg tests (`X > 0`, a call), which indexing cannot discriminate.

## Related

ADR-025 (inline ITE + the CP-free region finding + the guard-only-CP follow-up —
the direct foundation); ADR-030 (redundant-cut elision — the last-clause
counterpart); ADR-028 (indexing handles the first-arg-test guards); ADR-021 /
ADR-026 (measured-ceiling discipline for rejecting/accepting); [[tier1-register-cost-poc]].
