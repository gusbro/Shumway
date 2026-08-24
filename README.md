# Shumway

**Shumway** is a cross-platform Prolog engine for .NET, built for Prolog systems 
large enough to need real engineering: separate compilation, a linker, a
module system, and a source-level debugger. It runs standard ISO Prolog and 
builds standalone executables. When you need it, it also interops with the
.NET ecosystem.

The engine aims to be fully ISO compliant whilst providing useful extensions
including attributed variables, coroutining, CLP(FD), CLP(R), tabling with
well-founded negation, rationals, and Arity Prolog/32 compatibility. It also
ships with multi-dialect shims allowing you to import libraries from other
Prolog engines (e.g. Scryer's CLP(Z), reif, dcgs, csv; SWI's rbtrees, heaps,
yall, record).

The engine provides a two-tier execution system. On tier-0, programs are
compiled to WAM bytecode and executed by the integrated WAM VM. On tier-1, WAM
procedures are compiled to .NET IL chunks, which are themselves promoted to
native code by the .NET JIT compiler. Shumway's own JIT promotes tier-0
code to tier-1 when it detects a hot path.
Shumway is single-threaded but supports multiple engines in one process.

Shumway supports a flexible module system for large applications: a standalone
compiler produces Prolog object modules, a librarian and linker take those
modules and produce bundles, .NET libraries and executables.

The linker applies Link Time Optimization (LTO) techniques to improve the
performance of the produced .NET libraries and executables.

Shumway provides source-level debuggers for Visual Studio and Visual Studio Code
allowing you to set (conditional) breakpoints, execute step-by-step, inspect the
call stack showing each frame's local variables and attributed-variable
residues. Debugging sessions run in a special tier-0 mode which turns off some
optimizations allowing you to fully inspect the program's state. It lets you
"rewind" the execution to any prior location in the call stack, undoing all
unifications performed in between.

The whole engine can be compiled to WebAssembly and run on any modern browser.
WebShumway provides a Prolog top level, an editor, and workspaces. It lets
you import libraries from a local folder or straight from a GitHub URL,
including libraries written for Scryer and SWI.

Shumway's web engine also supports debug mode with an embedded web debugger
featuring the same set of debugging capabilities provided in VS Code / VS.

Shumway runs wherever .NET 10 runs — Windows, Linux, macOS — plus any modern
browser via WebAssembly, and .NET Framework 4.8 hosts through an opt-in
multi-target build. The test gate currently runs on Windows.

## Try it online: [gusbro.github.io/Shumway](https://gusbro.github.io/Shumway/)

## Highlights

- **ISO Prolog** passing all four of [Neumerkel conformity suites](tests/conformity/README.md) (Aug 2026): syntax (365/365), number_chars/2 (67/67), variable_names/1 (63/63), and dif/2 (26/26).
- **Prolog extensions** including attributed variables, coroutining (`dif/2`,
  `freeze/2`, `when/2`), tabling with well-founded negation, CLP(FD) and CLP(R),
  rationals, module system, partial strings, and exceptions with full error terms.
- **Two garbage collectors**: a
  [sliding mark-compact heap GC](docs/architecture/adr/016-heap-garbage-collection.md)
  which runs
  automatically at safe points (with attributed variables and their
  trails live) and allows long deterministic runs to stay within a
  reasonable memory cap (e.g. a lazy DCG which parses a 4 MB file holds
  a live set of about 1,000 cells), and an [atom GC](docs/architecture/adr/003-atom-three-tier-system.md) which
  reclaims transient atoms and keeps atoms held by foreign C# interop safely retained.
- **Performance measurement**: ~2× faster than Scryer on typical CLP(Z) models, running Scryer's own clpz library; see
  [the benchmarks](docs/benchmarks/cross-engine-comparison.md).
- **Full Unicode** support including [astral planes](docs/architecture/adr/048-code-point-text-semantics.md).
- **A full toolchain**: `shumway-compile` (`.pl` → `.shmo`), `shumway-link`
  (`.shum` bundles, `--exe` native executables, `--dll` loadable class
  libraries), `shumway-lib` (archives), and the `shumway` REPL.
- **Link-time optimization**: Shumway's JIT promotes hot predicates knowing only what it can see. The linker knows the whole program, so bundles get cross-module unfolding, cross-module cut elision, and larger [deterministic regions](docs/design/il-region-compilation.md). So a linked bundle isn't just precompiled, it's compiled better.
- **Builtin predicates**: check the [current list](docs/guide/predicates.md).
- **Support for third-party libraries**: SWI-Prolog and Scryer Prolog libraries
  load under per-subtree dialects, [Logtalk](docs/guide/logtalk.md) (3.101.0) runs as a
  first-class backend.
- **Source-level debugging** in [Visual Studio and VS Code](docs/guide/debugger.md): breakpoints
  (conditional, logpoints), port-based stepping, the real Prolog call stack
  with per-frame variables and residual constraints, goal evaluation at a
  stop, Set Next Statement.
- **Runs in a browser**: the same engine on WebAssembly, as a static site with
  no backend ([WebShumway](docs/guide/webshumway.md)).
- **A .NET embedding API**: `PrologEngine`, typed term conversion
  (`ToTerm<T>`/`FromTerm<T>`, source-generated mappers), foreign predicates via
  `[PrologPredicate]`, async queries, engine pooling.
- **Interop**: typed C# foreign predicates, embedded native C blocks
  (`:- c` / `{...}` compiled to IL), and whole-term marshalling to native
  `t_reftype` graphs (P/Invoke) — [the interop guide](docs/guide/interop.md)
  routes every mechanism.

## Quick start

Nothing to install: [try it in the browser](https://gusbro.github.io/Shumway/).

There are no published packages yet — NuGet publication comes with the first
tagged release. Until then it is the browser build above, or building from
source:

```
dotnet build
dotnet run --project src/Shumway.Repl/ -- myprogram.pl
```

Embedding Shumway in a .NET application:

```csharp
using Shumway.Embedding;

var engine = new PrologEngine();
engine.ConsultString("parent(tom, bob). grandparent(X, Z) :- parent(X, Y), parent(Y, Z).");
foreach (var solution in engine.QueryAll("parent(tom, Who)."))
    Console.WriteLine(solution["Who"]);   // bob
```

To create an optimized native executable:

```
shumway-compile app.pl -o app.shmo
shumway-link app.shmo --goal main --exe app.exe
```

These tools live in `src/Shumway.Compile` and `src/Shumway.Link`: publish them to
put them on your PATH, or run them in place with
`dotnet run --project src/Shumway.Compile/ -- app.pl -o app.shmo`.

## Documentation

Everything lives under [`docs/`](docs/README.md):

- [User guide](docs/guide/user-guide.md): building, REPL, embedding, modules,
  the whole toolchain.
- [Predicate reference](docs/guide/predicates.md): every builtin, generated
  from the source of truth.
- [Debugger](docs/guide/debugger.md) (Visual Studio) ·
  [Debugger for VS Code](docs/guide/debugger-vscode.md).
- [Architecture overview](docs/architecture/overview.md) ·
  [ADRs](docs/architecture/adr/) ·
  [Invariants](docs/architecture/invariants.md).

## Tests

```
dotnet test
```

The gate is six projects; [CONTRIBUTING.md](CONTRIBUTING.md) is the source of
truth for what it contains. No phase closes with failing tests or compiler
warnings.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build and test workflow, coding
conventions, the invariants and ADR process, and how contributions are
licensed.

## License

[MIT](LICENSE). Third-party components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), currently the Sigil IL
library (MS-PL) and the Visual Studio SDK components the opt-in `vs/`
debugger build references.
