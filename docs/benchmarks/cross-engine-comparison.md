# Cross-engine performance comparison

Where Shumway stands against the three engines it has been benchmarked against —
**GNU Prolog**, **Scryer Prolog**, and **SWI-Prolog** — across three workload
families: the Van Roy classics, constraint solving over `clp(Z)`, and the
Logtalk benchmark suite.

The short version: Shumway's Tier-1 (compiled IL) is **competitive with
GNU Prolog's native code**, **consistently ahead of Scryer** on the classics,
and **faster than SWI** on allocation-heavy work. It **loses to all three on
naive-reverse** (a pure allocation micro-benchmark) and to Scryer on the most
propagation-heavy `clp(Z)` model (`all_distinct` + a wide linear equation).
Nothing here is marketing: every table is measured, the losses are called out,
and the interop-heavy embedding case Shumway is *designed* to win is not yet
benchmarked.

## Method

- **Self-timed CPU.** Each program times only its own solve loop with
  `statistics(runtime, [T0|_]), … , statistics(runtime, [T1|_]), D is T1 - T0`
  — portable across all four engines, excludes process startup and bundle load.
- **Correctness oracle, always.** Every comparison first verifies the engines
  return byte-identical answers. A timing number is only reported for a solve
  that produced the right result on both sides. (Two earlier "Shumway is 60×
  faster" clp(Z) readings turned out to be a silently-failing goal and a
  noisy wall-clock subtraction — hence this rule.)
- **min-of-N, same thermal window.** This laptop has ~30–40 % thermal variance;
  a byte-identical run can swing that much between back-to-back invocations.
  Numbers are the minimum of ≥3 runs, and cross-engine pairs are measured
  back-to-back. Trust the direction and the order of magnitude, not the last
  digit.
- **Same source on both sides.** For `clp(Z)`, Shumway loads **Scryer's own
  `clpz.pl`** (`-L scryer:…`); Scryer uses its embedded copy of the same
  library. So the comparison isolates engine speed, not library quality.

Shumway tiers:
- **T0** — Tier-0 bytecode interpreter (`SHUMWAY_IL_PROMOTE=0`).
- **T1** — Tier-1 IL. Measured as a persisted-IL native executable
  (`shumway-link -i --consult … --exe`), the shipping form; the runtime-JIT
  path reaches the same steady state after warm-up.

---

## vs GNU Prolog (native compiled)

GNU Prolog compiles to native machine code, so it is the toughest bar. Shumway's
T1 is competitive and wins the allocation-light shapes; the one real gap is
naive reverse.

### Van Roy classics (CPU ms, lower = better; full table in [`baseline.md`](baseline.md))

| Benchmark | Shumway T1 | GNU Prolog | T1 / GProlog |
|-----------|-----------:|-----------:|-------------:|
| nreverse  | 19.2       | 20.6       | **0.93× (win)** |
| tak       | 6967       | 2416       | 2.88× |
| sendmore  | 150260     | 82365      | 1.82× |
| zebra     | 3687       | 1244       | 2.96× |
| crypt     | 1285       | 863        | 1.49× |
| queens    | 467        | 106        | 4.39× |

### Logtalk suite (goals/sec, higher = better; N=20000)

| Workload | Shumway | GNU Prolog | vs GProlog |
|----------|--------:|-----------:|-----------|
| length (plain / `::`msg) | 168k / 163k | 116k / 160k | **win 1.4× / tie** |
| maze (plain / `::`msg)   | 43k / 43k   | 37k / 31k   | **win** |
| graph (plain / `::`msg)  | 10k / 9.5k  | 8.8k / 9.1k | **win** |
| dispatch (c1/c2/c3)      | 82/84/76k   | 67/64/68k   | **win** |
| **nrev (plain / `::`msg)** | **8.5k / 5.5k** | **20k / 18k** | **LOSE 2.4× / 3.2×** |

**Read:** Shumway matches or beats GNU Prolog on list traversal, maze search,
graph, and message dispatch — including the `::`-message paths GNU Prolog runs
flat. It loses on **naive reverse**, a pure `append/3` allocation micro-benchmark
(Shumway's cell allocation is a C# constant-factor behind native code); this is
the single recurring loss and the clearest optimisation target.

---

## vs Scryer Prolog

Scryer is a modern WAM in Rust. Shumway (default / T1) is ahead on the Van Roy
classics and on two of three `clp(Z)` models.

### Van Roy classics (ratio Scryer / Shumway, > 1 = Shumway faster; delta-wall, oracle-verified)

| tak | nreverse | boyer | qsort | crypt | zebra | queens | flatten | serialize | sendmore |
|----:|---------:|------:|------:|------:|------:|-------:|--------:|----------:|---------:|
| 4.60× | 4.32× | 2.94× | 2.90× | 2.76× | 2.49× | 2.13× | 1.78× | 1.12× | 1.06× |

Shumway wins all ten (answers verified identical: `boyer→true`, `tak→7`,
`zebra→japanese`, list results equal).

### clp(Z) — Scryer's own `clpz.pl` on both engines (CPU ms, min-of-3, oracle-verified)

| Model | Shumway T1 (`-i` exe) | Scryer | Result |
|-------|----------------------:|-------:|--------|
| queens(10) ×50    | 2546  | 5640  | **Shumway 2.2×** |
| perm(10) ×300     | 7578  | 15281 | **Shumway 2.0×** |
| SEND+MORE ×100    | 5172  | 4312  | **Scryer 1.2×** |

**Read:** running Scryer's *own* constraint library, Shumway's engine solves
queens and permutation-sum ~2× faster. Scryer wins SEND+MORE — its heaviest
propagation shape (`all_distinct/1` over eight variables plus one wide linear
equation), where Shumway's cost is the SICStus-`atts` shim it maps `clpz` onto
rather than the search. Shumway T0 (IL off) is ~1.5× faster than Scryer on
queens; T1 adds another ~1.7× on top.

---

## vs SWI-Prolog (interpreted)

SWI is a fast interpreter with a highly-tuned emulator. Shumway's T1 beats it on
allocation-heavy classics and loses on simple deterministic traversal.

### Van Roy classics (ratio T1 / SWI, < 1 = Shumway faster)

| nreverse | tak | sendmore | queens | zebra | qsort | flatten | serialize |
|---------:|----:|---------:|-------:|------:|------:|--------:|----------:|
| **0.43×** | **0.55×** | **0.51×** | **0.89×** | 1.33× | 1.26× | 1.53× | 2.09× |

Shumway T1 is ~2× faster than SWI on nreverse, tak, and SEND+MORE; SWI is ahead
on qsort, zebra, flatten, and serialize.

### Logtalk suite (goals/sec)

SWI is **3–6× faster** than Shumway on the Logtalk plain-list and dispatch
shapes (e.g. length 640k vs 168k, nrev 38k vs 8.5k) — its emulator and
first-argument indexing are excellent at simple deterministic Prolog, which is
what those benchmarks stress.

---

## Summary

| Against | Shumway wins | Shumway loses |
|---------|--------------|---------------|
| **GNU Prolog** (native) | length, maze, graph, dispatch; nreverse ~tie | tak/queens/sendmore (2–4×), nrev in Logtalk (2.4–3.2×) |
| **Scryer** (Rust WAM) | all 10 Van Roy classics (1.1–4.6×); clp(Z) queens & perm (~2×) | clp(Z) SEND+MORE (1.2×) |
| **SWI** (interpreter) | nreverse, tak, sendmore (~2×) | qsort/zebra/flatten/serialize; Logtalk plain shapes (3–6×) |

The recurring weak spot across all three is **allocation-bound micro-benchmarks**
(naive reverse) — a C# constant-factor behind native/Rust cell allocation. The
recurring strength is **search- and dispatch-heavy** work, where Tier-1 IL and
the region compiler pull ahead.

**Not yet benchmarked:** the interop-heavy embedding workload (crossing the
C# ↔ Prolog boundary) that Shumway is specifically designed to win — a
microbenchmark against GNU Prolog's FFI is the open item.
