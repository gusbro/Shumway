# Van Roy benchmark baseline

_Generated 2026-06-03 10:23_  
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
| nreverse | 10000 | 109.70 | 6.82 | 26.36 |
| qsort | 5000 | 207.10 | 8.32 | 65.99 |
| queens | 2000 | 972.86 | 83.20 | 587.17 |
| tak | 500 | 41929.20 | 2634.59 | 13858.82 |
| serialize | 1000 | 91.19 | &lt;noise&gt; | 14.06 |
| flatten | 10000 | 87.81 | 5.33 | 19.32 |
| sendmore | 100 | 635238.30 | 57682.37 | 288121.36 |
| zebra | 200 | 5969.22 | 1212.05 | 3022.70 |
| boyer | 2000 | 15.79 | &lt;noise&gt; | 3.40 |
| crypt | 500 | 6103.47 | 445.40 | 3498.11 |

> qsort's Shumway figure was re-measured back-to-back against the
> pre-Phase-25 build (chunk 286): both land at ~200-220 µs/iter, so there
> is no regression — the original run's 335 µs was a machine-noise spike
> (qsort wall-clock here has ~33 % stddev). boyer (~16 µs/iter) is below
> the reliable wall-clock threshold; trust its `--alloc` cell count
> instead. The deterministic `--alloc` metric is the canonical signal for
> Shumway-internal change; this table is for cross-engine positioning.

## Ratios vs Shumway (>1.0 means Shumway is slower)

| Benchmark | Shumway / GProlog | Shumway / SWI |
|---|---:|---:|
| nreverse | 16.07× | 4.16× |
| qsort | 24.89× | 3.14× |
| queens | 11.69× | 1.66× |
| tak | 15.91× | 3.03× |
| serialize | &lt;noise&gt; | 6.49× |
| flatten | 16.49× | 4.55× |
| sendmore | 11.01× | 2.20× |
| zebra | 4.92× | 1.97× |
| boyer | &lt;noise&gt; | 4.65× |
| crypt | 13.70× | 1.74× |

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
