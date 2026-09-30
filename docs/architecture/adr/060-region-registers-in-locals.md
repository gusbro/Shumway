# ADR-060: A Region Holds the Machine Registers in Locals

## Status

Proposed (2026-09-29). Builds on
[ADR-058](058-region-choice-points-are-wam-choice-points.md) and extends
[ADR-011](011-il-compiler-architecture.md). When its stage 2 lands it replaces
the emission form of ADR-058 item 6 (the push and the restore inline at every
site) with sequences shared inside the region. The frame layout and the choice
point model of ADR-058 do not change.

## Context

The machine registers are instance fields of the activation: `E`, `CP`, `B`,
`B0`, the stack top, the heap top, `HB`, the two trail tops. The interpreter,
the builtins and the compiled code all read and write them there. A Tier-1
region receives the activation as its first argument and reaches a register
through it, so every use is a memory access.

Measured on the region of `queens/2` (`boards.pl`), from the JIT's
listing:

| | count |
|---|---:|
| native instructions | 4,662 |
| loads of an activation field | 438 |
| stores to an activation field | 79 |
| calls left in the region | 261 |
| calls the JIT inlined | about 620 |

Three facts make the fields expensive:

- The JIT cannot keep a field in a processor register across a call:
  the callee may have changed it. With 261 calls in 4,662 instructions, a
  register read twice is loaded twice.
- A helper the JIT inlines still reads the field: it is code written
  over `this`. Inlining removes the call, not the memory access.
- The JIT stops inlining in a large method. A region that depends on a
  helper being inlined gets a call instead when the region grows.

The wasm tier ([ADR-050](050-wasm-tier1-backend.md)) holds the heap top, the
binding trail top, `E`, `B`, `HB`, the stack top and `CP` in locals of the
module's function, and writes them back when it leaves the module.

Code size is the second problem. ADR-058 item 6 emits the choice point push
and restore inline at every site, about 180 IL instructions each. Three forms
of the same operation, on `queens/2`:

| | calls | shared sequence | inline at each site |
|---|---:|---:|---:|
| IL of the region | 4,768 | 6,631 | 12,049 |
| native code of the region | 17,589 | 20,733 | 28,619 |
| `queens(9)`, minimum of 12 processes, two runs | 0.386, 0.416 s | 0.363, 0.400 s | 0.351, 0.382 s |
| `bc3(3000000)`, minimum of 12 processes | 0.471 s | 0.428 s | 0.435 s |

A region's IL grows with the number of sites, and the JIT's budgets
(inlining, optimization) are per method. Code repeated at every site spends
them on a small program and runs out on a large one.

What the operations of this ADR cost today, in calls per run:

| | choice point pushes | `Allocate` | `Cut` |
|---|---:|---:|---:|
| `queens(9)` (`boards.pl`) | 2,333,818 | 3,964,273 | 9 |
| `tak`, `bench(5)` | 0 | 397,563 | 238,536 |

## Decision

1. **The registers a region holds.** Within one invocation of a region method,
   these live in IL locals: `E`, `CP`, `B`, `B0`, the stack top, the heap top,
   `HB`, the binding trail top, and the references to the stack, heap,
   register and binding trail arrays. The extra trail top and the view
   generation stay in their fields: helpers write them, and the region reads
   them only at a push and at a restore.

   The activation's fields remain the state between invocations and for
   everything outside the region. A local is a working copy that lives for
   one invocation.

   The choice is per region and per register, made before emission from the
   region's bytecode: a region holds a register when it has code of its own
   that uses it. A register a region does not hold is read and written
   through the field, as today. A region whose operations are all helper
   calls holds nothing and compiles to the code it compiles to today.

2. **Inside the region the local is the truth.** For a held register the
   field is stale between two points of synchronization:
   - *load*: at the region's entry, every held register;
   - *spill*: before anything outside the region's own code reads the
     register: a helper call, a builtin, a return (success, failure, a
     continuation elsewhere), a call that can throw, a wakeup, a heap
     collection;
   - *reload*: after a call that can write the register.

   The emitter knows, along straight-line code, whether a held register was
   written since its last spill, and omits the spill of one that was not. At a
   label it assumes every held register was written.

3. **One table of helpers.** Every method handle the emitter owns (121) has
   a row: the registers the method reads and the registers it writes, read
   off its IL and the IL of everything it calls. A call the analysis cannot
   follow (a delegate, a virtual method, a function pointer) makes the method
   read and write all of them; that is what a wakeup, a heap collection, a
   builtin, a cut with a prune callback and a trail unwind come to. A method
   that can throw a Prolog error also reads the heap top, `HB` and the
   binding trail top: the catch resolution trial-unifies the ball at the
   current heap top and unwinds the trails from the current tops before it
   rolls the rest of the machine back from the catch frame.

   Every call is emitted through one function that spills what the row reads,
   calls, and reloads what the row writes. A method without a row (a handle
   made per host, in an embedded native block) is called as if it read and
   wrote every register.

4. **Values cross by value.** The address of a held register is never taken.
   A helper that computes a new value for a register receives the old one as
   an argument and returns the new one, or the region spills and reloads
   around it. This holds for helpers marked for inlining too: when the
   JIT declines to inline one, a local whose address was passed lives in
   memory for the whole method.

5. **The test of a rare path is inline, its work is not.** The pending wakeup
   check, the heap collection and cancellation safe point of a call, and the
   frame hooks of ADR-058 are each a compare on the fast path. The spill, the
   call and the reload are in the branch that the compare guards.

6. **A long sequence is emitted once per region.** Sites enter it by a branch
   and it returns through a switch on an index the site left in a local. A
   short sequence is emitted at the site.

   | operation | IL instructions | form |
   |---|---:|---|
   | choice point push | about 180 | shared |
   | choice point restore | about 180 | shared |
   | `allocate` | about 30 | at the site |
   | `deallocate`, without reclaiming | about 15 | at the site; the reclaiming path is a call |
   | cut, nothing to discard | about 6 | at the site; discarding is a call |

   - *Push.* One ladder per region with an entry per arity: the entry for
     arity n stores argument n and falls into the entry for n - 1. An
     argument's cell is at a fixed offset from the frame's base whatever the
     arity, so each step is one store. After the last step come the control
     words, whose base depends on the arity the site left in a local, the
     frame hooks test and the return.
   - *Restore.* Every resumption inside the region passes through the fail
     handler and the dispatch switch. The restore (arguments, control words,
     trail unwind) goes between the two, once per region, and needs no
     return: the dispatch is its return. A resume entry keeps the two or
     three stores that tell a retry from a trust. A resumption from outside
     enters the region at a resume cursor and takes the same path.

7. **The region keeps room for its largest frame.** The ladder stores before
   any check, so the check moves: inside the region the stack always has room
   above its top for the largest frame the region writes, a size known when
   the region is compiled. The region establishes it at entry, after each
   frame it writes, and after a call whose row says it writes the stack top
   or replaces the stack array. It is one check per frame, as today.

8. **The table is verified twice.** A test reads the IL of every method with
   a row, and of everything it calls, and fails with the corrected rows when
   a row differs from the code: a method that starts reading one more register
   changes the table or breaks the gate. A checked emission mode verifies the
   writes and the emitter's own bookkeeping at run time: under it every call
   spills all held registers and, after the call, every field the row says
   the method does not write must still equal its local. The test gate runs
   regions in this mode; a release build does not.

9. **A region compiled for verification or for the debugger holds nothing**,
   the rule ADR-058 has for its inline frames. Until stage 2 emits those
   frames over the locals, a region that holds a register takes the frame
   methods, whose rows spill and reload it.

10. **The low-level public surface grows** by the heap and binding trail
    arrays and the binding trail top, under the terms of ADR-058 item 6:
    public fields with their current names, hidden from editor completion,
    reserved for generated code. One form serves the regions compiled at run
    time, the persisted ones and the .NET Framework target
    ([ADR-043](043-net-framework-target.md)).

## Stages

Each stage is measured against the one before it and stays only if it gains on
its benchmark without losing on the others.

| stage | content | benchmark |
|---|---|---|
| 1 | the locals, the load and spill points, the table, the checked mode; no operation converted | none: the time must not move |
| 2 | `allocate`, `deallocate`, cut, call and proceed over locals; the push ladder; the restore in the fail path | `tak` (control and arithmetic, no heap construction), `queens(9)` |
| 3 | heap construction: `put` and `unify` in write mode | `nreverse` |
| 4 | `unify` in read mode and binding (the `HB` test, the trail push) | `queens(9)`, `zebra` |
| 5 | the wasm tier: its long sequences as functions of the module | measured in a browser |

Stage 4 emits the binding rule of [ADR-004](004-two-trails.md) a third time
(the activation, the wasm emitter, the IL emitter).

## Results

Stage 1. The emitter routes its 273 helper calls and 17 returns through the
register file and holds nothing, so the IL of every region is byte for byte
what it was: the JIT reports the same IL size for the six regions of the Van
Roy programs and `queens/2`. `queens(9)`, minimum and median of 8 processes:
0.355 and 0.365 s before, 0.356 and 0.369 s after. The table has 121 rows,
101 of them narrower than everything; the 20 at everything are the cut and
trail-unwind methods (a prune callback, an extra-trail entry), the wakeup
boundaries, the heap collection, the builtin, meta-call and indexed dispatch,
and four arithmetic entries. With every exposed register held and the checked mode on,
a region computes what Tier-0 computes over choice points, cuts, a caught
error, a 20,000-frame recursion and builtin choice points; a row that hides
the write of `B` by a push fails at the call.

## Alternatives considered

- **Leave the registers in the fields.** The state of things. The memory
  accesses stay, and so does the dependence on the JIT inlining helpers.
- **Pass a held register by reference to helpers marked for inlining.** Fewer
  rows in the table. Rejected: the inlining is not guaranteed in a large
  region, and a local whose address escaped stays in memory (item 4).
- **Spill every register at every call**, as the wasm tier does when it leaves
  the module. Simple, and right for the wasm tier, where leaving is expensive
  whatever is spilled. In IL a helper that touches only the heap top would pay
  eight stores and eight loads.
- **Inline at every site** (ADR-058 item 6 as it stands). The fastest form
  measured, by 3 to 5% on `queens(9)` over the shared sequence, at 1.8 times
  its IL.
- **The push and the restore as lean methods of the activation.** One copy
  per process instead of one per region, and the processor predicts a return
  better than a switch. With the registers held, such a method needs a spill
  and a reload of most of them around it. It is measured in stage 2 as the
  control.
- **A smaller frame.** The saved `HB` can be derived from the previous choice
  point, and the view generation is only read by dynamic predicates. Not in
  this ADR: the frame is written in three places and read by the heap
  collector.

## Why this is sound

The fields hold the machine's state at every point where anything other than
the region's own code can observe it: that is what items 2 and 3 establish.
Between two such points only the region's code runs, and it reads the locals.

Activations stay thread-agile: a local does not outlive an invocation, and an
activation changes thread only between invocations. Compiled code stays
independent of the engine: it loads the registers from the activation it
receives.

The shared sequences are the stores and loads of ADR-058 item 6, on the same
frame, reached by a branch instead of repeated.

## Risks

- **A missed spill or reload corrupts without an error.** The region writes
  through a stale array reference, or a helper reads a stale field. The
  checked mode (item 8) turns both into a failure at the call.
- **The table goes stale.** A change to an activation method that makes it
  read one more register is invisible to the emitter. The checked mode in the
  gate is the only guard; a method's row is part of its contract from now on.
- **A region that leaves often.** Every exit spills and every entry loads. A
  region whose calls mostly go to other regions may lose more there than it
  gains inside. Item 1 lets such a region hold fewer registers; stage 1
  measures the round trip.
- **The shared sequence returns through an indirect jump**, which cost 3 to 5%
  on `queens(9)` against the inline form. The restore avoids it (item 6); the
  push does not.
- **Three emitters of the same rules** after stage 4. A change to binding or
  trailing that misses one is a silent divergence between tiers. The
  equivalence suites against Tier-0 are the guard.
- **Emitter complexity.** Every emission site that touches a register or
  calls a helper changes. The stages keep each change measurable and
  reversible on its own.

## Validation

- The equivalence suites of ADR-057 and ADR-058 (Tier-1 against Tier-0), the
  cut, soft-cut and guard suites, the persisted-IL bundles, the .NET Framework
  lane, and the full Embedding gate with regions compiled in checked mode.
- The table test: each row against the code of its method.
- Heap cells and inferences of `time/1` identical to Tier-0 at every stage.
- A run with a minimal initial stack, so that growth falls at every point
  where the region can meet it, compared against Tier-0. It counts the
  growths: a run without any does not pass.
- Counter-proofs, each of which must fail: a spill removed before a helper
  that reads the register; a row of the table with a register missing from
  its writes; the reload of the stack array removed after a call that grows
  the stack.
- Timing: minimum and median over at least 8 processes per variant, in
  rotation, against the previous stage.
