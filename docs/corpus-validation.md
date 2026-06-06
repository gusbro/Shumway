# Corpus validation (Phase 28)

Running real third-party Prolog programs through Shumway and diffing the result
against **GNU Prolog** (the oracle) to surface correctness / compatibility gaps —
the same empirical approach that drove Phases 25–27 (Blint), now widened to a
corpus.

**Oracle**: GProlog 1.5 via `gplc --no-top-level` native console exe (NEVER
`gprolog.exe` through a pipe — it pops the GUI). Driver appends
`:- initialization((catch((benchmark(R)->write(res(R));write(failed)), E,
write(err(E))), nl, halt)).`. See the `gprolog-windows-toolchain` memory for the
exact recipe.

**Corpus** (must run on GProlog, so the oracle is valid): GProlog's own
`examples/ExamplesPl` (16 pure Prolog) and `examples/ExamplesFD` (31 CLP(FD)),
plus vanilla programs from `c:\temp`. Arity demos are excluded (Arity-specific,
GProlog can't run them).

## ExamplesPl (16) — status

| Program | GProlog | Shumway | Notes |
|---------|---------|---------|-------|
| boyer | res(true) | ✅ | Boyer-Moore tautology prover |
| browse | res(_) | ✅ | |
| cal | res(true) | ✅ | |
| chat_parser | res(_) | ✅ | CHAT-80 NL parser fragment |
| crypt | res(true) | ✅ | |
| ham | res(true) | ✅ | Hamiltonian |
| meta_qsort | res(true) | ✅ | |
| nand | res(true) | ✅ | nand-circuit synthesis |
| nrev | res(true) | ✅ runs | needed `get_cpu_time/1` (added); output is timing (LIPS), not output-comparable |
| poly_10 | res(_) | ✅ | |
| queens | res(true) | ✅ | |
| queensn | res(true) | ✅ | |
| reducer | res(true) | ✅ **FIXED** | combinator graph reducer — see below |
| sendmore | res(_) | ✅ | |
| tak | res(true) | ✅ | |
| zebra | res(true) | ✅ | |

**Deep validation (computed output, not just `benchmark/0` success):** running
`benchmark(true)` (which forces each program to WRITE its computed answer) and
diffing stdout against the GProlog oracle — **all 15 deterministic programs are
byte-identical to GProlog** (modulo list whitespace, `[1, 2]` vs `[1,2]`).
`nrev` runs but only prints a LIPS/timing line, so it has no output to diff.

The two gaps found and closed:

### reducer — FIXED (append/3 improper-list split)

`reducer` (an applicative/combinator graph reducer) gave `false` where GProlog
gives `res(true)`. Narrowed by stage diff (`listify`/`curry` matched GProlog;
`t_reduce` diverged) to `t_redex`'s last clause, which peels a combinator's atom
tag with `append(_par, _func, [3|fac])` — i.e. **splitting an improper list**
(`[3|fac]`, tail = the atom `fac`).

Root cause: the `append/3` C# builtin's non-deterministic split path
(`AtomListBuiltins.AppendSplit`, var L1 / bound L3) rejected any L3 whose final
tail isn't `[]` with `return false`. But ISO `append/3` splits an improper list
fine — every suffix `L2` simply carries the improper tail
(`append([], fac, [3|fac])`-style). Fix: thread L3's actual tail through to the
L2 build instead of hardcoding `[]`; a proper list still has tail `[]`, so the
common case is unchanged. Regression tests in `AtomListBuiltinsTests`.

### nrev — `get_cpu_time/1` added

`nrev`'s `benchmark/1` calls `get_cpu_time/1` (a GNU-Prolog timing builtin). Added
as a C# builtin (`ControlBuiltins.GetCpuTime`, reports the .NET process'
`TotalProcessorTime` in ms). nrev now runs; its output is a LIPS rate (timing), so
nothing to deep-diff.

## Open gaps (cross-program)

- `include/1` directive — worked around in the harness (concatenating `common.pl`);
  GProlog has it. TODO if a corpus program needs it structurally.

## Status

**ExamplesPl: 16/16 run; 15/15 deterministic programs byte-match GProlog.**

## ExamplesFD (31 CLP(FD)) — in progress

GProlog's FD examples use GProlog's `fd_*` primitives, a different dialect from
Shumway's SWI/SICStus-style clpfd (`in`/`ins`, `#=`, `all_different`, `label`).
Added a **GProlog-FD compatibility shim** in `Clpfd.cs` mapping the core
primitives — `fd_domain`→`ins`, `fd_labeling`→`label`, `fd_labelingff`→
`labeling([ff],_)`, `fd_all_different`→`all_different`, `fd_atmost`/`fd_exactly`/
`fd_only_one`/`fd_at_most_one`→reified counts, `fd_set_vector_max`→no-op — which
covers **24/31** programs (the other 7 need `fd_element` (2), `fd_minimize`/
`fd_tell` (5)).

Also added a REPL **`--clpfd`** / `--clpr` flag: it calls `UseClpfd()` BEFORE
consulting, so the constraint operators are in the table when files are parsed.
(A `:- use_module(library(clpfd))` directive inside a file is too late — Shumway
parses the whole file before running directives. That, and the fact that
`use_module(library(clpfd))` doesn't persist the library / its operators to the
REPL parser, are real gaps the corpus surfaced — see below.)

### Validated so far (oracle-vs-Shumway diff of `q`'s solution, time stripped)

MATCH: **crypta, eq10, eq20, five, send** (SEND+MORE=MONEY etc.). The core solver
agrees with GProlog on these.

### Operator / expression gaps — ADDED (chunk 330)

- **`#<=>` / `#=>` / `#<=`** (single-arrow reification, GProlog spelling) →
  aliases of Shumway's `#<==>` / `#==>` / `#<==`. Verified.
- **`##`** (GProlog boolean exclusive-or) → reified as `#\=` (xor ≡ inequality on
  0/1). Verified.
- **`**`** (power) in FD expressions → expands to repeated `$fd_times`
  (`X**2` → `X*X`). Parses now — but see the multiplication bug below.

### KEY FINDING — `$fd_times` product propagator under-propagates (real bug)

`X*X #= 9, label([X])` gives **X = 1** (wrong; the only solution is 3). Same for
`X*Y #= 9, X #= Y` → 1,1. The product propagator does not enforce the product
when a factor is the same variable as the other (squaring) / when factors become
ground during labeling — so any multiplication-with-shared-var constraint can
yield a wrong answer. Pre-existing (the `**` work only surfaced it); the chunk-90
multiplication tests don't cover `X*X`, a test gap. **Highest-value FD fix.**

### Other FD findings (TODO)

- **`read_integer/1`** missing — bqueens, bramsey, interval, magic, square read
  the problem size from input. Needs the builtin (+ a way to feed input, or a
  default).
- **donald** — `NotSupportedException: TermReader.Materialize …` (a Shumway
  internal exception): real bug.
- **alpha**, **multipl** — empty output: investigate (may be the mult bug).
- Several harder programs time out the GProlog oracle at 25s.
- REPL `use_module(library(clpfd))` doesn't load the library / operators (had to
  add `--clpfd`); Shumway parses a whole file before running its directives, so
  in-file `:- op` / `:- use_module` don't affect the same file. Both real gaps.

**Current: 5/17 oracle-comparable programs byte-match** (crypta, eq10, eq20,
five, send). The multiplication bug + missing `read_integer` block most of the
rest.

## TODO

- Fix the `$fd_times` shared-var / ground-enforcement bug (highest value).
- `read_integer/1`; donald's materializer exception; alpha/multipl.
- The 7 programs needing `fd_element` / `fd_minimize` / `fd_tell`.
- Vanilla `c:\temp` programs (LinesOfAction.pl, …).
