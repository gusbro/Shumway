# Shumway: User Guide

Shumway is a Prolog compiler and interpreter for the .NET platform.
This guide is for developers who want to **use** Shumway: run Prolog
interactively, embed the engine in a .NET application, and ship
precompiled Prolog programs as deployable bundles.

For internal design see
[`architecture/overview.md`](../architecture/overview.md) and the ADRs in
[`architecture/adr/`](../architecture/adr/). For the predicate library
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
9. [.NET Framework hosts](#net-framework-hosts)
10. [Logtalk](#logtalk)
11. [Debugging](#debugging)
12. [Limits](#limits)

Related guides outside this document: [embedded native C](embedded-native-c.md)
(`:- c` / `{…}` blocks), [the interop guide](interop.md) (every C# ↔ Prolog
mechanism, routed), and [WebShumway](webshumway.md) (the engine in a browser).

---

## What you get

Shumway ships as several .NET projects, each with a clear role:

| Project | Output | Purpose |
|---|---|---|
| `Shumway.Embedding` | `Shumway.Embedding.dll` | Main library. Reference from your .NET app to embed the engine. |
| `Shumway.Repl` | `shumway` executable | Interactive top-level (REPL). Consults files, prints solutions, exits on `halt.` |
| `Shumway.Compile` | `shumway-compile` executable | Compiles one `.pl` to a `.shmo` (per-module compiled object). |
| `Shumway.Link` | `shumway-link` executable | Links one or more `.shmo`s into a `.shum` bundle with reachability + missing-predicate analysis. Also produces standalone executables (`--exe`). |
| `Shumway.Lib` | `shumway-lib` executable | Librarian: packages `.shmo` objects into a `.shum` **library archive** (the `ar` model: every added object kept, no reachability pruning; `create`/`add`/`delete`/`list`/`extract`). The linker pulls members from such a library on demand, like a C linker pulling from a `.a`. |
| `Shumway.Dap` | `shumway-dap` executable | Debug adapter for VS Code (ADR-036): the small executable the VS Code extension launches, forwarding the Debug Adapter Protocol to a running Shumway's `--dap` endpoint. Not run by hand. |
| `Shumway.Disasm` | `shumway-disasm` executable | Diagnostic: prints the WAM bytecode disassembly of each predicate (post-indexing dispatch + clause bodies). For inspecting code generation. |

You typically need only `Shumway.Embedding` plus one or more of the
CLI tools.

### Inspecting compiled bytecode (`shumway-disasm`)

`shumway-disasm` compiles the static predicates in a source file (with
first-/multi-argument indexing) and prints the WAM bytecode the Tier-0
interpreter runs: the `switch_on_term` / `try` / `retry` / `trust`
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
`compile_mode=release`: no `meta dbg_info` markers); pass `--debug` to
include the per-clause source-position markers. DCG rules are expanded;
directives are skipped. The same functionality is available in-process
via `Shumway.Compiler.Wam.PredicateDisassembler`.

For the **Tier-1 IL** counterpart (what the IL compiler emits for a
module, including region methods) use
[`shumway-compile --dump-il`](#dumping-generated-wam-and-il-for-analysis)
(and `--dump-wam` for a whole-module, dump-to-file WAM disassembly).

---

## Building from source

```bash
git clone <repo>
cd Shumway
dotnet build -c Release Shumway.slnx
dotnet test
```

### The toolchain directory

Each project builds to its own `src/<Project>/bin/<configuration>/net10.0/`,
and the build then collects **all of them into one directory**:

```
dist/<configuration>/
├── shumway.exe            shumway-lib.exe
├── shumway-compile.exe    shumway-disasm.exe
├── shumway-link.exe       shumway-dap.exe
├── Shumway.*.dll          the assemblies they share
└── lib/                   the shipped Prolog libraries (ADR-038)
```

Put it on your PATH and the tools are simply available, the layout GNU Prolog
ships:

```bash
export PATH="$PWD/dist/Release:$PATH"       # Windows: add dist\Release
shumway myprogram.pl
shumway-compile app.pl -o app.shmo
```

This is worth doing rather than `dotnet run --project ...`, which re-evaluates
the project (restore check, up-to-date check, MSBuild graph) on *every*
invocation: **about 5.8 seconds against 0.65** for the executable, paid again
at every step of a compile/link/run cycle.

The directory is collected by `build/Shumway.Dist`, which runs after the CLIs
build. `dotnet clean` removes it, so a stale executable never lingers on
someone's PATH. Under `-p:ShumwayNetFx=true` (ADR-043) the .NET Framework
flavour lands in `dist/<configuration>/net48/`.

For a single self-contained executable instead, see
[Native AOT](native-aot.md) or `shumway-link --exe`.

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
`--dap <port>` / `--dap-wait <port>` (VS Code debugging: see
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
- Press `Esc` to abort a long-running query. Cancellation is cooperative:
  the engine stops at its next safe point (not instantaneous, but
  responsive) and prints `% Execution aborted.`. This covers the usual
  runaways, including failure-driven builtin loops like
  `between(0, BIG, X), fail` and `repeat, fail`.
- A query wider than the terminal wraps across rows; the cursor tracks
  the edit position. `↑`/`↓` walk history, `Tab` completes predicate
  names, and the usual Home/End/Ctrl-A/E/U/K editing keys work.
- `?- [file1, file2, …].` consults each file of the list, in order (the
  Edinburgh syntax). An extensionless name is retried as `name.pl`;
  `consult/1` accepts the same list.
- `?- [user].` enters clauses **interactively**: each input line is read
  behind a `|: ` prompt and collected until Ctrl-D / Ctrl-Z+Enter or a
  line reading `end_of_file.`, and the collected text is then consulted
  like a file.
- End the session with `halt.` (or `halt(N).` to exit with an explicit
  exit code), or with Ctrl-D / Ctrl-Z+Enter (end of input).
- `true` is printed for variable-less success, `false` for failure,
  otherwise `X = …, Y = …` for the binding set.
- Answers print **quoted**, so they read back as typed: an atom that
  needs quotes keeps them (`X = 'hello world'`), and a control character
  in a value appears as its escape, never as a raw byte in the
  transcript. A list of characters displays as a double-quoted string
  (`L = "abc"`, matching the default `double_quotes = chars` reading);
  a list of integer codes stays numeric. A list of characters left **open**
  displays with the double bar, `S0 = "a text"||S`, which says where the text
  ends and the tail begins. It is read back the same way: `"abc"||T` is
  `[a,b,c|T]`, the bars attach to the double-quoted literal itself (so
  `("a")||T` is a syntax error), and the tail follows the same rule, which
  makes `"a"||"b"||"c"` the list `[a,b,c]`.
- An uncaught error prints the same term that `catch/3` would have
  received. If the prompt reports
  `% error: existence_error(procedure, foo/0)`, then
  `catch(Goal, error(existence_error(procedure, foo/0), _), Recovery)`
  is a catcher that matches it; the message never shows a shape the
  catcher would miss.
- Loading a `.shum` bundle makes its program's predicates callable from the
  prompt, just as consulting the source would. For each **bare** module (one
  *without* `:- module(Name, [Exports])`) the REPL aliases the module's
  predicates into the top-level `user` module and also inherits what that
  module imported, so if it did `use_module(library(clpz))`, you can pose
  raw `X in 1..3` goals at the prompt too. It prints what it promoted
  (`%   promoted 'prog' to user: …`). Library modules (export-qualified) are
  never promoted: their names stay namespaced; reach them by importing
  (`use_module(library(X))`). If two bare modules would define the same name,
  **neither** is promoted (all-or-nothing per module, so `user` is never left
  half-populated); the REPL says so (`%   NOT promoted 'a' - name clash …`)
  and you call those by qualifying (`a:pred(…)`). This convenience is
  REPL-only; an embedded `PrologEngine.LoadBundle` leaves the namespaces
  exactly as linked.

The REPL is also AOT-publishable: see
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
| `Bindings` | `IReadOnlyDictionary<string, Term>`: every captured variable. |
| `this[name]` | `Term?`: convenient indexer for one binding. |
| `ToString()` | `"X = 1, Y = foo(2)"` style. |

A `Term` is the parser's AST (`AtomTerm`, `IntTerm`, `FloatTerm`,
`BigIntTerm`, `CompoundTerm`, `VarTerm`). Cast / pattern-match for typed access:

```csharp
if (sol["X"] is IntTerm i) Console.WriteLine(i.Value);
if (sol["L"] is CompoundTerm cons && cons.Functor == "." && cons.Args.Length == 2)
    Console.WriteLine($"list head = {cons.Args[0]}");
```

**Text** reaches C# as one of two things, decided by what it *is* in Prolog and
never by how the engine stored it (ADR-047): text as a **value** is an atom, and
text as a **sequence** is a list. A double-quoted literal is a list, so it
arrives as one (the same list whether or not the engine packed it) and a
foreign predicate called with a packed list and with the equivalent cons list
receives the same argument.

`Term.TryAsText` reads either shape, plus an atom, when what you want is the
characters:

```csharp
if (sol["X"]!.TryAsText(out string text)) Console.WriteLine(text);
```

`Get<string>(name)` uses it, so a `string` binding works whichever way the
Prolog side produced the text.

### Loading constraint and coroutining libraries

The attributed-variable libraries are opt-in:

```csharp
engine.UseClpfd();        // module 'clpfd': finite-domain constraints
engine.UseClpr();         // module 'clpr': linear-real constraints
engine.UseCoroutining();  // module 'coroutining': freeze/2, dif/2
```

All three can be enabled on one engine (their `verify_attributes/4`
hooks are `:- multifile`, dispatched by the attribute module); keep each
variable's constraints within one library: mixed clpfd+clpr constraints
on the same variable are not supported. From Prolog source, the same
libraries load with `:- use_module(library(clpfd))` /
`library(clpr)` / `library(coroutining))`.

The coroutining library provides `freeze/2` (delay a goal until a
variable is bound), `frozen/2`, `when/2` (delay on a general condition:
`nonvar/1`, `ground/1`, `?=/2`, and their `(,)`/`(;)` combinations), and
`dif/2` (a sound disequality that fails the moment its arguments become
identical). `?=/2` (decided (in)equality), `unifiable/3` (the unifier of
two terms as a `V=Value` list), `term_attvars/2` and `call_residue_vars/2`
are always available and need no library.

A copy does not carry constraints. `copy_term/2` copies an attributed
variable as a plain one, so the copy of a constrained term is an
unconstrained term. What carries them is `copy_term/3`, which hands back
the goals that put the attributes on the copy:

```prolog
?- freeze(X, foo), copy_term(X, Y, Goals), maplist(call, Goals).
```

leaves `Y` frozen on the same goal as `X`, while `copy_term(X, Y)` alone
leaves `Y` a plain variable. The same three-argument form is what the top
level uses to show residual constraints, and what the blackboard uses to
store a constrained value.

Everything that stores a term stores a copy, so the rule reaches further
than `copy_term/2`: a clause you `assert`, a solution `findall/3`
collects, an entry in the recorded database and a non-backtrackable
global variable all hold plain variables where the term you gave them
held constrained ones. The original is untouched, and reading a stored
term back gives no constraint. To carry one, store the goals beside the
value:

```prolog
?- freeze(X, foo), copy_term(X, Copy, Goals), assertz(p(Copy, Goals)).
?- p(Y, Goals), maplist(call, Goals).
```

`bb_put/2` does exactly that for you, which is why a value put on the
blackboard comes back constrained while the same value asserted does
not. `b_setval/2` stores the term itself rather than a copy, so a
constraint does survive it, but only for the duration of the query that
set it.

### Running quad test transcripts

A quad file is a machine-readable test transcript (queries with their
sanctioned outcomes) rather than a program, so consulting one directly
is a syntax error. `library(quads)` makes the transcript loadable: the
import activates the `?-` and `|` operators for your session, each quad
is captured as it is consulted, and `run_quads/0` runs every loaded quad
and reports a pass count with the failing ids.

```prolog
?- use_module(library(quads)).
?- consult('length_quad.pl').
?- run_quads.
quads: 37/37
```

`run_quads(Id)` runs one quad, `clear_quads/0` forgets the loaded set.
A quad whose sanctioned outcomes include looping runs under a
15-second limit; still running then counts as the looping outcome. The
same workflow runs in the browser build, and
`shumway --quads file.pl` does all of it from the command line. The
format and workflow are described in detail in [quads.md](quads.md).

### Loading third-party Prolog libraries

Beyond the baked-in libraries above, you can drop your own (or third-party)
Prolog `.pl` sources in a directory and load them with
`use_module(library(X))`: the SICStus/Scryer/SWI convention. Shumway resolves
`library(X)` to `X.pl` (or a compiled `X.shum`) on a **library search path**,
fed by all of:

- the shipped `lib/` directory next to the executable (the REPL and CLIs add it
  automatically: that is where `library(lists_ext)` lives);
- the `SHUMWAY_LIBRARY_PATH` environment variable (`;`/`:`-separated dirs);
- `file_search_path(library, Dir).` facts (SWI/Scryer) and
  `library_directory(Dir).` facts (SICStus); both are `:- dynamic`, so a
  program can add to the path at load time;
- the embedding API: `engine.AddLibraryDirectory("/path/to/libs")` (or
  `engine.AddDefaultLibraryDirectories()` for the shipped `lib/`).

```prolog
:- use_module(library(lists_ext)).      % imports its whole export surface
:- use_module(library(lists_ext), [take/3, drop/3]).  % only these
```

**Libraries from another Prolog system (ADR-040).** Shumway can host libraries
written for Scryer or SWI *side by side in one engine*: "uniting worlds". Tag a
search directory with the dialect its libraries are written in, and the
libraries loaded from it parse in that dialect (name resolution + `double_quotes`:
Scryer `chars`, SWI `codes`):

```bash
# CLI / REPL: a leading dialect: prefix on -L (or a SHUMWAY_LIBRARY_PATH entry).
shumway -L scryer:C:/Scryer/lib -L swi:C:/swipl/library
```
```csharp
// Embedding:
engine.AddLibraryDirectory("/path/to/scryer/lib", "scryer");
engine.AddLibraryDirectory("/path/to/swipl/library", "swi");
// or the preferred dialect for an ambiguous name:  set_prolog_flag(library_dialect, swi).
```

Coexistence is the default: a name unique to one system always resolves; the
dialect only disambiguates a name two systems both define. (The
`Shumway.Tests.DialectInterop` project (part of the regular test gate) 
exercises this; its deeper end-to-end sweeps against a real Scryer / SWI
checkout are the opt-in part, gated on `SHUMWAY_SCRYER_LIB` /
`SHUMWAY_SWI_LIB`.)

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

Consulting a module file **directly** (on the REPL command line, via
`consult/1`, or through the embedding `ConsultFile`/`ConsultString`) imports
its whole export surface into the top level automatically (the SWI
convention) so `hello(X)` is callable right after loading the file above. A
module loaded as a *dependency* (via another module's `use_module`) feeds only
the importer's table and stays invisible at the top level.

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

`LoadBundle` consults every module in the bundle. A **persisted** Tier-1 IL
assembly (`shumway-link --with-compiled-il`) is bound at load, so those
predicates run as compiled IL from the first query. Predicates that ship as
WAM bytecode (a plain bundle) are **not** compiled at load: that would Sigil-
compile the whole program up front (~1.5 s on a large one) for code that may
never run hot. Instead each promotes to Tier-1 IL lazily once its call counter
crosses the threshold. To front-load the whole set anyway (a server that will
serve many queries and wants steady-state speed from the first) call
`compile_all/0` (or `compile_all(-Count)` for how many it compiled), or the C#
`engine.WarmAllCompilable()`.

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

### Step 1: `shumway-compile` (per-module compilation)

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
  (currently `3`). The format is frozen pre-release: writer and reader
  require exactly this version: there is no backward compatibility with
  older `.shmo` versions until the first public release.
- Every `.shmo` and `.shum` also records **the Shumway version that wrote
  it** (three `uint32`s at the start of the body). That is a different
  question from the format version: the format version says whether a
  reader can read the file at all, the generator version says which build
  produced it, so an old artifact can be identified and diagnosed when the
  format eventually changes, rather than only rejected. `shumway-link
  --map` prints it, and it is on `Bundle.GeneratorVersion` /
  `ShmoObject.GeneratorVersion` for embedders.

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
| `-c, --consult` | Compile by **consulting** the file(s) in an ephemeral engine (directives execute, `term_expansion` / `goal_expansion` hooks run, `use_module` dependencies load) and emit one `.shmo` per module the load brought in. Required for libraries that *generate clauses at load time* or need operators defined by their dependencies: the file-at-a-time compile above cannot run those hooks. All inputs are consulted into **one** engine, so a module several inputs share (a library and its dependencies) compiles **once**; and a dragged-in dependency's `.shmo` is **reused when up to date** (it exists, is at least as new as the module's source, and was built in the same mode), so compiling `a.pl` then `b.pl` in separate runs produces the same object set as one batch. Each object is self-contained (it carries its own clauses, dynamic seeds, and operators) so any reachability-complete subset links correctly. See [Packaging a third-party library](#packaging-a-third-party-library-into-a-bundle---consult). |
| `-L, --library-dir <dir>` | With `--consult`: an extra directory searched to resolve `use_module(library(X))` when a dependency is not next to the compiled file. Repeatable; also reads `SHUMWAY_LIBRARY_PATH`. (The compiled file's *own* directory is always searched, so siblings resolve with no flag.) |
| `-h, --help` | Usage summary. |

**Error handling**: the compiler is C-style, on a parse or directive
error, it resyncs to the next clause-terminator dot and keeps going,
so you see every error in one pass (up to a 100-error cap). All
diagnostics are printed in the standard `file:line:col: error: msg`
shape. Exit codes: `0` ok, `1` compile error, `3` usage error.

#### Dumping generated WAM and IL for analysis

Two analysis flags write the compiler's intermediate code to a text file
so you can read exactly what it generates for a module. They are
diagnostic aids (**they do not change the emitted `.shmo`** (always WAM)
) and both **append**, so delete the target file between runs.

```bash
# Dump both the WAM and the region-form IL for a module.
shumway-compile --dump-wam prog.wam.txt --dump-il prog.il.txt --regions \
  -o prog.shmo prog.pl
```

- **`--dump-wam <file>`** decodes the just-built module and appends a
  readable disassembly of every predicate's WAM bytecode: the same
  `switch_on_term` / `try` / `retry` / `trust` dispatch and clause bodies
  the Tier-0 interpreter runs. (For ad-hoc, stdout-only WAM inspection of
  a single predicate, [`shumway-disasm`](#inspecting-compiled-bytecode-shumway-disasm)
  is often handier; `--dump-wam` is the whole-module, dump-to-file form
  that pairs with `--dump-il`.)

- **`--dump-il <file>`** runs the Tier-1 IL compiler over each predicate
 (with the whole module as the callee map, so it can see the local
  closure) and appends each generated method's IL. Without `--regions`
  you get one method per predicate; with `--regions` you get the
  **region** methods, where a predicate and its transitively-reachable
  local callees are compiled into one IL method (each member a labelled
  block, intra-region calls a `br`). This is how you inspect what
  Tier-1 actually emits, including the region dispatch `switch`, the
  per-member blocks, and the inline first-argument index decision.

Each method is preceded by a header: `;;; user$pick/2 clauses=3 …` for
WAM, `;;; ===== region root=… members=[…] =====` or `;;; ===== compile
fid=… =====` for IL. Note the dump compiles **every** predicate as a
region root, so it is a superset of what runs (at runtime only
invocation-promoted predicates become roots; many are only reached as
members of another region).

> The same hooks are reachable in-process: set
> `Shumway.Compiler.Il.IlPredicateCompiler.IlDumpPath` (and
> `.RegionCompile`) before compiling, or use the `SHUMWAY_IL_DUMP` /
> `SHUMWAY_REGION` environment variables when running the REPL.

#### Packaging a third-party library into a bundle (`--consult`)

The per-file compile above reads each `.pl` **without running it**. That is
fine for ordinary Prolog, but a real third-party library (a constraint
solver, an attributed-variable package, a DCG-heavy grammar) often does
work *at load time* that its own clauses then depend on:

- **`term_expansion` / `goal_expansion`** that generate clauses by
  *executing* a predicate defined earlier in the same file (Scryer's
  `clpz`, `dcgs`, the `atts` machinery all do this);
- **operators** (`:- op(...)`) declared by a *dependency* that the
  importing file's clauses need in order to parse;
- **`:- initialization`** or other directives whose effects are part of
  the compiled program.

A file-at-a-time compile cannot see any of that (the hooks never run), so
it either fails to parse or silently drops the generated clauses. **`--consult`
compiles by actually loading the file** in a throwaway engine (directives
execute, hooks run, `use_module` dependencies are pulled in) and then writes
**one `.shmo` per module** the load brought into memory. This is how you take
a library written for another engine (SICStus, Scryer, SWI) and turn it into a
Shumway bundle *without editing its source*.

**Worked recipe: a program using Scryer's `clpz`, from an unpatched
checkout.** Point Shumway at your own copy of the library (nothing
third-party is shipped in Shumway; you supply the sources). Say `app.pl` is:

```prolog
:- use_module(library(clpz)).
main :- X #> 2, X #< 6, label([X]), write(x(X)), nl, fail ; write(done), nl.
```

`app.pl` itself cannot be compiled file-at-a-time: it uses `clpz`'s
operators (`#>`, `#<`), which only exist once `clpz` is loaded. So the whole
thing goes through **one `--consult` pass over `app.pl`**: the ephemeral
engine loads `app.pl`, which pulls in `clpz` (its operators, its
load-generated clauses) and `clpz`'s own dependency graph, and every module
that ends up in memory is emitted as its own `.shmo`.

```bash
# 1. One consult pass over your program. -L points at your Scryer lib dir so
#    use_module(library(clpz)) resolves; -o names an output directory.
shumway-compile --consult -o out/ -L /path/to/scryer/lib app.pl
#   → out/app.shmo, out/clpz.shmo, out/atts.shmo, out/lists.shmo, … (19 objects)

# 2. Link them into a bundle. --allow-undefined is needed here (see below).
shumway-link -o app.shum --entry main/0 --allow-undefined out/*.shmo

# 3. Run it: no source, no Scryer present.
echo 'main, halt.' | shumway app.shum        # → x(3) x(4) x(5) done
```

**Why `--allow-undefined`.** A library written for another engine references
*that* engine's internal builtins: Scryer's `lists` calls `$unattributed_var/1`
and `$det_length_rundown/2`, which are Scryer primitives Shumway does not
provide. Those references sit on library branches the reachable runtime path
never takes (Shumway's own prelude `length/2` wins), so the program runs
correctly; but the linker's default strict check would refuse the bundle over
them. `--allow-undefined` downgrades those to warnings and still produces the
bundle: the engine only raises `existence_error/2` if such a predicate is
*actually called*. If your workload genuinely reaches one, that error tells
you exactly which primitive to supply (as a `[PrologPredicate]` foreign, or by
providing a `.pl` definition on the library path).

The resulting `app.shum` is a normal bundle: load it in the REPL, embed it
(`PrologEngine.FromBundle`), or turn it into a native executable with `--exe`
(below). You can add `--with-compiled-il` for a Tier-1 IL bundle, but whether
it pays off is library-dependent (for `clpz` the hot predicates are dynamic,
so IL helps less than for ordinary static code: measure your own workload).

Two things to know going in:

- **The directives DO run during compilation.** `--consult` executes the
  file. Only consult sources you trust, exactly as you would only *run* a
  program you trust.
- **You do not have to know the flag in advance.** `shumway-compile` prints a
  hint pointing at `--consult` whenever a file compiled the ordinary way
  relies on load-time hooks or dependency-defined operators.

### Step 2: `shumway-link` (linker)

```bash
shumway-link -o app.shum \
  --entry main/0,init/1 \
  --entry shutdown/0 \
  [--allow-undefined] [--strip] [--map app.map] [-v] \
  lib.shmo util.shmo app.shmo
```

- One or more positional inputs: `.shmo` objects, `.pl` sources (compiled on
  the fly: file-at-a-time, or through the consult pipeline with `--consult`),
  and `.shum` librarian archives (members pulled on demand).
- **Reachability root** is either `--entry pred/N` (repeatable;
  comma-separated within a flag) or `--goal Term` (see
  [Producing a runnable executable](#step-3a-producing-a-runnable-executable)).
  At least one is required.

| Flag | Effect |
|---|---|
| `-o, --output <path>` | Output `.shum` path (required). |
| `--entry pred/N[,…]` | Entry-point predicates. Repeatable; comma-separated within a flag. |
| `--goal Term` | Adds the goal's head as an implicit entry point. Required when `--exe` is set. |
| `--allow-undefined` | Downgrade missing-predicate errors to warnings; still produce the bundle. The engine raises `existence_error/2` at call time if the missing predicate is actually invoked. |
| `--warn-shadow` | Warn when a module's **local** predicate shares an indicator with another linked module's public: the C `static`-shadows-global shape. Legal either way (inside its module the local wins); the `--map` file always lists these regardless of the flag. (Two *publics* with the same indicator are always a `duplicate_public` **error**.) |
| `-L, --library-dir <dir>` | Directory searched to resolve a `use_module(library(X))` dependency not passed explicitly: `X.pl`/`X.shmo` is compiled and linked in (transitively), C-linker style: already-provided inputs win, source is the last resort. Repeatable; also reads `SHUMWAY_LIBRARY_PATH`. |
| `--consult` | Compile `.pl` inputs **through the consult pipeline** (directives and `term_expansion` / `goal_expansion` hooks run, `use_module` dependencies load) instead of file-at-a-time: the linker equivalent of `shumway-compile --consult`. Needed when a source uses a library's operators or generates clauses at load time; every module the load brings in is linked. Without it, a `.pl` that uses `library(...)` compiles file-at-a-time and the linker prints a hint pointing here. |
| `-s, --strip` | Remove the embedded Prolog source from every bundle entry. Bytecode preserved. Useful for size analysis / IP-protection. (Stripped bundles dispatch correctly via the source-less load path.) Note: a `.shmo` always carries the module's clause terms: it is an *intermediate* build artifact, like an object file with embedded IR, and the linker uses them for cross-module optimization (e.g. the meta-wrapper unfold). IP stripping is about what ships: the `.shum` / executable, which never carry clause terms. |
| `-m, --map <path>` | Write a C-toolchain-style audit file describing what landed in the bundle: per-module sizes, exported / dynamic predicate lists, local-shadows-public listing, dropped modules, totals. |
| `-i, --with-compiled-il` | Persist a Tier-1 IL assembly inside the bundle so it runs as compiled IL (no load-time JIT of the WAM). By default the IL uses the **region** layout with the dead-region prune applied: a predicate and its local closure share one IL method, and each absorbed-only predicate drops its standalone IL. |
| `--no-region-prune` | With `--with-compiled-il`: emit one standalone IL method per predicate instead of the default pruned region layout. Mainly for inspecting the generated code; bundles are larger and typically slower. |
| `--strip-wam` | Implies `--with-compiled-il`. Drop the redundant WAM bodies of the predicates the bundle runs as IL: standalone-IL predicates (each has its own IL delegate) and, under the default region prune, the region-absorbed members too (each is reachable by functor id through its region method's member-entry cursor). The bundle then ships IL, not WAM. JIT-only (the IL must load, not for Native AOT). |
| `--prune-report` | Stage-9 dead-region dry-run: report how many standalone forms would be prunable. Info diagnostic; no change to the bundle. |
| `--dump-wam <path>` | Append a disassembly of the WAM the bundle **ships** (each entry's final bytecode, after `--strip-wam` / region prune) to `<path>`. See [below](#dumping-the-shipped-il--wam-from-the-linker). |
| `--dump-il <path>` | Append the Tier-1 IL the bundle **ships** to `<path>` (implies `--with-compiled-il`). See [below](#dumping-the-shipped-il--wam-from-the-linker). |
| `-e, --exe <path>` | Emit a single-file native executable. See [step 3a](#step-3a-producing-a-runnable-executable). |
| `-g, --goal Term` | The goal the `--exe` runs at startup. Trailing `.` optional. |
| `--self-contained` | Used with `--exe`: bake the .NET runtime into the binary (~70 MB exe, runs on machines without .NET). Default is framework-dependent (~5-10 MB exe, requires .NET 10 runtime on the target). |
| `--debug` | Used with `--exe`: build the executable debuggable, so its modules compile debuggable and it materialises their embedded source at startup, so a debugger attached to the process sets breakpoints and steps (see [`debugger.md`](debugger.md)). Requires the bundle to carry source (compile inputs with `shumway-compile --debug`; not with `--strip`). |
| `--debug-wait` | Like `--debug`, but the executable also blocks at startup until a debugger has attached and armed its breakpoints, so the first goal can be stopped in. Implies `--debug`. |
| `--dap-port <port>` | With `--exe --debug`: bake a VS Code (DAP) debug endpoint into the executable: it listens on `127.0.0.1:<port>` whenever it runs. At run time `SHUMWAY_DAP_PORT` overrides the baked port (`0` disables). Implies `--debug`. See [`debugger-vscode.md`](debugger-vscode.md). |
| `-d, --dll <path>` | Emit a loadable .NET class library embedding the bundle, with a factory that hands back a ready engine. See [step 3b](#step-3b-producing-a-loadable-net-class-library---dll). Mutually exclusive with `--exe`. |
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
**superset**: every predicate compiled as a region root. The linker's
equivalents dump the **ground truth of what the bundle actually ships**, after
reachability + the Stage-9 region prune:

```bash
shumway-link -o app.shum --entry main/0 \
  --strip-wam \
  --dump-il app.il.txt --dump-wam app.wam.txt \
  lib.shmo app.shmo
```

- **`--dump-il <file>`** appends the persisted Tier-1 IL the bundle ships:
  post-prune, region mode + forced roots (region prune is on by default). Each method
  is headed `;;; ===== persist region root fid=… members=N [member list] =====`
  (a region) or `;;; ===== persist fid=… clauses=N =====` (standalone). Implies
  `--with-compiled-il` (there is no IL to dump otherwise). This is the IL that
  runs cross-process and as `--exe`.
- **`--dump-wam <file>`** appends the WAM of each bundle entry's **final**
  bytecode, *after* any strip. With `--strip-wam` the dump shrinks to only the
  predicates that kept a Tier-0 body (with `--strip-wam` it can
  be empty: the whole user module runs on IL; that emptiness is the
  confirmation the strip was complete).

Both **append**: delete the files between runs.

### Step 3: Load the bundle

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

### Step 3a: Producing a runnable executable

For deployment as a standalone binary (no host application needed),
use `--exe`:

```bash
shumway-link -o app.shum \
  --exe ./myapp \
  --goal "main" \
  lib.shmo app.shmo
```

This produces `./myapp` (or `./myapp.exe` on Windows): a single-file
native executable for the current platform. On launch it loads the
embedded bundle, runs the goal, and exits with:

- `0` (goal succeeded
- `1`) goal failed
- `2`: uncaught Prolog exception or unexpected host error

The `--goal` accepts both `main` and `main.` (the trailing
clause-terminator dot is optional). The argument is parsed and
validated syntactically at link time, so typos surface immediately.
The goal's head predicate is also added as an implicit reachability
root, so `--goal` alone is enough: `--entry` is optional in the
`--exe` flow.

**Deployment modes:**

- **Default (framework-dependent)**: ~5-10 MB single file. Requires
  .NET 10 runtime to be installed on the target machine.
- **`--self-contained`**: ~70 MB single file. The .NET runtime is
  baked in; the binary runs on machines with nothing installed.

The build host must have the .NET 10 SDK (which it does, since
`shumway-link` is itself a .NET tool). Cross-targeting other
platforms isn't supported yet: the produced binary matches the
current platform.

### Step 3b: Producing a loadable .NET class library (`--dll`)

`--exe` is for "the whole program *is* a Prolog goal". When instead you
have a **.NET application** that wants to call into Shumway for certain
goals, use `--dll`. It emits a .NET class library that embeds the bundle
and exposes a small factory you call to get a ready-to-query engine:

```bash
shumway-link greet.shmo \
  --dll ./Greeter.dll \
  --entry greet/1
```

`--dll` needs a reachability root just like a normal link: pass
`--entry` (or `--goal`). Unlike `--exe` there is no startup goal: the
DLL never runs anything on its own; it just makes the bundle available.

This produces `Greeter.dll` plus the Shumway runtime DLLs it depends on
(all written next to the output). Your application references `Greeter.dll`
and calls the generated factory:

```csharp
// The factory type is <Namespace>.<Class>: default Greeter.Bundle here
// (namespace inferred from the DLL filename, class defaults to "Bundle").
var engine = Greeter.Bundle.CreateEngine();

foreach (var sol in engine.QueryAll("greet(X)."))
    Console.WriteLine(sol["X"]);     // hello, world
```

`CreateEngine()` returns a fresh `Shumway.Embedding.PrologEngine` with the
bundle already loaded (it calls `PrologEngine.FromBundle` internally, so the
baked prelude warms the runtime). Call it once per engine you want: engines
are single-threaded, so use one per thread (or an `EnginePool`). The bundle
itself is parsed once and cached; `GetBundle()` exposes that shared
`Shumway.Embedding.Bundle` if you want to load it into engines yourself.

**Debugging the bundled program.** Pass `debug: true` to get a source-level
debuggable engine: the same as `shumway --debug`, but for the code embedded
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

`ShmoLinker` is also a plain .NET API: no need to shell out:

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

- `ShmoLinker.Link(LinkConfig)`: the core synchronous link.
- `ShmoLinker.LinkAsync(LinkConfig, CancellationToken)`: thread-pool
  wrapper.
- `ShmoLinker.LinkFromFiles(paths, entries, ...)`: reads `.shmo`s
  from disk for you.
- `ShmoCompiler.CompileSource(source, fallbackModuleName)` and
  `ShmoCompiler.CompileFile(path)`: build `.shmo`s by hand for
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
| `:- ensure_linked Name/N.` | Tells the linker to treat this predicate as a **reachability root**. Use it when the predicate is called only via runtime meta-call (`call/1` with a constructed goal): the static call graph won't see the edge, and without this hint the linker would drop the predicate as unreachable. |
| `:- ensure_linked [a/1, b/2].` | List form of the above. |

Other directives (`set_prolog_flag/2`, etc.) are honoured at consult time
but do not affect link-time decisions.

`?- Goal.` in Prolog text is the Edinburgh spelling of a directive and is
equivalent to `:- Goal.` so the goal runs at consult time. This makes a 
pasted top-level transcript loadable as a file: the `?-` lines execute
instead of being misread as clauses.

### Standard order of terms

Shumway follows ISO 13211-1 §7.2.1 exactly, which differs from SWI on one
point worth knowing: between a **float and an integer the TYPE decides,
never the value**: every float sorts before every integer. The value only
breaks ties within one type.

```prolog
?- compare(Order, 1.1, 1).      % Order = (<): float first, though 1.1 > 1
?- msort([3, 1.5, 2, 0.5, 1], L).
L = [0.5, 1.5, 1, 2, 3].
```

This matches GNU Prolog, SICStus and Scryer (SWI orders numbers by value).
It affects everything built on the standard order: `sort/2`, `msort/2`,
`setof/3`, `keysort/2`, `@</2` and friends.

### The occurs check

By default unification builds rational trees: `X = f(X)` succeeds and yields
a cyclic term, which `write/1` and `==/2` handle safely. The `occurs_check`
flag changes what happens when a unification would bind a variable to a term
containing that same variable:

| Value | Effect |
|---|---|
| `false` | Default. The binding is made; the result is a cyclic term. |
| `true` | The unification **fails** instead. |
| `error` | The unification raises `representation_error(term)`. |

```prolog
?- set_prolog_flag(occurs_check, true), -X = X.
false.
?- set_prolog_flag(occurs_check, error), X = f(X).
caught: error(representation_error(term), ...)
```

The setting covers every unification: explicit `=/2`, clause-head matching,
the implicit unifications inside builtins, and attributed variables (so a
frozen variable cannot be bound into a cycle either). It takes effect
immediately, including for the remainder of the goal that set it, and on
both execution tiers.

`unify_with_occurs_check/2` keeps its own contract regardless of the flag:
it always fails, never raises, when the check trips. Conversely, terms that
are already cyclic remain legal values under `true`; the flag guards new
bindings, it does not retroactively reject existing terms.

### Integer limits

Integers are unbounded: arithmetic promotes past 64 bits transparently
(`X is 2^100` answers exactly), and rationals (ADR-039) build on the same
representation. "Unbounded" means no bound imposed by the language. The
ceiling is the representation's, which for Shumway is .NET's
[`System.Numerics.BigInteger`](https://learn.microsoft.com/dotnet/api/system.numerics.biginteger):
Microsoft documents it as having "no theoretical upper or lower bounds",
with any operation free to fail once a value grows too large for memory.
In practice its magnitude is indexed in 32 bits (a hard format cap near
2^31 bits ≈ 256 MB ≈ 646 million decimal digits per number), and internal
buffers give out somewhat earlier: measured on this engine, `2^100000000`
(a 12.5 MB integer) computes exactly and `2^1000000000` does not.

At the ceiling the engine raises the ISO error

```prolog
?- catch(_ is 2^2147483647, error(E, _), true).
E = resource_error(memory).
```

uniformly, whichever operation hits it (`^`, `<<`, a multiplication) and
whichever tier runs it.

### Float limits

Floats are IEEE 754 doubles, so the largest finite value is about
`1.797e308`. A float literal past the representable range raises
`representation_error(max_float)`, or `min_float` for a negative literal,
wherever the literal enters: source text, `read_term/2`, `number_chars/2`
and its family.

```prolog
?- catch(number_chars(N, "9.9e999"), error(E, _), true).
E = representation_error(max_float).
```

There is a single zero: the literal `-0.0` denotes plain `0.0`, as does
any arithmetic result that would be a negative zero, so `writeq(-0.0)`
prints `0.0` and agrees with `==/2` and `compare/3`, which treat the two
spellings as the same value.

A literal that underflows (`1.0e-999`) rounds to `0.0` and succeeds.
`atom_number('9.9e999', N)` fails quietly, keeping that predicate's
no-exceptions convention. Arithmetic that overflows at run time is a
different animal: `X is 1.0e308 * 10` raises
`evaluation_error(float_overflow)`.

### Digit separators

Long numbers are easier to read in groups, so you may put an underscore
between two digits of an integer. It groups the digits and is not part of the
value:

```prolog
?- X = 1_000_000.
X = 1000000.
?- X is 0xdead_beef + 0b1_0_1.
X = 3735928564.
```

Since integers here have no size limit, a literal can outgrow a line. After
the underscore you may leave a space, a line break, or a comment before
continuing with the digits, so a very long number can be written across as
many lines as it needs:

```prolog
big(1_
    /* the next 40 digits */
    2345678901234567890123456789012345678901).
```

Two rules to keep in mind:

- A digit **must** follow. Where none does there is no separator: the
  underscore begins a variable name, just as it does anywhere else.
- They work in every number: decimal, binary, octal and hexadecimal
  integers, and both parts of a float (`1_1.2_5e1_1`).

### Operator atoms as operands

An atom that is an operator cannot be the bare operand of another
operator; write it in parentheses. This includes the predicate-indicator
shape, so `mod/2` and `--> /2` are syntax errors and the readable forms
are `(mod)/2` and `(-->)/2`. Delimited argument positions still take the
bare atom: `f(-->)`, `[mod, is]` and `{-->}` all read fine, and so does
the atom standing alone as a whole term.

```prolog
?- X = (mod)/2.
X = (mod)/2.
?- atom_to_term('mod/2', T, _).
% error: syntax_error(...)
```

Sources loaded under a dialect (`library_dialect` or a dialect-tagged
library) keep that dialect's laxer reading, as do `arity_compat`
sources.

### Operator scope (ADR-046)

Operator tables are **module-scoped**, following SWI/YAP/Ciao/Scryer:

- `:- op(P, T, Name)` in a module's text defines the operator for that
  module's own source only; in module-less text it is global (`user`
  table), exactly the ISO behaviour.
- `:- op(P, T, user:Name)` (from anywhere) defines in the global
  (`user`) table (SWI's escape).
- A module's export list may carry `op(P, T, Name)` terms: importing the
  module (`use_module`, either form) activates those operators for the
  importer, in the importing module's table, or in `user` when the
  import happens at the top level (goal-form `use_module/1`, or directly
  consulting the module file).
- `op(0, T, Name)` inside a module hides an inherited global operator for
  that module only.
- Runtime `op/3` and `current_op/3` inside module code use the module's
  table; at the top level they use the global one.
- Separate compilation preserves all of this: a `.shmo`/`.shum` carries
  each module's own and exported operators, and `LoadBundle` restores the
  same scoping (a bundle module's private syntax never leaks into your
  code; its exported operators arrive when you `use_module` it).

### How a goal resolves (the name-lookup algorithm)

Resolution happens in three stages. Stages 1 and 3 are **identical**
everywhere (REPL consults, embedding `ConsultString`/`ConsultFile`,
`shumway-compile`) and a query you type at the REPL is treated as a clause
of the `user` module. Only stage 2 (binding the names nothing resolved
earlier) differs between the interactive engine and `shumway-link`.

**Stage 1: compile time.** Each body goal of a clause in module *M*
(*M* = `user` for non-module files and REPL queries) is resolved in this
order; the **first match wins**:

1. **Control constructs** (`,` `;` `->` `*->` `\+` `call/1` …) are
   transparent: resolution recurses into their sub-goals.
2. A **variable goal**: a bare variable body goal, or a variable in a
   goal argument of a meta-predicate (`findall/3`, `call/N`, …): is
   tagged with *M* and deferred to stage 3, which resolves it with *M*'s
   context when it runs.
3. A predicate declared **`:- dynamic`** is referenced bare: dynamic
   predicates live in one flat global namespace, **even inside an
   export-qualified `:- module/2`** (their clauses can be asserted from
   anywhere).
4. **M's own predicates** (anything *defined in the same file*, exported
   or not) resolve module-locally. Locals shadow everything: imports,
   other modules' publics, the prelude, **and C# builtins** (a file that
   defines `length/2` calls its own `length/2`; other modules are
   unaffected).
5. **M's import table**: entries added by `use_module/1,2` (and, for
   `user`, by the auto-import of a directly consulted `:- module/2`
   file); resolve to the exporting module's definition.
   First-import-wins on name collisions between imports.
6. Anything else is left as a **bare name** for stage 2.

**Stage 2: binding bare names.**

*Interactive / embedding* (at query setup, against everything consulted so
far), a bare name binds to a **C# builtin** first; else the **global
namespace** (legacy modules' `:- public` predicates, all dynamics, the
prelude); else it stays unresolved and, if the call is reached, the
`unknown` prolog_flag decides: `error` (default, ISO
`existence_error(procedure, N/A)`), `fail`, or `warning`.

*Compiled (`shumway-link` → `.shum`/`--exe`)*: the same binding is done
once, at link time, by the reachability walk. Each call edge resolves in
order: **module-local → the module's imports → global public → global
dynamic → builtin → prelude**. An edge that resolves nowhere is a
`missing_predicate` **error** (`--allow-undefined` downgrades it to a
warning and leaves the runtime `unknown`-flag behaviour). Modules nothing
reaches are dropped (dead-code elimination): entry points,
`:- ensure_linked` and imports are the roots, and `.shum` library archives
are pulled member-by-member, first-provider-wins, only when an edge needs
them. The behaviour of a resolvable goal is therefore the same as in the
REPL; the difference is *when* you find out about an unresolvable one
(link error vs runtime error) and that unreachable code is not shipped.

**Stage 3 (runtime meta-calls.** A goal built at runtime) `call/N`, a
variable body goal, a goal from `assert`-ed clauses, an explicit
`M:Goal`; resolves the same way in the REPL and inside a linked
executable (bundles carry every module's import table):

1. Module qualification is unwrapped (`M:G`, innermost wins); on a control
   construct the module distributes into the branch goals.
2. Control constructs route to their cut-transparent helpers; `!`, `true`,
   `fail` are handled inline.
3. If the goal carries a module *M* (an `M:G`, or a variable goal that
   originated in *M*'s code): **M's own predicates** (including
   non-exported locals: `call(mymod:internal(X))` works, SWI-style) →
   **M's import table** → fall through.
4. The **C# builtin** registry.
5. The **global namespace**: bare names of `user` and legacy-module
   predicates, publics, dynamics, the prelude.
6. Still nothing → the `unknown` flag, as above.

One asymmetry to be aware of: a *runtime* `M:Goal` reaches `M`'s
non-exported locals, while a `Module:goal(...)` written *statically* in a
compiled module is checked by the linker against the target module's
public surface.

**Rules of thumb.** Nearest context wins: own module > imports > global.
Dynamics are always global. Builtins are shadowed only by a module that
*defines* the name itself. The prelude is always visible without imports.
Consulting a `:- module/2` file directly auto-imports its exports into
`user`; loading it as a `use_module` dependency does not.

Because top-level imports beat bare-global publics, loading two libraries
with overlapping surfaces (say `clpfd` and `clpz`, which share `#=`, `in`,
…) silently reroutes the bare names to the imported one. The engine warns
on stderr (aggregated per module, in either load order) when an import
hides an already-loaded module's publics, when a module's publics land
under an existing import, or when two imports collide (first import wins;
the prelude is exempt). To reach the shadowed side explicitly, qualify the
call: `clpfd:sum(...)`.

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

## .NET Framework hosts

Shumway also multi-targets **.NET Framework 4.8**, so a legacy C#
application (32-bit included) can embed the engine, promote to Tier-1 IL
at runtime, and load persisted-IL bundles (link those with the net48 build
of `shumway-link`). See [`net-framework-hosts.md`](net-framework-hosts.md)
for the deployment matrix, the app.config a Framework host wants, and the
32-bit memory limits.

## Logtalk

Shumway is a working backend for [Logtalk](https://logtalk.org/); the glue
(a backend adapter and a launcher) ships in the repository under `logtalk/`,
and the standard Logtalk library test suites and benchmarks run on it
(Tier-1 IL promotion included). See [`logtalk.md`](logtalk.md).

## Debugging

Shumway has a source-level debugger with two IDE frontends over one engine
core: breakpoints (including conditional ones whose condition is a Prolog
goal) in your `.pl` files, a call stack of your own predicates, the variables
of each frame, and stepping through the Prolog ports.

- **Visual Studio 2026** (Windows), the richest integration: a program that
  calls out to C# or native C shows those frames in the *same* mixed stack.
  See [`debugger.md`](debugger.md).
- **VS Code** (Windows and Linux): cross-platform, over the Debug Adapter
  Protocol: launch or attach, Debug Console evaluation in the live engine,
  variable editing, Jump to Cursor, logpoints. Works against the REPL
  (`--dap`), any embedded host (`SHUMWAY_DAP_PORT`), and linked executables
  (`--dap-port`). See [`debugger-vscode.md`](debugger-vscode.md).

---

## Limits

Integers are unbounded; the heap, the stacks and the trails grow as a query
needs them. Three limits are deliberate.

| Limit | Value | At the limit |
|---|---|---|
| Arity of a compound term | none: the `max_arity` flag is `unbounded` | past address-space capacity, `resource_error(finite_memory)` |
| Arity of a predicate | 1023, the `max_procedure_arity` flag | `representation_error(max_procedure_arity)` when a clause is defined or an indicator names one |
| Expansions of one term or goal along one path | 127 | `resource_error(expansion_depth)` |
| Items shown of one answer | 100, the `answer_max_depth` flag; 0 shows all | the rest reads as `...` |

A term's arity has no limit of its own, only cost: arity N is N+1 cells of
eight bytes, and what refuses an absurd request is the address space (the
refusal comes before any allocation, so probing it returns at once). A wide
term of variables is how an array is modelled, and
`functor(A, array, 1000000)` builds one. Predicates are different: a clause
head wider than 1023 arguments cannot be defined, by `assertz/1` or by a
consulted file alike.

The expansion budget counts replacements along one path, not goals in a
clause. A hook is applied again to what it produces, so one that expands a
goal into itself never finishes; to recurse deeper on purpose, mark what is
already expanded with the identifier list of `term_expansion/6`.

Only answers are shortened. `write/1` prints what it is given.
