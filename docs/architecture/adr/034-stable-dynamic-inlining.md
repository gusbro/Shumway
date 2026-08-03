# ADR-034: Sound stable-dynamic inlining (checked caller-inline of dynamic snapshots)

**Status:** SHIPPED (default ON — it is a soundness fix plus the fast path that
keeps the optimization).

## Context

Two converging facts, one shipped bug.

**The practical model (user-supplied, corpus-validated).** ISO permits
asserting new clauses onto any `:- dynamic` predicate, including one compiled
with rule bodies — but real programs never do that. What real (Arity) programs
do is assert **facts**, onto predicates that start **empty** (or fact-only).
A dynamic predicate that ships **rules** is declared `:- visible`/`:- dynamic`
only so findall/setof/meta-calls can reach it — it is mutation-cold. The
whole-program census agrees: test/ 634 seeded dynamics — **all 634
rule-bearing, 0 fact-only**; testGen/ 771 rule-bearing vs 9 fact-only. The
mutable population is the *empty* dynamics (the ~500 `arity_implicit_dynamic`
assert targets), which have nothing to inline anyway.

**The shipped bug (probe4, 2026-07-10).** ADR-023's persisted bake replaces a
seeded dynamic in the link-time calleeMap with its static-style SNAPSHOT so the
predicate's own delegate can bake (evictable via `EvictDelegate` — sound). But
the same calleeMap feeds every **caller's** emit, and ADR-031's guard tiers
(G/G2/G3/ADR-033 copies), the chunk-69 leaf inliner and the chunk-362 fact
inliner treated the snapshot as a normal static callee and **inlined its code
into the caller's IL**, where eviction cannot reach:

```prolog
:- dynamic r/1.
r(X) :- X > 0.
g(X, R) :- r(X), !, R = yes.
g(_, R) :- R = no.

?- assertz(r(-1)), g(-1, R).
% WAM-only bundle:  R = yes   (ISO logical update view)
% IL bundle pre-fix: R = no   (stale snapshot inlined in g/2)
```

The scale: **423 of test/'s 724** accepted CP-free guards (303/601 on testGen)
embed dynamic snapshots — over half the optimization's coverage was riding on
the unsound inline. Two further dispatch-level variants of the same staleness
were found while fixing it (see §Dispatch fixes).

## Decision

Keep the inline — it is the fast path the corpus profits from — and make it
sound with a **clause-entry staleness test + un-inlined fallback**, per the
user's design ("fast path for the usual case; if one of them is ever asserted,
patch to the slow path, losing the optimization").

1. **Marker.** `CompiledPredicate.IsDynamicSnapshot` +
   `SnapshotRuleBearing`, set by `BuildDynamicSnapshot` (rule-bearing = any
   RAW source clause is a rule). Callee-side inlining decisions can now tell a
   snapshot from a real static.

2. **Gating.** Only **rule-bearing** snapshots may be caller-inlined, and only
   through the checked guard machinery. Fact-only snapshots (the real assert
   targets) are rejected from guards (`dyn-snapshot-facts`) and excluded from
   the leaf inliner, the case-2 rule inliner and the multi-fact inliner — their
   call sites stay threaded by fid, dispatching against the live predicate.

3. **Collection.** The recognizer/describe walk (`TryGetCpFreeGuard`,
   `DescribeFailDirectCore` — G3 inners and ADR-033 cross-tail targets
   included, transitively) collects the embedded snapshot fids into
   `CpFreeGuardInfo.EmbeddedDynamicFids` via the `FailDirectExtras`
   side-channel.

4. **Clause-entry check.** A clause whose guard embeds snapshots is prefixed
   with one `Engine.IsDynMutated(fid)` test per fid (a shared
   `HashSet<int>` membership probe; the set lives on the host `PrologEngine`
   for its lifetime, is shared by reference into every per-query `Engine`, and
   `InvalidateDynamicCache` adds the fid on every assert/retract/abolish — so
   a mid-query mutation is visible at the very next clause entry). Baked via
   `EmitFunctorId`, so persisted IL is patched by name at load.

5. **Fallback.** A stale fid branches to the clause's fallback: plain entry
   CP + the guard-and-cut slice re-emitted un-inlined (its dynamic call is a
   threaded by-fid call → live `enter_dynamic` chain; `--strip-wam` already
   keeps snapshot fids' WAM) + a jump into the **shared post-commit body** of
   the optimized clause. Emit mechanics: the fallback re-uses the guard Call
   cursors the optimized path no longer dead-marks (regions) / takes
   pre-scanned extra resume cursors (chains); `localSalt` keeps the re-emitted
   pc-named locals from colliding. Once mutated, the predicate's callers stay
   on the fallback for the rest of the process — per the model, that case is
   ISO-paper-only.

6. **The staleness window.** Nothing may mutate the database between the
   clause-entry check and the inlined snapshot code. All code on that path is
   whitelist-controlled, so it suffices to reject the combination *embedded
   snapshot + DB-mutation builtin* (`assert*/retractall/abolish/consult/…`;
   `retract/1` is backtrackable, already rejected) anywhere in the walked
   clause — `dyn+mutation`. Order-insensitive, checked at the accept point.

### Dispatch fixes (the same staleness one level down)

Found reproducing the bug minimally — both pre-existing, both LUV breaks
visible with **no caller inline at all** (`assertz(f(7)), f(7)` → false
through a baked bundle):

- **Stage-B hardening.** `InstallCallIlRewrites` rewrote `Call`→`CallIl` for
  ANY callee with a delegate — including a dynamic's evictable snapshot. The
  rewrite persists in the buffer across queries; after eviction the site ran
  the stale wrapper (or would crash on a cleared slot). Fix: dynamic callees
  are never hardened — they stay on generic `Call`/`Execute`, whose OnDispatch
  resolves per call. (Extends chunk 227's rule to ADR-023 baked delegates.)
- **Per-query fid table.** IL callers dispatch callees by fid through the
  interpreter's `IlByFunctorId` snapshot, taken at query setup — a mid-query
  eviction left the stale delegate in the table. Fix: `InvalidateDynamicCache`
  clears the live interpreter's slot; the next dispatch falls back to
  `ResolveByFunctorId` (live) → miss → the callee's WAM chain.

## Rejected: empty-dynamic-as-fail (measured 2026-07-10, then removed)

The "eviction cascade for empty dynamics" question was answered by building
the minimal alternative and measuring: a guard call to a dynamic with NO
clauses at link time was inlined as FAIL (semantically exact while empty)
under the same clause-entry staleness test — the first assert flipped every
embedding clause to the live fallback. Static acceptance exploded — test/
724 → 1 222 (+69%), testGen/ 601 → 1 269 (+111%) in the default
configuration; tierG 5→315 / 47→498 —

**and it was still rejected**, on the runtime-cost argument:

1. **In any reasonable program the assert happens** (assert-before-call is
   the dominant idiom for these predicates), so the steady state is the
   FALLBACK — which is exactly the pre-feature plain path — **plus** a
   per-clause-entry membership probe and ~2% of dead optimized code. The
   static acceptance counts measured link-time conversions, not runtime
   wins; for the dominant population the feature is a net runtime cost.
   The only true beneficiaries are never-asserted empties (dead features,
   mode-gated hooks) — not worth taxing everything else.
2. **The corpus counts were inflated by placeholders.** Most of the "empty
   dynamics" are GX host-interface predicates (`i_*`) that these links model
   as empty dynamics only because they were linked WITHOUT the host
   (`--allow-undefined`). In a production link they are FOREIGN predicates
   (`[PrologPredicate]` / `:- native`), and the guard machinery already
   derives their det-ness from the implementation itself
   (`BacktrackableDetector` — det foreigns are accepted as det builtins,
   non-det ones are `IsBacktrackable` and rejected). Foreign calls need no
   dynamic modelling at all.

What the assert-before-call idiom would actually profit from is the
**runtime tier**: caller re-promotion with the SETTLED fact set inlined
(ADR-023's churn re-arm already re-snapshots the predicate itself after 4096
mutation-free calls) plus caller invalidation — a properly-scoped cascade /
generation check. Future work, to be sized on a runnable corpus.

### Mixed-cycle soundness fix (found by the same measurement — KEPT)

The deep-G3 tail-cycle rule accepted a cycle whose BACK-edge was tail without
checking the rest of the segment — a MIXED cycle (`A -Call→ B -Execute→ A`)
nests activations of the same copy (the case-3 IL-local clobber), and the
describe became entry-point-dependent (accepted from A, rejected from B — the
emit's re-describe from B then crashed the link). Fix: `visiting` maps each
on-path fid to the count of NON-TAIL edges at its entry; a back-edge is
accepted only when the count is unchanged (pure-tail cycle segment), which is
also entry-point-independent (a mixed cycle rejects from every entry).
Measured residuals: `g3:cycle-mixed` 67, `g3:cycle-nontail` 48 (test/).

## Soundness argument

The check dominates every path to embedded-snapshot code: clause entries are
the only way in (fresh calls, CP resumes at clause cursors, self-tail loops
re-enter through the entry), shared copies are reached only from checked
clauses, and rule 6 guarantees no mutation can occur after the check fires.
The fallback is the ordinary CP-carrying clause emission — ISO semantics by
construction — and its dynamic dispatch is the live chain. Mutation marking is
monotonic (never cleared), so the check can only flip fast→slow, never back:
no ABA. `catch/3`, wakeups and GC are untouched (the check is a pure read).

## Measured (whole-program links, 2026-07-10)

- test/ (te/4): accepted 724 (CONT 733) — **unchanged** from pre-ADR; 423
  (430) of them now carry the check. Bundle +0.8% (the fallback guard slices).
- testGen/ (generate/3): accepted 601 (CONT 650) — unchanged; 303 (310)
  checked. Bundle +0.35%.
- Probe end-to-end on shipped binaries: `assertz(r(-1)), g(-1,R)` → `yes`
  (was `no`); fact-only `assertz(f(7)), h(7,R)` → `seen` (was stale).
- Check cost: one hash-probe + branch per dyn-embedding clause entry
  (`Count == 0` fast exit while nothing has ever mutated).

## Deferred

- **Runtime-promotion feeding**: the in-process promotion calleeMap still
  shows dynamics as `enter_dynamic` (rejected) — feeding rule-bearing
  snapshots there (same check machinery) would extend the fast path to
  non-bundle runs.
- **Re-earning the fast path** after a mutation (re-promotion with a fresh
  snapshot + generation-stamped checks) — pointless under the practical model.
- Empty/fact-only dynamics stay un-inlinable by design (`g3:inner-dynamic-*`
  residual); a caller-eviction cascade remains the only route there (ADR-023
  extension, future).

## Related

ADR-023 (dynamic snapshots + evict — the predicate-side half this completes);
ADR-031/033 (the guard tiers and copies that do the inlining); ADR-015
(logical update view — the semantics being preserved); chunk 227 (dynamics
stay on generic dispatch — now uniformly enforced).
