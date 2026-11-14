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

## Analysis still to do

- Audit the roughly 96 `File.`/`Directory.` call sites and find out how many
  already go through a path helper (for example `PrologPath`). That
  determines the cost.
- Paths that cross into foreign code (`:- c`, process spawning, sockets with
  Unix paths): relative to what?
- Interaction with source-relative resolution (`absolute_file_name/3`
  relative to the file being loaded), which must keep working as now.
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
