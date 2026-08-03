# Shumway documentation

**Shumway** is a Prolog compiler and interpreter for the .NET platform. This is the
documentation index; start with the [user guide](guide/user-guide.md).

## Guides (`guide/`)

| Doc | What it covers |
|---|---|
| [user-guide.md](guide/user-guide.md) | The complete walkthrough: building, the REPL, embedding in .NET, modules, the compile/link/exe toolchain |
| [predicates.md](guide/predicates.md) | Reference of every builtin and library predicate (auto-generated — do not edit) |
| [debugger.md](guide/debugger.md) | Source-level debugging in Visual Studio |
| [debugger-vscode.md](guide/debugger-vscode.md) | Source-level debugging in VS Code |
| [embedded-native-c.md](guide/embedded-native-c.md) | `:- c` declarations and `{...}` embedded native C blocks |
| [generic-term-interop.md](guide/generic-term-interop.md) | Whole-term interop with C# and native C (the reftype tier) |
| [native-aot.md](guide/native-aot.md) | Publishing self-contained Native AOT executables |
| [configuration.md](guide/configuration.md) | Runtime `SHUMWAY_*` environment variables and build-time diagnostic constants |
| [logtalk.md](guide/logtalk.md) | Running Logtalk on Shumway |

### Compatibility status

| Doc | Ecosystem |
|---|---|
| [swi-library-support.md](guide/swi-library-support.md) | SWI-Prolog libraries under the `swi` dialect |
| [scryer-library-support.md](guide/scryer-library-support.md) | Scryer Prolog libraries under the `scryer` dialect |
| [logtalk-library-support.md](guide/logtalk-library-support.md) | Logtalk's bundled library test suites |

## Architecture (`architecture/`)

- [overview.md](architecture/overview.md) — the high-level architecture.
- [invariants.md](architecture/invariants.md) — the consolidated catalog of
  non-negotiable invariants, by subsystem.
- [decision-policy.md](architecture/decision-policy.md) — what counts as a
  major decision, and where decisions are recorded.
- [adr/](architecture/adr/) — Architecture Decision Records 001–041, one per
  major design decision, each with its status.

## Subsystem designs (`design/`)

Detailed designs referenced from the code: cell layout, WAM instruction set,
bytecode/bundle formats, PSTR, IL emission/regions/inlining, atom GC
coordination, debug info, foreign predicates, the embedding API reference.

## Benchmarks (`benchmarks/`)

- [analysis.md](benchmarks/analysis.md) — the curated cross-engine analysis.
- [baseline.md](benchmarks/baseline.md) — the current auto-generated baseline.

## History (`history/`)

The project's work log: one closure summary per development phase
(`phase-N-closure.md`), the audit backlogs, and past instruction-count
comparisons. Point-in-time records — for current state, read the guides above.
