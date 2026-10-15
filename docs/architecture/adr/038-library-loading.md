# ADR-038: Library loading (`use_module(library(X))`) + scoped module qualification

## Status

Shipped ([Phase 36](../../history/phase-36-closure.md)).

## Implementation status

All four components are implemented and tested:

- **Component 1 — library search path + resolver.** `AddLibraryDirectory` /
  `AddDefaultLibraryDirectories`, `SHUMWAY_LIBRARY_PATH`, and the
  `file_search_path(library, Dir)` / `library_directory(Dir)` dynamic facts feed a
  per-engine search path; `use_module(library(X))` resolves `X.pl` / `X.shum`
  (order: engine's own baked bundle → search-path file → CompatLibraries → error).
  `absolute_file_name(library(X), Abs)` resolves the same alias.
- **Component 2 — export-qualified modules + import tables.** `:- module(Name,
  [Exports])` mangles every predicate `Name$x`, records the export surface, and
  builds a per-module import table; resolution is local → imports → bare-global at
  compile time (`ModuleRewrite`) and at runtime (`$mqual`, interpreter + IL, via
  `Activation.CurrentImportMap`). Same-name exports coexist. Import of a non-export
  is an error.
- **Component 3 — separate compilation + linking.** `shumway-compile` recognises
  `:- module/2` (export-qualified) and both `use_module(library(X))` forms — but
  **never reads a library**: the two-arg filtered import is resolved from the
  *source* (the filter is the imported set), and the one-arg import-all is recorded
  as a dependency and left for the linker. The `.shmo`/`.shum` carry
  export-qualification + the (two-arg) resolved import table (shared serializer
  across both `.shum` writers). `shumway-link` — which has the library's export
  surface — **resolves the one-arg import-all** (recompiling the importer so its
  bare calls mangle to `Source$pred`), reaches an imported module through the
  import table (also a reachability root, so a meta-called import is not
  dead-code-eliminated), and **pulls** a `use_module(library(X))` dependency from
  the `--library-dir` search path when it is not already among the passed objects /
  `.shum` libraries (C-linker order). An unresolved library is reported by name
  (`unresolved_library`), not as a per-predicate error. `LoadBundle` reconstructs
  the runtime manifests so a loaded bundle resolves imports cross-process.
- **Component 4 — repo `lib/` + REPL.** A shipped `lib/` (starter
  `library(lists_ext)`) is on the default search path; the goal-form
  `use_module/1` builtin loads from it and imports into the `user` module so an
  interactive query resolves the imports.

**Deferred (narrow):** only the **file-at-a-time** compile of a program using a
engine library (`clpfd`/`clpr`/`coroutining`). On that path the library is
recorded as a `ShmoLibraryDep{Baked}` but nothing consumes it: an
operator-carrying library (clpfd/clpr) fails at parse (its operators are not
registered), and even an operator-free one (coroutining) compiles but **fails
at link** with `missing_predicate` — the baked dep is neither counted as
providing its predicates nor replayed at load. **The consult-mode path covers
the case end to end** (verified: clpfd and coroutining programs through
`--consult` → link → `--exe` run correctly): `shumway-compile --consult` loads
the file in an ephemeral engine (directives run, operators register), emits
one `.shmo` per module the load brought in — the library's Prolog side
included — and the libraries' C# propagators are engine builtins present in
any engine. The CLI hints `--consult` when it detects the pattern. The
`M:goal` qualified-call syntax remains deferred as before.

## Context

ADR-008 gave Shumway a **flat global namespace**: `:- public foo/N` (and the exports
of `:- module(Name, [...])`) make `foo/N` a single, globally-unique bare name;
module-local predicates are mangled `module$name`; there are no qualified `M:goal`
calls. That was deliberate initially and works for a self-contained program plus the
baked libraries (prelude, clpfd, clpr, coroutining — all `:- module(Name)` 1-arg +
`:- public`, provided as C# source strings).

It does **not** support bringing in third-party Prolog libraries the way
SICStus/Scryer/SWI do:

- `use_module(library(X))` resolves only through a hard-coded C# name switch
  (`clpfd`/`clpr`/`coroutining`) plus a compiled-in `CompatLibraries` table — there is
  no `file_search_path/2`, no `library_directory/1`, no `library(X)`→file mapping, no
  place to drop `.pl` sources.
- The `use_module/2` import list is dropped.
- Because every export is one flat-global name, **two libraries exporting the same
  predicate collide** — there is no per-module isolation.

Third-party sources routinely use `:- module(Name, [Exports])` + `use_module(library(dep))`
and assume per-module import isolation. We want them to load unchanged.

## Decision

### 1. Scoped module qualification — triggered ONLY by `:- module(Name, [Exports])`

A **2-argument** module directive is the sole trigger for the new model. Everything
that exists today is untouched:

- **Legacy (unchanged, bare-global).** `:- module(Name)` (1-arg) + `:- public`, the
  prelude, and the engine's own libraries keep their bare-global names (`member/2`,
  `#=/2`, …). Those libraries ship as bundles baked at build time
  (`src/Shumway.Libraries`), never as source consulted at load.
- **Export-qualified module** = a source with `:- module(Name, [Exports])`. ALL its
  predicates are mangled `Name$x` — it contributes **nothing** to the bare-global
  namespace. `Exports` is its *importable surface*. Two export-qualified modules can
  both export `foo/1` — they are `A$foo` and `B$foo`, so they coexist without a
  uniqueness collision.
- **Per-module import table.** `:- use_module(library(A))` in module B imports ALL of
  A's exports; `:- use_module(library(A), [pepe/1])` imports only `pepe/1`. B records
  `pepe/1 → A$pepe/1`. Importing a name A does not export is an **error**. Repeated
  imports union.
- **Resolution of a call to `p/N` inside module M** — identical at compile time
  (`ModuleRewrite`) and at runtime (the `$mqual` variable-meta-call path):
  1. `M$p` — M's own local (including M's own exports),
  2. **M's import table** (`p → Source$p`) — the new step,
  3. bare-global — the prelude, `:- public`, builtins (where `member/2` etc. resolve),
  4. error / unresolved.
- An export-qualified module may call bare-global predicates (prelude `member/2`) via
  step 3 without importing them — the prelude is always visible. This is more
  permissive than SWI (which requires `use_module(library(lists))`), by design.
- **No explicit `M:goal` qualified-call syntax** — still deferred (a later ADR if a
  real need appears). The import table gives the isolation without the syntax.

### 2. Library resolution — both conventions, file-based

`library(X)` resolves to a file through a per-engine ordered **library search path**,
fed from all of:
- `file_search_path(library, Dir)` facts (SWI/Scryer) and `library_directory(Dir)`
  facts (SICStus), declared `:- dynamic` so user directives populate them;
- a C# API `PrologEngine.AddLibraryDirectory`;
- env `SHUMWAY_LIBRARY_PATH`;
- the shipped `lib/` directory (added by the REPL/CLI).

`use_module(library(X))` resolves in order: (1) baked C# switch (unchanged) →
(2) file resolver (`Dir/X.pl` then `Dir/X.shum`) → (3) `CompatLibraries` (unchanged) →
(4) error.

A host may set `PrologEngine.LibrarySourceResolver`. When step (2) lands on a
`.pl`, the resolver is asked first and may return another file to load in its
place, or null for the source. WebShumway uses it to compile an imported library
into its bundle the first time a program imports it, so a library never loads
from source over others already compiled. A native override (ADR-040) is decided
before it, on the source, and never reaches it.

### 3. Linker — resolve a `use_module` dependency like a C linker resolves a symbol

`:- use_module(library(X))` in a separately-compiled program adds a link dependency
resolved in order — already-provided inputs first, source compilation last:
1. module X among the explicit `.shmo` objects passed to `shumway-link` → use it;
2. X provided by a passed `.shum` library archive → pull that member (the existing
   on-demand FIFO library-pull);
3. otherwise resolve `X.pl` via the library search path (`--library-dir` + env),
   compile it, include it.
A dependency on one of the **engine's own libraries** (clpfd, clpr, coroutining: bundles baked at build time, loaded by name) is not a file — record it so `LoadBundle`
**replays** the `use_module(library(clpfd))` directive (`UseClpfd()`). Each
export-qualified module's `ExportFunctors` + `Imports` table travel in the `.shmo` /
`.shum` so a fresh process reconstructs the resolution.

### 4. A library's archive holds its own modules; its dependencies resolve when it loads

A library compiled into a librarian archive (WebShumway builds one per library
of a collection) holds that library's modules only: its own module, and the
modules it loaded that are not libraries with an archive of their own. A
dependency that has one, such as `library(clpz)` for Scryer's `crypto`, is left
out, so a collection stores each library's code once instead of once per
library that imports it.

Loading an archive resolves what it left out. Before the archive's code, each
`use_module(library(D))` dependency of a member is loaded through
`use_module(library(D))` when the archive does not hold `D`, `D` is not one of
the engine's own libraries, and `D` is not loaded yet: the order of section 2,
the host's resolver included. The archive holds `D` when a member module is
named `D`, the convention the linker (section 3) and `use_module` of a module
a bundle already brought in both use.

Compiling a library loads its dependencies the way a program does: from their
archives when they have them. A dependency with no archive yet is compiled
first, into its own, and the library's compile goes on with it loaded from
there. A cycle (A imports B, B imports A) is broken by loading A from source
while B compiles; A stays out of B's archive and resolves from its own when B
loads.

The archive's baked wasm module (ADR-050) holds the predicates of its own
members only. A call into a dependency is a relocation by name, resolved when
the module is installed against the dependency's code, which the load order
installs first.

## Consequences

- Third-party `:- module(Name, [Exports])` libraries load via `use_module(library(X))`
  from a configurable directory, with selective import and same-name coexistence —
  no source changes.
- Two export mechanisms coexist: bare-global (`:- public` / 1-arg module — legacy,
  prelude, baked) and mangled + import-table (`:- module/2` — new). The 2-arg
  directive is the ONLY trigger; the rule must stay crisp.
- The runtime import map rides the existing `$mqual` machinery and the mangled-address
  setup — no new dispatch mechanism.
- `ValidatePublicUniqueness` is unchanged (it only sees bare-global publics), so
  export-qualified same-name exports coexist by construction.
- The linker gains a library search path at link time and a directive-replay record
  for baked-C# deps.
- A library archive is not self-contained: loading it needs its dependencies loaded
  or on the library search path. An archive that leaves one out, such as a
  `shumway-lib` archive built without it, used to load without it and fail at the
  first call; it now resolves it, or warns of an unknown library as a program's
  `use_module` does.

## Alternatives considered

- **Flat bare-global with an import filter** (exports stay bare-global; `use_module/2`
  filters which become global). Rejected: the "import" is a global filter, not
  per-importer, and it cannot resolve two libraries exporting the same name — which is
  exactly the third-party-compat case.
- **Full qualified modules with `M:goal` syntax** (the ADR-008-deferred design applied
  to everything, prelude included). Rejected for now: a much larger rework touching
  the prelude and every public; the scoped model gets per-module isolation for the
  libraries that need it while leaving the working bare-global core alone.
