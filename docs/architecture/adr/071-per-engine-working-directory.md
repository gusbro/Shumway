# ADR-071: Working directory per engine, as an option of the host

## Status

Accepted (2026-11-14), after the analysis below. Not implemented yet. It
changes the observable behaviour of a builtin when several engines share a
process, and only for a host that asks for it: the default is today's
behaviour.

## Context

- **Today it is process-wide.** `working_directory/2`
  (`MetaBuiltins.Terms.cs`) calls `Directory.SetCurrentDirectory`, which
  changes the directory of the whole .NET process. `Workspace.cs` in
  `Shumway.Web` does the same.
- **Several engines per process are now normal:** the desktop `EnginePool`,
  the compile engine in WebShumway, and one engine per notebook kernel
  (ADR-068). Two of them in different directories trample each other.
- **The predicate is not ISO.** It comes from SWI-Prolog, whose manual says
  the working directory is shared between all threads, and SWI's guidance is
  to change it at most once, at initialisation.
- **What "the process" means differs.** In SWI, the threads that share the
  directory also share the database. A Shumway engine has its own database,
  flags and operators, so it is closer to a whole Prolog instance than to a
  thread. Read that way, a directory per engine is the faithful analogue of
  "one directory per Prolog process". This is an argument for analysis, not
  a conclusion.

## Decision

- **An option the host sets, not a Prolog flag.** A program that could
  switch to the process scope could move the directory of every other
  engine, so only the host chooses:

  ```
  CwdScope = Process | Engine
  ```

  The host sets it with `SetCwdScope(scope, initialDirectory)`, at any time
  and in either direction. Going back to `Process` drops the engine's own
  directory. The host can also move an engine's directory directly, without
  a query.

- **`Process` is the default**: today's behaviour. The REPL, existing
  embedders and single-engine hosts see no change.
- **In `Engine` mode:**
  - the host supplies the engine's initial directory;
  - the engine **never** touches the process directory;
  - every relative path the engine itself resolves goes against the
    engine's directory, through one helper. That covers `open/4`,
    `consult/1`/`ensure_loaded/1`, `absolute_file_name/3`, `see/tell`,
    `exists_file/1`, `directory_files/2` and library resolution relative to
    the current directory;
  - `working_directory/2` reads and changes the engine's directory only;
  - code the engine does not run follows the rules under "Code the engine
    does not run".
- **The read-only Prolog flag `cwd_scope`** reports the mode, `process` or
  `engine`, so code can tell which one it is in.
- **Invariant:** with a single engine, the two modes are indistinguishable
  from Prolog, with one exception: a relative path handed to native code
  (below). Otherwise they differ only with several engines, or when the host
  inspects the process directory.
- **Who uses it.** The notebook host (ADR-068) uses `Engine`. The desktop
  `EnginePool` supports it with no change of its own: the pool builds its
  engines through the caller's factory, and the factory picks the mode. An
  engine that is reused keeps its directory like the rest of its state.
- **There is no interim with a global lock.** A lock that swaps the process
  directory around each request serialises every engine, and the owner
  ruled it out. Hosts that need a directory per engine wait for this ADR.

## Analysis, first pass (2026-11-14)

Read against `main` at `7713419`.

- **No helper resolves a relative path today.** `PrologPath` converts
  separators only (ADR-044). A relative path reaches `System.IO` as it is,
  and the operating system resolves it against the process directory.
- **About 75 call sites** in the engine's run-time code take a path that a
  program supplied, in 16 files. They pass through few places, and the
  helper goes in those:

  | Place | What it covers |
  |---|---|
  | `RequireAtomPath` (30 callers) | the file-system builtins: create, delete, rename, copy, exists, size, time, directory listing |
  | `HostFileStream.Open` | `open/3,4` and everything built on it |
  | `FileSpecAtom` and the consult pipeline | `consult/1`, `ensure_loaded/1`, `include/1`, load paths |
  | `absolute_file_name` | its `Path.GetFullPath(spec)` |
  | `see/1`, `tell/1` | their lookup of an open stream by full path |
  | Library directories | `AddLibraryDirectory`, and the default `./lib` |
  | Others, one site each | saved states, certificate files, Unix socket paths, native libraries, assemblies of foreign predicates |

- **Source-relative resolution keeps working.** A file being loaded already
  carries its own base directory, and falls back to the process directory
  only for the outermost file. The helper replaces that fallback and
  nothing else.
- **.NET Framework 4.8** has no `Path.GetFullPath(path, basePath)`, so the
  helper combines and normalises by itself.
- **Out of scope:** about 50 more sites belong to the toolchain (the
  executable and library emitters, the linker, the compiler). They run
  outside any engine, against the directory the tool was started in.

## Code the engine does not run

The operating system keeps one directory per process. In `Engine` mode the
engine never changes it, so what matters is who resolves a relative path.

### A process the program starts

`shell/1,2` and `process_create/3`. A child process has a directory of its
own, fixed when it starts, so nothing is shared and the rule is complete:

- the child starts in the engine's directory, where today it inherits the
  process's;
- a relative `cwd(Dir)` option, and an executable named with a directory,
  resolve against the engine's directory;
- arguments are text. The child resolves them against its own directory,
  which is the engine's, so `shell('ls data')` lists the same directory in
  both modes.

A program name with no directory is still looked up on `PATH`.

**The process directory is not touched, and no lock is taken.** The
operating system takes the child's directory as an argument of the call
that creates it (`ProcessStartInfo.WorkingDirectory`), so the parent never
has to stand in that directory. This is what the `cwd(Dir)` option of
`process_create/3` already does. Checked on .NET 8 and on .NET Framework
4.8: a child started with a directory of its own reports it, and the
parent's directory is the same before and after.

### Native code the program calls

A `{...}` block and a `:- c` region compile to IL and open no file: they
read and write Prolog variables and call functions. The functions are the
case. A `:- native` function that receives a relative path as a `char*`
resolves it inside the operating system, against the process directory.
The engine cannot change that: it does not know which argument is a path,
and it does not move the process directory.

- **Decided:** in `Engine` mode native code sees the process directory,
  and this is documented. A program that passes a path to native code makes
  it absolute first; `absolute_file_name/2` does that against the engine's
  directory.
- **Later, if a program needs it:** a way to declare that a parameter of a
  `:- native` function is a path, so the engine makes it absolute before
  the call.
- **Rejected:** moving the process directory around each native call. It is
  the global lock again.

### Host code

- **Foreign predicates in C#.** `File.Open("data.txt")` inside a
  `[PrologPredicate]` resolves against the process directory. The engine
  exposes its directory and the helper, so a foreign predicate that takes a
  path can resolve it as the builtins do.
- **The host's own calls that take a path** (`ConsultFile`, for one)
  resolve against the engine's directory in `Engine` mode. The rule
  stays one sentence: a relative path that goes through the engine is
  relative to the engine's directory.
- **The initial directory**, when the host supplies none, is the process
  directory at the moment the engine is created.

None of this reaches the notebooks: a browser has no processes and no
native libraries.

## Left open

- Whether `Engine` should become the default in a later version.

## Consequences

- Multi-engine hosts get isolated directories without a global lock.
- Every new file-touching builtin must use the helper, which is enforceable
  as an invariant.

## Alternatives

- **A global gate that swaps the process directory per request.** Works
  without engine changes, but serialises all engines. Rejected.
- **Leave it process-wide and document it.** Breaks notebooks in
  subdirectories when two are open.

## Related

ADR-068, ADR-070.
