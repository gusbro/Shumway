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
   - **Foundation DONE (chunk 372)**: `Engine.RegionReturnCursor(regionRootFid)` —
     the `ret` handler's Cp-decode. At a member's proceed: if `Cp` is a resume
     marker into THIS region → the return cursor (intra-region, the emit `br`s to
     `dispatch`); else −1 (cross-region, the member returns true and the loop runs
     `Cp`). Tested in isolation (Chunk372Tests). Confirms the dispatch protocol: the
     loop, after `del(e,cursor)` returns true with `IlTailCallPending` false, does
     `SetPc(Cp)` — so a cross-region return needs only `return true`.
   - **Method emit DONE (chunk 373)**: `CompileRegion(region, plan, calleeMap)` emits
     the region method — a `cur` local seeded from `arg1`; a `dispatch` IL `switch`
     over the plan's cursor space (0 = root entry); each member as a labeled block;
     a shared `ret` handler (`cur = RegionReturnCursor(e, fid); if ≥0 br dispatch;
     else return true`). The body emit reuses `EmitClauseBody` with a
     `RegionEmitContext`: `TryEmitRegionOpcode` rewrites proceed/deallocate_proceed →
     `br ret`, an intra-region non-tail `Call` → `SetB0` + `SetCp(return marker)` +
     `br member` + the return-continuation label, an intra-region tail `Execute` →
     `SetB0` + `br member` (Cp unchanged); every other opcode (head match, unify,
     arith, allocate/deallocate, deterministic builtin) is emitted unchanged.
     `IsStage3RegionEmittable` gates the minimal subset (≥2 members, all
     single-clause, intra-region calls + det builtins, no cut). Gated
     `SHUMWAY_REGION=1`. Validated: minimal `go→a→{b,c}` and value-flow
     `a(X,Y):-b(X),c(Y)` give identical answers to the trampoline; the **full
     Embedding suite is green with SHUMWAY_REGION=1 (2147)**. 5 Chunk373Tests.
     Stages 4–6 add multi-clause members (backtracking), cut, and cross-region calls.
4. **Intra-region backtracking** — a multi-clause member as a block; CPs re-enter
   `dispatch`. Validate enumeration + a caller CP surviving.
   - **Planner extension DONE (chunk 374)**: a multi-clause member's non-first
     clauses (1..N-1) each get a `ClauseAlt` cursor in the region cursor space
     (assigned before the member's call cursors, in clause order). The member's
     clause dispatch will push a choice point carrying it; a backtrack re-enters the
     region method at that cursor (→ `dispatch` → the next clause). `RegionCursorKind.
     ClauseAlt` + `RegionCursorSite.ClauseIndex`; tests in Chunk371Tests. Stage-3
     emit unaffected (its single-clause regions have no clause-alts; `cursorBySite`
     keeps only call cursors).
   - **Dispatch emit DONE (chunk 375)**: `EmitRegionMultiClauseMember` emits a
     try_me_else-chain member block — clause 0 at the member-entry label, clauses
     1..N-1 at their `ClauseAlt` cursor labels; each clause except the last pushes
     `PushIlChoicePoint(region delegate, next-clause cursor, arity)` before its
     region-aware body, so a head-match failure → region fail → `return false` →
     backtrack → the CP → re-enter the region method at the next clause via
     `dispatch`. `CompileRegion` switched to the holder pattern (`SelfFromHolder` +
     `IndexedDelegateHolder.Register`) for the self-delegate. `IsRegionEmittable`
     (renamed) admits a multi-clause member iff it is a plain try_me_else chain
     (indexed / switched dispatch deferred to Stage 6). **Validated sound** on
     discriminating findall: a 3-clause chain member called intra-region enumerates
     all 3 (tail and non-tail), and a caller `member(S,[s1,s2])` CP created BEFORE
     the call survives the member's backtracking (`[s1-1,…,s2-3]`). **Full Embedding
     suite green with SHUMWAY_REGION=1 (2153)** — the multi-clause path is correct
     across the Tier-1 suite's many backtracking predicates.
     (Note: integer/atom facts like `p(1).p(2).p(3).` compile to INDEXED dispatch,
     not a chain, so they're Stage 6, not Stage 4 — a chain needs non-indexable
     heads, e.g. `gen(R):-R=1. gen(R):-R=2.`)
5. **Cut within the region** — ✅ DONE (chunk 376). The intra-region call already
   emits `SetB0(e.B)` before the `br`, so a member's deep cut (allocate_get_level
   captures `_b0` into a Y-slot; cut slot cuts to it) and neck cut prune only the
   member's own choice points — including its clause-alternative CPs — exactly as a
   real call would (chunk-367 barrier scoping). The change is just removing the cut
   reject from `IsRegionEmittable`; `EmitClauseBody`'s normal NeckCut/GetLevel/Cut/
   AllocateGetLevel handlers emit the cuts unchanged. Validated SOUND with the region
   firing: `root(X):-gen(X),!` cuts the chain member `gen` to its first solution
   (`[1]`), and a caller `gen(S)` CP created BEFORE the call survives the cut
   (`[1-1,2-1,3-1]`). Full Embedding suite green at SHUMWAY_REGION=1 (2153).
   - **Pre-existing bug fixed along the way**: `CanCompileSingleClause` did not treat
     `deallocate_proceed` as a terminator (it was caught by the earlier
     `IsSupportedOpcode` branch, which doesn't record the terminator), so a
     single-clause body with a frame ending in a non-tail-call goal (a cut or a
     builtin) — e.g. `p(X):-a(X),!.` — was wrongly rejected as cannot-compile. Moving
     the `DeallocateProceed` terminator check before `IsSupportedOpcode` lets those
     promote standalone AND become region members (without it, cut-bearing members
     were excluded from regions as cross-region). Region-off Embedding 2154 green.
6. **Cross-region calls + builtins** inside the region; **recursion/cycles** (br to
   existing block).
   - **Cross-region calls DONE (chunk 377)**: a member's call to a NON-member (a
     dynamic / public / budget-pruned / not-yet-handled callee) stays the Phase-16
     trampoline but with the plan's `CrossCallResume` cursor — `SetB0`; `SetCp(region
     resume marker)`; `SetPc(callee entry marker)`; `IlTailCallPending`; `return` for
     a non-tail Call (the loop re-enters the region at the cursor when the callee
     proceeds); a tail Execute is the tail-trampoline (Cp unchanged). `IsRegionEmittable`
     now admits cross-region Call/Execute. So a region no longer needs to be a CLOSED
     local cluster — it can call out. Validated SOUND: a member's cross-region call to a
     dynamic `d/1` enumerates (`[1,2,3]`) and a caller `gen(S)` CP survives the
     cross-region enumeration (`[a-1,…,b-3]`). Full Embedding green at SHUMWAY_REGION=1
     (2153) — much broader region coverage now that cross-region calls don't reject.
     Recursion/cycles already work (the discovery br's to the existing block).
   - **Indexed-dispatch members — EMITTED in-region (chunk 381, Stage 6c).** An
     indexed (switch_on_term/arg) callee is now a full region member, not a
     trampoline boundary. `EmitRegionIndexedMember` is the region analog of
     `EmitIndexedDispatchBody`: the member-entry label holds the inline index decision
     (`TryEmitInlineIndexResolve`, the compile-time index graph lowered to deref +
     tag/key branches) which branches forward to a chain node's label; each node
     pushes the region delegate's choice point carrying the NEXT node's region cursor
     (a bucket-chain backtrack re-enters via the dispatch switch) then branches to its
     clause body; bodies are emitted region-aware (proceed → `br ret`, intra calls →
     `br`) exactly like every other member. The planner gains a
     `RegionCursorKind.IndexNode` (one cursor per dispatch node, replacing the
     try_me_else chain's clause-alts); `IlRegionPlanner.Plan` takes an
     `indexNodeCount` callback. `IsRegionEmittable`/`IsRegionMemberEligible` now admit
     indexed members (validating the CLAUSE BODY ranges, since the resolve replaces
     the dispatch cascade). The index resolve's labels/locals are salted per member
     (`_rm{mi}`, same fix class as chunk 380). VALIDATED SOUND across atom/integer/
     struct index node kinds: a bound key dispatches deterministically, an unbound key
     enumerates all clauses via the node CPs, and a caller CP created before the
     indexed call survives the full index-node backtracking (`co(S,K,V)` =
     caller-`gen(S)` × every indexed answer). clpfd repro still `b`. **Blint regions
     jump from 2 → 24** (now incl. indexed members `parse_subgoal1x10`,
     `lint_msg_textx16`, `parse_subgoal_contx4`, regions up to **17 members**), output
     byte-identical OFF vs ON. Embedding default 2154 green, REGION=1 all real pass.
   - **Wakeup flush at region boundaries DONE (chunk 379).** A correctness piece: the
     interpreter flushes pending attribute wakeups at every
     Call/Execute/Proceed/Deallocate goal boundary; IL relies on control passing
     through the dispatch loop between trampoline calls to get those flushes — but an
     intra-region `br`-call/return bypasses the loop. So the region flushes
     (`EmitRegionWakeupFlush` = `FlushWakeupsForIlCut; brfalse fail`) at its OWN
     boundaries: before every `br`/trampoline call and at every proceed (same class as
     the chunk-339 IL-cut flush). Harmless for non-attvar code (a
     `_pendingWakeups.Count==0` fast path).
   - **The clpfd-in-region bug is FIXED (chunk 380) — it was a local-name collision,
     exactly the "bug not barrier" the merge model predicts.** With indexed members
     excluded, a 24-member clpfd-internal region (root `in/2`) formed and `X in 1..5,
     m(X,R)` FAILED where it should give `R=b`. Root cause, isolated with a
     deterministic BFS-order member cap: two members each with a `put_variable` at
     pc 0 (here `clpfd_makevar` and `clpfd_dom_of`) both declared the Sigil local
     `freshRef_pc0` — a pc-based local name is unique *within a single predicate* (pc
     starts at 0) but **collides across members merged into one IL method**
     (`InvalidOperationException: Local with name 'freshRef_pc0' already exists`),
     which aborted the region compile and left `in/2` failing. NOT an attvar/trail/cut
     interaction at all — the wakeup flush was a real but separate fix. The fix:
     `EmitClauseBody` salts every per-member-emitted local name with the member index
     (`_rm{CurrentMemberIndex}`) when `regionCtx != null` — the five pc-based locals
     (`freshRef`, `freshRefY`, `preE_alloc`, `preE_dealloc`, `metaCallTarget`). This
     is the local-naming half of making "N IL methods → 1 IL method is opaque to the
     engine" actually true. Validated: `X in 1..5, m(X,R)` → `b`, the 2 chunk-339
     clpfd cut+wakeup tests pass under `SHUMWAY_REGION=1`, full Embedding green at
     `SHUMWAY_REGION=1` (only `Chunk373.Flag_DefaultsOff` "fails", correctly, because
     the env var overrides the default-off flag it asserts).
   - **Backtrackable-builtin members — excluded from membership (chunk 385, Stage 6d,
     "path 1").** A member whose emitted body calls a backtrackable / meta builtin
     (`retract`, `atom_concat` via `concat`, `call` via `once`, `s_get_char`, ...) needs
     a resume cursor the region planner doesn't yet allocate, so `IsRegionEmittable`
     rejected the whole region if any member had one — and on Blint ONE such member
     poisoned the region for **60 local-closure predicates** (e.g. `blint_file/1`'s
     22-member region died on member `parse_pred/3`'s `retract`). Path 1: refuse such a
     callee MEMBERSHIP (`IsRegionMemberEligible` now runs the shared `RegionMemberOk`
     per-member check) so it stays a cross-region trampoline (Stage 6a) and the rest of
     the region still forms. `RegionMemberOk` is the per-member validation factored out
     and shared with `IsRegionEmittable`. Result: **Blint regions 24 → 55** (skips 60 →
     4; the 4 left are predicates whose OWN root body has a backtrackable builtin — they
     can't be a region root without the resume-cursor threading, and stay standalone).
     `blint_file/1` is now absorbed into `blint_file_start`'s 21-member region. Blint
     byte-identical OFF vs ON; Embedding default 2157, REGION=1 all real pass. The
     `[region-skip]` diagnostic (chunk 384, SHUMWAY_IL_SHAPE=1) reports the cause for any
     local-closure predicate that still isn't a region.
   - **Backtrackable-builtin / meta-call members — INCLUDED (chunk 424, Stage 6d
     "path 2").** The planner gained `RegionCursorKind.BuiltinResume`: one cursor per
     backtrackable-builtin or `call/N`/`'$call'/2 ` site in a member's body (keyed
     (member, pc), merged with the call cursors in pc order). The emit threads the
     existing mechanisms with the REGION's identity: a backtrackable builtin pre-sets
     `BuiltinReturnPc = EncodeResumeMarker(regionRootFid, cursor)` (chunk 218); a
     non-tail meta-call sets `Cp` to the same marker shape (chunk 182) — either way the
     dispatch loop re-enters the region method at the post-site switch slot. The
     chunk-385 membership refusal and the `RegionBodyOpcodesOk` rejection are gone:
     a predicate whose body retracts / between's / meta-calls is now a full member
     (and a valid root). Blint: **0 region-skips** (was 4 roots + per-member
     exclusions); 52 region roots (fewer than 55 because former trampoline-boundary
     callees are now absorbed into bigger regions). Validated: 9 Chunk424Tests
     (builtin enumeration re-entering the region, clause-CP × builtin-CP cross
     product, retract members, tail + non-tail meta-call members, cursor
     interleaving, cut pruning the builtin CP, caller-CP survival), full suites,
     Blint byte-identical in REPL and `--strip-wam` persisted bundle.
   - **State (post-385)**: region compiler is correct + validated through Stage 6d —
     discovery / planner / single-clause / try_me_else-chain / cut / cross-region /
     indexed members / backtrackable-builtin-member exclusion — all sound (Embedding
     default 2157, REGION=1 all real pass; Blint **55 regions** byte-identical). The
     merge being opaque to the engine is confirmed three times over (local-collision,
     index-resolve naming, and now the membership-exclusion trick — all gaps, not
     barriers). Coverage on Blint is now broad (55 of ~95 promotable predicate clusters).
     **Real-world wall-clock payoff still NOT demonstrated** — the chunk-382 A/B (at 24
     regions) showed no win because Blint's hot path is parsing, not call dispatch; more
     coverage doesn't change that bottleneck. The infrastructure is kept and broadened
     deliberately as the substrate for: (a) OTHER real programs that MAY be call-bound;
     (b) Stage 9 module-level dead-region elimination; (c) further IL-level optimisation
     passes that want whole-closure-in-one-method as their unit. The remaining membership
     gap is root-body backtrackable builtins (the 4) + the proper resume-cursor threading
     that would let a backtrackable builtin live INSIDE a member (path 2).
7. **Budget + method-size guard**; fall back to trampoline past the budget.
8. **Gating + full-bench validation + measurement**; decide the default flip and
   whether the duplication inliners are subsumed.
   - **MEASURED on Blint (chunk 382): NO clear win — default stays OFF.** Same binary,
     `SHUMWAY_REGION=1` toggle, `SHUMWAY_IL_PROMOTE=1` both sides (Tier-1 trampoline vs
     Tier-1 region), interleaved min-of-N on the `SHUMWAY_TIMING=1` exec phase (97.5% of
     total). **One-shot exec (N=1, includes JIT): region LOSES ~19%** (OFF min 4153 ms,
     ON min 4945 ms) — the penalty is JIT compiling region's big methods (up to 17
     members each). **Steady-state per-lint (slope (exec@N12−exec@N2)/10, JIT removed):
     WITHIN NOISE** (OFF min 995 ms, ON min 943 ms; ranges 995-1288 vs 943-1153 overlap).
     JIT is ~3.4 s one-time vs ~1 s/lint steady-state, so a one-shot is JIT-bound and
     region worsens it. Why no dispatch win: Blint's hot path is parsing
     (unification / list / char-IO), not predicate-call-bound, and the Phase-16 threaded
     trampoline is already cheap. Region is correct, broad-coverage infrastructure with
     no demonstrated payoff — the same outcome as the duplication inliner; mechanism
     correctness ≠ speed. The remaining identified gap vs GProlog is the WAM register
     allocator (chunk 347b), which is where Blint's time actually goes.
9. **Module-level dead-region elimination — prune inaccessible regions at `.shum`
   link time (prerequisite for IL inspection).** When the linker assembles a `.shum`,
   it knows the complete picture the runtime never has in one place: every entry point,
   every public/dynamic predicate, and — once regions are built — which local predicates
   are reached ONLY as absorbed `br` members of another region. Such a local predicate's
   standalone delegate is **unreachable** and must be **pruned** from the bundle. This
   is a reachability **fixpoint closure**: keep public / visible / dynamic / entry-point
   predicates and any predicate reached by a NON-inlined (trampoline) edge (un-pulled by
   budget or non-local); drop a local predicate reached only by inlined (`br`) edges;
   iterate, since dropping one may expose another. This is a LINK-time pass (the bundle
   path), NOT the runtime promotion path (where standalone delegates coexist lazily). It
   both shrinks the compiled module AND produces the **real, minimal set of
   regions/predicates** — the prerequisite for inspecting the IL that actually ships
   (Stage 11): `shumway-compile --dump-il` (chunk 386) compiles EVERY predicate as a
   root, a superset; only the post-prune link-time set is what runs.
   - **9a — the reachability analysis DONE (chunk 388).** `RegionReachability`
     (`src/Shumway.Compiler.Il/RegionReachability.cs`): a pure fixpoint over region
     roots. `TrampolineReachable(predicates, externallyReachable, regionMembers)` seeds
     with the external roots, and for each live root follows only its region's
     CROSS-region (trampoline) edges — a member's call to a predicate the region does
     NOT absorb — to discover more live roots; `Prunable` is the complement.
     `regionMembers` is `IlPredicateCompiler.RegionMemberFids(root, calleeMap)` (the
     absorbed-fid set the compiler actually emits, or `{root}` when the region isn't
     emittable). 7 synthetic Chunk388Tests (chain-absorbed → prune interior;
     cross-region callee kept; absorbed-but-public kept; diamond; split-caller kept;
     builtin callee ignored; cycle terminates). `shumway-compile --prune-report` is the
     module dry-run (roots = call-graph roots): **Blint = 67 of 256 predicates prunable**
     (~26%), e.g. `blint_file` (absorbed into `blint_file_start`'s region).
   - **9b — wire the prune into the bundle build (PLANNED).** Two prerequisites the
     analysis does NOT yet have: (i) ✅ **DONE (chunk 391)** the bundle IL path must
     actually compile in REGION mode — `EmitPersistedMethod` gained a region branch
     (gated by `RegionCompile`, mirroring the runtime `Compile` integration) via a
     shared `EmitRegionInto`; the region's functor-id / resume-marker uses already go
     through the chunk-194 patchable helpers, so persisted region methods patch
     cross-process. Validated: a region program round-trips through a
     `--with-compiled-il` + `SHUMWAY_REGION=1` bundle and runs correctly cross-process
     (caller CP survives); Blint emits 255 region methods and its bundle output is
     byte-identical to the Tier-0 reference. The with-region bundle is ~2.3× bigger
     (all-as-roots duplication) — exactly the bloat the prune removes. (Without the
     prune this is correct but not yet a size win.) And (ii) the
     linker must compute the complete **externally-reachable seed set** and pass it as
     `RegionReachability`'s `externallyReachable` argument. That set is everything
     callable BY NAME from outside the region-absorption world — a soundness
     over-approximation, NOT just "all reachable":
       - entry points (`--entry`),
       - public predicates (`:- public` — the global namespace; another module / the
         embedding host calls them by name),
       - dynamic predicates (`:- dynamic`) AND visible predicates (`:- visible`, a
         semantic alias of dynamic, chunk 265) — both are called by name and are never
         region-compiled (they open with `enter_dynamic`), so they always keep a
         standalone form,
       - `:- ensure_linked` indicators and any other runtime meta-call (`call/1`) target
         the linker can identify — invoked by name at run time.
     A seed is needed both to KEEP the predicate itself and because the analysis follows
     a seed's cross-region edges to keep its callees alive (a dynamic predicate that
     reaches P only via meta-call would otherwise let P be wrongly pruned). The linker
     already builds most of this (its module-reachability walk roots from entry points +
     `ensure_linked` + qualified refs, and `ShmoObject.Defined` carries public / dynamic
     / visible). The dry-run (`--prune-report`) approximates the seed set with the
     call-graph roots; the real linker uses the set above.
   - **THE PRUNE RULE (soundness — do NOT harden the `ensure_linked` contract).** Prune
     P **only if P was ABSORBED as a `br`-member of some live region** and is not
     otherwise reachable as a standalone. The prune set is exactly
     **`fullReachable − regionReachable`** (reachable via ALL static edges, minus
     reachable via cross-region edges only = "live but reached only as an absorbed
     member"). **Never prune a predicate that was part of NO region** — i.e. never touch
     the unreachable / dead-code bucket. This is what keeps the prune sound WITHOUT
     forcing every meta-call target to be declared: a static-local predicate reached only
     by a runtime (variable) `call(Goal)` has no static caller, so it is absorbed by no
     region and appears "unreachable" to the analysis (the meta-call edge is not in the
     static graph) → it lands in the keep bucket automatically, no `:- ensure_linked`
     needed. Dead code is likewise kept (the linker does no per-predicate dead-code
     elimination anyway). The chunk-390 report already isolates this set (Blint: 65
     region-absorbed, the prune target, vs 35 unreachable, kept). Residual (~1%, NOT a
     new burden): a predicate that is BOTH statically called (→ absorbed, its standalone
     pruned) AND variable-meta-called still needs the EXISTING `:- ensure_linked` (or
     public / dynamic) declaration — that target becomes a seed → region-reachable → not
     pruned. So the existing contract's scope is unchanged; we just do not widen it.
     - **9b-3 — the APPLIED prune DONE (chunk 393), `shumway-link --region-prune`.**
       Region-compiles the bundle (implies `--with-compiled-il`) and SKIPS emitting a
       standalone IL method for each absorbed-only predicate
       (`PersistedIlBuilder.Build`'s new `prunableFids` — skipped in the eligible pass
       but kept in the callee map so the region methods still absorb its body, and keeps
       its Tier-0 WAM as a fallback). The linker computes the prune set
       (`fullReachable − regionReachable`, threaded `ToBytes → CompileEntryToIl → Build`)
       and sets `RegionCompile` for the build (save/restore). Validated: synthetic +
       Blint run correct; Blint bundle 1834 KB (all-as-roots region) → **1463 KB (−20% of
       the region bloat)**, byte-identical output. Stays larger than the 743 KB
       per-predicate-IL bundle: region-reachable ROOTS still duplicate their members (the
       prune removes the absorbed-only standalone copies, not the inter-root
       duplication) — region compilation is inherently bigger, and the chunk-382 A/B
       already showed no runtime win on Blint. 2 Chunk393Tests.
     - **9c — cost-based root selection (minimal-root-set) DONE (chunk 394).** Cuts the
       inter-root duplication 9b-3 doesn't touch: a member shared by N regions is baked
       into all N. The selector promotes such a member to its OWN root (one copy + N
       trampolines) when it pays off. **Cost model**: absorbing M bakes in M AND its whole
       absorbed sub-closure, so the duplicated code per absorbing region is
       **size(region(M))** (M's region closure, NOT M's bytecode); promoting M saves
       `(dup(M) − 1) × size(region(M))`. Regions overlap, so it's a GLOBAL optimisation —
       an **iterative greedy fixpoint** (`RegionRootSelector.ComputeForcedRoots`): build
       all regions with the current promotion set excluded, score every still-shared
       predicate, promote the best, recompute, repeat until none beats `minSaving`
       (default 64 B, the measured Blint optimum, env `SHUMWAY_REGION_ROOT_MINSAVE`).
       Forced roots are excluded from absorption via `IlPredicateCompiler.
       RegionForcedRootFids` (static, set by the linker for the build) + the
       `RegionMemberFids(extraExcluded)` overload (for the selector's probes). **MEASURED
       on Blint**: bundle 1834 KB (all-as-roots) → 1463 KB (9b-3) → **~914 KB (9b-3 + 9c,
       86 promoted)**, byte-identical to the Tier-0 reference — now within ~23% of the
       743 KB per-predicate-IL bundle, down from 2.5×. A nice property the fixpoint gives:
       promoting a MIDDLE node de-dups its whole tail in one step (the recompute shows the
       tail is no longer shared). 3 Chunk394Tests.
     - **9d — stripping the pruned predicates' WAM: DONE FOR REAL (chunk 402), after the
       chunk-398/400 attempt shipped broken and chunk 401 disabled it.** The chunk-401
       incident (user-reported: a `--exe --goal main` Blint parsed wrong /
       `startPc 0x… out of range` on a missing file — the chunk-398/399 validation only ran
       the `test/0` entry, and the prune is ENTRY-DEPENDENT) decomposed into two root causes,
       both now fixed structurally:
       * **`Run()`'s startPc bounds guard rejected resume-marker addresses — a PRE-EXISTING
         `--strip-wam` bug, no regions needed.** `catch/3` recovery resolves the recovery
         predicate through `CurrentFunctorAddresses`; a stripped predicate's entry there is
         its marker alias (`EncodeResumeMarker(fid, 0)`, intentionally above the code range),
         and `RunCatching` → `interp.Run(program, marker)` threw before the dispatch loop's
         `IsResumeMarker` routing could see it. Fix: the guard admits a marker start PC.
         Verified fail-when-reverted (`Chunk402Tests`).
       * **Absorbed-only members had no by-fid resolution — fixed with MEMBER-ENTRY
         CURSORS.** The planner now assigns one `RegionCursorKind.MemberEntry` cursor per
         non-root member, appended AFTER all other cursors (existing consumption order
         untouched); its dispatch-switch slot is the member's entry label (no new block).
         The persisted entry metadata carries the (memberName, arity, cursor) table
         (`IlPersistedEntry.RegionMembers`, name-relative), and `LoadBundle` aliases each
         member with no better resolution to `EncodeResumeMarker(rootFid, entryCursor)` in
         `CurrentFunctorAddresses` — so a top-level query, a meta-call through a user
         meta-predicate, or a catch recovery dispatches INTO the owning region method at the
         member's entry. `ResolveTargetMaybeAutoPromoted` (the unresolved-Call sentinel
         path) also accepts a marker value — sound for module visibility because the
         sentinel's fid was chosen by the link layer.
       **Runtime cost: zero on hot paths.** Static linked calls (CallIl/ExecuteIl rewrites)
       and intra-region `br`s are untouched; the member alias only enters via the EXISTING
       address-map lookup on by-fid paths (meta-call / sentinel / recovery — already
       map-probing), and the marker dispatch is the same Phase-16 machinery as any IL call:
       O(1) `ilTable[rootFid]` + the region's jump-table switch. The switch grows by one
       slot per member (a label reuse, no code).
       The chunk-398 constructed-constant meta-guard is GONE — superseded: every absorbed
       member is now fid-resolvable, so nothing needs rescuing from the prune. Measured on
       Blint (`-E main/0 --region-prune --strip-wam`): **599 674 vs 754 863 B unstripped
       (−155 KB, −21%)**; both `--exe --goal main` runs (Blint.pl, missing file)
       byte-identical to the no-region build; the by-fid REPL queries (`main(['Blint.pl'])`)
       that used to `existence_error` now run; `test/0` byte-identical. Embedding 2189 /
       REGION=1 all-real-pass / Core 432 / ISO 277.
       The history below is kept because the prune-correctness work (issues 2-3) still
       stands; the chunk-398/400 WAM strip on top of it was unsound until chunk 402.
       Two issues had to be resolved (the path there went through two misdiagnoses):
       1. **NOT an IL-activation problem (an earlier misdiagnosis, chunk 396).** A first
          guess was that the bundle runs Tier-0 WAM (persisted IL gated by
          `IlPromotion.Threshold > 0`). EMPIRICALLY FALSE: persisted IL delegates are
          registered unconditionally (`RegisterBoundDelegate`) and `InstallCallIlRewrites`
          rewrites every Call-to-an-IL-callee to `CallIl` regardless of Threshold —
          measured (`SHUMWAY_IL_DIAG=1`) **890 CallIl/ExecuteIl rewrites on the Blint
          `--region-prune` bundle at Threshold 0**. A `--with-compiled-il` bundle already
          runs its IL — no IL-mandatory work needed.
       2. **Analysis ↔ compile region-membership consistency (FIXED — move the prune into
          the compile).** The dead-region ANALYSIS used to run in the linker over only the
          entry's OWN module bytecode (the `.shmo`), but the IL COMPILE's calleeMap is the
          warm-up engine's FULL predicate set — user module PLUS the prelude (plus any other
          reached module). Under the region BUDGET the two absorbed DIFFERENT members. Fix:
          the applied prune now runs INSIDE `BundleWriter.CompileEntryToIl`, over that exact
          calleeMap. The linker passes the SEEDS (its externally-reachable set); the compile
          resolves them to fids (`ShmoLinker.ResolveSeedFids`), runs `RegionRootSelector`
          (Stage 9c) + `RegionReachability`, installs the forced roots, and the resulting
          absorbed-only set is BOTH skipped for standalone IL AND WAM-stripped — so the prune
          can't disagree with what ships. The linker's step-6c is now a `--prune-report`-only
          DRY-RUN (its per-module figures no longer drive the build).
       3. **Meta-call-by-fid to an absorbed predicate (the residual, now handled).** A
          predicate reached ONLY as a region `br`-member by the static CallSites graph can
          STILL be meta-called by functor id at runtime — e.g. Blint's `show_in_console/0`,
          built as the TERM `not(show_in_console)` inside `ifthen(not(show_in_console), …)`
          and run by the user-defined `ifthen/2`. That path is invisible to the CallSites
          graph (the goal is constructed DATA, not a Call site), so the analysis marked it
          absorbed-only and the strip removed it → `existence_error`. Guard: any predicate
          whose head functor appears as a CONSTRUCTED constant anywhere in the bytecode
          (`put_atom` / `put_structure[_r]` / write-mode `unify_atom` / `unify_structure`,
          names DEMANGLED since a constructed goal carries the bare source name) is excluded
          from the prune and keeps its standalone form — exactly the user's model "(los
          menos) serán WAM si no se pudo armar región ni IL". Over-approximate (a
          functor-shaped term used purely as data also keeps its predicate) but sound: it
          only ever keeps MORE standalone forms, never strips a live one.
       `CollectConstructedFunctors` (the guard) + the in-compile prune live in
       `BundleWriter.cs`; `SHUMWAY_PRUNE_DIAG=1` prints the per-entry seed / forced-root /
       absorbed-only counts.
     - **9b-1 — linker seed-set computation DONE (chunk 389).**
       `ShmoLinker.ComputeExternallyReachableSeeds(reachedRoots, reached, moduleDefined)`
       (public, pure): the entry / `ensure_linked` roots ∪ every reached PUBLIC ∪ every
       reached DYNAMIC predicate (Dynamic covers `:- visible`, chunk 265). Computed in
       `Link` right after the reachability walk, reported as a `stage9_seeds` Info
       diagnostic and exposed on `LinkResult.ExternallyReachableSeeds`. End-to-end on
       Blint (entry `test/0`): **18 externally-reachable seeds among 238 reached
       predicates** — the REAL seed set, broader than `shumway-compile --prune-report`'s
       11 call-graph roots because it keeps public/dynamic predicates also called
       internally. 4 Chunk389Tests.
     - **9b-2 — the fid bridge + analysis report DONE (chunk 390).** Gated by
       `LinkConfig.RegionPruneReport` / `shumway-link --prune-report`: after the walk the
       linker decodes the reached modules (`CompiledModuleCodec`) into a global
       fid→CompiledPredicate map, resolves the seeds to functor ids
       (`ShmoLinker.ResolveSeedFids`, public — tries BOTH the mangled `module$name` and
       the bare `name`, a sound over-keep), and runs `RegionReachability` twice — region-
       aware (intra-region = `br`) and plain (every call trampolines) — so the report
       separates the genuine region benefit from ordinary dead code. Blint (entry
       `test/0`): **65 region-absorbed (standalone prunable) + 35 unreachable = 100 of
       256**; the 65 matches `shumway-compile --prune-report`'s 67. Only **1 seed fid**
       resolves (`test/0`) — correct: Blint's other 17 seeds are `:- dynamic`
       predicates whose clauses live in the DynamicSeeds trailer, not the static
       bytecode, and which are never region-compiled / never prunable. 5 Chunk390Tests.
       A REPORT only; the applied prune still needs region-mode bundle compilation
       (prereq i) + dropping each prunable predicate's standalone WAM/IL entry (9b-3+).
10. **Linker IL / WAM dump options — DONE (chunk 399) — `shumway-link --dump-il <f>` /
    `--dump-wam <f>`.** The link step dumps the code that actually SHIPS: after Stage 9
    pruning, the linker holds the REAL IL the bundle needs (the surviving roots/regions),
    not the per-predicate superset `shumway-compile` produces.
    - **`--dump-il`** routes the PERSISTED-IL emit's per-method dump to the file. A new
      `IlPredicateCompiler.FinishPersistedEmit(emit, header)` (the `EmitPersistedMethod`
      counterpart of the chunk-383 `FinishEmit`) dumps `emit.Instructions()` to
      `IlDumpPath` before `CreateMethod`, at both create sites (region + per-predicate);
      the linker sets `IlDumpPath` around the `BundleWriter.ToBytes` build (save/restore).
      So the dump reflects EXACTLY what runs — post-prune, region mode + forced roots under
      `--region-prune`. Implies `--with-compiled-il` (no IL to dump otherwise). On Blint
      `--region-prune --strip-wam`: **108 region-method dumps** (each headed
      `persist region root fid=… members=N [member list]`) + 248 standalone.
    - **`--dump-wam`** decodes each bundle entry's FINAL `CompiledBytecode` (after
      `--strip-wam` / region prune) via `CompiledModuleCodec` + `Core.Disassembler`
      (`DumpBundleWam`). Ground truth of the Tier-0 code that remains: plain `--dump-wam`
      → all 256 Blint predicates; `--region-prune --dump-wam` → 256 (WAM kept as fallback);
      `--region-prune --strip-wam --dump-wam` → **0 predicates** (correct — the whole
      user-module WAM is stripped, the bundle runs on IL).
    Both APPEND (delete between runs). Chunk 398's report-gating change (step-6c now
    `--prune-report`-only) updated `Chunk393Tests` to opt into the report. Embedding 2181.
11. **Inspect region IL for optimisations — STARTED (chunk 400).** Drove a frequency
    analysis over Blint's shipped region IL (`shumway-link --region-prune --strip-wam
    --dump-il`, 108 region methods + 248 standalone). Findings + what was / wasn't acted on:
    - **Self-delegate reload (the cleanest cross-member CSE) — HOISTED.** Every
      multi-clause / indexed member's `PushIlChoicePoint` reloaded the SAME region
      self-delegate: `SelfFromArrayField` = 3 IL ops (`ldsfld`/`ldc.i4`/`ldelem`) in the
      persisted path, `SelfFromHolder` = a dictionary lookup in the runtime path. 1470
      push sites bundle-wide. `EmitRegionInto` now loads it ONCE into a local ahead of the
      dispatch switch (which dominates every member / cursor label, so the store reaches
      every push site) and hands members a loader that reads it. Gated on ≥3 push sites
      (the hoist costs 4 ops once, so it only shrinks at `2·P−4 > 0`; the exact push count
      is the plan's ClauseAlt + IndexNode cursors). Result: Blint bundle **598326 →
      594230 bytes (−4 KB)**, ~1470 fewer IL ops in hoisted regions, and the runtime path's
      holder lookup runs once per invocation instead of once per push. (Note: the
      `--dump-il` LINE count goes UP because adding the local renumbers all later locals —
      a text-render artifact; the bundle byte size is the truth.) Blint byte-identical,
      Embedding 2181 / REGION=1 all-real-pass.
    - **`FlushWakeupsForIlCut` (1765 sites, ~3 IL ops each) — LEFT ALONE.** The chunk-379
      wakeup flushes at every region br / proceed dominate the IL after the CP machinery.
      Eliminating provably-redundant ones is a soundness-delicate dataflow problem (the
      clpfd-in-region history, chunks 378-380); not worth the risk for a phase with no
      demonstrated wall-clock payoff.
    - **`put_value` round-trips (`SetY(k,GetReg(r))` … `SetRegister(r,GetY(k))`) — OUT OF
      SCOPE.** A faithful lowering of the WAM head/body register traffic; removing it is
      the WAM register-allocator lever (see `chunk-model-refinement-failed` memory), a
      separate hard effort, not an IL peephole.
    Net: the region IL is mostly irreducible faithful WAM lowering. The one clean,
    safe, validated micro-opt (the self-delegate hoist) lands a small size win; the larger
    levers are either soundness-delicate or belong to the register allocator — consistent
    with chunk-382's finding that region compilation has no demonstrated real-program
    wall-clock payoff.

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
