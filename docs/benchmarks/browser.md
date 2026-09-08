# WebShumway: wasm Tier-1 vs the interpreted Tier-0

The browser runs the engine on Mono's wasm interpreter (no `Reflection.Emit`,
so no IL Tier-1). The wasm Tier-1 (`docs/design/wasm-tier1-plan.md`) compiles
a hot predicate to a WebAssembly module and runs it natively, keeping the
engine's own heap, stack, trail and registers as its working memory. This page
is the tier's measurement: a tiered engine against a plain Tier-0 engine in
the same browser, correctness cross-checked first.

Reached at `#wasmbench` (or `#wasmbench=<rounds>`; `#wasmtier` keeps the
older three-program probe with its time-split diagnostics). Each figure is
the best of five runs (a min, the standard defence against scheduler noise).
Chrome, threads on, one desktop machine. These are wall-clock ratios in one
browser, not the deterministic `--alloc` metric the desktop harness uses.

## The benchmark: five programs

Each program gets its OWN pair of engines (so its group module is its own):
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
crypt at **0.03x** -- thirty-one times SLOWER than the interpreter -- and
zebra at 0.9x, and the per-program diagnostics attributed both on the spot:

1. **crypt: 182,990 builtin exits.** Every `\==/2` in its digit-picking
   chains left the chain for the host, at ~0.18 ms per exit in the browser.
   Fix: **==/2 and \==/2 are open-coded for ATOMIC cells** -- two
   dereferenced Atom/Int cells are identical exactly when they are the same
   cell, so the test is one 64-bit compare inside the module. Floats stay
   out (a float is boxed, equal values live in different cells) and anything
   non-atomic still exits to the real builtin. Exits: 182,990 to zero.
2. **Then crypt still lost (0.1x), and zebra with it -- 283k and 61k chain
   RE-ENTRIES with zero exits.** The diagnostics showed crypt/5 and zebra/3
   never promoted at all: each was reached only through its promoted caller,
   and a wasm caller's forward call to an out-of-group callee entered the
   callee's bytecode directly, so the dispatch was never COUNTED and the
   promotion counters starved. Every backtracking step then crossed the
   tier boundary. Fix: the interpreter's forward-marker fallback now routes
   through the same `OnDispatch` a plain call uses -- one cached probe on a
   path that already probes the address map -- so the callee is counted,
   promotes, and its whole generate-and-test loop stays inside the module.
   crypt: 283k entries to 5; zebra: 61k to 5.

The verdict diagnostics pin the mechanism for the originals too: one
`nrev(200)` is 2 chain entries with ZERO module switches, and `tak(14,10,4)`
is 37 entries (all of them trail-growth deopts) with zero switches and zero
builtin exits -- both run natively end to end.

## How it got there: the measurement drove four designs

The first working tier used a **per-entry model**: every entry into a module
pinned the four engine arrays, filled the 24-slot mailbox from engine state,
called, and synced back. Its numbers were counter ~200x but nrev 1.3x and tak
unrunnable -- and a verdict-tally + time-split diagnostic attributed the cost
precisely: nrev ran ENTIRELY in wasm (zero deopts, zero builtin crossings,
2 ms of wasm execution) while the per-entry staging cost 90 ms, about 150 us
per entry. The reason is specific to the browser: all of that staging is C#
executed by Mono's interpreter, roughly 100x slower than the same code JITted.
The enemy was not the boundary crossing (~0.3 us) but every LINE of
interpreted C# on the per-entry path.

That dictated the **chain model** that replaced it. A `PredicateDelegate`
invocation now opens a CHAIN: stage once (pin + fill), then hop
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
   leaf DEOPTED: the trail had hit its guard limit and stayed there. The bind
   emission stored the heap cell BEFORE checking trail space, so the full-trail
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
Every promoted predicate compiles into ONE module over a unified pc space
(each member's bias is its linked base), so a cross-member call -- tail or
NON-tail -- is an internal dispatch jump, the same mechanism the self-tail
always used. A non-tail call bakes its resume marker as a constant and the
callee's proceed compares Cp against the group's known markers and jumps
straight back; backtracking across members stays native the same way (the
shared fail case knows every member's BP markers). Markers encode (functor,
ADDRESS) rather than cursor ordinals, so they survive the group rebuild
that each new promotion triggers. nrev: ~35x to ~75x; tak: 3.4x to ~77x.

## Reading what remains

- All three cases now sit within the same order of magnitude of the
  counter's ceiling; what separates them is real work (nrev's allocation,
  tak's trail-growth deopts) plus the per-chain staging, which amortises.
- The remaining glue in the diagnostics is the chain OPEN/CLOSE around each
  query goal and the builtin path -- both once-per-boundary costs, not
  per-call ones.

None of the remaining bounds is a correctness limit: deopt returns any
predicate to the tier it was already on.
