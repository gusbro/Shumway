# Phase 39 — the .NET Framework 4.8 target + the debugger round it drove

**Closed 2026-08-11**, tagged `phase-39`. 32 commits on branch `netfx-target`,
101 files, +3128/−430, squash-merged to main as one commit (`0aa6729`, PR #1);
the branch is kept on origin as the detailed history.

The phase's question: can the same codebase serve a **.NET Framework 4.8, 32-bit
legacy host — including persisted Tier-1 IL**? The answer is yes, opt-in, with
no engine redesign; and the manual testing of the result drove a full Visual
Studio debugger round on top.

The design record is [ADR-043](../architecture/adr/043-net-framework-target.md);
the user guide is [`guide/net-framework-hosts.md`](../guide/net-framework-hosts.md).

---

## The net48 target (milestones 1–8)

**Opt-in by construction.** A default build is net10.0-only — identical outputs,
`dotnet run` without `-f`, one test pass, nothing Framework-related restored on
Linux. `-p:ShumwayNetFx=true` resolves `$(ShumwayTargetFrameworks)` to
`net10.0;net48` across fourteen projects (engine, toolchain, five test suites,
TopLevel, REPL). Compiling net48 needs no Framework install (NuGet reference
assemblies — works on any OS); only *running* net48 binaries needs Windows.

**One polyfill carrier.** All Framework polyfills live in a single file compiled
only into Core, which grants net48-only `InternalsVisibleTo` to the siblings —
the usual per-assembly-copy model is impossible against the existing IVT graph
(two visible copies make every lookup ambiguous: CS0121/CS8356, unreachable by
NoWarn). C# 14 **static extension members** carry the BCL gaps
(`ThrowIfNull` — 176 call sites untouched — `TickCount64`, `Math.Clamp`,
`SHA256.HashData`, `NativeLibrary` over kernel32, …). The only true runtime
gaps: default interface implementations (`#if` bodiless declarations +
explicit no-ops) and `IReadOnlySet` (softened to `ISet` at 24 sites).

**Tier-1, both flavors.** Sigil's `DynamicMethod` emission ran on Framework's
JIT — x86 and x64 — without a single change. Persisted IL is emitted *natively*
on net48 through `AssemblyBuilder` in Save mode (the API
`PersistedAssemblyBuilder` was designed to mirror): the whole emit below the
assembly shell — Sigil `BuildMethod`, patch sentinels, the PE scan — is shared
code, and the 143 persisted-IL tests went green with zero test edits. The
**deployment matrix** was settled empirically: a bundle linked by the net48
toolchain carries mscorlib-referencing IL that binds on *both* runtimes;
net10-linked IL falls back to bytecode on net48, with a warning (the reverse
cross-emit was probed and rejected — `PersistedAssemblyBuilder` does not
normalize runtime `typeof()`s to a `MetadataLoadContext` core assembly).
`--exe`/`--dll` from the net48 toolchain emit Framework **folder apps**; `--dll`
ships an `app.config` sample computed from the deployed assemblies (a consumer's
auto-generated binding redirects can land *below* the deployed versions and
override a correct hand-written config).

**32-bit facts, measured.** Memory knees (plain x86 3–4M list elements; LAA
5–10M; x64 needs `gcAllowVeryLargeObjects` past the Framework's 2 GB-per-array
cap). The `t_reftype` offsets hold on x86 (MSVC aligns int64/double at 8;
verified by an offsetof probe) and the managed side was already
pointer-size-agnostic. Real-DLL native interop 116/116 per bitness — those
tests had silently self-skipped in every historical gate (no `cl` on the bare
PATH); the CI arms MSVC per lane so they actually run.

**CI.** Three Windows lanes (net10, net48/x86, net48/x64). Two environment
traps found on the first runs: vcvars exports `Platform=<arch>` and MSBuild
stamps every assembly with it (an x86-required *analyzer* cannot load into the
x64 Roslyn compiler — CS8034) — neutralized per lane; and a failed test's
message lives in the parallel script's per-bucket logs, which die with the
runner — uploaded as artifacts on failure.

## The debugger round (VSIX 0.28 → 0.30)

Driven live by the user exercising the net48 REPL under Visual Studio:

- **Unified Settings** (VS 2026's scheme): the VSIX became the documented
  `VSSDK+VisualStudio.Extensibility` hybrid; the old `DialogPage` is gone.
  Three walls, each now recorded: the 17.14 SDK's generator emits the
  pre-VS2026 `"format": "filePath"` token and the VS 18 settings UI silently
  *drops* the property (a build-time patch rewrites it to
  `"format": "path" + "pathKind": "file"` — the Terminal's own schema); the
  `VisualStudioExtensibility` service is not proffered to VSSDK packages
  (values are read from the instance's `settings.json` store, located via
  `VSSPROPID_LocalAppDataDir`); and a silent catch had hidden the whole
  read-path break behind a misleading error.
- **Launch by runtime**: "Debug Prolog File" hands the process to the CoreCLR
  or the classic v4 managed engine according to the target exe (a
  `runtimeconfig.json` sibling decides) — a net48 engine under the CoreCLR
  engine never attaches. The command also reached the Solution Explorer item
  and document-tab context menus.
- **Immediate window**: answers show residual constraints and per-solution
  bindings under the user's names (`label([A])` answers `A = 6`; `;` walks the
  labeling); a leading **`!`** runs the goal *on the suspended frame* with
  Prolog's own trail as the transaction — the user's observation that
  `!(G, fail)` is the free dry-run collapsed the design to one gesture.

## Engine fixes the round surfaced

- The nested residual capture cleared the Immediate goal's transplant source,
  so a native-clpfd attribute reattached as its `'$foreign'(N)` marker and the
  first post exploded ("Cell tag is Str, expected Foreign") — save/restore
  discipline in both nested brackets; the covering test had been green through
  it on weak asserts, now hardened.
- `CallIl`/`ExecuteIl` threw on a per-query IL-table snapshot that predates a
  mid-stop promotion (the REPL arms promotion under `--debug` too; semi-native
  library code is promotable by design) — both now fall back to the engine-wide
  dispatcher, the pattern the resume-marker site already used.
- `consult/1` retries an extensionless file as `.pl` (SWI-style), one resolver
  serving the builtin, `reconsult/1`, the embedding API and the REPL.

## Documentation truth passes

The `t_reftype` layout is **Shumway's own contract** (the Arity material holds
no such declaration; native C interops by recompiling against ours — same
offsets on x86). The reftype marshalling reframed as what it is: a thin
**convenience layer**, in-process, decoupling the "C world" from engine
internals — which is exactly why the same interface is implementable over
Shumway. Example identifiers neutralized throughout; the README cover trimmed.

## Gate at close

Local full gate (Slow included): Embedding 3851 / Core 444 / Interpreter 105 /
Compiler 364 / ISO 298 / DialectInterop 9. CI green on main's HEAD across
net10, net48/x86 and net48/x64 (the one x86 wobble was a runner-image flake:
two failures of a single real-DLL test, then green on the same tree — the
artifact upload now captures the detail if it recurs).
