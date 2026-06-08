# Phase 28 — Closure

**Status**: complete.

**Tagged**: `phase-28`.

Phase 28 began as a **real-program validation corpus** — run third-party Prolog
through Shumway and diff against GNU Prolog as the oracle — and, once that surfaced
the codegen/runtime gaps, turned into a sustained **Tier-1 IL runtime-speed arc**.
The connecting thread the user set: *most of a shipped program will run as Tier-1
IL packed in a bundle, so make that fast.* Thirty-eight chunks (327–364, plus the
347b analysis note).

| # | Chunk | Theme | What it adds |
|---|-------|-------|--------------|
| 327 | append/3 improper-list split | corpus | `append/3` splits improper lists (ISO); found via the reducer combinator program |
| 328 | ExamplesPl deep validation | corpus | 16/16 GProlog ExamplesPl run; 15/15 deterministic byte-match the oracle; `get_cpu_time/1` |
| 329–331 | GProlog-FD shim + squaring | corpus | `fd_*` compat shim, REPL `--clpfd`/`--clpr`, reification ops, `clpfd_narrow` ground-int membership fix |
| 332–334 | clpfd soundness dig | corpus | bare-`Functor`-cell materialiser; narrowed a soundness bug to a precise repro |
| 335 | cut flushes wakeups | engine | a cut/`->` commit flushes pending attribute wakeups before pruning (unsound whole-goal failure fixed) |
| 336–338 | cut trail-survival | engine | `CompactTrails` kept `AttrModify`/`BigIntAlloc` entries by a HeapIdx overloaded as a side-table index → dropped live entries; fixed per trail-kind |
| 339 | IL cut flushes wakeups | engine | Tier-1 IL cut path gets the same wakeup flush (closes the chunk-335 IL gap) |
| 340–345 | native clpfd | clpfd | global linear propagator; O(n) finite fast path; native C# bound primitives + **immutable C# domain layer (~3.5–4×)**; native Hall `all_distinct` |
| 346 | vanilla-program compat | corpus | `nth0/3`/`nth1/3` enumerate on var index, `sumlist/2`, `format/1`, `~c`/column directives (Lines of Action) |
| 347 / 347b | WAM-vs-GProlog refocus | codegen | void-batching (`zebra` 1.29×→0.99×); aggregate gap analysis (register allocator is the one real axis left) |
| 348 | inline index switch | IL speed | compile the Tier-1 index decision to native IL branches (no per-call dict + graph walk) |
| 349–350 | self-tail-recursion loop | IL speed | a self `Execute` becomes an in-method `br` loop (indexed / single-clause / chain) — O(1) C# stack |
| 351 | lazy Y-slot allocation | IL speed | **the big one** — `allocate` no longer heap-allocs one cell per permanent; tight-loop heap GC was ~90%; ~4.6× |
| 352 | reclaim env frame | IL speed | `deallocate` trims `_stackTop` when no CP protects the frame; kills unbounded stack growth (~1.4×) |
| 353 | inline unify/get_list | IL speed | hot/cold split so the JIT inlines the read-mode fast paths |
| 354–355 | arithmetic fast lane | IL speed | try/catch-free, inlinable integer fast lanes for `a_int_*` (cx ~26%) and the RPN eval-stack (~5%) |
| 356–357 | inliner design | IL speed | design + scoping of the Tier-1 local-predicate inliner (`docs/design/il-local-inlining.md`) |
| 358–360 | fact inliner | IL speed | multi-clause-FACT inline: `IsFactPredicate`, cursor-merge mechanism, index pre-filter (crypt ~23%) |
| 361 | full-bench validation | IL speed | (later retracted) read the noisy bench as "default stays OFF" |
| 362 | profitability + default ON | IL speed | index-eligibility gate; default ON; (a size budget, dropped in 363) |
| 363 | O(1) cursor jump table | IL speed | the real fix — re-entry was an O(n) compare chain; a jump table makes inlining strictly cheaper, budget removed |
| 364 | Blint survey | IL speed | a diagnostic + survey showing the fact inliner is inert on real code — rules are the payoff (→ Phase 29) |

## Theme — real-program validation corpus (327–346)

The empirical approach that drove Blint and Phases 25–27, widened to a corpus:
run programs GNU Prolog can run (its own `ExamplesPl` / `ExamplesFD`, vanilla
third-party programs), diff Shumway's computed output against GProlog. It paid off
in correctness fixes that synthetic tests missed:

- **`append/3` splits improper lists** (327) — the list builtins historically
  assumed proper lists; `append(P, F, [3|fac])` (reducer peeling combinator tags)
  returned `false`.
- **clpfd soundness** — a cut/`->` commit must flush pending attribute wakeups
  before pruning (335); and `CompactTrails` (run by every cut) dropped live
  `AttrModify` / `BigIntAlloc` trail entries because it compared a side-table
  index against a heap top (337/338). Both are general engine-correctness fixes
  surfaced by clpfd-heavy corpus programs (donald, alpha).
- **Native clpfd** (340–345) — the GProlog FD gap was constant-factor (interpreted
  propagation vs native C). Moved the domain off the Prolog heap into an immutable
  C# `ClpfdDomain` with native `$dom_*` builtins (~3.5–4×); donald + alpha-first-
  fail now solve fast. Remaining (deferred): native control-flow (clpfd_run /
  labeling) for the node-count-bound leftmost searches.
- **Vanilla compat** (346) — `nth0/3`/`nth1/3` enumerate on a variable index,
  `sumlist/2`, `format/1`, `~c` and column directives.

## Theme — Tier-1 IL runtime speed (348–364)

The phase's centre of gravity. The horizon: a shipped bundle runs as Tier-1 IL, so
Tier-1 throughput is what matters. **Profile-first** was the discipline, and it
repeatedly redirected the work.

- **Environment-allocation garbage was ~90% of a tight loop** (351/352). `allocate`
  heap-allocated one cell per permanent every call (immediately overwritten →
  garbage), so heap GC dominated; lazy Y-slots = ~4.6×. Then `deallocate` never
  trimmed the stack, so a deterministic tail loop grew it unboundedly; frame
  reclaim = ~1.4×. A tight integer loop went **6204 ms → ~764 ms ≈ 8.1×** with the
  arithmetic fast lane (354) on top.
- **Arithmetic try/catch blocked JIT inlining** (354/355). The integer fast lane
  can't raise a Prolog error, so the `try/catch` (error stamping) was unnecessary
  there; splitting into an inlinable fast lane + cold slow path gave cx ~26%.
- **Self-tail-recursion** (349/350) became an in-method IL `br` loop — the one
  GProlog-style flat jump IL can express — keeping the C# stack O(1).
- **The local-predicate inliner** (356–364). Merges a local callee's clause
  dispatch into a hot caller's IL method, removing the trampoline. Phase 1 =
  multi-clause FACTS, with the index pre-filter making bound calls deterministic
  (crypt ~22–25%). The arc's lesson is in **362→363**: a clause/arity "size budget"
  added to dodge a chat_parser regression was masking an algorithm flaw — re-entry
  ran through a LINEAR cursor compare-chain that grew with each inlined alternative.
  Replacing it with an **O(1) IL `switch` jump table** made inlining strictly
  cheaper than the trampoline, and the budget was removed. Validated: only crypt +
  chat_parser inline across the 27-program bench; the other 25 are byte-identical
  IL (provably no-regression); crypt ~22%, chat_parser neutral.

## Measurement discipline (carried, reinforced)

This phase is a case study in the laptop's ~40 % thermal variance corrupting
wall-clock A/B. Chunk 361's "default stays OFF (sieve +42 %, boyer +15 %)" was pure
noise — those programs **don't even inline** (0 sites; byte-identical builds). The
durable rules: trust the deterministic structural argument (which programs change
at all) over a wall-clock table; measure INTERLEAVED back-to-back, min-of-N; never
compare across runs/sessions. [[wallclock-ab-must-be-back-to-back]]
[[no-stacked-background-tests]]

## What this phase deliberately did NOT do

- **Rule inlining.** The chunk-364 survey is explicit: the fact inliner is inert on
  real programs (Blint = 3 fact call sites vs 229 rule call sites). Inlining rules
  (single-clause leaf → single-clause non-leaf → multi-clause) is the real-world
  payoff and is **Phase 29's** scope. The fact inliner built the structural
  scaffold (cursor-merge, CPs-into-caller, the jump table) it will reuse.
- **WAM register allocator** (347b): the one remaining WAM codegen gap vs GProlog;
  a separate, risky axis, left scheduled.
- **Native clpfd control flow**: the FD leftmost-search ceiling; deferred.

## Suite state at close

Core 428 / Compiler 284 / ISO 277 / Embedding 2103, all green. The fact inliner is
ON by default (`SHUMWAY_INLINE_FACTS != "0"`).
