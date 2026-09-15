# Phase 0 of the many-modules arc: can wasm reach wasm?

Measured 2026-09-09 on Windows 10, .NET 10, Edge 152 headless (V8), WebShumway
published with `-p:ShumwayWasmTier=true` and served with the isolation headers.
Desktop figures are the emitter library's own wasm engine, which executes by
compiling to IL.

The arc wants many small modules instead of one big one, compiled
incrementally, with the transfer between them happening inside wasm rather than
by returning to the host. Everything rests on one claim: that a cross-module
hop is cheap **when it is not a trip through mono-interpreted C#**. These are
the gates that had to pass before any of it was worth building.

## The results

| gate | asks | result |
|---|---|---|
| G0 | 10⁷ hops per round in bounded stack | **PASS** — 5 rounds, 50 M hops, no growth |
| G1 | ≤ 100 ns per hop | **PASS** — 6.1 ns best, ~6.3 ns median |
| G3 | the test library executes the hop | **PASS** — so xUnit can exercise it |
| G4 | a module reaches slots added later | **PASS** |
| G2 | incremental compile ≤ 1.5× batch | **PASS** — 0.76×, i.e. cheaper |

```
ping: 139 bytes, registered at index 7874 on thread 7
pong: 139 bytes, registered at index 7875 on thread 7
two hops answer: 1 (memory says 1)
round 0: 10000000 hops in 62.5 ms = 6.3 ns/hop
round 1: 10000000 hops in 73.8 ms = 7.4 ns/hop
round 2: 10000000 hops in 62.4 ms = 6.2 ns/hop
round 3: 10000000 hops in 61.4 ms = 6.1 ns/hop
round 4: 10000000 hops in 70.6 ms = 7.1 ns/hop
```

## What each gate settled

**G0 — is it really a tail call.** This is the one that could have killed the
arc outright, and it is a property rather than a speed. A Prolog program makes
millions of calls, and in the WAM a call IS a jump: the continuation lives in
CP, not on the host stack. Had V8 compiled `return_call_indirect` as an
ordinary call, the stack would have grown per hop and there was no fallback
design. Fifty million hops without unwinding says it does not.

**G1 — 6.1 ns against 4,000–15,000 ns.** That range is what a cross-module
switch costs today: decode the marker, probe a dictionary, close the chain, let
the interpreter re-dispatch, open another chain — all in C# that the browser
runs interpreted. Same crossing, 650× to 2,400× cheaper, because it never
leaves wasm.

**G4 — the table is live, not a snapshot.** `ping` was registered at index 7874
and `pong` at 7875, so `pong` did not exist when `ping` was instantiated, and
`ping` still reaches it. A table import is by reference. Without that, every
new module would have meant re-instantiating the ones that call it.

**G3 — the arc is testable without a browser.** The emitter library executes
`return_call_indirect` through an imported table, so `tests/Shumway.Tests.Wasm`
can exercise the hop. Had it not, every cross-module path would have been
browser-only to test, which changes the cost of the whole arc.

## G2 — compiling one at a time is CHEAPER

818 predicates of the prelude and clpfd, compile time only (instantiation and
per-thread registration are the browser's):

```
one batch      : 763 ms, 4,095,932 bytes
one at a time  : 583 ms, 6,268,753 bytes total
ratio          : 0.76x time, 1.53x bytes
per predicate  : median 0.31 ms, max 35.74 ms
```

The gate asked for no worse than 1.5x. It is 0.76x — separate modules are
*faster* to compile, because a group pays work that is superlinear in its
member count: numbering global cursors, building the br_table, cutting
partitions. Incremental promotion does not pay a toll here, it collects one.

A median of 0.31 ms per predicate against a 30 ms budget means a promotion can
happen mid-session without being felt, which is what lets the tier stop needing
a batch mode at all.

The cost has moved to **bytes: 1.53x**. Every module repeats the dispatcher,
the fail/proceed resolver and the general unifier, so 818 of them carry 2.2 MB
more wasm than one group — and the browser compiles all of it. That is the same
currency the resume table just saved 26.8% of, so it does not sink the arc, but
it names the next fight: either modules share those functions through imports,
or predicates are grouped a few at a time rather than one each.

The 35.74 ms maximum against a 0.31 ms median says one predicate is enormous
(almost certainly in clpfd) and on its own justifies keeping partitions as a
safety valve rather than deleting them.

## The grain: 16 predicates a module, not one

The 1.53x above is entirely fixed furniture -- the dispatcher, the fail/proceed
resolver and the general unifier -- repeated per module. Measured on the same
818 predicates:

```
smallest module : 3,028 bytes (its predicate is 1 byte of WAM)
median module   : 5,727 bytes
one group       : 4,095,932 bytes

  1 per module  : 6,268,753 bytes  (1.53x)
  4 per module  : 4,576,292 bytes  (1.12x)
 16 per module  : 4,192,857 bytes  (1.02x)
 64 per module  : 4,110,601 bytes  (1.00x)
```

A one-byte predicate yields a 3 KB module, so the floor is ~3 KB and the median
one-predicate module is more than half furniture.

At 16 per module the overhead is 2%. That keeps what the arc actually wants --
adding a predicate recompiles sixteen, not eight hundred -- without asking the
browser to compile 2.2 MB more. "One module per predicate" was the intuitive
phrasing and is the worst of the measured options.

Untried, and possibly better than either: put the three shared functions in
their own module and import them, which would give per-predicate grain with no
repetition at all. Whether functions can be imported across these modules the
way the table is has not been established.

## Lazy against batch, in the browser, on whole programs

The two grains the browser tier can run, on the same four programs, against
Tier-0. **Batch** compiles everything consulted into one module at the
boundary tick (the prelude included, as a page does at boot); **lazy**
compiles one module per predicate the first time it is called. Both engines
start without the baked prelude, so the compile columns are comparable.
Edge headless (V8), Release publish, best of 5; `#wasmgrain=5`.

```
                    tier0      batch                 lazy
nrev 200 x5       489.7 ms    10.5 ms  46.7x        5.7 ms   85.7x
  modules / bytes            1 / 2,687,050         3 / 34,964
  compile+register           4,791 + 25 ms         28 + 2 ms
  per run                    hops 0                hops 9,975

tak 18,12,6       914.6 ms    11.9 ms  76.8x        6.8 ms  134.3x
  modules / bytes            1 / 2,687,050         1 / 10,665
  compile+register           4,398 + 12 ms         7 + 1 ms
  per run                    deopts 30             deopts 30

zebra x10       1,717.3 ms    45.5 ms  37.7x       43.9 ms   39.1x
  modules / bytes            1 / 2,724,639         7 / 101,165
  compile+register           4,555 + 12 ms         68 + 4 ms
  per run                    hops 0                hops 447,750

queens 12 clpfd 5,215.0 ms 4,154.5 ms   1.3x    4,600.5 ms    1.1x
  modules / bytes            1 / 4,433,024       101 / 1,058,652
  compile+register           8,137 + 13 ms         682 + 54 ms
  per run                    chains 524,030        chains 524,030
                             deopts 95,140         deopts 95,140
                             builtin exits 626,930 builtin exits 626,930
                             hops 0                hops 1,499,135
```

Three things settle here.

**The hop is free at program scale.** zebra crosses a module boundary 447,750
times per run in the lazy grain and runs in the same time as the single
module; queens crosses 1.5 million times and lands within the noise of its
batch twin. Switches stay at zero in every row: no cross-module call or
backtrack falls back to the host.

**Lazy costs nothing the batch does not.** The batch pays 4.4–8.1 s to compile the 539
prelude predicates a small program never calls, and the first run is not
faster for it. Lazy compiles 1–7 modules for the three classic programs, 101
for the clpfd one, and its first run lands 0.4–0.5 s after the consult: a
promotion costs ~7 ms of mono-interpreted compile plus ~0.5 ms of registration.
Whether the batch machinery stays is the phase 6 question. **Read the next
section before answering it: the cost this paragraph charges the batch is
the prelude, and the bake stopped charging it.**

**clpfd is not a hop problem.** queens 12 is 1.1–1.3x in both grains because
a run is 524,030 short chains that exit to a builtin 626,930 times and deopt
95,140 times; the chain hardly runs any WAM code before leaving. The
per-module bytes (1.06 MB lazy against 4.43 MB batch) and the hops are the
same story as above; what the tier needs on this program is the builtin exit
ranking, not more modules. The deopts are the same in both grains, so they
are the code, not the partition.

**The ranking, now that it is shown.** `wasm_compile(status)` reports it
beside the deopt sites. On the queens-8 clpfd program in the browser:

```
builtin exits (of 32,872, 24 distinct)      deopt sites (of 5,073)
  15,584 (47%)  integer/1                     3,884 (77%)  clpfd_run/1@+85 CallBuiltin
   5,540 (17%)  get_attr/3                      878 (17%)  $wake_call/1@+28 CallBuiltin
   4,075 (12%)  ==/2                            108  (2%)  $disj_5/6@+114 CallBuiltin
   2,045  (6%)  $dom_same/2
   1,830  (6%)  $dom_del/3
     987  (3%)  var/1
     968  (3%)  $dom_new/3
```

Half the exits are TYPE TESTS: `integer/1` alone is 47% and `var/1`
another 3%, each a one-instruction check on a tagged cell that the module
leaves the chain to ask the host about. They are the cheapest thing in the
list to open-code and the largest share of it, which is the answer the
ranking was added to give. The attribute pair (`get_attr/3`, `put_attr/3`)
and the domain helpers are the next band, and those are real work.

## The same question once the prelude is baked

The measurement above ran every engine from a bare `new PrologEngine()`, so
the batch compiled the prelude in the browser and that dominated it. The
relocatable bake changed the premise: a page boots from the stdlib bundle
with its wasm module installed, and nothing of the prelude is compiled at
all. Re-measured with every engine booted the way the page boots (Edge
headless, Release publish, two runs at 3 and 5 rounds).

```
                  run 1 (x3)                 run 2 (x5)
                  batch   eager   lazy       batch   eager   lazy
nrev 200 x5       50.7     9.6     8.3       10.5     8.9     8.3   ms
tak 18,12,6        7.5     7.3    40.8        6.5    11.6     6.8
zebra x10         32.1    22.6    50.1       45.1    23.4    22.3

predicates compiled at the consult: 5, 5, 7 -- the user's, in every mode
compile cost: batch 69-1,334 ms, eager 40-86 ms, lazy 13-97 ms
modules: batch 2 (the baked stdlib + one), eager and lazy 2-8
switches: 0 everywhere; deopts identical across the three
```

**What the batch compiles is now the program, not the prelude.** Its compile
column fell from 4.4-8.1 s to tens or hundreds of milliseconds, because the
535 stdlib predicates arrive baked and every mode starts with them installed
(`promoted` is ~540 in all three). The batch and the lazy grain now differ by
a handful of predicates, so the old argument against the batch -- that it
pays seconds for code the program never calls -- is void.

**The run times do not separate the grains.** Every per-program ordering
flips between the two runs (nrev's batch 50.7 then 10.5; tak's lazy 40.8 then
6.8), which is the signature of wall-clock noise in a browser rather than of
an effect. What repeats is zebra, where the batch is slower than either fine
grain in both runs (32.1/45.1 against 22.3-23.4) despite its 447,750 hops a
run being the ones it does NOT pay: worth its own look, not a conclusion
here.

**So the batch stays**, and for a different reason than it was kept before.
Not because it is faster, but because after the bake it costs almost nothing
and is the only grain that leaves one module and no hops. The machinery it
needs is a flag and a tick.

## The desktop world is not a stopwatch

clpr is the program that made this explicit, and it is worth recording
because the desktop number was not merely noisy, it was the wrong shape.

The two worlds stage the engine's memory differently.
`DesktopWasmWorld.StageFromEngine` copies the live heap, stack, registers
and trail into linear memory on every chain entry; the browser pins the
engine's own arrays and copies nothing. So a crossing costs O(live data)
on the desktop and O(1) in the browser, and clpr's heap grows as it runs.

Desktop, the same program at three sizes, with the crossings doubling each
time:

```
N     crossings   wasm      us per crossing
100      8,504    300 ms         35
200     17,005    892 ms         52
400     34,006  3,101 ms         91
```

The per-crossing cost doubles with the problem: the tier reads as 5x
slower than Tier-0 and getting worse. In the browser, the same program and
the same counts:

```
                 tier0     batch          eager          lazy
clpr x200      1,166 ms   924 (1.3x)    533 (2.2x)    551 (2.1x)
clpr x400      1,552 ms  1,158 (1.3x) 1,365 (1.1x)  1,174 (1.3x)
```

The tier WINS, 1.1 to 2.2x. Same code, same counters (43,206 chains and
72,594 builtin exits at x200 either way) -- the desktop figure was
measuring the harness.

**What the browser says is left.** The time split the probe now reports
puts most of it at the boundary rather than inside the module:

```
clpr x200 lazy:   inWasm 185 ms    stage 497 ms      (3 rounds)
clpr x400 lazy:   inWasm 442 ms    stage 1,128 ms
```

Staging is ~70% of the accounted time even where it pins instead of
copying, and it is paid per crossing: 216 chain entries per iteration of a
program whose body is four constraints. That is what makes open-coding a
builtin worth doing -- not the work of the builtin, which is trivial, but
the crossing it avoids. `get_attr/3` tops both libraries' exit rankings
(41-43%) and is the next one.

Reproducing needs one caveat: the `#wasmgrain` hook closes its window the
moment the report is posted, so read `/collect` (or suppress `window.close`
from a debugger session) rather than polling the DOM: a poll that never sees
the report looks exactly like a hang.

## wasm_compile(all): one module, or one per predicate

The batch and the lazy grain differ in two things at once: what gets
compiled (everything, or what runs) and how it is cut (one module, or one
per predicate). This isolates the cut. `eager` compiles the same 827
predicates as the batch, one module each, at the same boundary tick; the
`b*` rows first compile the prelude as ONE module (what a page boots from)
and then cut only the 293 predicates of the clpfd program. queens 12, Edge
headless, Release publish, best of 5, two runs of each cell back to back.

```
                          run (ms)       compile     register   modules / bytes
batch   (827 in 1)     4,072  4,140    7.3-8.8 s    18-21 ms      1 / 4,433,024
eager   (827 in 827)   5,194  5,353    5.6-6.9 s   416-457 ms   827 / 7,420,398

bbatch  (prelude + 293 in 1)   4,025  4,272   8.5-9.0 s    25-37 ms     2 / 4,439,377
beager  (prelude + 293 in 293) 4,545  4,735   7.5-7.9 s   157-160 ms  294 / 5,527,451
```

Per run both cuts count the same 524,030 chains, 95,140 deopts and 626,930
builtin exits; the only counter that moves is hops: 0 against 1,499,135 for
the whole program, 9,560 against 1,487,575 with the prelude fused. So the
clpfd program crosses between ITS OWN predicates 1.5 million times per run,
and the cut costs 0.5 s for it: about 300 ns per hop on chains this short,
where the hop is a large fraction of the chain. zebra's 447,750 hops did not
show because its chains are long.

The rest of the cut's price is fixed per module: ~3.6 KB (dispatcher, resolver,
the shared preamble) and ~0.5 ms of registration each. Compiling per predicate
is 10-20% cheaper than the monolith, so the compile is linear either way; the
batch's one build is not what makes it slow, the mono-interpreted compiler is.

What this decides: `wasm_compile(all)` keeps the monolith. It is the
whole-program build the user asked for by name, it runs fastest, and its
extra cost is a single build. The lazy grain keeps one module per predicate:
its +11% on queens is this same hop cost, and it compiles only what runs.
Once the libraries are baked as groups the hops inside clpfd vanish from both.

## Where the scalars live, and what a crossing really costs

The WAM's scalars live in LOCALS, loaded from the mailbox on entry and spilled
on exit. That prologue and epilogue are ~1,198 of a small module's ~3,234 byte
floor, and every crossing pays them, so three alternatives were measured.

**A local is a register and nothing else is.** The same counting loop, four
ways, V8:

```
Local            0.32 ns/access
OwnGlobal        1.05 ns/access    3.3x
ImportedGlobal   2.57 ns/access    8x
Memory (mailbox) 2.49 ns/access    7.8x
```

Keeping the scalars in imported globals and reading them directly is 8x per
access, on values like `H` that move on every allocation. And an imported
global costs the same as a mailbox slot (2.57 against 2.49), so using globals
as the home and caching them in locals at the boundaries — the second variant —
buys nothing either: the prologue would read fifteen globals instead of fifteen
memory slots at the same price. Note it is *importing* that costs: a module's
own global is 1.05 ns, but a private global cannot be shared state.

**And the crossing's state transfer is nearly free**, which is the number that
settles it:

```
bare hop                                6.1 ns
hop spilling 9 scalars and reloading 13  10.2 ns
                                        --------
state transfer                          ~4.1 ns
```

Estimating that at 24 accesses times 2.5 ns gives ~60 ns, and that estimate is
wrong by fifteen. The accesses are to contiguous memory in one cache line with
no dependencies between them, and the processor overlaps them; the 2.5 ns above
was the cost of an access *on the critical path of a dependent loop*, where
each iteration waits for the last. A unit cost measured in a dependent loop
does not multiply.

So a full cross-module crossing, state and all, is **~10 ns against the
4,000–15,000 ns** the same crossing costs today going out through
mono-interpreted C#. The prologue and epilogue are not the problem, in time or
in bytes, and the design stands as it is: scalars in locals, mailbox for
crossings.

## What the spike caught

The module addressed linear memory absolutely — slots 0, 8, 16 — instead of
relative to the base the host passes it. On the desktop that is invisible,
because the test image is private and the harness writes at those same
addresses. In a browser address 0 belongs to the runtime, so the module read
garbage and wrote over memory that was not its own.

It announced itself as a wrong *parity*: with two hops the run must end in half
B, and it reported half A. The timing was nonsense too — ten million hops in
"0.0 ms" — but the parity is what identified it. Worth remembering: a
measurement that is too good is a bug report.

## What is not settled

**Firefox.** The gates were taken on V8 only; this machine has no Firefox.
SpiderMonkey implemented tail calls separately, so G0 is genuinely open there.

**Tablets.** iPad and Android are untested. The fallback if a device lacks tail
calls is Tier-0, which already works; how to detect it — by feature probe at
boot, or by user agent after a report — is a decision for when there is one.

**The 1.53× in bytes.** Every module repeating the dispatcher, the resolver and
the unifier is the one number that got worse, and the browser compiles all of
it at load. Sharing those functions through imports, or grouping a few
predicates per module, is the obvious answer and neither has been tried.

## Reproducing

```
dotnet publish src/Shumway.Web/ -c Release -p:ShumwayWasmTier=true
powershell -File src/Shumway.Web/WebShumwayServe.ps1 -Port 8099 -Collect out.txt
msedge --headless=new --user-data-dir=<scratch> \
       "http://localhost:8099/index.html#wasmsplit=10000000x5"
```

The page posts its report to `/collect`: a page cannot write to disk, and
reading it out of the DOM depends on when the browser is asked. Kill only the
browsers started with that `--user-data-dir` — never by process name, which
takes the user's own windows with it.
