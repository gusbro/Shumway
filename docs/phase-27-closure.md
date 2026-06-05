# Phase 27 — Closure

**Status**: complete.

**Tagged**: `phase-27`.

Phase 27 is a **mixed phase**: one performance/size theme (Tier-1 IL bundle
slimming), one codegen-quality theme (extending ADR-019 to non-last nesting),
and two cleanup themes (deferred ISO/parser items and the Phase-21 embedding-API
leftovers). The user picked the order **1, 3, 4, 2**.

Thirteen code chunks (316–326 plus the two letter-numbered theme-1 chunks
**B**/**C**), plus a bonus ISO fix (323) a user question surfaced.

| # | Chunk | Theme | What it adds |
|---|-------|-------|--------------|
| 316 | IL→IL by functor id | 1 | an IL caller dispatches an IL callee via `EncodeResumeMarker(fid, 0)`, not a WAM address |
| 317 | `--strip-wam` (self-contained) | 1 | drop the WAM body of every IL-promoted self-contained predicate from a bundle |
| 318 | keep indexed WAM | 1 | correctness: an indexed predicate read its WAM cascade at runtime, so stripping it crashed; `Strippable` flag introduced |
| 319 | `IlByFunctorId` direct | 1 | IL→IL dispatch uses the `IlByFunctorId[fid]` array, not the `Tier1Dispatcher` interface + dict |
| 320 | index node graph | 1 | indexed dispatch runs on a WAM-independent `IlIndexGraph` (built from the switch cascade), reading no WAM |
| B | persist the graph | 1 | `IndexGraphCodec` serialises the graph into the bundle; registered per-query → indexed predicates' WAM strippable too |
| C | meta-call correctness | 1 | a stripped predicate reached by a runtime meta-call resolves via a resume-marker alias in `CurrentFunctorAddresses` |
| 321 | ADR-020 non-last nested | 3 | inline non-last nested compound build (reserve-upfront write mode + write-pointer frame stack) |
| 322 | parser `\+ (a,b)` | 4 | verified already-fixed (chunk 149); removed a stale "dodges ambiguity" comment, added direct coverage |
| 323 | existence_error PI | bonus | the procedure indicator is the COMPOUND `'/'(Name, Arity)`, not an atom `"Name/Arity"` |
| 324 | `EnginePool` | 2 | bounded reuse of thread-agile engines for concurrent embedding |
| 325 | async query API | 2 | `QueryAsync` (`IAsyncEnumerable<Solution>`) + `QueryAll(string, ct)`, cooperative cancellation |
| 326 | cancel at watermark | 2 | move the cancellation check off the per-goal path into the GC-watermark branch (zero common-path cost) |

## Theme 1 — `--strip-wam` and the flat index model

A Tier-1 IL bundle shipped BOTH the WAM bytecode and the compiled IL for each
predicate. `shumway-link --strip-wam` now drops the redundant WAM, because every
dispatch path that previously needed it has a WAM-free equivalent:

- **IL→IL** calls dispatch by functor id through `IlByFunctorId` (316/319) — the
  same array a bytecode `CallIl` uses, so no WAM address is consulted.
- **Indexed dispatch** (first-/multi-arg switch cascades) used to walk the WAM
  `switch_on_term`/`switch_on_arg` chain at runtime. Chunk 320 lifts that cascade
  into a **WAM-independent node graph** (`IlIndexGraph`), and chunk **B**
  persists it in the bundle (`IndexGraphCodec`, name-relative keys) and registers
  it onto each per-query engine — so an indexed predicate dispatches with no WAM
  body either.
- **Runtime meta-calls** (an if-then-else condition, `call/N`) resolve a goal by
  functor id through `CurrentFunctorAddresses`, which only held WAM-backed
  predicates. Chunk **C** maps every IL-only functor (including the bare-name
  alias of a module-local) to its resume marker there, so the marker flows
  through `SetPc` and the dispatch loop routes it to the IL delegate.

Surfaced and fixed along the way: stripping an indexed predicate crashed Blint
("functor id 907 not in CurrentFunctorAddresses", chunk 318 → graph in B), and
`main`'s `ifthenelse(is_sicstus, …)` raised `existence_error(procedure,
is_sicstus/0)` because the meta-call path didn't see the stripped local (chunk
C). Result: Blint bundle 650302 → 520012 bytes (−20%), runs correct cross-process
and as a native `--exe`.

**Size reality, recorded:** `--strip-wam` implies shipping IL, and IL is more
verbose than the WAM it replaces — so a stripped IL bundle is SMALLER than a
`--with-compiled-il` bundle (−6.5% on the Blint exe) but LARGER than a WAM-only
bundle. It is a win only when you already want IL (for speed); it is never
smaller than plain WAM.

## Theme 3 — ADR-020: inline non-last nested compound build

ADR-019 (Phase 26) inlined a nested compound only in the LAST argument position,
where the build stays linear. ADR-020 extends this to **non-last** positions in
**body** building via two reserve-upfront roots, `put_structure_r` /
`put_list_r` (the reserve size baked at compile time — the user's catch that the
arity is known, so no runtime `FunctorTable.Lookup` on the common path), plus a
runtime write-pointer frame stack: a scalar `unify_*` writes in place and
cascade-pops completed frames; a nested `unify_structure`/`unify_list` pushes a
frame and the cascade resumes the parent. The on-demand path (no nesting, or
last-arg only — every list literal) is untouched.

**Result on Blint:** total WAM 15087 → 14039 (−1048, −7%); `get_structure`
499 → 159 (−68%), `get_list` 886 → 178 (−80%). GProlog BFSs these too, so we now
beat it on body nested-build.

**Head matching is deliberately not done.** It would need read-mode resume plus
the WAM read/write mode flip per argument (the hottest path). Measured ceiling:
of the 337 `get_structure`/`get_list` left, 266 (79%) are intrinsic top-level
head-arg matches and only 71 (21%) are nested deferrals — a ~15× smaller win for
a far riskier change. Recorded as future work in ADR-020.

## Theme 4 — deferred ISO/parser items (all already fixed)

The three items recorded inline in the ISO conformance suite since Phase 9 were
already closed in Phase 10: `char_conversion/2` (chunk 152), the cyclic-term
materialiser overflow (chunk 148), and the parser `\+ (a, b)` ambiguity (chunk
149 — verified empirically: `\+ (fail, true)` succeeds, `\+(fail, true)` is the
function-call `\+/2`). Chunk 322 removed the stale "dodges a parser ambiguity"
comment and added direct conformance coverage.

## Bonus (323) — existence_error procedure indicator

A user question — *can you write `(\+)/2`?* (yes; the bare `\+/2` cannot, because
`\+/` is one graphic token under maximal munch) — surfaced a real ISO bug:
`existence_error(procedure, PI)` built `PI` as the ATOM `"name/arity"`, not the
compound `'/'(Name, Arity)`. Operator-form rendering hid it (an atom `'foo/3'`
and the compound `foo/3` print identically); `functor/3` revealed the arity-0
atom. So a specific catcher `error(existence_error(procedure, foo/3), _)` could
never unify. Fixed in `MetaBuiltins.TranslateRuntimeError`.

## Theme 2 — embedding API leftovers (Phase 21)

Two of the four deferred items were genuinely missing and built:

- **`EnginePool` (324)** — engines are single-threaded internally but
  thread-agile, so a bounded pool (SemaphoreSlim + ConcurrentBag, Rent/RentAsync
  → disposable Lease, `FromSource` one-liner) runs N queries in parallel, each on
  its own engine, never shared concurrently.
- **Async + cancellable query API (325/326)** — `QueryAsync(string, ct)` returns
  `IAsyncEnumerable<Solution>` driving each Run/Backtrack step off the calling
  thread; `QueryAll(string, ct)` is the synchronous cancellable form. Cancellation
  is cooperative via an Engine `_cancelRequested` flag that throws
  `OperationCanceledException` (not a Prolog ball). Chunk 326 moved the check off
  the per-goal path into the GC-watermark branch (zero common-path cost); the
  trade-off is that a heap-bounded loop (`repeat, fail`) is not cancellable —
  the standard GC-safe-point granularity.

The other two were already addressed in Phase 22: **mode declarations for
multi-output foreigns** → chunk 246 (`out`→`-`, `ref`→`?`, plain→`+`); and
**`ForeignContext.IsFirstCall`/`State`** → superseded by chunk 244's
`IEnumerable<T>` generator non-det foreigns, which carry redo state automatically
(more ergonomic than SWI's manual context). Left unbuilt absent a concrete need.

## New architecture

- **ADR-020** — inline non-last nested compound build: reserve-upfront write mode
  (`put_structure_r` 0x2C / `put_list_r` 0x2D) + a write-pointer frame stack.
  Body building only; head matching keeps the BFS.

## Measurement note

Confirmed again this phase: the laptop's SAME-binary run-to-run wall-clock
variance is ~12% (per-run sd 22–37%). When the cancellation-check placement was
A/B'd, "with" vs "without" (7%) was smaller than the same-binary spread (12%) —
so a single predicted branch is unmeasurable. Codegen/size wins (theme 1, 3) are
reported on deterministic metrics (bytes, instruction count); micro hot-path
tweaks are reasoned about, not A/B'd below this machine's noise floor.

## Items NOT in this phase

- Head-matching reserve-upfront (ADR-020 future work — ~71-instruction ceiling on
  Blint vs the 1048 body win, for a read/write-mode-flip change on the hottest
  path).
- `ForeignContext.IsFirstCall`/`State` (niche; the `IEnumerable<T>` generator
  supersedes it for the practical cases).
- Cancellability of heap-bounded loops (`repeat, fail`) — intentional, the cost
  of keeping the common per-goal path free.
