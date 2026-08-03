# Phase 36 closure — the third-party ecosystem phase

145 commits after `phase-35`. What began as "load `use_module(library(X))` from a
directory" (ADR-038) grew into the largest compatibility campaign to date: running
**SWI, Scryer and Logtalk library code unmodified** on Shumway, with every
non-structural test failure treated as a presumed engine bug — and closing them.
Plus a debugger round the constraint work made possible: residual constraints of
attributed variables in both IDE frontends.

## 1. Library loading + scoped modules (ADR-038)

`use_module(library(X))` resolves through a configurable search path
(`file_search_path/2`, `library_directory/1`, C# API, `SHUMWAY_LIBRARY_PATH`,
shipped `lib/`). `:- module(Name, [Exports])` (2-arg) is the sole trigger for
scoped qualification: all predicates mangle `Name$x`, exports are the importable
surface, per-module import tables resolve local → imports → bare-global at compile
time (ModuleRewrite) and runtime (`$mqual`). Two modules can export the same name.
The linker resolves library dependencies C-linker-style (explicit `.shmo` →
`.shum` archive member → search-path source, `--library-dir`).

## 2. Rationals (ADR-039)

Exact `Num/Den` as cell tag 0xE mirroring BigInt (per-activation table,
trailed, GC leaf); canonical form; `rdiv`, `numerator`/`denominator`/
`rationalize`; `/` gated by the `prefer_rationals` flag (default false — no
conformance churn). Unblocked Scryer `library(arithmetic)`.

## 3. Multi-dialect shims — "unite worlds" (ADR-040)

Dialect is a per-subtree resolution context (name map + `double_quotes` + atts
API) travelling with each top-level `use_module`'s dependency graph — so Scryer
`clpz` and SWI libraries coexist in one engine. `verify_attributes/4` dispatches
PER attribute module, lifting the old "CLP(R)/CLP(FD) cannot share an engine"
restriction. `-L dir:dialect` CLI; opt-in `Shumway.Tests.DialectInterop` project
runs real Scryer clpz + SWI assoc together.

## 4. The library campaign — final state

- **SWI**: 94/129 top-level libraries load clean (rest structural:
  dicts/threads/foreign/IDE). Mechanisms: marker-based native overrides,
  SWI-scoped parser leniencies, autoload-as-eager-use_module, early
  term-expansion activation with renamed hooks. `docs/swi-library-support.md`.
- **Scryer**: 46/46 load clean, 33 runtime-validated; `ScryerShim.cs` emulates
  the Rust-VM `'$...'` natives. `docs/scryer-library-support.md`.
- **clpz certified**: byte-identical answers to Scryer on queens/perm, steady
  solve at parity (instrumented-counter proof); load 41s → ~5.7s over four
  rounds.
- **Logtalk 3.101.0**: all 240 library testers swept — **192 of 194 runnable
  suites fully green, 99.98% of individual tests (10,317/10,319)**; the 2
  residual failures are external (an upstream geojson bug; a test needing
  pwsh.exe), verified with the SWI-Logtalk oracle. Shumway now beats
  SWI-on-Windows on `os`, `tzif` and `mime_types`.
  `docs/logtalk-library-support.md`.

## 5. The engine bugs the campaign surfaced (all fixed)

The determinism arc: `<<` bignum promotion (C# `checked` does not cover
shifts); **ADR-041** dispatch-time clause selection for unindexed dynamic
chains — a single-entry chain's surviving `try_me_else` CP and the
setup-compiled-trampoline registration gap were, between them, nearly the whole
Logtalk failure tail (the `^^` send-cache and `'$lgt_current_category_'`
leaks). The phantom-nondeterminism family in enumeration builtins:
`sub_atom/5`, `atom_concat/3`, `append(-,+,+)`, `string_concat/3` now derive
mode-directed candidate sets and leave no dead choice point. `bagof/setof`
runtime fallback does real ISO witness grouping. `existence_error` translation
honours the object type (`source_sink` + culprit). `file_permission/2`,
`copy_file/2`, the Windows null device, float exponents printed with their sign
(`1.0e+300`, one formatter), live-link consult redirects a redefined
predicate's old entry, and `nl` writes byte-faithful `\n` to files and memory
captures (GNU parity — the write/read asymmetry broke every temp-file round
trip).

## 6. Debugger: residual constraints of attributed variables (wire v8, VSIX 0.28)

A CLP variable in Locals was a bare `_G12`. Now every stop projects the frame
variables' constraints (the REPL's `attribute_goals` projection) and shows
per-variable rows — VS: `X ⟨constraints⟩ = X in 1..6, X#<Y` appended to Locals;
VS Code: a "Constraints" scope next to Locals. The Immediate window / Debug
Console / breakpoint conditions are attvar-aware via a cross-activation
**attvar transplant** (attribute graphs rebuilt over the evaluation's fresh
variables; per-activation FOREIGN payloads re-registered in place by
`'$dbg_fix_foreign'/1`). Set Next Statement composes: a backward rewind unwinds
the trailed attribute mutations (test-pinned: no doubled propagators on
re-run). Smokes: VS main 7/7 + new `run-attvar-smoke.ps1` green.

## Gate at close

ISO 298 / Core 444 / Interpreter 105 / Compiler 364 / Embedding 3726 /
DialectInterop 9 — all green. VS smokes: main + attvar green on wire v8.

## Deferred

- `M:goal` qualified-call syntax (ADR-038).
- Baked-C# libraries through separate compilation (ADR-038).
- SWI `=>` (SSU); marketplace packaging for the VS Code extension.
- Logtalk adapter flags (`encoding_directive`/`tabling`/`unicode`) need real
  compile-chain wiring before flipping.
