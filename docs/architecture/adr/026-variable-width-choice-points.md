# ADR-026: Variable-width choice-point frames (drop ViewGen for static callees) — verdict

## Status

Rejected ([Phase 33](../../history/phase-33-closure.md); on a measured ceiling).

The soundness
analysis and the implementation blueprint below are kept complete so the
decision can be revisited cheaply if the workload evidence ever changes (see
*Revisit triggers*). The one change that shipped from this ADR is
documentation: the CP-layout comment in `Engine.cs` was stale (it omitted the
`B0` slot and mis-stated the ViewGen semantics) and now matches the code.

## Context

The 2026-06-30 audit (Phase 33 backlog item I6) flagged that every choice
point saves **10 control words** — including `ViewGen`, the ADR-015 logical
-update-view timestamp — even for static predicates that never execute a
`check_visible`. The actual frame (ADR-005, extended by ADR-015 chunk C and
the deep-cut `B0` slot):

```
[arity | A1 .. An | CE | CP | B | BP | BindingTrailTop | ExtraTrailTop |
 HeapTop | Hb | ViewGen | B0]                    — CpSize = 11 + arity cells
```

The proposal: a narrow 10-word frame for static callees, keeping the wide
frame only where ViewGen is semantically needed. The backlog deferred it as
"high-blast-radius for one saved word; wants an ADR + the full gate".

## Soundness analysis — where is ViewGen actually needed?

This is the durable part of this ADR. Verified against the code (2026-07-05):

**`CurrentViewGen` has exactly one reader**: the `CheckVisible` opcode — two
interpreter sites, the plain handler and the `TryInlineCheckVisible` peel that
the `TryMeElse`/`RetryMeElse`/`TrustMe` handlers call when a `check_visible`
is adjacent. **Writers**: `EnterDynamic` (samples `DbGeneration`) and the CP
restore (`RestoreCommonFromCurrentCp`). The `ViewGenOf(cpBase, arity)` helper
has **zero callers**. Nothing else — not `clause/2`, not `retract/1`, not the
tabling machinery — reads the register (they consult the live store).

**Claim**: the ViewGen slot is semantically required only on CPs pushed
*inside dynamic-chain dispatch* — between an `enter_dynamic` and the clause
body's `execute`. Every other CP (static chains, backtrackable-builtin
cursors, IL CPs — including ADR-023 dynamic snapshots, which compile
static-style with no `check_visible`) restores it redundantly.

**Argument**: a `check_visible` executes in exactly two dynamic contexts:

1. *Straight-line after `enter_dynamic`* — the register was just sampled;
   fresh by construction. (Chain code between the trampoline and the first
   body `execute` runs no builtins and no calls, so nothing can clobber the
   register in this window.)
2. *After a restore of the chain's own CP* (`retry_me_else` / `trust_me` /
   a bucket `Retry`/`Trust` reached via `BP`). That CP was pushed within the
   same activation window, so it saved the activation's gen; the backtrack
   cascade restores newest-to-oldest, so the **last** restore before the
   `check_visible` is that chain CP's — any staler value written by a nested
   activation's `enter_dynamic` is overwritten by exactly the right one.

A CP *outside* a chain window can therefore skip both the save and the
restore: the value it would restore is either dead (overwritten by the next
`enter_dynamic` sample) or re-established by a subsequent chain-CP restore
before any `check_visible` can read it.

## The design that would ship (blueprint, if ever revisited)

- **Frame**: swap the last two slots — `B0` moves to `+8`, `ViewGen` becomes
  an optional `+9` present only in wide frames. All other offsets (`CE`..`Hb`,
  args) unchanged, so the GC boundary-relocation walk (`HeapTop`/`Hb` at
  `+6`/`+7`) and the Cut parent-reads are untouched.
- **Width bit**: high bit of the arity control word (`CpWideFlag`), masked by
  a `CpArityMask`. This is unavoidable: restore-side and every raw frame
  walker need the frame to be self-describing (the backtrack that lands on a
  `retry_me_else` can come from anywhere, so no push-site context survives).
  ~13 raw readers of the arity word must mask: `PushChoicePoint`,
  `RestoreCommonFromCurrentCp`, `TrustMe`, the env-trim stack-top clamp,
  `Cut`'s parent reads, the GC `RelocateRoots` CP walk + its `CpSize` sanity
  check, `EnumerateChoicePoints`, `DiagnoseCpFloor`, the IL-CP pop path, the
  interpreter's backtrack `BP` read, and the `PopRestoreTrace` /
  `RetractTrace` diagnostics.
- **Push-side classification**: an engine flag `_inDynamicChain` — set by
  `EnterDynamic`; **resynced to the restored frame's width bit on every CP
  restore** (this covers the subtle case of backtracking into an *indexed*
  dynamic chain whose bucket dispatch then pushes a fresh bucket CP); cleared
  by the `Call`/`Execute`/`CallBuiltin`/`ExecuteBuiltin`/`Proceed` handlers
  (one dead-store per call dispatch). Any CP pushed while the flag is set is
  wide. Over-approximation is safe: a wide frame always restores correctly —
  today *every* frame is wide.

**Rejected discriminators**:

- *Peek at the following opcode* (`code[after] == CheckVisible`): adjacency
  is an optimization, **not an invariant** — `TryInlineCheckVisible` itself
  carries a non-adjacent fallback, and the chunk-128 `asserta` head demotion
  rewrites `try_me_else` (9 B) into `retry_me_else` + 4×`Nop`, parking Nops
  between the entry and its `check_visible`. A push-side misclassification is
  silent wrong-answers.
- *A `TryMeElseDyn` opcode family*: the chunk-155a–g in-place chain machinery
  (`IsExtensibleIndexedLayout`, `EnumerateChainHeadsRecursive`,
  `ChainEntryHeaderSize`, the assertz/asserta/retract patchers) pattern-matches
  chain-entry opcodes byte-by-byte; a parallel opcode family doubles every
  match site. Far larger blast radius than the flag.
- *ExtraTrail `ViewGenChange` entry written by `enter_dynamic`* (restore via
  normal trail unwind — semantically exact): Pareto-negative for the Arity
  workload, which is dominated by *deterministic* dynamic calls that push no
  CP today and would newly pay a trail entry per call.
- *Side stack of (b, gen) pairs*: must shrink in lockstep with wholesale CP
  discards, adding a compare to every `Cut`/`TrustMe`. Taxes all cuts to
  subsidize dynamics.

## The measurement — ceiling probe (2026-07-05)

Method: unsound-for-dynamics narrow-frame hack (drop the ViewGen slot from
*all* frames: 10-word control block, `B0` at `+8`) benchmarked on **static
-only** CP-heavy workloads, so the hack is sound for what runs. Two frozen
side-by-side publishes (SHA-verified distinct), interleaved A-B-B-A-A-B-B-A,
each invocation min-of-7 in-process, fresh engine per rep:

| workload | A = baseline (11 words) | B = narrow (10 words) |
|---|---|---|
| queens(8) ×60 | **39** ms (39–49) | **39** ms (39–50) |
| crypt ×40 | **183** ms (183–236) | **154** ms (154–215) |
| member-fail 50-list ×120k (~6M CP push+restore pairs) | **1241** ms (1241–1817) | **1286** ms (1286–1404) |

queens: identical. crypt: overlapping ranges with visible thermal drift in
both columns. memfail — the purest CP churn constructible — has the **wrong
sign** (baseline "faster"): pure noise. The arithmetic agrees with the
measurement: 6M pairs × (1 store + 1 load) ≈ 12M memory ops ≈ 5–12 ms ≈
**0.3–1% on the most CP-intensive synthetic possible**, and real programs sit
far below that CP density (regions + neck-cut already elide most CPs —
ADR-021's Class-B lesson applies verbatim: the addressable share of the real
workload is a rounding error).

The remaining win would be stack **memory**: 8 bytes per *live* CP. Deep
simultaneous nondeterminism at 1M live CPs saves 8 MB — no observed workload
is within orders of magnitude of caring.

## Decision

**REJECTED.** The cost side is concrete: ~13 mask sites each a silent
-stack-corruption hazard of the worst debuggable kind (the chunk-404
post-mortem class), a new flag written on the hot call-dispatch path, a
weaker GC sanity check, and a permanent tax on every future CP-walking
diagnostic. The benefit side is measured at or below noise on synthetics
built to maximize it. Per the project's measurement discipline
(ADR-021, chunk 418, I7/I8), hot-path structure is not spent on unmeasurable
wins.

## Revisit triggers

- A real workload where CP-stack residency (bytes of live CPs) is a
  demonstrated limit — the blueprint's memory win then has a customer.
- The CP frame growing further (e.g. a hypothetical ADR adds more per-CP
  state): the relative win of width-splitting grows with frame size.
- A Tier-0 interpreter rework that already forces touching every frame
  walker — the mask sites would then be free riders on an audited change.
