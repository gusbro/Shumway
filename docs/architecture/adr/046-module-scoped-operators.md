# ADR-046: Module-scoped operator tables

## Status

Shipped (2026-08-18, branch `module-conformity`). Engine, consult pipeline,
runtime `op/3`/`current_op/3` context, and separate compilation all
implemented; gates green (ISO, Compiler, Embedding full, Logtalk-battery
sample unchanged). The full SWI/Scryer library-campaign re-runs remain for
the arc's close.

## Context

Shumway's operator table is a single global: `:- op/3` anywhere changes how
every subsequent clause in every file parses. That is what SICStus, Quintus
and GNU Prolog do — and it is the behaviour ISO 13211-1 describes, but only
because **ISO defines no module system at all**: with one flat program there
is nothing else an operator table *could* be. "Global is the ISO reading" is
therefore not an argument in either direction once modules exist; the only
real ISO obligation is that a program which never uses `:- module/2` must
behave exactly as 13211-1 says.

The systems that do have modules split cleanly by generation:

| System | Operator scope |
|---|---|
| SICStus / Quintus | global ("operators are global, as opposed to being local to the current module, Prolog text, or otherwise" — SICStus manual, mpg-ref-op) |
| GNU | global (no module system) |
| SWI-Prolog | per-module, exportable; `:- op(P,T,user:Name)` escapes to global |
| YAP, Ciao | per-module (SWI-family model) |
| Scryer | per-module, exportable in the module's export list |
| Logtalk | entity-scoped (same idea, finer grain) |

Every system designed in the last two decades chose module-local. The reason
is hygiene, and we have already been bitten by its absence: loading Scryer's
`dcgs.pl` — whose export list carries `op(1105, xfy, '|')` — under a global
table would change how `(a | b)` parses in *unrelated* files consulted
afterwards. Today we sidestep that per-library in the dialect shims; the
Scryer conformity suite's test_285 divergence is the same issue surfacing as
a test failure. Any SWI or Scryer library that declares operators re-opens
the problem.

Two prior decisions interact with this one:

- **ADR-038** gave modules an export surface, per-module import tables and
  separate compilation; the `.shmo` already persists a per-module operator
  list (`ShmoOperatorDef`) so the linker can *recompile* a module with its
  own syntax. What stays global today is only the *application* of those
  operators to the reader at consult/load time.
- The module-conformity arc aligned `current_predicate`/`predicate_property`
  doctrine with SICStus. This ADR deliberately departs from SICStus on
  operators: predicate visibility doctrine and syntax scoping are separable,
  and on syntax the hygiene argument wins.

## Decision

Adopt the SWI-family model: **operator tables are module-scoped with
inheritance from `user`, plus an explicit escape for global declarations.**

### 1. Layered tables, `user` as the base

Every module `M` gets an operator layer. The *effective* table used to read
`M`'s text is `M`'s layer over the `user` table; the `user` table starts as
the ISO/default table and is what module-less text reads with.

- `:- op(P, T, Name)` in `M`'s text defines in `M`'s layer only.
- `:- op(P, T, Name)` in bare (module-less) text defines in `user` —
  observable behaviour identical to today, which is the ISO guarantee.
- A local definition shadows an inherited one, including removal:
  `:- op(0, T, Name)` inside `M` hides `user`'s definition for `M` only.

### 2. The global escape (the mechanism other Prologs use)

`:- op(P, T, user:Name)` — executed anywhere, directive or goal — defines in
the `user` table, hence visible to every module that does not shadow it.
This is SWI's exact mechanism and is the migration path for SICStus/GNU-style
code that genuinely wants a program-wide operator from inside a module.

### 3. Exportable operators

A module's export list may carry `op(P, T, Name)` terms alongside predicate
indicators (SWI and Scryer share this convention; Scryer's `dcgs` is the
motivating example). `use_module(library(X))` — both forms — installs the
exported operators into the **importer's** layer (into `user` when the
importer is bare text or the REPL, matching ADR-038 component 4's
import-into-user behaviour). A filtered import list may name `op(P,T,N)`
terms to select them.

### 4. Reading, writing, reflection

- `read_term`/`read` and `write_term`/`writeq`/`print` use the effective
  table of the **context module** (the same compile-time context injection
  that ADR-038/module-conformity gave `clause/2` and `current_predicate/1`);
  where no module context exists (host API calls, streams read outside any
  module) the context is `user`.
- `current_op/3` enumerates the context module's effective view.
- `listing/1` and the debugger render with the listed predicate's module
  table.

**Precision that emerged in implementation** — *where imported operators
land*: a `use_module` from inside a module installs the exported ops in
that module's layer; a **top-level** import (goal-form `use_module/1`, or
directly consulting a module file, which auto-imports its exports) installs
them in `user` — SWI's exact behaviour. Consequence: Scryer's conformity
test 285 (`(a|b)` must stay a syntax error after loading `dcgs`) remains
divergent in the direct-consult shape, because the suite itself is
directly consulted and `dcgs`'s exported `'|'` op legitimately lands in
`user` — the same thing happens on SWI. What ADR-046 does fix is the
hygiene leak: files and modules that never import the library no longer
see its syntax.

### 5. ISO conformance is preserved by construction

A program that never mentions `:- module/2` lives entirely in `user`: one
table, ISO semantics, byte-for-byte today's behaviour. The Neumerkel suite,
the ISO conformance project and the Logtalk `tests/prolog` battery all run
module-less and must stay green unchanged — they are the regression gate for
this guarantee.

## Consequences and impact map

- **`OperatorTable`** grows a parent link (module layer → `user`). The
  common case — a module with no local operators — points its reader at the
  `user` table directly (reference identity), so the hot lookup path pays
  nothing; only modules that actually declare operators pay one extra
  dictionary probe on a miss. Measure with the parser benchmarks anyway
  (wall-clock A/B, back-to-back).
- **`ClauseReader` / consult pipeline** select the layer from the module
  being read; the `:- module/2` directive switches layers mid-consult (the
  existing pre-pass already flips reader state mid-file for
  `set_prolog_flag`, same seam).
- **`.shmo` / `.shum`**: `ShmoObject.Operators` already recorded the
  module's own definitions — the load-time replay changed target: an
  export-qualified module's ops go to its layer; bare-global text's ops go
  to `user` (matching where its consult defined them). Exported-ness rides
  the EXISTING `ShmoOperatorDef.Type` string as a `*` suffix (`"xfx*"`),
  so neither format's shape changed and the two byte-identical `.shum`
  writers stayed untouched. `LoadBundle` reconstructs the exported-op
  registry from the marker, and `use_module(library(X))` gained an
  already-loaded-module resolution step (import from the live manifest —
  predicates and operators — with no file involved), which a loaded
  bundle's modules needed.
- **ADR-038 import tables** gain operator entries so the linker's
  recompile-the-importer step parses with the imported syntax — today this
  works by accident of the global table.
- **ADR-040 dialect shims**: the Scryer shim stops needing operator
  special-casing; loading `dcgs` under `dialect:scryer` scopes `'|'` to its
  importers and the test_285 divergence disappears (re-run the Scryer suite
  to reclassify). Shims emulating global-op systems (GNU, SICStus) define
  into `user` explicitly.
- **REPL** reads queries in `user` context; `use_module` at the top level
  already imports into `user`, and exported operators ride along — an
  interactive session behaves like SWI's.
- **Campaign gates for the arc**: full Neumerkel + ISO suites (module-less
  guarantee), the Scryer conformity suite (expect 172 → higher; test_285
  resolves), the SWI/Scryer/Logtalk library sweeps, and the module-conformity
  Embedding tests.

## Alternatives considered

- **Stay global, document the divergence** (status quo). Rejected: the
  dcgs-class problem recurs with every operator-declaring SWI/Scryer
  library, and each recurrence costs a shim special case; hygiene only gets
  more valuable as the library surface grows.
- **Global table with consult-time save/restore** (scope ops to the file
  being read, restore afterwards). Rejected: fixes consult ordering only —
  runtime `read_term`/`write_term` and cross-module rendering still leak,
  exports become impossible to express, and it matches no other system, so
  it buys no compatibility.
- **Follow SICStus (global) for doctrine consistency.** Rejected: predicate
  visibility doctrine and syntax scoping are independent choices; SICStus
  itself is the outlier among moduled systems here, and the `user:` escape
  keeps every SICStus-style global declaration expressible.
