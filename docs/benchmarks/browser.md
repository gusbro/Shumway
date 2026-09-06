# WebShumway: wasm Tier-1 vs the interpreted Tier-0

The browser runs the engine on Mono's wasm interpreter (no `Reflection.Emit`,
so no IL Tier-1). The wasm Tier-1 (`docs/design/wasm-tier1-plan.md`) compiles
a hot predicate to a WebAssembly module and runs it natively, keeping the
engine's own heap, stack, trail and registers as its working memory. This page
is the phase-2 measurement: a tiered engine against a plain Tier-0 engine in
the same browser, correctness cross-checked first.

Reached at `#wasmtier` (or `#wasmtier=<rounds>`) on a published site; each
figure is the best of five runs (a min, the standard defence against scheduler
noise). Chrome, threads on, one desktop machine. These are wall-clock ratios
in one browser, not the deterministic `--alloc` metric the desktop harness
uses.

## What it runs

Two engines consult the same corpus. The tiered one has the wasm store
attached at threshold 1 (promote on first dispatch); the plain one is
untouched Tier-0. Both must agree on `loop(1000)`, `nrev` of `[1..30]`, and
`tak(18,12,6,7)` before any timing runs. Six predicates promote.

| case | goal | tier-1 wasm | tier-0 interp | speedup |
|---|---|---:|---:|---:|
| counter 300k | `loop(300000)` | 8-16 ms | ~1665 ms | **100-220x** |
| nrev 200 (×5) | `nrev` of a 200-element list, five times | 270-320 ms | ~385 ms | **1.2-1.4x** |

The counter's tiered time is a handful of milliseconds and swings with the
scheduler from run to run; the Tier-0 baseline is steady near 1665 ms, so the
ratio lands anywhere from ~100x to ~220x across runs. Either end is two orders
of magnitude, which is the only claim that matters.

`tak` is deliberately absent from the timing table: it is nothing but `is/2`
and `=</2`, and every arithmetic goal in the current design leaves the module
as a `BuiltinRequest` for the host to run (~150 ns per crossing, measured in
the phase-0 spike). For `tak(20,14,6)` that boundary traffic dominates
outright, so it is a marker for the next optimization, not a number to quote.

## Reading the spread

The two results are the whole story of where this design wins and where it
does not, and the gap between them is not noise:

- **The counter is the best case and it is enormous.** A tight
  `N>0, N1 is N-1, loop(N1)` self-tail-recurses, so it stays inside the wasm
  module across every iteration -- the self-tail-call compiles to a branch back
  to the dispatcher, never a boundary crossing -- and the one arithmetic
  builtin per turn is the only host trip. 222x over an interpreter-on-an-
  interpreter is the arc's thesis made concrete: native wasm against Mono's
  wasm interpreter is a different order of speed.

- **nrev is call-and-allocate heavy and barely moves.** It builds a great many
  cons cells (heap pressure, which trips the watermark and deopts back to the
  interpreter to collect) and crosses functors (each `nrev`/`app` dispatch
  round-trips through the interpreter's marker machinery rather than calling
  the next module directly). The structure work itself is open-coded in wasm
  and fast; what it does not yet have is a way to stay native ACROSS a
  predicate boundary or a heap collection.

## What the spread tells the next phase

The design bails to the interpreter at three seams, and the spread names their
cost precisely:

1. **Every inter-predicate call** round-trips through the interpreter's resume
   markers. Free for a self-tail loop, per-call for everything else. A direct
   wasm-module-to-wasm-module call (a `call_indirect` between registered
   modules, resolving a callee's index instead of returning verdict 2) is the
   large lever for `nrev`-shaped code.
2. **Every builtin** is a `BuiltinRequest`. Cheap when rare (the counter),
   dominant when the predicate IS builtins (`tak`). Open-coded wasm
   counterparts for the type tests, `=/2` over immediates, and the arithmetic
   comparisons -- the plan's standing note -- close this, decided by measuring
   which builtins actually dominate the bail counts, not before.
3. **Every heap-watermark crossing** deopts to collect. Correct and cheap per
   event; it caps how long an allocation-heavy predicate stays native.

None of the three is a correctness limit -- the deopt path means every one of
them is a predicate that merely runs on the tier it was already on. They are
the phase-B and phase-3 work items, and the counter proves the ceiling worth
reaching for.
