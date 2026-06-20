# ADR-023: Dynamic Predicates in Tier-1 IL (snapshot + evict-on-mutation)

## Status

Accepted (Phase 30) — implemented. `IlPromotionStore` gains `DynamicSnapshotProvider`,
`EvictDelegate`, and the eviction-churn limit; `RecordInvocation` compiles a
static-style snapshot for the `enter_dynamic` shape instead of rejecting it;
`PrologEngine.BuildDynamicSnapshot` produces the snapshot from the rewrite cache
and `InvalidateDynamicCache` evicts on every mutation. `IsExcludedByLayout` still
classifies the *bytecode* shape (used by `Warm` and the link-time
`IsPermanentlyBytecodeOnly` → `CallBytecode` rewrite, which already keeps dynamic
call sites on the OnDispatch path via `!IsDynamicPredicate`). Covered by
`DynamicIlPromotionTests` (promote / evict / retract / churn-guard / LUV).

Supersedes the standing rule, from ADR-015 / chunk 159
(`IlPromotionStore.IsExcludedByLayout`), that a `:- dynamic` predicate always
runs on Tier 0. That rule was *load-bearing*, not over-defensive: the dynamic
dispatch mutates its own bytecode in place on `assert`/`retract`, so a cached IL
delegate compiled from a snapshot would not observe the change and would go
stale. This ADR keeps that soundness guarantee while letting a dynamic predicate
run as IL **between** mutations.

## Context

A static predicate is immutable, so it IL-compiles freely. A dynamic predicate
accepts `assertz`/`asserta`/`retract`/`abolish` at run time; the mutation is
implemented as in-place patching of the predicate's bytecode (ADR-015: the
`enter_dynamic` trampoline + a `try_me_else` chain with a `check_visible <born>
<died>` guard per clause). `IsExcludedByLayout` therefore pins any predicate
whose bytecode opens with `enter_dynamic` to Tier 0, where the mutation lands.

GNU Prolog makes the same trade: `gplc` does **not** native-compile dynamic
predicates even when they have source clauses — it loads the clauses into the
dynamic database and runs them interpreted, for exactly this reason. So Shumway
currently *matches* GProlog here.

But the blanket exclusion costs us in a common, important shape:

- **The 99% case.** When a predicate is hot enough to promote, it is almost
  never mutated again. Source clauses *with bodies* are real code; programs very
  rarely `assert`/`retract` body-carrying clauses at run time.
- **The genuinely-mutated case is different.** A `:- dynamic foo/N.` declared
  with **no** clauses is the assert/retract-heavy idiom (a fact base built and
  torn down at run time). Those should stay on Tier 0 — there is nothing to
  compile and they would only churn.

So the predicates the exclusion hurts (read-hot, body-carrying, mutation-cold)
are precisely the ones IL helps most, and the predicates it protects
(mutation-hot) are easy to tell apart (they mutate). This ADR exploits that
separation. It goes **beyond** GProlog — a Shumway optimization for read-hot
dynamic relations, in keeping with the project's "outperform GProlog in
interop-heavy / embedded workloads" goal.

## Decision

Let a dynamic predicate be Tier-1-promoted as a **snapshot** of its currently
visible clauses, and **evict** that snapshot on any mutation of the predicate.

1. **Eligibility.** `IsExcludedByLayout` no longer permanently rejects the
   `enter_dynamic` shape. A dynamic predicate becomes promotable when it has
   **≥ 1 visible clause** and is not churning (see 5). An **empty** dynamic
   predicate is never promoted — there is nothing to compile and it is the
   assert/retract-heavy idiom.

2. **Snapshot compile.** At promotion the engine compiles the predicate's
   currently-visible clauses into a *static-style* `CompiledPredicate` — an
   ordinary `try_me_else` chain, with **no** `enter_dynamic` and **no**
   `check_visible` (every clause in the snapshot is visible by construction).
   This reuses the static `ClauseCompiler` / IL path unchanged; the IL compiler
   sees a normal predicate. The visible-clause set is taken from the engine's
   authoritative dynamic-clause list (`_dynamicClauses[fid]`), which `assert` /
   `retract` keep in step with the bytecode.

3. **Dispatch.** The dynamic-predicate dispatch consults the IL delegate first:
   present → run the snapshot delegate; absent → the Tier-0 bytecode (the
   `enter_dynamic` chain). The bytecode is **always** maintained — `assert` /
   `retract` patch it in place regardless of whether an IL snapshot exists — so
   the Tier-0 fallback always reflects the current database.

4. **Evict on mutation.** Every dynamic-store mutation already funnels through
   `PrologEngine.InvalidateDynamicCache(fid)` (the one place the ADR-015
   generation clock advances). It additionally **evicts the functor's IL
   delegate**. The next dispatch then falls to the in-place-patched Tier-0
   bytecode, which carries the post-mutation state.

5. **Churn guard.** Each eviction increments a per-functor counter. After **K =
   3** evictions the functor is marked **permanently Tier 0** (it will never be
   IL-promoted again this session). This bounds the promote→evict thrash for a
   predicate that turns out to be mutation-hot (e.g. an empty dynamic that gets
   called between asserts and briefly promotes). Re-promotion **is** otherwise
   allowed: after an eviction the normal invocation counter resumes, and a
   predicate that goes hot again *and stays unmutated* re-snapshots the current
   clauses (a re-compile reads the current bytecode/clauses, so the new snapshot
   is current).

## Soundness — the logical update view (ADR-015) is preserved

ADR-015 requires that a call sees the database as of when its goal began, and
that a later `assert`/`retract` does not perturb an in-progress call. The
snapshot + evict design preserves this:

- **In-progress call.** A call running the snapshot delegate completes on its
  snapshot — including on backtracking, where a choice point re-enters the same
  delegate's clause chain. That is exactly "the database as of when the goal
  began". A mutation during the call evicts the delegate from the *cache* (a
  future-dispatch concern) but does not touch the running delegate, which the
  live choice point keeps reachable. So the in-progress call neither sees the
  mutation nor is freed underneath.

- **New call after a mutation.** Dispatch finds no delegate (evicted) and runs
  the Tier-0 bytecode, which the mutation patched in place — so it observes the
  new state. If the predicate goes hot again unmutated, it re-snapshots.

- **Self-mutating predicate.** A clause body that asserts/retracts its own
  predicate evicts the delegate mid-call; the running snapshot finishes
  unaffected (correct LUV), the next entry sees the change. Identical to the
  Tier-0 semantics.

The key invariant: **the bytecode chain is the always-current source of truth;
the IL delegate is a cache of one snapshot, dropped the instant the truth
changes.** A delegate is never mutated in place (unlike the bytecode), so it can
only ever be correct-for-its-snapshot or absent.

## Consequences

- Read-hot, mutation-cold dynamic predicates (config tables, rule bases asserted
  at startup then queried heavily) now run as Tier-1 IL.
- Mutation-hot predicates pay at most K snapshot compiles before being pinned to
  Tier 0; steady-state they behave exactly as today.
- One more reason for `InvalidateDynamicCache` to be the sole mutation funnel —
  the eviction hook lives there, so any mutation path that forgets to call it is
  already a pre-existing ADR-015 bug, not a new failure mode.
- `IlPromotionStore` gains: a snapshot-compile entry point, a per-functor
  eviction counter + permanent-Tier-0 mark, and `EvictOnMutation(fid)`.
- The IL compiler is unchanged — it only ever sees the static-style snapshot
  `CompiledPredicate`, never the `enter_dynamic` layout.

## Alternatives considered

- **Compile the `enter_dynamic`/`check_visible` layout directly in IL** (emit
  the per-clause visibility guard). Rejected: it bakes the born/died generation
  filter into IL that a mutation can't update — the same staleness the snapshot
  avoids — and it defeats the point (the guard is pure overhead once we evict on
  mutation).
- **Keep dynamics on Tier 0 forever (status quo / GProlog).** Rejected: leaves
  the read-hot, mutation-cold case — the exact case IL helps most — on the
  interpreter, for a restriction that the mutation signal makes unnecessary.
- **Recompile-in-place on mutation instead of evicting.** Rejected: re-emitting
  a Sigil method on every `assert`/`retract` is far costlier than dropping a
  cache entry, and would stall the mutating call; eviction + lazy re-promotion
  pays only when the predicate is both hot and stable.
