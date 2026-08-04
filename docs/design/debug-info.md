# Debug info

How Shumway carries source-level debug information and drives the debugger. The
user-facing guides are [`debugger.md`](../guide/debugger.md) (Visual Studio) and
[`debugger-vscode.md`](../guide/debugger-vscode.md) (VS Code); ADR-035 and
ADR-036 are the design records. This page is the internal-mechanism summary.

## Compile modes

The `compile_mode` prolog flag selects what a clause carries:

- **release** (default) — no per-clause debug markers; smallest, fastest code.
- **debug** — the compiler emits source-position markers and the debug opcodes
  so a debugger can map a program counter back to a `.pl` line/column and stop
  at the Prolog ports.

Source positions ride on the `Meta` opcode (`0x62`) with the `DbgInfo`
sub-opcode; the per-predicate site data lives in `DebugSiteTable`
(`src/Shumway.Core/DebugSiteTable.cs`).

## Debug opcodes

Under `compile_mode=debug` the codegen also uses:

- **`DebugLastCall` (`0x63`)** — a last-call site made steppable (last-call
  optimization is toggleable so a tail frame can stay visible;
  `SHUMWAY_DEBUG_LCO`).
- **`Break` (`0x64`)** — an armed breakpoint. Like a gdb `INT3`, it is swapped
  in at a pc and resolved against the engine's breakpoint table, so setting a
  breakpoint does not recompile the predicate.
- **`DebugPort` (`0x65`)** — marks a Prolog port (call/exit/redo/fail) where the
  debugger can stop and report the frame.

## Runtime integration

The engine talks to a debugger through `IDebugSession`
(`src/Shumway.Core/IDebugSession.cs`): the interpreter calls
`DebugSession?.OnLeaveProlog(...)` at the relevant points, and the session
decides whether to stop. `DebugService`
(`src/Shumway.Embedding/Debugging/DebugService.cs`) is the engine-side core;
`ChannelDebugSession` and the DAP server (`Debugging/Dap/DapDebugServer.cs`) are
the two front-end transports (Visual Studio via a pinned-memory channel, VS Code
via the Debug Adapter Protocol). The wire carried over these is versioned —
`DebugWire.FormatVersion` — and the engine and the IDE extensions move together
on it.

For the port model, conditional breakpoints, live evaluation, Set Next
Statement, and the residual-constraint display, see ADR-035 / ADR-036 and the
two guides above.
