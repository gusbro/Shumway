# Debugging Shumway Prolog in Visual Studio Code (ADR-036)

Cross-platform (Windows and Linux) source-level debugging in VS Code: breakpoints —
including conditional ones whose condition is a Prolog goal — port-based stepping, the
call stack, and per-frame variables. The engine hosts a DAP endpoint in-process; the
`shumway-dap` adapter and a purely declarative extension connect VS Code to it. The
Visual Studio (Concord) debugger of `docs/debugger.md` is unaffected — both endpoints
are available in any debug build, first connected debugger drives.

## One-time setup

```
# Windows
powershell -ExecutionPolicy Bypass -File vscode\install-extension.ps1
# Linux / macOS
sh vscode/install-extension.sh
```

This publishes `shumway-dap`, stages it inside the extension, packages a `.vsix`, and
installs it with the `code` CLI. Restart VS Code afterwards.

## Launch (F5)

Open your `.pl` file and press F5 (pick **Shumway Prolog** the first time), or use a
`launch.json`:

```json
{
  "type": "shumway",
  "request": "launch",
  "name": "Debug Prolog file",
  "program": "${file}",
  "shumwayPath": "C:/path/to/shumway.exe"
}
```

VS Code starts `shumway --dap <port> <program>` in the **integrated terminal** — the
REPL prompt is yours. Set breakpoints in the editor, type a query in the terminal, and
execution stops in VS Code. Optional config: `goal` (run a goal after consulting, like
`-g`), `args` (extra shumway arguments — more files, `--clpfd`, `--foreign-dll` ...),
`port` (fix the DAP port), `cwd`.

## Attach

Start any debug-built Shumway with a DAP port:

```
shumway --dap 4711 program.pl          # the REPL (implies --debug)
SHUMWAY_DAP_PORT=4711 ./app            # a linked --exe --debug, or any embedded host
shumway-link ... --exe app --debug --dap-port 4711   # or bake the port at link time
```

(`--dap 0` picks a free port and prints it. `SHUMWAY_DAP_PORT` opens the endpoint in
ANY deployment shape whose debug session opens — no code or link change — and at run
time it overrides a baked `--dap-port` (0 disables).) Then:

```json
{ "type": "shumway", "request": "attach", "name": "Attach", "port": 4711 }
```

Embedded hosts can also set it in code: `engine.EnableDebugging(new DebugOptions {
DapPort = 4711 })`, reading `session.DapPort` back when they pass 0.

## What works (V1–V4)

Breakpoints (add/remove live, conditions as Prolog goals evaluated in the frame),
continue/step over/step into/step out (port-based; redo/fail stops are annotated in the
stop description), pause (stops at the next port, where a stack means something), call
stack with clause heads, per-frame variables (writeq-rendered), disconnect = run free +
reconnect later.

**Debug Console** (V3) — the Immediate window: goals run in the live suspended engine
(side effects persist), `;` asks for the next solution, `X = term(1)` on a free frame
variable commits the binding into the frame (Locals refresh at once), and a bare variable
name prints its value. During a console evaluation breakpoints do not stop (a nested
break state has no DAP shape). **Set Value** (V3) in the Variables panel is the
destructive edit: trailed (backtracking restores it), `_` un-instantiates, values render
writeq so they round-trip. Hover shows frame variables and refuses goals.

**Jump to Cursor** (V4) — right-click a line → *Jump to Cursor*: the ADR-035 Set Next
Statement. Forward skips the goals in between; backward rewinds the trail to the recorded
mark (bindings undone; database effects are permanent, as designed); selecting another
frame in the Call Stack first targets THAT frame (the frames above it pop). Only the
lines the engine published as valid are offered; anything else is refused honestly.

**Logpoints** (V5) — right-click a breakpoint → *Edit Breakpoint* → *Log Message* (or
add a logpoint directly): the machine pauses invisibly, the message prints to the Debug
Console with `{Var}` holes filled from the frame (writeq-rendered), and execution
continues — no stop ever reaches the editor.

## Not yet / different from Visual Studio

- **Logpoints and the packaged-marketplace polish** — V5 of ADR-036.
- **Mixed Prolog+C# stack in one view** — a DAP session shows Prolog only. For interop
  work run a compound session (this + `coreclr` attach to the same process) and switch
  stacks in the Call Stack panel.
- **Step into C# from Prolog** — behaves as step over; use a C# breakpoint via the
  compound session.
- One debugger drives at a time (VS Code vs Visual Studio vs a second VS Code): the
  second gets a clean refusal.

## Linux

Everything here is cross-platform by construction — the engine, the DAP server, the
adapter and the extension are the same bits, and the protocol suite (`Adr036*Tests`)
runs on any OS. Install with `sh vscode/install-extension.sh`. An end-to-end smoke on a
real Linux box is still pending (no box at hand); if you hit anything there,
`SHUMWAY_DEBUG_DIAG=1` on the debuggee and the adapter is the first stop.

## Troubleshooting

- `SHUMWAY_DEBUG_DIAG=1` makes both the debuggee and `shumway-dap` log diagnostics to
  stderr.
- A breakpoint that never hits: confirm the debuggee was started with `--dap` (the
  banner names the port) and the file consulted is the file the breakpoint is in
  (files are matched by name).
- "a debugger is already attached": something else owns the session — detach it (or
  disconnect the other VS Code session) and retry.
