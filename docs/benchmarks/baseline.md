# Van Roy benchmark baseline

_Generated 2026-06-02 11:40_  
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
| nreverse | 10000 | 236.49 | 18.03 | 84.53 |
| qsort | 5000 | 241.13 | 24.75 | 93.01 |
| queens | 2000 | 2149.18 | 100.14 | 655.58 |
| tak | 500 | 70517.89 | 4908.14 | 19076.74 |
| serialize | 1000 | 107.05 | 4.19 | 48.27 |
| flatten | 10000 | 91.10 | 8.56 | 23.79 |
| sendmore | 100 | 1230842.22 | 89989.87 | 432726.73 |
| zebra | 200 | 10985.20 | 1295.22 | 2941.94 |
| boyer | 2000 | 25.10 | &lt;noise&gt; | 3.26 |
| crypt | 500 | 11712.46 | 503.15 | 5008.05 |

## Ratios vs Shumway (>1.0 means Shumway is slower)

| Benchmark | Shumway / GProlog | Shumway / SWI |
|---|---:|---:|
| nreverse | 13.12× | 2.80× |
| qsort | 9.74× | 2.59× |
| queens | 21.46× | 3.28× |
| tak | 14.37× | 3.70× |
| serialize | 25.53× | 2.22× |
| flatten | 10.65× | 3.83× |
| sendmore | 13.68× | 2.84× |
| zebra | 8.48× | 3.73× |
| boyer | &lt;noise&gt; | 7.70× |
| crypt | 23.28× | 2.34× |

## Methodology

- Each cell measures wall time of running `bench(N)` against the engine. 
  For Shumway that's a `PrologEngine.Query` call; for GProlog it's a fresh 
  process running the gplc-compiled native `.exe`; for SWI it's a fresh 
  `swipl -g` process.
- `startup_ms` is `bench(0)` (just consult + halt), `total_ms` is `bench(N)`. 
  Per-iteration time = `(total - startup) / N`. When `total - startup ≤ 0.5 ms` 
  the work is below the wall-clock noise floor and we print `<noise>` 
  instead of a misleading number.
- 5 timing runs per cell; median reported. 
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
