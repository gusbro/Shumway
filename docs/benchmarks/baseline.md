# Van Roy benchmark baseline

_Generated 2026-06-02 15:34_  
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
| nreverse | 10000 | 122.96 | 10.06 | 28.69 |
| qsort | 5000 | 217.53 | 20.03 | 60.78 |
| queens | 2000 | 1074.63 | 88.79 | 800.32 |
| tak | 500 | 48224.59 | 2732.19 | 15221.60 |
| serialize | 1000 | 115.66 | 10.13 | 21.22 |
| flatten | 10000 | 92.84 | 9.93 | 51.95 |
| sendmore | 100 | 858895.97 | 58315.03 | 293879.61 |
| zebra | 200 | 6175.49 | 863.91 | 2860.16 |
| boyer | 2000 | 12.40 | &lt;noise&gt; | 5.07 |
| crypt | 500 | 8053.74 | 348.20 | 3278.34 |

## Ratios vs Shumway (>1.0 means Shumway is slower)

| Benchmark | Shumway / GProlog | Shumway / SWI |
|---|---:|---:|
| nreverse | 12.22× | 4.29× |
| qsort | 10.86× | 3.58× |
| queens | 12.10× | 1.34× |
| tak | 17.65× | 3.17× |
| serialize | 11.42× | 5.45× |
| flatten | 9.35× | 1.79× |
| sendmore | 14.73× | 2.92× |
| zebra | 7.15× | 2.16× |
| boyer | &lt;noise&gt; | 2.44× |
| crypt | 23.13× | 2.46× |

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
