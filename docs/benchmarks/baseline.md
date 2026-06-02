# Van Roy benchmark baseline

_Generated 2026-06-02 13:20_  
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
| nreverse | 10000 | 139.98 | 11.76 | 26.63 |
| qsort | 5000 | 196.06 | 18.85 | 64.37 |
| queens | 2000 | 1073.12 | 85.06 | 1108.75 |
| tak | 500 | 59967.77 | 3285.00 | 17911.39 |
| serialize | 1000 | 93.98 | 9.42 | 53.89 |
| flatten | 10000 | 89.61 | 9.27 | 23.36 |
| sendmore | 100 | 1076407.37 | 90608.75 | 393556.94 |
| zebra | 200 | 6846.09 | 1461.79 | 3019.26 |
| boyer | 2000 | 13.53 | 8.15 | &lt;noise&gt; |
| crypt | 500 | 10025.78 | 551.49 | 4786.53 |

## Ratios vs Shumway (>1.0 means Shumway is slower)

| Benchmark | Shumway / GProlog | Shumway / SWI |
|---|---:|---:|
| nreverse | 11.91× | 5.26× |
| qsort | 10.40× | 3.05× |
| queens | 12.62× | 0.97× |
| tak | 18.26× | 3.35× |
| serialize | 9.98× | 1.74× |
| flatten | 9.67× | 3.84× |
| sendmore | 11.88× | 2.74× |
| zebra | 4.68× | 2.27× |
| boyer | 1.66× | &lt;noise&gt; |
| crypt | 18.18× | 2.09× |

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
