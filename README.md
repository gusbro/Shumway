# Shumway

**Shumway** is a Prolog compiler and interpreter for the .NET platform, built for
embedding Prolog in .NET applications: grammar processing (DCGs), rules engines,
and symbolic reasoning inside larger systems — with performance comparable to or
better than GNU Prolog, and far ahead of it on interop-heavy workloads.

## Highlights

- **ISO Prolog** with a real conformance record (driven by the standard test
  suites), plus the extensions real programs use: attributed variables,
  coroutining (`dif/2`, `freeze/2`, `when/2`), tabling with well-founded
  negation, CLP(FD) and CLP(R), rationals, exceptions with full error terms.
- **Two execution tiers**: a WAM bytecode interpreter (Tier 0, always available,
  AOT-friendly) and an IL compiler (Tier 1) that promotes hot predicates to
  .NET-JIT-compiled code — automatically, at runtime, or persisted into bundles.
- **A .NET embedding API**: `PrologEngine`, typed term conversion
  (`ToTerm<T>`/`FromTerm<T>`, source-generated mappers), foreign predicates via
  `[PrologPredicate]`, async queries, engine pooling.
- **A full toolchain**: `shumway-compile` (`.pl` → `.shmo`), `shumway-link`
  (`.shum` bundles, `--exe` native executables, `--dll` loadable class
  libraries), `shumway-lib` (archives), and the `shumway` REPL.
- **Interop three ways**: typed C# foreign predicates, embedded native C blocks
  (`:- c` / `{...}` compiled to IL), and whole-term marshalling to native
  `t_reftype` graphs (P/Invoke).
- **Runs other systems' code**: SWI-Prolog and Scryer Prolog libraries load
  under per-subtree dialects (Scryer's clpz runs at parity, certified against
  Scryer itself); Logtalk runs as a first-class backend — 99.98 % of its
  bundled library test suite passes.
- **Source-level debugging** in Visual Studio and VS Code: breakpoints
  (conditional, logpoints), port-based stepping, the real Prolog call stack
  with per-frame variables and residual constraints, goal evaluation at a
  stop, Set Next Statement — including into interop C#.

## Quick start

Build and run the REPL:

```
dotnet build
dotnet run --project src/Shumway.Repl/ -- myprogram.pl
```

Embed in a .NET application:

```csharp
using Shumway.Embedding;

var engine = new PrologEngine();
engine.ConsultString("parent(tom, bob). grandparent(X, Z) :- parent(X, Y), parent(Y, Z).");
foreach (var solution in engine.QueryAll("parent(tom, Who)."))
    Console.WriteLine(solution["Who"]);   // bob
```

Ship a native executable:

```
shumway-compile app.pl -o app.shmo
shumway-link app.shmo --goal main --exe app.exe
```

## Documentation

Everything lives under [`docs/`](docs/README.md):

- [User guide](docs/guide/user-guide.md) — building, REPL, embedding, modules,
  the whole toolchain.
- [Predicate reference](docs/guide/predicates.md) — every builtin, generated
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

The gate is five projects (Core, Interpreter, Compiler, IsoConformance,
Embedding) — several thousand tests, all green on every commit.
