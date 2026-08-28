# Shumway documentation

**Shumway** is a Prolog compiler and interpreter for the .NET platform. This is the
documentation index; start with the [user guide](guide/user-guide.md).

## Guides (`guide/`)

| Doc | What it covers |
|---|---|
| [user-guide.md](guide/user-guide.md) | The complete walkthrough: building, the REPL, embedding in .NET, modules, the compile/link/exe toolchain |
| [webshumway.md](guide/webshumway.md) | Prolog in the browser: the top level, workspaces, imported libraries, sharing, hosting |
| [interop.md](guide/interop.md) | C# ↔ Prolog interop: typed foreign predicates, typed queries, re-entrant `SolveOnce`, and the zero-copy cell-access hot path |
| [predicates.md](guide/predicates.md) | Reference of every builtin and library predicate (auto-generated — do not edit) |
| [debugger.md](guide/debugger.md) | Source-level debugging in Visual Studio |
| [debugger-vscode.md](guide/debugger-vscode.md) | Source-level debugging in VS Code |
| [embedded-native-c.md](guide/embedded-native-c.md) | `:- c` declarations and `{...}` embedded native C blocks |
| [generic-term-interop.md](guide/generic-term-interop.md) | Whole-term interop with C# and native C (the reftype tier) |
| [native-aot.md](guide/native-aot.md) | Publishing self-contained Native AOT executables |
| [net-framework-hosts.md](guide/net-framework-hosts.md) | Embedding in .NET Framework 4.8 apps (32-bit legacy hosts): bundles, app.config, memory limits |
| [configuration.md](guide/configuration.md) | Runtime `SHUMWAY_*` environment variables and build-time diagnostic constants |
| [logtalk.md](guide/logtalk.md) | Running Logtalk on Shumway |

### Compatibility status

| Doc | Ecosystem |
|---|---|
| [swi-library-support.md](guide/swi-library-support.md) | SWI-Prolog libraries under the `swi` dialect |
| [scryer-library-support.md](guide/scryer-library-support.md) | Scryer Prolog libraries under the `scryer` dialect |
| [trealla-library-support.md](guide/trealla-library-support.md) | Trealla Prolog libraries under the `trealla` dialect |
| [logtalk-library-support.md](guide/logtalk-library-support.md) | Logtalk's bundled library test suites |

## Architecture (`architecture/`)

- [overview.md](architecture/overview.md) — the high-level architecture.
- [invariants.md](architecture/invariants.md) — the consolidated catalog of
  non-negotiable invariants, by subsystem.
- [decision-policy.md](architecture/decision-policy.md) — what counts as a
  major decision, and where decisions are recorded.
- [adr/](architecture/adr/) — the Architecture Decision Records, one per
  major design decision, each with its status.

## Subsystem designs (`design/`)

Detailed designs referenced from the code:

- [cell-layout-detail.md](design/cell-layout-detail.md) — the 8-byte cell, tags, payloads.
- [wam-instruction-set.md](design/wam-instruction-set.md) — every opcode and its encoding.
- [bundle-format.md](design/bundle-format.md) — the `.shmo` / `.shum` file formats.
- [pstr-design.md](design/pstr-design.md) — partial strings for grammar processing.
- [il-emission-patterns.md](design/il-emission-patterns.md) · [il-region-compilation.md](design/il-region-compilation.md) · [il-local-inlining.md](design/il-local-inlining.md) — the Tier-1 IL backend.
- [atom-gc-coordination.md](design/atom-gc-coordination.md) — atom GC and safe points.
- [debug-info.md](design/debug-info.md) — positions, ports, the debugger's site tables.
- [foreign-predicates.md](design/foreign-predicates.md) — the `[PrologPredicate]` bridge.
- [api-reference.md](design/api-reference.md) — the embedding API reference.
- [inline-caching.md](design/inline-caching.md) — NOT BUILT; kept as a redirect to what shipped instead.

## Benchmarks (`benchmarks/`)

- [cross-engine-comparison.md](benchmarks/cross-engine-comparison.md) — Shumway vs GNU Prolog, Scryer, and SWI across Van Roy, clp(Z), and Logtalk.
- [analysis.md](benchmarks/analysis.md) — the curated Van Roy analysis & hotspots.
- [baseline.md](benchmarks/baseline.md) — the current auto-generated baseline.

## History (`history/`)

The project's work log: one closure summary per development phase
(`phase-N-closure.md`), the audit backlogs, and past instruction-count
comparisons. Point-in-time records — for current state, read the guides above.
