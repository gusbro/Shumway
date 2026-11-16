# ADR-068: Hosting the runtime in JupyterLite: one engine per kernel

## Status

Accepted (2026-11-14), with the work the audit below found. The session host
and the four pieces of work of the audit are implemented (see "What was built",
2026-11-15); the notebook app and its export surface are not. Records D6 and
D7. This changes the threading model of the web host, which
`decision-policy.md` names a major decision.

## Context

- **Hosting.** ADR-042 §3: the .NET runtime runs with `WasmEnableThreads`,
  every `[JSExport]` returns a `Task`, engine work is serialised behind one
  gate, and cancellation stays outside the gate. *Hosting a JS worker that
  owns the runtime was tried first and does not boot.*
- **Today's host is a singleton.** `WebShumwayApp` holds one engine with its
  own thread (`_engineThread`), `QueryCancel`, `EngineReset`, and page input
  (`PageInput.cs`: `AskForInput`, `SupplyInput`, `SupplyEndOfFile`).
- **Jupyter needs isolation.** JupyterLite can have several notebooks open in
  one page, each with its own kernel, and Jupyter semantics require each
  kernel's state to be isolated. The runtime, however, loads **once per
  page**.
- **Several engines in one runtime already work.** ADR-042 §7: compilation
  builds its own ephemeral engine next to the session's, and "two engines at
  once is the threading model working as designed".

## Decision

### D6: The runtime lives in the JupyterLite page

The kernel extension loads the runtime into the page, as the WebShumway
demo does. There is no Web Worker; the TypeScript kernel calls `[JSExport]`
methods directly.

- **Interrupt** maps to the session's `QueryCancel` (with the consequences in
  ADR-067 D5).
- **Restart** discards the session and creates a new one.
- **`read/1` and friends** use the session's page input. When the engine
  asks for a line, the kernel sends Jupyter an `input_request`, and its
  `input_reply` is supplied as the line. Closing the input supplies end of
  file.

### D7: One engine per kernel, one runtime per page, with a limit

- **Sessions replace the singleton.** The static engine becomes a *session*
  object: an engine, its thread, the request and response pipes of ADR-067,
  a cancellation source, page input and a working directory (ADR-070/071).
  Sessions are keyed by Jupyter's `kernelId`.
- **Lifetime.** A session is created when the kernel starts (load the bundle,
  boot the server) and lives until shutdown or restart. Cells never create
  engines.
- **The limit.** A configurable maximum number of live sessions per page.
  Creating one beyond the limit **fails with a clear message**; idle sessions
  are never evicted silently, because the user would lose state without
  noticing.
- **The default** is set by measurement (plan, verification V2): the memory
  of an engine with the server loaded, on a desktop browser and on a tablet.
  A phone is not a target: a notebook is not usable on its screen.
- **The WebShumway demo** is rewritten as a host with exactly one session, so
  there is one code path.

Sketch of the export surface (names are not binding):

```
Task<string> SessionCreate(string kernelId, string bundle, string cwd)
Task<string> SessionRequest(string kernelId, string jsonLine)   // ADR-067
Task<bool>   SessionCancel(string kernelId)
Task         SessionSupplyInput(string kernelId, string line)
Task         SessionSupplyEndOfFile(string kernelId)
Task         SessionDispose(string kernelId)
```

The engine reports, through a `[JSImport]` callback carrying the
`kernelId`, that it wants input or that it wrote output. This is the
per-session counterpart of today's `ui.askForInput`.

## Consequences

- Each notebook keeps its own database, flags and operators, and nothing
  leaks between kernels, with one exception: the working directory, which is
  process-global today (ADR-071).
- Per-session state, previously static, was audited for anything else that
  is still global. The findings are below; four of them are work this ADR
  brings with it (`user_error`, the pinned tables, the tier settings and
  `jit_compile(cps)`).
- Memory grows with each open notebook, which is the reason for the limit.
- ADR-042 §3 still holds per session. It is the number of sessions that
  changes.

## What the audit found (2026-11-14)

Read against `main` at `7713419`. Nothing here is implemented; it is the
work this ADR was accepted with.

### One per page today, one per session after

| State | Where | Note |
|---|---|---|
| The engine and its open query | `WebShumwayApp._session`, `_run` | |
| The engine thread and its queue | `_engineQueue`, `_engineThread` | see "One thread per session" |
| Page input | `_pageInput` in `PageInput.cs` | `ui.askForInput` carries no session id |
| The output buffer and its throttle | `_outSegments`, `_outDrained` | `ui.write`, `ui.writeError` and `ui.writeNote` carry no session id; Stop must drop one session's unpainted output, not everyone's |
| Tier settings kept across a restart | `BrowserWasmTier.Disabled`, `BootThreshold`, `BootBatch` | as statics, `jit_compile/1` in one notebook would change the next boot of every other |

### One per page, and it needs a fix or a decision

- **`user_error` cannot be routed.** Every engine binds `user_error` to
  `Console.Error` (`StreamRegistry`), and the page sets `Console.Error` once.
  The engine needs an error writer of its own, next to `Out` and `Warnings`.
  This is a small addition to the embedding API.
- **Two tables of the wasm tier are pinned in statics without a lock.** The
  wasm world is already one per engine and its module index one per thread.
  But the arithmetic functor table and the reverse functor table are pinned
  in static fields (`_arithPin`, `_funRevPin` in `BrowserWasmTier.cs`) and
  repinned, unlocked, when they grow. Two sessions entering wasm at once can
  free a pin the other is using. This must be fixed before two sessions run
  the tier.
- **`jit_compile(cps)` is process-wide** (`WasmPredicateCompiler.CpsMode`). One
  notebook's choice of form applies to the modules every other builds next.
- **The working directory** is process-wide (ADR-071).
- **Libraries stay page-wide, by design.** The registry lives on MEMFS and
  compilation is behind one gate, so two sessions compiling on demand queue.
  The queue of "compiled on demand" notices is page-wide too.
- **Debug mode stays with the demo.** `WebDebug.cs` holds one debug service
  and one resume gate. Notebook sessions do not offer debug mode.
- **Diagnostic counters** are static and unsynchronised. Harmless, but what
  `jit_compile(status)` prints mixes sessions.

### One thread per session

- The server's loop blocks on its session's thread (ADR-067), so sessions
  cannot share one.
- A compiled wasm module is registered in the function table of the thread
  that calls it. Each session thread therefore registers the stdlib module
  and the server's module again: time at session start, and compiled code
  per thread. A comment in `WebShumwayApp` records 37 ms for one large group.
  Both costs are part of verification V2.
- The runtime boots with a pool of 5 workers and keeps 1 spare
  (`pthreadPoolInitialSize`, `pthreadPoolUnusedSize` in the built
  `dotnet.js`). Session threads come out of that pool.

### Not exercised today

No test was found that runs two engines with the wasm tier on different
threads. On the desktop, `EnginePool` covers several engines with the IL
tier. The acceptance test of Phase 1 (two sessions, isolated) is the first
to do it in the browser.

### Memory, a first figure (V2)

Desktop .NET 10, 64-bit, managed heap after a full collection, engines kept
alive, measured from the second engine on:

| Engine | Managed heap |
|---|---|
| Booted, one query run | 1.7 MB |
| With `library(clpfd)` loaded (2,300 lines) and a query run | 6.4 MB |

The first engine costs 5.5 MB and 14 MB: it pays for the shared tables. The
figure leaves out the session's thread, the compiled code of its modules and
everything specific to the browser, so it is a floor. The browser
measurement is still to do.

## What was built (2026-11-15)

- **The host library, `Shumway.WebHost`.** A session is an engine, its
  thread, the query it has open, its input, its output with the throttle, and
  a working directory of its own (ADR-071). A registry keeps the sessions of
  a page by id, with the limit: a session past it is refused with a message.
  The library has no JavaScript in it; the app gives each session an object
  that speaks for its part of the page. That is also what lets the desktop
  tests drive two sessions against each other.
- **WebShumway is a host with one session.** Same exports, same page.
- **The four pieces of work of the audit.**
  - `PrologEngine.Error`: what `user_error` writes to, per engine.
  - The two pinned tables are pinned by each chain, like every other area.
  - What `jit_compile/1` chose (off, threshold, batch, form) is an object of
    the session, not statics: it survives a restart of that session and
    reaches no other.
- **A bug the second session found.** `System.Threading.Lock` does not
  exclude on browser-wasm with threads: two threads contending for one end
  with the exit throwing that the thread does not hold it, in Chromium,
  Firefox and WebKit alike. The atom table was the one user of that type, so
  two threads interning one new name made two atoms, and a predicate whose
  clauses were read on either side of that came out as two. The table uses a
  monitor now. WebShumway already had the exposure: a library compiles on
  its own thread beside the session's engine (ADR-042 §7).
- **Tests.** On the desktop, two sessions: databases, flags and operators
  apart, a cancel that leaves the other running, input, output and
  `user_error` to the session's own page, a directory each, the limit, and a
  close that lets go of a goal waiting for input. In the browser, the
  `#sessions` hook: two sessions beside the page's own, both inside wasm at
  once, one growing the shared tables under the other, `jit_compile/1` per
  session, cancellation in both forms. It passes in Chromium 159 and WebKit
  27.2.

### Still open

- **The tier's own notes** ("compiling N predicates") still go to the page
  as a whole, not to a session.
- **The session limit's default.** D7 sets it with the server loaded, which
  Phase 2 builds. The tablet's figure with `library(clpfd)` is below, "On a
  tablet".
- **Five megabytes that stay held after some boots** (below, "What a session
  is made of"): what holds them is not known.
- **The stack of a session's thread** stays the runtime's default, about
  5 MB. A 512 KB stack ran the same deep goals (below, "A smaller stack"),
  and what it would save did not show in the runtime's memory.

### What Firefox was

In Firefox 159 the `#sessions` hook stopped part way, and the page's own
selftest stopped at 87 checks on `main` too. Neither was the sessions. The
service worker's `activate` waited for `clients.claim()`, and in Firefox the
claim does not settle while the page's worker threads are blocked, which the
runtime's threads are. The worker stayed `activating` with every fetch of the
page held behind it, the browser killed it a minute later, and those fetches
failed. The worker now asks for the claim without waiting for it (a fix of
WebShumway's own). With it the hook passes in the three engines.

### Memory and threads in a browser (V2, 2026-11-15)

The `#sessionsmem` hook opens sessions one at a time, each with
`library(clpfd)` loaded and a query run. Headless Chromium 159, Firefox 159
and WebKit 27.2, a publish without AOT.

| | Chromium | Firefox | WebKit |
|---|---|---|---|
| The runtime's memory with the page's own session | 138 MB | 140 MB | 140 MB |
| After 6 more sessions | 412 MB | 349 MB | 419 MB |
| After 12 more sessions | 593 MB | 600 MB | not run |

- **A session costs about 38 MB of the runtime's memory**, taken over 12:
  about 12 MB of managed heap to boot and 10 MB more with `library(clpfd)`
  loaded, and the rest outside the managed heap. The runtime's memory grows
  a fifth at a time, so this figure is coarse; the managed heap is exact.
- **Most of the managed heap was the same bytes again** (see "What a session
  is made of" below).
- **Time.** A session boots in 0.4 to 1.5 s and loads `library(clpfd)` in
  0.3 to 1.6 s.
- **Threads are not the limit.** Twelve sessions start with the pool the
  runtime makes at boot (5), opened one at a time. A test that started three
  threads in a row and blocked waiting for them never ran them: a thread
  past the pool starts only once its creator lets go.

### What a session is made of (2026-11-15)

The hook takes a level: what each session holds. Chromium, managed heap a
session adds:

| A session holds | Managed heap |
|---|---|
| Its thread alone | none; about 5 MB of stack outside the heap |
| An engine with no wasm in it | 1.3 MB |
| The engine as the page boots it | 12.2 MB |
| That, with `library(clpfd)` loaded | 22.3 MB |

- **The engine is small; the wasm modules are not.** The standard library's
  bundle is 5.8 MB, of which 5.6 MB is its wasm module; the library's is
  4.8 MB, about 4.1 MB of it the module.
- **Each session held two copies of each module**: the bytes its own read of
  the bundle gave it, kept to install the module again after
  `jit_compile(bundles_off)`, and a patched, pinned copy its threads register
  the module from. That is about 19.5 MB of the 22.3 MB.
- **A session's thread costs about 5 MB**, the stack the runtime gives a
  thread. It is the floor of what a session costs.

**Shared by content.** The page reads the standard library's bundle once; a
bundle's wasm modules are one array per content, whichever engine reads the
bundle; and the sessions that install the same module register it from one
pinned copy. Measured with that, sessions with `library(clpfd)` loaded:

| The runtime's memory | Before | Shared |
|---|---|---|
| Chromium, 24 sessions | 887 MB | 495 to 591 MB |
| Chromium, 12 sessions | 593 MB | 344 to 349 MB |
| Firefox, 12 sessions | 600 MB | 419 MB |
| WebKit, 12 sessions | not run | 349 MB |

The managed heap a session adds goes from 22.3 MB to 3.9 MB, or 8.9 MB: in
about half of the first dozen sessions 5 MB more stays held after the boot.
It did before the change too, and what holds it is not known. Over 24
sessions the average is 7 MB.

**A smaller stack.** Three sessions with `library(clpfd)` ran eight deep
goals each, with the runtime's stack and with 512 KB: `copy_term` of a term
100,000 deep, `term_to_atom` and `print` at 20,000, `throw` at 50,000,
`garbage_collect` with two terms 200,000 deep, `msort` of 100,000 and a
`sum/3` over 2,000 variables. All 24 passed with both stacks: the engine's
walks over a term keep their own stacks. The runtime's memory came out at
343.5 MB with both, but it grows in steps of 20%, so the measure cannot show
the at most 4.5 MB a thread the smaller stack would save. The stack stays the
default.

**On a tablet.** The figures above are a desktop's. The device with the
least memory that a notebook is used on is a tablet, measured with the hook
on the published site (a tablet reaches a development machine without https,
and a page without https is not isolated). With `#sessionsmem=6`, six
sessions with `library(clpfd)` loaded all answer with all of them open; the
runtime's memory goes from 138 MB to 286.3 MB, and the managed heap is 79 MB,
10.3 MB a session.

## Alternatives considered

- **One Shumway kernel per page.** Simplest, but it breaks as soon as a user
  opens two notebooks. Rejected.
- **A runtime per kernel in Web Workers.** Rejected: ADR-042 records that a
  worker-hosted runtime does not boot, and each one would cost a full
  runtime's memory.
- **Silently evicting idle sessions at the limit.** Rejected because of
  invisible state loss.

## Related

ADR-042 §3 and §7, ADR-067, ADR-070, ADR-071.
