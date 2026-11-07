# ADR-038: Library loading (`use_module(library(X))`) + scoped module qualification

## Status

Shipped ([Phase 36](../../history/phase-36-closure.md)).

Amended 2026-11-06 (accepted; built 2026-11-07): a dynamic predicate of an
export-qualified module belongs to that module (the amendment at the end).

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

## Amendment (2026-11-06): a dynamic predicate belongs to its module

Accepted 2026-11-06, built 2026-11-07 (`invariants.md` and the user guide
describe the built model).

### Context

Section 1 left dynamic predicates in the bare-global namespace: `assertz(a(1))`
from any module lands in one shared `a/1`, and a module's `:- dynamic counter/1`
is the same predicate as every other module's. Issue #214 found the visible end
of it: `assertz(nd:a(1))` drops `nd`, and `current_predicate(nd:F/A)` finds
nothing. Measured against the systems whose module model this ADR follows
(SICStus and Scryer; SWI agrees on every point below, except that its modules
also see `user`'s predicates bare):

- Two export-qualified modules that each keep a private `counter/1` share its
  clauses, with no warning. The others keep two predicates.
- A plain file's dynamic `f/1` takes over a module's static `f(mine)` of that
  name: both sides then see `[mine, global]`. The others see `[mine]` in the
  module and `[global]` in `user`.
- A rule asserted inside `n` that calls `n`'s private `helper/1` raises an
  existence error, because an asserted clause compiles in `user`'s context.
  The others run it in `n`.
- `abolish(nd:a/1)` also removes `ot`'s `a/1`.

Shumway has two worlds. The legacy one, an Arity program in files with
`:- public`, compiled under `arity_compat`, has no static clauses in the Arity
sense: every fact a program asserts is global, and programs rely on it. The
modern one, `:- module(Name, [Exports])`, is where a program is written to run
on the other systems too, and there a global dynamic is wrong.

### Decision

1. **The world of a clause is fixed where it is compiled.** A clause of an
   export-qualified module compiled with the `arity_compat` flag off is modern.
   Everything else is legacy: a file without a module directive, a
   `:- module(Name)` module, and any clause compiled with `arity_compat` on,
   `:- module/2` or not. The legacy world is unchanged: its dynamics are the
   global ones, `assertz` from anywhere reaches them, and `:- public` is what
   makes one visible to the modern world.
2. **In the modern world a dynamic predicate is the module's own**, named
   `M$p` as its statics are. `:- dynamic p/N`, `:- multifile p/N` (a dynamic
   here, ADR-055) and an `assertz` of an undeclared `p` (`implicit_dynamic`)
   in `M` define `M$p`. `assert/1`, `asserta/1`, `assertz/1`, `retract/1`,
   `retractall/1`, `abolish/1`, `clause/2`, `dynamic/1`, `current_predicate/1`
   and `predicate_property/2` act on `M`'s own predicate when called from
   `M`'s clauses, and the clauses of `M$p` compile in `M`'s context: a rule
   asserted in `n` calls `n`'s `helper/1`.
3. **The predicate argument of a database builtin is a meta-argument**
   (ADR-056 point 5): it carries the module of the clause it was written in.
   A term handed to a module's predicate that asserts it is asserted in that
   module; one handed through a declared `:- meta_predicate p(:)` is asserted
   in the caller's; `G = assertz(t(1)), call(G)` in `M` asserts `M`'s `t/1`.
   `M:Term` names the module outright and `user:Term` the global one.
   `assertz(z:q(1))` with no module `z` creates it, as a qualified clause head
   does (ADR-055 point 1), and `current_module(z)` then holds.
4. **A qualified head keeps its module.** `M:Head :- Body` for a dynamic or
   multifile predicate of an export-qualified `M` is a clause of `M$Head`;
   ADR-055 point 4 no longer strips the qualifier, and the body still runs in
   the source module (ADR-055 point 2). Another source contributes to a
   module's multifile predicate by naming it, `solver:step(...) --> ...`, as
   it does today.
5. **An exported dynamic is one predicate.** `:- dynamic e/1` exported by `a`
   and imported by `c` is `a$e` in both: `c`'s `assertz(e(2))` adds to it
   (SICStus; Scryer keeps them apart).
6. **What the modern world sees of the global namespace is section 1, step 3**:
   the prelude, `:- public` predicates, dynamic or not, and builtins. A plain
   file's dynamic that is not public is reached as `user:p(X)`, a legacy
   module's as `user:p(X)` or `L:p(X)`. A bare call to one from a module is an
   existence error, as in SICStus and Scryer; today's read of it is withdrawn.
   Under `unknown=fail`, which an Arity bundle sets, that call fails instead.
7. **A call `M`'s compile cannot resolve** (no local, no import, no global yet)
   resolves when it runs, in `M`'s context, as a `$mqual` goal does today:
   `M`'s own, a dynamic asserted later included, then its imports, then step 3.
   The cost of that resolution is measured before the shape is chosen. If
   resolving on every call is expensive, the site links lazily: the first
   call that finds the predicate binds the site to it, and the site stays
   bound, as a link-time resolution would have left it.
8. **A consult qualified with a module loads into it**: `nd:reconsult(File)`,
   `nd:consult(File)` and `consult(nd:File)` define `nd`'s predicates, a
   module created of the modern world when nothing defined it; and so does
   `consult(File)` called from `nd`'s own code, since `consult/1`,
   `reconsult/1` and `ensure_loaded/1` take their file as a `:` argument
   (SICStus and SWI; Scryer loads into `user`). A file with its own
   `:- module` directive loads as that module wherever it is called from.
   They used to load into `user`.
9. **Hooks.** The expansion hooks keep the path of ADR-055 point 7. The dynamic
   facts the engine reads, `file_search_path/2`, `library_directory/1`,
   `portray/1` and the attribute hooks, are predicates of `user`: a module adds
   to them as `user:file_search_path(...)`, the spelling the three systems
   take; its bare `assertz(file_search_path(...))` defines its own.
10. **Separate compilation and the tiers.** A module's object names its
    dynamic predicates bare, as it names its locals, in its definitions and
    its dynamic seeds; its bytecode calls them `M$p`, and the load keys the
    seeds `M$p` again for an export-qualified entry compiled with
    `arity_compat` off. The linker keeps such a predicate out of the global
    dynamic namespace: it is the module's own definition. A file compiled
    alone, with no engine to ask what the global namespace holds, resolves
    its declared dynamics as its own and leaves every other unknown name
    bare, for the linker. The Tier-1 and wasm forms of a dynamic (ADR-023,
    ADR-054) are untouched: a name changes, not a mechanism.

### What this amends

- ADR-008, "Dynamic predicates respect visibility": true for the modern world
  from here. It was never built for the legacy one, where a dynamic is global
  and `:- public` only declares it.
- ADR-055 point 4 holds for the legacy world only.
- ADR-056 point 2 ("dynamic predicates stay global") and point 6 hold for the
  legacy world only. Point 3 is withdrawn: with `M`'s `priv/1` named `M$priv`,
  a global `priv/1` cannot take it over, so `assertz(priv(1))` from `user`
  defines `user`'s own, as in SICStus and Scryer, instead of raising
  `permission_error`.

### Consequences

- A modern program that reads a plain file's dynamic bare gets an existence
  error and writes `user:`, the spelling that works on the three systems. A
  census with a temporary probe (every call and database operation of an
  export-qualified module, checked against what is dynamic in the engine)
  over the engine's own libraries, the Scryer, Trealla and SWI library trees,
  four Logtalk test sets and the Embedding tests found no library that reads
  one; every hit was the module's own multifile or local predicate. Six tests
  pin the global model (a module asserts, `user` reads bare) and change to
  `m:`. The census loads the libraries without running them, and cannot
  attribute an `assertz(T)` whose `T` is a variable.
- Logtalk's `named_databases` example and the four tests of #214 work with
  no change to the example.
- `current_predicate(M:PI)`, `clause(M:H, B)` and `predicate_property(M:H, P)`
  keep their meaning, `M`'s own definitions, and gain the dynamics. Asked
  about a module nothing defined, they fail without creating it; the
  database builtins create it.
- A goal a module meta-calls at run time (`G = p(X), call(G)`) still falls
  through to the global namespace when the module has no `p/1`, `user`'s
  predicates and the global dynamics included, as ADR-056 point 5 has it
  for `user`'s closures; the module's compiled calls do not. The asymmetry
  predates this amendment for statics and stays: `user` passes no module
  with its closures, so a library's `call(G)` must reach `user`'s predicates
  bare.
- The goals a `verify_attributes` hook returns run in the attribute's
  module only when the hook is that module's own; the shared
  `verify_attributes/4` hook's goals run in `user`, as the attribute's name
  alone does not make a module.
- A `:` argument of a predicate the program declares is qualified by every
  caller, `user` included (SICStus): `keep(T) :- assertz(T)` under
  `:- meta_predicate keep(:)` receives `user:t(1)` from the top level. On a
  builtin or a prelude predicate the global module and a legacy module
  leave it bare, since bare already names the global store there.

### Measured while building

- `assertz(M:(H :- B))` from a source `S`: `B` runs in `M` on the three
  systems. `assertz((M:H :- B))`, the head alone qualified, runs `B` in `S`
  on SICStus and SWI (Scryer rejects the form). Built so: the body of a
  head-qualified clause is stored qualified with `S`, as ADR-055 stores a
  consulted one.
- A qualified `retract/1` gave its first solution only, on the old engine
  too: the enumeration re-reads its pattern from the register on every
  resume, qualifiers and all, and the unpeeled pattern unified with no
  clause. It peels on every resume now.
- The cost of point 7, instrumented: two diagnostic counters on the
  activation count the dispatches of a site the link left unresolved, by
  what answered them (the address map, or the module's imports and the
  global view), and `ModuleDynamicsCostTests` pins them. A literal
  `assertz(p(..))` in the module's text makes `p` a dynamic at consult (the
  `implicit_dynamic` scan), so its sites link at setup and resolve nothing
  at run time, as a declared dynamic's do. Only a predicate the module
  neither declares nor names, asserted into it from outside in the query
  that calls it, is resolved per dispatch: every interpreter dispatch of
  that query goes through the address map, and the next query's setup links
  the site directly, with no resolution left. A name a later load makes
  public is answered by the global view until the module is transformed
  again, which the next setup does. Timed with the processor capped, three
  million calls per arm, three runs, the loop calling that predicate against
  the same loop over a declared dynamic asserted in the same query: on
  Tier-0 the two arms are equal within the spread in the asserting query and
  in the next, with identical inferences and heap cells. On Tier-1 the arm
  over the asserted predicate runs slower in both queries, and the global
  module shows the same numbers for the same shape: a loop promoted in the
  query whose assert created its callee keeps one interpreter dispatch per
  call for the rest of the process, while a loop promoted over a declared
  dynamic, or in a later query, runs entirely in IL. That is the tier's
  behaviour for a predicate created mid-query, not the amendment's cost:
  the tier promotes a dynamic mutated in the running query at the next
  setup only, on the engine before this amendment as on the one after it,
  and the test pins the equality between the module's case and the global
  module's. The lazy link is not needed.

### Tests

`ModuleDynamicsTests`, the measured table on both tiers: `a` asserts `b(1)`
and `c` calls `b(X)` (existence error), `a:b(X)` from `c` and `user`, `c`
asserts `b(2)` (two predicates), `retract`, `retractall` and `abolish` in
`a` leave `c`'s alone, a term passed plain and through a
`:- meta_predicate p(:)`, `call/1` on an assert and `maplist(assertz, L)`,
a meta-call of the module's own dynamic, a call before the first assert
(and under `unknown=fail`), a rule asserted in a module calling its
private, the three spellings of an asserted rule, `assertz(z:q(1))` with
`current_module(z)` and the reflection of `z`, an exported dynamic shared
with its importer, a static `f/1` beside `user`'s dynamic `f/1`, `user:`
from a module, the engine's hook facts, the withdrawn ADR-056 point 3,
`nd:reconsult(File)` and `consult(File)` from a module, and the legacy world
unchanged: a plain file, a `:- module(Name)` module and a `:- module/2` file
under `arity_compat`. `QualifiedDatabaseTests` and `QualifiedReflectionTests`
pin the qualified spellings; the tests that pinned the global model now
write `m:` or `user:`. Red counter-proofs: the global store for a modern
module's dynamic fails the two-predicates test; compiling an asserted clause
in `user`'s context fails the private-helper test.
