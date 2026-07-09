# ADR-030: Redundant-cut elimination via a whole-program determinism fixpoint

**Status:** **Accepted — intra-module elision shipped (default ON).** A sound,
mode-independent, cut-aware **determinism fixpoint** used to elide a cut that
provably prunes nothing — dropping the cut, its `get_level`, and (where the cut
was the sole reason) the environment frame, and turning `Head :- …, call, !.`
into a clean tail call eligible for LCO / Tier-1 self-tail loops. Tier-agnostic:
it removes work from both Tier-0 bytecode and Tier-1 IL. The **whole-program
(linker-closure)** extension that unblocks cross-module-callee candidates is
**deferred** (see Implementation notes).

## Implementation notes (2026-07-09)

- **Shipped: intra-module elision.** `DeterminismAnalysis` (new,
  `Shumway.Compiler.Wam`) is the single source of truth for the determinism
  model; it computes the least-fixpoint det set over a module's clauses and
  exposes `EliminateRedundantTrailingCuts(clauses, isEligible)` — a clause-AST
  rewrite that drops the trailing top-level `!` from each eligible predicate's
  **last** clause when every prefix goal is det (an empty body becomes a fact).
  `PredicateDisassembler.CensusDet` (`--detcensus`) now delegates to it, so the
  census and the shipped elision can never diverge. Wired into
  `ModuleCompiler` behind `ElideRedundantCuts` (default off on the type; the
  `implicit_dynamic`-style `PrologFlags.ElideRedundantCuts` default is **on**),
  enabled at the two whole-module engine consult sites (query-setup +
  runtime-consult) where `dynamicFunctors` is passed so dynamic predicates are
  excluded. Full five-project gate green with it on: Embedding 2884 /
  Compiler 336 / Core 436 / Interpreter 105 / ISO 277.
- **Soundness fix found by the gate — first-argument indexing is NOT usable.**
  The initial model treated a predicate whose clause first-args are mutually
  exclusive (`q(a). q(b).`) as det. That is **mode-dependent**: `q(X)` with `X`
  unbound still enumerates both clauses, so the cut in `p(X) :- q(X), !.` is
  load-bearing. `MultiSolutionTests.QueryAll_DeepCut_YieldsCommittedBranchOnly`
  caught it. `DispatchDet` now uses only the two **mode-independent** criteria:
  single clause (no clause-alternative CP is ever created) and all-clauses-commit
  (every clause has a top-level cut). Dropping first-arg barely moved the det set
  (corpus 60.5% → 59.8%) because almost all det predicates are single-clause or
  all-cut anyway. A mode-aware first-arg refinement is a follow-up.
- **Why only the last clause.** A predicate's last clause is always reached with
  its clause-alternative CP already consumed (the dispatch chain's `trust` pops
  it, and earlier clauses' body CPs were unwound on backtrack into it), so a
  trailing cut there can only prune the clause's own prefix CPs — which
  prefix-det rules out. A non-last clause's cut may prune the CP pointing at
  later clauses; those need per-clause dispatch analysis (mode-aware) and are
  left alone.
- **Corpus impact (556 Arity files, sound model).** 13 119 deep last-cut
  candidates: 2 191 elidable intra-module (16.7%), 3 218 genuinely load-bearing
  (nondet prefix), 7 710 blocked only by a cross-module callee (the deferred
  linker-closure win). Plus 6 727 last-clause **neck** cuts, all elidable
  (all-inline prefix) — 8 918 redundant cuts removed intra-module in total.
- **Deferred — whole-program linker closure.** Running the fixpoint in the
  linker (which owns the complete call graph) would resolve the 7 710
  cross-module-blocked candidates. The intra-module pass is the foundation; the
  linker extension reuses the same `DeterminismAnalysis` over the merged program.

### Original proposal (retained below)

A sound, mode-independent, cut-aware **determinism fixpoint** — computed
intra-module at compile time and, for the whole program, in the **linker**
(which already owns the complete call graph) — used to elide a cut that provably
prunes nothing, dropping the cut, its `get_level`, and (where the cut was the
sole reason) the environment frame. Tier-agnostic: it removes work from both
Tier-0 bytecode and Tier-1 IL.

## Context

58.4% of Arity-corpus clause bodies end in `Head :- Body, !.` (measured, ADR-029
census). Many of those cuts are **redundant**: the clause is the last/only one of
its predicate (so `trust_me` already popped the clause-selection choice point,
i.e. `B == B0` at the body), and every goal before the cut is deterministic
(leaves no choice point), so the cut has nothing to prune.

`Engine.Cut(barrier)` already **runtime-no-ops** this case (`Engine.cs:763`:
`if (_b == barrier) return;`), so a redundant cut is cheap at runtime. But the
compiler still pays for it structurally: a `!` after a real call is a **deep
cut** (`ClauseCompiler`), forcing `get_level` (capture `B0` into a Y slot before
the call clobbers the `B0` register), a cut-barrier Y slot, and — often — an
environment frame. Proving the cut redundant lets the compiler **drop all of
that**, in both tiers.

This is the complement of ADR-031: there the cut commits *clause selection* (a
non-last clause) and is folded to an if-then-else; here the cut sits in the
*last/only* clause where no clause CP exists, and is simply removed.

### The determinism it needs

At a cut in the last clause of predicate `P`, `B == B0` (the cut is redundant)
iff every goal from procedure entry to the cut leaves no choice point. A goal
`q(…)` leaves no CP iff `q` is **det** (det/semidet — no residual CP on success).
So the analysis is a determinism property of callees, computed by a fixpoint over
the call graph.

## Decision

Compute a conservative, sound, mode-independent determinism fixpoint and use it
to elide redundant trailing cuts.

### Determinism model (`det(P)` = P leaves no CP on success)

- **`dispatchDet(P)`** — P's clause selection leaves no residual CP: `P` is
  single-clause, **or** every clause commits via a top-level cut, **or** the
  first head argument is **mutually exclusive** across clauses (first-argument
  indexing pins one clause per ground call — key by atom / integer / functor·arity
  / list-cons / nil; a variable first-arg or a duplicate key defeats it).
- **`bodyDet(clause)`** — the goals **after the last top-level cut** all leave no
  CP (a cut prunes everything before it, so pre-cut nondeterminism does not leak;
  a clause with no cut needs *all* goals det). A goal leaves no CP if it is inline
  (`!`/`is`/`=`/the six comparisons), a **known-det builtin** (a whitelist —
  arithmetic, comparisons, type tests, `functor`/`arg`/`=..`/`copy_term`,
  non-backtracking atom/number conversions, output, `assert*`, globals; **not**
  `atom_concat/3` or `sub_atom/5`, which backtrack), a **det control** wrapper
  (`\+`/`once`/`findall`/`forall`/`ignore` — they never leak the inner CP), or a
  call to a **det user predicate**.
- **`det(P) = dispatchDet(P) ∧ ∀ clause: bodyDet(clause)`**, seeded with the
  known-det builtins and iterated to a **least fixpoint** (monotone: a predicate
  is only ever *added* to the det set). Recursion converges naturally.

Mode-independent by design: `det(P)` holds only if P is det in **all** modes
(a call to an unknown-mode callee is treated as possibly-CP-leaving). Sound,
conservative — it never over-approximates determinism, which is required because
running extra clauses is a correctness bug, not a cosmetic one
([[extra-backtracking-not-sound]]).

### Where it runs, and where elision applies

- **Intra-module** (first step): `ShmoCompiler` runs the fixpoint over one file's
  predicates; cross-module callees are unknown (not-det → conservative). This
  already elides a meaningful fraction (below) with no new whole-program
  infrastructure.
- **Whole-program** (the payoff): the **linker** (`ShmoLinker`) owns the complete
  cross-module call graph (per-predicate call edges in `ShmoObject`, reachability
  already walked) — it runs the same fixpoint over the linked program, resolving
  the cross-module callees the per-file pass could not. This is the natural home
  the user identified: the closure "if all reachable predicates from a goal are
  det, the goal is det" is exactly a call-graph fixpoint the linker can compute.
- **Applying the elision**: the affected predicate is **recompiled from
  `ClauseTerms`** without the trailing cut. The linker already recompiles from
  `ClauseTerms` for the chunk-411 cross-module LTO unfold, so no new
  recompilation machinery is needed — the det fixpoint feeds the same re-emit.

### What is elided

In the **last/only** clause of `P`, when the body ends in a top-level `!` and the
prefix is provably det: drop the trailing `cut`, its `get_level`, and the
cut-barrier Y slot; drop the frame if the cut was its only cause (`needsDeepCut`
was the sole `needFrame` trigger). Non-last clauses (where the cut commits clause
selection) are **out of scope** — those are ADR-031.

## Corpus impact (`shumway-disasm --detcensus --arity`, 556 files)

- **60.5%** of predicates (18 647 / 30 820) are proven det by the **intra-module**
  fixpoint alone.
- Candidate population — last/only clause ending in a top-level cut: 6 727 neck
  (already W2-cheap) + **13 119 DEEP** (pay `get_level`+frame today).
- Of the 13 119 deep last-cut clauses, by prefix determinism:
  - **258 (2.0%)** elidable with **no** predicate analysis (builtin/det-control
    prefix only);
  - **+1 958 (14.9%)** more elidable with the **intra-module det fixpoint** — the
    fixpoint is an **8.6× multiplier** (258 → 2 216, 2% → 16.9%);
  - **7 742 (59.0%)** blocked *only* by a cross-module callee — the **linker
    whole-program closure envelope** (some resolve to det → elidable, some to
    nondet → confirmed blocked);
  - **3 161 (24.1%)** blocked by a genuinely nondet callee (not elidable).
- Ceiling with the linker fixpoint: up to ~76% of deep last-cut clauses (16.9%
  intra + up to 59% cross), minus the cross-module callees that resolve to
  nondet.

Interaction with ADR-028: deterministic indexing **increases** redundant cuts (a
uniquely-dispatched predicate leaves no clause CP, so a defensive trailing cut
becomes a no-op), so the sibling/bucket-indexing work raised this ADR's value.

## Soundness

Elide **only** with proof. Cross-module or otherwise-unknown callees are **not**
assumed det — they keep the cut. The fixpoint is monotone and conservative
(det-in-all-modes). Removing a provably-redundant cut cannot change solutions or
backtracking: the cut pruned nothing (`B == B0`), and re-entry on backtrack was
already blocked by the proven determinism. A wrong elision would leave a CP the
cut removed → extra solutions → unsound, which is precisely why the analysis must
be conservative and cross-module unknowns must not be trusted.

## Implementation plan

1. Promote the census determinism model (already in
   `PredicateDisassembler.CensusDet`) into a reusable analysis
   (`DeterminismAnalysis`) over a predicate set: `dispatchDet`, `bodyDet`, the
   known-det builtin whitelist, the least-fixpoint driver.
2. **Intra-module**: `ShmoCompiler` runs it; the redundant trailing cut is
   dropped before/at `ClauseCompiler` (a clause-rewrite or a `ClauseCompiler`
   flag "trailing cut is redundant" that skips `needsDeepCut`/`get_level`/cut for
   the last clause).
3. **Whole-program**: `ShmoLinker` runs the fixpoint over the linked call graph
   and recompiles cut-elidable predicates from `ClauseTerms` (chunk-411 path).
4. Persist a per-predicate `det` bit in `.shmo`/`.shum` if useful for
   cross-bundle reuse (optional; pre-release format freedom).

## Verification

- Correctness first: a differential run of corpus programs (and the ISO /
  Embedding suites) proving **no** change in solution count — the anti-unsoundness
  gate.
- `--detcensus` numbers as the go/no-go and the regression baseline.
- Unit tests: elision fires on `last-clause det-prefix` shapes and does **not**
  fire on (a) non-last clauses, (b) a nondet prefix callee, (c) a cross-module
  callee under the intra-module pass.
- Deterministic opcode-count / frame-count drop on the elided predicates.
- Full five-project gate.

## Deferred

- **Mode-dependent determinism**: a predicate det only in the call's actual mode
  (e.g. `append(+,+,-)`). Needs call-site mode info (Phase-3 mode inference is
  declaration-driven; the corpus declares none). The mode-independent version
  already captures 60.5% of predicates.
- Second-order det (a callee passed as a goal argument) — treated as nondet.

## Related

ADR-031 (delayed choice point — the non-last-clause counterpart); ADR-028
(indexing increases redundant cuts); Phase-33 W2 (neck cut — the all-inline
prefix is already cheap); [[extra-backtracking-not-sound]];
[[logtalk-benchmark-comparison]] (the det census).
