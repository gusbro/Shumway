# WebShumway — Prolog in the browser

WebShumway is the Shumway engine compiled to WebAssembly, with a Prolog top level
around it. The engine, the files and the search all live in the page; there is no
server behind it, and nothing to upload anything to. It is served as static
files — with one header requirement, described under [Hosting](#hosting).

The source is `src/Shumway.Web/`; the design decisions behind it are
[ADR-042](../architecture/adr/042-webshumway.md).

```bash
dotnet publish src/Shumway.Web -c Release
# the site is bin/Release/Shumway.Web/net10.0/publish/wwwroot
```

---

## What it is

Two panes. On the left a program and the files it belongs to; on the right the
top level. The interaction is the one every Prolog top level has: type a goal,
get an answer, press `;` for the next one and `.` to stop.

The browser build runs the **bytecode interpreter only**. Shumway's second tier
compiles predicates to IL at runtime, which a browser does not allow — the
capability gate (`Shumway.Core.RuntimeCaps`) reports it as absent, and the
trimmer removes the IL compiler and its dependency from the payload. Programs
behave identically; only speed differs.

### Keys

| | |
|---|---|
| `;` or space | the next solution |
| `.`, Enter or Escape | stop asking |
| **Ctrl-Enter** | consult the program |
| **Ctrl-S** | save the file |
| Tab | complete a predicate name |
| Escape (while a program reads) | end of file |

Ctrl-Enter rather than a Ctrl-letter because no letter is free across browsers:
`B`, `I` and `M` open a sidebar, page info or mute the tab in Firefox; `E`, `K`
and `L` go to the address bar; `J`, `D`, `P`, `R`, `U`, `W` are taken everywhere.

---

## Workspaces

A workspace is a folder of files **and the engine's working directory**, so
`:- use_module('other.pl').`, `consult/1` and `open/4` resolve inside it with no
special casing — a program that spans several files works the way it does on a
desktop.

Switching workspace **starts a fresh engine**. A workspace is a separate
program, and carrying the last one's predicates into it would let a query answer
from a program you are no longer looking at. You are asked first if there is
anything to lose. The active workspace cannot be deleted: it is the current
directory.

Files live in the browser's private storage (OPFS) and survive a reload.
**Download** saves one file to your computer; **Export** saves the whole
workspace as a zip.

The `examples` workspace is seeded on the first visit and only then: delete it
and it stays deleted.

---

## Libraries

**Libraries…** brings in libraries written elsewhere, so that
`:- use_module(library(clpz)).` works here.

### Importing

Two ways in:

- **Import folder…** — a folder from your computer.
- **Import from URL…** — a GitHub directory address, the one you get by
  navigating there and copying the bar. Scryer's and SWI's are offered, so
  borrowing a library does not mean installing a Prolog system.

What you import is a **collection**, not a library. Scryer's `lib/` is one
folder and about sixty libraries: every `x.pl` in it is `library(x)`.

A collection carries a **dialect** (`scryer`, `swi`, or none). A library
resolved from a collection tagged `scryer` loads under Scryer's name resolution
and `double_quotes`, which is what lets both systems' versions of the same
library sit in one engine — see [ADR-040](../architecture/adr/040-multi-dialect-shims.md).

Libraries are **global**: shared by every workspace, unaffected by switching,
and not part of a workspace's zip or share link.

### Compiling

A library loads several times faster once compiled, because the compiling — the
expensive part — stops happening on every consult. Scryer's clpz was measured at
about six times faster from a bundle than from source.

It runs by itself in the background after an import, one library after another:

- **what your programs import goes first**, read out of the workspace's sources
  (the point is to build them *before* anything asks), and re-aimed after every
  consult and workspace switch — which is when that answer changes;
- each library becomes fast the moment its own build lands; the batch does not
  have to finish to be worth having;
- it **pauses while you consult**, so what a consult resolves against is a
  settled set, and pressing Consult does not mean waiting behind a library
  nobody asked for;
- it can be stopped, and what is built stays built — across reloads too;
- a big collection (SWI's library is about two hundred files) is not compiled
  through unasked: above eighty libraries the batch does what the workspace
  imports and stops. The rest are a button away and still load from source.

Compiling uses the **consult** path (`ShmoViaConsult`), the only one that works
for a library which generates clauses as it loads — which is what clpz and its
attributed-variable machinery do — and packs the result with the **librarian**
rather than the linker, because a library has no entry point to compute
reachability from.

### Sources are sources

A collection is laid out so the rule needs no enforcing:

```
/libraries/scryer/clpz.shum     ← what library(clpz) resolves to
/libraries/scryer/src/*.pl      ← the sources, readable and editable
```

The search path reaches the collection's root before its `src/`, so a compiled
bundle wins over the source beside it. Editing a source therefore does nothing
until **rebuild** — and that is visible in the layout rather than imposed by a
read-only flag somebody has to police. A library that will not compile still
works; it just loads slowly.

---

## Sharing

**Share** puts the code itself in the link, in the fragment — the part of a URL
browsers never send to a server. Sharing a link does not hand anyone's program
to a third party.

It asks what to carry: this file, or the whole workspace (a program that spans
files is not shareable one file at a time). The fragment reads
`#name~payload`: a name a person recognises before the encoded part, and
decoration only — the payload says what it is, so a hand-edited label cannot
mislead the loader.

Opening a link never overwrites anything silently. A file identical to yours is
left alone; a different one of the same name asks (Yes / No / Always / Never,
the last two for the rest of that link). The fragment is cleared afterwards,
which is also what lets the same link work twice: a browser already sitting on a
URL does not navigate to it again.

---

## What the page remembers

| | |
|---|---|
| Files, workspaces, libraries | OPFS (origin-private storage) |
| Theme, active workspace | `localStorage`, under one versioned envelope |

Preferences are the page's, not the user's, so they are not in OPFS: deleting a
workspace must not take the theme with it. The envelope carries a version, and a
stored one of another version is **discarded** rather than half-read — the right
policy while the shape is still moving, and stated in one place (`settings.js`)
rather than discovered later as a page misreading yesterday's data.

A browser that refuses storage is not an error: the session works, it just
forgets, and says so.

---

## Hosting

Static files, with one requirement: the app uses threads (so a long search
cannot freeze the tab), threads need `SharedArrayBuffer`, and that needs the
page to be **cross-origin isolated**. The host must send:

```
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: credentialless
```

`credentialless` rather than `require-corp` so that the library importer can
read GitHub's listing API, which sends CORS but no `Cross-Origin-Resource-Policy`.
Everything else it fetches (`raw.githubusercontent.com`) does send CORP and
would work under either.

This rules out hosts that cannot set headers — GitHub Pages among them. And
`credentialless` is unsupported in Safari today: the app runs there, but
importing a library from a URL does not (importing a folder does).

The app registers a **service worker**, so a second visit starts offline. Files
whose name pins their contents (the runtime, the fingerprinted modules) are
served from the cache; everything else — the stylesheet, the manifest, the
examples — goes to the network first with the cache as fallback, because those
keep their names across publishes and a cached copy can be stale.

---

## Verifying a build

Loading the page with `#selftest` runs an end-to-end check against the deployed
app and prints the results into the transcript: consult, pulling solutions,
failure, a syntax error, engine output, cancellation, the editor's highlighting
over the real DOM, the workspace and Prolog's view of it, workspace separation,
the zip, sharing both shapes, and the settings envelope. It is the only
automatic test that reaches this layer — a browser-wasm project cannot be
referenced by a normal test project.

`#persist=write` then `#persist=check` (two loads, same profile) checks that a
file survives a reload, which one page load cannot.
