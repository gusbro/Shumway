# Tier-1 IL local-predicate inlining (design)

**Status:** design / feasibility. No implementation yet. Phased; Phase 1 is the
bounded first step.

## Motivation

Profiling crypt (van-Roy cryptomultiplication search) in Tier-1 IL
(`dotnet-trace`, after the chunk 351/352/354/355 wins) showed it is **dispatch-
bound**, not register- or arithmetic-bound:

| | crypt |
|---|--:|
| `BytecodeInterpreter.Dispatch` (exclusive) | ~29% |
| `Tier1DispatcherAdapter.ResolveByFunctorId` | ~3.6% |
| RPN eval-stack arithmetic | ~16% |
| register / unify | ~12% |
| IL delegate work | ~18% |

crypt is a generate-and-test backtracking search
(`odd(A), even(B), …, mult(…), sum(…)`) — masses of **non-self** calls between
small module-local predicates, plus backtrack re-invocations. Every one routes
through the trampoline (below). The self-tail-recursion loop (chunks 349/350)
only removes the trampoline for *self* calls; the bulk of crypt's calls are to
*other* predicates.

The structural fix is GProlog's flat code space, scoped to a module: **emit a
module-local callee's code inside the IL method of the predicate that calls it**,
turning the inter-predicate call into an intra-method branch.

## Current dispatch model (what we are replacing for local calls)

Each predicate is a separate `PredicateDelegate(Engine engine, int cursor) →
bool`. A non-tail IL→IL call `q … p(X) …`:

1. `q`'s delegate sets up `p`'s args in the argument registers, sets
   `Cp = EncodeResumeMarker(p_fid, 0)` and `Pc = …`, sets `IlTailCallPending` /
   returns to the dispatch loop (chunk 182 threaded continuation).
2. `BytecodeInterpreter.Dispatch` decodes the resume marker → `IlByFunctorId[p_fid]`
   (O(1) array) → invokes `p`'s delegate at cursor 0.
3. On `p`'s proceed, `Pc = Cp = ` a resume marker pointing back into `q` at the
   post-call cursor; the loop re-invokes `q`'s delegate there.
4. Backtracking: `p`'s choice points carry `Cp = q`'s resume marker; a CP is
   `(delegate, nextCursor)` (`PushIlChoicePoint`). Re-trying `p`'s next clause
   re-invokes `p`'s delegate at that cursor — again through the loop.

The per-call cost (marker encode/decode + `IlByFunctorId` index + delegate
invoke + loop iteration), paid on every call AND every backtrack re-entry, is
crypt's ~32%.

Two enabling facts make inlining expressible in this model:

- **A choice point's continuation is `(delegate, cursor)`** — not a raw code
  address. So an inlined callee's clause-alternative CPs can point at *the
  caller's* delegate and *a cursor in the caller's cursor space*.
- **`ResumeMarkerCursorStride = 4096`** cursors per predicate. The merged cursor
  space (caller + every inlined callee) must stay under this — ample for
  realistic inlining.

Precedent: chunk 69 already inlines **leaf** callees (single-clause,
head-match-only, e.g. a fact with one clause) at **BOTH tail (`Execute`,
EmitClauseBody ~line 2129) and non-tail (`Call`, ~line 2009)** sites —
`EmitClauseBody(callee, suppressProceedReturn: true)` emits the callee's
head-match in place; a single clause has no choice point, so there is nothing to
merge. **So the deterministic single-clause case is DONE.** This design
generalises it to the part that is left and that actually matters for crypt: the
**multi-clause** case (odd/even/lefteven are 5-/4-clause facts), where the
callee's clause alternatives create choice points that must be merged into the
caller's cursor space. That merge is the whole of the remaining work.

## The hard parts

1. **Multi-clause callees + backtracking.** `p` has N clauses; a call tries
   clause 1, backtracks to clause 2, … Inlined, `p`'s clause dispatch + choice
   points must run inside `q`. Each CP's continuation becomes a cursor in `q`'s
   space that re-enters `p`'s next clause.
2. **The callee's environment.** `p` may have permanent (Y) variables. Inlined,
   they need frame slots. Two options: merge into `q`'s frame (hard — live-Y
   analysis, trimming across the boundary) or a **nested frame** (`p` runs its
   own `allocate`/`deallocate` within `q`'s method — simpler, keeps the env
   machinery but removes the dispatch). Phase 1 sidesteps this (facts have no
   env).
3. **Non-tail continuation.** After an inlined `p` succeeds at a non-tail site,
   control must reach `q`'s next goal — a `br` to a continuation label in `q`,
   not a proceed/return.
4. **Cursor-space merging + budget.** `q`'s cursors (0, its own clause
   alternatives, its call-site resumes) plus every inlined `p`'s cursors must
   fit in 4096. A size/recursion guard caps inlining.
5. **Recursion.** A recursive `p` (or a `q↔p` cycle) cannot be inlined naively
   (infinite expansion). Recursive callees stay separate delegates (and keep the
   self-loop). Inline only acyclic local callees.
6. **Argument passing.** Unchanged — the call's `put_*` already set the argument
   registers; `p`'s head matches them. No new mechanism.
7. **Cut.** A cut inside `p` is scoped to `p`'s call (it prunes `p`'s
   alternatives, not `q`'s). The inlined cut must cut to the choice-point level
   captured at `p`'s inlined entry (B0), exactly as a normal call sets B0.

## Phasing

Smallest, lowest-risk first; each phase is independently shippable and tested.

- **Phase 1 — multi-clause FACTS (no env, no body calls, no recursion).** A
  predicate whose every clause is head-match-only (`odd/1`, `even/1`,
  `lefteven/1`, list-membership facts). Covers crypt's generators — the bulk of
  its dispatch. Inline the fact's clause dispatch (indexed / try-chain) into the
  caller at the call site; the alternative CPs point at caller cursors; a clause
  match falls through (non-tail) to the caller's continuation. No environment to
  manage.
- **Phase 2 — single-clause DETERMINISTIC rules (env + nested calls, no CP).**
  `helper(X) :- a(X), b(X)` with one clause. Inline the body; its own calls go
  through the normal trampoline (or recurse into inlining). Introduces the
  nested-frame env handling and the non-tail continuation for a rule.
- **Phase 3 — general multi-clause rules with backtracking + env.** The full
  case: clause dispatch + CPs + env + nested calls, all inlined.
- **Phase 4 — bounded recursion.** Optionally inline one level of a recursive
  callee, or leave recursion to the self-loop. Likely "never inline recursive."

## Phase 1 detailed plan (multi-clause facts)

Eligibility for inlining `p` at a `Call p/n` site in `q`:
- `p` is module-**local** (visibility from the linker — must be threaded into the
  IL compiler; today `CompiledPredicate` lacks it, the linker's
  `PredicateVisibility` has it). Public predicates keep a standalone delegate for
  external callers; inlining a local one at its (few) call sites is pure win.
- Every clause of `p` is head-match-only + `proceed` (the chunk-69
  `IsLeafPredicate` test, generalised to N clauses → call it `IsFactPredicate`).
- `p` is not recursive and `p ≠ q` (trivially true for facts).
- Cursor budget: `q.cursors + p.clauseCount ≤ 4096`.
- Optional: only when `p`'s call sites are few (avoid code-size blow-up from
  duplicating `p` into many callers). Heuristic: inline if `p` has ≤ K call
  sites OR ≤ M clauses.

### Concrete emit — a two-pass addition to the caller's compile

The caller `q`'s delegate-top cursor switch is emitted *before* its body, but the
inlined callees (and how many alternative cursors they need) are discovered
*during* body emission. So inlining needs a pre-scan to fix the cursor layout
first. Concretely, in `q`'s compile (`EmitSingleClauseMetaCpBody` /
`EmitTryMeElseChainBody` / `EmitIndexedDispatchBody`):

**Pass 0 — pre-scan + cursor allocation (new).** Walk `q`'s body bytecode; for
every `Call p/n` / tail `Execute p/n` where `p` is an eligible multi-clause fact
(`IsFactPredicate(p)`, `p.ClauseCount ≥ 2`, local, cursor budget ok), record
`(siteOffset, p, K = p.ClauseCount)`. Append `K-1` "inlined-alternative" cursors
to `q`'s cursor space (after `q`'s own clause cursors and its call-site-resume
cursors), giving each site a `baseCursor`. Build `siteOffset → (baseCursor, K,
clauseRanges)`. Check `total cursors < ResumeMarkerCursorStride (4096)`.

**Pass 1 — cursor switch (extend existing).** For each inlined site, add to the
delegate-top cursor dispatch: `if (cursor == baseCursor + j) br
inlinedClauseLabel[site][j]` for `j = 0 … K-2` (the clause-2..K re-entry points).

**Pass 2 — body emit (at the Call/Execute site, replacing the trampoline).**
- Clause 1: if `K > 1`, `engine.PushIlChoicePoint(q-delegate, baseCursor+0,
  q.arity)` (the next-alternative CP — points back at *q*, cursor `baseCursor+0`).
  Then emit `p`'s clause-1 head-match against the argument registers (the `put_*`
  before the call already set them). On match → fall through to the continuation
  (the next opcode in `q`'s body). On head-match failure the `get_*`/`unify_*`
  emitters already branch to `failLabel`; backtracking pops the CP we just
  pushed → re-enters `q`'s delegate at `baseCursor+0` → clause 2.
- At `inlinedClauseLabel[site][j]` (cursor `baseCursor+j`, clause `j+2`): if not
  the last clause, `PushIlChoicePoint(q, baseCursor+j+1, q.arity)`; emit clause
  `j+2`'s head-match; on match → `br continuationLabel[site]`. These bodies are
  emitted after `q`'s main body (like the chain emit lays out clause bodies),
  reached only via the cursor switch.
- `continuationLabel[site]` = the post-call code (already where clause 1 falls
  through). Non-tail: a label right after the site. Tail `Execute`: the
  continuation is proceed/return (tail semantics) — `suppressProceedReturn`
  stays false so the inlined fact's match proceeds like the tail call did.

The argument registers seen by the clause-2..K head-matches are the *original*
call args: the CP save/restore (`PushIlChoicePoint` snapshots arity registers,
backtrack restores them) handles this exactly as a normal multi-clause
predicate's clause backtracking does. No new arg mechanism.

Net effect: `odd(A)` inside `top`'s IL method becomes a small in-method chain
(push CP, unify) — no resume marker, no `IlByFunctorId` index, no delegate
invoke. Backtracking re-enters `top`'s delegate at the alternative cursor (still
one dispatch-loop hop per backtrack — the loop re-invokes the delegate at the
cursor — but the *forward* call and its marker/index/invoke are gone, and the
backtrack stays within `top`'s delegate rather than bouncing to `odd`'s).

Reuses `EnginePushIlChoicePoint` and the head-unify opcode emitters
`EmitIndexedAtomBody` / `EmitTryMeElseChainBody` already use; the genuinely new
machinery is Pass 0 (cursor pre-allocation) and threading the inlined-cursor
labels into each caller-shape's cursor switch (Pass 1).

### Status

- Single-clause inline: done (chunk 69).
- `IsFactPredicate` detector: done (chunk 358), tested.
- **Multi-clause inline MECHANISM: BUILT + validated, gated `SHUMWAY_INLINE_FACTS=1`
  (chunk 359).** The cursor-merging emit for the metaCp caller shape
  (`ComputeInlineSites` pre-scan + alternative cursors in the caller's switch +
  `EmitInlinedFact` chain with CPs pointing at the caller's delegate). **Correct:**
  the full Embedding suite (2099 tests, heavy backtracking/cut/CP coverage) passes
  with the flag ON; hand cases (`gen :- d(X), X>1`, nested `d(X), d(Y), X<Y`) give
  the right answers. The hard part — multi-clause backtracking through an inlined
  fact, merged into the caller's cursor space — works.

- **BUT it does NOT yet WIN — chunk-359 emits a LINEAR try-chain over all K
  clauses, dropping the fact's first-argument indexing.** For a call with a BOUND
  arg (a *test*, e.g. crypt's `odd(G)` where G came from `mult`), the trampoline
  used `switch_on_integer` to jump straight to the matching clause
  (deterministic, no CP); the linear inline instead tries every clause with
  CP push/pop. crypt is a mix of *generate* (unbound → try-all anyway, inline
  neutral) and *test* (bound → indexing matters) calls, and the test calls
  dominate the backtracking, so flag-ON crypt is ~12% SLOWER, not faster.

### Phase 1b — preserve indexing (DONE, chunk 360 — the win)

`EmitInlinedFact` now emits a first-argument index pre-filter for facts whose
every clause has a DISTINCT constant first arg (all integer or all atom —
crypt's odd/even/lefteven): deref X0; if it is the indexed type and BOUND,
switch on the value straight to its single clause (deterministic, NO choice
point) or fail; if UNBOUND, fall to the linear chain (generate); if a bound
non-indexed type, fail. The deterministic clause's head match re-checks the
(already-matched) key and unifies the rest — a non-indexed-arg mismatch fails to
the caller's fail since the unique key leaves no other clause. Reuses the
chunk-348 deref + Cell getters. (`TryGetFactFirstArgKeys` decides eligibility;
non-eligible facts — var-headed or duplicate-key clauses — keep the plain linear
chain.)

**Result: crypt ~23% faster with the flag ON** (interleaved A/B, ON < OFF every
round), and correct (Embedding 2099 green flag-on; top finds the solution).
The local-predicate inliner now BEATS the trampoline on the dispatch-bound
benchmark. Label/local names are per-site-unique (the BaseCursor) — a caller can
inline several facts.

### Full-bench validation (chunk 360 follow-up) — chunk-361 conclusion was WRONG

Chunk 361 ran the 27-program bench OFF vs ON (single-run min-of-2) and concluded
"DEFAULT STAYS OFF — sieve +42%, boyer +15%." **That conclusion was a thermal-noise
artifact and is retracted.** Two facts kill it (chunk 362):

1. **sieve and boyer do not inline at all** — 0 inline sites each (no pure
   multi-clause fact called from a metaCp caller). The reported +42% / +15% were
   pure run-to-run variance (~40% stddev on this laptop) over a NO-OP, byte-identical
   build. [[wallclock-ab-must-be-back-to-back]]
2. **Only crypt and chat_parser ever inline** across the whole set. crypt wins
   repeatably (~25%). chat_parser, measured robustly (min-of-8 interleaved), was
   *within noise* (median ON faster, min ON 12% slower — indistinguishable from
   parity) and its facts are wide grammar facts that gain nothing from the inline.

### Chunk 362/363 — one principled gate + the O(1) cursor jump table → DEFAULT ON

**Chunk 362 (superseded in part):** added two gates — index-eligibility AND a
`clauseCount*(arity+1) ≤ 10` size budget. The budget forced chat_parser to 0
inline sites for a "clean" story, but it was the WRONG fix: it excluded perfectly
normal facts (a 4-clause arity-2 fact costs 12 > 10) on a threshold tuned to two
programs. The premise — "inlining a wide fact costs more than the trampoline" —
pointed at a real cost, but that cost was an **implementation flaw, not an
inherent property of inlining**.

**Chunk 363 — found and fixed the real cost; budget removed.** Inlining should
never cost MORE than the trampoline: same IL context, no dispatch round-trip. It
did, because backtracking re-entered the caller delegate through a **linear
cursor compare-chain** at the top of `EmitSingleClauseMetaCpBody` — and that chain
grows with every inlined clause alternative. An inlined fact's generate path
re-enters once per clause, so a 9-clause fact paid O(cursors) per backtrack, which
the compact callee-side trampoline dispatch did not. Fix: the cursors are dense
small ints from 0, so the compare-chain becomes a single **O(1) IL `switch` jump
table**. Re-entry is now constant regardless of inline-site count, so inlining is
strictly cheaper than the trampoline — and the jump table also speeds the normal
multi-call resume path for every promoted predicate.

With re-entry O(1), the only gate needed is **index-eligibility**
(`TryGetFactFirstArgKeys`): inline a fact only when every clause has a distinct
constant first arg, so the Phase-1b index pre-filter makes a BOUND call
deterministic (the crypt win). A non-index fact would inline as a linear chain with
no indexing gain, so the trampoline keeps it.

Measured (interleaved, OFF = `SHUMWAY_INLINE_FACTS=0`):
- **crypt** ~22% faster (avg ratio 0.782, ON < OFF in 7/8 rounds) — 16 inline sites.
- **chat_parser** neutral (avg 0.976) — its **9-clause** grammar fact now inlines
  with no regression (it regressed before the jump table; the budget had hidden it
  by excluding the fact entirely).
- **nreverse / other non-inliners** unchanged (0 inline sites; jump table ≈ linear
  chain when there are few cursors).

`InlineFacts` defaults ON (`SHUMWAY_INLINE_FACTS != "0"`; set `0` to disable). Full
suite green (Embedding 2103, Compiler 284, Core 428, ISO 277).
[[run-embedding-tests-for-engine-changes]]

LESSON: when a transform that removes work appears to cost more, suspect the
transform's own machinery before adding a heuristic to avoid it. The budget would
have permanently capped the inliner to tiny facts to dodge a bug that a jump table
fixed outright.

### What else can be inlined — Blint survey (chunk 364)

Before closing, surveyed a REAL program (`c:\temp\Blint.pl`, 2571 lines) to see
what the fact inliner reaches and what an extended one would. A diagnostic
(`SHUMWAY_IL_SHAPE=2`, `DiagnoseInlineCandidates`) classifies every non-tail
`Call` site's callee by shape. Result on Blint (forced promotion, goal `test.`):

| callee shape | call sites | distinct preds | inlinable today? |
|---|--:|--:|---|
| `Ncl-rule-nonleaf` (multi-clause rule, body has calls) | 109 | 57 | no |
| `1cl-rule-nonleaf` (single-clause rule, body has calls) | 79 | 34 | no |
| `1cl-rule-leaf` (single-clause rule, body = builtins only) | 29 | 14 | no |
| `Ncl-rule-leaf` (multi-clause rule, body = builtins) | 12 | 8 | no |
| facts (`Nfact-*`) | 3 | 2 | the 1 IDX one |

**The fact inliner is essentially inert on real code** — Blint has 3 fact call
sites vs **229 rule** call sites, and inlines 0 in practice. Real Prolog hot paths
are RULES (head + body), not pure facts. So the meaningful next cases, easiest
first:

1. **Single-clause leaf rules** (29 sites): body is only builtins (arith, compare,
   type tests, unify) — no nested predicate frame, no backtracking. The natural
   generalisation of the chunk-69 leaf inline (head-match-only) to head + builtin
   body — a macro expansion. Lowest risk, no env/CP machinery.
2. **Single-clause non-leaf rules** (79 sites): add a nested env frame + the body's
   own (still-trampolined) calls. Deterministic, no clause backtracking.
3. **Multi-clause rules** (121 sites): the big one. KEY: the multi-clause-FACT
   inliner already built (chunks 358–363) is exactly the structural groundwork —
   cursor-merge, CPs pointing into the caller's delegate, the O(1) cursor jump
   table. Extending to rules = add per-clause env-frame + body-goal emission on top
   of that machinery. So facts were the hard scaffold; rules are the payoff.

(`leaf` here = callee has no non-tail `Call`; a body whose only call is a tail
`Execute` is counted leaf too, so item 1's true pure-builtin subset is somewhat
smaller — refine the diagnostic if scoping item 1.)

## Phase 29 — inlining RULES (chunk 365+)

The chunk-364 survey showed facts are negligible in real code; rules are the
payoff. Phase 29 attacks them easiest-first.

### Case 1 — single-clause LEAF rules (chunk 365)

A single-clause rule whose body is deterministic builtins / arithmetic /
unification only — no env frame, no choice point, no cut, no user call — can be
inlined FLAT exactly like the chunk-69 leaf inline (head-match-only). The
mechanism is *free*: the existing leaf-inline site already calls
`EmitClauseBody(callee, suppressProceedReturn)`, and that emitter already lowers
det `CallBuiltin` (→ `Impl(engine)` + `brfalse fail`), arithmetic and unify
opcodes inline with a fail-to-caller branch. So the whole change is the
eligibility predicate `IsInlinableLeafRule` (single clause; reject
allocate/deallocate, cut/neck_cut/get_level, Call/Execute any tier, and a
`CallBuiltin` to a meta or backtrackable builtin) wired into both the non-tail
`Call` and tail `Execute` inline sites.

Gated behind `SHUMWAY_INLINE_RULES` (default OFF) while validated. Results:
- **Correct** across the whole Embedding (Tier-1) suite with the flag ON, and
  Blint's lint output is byte-identical OFF vs ON.
- **~25 % faster** on a leaf-rule-heavy synthetic loop (`sq`/`pos`/`inc` called
  in a tight loop; min ratio 0.731) — proof the inline fires and pays when the
  leaf rules are hot.
- **Neutral on Blint** (~1.03, within noise). Blint's hot path is the *non-leaf*
  rules (survey: 188 non-leaf vs 41 leaf call sites), which case 1 doesn't touch.

So case 1 is a correct, low-risk mechanism with a real but narrow win; the
default stays OFF until cases 2/3 make a real program move. (A single builtin in
TAIL position compiles to `Execute` not `CallBuiltin`, so `p(X):-integer(X)` is
not yet covered — `EmitClauseBody` has no `ExecuteBuiltin` path; a follow-up.)

### Case 2 — single-clause non-leaf rules (next)

79 Blint call sites. Adds a nested env frame + the body's own (still-trampolined)
calls. Deterministic, no clause backtracking. This is where Blint's time is.

### Case 3 — multi-clause rules

121 Blint call sites. Per-clause env-frame + body-goal emission on top of the
fact inliner's cursor-merge / CPs-into-caller / O(1) jump table machinery.

## Remaining (other follow-ups)

- The det-path index pre-filter is a linear key-compare scan (O(clauses)); fine for
  the handful-of-clauses facts seen so far, but a very large index-eligible fact
  called in bound mode would want a jump table / binary search there too (the
  callee trampoline uses `switch_on_integer`). No cap is imposed now — add one only
  if a real large-fact regression appears.

- Extend beyond the metaCp caller shape (try-me-else chain / indexed callers
  still fall back to the trampoline at their inlinable sites).
- Phase 1b's index pre-filter handles the unique-constant-key shape; a fact with
  duplicate first-arg keys or a catch-all var clause keeps the linear chain
  (correct, just not indexed) — generalising it (reuse `IlIndexGraph` fully) is a
  later refinement.

## Risks / open questions

- **Cursor accounting.** The merged cursor space must be assigned without
  collision; the resume-marker re-entry for the inlined alternatives must decode
  to the right place. Highest-risk correctness area.
- **Locality plumbing.** Thread `PredicateVisibility` from the linker into
  `CompiledPredicate` / the IL `calleeMap` so the inliner knows what is local.
- **Code-size / cursor-budget blow-up.** A guard (max clauses, max call sites,
  cursor budget) is mandatory.
- **Interaction with `--strip-wam` + persisted IL.** An inlined callee's WAM may
  be strippable only if no non-inlined caller / external caller needs it; the
  inliner must not strip a predicate that still has a standalone delegate.
- **Cut scope** inside an inlined multi-clause rule (Phase 3) — B0 must be the
  level at the inlined entry, not `q`'s entry.
- **Measurement.** Validate on crypt (expect the ~32% dispatch to drop for the
  inlined local generators) and guard against regressions on
  non-call-heavy programs (the inliner must be a no-op there).

## Why this is the right next big lever

The structural wins already banked — env allocation (351/352, ~6.5× on
permanent-heavy loops) and arithmetic try/catch (354/355) — came from removing
*structural overhead*, not real work. The trampoline dispatch is the last large
structural overhead: for call-heavy / backtracking programs (a large fraction of
real Prolog, and exactly where Shumway targets GProlog) it is ~⅓ of the time.
Inlining module-local predicates is the WAM-level analogue of GProlog's single
flat native code space, recovered for everything intra-module — the part a
managed runtime *can* do (a method can hold many predicates' worth of IL and
`br` between them; it cannot be one giant method for the whole program, but a
module-local cluster is bounded).
