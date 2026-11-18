# ADR-070: Files in notebooks: mirroring the Contents API into MEMFS

## Status

Accepted (2026-11-16). Records D14. Its working-directory requirement
depends on ADR-071, which is built. The mirror is built (`mirror.js`, with
the demo's workspaces on it) and measured against JupyterLite 0.8.7's
contents manager ("What was built"); the kernel that calls it around each
cell is not.

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
  the demo and the Contents API for the lite site. It stays with the rest
  of the runtime's JavaScript, which the two sites share (ADR-073).
- **No OPFS in the lite site.** Two sources of truth would need
  reconciliation, which the one-direction-at-a-time rule exists to avoid.
- **The engine is unchanged.** It sees a file system, as it does in the demo.
- **Each session starts in its notebook's directory** under `/drive`. That
  needs a working directory per engine, because several sessions share one
  process (ADR-068). ADR-071 provides it and is a prerequisite: there is
  no interim that swaps the process directory under a lock.

## What was built (2026-11-17)

`mirror.js` copies between two sides, each a list, a read, a write and a
remove. The demo's workspaces use it with origin-private storage as the
store; a notebook page uses it with the contents manager. Measured in
headless Chromium, Firefox and WebKit, with a JupyterLab page of JupyterLite
0.8.7 running the demo's runtime:

- A file saved in the editor is there for `consult/1`, in its directory. A
  file a cell wrote is in the file browser, with the directory it made. An
  edit replaces the file, and a deletion on either side reaches the other.
- **`last_modified` is reliable**: a file the mirror wrote comes back with
  the time its save answered, so a second copy sends nothing.
- **The engine's file times are kept to the second**, and a file a cell
  rewrote within the second of the cell before it was not copied (found by
  the kernel's checks, 2026-11-18). The engine's side tells one version of a
  file from the next by its size and a hash of its content. The hash is
  kept once the second of the file's time is over, so a cell reads only the
  files written in the last two seconds.
- **Text is saved as text.** The contents keep the format a save gives
  them, and the editor does not open a text file kept as base64. What the
  engine wrote is text when its bytes are UTF-8 with no zero byte.
- **Notebooks are not mirrored**, in either direction. They are the
  reader's documents, and one is saved again each time a cell runs.
- **The contents are listed only after the manager says something
  changed.** Listing is slow in WebKit: 0.3 s for four files and 7 s for
  150, against 10 to 30 ms in the other two, and a kernel lists before
  every cell. The manager signals every save, rename and deletion made
  through it, by the editor or the file browser; with nothing signalled the
  copy costs nothing. Copying 150 new files takes 0.3 s in Chromium and
  Firefox and 14 s in WebKit.
- **Not built:** an empty directory is not mirrored, since a side lists
  files. Where imported libraries live is still open.

## Consequences

- Files are synced at cell boundaries. A file changed by another tab *during*
  a long-running cell is not seen until the next cell. That is acceptable.
- A change made in another tab is not signalled to this one, so it is seen
  only after something changes here too.
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
