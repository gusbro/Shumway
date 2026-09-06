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

`tak` is deliberately absent from the timing table, and the reason is worth
stating precisely because it is easy to get wrong. Its arithmetic is NOT the
problem: `X =< Y` compiles to `a_int_cmp` and `X - 1` to `a_int_bin`, both
open-coded in wasm, so tak makes zero builtin crossings. Its cost is the
CALLS. tak's recursive clause makes three non-tail `call`s to tak plus one
tail `execute`; the tail self-call stays in the module (a branch back to the
dispatcher), but every non-tail call returns to the interpreter, which
dispatches the callee back into wasm and, when it proceeds, re-enters the
caller — two boundary round-trips per non-tail call. Takeuchi's call count is
enormous and three of every four recursive calls pay that, so the round-trip
traffic dominates. tak is the marker for the inter-predicate-call optimization
(phase 3+), not the builtin one.

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
  interpreter to collect) and crosses functors: `nrev`'s non-tail call to
  itself and its tail call to `app` each leave the module, and while `app`'s
  own recursion is a tail self-call that stays native, the per-element
  `nrev`→`app` handoff round-trips the interpreter's marker machinery. The
  structure work itself is open-coded in wasm and fast; what it does not yet
  have is a way to stay native ACROSS a non-tail predicate boundary or a heap
  collection.

## What the spread tells the next phase

The design bails to the interpreter at three seams, and the spread names their
cost precisely:

1. **Every non-tail inter-predicate call** round-trips through the
   interpreter's resume markers -- twice, once to dispatch the callee and once
   when it proceeds back. Free for a self-tail loop (which branches inside the
   module), per-call for everything else. This is the dominant cost for BOTH
   `tak` (three non-tail self-calls per invocation) and `nrev` (the per-element
   `nrev`→`app` handoff), so a direct wasm-to-wasm call -- resolving the
   callee's table index and calling it, rather than returning verdict 2 -- is
   the single largest lever, and the measured priority for the next phase.
2. **Every builtin** is a `BuiltinRequest`. Cheap when rare (the counter's one
   `is` per turn), and NOT what bounds `tak` (its arithmetic is open-coded).
   Open-coded wasm counterparts for the type tests and `=/2` over immediates
   help builtin-dense predicates, but the measurement says calls come first.
3. **Every heap-watermark crossing** deopts to collect. Correct and cheap per
   event; it caps how long an allocation-heavy predicate (`nrev`) stays native.

None of the three is a correctness limit -- the deopt path means every one of
them is a predicate that merely runs on the tier it was already on. The
measurement reorders the plan's next steps: the inter-predicate call boundary,
not open-coded builtins, is where the data points, and the counter proves the
ceiling worth reaching for.
