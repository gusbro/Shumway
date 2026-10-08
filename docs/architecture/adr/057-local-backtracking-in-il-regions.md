# ADR-057: Local Backtracking in Tier-1 IL Regions

## Status

Accepted (2026-09-28). Amends [ADR-014](014-il-choice-points.md): an IL
choice point can be resumed by the region that pushed it, without the
interpreter's `TryBacktrack`. The choice-point frame, the IL side stack and
the delegate ABI do not change.

Measured gain on its own: 0 to 5%. Accepted as the enabling step for inline
choice points in IL (see [What this enables](#what-this-enables)), which is
where the backtracking gain is.

## Context

A Tier-1 IL region (Phase 29) is one method per closure of predicates, with a
`dispatch` switch over its cursor space. Calls and returns inside the region
are already jumps: a member's proceed goes to the shared `ret` handler, which
decodes `Cp` and branches back to `dispatch`. Failure was the exception. The
region's `rfail` handler was `return false`, so every failure inside the
region left the method, and the interpreter's `TryBacktrack` popped the IL
choice point (`PopIlChoicePointAndRestore`) and invoked the region's delegate
again at the cursor it recorded. The alternative that runs next is almost
always in the same region, often in the same member.

The question that started this was why the wasm tier in a browser matched or
beat IL on the desktop on generate-and-test (`queens(10)` from
`examples/boards.pl`: 7 s in the browser, 12 s in the desktop REPL). Most of
that gap was the measurement: a video call and a browser tab were loading the
machine, and the notebook's clock is bimodal per process (turbo or base, 1.8x
apart for identical runs). With the machine idle and both runs at the same
clock, `queens(9)` takes 1.5 s on Tier-0, 0.49 s on IL and 0.44 s on the wasm
tier: IL is x3.1 over Tier-0 and the wasm tier about 10% ahead of IL.

The wasm tier resolves its own choice points inside the module, and there that
was decisive: a module exit cost 4 to 15 us of interpreted C# staging. IL has
no such exit cost. Its round trip through `TryBacktrack` is a return, one loop
iteration, a delegate call and a prologue: 10 to 30 cycles.

What a choice point costs in IL, measured on a loop that pushes one, fails
into it and retries (`bc/1` below) against the same loop without the choice
point: 95 ns per cycle, of which

| part | ns |
|---|---:|
| `PushChoicePoint` + `PushIlChoicePoint` | 18 |
| restore (`RestoreCommonFromCurrentCp`, `UnwindTrails`, the pop) | 18 |
| the region's own code (entry marks, two dispatch switches, marker decode in the ret handler) | about 45 |
| the round trip through `TryBacktrack` | under 10 |

The wasm tier does the push and the restore inline, as memory writes and reads
in the module. IL calls into the activation for both, with a bounds check per
store.

## Decision

1. **A region's failure handler first tries to resume a choice point of its
   own.** `rfail` becomes:

   ```
   rfail:
     if (engine.TryResumeOwnIlChoicePoint(self, out cur)) goto dispatch;
     return false;
   ```

   where `self` is the region's delegate, the one its multi-clause and indexed
   members already push with `PushIlChoicePoint` (hoisted into a local in
   every region that pushes).

2. **`Activation.TryResumeOwnIlChoicePoint(self, out cursor)`** returns false,
   touching nothing, unless all of these hold:
   - the top choice point is an IL choice point and its entry's delegate is
     `self`, by reference;
   - it lies above the backtracking floor of the current run, the one an
     in-engine sub-goal (the meta-call path of findall/3, `\+`, call/N) sets
     so its failure cannot unwind choice points the outer computation owns.
     The floor moves from the interpreter onto the activation
     (`Activation.BacktrackFloor`) so that compiled code can read it;
   - no debug session is attached (ADR-035: the redo port must be raised for
     every resumed choice point, which `TryBacktrack` does).

   When they hold, it does what `TryBacktrack` does for an IL choice point
   before invoking the delegate: the profiler's backtrack event, the
   cancellation safe point (`BacktrackSafePoint`, so ESC still reaches a pure
   backtracking loop), the choice-point trace ring, then
   `PopIlChoicePointAndRestore`. It hands back the cursor and returns true.

3. **Nothing else changes.** A choice point of another region, a
   backtrackable builtin's, a soft-cut-neutralised one (its delegate is no
   longer `self`), and every bytecode choice point still go through
   `TryBacktrack`. A failure outside the region's code (in a callee of
   another region or in bytecode) reaches the region through `TryBacktrack`
   as before. Success is unchanged: the method returns `true` to whoever
   invoked it, and every invoker already treats success the same way
   (`IlTailCallPending`, or continue at `Cp`).

4. **Regions only.** A predicate compiled alone (outside region compilation,
   which is the default) keeps returning `false` on failure. The same helper
   applies to it when there is a reason to.

5. **A diagnostic counter** (`Activation.DiagLocalResumes`, `SHUMWAY_DIAG`)
   counts the local resumptions and the ones declined by reason (at the floor
   or no IL choice point, another code's choice point, a debug session).

## Why this is sound

The resumption is the same sequence `TryBacktrack` runs, on the same choice
point: the only difference is that the retry's cursor is dispatched by a
branch instead of a new invocation of the same delegate. The invocation
contract of ADR-014 (cursor 0 fresh, N a retry) is unchanged; a cursor reached
by `br dispatch` is indistinguishable from one reached through the delegate's
argument, which is how the region's intra-region returns already work.

The C# stack stays O(1) (Phase 16): the retry is a backward branch in the
same frame, not a call.

Which invocation of the region pushed the choice point does not matter. If an
older invocation pushed it and has since returned, resuming it here and
returning `true` leaves the interpreter at the restored `Cp`, which is where
`TryBacktrack` would have left it.

## Results

Minimum over 8 processes per variant, ABBA, machine idle, both variants in the
fast clock mode. Two million push/fail/retry cycles for the loops:

```prolog
pk(X) :- X = 1.            bc(0) :- !.
pk(X) :- X = 2.            bc(N) :- pk(X), X == 2, N1 is N - 1, bc(N1).
pick(1). pick(2).          bt(N) :- pick(X), X == 2, N1 is N - 1, bt(N1).
```

| | before | after |
|---|---:|---:|
| `bc(2000000)` | 0.262 s | 0.256 s (-2%) |
| `bt(2000000)` | 0.267 s | 0.248 s (-7%) |
| `queens(9)` by generate and test | 0.486 s | 0.494 s (0%) |

The counter shows the mechanism engaged: every backtrack of `bt` and all but
one of `queens(7)`'s were resumed in the region. Heap cells and inferences of
`time/1` are unchanged, as they must be.

## What this enables

The gain of this step alone is what its round trip cost. Its value is that the
failure and the retry now live inside the region's method, which is what three
further steps need:

1. **No IL side-stack entry for a region's own choice points.** The side stack
   exists because the interpreter resumes the choice point and needs the
   delegate. Once the region resumes its own, the cursor can travel in the
   frame's BP field as an encoded marker, as the wasm tier's do. The
   interpreter, for the rare choice point it still resumes, decodes the
   marker to the delegate through the holder table that already exists. The
   side-stack push and pop, the triple test of `TopChoicePointIsIl` and the
   side-stack walk on every cut go away.
2. **Push and restore emitted inline in the region's IL**, about 13 stores and
   10 loads on the frame, instead of two calls with a bounds check per
   access. Only possible because the restore now happens in the region.
3. **The retry branches straight to the alternative's label**, known at
   compile time, instead of going through `dispatch` and the ret handler's
   marker decode.

Projected from the measured parts: a push/fail/retry cycle from 95 ns to
about 35 to 45 ns. About 3M of those cycles are 285 of the 490 ms of
`queens(9)`, so the program would come down to about 0.32 s, x4.6 over
Tier-0 instead of x3.1, and past the wasm tier. On the Van Roy baseline that
narrows the gap to native GNU Prolog on queens from x4.4 to about x3.

What neither this nor those steps address: the bounds checks and array
reloads the JIT cannot hoist past a call (a separate `ref`/`Unsafe` arc), the
trail unwind loop, and the open-coding of `==/2` and `=/2` with a constant,
which IL does not do and the wasm tier does (about 27 ns of the loop above).

## Alternatives considered

- **Keep the round trip, make it cheaper.** Skipping the zero-initialisation
  of the region's locals, or caching the delegate lookup, lowers the re-entry
  cost but keeps the exit, the loop and the call. It would not enable the
  steps above.
- **Resume any IL choice point locally, not only the region's own.** A
  different region's cursor space is not this method's; it would need a call,
  which is what the delegate invocation already is.
- **Drop the IL side stack in the same step.** Step 1 of the list above. Kept
  apart so this one could be measured on its own.

## Consequences

### Positive

- A backtrack whose next alternative is in the same region costs a branch and
  the restore, not an exit and a re-entry.
- The steps listed in [What this enables](#what-this-enables) become possible.

### Negative

- One more path to keep equivalent to `TryBacktrack`. The helper is the only
  copy of that sequence for the local case; a later change to what
  `TryBacktrack` does for an IL choice point has to be made in both places.
- The backtracking floor becomes activation state, written by the
  interpreter's nested runs and read by compiled code.

## Validation

- **Equivalence.** The Tier-1 suites (Embedding), the cut and soft-cut suites
  (ADR-029 to ADR-031, ADR-037), findall over IL predicates that backtrack
  below and above a sub-goal's floor, backtrackable builtins interleaved with
  IL choice points, and catch/throw across a resumed choice point. Heap cells
  and inference counts of `time/1` do not move.
- **Cancellation.** A pure backtracking loop in a promoted region is cancelled
  by ESC, as before.
- **Debugger.** With a session attached, the resumption is declined and the
  redo port fires from `TryBacktrack`, as before.

## References

- [ADR-014](014-il-choice-points.md): the IL choice-point ABI.
- [ADR-011](011-il-compiler-architecture.md): the IL compiler.
- [ADR-035](035-source-level-debugger.md): the redo port.
- [ADR-050](050-wasm-tier1-backend.md): the wasm tier's local backtracking.
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Regions.cs`: `EmitRegionInto`,
  the `rfail` handler.
- `src/Shumway.Interpreter/BytecodeInterpreter.Runs.cs`: `TryBacktrack`.
- `src/Shumway.Core/Activation.Tier1.cs`: `TryResumeOwnIlChoicePoint`,
  `BacktrackFloor`, `PushIlChoicePoint`, `PopIlChoicePointAndRestore`.
