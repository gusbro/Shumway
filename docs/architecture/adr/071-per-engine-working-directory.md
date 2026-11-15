# ADR-071: Working directory per engine, as an engine creation option

## Status

Proposed (2026-11-14). **Needs analysis before acceptance.** It changes the
observable behaviour of a builtin when several engines share a process, and
the owner wants the semantics settled deliberately, not as a side effect of
the notebook work.

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

## Proposed decision

- **A host-level option at engine creation**, not a Prolog flag that can be
  changed at run time (switching semantics mid-session would be worse than
  either choice):

  ```
  CwdScope = Process | Engine
  ```

- **`Process` is the default**: today's behaviour. The REPL, existing
  embedders and single-engine hosts see no change.
- **In `Engine` mode:**
  - the host supplies the engine's initial directory;
  - the engine **never** touches the process directory;
  - every relative path is resolved against the engine's directory, through
    one helper. That covers `open/4`, `consult/1`/`ensure_loaded/1`,
    `absolute_file_name/3`, `see/tell`, `exists_file/1`, `directory_files/2`,
    library resolution relative to the current directory, and any path
    handed to native code.
- **A read-only Prolog flag** reports the mode, so code can tell which one it
  is in.
- **Invariant:** with a single engine, the two modes are indistinguishable
  from Prolog. They differ only with several engines, or when the host
  inspects the process directory.
- The notebook host (ADR-068) uses `Engine`. The desktop `EnginePool` is a
  candidate, decided separately.

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

## Analysis still to do

- Paths that cross into foreign code (`:- c`, process spawning): relative to
  what? In `Engine` mode a spawned process should start in the engine's
  directory, not the process's.
- Whether the desktop `EnginePool` uses `Engine` mode.
- Whether `Engine` should become the default in a later version.

## Consequences if accepted

- Multi-engine hosts get isolated directories without a global lock.
- Every new file-touching builtin must use the helper, which is enforceable
  as an invariant.

## Alternatives

- **A global gate that swaps the process directory per request.** Works
  without engine changes, but serialises all engines. This is ADR-070's
  interim.
- **Leave it process-wide and document it.** Breaks notebooks in
  subdirectories when two are open.

## Related

ADR-068, ADR-070.
