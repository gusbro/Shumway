# ADR-043: .NET Framework 4.8 target (32-bit legacy hosts)

## Status

Accepted (2026-08-08). SHIPPED on branch `netfx-target`: the embedding path,
the Tier-1 IL runtime, the persisted-IL emitter and the compile/link toolchain
all multi-target `net10.0;net48`, verified in genuinely 32-bit processes.
The user-facing guide is [`docs/guide/net-framework-hosts.md`](../../guide/net-framework-hosts.md).

## Context

Legacy C# applications — WinForms/WPF/ASP.NET apps that cannot move off
.NET Framework, many of them 32-bit — are exactly the hosts that embed a
rules engine. Shumway targeted .NET 10 only. The question this arc answered:
can the same codebase serve a `net48`, x86 host, **including persisted
Tier-1 IL** (the user's explicit requirement — the linker's whole-program
optimizations must reach the Framework host, not just Tier-0 bytecode)?

Answer: yes, with no engine redesign. The `Cell` (one `long`, 4-bit tag)
is architecture-neutral; the heap, trails, unification and the interpreter
run unmodified. Everything else is packaging.

## Decisions

### 1. Multi-target set — opt-in, net10-only by default

The net48 flavor exists ONLY under `-p:ShumwayNetFx=true`
(`Directory.Build.props` resolves `$(ShumwayTargetFrameworks)` to `net10.0`
or `net10.0;net48`). A default build is single-target: same outputs, same
`dotnet run`/`dotnet test` behavior as before the arc, nothing extra to
restore on Linux/macOS. With the switch on, COMPILING net48 works on any OS
(NuGet reference assemblies — no Framework install); only RUNNING net48
binaries needs Windows. The CI matrix and
`tests/test-embedding-parallel.ps1 -Framework net48` pass the switch
themselves.

`net10.0;net48` (under the switch) on: Core, Builtins, Compiler, Interpreter,
Compiler.Il, Embedding, TopLevel, shumway-compile, shumway-link, and five test
projects (Core, Interpreter, IsoConformance, Compiler, Embedding). NOT
multi-targeted:
the REPL (line editor, AOT publish target), Shumway.Web (browser), the VS/DAP
debugger frontends' host processes (the engine-side debug core IS in the
net48 build — the DAP server runs on net48), and the emitters' publish path
(below). `Directory.Build.props` keeps the singular `TargetFramework` default;
a multi-targeted csproj CLEARS it and sets the plural (singular wins over
plural, so the clear is load-bearing).

### 2. Polyfills: one carrier + net48-only InternalsVisibleTo

All Framework polyfills live in ONE file (`src/Shared/NetFrameworkPolyfills.cs`)
compiled ONLY into Shumway.Core, which grants `InternalsVisibleTo` to the
sibling assemblies **conditioned on net48** so the net10 encapsulation is
untouched. The usual per-assembly-copy polyfill model is IMPOSSIBLE here: two
copies visible through the existing IVT graph make every extension lookup and
predefined-type lookup ambiguous (CS0121 / CS8356 — errors NoWarn cannot
reach).

The heavy lifting is **C# 14 static extension members** (`extension(Type)`
blocks in an imported namespace): `ArgumentNullException.ThrowIfNull` (176
call sites untouched), `int.TryParse(span)`, `Environment.TickCount64`,
`Convert.ToHexString`, `SHA256.HashData`, `Math.Clamp`, `Encoding.Latin1`,
`OperatingSystem.Is*`, `Array.Fill`, `ConditionalWeakTable.AddOrUpdate`,
`NativeLibrary` (real kernel32 P/Invoke), string/collection/LINQ overloads,
`HashCode`, `Index`/`Range`, compiler-service attributes. Trimmer attributes
polyfill as inert (no trimmer on Framework).

Two genuine runtime gaps have no polyfill: **default interface
implementations** (CS8701 — `IDebugSession` declares the members bodiless
under `#if NETFRAMEWORK`; implementers supply explicit no-ops) and
**`IReadOnlySet<T>`** (BCL types cannot retroactively implement an interface —
24 sites softened to `ISet<T>`, an accepted contract widening).

### 3. Deliberate scope cuts (each stated where it bites)

- **ExecutableEmitter / LibraryEmitter excluded from net48**: they shell out
  to `dotnet publish` — a .NET 10 SDK affair. The net48 `shumway-link`
  rejects `--exe` / `--dll` with a clear error. Goal parsing
  (`TryCollectGoalRefs`) is linker logic and moved to a both-TFM partial file.
- **Brotli**: no codec on net48 (same as browser-wasm). Writes are always
  plain; reading a compressed bundle throws naming `--no-compress`.
- **REPL/toolchain cross-process tests** (13 files) excluded from the net48
  test flavor: they spawn the net10 CLIs and REPL.

### 4. Tier-1 and persisted IL

Sigil (net461) emits `DynamicMethod` on Framework's JIT unchanged — runtime
promotion worked on first try, x86 and x64. For persisted IL,
`PersistedIlBuilder.Build` uses **Framework's `AssemblyBuilder` in Save
mode** — the API `PersistedAssemblyBuilder` was designed to mirror — under
`#if NETFRAMEWORK`; everything below the assembly shell (TypeBuilder, Sigil
`BuildMethod`, patch sentinels, the PE scan) is shared code. Save is
disk-only there, so a temp dir round-trips the bytes.

**The deployment matrix** (settled empirically):

| linked by | IL core refs | binds on net10 | binds on net48 |
|---|---|---|---|
| net10 toolchain | System.Private.CoreLib | yes | **no** (falls back to bytecode, with a warning) |
| net48 toolchain | mscorlib | **yes** (compat shims redirect) | yes |

So: link with the net48 toolchain when a Framework host is among the
targets — the result serves both runtimes. The reverse (teaching the net10
toolchain to emit mscorlib refs) was probed and rejected:
`PersistedAssemblyBuilder` does not normalize runtime `typeof()`s to a
`MetadataLoadContext` coreAssembly (the image keeps System.Private.CoreLib
refs alongside the mscorlib ones), and Framework's binder rejects an
`AssemblyResolve` answer whose identity differs (no cross-name redirect).
A host that cannot bind a bundle's persisted IL now **warns** through
`PrologEngine.Warnings` instead of silently serving bytecode.

### 5. 32-bit facts (measured)

- Single-query list growth knees: plain x86 (2 GB VA) between 3M and 4M
  elements; x86 `LARGEADDRESSAWARE` (4 GB) between 5M and 10M; x64 net48
  dies at 10M on the Framework's **2 GB per-array cap** (the control stack),
  NOT address space — `gcAllowVeryLargeObjects` lifts it to net10 parity.
- `t_reftype` keeps the declared offsets on x86 (MSVC aligns int64/double
  at 8): pars is a 4-byte pointer at +16, sizeof stays 32. The managed side
  was already pointer-size-agnostic (`ReadIntPtr`/`WriteIntPtr`,
  `IntPtr.Size` strides). Verified with an offsetof probe and the real-DLL
  test family compiled x86 (116/116 in the 32-bit testhost).
- Cross-thread `long` state (FunctorTable et al.) uses `Volatile`
  `Read/Write(Int64)`, which is atomic by contract on 32-bit too.

### 6. Testing discipline

- An AnyCPU net48 test run executes in an **x64** testhost; "testing 32-bit"
  requires `RunConfiguration.TargetPlatform=x86` explicitly. The suites run
  on BOTH bitnesses (`tests/test-embedding-parallel.ps1 -Framework net48
  -Platform x86|x64`).
- net48 test projects need `AutoGenerateBindingRedirects` +
  `GenerateBindingRedirectsOutputType`, plus a pinned
  `System.Runtime.CompilerServices.Unsafe` (the test platform binds 6.0.3.0;
  the transitive restore gave 6.0.1.0; strict Framework binding then kills
  every test in FileLoadException).
- Two build traps, recorded in the smoke csproj: a **global**
  `-p:PlatformTarget` flows into referenced projects and stamps the DLLs
  x86/x64-required (BadImageFormatException in OTHER consumers' incremental
  builds) — bitness must ride a private property; and `obj\` splits by
  `Platform`, not `PlatformTarget`, so switching bitness needs a rebuild.
- The real-DLL native tests self-skip without a C compiler on PATH — they
  had silently skipped in every historical gate. A native-interop CI lane
  needs vcvars (msvc-dev-cmd) armed.

### 7. Strong naming: no

Shumway assemblies stay unsigned. Framework's loader resolves unsigned
assemblies by simple name (no binding-redirect ceremony for consumers), and
none of our dependencies force signing. A consumer that requires
strong-named references can not use the net48 build as-is; revisit only if
a real host demands it.

## Consequences

- One codebase, no forks. The net10 build is byte-for-byte unaffected
  (`#if NETFRAMEWORK` everywhere; the net48-only IVT and packages are
  conditioned on the TFM).
- The zero-warning invariant holds across both TFMs.
- Merge gate for this branch: the Embedding suite green on net10 (3844),
  net48/x86 (3726) and net48/x64 (3726); Core/Interpreter/ISO/Compiler
  green on all three flavors; the CI workflow runs the same matrix.
