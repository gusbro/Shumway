# ADR-040: Multi-dialect library shims + per-module attribute hook (uniting Prolog worlds)

**Status:** Accepted — core implemented. D5.2 per-search-path dialect threading,
D5.3 content sniff, and a fuller SWI pack are deferred (see below).

**Supersedes/extends:** [ADR-038](038-library-loading.md) (library loading +
export-qualified modules) and the flat, Scryer-only `CompatLibraries` shim.

## Implementation status

- **Component 3 — per-module attribute hook (done).** `Activation.Verify4FunctorId`
  mirrors the existing `Verify3FunctorId`: a module's `Module$verify_attributes/4`
  (module-local) is resolved and dispatched per attribute module, with the
  bare-global `verify_attributes/4` as a fallback. `HasAnyAttributeHook` (replacing
  the /3-only scan) gates the wakeup flush over both arities, mangled or bare.
  `ModuleHasHook` is per-module. Result: two libraries with module-local `/4` hooks
  coexist (no `:- public` collision), and a variable carrying attributes from two
  modules runs both hooks. The baked clpfd/clpr/coroutining keep their bare-global
  multifile `/4` and are unchanged (they already coexisted via multifile — so the
  old CLAUDE.md "CLP(R)/CLP(FD) cannot share an engine" note was already stale and
  is corrected). Tested: `PerModuleAttributeHookTests`.
- **Component 1 — DialectRegistry (done).** `DialectRegistry` replaces the flat
  `CompatLibraries` switch: dialect packs (`scryer` = the former data, `double_quotes
  = chars`; `swi` = a starter no-op set, `double_quotes = codes`), extensible by
  adding a pack. Resolution prefers the active dialect, then falls back to every
  pack — coexistence is the default.
- **Component 2 — dialect selection, explicit layer (done).** `engine.SetLibraryDialect`
  + the writable `library_dialect` prolog flag (distinct from the read-only ISO
  `dialect`, which still reports `shumway`). The active dialect only disambiguates a
  name two packs define; a name unique to one dialect always resolves.
- **Component 4 — per-load `double_quotes` scoping (done).** `UseCompatLibrary`
  parses each pack's shim source with that pack's `double_quotes`, restoring after,
  so a Scryer (chars) and an SWI (codes) library parse correctly in one engine.
- **Deferred:** D5.2 full per-search-path dialect threading (the subtree inheriting
  a dir's dialect end-to-end), D5.3 content sniff, and a real (non-stub) SWI pack.

Tests: `PerModuleAttributeHookTests`, `DialectRegistryTests`.

## Motivation

A distinguishing strength we want for Shumway is that it can **unite worlds** —
load libraries written for *different* Prolog systems (Scryer, SWI, SICStus, …)
**side by side in one engine**, unmodified. A user should be able to import
Scryer's `library(clpz)` and SWI's `library(http)` in the same program and have
both just work.

Today that is not possible, for two reasons:

1. **The shim is single-dialect and flat.** `CompatLibraries.cs` is a C# `switch`
   mapping *Scryer* library names (`iso_ext`, `charsio`, `si`, `pio`,
   `$project_atts`, …) to Shumway equivalents, and it implicitly assumes Scryer
   semantics (`double_quotes = chars`, the SICStus `put_atts`/`get_atts`
   attribute API bridged onto our native `put_attr`/`get_attr`). There is no
   notion of "which system is *this* library from", so a second dialect's names
   and semantics have nowhere to live.
2. **The bare-global public model collides across dialects.** Two libraries that
   each declare the same `:- public` predicate trip `ValidatePublicUniqueness` —
   e.g. Scryer `clpz` and our baked `clpfd` both want `verify_attributes/4`.

We already have most of the machinery this needs; ADR-040 composes it into a
coherent multi-dialect design and removes the two blockers.

## What already exists (grounding)

- **Per-module attribute hook, partially.** The baked constraint libraries do
  **not** dispatch a bare `verify_attributes/4`; they use a `:- multifile` hook
  whose FIRST argument is the attribute module:
  `verify_attributes(clpfd, fd(Dom,Props), Value, Goals)`,
  `verify_attributes(clpr, …)`, `verify_attributes(coroutining, frozen(G), …)`.
  So CLP(FD)/CLP(R)/coroutining **already coexist** on one engine (as long as no
  single variable carries two libraries' attributes). The per-module idea is
  real; it is just not yet the *formalised, only* mechanism, and the Scryer-shim
  path does not route through it cleanly.
- **Export-qualified modules + per-module import tables** (ADR-038): `:- module(Name,
  [Exports])` mangles every predicate `Name$x`, and each importer resolves a call
  local → imports → bare-global, at compile time (`ModuleRewrite`) and runtime
  (`$mqual` / `Activation.CurrentImportMap`). Same-name exports of two modules
  already coexist as `A$foo` / `B$foo`.
- **`double_quotes` is a per-parse flag** (`Flags.DoubleQuotes`, set before a
  consult and honoured by the next parse) — so it can be scoped to a single
  library's load rather than being a hard engine-global.
- A `dialect` prolog flag exists but is **cosmetic** (reports `shumway`); it
  selects nothing.

## Key insight: dialect is a per-subtree resolution context, not an engine mode

The design mistake to avoid is treating "dialect" as a **sticky, engine-wide
mode** ("once you're in Scryer, loading anything from SWI is an error"). That is
wrong: Scryer `clpz` (attributed-variable constraints) and SWI `http` (I/O) share
*nothing* — there is no reason they cannot coexist. A blanket "two dialects =
conflict" rule rejects a compatibility that genuinely exists.

Instead, **the dialect travels with each top-level `use_module`'s dependency
subtree**, and is a *resolution context*, not global state:

- `use_module(library(clpz))` resolved from a Scryer source tree →
  `clpz` **and all its dependencies** (`iso_ext`, `charsio`, `si`,
  `$project_atts`, …) resolve **Scryer-flavoured**: parsed with
  `double_quotes = chars`, their `library(X)` deps resolved against the Scryer
  name map, seeing the SICStus `put_atts` API.
- `use_module(library(http))` resolved from an SWI tree → `http` and its
  deps resolve **SWI-flavoured**: `double_quotes = codes`/`string`, the SWI name
  map, `put_attr`.

The two subtrees **coexist without touching each other**. "Dialect" answers only
"with which name map and which parse flags do I load *this* tree", never "what
global mode is the engine in".

## Decision

### D1 — Shims are dialect-scoped, namespaced modules (coexistence by default)

Replace the flat `CompatLibraries` switch with a **`DialectRegistry`**. Each
dialect (`scryer`, `swi`, …) is a *pack*: a set of shim libraries plus the
semantic defaults it assumes. Shim libraries are ordinary **export-qualified
modules** (ADR-038), namespaced by dialect, so `scryer$format` and `swi$format`
are distinct predicates and each importer resolves `format` against its own
dialect's import table. Even a "global-looking" predicate like `format/2` exists
in both variants at once, without a fight. **Coexistence is the default**, not the
exception — this is the "unite worlds" property, made structural.

The resolver order for a `use_module(library(X))` becomes: the **active subtree's
dialect pack** → dialect-neutral libraries (ISO / prelude-covered) → the
search-path file → error. (The current baked C# clpfd/clpr/coroutining become the
`swi`/native dialect's pack; the Scryer names become the `scryer` pack.)

### D2 — Per-module attribute hook is THE mechanism

Formalise `verify_attributes(Module, AttrValue, Value, Goals)` (first argument =
the attribute module) as the *only* attribute-unification hook, and **dispatch it
per attribute module**: when a variable carrying an attribute in module `M` is
unified, the engine runs only `M`'s hook. A dialect's constraint library declares
its hook for its **own** module (`clpz` for the Scryer pack, `clpfd` for the SWI
pack); two dialects' constraint libraries therefore **coexist** — the
long-standing "CLP(R) and CLP(FD) cannot share an engine" restriction is
**removed** (it was an artefact of a bare-global `:- public verify_attributes/4`,
not a real incompatibility). A single variable carrying attributes from two
modules runs both modules' hooks in turn, as SWI/SICStus do.

### D3 — `double_quotes` (and other parse-time flags) scoped per library load

Each dialect pack sets its parse flags (notably `double_quotes`) before consulting
one of its libraries and restores them after, so a Scryer library (chars) and an
SWI library (codes) both parse correctly in the same engine. Parse-time flags are
never left as a hard engine-global for the multi-dialect case.

### D4 — Conflicts are narrow and specific (no dialect-level guard)

There is **no "two dialects" conflict guard**. The only real conflict is two
libraries fighting over a **genuinely shared, non-namespaceable engine resource**
— a single bare-global predicate name both want to own. That is exactly what
`ValidatePublicUniqueness` already catches, and D2 removes it for the attribute
hook (the common case). Everything else — `clpz` + `http`, `clpz` + `clpfd`
(now, via per-module hooks) — coexists.

### D5 — Dialect selection: layered, most-explicit wins

1. **Explicit** (the robust base, ship first): `:- set_prolog_flag(dialect,
   scryer).` in a source, CLI `--dialect scryer`, or `engine.UseDialect(...)`.
2. **Per-search-path association**: a library directory is tagged with a dialect
   (engine config, or a marker file in the dir). Pointing `-L` /
   `SHUMWAY_LIBRARY_PATH` at a Scryer checkout *is* the statement "these are
   Scryer libraries"; a `library(X)` resolved from there loads in that dialect,
   and its dependency subtree inherits it.
3. **Content sniff** (a convenience *on top of* the above, never the only path,
   and **never silent**): on first resolution to a file, scan for signature
   markers (`:- attribute` / `put_atts` → Scryer/SICStus; `put_attr` +
   `library(yall)` / `library(apply_macros)` → SWI) and auto-select, **announcing
   it** (`% detected scryer dialect from library(clpz)`).

Ship layers 1–2 first; layer 3 is deferred (nice but fragile — it belongs above a
working explicit mechanism, not as the sole one).

## Consequences

- **Breaking changes are acceptable** — the repo is private pre-release. But every
  such change **must be reflected in the docs the same commit**: this ADR, the
  ADR-038 cross-reference, the CLAUDE.md quick-reference and any stale note (e.g.
  the Phase-7 "CLP(R) and CLP(FD) cannot share an engine" line, which D2
  invalidates), the user guide's library section, and the generated predicate
  docs. The invariant is: **the documentation always describes the system as it
  now is**, never as it was.
- The flat `CompatLibraries.cs` is replaced by the `DialectRegistry`; its current
  Scryer entries become the `scryer` pack, the baked clpfd/clpr/coroutining become
  the native/`swi` pack.
- The attribute-hook change (D2) is an **engine-mechanism** change (per-module hook
  dispatch); it needs the attribute-variable unification path and the wakeup queue
  to key on the attribute's module. Existing single-dialect programs keep working
  (one module's hook is the degenerate case).
- New tests must use **original library shapes** (no third-party code copied into
  the repo; scratch copies for debugging only) — a Scryer library and an SWI
  library loaded together, each resolving its own names and hooks, with a variable
  carrying attributes from one dialect while the other dialect's library is also
  loaded.

## Implementation components (proposed)

1. **`DialectRegistry` + dialect packs.** Replace `CompatLibraries` with a registry
   keyed by dialect; move the Scryer names into a `scryer` pack and the baked
   constraint libs into the native pack. Namespaced (export-qualified) shim
   modules.
2. **Dialect selection.** `dialect` promoted from cosmetic to functional: the
   explicit flag/CLI/API (D5.1) and per-search-path association (D5.2); the
   resolver threads the active subtree's dialect through `ExecuteUseModuleDirective`
   and the dependency walk.
3. **Per-module attribute hook (D2).** Formalise `verify_attributes(Module, …)`
   dispatch per attribute module in the engine; route the Scryer `put_atts` shim
   and the native libs through it; remove the bare-global-public collision. Update
   CLP(FD)/CLP(R)/coroutining + the clpz shim to the formal form.
4. **Per-load parse flags (D3).** Scope `double_quotes` (and any other parse-time
   flag a pack needs) to each library consult.
5. **Docs sweep.** This ADR to Accepted; CLAUDE.md quick-reference row; fix the
   stale CLP coexistence note; user-guide library section; regenerate predicate
   docs.
6. **Content sniff (D5.3).** Deferred.

## Alternatives rejected

- **Sticky engine-wide dialect mode with a conflict guard.** Rejected: it forbids
  genuinely-compatible combinations (`clpz` + `http`) for no reason. Dialect is a
  per-subtree resolution context, not global state (see the key insight above).
- **One flat shim that tries to cover all dialects' names at once.** Rejected:
  name collisions (`lists`, `apply`, `pairs`, `assoc` differ between Scryer and
  SWI) and semantic divergence (atts API, `double_quotes`, `format`) have nowhere
  to go without per-dialect namespacing.
