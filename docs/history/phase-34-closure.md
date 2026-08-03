# Phase 34 — Source-level debugger: Visual Studio + VS Code (ADR-035 / ADR-036)

**Status: complete** (tagged `phase-34`). The phase closes the debugger arc as a whole:
one engine-side debug core, two full IDE frontends, every deployment shape, on Windows
and (by construction) Linux.

## What shipped

### ADR-035 — the engine core + the Visual Studio frontend (Concord)

Closed earlier in its own document (see the ADR's *Final state*): port-based stepping
over Tier-0, `Break`/`debug_lastcall`/`debug_port` opcodes, per-predicate
`:- disable_debug.`, the pinned-memory channel, breakpoints with engine-evaluated
conditions, live-engine evaluation with bind-into-frame, destructive Watch edits,
Set Next Statement (forward skip / backward trail rewind / cross-frame / sibling-clause
with Prolog fall-through), control-construct transparency with a position-coverage
tripwire suite, clean detach, lazy arm-on-attach (`SHUMWAY_DEBUG_ACTIVATION=attach`)
with armed-mode caching, and func-eval working against Release-built engines (the
fully-interruptible `Notify`). VSIX 0.27, wire v7.

### ADR-036 — the VS Code frontend (DAP, cross-platform)

- **In-process DAP server** (`DapDebugServer`, TCP loopback) over the
  `ChannelDebugSession` external-driver seam: the stop blocks the engine thread on a
  semaphore where the VS transport traps a hidden breakpoint; stack/variables served
  from the pre-stop snapshot; **no func-eval anywhere** — while-stopped operations are
  direct calls from the reader thread with the engine parked.
- **Both endpoints in one `--debug` build**: the Concord channel (passive) and the DAP
  listener (`--dap`/`--dap-wait` in the REPL, `SHUMWAY_DAP_PORT` anywhere,
  `shumway-link --exe --debug --dap-port N` baked with env precedence,
  `DebugOptions.DapPort` embedded). Single-driver arbitration with honest refusals;
  disconnect = detach (run free, reconnect later).
- **Zero-JavaScript extension** (`vscode/shumway-debug`, declarative manifest + the
  VSIX's grammar) + **`shumway-dap`** C# adapter (verbatim byte forwarding;
  `runInTerminal` launch so the program keeps its console). Packaged as a real `.vsix`
  by `vscode/install-extension.{ps1,sh}`.
- **Feature surface**: breakpoints (live add/remove, conditions as Prolog goals,
  **logpoints** with `{Var}` interpolation), 4-port stepping with annotated redo/fail,
  pause-at-a-port, call stack with clause heads, writeq variables, Debug Console = the
  Immediate window (live goals, `;` pumps, bind-into-frame, bare-variable answers),
  destructive `setVariable` (`_` un-instantiates), hover with the NoSideEffects policy,
  Jump to Cursor = Set Next Statement (cross-frame via the Call Stack selection).
- **Launch race closed**: `--dap-wait` holds the prompt until `configurationDone`
  (breakpoints armed) — and root-caused a real arm-vs-consult data race in the ADR-035
  core (consult now runs under the debug-arm gate; the idle watcher takes the gates in
  the engine's order), pinned by `Adr036LaunchRaceTests`.
- **Verification without an IDE**: the whole protocol is driven by xUnit clients over
  real sockets/pipes (`Adr036*Tests`, 31 tests) inside the normal five-project gate —
  cross-platform by construction, no DTE smokes needed.

## Known limitations (documented in `docs/debugger-vscode.md`)

- One VS Code session shows the Prolog stack only (DAP has no mixed-runtime stack);
  the compound-session recipe covers interop work.
- Breakpoints do not stop during a Debug Console evaluation (a nested break state has
  no DAP shape).
- The Linux end-to-end smoke is pending a physical box; everything is cross-platform
  by construction and the protocol suite runs on any OS.

## Gate at close

Core 444 / Interpreter 105 / Compiler 351 / Embedding 3306 (3 skipped) / ISO 277 —
all green.
