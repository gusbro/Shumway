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
9. [Embedded native C (`:- c` / `{…}`)](embedded-native-c.md) — calling .NET methods from Arity-style native blocks

---

## What you get

Shumway ships as several .NET projects, each with a clear role:

| Project | Output | Purpose |
|---|---|---|
| `Shumway.Embedding` | `Shumway.Embedding.dll` | Main library. Reference from your .NET app to embed the engine. |
| `Shumway.Repl` | `shumway` executable | Interactive top-level (REPL). Consults files, prints solutions, exits on `halt.` |
| `Shumway.Compile` | `shumway-compile` executable | Compiles one `.pl` to a `.shmo` (per-module compiled object). |
| `Shumway.Link` | `shumway-link` executable | Links one or more `.shmo`s into a `.shum` bundle with reachability + missing-predicate analysis. Also produces standalone executables (`--exe`). |
| `Shumway.Lib` | `shumway-lib` executable | Librarian: packages `.shmo` objects into a `.shum` **library archive** (the `ar` model — every added object kept, no reachability pruning; `create`/`add`/`delete`/`list`/`extract`). The linker pulls members from such a library on demand, like a C linker pulling from a `.a`. |
| `Shumway.Dap` | `shumway-dap` executable | Debug adapter for VS Code (ADR-036): the small executable the VS Code extension launches, forwarding the Debug Adapter Protocol to a running Shumway's `--dap` endpoint. Not run by hand. |
| `Shumway.Disasm` | `shumway-disasm` executable | Diagnostic: prints the WAM bytecode disassembly of each predicate (post-indexing dispatch + clause bodies). For inspecting code generation. |

You typically need only `Shumway.Embedding` plus one or more of the
CLI tools.

### Inspecting compiled bytecode (`shumway-disasm`)

`shumway-disasm` compiles the static predicates in a source file (with
first-/multi-argument indexing) and prints the WAM bytecode the Tier-0
interpreter runs — the `switch_on_term` / `try` / `retry` / `trust`
dispatch plus each clause body. It is a diagnostic aid for understanding
or optimising code generation, not part of the build pipeline.

```bash
shumway-disasm benchmarks/vanroy/nreverse.pl      # every predicate
shumway-disasm -p conc/3 benchmarks/vanroy/nreverse.pl   # one predicate
shumway-disasm -e "p(X) :- X > 0."                # inline source
```

`-p Name/Arity` restricts the output (repeatable / comma-separated);
`-e <source>` disassembles inline source instead of a file. By default
it shows **release** bytecode (what the engine runs under
`compile_mode=release` — no `meta dbg_info` markers); pass `--debug` to
include the per-clause source-position markers. DCG rules are expanded;
directives are skipped. The same functionality is available in-process
via `Shumway.Compiler.Wam.PredicateDisassembler`.

For the **Tier-1 IL** counterpart — what the IL compiler emits for a
module, including region methods — use
[`shumway-compile --dump-il`](#dumping-generated-wam-and-il-for-analysis)
(and `--dump-wam` for a whole-module, dump-to-file WAM disassembly).

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

Run `shumway --help` for the full flag list. Highlights: `--clpfd` / `--clpr`
(enable a constraint library before parsing, so its operators are known),
`--foreign-dll` / `--native-dll` (interop, same names as `shumway-link`),
`-g goal` / `--goal goal` (run a goal after consulting, then stay at the
prompt), `--debug` / `--debug-wait` (Visual Studio debugging), and
`--dap <port>` / `--dap-wait <port>` (VS Code debugging — see
[`debugger-vscode.md`](debugger-vscode.md)).

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
- Press `Esc` to abort a long-running query. Cancellation is cooperative
  — the engine stops at its next safe point (not instantaneous, but
  responsive) and prints `% Execution aborted.`. This covers the usual
  runaways, including failure-driven builtin loops like
  `between(0, BIG, X), fail` and `repeat, fail`.
- A query wider than the terminal wraps across rows; the cursor tracks
  the edit position. `↑`/`↓` walk history, `Tab` completes predicate
  names, and the usual Home/End/Ctrl-A/E/U/K editing keys work.
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

### Loading constraint and coroutining libraries

The attributed-variable libraries are opt-in:

```csharp
engine.UseClpfd();        // module 'clpfd'        — finite-domain constraints
engine.UseClpr();         // module 'clpr'         — linear-real constraints
engine.UseCoroutining();  // module 'coroutining'  — freeze/2, dif/2
```

All three can be enabled on one engine (their `verify_attributes/4`
hooks are `:- multifile`, dispatched by the attribute module); keep each
variable's constraints within one library — mixed clpfd+clpr constraints
on the same variable are not supported. From Prolog source, the same
libraries load with `:- use_module(library(clpfd))` /
`library(clpr)` / `library(coroutining))`.

The coroutining library provides `freeze/2` (delay a goal until a
variable is bound), `frozen/2`, `when/2` (delay on a general condition —
`nonvar/1`, `ground/1`, `?=/2`, and their `(,)`/`(;)` combinations), and
`dif/2` (a sound disequality that fails the moment its arguments become
identical). `?=/2` (decided (in)equality), `unifiable/3` (the unifier of
two terms as a `V=Value` list), `term_attvars/2` and `call_residue_vars/2`
are always available and need no library.

### Loading third-party Prolog libraries

Beyond the baked-in libraries above, you can drop your own (or third-party)
Prolog `.pl` sources in a directory and load them with
`use_module(library(X))` — the SICStus/Scryer/SWI convention. Shumway resolves
`library(X)` to `X.pl` (or a compiled `X.shum`) on a **library search path**,
fed by all of:

- the shipped `lib/` directory next to the executable (the REPL and CLIs add it
  automatically — that is where `library(lists_ext)` lives);
- the `SHUMWAY_LIBRARY_PATH` environment variable (`;`/`:`-separated dirs);
- `file_search_path(library, Dir).` facts (SWI/Scryer) and
  `library_directory(Dir).` facts (SICStus) — both are `:- dynamic`, so a
  program can add to the path at load time;
- the embedding API: `engine.AddLibraryDirectory("/path/to/libs")` (or
  `engine.AddDefaultLibraryDirectories()` for the shipped `lib/`).

```prolog
:- use_module(library(lists_ext)).      % imports its whole export surface
:- use_module(library(lists_ext), [take/3, drop/3]).  % only these
```

At the REPL you load a library the same way, as a goal:

```
?- use_module(library(lists_ext)).
?- take(2, [a,b,c,d], L).
L = [a, b].
```

**Export-qualified modules.** A library that isolates its predicates uses the
two-argument module directive:

```prolog
:- module(greetings, [hello/1, bye/1]).   % ONLY hello/1 and bye/1 are importable
hello(world).
bye(gone).
detail(internal).                          % private: invisible to importers
```

Unlike `:- module(Name).` + `:- public`, which put predicates in one flat global
namespace, `:- module(Name, [Exports])` makes **every** predicate module-private
(internally renamed `Name$pred`); only the listed exports can be imported, and a
module resolves a call as *its own predicate → its imports → the global
prelude/builtins*. Two export-qualified modules can therefore export the same
name without colliding, and a library freely calls prelude predicates
(`member/2`, `append/3`, …) without importing them. Importing a name a module
does not export is an error.

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
shumway-compile [options] input1.pl [input2.pl ...]
```

- Accepts one *or more* source files in a single invocation.
- For each input, prints `compiling X -> Y` to stderr and emits a
  `.shmo` artifact.
- Output path: with one input, `-o file.shmo` names the file (default
  is `input.pl → input.shmo`). With multiple inputs, `-o <dir>` is
  treated as an output directory; without `-o`, each output lands
  next to its source.
- A `.shmo` carries the WAM bytecode plus the link-time metadata the
  linker needs: defined predicates with visibility, the per-predicate
  call graph, the `:- ensure_linked` set, and any module-qualified
  references.
- File header: magic bytes `SHMO` + a `uint32` version field
  (currently `2`). V1 artifacts are still readable; the linker rejects
  unsupported future versions.

Flags:

| Flag | Effect |
|---|---|
| `-o, --output <path>` | Single-input output file, or multi-input output directory. |
| `-r, --release` | Release build (default). Smaller `.shmo`, no per-instruction debug info. |
| `-d, --debug` | Debug build. The build mode is recorded in the `.shmo` and surfaces in `shumway-link --map` output. |
| `-v, --verbose` | After each file, list every `:- public` and `:- dynamic` indicator the module exports. |
| `--dump-wam <file>` | Append a readable disassembly of each predicate's WAM bytecode to `<file>` (analysis aid; see below). |
| `--dump-il <file>` | Append the Tier-1 IL the compiler generates for each predicate to `<file>` (analysis aid; see below). |
| `--regions` | With `--dump-il`, enable **region compilation** so the IL dump shows region methods (flat local code space) instead of one method per predicate. |
| `-h, --help` | Usage summary. |

**Error handling**: the compiler is C-style — on a parse or directive
error, it resyncs to the next clause-terminator dot and keeps going,
so you see every error in one pass (up to a 100-error cap). All
diagnostics are printed in the standard `file:line:col: error: msg`
shape. Exit codes: `0` ok, `1` compile error, `3` usage error.

#### Dumping generated WAM and IL for analysis

Two analysis flags write the compiler's intermediate code to a text file
so you can read exactly what it generates for a module. They are
diagnostic aids — **they do not change the emitted `.shmo`** (always WAM)
— and both **append**, so delete the target file between runs.

```bash
# Dump both the WAM and the region-form IL for a module.
shumway-compile --dump-wam prog.wam.txt --dump-il prog.il.txt --regions \
  -o prog.shmo prog.pl
```

- **`--dump-wam <file>`** decodes the just-built module and appends a
  readable disassembly of every predicate's WAM bytecode — the same
  `switch_on_term` / `try` / `retry` / `trust` dispatch and clause bodies
  the Tier-0 interpreter runs. (For ad-hoc, stdout-only WAM inspection of
  a single predicate, [`shumway-disasm`](#inspecting-compiled-bytecode-shumway-disasm)
  is often handier; `--dump-wam` is the whole-module, dump-to-file form
  that pairs with `--dump-il`.)

- **`--dump-il <file>`** runs the Tier-1 IL compiler over each predicate
  — with the whole module as the callee map, so it can see the local
  closure — and appends each generated method's IL. Without `--regions`
  you get one method per predicate; with `--regions` you get the
  **region** methods, where a predicate and its transitively-reachable
  local callees are compiled into one IL method (each member a labelled
  block, intra-region calls a `br`). This is how you inspect what
  Tier-1 actually emits, including the region dispatch `switch`, the
  per-member blocks, and the inline first-argument index decision.

Each method is preceded by a header — `;;; user$pick/2 clauses=3 …` for
WAM, `;;; ===== region root=… members=[…] =====` or `;;; ===== compile
fid=… =====` for IL. Note the dump compiles **every** predicate as a
region root, so it is a superset of what runs (at runtime only
invocation-promoted predicates become roots; many are only reached as
members of another region).

> The same hooks are reachable in-process: set
> `Shumway.Compiler.Il.IlPredicateCompiler.IlDumpPath` (and
> `.RegionCompile`) before compiling, or use the `SHUMWAY_IL_DUMP` /
> `SHUMWAY_REGION` environment variables when running the REPL.

### Step 2 — `shumway-link` (linker)

```bash
shumway-link -o app.shum \
  --entry main/0,init/1 \
  --entry shutdown/0 \
  [--allow-undefined] [--strip] [--map app.map] [-v] \
  lib.shmo util.shmo app.shmo
```

- One or more `.shmo`s as positional arguments.
- **Reachability root** is either `--entry pred/N` (repeatable;
  comma-separated within a flag) or `--goal Term` (see
  [Producing a runnable executable](#step-3a--producing-a-runnable-executable)).
  At least one is required.

| Flag | Effect |
|---|---|
| `-o, --output <path>` | Output `.shum` path (required). |
| `--entry pred/N[,…]` | Entry-point predicates. Repeatable; comma-separated within a flag. |
| `--goal Term` | Adds the goal's head as an implicit entry point. Required when `--exe` is set. |
| `--allow-undefined` | Downgrade missing-predicate errors to warnings; still produce the bundle. The engine raises `existence_error/2` at call time if the missing predicate is actually invoked. |
| `-L, --library-dir <dir>` | Directory searched to resolve a `use_module(library(X))` dependency not passed explicitly: `X.pl`/`X.shmo` is compiled and linked in (transitively), C-linker style — already-provided inputs win, source is the last resort. Repeatable; also reads `SHUMWAY_LIBRARY_PATH`. |
| `-s, --strip` | Remove the embedded Prolog source from every bundle entry. Bytecode preserved. Useful for size analysis / IP-protection. (Stripped bundles dispatch correctly via the source-less load path.) Note: a `.shmo` always carries the module's clause terms — it is an *intermediate* build artifact, like an object file with embedded IR, and the linker uses them for cross-module optimization (e.g. the meta-wrapper unfold). IP stripping is about what ships: the `.shum` / executable, which never carry clause terms. |
| `-m, --map <path>` | Write a C-toolchain-style audit file describing what landed in the bundle: per-module sizes, exported / dynamic predicate lists, dropped modules, totals. |
| `-i, --with-compiled-il` | Persist a Tier-1 IL assembly inside the bundle so it runs as compiled IL (no load-time JIT of the WAM). By default the IL uses the **region** layout with the dead-region prune applied: a predicate and its local closure share one IL method, and each absorbed-only predicate drops its standalone IL. |
| `--no-region-prune` | With `--with-compiled-il`: emit one standalone IL method per predicate instead of the default pruned region layout. Mainly for inspecting the generated code; bundles are larger and typically slower. |
| `--strip-wam` | Implies `--with-compiled-il`. Drop the redundant WAM bodies of the predicates the bundle runs as IL — standalone-IL predicates (each has its own IL delegate) and, under `--region-prune`, the region-absorbed members too (each is reachable by functor id through its region method's member-entry cursor). The bundle then ships IL, not WAM. JIT-only (the IL must load — not for Native AOT). |
| `--prune-report` | Stage-9 dead-region dry-run: report how many standalone forms would be prunable. Info diagnostic; no change to the bundle. |
| `--dump-wam <path>` | Append a disassembly of the WAM the bundle **ships** (each entry's final bytecode, after `--strip-wam` / region prune) to `<path>`. See [below](#dumping-the-shipped-il--wam-from-the-linker). |
| `--dump-il <path>` | Append the Tier-1 IL the bundle **ships** to `<path>` (implies `--with-compiled-il`). See [below](#dumping-the-shipped-il--wam-from-the-linker). |
| `-e, --exe <path>` | Emit a single-file native executable. See [step 3a](#step-3a--producing-a-runnable-executable). |
| `-g, --goal Term` | The goal the `--exe` runs at startup. Trailing `.` optional. |
| `--self-contained` | Used with `--exe`: bake the .NET runtime into the binary (~70 MB exe, runs on machines without .NET). Default is framework-dependent (~5-10 MB exe, requires .NET 10 runtime on the target). |
| `--debug` | Used with `--exe`: build the executable debuggable — its modules compile debuggable and it materialises their embedded source at startup, so a debugger attached to the process sets breakpoints and steps (see [`docs/debugger.md`](debugger.md)). Requires the bundle to carry source (compile inputs with `shumway-compile --debug`; not with `--strip`). |
| `--debug-wait` | Like `--debug`, but the executable also blocks at startup until a debugger has attached and armed its breakpoints, so the first goal can be stopped in. Implies `--debug`. |
| `--dap-port <port>` | With `--exe --debug`: bake a VS Code (DAP) debug endpoint into the executable — it listens on `127.0.0.1:<port>` whenever it runs. At run time `SHUMWAY_DAP_PORT` overrides the baked port (`0` disables). Implies `--debug`. See [`debugger-vscode.md`](debugger-vscode.md). |
| `-d, --dll <path>` | Emit a loadable .NET class library embedding the bundle, with a factory that hands back a ready engine. See [step 3b](#step-3b--producing-a-loadable-net-class-library---dll). Mutually exclusive with `--exe`. |
| `-n, --native-dll <path>` | A native C library (DLL/.so/.dylib) backing `:- native` functions (resolved by P/Invoke). The bundle records its name so the engine auto-loads it at runtime; `--exe` copies each next to the executable. Repeatable. |
| `--dll-namespace <ns>` | Namespace of the `--dll` factory class. Default: inferred from the DLL filename. |
| `--dll-class <name>` | Class name of the `--dll` factory. Default `Bundle`. |
| `-v, --verbose` | Stream diagnostics to stderr as the linker runs. |

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

#### Dumping the shipped IL / WAM from the linker

`shumway-compile --dump-il` / `--dump-wam` (above) dump a single module as a
**superset** — every predicate compiled as a region root. The linker's
equivalents dump the **ground truth of what the bundle actually ships**, after
reachability + the Stage-9 region prune:

```bash
shumway-link -o app.shum --entry main/0 \
  --region-prune --strip-wam \
  --dump-il app.il.txt --dump-wam app.wam.txt \
  lib.shmo app.shmo
```

- **`--dump-il <file>`** appends the persisted Tier-1 IL the bundle ships —
  post-prune, region mode + forced roots under `--region-prune`. Each method
  is headed `;;; ===== persist region root fid=… members=N [member list] =====`
  (a region) or `;;; ===== persist fid=… clauses=N =====` (standalone). Implies
  `--with-compiled-il` (there is no IL to dump otherwise). This is the IL that
  runs cross-process and as `--exe`.
- **`--dump-wam <file>`** appends the WAM of each bundle entry's **final**
  bytecode, *after* any strip. With `--strip-wam` the dump shrinks to only the
  predicates that kept a Tier-0 body (with `--region-prune --strip-wam` it can
  be empty — the whole user module runs on IL; that emptiness is the
  confirmation the strip was complete).

Both **append** — delete the files between runs.

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

### Step 3a — Producing a runnable executable

For deployment as a standalone binary (no host application needed),
use `--exe`:

```bash
shumway-link -o app.shum \
  --exe ./myapp \
  --goal "main" \
  lib.shmo app.shmo
```

This produces `./myapp` (or `./myapp.exe` on Windows) — a single-file
native executable for the current platform. On launch it loads the
embedded bundle, runs the goal, and exits with:

- `0` — goal succeeded
- `1` — goal failed
- `2` — uncaught Prolog exception or unexpected host error

The `--goal` accepts both `main` and `main.` (the trailing
clause-terminator dot is optional). The argument is parsed and
validated syntactically at link time, so typos surface immediately.
The goal's head predicate is also added as an implicit reachability
root, so `--goal` alone is enough — `--entry` is optional in the
`--exe` flow.

**Deployment modes:**

- **Default (framework-dependent)**: ~5-10 MB single file. Requires
  .NET 10 runtime to be installed on the target machine.
- **`--self-contained`**: ~70 MB single file. The .NET runtime is
  baked in; the binary runs on machines with nothing installed.

The build host must have the .NET 10 SDK (which it does, since
`shumway-link` is itself a .NET tool). Cross-targeting other
platforms isn't supported yet — the produced binary matches the
current platform.

### Step 3b — Producing a loadable .NET class library (`--dll`)

`--exe` is for "the whole program *is* a Prolog goal". When instead you
have a **.NET application** that wants to call into Shumway for certain
goals, use `--dll`. It emits a .NET class library that embeds the bundle
and exposes a small factory you call to get a ready-to-query engine:

```bash
shumway-link greet.shmo \
  --dll ./Greeter.dll \
  --entry greet/1
```

`--dll` needs a reachability root just like a normal link — pass
`--entry` (or `--goal`). Unlike `--exe` there is no startup goal: the
DLL never runs anything on its own; it just makes the bundle available.

This produces `Greeter.dll` plus the Shumway runtime DLLs it depends on
(all written next to the output). Your application references `Greeter.dll`
and calls the generated factory:

```csharp
// The factory type is <Namespace>.<Class> — default Greeter.Bundle here
// (namespace inferred from the DLL filename, class defaults to "Bundle").
var engine = Greeter.Bundle.CreateEngine();

foreach (var sol in engine.QueryAll("greet(X)."))
    Console.WriteLine(sol["X"]);     // hello, world
```

`CreateEngine()` returns a fresh `Shumway.Embedding.PrologEngine` with the
bundle already loaded (it calls `PrologEngine.FromBundle` internally, so the
baked prelude warms the runtime). Call it once per engine you want — engines
are single-threaded, so use one per thread (or an `EnginePool`). The bundle
itself is parsed once and cached; `GetBundle()` exposes that shared
`Shumway.Embedding.Bundle` if you want to load it into engines yourself.

**Debugging the bundled program.** Pass `debug: true` to get a source-level
debuggable engine — the same as `shumway --debug`, but for the code embedded
in your DLL:

```csharp
var engine = Greeter.Bundle.CreateEngine(debug: true);
```

Attach a debugger to your process (as Managed .NET Core) and set breakpoints
in the bundled modules; a module that still carries its source is shown from
that embedded source. Enable it only if the bundle was built debuggable
(`shumway-compile --debug`); there is one debugger per process, so a second
`CreateEngine(debug: true)` throws. See [the debugger guide](debugger.md) for
the full picture.

**Naming the factory.** By default the namespace is inferred from the DLL
filename (`Greeter.dll` → namespace `Greeter`) and the class is `Bundle`.
Override either with:

| Flag | Effect |
|------|--------|
| `--dll-namespace <ns>` | The namespace of the generated factory class. |
| `--dll-class <name>` | The factory class name (default `Bundle`). |

So `--dll ./acme.dll --dll-namespace Acme.Rules --dll-class Engine` gives
`Acme.Rules.Engine.CreateEngine()`.

Foreign-predicate DLLs passed with `--foreign-dll` are copied next to the
output and auto-loaded by the engine at `CreateEngine()` time, exactly as
with `--exe`.

`--dll` and `--exe` are mutually exclusive (one bundle, one output shape).
The same `LibraryEmitter.Emit(...)` is available as a plain .NET API for
callers that drive the linker in-process.

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
| `:- module(Name).` | Sets this file's module name (default: filename without extension). Predicates are local unless `:- public`. |
| `:- module(Name, [a/1, b/2]).` | **Export-qualified** module: every predicate is module-private (renamed `Name$pred`); only the listed indicators are importable via `use_module`. Nothing goes to the flat global namespace, so two such modules may export the same name. |
| `:- public Name/N.` | Exports the predicate to the global namespace. Required for any predicate called from another module. |
| `:- public [a/1, b/2].` | List form of the above. |
| `:- use_module(library(X)).` | Loads library `X` (baked-in, or `X.pl`/`X.shum` on the library search path) and imports its whole export surface. |
| `:- use_module(library(X), [a/1]).` | As above but imports only the listed indicators. Importing a non-exported name is an error. |
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

## Logtalk

Shumway is a working backend for [Logtalk](https://logtalk.org/) — the glue
(a backend adapter and a launcher) ships in the repository under `logtalk/`,
and the standard Logtalk library test suites and benchmarks run on it
(Tier-1 IL promotion included). See [`logtalk.md`](logtalk.md).

## Debugging

Shumway has a source-level debugger with two IDE frontends over one engine
core: breakpoints (including conditional ones whose condition is a Prolog
goal) in your `.pl` files, a call stack of your own predicates, the variables
of each frame, and stepping through the Prolog ports.

- **Visual Studio 2026** (Windows): the richest integration — a program that
  calls out to C# or native C shows those frames in the *same* mixed stack.
  See [`debugger.md`](debugger.md).
- **VS Code** (Windows and Linux): cross-platform, over the Debug Adapter
  Protocol — launch or attach, Debug Console evaluation in the live engine,
  variable editing, Jump to Cursor, logpoints. Works against the REPL
  (`--dap`), any embedded host (`SHUMWAY_DAP_PORT`), and linked executables
  (`--dap-port`). See [`debugger-vscode.md`](debugger-vscode.md).
