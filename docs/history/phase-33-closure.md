# Phase 33 — Closure

**Status**: complete.

**Tagged**: `phase-33`.

Phase 33 opened as **audit remediation round 1** — five waves attacking the
exhaustive six-way audit of 2026-06-30 (errors / interpreter / WAM codegen /
IL / LTO / four interop boundaries, Arity-compat lens; master backlog with
per-item status in [`phase-33-backlog.md`](phase-33-backlog.md), closed
65/66) — and grew, through user-directed rounds, into the largest phase to
date (138 commits since `phase-32`): real-program bring-up (Logtalk, Djota,
PrologToC), an ISO predicate audit, two indexing ADRs (027/028), and the
**cut / tail-call optimization arc** (ADRs 029–034) that closed with CP-free
guards through every dispatch shape including indexed buckets.

## 1. Audit remediation (waves 1–5)

| Wave | Scope | Outcome |
|------|-------|---------|
| 1 | Correctness critical (E-series) | Native-memory exception safety, HGlobal free-of-foreign-pointer, `string_term/2` operator asymmetry, reftype int truncation, encoding guard, recorded-DB deep ground keys, parse guard, EnginePool reset, `{...}` silent-success. |
| 2 | Interop hot path | Register-read scalar fast path, unboxed converters + compiled convention delegates, typed P/Invoke invoker, pooled string/scalar marshalling, per-block plan, reftype materialize pooling. |
| 3 | WAM codegen | `once`/snips rewrite, neck-cut after inline guards, assert fast-path, Tier-0 ITE inline, DCG disjunction, `execute_builtin` fusion, string-literal pool stability, cut-barrier register threading. |
| 4 | IL dispatch / promotion | Stage B.4 runtime Call→CallIl, background promotion (L2), churn re-arm (L5), 16 KB cap lift, region member widening, `a_int` kind specialization, baked index graphs. |
| 5 | LTO / startup / size | Prelude pruning (`--prune-prelude`, −94% on a small program), bundle compression, process-wide persisted-IL cache, baked WAM link, unfold widening, representation slimming. |

Plus the user-directed **IL round 2** (profile-driven Tier-1 pass over
dotnet-trace sampling) and the corpus rounds below. The one item left open at
the time — the **intermittent native AV** (`0xC0000005` in
`shumway_native_calli`, only under the full parallel suite,
first-run-after-rebuild profile) — had its likeliest cause found and fixed
(`_emitOwnerFid` plain-static under concurrent compiles → `[ThreadStatic]`)
and was later CLOSED as not reproducible (2026-08-02): months of dump-armed
runs without a single hit.

## 2. Real-program bring-up

- **Logtalk 3.101.0**: many library testers green — random 457/457,
  term_io 87/87 (write_term `variable_names`), types 148/149
  (dynamic-dispatch determinism trust-on-last-clause + `$choice_level/1`),
  meta / hierarchies / dictionaries / meta_compiler / sets / heaps / queues /
  assignvars 100%. Cross-engine benchmark: with a fixed 15 ms-granularity
  timer bug, Shumway **matches or beats GNU Prolog on every shape except
  nrev** (2.4–3.2×, allocation-bound — the C# interpreter constant factor).
- **Djota** (Scryer Djot library, heavy DCG): 32/32 tests — six standard-DCG
  engine gaps fixed + a DCG fail-fast output-deferral lowering
  (−15.3% heap cells/render).
- **ISO predicate audit** (GProlog-doc-driven): all gaps fixed — meta-call
  `BuiltinReturnPc`, compact write layout, flags enum, PSTR-as-codes, full
  number syntax, `call/8`, `div`/`**`, stream positions.

## 3. Indexing ADRs

- **ADR-027** — second-level (sub-argument) indexing:
  `switch_on_{atom,integer}_sub`, a bounded 2-hop path (list head / struct
  sub-arg / the Arity token-stream `[t(Sym,_)|_]` idiom).
- **ADR-028** — sibling-arg + structure-keyed indexing INSIDE value buckets
  (`switch_on_structure_sub`, nested `BucketSwitch`), all three tiers, with
  an incidental ADR-027 soundness fix (unbound sub-discriminator dropped
  ground-key clauses).

## 4. The cut / tail-call optimization arc (ADRs 029–034)

Driven by the corpus census (58% of clause bodies end `…, !.`; 36% of tail
calls are `!, tailCall`) and sized at every step by purpose-built
instruments (`--census`, `--detcensus`, `--foldcensus`, `--cpfree`,
`SHUMWAY_CPFREE_IDXCENSUS`, the `shumway-link --verbose` optimization
panorama).

| ADR | Delivered |
|-----|-----------|
| **029** | Clause-epilogue peephole fusion — `CutDeallocateProceed` (30 223 corpus sites); Tier-1 reads `BytecodeUnfused`. |
| **030** | Redundant-cut elimination via a determinism fixpoint — greatest fixpoint, all-but-last-commit dispatch rule, fail-exemption; intra-module default ON; **linker whole-program closure** (`DeterminismAnalysis.WholeProgram`, fired on 154/294 modules of the real apps). |
| **031** | **CP-free guard commit, default ON, every tier**: A (inline cmp, 2.6×), B (binding guards, snapshot/restore, ~1.8×), G (forced leaf inlining, ~2×), G2 (fail-direct multi-clause / self-tail callees as sequential chains + in-place loop), G2-cuts (neck/deep-cut split), G3 (nested fail-direct, budget), staging + callee-cut widenings; lazy CP materialisation under pending wakeups. **Indexed buckets** (see below). |
| **032** | Dynamic guard fail-continuation — SOFT-REJECTED with ceiling analysis (the fail path intrinsically round-trips the engine); superseded by ADR-033. |
| **033** | **Guard continuation stack** (prototype, `SHUMWAY_CPFREE_CONT=1`): ONE shared fail-direct callee copy per IL method + an engine int stack of packed (ok,fail) continuation cursors; runtime parity with duplication; **cross-tail composition** (LCO `br` into the target's copy, last-or-cut-committed + det folding) and **deep G3 v1** (tail cycles via pure-tail-segment back-edges — the mixed-cycle rule; fresh per-copy budget). Non-tail cycles measured (test/ 14+67) and deferred. |
| **034** | **Sound stable-dynamic inlining, default ON** — fixed a shipped logical-update-view bug (ADR-023 snapshots inlined into caller IL with no eviction path; 423/724 of test/'s accepted guards) plus two dispatch-level variants (Call→CallIl hardening of evictable delegates; stale per-query `IlByFunctorId` slot). Rule-bearing dynamics inline under a clause-entry staleness test + un-inlined fallback; fact-only never inline; DB-mutation builtins never combine with embedded snapshots. **Empty-dynamic-as-fail: measured (+69/+111% static acceptance) then rejected** — in real programs the assert happens, so the steady state is the plain path plus a probe; the corpus counts were inflated by host-foreign placeholders whose det-ness the guard machinery already derives (`BacktrackableDetector`). |

**ADR-031 indexed buckets (the arc's capstone, default ON)** — indexed
predicates were invisible to every prior CP-free stat; the census sized them
at ~9–12× the chain population. The "lazy bucket CP" mechanism: a chain node
whose clause is an accepted guard stores the next node's cursor in a
per-member IL local (−1 = tail) and branches to the clause's ONE shared
guard block; guard failure dispatches on the local (an IL switch), and the
rare paths (wakeups, ADR-034 fallback) materialize the skipped CP from it.
Whole-program: test/ 724 → **2 796** accepted, testGen/ 601 → **3 014**
(5×); bundles +9–12%. **A/B: 1.58× end-to-end (~1.7× loop) on a 20 M
dispatch-then-validate loop, every ON sample beating every OFF sample.**
Shipped with a latent case-B/G fix the extension exposed: the lazy CP saved
guard-clobbered argument registers; `Engine.SetTopCpArgRegister` now patches
them back to clause-entry values.

## 5. Carried forward

- ~~The intermittent native AV~~ — closed 2026-08-02, not reproducible.
- ADR-031 minors: `unify_*_y` guard-op residual (~630 corpus), caps raise
  (must come with IL `switch` emission — user directive), a_eval cmp guards.
- ADR-033: non-tail-cycle frames (measured small), v2 cross-method
  continuations, CONT default-ON decision.
- ADR-034: runtime-promotion snapshot feeding; caller re-promotion with the
  settled fact set (needs a runnable corpus).
- Corpus-link fidelity: declare the `i_*` host natives as foreign stubs
  so measurement links stop modelling them as empty dynamics.

## Gate at close

Core **436** / Interpreter **105** / Compiler **351** (+2 skip) /
IsoConformance **277** / Embedding **3009** (+3 skip) — all green, all five
projects.
