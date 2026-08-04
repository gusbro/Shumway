# ADR-036: VS Code debugger frontend (DAP, in-process, cross-platform)

**Status:** ACCEPTED and **IMPLEMENTED** — the arc is closed (V1–V5 delivered
and user-verified in real VS Code; the one open item is an end-to-end smoke on
a physical Linux machine, pending a box — the code and the protocol suite are
cross-platform by construction). Together with ADR-035 this completes the
source-level debugger: one engine core, two frontends (Visual Studio via
Concord, VS Code via DAP), every deployment shape.

## Context

ADR-035 delivered the source-level debugger: the engine-side core (breakpoint
table with `Break` patching, condition evaluation, 4-port stepping, port marks +
Set Next Statement, snapshot serialization, `SetFrameVariable`, lazy arming) and
a Visual Studio frontend over Concord. That ADR **rejected DAP** — for VS, where
only a Concord component can interleave synthesized frames into the mixed
Prolog+C#+native stack.

This ADR adds **Visual Studio Code** as a second frontend, with two goals:

1. **Reuse the whole engine-side debugging core unchanged.** Concord was only
   ever the *transport*; everything of substance already runs inside the
   debuggee.
2. **Cross-platform**: debugging must work on Linux (the engine, REPL and
   linked executables already do — ADR-035's core is xplat by construction; only
   the Concord/VSIX frontend is Windows-only).

In VS Code the debug protocol is **DAP** (Debug Adapter Protocol) — there is no
Concord equivalent, and no memory-level access to the debuggee. That constraint
turns out to *simplify* the design rather than fight it.

## Decision

### Architecture: in-process DAP server over the existing session seam

```
┌─ debuggee: shumway / app.exe (win/linux) ────────┐     ┌─ VS Code ────────────────┐
│ DebugService (UNCHANGED: bps, conditions, ports, │     │ Shumway extension        │
│  marks, SNS, snapshot, SetFrameVariable)         │     │  (ZERO extension code):  │
│ DapDebugSession (NEW, sibling of                 │◄───►│  - declarative debugger  │
│  ChannelDebugSession):                           │ DAP │    contribution          │
│  - _notify = BLOCK on a semaphore until resume   │ TCP │  - .pl TextMate grammar  │
│  - DAP listener thread (JSON, System.Text.Json   │loop-│    (reused from the VSIX)│
│    source-generated — AOT-safe)                  │back │  - launch/attach config  │
│  - serves stack/variables from the DebugStopEvent│     │    snippets              │
│    objects directly (no byte serialization)      │     │ shumway-dap (C# adapter) │
└──────────────────────────────────────────────────┘     └──────────────────────────┘
```

- **`DapDebugSession`** is a sibling of `ChannelDebugSession` over the same
  `DebugService`. The stop path reuses the proven seam (`OnStopLocked`: write →
  notify → drain): here `_notify` **blocks the engine thread on a semaphore**
  until the DAP client resumes, instead of trapping into a hidden breakpoint.
  The test-notify session constructor already proved this shape.
- **No func-eval, anywhere.** Evaluating a goal / editing a variable while
  stopped is a direct call from the DAP listener thread with the engine thread
  parked — legal under the threading model (activations are thread-agile; access
  is serialized because the engine thread is blocked). The entire GC-safe-point
  / IL-interpreter saga of the VS frontend does not exist here, on any build
  config, on any OS.
- **No byte serialization for reads**: the DAP thread consumes the
  `DebugStopEvent` / frame objects in memory. Wire v7 stays untouched, for VS.

### Both endpoints available in one build

A `--debug` build exposes **both** frontends; no rebuild to switch IDE:

- **VS/Concord channel**: passive (published fields + pinned buffer), always on
  under `--debug` exactly as today; costs nothing until a debugger attaches.
- **DAP endpoint**: a TCP listener on `127.0.0.1` only, active when a port is
  configured — baked at link time (`shumway-link --exe --debug --dap-port N`),
  set per run (`SHUMWAY_DAP_PORT=N`, which overrides the baked value; `=0`
  disables), or via `DebugOptions.DapPort` for embedded hosts. REPL:
  `shumway --debug --dap <port>` (`--dap-wait` = stop on entry).

**Session arbitration**: both endpoints *listen* concurrently, but only one
debugger *drives* at a time — two masters stepping a single-threaded machine is
guaranteed incoherence. First attach/connect owns the session; the second is
refused cleanly ("a debugger is already attached: <which>"). The lazy model
(ADR-035 `ActivateOnAttach`) applies to both: full debug arms on VS attach OR on
DAP connect, and disarms on detach/disconnect (`DisarmAfterDetach` reused —
socket disconnect maps to it naturally, so a killed VS Code never leaves the
debuggee stopping into the void).

### Extension: zero JavaScript; adapter in C#

The VS Code extension is **purely declarative** — `package.json` (debugger type
`shumway`, launch/attach configuration attributes and snippets, breakpoint
enablement for `.pl`) plus the TextMate grammar already shipped in the VS VSIX.
No TypeScript, no npm toolchain in the repo. The `.vsix` is a zip built by a
repo script (`vsce` would be needed only for a future Marketplace publish — a
publish-time tool, not a dependency).

The **debug adapter is C#**: `shumway-dap`, a fifth small CLI (~300–500 lines).
VS Code launches it and speaks DAP over stdio (the declarative `program` field);
it forwards to the debuggee's DAP TCP socket. It is protocol glue on the IDE
side — never the debuggee:

- **launch**: sends the DAP `runInTerminal` reverse request so VS Code starts
  the debuggee (`shumway --debug ... file.pl`, or a linked `app.exe`) in the
  integrated terminal — the Prolog program keeps its own console, stdio is not
  stolen — then connects to the socket.
- **attach**: connects to an already-running process's socket. (For attach, the
  adapter is even optional: `"debugServer": <port>` in `launch.json` connects
  VS Code directly to the in-process server.)

### DAP implementation: hand-rolled, AOT-safe

DAP is JSON messages behind `Content-Length` headers — ~20 message types for our
surface. Implemented by hand in `Shumway.Embedding.Debugging.Dap` with
**source-generated `System.Text.Json`**: no external dependency (Microsoft's DAP
NuGet is unnecessary and its license would need vetting), and the server
survives the REPL's Native AOT publish.

### Alternatives rejected

- **TypeScript extension** (the ecosystem default). The extension host runs JS,
  but a *debugger* extension needs no extension code at all: the declarative
  contribution + an adapter executable cover launch, attach, breakpoints and
  grammar. Rejected to keep the repo single-language and toolchain-free. Costs
  accepted: no dynamic "debug current file without launch.json" (mitigated by
  configuration snippets); a future LSP *client* would need a small JS shim —
  the one door left closed, and a small, isolated one.
- **Separate adapter process speaking wire v7** (adapter translates DAP ↔ the
  VS pinned-memory wire). Duplicates the protocol surface for zero gain — the
  wire exists *because* Concord reads memory; a socket peer can speak DAP
  directly. Rejected.
- **Concord components under vsdbg in VS Code.** vsdbg is closed, its
  redistribution license is restrictive, the componentry is Windows-centric,
  and the result would still not be cross-platform Prolog debugging. Rejected.

## Feature mapping (VS frontend → VS Code)

Direct (the engine already does the work): breakpoints incl. **conditions**
(DAP `condition` → the engine-side goal, evaluated before notify),
add/remove-while-running (safe-point application), 4-port stepping (redo/fail as
annotated `stopped` reasons), call stack with module names and semi-native
(`disable_debug`) frames, writeq locals, **destructive `setVariable`**, Debug
Console = Immediate (goal eval + bind-into-frame), hover with the NoSideEffects
policy, **`gotoTargets`/`goto` = Set Next Statement** ("Jump to Cursor"),
`stopOnEntry` = `--debug-wait`, lazy arm on connect, materialized bundle source
(`$TMP/shumway-debug/…` — same mechanism, xplat), **logpoints** (DAP
`logMessage`: evaluate + print without stopping — new, cheap engine-side).

**Limitations to track (with alternatives):**

1. **Mixed Prolog+C#+native stack — not possible in one VS Code session.** A
   DAP session shows only its own frames. Alternatives: (a) *compound launch*
   (this session + `coreclr` attach to the same pid) — VS Code shows both
   sessions side-by-side in the Call Stack panel, the standard polyglot answer;
   (b) render interop spans inside our Prolog stack as opaque `[C# call: …]`
   frames (the env chain has the information). Shipped: (b) in V1; (a) documented
   as a recipe.
2. **Step into C# from Prolog** — no cross-session step arbitration in DAP.
   Behaves as step-over; with the compound session, a C# breakpoint at the
   target fills the role.
3. **Cross-frame / sibling-clause SNS** — DAP `goto` carries no target frame.
   The selected frame is known from the last `stackTrace`/`scopes` requests
   (the same inference `MsgSelectedFrame` formalized in VS), which likely
   covers cross-frame; whatever does not map gets a Debug Console meta-command
   (`:sns <line>`) over `evaluate`. Settled in V4: cross-frame works, no `:sns` fallback needed.
4. **Rich SNS refusal popups** — `goto` succeeds or fails; the honest
   explanation goes to the Debug Console via `output` events (as the VS Output
   feedback already does).

## Phases

- **V1 — DAP core, engine-side (xplat, no VS Code needed).** ✅ **Done.**
  `DapDebugServer` + per-connection handler over the `ChannelDebugSession`
  external-driver seam (`NotifyOverride` blocks the engine thread on a
  semaphore; `ExternalDriverConnected` extends the idle watcher and lazy
  arming to DAP clients): initialize/launch/attach/configurationDone (the
  latter answers only once the engine consumed the breakpoint state — the DAP
  "hold the door"), setBreakpoints (+conditions), stopped/continued events
  (redo/fail annotated), continue/next/stepIn/stepOut, pause (`BreakNow`),
  threads, stackTrace/scopes/variables from the pre-stop snapshot, disconnect
  = detach (breakpoints cleared, lazy disarm, seat freed for a reconnect).
  Arbitration shipped (native debugger or earlier client wins; the loser's
  initialize fails with the reason). Verified by `Adr036DapTests` — 9 xUnit
  tests speaking framed DAP over a real socket through a client that shares no
  code with the server — plus the full ADR-035 suite green (240) over the
  seam changes. Two defects the suite caught: an inline accept loop that
  starved arbitration's refusal, and the drained-one-shot check eating a
  freshly-set resume ("next" silently became "continue").
- **V2 — VS Code extension MVP.** ✅ **Done** (pending the real-VS-Code manual
  smoke). `vscode/shumway-debug/`: declarative `package.json` (debugger type
  `shumway` with per-OS adapter program, launch/attach schemas + snippets,
  language + grammar reused from the VSIX, breakpoint enablement), README, and
  `vscode/install-extension.{ps1,sh}` (publish adapter → stage → folder-install).
  `shumway-dap` CLI (`src/Shumway.Dap/`, sixth CLI) is a thin main over
  `DapProxy` (in Embedding, unit-testable): verbatim byte-for-byte forwarding
  both ways; the adapter speaks for itself only on `initialize` (backend not
  born yet), `launch`/`attach`, and a `terminated` event if the debuggee dies;
  its private backend `initialize` rides seq ≥1,000,000 and its response is
  swallowed by that number. `launch` = pick a free port → `runInTerminal`
  reverse request (`shumway --dap <port> <program> [args] [-g goal]` in the
  integrated terminal, so the program keeps its console) → retry-connect.
  Tested by `Adr036ProxyTests` — the test plays VS Code on anonymous pipes AND
  the launched debuggee (answers `runInTerminal` by starting a real server on
  the adapter's chosen port): full breakpoint rounds through the proxy in both
  launch and attach shapes. Deployment wiring pulled forward from V5:
  `DebugOptions.DapPort` (defaults from `SHUMWAY_DAP_PORT` — any deployment
  shape grows the endpoint with no code change), `EnableDebugging` starts the
  server, `ChannelDebugSession.DapPort`/`StartDapServer`, REPL `--dap <port>`
  (implies `--debug`; 0 = ephemeral, banner prints it). `docs/debugger-vscode.md`
  is the user guide + smoke procedure.
- **V3 — Evaluation + editing.** ✅ **Done** (logpoints deferred to V5). The
  Debug Console (`evaluate` context `repl`) IS the Immediate window: the goal
  runs in the live suspended engine — side effects persist, bind-into-frame
  commits real bindings, `;` pumps the next solution — via ADR-035's session
  entry points called directly on the DAP reader thread while the engine
  thread is parked (legal under thread-agility). Nested stops are SUPPRESSED
  during a DAP evaluation (the engine's documented suppression mode): a stop
  routed to the reader thread would deadlock against the parked thread's
  gate, and a VS-style nested break state has no DAP shape anyway. A commit
  re-captures the snapshot and emits `invalidated(variables)` so Locals
  refresh mid-stop. `setVariable` = the destructive Watch edit verbatim
  (trailed rebind, `_` un-instantiates, refusals as honest errors, response
  value re-read writeq-rendered so it round-trips). Hover/watch contexts are
  NoSideEffects: frame variables only, goals refused (the DataTip lesson).
  Capabilities (`supportsSetVariable`, `supportsEvaluateForHovers`) declared
  by the server AND mirrored by the adapter's local initialize. 9 tests in
  `Adr036EvalTests` (incl. the nested-breakpoint no-deadlock round and the
  writeq round-trip).
- **V4 — SNS / Jump to Cursor.** ✅ **Done.** `gotoTargets` answers from the
  per-frame `SetNextLines` the engine already publishes per stop (empty = the
  editor greys the action); `goto` calls the session's Set Next Statement
  directly on the reader thread (the V3 threading argument) — the session
  bracket re-captures the snapshot, so the `stopped(goto)` event makes the
  client re-read a stack already standing on the new line; the machine
  consumes the redirect on resume. **Cross-frame works**: the target frame is
  the Call Stack selection, inferred from the last `scopes` request (the DAP
  twin of `MsgSelectedFrame`) — no `:sns` fallback needed. Sibling-clause
  head targets ride the same published lines. Refusals (engine's honest
  message, stale target ids) are error responses. 5 tests in
  `Adr036GotoTests`: targets valid/invalid, forward skip (only `c` logged),
  backward rewind (`b` re-runs; a breakpoint on the departed line correctly
  re-fires), cross-frame via selection (inner popped, `in2` skipped), stale
  target refusal.
- **V5 — Deployment + docs.** ✅ **Done except the Linux E2E smoke** (no Linux
  box at hand; everything is cross-platform by construction and the protocol
  suite runs on any OS — the smoke stays on the list for when one exists).
  **Logpoints**: a breakpoint with a `logMessage` stops the machine but never
  the user — the server answers the hit with an `output` event (`{Var}` holes
  filled from the frame, writeq) and an immediate resume; no `stopped` reaches
  the client (`supportsLogPoints`, mirrored in the adapter). **`--dap-port`**:
  `shumway-link --exe --debug --dap-port N` bakes the endpoint into the
  executable's wrapper; `SHUMWAY_DAP_PORT` at run time overrides the bake
  (0 disables) — verified on a real linked exe (baked port listens; env
  override wins). Embedding via `DebugOptions.DapPort` and the env default
  shipped in V2. Launch-race hardening shipped mid-phase: `--dap-wait` (the
  DAP twin of `--debug-wait` — the REPL holds its prompt until
  `configurationDone`; the adapter's launch uses it), which surfaced and fixed
  a REAL arm-vs-consult data race in the ADR-035 core (consult now runs under
  the debug-arm gate; the idle watcher takes the gates in the engine's order —
  see `Adr036LaunchRaceTests`). `docs/debugger-vscode.md` is the user guide,
  including the compound-session recipe and the Linux status.

## Invariants touched

- **A network surface in the debuggee** — new. Loopback-only binding, off by
  default (no port configured = no listener), optional one-time token in the
  DAP handshake. Never exposed by release builds (`--debug` only).
- **No new dependencies**: DAP hand-rolled on the BCL; extension has no code;
  no node/npm in the repo. Native AOT compatibility preserved (source-generated
  JSON).
- **ADR-035's engine core is not modified** — `DapDebugSession` is additive;
  wire v7 and the Concord frontend are untouched. The only shared-code change
  is session-ownership arbitration in `DebugService`.
- Release-mode impact: none (all of it gated exactly as ADR-035's machinery).

## Verification

- V1/V3/V4: `Adr036DapTests` in Shumway.Tests.Embedding — a test DAP client
  against the real server, cross-platform by construction, part of the standard
  gate.
- V2/V5: documented manual smoke (VS Code on Windows + Linux); optionally
  automatable later with `@vscode/test-electron` (no DTE/COM — another
  improvement over the VS arc).
