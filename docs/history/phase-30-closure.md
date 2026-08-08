# Phase 30 — Closure

**Status**: complete.

**Tagged**: `phase-30`.

Phase 30 opened as *Arity/Prolog32 compatibility, round 2* — widening the Phase-24
source-compat work against real Arity programs (`C:\Arity`, `C:\temp\test`,
`C:\temp\testGen`, `C:\temp\testProcDotNet`). It grew well past that: an efficiency
audit, a fourth CLI tool, three ADRs delivered end-to-end (embedded native C,
generic term interop, dynamic predicates in IL), and a runtime-correctness arc that
the real-program validation surfaced (float literals in IL, source-less-bundle
literal remapping, the PSTR `==` overflow). Chunks 425–444 plus the unnumbered
native-C / reftype / float / literal arcs.

| # | Chunks / arc | Theme | What it adds |
|---|--------------|-------|--------------|
| 1 | 425, 436–441 | **`arity_compat` flag + Arity source compat** | the off-by-default flag: `$…$` quoted atoms, `#line` markers (consumed + position-honoured, leading-whitespace tolerant), annotated directive indicators (`foo/8:far`), `extrn`/silent-ignored directives, backquote char literals, literal-backslash `'…'` atoms, `:- define(A=B)` substitution, `$`-terminated symbol runs, embedded native `{…}` goals, quoted-`!` lists, trailing commas, DCG string terminals, per-file module identity, arity-meta-called undeclared facts → implicit empty dynamics. `C:\temp\test` 245/245 + `testGen` 311/311 compile clean |
| 2 | 426–433 | **efficiency audit** (4-agent codebase sweep) | IL cursor jump tables; assert fast path; Core batch (dense FunctorTable, inlining); interpreter batch (opcodes RENUMBERED contiguous → one dense jump table — CLAUDE.md invariant updated); per-query setup caching; retract/Materializer pooling (found AtomTable.Sweep is unwired in prod); Tier-B (findall pooling, `_attrTrailLog` unbounded-growth fix); `TryDescribe*` memoization (LINK ~5× faster, .shmo byte-identical) |
| 3 | 434–435, 442–444 | **CLI + librarian** | wildcard inputs for `shumway-compile`/`shumway-link`; `shumway-lib` (4th CLI) packaging `.shmo` into a runnable `.shum` archive with NO dead-module pruning (the `ar`/`.a` model, zero duplication, byte-identical extract); `shumway-link` accepts `.shum` libraries (C-archive semantics: FIFO on-demand pull, transitive, before LTO); `--map` lists pulled members; `--foreign-dll` honoured by the pull pre-pass |
| 4 | ADR-022 | **embedded native C** (`:- c` / `{…}`) → IL | two-parser capture, guard-driven type/mode inference, portable `'$native_run'` dispatch, interpreted quick-win (compiled thunk), shared codegen (Expression delegate at runtime promotion), **IL inline** both runtime-promotion AND build-time persisted (cross-assembly MemberRefs, no patch entry). Fails loudly, never a silent no-op. Bundle + `--exe` run native blocks source-less |
| 5 | ADR-024 | **generic term interop** (reftype tier) | `TermSlot` zero-copy cursor; the Arity `*_c` compat layer (`findtype_c`/`getint_c`/…) AND the native `TermSlot` API; interface predicates dropped by name; reftype globals as on-demand slots (bundle-safe); the **string-holder model** (Arity `char*` buffers, copy-not-alias); FULL IL coverage — reftype blocks compile to a delegate, and at Tier-1 the block + `fill_par`/`reftype_term` fuse into one IL method (4.53× over interpreter). `prlg_ifce.pl` + the corpus interop sources compile + run |
| 6 | ADR-023 | **dynamic predicates in Tier-1 IL** | a read-hot, mutation-cold `:- dynamic`/`:- visible` predicate runs as a static-style IL snapshot, evicted on the first assert/retract (churn-guarded). PRIMING: a predicate declared WITH clauses promotes on call 1; the snapshot WAM/IL is dumpable; and it is BAKED into `--with-compiled-il`/`--exe` persisted bundles (registered at load, evictable) so it runs as IL with no warm-up. Beyond GProlog (which runs all dynamics interpreted) |
| 7 | (corpus arc) | **`:- visible` = exported-mutable, not static** | chunk-265 had mis-aliased `:- visible` to `:- dynamic`-as-static, peeling clause-bearing visible predicates out of WAM/IL (real ProcDotNet corpus sources: `--dump-wam` showed 0 predicates). Now visible is dynamic (Arity's modifiable "visible table"), so its clauses run as a primed/bakeable snapshot — compiled AND mutable |
| 8 | (float arc) | **float literals in Tier-1 IL — all paths** | `get_float`/`put_float` emitted by value-baking (`ldc.r8` — process-independent, no Phase-17 patch). Driven into runtime promotion, `--dump-il`, and persisted bundles via a per-predicate float-pool resolver; `ComputePoolFree` makes float-only predicates cacheable. Corpus `--dump-il` skips 20/19 → 0 |
| 9 | (correctness) | **source-less-bundle + PSTR fixes** | (a) a source-less precompiled module's module-local float/string/bigint literal ids are REMAPPED into the engine's shared pools at load (`RemapPrecompiledLiterals`) — fixes a static `X =:= 2.5` reading the wrong float; (b) the two precompiled-bytecode decode sites unified into `DecodeAndRegisterPrecompiledModule`; (c) `==`/structural-equality over PSTR strings no longer infinitely recurses (it was routed through the `Tag.Str` functor+args comparator) — now walks the packed-string spine + compares final tails |
| 10 | (tooling) | **`--dump-wam`/`--dump-il` polish** | auto-name to `<source>.wam`/`.il` (wildcard-safe); show dynamic/visible snapshot predicates; print WHY a predicate is skipped (naming the unresolved callee); emit cross-module calls as runtime fid-dispatch (like the WAM) so the dump is never silently empty |

## End state

- Four Arity corpora compile/link/run: `test` 245, `testGen` 311, `testProcDotNet`
  31, plus `prlg_ifce.pl` and the ProcDotNet corpus interop sources.
- Three ADRs delivered end-to-end (022 native C, 023 dynamic-in-IL, 024 reftype),
  each verified through bundle + `--exe`.
- Gate green at close: **Embedding 2542 / Compiler 302 / Core 432 / ISO 277**.

## Deliberately deferred (out of scope, not blocking)

- ADR-024 **materializer/dematerializer** tier (C#-trampoline-to-native-C via
  P/Invoke, a physical `Reftype` struct) — designed-for, not implemented.
- Replay of **runtime-effective flags** (e.g. `unknown`) in source-less `--exe`
  bundles — the host sets them at init; only `--exe`-without-init would need
  serialization, and no real program requires it yet.
- The Arity silent-ignore directive set (`disable_lint_error`, …) pending the user
  naming entries.
- Efficiency-audit deferrals (loop-preamble fold, Engine LOH pooling, findall
  `Cell[]` redesign).
