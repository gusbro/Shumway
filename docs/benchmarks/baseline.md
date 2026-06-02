# Van Roy benchmark baseline

_Generated 2026-06-02 12:13_  
_Runs per cell_: **3** (median reported)  
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
| nreverse | 10000 | 211.32 | &lt;noise&gt; | 20.92 |
| qsort | 5000 | 252.02 | 21.45 | 240.13 |
| queens | 2000 | 2735.57 | 97.50 | 1245.94 |
| tak | 500 | 74198.08 | 3463.62 | 18010.04 |
| serialize | 1000 | 127.99 | 10.08 | 31.30 |
| flatten | 10000 | 124.24 | 9.89 | 23.10 |
| sendmore | 100 | 1190518.00 | 92934.73 | 396480.43 |
| zebra | 200 | 7829.17 | 1452.53 | 4511.38 |
| boyer | 2000 | 19.69 | &lt;noise&gt; | 6.56 |
| crypt | 500 | 14616.22 | 655.89 | 4960.18 |

## Ratios vs Shumway (>1.0 means Shumway is slower)

| Benchmark | Shumway / GProlog | Shumway / SWI |
|---|---:|---:|
| nreverse | &lt;noise&gt; | 10.10× |
| qsort | 11.75× | 1.05× |
| queens | 28.06× | 2.20× |
| tak | 21.42× | 4.12× |
| serialize | 12.70× | 4.09× |
| flatten | 12.56× | 5.38× |
| sendmore | 12.81× | 3.00× |
| zebra | 5.39× | 1.74× |
| boyer | &lt;noise&gt; | 3.00× |
| crypt | 22.28× | 2.95× |

## Methodology

- Each cell measures wall time of running `bench(N)` against the engine. 
  For Shumway that's a `PrologEngine.Query` call; for GProlog it's a fresh 
  process running the gplc-compiled native `.exe`; for SWI it's a fresh 
  `swipl -g` process.
- `startup_ms` is `bench(0)` (just consult + halt), `total_ms` is `bench(N)`. 
  Per-iteration time = `(total - startup) / N`. When `total - startup ≤ 0.5 ms` 
  the work is below the wall-clock noise floor and we print `<noise>` 
  instead of a misleading number.
- 3 timing runs per cell; median reported. 
  Re-run with `--runs N` to average more samples.
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
SWI-Prolog 9.x at the standard install path, and Visual Studio 2017+ with 
VC++ C++ Build Tools (vcvars64 discovered via vswhere).
