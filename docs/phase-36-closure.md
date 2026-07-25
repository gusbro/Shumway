# Phase 36 — Third-party library loading (`use_module(library(X))`) + scoped module qualification (ADR-038)

**Status: implementation complete, pending tag.** Delivers a configurable place
to drop third-party Prolog `.pl` libraries and load them with
`use_module(library(X))` — compatible with SICStus/Scryer/SWI — including
selective import, same-name coexistence, and separate compilation. Designed and
specified in [ADR-038](architecture/adr/038-library-loading.md).

## The model (settled with the user)

Only the **two-arg** `:- module(Name, [Exports])` directive triggers the new
scoped mechanism. Everything that existed before is untouched: `:- public` / the
one-arg module / the prelude / the baked C# libraries stay **bare-global**. An
export-qualified module mangles **every** predicate `Name$x` (nothing
bare-global), its `[Exports]` list is the importable surface, and
`use_module(library(A))` builds a per-module **import table** so a call to `p/N`
resolves **local (`M$p`) → import table (`Source$p`) → bare-global → error** — the
same rule at compile time and at runtime. Two export-qualified modules can export
the same name (`A$foo` / `B$foo`, no collision).

## The four components

- **Component 1 — search path + resolver.** A per-engine library search path fed
  by `AddLibraryDirectory` / `AddDefaultLibraryDirectories`,
  `SHUMWAY_LIBRARY_PATH`, and the `file_search_path(library, Dir)` /
  `library_directory(Dir)` dynamic facts. `use_module(library(X))` resolves
  `X.pl` / `X.shum` (baked C# → search-path file → CompatLibraries → error);
  `absolute_file_name(library(X), Abs)` resolves the same alias.
- **Component 2 — export-qualified modules + import tables.** `:- module/2` marks
  the module export-qualified (`ModuleManifest.IsExportQualified` / `ExportFunctors`
  / `Imports`); `ModuleRewrite` mangles all predicates and resolves imports at
  compile time; the runtime `$mqual` path resolves imports for variable
  meta-calls (interpreter + IL, via `Activation.CurrentImportMap`). Import of a
  non-export is an error; first import of a name wins. The one behaviour change —
  a two-arg module's exports are no longer bare-global — updated the two
  `CompatLibraries` tests that asserted the old contract; no `.pl` in the repo
  used the two-arg form.
- **Component 3 — separate compilation + linking.** `shumway-compile` recognises
  `:- module/2` and both `use_module` forms (the two-arg filter directly, the
  one-arg import-all by reading `X`'s export surface off a compile-time
  `--library-dir` path); the `.shmo`/`.shum` carry export-qualification + the
  resolved import table (a shared `WriteExportQualification` serializer keeps the
  two `.shum` writers byte-identical). `shumway-link` reaches an imported module
  through the import table (also a reachability **root**, so a meta-called import
  survives dead-code elimination) and **pulls** a `use_module(library(X))`
  dependency from `--library-dir` when not passed explicitly (C-linker order:
  explicit `.shmo` → `.shum` archive member → search-path source). `LoadBundle`
  reconstructs the runtime manifests so a loaded/`--exe` bundle resolves imports
  cross-process.
- **Component 4 — repo `lib/` + REPL.** A shipped `lib/lists_ext.pl` (a genuine
  export-qualified starter library: `take`/`drop`/`split_at`/`zip`/`unzip`/
  `intersperse`/`flatten`) rides on the default search path (copied beside the
  executable). The goal-form `use_module/1` builtin loads from it (delegating to
  the same resolver, raising `existence_error` on an unknown library) and imports
  the loaded module's surface into the `user` module, so an interactive REPL query
  resolves the imports — verified end-to-end.

## Verification / gate

Full five-project gate at close: **Core 444 · Interpreter 105 · Compiler 360 ·
ISO 298 · Embedding 3449** (one flaky `Adr035LazyDebugTests` timing test that
passes in isolation, unrelated to this phase). Zero compiler warnings. New tests:
`LibrarySearchPathTests` (7), `ExportQualifiedModuleTests` (8), `ShippedLibraryTests`
(4), `SeparateCompilationModuleTests` (7), plus two updated `CompatLibrariesTests`.

## Deferred

- A **baked C# library** (`clpfd`/`clpr`/`coroutining`) consumed through *separate
  compilation*: recorded as a `Baked` library dep but not replayed at load, and —
  more fundamentally — its operators aren't available to `shumway-compile` at parse
  time, so a CLP program can't yet be `shumway-compile`d. Works fully in the
  embedded / REPL path (the primary use case).
- `M:goal` qualified-call syntax (deferred as in ADR-008).
