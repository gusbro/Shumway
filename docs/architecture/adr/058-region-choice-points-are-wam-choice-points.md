# ADR-058: A Region's Choice Points Are WAM Choice Points

## Status

Accepted (2026-09-28). Builds on [ADR-057](057-local-backtracking-in-il-regions.md)
(the region resumes its own choice points) and amends
[ADR-014](014-il-choice-points.md) for choice points a Tier-1 region pushes on
itself: they carry a resume marker in the frame's BP instead of an entry on
the IL side stack. The frame layout does not change.

## Context

A region's choice point is today two things: the WAM frame, whose BP holds
the IL sentinel, and an entry `(delegate, cursor)` on the IL side stack. The
side stack exists so that the interpreter, which resumed every IL choice point
before ADR-057, can find the code to call. Whoever resumes it restores the
machine state (`PopIlChoicePointAndRestore`) and the alternative's code starts
from a restored state.

Measured on a loop that pushes a choice point, fails into it and retries
(ADR-057, `bc/1`): 95 ns per cycle. The push is 18 ns (`PushChoicePoint`, and
`PushIlChoicePoint` on top of it), the restore 18 ns, and most of the rest is
the region's own code around them. ADR-057 moved the resumption into the
region and gained 0 to 5%, because the round trip it removed was cheap; it
left the push, the side stack and the restore as they were.

The wasm tier (ADR-050) has no side stack. Its choice points are WAM frames
whose BP is a resume marker; the alternative's code restores the state itself
(`EmitRestoreCommon`), a failure inside the module resolves the marker and
jumps, and a failure outside it reaches the marker through the interpreter's
ordinary bytecode path. That is the WAM's own design, `try`/`retry`/`trust`
with the restore in the retry.

## Decision

For the choice points a region pushes for its own clause alternatives and
index nodes:

1. **The push is a WAM choice point with a region marker in BP.**
   `PushChoicePoint(arity, marker)`, where `marker` is the region's resume
   marker for the alternative's cursor, `(root functor, cursor)`: the same
   encoding the region already uses for continuations, baked as a constant
   through the patchable marker helper, so persisted regions relocate it as
   they relocate the others. No side-stack entry.

2. **The alternative restores the state itself.** Every such alternative gets
   a resume entry, a cursor of its own beyond the plan's, which the marker
   names:
   - an alternative that pushes the next one's choice point on the plain path:
     `RetryMeElse(nextMarker)`, the WAM retry (restore, `HB := H`, BP to the
     next alternative), then the code right after that push;
   - any other (the last, or one whose own push is not the plain one):
     `TrustMe()` (restore and pop), then the alternative's own label.
   The alternative's own label is unchanged and keeps serving the entries that
   arrive without a choice point to restore: the index resolve, and an ADR-031
   guard failure, which branches straight to the next alternative.

3. **`rfail` reads the top's BP.** If it is a marker of this region, its
   cursor goes to `dispatch`, which reaches the alternative's resume entry.
   The floor, the debug-session and safe-point conditions of ADR-057 hold
   unchanged. Anything else returns `false`.

4. **A failure outside the region: `TryBacktrack` invokes the marker's code
   directly.** For a bytecode choice point whose BP is a resume marker it
   resolves the delegate (the link-time table, the dispatcher, a retired
   snapshot, the same lookup the dispatch loop uses) and calls it at the
   cursor, after the cancellation and deadline safe point the IL branch takes,
   handling success as for an IL choice point. That safe point is the only one
   a failure-driven loop whose retries all come through here reaches
   (`length(_, _), fail` under `time_out/3` ran forever without it). It does not set the pc
   to the marker: the dispatch loop's marker path is the return path, whose
   heap collection and wakeup checks would run before the alternative's
   restore and fire a wake whose binding is about to be undone. This applies
   to the wasm tier's choice points too, whose BPs are markers: their retry
   now runs the same way, and a pending wakeup is handled by the delegate,
   which steps aside to the bytecode retry.

5. **Everything else keeps the IL side stack**: choice points of standalone
   methods, of backtrackable builtins, of `IlIteHelper`, an ADR-031 choice
   point materialised at a guard's commit, and an ADR-034 dynamic fallback's.
   `rfail` resumes those of the region as ADR-057 does.

6. **Deferred: the push and the restore inline in the region's IL.** Emitted
   code reaches the activation only through public methods, because persisted
   IL is loaded without `InternalsVisibleTo`. Inlining the frame's stores and
   loads would mean exposing the stack array, its tops and the registers to
   generated code, a decision of its own, for what the measurements below leave
   (about 5 to 10%).

Independent of the above and in the same arc: `PushChoicePoint` keeps its
cold diagnostic branch (`TraceCpStack`) out of line. Today that branch's
interpolated string makes the JIT zero an 80-byte frame on every push, on
Tier-0 as well.

## Why this is sound

It is the WAM's retry/trust, which the interpreter runs for every bytecode
choice point: the restore sequence is the same method (`RestoreCommonFromCurrentCp`
through `RetryMeElse`/`TrustMe`). What changes is who holds the pointer to the
alternative (the frame's BP instead of the side stack) and who calls the
restore (the alternative instead of the resumer).

The side stack's other users do not see these choice points: cut (the
`OnPrune` callbacks) only walks entries that exist; soft cut marks a frame's
BP dead whatever it holds; `ReconcileIlChoicePointsToB` drops only entries.
A region marker in a BP is a value the dispatch loop already resolves.

## Expected gain (before measuring)

From the measured parts of the 95 ns cycle: the side-stack push and pop and
the `TopChoicePointIsIl` tests go (about 5 ns), the pop plus re-push of an
alternative that is not the last becomes one retry (about 10 ns), and with
step 6 the two calls become inline code (another 15 to 20 ns). About a third
of the cycle. `queens(9)` has about 3M cycles (285 of its 490 ms), so 15 to
20% on generate-and-test programs; nothing on deterministic code.

## Results

Minimum over 8 processes per variant, ABBA against ADR-057, machine idle,
both variants in the same clock mode:

| | ADR-057 | this ADR |
|---|---:|---:|
| `bc(2000000)` (one choice point per iteration) | 0.282 s | 0.235 s (-17%) |
| `bt(2000000)` (the same, over facts) | 0.281 s | 0.259 s (-8%) |
| `queens(9)` by generate and test | 0.915 s | 0.891 s (-3%) |

Without the retry (resume entries that pop and push again), `bc` gained 5%.
`queens(9)` gains little: its cost is mostly in the region's own code
(unification of lists, arithmetic comparisons), not in the choice point.

## Risks

- **Two entries per alternative.** Every site that branches to an
  alternative's label (ADR-031's guard fail, ADR-033's continuations, ADR-034's
  dynamic fallbacks, indexed buckets) must pick the right one. A wrong pick
  either restores from a choice point that is not there or skips a restore.
- **Persisted regions.** The markers go through the same relocation as the
  continuation markers; a bundle from before this change has IL side-stack
  pushes and keeps working as it is.
- **Debugger.** User code runs Tier-0 under a session; the prelude's regions
  can backtrack through a marker BP, which the redo port reports as a bytecode
  choice point with the marker as its address.

## Validation

The ADR-057 suites (equivalence against Tier-0, the counter), the cut,
soft-cut and guard suites (ADR-029 to ADR-034, ADR-037), the full Embedding
gate, persisted-IL bundles, and the Logtalk and Scryer suites. Heap cells and
inferences of `time/1` do not move. The gain: `bc`, `bt`, `queens(9)` and the
Van Roy baseline against the parent commit, minimum over 8 processes per
variant (the notebook's clock is bimodal per process).
