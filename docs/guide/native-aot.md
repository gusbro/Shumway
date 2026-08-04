# Native AOT

Shumway can be published as a self-contained **Native AOT** executable —
a single native binary with no .NET runtime dependency and no JIT.

## What runs under AOT

- **Tier-0 (the bytecode interpreter) is AOT-compatible** and runs the
  full engine: the compiler, builtins, the prelude, CLP(FD) and CLP(R).
- **Tier-1 (IL promotion) is not** — it is runtime code generation
  (`System.Reflection.Emit` / Sigil), which Native AOT does not support
  by design. Under AOT the engine cleanly stays on Tier-0:
  - `IlPromotionStore` checks `RuntimeFeature.IsDynamicCodeSupported`
    and, when false, never compiles and never even constructs the IL
    compiler (so its reflection-heavy type initialiser is never reached).
  - `PrologEngine.LoadBundle` skips a persisted-IL blob under AOT and
    uses the bundle entry's bytecode instead.

  Tier-1 is an opt-in performance tier (`engine.IlPromotion.Threshold`);
  the interpreter answers every query correctly without it.

## Publishing

The REPL project (`src/Shumway.Repl/`) is the AOT publish target. AOT is
**opt-in at publish time** — the `.csproj` does not set `<PublishAot>`, so an
ordinary build stays a normal managed binary; pass `-p:PublishAot=true` to
`dotnet publish` to produce the native `shumway` executable:

```
dotnet publish src/Shumway.Repl/ -r win-x64 -c Release -p:PublishAot=true
```

The output is `src/Shumway.Repl/bin/Release/net10.0/win-x64/publish/shumway.exe`
— a self-contained native binary (~2.5 MB).

## Windows native-link requirement

Native AOT compiles managed code to native object code (ILC) and then
**links it with the platform C/C++ linker**. On Windows that linker is
MSVC `link.exe`, which comes with the **Visual C++ build tools**
("Desktop development with C++" — either Visual Studio or the standalone
Build Tools).

The publish locates the C++ toolchain by running `vswhere.exe`. If
`vswhere.exe` is not on `PATH`, the link step fails with, e.g.:

```
'vswhere.exe' is not recognized as an internal or external command
error MSB3073: ... link.exe ... exited with code 123
```

`vswhere.exe` ships at:

```
C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe
```

Two reliable ways to make the link step succeed:

1. Run `dotnet publish` from a **Developer Command Prompt / Developer
   PowerShell for VS** — these put the toolchain on `PATH`.
2. Prepend the VS Installer directory to `PATH` for the publish, e.g. in
   PowerShell:

   ```
   $env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
   dotnet publish src/Shumway.Repl/ -r win-x64 -c Release
   ```

The ILC (managed → native) step itself has no such dependency; only the
final native link does. On Linux/macOS the system `clang`/`ld` toolchain
is used and no equivalent setup is needed.

## Debugging under AOT

The VS Code (DAP) debugging endpoint survives an AOT publish: the DAP layer
is deliberately reflection-free (hand-rolled JSON over
`JsonDocument`/`Utf8JsonWriter` — see `Debugging/Dap/DapWire.cs`), so an
AOT-published binary built from debug-compiled sources still serves
`--dap` / `SHUMWAY_DAP_PORT`. The Visual Studio (Concord) frontend also
works — its channel is plain pinned memory — with the usual AOT caveat that
everything runs Tier-0.
