# Phase 14 — Closure

**Status**: complete.

**Tagged**: `phase-14` (this commit).

Phase 14 polished the developer experience around the
`.pl → .shmo → .shum` workflow established in Phase 13:
multi-file compile, `--debug`/`--release` flags, verbose
predicate listings, C-compiler-style error recovery, linker
strip/map/exe flags, and a real `--exe` path that produces a
single-file native executable. Plus two parser fixes (the
`prefix_op/N` ambiguity and `:- dynamic a/0, b/1` comma form)
that turned up while compiling a real-world Blint.pl.

Eight chunks:

- **Chunk 168 — `shumway-compile` multi-file + progress.**
  Multiple `.pl` inputs in one invocation. Default mode prints
  `compiling X -> Y` per file. `-o` switches to "output
  directory" when multiple inputs are supplied. Worst-case exit
  code wins.

- **Chunk 169 — `--debug` / `--release`.** Default release;
  `-d`/`--debug` enables. Mode is recorded in the new
  `ShmoObject.BuildMode` and persisted via a build-mode byte in
  the new V2 `.shmo` format (V1 still readable). Currently
  metadata-only — distinguishes release from debug in the map
  file and in `--strip`'s decisioning.

- **Chunk 170 — Verbose lists exported predicates.** In
  `--verbose` mode, each per-file footer enumerates every
  `:- public` and `:- dynamic` indicator the module exports.

- **Chunk 171 — Parser error recovery.** The fail-fast
  `CompileSource` is kept; alongside it
  `ShmoCompiler.TryCompileSource` /
  `ClauseReader.ReadAllCollectingErrors` accumulate parse and
  directive errors across one source pass, resyncing to the
  next clause-terminator dot between attempts. Stops after 100
  errors. `shumway-compile` now uses the recovery path and
  prints every diagnostic in the standard
  `file:line:col: error: msg` shape.

- **Chunk 172 — `shumway-link --strip`.** Replaces each
  `BundleEntry.Source` with the empty string before
  serialising. Useful for size analysis and IP-protection
  archives. Known limitation: the engine's `LoadBundle` still
  re-consults source to register clauses, so stripped bundles
  load but their predicates raise `existence_error/2` at call
  time. A loadable-strip is queued for a future chunk; the
  linker emits a `stripped_bundle` warning to make this
  explicit.

- **Chunk 173 — `shumway-link --map`.** New `ShmoBundleMap`
  helper writes a C-toolchain-style audit file: bundle
  metadata, entry points, per-module sizes and visibility-
  classified predicate lists, dropped modules, missing
  predicates, totals. CLI flag is `-m / --map <path>`.

- **Chunk 174 — `shumway-link --exe`.** Produces a single-file
  native executable for the current platform. The exe embeds
  the bundle as a manifest resource, loads it on `Main`, runs
  the user-supplied `--goal` at startup, and exits with 0 /
  1 / 2 for success / failure / uncaught exception.
  Implementation shells out to `dotnet publish` with
  `PublishSingleFile=true`. Default deployment is
  framework-dependent (~5-10 MB exe, needs .NET runtime on
  target); `--self-contained` produces a ~70 MB exe that runs
  on machines with no .NET installed.

  `--goal` accepts both `main` and `main.` (trailing dot
  optional). The goal is parsed and validated syntactically at
  link time; the head predicate also becomes an implicit
  reachability root (so `--goal alone` is enough — no `--entry`
  required).

  The generated temp project ships its own `nuget.config` with
  `<clear/>` + nuget.org, so a host with corporate HTTP NuGet
  sources doesn't break the build.

- **Chunk 175 — Closure.** This document.

---

## Bonus parser fixes (Blint.pl compatibility)

Surfaced while running `shumway-compile` against a real third-
party source. Landed before chunk 168, recorded here for
completeness:

- **Prefix-op + `/N` ambiguity** — `[not/1, catch/3, …]` failed
  because `not` is an `fy 900` prefix operator. The narrow
  disambiguation: when a prefix-op atom is followed by `/`
  then an integer, fall through to ReadPrimary so the outer
  infix loop applies `/` with the prefix-op atom as left
  operand. Specifically narrow (`/ <integer>`) so quoted-
  symbolic operands like `:- public '#='/2.` (clpfd) still take
  the prefix path.

- **Comma-separated directive specs** — `:- dynamic a/0, b/1,
  c/2.` (GNU Prolog grouped form) wasn't accepted. The
  ShmoCompiler's `ReadFunctorSpecs` now also walks the `,/2`
  conjunction shape, alongside the existing `Name/Arity` and
  `[Name/Arity, ...]` forms.

---

## Deliverables checklist

| Chunk | Deliverable | Status |
|---|---|---|
| 168 | Multi-positional input, per-file "compiling X" line. | ✓ |
| 168 | `-o` switches to output-dir mode for multi-input. | ✓ |
| 168 | 5 tests in Chunk168Tests. | ✓ |
| 169 | `--debug` / `--release` flags + `ShmoBuildMode` + .shmo V2 format byte. | ✓ |
| 169 | V1 reader compat; future-version rejection. | ✓ |
| 169 | 6 tests in Chunk169Tests; Chunk160Tests updated for the new layout offset. | ✓ |
| 170 | Verbose footer lists public + dynamic predicates per file. | ✓ |
| 170 | 3 tests in Chunk170Tests. | ✓ |
| 171 | `ClauseOrError`, `ClauseReader.ReadAllCollectingErrors`, `Parser.SkipToClauseTerminator`. | ✓ |
| 171 | `ShmoCompileResult` / `ShmoCompileError` + `ShmoCompiler.TryCompile{Source,File}`. | ✓ |
| 171 | shumway-compile prints every diagnostic in `file:line:col: error: msg`. | ✓ |
| 171 | 8 tests in Chunk171Tests. | ✓ |
| 172 | `LinkConfig.StripSource` + CLI `-s/--strip`; warning when active. | ✓ |
| 172 | 5 tests in Chunk172Tests. | ✓ |
| 173 | `ShmoBundleMap.GenerateText` / `WriteToFile`; CLI `-m/--map <path>`. | ✓ |
| 173 | 5 tests in Chunk173Tests. | ✓ |
| 174 | `ExecutableEmitter` + CLI `-e/--exe`, `-g/--goal`, `--self-contained`. | ✓ |
| 174 | Goal validation (trailing dot optional; syntactic parse; non-callable rejected). | ✓ |
| 174 | Temp project + `dotnet publish` shell-out + `nuget.config` clear. | ✓ |
| 174 | 13 tests in Chunk174Tests (12 fast + 1 e2e gated by `SHUMWAY_RUN_EXE_TESTS=1`). | ✓ |

---

## Test totals at closure

| Suite | Count |
|---|---|
| `Shumway.Tests.Core` | 417 |
| `Shumway.Tests.Interpreter` | 105 |
| `Shumway.Tests.Compiler` | 248 |
| `Shumway.Tests.IsoConformance` | 275 |
| `Shumway.Tests.Embedding` | 1512 |
| **Total** | **2557** |

All green at the closure tag (the e2e `--exe` test runs only
under `SHUMWAY_RUN_EXE_TESTS=1`; otherwise it self-skips and is
counted as Passed). Phase 14 added 51 new tests: 45 in
Embedding (Chunks 168-174) and 6 in Compiler
(PrefixOpSlashIndicatorTests).

---

## Roll forward to Phase 15+

Open candidates:

- **Loadable strip.** `LoadBundle`'s source-less path: register
  predicates directly from `Bundle.CompiledBytecode` so the
  chunk-172 `--strip` produces bundles that actually run.
  Requires bypassing `ConsultString` for entries with empty
  source — the engine builds a `ModuleManifest` whose
  predicates come from the decoded `CompiledModule`.

- **Real debug metadata.** Today `--debug` is a flag without
  observable effects beyond the map file. Next step:
  per-instruction source-line metadata emitted with the
  bytecode under `--debug`, surfaced in
  `PrologRuntimeException` stack traces.

- **In-process Roslyn for `--exe`.** Drop the `dotnet publish`
  shell-out in favour of Microsoft.CodeAnalysis.CSharp +
  HostModel in-process. Linker grows ~50 MB; output is the
  same, no SDK dependency at link time.

- **NativeAOT `--exe`.** Pure native single-file exe via the
  AOT toolchain. Much smaller (~3-5 MB), no .NET runtime
  required, but requires the AOT compile-time prerequisites.

- **`--exe` cross-target.** Today `--exe` only produces a
  binary for the current platform. A `--rid <runtime-id>` flag
  would let one build host produce a Linux binary on Windows,
  etc.
