# Tier-1 WebAssembly: what the phase 0 spike measured

The plan ([wasm-tier1-plan.md](../design/wasm-tier1-plan.md)) put a numeric
gate in front of the arc: the counter had to run at least 2.0x faster as wasm
than on Tier-0, in two browsers, with a boundary under 1 microsecond per
entry. This is what the spike found.

Measured 2026-09-06 on Windows 10, .NET 10, Chrome 141 headless, WebShumway
published Release with threads on and the page cross-origin isolated.

## The verdict

**The code is fast. The call into it does not work.**

A compiled predicate is reached, by design, through a function pointer whose
value is a table index: JavaScript registers the module's export in the
runtime's function table at instantiation, and C# calls it with no JavaScript
on the path (the plan's D1). In this runtime that call **never returns** —
neither from the runtime thread nor from a pool thread. It does not throw,
which would be recoverable; it hangs.

It is the call and not the callee. The same module runs perfectly when
JavaScript calls it, and a second module that cannot loop at all (its whole
body is `return param2`) hangs exactly the same way when called from C#.

The path the plan pre-rejected as a product, C# to JavaScript to wasm, does
work, and costs **52.4 microseconds per call** — 52 times the plan's ceiling,
and thread-affine besides, so the engine (which runs on pool threads) could
not take it without hopping threads for every crossing.

By the plan's own criterion this is a **No-Go for D1 as designed**.

## What did work

Everything else, and it worked well.

| | |
|---|---|
| the runtime's memory, imported | 83.689.472 bytes, shared |
| the module, instantiated against it | yes, `run` exported |
| mailbox and registers inside that memory | read and written by the module |
| `addFunction` into the runtime's table | yes, index 1687 |
| pinned arrays as bases, from a pool thread | yes: mailbox and registers pinned, addresses passed |

So D2 — the memory contract, which is the question ADR-042 left open about
the heap being a managed array — is answered: a module can address the
engine's arrays with no copying, and the bases can be handed to it per entry.

## The numbers

The counter is `loop(N) :- N > 0, N1 is N - 1, loop(N1). loop(0).` — the
friendliest shape there is, no heap and no builtins, deliberately: if the
codegen cannot win here it cannot win anywhere.

**In Chrome**, the module driven from JavaScript (the faithful variant, which
loads X0 from the register file, tests its tag, unboxes, decrements, boxes and
stores back on every round):

```
counter 1.000.000:  5,56 ns per iteration
counter 20.000.000: 4,21 ns per iteration
boundary from JS:   56,5 ns per entry
```

**On the desktop**, where the same module runs through the emitter library's
wasm-to-IL engine, beside the same counter run by both of our tiers, in one
process, minimum of seven rounds:

| per counter iteration | ns | vs Tier-0 |
|---|---|---|
| Tier-0 (bytecode) | 168,25 | 1,00x |
| Tier-1 (IL) | 25,66 | 6,6x |
| wasm, counter in memory | 3,60 | 46,7x |
| wasm, counter in a local | 1,75 | 96,4x |

The two wasm rows are the two honest readings of "what the backend would
emit". The first is a straight translation of the WAM, which is what this
engine would produce (ADR-021 turned a register allocator down on
measurement); the second keeps the counter in a wasm local across the loop,
which is the ceiling of the shape.

Writing the counter so that first-argument indexing decides it changes
nothing (172 ns at Tier-0, 24 ns at Tier-1): what costs, per round, is the
call machinery, not a choice point.

## What this says, and what it does not

The generated code is not the problem, and it is not close to being the
problem: on the same machine, in the same process, the shape a wasm backend
would emit runs 46x the bytecode interpreter and 7x the IL tier. In the
browser Tier-0 is worse still, because there the interpreter is itself
interpreted.

The problem is that .NET's wasm runtime has no way for managed code to call a
function that was put into its table at run time. That is not a performance
finding and no amount of tuning changes it.

Two things remain possible, and neither is the arc as planned:

- **Ahead of time, natively linked.** A module linked into the runtime's own
  build and called as a `DllImport` is an ordinary native call, which does
  work. That is build-time only: nothing can be added to the linked module
  while the program runs, so it is an AOT story and the JIT half of the plan
  has no path in it.
- **Fewer, longer crossings.** 52 microseconds is affordable only if a
  crossing buys milliseconds of work, which is not what a per-predicate-call
  design does, and the thread affinity remains.

Firefox was not measured: it is not installed on this machine. The Go
criterion asks for two browsers, so it could not have been met here in any
case — but D1 fails in the one browser that was measured, and the mechanism
it fails on is the runtime's, not the browser's.

## How to run it again

```
dotnet run -c Release --project tests/Shumway.Tests.Benchmarks/ -- --wasm-spike
```

for the desktop table, and for the browser:

```
dotnet publish src/Shumway.Web -c Release -p:CompressionEnabled=false
powershell -File src/Shumway.Web/WebShumwayServe.ps1 -Port 8099 -Collect out.txt
```

then open `http://localhost:8099/#wasmspike=-99x1`, which walks the steps in
order and posts each one back to `out.txt` as it goes. `CompressionEnabled=false`
is what makes the publish take 27 seconds instead of four minutes, which
matters when the loop is publish, load, measure.
