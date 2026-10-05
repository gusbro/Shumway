# ADR-061: Tier-1 as Continuation Methods per Predicate

## Status

Proposed (2026-09-30). Stage 1 is implemented behind
`SHUMWAY_IL_CPS=1` (2026-10-01; see Stage 1 in the engine). If accepted it replaces region compilation
([Phase 29](../../history/phase-29-closure.md),
[design](../../design/il-region-compilation.md)) and, for transfers between
compiled code, the resume-marker round trip through the dispatch loop
([Phase 16](../../history/phase-16-closure.md)). It keeps the choice point
model of [ADR-058](058-region-choice-points-are-wam-choice-points.md) and the
local backtracking of [ADR-057](057-local-backtracking-in-il-regions.md),
generalized from a region to the whole program. It narrows
[ADR-060](060-region-registers-in-locals.md) to what still pays inside a small
method. It extends [ADR-011](011-il-compiler-architecture.md). The wasm tier
([ADR-050](050-wasm-tier1-backend.md)) takes the same shape with
`return_call` and `return_call_indirect`.

## Context

A Tier-1 region is one IL method holding several predicates. Inside it a call
is a branch and a return is a `switch` on a cursor. Between regions, and
between a region and a standalone predicate, control goes back to the
bytecode interpreter's dispatch loop: the callee returns `true`, the loop
decodes the resume marker in `Pc`, finds the delegate and invokes it.

Measured on a program that makes one call and one return between regions per
iteration (`xr2.pl`: `loop/1` calls `p/1` and `q/1`, `q/1` calls `q2/1`),
20,000,000 iterations, the same build and process:

| | seconds | per iteration |
|---|---:|---:|
| all four predicates in one region | 0.65 | 32 ns |
| `loop` + `p` in one region, `q` + `q2` in another | 2.25 | 112 ns |

About 80 ns per call and return between regions. Replacing the round trip
through the loop with an IL tail call from one region to the other's
continuation (a prototype, `tail. callvirt` on the continuation's delegate)
saved 7%: the loop was not the main cost. A profile of the prototype put the
rest in the transfer itself: a non-inlined helper, a chain of delegates
(`Func` to `PredicateDelegate` to a shuffle thunk), and the prologue and
epilogue of a large region method (eight callee-saved registers pushed and
popped, the held registers of ADR-060 reloaded).

Growing regions does not remove the boundary. An IL method cannot branch into
another method: `call`, `jmp` and `calli` enter at the start. And a region
cannot grow without limit: the JIT stops inlining helpers in a large method,
stops tracking locals past about a thousand, and falls back to unoptimized
code past about 60,000 bytes of IL or 2,000 basic blocks. A region is also
recompiled whole when one of its members changes.

## Decision

1. **The unit of compiled code is a method per entry point of a predicate.**
   A predicate compiles to one method for its entry, one per continuation
   (the code after a call that is not the last of its clause), and one per
   alternative that a choice point resumes. Every method has the signature
   `bool M(Activation)`. There is no cursor argument and no `switch` on
   entry: each method has exactly one entry.

2. **Every transfer is a tail call.** A call to a predicate, a proceed to
   `Cp`, and a failure to the alternative of the top choice point all leave
   the method with `tail.` followed by `ret`. The .NET stack holds one
   compiled frame at any time, whatever the depth of the Prolog computation.
   Code inside one method (clause bodies, local backtracking between the
   clauses of the predicate, inline if-then-else, indexing) stays as today.

3. **The code lives in a collectible dynamic assembly per program
   generation** (`AssemblyBuilder`, `RunAndCollect`), one type per promotion
   batch. A generation is what one consult or bundle load produces; it is
   unloaded when none of its methods is reachable. Methods are marked
   `AggressiveOptimization` (compiled once, optimized, like a
   `DynamicMethod`), with `InitLocals` off. Persisted bundles use the same
   emitter into their `PersistedAssemblyBuilder`: one code shape for runtime
   promotion and for bundles.

4. **Transfers go through tables of native code pointers held in Core.**
   - A call to a predicate in the same promotion batch is a direct
     `tail. call`.
   - A call to a predicate in another batch is `tail. calli` through the
     callee's slot in a per-functor table. The slot always holds a valid
     target: the compiled entry, or a trampoline into the interpreter.
     Promotion and eviction write the slot; no caller is recompiled.
   - A proceed is `tail. calli` through the resume table, indexed by the
     dense resume id that `Cp` holds (today's resume marker).
   - A choice point stores the resume id of its alternative; failure resumes
     it through the same table.

5. **A table entry is the method's native code, and the table keeps its
   generation alive.** The pointer is taken after `RuntimeHelpers.PrepareMethod`
   (without it `GetFunctionPointer` returns a precode stub, one extra jump
   per transfer). A raw pointer does not keep a collectible assembly alive:
   the generation is referenced strongly while any slot or resume entry
   points into it. A continuation already captured in a frame or a choice
   point keeps resuming into retired code, as a retired resume does today;
   the slot swap guarantees that no new call enters it.

6. **The fast path of a method makes no call other than its final tail
   call.** A slow path (a safe point, a wake, an error) leaves by a tail call
   to a trampoline that does the work and then enters the target. The JIT does
   not shrink-wrap: a call on any path makes every entry save what is live
   across it in callee-saved registers. Frames written to the stack (a choice
   point, an environment) are addressed from one base reference after one
   bounds check. Values used after a loop are reloaded from the activation
   instead of being kept live across it.

   Any call that returns breaks this, on however cold a path: one is enough
   for every entry to push the callee-saved registers. A helper that must do
   work does not return to the method; it continues, by a tail call, at a
   point where all the machine's state is in the activation. Three ways to
   give that point an entry, used in this order:

   - **The method itself.** Capacity checks (heap cells, registers) move to
     the method's entry, sized at compile time for the code up to the next
     transfer, and to the self-recursion back-edge. When one fails, the
     helper grows the area and enters the same method again.
   - **A continuation method per slow site**, at the next instruction
     boundary, like the continuation after a call. For a slow path that
     measurement shows is taken often.
   - **One cold method per predicate**: the ordinary emission, with a switch
     over the instruction boundaries, for the remaining slow paths not worth
     a method of their own. It pays its prologue only when a slow path was
     taken, until the next transfer.

   Error helpers that always throw are not calls in this sense when the JIT
   can see that they never return: they must not be marked `NoInlining`,
   which hides that from it.

7. **Safe points and goal boundaries are tested at the call site**, in the
   caller, before the transfer: cancellation, deadline, the heap collector's
   threshold, pending wakes (ADR-049), the debugger's arming. One predicted
   branch on the fast path; the slow path is a trampoline (item 6). This is
   the check the dispatch loop makes today at a resume marker.

8. **A failure leaves by a tail call to `Fail`**, which resumes the top
   choice point's alternative, or returns `false` to the driver when the
   alternative has no continuation method. Backtracking between compiled
   predicates does not return to C#. The failing method makes no call that
   returns; the alternative's own entry restores and pops the frame (a
   trust), inline, since it knows it is entered that way. A choice point
   names the code of the generation that pushed it (`CpsAlt`), not its
   functor's current code: a recompiled predicate may number its cursors
   differently.

9. **Continuation methods are compiled from the delegate's own variant.**
   They push choice points that the delegate resumes, so both must number
   the cursors alike. A predicate whose delegate is the instrumented PGO
   variant gets no continuation methods in stage 1 (the plain variant
   numbers its alternatives differently, and resuming one of its choice
   points in the instrumented delegate skipped solutions).

10. **Snapshots of dynamic predicates stay `DynamicMethod`s** (ADR-023,
   ADR-034). They are retired on mutation and must be collectible one by one.
   Their functor's slot points to a fixed trampoline that tail-calls the
   current snapshot's delegate; only calls to a snapshot pay the extra jump.

11. **Where a runtime does not honour `tail.`, a hop counter bounds the .NET
    stack.** Every 64th transfer returns to the dispatch loop, which
    continues at `Pc`. On CoreCLR x64 an explicit tail call with this
    signature always compiles to a jump; the bound covers .NET Framework
    (ADR-043), where the JIT may decline it.

12. **`tail. calli` is emitted directly.** The emitter (ADR-062) adds `tail.`
    to a `call` or `callvirt` before `ret`; the one `tail. calli` is in the
    `Jump` stub, written with `ILGenerator`, which each site tail-calls.

13. **When the target is known at compile time, execution continues in the
    same method instead of transferring.** A transfer runs the method's
    epilogue (its pops) before the jump and the target's prologue (its
    pushes) after it; continuing pays neither. Self-recursion branches back
    to the entry's code inside the continuation method: the branch is not a
    loop when the clause makes a non-tail call, since that call leaves the
    method, so the JIT sees straight-line code. The size of the method bounds
    how far this goes. A proceed stays a transfer: its target is known only
    at run time.

## Bundles and link-time optimization

The linker optimizes a program in two layers, and only the second one knows
about regions.

**Clause level, unchanged.** These passes work on the clauses and the
bytecode the `.shmo` objects carry, before any IL exists, and do not depend
on the Tier-1 shape:

- the cross-module meta-wrapper unfold, recompiling callers from the raw
  clauses (the `ClauseTerms` channel);
- whole-program redundant-cut elision (ADR-030's linker closure);
- reachability from the roots, and the prune of what no root reaches;
- on-demand pulls from `.shum` libraries, optimized with the explicit
  objects.

**IL level, what regions give a bundle today** (`BundleWriter`):

| today, with regions | with continuation methods |
|---|---|
| A call between members of one region is a branch. | A call inside the bundle is a direct `tail. call` to a method of the same assembly. |
| A region absorbs only its own module's predicates (`RegionMemberScopeFids`): absorbing across modules would bake a callee into every entry that reaches it. | No absorption and no duplication: each predicate is emitted once, and a call across modules is as direct as one inside a module. |
| Without the dead-region prune, a member absorbed by several regions is emitted in each (2.3 times the bundle). | Each predicate is emitted once, prune or not. |
| The dead-region prune gives an absorbed-only member no standalone method and strips its WAM; a call by name reaches it through a member-entry alias (`RegionMembers`: root marker plus entry cursor). | Every compiled predicate has its own entry method. A predicate no one calls by name needs no slot; `--strip-wam` drops the WAM of every predicate with a method, except where Tier-0 still needs it. |
| Leaf and small-rule inlining at a call site, against the whole bundle's `calleeMap`. | Unchanged: it is done by the emitter at the call site. |
| Inside a region the JIT sees several predicates at once and keeps values in registers across the calls between them. | Lost at every call. This is the one thing regions do that this model gives up; the emitter's inlining of small callees is what recovers it, and the corpus measures it. |

Two rules follow for the bundle:

- **Direct calls only to what cannot change.** A call inside the bundle is a
  direct `call` when the callee cannot be replaced for the life of the
  loaded bundle, and a `calli` through its slot otherwise (a dynamic
  predicate's snapshot, a predicate a later consult may define). The linker
  knows which is which; .NET metadata then resolves the direct calls, and
  the persisted image needs no patch for them.
- **Loading compiles nothing, and neither does the engine's thread.** At
  load a predicate whose bytecode the bundle carries gets an offer of its
  compiled code and runs on the bytecode. When it has earned the code (When
  a bundle's code is taken, below) the compile worker loads the image, if no
  predicate needed it before, and compiles the predicate's methods
  (`PrepareMethod`); the engine switches at its next dispatch of the
  predicate. A predicate with no bytecode
  (`--strip-wam`) has its delegate bound at load, which the runtime compiles
  at its first call; its continuation methods wait for the worker in the
  same way. `PromoteOffers` (`compile_all/0`, `WarmAllCompilable`) takes
  every offer at once, for a host that pays at load.
- **A predicate with continuation methods has no method of its own.** In a
  bundle that keeps its bytecode the cold method is bound as the predicate's
  delegate. It is the whole predicate, and with the resume entries a
  delegate has (an alternative's restores the choice point on top first) it
  takes every cursor the dispatch loop enters a predicate at. The
  predicate's own method would be the same code a fifth time (What a
  bundle's methods cost to compile, below). With no bytecode
  (`--strip-wam`) the predicate keeps its own method: that one runs from the
  first call and compiles there, on the engine's thread, and the cold method
  is twice its IL (The cold method through the dispatch loop, below; while
  the cold method made tail calls the JIT also compiled it with full
  optimization at once, 1.28 s on the engine's thread for Blint's first
  pass against 0.40 s).

The bundle assembly is loaded once per process and never unloaded, as today;
the collectible generation (item 3) is for runtime promotion only.

### When a bundle's code is taken

Compiling a bundle's code is the expensive step (What a promotion costs).
Blint's bundle holds 1.1 MB of IL as regions and 4.4 MB as continuation
methods; in a traced run the worker took 7 s for the 495 methods of the
first 95 predicates it was asked for. A worker that compiles each predicate
as soon as it is warm keeps a core busy through the whole of a short run,
and the engine's thread pays for it:

- The runtime tiers up its own methods only after a delay that every method
  called for the first time re-arms. Each switch to compiled code calls
  engine methods the interpreter had not, and the interpreter's hot methods
  stay unoptimized about a second longer.
- The two compilers share the machine with the interpreter. On the
  four-core notebook these numbers come from, one more thread that works on
  memory slows a compute-bound thread by 16%, and two by 32%.

So a predicate takes its code when it has paid for it:

1. it has been called `PersistedThreshold` times (32, the threshold of
   promotion at run time), and
2. either the code taken so far, with its own, stays within
   `PersistedFreeBytes` (64,000), or its calls are at least
   `PersistedCallsPerByte` (4) times its own bytes.

The bytes are the IL of the methods the bundle carries for the predicate:
the JIT's time grows with them. The linker records
them per predicate (`IlPersistedEntry.Cost`). The free bytes let a small
program run compiled at once, callers and callees together: without them
`queens` takes 1.62 s with continuation methods and 1.39 s with regions.
Past them a predicate that is barely called stays on its bytecode.
`SHUMWAY_IL_BUNDLE_PROMOTE=threshold[,freeBytes[,callsPerByte]]`
sets the three: `32,0,0` takes every predicate at 32 calls, and a threshold
of `0` binds every delegate at load, each method compiling at its first
call on the thread that calls it.

Cold processes, seconds, minimum of eight (of five for the eight passes),
bundles that keep their bytecode:

| | interpreter | regions, at 32 calls | regions, earned | continuation methods, at 32 calls | continuation methods, earned |
|---|---:|---:|---:|---:|---:|
| Blint over its own source | 2.09 | 2.58 | 2.37 | 3.16 | 2.47 |
| the same, eight passes in one process | 8.79 | 7.43 | 6.91 | 10.27 | 7.06 |
| `queens`, 3000 times | 2.15 | 1.10 | 1.07 | 1.11 | 1.19 |
| `nreverse`, 30000 times | 2.24 | 0.87 | 0.90 | 0.88 | 0.88 |
| `tak`, 10 times | 0.89 | 0.69 | 0.69 | 0.65 | 0.65 |

A first calibration asked for 1000 calls and 40 per byte. It compiled about
25 of Blint's 182 region methods and stopped there (101 are called 32 times
or more), which is the tier turned off for most of a program: 7.19 s over
the eight passes with regions and 8.55 s with continuation methods.

Still open:

- A predicate that waits is counted at every dispatch, about 60 ns: a
  bundle whose predicates never earn their code runs up to 10% behind the
  same program with no compiled code at all.
- With `--strip-wam` there is no bytecode to run meanwhile: the delegates
  compile on the engine's thread at their first call (Blint: 3.09 s with
  continuation methods, 4.12 s with regions). A region method that hands
  over to another with a `tail.` call compiles with full optimization at its
  first call, whatever its size: the JIT gives no first tier to a method
  with an explicit tail call. Replacing that call with an ordinary one
  brings Blint to 3.41 s and was rejected: the transfers between methods
  stop being jumps, and the stack that 64 hops then hold depends on the
  frames (up to 15 KB each for an unoptimized region).
- The cold method and the alternatives method are each the whole predicate
  again, three fifths of the compile time between them (next section).

### What a bundle's methods cost to compile

Blint's bundle with every predicate's code taken at once (`PromoteOffers`),
on one thread:

| | methods | IL | JIT |
|---|---:|---:|---:|
| regions | 189 | 1.13 MB | 3.0 s |
| continuation methods, and a method of its own per predicate | 1,752 | 4.40 MB | 12.0 s |
| continuation methods, the cold method as the delegate | 1,455 | 4.06 MB | 9.6 s |

By kind, in a traced run (the trace slows the JIT): the alternatives
methods 3.4 s (200 methods, 1.05 MB), the cold methods 3.3 s (297, 1.07 MB),
the entries 1.9 s (297, 0.91 MB), the continuations 1.1 s (656, 0.94 MB). A
run that takes its code as it earns it pays for what it uses: over Blint's
eight passes the worker compiled 237 generated methods in 1,504 ms before
and 188 in 1,207 ms after, for 54 predicates; with regions, 56 methods in
714 ms.

The cold method is not only a slow path. Share of the engine thread's
samples inside cold methods once everything is compiled (one process, the
programs of What a promotion costs): `qsort` 23%, `flatten` 25%, `serialize`
5%, `boyer` 5%, `queens` 4%, `zebra` 2%, none in `sendmore`, `crypt`,
`nreverse` and `tak`; Blint 3%. A cut that discards a choice point leaves
the hot method for the cold one (the intrinsic covers the cut with nothing
to discard), and a self-recursive last call is a branch inside the method
it runs in: `partition/4` stays in its cold method for the rest of the
list. The delegates of code emitted at run time take another 6 to 9% of
the samples.

So the cold method has to run as fast as the hot ones for now. In a
delegate's form (no register held, each operation its helper's call) its IL
halves, 1.07 to 0.49 MB, and its JIT time drops by 13%: the JIT inlines the
helpers, and its time follows the code it produces, not the IL. That was
0.3 s of the bill above, and `qsort` ran 7% slower (0.285 to 0.307 s, three
copies, three runs). Rejected until the cut that discards stays in the hot
methods.

### What sent a hot method to its cold method

A census: a cold method counts its entries by instruction boundary, each
exit site of a hot method names itself when it is emitted, and the
interpreter's table counts what the dispatch loop enters. Entries into cold
methods over one run of the harness of What a promotion costs (two rounds
of each program, and of Blint):

| program | before | after |
|---|---:|---:|
| `flatten` | 48,924,048 | 15,840 |
| `boyer` | 25,080,156 | 18,180 |
| `qsort` | 21,300,048 | 26,268 |
| `zebra` | 17,764,320 | 276 |
| Blint | about 14,100,000 | 15,627 |
| `queens` | 11,251,200 | 624 |
| `serialize` | 2,070,084 | 5,712 |
| `nreverse` | 720 | 720 |
| `tak` | 120 | 120 |
| `crypt` | 60 | 60 |
| `sendmore` | 0 | 0 |

The cut that discards a choice point was the third cause, not the first.
In the order of what they sent:

1. **A binding that has to be trailed.** The inline unifications
   (`get_list`, a register against a constant) left for the cold method
   when the cell was older than HB. A hot method now pushes the trail entry
   itself (`EmitHotTrail`), after the site's last exit and before its first
   write, with one comparison against HB as before. A full trail is still
   the cold method's, which grows it. One path that was rare became common
   with it: a CP-free guard (ADR-031) that fails with a binding to undo
   leaves for the cold method from its fail point. In a clause with a frame
   that boundary sat before the frame's deallocation, which the cold method
   then ran a second time; it is after it now.
2. **A unification that fails on its first test.** `get_list` against an
   atom, or a constant against another constant, took the slow path only to
   be told no. A cell whose tag no list unifies with (a structure, an atom,
   an integer, a float, a big integer) fails at the site; so does an atom or
   a small integer against a cell that is an atom, an integer, a structure
   or a list. An attributed variable and a packed string are in neither set
   (ADR-047, ADR-049). The tests sit after the reference test, off the
   paths of a list and of a variable: on those paths they cost `nreverse` 8
   to 14%.
3. **A reference to a reference.** The inline paths followed one step; a
   hot method follows the chain.
4. **A call to a predicate with no continuation methods** (a dynamic
   predicate on its bytecode, a predicate still earning its code).
   `CpsCallTarget` answers `CpsThroughLoop` for it, with Pc at the callee's
   marker, and the hot method returns to the dispatch loop itself. It still
   leaves for the cold method when the call has work to do first (a safe
   point is due, a wake is pending).
5. **A cut that discards a choice point.** Next.

The dispatch loop was the other way out of the hot methods. A return from
a callee on its bytecode, and a failure the interpreter backtracks into a
choice point of compiled code, entered the predicate's delegate, which ran
the rest of the clause. The interpreter's table (`TableEntry`) now enters
the continuation method of a cursor a proceed enters and the alternatives
method for a cursor a choice point resumes (it restores the frame at its
entry, as when `Fail` enters it). In Blint's run the loop made 3.6 million
entries into delegates at continuation cursors and 2.5 million at other
cursors; it now makes 1.75 million, all into the predicates' own methods,
and none into a delegate.

#### A cut that only lowers B

`Cut` does four things: it fires the cleanup handlers at or above its
barrier (`setup_call_cleanup/3`), pops the side-stack entries above it and
runs their prune hooks (a foreign iterator's `Dispose`), lowers B, and
compacts the trails. A hot method's cut to a choice point below B is the
store to B alone when:

- the barrier is at or above `_cutQuickFloor`, a word the activation keeps:
  the key of the top side-stack entry, or one above the highest cleanup
  handler's level, whichever is higher. It is raised where a side-stack
  entry is pushed, and recomputed where one is popped and where a handler
  is registered or forgotten;
- the two trails together hold at most `_cutCompactAt` entries.

Otherwise the cut leaves for the cold method's, which is `Cut`. What the
store skips is the compaction: the entries a cut makes unnecessary stay on
the trails. That is sound (untrailing one resets a cell the backtrack
discards anyway) and bounded: after a `Cut` has compacted them, the trails
may grow by as much as they then hold, and by 1,024 entries at least,
before a hot method's cut leaves for it again (`Cut` sets `_cutCompactAt`
from what its compaction kept). 200,000 trailed bindings under 200,000 cuts
in one pass keep 125 entries; without the bound, 200,000. The room is
1,024 entries by measurement (one process, the room a setting): from 256 to
1,024 `flatten`, `qsort`, `boyer` and `serialize` ran the same; at 8,192
`flatten` and `qsort` ran 2 to 7% slower, the entries the cuts leave being
written through that much more of the cache; at 128, 1 to 3% slower, for
the compactions.

The test reads the floor and the trail tops and nothing else. A first form
read the three conditions from their sources (the side stack's top entry
with its bounds check, the handler list, the trail tops); the JIT inlined
it, and `tak`, whose cut never discards anything, ran 4 to 6% slower (eight
rounds, two copies, repeated): the extra code changed the register
allocation of the whole method (its frame grew from 72 to 88 bytes, and
the clause that allocates ten Y slots spilled). With the floor, 0.99 to
1.00.

A cut that discards a `setup_call_cleanup/3` scope runs the cleanup before
the goal after the cut. Bytecode did; compiled code, regions and
continuation methods alike, left it queued until the interpreter next ran a
goal, or until the query ended, where it ran on a copy and its bindings
were lost. `NeckCut` and `CutToLevel` now run what their cut queued
(`Tier1CleanupFlusher`). The wasm tier's cut lowered B in its own memory
and knew nothing of the handlers; it steps aside to the interpreter's cut
when its barrier reaches one (`WasmAbi.CleanupReach`, the handlers' part of
the floor).

Against the code before these changes (one process, ABBA over renamed
copies, two copies of each build, minimum of eight rounds):

| program | ratio |
|---|---:|
| `qsort` | 0.79 |
| `crypt` | 0.81 |
| `flatten` | 0.82 |
| `queens` | 0.93 |
| Blint | 0.93 |
| `zebra` | 0.94 |
| `boyer` | 0.96 |
| `serialize` | 0.97 |
| `nreverse` | 1.01 |
| `tak` | 1.02 |
| `sendmore` | 1.03 to 1.07 |

`sendmore` never left its hot methods and gains nothing. Its two large
methods (19 and 23 KB of machine code) grew by 91 bytes of IL, at the list
of its head, and the code of its loops is the same IL as before: the
difference is the JIT's layout and register allocation, the 5% a copy of
the same code moves by (What a promotion costs).

The cold method is now cold, and stopped being compiled as if it were not
(next).

### The cold method through the dispatch loop

With its entries down to a few thousand, the cold method no longer has to
run as fast as the hot ones, and it stopped costing what they cost:

- It makes no tail call. A call, a proceed and a failure return to the
  dispatch loop, as a delegate's do, and the loop enters the callee's entry
  method, the continuation method or the alternatives method (the table
  above). A hot method still reaches it by a tail call, so the transfer
  into it stacks nothing, and its return is to the loop.
- Its self-recursive last call is a call like any other. The recursion is
  not a loop in the method, and a cold stretch ends at it instead of
  running the rest of the list.
- It is not marked to be optimized at once. `PrepareMethod`, on the compile
  worker, compiles the JIT's first tier; the runtime's own background
  thread compiles the optimized one for a cold method that is called
  thirty times. The first tier replaces a method on the stack when a loop
  of it runs long, and that compile is on the thread running the method:
  with the recursion out of it, the JIT's trace of Blint's eight passes
  shows none for a cold method.

The emitter adds `tail.` to a call that the method's return follows; a cold
method takes none (`IlEmit.NoTailCalls`).

Blint's bundle with every predicate's code taken at once, traced, by kind:

| | methods | IL | before | after |
|---|---:|---:|---:|---:|
| alternatives | 200 | 1.11 MB | 3,148 ms | 3,238 ms |
| cold | 297 | 1.08 to 1.02 MB | 2,906 ms | 666 ms |
| entries | 297 | 0.98 MB | 1,935 ms | 1,896 ms |
| continuations | 656 | 1.03 MB | 1,109 ms | 1,213 ms |
| all | 1,457 | 4.20 to 4.15 MB | 9,102 ms | 7,015 ms |

A run that takes its code as it earns it, Blint's eight passes from a
bundle with its bytecode: the worker compiled 184 generated methods in
1,229 ms before (the cold methods 397 ms) and 187 in 805 ms after (94 ms:
51 cold methods at the first tier and one at the optimized one). With
regions it compiled 56 methods in 714 ms.

One pass of Blint from that bundle (16 processes, the best five): 2.30 to
2.40 s against 2.33 to 2.49 s, and 3.05 to 3.28 s of CPU against 3.25 to
3.55 s. Once compiled, the cold method through the loop against the cold
method by tail calls (one build, one process, ABBA, two copies, minimum of
eight rounds):

| program | ratio |
|---|---:|
| Blint | 0.97 to 1.01 |
| `boyer` | 0.98 |
| `crypt` | 0.98 |
| `flatten` | 0.95 to 0.99 |
| `nreverse` | 1.00 |
| `qsort` | 0.98 to 1.03 |
| `queens` | 0.99 |
| `sendmore` | 0.98 |
| `serialize` | 1.00 |
| `tak` | 0.98 |
| `zebra` | 0.99 to 1.03 |

Tried and rejected:

- **The cold method in the delegate's form** (no register held in a local,
  each operation its helper's call). Its IL halves again, 1.02 to 0.45 MB,
  and its first-tier compile goes from 666 to 471 ms; one pass of Blint ran
  0.15 s slower (2.49 to 2.53 s against 2.30 to 2.40 s). The first tier
  inlines nothing, so every operation is a call.
- **The cold method as the delegate of a bundle with no bytecode**
  (`--strip-wam`), in place of the predicate's own method. That delegate
  compiles at its first call, on the engine's thread, and the cold method
  is twice the IL of the method it would replace: one pass of Blint went
  from 2.87 to 3.23 s (minimum of eight processes). Such a bundle keeps a
  method of its own per predicate.

## Results

All figures are from one process, variants interleaved in rotation, minimum
of 10 to 20 rounds. The spike programs are hand-written against a minimal
machine (a heap and stacks of 64-bit cells, a register bank in memory,
environment frames, choice points, the trail, the HB check, a safe point per
call) with the clause bodies as C# helpers the JIT inlines, so that only the
transfer differs between variants. The noise floor of these programs is
about 10%: a change of code placement alone moved one variant from 6.8 to
7.75 ns.

Transfer shapes, `xr2`, six transfers per iteration:

| shape | ns per iteration |
|---|---:|
| everything inline in one C# method (the floor) | 8.4 |
| one method, cursor `switch` (today's region, in C#) | 18.8 to 20.6 |
| `DynamicMethod`s, closed delegates for calls and returns | 16.9 |
| `DynamicMethod`s, direct calls, closed-delegate returns | 14.8 |
| collectible assembly, `calli` through slots, `calli` returns | 14.3 |
| collectible assembly, direct calls, `calli` returns | 12.9 |

Backtracking and non-tail recursion, collectible assembly against the region
shape in C#, both compiled once with full optimization and no PGO:

| program | region, `switch` | continuation methods |
|---|---:|---:|
| `member/2` over 100 elements, then `!` (per backtrack) | 17.8 | 18.3 to 18.8 |
| `nrev/2` of 30 elements (per logical inference) | 7.75 | 7.13 to 7.18 |

Other measurements:

- `TypeBuilder.CreateType` for a batch of eight methods: 0.07 to 0.5 ms.
- A slot call costs about 0.35 ns over a direct call.
- Passing `H`, `E` and `CP` as arguments besides the activation: 13.0 against
  13.3 ns frameless, 15.2 against 14.1 ns with frames. On x64 Windows they are
  three of the seven volatile registers, which the JIT already uses as
  temporaries.
- Writing the choice point with one bounds check took the method that pushes
  it from eight callee-saved registers to none (19.7 to 17.9 ns per
  backtrack). Reloading after the untrail loop took the method that restores
  it from five to one.
- Undoing the trail by a tail call to a trampoline lost 2 to 3 ns per
  backtrack: in `member/2` every backtrack has a binding to undo.

### Stage 1 in the engine

The engine's own emitter, `SHUMWAY_IL_CPS=1`, against regions with the region
GC safe point on (without it regions never collect inside a region, and the
comparison is unfair). One process, ABBA over renamed copies of each program;
ratio continuation methods / regions, minimum and median:

| program | min | median |
|---|---:|---:|
| `xr2` | 0.70 | 0.68 |
| `nrev/2` | 0.76 | 0.75 |
| `tak/4` | 0.57 | 0.60 |

Of that, the cold method (item 6, third way) gave 11 to 13% on `xr2`, 9% on
`nrev/2` and 6 to 7% on `tak/4`, against hot methods whose slow paths call
their helpers and return.

The JIT listings (`DOTNET_JitDisasm`) show what remains. The small methods
(the continuations of `xr2`'s helpers) have no pushes. The entry method of a
large predicate (`loop/1` in `xr2`, about 2 KB of native code) keeps seven or
eight pushes with no returning call left on any path: they come from peak
register pressure, not from calls. The JIT sees no loop in it (no `align`),
and the pushes cover values live at the busiest point: the array references
it keeps after common-subexpression elimination, the held registers
(ADR-060), 64-bit constants and temporaries. The epilogue runs before every
tail jump, so a large method pays its pops at each transfer out; item 13
avoids the transfer where the target is known.

### The size of the emitted IL

The JIT compiles a method with minimal optimization (MinOpts) past about
60,000 bytes of IL, 20,000 instructions, 2,000 blocks or 2,000 locals,
decided before inlining, and its inline budget grows with the method's IL. A
large predicate has to stay small in IL, not only in machine code.
`sendmore/1` (one clause: eight `digit/1` generators, the `\==` tests between
them, one arithmetic comparison) was the measure. Its continuation methods
took 8,910 IL instructions each at first, and 3,226 after a fact enumerator
(one push site and one bind for facts of one constant argument), the
comparison of two Y slots without the builtin, and plain stores to the
registers below `Activation.MinRegisterCount`. Without the inference count
(Stages, item 3) they took 3,122; then:

| change | instructions per method |
|---|---:|
| a Y slot read and written at a constant offset from the held `E` (`EmitLoadY`, `EmitStoreY`) | -432 |
| one check per arithmetic operand instead of one per operation (`ArithBounds`) | -140 |
| a branch to the method's own cursor instead of the cursor switch | -1 (325 bytes) |
| `allocate`'s `RawInt` cells built at compile time | -66 |
| the choice point push's slow path shared per arity | -134 |

The methods ended at 2,349 instructions, the delegate at 1,665 (4,679 at
first). What the round showed:

- An IL reduction that leaves the machine code as it was does not change the
  time. The constant Y offsets, the precomputed cells and the direct branch
  were within 3% on ten programs: the JIT had already folded each. Their
  worth is the budget above.
- Removing a branch from the hot path does change it. With a check per
  operand, `sendmore` ran 4% faster and `crypt` 4 to 7%, the others
  unchanged. The bounds are the sequence's interval arithmetic for the
  widest operand width, 32 to 60 bits, that keeps every value within 64
  bits; a comparison is then exact past 60 bits, and `is/2` checks its result
  once. The wasm tier takes the same bounds (`Shumway.Compiler.Wam.ArithBounds`).
  There an int past 60 bits may meet a float, and it converts to the nearest
  double as the interpreter converts a bignum (`DoubleConversion`;
  `BigInteger`'s own conversion truncates).
- A block shared between sites costs a jump at each site. Hot paths stay at
  the site, and only what is cold is shared (see the push checks under
  Tried in the engine and rejected).
- A method per cursor of the switch made one for every cursor, entered or
  not: an inlined call (a fact, a leaf, a rule) keeps a resume cursor only to
  fill the switch, and an index node is entered by a branch. Now only the
  entry and the continuations get a method: the cursor of a call, of an
  untailed call, of a meta-call and of a backtrackable builtin's return
  (`CpsRecordContinuation`). A cursor entered all the same resumes through
  the delegate (`CpsProceedTarget` returns 0). Over the ten programs, with
  the library predicates they promote, the IL compiled fell by 12 to 67% and
  the machine code by 6 to 68% (`sendmore` from 39 methods and 53,486
  instructions to 17 and 17,992); the methods that run kept the same machine
  code.
- A continuation method carried the whole body of its predicate. The JIT
  imports only what the method's entry reaches, so the machine code was that
  part, but the IL limits count all of it: in Blint (a 2,570-line program,
  its self-lint) 91% of the continuation methods' IL could not be reached,
  and in a clause with many calls each continuation repeated the clause. A
  continuation method is now emitted from its resume point
  (`CpsResumePoint`) when nothing before it can be reached: the clause ends
  in the same emission with no self tail call after the point (the branch to
  the predicate's entry would reach all of it again), and the point is not
  inside a construct that keeps state in IL locals. The cold pass records,
  at each call, the pc after it, its site index and the boundary number that
  follows; the hot pass skips the dispatch and the other clauses, consumes
  the site counter up to the index, sets the boundary number and marks the
  resume label, so that from there the method numbers its sites and
  boundaries as the cold method does. A label only the skipped code would
  have marked is marked at a throw: none is reachable. In Blint the
  continuation methods present before and after went from 468,685 to 118,843
  instructions, the reachable instructions the same 41,106 (`blint_goal/4`,
  18 clauses: 11,749 to 306 per continuation). The process's CPU time did
  not move beyond its noise (about 40 s either way): Sigil's emission of the
  dead code was not where the time went. `SHUMWAY_IL_CPS_PRUNE=0` emits the
  whole body.

These figures come from one process with each build loaded twice (two
`AssemblyLoadContext`s, each with its own JIT'd code), one engine per copy
warmed once, runs of 0.2 to 0.8 s interleaved ABBA, minimum over the pooled
runs. The layout of a copy's code alone moves a program by about 5%: a build
against a copy of itself read 0.94 with one copy each and 0.996 to 1.02 with
two. `crypt` moves by about 13% with layout and does not decide a change.
The set of methods a build compiles moves the layout of all of them: dropping
the methods no cursor entered left the machine code of the others as it was
and still read 4% slower on `nrev/2` and 3 to 6% on `tak/4`.
Runs of 10 to 70 ms, as the first measurements of this round took, gave
apparent losses of 50 to 80%.

### What a promotion costs

With the emitter linear (ADR-062), most of a promotion's cost is the JIT's.
Blint linting itself three times in the REPL, threshold promotion, JIT time
of the generated methods:

| Tier-1 form | methods | IL | JIT |
|---|---:|---:|---:|
| regions | 115 | 2.9 MB | 5.6 s |
| standalone (`SHUMWAY_REGION=0`) | 154 | 0.30 MB | 1.1 s |
| continuation methods: delegates | 138 | | 1.15 s |
| continuation methods: continuations | 474 | 1.01 MB | 2.07 s |
| continuation methods: alternatives | 101 | 0.55 MB | 2.01 s |
| continuation methods: cold | 97 | 0.41 MB | 1.63 s |

A runtime region holds its root's closure, and the closures of the roots
overlap: 202 predicates appear 962 times. Twenty-nine regions passed the
JIT's size limits and ran without optimization. Continuation methods
repeat each predicate instead: its delegate, its cold method and its
alternatives method are each about the whole body.

Against regions the continuation methods are as fast or faster once
compiled (one process, ABBA, two copies per variant, runs of about 1 s;
continuation methods / regions, minimum and median): `sendmore`
0.82/0.81, `crypt` 0.73/0.97, `zebra` 0.93/0.90, `boyer` 0.96/1.06,
`flatten` 0.75/0.67, `nreverse` 0.84/1.05, `qsort` 0.78/0.82, `queens`
0.88/0.86, `serialize` 0.78/0.91, `tak` 0.80/0.82. Blint's fifth pass:
0.87 to 1.08 s against 1.03 to 1.07 s.

- The methods of a collectible `AssemblyBuilder` are not tiered: without
  `AggressiveOptimization` they still compile with full optimization. The
  one choice per method is `NoOptimization`.
- Every method of a predicate compiles on the worker before the engine
  switches to it, the alternatives method and the cold method too, so that
  nothing compiles on the engine's thread. Compiling those two at their
  first use was the earlier shape (in Blint 46 of the 101 alternatives
  methods are ever entered): it saved their JIT and put it on the engine's
  thread. An alternatives method entered through its stub instead of its
  native code, one jump more per entry, measured nothing (`crypt`:
  0.99/1.01).
- The JIT inlines engine methods with their dynamic PGO profile, and a
  method compiled later finds more of it: `crypt`'s meta-call alternatives
  method had 16 inlinees with profile data compiled at first use, 9 at
  install. The profile comes from the engine's own use of those methods,
  mostly the interpreter's, and here it made the code worse: `crypt` read
  1.17 to 1.25 with the alternatives compiled at first use, and 1.000 with
  `DOTNET_TieredPGO=0`. When a method compiles moves its machine code;
  this is part of `crypt`'s sensitivity.
- What compiled code inlines is a property of the engine, not of the
  profile a run leaves. The 56 branching engine methods compiled code
  inlines (the JIT's inlining events over Blint and the ten programs) are
  marked `HelperImpl.Fixed` (`AggressiveOptimization`): never
  instrumented, so the same code wherever and whenever they are inlined
  (`crypt` with the alternatives at first use: 1.01/0.97). Without the
  profile they cost 0 to 5%, up to 14% (`serialize` with regions, `zebra`
  with continuation methods, `sendmore` on Tier-0) until each is written
  with the layout the profile gave it: the common path first, the rare one
  in a call. In progress.

### Tried in the engine and rejected

Each was built and measured against the stage 1 shape above. They are
recorded so that they are not retried without a new reason.

- **The alternatives method as the predicate's entry.** A predicate with
  alternatives got no entry method: its alternatives method, which carries
  every clause already, was entered at cursor 0 too and skipped its restore
  there. Blint's bundle compiled in 5,800 ms for 7,917 (1,257 methods for
  1,458, 3.27 MB of IL for 4.15). Once compiled, every call paid for it:
  the entry ran the prologue of a method with calls, and an unfolded
  cursor. `crypt` 20% slower, `nreverse` 13%, `qsort` 11%, `flatten` 9%,
  `queens` 7%, `tak` 6%; `sendmore` 11% faster; Blint and `zebra` the same
  (one build, one process, ABBA, two copies). A predicate's code is taken
  when it is hot, and the entry's speed is what it is taken for.
- **Registers read from their fields instead of held in locals.** The
  intrinsics reloaded each register from its field where they needed it, and
  nothing was held across instructions, to lower register pressure. The
  reloads cost more than the pushes they saved: `xr2` 26 to 44% slower. The
  held registers of ADR-060 stay.
- **Holding fewer registers (a smaller mask).** Three or four pushes fewer
  in the large method, but the intrinsics that need an unheld register fall
  back to their helpers, and those calls return: the pushes come back and the
  calls cost more.
- **Self-recursion as a transfer to the entry method.** Proposed to give the
  JIT a method without a back-edge. It made the method no smaller, and each
  iteration paid two method entries: `xr2` 8% slower, `nrev/2` within noise.
  Item 13 is the rule that came out of it.
- **Building tagged 64-bit constants by a rotate**, so that no `mov` of a
  64-bit immediate keeps a register busy. No change in pushes.
- **Writing a frame through a byref from `ldelema`**, one bounds check per
  frame instead of one per cell, as the spike did in C#. The method was
  rejected as an invalid program. A C# helper that writes the frame was
  inlined by the JIT with no change in pushes, and `tak/4` was 4 to 11%
  slower.
- **`volatile.` loads of the machine's arrays**, to stop the JIT from keeping
  them live across the method. One push fewer, and only with registers read
  from their fields, which were rejected.
- **A region of one predicate as the continuation method** (stage 2), so
  that its choice points are pushed and restored inline (ADR-058) and its
  own alternatives resumed by a branch. Against the standalone emission,
  both in continuation methods: `queens` 13% faster, `member/2` equal,
  `nrev/2` 6% slower; restricted to predicates that push choice points,
  `xr2` was 1.9 times slower than with regions, since its `loop/1` has two
  clauses. A region keeps its cursor and frame state in IL locals, so the
  cold method cannot resume inside it, and its slow paths call. What it
  showed is what inline choice points can give: about 13% on `queens`.
- **A method per alternative cursor** (stage 2): each a full emission of
  the predicate, so a predicate with many alternatives took seconds to
  compile. One alternatives method per predicate replaced them. The time
  was mostly Sigil's (see Risks), which ADR-062 removed; what remains is
  the JIT's cost per method, to measure before this is revisited.
- **The frame restore inline in the failing method** (stage 2): it put
  calls into every method that can fail, and the pushes with them (`xr2`
  from 0.86 to 0.93). The restore moved to the alternatives method's entry.
- **Local resumption in every method** (stage 2): the switch over the
  cursor made each method carry every clause; `member/2` from 0.75 to
  1.02. Only the alternatives method resumes locally.
- **The choice point push's checks shared per arity**, with its slow path:
  each push branched to its arity's block for the frame and register checks.
  One jump more per push: `sendmore` 9% slower. Sharing only the slow path
  (the call and the reload after it) was 11% faster than sharing both.
- **A call to a helper that always throws, in place of each slow-path
  call**, as a probe: it showed which calls kept values live, and confirmed
  that with no returning call the remaining pushes are pressure. It is not a
  design: the cold method is.

## Alternatives considered

- **Keep regions and make the transfer between them a tail call.** Built as
  a prototype: 7% on `xr2`. The cursor `switch` and the large method's
  prologue remain.
- **One region for the whole program.** Not possible: the JIT's limits on
  method size, inlining and tracked locals, and recompilation of the whole
  for any change.
- **Direct .NET calls to deterministic predicates**, continuations on the
  .NET stack. The fastest measured (12.3 to 12.5 ns on `xr2`), within noise
  of continuation methods, but the .NET stack grows with the Prolog
  recursion: it needs a depth bound and a second mechanism beside it.
- **Continuation methods as `DynamicMethod`s.** No `ldftn` or function
  pointer to a `DynamicMethod` exists in .NET, so returns go through
  delegates: 14.8 to 16.9 ns against 12.9.
- **A non-collectible assembly.** Simpler, and evicted code stays loaded.
  Right for a program that never changes; a process that reconsults keeps
  every version. The generation costs little more.
- **Undo the trail out of line.** Measured slower (Results).

## Why this is sound

Control between compiled methods follows the WAM's own transfers: a call sets
`Cp` and enters the callee, a proceed enters `Cp`, a failure enters the top
choice point's alternative. The machine state is the activation's: the
registers, the heap, the stacks and the choice points are where Tier-0 and
the builtins read them, at every transfer. A method's locals do not outlive
it, so activations stay thread-agile, and compiled code stays independent of
the engine: it receives the activation.

The tables are shared by all activations like today's code cache. A slot
write is a single pointer store, so promotion stays atomic, and after
eviction no new call reaches the evicted code. The .NET stack is bounded by
construction: every transfer replaces the frame, and item 11 bounds it where
the runtime may not.

## Risks

- **Engine-level cost is not yet measured.** The spike isolates the transfer
  mechanism. The engine's methods do more per transfer (wake checks, `B0`,
  the debugger's arming); stage 1 measures them in place.
- **Native code pointers go stale on re-JIT.** With `AggressiveOptimization`
  the runtime does not tier the methods again; a profiler's ReJIT would. The
  precode entry is the fallback when a profiler is attached.
- **Code size and promotion latency.** One method per continuation means more
  methods than predicates, and a type per batch. Measured at under a
  millisecond per batch; the promotion is asynchronous.
- **Everything that depends on regions changes**: the region planner, the
  cursor numbering, local backtracking's frame restore, persisted bundles'
  layout, the dead-region prune and its member-entry aliases, the
  disassembler's view, the region invariants in `invariants.md`.
- **What the JIT optimizes across predicates inside a region is lost.** A
  program whose hot loop spans several small predicates may lose more there
  than it gains at the transfers. Stage 4 measures it on the corpus, where
  regions were tuned. The stages keep the region path working until the last
  one removes it.
- **Frame discipline is easy to lose.** One non-tail call on a fast path, or
  one value kept live across a loop, brings the prologue back in every entry
  of the method. The emitter's rules (item 6) need a check: the JIT listing of
  a sample of methods in the gate, counting pushes.
- **.NET Framework.** The hop bound keeps it correct; its speed is secondary
  (ADR-043).
- **Emission cost on large methods.** The cold method carries a label per
  instruction, and Sigil's emission and its recursive return tracer grew
  badly with labels: a fact table of thousands of clauses kept the shared
  compile worker busy for minutes and overflowed the tracer's stack. Stage 1
  caps continuation methods at 4 KB of bytecode (`SHUMWAY_IL_CPS_MAX`).
  ADR-062 replaced Sigil with an emitter linear in the instructions; the cap
  stays until the JIT's cost on such methods is measured.

## Stages

1. Deterministic predicates without choice points (`xr2`, `nrev/2`, `tak/4`)
   as continuation methods behind a switch, beside regions. A/B in one
   process over renamed copies of each program (the compiled code is cached
   per process: one copy would give both variants the first one's methods).
   Done, with the cold method and the integer fast paths in hot methods.
   Next in this stage: calls to known small predicates continued in the
   caller (item 13); labels in the cold method only at the boundaries a hot
   method leaves from, which replaces the 4 KB cap; a continuation method per
   slow site for the slow paths measurement shows are frequent (item 6,
   second way); continuation methods for the instrumented PGO variant.
2. Choice points, cut, soft cut, local backtracking between clauses; `Fail`
   by tail call. `queens/2`, `member/2`, the cut and guard suites.
   Done, in four steps measured against regions (one process, ABBA,
   minimum):

   | step | `member/2` | `queens` | `xr2` |
   |---|---:|---:|---:|
   | stage 1 (a failure returns to the dispatch loop) | 1.54 | 1.22 | 0.70 |
   | a method per cursor; `Fail` restores out of line | 1.15 | 1.15 | 0.86 |
   | the frame pushed inline, the restore inline in the failing method | 1.25 | 1.01 | 0.93 |
   | the restore inline in the alternative's entry, `Fail` a stub | 0.75 | 1.01 | 0.75 |

   The third step put calls (the restore's trail unwind and slow paths)
   into every method that can fail, and with them the callee-saved pushes
   (item 6): `q2/1` of `xr2` went from none to five. The fourth keeps the
   failing method free of calls and puts the restore where calls already
   are. `fail/0` is a branch to the fail label, not a builtin dispatch
   (regions still dispatch it).

   Two more changes came from the Van Roy set. A method per alternative
   cursor made a predicate with many alternatives (`sendmore`, eight
   inlined `digit/1` fact tables) take seconds to compile, during which it
   ran on Tier-0. The alternatives now share one method per predicate,
   whose cursor is its argument: it restores the frame at its entry and,
   like a region (ADR-057), resumes its own choice points in place, so a
   predicate's backtracking stays in it after the first failure. The entry
   and the continuations keep their cursor folded and do not resume
   locally (doing so made every method carry every clause: `member/2` went
   from 0.75 to 1.02). And the delegate is installed as soon as it is
   compiled; the continuation methods follow from a second job of the
   compile worker, discarded if the predicate changed meanwhile.

   Against regions, one process, ABBA, minimum and median:

   | program | min | median |
   |---|---:|---:|
   | `boyer` | 0.94 | 0.89 |
   | `crypt` | 0.97 | 0.96 |
   | `flatten` | 0.59 | 0.44 |
   | `nreverse` | 0.71 | 1.00 |
   | `qsort` | 0.78 | 0.59 |
   | `queens` (Van Roy) | 0.86 | 1.01 |
   | `sendmore` | 1.94 | 1.90 |
   | `serialize` | 0.80 | 0.53 |
   | `tak` (Van Roy) | 0.85 | 0.85 |
   | `zebra` | 1.02 | 1.11 |
   | `member/2` | 0.91 | 0.88 |
   | `queens/2` (all solutions) | 1.04 | 1.03 |
   | `nrev/2` | 0.75 | 0.71 |
   | `xr2` | 0.76 | 0.71 |
   | `tak/4` | 0.58 | 0.65 |

   Then the choice points became WAM ones (step 1 below), and `sendmore`
   showed the real cost: its continuation methods were about 60 000 bytes
   of IL each, which the JIT compiles without optimization (MinOpts). Its
   eight calls of `digit/1`, ten one-argument facts, are inlined at each
   site, each fact twice (the bound-argument path and the chain), and the
   inline unification of a register with a constant turned each `X = 3`
   from two instructions into about ninety. Three changes: the
   bound-argument path of a one-argument constant fact branches straight to
   the continuation (the key is the head) and otherwise enters the chain's
   body past its push, so each fact's body is emitted once; where no cold
   exit is possible (inside an inlined fact) the register-constant
   unification is one shared block per method, entered by a branch and left
   by a switch; and a predicate whose cold method exceeds 40 000 bytes of
   IL gets no continuation methods (`CpsMaxIlBytes`). `one(M) :- digit(M),
   M > 0` went from 2 287 instructions per continuation method to 848 (the
   delegate from 312 to 253); `sendmore`'s cold method from 62 011 bytes to
   27 951, and `sendmore` from 1.78 times regions to 0.91 (`crypt` 0.88,
   `zebra` 0.97).

   Before those changes, `sendmore` was the one loss. Its backtracking runs in the alternatives
   method, 1.85 times the region method's time for the same work. The
   difference is the choice point: a region's is a WAM choice point whose
   BP is a resume marker (ADR-058), a continuation method's is an IL choice
   point, which adds a side-stack entry to every push and an entry check
   and pop to every resumption. Next: continuation methods on WAM choice
   points as regions have them, with the resume marker naming the
   generation's code (the reason the side stack carries `CpsAlt`); the
   dispatch loop's backtrack into a choice point entering the alternatives
   method instead of the delegate; the ITE and guard pushes inline.
3. Builtins, meta-call, `catch/3` and `throw/1`, wakes, the heap collector's
   safe points, cancellation, the debugger's arming.

   A Prolog exception is a .NET exception today: a builtin or a helper
   throws `PrologRuntimeException` (or `ShumwayPrologException`; `throw/1`
   too), which unwinds through every compiled frame and the dispatch loop's
   `Run` up to `RunCatching`, which finds the catch frame and restarts `Run`
   at the recovery. The unwind is what makes it correct: it passes every
   place that reacts to a failure on its own (a CP-free guard that jumps to
   the next clause, a region's or an alternatives method's local
   resumption, an inline if-then-else's else, `findall/3`, `\+/1`, any C#
   code that returns false when a unification does). An exception turned
   into a returned false would be taken for a failure by each of them and
   resume a choice point. Doing without .NET exceptions inside compiled code
   needs a third outcome (success, failure, exception) with an exit of its
   own, distinct from the fail exit, and a review of every observer of
   failure. Not planned; the cost of an exception (the ball materialized as
   a term, the unwind, its translation back) stays.

   Compiled code counts no inferences; only the interpreter does. `time/1`
   reports inferences and Lips with Tier-1 off, seconds and heap cells with
   it on; `statistics(inferences, N)` gives the interpreter's count, and with
   Tier-1 on a reading counts itself, so it is never 0. SICStus and GNU
   Prolog keep no inference count. A count in compiled code costs an
   increment per goal in every method (5% of `sendmore`'s IL) and an
   unwind-safe flush before every call that can raise; kept on the IL stack
   instead of the field it lost (`xr2` 32% slower, `sendmore` 8%).

   Wakes (ADR-049 points 9 and 10). A hot method checks the queue in front
   of each builtin but `=/2` and of each inlined call, and leaves for its
   cold method at the instruction's boundary when something is pending; the
   cold method arms the interrupt with a marker whose cursor is that
   boundary, and the interpreter's table enters the cold method at a cursor
   from `CpsBoundaryBase`. A delegate arms it at its own wake cursors, from
   2^18, so the code a resume through the dispatch loop runs, and a
   predicate with no continuation methods, wake too. A choice point a wake
   pushes for a chain's later clauses names one of those cursors;
   `CpsFailTarget` leaves it to the dispatch loop, since the alternatives
   method knows none of them.
   A CP-free guard that saves its argument registers is opaque up to its
   commit only: the code after it has boundaries. Against the code without
   them, the Van Roy set and Blint measured 0.99 to 1.00 (one process, ABBA,
   minimum over eight rounds).

   A predicate with an inlined native block (ADR-024) gets no continuation
   methods: the block calls into the embedding layer and the program's
   interop types, which a collectible assembly cannot reach. Its delegate,
   a `DynamicMethod`, can.
4. Persisted bundles on the same emitter (see Bundles and link-time
   optimization): direct calls across the whole bundle, slots for what can
   change, the lazy upgrade of slots at load, `--strip-wam` over every
   predicate with a method, the linker's `--map` and `shumway-disasm`.
   Measured against region bundles on the real-program corpus: throughput,
   bundle size, load time and time to first answer (`--exe`).
   Done in part: a bundle linked with `SHUMWAY_IL_CPS=1` carries each
   predicate's continuation methods beside its method, in the same
   assembly with its own copy of the tail-call stubs, and no regions (so no
   dead-region prune). The loader offers them under the runtime functor
   ids, and the compile worker binds and compiles a predicate's methods when
   it has earned them (When a bundle's code is taken); transfers go through
   the same tables as at run time, not yet direct calls. Their emission runs first
   into a scratch type, since a method left half emitted would fail the
   whole assembly.
5. Regions removed. The wasm tier mirrored with `return_call` and
   `return_call_indirect` over its function tables.

## Validation

- The Tier-1 equivalence suites against Tier-0, the cut, soft-cut and guard
  suites, persisted bundles, the .NET Framework lane, the full Embedding gate.
- `time/1` heap cells identical to Tier-0 at every stage.
- A deep non-tail recursion (one million frames) and a long failure-driven
  loop under the default 1 MB stack: the .NET stack must not grow.
- A test that evicts a predicate while a continuation into it is captured in
  a choice point, and then backtracks into it.
- A test that unloads a generation after reconsult and checks that its
  assembly is collected, and one that keeps a slot pointing into a
  generation and checks that it is not.
- Counter-proofs that must fail: a slot left pointing at evicted code, a
  generation released while a slot points into it, the hop bound removed on
  .NET Framework.

## References

- The spike programs are not in the repository. Stage 1 replaces their
  figures with the engine's.
- [ADR-057](057-local-backtracking-in-il-regions.md),
  [ADR-058](058-region-choice-points-are-wam-choice-points.md),
  [ADR-060](060-region-registers-in-locals.md),
  [ADR-050](050-wasm-tier1-backend.md),
  [ADR-023](023-dynamic-predicates-in-il.md),
  [ADR-043](043-net-framework-target.md).
