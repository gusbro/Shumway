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

## TODO

- ExamplesFD (31 CLP(FD) programs).
- Vanilla `c:\temp` programs (LinesOfAction.pl, …).
