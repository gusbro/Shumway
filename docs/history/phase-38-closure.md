# Phase 38 — WebShumway: the engine in a browser

**Closed 2026-08-07**, tagged `phase-38`. 25 commits, 84 files, +7668/−506.

The phase's goal: make Shumway usable with nothing installed. The result is
`src/Shumway.Web/` — a static site running the full engine on WebAssembly, with
a top level, an editor, workspaces, imported libraries and offline support.

The user-facing documentation is [`guide/webshumway.md`](../guide/webshumway.md);
the design decisions are [ADR-042](../architecture/adr/042-webshumway.md).

---

## What shipped

**The app** (chunks 1–8). A feasibility spike first, because two things had to be
true and neither was certain: that the engine would trim and boot under
`browser-wasm` with its IL compiler folded out, and that `System.IO` would work
over Emscripten's MEMFS. Both did — the second so completely that `consult/1`,
`open/4` and the library search path needed no browser special-casing at all.
Then `Shumway.TopLevel` extracted from the REPL so both top levels share one
implementation, the query UI, the editor, the workspace, and PWA/offline/sharing.

**Threads** (chunk 8). The engine's search blocks the thread it is on, so on one
thread the tab freezes. `WasmEnableThreads` moves the runtime off the UI thread
and the search onto a pool thread. Every consequence of that is load-bearing and
is recorded in the ADR: every export returns a `Task`, output is posted back
because JS interop is thread-affine, engine work is serialized behind one gate,
and the host must send COOP/COEP headers.

**The usability round.** Light and dark from one palette (the sheet was already
written in `light-dark()`, so choosing a theme is narrowing the scheme on the
root element); one versioned settings envelope; workspaces as directories that
are also the engine's working directory; export of a file and of a workspace zip;
a predicate reference built from the engine's own metadata; a guide.

**Libraries** (the last round). Import a collection from a folder or from a
GitHub URL, tagged with a dialect, and compile the libraries in it in the
background — prioritising what the workspace's programs import, pausing while you
consult, stoppable, and stopping by itself on a collection too big to grind
through unasked.

---

## Engine work the browser forced

The phase was supposed to be an application. Half of it turned out to be engine
defects that a browser surfaced and that were **not browser-specific** — the
pattern worth remembering:

| Defect | Why the browser found it |
|---|---|
| `Console.In` throws rather than returning an exhausted reader | a host with no stdin at all |
| `PrologEngine.In` did not exist | `Out` had a counterpart nowhere; `read/1` always came from the host's stdin |
| `PrologEngine.Warnings` did not exist | load-time warnings went to stderr, which a page does not have — a program loaded without part of itself and said nothing |
| `CollectAttvars` recursed per list element | a small stack made a 1000-element list overflow; the desktop needed 200 000 |
| `use_module` of a **bundle** left exports unresolvable | compiling a library to `.shum` is only worth doing where loading is slow |
| attributed variables inside a compound's own argument cell were never collected | `Qs ins 1..N` projected no domains at all |
| `listing/0` printed the engine's own library internals | only on the baked-prelude path, which is how the browser boots |

Two of these are worth calling out.

**The attvar collector** conflated two address namespaces in one visited set. An
unbound variable inside a list lives in the argument cell itself, so once it
gains an attribute its address IS the compound's — and the compound's own mark
swallowed it. `length(Qs,N), Qs ins 1..N` answered `Qs = [_G4,_G6,_G8]` with no
domains: silently wrong rather than merely unhelpful.

**`use_module` from a bundle** failed in a way that pointed away from the cause:
the module loaded, its OPERATORS arrived so the program parsed, and its
predicates resolved to nothing. Two things only the source path did — recording
which module the consult declared, and matching exports against the clauses a
module defines, which a bundle has none of.

---

## Measurements

Interleaved, back to back, on the desktop (`use_module(library(clpz))` from
Scryer's library, three runs each):

| | |
|---|---|
| from source | 8195 / 9228 / 7079 ms |
| from a compiled `.shum` | 1266 / 980 / 1706 ms |

About six times faster, same answers. Compiling costs 9.2 s (`--consult`) plus
3.0 s of packaging, once, for 293 KB over 16 modules. In the browser the same
consult went from about 28 s to seconds.

`answer_max_depth` was added because `numlist(1, 10000000, X)` has an answer
nobody wants delivered in full.

---

## Deliberate limits

- **Tier-0 only.** A browser does not allow runtime code generation. Programs
  behave identically; only speed differs — the project's standing invariant, here
  enforced by the platform.
- **The hosting requirement is real.** COOP/COEP rules out hosts that cannot set
  headers, GitHub Pages among them. `credentialless` (needed for the library
  importer's listing call) is unsupported in Safari, where URL import does not
  work and folder import does.
- **`#selftest` is the only automatic test at this layer.** A browser-wasm project
  cannot be referenced by a normal test project. Everything below it —
  `Shumway.TopLevel` included — is covered by the ordinary gate.
- **One report is unexplained.** An intermittent `unknown library 'clpz'` right
  after a background compile, which a reload clears. The resolver caches nothing,
  so it means the file or the search-path entry was absent at that instant; the
  warning now names the directories it searched, which will settle it next time
  it appears.

---

## Gate at close

Core 444 / Interpreter 105 / Compiler 364 / ISO 298 / Embedding 3840 /
DialectInterop 9.
