# Shumway Prolog Debugger for VS Code

Debug Shumway Prolog programs in VS Code: breakpoints (including conditional ones with
Prolog goals), port-based stepping, call stack, and per-frame variables — on Windows and
Linux. ADR-036: a purely declarative extension (no extension code) over the `shumway-dap`
adapter, which forwards to the DAP endpoint the Shumway engine hosts in-process.

## Install (development)

Run `vscode/install-extension.ps1` (Windows) or `vscode/install-extension.sh`
(Linux/macOS) from the Shumway repository, then restart VS Code.

## Launch

Open a `.pl` file, press F5 and pick **Shumway Prolog**, or add to `launch.json`:

```json
{
  "type": "shumway",
  "request": "launch",
  "name": "Debug Prolog file",
  "program": "${file}",
  "shumwayPath": "C:/path/to/shumway.exe"
}
```

The program starts as `shumway --dap <port> <program>` in the integrated terminal — the
REPL prompt is yours, and breakpoints hit as your queries run. Optional: `goal` (run a
goal after consulting, like `-g`), `args` (extra shumway arguments), `port`, `cwd`.

## Attach

Start any debug-built Shumway with a DAP port — `shumway --dap 4711 program.pl`, or any
deployment shape (a linked `--exe`, an embedded host) with `SHUMWAY_DAP_PORT=4711` — and:

```json
{
  "type": "shumway",
  "request": "attach",
  "name": "Attach to Shumway",
  "port": 4711
}
```

(For adapter-less experiments, `"debugServer": 4711` in a config connects VS Code
directly to the engine's endpoint.)

## Notes

- One debugger drives at a time: a second client — or a VS Code client while Visual
  Studio is attached — is refused with a clear message.
- Disconnecting clears breakpoints and lets the program run free; reconnect at will.
- The C# frames of interop calls belong to the .NET debugger: run a compound session
  with `coreclr` attach to see both stacks side by side.
