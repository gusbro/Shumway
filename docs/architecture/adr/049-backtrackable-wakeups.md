# ADR-049: Backtrackable wakeups — the interrupted-goal model

## Status

Accepted. Stage 1 (Tier-0) shipped 2026-09-03 (#55); stage 2 (Tier-1 region
boundaries) implemented in the same arc — the interrupt core moved onto the
Activation so both tiers fire the same machinery, and the emitted region
flushes became suspend/resume points over the phase-16 resume markers.
Refines the deferred-wakeup design that has carried attributed variables
since phase 4. Supersedes the once-semantics drain for the non-cut goal
boundaries; the cut-boundary drain stays, deliberately (see Decision §5).

## Context

When a unification binds attributed variables, the engine queues the
bindings and, at the next goal boundary, runs each module's
`verify_attributes` hook and every goal the hooks return. Today that run is
a **nested drain**: `FlushPendingWakeupsSlow` snapshots the X registers and
the choice-point level, executes the batch in a nested `Dispatch` with a
backtrack floor at the entry level, cuts back to the entry level on success
("once-semantics", by design), restores the registers, and resumes the
interrupted instruction.

The once-cut is not an accident. A choice point created inside the nested
drain refers to a C# driver frame (`RunGoalInEngine`) that no longer exists
once the drain returns, and any later re-entry would find the interrupted
goal's argument registers clobbered by the wake goals. Nested-interpreter
choice points cannot outlive the nesting; cutting them was the only sound
option within that structure.

The consequence is the defect this ADR removes: **a woken goal runs to its
first solution and that choice is final.**

```prolog
?- freeze(X, member(Y, [1,2])), X = a, Y = 2.
   false.                    % SWI and Scryer answer Y = 2
?- findall(Y, (freeze(X, member(Y, [1,2,3])), X = a), L).
   L = [1].                  % they answer L = [1,2,3]
```

Most wake goals in practice are (semi)deterministic — type checks, clp(FD)/
clp(Z) propagators, `dif/2`'s canonical wake — which is why the limitation
stayed unnoticed through four solver campaigns. But a nondeterministic
frozen goal silently commits, which can turn a sound program into an
incomplete one with no error pointing at the cause.

### The interrupted-goal model

The alternative structure — the classical one for attributed-variable
wakeups — keeps everything in the one flat machine:

1. **The wakeup is an interruption of the program, not a side loop.** At
   the boundary, execution simply continues at the wake driver; the
   interrupted goal becomes the driver's continuation.
2. **The interrupted goal's state is saved in an ordinary environment
   frame**: the live argument registers, the cut barrier, and the
   continuation. Nothing lives on the host stack.
3. **The driver is ordinary Prolog**: it calls each module's hook, collects
   the returned goal lists, and runs them through `call/1` — ordinary
   conjunctions creating ordinary choice points. Its last goal is a return
   primitive that restores the registers, barrier and continuation from the
   environment frame and resumes the interrupted instruction.

Backtracking then needs no mechanism at all: the wake goals' choice points
are ordinary, the environment frame holding the interrupted state is
protected by them (standard WAM stack discipline), and when a later failure
re-enters a wake alternative and the driver succeeds again, the return
primitive restores from the same frame and resumes the same instruction.

## Decision

Adopt the interrupted-goal model on our engine. `Dispatch` is already a flat
loop over engine state (P/CP/E/B), so the mapping is direct.

1. **The interrupt.** At a goal boundary whose wakeup check finds the queue
   non-empty (the existing inlined `HasPendingWakeups` test — the hot path
   does not change), the boundary no longer calls the drain. It allocates an
   environment frame holding exactly the live state: the interrupted
   goal's argument registers, `B0`, `CP`, and the resume point; takes the
   pending batch; loads it as the driver's argument; and jumps to the
   driver. One flat machine throughout.

2. **What "exactly the live state" is.** The register file is not
   snapshotted wholesale (today's drain saves all of it). At a `Call` /
   `Execute` / `CallBuiltin` / `CallIl` boundary the live registers are
   precisely `X0..X[arity-1]` of the callee, and the arity is static (the
   instruction operand). At `Proceed` / `DeallocateProceed` no argument
   registers are live — the frame saves none. The frame is therefore
   `arity + 3` cells: the registers, `B0`, `CP`, and the resume point.

3. **The driver is prelude Prolog.** `'$wake_driver'(Batch)` runs each
   module's `verify_attributes` hook (per-module 3/4-arity resolution as
   today, ADR-040), then every returned goal and every released frozen goal
   through `call/1`, then executes the return builtin. Hook goals that bind
   further attributed variables queue new wakeups, which fire at the
   driver's own goal boundaries — recursion replaces today's drain loop.
   The batch is taken atomically at interrupt entry, exactly as
   `TakePendingWakeups` does now.

4. **The return builtin** restores the saved registers, `B0` and `CP` from
   the environment frame and sets P to the resume point. The interrupted
   instruction re-executes; its wakeup check now finds the queue empty and
   proceeds. On a later backtrack into a wake alternative the driver
   succeeds again and the return builtin runs again against the same,
   still-protected frame. The frame is popped under the same rules as any
   environment (the phase-40 `Deallocate` floor already handles frames
   protected by younger choice points).

5. **Cut boundaries keep the once-drain.** The flush sites in front of
   `NeckCut` / `CutToLevel` (both tiers) stay as they are: the cut that
   follows immediately prunes every choice point the wake could have left,
   so injection and once-drain are observationally identical there — only
   the failure path matters, and it is the same. This is not a compromise;
   it is the same semantics for less machinery.

6. **Cut inside a woken goal stays local to that goal.** The driver enters
   with a fresh barrier (`B0 = B`) and runs each goal
   via `call/1`, so `!` inside a frozen goal commits that goal — never the
   driver, never the interrupted continuation, never the caller that
   happened to trigger the binding. The pins from the #47 arc
   (`Freeze_CutInWokenGoal_StaysLocalToIt`) carry over verbatim as the
   specification. What changes is only that a `!` in a woken goal now also
   has alternatives *to* prune: `freeze(X, (member(Y,[1,2]), !)), X = a`
   leaves one solution, exactly as `call((member(Y,[1,2]), !))` does.

7. **The debugger sees the woken goal.** Because the driver and the woken
   goals are ordinary frames, ADR-035/036 stack capture shows them with no
   new machinery: the user's frozen goal appears in the call stack while it
   runs, its Call/Exit ports fire, and breakpoints inside predicates it
   calls bind and stop. The `$`-prefixed driver plumbing is presented the
   way the debugger already presents prelude internals; the woken goal's
   frame is the user-visible one. The stack thus answers "why is this goal
   running here?" — it is parented at the boundary that fired it — which
   the invisible nested drain never could.

### Staging

Tier-1's flush sites are goal boundaries too (region `Call`/`Execute`/
`Proceed`/`DeallocateProceed` emit the same flush the interpreter performs;
control passing through the dispatch loop gets the loop's own checks). The
constraint is not that IL cannot be interrupted — phase 16's resume markers
exist exactly to suspend an IL body at a call site and re-enter it later.
The constraint is breadth: every emission site must change shape from
"call a bool-returning flush" to "suspend with a marker, let the driver
run, resume", and that is its own round of IL-emission work with its own
canaries (`PreludeIlBakeTests`).

- **Stage 1 — Tier-0.** The interrupt, frame, driver, sentinel return, and
  the test suite.
- **Stage 2 — Tier-1.** The region-boundary flush sites became suspend
  points: the emitted IL arms the interrupt (a verdict call), bails to the
  dispatch loop via the tail-call protocol, the loop runs the same driver,
  and the resume is a forward resume marker — for a call boundary it
  dispatches the callee (whose cut barrier is re-established as B post-wake,
  so a callee cut can never prune the wake's alternatives); for a proceed
  boundary it jumps to the continuation CP. The IL cut sites stay on the
  once-drain per Decision §5, as do non-region IL bodies, which have never
  flushed at their call sites — their wakes surface at the surrounding
  region or interpreter boundaries, unchanged.
- **Stage 3 — retirement.** The nested drain (`RunWakeups`,
  `MetaCallInEngine`'s wake role) shrinks to what still needs it
  (`ReentrantSolve` keeps its documented once-semantics).

## Consequences

- `freeze/2`, `when/2`, and every `verify_attributes` hook gain full
  backtracking through their goals; the #51 examples answer as SWI/Scryer.
- The hot path is untouched: the inlined empty-queue check at every
  boundary is byte-for-byte what it was. The interrupt path allocates one
  environment frame where the drain allocated a register snapshot array —
  comparable cold-path cost. Verified by Van Roy/Blint A/B (back-to-back,
  per the phase-25 discipline) and `--alloc`.
- Wake ORDER within a batch is unchanged (queue order = freeze order);
  chained wakes change from "drained in a loop" to "fired at the driver's
  own boundaries", which preserves order for the deterministic case and
  must be pinned against SWI for the nondeterministic one.
- clp(FD)/clp(Z)/clp(B)/clp(R) propagators are deterministic wakes: their
  behaviour must be bit-identical. The dif battery (26), the clpz smokes
  (5), and the solver campaign suites are the regression net.
- Exceptions from woken goals propagate as ordinary exceptions through
  ordinary frames — the special nested-catch resolution
  (`ResolveNestedCatch`) stops being involved on this path.
- The debugger gains frames it never showed; the VSIX/DAP snapshot tests
  that count frames may need their expectations refreshed.

## Future (explicitly out of scope here)

- Running hooks against the still-unbound variable, re-establishing the
  binding afterwards — the original `verify_attributes` contract — which
  would lift the documented "attribute writes do not stick" limitation of
  the 3-arity hook bridge. The interrupt structure is the prerequisite; the
  trail/LUV interaction of unbind-rerun deserves its own ADR.
- Eliding the wakeup checks from IL emission for hook-less programs
  (the FUTURE note on `FlushWakeupsForIlCut` stands unchanged).
