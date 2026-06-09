# Tier-1 IL region compilation (flat local code space)

**Status**: proposed design (Phase 29). Supersedes the body-duplication inliner
(chunks 358–368) as the mechanism for real programs; the duplication inliner is
kept for the degenerate tiny case (a single-clause thin wrapper, `a:-b. b:-c.` →
`a:-c`).

## Problem

The Tier-1 trampoline makes every IL→IL call a round trip through the bytecode
dispatch loop (`set Cp=marker; return; loop decodes; re-invoke delegate`). For a
call-heavy program that dispatch is a large fraction of runtime. GNU Prolog has no
methods — all predicates are labels in one native blob and a call is a `jmp`. IL
cannot replicate that across methods (no cheap cross-method jump; 64 KB method
limit; separate delegates), **but `br` IS a cheap intra-method jump.**

The body-duplication inliner (chunks 358–368) removes a call by splicing the
callee's body into the caller. That duplicates code at every call site and, over a
tree of locals, flattens the whole tree per occurrence — `a:-b,c,b,d` with
`b:-b1,b2` etc. becomes `a:-b1,b2,c1,c2,b1,b2,d1,d2`, with `b`'s body twice and the
leaf calls still trampolining. It only pays for tiny callees.

## Idea

Compile a **region** — a root predicate `a` plus its transitively-reachable LOCAL
callees, up to an IL-size budget — into **one IL method**, where each region member
is a **labeled block emitted once** and a call between region members is a **`br`**
plus a return cursor. `a:-b,c,b,d` becomes one method:

```
method(Engine e, int cursor):
  int cur = cursor;
dispatch:                      // the region's single jump table (IL `switch`)
  switch (cur) { … }           // → block entries / return conts / resume / clause alts
A_entry:   …put b-args…; e.SetB0(e.B); e.SetCp(rmark(R1)); br B_entry
R1:        …put c-args…; e.SetB0(e.B); e.SetCp(rmark(R2)); br C_entry
R2:        …put b-args…; e.SetB0(e.B); e.SetCp(rmark(R3)); br B_entry   // same block
R3:        …put d-args…; e.SetB0(e.B); e.SetCp(rmark(R4)); br D_entry
R4:        …a's proceed…
B_entry:   …b1…; …b2…; br ret                                          // emitted ONCE
C_entry:   …c1…; …c2…; br ret
D_entry:   …d1…; …d2…; br ret
ret:       cur = decode(e.Cp); if region-internal { br dispatch } else { return to loop }
```

`b` appears once; the two calls are two `br B_entry` with different return cursors.
The forward call and the return are `br` — no dispatch-loop round trip. This is the
flat local code space, within an IL method.

## Region selection

- **Root**: the predicate being promoted to Tier-1 (`a`).
- **Members**: BFS/DFS over LOCAL (module-private, non-dynamic, IL-eligible) call
  edges from the root. A call to a region member → `br` to its block; a call to a
  non-member → trampoline (cross-region) or `CallBuiltin`.
- **Budget**: stop adding members when the summed IL size crosses a budget well
  under the 64 KB method limit (and below Sigil's `ReturnTracer` stack ceiling —
  see `MaxIlPromotionBytecodeBytes`). Calls past the budget stay trampolines.
- **Cycles / recursion**: a call to an already-included member is a `br` to its
  existing block — no re-expansion. So a recursive or mutually-recursive local
  cluster collapses to br edges.
- **Why local-only**: a public predicate can be called from outside the region and
  must keep its standalone delegate; it stays a trampoline target. A local member
  may ALSO be compiled standalone (for callers outside this region) — its block
  here is a per-region copy, but bounded (one per region that reaches it).

## Cursor space

One cursor space per region (generalising the current per-predicate space). A
cursor identifies a label the method can be (re-)entered at:

- **block entry** — one per region member (so a backtrack / external call lands at
  the member's start);
- **return continuation** — one per intra-region call site (where to continue after
  the callee returns);
- **resume point** — one per cross-region / backtrackable-builtin call (the existing
  Phase-16 forward-resume cursor);
- **clause alternative** — one per non-last clause of a multi-clause member (the
  existing fact/chain backtrack cursor).

The jump table (`dispatch`) routes `cur` to the matching label. `cur` is set from
`arg1` on a method invocation (initial call / backtrack re-entry from the loop) or
from the decoded `Cp` on an intra-region return.

## Call and return mechanism

**Intra-region call** (member X calls member Y at a non-tail position):
```
…put Y's args into X registers…
e.SetB0(e.B)                              // cut barrier for Y (as a real call does)
e.SetCp(EncodeResumeMarker(regionRootFid, returnCursor))
br Y_entry                                // direct intra-method jump
markLabel(returnCursor)                   // the continuation
```
No `IlTailCallPending`, no `return`. Just a branch.

**Return** (a member's `proceed` / `deallocate_proceed`):
```
cur = DecodeCursor(e.Cp)                  // the return marker
if (IsRegionMarker(e.Cp, regionRootFid))  // continuation is inside THIS region
    br dispatch                           //   → switch routes to the return cont
else
    e.SetPc(e.Cp); e.IlTailCallPending = …; return true   // cross-region → loop
```
One shared `ret`/`dispatch` tail handles every member's proceed. The intra-region
case is a `br`; only a return whose continuation lives in another method falls back
to the loop.

**Cross-region call / backtrackable builtin**: unchanged — the Phase-16 threaded
trampoline (set `Cp` = region resume marker, set `Pc` = callee marker, return to
loop), with the resume cursor in this region's space. **Deterministic builtin**:
`CallBuiltin` inline as today.

**Tail call** at the region root's own tail position: a real tail call (the root's
last goal), emitted as today (the root IS the method's contract). A member's tail
call to another member is a `br` to that member's block (the return cursor is the
member's caller's continuation, threaded via `Cp`).

## Choice points & backtracking

Unchanged in spirit: a CP carries `(delegate, cursor)` = `(this region's delegate,
a region cursor)`. A member that pushes a CP (multi-clause try_me_else, a
backtrackable builtin) saves `Cp`/registers as today. On backtrack the engine
re-invokes the region delegate with `arg1 = cursor` (through the loop) — landing at
`dispatch`. So **forward control is `br` (cheap); backward control (backtrack) still
goes through the loop** (acceptable — backtracking is the less-hot direction, and
its continuations must be reconstructable from the saved CP regardless).

## Cut

A member's deep cut is self-contained via a Y-slot (`get_level` captures `_b0`,
`cut slot` cuts to it) — exactly the chunk-367 result. The intra-region call sets
`B0 = e.B` before the `br`, so the member's captured barrier prunes only its own
choice points. Neck cut cuts to `_b0` (= the call's `B`), correct at the member's
neck.

## Registers & frames

The WAM X/Y registers are engine state, not IL locals, so merging member bodies
into one IL method introduces **no new register collision**: the WAM compiler
already saves values live across a call into permanents (Y-slots) and reloads them.
An intra-region `br` reaches a member block with exactly the register/frame state a
real call would. Each member's `allocate`/`deallocate` manages its own frame
(nested as today). The method's own IL locals (`cur`, scratch) are disjoint from
WAM state.

## Boundary conditions

- **Dynamic predicates**: never region members (mutation-driven dispatch must stay
  Tier-0; chunk 159 invariant). Cross-region trampoline.
- **Public predicates**: never region members (callable from elsewhere). Trampoline.
- **Persisted bundles**: the region method bakes the root fid into markers; the
  patch-site mechanism (Phase 17) already remaps fids. Members reached only inside
  the region need no separate persisted entry, but their standalone delegate (if any
  external caller exists) is persisted as today. **Out of scope for the first
  cut — runtime DynamicMethod path only**, like the chunk-367 rule inline.
- **Budget exceeded / non-IL-eligible member**: that edge stays a trampoline; the
  region is just smaller.

## Relationship to the existing inliner

- **Keep** the chunk-69 leaf inline and the tiny single-clause-rule duplication
  (`a:-b. b:-c.` → splice `b`'s body) — for a thin wrapper, splicing is smaller than
  a block + return cursor, and there's no sharing to lose.
- **Replace** the chunk-358–368 multi-clause-fact / rule duplication inliner's role
  for real programs with region compilation. (The fact inliner can stay gated for
  the crypt-style tiny-generator case, or be subsumed — decide after measuring.)

## Implementation plan (incremental, each validated on the Embedding Tier-1 suite)

1. **Region discovery** — ✅ DONE (chunk 370). `IlRegionBuilder.Build(root,
   calleeMap, budget, extraEligible)` → ordered member list (root first, BFS),
   `IsIntraRegion(fid)`, `TotalBytecodeBytes`. Cycles → not re-expanded; budget →
   edge stays a trampoline; dynamic / non-compiled / `extraEligible`-rejected →
   excluded. `IlRegion` + `IlRegionBuilder` in `IlRegion.cs`; 9 Chunk370Tests.
   Blint sizing (`SHUMWAY_IL_SHAPE=3`, IL-eligible members): **61 predicates have a
   non-trivial local closure**, uncapped up to **76 members / 17.7 KB bytecode**;
   at the default 3072-byte budget, ~10–18 members. So real programs have rich
   local clusters and **the budget is the binding knob** (coverage vs method size;
   ~3–4× bytecode→IL expansion → 3072 B ≈ 10–12 KB IL, safe). One root already
   exceeds the budget alone (no region) — large roots want a higher budget or to
   stay un-regioned.
   **Budget knob**: the budget is configurable via the `SHUMWAY_REGION_BUDGET`
   env var (`IlRegionBuilder.DefaultBudgetBytes`); a CLI / compiler aggressiveness
   option can map onto it. The budget is the prune point — a member that would push
   the region past it stays a trampoline boundary, so a region that would overflow
   is pruned and the un-pulled callees are ordinary (visible) predicates. A real
   post-emit IL-size guard (fall back if the EMITTED method nears 64 KB, vs the
   current bytecode proxy) is Stage 7.
2. **Cursor planner** — ✅ DONE (chunk 371). `IlRegionPlanner.Plan(region)` →
   `IlRegionPlan`: cursor 0 = root entry; each non-tail `Call` gets the next cursor
   (intra-region → `IntraCallReturn`, cross-region → `CrossCallResume`), walked per
   member in region order, per call site in pc order — the exact order the emit
   consumes, so the dispatch jump table and the emit agree by construction. Tail
   `Execute` takes no cursor (intra = `br`, cross = tail trampoline) — **the region
   model needs no chunk-368 un-tailing**. `RegionCursorKind` / `RegionCursorSite` /
   `IlRegionPlan` / `IlRegionPlanner` in `IlRegion.cs`; 5 Chunk371Tests. Stage-2
   scope is single-clause members' non-tail calls; multi-clause clause-alternative
   cursors and backtrackable-builtin resume cursors are added with those shapes
   (Stages 4+).
3. **Two-member skeleton** — root + one LEAF local member, no backtracking, no cut:
   emit the single method with `dispatch`, `br`-call, `ret`-decode. Validate
   answers vs trampoline.
4. **Intra-region backtracking** — a multi-clause member as a block; CPs re-enter
   `dispatch`. Validate enumeration + a caller CP surviving.
5. **Cut within the region** — reuse chunk-367 barrier scoping. Validate soundness
   (the discriminating `member`-survives-cut cases).
6. **Cross-region calls + builtins** inside the region; **recursion/cycles** (br to
   existing block).
7. **Budget + method-size guard**; fall back to trampoline past the budget.
8. **Gating + full-bench validation + measurement**; decide the default flip and
   whether the duplication inliners are subsumed.

## Risks

- **Cursor accounting** must be exact (Sigil errors loudly on a mismatch — a good
  property). The crux is the planner ↔ emit agreement.
- **Soundness of backtrack re-entry** across the merged region — the
  [[extra-backtracking-not-sound]] class; validate with discriminating findall
  cases at every step.
- **Method size** — a large region risks the 64 KB / ReturnTracer limits; the budget
  must be conservative and measured.
- **It may not beat the trampoline by much** if backtracking dominates (backtracks
  still use the loop). Measure on a forward-call-heavy region first (the case it
  targets).
