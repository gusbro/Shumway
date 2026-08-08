# Shumway on .NET Framework 4.8 (32-bit legacy hosts)

Shumway multi-targets `net10.0;net48`, so a legacy C# application — WinForms,
WPF, ASP.NET on .NET Framework, 32-bit or 64-bit — can embed the engine
without leaving its runtime. The engine core, Tier-1 IL promotion and
persisted-IL bundles all work on Framework; this page collects what a
Framework host needs to know. Design rationale: [ADR-043](../architecture/adr/043-net-framework-target.md).

## What works, what does not

| Area | net48 |
|---|---|
| Engine (Tier-0), all builtins, CLP(FD)/CLP(R), tabling, debugger DAP server | ✅ unchanged |
| Tier-1 runtime IL promotion (`engine.IlPromotion.Threshold = N`) | ✅ unchanged |
| Loading `.shum` bundles, including persisted IL | ✅ link them with the **net48 toolchain** (below) |
| Producing bundles in-process (`BundleWriter`, `SaveState`) | ✅ (always uncompressed) |
| `shumway-compile` / `shumway-link` (net48 builds) | ✅ except `--exe` / `--dll` |
| `--exe` / `--dll`, Native AOT, Brotli-compressed bundles | ❌ .NET 10 toolchain only |

## Bundles: which toolchain links for which host

Persisted IL is emitted against the *linking* runtime's core library:

- Linked by the **net10** toolchain → IL references `System.Private.CoreLib`.
  Loads on net10; a net48 host **cannot bind it** — it falls back to the
  bundle's bytecode (correct answers, no persisted tier) and warns through
  `PrologEngine.Warnings`.
- Linked by the **net48** toolchain → IL references `mscorlib`. Binds on
  **both** runtimes (.NET redirects mscorlib references via its standard
  compat shims).

Rule of thumb: **if a Framework host is among your targets, link with the
net48 build of `shumway-link`** — one bundle serves everything. Remember
`--no-compress` if a net48 process must also *read* the bundle.

## Host configuration (app.config)

A Framework host wants two things in its `app.config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <runtime>
    <!-- .NET Framework caps any single array at 2 GB. A 64-bit host running
         heap-hungry queries hits this in the engine's stack growth long
         before real memory runs out. Irrelevant on x86 (address space binds
         first). -->
    <gcAllowVeryLargeObjects enabled="true" />

    <!-- Unify the versions of the compatibility packages Shumway's net48
         build depends on. SDK-style projects generate these automatically
         (AutoGenerateBindingRedirects); old-style csproj consumers need
         them spelled out. Versions are the ones your restore resolves —
         these are typical: -->
    <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
      <dependentAssembly>
        <assemblyIdentity name="System.Runtime.CompilerServices.Unsafe"
                          publicKeyToken="b03f5f7f11d50a3a" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-6.0.3.0" newVersion="6.0.3.0" />
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="System.Memory"
                          publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-4.0.2.0" newVersion="4.0.2.0" />
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="System.Buffers"
                          publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-4.0.4.0" newVersion="4.0.4.0" />
      </dependentAssembly>
    </assemblyBinding>
  </runtime>
</configuration>
```

Shumway's own assemblies are **unsigned** — Framework resolves them by simple
name, no redirects needed for them.

## 32-bit memory expectations

Measured single-query limits (a list built in one query; the engine heap and
control stack are contiguous arrays that double on growth, so the transient
peak is ~1.5× the steady size):

| Process | Practical knee |
|---|---|
| x86, default (2 GB address space) | between 3M and 4M list elements |
| x86 `LARGEADDRESSAWARE` (4 GB on 64-bit Windows) | between 5M and 10M |
| x64 with `gcAllowVeryLargeObjects` | matches .NET 10 (20M+ verified) |

For a 32-bit host that pushes these sizes, prefer AnyCPU +
`<Prefer32Bit>true</Prefer32Bit>` over `PlatformTarget=x86`: same 32-bit
execution, but the exe is `LARGEADDRESSAWARE` and gets 4 GB on a 64-bit OS.

## Native C interop on x86

The `t_reftype` declaration in
[generic-term-interop §10](generic-term-interop.md) serves 32-bit builds
unchanged: MSVC x86 aligns the `int64_t`/`double` members at 8, so the field
offsets (0/8/16/24) and `sizeof` (32) are identical — `pars` is simply a
4-byte pointer at +16. Compile your native library for the **same bitness as
the host process** and nothing else changes.

## Testing your integration

If you multi-target your own test project, two Framework-specific rules:

- The testhost picks its own bitness: an AnyCPU run executes **x64**. To test
  what a 32-bit host will run, pass `-- RunConfiguration.TargetPlatform=x86`
  to `dotnet test`.
- Add `<AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>` and
  `<GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>`
  to the test csproj, or every test dies in `FileLoadException` on the first
  compatibility-package version skew.
