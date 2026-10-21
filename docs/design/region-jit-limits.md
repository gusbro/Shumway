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
- Before a plan sheds members, a region measured past the cap on locals
  (the measure alone, under the floor of references) or past a limit of
  the JIT sends to the engine's matcher the head-matching streams it
  emitted, at or past a threshold of locals, of the arguments every local
  caller passes fresh (ADR-063, `IlRegionCost.CompactStreams`,
  `IlColdArguments`): the stream of a data fact's nested argument costs
  the method its locals whether it runs or not, and the members stay
  together; a stream some caller reads bound stays.
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
limit allows is the next section.

## The inliner's cap

The JIT keeps one more count against a method: its locals, the arguments,
the locals declared and the temporaries it creates as it imports the IL.
Every inline it makes adds temporaries, for the callee's arguments, its
locals and its result. Once the count reaches 1,024 (the JIT's
`JitMaxLocalsToTrack`, a process-wide setting a library cannot change), the
JIT declines every remaining call site of the method, "too many locals",
whatever the callee: a field read and a struct constructor stay calls. The
cap is absolute, not a share of the method's size, so a larger region loses
a larger tail of itself to it.

The JIT's own events (`MethodJitInliningFailed`, keyword JitTracing) show
it on Blint at the 7,200 target: 69 of 121 regions reach the cap, after
435 inlines at the median, and 19,680 inlines are declined for it; the
hottest among them are the wake check after a builtin call, the register
reads, the atom cells, the trail push. In the native code the regions keep
two to four times the calls to small helpers per instruction that regions
planned at 5,000 do. boyer's one region of 5,409 references never reaches
the cap: it holds few call sites per reference. The reference target cannot
tell the two apart.

### What a call site costs the JIT

A count of call sites does not tell either: the JIT inlines in cascade, a
call to `TrailBind` becomes four inlines (`TrailBinding`, the capacity
check and the size getter inside it), a fused arithmetic step thirty, and
a call to a helper over its size limit none. Over the corpus the
temporaries per emitted call site ran from 2.3 in Blint to 5.1 in crypt,
while the temporaries per inline the JIT made ran from 1.4 to 1.8, and
what separates the two is a property of the callee, the same in every
program: how many inlines its call expands into, and whether the JIT takes
it at all.

So the emitter counts inlines, not sites. `IlInlineTable` holds, for every
method the emitter calls, the inlines the JIT makes for a call to it,
measured from the JIT's events over a probe method per callee (a method
with one call to the callee, its arguments fresh locals, behind a branch
the invocation takes so nothing runs). `IlInlineTable.Generated.cs` is
written by `IlInlineTableTests` with `SHUMWAY_INLINE_TABLE_OUT` set, run
with that class alone in the process: the JIT remembers a callee it once
refused for its own code, and the probes go before any compilation. The
same test, on Release, holds the table against the JIT of the build, and
on every framework holds it against the callees the emitter names and
uses. A method the table does not name counts one inline when its body is
small or marked for aggressive inlining, none otherwise.

### The locals model

A member's cost in the JIT's count is the locals its block declares, exact
and additive since the count only grows, plus 1.19 temporaries per inline
the table predicts: the JIT creates 1.4 to 1.8 per inline it makes and
makes 80 to 95% of the predicted ones, the rest judged unprofitable at
their site, and the regions that reach the cap with the largest that does
not pin the product between 1.17 and 1.24. A member never emitted is
estimated from its opcodes, fitted like the reference model (median error
20%; a ratio to the references errs 36%, the arithmetic opcodes inlining
far more per reference than the rest). The prologue, the tail and the two
arguments add 25.

`IlRegionBuilder.Build` holds the sum under the locals budget beside the
reference budget, and a region emitted past the target is planned again
like one past a limit. The target, `SHUMWAY_REGION_LOCALS`, 0 for off, is
1,000, the cap less the estimate's slack: on Blint it runs 5% ahead of
1,100 and 4% ahead of 900. The budget applies to a region of at least 4,000
references: a smaller one is one hot path, and a cut into it costs more
than its last sites as calls (flatten's largest, 3,493 references and a
third past the cap, loses 5% cut; sendmore's hold a single member each
past the cap, which no cut mends). The budget never leaves a root alone
either: a root alone is a method, not a region, and it takes the first
callee the budget refused, two members past the cap beating two methods
and a trampoline between them. The Van Roy programs keep their regions
to the member; Blint's and the puzzles' are cut.

A site the emitter can avoid is one fewer for the JIT to decide and to
spend temporaries on. The wake check every goal boundary makes
(`HasPendingWakeups`, a fifth of the sites of a Blint region) is emitted as
a read of the queue's count through the activation's field, not as a call
of the getter: the programs whose regions reach the cap inside their one
hot loop gain from it, sendmore 0.95, flatten 0.95, crypt 0.96, qsort
0.97, tak 0.98; Blint and the puzzles, whose regions reach it in their
cold tails, are even.

| program, at the 7,200 target | regions reaching the cap, by references alone | under the locals target | call sites left as calls, before and after |
|---|---|---|---|
| Blint | 69 of 121 | 28 of 124 | 19,680 and 2,578 |
| clpz puzzles | 302 of 583 | 125 of 583 | 105,589 and 10,969 |
| boyer and the Van Roy nine | as before | the same regions, to the member | as before |

A region still at the cap is one method under full optimization like any
other: the cap is a point of no return inside the method, so what it loses
is its last call sites in IL order, the choice-point helpers and register
reads of its last members, and nothing before them. In Blint those are 6%
to 27% of the sites of each such region. Of its 28, 21 were planned at the
edge of the target (the model's factor is the middle of its band, so a
region planned at the target reaches the cap about half the time), 3 are a
root with the one callee the budget refused, 4 are under the floor. A
target of 900 takes the 21 under the cap and runs Blint 3% slower than
1,000: each cut is a trampoline, and that costs more than the sites it
saves.

Measured in one process against the tip this branch grew from, eight
rounds ABBA over two copies, heap cells identical: Blint 0.93 at the
minimum and 0.96 at the median, the clpz puzzles 0.94 and 0.90, sendmore
0.96, crypt 0.96, flatten 0.96, qsort 0.97, the rest within their noise.
A first form counted call sites, one each, and held them under 400: it
gave the same on Blint and the puzzles and nothing on the Van Roy
programs, and it did not generalize, since what a site costs ran from 2.3
to 5.1 temporaries by program.

`RegionJitLimitTests` compiles the chain program twice under other names,
by references alone and under a locals target below its widest region,
and asks the JIT's inlining events how often it stopped: the second must
be under half of the first, and the planner's own count under the target
in every region. The JIT's part runs on Release only: Debug helpers inline
less and never reach the cap.

### Raising the cap from the host

The cap is the runtime's `JitMaxLocalsToTrack`, a hexadecimal number the
JIT reads from the environment once, when it starts, before `Main` runs:
`DOTNET_JitMaxLocalsToTrack`, then the `COMPlus_` prefix, and nothing
else. A probe method with 1,200 call sites of a two-argument helper shows
what reaches it: 921 inlined and 279 refused with nothing set, 1,200 and
none with the variable in the environment at launch, 921 and 279 again
with the variable set by `Main` itself as its first statement or written
in `runtimeconfig.json` under any name. So a shell, a launcher or a parent
process raises it, and a program cannot raise it for itself. `3072` reads
as 0x3072: write `0xC00`.

The engine reads the value the way the JIT does (`IlJitLimits.InlineLocals`)
and the default target follows it, the cap less 24. Measured with one
process per configuration in ABBA order, eight processes each on Blint
and four on the clpz puzzles, the minimum over them, the median of every
round, and the JIT's own compilation time per process (the puzzles
compile their regions twice per process):

| cap, target | Blint minimum | Blint median | puzzles minimum | puzzles median | puzzles JIT time |
|---|---|---|---|---|---|
| 0x400, 1,000 (the defaults) | 0.400 s | 0.493 s | 1.194 s | 1.614 s | 116 s |
| 0xC00, 3,048 | 1.01 | 1.02 | 1.09 | 1.63 | 1.65 |
| 0xC00, 1,000 | 0.98 | 1.02 | 0.95 | 1.23 | 1.35 |
| 0x1600, 5,608 | 1.01 | 1.11 | 1.20 | 1.70 | 1.54 |

With the cap tripled and the same plans, the regions at it are inlined
whole and the programs run the same at the minimum, Blint 0.98 and the
puzzles 0.95, inside the noise of a measurement across processes. With
the target following the cap, the plans are the ones from before the
locals model, now under no cap, and that is slower: Blint 1.01, the
puzzles 1.09. The 7% those plans lost to the cap is what the model
recovered, by planning rather than by the runtime, and a larger method
is not a faster one. The cost is on the other side: the JIT spends 35%
to 65% more time compiling the puzzles' regions, and the medians, which
the background compilation overlaps, say so. Nothing in the toolchain
sets the variable; a host that tries it has `0xC00` to `0x1600` as the
sensible range, and the planner follows.

## The wasm tier

Not mirrored: the wasm tier emits one function per predicate and the browser
engines tier their own code up; no size at which they stop optimizing is
known. Recorded here as a divergence of the backends, not of the design.
