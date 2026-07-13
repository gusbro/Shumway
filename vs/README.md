# Shumway Visual Studio debugger (ADR-035) — Windows-only, opt-in

This folder holds the Visual Studio side of the Shumway source-level debugger:

- **Shumway.Debugger.Concord** — the Concord debug-engine components
  (`IDkmCallStackFilter`, expression evaluator, breakpoint/stepping runtime),
  netstandard2.0, compiled `.vsdconfigxml` → `.vsdconfig` by
  `Microsoft.VSSDK.Debugger.VSDConfigTool`.
- **Shumway.Debugger.Vsix** — VSIX packaging (`DebuggerEngineExtension` asset).
- **spike\SpikeDebuggee** — a net10.0 console app simulating the engine shape
  (persistent `Dispatch` frame, pinned channel buffer, no-inline `Notify`)
  used by the Phase D0 de-risk spike.

## Build (Windows + Visual Studio 2026 only)

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" ^
    vs\Shumway.Debugger.sln /restore /p:Configuration=Debug
```

This solution is **deliberately NOT part of `Shumway.slnx`**: the VSIX project
requires desktop MSBuild + VSSDK targets and does not build with `dotnet build`,
and none of it is needed on Linux. The engine-side debugger code (DebugService,
Break/debug_lastcall opcodes, port hooks) lives in the main solution as ordinary
cross-platform code.

The Concord components reference **no Shumway project** — they talk to the
debuggee engine via a pinned-memory channel + func-eval by name (see ADR-035).

## Dev loop (read this before touching the Exp hive)

Use `spike\deploy-exp.ps1` — build, deploy, and it **asserts a single installed
copy**. Then `spike\run-spike-check.ps1` drives VS end-to-end and prints a
per-leg PASS/FAIL table.

Three traps that cost a full session, all of which fail *silently*:

1. **Duplicate installs kill the extension.** The VSIX project's MSBuild targets
   already deploy to the Exp hive on every build. Running `VSIXInstaller.exe` on
   top of that adds a second copy under a random directory name, and VS then
   finds two copies of the same id+version and drops **both** — no error, no
   activity-log entry, the components simply never load. Only the VSIX installer
   log says it: *"The conflict cannot be resolved ... we are not adding either
   copy to the cache"* (`%TEMP%\dd_VSIXInstaller_*.log`). Let the build deploy;
   never mix in VSIXInstaller.
2. **`devenv /updateconfiguration` materializes a second (per-publisher) copy** —
   i.e. it can *create* trap 1. It is not needed after an in-place file update.
3. **A leftover `devenv.exe` locks the deployed DLL**, so the next build fails
   with VSSDK1081 and you silently test the previous binary.

Also: keep the `.ps1` files ASCII-only — Windows PowerShell 5.1 reads them as
CP1252, where a UTF-8 em-dash decodes to a right-double-quote and terminates a
string literal mid-line.

## Try it (experimental instance)

Build, then F5 on Shumway.Debugger.Vsix (launches `devenv /rootsuffix Exp`), or
install `Shumway.Debugger.Vsix\bin\Debug\Shumway.Debugger.vsix`.

### D2 smoke — the real engine

1. Start the REPL with a debug session open:

   ```
   src\Shumway.Repl\bin\Debug\net10.0\shumway.exe --debug yourfile.pl
   ```

   `--debug` compiles in debug mode (named variables kept, frames intact, LCO off)
   and opens a `ChannelDebugSession`. It prints its pid. (`--debug-wait` also holds
   the process until a debugger attaches — that is what D4's F5 will use.)

2. In the Exp instance: **Debug → Attach to Process**, pick `shumway.exe`, attach
   with the **Managed (.NET Core)** code type.

3. Run a query that takes a moment (`?- between(1, 20000000, _), fail.`), then hit
   **Break All**.

Expect: the call stack shows one frame per Prolog goal on the environment chain —
`p/1`, `main/0` — where the CLR would have shown a single
`BytecodeInterpreter.Dispatch`. The engine's own frames are gone; anything else on
the stack (your C# embedder, a `[PrologPredicate]` bridge, a native frame under a
P/Invoke) is still there, which is what makes the stack *mixed*. Double-clicking a
Prolog frame opens the `.pl` at the right line; **Locals** shows that clause's
variables with their terms rendered by the engine.

If a Prolog frame is grey and not navigable, its `.pl` has no module yet: the
server creates them at a process pause, so it becomes navigable at the next stop.
Stopping *at* a Prolog breakpoint is D3 — a port stop currently resumes.

### D0 spike (superseded, kept for re-running the legs)

Run `spike\SpikeDebuggee`, attach, Break All — the call stack shows synthesized
`[Prolog]` frames in place of the `Dispatch` frame. `spike\run-spike-check.ps1`
drives it end-to-end. The spike's components have since been rewritten to talk to
the real engine, so the spike debuggee no longer exercises them.

Licensing: the sample plumbing here is adapted from Microsoft's
ConcordExtensibilitySamples (MIT) and informed by PTVS (Apache-2.0);
`Microsoft.VisualStudio.Debugger.Engine` / VSSDK packages are Microsoft VS SDK
reference assemblies (used only inside this opt-in folder).
