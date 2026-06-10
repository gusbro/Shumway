# ADR-021: WAM register allocator — survey-driven verdict (arc rejected)

## Status

**Decided — the global register-allocator arc is REJECTED on quantified evidence**
(Phase 29, chunk 405). The survey tool (`SHUMWAY_Y_SURVEY=1` on `shumway-compile`)
stays in the tree so the numbers can be re-checked when the workload mix changes.

## Context

Three project records pointed at "a proper GProlog-style register allocator" as
the next big performance lever:

- `phase-26-planned` item 1 ("GProlog-style register allocator").
- The Phase-25/26 *chunk-model refinement* experiment: treating inline-compiled
  goals (cut, `=/2`, `is/2`, the six comparisons) as chunk-transparent so a
  variable used on both sides of a guard stays in an X register instead of
  being promoted to an environment Y slot. It produced beautiful code on qsort's
  `partition` (17→11 opcodes, zero `put_value`) and was **reverted twice as
  unsound** — see the failure analysis below.
- The chunk-403 Blint self-lint profile: `get_variable_y` 1.86 M +
  `put_value_y` 1.73 M + `put_variable_y` 0.73 M ≈ **4.3 M of 28.4 M executed
  opcodes (~15 %) are Y-slot traffic**, the single biggest addressable-looking
  block once the dead-chain tombstones (chunk 404) were fixed.

What was never measured: how much of that traffic a sound allocator could
actually reclaim. This ADR is that measurement, and the decision it forces.

## The survey

`ClauseCompiler` (under `SHUMWAY_Y_SURVEY=1`) classifies every permanent it
allocates:

- **Class A** — the variable's uses cross at least one *real call*. Irreducible
  in the WAM model: the callee owns the X-register file, so the only storage
  that survives a call is the environment. This is not a Shumway limitation;
  it is the WAM, and GProlog allocates these exactly the same way (verified
  against `pl2wam` dumps in Phase 26 — GProlog even compiles `is`/`=<` as
  *calls*, so its permanents are a superset of ours on arithmetic code).
- **Class B** — the variable's uses cross only *inline-compiled* goals
  (cut, `=/2`, `is/2`, comparisons). This is the entire feasible target of the
  chunk-transparency allocator: what it would demote to temporaries.

Results:

| Program | Permanents | Class B | Class B share |
|---|---|---|---|
| **Blint.pl** (real, 2 570 lines) | **875** | **13** | **1.5 %** |
| tak.pl | 15 | 6 | 40 % |
| queens.pl | 18 | 10 | 56 % |
| qsort.pl | 15 | 5 | 33 % |
| crypt / flatten / zebra / nreverse / boyer / sendmore / serialize | 7–19 | 1–2 each | 7–14 % |

None of Blint's hot predicates (`on/2` 388 K calls, `on_test/2` 182 K,
`next_char_i/1` 106 K, the tokenizer family) has a single Class-B permanent —
the 13 sit in `parse_op/5`, `print_term/3` and four cold helpers. Weighting by
the dynamic profile, the Class-B traffic is **well under 2 % of executed
opcodes** on the real program; even a perfect, free demotion would be invisible
against this machine's thermal noise.

The synthetic benchmarks tell the opposite story (33–56 %) — they are the
guard-before-recursion shape the optimization was invented for. This is the
cleanest demonstration to date that **optimizing for the Van Roy suite and
optimizing for real programs are different projects**, and the project target
(CLAUDE.md: "comparable to or better than GNU Prolog in *real-world*
scenarios"; the whole Phase 28/29 discipline) picks the real program.

## Why Class B cannot be reclaimed soundly anyway

Recorded across the two reverted attempts (`chunk-model-refinement-failed`),
re-verified here:

1. **The choice point only saves argument registers.** `PushChoicePoint(arity)`
   snapshots `A1..Aarity`; an environment survives backtracking via the single
   saved `E` pointer. A variable demoted to a non-argument X register is stale
   after any backtrack that re-enters code still using it.
2. **Choice-point liveness is not clause-local.** A clause with no control
   constructs, called from a backtracking context, still has its demoted temps
   corrupted — every clause-local gate tried (no-control-constructs,
   determinism) failed on real suites (27/43 CLP failures persisted).
3. **Head unification can run arbitrary code.** An attvar in any head argument
   fires `verify_attributes/4` — a meta-call that clobbers the X file *between
   the head and the guard*. This is what broke CLP(FD)/CLP(R) even on clauses
   that look like pure guard-before-recursion. Not statically decidable at the
   clause (callers pass what they pass).
4. The sound fragment that remains — a head-extracted temporary flowing through
   the inline prefix into the first real call's argument position — **is the
   chunk-305 argument-register preferencing, already shipped**.

Making demotion safe would require the CP to save the caller's full live
register set, which the failure record correctly calls "reinventing the
environment frame" — strictly worse than the environment it replaces (paid per
CP on backtracking-heavy code instead of per frame).

## Decision

1. **No global register-allocator arc.** The conservative chunk-based
   classification (`ClassifyPermanents`) + neck-cut transparency (chunk 309) +
   first-goal preferencing (chunk 305) + inline `=/2` (chunk 307) is the final
   register-allocation design for the WAM tiers. It already beats GProlog by
   −12 % non-index instructions on Blint (Phase 26), and the residual headroom
   on real programs is ~1.5 % of permanents, unsound to take.
2. **The survey tool stays** (`SHUMWAY_Y_SURVEY=1`). If a future workload shows
   a materially different Class-B share, this ADR should be revisited with that
   evidence — not before.
3. The Class-A traffic (the real 15 %) is addressed, if at all, by doing
   *fewer dispatches per opcode*, not fewer opcodes-by-allocation. The same
   chunk-403 profile ranks the candidates for the next performance arc:
   - **Opcode-pair superinstructions**: `unify_list → unify_atom` ran 948 K
     times (char-list scanning), with several more pairs in the same family —
     the chunk-220/221 fusion mechanism exists and the profiler's pair table
     names the candidates with real frequencies.
   - **Runtime meta-call dispatch**: Blint's user-redefined `ifthen/2` causes
     79 K runtime `call/1` dispatches + 79 K synthetic-helper calls per lint;
     the `$call`-family path is the cost, not register traffic.
   - **Tier-1 CP save/restore cost** (`tier1-register-cost-poc`): orthogonal
     to allocation; a Tier-1-side lever.

## Consequences

- Weeks of allocator work avoided on a quantified ~1.5 % unsound ceiling.
- The Van Roy benchmark gap on tak/queens-style code (where the demotion would
  pay) is consciously left on the table: GProlog does not take it either
  (arithmetic-as-calls makes its permanents a superset of ours), so the
  competitive position is unaffected.
- `docs/wam-vs-gprolog-blint.md` remains the codegen-parity reference; this ADR
  closes the allocation chapter of that comparison.
