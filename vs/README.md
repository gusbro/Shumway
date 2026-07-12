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

## Try it (experimental instance)

Build, then F5 on Shumway.Debugger.Vsix (launches `devenv /rootsuffix Exp`), or
install `Shumway.Debugger.Vsix\bin\Debug\Shumway.Debugger.vsix`. Run
`spike\SpikeDebuggee`, attach the managed debugger, Break All — the call stack
shows synthesized `[Prolog]` frames in place of the `Dispatch` frame.

Licensing: the sample plumbing here is adapted from Microsoft's
ConcordExtensibilitySamples (MIT) and informed by PTVS (Apache-2.0);
`Microsoft.VisualStudio.Debugger.Engine` / VSSDK packages are Microsoft VS SDK
reference assemblies (used only inside this opt-in folder).
