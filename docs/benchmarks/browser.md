# WebShumway: wasm Tier-1 vs the interpreted Tier-0

The browser runs the engine on Mono's wasm interpreter (no `Reflection.Emit`,
so no IL Tier-1). The wasm Tier-1 (`docs/design/wasm-tier1-plan.md`) compiles
a hot predicate to a WebAssembly module and runs it natively, keeping the
engine's own heap, stack, trail and registers as its working memory. This page
is the tier's measurement: a tiered engine against a plain Tier-0 engine in
the same browser, correctness cross-checked first.

Reached at `#wasmtier` (or `#wasmtier=<rounds>`) on a published site; each
figure is the best of five runs (a min, the standard defence against scheduler
noise). Chrome, threads on, one desktop machine. These are wall-clock ratios
in one browser, not the deterministic `--alloc` metric the desktop harness
uses.

## Current numbers

Two engines consult the same corpus. The tiered one has the wasm store
attached at threshold 1 (promote on first dispatch); the plain one is
untouched Tier-0. Both must agree on `loop(1000)`, `nrev` of `[1..30]`, and
`tak(18,12,6,7)` before any timing runs. Six predicates promote.

| case | goal | tier-1 wasm | tier-0 interp | speedup |
|---|---|---:|---:|---:|
| counter 300k | `loop(300000)` | 7-20 ms | ~1800 ms | **~90-250x** |
| nrev 200 (×5) | `nrev` of a 200-element list, five times | ~11 ms | ~400 ms | **~31-39x** |
| tak 18,12,6 | `tak(18,12,6,_)` | ~260 ms | ~890 ms | **~3.4x** |

The tiered times are small and swing with the scheduler; the Tier-0 baselines
are steady, so the ratios move run to run. The orders of magnitude do not.

## How it got there: the measurement drove three designs

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

## Reading the spread that remains

- **The counter** stays the ceiling: one chain, everything in-module, the one
  `is/2` per turn open-coded.
- **nrev** is now bounded by the ~600 in-chain switches (~15 us of interpreted
  TryChain guards each) plus its real allocation work.
- **tak** at 3.4x is bounded the same way at larger scale: ~32k switches per
  `tak(14,10,4)`, glue ~140 ms vs 108 ms in-wasm. The next lever, if wanted,
  is moving the cross-functor tail hop INTO wasm (`call_indirect` through an
  imported function table with a per-thread functor-to-index map), which
  removes the interpreted switch entirely. That is an arc of its own -- realm
  traps are silent -- and 3.4x already clears the plan's 2x gate.

None of the remaining bounds is a correctness limit: deopt returns any
predicate to the tier it was already on.
