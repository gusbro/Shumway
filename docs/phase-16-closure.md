# Phase 16 — Closure

**Status**: complete.

**Tagged**: `phase-16` (this commit).

Phase 16 redesigned Tier-1 IL non-tail Call dispatch from
recursive-into-bytecode-interpreter to threaded continuation. The
RunSubroutine recursive frame, the chunk-66 meta-CP backtrack
driver, and the chunk-174 floor pin all went away — replaced by
resume-marker addresses (chunk 181) that the bytecode interpreter
decodes back to (delegate, cursor) when an IL'd predicate's callee
proceeds.

## Why

The chunk-50 IL Call ran the sub-predicate by invoking
`engine.IlSubroutineRunner` → `BytecodeInterpreter.RunSubroutine`
→ `Dispatch` — a recursive C# call into the bytecode interpreter.
Every Prolog non-tail call grew the C# stack by ~2 frames. A
Blint-shaped program ran into two problems:

1. **Y-slot corruption (chunk 174)**: backtracking inside the
   sub-call could pop a CP whose saved `_e` named the
   grand-caller's frame, leaving `_e` pointing at the wrong
   frame and the subsequent `put_value_y` reading from a stale
   Y slot. The chunk-174 fix pinned the backtrack floor at the
   sub-call's entry-B level — semantically correct but it
   forced the IL caller to redo work the (buggy) cascade was
   eliding, causing a ~7× slowdown on Blint.

2. **C# stack growth**: deep Prolog call chains (10000+
   nested calls) overflowed C#'s default 1 MB stack.

The threading redesign solves both without compromise: the IL
caller sets `Cp = resume_marker` and `Pc = callee`, marks
`IlTailCallPending = true`, and *returns* to the outer Dispatch
loop. The C# stack stays O(1) regardless of Prolog call depth,
and backtracking through the callee's CPs naturally re-enters
the caller at the same resume marker — no floor pin, no
meta-CP push, no redo loop.

## Chunks

- **181** — Resume-marker encoding. `Engine.EncodeResumeMarker
  (functorId, cursor)` packs the marker into a single int in a
  reserved high range (`0x4000_0000+`). The bytecode
  interpreter's main loop, at the top of every iteration, checks
  if Pc is a marker; if yes, decodes back to (functorId, cursor)
  and invokes the IL delegate via the new
  `ITier1Dispatcher.ResolveByFunctorId` hook. Inert until
  chunk 182 emits markers.

- **182** — IL non-tail Call switches to threaded emission. The
  EmitClauseBody Call opcode handler now emits the threaded
  pattern (`SetB0` for the callee's cut barrier; `SetCp`(marker);
  `SetPc`(callee address); `IlTailCallPending = true`;
  `return true`). The chunk-66 meta-CP push is gone — the
  callee's `try_me_else` CPs naturally carry the caller's marker
  as their saved Cp, so popping a callee CP on backtrack and
  running the next clause's body eventually proceeds back to the
  marker and re-invokes the caller at the same cursor. The
  cursor switch at the delegate's top collapses to a direct
  branch to the post-Call body — no backtrack-driving logic.

- **183** — Delete dead code from chunks 50, 66, 174. Gone:
  `IlRuntimeHelpers.Call`, `RunBacktrack`,
  `RunBacktrackWithFloor`, `ReadPreCallB`;
  `BytecodeInterpreter.RunSubroutine` and `SetBacktrackFloor`;
  `Engine.IlSubroutineRunner`, `BacktrackRunner`,
  `SetBacktrackFloor`, `SetE`; the corresponding MethodInfo
  references and engine wirings. Net delete: 223 lines.

- **184/185** — Tests + closure (merged). The chunk-182 tests
  cover the architectural deliverable (deep chain, mixed
  IL/bytecode backtracking, threshold=32 correctness).

## Deliverables

| Chunk | Deliverable | Status |
|---|---|---|
| 181 | `Engine.EncodeResumeMarker` / `IsResumeMarker` / `DecodeResumeMarker`. | ✓ |
| 181 | `ITier1Dispatcher.ResolveByFunctorId` extension. | ✓ |
| 181 | BytecodeInterpreter main-loop marker check + invocation path. | ✓ |
| 182 | `EmitClauseBody` non-tail Call → threaded emit. | ✓ |
| 182 | `EmitSingleClauseMetaCpBody`: cursor=N entry collapsed to direct branch. | ✓ |
| 182 | 3 tests in Chunk182Tests covering deep chains, mixed boundaries, threshold=32. | ✓ |
| 183 | Delete RunSubroutine + IlSubroutineRunner. | ✓ |
| 183 | Delete RunBacktrackWithFloor / ReadPreCallB / RunBacktrack helpers. | ✓ |
| 183 | Delete chunk-174 SetBacktrackFloor + SetE callbacks. | ✓ |

## Test totals at closure

| Suite | Count |
|---|---|
| `Shumway.Tests.Core` | 417 |
| `Shumway.Tests.Interpreter` | 105 |
| `Shumway.Tests.Compiler` | 248 |
| `Shumway.Tests.IsoConformance` | 275 |
| `Shumway.Tests.Embedding` | 1595 |
| **Total** | **2640** |

All green at the closure tag. Phase 16 added 3 new tests
(`Chunk182Tests`) — small but architecturally definitive: the
deep-chain test demonstrates the C# stack stays O(1) regardless
of Prolog depth, which is the headline guarantee threading was
opened to provide.

## What this didn't fix

Blint with Tier-1 (`SHUMWAY_IL_PROMOTE=32`) is still ~8× slower
than Tier-0 (~50s vs ~6.7s). Threading was a *necessary* but not
*sufficient* change — the structural concern Phase 16 set out to
fix (recursive C# stack frames per Prolog call) is resolved, but
the perf gap turns out to live elsewhere.

Diagnostic data after threading:

- `DiagSubroutineCalls = 0` (RunSubroutine genuinely unused).
- `resumeDispatches = 15399` (marker decoding fires only when
  IL'd predicates return — a small fraction of total dispatch).
- `OnDispatch calls = 2.1M` — every bytecode Call/Execute still
  consults the dispatcher; the per-call lookup cost is the
  dominant remaining overhead.

Possible follow-ups outside Phase 16 scope:

- Cache OnDispatch result at the call site (per-bytecode-PC
  cache instead of per-functor dictionary lookup).
- Replace the linear cursor-switch (each delegate's top) with
  an IL `switch` opcode (jump-table) for predicates with many
  Call sites.
- Investigate whether the IL emission inflates code size for
  Blint's hot predicates such that JIT'd native code is slower
  than the bytecode interpreter for them.
- Address the IndexOutOfRangeException at `engine.IlPromotion.
  Threshold = 1` (a pre-existing IL-emit bug at low
  thresholds, separate from threading).

## Roll forward to Phase 17+

Open candidates:

- **Cursor switch as IL `switch` opcode**. A jump table is O(1)
  vs the current linear chain of `BranchIfEqual`s. For
  predicates with > 8 Call sites the gain is substantial.

- **OnDispatch result caching per Call site**. The bytecode
  interpreter currently looks up `_predicatesByAddress` and
  `_unpromotable` on every Call/Execute. A per-PC cache (clear
  on link rebuild) would eliminate ~90% of those dict lookups.

- **The IndexOutOfRangeException at threshold=1**. Surfaces
  when promote=1 IL-compiles a predicate whose
  `engine.UnifyVariableX` references X[slot] for an unallocated
  slot. Almost certainly an IL emit ordering bug — needs a
  small reproducer.

- **Persisted-IL bundles under threading**. The chunk-71
  `EmitPersistedMethod` path emits IL into a .NET assembly for
  AOT-bundling. Chunk 182 only updated the runtime DynamicMethod
  emitter; persisted-IL still emits the chunk-50 synchronous
  Call helper. Threading should propagate to the persisted path
  for end-to-end consistency.
