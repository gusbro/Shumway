# Tier-1 WebAssembly: what the phase 0 spike measured

The plan ([wasm-tier1-plan.md](../design/wasm-tier1-plan.md)) put a numeric
gate in front of the arc: the counter had to run at least 2.0x faster as wasm
than on Tier-0, with a boundary under 1000 ns per entry. This is what the
spike found — including one wrong verdict on the way, kept here because the
diagnosis is the most load-bearing fact in the file.

Measured 2026-09-06 on Windows 10, .NET 10, Chrome headless, WebShumway
published Release with threads on and the page cross-origin isolated.

## The verdict

**Go, by three orders of magnitude.** Representative full run:

```
echo answers: 4242
boundary via the shim:  284.4 ns per entry
boundary via calli:     285.0 ns per entry

per counter iteration, 2.000.000 x3 rounds
  Tier-0 (interpreted in the browser): 5782.88 ns
  wasm (native, same page):               5.42 ns
  ratio: 1067x        (the gate asked for 2.0x)
```

Run-to-run the wasm counter lands between 5.4 and 9.8 ns and the boundary
between 250 and 420 ns; the ratio between 600x and 1100x. Every run clears
both gates with two orders of margin. The counter is the friendliest shape
there is (no heap, no builtins) and the ratio on real programs will be far
smaller — but the gate was about whether the mechanism can win at all, and
it can.

Firefox was not measured (not installed on this machine); the plan's
two-browser criterion is therefore not formally met here. What failed and was
fixed along the way was runtime-side, not browser-side, so the remaining
browser risk is the ordinary kind.

## The wrong verdict, and what it taught

The first attempt declared a No-Go: a `delegate* unmanaged` built from the
table index **hung the runtime** — no exception, from any thread, and an echo
module whose whole body is `return param2` hung identically. The conclusion
"managed code cannot call a runtime-registered wasm function" was written
down here for a day. It was wrong, and the reason matters more than the
numbers:

**With threads on, every worker has its own `WebAssembly.Table`.** Only the
memory is shared. The module had been instantiated and `addFunction`-ed from
the PAGE's JavaScript — the UI thread's realm — so the index it returned
named a slot in the UI thread's table. The .NET runtime lives in a worker.
Calling that index there is a `call_indirect` into a slot that either does
not exist (trap, and a trap in that position takes the worker down silently:
the observed hang) or — worse — exists and holds a **different function**,
which would run wrong code without any fault at all. The spike measured both
outcomes directly:

```
thread 8 registered echo at index 7873
thread 5 (table length 7876): the slot exists there and is occupied   <- different function
thread 9 (table length 7873): the slot does not exist there           <- trap if called
```

## The design that follows

Registration must happen **in the realm of the thread that will call**, and
the sanctioned way to be in that realm from C# is native code: `spike.c`,
linked into `dotnet.native.wasm` (threads already force the native relink,
so it costs nothing extra), with two `EM_JS` functions —
`shumway_wasm_register(bytes, len)` instantiates the module against the
shared memory and `addFunction`s its export into **the calling thread's**
table — and a one-line `shumway_wasm_call` whose function-pointer cast is a
single `call_indirect`.

With a thread-local index, **everything** works:

- the C shim path (`DllImport` + call_indirect): 284 ns per entry;
- the **raw managed calli** — D1 exactly as the plan designed it: 285 ns.
  The mechanism was never broken; the index was foreign.

So the product shape is: compile the predicate to bytes once; each pool
thread that dispatches it registers the bytes lazily and caches its own index
(a per-thread map, exactly the kind of thing the engine's thread-agility
rules already accommodate — the bytes are shared, the index is not). The
page's JavaScript plays no part; C# holds the bytes and the whole path is
native.

## D2, confirmed on the way

The module imports the runtime's own memory (shared, 80+ MB in the page) and
addresses the mailbox and the register file inside it; the pinned-POH
mailbox/register arrays are written and read by the module across the
boundary. The question ADR-042 left open about the managed heap is answered:
no copying, bases handed over per entry.

## Reference numbers

Desktop, same counter, one process, minimum of seven rounds (the wasm rows
run through the emitter library's wasm-to-IL engine):

| per counter iteration | ns | vs Tier-0 |
|---|---|---|
| Tier-0 (bytecode) | 168,25 | 1,00x |
| Tier-1 (IL) | 25,66 | 6,6x |
| wasm, counter in memory | 3,60 | 46,7x |
| wasm, counter in a local | 1,75 | 96,4x |

The two wasm rows are the two honest readings of "what the backend would
emit": a straight WAM translation (ADR-021 rejected a register allocator on
measurement), and the ceiling with the counter cached in a local. In the
browser the Tier-0 column is ~34x worse (5783 vs 168 ns: an interpreter
running on an interpreter) while the wasm column is the same native speed —
which is the whole reason this arc exists.

Also measured, for the record: the pre-rejected C# → JavaScript → wasm thunk
costs 52.4 us per call and is thread-affine; rejecting it was right.

## How to run it again

```
dotnet run -c Release --project tests/Shumway.Tests.Benchmarks/ -- --wasm-spike
```

for the desktop table, and for the browser:

```
dotnet publish src/Shumway.Web -c Release -p:CompressionEnabled=false
powershell -File src/Shumway.Web/WebShumwayServe.ps1 -Port 8099 -Collect out.txt
```

then open `http://localhost:8099/#wasmspike=2000000x3`. The page posts each
step back to `out.txt` as it completes, so a hang names the step that never
answered. `CompressionEnabled=false` takes the publish from four minutes to
under thirty seconds, which is what makes the loop bearable.
