# Van Roy benchmark baseline

_Generated 2026-06-02 13:01_  
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
| nreverse | 10000 | 123.34 | 13.04 | 27.71 |
| qsort | 5000 | 202.08 | 21.66 | 63.78 |
| queens | 2000 | 997.13 | 117.35 | 972.14 |
| tak | 500 | 60310.59 | 4712.28 | 19634.13 |
| serialize | 1000 | 270.74 | &lt;noise&gt; | &lt;noise&gt; |
| flatten | 10000 | 93.97 | 9.79 | 32.26 |
| sendmore | 100 | 1103508.74 | 84950.53 | 396880.16 |
| zebra | 200 | 8850.61 | 1439.67 | 4271.93 |
| boyer | 2000 | 21.16 | 14.61 | &lt;noise&gt; |
| crypt | 500 | 11569.70 | 576.17 | 5386.20 |

## Ratios vs Shumway (>1.0 means Shumway is slower)

| Benchmark | Shumway / GProlog | Shumway / SWI |
|---|---:|---:|
| nreverse | 9.46× | 4.45× |
| qsort | 9.33× | 3.17× |
| queens | 8.50× | 1.03× |
| tak | 12.80× | 3.07× |
| serialize | &lt;noise&gt; | &lt;noise&gt; |
| flatten | 9.60× | 2.91× |
| sendmore | 12.99× | 2.78× |
| zebra | 6.15× | 2.07× |
| boyer | 1.45× | &lt;noise&gt; |
| crypt | 20.08× | 2.15× |

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
