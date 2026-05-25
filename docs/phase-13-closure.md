# Phase 13 — Closure

**Status**: complete.

**Tagged**: `phase-13` (this commit).

Phase 13 delivers the **separate-compilation workflow** Shumway
needed to graduate from "single-shot bundler over a list of `.pl`
files" to "compile each module once, link many into a deployable
bundle with reachability + missing-predicate validation". Plus the
user-facing documentation that ties the whole tool family
(`shumway`, `shumway-compile`, `shumway-link`,
`shumway-bundler`, the embedding API) together.

Eight chunks:

- **Chunk 160 — `.shmo` V1 format.** Per-module compiled-object
  file: magic `SHMO` + `uint32` version (V1=1) + module name +
  source + WAM bytecode + defined-set with visibility +
  ensure-linked set + per-predicate call graph + qualified
  references. `ShmoFormat` / `ShmoObject` / `ShmoReader` /
  `ShmoWriter`.

- **Chunk 161 — `shumway-compile` CLI.** Source `.pl` →
  populated `ShmoObject` → `.shmo`. `ShmoCompiler` reads
  `:- module/1` / `:- public/1` / `:- dynamic/1`, applies
  `DcgTransform`, infers per-clause head, walks the body for call
  edges (descending control / negation / `call/1`, emitting
  `Module:Goal` as qualified refs, skipping cuts). New
  `Shumway.Compile` project.

- **Chunk 162 — `:- ensure_linked/1` directive.** GNU-Prolog-style
  reachability hint for predicates invoked only via runtime
  meta-call. Recorded into `ShmoObject.EnsureLinked`; the linker
  treats every indicator as an additional root. Required adding
  `ensure_linked` as an `fx 1150` prefix operator to
  `OperatorTable.Default()` alongside `dynamic`/`public`.

- **Chunk 163 — `ShmoLinker` (reachability + missing-predicate).**
  Takes a set of `ShmoObject`s + entry points and:
  - builds the global namespace from every object's
    `:- public`/`:- dynamic` set (with `duplicate_public` collision
    detection);
  - compiles the prelude on the fly and snapshots
    `BuiltinsRegistry` + `MetaBuiltins` as the always-available
    filter;
  - walks reachability from entry points + every object's
    `:- ensure_linked` + the per-module qualified-ref list,
    resolving each edge in order: module-local / global public /
    global dynamic / builtin / prelude;
  - emits `missing_predicate` diagnostics (error, or warning under
    `AllowUndefined`);
  - drops unreachable modules with a warning;
  - serialises a `Bundle` in the existing `BundleFormat` magic +
    layout.

- **Chunk 164 — `shumway-link` CLI.** Wraps the linker. `--entry`
  is repeatable AND accepts a comma-separated list; both combine.
  `--allow-undefined` downgrades to warnings. Verbose mode streams
  diagnostics to stderr live; non-verbose mode emits the final
  summary at the end. Exit codes: 0 ok, 1 link error, 3 usage.

- **Chunk 165 — Linker async + source/file conveniences.**
  Mirrors the chunk-72 Bundler API shape so .NET callers don't
  have to shell out: `LinkAsync(LinkConfig, CancellationToken)`,
  `LinkFromFiles(paths, entries, ...)`, `LinkFromSources(...)`.

- **Chunk 166 — User-facing documentation.**
  `docs/user-guide.md`: what ships in each project, building from
  source, running the REPL, embedding the engine, the full
  `.pl → .shmo → .shum → engine` workflow with diagrams, the
  module directives reference table, a worked grandparent
  example (including the "now break it" failure case), and a
  pointer to `native-aot.md`.

- **Chunk 167 — Closure.** This document.

---

## Deliverables checklist

| Chunk | Deliverable | Status |
|---|---|---|
| 160 | `ShmoFormat` (magic + version), `ShmoObject` + `PredicateRef` + `QualifiedPredicateRef` + `ShmoDefinedPredicate` + `PredicateVisibility`, `ShmoWriter`, `ShmoReader`. | ✓ |
| 160 | 11 round-trip / magic / version / corruption tests. | ✓ |
| 161 | `ShmoCompiler.CompileSource` / `CompileFile` covering module/public/dynamic directives, DCG, call-graph extraction across control structures + qualified refs. | ✓ |
| 161 | `Shumway.Compile` CLI project producing `shumway-compile` with `-o` / `-v` / `-h`. | ✓ |
| 161 | 17 tests in `Chunk161Tests`. | ✓ |
| 162 | `ensure_linked` prefix operator in `OperatorTable.Default()`. | ✓ |
| 162 | `ShmoCompiler` records the indicators into `ShmoObject.EnsureLinked`. | ✓ |
| 162 | 6 tests in `Chunk162Tests`. | ✓ |
| 163 | `LinkConfig` / `LinkResult` / `LinkDiagnostic` / `LinkSeverity`. | ✓ |
| 163 | `ShmoLinker.Link` with global namespace build, duplicate-public detection, reachability walk, missing-predicate report, dead-code elimination, prelude/builtin filter. | ✓ |
| 163 | 16 tests in `Chunk163Tests` including a load-and-execute round-trip. | ✓ |
| 164 | `Shumway.Link` CLI project producing `shumway-link` with `--entry` (comma + repeatable), `--allow-undefined`, `-o`, `-v`, `-h`. | ✓ |
| 164 | 6 end-to-end tests driving the real CLIs through a temp dir. | ✓ |
| 165 | `LinkAsync`, `LinkFromFiles`, `LinkFromSources` on `ShmoLinker`. | ✓ |
| 165 | 6 tests in `Chunk165Tests`. | ✓ |
| 166 | `docs/user-guide.md`. | ✓ |
| 167 | This closure document; CLAUDE.md roadmap entry. | ✓ |

---

## What chunk 163 actually means

Before Phase 13, `Shumway.Bundler` already had `--entry-points`,
but it was only a *probe of existence*: the writer ran
`(pred(...) ; true).` as a setup query and considered it
"validated" if no exception fired. That doesn't detect missing
predicates inside the bodies the entry calls, doesn't surface
duplicate-public collisions before deployment, and doesn't help
the developer trim unused modules.

Chunk 163's linker is the genuine article:

- **Duplicate-public collisions** become a hard error at link
  time. CLAUDE.md's "Public predicates are globally unique"
  invariant is enforced before the bundle is even written.

- **Missing-predicate report** walks the actual call graph (built
  by `ShmoCompiler` from each clause body) from the entry points
  and `:- ensure_linked` indicators. Every unresolved edge surfaces
  with the calling predicate and the called indicator, so the
  developer can find and fix the typo / forgotten `:- public` /
  missing module without booting the app.

- **Dead-code elimination falls out for free.** Modules no root
  reached are dropped with a warning, so an `.shum` only carries
  what the program actually exercises.

The walk also follows the `Module:Goal` qualified refs collected
by the compiler, so a deliberate cross-module call resolves
through the named module's public set (mirroring how the engine
itself dispatches qualified goals).

---

## What chunk 162 actually means

`:- ensure_linked` is the hatch that keeps the static-analysis
contract honest in the presence of `call/1` with constructed
goals. A predicate invoked only via

```prolog
dispatch(X) :- G =.. [handler, X], call(G).
```

doesn't appear anywhere in the static call graph for `dispatch/1`
— the linker would drop `handler/1` as unreachable, the bundle
would not contain its module, and the engine would surface
`existence_error/2` at runtime. With

```prolog
:- ensure_linked handler/1.
```

the linker treats `handler/1` as a root, walks its call graph,
and pulls in its defining module. The directive is
file-scoped: a module that does this kind of dispatch declares
the hint itself; consumer modules don't need to know.

The fx-1150 operator parse means both the unparenthesised and
parenthesised forms work:

```prolog
:- ensure_linked foo/2.
:- ensure_linked(foo/2).
:- ensure_linked [a/1, b/2, c/3].
```

---

## What is *not* in Phase 13

- **Embedded `.shmo` bytecode reuse.** The linker copies each
  `.shmo`'s bytecode into the resulting `Bundle.CompiledBytecode`
  slot, but the engine's `LoadBundle` re-consults the source and
  re-compiles (the per-functor-id stash is used as a warm-up cache
  for Tier-1 IL, not as a skip-the-WAM-compile path). A future
  phase could make `LoadBundle` skip parsing entirely when a
  bundle's pre-compiled bytecode is present.

- **Module-qualified call-graph edges.** `QualifiedRefs` lives at
  the per-module level rather than the per-caller level. The
  linker treats every qualified ref as automatically walked once
  its containing module is reached, which is correct (over-
  approximates reachability) but slightly loses precision for
  dead-code elimination. A future phase could attach qualified
  refs to the per-caller edge set.

- **Per-predicate compaction surface for `.shum`.** Phase 11/12's
  `compact_dynamic_buffer/1` is still the only per-predicate
  surface in the engine. Bundles themselves are append-only — a
  future phase could add bundle-level incremental updates if a
  hot-deploy workflow needs them.

- **Bundle-level IL persistence in the linker.** The existing
  `shumway-bundler --with-compiled-il` produces persisted IL
  assemblies; `shumway-link` doesn't yet wire that flag through.
  Trivial follow-on, not in this phase.

- **CLP libraries as implicit available set.** The linker's
  "always-available" filter is builtins + prelude. CLP(FD) and
  CLP(R) sources have to be linked explicitly (or accessed via
  `engine.UseClpfd()` after `LoadBundle`). A future phase could
  ship the CLP sources as implicit `.shmo`s the way the prelude
  is.

---

## Test totals at closure

| Suite | Count |
|---|---|
| `Shumway.Tests.Core` | 417 |
| `Shumway.Tests.Interpreter` | 105 |
| `Shumway.Tests.Compiler` | 242 |
| `Shumway.Tests.IsoConformance` | 275 |
| `Shumway.Tests.Embedding` | 1467 |
| **Total** | **2506** |

All green at the closure tag. Phase 13 added 62 new tests
(`Chunk160Tests` 11, `Chunk161Tests` 17, `Chunk162Tests` 6,
`Chunk163Tests` 16, `Chunk164Tests` 6, `Chunk165Tests` 6).

---

## Roll forward to Phase 14+

Open candidates:

- **Skip-the-WAM-compile `LoadBundle` path.** Use the bundle's
  embedded bytecode directly instead of re-running the parser /
  WAM compiler at load time.

- **Per-caller qualified-ref edges.** Tighter dead-code elimination
  for programs that use `Module:Goal` selectively.

- **Bundle-level IL persistence in `shumway-link`.** Wire
  `--with-compiled-il` (or its successor) through the linker so
  the resulting `.shum` carries pre-emitted IL assemblies.

- **CLP libraries as implicit available set.** Treat
  `clpfd`/`clpr` like the prelude — the linker considers their
  publics available without requiring the user to add them as
  link inputs.

- **Bundle hot-update.** Allow loading a delta `.shum` over an
  already-running engine for hot deployment scenarios.
