# Van Roy benchmark baseline

_Generated 2026-06-02 20:36_  
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
| nreverse | 10000 | 117.52 | 10.57 | 37.30 |
| qsort | 5000 | 168.60 | 19.13 | 64.42 |
| queens | 2000 | 979.77 | 69.72 | 910.73 |
| tak | 500 | 44515.04 | 3328.58 | 11829.61 |
| serialize | 1000 | 88.66 | &lt;noise&gt; | 28.44 |
| flatten | 10000 | 102.09 | 4.91 | 16.53 |
| sendmore | 100 | 736420.42 | 53623.46 | 297650.03 |
| zebra | 200 | 5888.67 | 717.64 | 2734.63 |
| boyer | 2000 | 16.21 | &lt;noise&gt; | 4.14 |
| crypt | 500 | 7487.91 | 270.78 | 3736.43 |

## Ratios vs Shumway (>1.0 means Shumway is slower)

| Benchmark | Shumway / GProlog | Shumway / SWI |
|---|---:|---:|
| nreverse | 11.12× | 3.15× |
| qsort | 8.82× | 2.62× |
| queens | 14.05× | 1.08× |
| tak | 13.37× | 3.76× |
| serialize | &lt;noise&gt; | 3.12× |
| flatten | 20.79× | 6.18× |
| sendmore | 13.73× | 2.47× |
| zebra | 8.21× | 2.15× |
| boyer | &lt;noise&gt; | 3.92× |
| crypt | 27.65× | 2.00× |

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
