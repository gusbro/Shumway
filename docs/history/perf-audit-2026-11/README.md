# Performance audit — 2026-11 (4-agent sweep, post-phase-43)

Static analysis only (no `dotnet` in the analysis container): every gain below
is an estimate to confirm with the Van Roy harness, `--alloc`, the interop
benchmark or BenchmarkDotNet, back-to-back A/B. Each agent checked its items
against `efficiency-audit-2026-06.md`, the ADRs (incl. rejected 021/026/032),
the phase closures and `git log`. Detailed per-area reports, with `file:line`,
cost analysis, measurement plan and novelty check per item:

| Area | Report | Items |
|---|---|---|
| Core engine + Tier-0 interpreter | `01-core-interpreter.md` | C1–C12 |
| Tier-1 IL | `02-tier1-il.md` | I1–I11 |
| WAM compiler / codegen / indexing | `03-wam-compiler.md` | W1–W8 |
| Builtins, embedding, libraries | `04-builtins-embedding.md` | B1–B12 |

Spot-verified by reading code during consolidation: B1 (`StaticClausesFor`
scans every clause of every module and interns a head per clause), B5
(`predsort/3` is insertion sort), B8 (`length/2` walks via `'$cyclic_spine'`
then `'$list_length'`), I1 (`IsRegionEmittable` rejects `MemberCount < 2`),
C3/I6 (`_cellsAllocated` touched at 27 sites).

---

## 0. Fix first — correctness / resource hazards found along the way

| # | Item | Why first |
|---|---|---|
| C7 | `UnifyPstrLis` (PSTR vs cons) recurses through public `Unify`, resetting the depth guard, and allocates 2 cells/element | Uncatchable C# stack overflow on long lists — contradicts phase-41 "deep walks off the C# stack". Rewrite as a lockstep loop. |
| W5 | Runtime `assertz` of a rule containing `;`/`->`/`\+`/… mints a **permanent** atom per construct (`HelperName` + `InternPredicate(permanent: true)` + monotonic helper id) | Unbounded atom growth in long-running rules engines (a primary use case). ADR-003 addendum territory. |

## 1. Tier A — algorithmic (big multipliers, mostly low risk)

| # | Item | Est. gain | Notes |
|---|---|---|---|
| B1 | `clause/2` is O(whole program) per call; `'$clause_enum'` copies the dynamic list and AST-filters, ignoring the `DynamicClauseIndex` that `retract/1` uses | 10–100× on meta-interpreters | Per-derivation fid→clauses index + reuse first-arg index |
| B2 | Tabling stores tables as dynamic preds read via `clause/2`; answer lookup O(answers), round O(subgoals²), `'$tbl_seen'` canonicalises answers to strings | ≥10× on the `Category=Slow` tabled fixpoint | C# table store keyed by variant hash, answers as `SolutionBuffer` images. **Needs design note/ADR** |
| I1 | Standalone multi-clause predicates (member/append/select/nth, library utilities, unowned fact tables) never get ADR-057/058/060 regions: helper CP push (11 cells + delegate side-stack), backtrack via interpreter, no registers in locals | 5–15% where hot code is shared utilities (clpz, Logtalk, Blint); ~0 on Van Roy | Allow one-member regions. ADR-061 measured +13% on queens for inline CPs in a one-predicate unit |
| B5/B6 | `predsort/3` insertion sort O(n²); `atomic_list_concat/2,3` right-fold interning an atom per step, split via `findall` per step | n-dependent, large | Merge sort; one-pass builtins |
| B3/B4 | Per-query fixed cost (loop over all promoted preds with `Substring`, `ValidatePublicUniqueness` + hotness census per query, ~15 closures, `Query<T>(string)` double parse, `BuildSolution` Term trees); no prepared/cached query | 2–4× on µs-scale query-per-request | `PreparedQuery` API + LRU keyed on derivation/program/pool stamps. Core embedding use case |
| W1 | Guard-driven type indexing: a clause starting `var/atom/compound(A)` on a fresh head var is `ArgKind.Var` and sits in every bucket → k−1 CP pushes + failed guards per call | Large on type-dispatching walkers | Bucket membership only, no opcode change; agent gives the soundness table |
| B10 | `EnginePool` isolation = full engine rebuild per rental | ms → µs per request | `ResetState` policy over existing `SaveDb`/`RestoreDb` + globals/recorded/flags/tables. **ADR-010 doc update** |

## 2. Tier B — constant-factor wins on hot paths

| # | Item | Est. gain | Notes |
|---|---|---|---|
| C3 = I6 | `_cellsAllocated` 64-bit RMW on every heap allocation, both tiers + 7 IL emit sites. **Found independently by two agents** | 1–3% on allocation loops (nreverse — the one loss vs GNU) | Derive as `_cellsReclaimed + _heapTop`, account at the ~9 sites that lower `_heapTop`; `--alloc` bit-identical |
| I2 | Inline ITE CPs inside regions still use `PushIlChoicePoint`, not the ADR-058 marker-BP ladder | 5–10% boyer | Give the ITE cursor a resume entry |
| I3 | CP-free inline ITE for binding-free det conditions (`==`, type tests, compares) | 2–8% ITE-dense code | Reuse ADR-031 guard recognizer. **Extends ADR-031 scope** |
| W2 | Control-construct helpers receive every named variable, incl. branch-locals → extra `put_variable` + heap cell + wider CP per `;`/`->`/`\+`/`once`/`findall` | broad, modest | Pass only vars shared with the enclosing clause (all under DebugCodegen) |
| I5 | JIT inline budget (1,024-locals cap) spent on `Cell.Atom`/`Cell.Int`, `GetRegister`, `TrailBind`, `AllocateHeapUnbound` | same class as the wake-check read that measured 2–5% | Emit as IL. `ldc.i8` cells need an 8-byte **bundle patch kind** |
| I4 | Two delegate hops per dispatch/backtrack (`TableEntry` → `del.Invoke`, `Register` wraps `new Func(del)`); `SetPc` called, not inlined | ~3–6 ns of ~80 ns per transfer | Create `Func` directly; emit `stfld` |
| C5 | Each dynamic call pays a `ConditionalWeakTable` probe + 3–5 `Dictionary` probes in the ADR-041 selector | Tier-0's main steady-state workload | Resolve chain table per activation; merge maps |
| C1/C2/C4 | Tier-0 dispatch: `pc` round-trips through `_engine._p` each instruction; ~35 operand sites still double-bounds-checked via `in ProgramView`; `RunUnifySequence` re-reads mode/pointer fields per op | 3–8% / small / 2–5% list-heavy, Tier-0 only | Locals across straight-line regime; peeled `ReadI32` + Invalid padding |

## 3. Quick wins (small, near-zero risk)

- **B8** `length/2` walks a proper list twice → reorder (~2× on `length`).
- **C6** read-mode `unify_value_x/y`/`get_value_x/y` bypass the atom/int fast path of `UnifyHeapWithCell` — 2-line routing fix.
- **B9** `aggregate_all(count/sum)`/`countall/2` build a findall list to fold it; `max`/`min` missing.
- **B12** `keysort` compares pair functor by name string per element; `format/2` allocs per call; `atom_codes(+,+)` double walk.
- **I7** `cursor == 0` fast test before the entry switch.
- **C9/I9** don't zero Y slots that are written before the first safe point (GC is conservative).
- **C12/I11** misc: unchecked `Cell.Int(int)`, unchecked `UnwindTrails` on CP path, double marker decode in `ContinueIlTailCall`.

## 4. Larger or model-dependent

- **B7** atom text builtins intern via `Substring`; `sub_string/5` interns every substring → `GetAlternateLookup<ReadOnlySpan<char>>` (.NET 9; **net48 fallback per ADR-043**).
- **B11** own clpfd has no propagation agenda (re-runs every propagator recursively) → queue with per-propagator scheduled flag; 1.3–2× on dense models.
- **C8** attribute store `Dictionary<int, Dictionary<int,int>>` → make the flat `AttrMirror` table the store; pool wake-drain scratch.
- **W4** compile-time: ~10 walks per clause with string-keyed allocating collections → 1.5–2× of consult/assert-rule compile share (`SHUMWAY_LOAD_PROF`).
- **W3** Y-slot last-occurrence ordering (Warren) so trimming actually shrinks frames.
- **W6** nested arithmetic via `a_int_bin` into X registers (Tier-0 only; must not regress Tier-1 `ArithBounds`).
- **I8/I10** size register bank at method entry; conditional inline CP push when region budget allows.
- **C11** split the 2,600-line `Dispatch` (measure JitDisasm spills first).

## 5. Needs an ADR / design note before implementing

| Item | Why |
|---|---|
| C10 arity in Functor cell payload | Persisted images, bundles and wasm encode Functor cells |
| B2 tabling store | Replaces the Prolog-level design |
| B10 pool reset policy | New ADR-010 surface |
| I3 CP-free ITE | Extends ADR-031 scope |
| I5 IL-emitted cell constants | Bundle patch format bump |
| W5 helper atoms for asserted rules | ADR-003 atom lifetime |

## 6. Suggested order

1. C7, W5 (hazards).
2. Quick wins (§3) + C3/I6 — cheap, measurable, both tiers.
3. B1 → B2 (clause/2 index unlocks the tabling rewrite), B5/B6.
4. I1 (one-member regions) then I2/I3 — biggest Tier-1 lever left.
5. B3/B4/B10 as an "embedding throughput" arc, measured on the interop benchmark.
6. W1, W2 codegen, verified with `shumway-disasm` instruction counts.

## 7. Measured against `main` (review, 2026-11-07)

The review branch (`perf-audit-review`, the items brought one by one) against
`main` at 78fba0d1, both built Release, on the same notebook with the processor
capped at 99% (no turbo) and the cap restored afterwards, in ABBA order so a
drift favours neither side. Wall-clock numbers are the second run inside one
process (the first run promotes); the deterministic metrics are the harness's
cells per iteration and the interpreter's inference counts with the tier off.

**The Van Roy suite is unchanged.** Cells per iteration are identical on all
ten programs on both tiers, and the wall clock stays inside the suite's own
noise: review/main between 0.95 and 1.07 on Tier-0 and 0.96 and 1.12 on
Tier-1, with the two `main` rounds themselves up to 10% apart (nreverse on
Tier-1: 146 and 180 ms). None of the items touches what these programs run.

**The items, where the work went** (cpu ms, mean of six capped runs, and the
interpreter's inferences for the same goal with the tier off):

| Item | Goal | main | review | Ratio | Inferences main → review |
|---|---|---|---|---|---|
| B8 `length/2` | a 200,000-element proper list, 100 times | 864 | 482 | 1.8× | 401,108 → 400,908 |
| B5 `predsort/3` | 3,000 pseudo-random integers | 2,506 | 271 | 9.3× | 17,764,468 → 224,157 |
| B6 `atomic_list_concat/3` | join 4,000 numbers, split them back | 3,490 | 26 | 134× | 32,128,026 → 48,032 |
| B1 `clause/2` | 3,000 keyed lookups in a 10,000-clause dynamic predicate | 1,086 | 104 | 10.4× | 60,006 → 60,006 |
| C7 packed string | a 20,000-character string unified with a cons list, 300 times | 1,365 | 399 | 3.4× | 40,330 → 40,328 |
| W5 asserted rules | 1,500 assert, call, retract rounds of a rule with control constructs | 549 | 507 | 1.1× | 28,506 → 28,506 |

Two of these need a word. `predsort/3` on a *reversed* list is the old
insertion sort's best case (one comparison per element, the new element always
goes first), and there the merge sort is six times slower (250 against 40 ms
for 6,000 elements); on a sorted list of 6,000 the insertion sort takes 10.6 s
and on a random one 4.2 s, where the merge sort sorts 50,000 in 344 and
312 ms. A sort is measured on random input. W5's gain is not time: it is the
helper names, atoms and compiled helpers that an assert/retract cycle no
longer mints per round (`AssertHelperReuseTests` pins the counts), which a
run of 1,500 rounds does not show on a clock. B1 and C7 move no inference:
their work was in C#, and the counts say the Prolog side is untouched.
