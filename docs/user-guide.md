# Shumway — User Guide

Shumway is a Prolog compiler and interpreter for the .NET platform.
This guide is for developers who want to **use** Shumway: run Prolog
interactively, embed the engine in a .NET application, and ship
precompiled Prolog programs as deployable bundles.

For internal design see
[`architecture/overview.md`](architecture/overview.md) and the ADRs in
[`architecture/adr/`](architecture/adr/). For the predicate library
reference see [`predicates.md`](predicates.md).

---

## Contents

1. [What you get](#what-you-get)
2. [Building from source](#building-from-source)
3. [The interactive top-level (REPL)](#the-interactive-top-level-repl)
4. [Embedding Shumway in a .NET application](#embedding-shumway-in-a-net-application)
5. [Separate compilation workflow](#separate-compilation-workflow)
6. [Module directives reference](#module-directives-reference)
7. [Worked example: tiny rules engine](#worked-example-tiny-rules-engine)
8. [Native AOT publishing](#native-aot-publishing)

---

## What you get

Shumway ships as several .NET projects, each with a clear role:

| Project | Output | Purpose |
|---|---|---|
| `Shumway.Embedding` | `Shumway.Embedding.dll` | Main library. Reference from your .NET app to embed the engine. |
| `Shumway.Repl` | `shumway` executable | Interactive top-level (REPL). Consults files, prints solutions, exits on `halt.` |
| `Shumway.Compile` | `shumway-compile` executable | Compiles one `.pl` to a `.shmo` (per-module compiled object). |
| `Shumway.Link` | `shumway-link` executable | Links one or more `.shmo`s into a `.shum` bundle with reachability + missing-predicate analysis. |
| `Shumway.Bundler` | `shumway-bundler` executable | Single-shot bundler: `.pl` files → `.shum` directly. Older surface; the compile-then-link flow is recommended for non-trivial programs. |

You typically need only `Shumway.Embedding` plus one or more of the
CLI tools.

---

## Building from source

```bash
git clone <repo>
cd Shumway
dotnet restore
dotnet build
dotnet test
```

This produces all CLI executables under `src/<Project>/bin/Debug/net10.0/`.
For release artifacts:

```bash
dotnet publish src/Shumway.Repl/ -c Release         # shumway
dotnet publish src/Shumway.Compile/ -c Release      # shumway-compile
dotnet publish src/Shumway.Link/ -c Release         # shumway-link
```

---

## The interactive top-level (REPL)

The `shumway` executable is a thin client over `PrologEngine`. It
consults every file you pass on the command line, then reads queries
from stdin one at a time.

### Starting it

```bash
shumway                            # empty database
shumway util.pl rules.pl           # consult both files at startup
```

### Using it

```text
?- member(X, [a, b, c]).
X = a ;
X = b ;
X = c ;
false.
?- halt.
```

- A query is terminated by a `.` (period) on the line.
- After each solution, type `;` to ask for the next. Any other input
  commits to the current answer and prompts again.
- End the session with `halt.` (or `halt(N).` to exit with an explicit
  exit code), or with Ctrl-D / Ctrl-Z+Enter (end of input).
- `true` is printed for variable-less success, `false` for failure,
  otherwise `X = …, Y = …` for the binding set.

The REPL is also AOT-publishable — see
[Native AOT publishing](#native-aot-publishing).

---

## Embedding Shumway in a .NET application

Add a project reference to `Shumway.Embedding`. The public surface is
small.

### Spinning up an engine and running a query

```csharp
using Shumway.Embedding;

var engine = new PrologEngine();

engine.ConsultString("""
    :- module(util).
    :- public greet/1.
    greet(X) :- member(X, [hello, world]).
""");

// First solution.
Solution s = engine.Query("greet(X).");
if (s.Success)
    Console.WriteLine($"X = {s["X"]}");   // → hello

// Every solution, lazily.
foreach (var sol in engine.QueryAll("greet(X)."))
    Console.WriteLine(sol["X"]);          // → hello, world
```

The engine is **single-threaded**: only one thread may use a given
`PrologEngine` at a time. It is **thread-agile**, so you can move it
between threads as long as access is serialised (no
`[ThreadStatic]` state).

### Working with `Solution`

| Member | What it returns |
|---|---|
| `Success` | `true` if the query had at least one answer. |
| `Bindings` | `IReadOnlyDictionary<string, Term>` — every captured variable. |
| `this[name]` | `Term?` — convenient indexer for one binding. |
| `ToString()` | `"X = 1, Y = foo(2)"` style. |

A `Term` is the parser's AST (`AtomTerm`, `IntTerm`, `FloatTerm`,
`BigIntTerm`, `StringTerm`, `CompoundTerm`, `VarTerm`). Cast / pattern-
match for typed access:

```csharp
if (sol["X"] is IntTerm i) Console.WriteLine(i.Value);
if (sol["L"] is CompoundTerm cons && cons.Functor == "." && cons.Args.Length == 2)
    Console.WriteLine($"list head = {cons.Args[0]}");
```

### Loading constraint libraries

CLP(FD) and CLP(R) are opt-in:

```csharp
engine.UseClpfd();   // module 'clpfd'  — finite-domain constraints
engine.UseClpr();    // module 'clpr'   — linear-real constraints
```

(The two cannot share an engine — both define a public
`verify_attributes/4`.)

### Catching runtime exceptions

A Prolog `throw(Ball)` that the engine does not catch surfaces as a
`PrologRuntimeException`:

```csharp
try { engine.Query("X is 1 / 0."); }
catch (Shumway.Core.PrologRuntimeException ex)
{
    Console.WriteLine(ex.Ball);   // error(evaluation_error(zero_divisor), _)
}
```

### Loading a precompiled bundle

```csharp
engine.LoadBundle("app.shum");
var sol = engine.Query("main(Arg).");
```

`LoadBundle` consults every module in the bundle and (when present)
warms up Tier-1 IL / persisted assemblies so the first query already
runs on the optimised path.

---

## Separate compilation workflow

For deployable applications you usually want to:

1. Compile each Prolog source file once, separately.
2. Link the compiled objects into a single bundle, validating that
   every reachable call resolves.
3. Ship the bundle. The runtime engine loads it without reparsing.

This is the `.pl → .shmo → .shum` pipeline.

```
┌──────────┐  shumway-compile  ┌──────────┐                  ┌──────────┐
│  lib.pl  │ ────────────────► │ lib.shmo │ ─┐               │  app.    │
└──────────┘                   └──────────┘  │ shumway-link  │  shum    │
                                             ├──────────────►│          │
┌──────────┐  shumway-compile  ┌──────────┐  │ --entry main/0│          │
│  app.pl  │ ────────────────► │ app.shmo │ ─┘               └──────────┘
└──────────┘                   └──────────┘
```

### Step 1 — `shumway-compile` (per-module compilation)

```bash
shumway-compile [-o output.shmo] [-v] input.pl
```

- One `.pl` per invocation, one `.shmo` out.
- Output path defaults to the input with the extension replaced
  (`lib.pl` → `lib.shmo`).
- A `.shmo` carries the WAM bytecode plus the link-time metadata the
  linker needs: defined predicates with visibility, the per-predicate
  call graph, the `:- ensure_linked` set, and any module-qualified
  references.
- File header: magic bytes `SHMO` + a `uint32` version field
  (currently `1`). The linker refuses unsupported versions.

Exit codes: `0` ok, `1` compile error, `3` usage error.

### Step 2 — `shumway-link` (linker)

```bash
shumway-link -o app.shum \
  --entry main/0,init/1 \
  --entry shutdown/0 \
  [--allow-undefined] [-v] \
  lib.shmo util.shmo app.shmo
```

- One or more `.shmo`s as positional arguments.
- `--entry pred/N` declares the starting predicates for the
  reachability walk. Repeatable; each flag accepts a comma-separated
  list. At least one entry is required (otherwise no module would be
  reachable).
- `--allow-undefined` downgrades the *missing predicate* error to a
  warning and still produces the bundle. The engine then raises
  `existence_error/2` if the missing predicate is actually called.
  Useful when some predicates only become available at runtime
  (`assertz` of code built at startup).
- `--verbose` streams the diagnostic stream to stderr as the linker
  runs.

The linker performs three checks:

1. **Duplicate publics.** Two modules declaring the same `:- public
   foo/N` is a fatal error (`duplicate_public` code).
2. **Reachability walk.** Starting from the entry points and every
   module's `:- ensure_linked` indicators, the linker follows the call
   graph. Each edge resolves against, in order:
   - Module-local definitions (any visibility).
   - The flat global namespace (`:- public` ∪ `:- dynamic` across all
     loaded `.shmo`s).
   - The builtin registry.
   - The always-loaded prelude (`member/2`, `length/2`,
     `current_predicate/1`, etc.).
3. **Missing predicates.** Anything unresolved is emitted as
   `missing_predicate` (error, or warning under `--allow-undefined`).
4. **Dead-code elimination.** Modules no root reached are dropped from
   the bundle with an `unreachable_module` warning.

Exit codes: `0` ok, `1` link error, `3` usage error.

### Step 3 — Load the bundle

```csharp
var engine = new PrologEngine();
engine.LoadBundle("app.shum");
foreach (var s in engine.QueryAll("main(Result).")) {
    Console.WriteLine(s["Result"]);
}
```

`LoadBundle` consults every module in the bundle. Pre-compiled
bytecode embedded in the bundle warms the runtime so the first query
hits indexed dispatch immediately.

### In-process (no CLI)

`ShmoLinker` is also a plain .NET API — no need to shell out:

```csharp
using Shumway.Embedding;

var result = ShmoLinker.LinkFromSources(
    sources: new[]
    {
        ("lib", File.ReadAllText("lib.pl")),
        ("app", File.ReadAllText("app.pl")),
    },
    entryPoints: new[] { new PredicateRef("main", 0) });

if (!result.Success) {
    foreach (var d in result.Diagnostics)
        Console.Error.WriteLine($"{d.Severity}: {d.Message}");
    return 1;
}

var engine = new PrologEngine();
engine.LoadBundle(result.Bundle!);
```

Related entry points:

- `ShmoLinker.Link(LinkConfig)` — the core synchronous link.
- `ShmoLinker.LinkAsync(LinkConfig, CancellationToken)` — thread-pool
  wrapper.
- `ShmoLinker.LinkFromFiles(paths, entries, ...)` — reads `.shmo`s
  from disk for you.
- `ShmoCompiler.CompileSource(source, fallbackModuleName)` and
  `ShmoCompiler.CompileFile(path)` — build `.shmo`s by hand for
  custom workflows.

---

## Module directives reference

A Shumway source file is one module. The following directives carry
link-time meaning:

| Directive | Effect |
|---|---|
| `:- module(Name).` | Sets this file's module name (default: filename without extension). |
| `:- public Name/N.` | Exports the predicate to the global namespace. Required for any predicate called from another module. |
| `:- public [a/1, b/2].` | List form of the above. |
| `:- dynamic Name/N.` | The predicate is modifiable at runtime (`assertz`, `retract`). Also contributes to the global namespace, so other modules can call it. Even with zero clauses, the indicator counts as defined. |
| `:- ensure_linked Name/N.` | Tells the linker to treat this predicate as a **reachability root**. Use it when the predicate is called only via runtime meta-call (`call/1` with a constructed goal) — the static call graph won't see the edge, and without this hint the linker would drop the predicate as unreachable. |
| `:- ensure_linked [a/1, b/2].` | List form of the above. |

Other directives (`op/3`, `set_prolog_flag/2`, etc.) are honoured at
consult time but do not affect link-time decisions.

### Invariants the linker enforces

- **Public predicates are globally unique.** Two modules cannot both
  declare `:- public foo/N`.
- **A local predicate is only visible inside its own module.** Calls
  from another module won't resolve and the linker reports a
  `missing_predicate` (unless a different module also declares the
  predicate public/dynamic).
- **Dynamic predicates may share the indicator across modules.** All
  contribute to the same global dynamic store at runtime.

---

## Worked example: tiny rules engine

`grandparent.pl`:

```prolog
:- module(grandparent).
:- public grandparent/2.
:- public parent/2.
:- dynamic parent/2.

grandparent(X, Z) :- parent(X, Y), parent(Y, Z).
```

`facts.pl`:

```prolog
:- module(facts).
:- public load_facts/0.

load_facts :-
    assertz(parent(tom, bob)),
    assertz(parent(bob, alice)),
    assertz(parent(alice, dave)).
```

`app.pl`:

```prolog
:- module(app).
:- public main/0.

main :-
    load_facts,
    grandparent(X, Y),
    format("~w is a grandparent of ~w~n", [X, Y]),
    fail.
main.
```

Compile and link:

```bash
shumway-compile -o grandparent.shmo grandparent.pl
shumway-compile -o facts.shmo facts.pl
shumway-compile -o app.shmo app.pl

shumway-link -o demo.shum \
  --entry main/0 \
  grandparent.shmo facts.shmo app.shmo
```

Run it from a host program:

```csharp
var engine = new PrologEngine();
engine.LoadBundle("demo.shum");
engine.Query("main.");
// tom is a grandparent of alice
// bob is a grandparent of dave
```

Now try breaking it. Comment out `:- public parent/2.` in
`grandparent.pl` and re-run the pipeline:

```text
shumway-link: error: Predicate parent/2 (called by 'facts':load_facts/0) is not
                     defined in any linked .shmo, builtin, or prelude. (facts)
```

The linker catches the missing reference before deployment instead of
letting it surface as `existence_error/2` from a customer's machine.

---

## Native AOT publishing

The REPL (and any host that does not require runtime IL compilation)
can be published as a self-contained native executable:

```bash
dotnet publish src/Shumway.Repl/ -r win-x64 -c Release
```

Under AOT, Tier-1 IL promotion is cleanly skipped (the IL compiler is
never even constructed) and the engine runs on the bytecode
interpreter only. See [`native-aot.md`](native-aot.md) for the full
story, including the Windows toolchain requirements.
