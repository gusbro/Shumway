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
| G2 | incremental compile ≤ 1.5× batch | not yet measured |

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

**G2, the incremental compile cost.** Whether compiling n predicates as n
modules, one at a time, stays within 1.5× of one batch build. That is the gate
that says a real JIT is possible rather than merely fast; it needs the actual
emitter rather than these hand-built modules.

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
