# WebShumway: wasm Tier-1 vs the interpreted Tier-0

The browser runs the engine AOT-compiled to wasm by default (a publish with
`-p:RunAOTCompilation=false` runs it on Mono's IL interpreter instead), and in
neither form is there `Reflection.Emit`, so no IL Tier-1. The wasm Tier-1 (`docs/design/wasm-tier1-plan.md`) compiles
a hot predicate to a WebAssembly module and runs it natively, keeping the
engine's own heap, stack, trail and registers as its working memory. This page
is the tier's measurement: a tiered engine against a plain Tier-0 engine in
the same browser, correctness cross-checked first.

The current reference figures, to compare a change against, are in
[wasm-tier-baseline.md](wasm-tier-baseline.md).

Reached at `#wasmbench` (or `#wasmbench=<rounds>`; `#wasmtier` keeps the
older three-program probe with its time-split diagnostics). See
[Running these in a browser](#running-these-in-a-browser) for the publish
flag every one of them needs, and for the full list of hooks. Each figure is
the best of five runs (a min, the standard defence against scheduler noise).
Chrome, threads on, one desktop machine. These are wall-clock ratios in one
browser, not the deterministic `--alloc` metric the desktop harness uses.


## Running these in a browser

Tier performance is measured here and nowhere else. The desktop world copies
the engine's areas into a private image on every crossing and the browser
world pins them, so the two disagree on direction, not just size: clpr came
out 5x slower on the desktop and 1.1-2.2x faster in the browser. Counts
(deopts, builtin exits, hops) are the same in both; times are not.

### Publish and serve

```bash
# Two flags, and leaving out either one fails quietly.
#
#   ShumwayWasmTier: the tier is opt-in. Without it a published
#   WebShumway is Tier-0, every hook below still runs, and the times
#   measure the interpreter.
#
#   ShumwayDiag: the counters are [Conditional("SHUMWAY_DIAG")]. Without
#   it they compile to nothing and read zero -- chains, hops, switches,
#   deopts, builtin exits and both rankings. A page reporting "deopts=0"
#   then means "not counted", not "none", and the output cannot tell the
#   two apart.
dotnet publish src/Shumway.Web -c Release -p:ShumwayWasmTier=true -p:ShumwayDiag=true

# Serves that publish with the cross-origin-isolation headers (COOP/COEP)
# sent for real, so there is no service-worker synthesis and no
# first-visit reload. -Collect writes any report the page POSTs.
powershell -File src/Shumway.Web/WebShumwayServe.ps1               # port 8080
powershell -File src/Shumway.Web/WebShumwayServe.ps1 -Port 9000 -Collect out.txt
```

The tell for a missing ShumwayDiag is the time line: every bucket reads
zero. The clocks are diagnostic too, and not only for tidiness: one clock
read costs 2 to 2.4 us in the browser, and a chain crosses several, so a
stock build that timed its chains paid for it on every one.

Ship neither flag in anything a user runs: a diagnostic build is not a
release.

The tier flag needs the `wasm-tools` workload, and the build fails without it
rather than shipping a runtime with no shim -- which used to die later at
`DllNotFoundException` the first time a module registered.
`Shumway.Web` is not in the solution, so `dotnet build` never builds it.

Confirm the tier is live before believing any number: `jit_compile(status).`
at the top level. "the capability is off in this build" means the flag was
missing from the publish.

### Running a hook headless

`WebShumwayHook.ps1` does the whole round without opening a window: it serves
the publish, opens the page on the hook in a headless Chrome (or Edge) with a
throwaway profile, waits until the collected report matches `-Done`, prints it,
and kills the browser and the server whether the run finished or not.

```bash
powershell -File src/Shumway.Web/WebShumwayHook.ps1 \
    -Hook '#wasmprobe=exits&n=100000' -Done 'probe exits: done|CRASHED' \
    -Out report.txt -TimeoutSec 900
```

It exits non-zero when nothing matched `-Done` in time. `-JsFlags` passes
flags to the browser's JavaScript engine for the run (`-JsFlags
'--no-liftoff'` compiles every wasm function with the optimizing tier from
the start). Three things that
cost time before they were known:

- **The report path.** The server appends to `-Collect` from its own
  process; a path it cannot resolve answers every POST with 500, and the
  hook runs with nothing recorded. The script resolves `-Out` itself,
  POSIX-style paths from a bash shell (`/c/...`) included.
- **Fingerprinted scripts.** The publish names every module with a hash
  (`main.<hash>.js`, `measure.<hash>.js`) and an import map resolves the
  plain names, so looking for `measure.js` in the publish finds nothing and
  that is not a missing file.
- **A throwaway probe** needs no republish: a `.pl` dropped into
  `publish/wwwroot/probes/` is served as it is.

### The hooks

Every one is a URL fragment on the published page, and every one POSTs its
report to `/collect`. **Most close their own window when done** -- that
looks like a hang and is not.

| hook | what it is |
|---|---|
| `#selftest` | The browser's own test suite. ADR-042 makes it the only automatic test that reaches this layer. |
| `#wasmcompilecheck` | End to end on the `jit_compile` pseudo-goal over the live session engine: attach, run something hot, and status must show the promotion. A test, not a measurement. |
| `#wasmbench[=rounds]` | The five-program benchmark this page reports. |
| `#wasmtier[=rounds]` | The older three-program probe, kept for its time-split diagnostics. |
| `#wasmgrain[=rounds[xqueens][:cell]]` | The many-modules measurement: batch, eager, lazy and Tier-0 side by side, with module count, bytes, compile and registration cost, and the per-run hop/switch/deopt tally including the ranking of deopt sites. The instrument for "what is still leaving the module". |
| `#wasmscalar[=iterationsxrounds]` | Design probe: locals or imported globals for the WAM's scalars. Locals are registers but need a prologue and an epilogue; globals need neither and may not be registers. |
| `#wasmthread` | Design probe: is engine work pinned to one thread? A module registers in the calling thread's own table, so a pool that hands out a different thread makes every module pay registration again. |
| `#wasmspike[=NxM]` | Phase-0 spike, kept as the reproducer for `browser-spike.md`. |
| `#wasmsplit[=hopsxrounds]` | Phase-0 spike (the `return_call_indirect` Go/No-Go), kept as the reproducer for `wasm-split-spike.md`. |
| `#wasmclpz[=rounds[:case,...]]` | Triska's CLP(Z) examples over Scryer's real `clpz.pl`, Tier-0 against the tier, with an oracle per case; the case list narrows the run. clpz loads from its bundle, compiled on demand. `#wasmclpzsrc=...` loads it from source instead, and a trailing `&bundles_off` has the tier compile the bundles' predicates live (`jit_compile(bundles_off)`). Needs [Scryer's library](#scryers-library). |
| `#wasmprobe=<file>[&n=N][&rounds=R][&trace=ch,...][&budget=s][&cps=ab|abc][&grain=lazy]` | The generic probe: the goals `wwwroot/probes/<file>.pl` declares, Tier-0 against the tier, best time, ratio and the tier's counters per goal. See [Probes](#probes). |

### Probes

A new measurement is a Prolog file, not a new hook. `wwwroot/probes/<file>.pl`
is consulted as it is; two kinds of comment lines drive `#wasmprobe`:

```prolog
%uses scryer                      % load Scryer's library tree first
%probe b_getval: b_setval(mbk, 1), mb_bget({N}).
```

Each `%probe` goal runs in both tiers, ABBA within each round, every timed run
after a discarded warm one (a cold run measures the promotion). `{N}` is the
`n` option. `trace=` arms diagnostic channels around the tier's timed runs and
posts their dumps: `trace`, `attrs`, `shapes`, `cells`, `builtins`,
`commits`, and the dump-only `live` and `seq`. They cost real time, so leave
them off for a timing. `cps=ab` also builds the program as continuation
functions (ADR-061) and times that form beside the partitions, ABBA in each
round, with a `cps/tier1` column; `cps=abc` adds the budget grain
(`cpsb/tier1`). `grain=lazy` builds a module per predicate as each crosses a
dispatch threshold (`threshold=`, default 2) instead of the program as one. `probes/exits.pl` prices one kind of module
exit per goal (`#wasmprobe=exits&n=100000`); `probes/transfers.pl` one kind of
transfer between functions (`#wasmprobe=transfers&n=4000000&cps=ab`). The helpers the hooks share (library
load, bounded goal, tier switch, counters, traces) are in `wwwroot/measure.js`.

Under a diagnostic publish (`-p:ShumwayDiag=true`) each goal's row carries a
second line, where the tier's best run went:

```
delegate 1399.3 ms = inWasm 109.4 + stage 549.2 + builtins 28.5 + glue 497.6 + verify 214.7; interp 842.5 ms
```

`verify` is the staging's own consistency checks (the attribute mirror and
log against their stores), a cost of the diagnostic build that is kept out of
`stage`. `interp` is the interpreter's own share: the bytecode a deopt hands
it and every predicate the tier does not cover. The report ends with a
`clock` line, the cost of one clock read in this browser. Every bucket is a
difference of reads, a chain takes several, and a read costs microseconds
(2.2 to 2.4 us measured in headless Chrome), so the split is only as sharp
as that line allows. For the ratio itself, publish without the diagnostic
build: its counters and clocks cost time on every chain.

### Scryer's library

`#wasmclpz` and any probe with `%uses scryer` load Scryer's library tree
from `wwwroot/scryerlib/`. It is not in the repository (it is theirs; the
page loads it at run time; `.gitignore` keeps it out), so regenerate it in
each checkout:

```bash
mkdir -p src/Shumway.Web/wwwroot/scryerlib
cp <scryer>/lib/*.pl src/Shumway.Web/wwwroot/scryerlib/
(cd src/Shumway.Web/wwwroot/scryerlib && ls *.pl > manifest.txt)
```

`#wasmclpz` also needs `scryerlib/cases.pl`, its benchmark program: the
examples of `clpz.pl`'s own documentation with an oracle per case. Being
theirs, it stays out of the repository with the tree and is copied in
beside it. A missing tree now stops the hook with an error; it used to load
one garbage file and let this engine's own clpfd answer, measuring the wrong
library without saying so.

Pass `rounds=1` unless you know why you want more. The wall figure is the
min across rounds and the tick breakdowns sum, so a higher count makes the
two disagree -- once to the point of reporting a negative setup time.

## The benchmark: five programs

Each program gets its own pair of engines (so its group module is its own):
the tiered one has the wasm store attached at threshold 1, the plain one is
untouched Tier-0. A correctness cross-check runs first on both -- it doubles
as the warmup that promotes the group. counter, nrev and tak are the tier
probe's originals; crypt and zebra are the Van Roy suite's, chosen for what
the first three don't have: builtin-heavy inner loops and deep
generate-and-test backtracking.

| case | tier-1 wasm | tier-0 interp | speedup | diagnostics |
|---|---:|---:|---:|---|
| counter 300k | 6.3 ms | 1504 ms | **240x** | 5 entries, clean |
| nrev 200 (×5) | 5.5 ms | 407 ms | **74x** | 10 trail-growth deopts |
| tak 18,12,6 | 8.4 ms | 630 ms | **75x** | 30 trail-growth deopts |
| crypt (×10) | 8.2 ms | 960 ms | **117x** | 5 entries, clean |
| zebra (×10) | 20.4 ms | 1154 ms | **56x** | 5 entries, clean |

**Geometric mean: ~97x.** The plan's gate for turning the tier on by default
on web was a geomean of 2x; it is cleared by a factor of ~50. The tiered
times are small and swing with the scheduler; the Tier-0 baselines are
steady, so the ratios move run to run. The orders of magnitude do not.

crypt and zebra did not start there. The first run of this page measured
crypt at **0.03x** -- thirty-one times slower than the interpreter -- and
zebra at 0.9x, and the per-program diagnostics attributed both on the spot:

1. **crypt: 182,990 builtin exits.** Every `\==/2` in its digit-picking
   chains left the chain for the host, at ~0.18 ms per exit in the browser.
   Fix: **==/2 and \==/2 are open-coded for atomic cells** -- two
   dereferenced Atom/Int cells are identical exactly when they are the same
   cell, so the test is one 64-bit compare inside the module. Floats stay
   out (a float is boxed, equal values live in different cells) and anything
   non-atomic still exits to the real builtin. Exits: 182,990 to zero.
2. **Then crypt still lost (0.1x), and zebra with it -- 283k and 61k chain
   re-entries with zero exits.** The diagnostics showed crypt/5 and zebra/3
   never promoted at all: each was reached only through its promoted caller,
   and a wasm caller's forward call to an out-of-group callee entered the
   callee's bytecode directly, so the dispatch was never counted and the
   promotion counters starved. Every backtracking step then crossed the
   tier boundary. Fix: the interpreter's forward-marker fallback now routes
   through the same `OnDispatch` a plain call uses -- one cached probe on a
   path that already probes the address map -- so the callee is counted,
   promotes, and its whole generate-and-test loop stays inside the module.
   crypt: 283k entries to 5; zebra: 61k to 5.

The verdict diagnostics pin the mechanism for the originals too: one
`nrev(200)` is 2 chain entries with zero module switches, and `tak(14,10,4)`
is 37 entries (all of them trail-growth deopts) with zero switches and zero
builtin exits -- both run natively end to end.

## How it got there: the measurement drove four designs

The first working tier used a **per-entry model**: every entry into a module
pinned the four engine arrays, filled the 24-slot mailbox from engine state,
called, and synced back. Its numbers were counter ~200x but nrev 1.3x and tak
unrunnable -- and a verdict-tally + time-split diagnostic attributed the cost
precisely: nrev ran entirely in wasm (zero deopts, zero builtin crossings,
2 ms of wasm execution) while the per-entry staging cost 90 ms, about 150 us
per entry. The reason is specific to the browser: all of that staging is C#
executed by Mono's interpreter, roughly 100x slower than the same code JITted.
The enemy was not the boundary crossing (~0.3 us) but every line of
interpreted C# on the per-entry path.

That dictated the **chain model** that replaced it. A `PredicateDelegate`
invocation now opens a *chain*: stage once (pin + fill), then hop
module-to-module on the mailbox the wasm itself keeps synced -- a cross-functor
tail call or a callee's proceed into a wasm caller is a marker decode, a
dictionary probe and a raw call. One `nrev(200)` is 2 chains and 600 in-chain
switches; staging fell from 90 ms to 0.2 ms and nrev went from 1.3x to ~35x.

tak then exposed two more layers, each caught by the same diagnostic rather
than by guesswork:

1. **Builtin exits.** tak's arithmetic is open-coded (`a_int_cmp`,
   `a_int_bin`), but its leaf clause ends in `A = Z`, and =/2 was a host
   builtin: one exit-plus-restage per leaf, ~16k of them. Fix: **=/2 is now
   open-coded in the module** (the same two-cell unify every `get_value` uses,
   compounds through the general unifier, attvars still deopt), at all four
   call-site shapes (call/execute, pre- and post-linker).
2. **A deopt storm that was also a soundness bug.** With =/2 inline, every tak
   leaf deopted: the trail had hit its guard limit and stayed there. The bind
   emission stored the heap cell before checking trail space, so the full-trail
   deopt left an untrailed bind behind; the interpreter's re-run found the
   variable already bound, never trailed, never grew the trail, and the next
   leaf deopted again -- 14,912 times. An untrailed bind that survives
   backtracking is unsound, independent of the storm. Fix: **trail-first
   ordering at every bind site** (the general unifier already had it, with the
   comment; the inline binds now share the one `EmitBindDa` helper). Deopts:
   14,912 to 36 -- one per actual trail growth.

The chain still left tak at 3.4x and nrev at ~35x, both bounded by the
interpreted per-switch cost (~4-15 us) of hopping between per-predicate
modules. The fourth design removed the hops themselves: **group modules**.
Every promoted predicate compiles into one module over a unified pc space
(each member's bias is its linked base), so a cross-member call -- tail or
non-tail -- is an internal dispatch jump, the same mechanism the self-tail
always used. A non-tail call bakes its resume marker as a constant and the
callee's proceed compares Cp against the group's known markers and jumps
straight back; backtracking across members stays native the same way (the
shared fail case knows every member's BP markers). Markers encode (functor,
address) rather than cursor ordinals, so they survive the group rebuild
that each new promotion triggers. nrev: ~35x to ~75x; tak: 3.4x to ~77x.

## Reading what remains

- All three cases now sit within the same order of magnitude of the
  counter's ceiling; what separates them is real work (nrev's allocation,
  tak's trail-growth deopts) plus the per-chain staging, which amortises.
- The remaining glue in the diagnostics is the chain open/close around each
  query goal and the builtin path -- both once-per-boundary costs, not
  per-call ones.

None of the remaining bounds is a correctness limit: deopt returns any
predicate to the tier it was already on.
