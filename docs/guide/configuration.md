# Configuration

Shumway exposes two separate kinds of `SHUMWAY_*` switch, through two different
mechanisms — don't confuse them:

- **Runtime environment variables** — read by the engine, the REPL, or the
  debugger while running. Set them in the environment before launching; no
  rebuild needed. Most of the list below is this kind.
- **Build-time constants** — MSBuild properties that define a compile constant
  gating `[Conditional]` diagnostic code. A normal build strips those call
  sites entirely, so the corresponding switch does nothing unless you built
  with the property set. These live in `Directory.Build.props` and are listed
  in [Build-time constants](#build-time-constants) at the end.

Everything a Prolog *program* can set at runtime is a `prolog_flag`
(`set_prolog_flag/2`), not an environment variable — see the
[predicate reference](predicates.md). This page is about the host-level knobs.

## Runtime environment variables

### Operator knobs

Useful on a stock build, for someone running or embedding Shumway.

| Variable | Effect |
|---|---|
| `SHUMWAY_IL_PROMOTE` | Tier-1 IL promotion threshold: a predicate is compiled to IL after this many calls. Default `32`. `0` or negative disables IL promotion (Tier-0 only). |
| `SHUMWAY_LIBRARY_PATH` | Extra directories (OS path-separated) searched to resolve `:- use_module(library(X))`, in addition to the configured `file_search_path(library, _)` and the shipped `lib/`. |
| `SHUMWAY_TIMING` | `=1` makes the REPL print a per-phase wall-clock breakdown (parse / consult / link / run) to stderr. |
| `SHUMWAY_LOAD_PROF` | `=1` enables load-profiling counters (time spent per consult phase), printed on exit. |
| `SHUMWAY_HISTORY` | Path to the REPL history file. Defaults to `~/.shumway_history`; redirect or disable by pointing it elsewhere. |

### Heap-GC tuning

The heap garbage collector is self-tuning; these override it, mainly for
reproducing or bisecting a GC-interaction bug.

| Variable | Effect |
|---|---|
| `SHUMWAY_GC_THRESHOLD` | Heap size (in cells) that triggers a collection. Overrides the adaptive watermark. |
| `SHUMWAY_GC_STRESS` | `=1` collects at every safe point — slow, for flushing out GC-safety bugs. |
| `SHUMWAY_GC_AT`, `SHUMWAY_GC_UPTO` | Fuzz/bisect bounds: collect only within a range of GC opportunities, to bracket the one that corrupts state. |

### Debugger

| Variable | Effect |
|---|---|
| `SHUMWAY_DAP_PORT` | Default DAP port for a `--debug` executable (loopback `127.0.0.1:N`). Overridden by an explicit `--dap-port`. |
| `SHUMWAY_DEBUG_LCO` | `on` / `off` — force last-call optimization under the debugger (off makes tail frames visible on the stack at the cost of constant-stack tail recursion). |
| `SHUMWAY_EXE`, `SHUMWAY_ARGS` | Used by the Visual Studio debugger extension to locate the `shumway` executable and its launch arguments when not set in the options page or on `PATH`. |

### Advanced performance toggles

These gate optimizations that are **on and tuned by default**. Flip one only to
A/B-measure its effect or to work around a suspected codegen bug — the default
is what ships. Any build honors them.

| Variable | Default | Effect when changed |
|---|---|---|
| `SHUMWAY_REGION` | on | `=0` disables region compilation (each local predicate emitted inline in the caller's IL method). |
| `SHUMWAY_REGION_BUDGET`, `SHUMWAY_REGION_ROOT_MINSAVE` | tuned | Size/benefit thresholds bounding which predicates become regions. |
| `SHUMWAY_CPFREE_IDXBUCKET` | on | `=0` disables the lazy choice-point in indexed-bucket dispatch (ADR-031). |
| `SHUMWAY_CPFREE_GUARD`, `SHUMWAY_CPFREE_CONT` | see ADR-031/033 | Toggle CP-free guard-commit tiers and the guard-continuation-stack prototype. |
| `SHUMWAY_INLINE_FACTS`, `SHUMWAY_INLINE_RULES`, `SHUMWAY_INLINE_RULES2` | on | Toggle the local-predicate fact/rule inliners. |
| `SHUMWAY_IL_STATS`, `SHUMWAY_IL_SHAPE` | off | Emit Tier-1 promotion statistics / per-predicate IL shape summaries to stderr. |
| `SHUMWAY_CPFREE_STATS`, `SHUMWAY_CPFREE_DETAIL`, `SHUMWAY_CPFREE_IDXCENSUS` | off | Emit CP-free-commit census/statistics. |

### Developer diagnostics

Internal instrumentation for working on the engine — **not a stable interface**;
individual names come and go with the code they instrument. Most only do
anything in a build made with `-p:ShumwayDiag=true` (their read sites sit inside
`[Conditional("SHUMWAY_DIAG")]` methods); the rest print developer traces. Set
one to `1`, or to a functor name where noted, to enable it. Grep the source for
the exact contract before relying on any of them.

| Group | Variables | Purpose |
|---|---|---|
| Subsystem trace/diagnostics | `SHUMWAY_ATTR_VERIFY`, `SHUMWAY_CATCH_DIAG`, `SHUMWAY_CUTFIX_DIAG`, `SHUMWAY_DYNSEL_DIAG`, `SHUMWAY_JUMP_DIAG`, `SHUMWAY_PRUNE_DIAG`, `SHUMWAY_TE_DIAG`, `SHUMWAY_UNFOLD_DIAG`, `SHUMWAY_UNDEF_DIAG`, `SHUMWAY_STACK_DIAG`, `SHUMWAY_IL_DIAG` | Per-subsystem dumps. `SHUMWAY_UNDEF_DIAG`/`SHUMWAY_STACK_DIAG` take a functor name to narrow the trace. |
| IL / execution trace | `SHUMWAY_IL_DUMP`, `SHUMWAY_IL_DEBUG`, `SHUMWAY_NATIVE_TRACE`, `SHUMWAY_PC_RING`, `SHUMWAY_TRAP_PC`, `SHUMWAY_Y_SURVEY` | Dump emitted IL, ring-buffer the recent program counters, trap on a PC, survey Y-slot usage. |
| Persisted-IL dumps | `SHUMWAY_PERSIST_DUMP_FIDS`, `SHUMWAY_PERSIST_DUMP_ORDINALS`, `SHUMWAY_PERSIST_RANGE`, `SHUMWAY_PERSIST_SKIP_DUMP` | Inspect the persisted-IL patch tables of a bundle. |
| Debug-core diagnostics | `SHUMWAY_DEBUG_DIAG`, `SHUMWAY_DEBUG_TRACE`, `SHUMWAY_DEBUG_ACTIVATION`, `SHUMWAY_DEBUG_EVAL_QUIET` | Trace the debug service / DAP plumbing. |

The REPL / executable-bootstrap goal variables `SHUMWAY_GOAL`, `SHUMWAY_EXE`,
`SHUMWAY_ARGS` are consumed by generated executables and the launcher glue;
`SHUMWAY_GOAL` separates a warm-up run from the real goal for benchmarking.

## Build-time constants

Set as an MSBuild property (`dotnet build -p:ShumwayX=true`) or the equivalent
environment variable before the build. Each defines a compile constant that
turns on a family of `[Conditional]` diagnostic hooks; a normal build strips
every call site, so production pays nothing and exposes no diagnostic surface.
Declared in `Directory.Build.props`.

| Property | Constant | Enables |
|---|---|---|
| `ShumwayDiag` | `SHUMWAY_DIAG` | The whole developer-diagnostics family above (the `SHUMWAY_*_DIAG` / dump / survey env-vars become live). |
| `ShumwayProfile` | `SHUMWAY_PROFILE` | The `Profiler` hooks — opcode histogram, per-predicate/builtin counts and inclusive time, backtrack/unify/choice-point counters. |
| `ShumwayRetractTrace` | `SHUMWAY_RETRACT_TRACE` | Trace of dynamic-store retract/assert bookkeeping. |
| `ShumwayCpTrace` | `SHUMWAY_CP_TRACE` | Choice-point stack dumps (`ChoicePointTrace`). |

## Test-suite variables

The test projects read a few environment variables of their own, unrelated to
the engine runtime: `SHUMWAY_REGEN_DOCS` (rewrite `predicates.md` from source
instead of asserting it is current), and `SHUMWAY_SCRYER_LIB` / `SHUMWAY_SWI_LIB`
/ `SHUMWAY_NATIVE_CC` (point the dialect-interop and native-interop tests at a
Scryer library tree, an SWI library tree, and a C compiler respectively).
