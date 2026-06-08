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
head-match-only, e.g. a fact with one clause) at **tail** (`Execute`) sites —
`EmitClauseBody` emits the callee's body inline. This design generalises that.

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

Emit (replacing the `Call p/n` trampoline at the site):
1. Allocate `q`-cursors `c_1 … c_{N-1}` for `p`'s clause-2..N alternatives, and a
   continuation cursor / label `c_cont` for "p succeeded, run q's next goal".
2. Emit `p`'s clause-1 entry: push an IL CP `(q's delegate, c_2)` (the next
   alternative) if N>1, unify the head args against the argument registers, on
   success `br c_cont`'s body, on failure `br` to the fail handler (which
   backtracks — popping the CP we just pushed re-enters at `c_2`).
3. At cursor `c_k` (k = 2..N): push CP `(q, c_{k+1})` if not last, unify clause
   k's head, on success `br c_cont`'s body.
4. `c_cont`: the caller's continuation — the IL for `q`'s goal after `p(X)`.
   (Already emitted by EmitClauseBody as the post-call code; the inline just
   reaches it by fall-through instead of via a resume marker.)
5. Reuse `EnginePushIlChoicePoint` and the head-unify opcode emitters that
   `EmitIndexedAtomBody` / `EmitTryMeElseChainBody` already use — Phase 1 is
   largely "emit the callee's chain body, but with the caller's delegate + the
   caller's cursor offsets, branching to the caller's continuation on match."

Net effect: `odd(A)` in crypt becomes, inside `top`'s IL method, a small chain
that pushes a CP and unifies — no marker, no `IlByFunctorId`, no delegate invoke.
Backtracking re-enters `top`'s delegate at the alternative cursor (still one
dispatch-loop hop per backtrack, but the forward path and the inter-predicate
call are gone).

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
