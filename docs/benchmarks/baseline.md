# Van Roy benchmark baseline

_Generated 2026-06-03 18:34_  
_Runs per cell_: **5** (median reported)  
_Machine_: GUSBRO-NB, .NET 10.0.8, 8 cores, Microsoft Windows NT 10.0.19044.0

## Engines

| Engine | Version | Mode |
|---|---|---|
| Shumway | (this build) | Bytecode interpreter + Tier-1 IL (in-process) |
| GNU Prolog | 1.5.0 | Native compiled (`gplc` + MSVC link) |
| SWI-Prolog | 9.2.3 | Interpreted (`swipl -g`) |

Correctness: every benchmark's `report/0` fingerprint was verified 
equal across the three engines (whitespace-normalised) before timing.

## Per-iteration time (µs, lower is better)

| Benchmark | Iters | Shumway | GProlog (native) | SWI |
|---|---:|---:|---:|---:|
| nreverse | 10000 | 454.84 | 17.84 | 34.88 |
| qsort | 5000 | 455.97 | 23.86 | 156.75 |
| queens | 2000 | 2006.11 | 184.63 | 931.55 |
| tak | 500 | 57011.76 | 4134.46 | 20635.43 |
| serialize | 1000 | 187.53 | &lt;noise&gt; | 36.12 |
| flatten | 10000 | 192.77 | 9.84 | 39.89 |
| sendmore | 100 | 615450.75 | 74665.69 | 376512.89 |
| zebra | 200 | 7689.22 | 1814.84 | 4426.84 |
| boyer | 2000 | 17.55 | &lt;noise&gt; | 5.37 |
| crypt | 500 | 5463.05 | 684.55 | 3394.52 |

## Ratios vs Shumway (>1.0 means Shumway is slower)

| Benchmark | Shumway / GProlog | Shumway / SWI |
|---|---:|---:|
| nreverse | 25.49× | 13.04× |
| qsort | 19.11× | 2.91× |
| queens | 10.87× | 2.15× |
| tak | 13.79× | 2.76× |
| serialize | &lt;noise&gt; | 5.19× |
| flatten | 19.60× | 4.83× |
| sendmore | 8.24× | 1.63× |
| zebra | 4.24× | 1.74× |
| boyer | &lt;noise&gt; | 3.27× |
| crypt | 7.98× | 1.61× |

## Measurement notes

- **This run's absolute µs/iter ran ~2× the previous (10:23) cool baseline
  across _every_ engine and benchmark — including `nreverse`, which has no
  arithmetic at all (109 → 455 µs/iter).** That is a thermal-state / sustained-
  load regime shift, **not** a regression: the deterministic `--alloc` cell
  counts are unchanged (e.g. `qsort` 2424 vs 2425 — a one-cell difference),
  and a structural regression cannot slow an arithmetic-free benchmark 4×.
  Treat absolute deltas against an older baseline as meaningless here; the
  **cross-engine ratios** (all three engines timed in the same session) and
  the **`--alloc` metric** are the signals. Several cells carry high
  wall-clock variance this run (`nreverse` 41 %, `sendmore` 34 % stddev);
  `qsort` / `boyer` are below the reliable wall-clock threshold — trust their
  `--alloc` counts.

- **ADR-018 arithmetic instruction set (chunks 298–301)** — `X is Expr` and
  the six comparisons now compile to RPN `a_eval_*` / fused `a_int_bin` /
  `a_int_cmp` opcodes over an eval stack with a raw-`long` integer fast lane,
  replacing the goal-rewriting `$arith2`/`$arith1` inlining. Back-to-back
  isolated A/B vs the pre-ADR build (chunk 297) confirmed **parity-or-better
  Tier-0 wall-clock**: `crypt` clearly faster (≈ −25 to −46 %), `qsort`
  ≈ −11 %, `tak` / `queens` at parity (within noise). `--alloc` wins (the
  arithmetic synthetic-variable heap homes are gone): `sendmore` 28,
  `crypt` 19, `queens` 6626, `tak` 397556 cells/iter; zero heap for nested
  integer arithmetic, and the whole path is Tier-1 IL-emittable.

## Methodology

- **Shumway** is timed **in-process** (a warm `PrologEngine.Query`), 
  because its target is embedding in a long-lived .NET application — that 
  is how it is actually used. Running it as a fresh `dotnet` subprocess to 
  match the externals would inject ~330 ms of JIT-cold runtime startup 
  with ~33% variance that the `bench(0)` subtraction can't cleanly remove 
  (Native-AOT would fix it, but AOT is not the target deployment).
- **GProlog** and **SWI** are timed as fresh processes under 
  [hyperfine](https://github.com/sharkdp/hyperfine) (warmup + statistical 
  outlier detection + median/stddev): GProlog the gplc-compiled native 
  `.exe`, SWI as `swipl -g`. Their native startup (~60-100 ms) is small 
  and stable, so the subprocess regime measures them well. When hyperfine 
  is absent the harness falls back to a Stopwatch median over the runs.
- `startup_ms` is `bench(0)` (consult + halt; for externals also process 
  start), `total_ms` is the median of `bench(N)`. Per-iteration time = 
  `(total - startup) / N`. When `total - startup ≤ 0.5 ms` the work is 
  below the wall-clock noise floor and we print `<noise>`.
- For deterministic, noise-free comparison of Shumway-internal changes 
  (immune to Turbo Boost / scheduler / background load), use the `--alloc` 
  mode: it reports WAM cells allocated per iteration, bit-identical across 
  runs. That is the right tool for A/B-ing an optimisation; wall-clock is 
  for cross-engine positioning.
- hyperfine runs: warmup 1 + max(5,3) timed runs per external cell; 
  median reported (stddev in the CSV / console `tot_sd%`). Shumway uses 
  the harness `--runs` value (5) directly. `--runs N` raises both.
- GProlog runs as a native, statically-linked Windows .exe 
  (`/SUBSYSTEM:CONSOLE`) compiled by `gplc` with 256 MB global stack, 
  32 MB local, 32 MB trail. Compilation routes through `cmd /c vcvars64 
  >NUL && gplc` to give gplc the MSVC environment its internal `cl` / 
  `link` shells need.

## Reproducibility

```
dotnet run -c Release --project tests/Shumway.Tests.Benchmarks/ -- --vanroy --runs 5
```

Requirements: GProlog 1.5+ on PATH or at `C:\GProlog\bin\gprolog.exe`, 
SWI-Prolog 9.x at the standard install path, Visual Studio 2017+ with 
VC++ C++ Build Tools (vcvars64 discovered via vswhere), and 
[hyperfine](https://github.com/sharkdp/hyperfine) (`winget install 
sharkdp.hyperfine`; resolved via PATH, `HYPERFINE_PATH`, or the winget 
package dir). Without hyperfine the harness still runs (Stopwatch fallback).
