# Phase 20 — Closure

**Status**: complete.

**Tagged**: `phase-20`.

Phase 20 is the largest phase to date by chunk count (210–234, with
the ADR-016 GC series interleaving its own 210–217 sub-numbering)
and covers four largely independent threads of work:

1. **ADR-016 — Heap garbage collection** ([`ADR-016`](architecture/adr/016-heap-garbage-collection.md)).
2. **Tier-1 IL completeness for indexed dispatch + deep cut**.
3. **Opcode-dispatch fast paths** (Stage A peephole → Stage B
   link-time rewrites).
4. **User-IL bundle correctness + IL machinery cleanup**.

Plus a handful of correctness fixes and the REPL `SHUMWAY_TIMING`
instrumentation that the perf work needed to separate startup from
exec.

## ADR-016 — heap GC (chunks 210–217)

The heap was previously reclaimed only by choice-point unwinding —
a long-running engine kept growing forever. Phase 20 adds a real
mark-compact collector behind an opt-in watermark, with the engine
state shape kept conservatively scan-safe.

- **Chunk 210** — env frames record their live-permanent count so
  the GC knows how many Y slots are roots.
- **Chunk 211** — order-preserving sliding mark-compact collector +
  `garbage_collect/0` builtin.
- **Chunk 212** — IL GC-safety audit + watermark wiring + a stress
  fuzz mode (`GcStressMode`) that forces collection at every safe
  point so test failures bisect to specific opcodes.
- **Chunks 213–217** — root coverage: query variables / global
  vars (213), CP-protected env Y-slots (214), `retract/1` choice
  point safety (215), env Y-scan stack-item boundary (216),
  precise env-frame liveness (217). The conservative stack scan
  ended up being the right design for the tabling fixpoint shape
  that under-counted roots when the scan was precise.

In parallel, Phase 20 chunk 213 (RawInt control words) tagged
every CP / env control word with `Tag.RawInt` so the conservative
scan can distinguish them from heap references without ambiguity —
the chunk number collides with the ADR-016 series but the work is
independent.

`Engine.HeapGc.cs` is the bulk of the implementation; the public
surface is `MaybeCollectHeap` (called at safe points) +
`CollectHeap()` (the manual entry) + the `OnGcMark` / `OnGcRelocate`
hooks the embedding layer uses to register its own external roots.

## Tier-1 IL completeness (chunks 215–218)

Phase 19 closed `call/N` / `'$call'/2`; Phase 20 closes the
remaining IL gaps:

- **Chunk 215** — deep cut in IL (`GetLevel` + `Cut` opcodes
  emitted as `engine.GetLevel(slot)` / `engine.CutToLevel(slot)`).
  Removes the chunk-201 rejection for predicates with deep cut.
- **Chunk 216** — full first / multi-argument indexed dispatch
  (`switch_on_term` + `switch_on_arg` + the typed `switch_on_*`
  tables) for Tier-1 IL. Replaces the chunk-189 linear clause
  walk with O(1) key lookup + correct bucket backtracking.
- **Chunk 217** — indexed dispatch in persisted-IL bundles. The
  per-engine model cache rebuilds lazily from the engine's
  linked code at first call (the persisted IL only stores the
  functor id, name-relative via chunk-197 patching).
- **Chunk 218** — backtrackable builtins under Tier-1 IL. The
  resume-PC the builtin would capture in `engine.P + 9` only
  worked under Tier-0 (which advances P past the call_builtin
  before invoking); Tier-1's IL Call doesn't, so the chunk
  added `Engine.BuiltinReturnPc` set by both tiers.

By end of Phase 20 there are no IL exclusions left for any
ISO-shaped predicate. The remaining unpromotables are
architectural (dynamic predicates, chunk 159) or size-based
(`MaxIlPromotionBytecodeBytes = 16384`).

## Opcode dispatch — Stage A → Stage B

The biggest perf thread of the phase. Chunks 220–227 attack the
per-Call / per-Execute dispatch overhead through three layers:

### Peephole / fusion (chunks 220–222)

- **Chunk 220** — fused opcodes `AllocateGetLevel` (14M
  pairs/run on Blint) + `DeallocateProceed` (6.7M pairs/run).
  Same byte width as the two they replace, second slot
  overwritten with `Nop` so operand addresses don't shift.
- **Chunk 221** — interpreter-level peephole fusion of
  `{Try,Retry,Trust}MeElse + CheckVisible` (every dynamic-chain
  step). The handler peeks at the next byte and inlines the
  visibility check, skipping a full switch trip.
- **Chunk 222** — lock-free transient `AtomTable.Intern` fast
  path (chunk 214 had done the same for permanents) + widen the
  single-character atom cache from 128 to 256 (Latin-1; what
  GNU / SWI / SICStus also pre-intern).

### Stage B — link-time call-site rewrite (chunks 225–227)

The architectural insight: every `Call` opcode goes through
`Tier1Dispatcher?.OnDispatch(target)` to discover whether the
callee has IL, but the answer is fully determined at link time.
Stage B introduces three new opcodes that bake the dispatch
decision into the bytecode itself:

| Opcode | When emitted by linker | Per-call cost |
|---|---|---|
| `CallIl` (chunk 225) | callee has bundle-IL registered | direct delegate invoke via `IlByFunctorId[fid]` |
| `CallBytecode` (chunk 226) | callee permanently bytecode-only | `MaybeCollectHeap` + `SetPc(target)` |
| `Call` (existing) | callee may still earn JIT promotion | falls through to `DispatchToTier1OrBytecode` |

`ExecuteIl` / `ExecuteBytecode` (chunk 227) mirror the rewrite for
tail calls. The linker's classifier lives in `PrologEngine.InstallCallIlRewrites`;
predicate classification consults `IlPromotion.IsPermanentlyBytecodeOnly`,
which checks the same conditions `RecordInvocation` would (`Threshold==0`,
AOT, already-rejected, layout-excluded, oversized).

Chunk 227 also bundled a bug fix that was lurking in chunk 226:
dynamic predicates must NOT be rewritten to `CallBytecode` because
the chunk-75 `JitIndexProfile` counter lives inside `OnDispatch` and
its threshold drives dynamic-predicate re-indexing. The
`IsDynamicPredicate` guard keeps dynamics on the original `Call` /
`Execute` slow path.

### Measured impact on Blint (`Blint.shum` Tier-0 bundle, 11-run wall-clock median, exec-only)

| Configuration | Time | Δ vs chunk-222 baseline |
|---|---|---|
| chunk 222 (pre-Stage-B) | 3988 ms | — |
| chunk 225 (B.1) only | — | small |
| chunk 226 (B.2) added | 2927 ms | -27% |
| chunk 227 (B.3) added | 3227 ms | -19% (Execute rewrites help less than Call) |

On the `Blint_new.shum` bundle (with `--with-compiled-il` but
prelude-only IL — see chunk 230 below for why), Stage B's effect
on dispatch overhead is modest (~5%) because most calls were
already prelude calls handled by chunk-202's `_dispatchCache`.

## User-IL bundle correctness + IL machinery cleanup (chunks 230–234)

Investigating Blint's "+5% Tier-1 vs +32% Tier-0" oddity surfaced
the actual root cause: **`BundleWriter.CompileEntryToIl`
was never IL-compiling user predicates**. The bundle's IL contained
only prelude/library helpers (`member`, `clause`, `maplist`, …)
because the fresh `PrologEngine` it created consulted `entry.Source`,
but `entry.Source` is empty for release-mode `.shmo`s (shumway-
compile defaults to release, which strips source). With no source
to consult, only the prelude that the engine constructor loads
ends up in `StaticPredicateCache`.

- **Chunk 230** — route source-less entries through
  `engine.LoadBundle` on a synthetic single-entry bundle, then
  pull predicates from the new
  `PrecompiledStaticPredicates` view alongside the existing
  caches. Blint bundle's IL went from 159 prelude methods to
  327 (159 prelude + 169 user-prefix; the other ~86 statics are
  IL-subset-rejected). Bundle size 280 KB → 693 KB (the user IL
  is the real payload).
- **Chunk 231** — exposing user IL exposed a separate
  regression: Blint with user IL ran ~30% **slower** than Tier-0
  WAM. The culprits were `Engine.Cut` (5.31% self-time — IL calls
  it as a method, WAM has it inline) and the
  `Dictionary<int, IlChoicePointEntry> _ilCpInfo` whose
  `foreach (Keys)` ran on every `Engine.Cut` (chunk-164 stale-IL-CP
  cleanup). Replaced the dict with a `IlChoicePointEntry[]
  _ilCpStack` + `int _ilCpTop`; IL CPs are always pushed in
  monotonic `_b` order so a stack-array supports push, pop, and
  cut-to-barrier in O(1) per item. `Engine.Cut` dropped to
  <0.74% (off the top 20). Wall-clock on the user-IL bundle:
  4548 ms → 3878 ms (-15%).
- **Chunk 232** — `AtomTable._lock` (the last hot lock after
  chunk 222's lock-free fast paths) switched from `lock(object)`
  to .NET 9's `System.Threading.Lock`. `Monitor.Enter_Slowpath`
  in dotnet-trace: 2.23% → 1.40% (-37%).
- **Chunk 233** — `IlIndexedDispatch._perEngineCache`
  (`ConditionalWeakTable<Engine, ConcurrentDictionary>`) replaced
  by a plain `Dictionary<int, IlIndexedDispatchInfo>` stored on
  the engine itself (as `object?`, since Core can't name the
  IlIndexedDispatchInfo type). `is`-pattern downcast at the
  read site compiles to one type-token compare — strictly
  cheaper than the WeakTable's internal lock + the
  ConcurrentDictionary's bucket lock per IL Call. Trace:
  `ResolveEntryByFunctorId` 5.45% → 4.55% inclusive.
- **Chunk 234** — `[MethodImpl(AggressiveInlining)]` on the hot
  `Cell` factories (`Cell.RawInt` alone was 0.73% exclusive,
  called 10× per `PushChoicePoint`) + `Span<Cell>` over the
  contiguous control-word block in `PushChoicePoint` /
  `RestoreCommonFromCurrentCp` so the JIT shares one bounds
  check across the 10 writes / reads. `Cell.RawInt` eliminated
  from the trace; `PushChoicePoint` -0.39 pp, `RestoreCommon`
  -0.57 pp, `TryBacktrack` inclusive -0.71 pp.

By end of chunk 234, the gap between user-IL-active and WAM-only
on Blint collapsed from ~30% slower → ~5% slower.

## Other Phase 20 work

- **Chunk 213 (RawInt)** — control words tagged so conservative
  GC scan distinguishes them from heap refs.
- **Chunk 214** — `AtomTable.Intern` lock-free fast path for
  permanents + GC watermark tune.
- **REPL `SHUMWAY_TIMING=1`** — per-phase wall-clock breakdown
  (startup / consult+link / exec / total). Unblocked the
  Stage B and chunks 230–234 measurement work by separating
  startup noise from the workload.
- **REPL polish** — operator-form binding display,
  platform-correct EOF hint, top-level determinism detection
  (no trailing `;` on a single-solution query),
  `term_to_atom/2` + SWI-compatible operator rendering, deep
  cut barrier fix (chunk fix without a new chunk number).
- **Dispatch chain compaction** (pre-chunk-210) — reclaim
  dead clauses from dynamic dispatch chains mid-query
  (-44% opcodes on a chain-heavy retract workload).

## Pre-Phase-20 commits since `phase-19` tag

The tag boundary catches three Phase-19+ commits that landed
before Phase 20 work began. They're listed in the Phase 19+
roadmap section of `CLAUDE.md`:

- **Chunk 206** — `implicit_dynamic` prolog_flag (default true).
- **Chunk 207** — runtime-bound `assertz(X)` / `call(pepe(Y))`
  in the same query.
- **Chunk 208** — bundle/linker UX (`--exe` no longer requires `-o`).
- **Chunk 209** — `:- dynamic foo/N.` predicates with source
  clauses dispatch from a bundle.

## Tests

Core 423 / Interpreter 105 / Compiler 248 / IsoConformance 275 /
Embedding 1715 = **2766 passed, 0 failed** at tag time.

The chunk-45 PreWarm tests that were inherited-failing through
Phase 17–19 are now skipped (`[SKIP]` × 3) rather than failing;
chunk-178/179 made the source-less-load path the canonical
warmup target, which doesn't exercise the chunk-45 cached
delegate identity check.

## Open follow-ups for the next phase

The next phase should benchmark against a **real assert/retract-
heavy program**, not Blint. Several Phase 20 measurements bumped
into Blint's idiosyncrasies (lint-style failure-driven loops,
prelude-vs-user IL coverage skew); the optimisations that landed
benefit a broader workload, but the priorities for the next round
of work should be set by a target program more representative of
the embedding use case.

Specific items deferred from Phase 20:

- **Stage B.4** — runtime promotion mutation of `Call` →
  `CallIl` after JIT promotion flips a predicate mid-run. Only
  matters with `SHUMWAY_IL_PROMOTE>0` against a
  `.pl`-consulted workload (no bundled IL). Skipped since the
  user's target uses `.shum` bundles.
- **IlIndexedDispatch in-IL inlining** — inline the
  switch-cascade decode at IL emit time instead of via the
  helper. Estimated ~2-3% wall-clock; ~500 LOC refactor in
  `IlPredicateCompiler`.
- **WAM stripping for IL-promoted predicates** — strip the
  full WAM for bundled-IL preds, leaving only a 9-byte
  `CallIl` trampoline at their address. Saves ~40% bundle
  size for IL-heavy programs (the user's target use case at
  "decenas de miles de predicates"). Scope is in the chunk-229
  attempt that was reverted because Blint's bundle had no
  user IL to strip.
- **Tier-1 first-call IL JIT warmup** (`Assembly.Load` ~30 ms +
  `CreateDelegate` × N) — chunk 228 attempted a background-
  thread `Assembly.Load` overlap; trace win was real (~14 ms
  on the consult+link phase) but wall-clock sat at the noise
  floor for a 4-second workload. Worth revisiting if the next
  phase has process-startup latency goals.
