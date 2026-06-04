# Van Roy benchmark baseline

_Generated 2026-06-04 12:58_  
_Runs per cell_: **1** (median reported)  
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
| nreverse | 10000 | 304.45 | 9.76 | 40.14 |
| qsort | 5000 | 599.49 | 45.62 | 101.48 |
| queens | 2000 | 1237.81 | 211.47 | 1054.65 |
| tak | 500 | 54699.17 | 3957.64 | 22166.11 |
| serialize | 1000 | 141.63 | &lt;noise&gt; | 17.30 |
| flatten | 10000 | 194.97 | 49.51 | 28.10 |
| sendmore | 100 | 740295.60 | 86927.18 | 626925.93 |
| zebra | 200 | 8374.37 | 1401.04 | 10965.81 |
| boyer | 2000 | 16.49 | &lt;noise&gt; | 4.69 |
| crypt | 500 | 11964.32 | 654.34 | 4761.40 |

## Ratios vs Shumway (>1.0 means Shumway is slower)

| Benchmark | Shumway / GProlog | Shumway / SWI |
|---|---:|---:|
| nreverse | 31.19× | 7.58× |
| qsort | 13.14× | 5.91× |
| queens | 5.85× | 1.17× |
| tak | 13.82× | 2.47× |
| serialize | &lt;noise&gt; | 8.19× |
| flatten | 3.94× | 6.94× |
| sendmore | 8.52× | 1.18× |
| zebra | 5.98× | 0.76× |
| boyer | &lt;noise&gt; | 3.52× |
| crypt | 18.28× | 2.51× |

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
- hyperfine runs: warmup 1 + max(1,3) timed runs per external cell; 
  median reported (stddev in the CSV / console `tot_sd%`). Shumway uses 
  the harness `--runs` value (1) directly. `--runs N` raises both.
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
