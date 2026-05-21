# Phase 5 — Closure

**Status**: complete.

**Tagged**: `phase-5` (this commit).

Phase 5 is a deliberately small phase: it adds an interactive top-level
(a REPL) so Shumway can be driven by hand, and nothing more. Phases 1–4
built and hardened the engine; Phase 5 makes it directly usable. The
REPL is a thin client over the existing `PrologEngine` embedding API —
no engine capability was added for it. The one engine change in the
phase is a correctness fix that exercising the REPL surfaced. The
top-level is basic by design and is expected to be revisited. This
document records what landed and what was deliberately left out.

---

## Deliverables checklist

Tracking the Phase 5 list from [`CLAUDE.md`](../CLAUDE.md).

| Deliverable | Status | Implementing work |
|-------------|--------|-------------------|
| Interactive top-level (REPL) | ✓ | chunk 87 |

Done in the same window, prompted by exercising the REPL:

| Deliverable | Status | Implementing work |
|-------------|--------|-------------------|
| Undefined predicate raises a catchable `existence_error` | ✓ | engine fix `229b745` |

---

## By the numbers

- **2 substantive commits** since the Phase-4 tag — chunk 87 (the REPL)
  and the undefined-predicate fix — plus the Phase-4 closure paperwork
  that landed in the same window.
- **1689 passing tests, 0 failing, 0 skipped** across 5 projects
  (+6 over the Phase-4 tag's 1683):
  - `Shumway.Tests.Core` — 413
  - `Shumway.Tests.Interpreter` — 98
  - `Shumway.Tests.Compiler` — 222
  - `Shumway.Tests.IsoConformance` — 61
  - `Shumway.Tests.Embedding` — 895 (+6: `UndefinedPredicateTests`)
- **One new project**: `src/Shumway.Repl/` — the `shumway` console
  executable — added to `Shumway.slnx`. The REPL itself ships with no
  unit tests (see below).
- **No new opcodes, no new cell tags, no ADR changes.** Phase 5 stayed
  entirely inside the established invariants.

---

## What Phase 5 added

### The interactive top-level (chunk 87)

`src/Shumway.Repl/` is a console-app project that builds the `shumway`
executable — a basic Prolog top-level. It:

- consults any files named on the command line at startup;
- reads a query at the `?-` prompt, accumulating lines until one ends
  with the `.` clause terminator;
- prints each solution and, on `;`, searches for the next;
- exits on `halt.` (surfaced through `PrologEngine.LastHaltExitCode`)
  or end of input;
- keeps the session alive after a bad query — a parse failure or an
  uncaught error becomes a `%`-prefixed line rather than a crash.

It reads a single keypress interactively but a whole line when input is
redirected, so the top-level is scriptable (which is how it is
smoke-tested). It is a thin client: every behaviour is the
`PrologEngine` embedding API — `ConsultString`, `QueryAll`,
`LastHaltExitCode` — wired to a console loop. No engine code was added
or changed to support it.

The REPL carries no unit tests of its own. It has no logic worth
pinning beyond the embedding API it delegates to (which is thoroughly
tested), and a console read/write loop is awkward to unit-test
meaningfully — it was verified by scripted smoke tests instead. If the
top-level grows real logic later, that logic should come with tests.

### Undefined predicates raise a catchable `existence_error`

Exercising the REPL surfaced a long-standing engine inconsistency: a
call to a predicate with no clauses made the linker throw
`InvalidOperationException`, aborting the whole query — uncatchable, and
triggered even when the undefined predicate sat in unrelated code. The
in-engine meta-call path, by contrast, already raised a proper
`existence_error`.

The fix makes the direct path agree. The linker patches an unresolved
call with a `CallTarget` sentinel (a negative operand carrying the
callee's functor id) instead of failing the link; the interpreter's
`call` / `execute` dispatch and the IL tail-call resolver decode it and
raise `existence_error(procedure, Name/Arity)` when — and only when —
the call is reached. The error is now catchable by `catch/3`, and an
undefined predicate in one part of a program no longer breaks unrelated
queries.

---

## Architecture notes

- **The REPL is outside the engine.** `Shumway.Repl` references only
  `Shumway.Embedding`; it is another consumer of the embedding API, not
  a new engine layer.
- **The undefined-predicate change is a fix, not a redesign.** It adds
  no opcode and no ADR change: the `call` / `execute` target operand
  simply gained a negative-sentinel convention for "unresolved",
  documented on the new `CallTarget` type.
  `PrologRuntimeException.UndefinedProcedure` centralises the error term
  so every call-resolution site reports a missing predicate identically.

---

## Deferred — top-level improvements

The top-level is basic on purpose; for now its job is to let Shumway be
driven by hand. Known gaps, left for a later pass:

- **No in-session `consult`.** Files are consulted only from the
  command line; there is no prompt command to load more.
- **No line editing or history.** Input is plain `Console.ReadLine`.
- **Uncaught errors print as a C# exception.** An error that no
  `catch/3` handles is shown as `% PrologRuntimeException: …` — the
  implementation type leaks. It is at least uniform across every
  uncaught runtime error now, but a Prolog-shaped rendering would read
  better.
- **No top-level directives** beyond running a query — no `listing`,
  no flag-setting.

None of these block using the REPL; they are the obvious next
increments when the top-level is revisited.

---

## What Phase 5 buys you

Shumway can now be run interactively: `dotnet run --project
src/Shumway.Repl/ -- [file.pl ...]` opens a top-level that consults the
listed files and answers queries, with `;` to enumerate solutions and
`halt.` to leave. It is the quickest way to try the engine by hand and
to reproduce behaviour while developing.

And, independent of the REPL, a call to an undefined predicate is now a
catchable ISO `existence_error` wherever it occurs — the same however
the predicate is reached.

Phase 6 picks up from a green 1689-test suite and an unchanged ADR
ledger: the unsound `!`-inside-`call` fix first, then constraints,
Native AOT and tabling.
