# Phase 42 — the WebAssembly tier ships, and Tier-1 IL is rebuilt

**Closed 2026-10-08**, tagged `phase-42`. 242 commits on the branch
`wasm-module-jit`, 2026-09-09 to 2026-10-08, merged to main as PR #128;
1,034 files, +72,859/−14,588 since the branch left main. Version 0.9.3.

The phase had two questions. Can WebShumway run on a compiled tier by
default, with the libraries people import from other systems? And can Tier-1
IL be made cheaper to produce and closer to the WAM's own control model? The
answer to the first is yes: the page builds with the WebAssembly tier on.
The second produced an IL emitter of Shumway's own and a choice-point model
for regions; the continuation-method form of Tier-1 is implemented behind a
switch, and which form is the default is not decided.

The design records are ADR-050 (now shipped) and ADR-051 through ADR-062.

---

## The WebAssembly tier (ADR-050)

[ADR-050](../architecture/adr/050-wasm-tier1-backend.md) entered the phase
as a spike (phase 41, PR #117) that compiled one module per program and
rebuilt it whole for each promotion: promoting n predicates one at a time
cost n(n+1)/2 compiles.

**Modules that call each other inside wasm.** A spike measured the crossing
first: a call from one module into another through an imported function
table is a real tail call, 6.1 ns per hop. Each engine now keeps a registry
of modules, with a hop that keeps one frame, and a resume marker resolves by
subscript into a per-world table instead of a chain of constant compares.
The lazy mode compiles one module per predicate as it crosses the
threshold. The batch mode keeps one module for the whole program at each
consult, because it measured faster (clpfd's 827 predicates: 4.07 s against
5.19 s). A call site survives the tier changing under it, and a relink
tells the tier where promoted code went before the query runs.

**Baked ahead of time.** Modules are relocatable: process-local addresses
become sentinels plus a relocation table, resolved at install. A module can
therefore travel inside a `.shum`. `shumway-link --wasm` bakes a program's
module into its bundle and `shumway-lib create --wasm` does the same for a
librarian archive; clpfd, clpr and coroutining ship as bundles baked at
build time (`src/Shumway.Libraries`), each with its module. The mailbox ABI
travels with every baked module and is checked on read.

**Work done inside the module.** Every exit to the host costs a crossing:
in a clpr run measured in the browser on 2026-09-15, 72% of the wall clock
went into leaving the module and coming back, and 8% into running wasm. The
module now answers the type tests, float arithmetic,
arithmetic expressions as trees, `min/2`, `max/2`, `==/2`, `get_attr/3` and
attribute inserts (against a linear-memory image of the attribute store),
`call/1` and `call/N` with meta-called cuts, `append/3`, `=../2`,
`ground/1`, `sort/2` (over a standard-order comparator of its own),
`b_getval/2` and `nb_getval/2`. A cut compacts the trails in place. Every
remaining exit names its reason, enforced by the signature, and the reasons
are counted under `SHUMWAY_DIAG`, which a published page does not carry.

- [ADR-051](../architecture/adr/051-fd-domains-on-the-heap.md): CLP(FD)
  domains live on the heap, so the module reads, walks and narrows them.
- [ADR-054](../architecture/adr/054-dynamic-predicates-in-wasm.md): dynamic
  predicates run in the tier as snapshots, retired on mutation.
- A Prolog conformance corpus that the tier must answer exactly as Tier-0
  does, and a static census that refuses a member whose host crossings
  dominate its work.
- Continuation functions, the wasm form of ADR-061, behind the CPS switch.
- The tier stops backtracking at a nested driver's floor.

**`jit_compile/1`** is a predicate, so a program can choose: `all` (the
browser's default), a threshold, `off`, `cps`/`nocps`, and at the top level
`bundles_off`/`bundles_on` and `status`. `off` returns compiled predicates to
the interpreter at the next goal and holds the baked modules of bundles
loaded after it.

## WebShumway on the tier

- The engine is published AOT (a 5x loss had been a `finally` in the
  dispatch loop), and the page builds with the wasm tier by default; the
  build switch is `src/Shumway.Web/Directory.Build.rsp`, overridden with
  `-p:ShumwayWasmTier=false`.
- Scryer's clpz loads and runs in the browser: `library(loader)` became a
  module, and two runtime primitives its `lists.pl` needs were added.
- An imported library is compiled the first time a program needs it, and
  the bundle is kept. It compiles in its collection's dialect, keeps its
  expansion hooks live, compiles its qualified goals as its consult resolves
  them, and is skipped when the engine provides the library natively. A
  compiled library bakes its wasm module once.
- Measurements run in headless Chrome through a probe file and
  `WebShumwayHook.ps1`; `docs/benchmarks/wasm-tier-baseline.md` is the
  reference.

| workload | Tier-0 | tier | speedup |
|---|---:|---:|---:|
| Blint linting itself (timed by hand in the page) | about 90 s | about 6 s | about x15 |
| Triska's CLP(Z) examples, clpz from its compiled bundle | | | x1.3 to x3.0 |
| Triska's CLP(Z) examples, clpz compiled live (2026-09-28) | | | x0.99 to x3.2, geometric mean x1.7 |
| 12 microbenchmarks, one kind of module exit each (2026-09-28) | | | x0.31 to x13.7, geometric mean x4.6 |

A program that is mostly plain Prolog looks like the first row. Constraint
solving gains less because most of its time goes into exits. The two
microbenchmarks below x1, waking a frozen variable and `bb_b_put/2`, are
open items.

## Tier-1 IL

- [ADR-057](../architecture/adr/057-local-backtracking-in-il-regions.md) and
  [ADR-058](../architecture/adr/058-region-choice-points-are-wam-choice-points.md):
  a region resumes its own choice points, and they are WAM choice points;
  a region pushes and restores them inline.
- [ADR-060](../architecture/adr/060-region-registers-in-locals.md), accepted
  in part: a region holds its control registers in locals, with one push
  ladder and one restore per region; `get_list` and its two unify
  instructions run as one window.
- [ADR-061](../architecture/adr/061-tier1-as-continuation-methods.md),
  proposed: Tier-1 as continuation methods per predicate, behind
  `SHUMWAY_IL_CPS=1`, persisted bundles included. Measured on Blint linting
  itself: 0.47 s against 0.57 s for plain Tier-1 and 0.69 s for regions, at
  about four times the IL and twice the JIT time. The default form is not
  decided.
- [ADR-062](../architecture/adr/062-own-il-emitter.md): an IL emitter of
  Shumway's own replaces Sigil, and the dependency is gone. The generated IL
  is identical (600,339 disassembled lines on each side). Linking Blint
  takes 2.0 to 2.4 s against 7.1 to 9.2 s; promoting all of it in the REPL,
  14.5 to 16.9 s of CPU against 36.0 to 47.2 s.
- Compiled code wakes where Tier-0 does (completing ADR-049): in front of
  builtins, inside CP-free guards and at region boundaries, and a wake
  inside a CP-free construct hands the activation to the interpreter.
  Bundles carry continuation methods and wake points.
- A bundle's code is taken when earned and compiled off the engine's
  thread.
- Tier-1 allocates the heap cells Tier-0 does, and `time/1` counts its
  goals the same way.

## Memory, modules, redefinition

- [ADR-052](../architecture/adr/052-attribute-table-is-a-weak-root.md): the
  attribute table observes, it does not retain.
- [ADR-053](../architecture/adr/053-the-foreign-table-is-swept.md): the
  side tables (foreign, BigInteger, Rational) are swept, not truncated; per
  activation means the system's whole life.
- [ADR-055](../architecture/adr/055-clauses-for-another-module.md): clauses
  for another module (`M:Head :- Body`).
- [ADR-056](../architecture/adr/056-private-predicates-stay-private.md): a
  module's private predicates stay private.
- [ADR-059](../architecture/adr/059-what-a-program-may-redefine.md): what a
  program may redefine.

## Correctness

- `catch/3` matches the catcher as it was when the catch began, and a catch
  nothing can come back to leaves nothing behind.
- A cleanup that throws propagates its ball unless another is in flight; a
  compiled cut runs the cleanup of the scope it discards.
- Builtins given a cyclic list terminate and answer as their Prolog
  definitions do.
- `call/1` of an n-goal conjunction is linear, a `call/N` body of any depth
  walks on an explicit stack, and `when/2` watches only the variables that
  can decide its condition.
- `nth0/3`, `nth1/3` and `append/3` enumerate in linear time, and a
  backtrackable builtin's choice point is retried in place.
- Dynamic predicates: a retired chain head is bypassed (a Tier-0 regression
  of 2026-09-12, found by bisect with deterministic counters), and an
  indexed predicate unlinks its dead entries.
- `library(clpfd)`: an integer past the range is an error, never a wrong
  bound; labeling steps through a domain; an answer keeps the equalities of
  a constrained variable.
- CLP(R) inequalities veto a direct bind and decide themselves when ground.
- Code compiled for debugging is never promoted, on either tier, and a
  library imported from a bundle is not debuggable.
- `shumway-compile` and `shumway-link` refuse what file-at-a-time
  compilation would lose.

## Tooling and the gate

- `tests/gate.ps1` runs the tests by level: `step`, `engine` (adds the wasm
  suite and the net48 build) and `full` (adds the web build), chosen from
  the changed files when no level is given.
- The CI runs two things the local gate does not: the wasm suite with
  `-p:ShumwayDiag=true`, and Embedding executed on net48.
- The net48-x86 lane's intermittent test host crash was found in a crash
  dump: the `SHUMWAY_DEBUG_DIAG` exception log's handler re-entered itself
  when another test process held the log, until the stack overflowed.

---

## The gate at close

`tests/gate.ps1 -Level full`: Core 502, Interpreter 102, Compiler 421, ISO
conformance 518, Embedding 5,563, Wasm 767; the net48 and web builds with
zero warnings. The wasm suite in Release with `-p:ShumwayDiag=true`: 936.
Embedding on net48 in Release, every lane green. CI green on all three
lanes (net10, net48-x64, net48-x86).
