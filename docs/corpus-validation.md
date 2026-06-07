# Corpus validation (Phase 28)

Running real third-party Prolog programs through Shumway and diffing the result
against **GNU Prolog** (the oracle) to surface correctness / compatibility gaps —
the same empirical approach that drove Phases 25–27 (Blint), now widened to a
corpus.

**Oracle**: GProlog 1.5 via `gplc --no-top-level` native console exe (NEVER
`gprolog.exe` through a pipe — it pops the GUI). Driver appends
`:- initialization((catch((benchmark(R)->write(res(R));write(failed)), E,
write(err(E))), nl, halt)).`. See the `gprolog-windows-toolchain` memory for the
exact recipe.

**Corpus** (must run on GProlog, so the oracle is valid): GProlog's own
`examples/ExamplesPl` (16 pure Prolog) and `examples/ExamplesFD` (31 CLP(FD)),
plus vanilla programs from `c:\temp`. Arity demos are excluded (Arity-specific,
GProlog can't run them).

## ExamplesPl (16) — status

| Program | GProlog | Shumway | Notes |
|---------|---------|---------|-------|
| boyer | res(true) | ✅ | Boyer-Moore tautology prover |
| browse | res(_) | ✅ | |
| cal | res(true) | ✅ | |
| chat_parser | res(_) | ✅ | CHAT-80 NL parser fragment |
| crypt | res(true) | ✅ | |
| ham | res(true) | ✅ | Hamiltonian |
| meta_qsort | res(true) | ✅ | |
| nand | res(true) | ✅ | nand-circuit synthesis |
| nrev | res(true) | ✅ runs | needed `get_cpu_time/1` (added); output is timing (LIPS), not output-comparable |
| poly_10 | res(_) | ✅ | |
| queens | res(true) | ✅ | |
| queensn | res(true) | ✅ | |
| reducer | res(true) | ✅ **FIXED** | combinator graph reducer — see below |
| sendmore | res(_) | ✅ | |
| tak | res(true) | ✅ | |
| zebra | res(true) | ✅ | |

**Deep validation (computed output, not just `benchmark/0` success):** running
`benchmark(true)` (which forces each program to WRITE its computed answer) and
diffing stdout against the GProlog oracle — **all 15 deterministic programs are
byte-identical to GProlog** (modulo list whitespace, `[1, 2]` vs `[1,2]`).
`nrev` runs but only prints a LIPS/timing line, so it has no output to diff.

The two gaps found and closed:

### reducer — FIXED (append/3 improper-list split)

`reducer` (an applicative/combinator graph reducer) gave `false` where GProlog
gives `res(true)`. Narrowed by stage diff (`listify`/`curry` matched GProlog;
`t_reduce` diverged) to `t_redex`'s last clause, which peels a combinator's atom
tag with `append(_par, _func, [3|fac])` — i.e. **splitting an improper list**
(`[3|fac]`, tail = the atom `fac`).

Root cause: the `append/3` C# builtin's non-deterministic split path
(`AtomListBuiltins.AppendSplit`, var L1 / bound L3) rejected any L3 whose final
tail isn't `[]` with `return false`. But ISO `append/3` splits an improper list
fine — every suffix `L2` simply carries the improper tail
(`append([], fac, [3|fac])`-style). Fix: thread L3's actual tail through to the
L2 build instead of hardcoding `[]`; a proper list still has tail `[]`, so the
common case is unchanged. Regression tests in `AtomListBuiltinsTests`.

### nrev — `get_cpu_time/1` added

`nrev`'s `benchmark/1` calls `get_cpu_time/1` (a GNU-Prolog timing builtin). Added
as a C# builtin (`ControlBuiltins.GetCpuTime`, reports the .NET process'
`TotalProcessorTime` in ms). nrev now runs; its output is a LIPS rate (timing), so
nothing to deep-diff.

## Open gaps (cross-program)

- `include/1` directive — worked around in the harness (concatenating `common.pl`);
  GProlog has it. TODO if a corpus program needs it structurally.

## Status

**ExamplesPl: 16/16 run; 15/15 deterministic programs byte-match GProlog.**

## ExamplesFD (31 CLP(FD)) — in progress

GProlog's FD examples use GProlog's `fd_*` primitives, a different dialect from
Shumway's SWI/SICStus-style clpfd (`in`/`ins`, `#=`, `all_different`, `label`).
Added a **GProlog-FD compatibility shim** in `Clpfd.cs` mapping the core
primitives — `fd_domain`→`ins`, `fd_labeling`→`label`, `fd_labelingff`→
`labeling([ff],_)`, `fd_all_different`→`all_different`, `fd_atmost`/`fd_exactly`/
`fd_only_one`/`fd_at_most_one`→reified counts, `fd_set_vector_max`→no-op — which
covers **24/31** programs (the other 7 need `fd_element` (2), `fd_minimize`/
`fd_tell` (5)).

Also added a REPL **`--clpfd`** / `--clpr` flag: it calls `UseClpfd()` BEFORE
consulting, so the constraint operators are in the table when files are parsed.
(A `:- use_module(library(clpfd))` directive inside a file is too late — Shumway
parses the whole file before running directives. That, and the fact that
`use_module(library(clpfd))` doesn't persist the library / its operators to the
REPL parser, are real gaps the corpus surfaced — see below.)

### Validated so far (oracle-vs-Shumway diff of `q`'s solution, time stripped)

MATCH: **crypta, eq10, eq20, five, send** (SEND+MORE=MONEY etc.). The core solver
agrees with GProlog on these.

### Operator / expression gaps — ADDED (chunk 330)

- **`#<=>` / `#=>` / `#<=`** (single-arrow reification, GProlog spelling) →
  aliases of Shumway's `#<==>` / `#==>` / `#<==`. Verified.
- **`##`** (GProlog boolean exclusive-or) → reified as `#\=` (xor ≡ inequality on
  0/1). Verified.
- **`**`** (power) in FD expressions → expands to repeated `$fd_times`
  (`X**2` → `X*X`). Parses now — but see the multiplication bug below.

### `$fd_times` shared-var squaring bug — FIXED (chunk 331)

`X*X #= 9, label([X])` gave **X = 1** (wrong; only solution is 3). Root cause was
not in `$fd_times` itself but in **`clpfd_narrow/2`**: when its first arg is a
ground integer, it only checked the new domain was non-empty (`NewDom \== []`)
instead of that the integer is IN it (`clpfd_in_dom`). So `$fd_times(1,1,9)` →
`clpfd_narrow(9, [1-1])` succeeded without verifying 9 = 1. (`+` was unaffected
because `$fd_plus` back-propagates the factor at post time, so labeling never
reaches a wrong value.) One-line fix → `integer(X) -> clpfd_in_dom(X, NewDom)`.
Now squares label correctly (X*X#=9 → 3, =16 → 4, …). Affects ANY propagator
that narrows a ground integer — a correctness fix across clpfd, not just `*`.
4 regression tests; chunk-90 mult tests had a gap (no squaring case).

### Other FD findings (TODO)

- **`read_integer/1`** missing — bqueens, bramsey, interval, magic, square read
  the problem size from input. Needs the builtin (+ a way to feed input, or a
  default).
- **donald** — investigated (chunk 332):
  - `TermReader.Materialize` threw `NotSupportedException` on a `Tag.Functor`
    cell — **FIXED**: materialize a bare functor cell as the compound rooted at
    it (ADR-017 normally wraps it in a STR ref; some paths land directly on the
    functor). It was being hit while materialising an error term's culprit; the
    fix turns an uncatchable C# crash into a proper Prolog error. General
    robustness win.
  - The underlying error it was masking: a **clpfd soundness gap** — donald's
    native constraint yields `false` where GProlog finds `[5,2,6,4,8,1,9,7,3,0]`.
    The ground solution is ACCEPTED (`all_different` + the `#=` succeed on it) and
    SURVIVES the initial propagation, yet `label/1` exhausts without finding it;
    fixing `DD=5` then labelling the rest DOES find it — so the bug is in the
    search's backtracking.
  - **Dug to a precise minimal repro (chunk 333 investigation, NOT fixed):**
    ```
    X in 1..9, ( X #< 3 ; true ), X = 5.        % FALSE  (wrong — want X=5)
    X in 1..9, ( X #< 3 ; true ), X #= 5.       % ok, X=5
    X in 1..9, ( (X#<3,X#>5) ; true ), X = 5.   % ok, X=5
    findall(X,(X in 1..9,(X#<3;X#>6),label([X])),L).  % ok, [1,2,7,8,9]
    ```
    The bug: unifying a clpfd attvar with an integer (`X = 5`) AFTER backtracking
    out of a disjunction whose FIRST branch SUCCEEDED sees the branch's narrowed
    (stale) domain — that narrowing isn't restored on this particular backtrack
    path. The constraint form `X #= 5` and `label` (both via `clpfd_narrow`) are
    unaffected; plain `=` goes through the attvar `verify_attributes` hook. So
    it's an attvar-unify ↔ trail/backtrack interaction (extra-trail / `AttrModify`
    undo), NOT a clpfd-propagator bug. The undo (`UnwindTrails` /
    `ProcessExtraUnwind`) and the WAM-CP restore (`RestoreCommonFromCurrentCp`,
    which DOES restore the extra trail) each look correct in isolation; the
    discrepancy is the branch-succeeds-then-later-goal-fails path. **Deep; a
    focused engine session.** (Donald uses `label`, which the findall repro shows
    restores fine — donald may be this bug via a path that ends in `=`, or a
    sibling complex-linear limit. TBD.)
  - **Refined by engine tracing (chunk 334 attempt):** the `[wakeup] flush`
    trace fires only ONCE for the if-then-else repro — branch 1's `X = 5` —
    then the whole goal fails with no second wakeup and no else branch. That
    pinned it down.
  - **FIXED — chunk 335 (cut must flush pending wakeups first).** Reproduced
    cleanly: the bug needs the goal inside an **if-then-else condition** (or any
    cut) — the *bare* query `X in 1..9,(X#<3;true),X=5` already gave `X=5`; only
    `( (...) -> yes ; no )` failed (and failed to `false`, running *neither*
    branch — the tell). Root cause: unifying a clpfd attvar with a value
    (`X=5`) **queues** a `verify_attributes` wakeup (the domain check is
    deferred to the next goal boundary) and the unify returns `true`. In
    `(Cond -> Then ; Else)`, the very next thing after Cond's last goal is the
    `->` **commit cut**, which removed both the inner `(X#<3;true)` choice point
    and the else choice point *before* the pending wakeup ran. The wakeup then
    fired (5 ∉ [1-2]), failed, and there were no choice points left to backtrack
    into → unsound whole-goal failure. Bare queries have no cut, so the wakeup
    failed while the `;true` CP still existed → branch 2 → `X=5`. The non-clpfd
    nested-disjunction-in-`->` case always worked (no deferred wakeup). Fix:
    `Opcode.Cut` / `Opcode.NeckCut` now call `FlushPendingWakeups` before
    performing the cut, exactly like `Call` / `Proceed` / `Deallocate` already
    did — a cut is a goal boundary too. A failed flush backtracks instead of
    cutting. Verified: the repro now gives `yes`; the genuinely-impossible
    `(X#<3,X=5)` still gives `no`; `once((member(V,[7,3]),X#=V))` over `X in 1..5`
    now yields `X=3` (backtrack to the 2nd member solution *inside* `once`'s cut,
    after the first constraint binding failed — the real-world shape). All four
    suites green (Core 423 / Compiler 282 / ISO 277 / Embedding 2057). **Known
    gap:** the Tier-1 IL compiler emits `engine.Cut` directly and never flushes
    wakeups (it has no wakeup handling at all — it relies on the bytecode
    interpreter's Call/Proceed boundaries via the Phase-16 threaded
    continuation). An IL-*promoted* user predicate that binds a clpfd attvar and
    then cuts could still hit the original bug; rare (clpfd lib predicates aren't
    promoted) and left as a documented follow-up.
  - **donald** itself is *not* fully fixed by this: it now gets past the
    soundness failure and hits a *separate* `type_error(evaluable) in is/2`,
    investigated in depth (chunk 336 below) but **not fixed** — it is a deep
    engine attribute-value heap-lifetime bug, not a clpfd-library gap.

### donald `type_error(evaluable, fd(_,_))` — FIXED (chunk 337): a cut dropped live attribute-trail entries

**Root cause (chunk 337).** `Engine.CompactTrails` (run by every `Cut`) decides
which extra-trail entries survive with the rule "keep if it references a heap
cell older than the parent CP's heap top" — `entry.HeapIdx < parentHeapTop`.
That is correct for a `ValueChange` entry, whose `HeapIdx` *is* the modified
heap cell. But an **`AttrModify`** entry overloads `HeapIdx` to mean an index
into `_attrTrailLog` (a monotonic counter of attribute mutations), **not a heap
address**. Comparing that counter against a heap top is meaningless; once a
long computation has mutated more attributes than the parent CP's heap top, the
counter exceeds `parentHeapTop` and the rule **wrongly dropped the entry — even
for an OLD attributed variable whose record-restore is still required.**

clpfd's library is full of small `if-then-else`s and `!`s (`clpfd_ble`,
`clpfd_bmin`, `clpfd_dom_max`, …), each of which cuts and compacts the trail.
Deep labeling drives the attribute-mutation counter past the cut's (recent,
high) heap top, so an old FD variable's domain stops being restored on
backtracking to a labeling value-choice — its attribute term is left stale, or
points at a heap cell already reclaimed by an unrelated heap-top rollback,
surfacing as `fd(Dom, <unbound>)` in bound arithmetic. **Not heap GC, not the
`#=` aliasing, not the hook** — exactly as narrowed in chunk 336; the missing
piece was the cut-compaction misclassification.

**Fix:** `CompactTrails` now decides per entry type — `CatchFrame` always
survives, `AttrModify` survives iff the attvar's HOME
(`_attrTrailLog[HeapIdx].Home`) is older than `parentHeapTop`, everything else
keeps the original `HeapIdx < parentHeapTop` heap-cell test.

**Impact on non-attvar programs: none.** The change is allocation-neutral
(`--alloc` is byte-identical with and without it across nreverse/qsort/queens),
and programs that use no attributed variables produce no `AttrModify` entries at
all, so the only path that changed is never taken — the `switch` falls straight
to the unchanged `ValueChange`/default arm. CompactTrails runs only on cut and
only over the (usually tiny) extra trail.

**Verification:** donald's known solution `[5,2,6,4,8,1,9,7,3,0]` is now found by
`labeling([ff], LD)` (and `D=5, label(LD)`); a precise Core regression test
(`Chunk337Tests`) reproduces the exact misclassification (old attvar, log index
past the parent heap top, cut, backtrack → attribute must restore) and fails
without the fix; all suites green (Core 425 / Compiler 282 / ISO 277 /
Embedding 2062). **Remaining donald caveat (now addressed — chunk 340):** plain
leftmost `label(LD)` was very slow because Shumway decomposed the linear
constraint into a tree of binary `$fd_plus`/`$fd_times` bounds propagators,
weaker than a global linear constraint.

### clpfd propagation strength: a global linear propagator (chunk 340)

A comparison whose two sides read as one linear form `sum(Ci*Vi) Rel K` — with
≥2 terms and a scaled coefficient (`|Ci| >= 2`) — now posts a single
bounds-consistency propagator (`$fd_linear`) over the whole sum instead of
decomposing it into binary `$fd_plus`/`$fd_times`. The normaliser (`clpfd_norm`)
flattens `L - R`, **combines a variable's coefficients** (donald's `D` appears
three times → coefficient 100002; the binary form lost that), and drops cancels;
the propagator prunes each variable from the *rest* of the sum (prefix+suffix,
not total−self, so an unbounded result var like `Z` in `X+Y#=Z` is still pinned,
and not by variable identity, so two vars bound to the same value aren't both
skipped). Reduces to `=`/`=<`; the other relations redirect. A `|Ci|>=2`
threshold keeps plain unit-coefficient sums (`A+B#=C`, `X#=Y`) on the existing
decomposition, preserving its aliasing and residual-goal projection — crypt-
arithmetic always crosses the threshold (powers of ten, repeated columns).

**donald now solves under plain LEFTMOST `label(LD)`** (was non-terminating).
Sound across multi-equation systems, negative coefficients, repeated variables
and `scalar_product`; all four suites green (Core 426 / Compiler 282 / ISO 277 /
Embedding 2064), 7 dedicated tests (`Chunk340Tests`). Still slower than GProlog
(the propagator is interpreted Prolog, O(n²)/propagation, vs GProlog's native C
linear constraint) — a constant-factor gap, not a completeness one.

### alpha (chunk 341): O(n) finite fast path; solves under first-fail

`alpha` (26 vars, 1..26, all_different + 20 column sums) is the corpus's hardest
linear case. Two changes made it tractable:

- **O(n) finite fast path in `$fd_linear`.** When every term bound is finite
  (the case for all crypt-arithmetic — every variable has a bounded domain),
  each term's rest-of-sum is the exact integer `SMin`/`SMax` minus that term's
  own contribution: O(1) per variable, O(n) per propagation, vs the
  O(n²) prefix+suffix walk (which is only needed when an unbounded variable
  makes a subtraction `inf - inf` undefined). "Total minus self" is safe from
  the value-coincidence pitfall because it subtracts THIS term arithmetically,
  not every term sharing its value. **alpha under first-fail: 41 s → ~12 s.**
- **`fd_all_different` stays pairwise `all_different`, NOT `all_distinct`.**
  Tried mapping GProlog's `fd_all_different` to `all_distinct` (Hall-interval,
  strictly stronger pruning) — but its per-propagation cost in interpreted
  Prolog made a permutation puzzle that re-fires it thousands of times ~8×
  *slower* (alpha ff 9 s → 71 s). Reverted. A C# `all_distinct` would flip this
  trade-off and is the right next step for native-comparable FD speed.

**alpha solves under first-fail with the correct GProlog answer**
(`[5,13,9,16,20,4,24,21,25,17,23,2,8,12,10,19,7,11,15,3,1,26,6,22,14,18]`).
**Leftmost (the program's `lab(normal)` default) is still too slow** — it is
search-bound (a huge node count under leftmost), not propagation-cost-bound, so
the O(n) path doesn't shrink it; closing that needs propagation as strong AND as
fast as GProlog's native FD (a C# linear + all_distinct propagator). donald
leftmost is the same shape (≈28 s, search-bound; ff/good order solve instantly).

`multipl` is "unknown multiplication" (var*var, genuinely non-linear) — outside
the linear propagator; its gap is `$fd_times` strength.

### Native FD: profiling + the first C# primitives (chunk 342)

Profiled `alpha ff` (`-p:ShumwayProfile=true`). The cost is **not** concentrated
in one place — it is the interpreted-Prolog overhead of clpfd spread across
**~3.1M predicate calls** (≈4 µs each). Top by call count: `clpfd_ble` (230k) and
the lowered `==`-chains inside it (4 × 230k), then the domain ops
(`clpfd_dom_of` 87k, `clpfd_dom_below`/`above` ~70k each, `dom_max`/`min`,
`clpfd_narrow`, `clpfd_run`). The single biggest builtin was **`==/2` at ≈1.36M
calls** — the `A == inf` / `B == sup` bound tests scattered through the bound
helpers.

**First C# step:** moved the scalar bound primitives
(`clpfd_ble`/`blt`/`bmin`/`bmax`, `add_lo`/`hi`, `sub_lo`/`hi`, `bneg`, `bmul`,
`bfloordiv`/`bceildiv`) from Prolog to native builtins (`FdBoundBuiltins`), where
a bound is a plain `long` with `inf`/`sup` as the `long` sentinels — so
`clpfd_ble` collapses to one native `<=` and the 1.36M `==` disappear. Drop-in
(same names; the Prolog clauses are removed so the module-local calls fall
through to the builtins). Correct (all 118 clpfd tests green), but only **~8%**
faster (alpha ff 12 s → 11 s) — confirming the cost is broad, not in the bound
helpers themselves.

**What this tells us:** the bound primitives were a large fraction of *calls* but
a small fraction of *time*; the real cost is the breadth of interpreted clpfd
machinery (the domain-list ops, attribute reads, the fixpoint driver). Closing
the gap to GProlog's native FD needs the **domain representation itself in C#**
(an immutable interval object referenced by id from the attribute, with all
`dom_*` ops native). The bound primitives are the foundation those C# domain ops
will reuse.

### Tier-1 IL for clpfd: investigated, a dead end for the speed goal (chunk 343)

Tested IL promotion as a cheaper alternative to a C# domain layer. Two findings:

- **It barely helps.** alpha first-fail: Tier-0 12 s → IL (threshold 20-50)
  ~10-11 s, ≈15%; donald leftmost 28 s → 20 s. IL shaves per-call dispatch, but
  clpfd's cost is the *volume* of propagation operations, which IL doesn't
  reduce. So even a clean IL path would not close the GProlog gap — the C#
  domain layer is the only route.
- **The `type_error(evaluable, inf)` is an edge case, not a general bug.** It
  fires only at `Threshold = 1` (promote a predicate after its *first* call) on
  an alpha-scale program (many constraints → deep `clpfd_run` fixpoint
  recursion). At any sane threshold (≥ 20) clpfd + IL is correct, and the
  **default `Threshold` is 0 (no promotion at all)**, so it never bites a real
  run. The signature (a guarded `is` running with `inf` only under aggressive,
  alpha-scale promotion) points at mid-flight promotion of a deeply-recursive
  clpfd predicate — a deep Tier-1-IL hazard, not a clpfd bug. Given it needs a
  pathological setting AND fixing it wouldn't help the speed goal, it is
  documented and left rather than fixed.

### Native FD: the domain in C# (chunk 344) — the real speed win

Moved the domain representation itself off the Prolog heap and into C#. A domain
is now an immutable `ClpfdDomain` (a sorted `long[]` interval set, with `inf`/
`sup` as the `long` sentinels), stored in the engine's foreign-object table and
named by a `Foreign` cell from the `fd(Dom, Props)` attribute. Fifteen native
`$dom_*` builtins (`new`, `universal`, `min`, `max`, `above`, `below`, `isect`,
`union`, `del`, `size`, `contains`, `empty`, `singleton`, `same`, `values`,
`intervals`) replace the interpreted interval-list walking. Backtracking needs no
per-domain trailing: domains are immutable, and the attribute (which holds the
Foreign cell) is already trailed, so restoring it restores the old domain.

The clpfd library keeps the `clpfd_dom_*` predicate names as thin wrappers over
the builtins, so every propagator is unchanged; only the predicates that
destructured the interval list were rewritten — `clpfd_narrow`, `$fd_set`, the
labeling enumeration (now `$dom_values` + `member`), reification entailment,
`$fd_alldiff`'s interval subtraction (now `$dom_union`), and the projection
(`$dom_intervals`). One subtlety: `copy_term/3` round-trips an attribute through
the AST, where a foreign domain renders as `'$foreign'(N)`; the materializer now
rebuilds that into the same Foreign cell, so projecting a copied FD variable's
domain still works.

**Result:** profiled alpha-ff exec **13 s → 2.9 s** (predicate calls 3.1M →
0.92M); wall-clock alpha first-fail **~11 s → ~3 s (~3.5×)**, donald leftmost
**28 s → ~5-8 s (~4×)**. All suites green (Core 426 / Compiler 282 / ISO 277 /
Embedding 2071; 5 dedicated Chunk344Tests). The bottleneck is now native
`get_attr` / `integer/1` / `$dom_above` — i.e. the work itself, not interpreter
overhead. **alpha leftmost still times out** (the program's `lab(normal)`): the
domain layer cuts the per-operation cost, not the *node count*, and leftmost on
26 vars explores a huge tree — that needs a stronger labeling/all_distinct, a
separate axis. donald and alpha-ff now solve comfortably.

### Native Hall all_distinct — does NOT make alpha leftmost feasible (chunk 345)

Chased the node-count axis: a native Hall-interval `all_distinct` (`$fd_hall`
builtin — reads the variables' domains, runs the O(n³) interval search in C#,
returns the shrunk domain per variable a saturated Hall interval pruned, and the
Prolog caller narrows each so re-propagation stays in the engine). It replaces
the interpreted-Prolog Hall (`clpfd_ad_*`) that was too slow to use at all.

Two findings:

- **It does not make alpha leftmost feasible.** Still times out (> 90 s). The
  C# domain layer already made each propagation cheap; alpha leftmost is bound
  by the *node count* (leftmost labels A, B, C… in order, and alpha's column
  sums don't constrain the early letters until the late ones are set, so the
  tree is huge) and by the interpreted *control flow per node* (the labelling
  loop and the `clpfd_run` fixpoint's `call(P)` per propagator). Stronger
  propagation doesn't shrink that tree enough.
- **As the `fd_all_different` shim it is a net loss on the corpus.** Hall
  re-fires its O(n³) pass on every domain change; pairwise `$fd_neq` only fires
  when a variable grounds. On crypt-arithmetic the pairwise version is faster
  (alpha first-fail ~3 s vs ~7 s). So `fd_all_different` stays pairwise.

Kept the native Hall for **`all_distinct/1`** (the user-facing strong
constraint): same pruning strength as the old Prolog Hall but native, so it is
strictly better for problems that genuinely need it. This matches SWI/SICStus,
where `all_different` is weak/cheap and `all_distinct` is strong/global, and the
user chooses. All suites green (Embedding 2076 / Core 426 / ISO 277 /
Compiler 282).

**Where alpha leftmost stands:** it needs native-speed *control flow* (the
labelling + propagator-dispatch loop), not just native propagation — i.e. moving
`clpfd_run` / labelling enumeration into C#, or a fundamentally smaller search.
A separate, larger effort; donald and alpha-first-fail are the achievable wins
and are fast.

#### Original investigation (chunk 336) — kept for the reasoning trail

donald posts one big linear column constraint and labels 10 vars. Labeling
**8+** of them throws `error(type_error(evaluable, fd(Dom, _)), is/2)` — an
arithmetic `is/2` whose operand is a literal `fd(Domain, Props)` **clpfd
attribute term**, i.e. a Prolog variable is bound to an attribute structure and
then flows into a bound-arithmetic `is`. The investigation (each step a built +
run probe, all instrumentation reverted afterwards):

- **Not the post, the search.** Posting the constraint succeeds; only deep
  `label/1` throws. Single bindings (`D=5`, `label([D])`) and short prefixes
  (`label([D,O,N,A,L,G])`) are fine; the throw needs `≥8` labelled vars — i.e.
  it needs deep **backtracking** through the propagator chain.
- **Not heap GC.** `SHUMWAY_GC_THRESHOLD=0` (GC fully off) still throws — so it
  is *not* the conservative collector failing to relocate `_attrTable` value
  indices.
- **Not the `#=` attvar=attvar aliasing.** `#=` is `clpfd_expr(L,X),
  clpfd_expr(R,Y), X = Y` — unifying the two sum-tree top vars, which fires the
  `verify_attributes` merge. Rewriting `#=` to a non-aliasing bounds form
  (`$fd_le(X,Y), $fd_le(Y,X)`) still throws (with `fd([],_)` instead of
  `fd(_,_)`). So the merge path is not the (sole) cause.
- **Not the hook receiving a bad attribute.** A diagnostic clause at the top of
  `verify_attributes/4` that throws if `Dom`/`Props` is unbound never fired —
  the engine invokes the hook with a well-formed `fd(Dom, Props)`.
- **Not `get_attr` mis-binding.** `get_attr/3` on a plain (non-attributed) var
  correctly fails and leaves the var unbound.
- **The consistent signature** across every variant is `fd(Dom, Props)` with the
  **`Props` field unbound**. *No clpfd-library `put_attr` ever stores unbound
  props* (every site passes a bound list or `[]`). So the malformed term is not
  built by the library — it is the engine's attribute-value representation read
  back stale: `_attrTable[home][module] = valueHeapIdx` stores the attribute
  *by heap index*, and those heap cells are subject to heap-top rollback on
  backtracking. The picture that fits all the evidence: deep labeling allocates
  an attribute term high on the heap, backtracks past it (heap top rolls back,
  the cells are reclaimed), and a record/trail edge leaves a `valueHeapIdx`
  pointing at a reclaimed cell — materialising later as `fd(Dom, <unbound>)`.
  This is the **attribute-value heap-lifetime under backtracking** subsystem
  (touches the trail + heap invariants — a "stop and consult" area per
  `CLAUDE.md`), not a one-line clpfd patch. Deferred to a focused engine
  session; **not** shipped as a guess. Minimal trigger on hand:
  `/tmp/r5.pl` (donald's constraint + `label([D,O,N,A,L,G,E,R])` under `catch`).
- **alpha**, **multipl** — empty output: investigate (may be the same linear gap).
- Several harder programs time out the GProlog oracle at 25s.
- REPL `use_module(library(clpfd))` doesn't load the library / operators (had to
  add `--clpfd`); Shumway parses a whole file before running its directives, so
  in-file `:- op` / `:- use_module` don't affect the same file. Both real gaps.

**Current: 5/17 oracle-comparable programs byte-match** (crypta, eq10, eq20,
five, send). The multiplication bug + missing `read_integer` block most of the
rest.

## TODO

- Fix the `$fd_times` shared-var / ground-enforcement bug (highest value).
- `read_integer/1`; donald's materializer exception; alpha/multipl.
- The 7 programs needing `fd_element` / `fd_minimize` / `fd_tell`.
- Vanilla `c:\temp` programs (LinesOfAction.pl, …).
