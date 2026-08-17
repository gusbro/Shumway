# Contributing to Shumway

Thanks for your interest in Shumway — a Prolog compiler and interpreter for
.NET. Contributions of all kinds are welcome: bug reports, failing test cases,
documentation fixes, new builtins, and engine work.

## License of contributions

Shumway is [MIT-licensed](LICENSE). By submitting a contribution you agree that
it is provided under the same MIT license (inbound = outbound). There is no
separate CLA or DCO sign-off to complete — keep it simple. Only contribute code
you have the right to license this way.

## Getting started

You need the **.NET 10 SDK** (the build targets .NET 10; the engine runs on
.NET 9+).

```
dotnet restore
dotnet build
dotnet test
```

The [user guide](docs/guide/user-guide.md) walks through the REPL, the
embedding API, and the `compile` → `link` → `exe` toolchain. The
[architecture overview](docs/architecture/overview.md) is the map of the engine.

## The test gate

A change is expected to keep the full gate green. It is six test projects:

```
tests/Shumway.Tests.Core/
tests/Shumway.Tests.Interpreter/
tests/Shumway.Tests.Compiler/
tests/Shumway.Tests.IsoConformance/
tests/Shumway.Tests.Embedding/
tests/Shumway.Tests.DialectInterop/
```

`DialectInterop` always runs in the gate; its deep library sweeps
additionally self-skip unless `SHUMWAY_SCRYER_LIB` / `SHUMWAY_SWI_LIB` point
at real library checkouts, so a machine without them still gets a green,
meaningful run. This section is the single source of truth for the gate —
if another document disagrees with it, this one wins.

`dotnet test` at the repository root runs them all. For engine-internal changes,
the `Embedding` project is the one that exercises Tier-1 IL and the higher-level
API, so run it even when your change looks Core-only.

When working on the engine, the `SHUMWAY_*` environment variables and the
build-time diagnostic constants (`-p:ShumwayDiag=true` and friends) are
catalogued in [`docs/guide/configuration.md`](docs/guide/configuration.md) —
the trace/dump/profile switches live there.

Guidelines for tests:

- **Every WAM instruction and every builtin** carries tests for its semantics;
  new ones should too.
- **Builtins with ISO semantics** get conformance tests under
  `tests/Shumway.Tests.IsoConformance/`.
- **Cut and backtracking** are easy to get subtly wrong — a change there wants
  dedicated coverage.

## Coding conventions

- **Zero warnings — enforced.** The build treats warnings as errors
  (`Directory.Build.props`). Fix a warning at its source; do not suppress it
  wholesale. A genuinely unavoidable case is silenced narrowly and locally
  (a targeted `#pragma warning disable <code>` with a comment), never by
  relaxing the invariant.
- **Standard .NET naming**: PascalCase types/methods, camelCase locals,
  `_camelCase` private fields.
- **Avoid LINQ and allocation in hot paths** (interpreter dispatch, unification,
  trail unwind). Use plain indexed loops; reach for `Span<T>` / `ref struct`
  for zero-allocation slices; prefer small immutable `struct`s for the value
  types (`Cell`, `FunctorId`, `AtomId`).
- **No `async` in the interpreter core** and **no `[ThreadStatic]` for engine
  state** — engines must stay thread-agile.

### Comments

A comment states something the code cannot show: an invariant, a constraint, a
non-obvious trick, or a trap ("don't simplify this to X — it breaks Y because
Z"). One to three lines. Not a narrative of what the code does, not history
(that lives in git), not measurement archaeology. Reference an ADR when the
rationale has one.

## Invariants and major decisions

Some things are load-bearing across the whole engine and are **not** changed as
a side effect of a feature. Before touching engine internals, read
[`docs/architecture/invariants.md`](docs/architecture/invariants.md) — the
consolidated catalog (cell layout, the two trails, atom-id stability, the
module model, dense bytecode, the logical update view, tier boundaries, GC).

A change that would break an invariant — or that is otherwise a **major
decision** (a new cell tag, a trail-format change, a new top-level opcode, an
atom-GC strategy change, a module-resolution change, a backtracking/choice-point
model change, a new external dependency, a threading-model change) — should
**stop and propose an [ADR](docs/architecture/adr/)** before implementation.
The process is described in
[`docs/architecture/decision-policy.md`](docs/architecture/decision-policy.md).

When you describe a change, it helps to say which kind it is:

- **fix** — corrects an existing implementation against its intended behaviour;
- **extension** — a new capability within the current design;
- **redesign** — changes an ADR (needs the ADR amended first).

## Dependencies

All dependencies must be permissively licensed (MIT, MS-PL, Apache-2.0, BSD).
No GPL/LGPL/AGPL. Adding a dependency is a major decision (see above).

## Commits and pull requests

- Write clear commit messages: what changed and why, in prose.
- Keep a PR focused on one logical change; include the tests that validate it.
- State plainly if something is left unfinished or a test is skipped — an honest
  "this part isn't covered yet" is better than a green wall that hides a gap.

## Reporting bugs

A failing Prolog program is the best bug report. The most useful form is a
minimal snippet plus what you expected and what Shumway did — ideally as a query
that reproduces it in the REPL, or a small xUnit test. If it is a conformance
divergence, a pointer to the relevant ISO clause or the behaviour of another
Prolog system (SWI, GNU, Scryer) helps a lot.
