# Phase 26 — Closure

**Status**: complete.

**Tagged**: `phase-26`.

Phase 26 is a **WAM codegen-quality** phase. Where Phase 25 added the
arithmetic instruction set (ADR-018) and inline compound *representation*
(ADR-017), Phase 26 makes the *generated code* tight — driven by a
predicate-by-predicate comparison of our WAM against GNU Prolog's `pl2wam`
on a real ~2570-line program (Blint), recorded in
[`docs/wam-vs-gprolog-blint.md`](wam-vs-gprolog-blint.md).

**Headline result**: over the 89 Blint predicates GProlog's `pl2wam` compiles,
Shumway now emits **3319 non-index WAM instructions vs GProlog's 3769 (−12%)** —
ahead of or at parity with GProlog on every clause shape, and *beating* it on
arithmetic, clause-prologue fusion, and common-subexpression sharing.

Nine chunks (307–315):

| # | Chunk | What it adds |
|---|-------|--------------|
| 307 | inline `=/2` | `X = T` compiles as head-style get/unify instead of a call to the `=/2` builtin |
| 308 | single `ClausePipeline` | one canonical DCG+meta+phrase+mode transform; the disassembler now shows exactly what executes |
| 309 | neck-cut transparency | a neck cut doesn't end a WAM chunk; the Warren scheduler targets the post-cut call |
| 310 | constant folding | `A is 1*2` → `get_integer 2` (compile-time eval, no runtime op) |
| 311 | compact `a_int_*` | pack the operand kinds + op into one word: `a_int_bin` 29→17, `a_int_cmp` 21→13 bytes |
| 312 | A+B (Blint) | neck-cut clauses lose the empty frame and extract args straight into call registers |
| 313 | D (Blint) | verified: no `unify_local_value` needed — permanents are heap-allocated |
| 314 | C / ADR-019 | inline nested compound build/match (`unify_structure` / `unify_list`) |
| 315 | CSE | a repeated head-arg compound is shared via `unify_value`, not rebuilt |

## The Blint vs GProlog comparison (the spine of the phase)

The user's instinct — *"look at the WAM GProlog generates for Blint and find
where it optimises things we don't"* — drove the phase. Two findings reframed
everything:

1. **The disassembler was lying.** It ran only `DcgTransform` while the engine
   ran the full pipeline, so a control construct appeared as a raw `;`/`->`
   instead of the lowered `$disj`/`$neg` helper the engine actually compiles.
   That divergence had wasted a long multi-session debugging effort on the
   chunk-model refinement. Chunk 308 fixed it with one canonical
   `ClausePipeline` both the engine and disassembler call — and revealed that
   Shumway *already* lowers if-then-else to compile-time helpers like GProlog.

2. **The earlier GProlog premise was false.** Re-reading the `pl2wam` dump
   showed GProlog does NOT keep cross-arithmetic variables in X registers — it
   compiles `is`/`=<` as *calls*, so those vars are permanent (Y-slots), exactly
   like our conservative model. The X-register "win" we had chased for sessions
   did not exist; our allocator already matched GProlog, and our inline `a_int_*`
   *beats* it.

With the disassembler truthful, the real bug behind the chunk-model breakage was
visible (chunk 309): the Warren argument scheduler bailed on a leading neck cut,
so the chunk-0 call after a neck cut got its argument shuffle in naive order and
clobbered a head var. Fixing the scheduler made neck-cut transparency safe —
closing the multi-session arc.

The four gaps the comparison then surfaced (`docs/wam-vs-gprolog-blint.md`):

- **A** — preferencing past a neck cut (chunk 312): `p :- !, recur(Args)` now
  extracts `Args` straight into the recursive call's argument registers (no
  `put_value`), like GProlog.
- **B** — empty-frame elision (chunk 312): `needFrame` keys off a real CALL
  before the last goal, not goal count, so a neck cut + single tail call needs
  no `allocate [0]`. Blint empty frames 26 → 4.
- **C** — inline nested build (chunk 314, ADR-019): `unify_structure` /
  `unify_list` build/match a nested compound in the LAST argument position in
  the same write stream, dropping the per-level temp + `get_structure`. Blint
  `get_list`+`get_structure` 2812 → 1387 (−51%), total WAM 16008 → 14582.
- **D** — globalisation (chunk 313): no change needed; Shumway heap-allocates
  permanents (`put_variable_y` → `AllocateHeapUnbound`), so `unify_value_y` into
  a heap structure can never dangle — classical WAM's `unify_local_value`
  problem cannot arise.

## Where we beat GProlog

- **Arithmetic** — inline `a_int_*` / `a_eval_*` (zero heap, zero call) vs
  GProlog's `put_structure (-)/2` + `call((is)/2)` and a heap expression term.
- **Clause prologue** — fused `allocate_get_level` / `deallocate_proceed`.
- **CSE** — a head-arg compound rebuilt in the output (`tok(token(L,eof), _,
  [token(L,eof)])`) is shared via `unify_value` rather than rebuilt; GProlog
  rebuilds it.

## New architecture

- **ADR-019** — inline nested structure construction (`unify_structure` /
  `unify_list`, opcodes 0x4B/0x4C). Last-argument position only, leveraging the
  ADR-017 contiguous inline cons, so the build is linear and needs no
  write-pointer stack.

## Measurement note

Wall-clock on this laptop has ~40% run-to-run / thermal variance (proven each
time by a byte-identical `nreverse` swinging that much between back-to-back
runs), so these codegen wins are reported on the **deterministic** metric —
instruction count — not wall-clock, which stayed within the noise band. The
size wins are real and provable; their throughput effect is below this machine's
measurement floor.

## Items NOT in this phase

- Non-last-argument nested-compound inlining (would need a write-pointer stack)
  — deferred; the last-arg / list case is the pervasive one.
- A general GProlog-style register allocator with X-register preferencing for
  cross-arithmetic vars — **refuted, not deferred**: GProlog doesn't do it and
  our conservative model already matches it (see
  `chunk-model-refinement-failed` for the full arc).
- Runtime-promotion mutation `Call → CallIl` (carried from Phase 20).
