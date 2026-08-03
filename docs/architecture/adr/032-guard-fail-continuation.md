# ADR-032: Dynamic guard fail-continuation (engine continuation stack)

**Status:** **SOFT-REJECTED (revisable).** Not a hard rejection: the design is
recorded in full and the decision is explicitly open to revisiting once the
promotion-time accept/reject statistics (`IlPredicateCompiler.CpFreeStats`,
surfaced by `shumway-link --verbose` and `SHUMWAY_CPFREE_STATS=1`) show, on
real corpus programs, how much of the guard-call population remains beyond the
static tiers after the planned widenings (caps, callee cuts, control shapes,
true-G3 nesting). If the residual is large AND hot, the ceiling analysis below
should be re-weighed against real numbers. Until then: widen the static
fail-direct tiers instead.

## Problem

The ADR-031 CP-free guard commit covers `Head :- Guard, !, Body. / Rest.` when
every failure path inside `Guard` is (or can be made, by inlining) a direct IL
branch — "fail-direct". A guard calling a predicate that pushes ANY engine
choice point during its run (multi-clause dispatch that isn't CP-free, a
backtrackable builtin, a non-tail call, mutual recursion) fails through
`TryBacktrack`: with the clause CP skipped, exhausting the callee's choice
points backtracks PAST the clause instead of into `Rest`. Those shapes today
keep the entry `PushIlChoicePoint` — correct, just not CP-free.

## Sizing (the `--foldcensus` guard-callee classification, 556-file corpus)

Of 3 291 fold-candidate guards containing a user call, classified at FILE
level: G1 leaf 2, G2 fail-direct 8, G3 closure 1, **NeedsDynamic 587
(17.8%)**, **CrossModule 2 693 (81.8%)**.

Two corrections to read this properly:

- **The 81.8% CrossModule is a census artefact, not a coverage gap.** The
  runtime promotion calleeMap (`Tier1DispatcherAdapter.CalleeMap`) is built
  from `_predicatesByAddress` — the WHOLE linked program, no module
  boundaries — and the persisted build uses the Phase-33 bundle-wide
  calleeMap. The shipped G1/G2 recognisers therefore already resolve
  cross-module callees at promotion time; a cross-module callee that is
  fail-direct takes the CP-free path TODAY. The file-level AST census simply
  cannot see across files. The true dynamic-only population is the
  whole-program analogue of the 17.8% plus whatever fraction of the 81.8%
  resolves to a non-fail-direct callee — measurable with a promotion-time
  diagnostic counter, not with a per-file AST census.
- The 587 NeedsDynamic decompose by cause: > 4 clauses (a cap, not a law),
  cuts inside the callee, control constructs, mutual recursion, non-tail
  self-recursion, genuine nondeterminism. Several of those causes are STATIC
  widenings, not dynamic-continuation territory (see Alternatives).

## The design (recorded for the future)

An engine-side stack of guard frames:

```
struct GuardFrame {
    int B;                       // engine B at clause entry — the barrier
    int BindingTop, ExtraTop, HeapTop, Hb, E, Cp, B0; long ViewGen;
    Cell[arity] SavedArgs;       // caller argument registers
    Func<Engine,int,bool> Del;   // region delegate
    int FailCursor;              // cursor of the next clause / restore stub
}
```

Push at the CP-free guard-call clause entry (instead of the clause CP); pop at
the commit. `TryBacktrack` gains a check: when the choice-point stack drops to
`frame.B` and a failure occurs (the callee exhausted every alternative), pop
the frame, restore its snapshot, and re-invoke `Del(FailCursor)` — routing the
exhaustion-failure into the next clause without a clause CP. Engine fields
survive dispatch re-entry (the reason IL locals cannot carry this: they die
when the dispatch loop re-invokes the region delegate on a CP-pop re-entry or
the wakeup lazy-CP path). Interactions that must be handled: `catch/3`
(`UnwindToCatchFrame` must pop guard frames above the catch snapshot),
`ClearPendingWakeups` on the restore, heap-GC root scanning of `SavedArgs`,
and cut (`Engine.Cut` past a guard frame must discard it — the `_ilCpStack`
precedent).

## Why the recommendation is REJECT

- **The win ceiling is the commit path only.** The failure path — the reason
  the frame exists — still round-trips through `TryBacktrack` + restore +
  delegate re-invoke, exactly like a clause CP: the callee's internal choice
  points are real and their backtracking is the semantics. What the frame
  saves over the CP is the entry push + the cut's pop/compaction on commit —
  of the ADR-031 tier-A measurement (~87 ns per push+cut+backtrack round
  trip), roughly the 30–40 ns commit-side half — on clauses whose guard, by
  definition of this class, does SUBSTANTIAL work (a multi-clause,
  CP-pushing, possibly recursive callee). The relative win shrinks precisely
  where this design applies.
- **It taxes the hottest engine path.** The `frame.B` check runs per
  `TryBacktrack` iteration for every program, guard frames present or not —
  the chunk-231/234-class dispatch costs this codebase has repeatedly paid to
  remove.
- **It is a backtracking-model change** (the [decision policy](../decision-policy.md) major-decision list) with
  four subtle interaction surfaces (catch, wakeups, GC, cut), each a
  soundness cliff of the kind the ADR-031 wakeup/lazy-CP work only just
  navigated for a far smaller mechanism.

The ADR-021 / ADR-026 discipline: a measured-ceiling rejection, blueprint
preserved.

## Alternatives that capture the same population statically

1. **Raise the fail-direct caps** (≤ 4 clauses / ≤ 512 bytes are prudence,
   not soundness): measure first with a promotion-time rejection-reason
   counter.
2. **Callee-internal cuts**: in the sequential-chain inline, a cut inside
   callee clause i means "post-cut failure skips the remaining alternatives" —
   emit by switching the fail label at the cut from `alt_{i+1}` to the outer
   stub. Static, bounded.
3. **Control constructs** (`(C -> T ; E)` in callee bodies): the MetaTransform
   helper shapes are themselves chain predicates — the fail-direct describe
   can recurse into them like any callee (G3 closure, already recognised at
   census level).
4. **Non-tail calls to fail-direct callees** (true G3): nested
   `EmitFailDirectCalleeInline` with a visited-set to reject cycles — static
   nesting, no engine changes; only MUTUAL recursion is fundamentally beyond
   static inlining.
5. **Measure shipped coverage honestly**: a `SHUMWAY_DIAG` counter on
   `TryGetCpFreeGuard` accept/reject (with rejection reason) over the real
   corpus programs (Blint, testGen suites) — the number that should drive any
   revisit of this ADR.

## Related

ADR-031 (the CP-free guard tiers this extends); ADR-030 (whole-program
determinism — the linker closure that resolves cross-module callees for the
static analysis); ADR-021 / ADR-026 (measured-ceiling rejections); Phase 33
bundle-wide calleeMap (why cross-module callees resolve at promotion time).
