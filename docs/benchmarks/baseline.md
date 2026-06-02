# Van Roy benchmark baseline

_Generated 2026-06-02 12:33_  
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
| nreverse | 10000 | 223.51 | 11.53 | 63.36 |
| qsort | 5000 | 281.74 | 14.34 | 67.00 |
| queens | 2000 | 1348.22 | 125.72 | 1042.40 |
| tak | 500 | 58060.85 | 2749.36 | 15010.71 |
| serialize | 1000 | 112.67 | &lt;noise&gt; | 86.09 |
| flatten | 10000 | 79.19 | 6.80 | 21.49 |
| sendmore | 100 | 1179558.82 | 84781.95 | 423320.99 |
| zebra | 200 | 6977.01 | 2464.87 | 3886.84 |
| boyer | 2000 | 23.50 | &lt;noise&gt; | 42.92 |
| crypt | 500 | 12466.95 | 134.92 | 7254.03 |

## Ratios vs Shumway (>1.0 means Shumway is slower)

| Benchmark | Shumway / GProlog | Shumway / SWI |
|---|---:|---:|
| nreverse | 19.38× | 3.53× |
| qsort | 19.65× | 4.21× |
| queens | 10.72× | 1.29× |
| tak | 21.12× | 3.87× |
| serialize | &lt;noise&gt; | 1.31× |
| flatten | 11.65× | 3.69× |
| sendmore | 13.91× | 2.79× |
| zebra | 2.83× | 1.80× |
| boyer | &lt;noise&gt; | 0.55× |
| crypt | 92.40× | 1.72× |

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
