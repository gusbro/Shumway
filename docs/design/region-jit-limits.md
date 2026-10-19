# A region stays inside the JIT's optimizing range

**Status**: IMPLEMENTED (2026-10-18). An extension of
[il-region-compilation.md](il-region-compilation.md); no ADR changes.

## Problem

The runtime's JIT compiles a method without optimization once it is past
fixed sizes, tested on the IL as emitted: 60,000 bytes, 20,000 instructions,
2,000 locals, 8,000 references to locals and arguments. Such a method gets no
inlining and no register allocation; every accessor is a call. The knobs that
raise the sizes exist only in checked builds of the JIT.

The region planner bounded a region by the bytecode it absorbs, 3,072 bytes,
a proxy for the IL. The proxy is loose: regions at that budget emit between
3 KB and 80 KB of IL, and the reference limit falls at about 47 KB. In Blint,
35 of its 118 regions crossed it, holding half of the emitted IL and more than
half of the native code; in the clpz puzzles, 13 of 139. This is the cause
of a result that had none: with regions, Blint ran 18 to 23% slower than
with every predicate compiled on its own.

A smaller byte budget is not the answer. At 1,024 bytes no Blint region
crosses the limit and Blint runs 15% faster, but sendmore loses 14% and
zebra 12%: their regions were well inside the limit, and the smaller budget
only cut them into trampolines.

## Idea

Plan in the JIT's own measure. The planner predicts what each member will
cost in local references before anything is emitted, stops adding members
near a target under the limit, and the emitter verifies the result with
exact counts. Only a region the estimate let past a limit is planned again,
now with every member measured, and emitted again; the discarded emission
cost its IL, never a JIT.

Three facts, measured on Blint, the clpz puzzles and the Van Roy suite,
make the prediction cheap:

| what | measured |
|---|---|
| a member's block, from one region to the next | the same to within 2% for 90% of the members, 28% at worst |
| a member never emitted, estimated from its opcodes | median error 21%, with a heavy tail where guards inline their callees |
| the shared tail (return and fail handlers, resume entries, push ladder) | 181 + 23 per resume entry + 5 per ladder entry, mean error 57 |

So a member that was ever emitted predicts itself, the opcode model serves
the first emission, and the tail follows from the plan.

## What changes

- `IlEmit` counts what it emits: instructions, references to locals and
  arguments, locals declared. `IlJitLimits` holds the JIT's sizes and tests
  an emitter against them.
- `IlRegionCost` is the model: the target (`SHUMWAY_REGION_LVREFS`, 0 turns
  the model off), the measured cost of each member (the first measure stays),
  the opcode estimate, the tail, the budget the members may add up to, and a
  log of emissions for diagnostics and tests.
- `IlRegionBuilder.Build` takes a member cost and a cost budget beside the
  byte budget: a callee whose cost would take the sum past the budget stays a
  trampoline, like one past the byte budget.
- The compiler plans every region through one method (`PlanRegion`), records
  each member's block as the region is emitted, and emits a region up to
  three times: at the plan, at the plan with every member measured, and at a
  budget lowered by the overshoot. A plan that keeps no member gives up the
  region for the root's own method, a method under the limit.
- The linker and the bundle writer measure every member first
  (`WarmRegionCosts`: each root's region emitted once to a scratch method)
  and then select roots. The membership the prune computes must be the
  membership the emission produces, so the persisted path never plans a
  region twice; it counts a region past a limit and ships it.

## Why the first measure stays

The prune of a bundle asks which predicates each root absorbs, then emits.
If a member's measured cost moved between the two, a predicate the prune
counted as absorbed could end up a trampoline target with no standalone
form. The warm pass measures every member before the first question, and a
measure never changes afterwards, so both answers come from the same
numbers.

## Tests

`RegionJitLimitTests` compiles a chain of predicates whose closure, absorbed
whole, is far past the limit, with the byte budget out of the way, and
checks every emission against the limits and the JIT's own verdict: an
`EventListener` on the runtime's method-load events, where the optimization
tier rides in the method flags. With the target off, the same program gives
the JIT a region it compiles without optimization: the counter-proof.

## Results

Measured in one process, base and candidate in load contexts of their own,
eight rounds ABBA over two copies, the minimum of each (the notebook's
clock is bimodal per process); heap cells identical throughout.

| program | regions past a limit, before | after | time, after / before |
|---|---|---|---|
| Blint | 35 of 118 | 0 | 0.82 |
| clpz puzzles (13, over the Scryer library) | 13 of 139 | 0 | 0.95 |
| Van Roy, ten programs | none | none | 0.98 to 1.03, each within its noise |

Two Blint regions and one clpz region are emitted twice; the rest land
under the limit at their first plan, 7,241 to 7,775 references where the
opcode estimate ran low, the margin's purpose.

A lower target gains a little more on Blint, 5% at 5,000, and loses it elsewhere:
at 5,000 boyer's one region of 5,409 references is cut in two and boyer
loses 7%. The default stays at 7,200, the largest target under the limit
with a margin for the estimate. Why Blint prefers smaller regions than the
limit allows, when the Van Roy programs do not, is not established: the
inliner's budget and the duplication of shared members are the candidates,
and a target by program is a lever for later.

## The wasm tier

Not mirrored: the wasm tier emits one function per predicate and the browser
engines tier their own code up; no size at which they stop optimizing is
known. Recorded here as a divergence of the backends, not of the design.
