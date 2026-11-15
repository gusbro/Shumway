# ADR-070: Files in notebooks: mirroring the Contents API into MEMFS

## Status

Proposed (2026-11-14). Design only; nothing is implemented. Records D14. Its
working-directory requirement depends on ADR-071.

## Context

- **What the engine sees.** In the browser, `System.IO` lands on Emscripten's
  MEMFS, so `consult/1`, `open/4`, `see/tell`, `directory/6` and the library
  path work unmodified (ADR-042 §4). The engine makes about 96 direct
  `File.`/`Directory.` calls with no abstraction. WebShumway mirrors MEMFS to
  OPFS from JavaScript, in one direction at a time: OPFS to memory on load,
  memory to OPFS on change.
- **What the user sees.** In JupyterLite, user files live in browser storage
  (IndexedDB) behind the **Contents API**, together with read-only files
  shipped with the site build. That is the file browser.
- **Jupyter convention.** A kernel starts in the notebook's directory.

## Decision

- **Mount the drive at `/drive` in MEMFS**, mirrored with the Contents API.
- **Before each execute request:** copy into MEMFS whatever changed in the
  Contents API since the last sync, by `last_modified`. A `.pl` edited in
  JupyterLite's editor is then what a following `consult/1` loads.
- **After each execute request:** copy back to the Contents API whatever the
  cell created or changed in MEMFS under `/drive`, so files written by
  Prolog (`print_and_save_table`, `tell/1`, ...) appear in the file browser.
- **Generalise WebShumway's mirror module to a pluggable target**, OPFS for
  the demo and the Contents API for the lite site. It lives in
  `@shumway/web-runtime` (ADR-073).
- **No OPFS in the lite site.** Two sources of truth would need
  reconciliation, which the one-direction-at-a-time rule exists to avoid.
- **The engine is unchanged.** It sees a file system, as it does in the demo.
- **Each session starts in its notebook's directory** under `/drive`. That
  needs a working directory per engine, because several sessions share one
  process (ADR-068). ADR-071 provides it and is a prerequisite: there is
  no interim that swaps the process directory under a lock.

## Consequences

- Files are synced at cell boundaries. A file changed by another tab *during*
  a long-running cell is not seen until the next cell. That is acceptable.
- Large drives make the "before" sync cost proportional to what changed, not
  to the drive's size, as long as `last_modified` is reliable. If it proves
  unreliable, fall back to content hashes for small files.
- Library imports (Scryer and others through the library path) need a home
  in the lite site, for example `/drive/lib` or the bundle. That is decided in
  the implementation, using WebShumway's import code.

## Alternatives considered

- **A file-system seam in the engine.** About 96 call sites; ADR-042 already
  rejected it as invasive.
- **A custom JupyterLite drive backed by OPFS.** Changes JupyterLite instead
  of our code, and puts a second store behind the file browser. Rejected.
- **A fixed working directory at the drive root.** Breaks the Jupyter
  convention, so `consult('data.pl')` fails from a notebook in a
  subdirectory. Rejected.

## Related

ADR-042 §4, ADR-068, ADR-071.
