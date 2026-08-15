# ADR-042: WebShumway — the engine in a browser

## Status

Shipped ([Phase 38](../../history/phase-38-closure.md), 2026-08-07).

`src/Shumway.Web/`, a static site that runs the
full engine (Tier-0), with an editor, workspaces, imported libraries and offline
support. The user-facing guide is [`docs/guide/webshumway.md`](../../guide/webshumway.md).

## Context

Shumway ran as a console REPL, embedded in .NET, and as a native executable. The
cheapest way for someone to *try* it — a web page — did not exist. The goal is
the one SWISH and Scryer's playground serve: type a program, ask a question, get
an answer, with nothing installed.

The constraint the user set at the outset shapes everything: **no backend**. A
static file server, so it can eventually be installed as a progressive web app
and run offline. No CDNs; everything same-origin.

## Decisions

### 1. Target and host

`browser-wasm` through `Microsoft.NET.Sdk.WebAssembly`, driven from hand-written
HTML and JavaScript via `[JSExport]` / `[JSImport]`. **No Blazor** — the app is a
top level and an editor, not a component tree, and Blazor's payload and model
would be paid for nothing.

Native AOT had already forced the engine to be trim-safe and to fold Tier-1 off
when runtime codegen is unavailable, so the browser needed no new engine work of
that kind. It did need the *gate* corrected: `RuntimeFeature.IsDynamicCodeSupported`
is true under Mono-wasm interpretation, so `Shumway.Core.RuntimeCaps` is the
capability test, and it is a `[FeatureSwitchDefinition]` the trimmer folds — the
IL compiler and Sigil leave the payload entirely.

### 2. Tier-0 only, and why that is not a compromise

A browser does not allow runtime code generation, so Tier-1 is off. **Programs
behave identically; only speed differs** — which is the same invariant the
project holds everywhere else (tier changes efficiency, never behaviour), now
enforced by a platform rather than by discipline.

The seam for a future WebAssembly-emitting Tier-1 backend is recorded in the
Phase-38 plan and unchanged by this ADR: the consumer side (`ITier1Dispatcher`)
is already an abstraction over delegates; the producer side is not; and the real
obstacle is that the heap is a **managed** `Cell[]` (ADR-002), which a
runtime-generated wasm module cannot address without either per-cell accessors
(which would eat the gain) or pinning plus a shared `WebAssembly.Memory` (to be
validated). None of that is decided here.

### 3. Threads, and the hosting requirement they impose

The engine's search is synchronous: whatever thread it runs on is blocked until
it answers. On one thread that means a page that stops responding, which is the
one thing a web app may not do.

So `WasmEnableThreads`: the .NET runtime moves off the browser's UI thread, and
the search goes one hop further onto a pool thread. Consequences, all of them
load-bearing:

- **Every `[JSExport]` returns a `Task`.** The runtime rejects synchronous ones
  outright — a synchronous call would block the UI thread waiting for a reply.
- **JavaScript interop is thread-affine.** A `write/1` executed on the pool
  thread cannot touch the page; output is posted to the runtime thread, which
  preserves order because posts on one context run in the order they were made.
- **Engine work is serialized behind one gate.** An activation is single-threaded
  internally; the editor asks for highlighting on every keystroke, which reads
  the live operator table a consult may be mutating. Cancellation stays outside
  the gate: it must reach a running search, and only sets a flag read at a safe
  point.
- **Cross-origin isolation is required**, which means the host must send
  `Cross-Origin-Opener-Policy: same-origin` and a `Cross-Origin-Embedder-Policy`.
  A host that cannot set headers — GitHub Pages — is still viable, because the
  service worker can add them to what the page receives: the browser judges what
  arrives, not who wrote it. The cost is one reload on a first visit, since a
  worker does not control the page that installed it. That in turn forces two
  things: the isolation check must run as a plain script BEFORE the module that
  boots the engine (a module's imports are evaluated before its own body, so the
  runtime was asserting about `SharedArrayBuffer` before any of the app's code
  ran), and the runtime is imported when it is started rather than when the
  module loads. Hosting a JS worker that owns the runtime was tried first and
  does not boot.

`Cross-Origin-Embedder-Policy: credentialless` rather than `require-corp`, so the
library importer can read GitHub's listing API (CORS, no CORP). Cost: Safari does
not support `credentialless`, so URL import is unavailable there.

### 4. Filesystem: MEMFS, mirrored to OPFS

The engine makes ~96 direct `File.`/`Directory.` calls with no abstraction, so a
seam would have been an invasive refactor. It was not needed: under
`browser-wasm`, .NET's `System.IO` lands on Emscripten's MEMFS, so `consult/1`,
`open/4`, `see/tell`, `directory/6` and the library search path work unmodified.

In-memory means gone on reload, so the page mirrors to **OPFS** on the JavaScript
side — origin-private, no prompt, in every current browser. The mirror is
one-directional at each moment (OPFS → memory on load, memory → OPFS on change),
so nothing ever has to be reconciled. Keeping persistence in JavaScript keeps the
engine unaware of the browser: nothing in the engine is a browser concept.

**File System Access** (`showOpenFilePicker` / `showSaveFilePicker` /
`showDirectoryPicker`) opens and saves real files where it exists, with an
`<input type=file>` fallback everywhere else.

### 5. The editor holds ONE copy of the text

The first design drew colours on a `<pre>` behind a transparent `<textarea>`.
That works only while the two lay out identically — same wrapping, same
scrollbar, same font fallback — and when they drift the caret lands where the
text is not. No amount of synchronising the CONTENT fixes a divergence of
LAYOUT.

So the editor is `contenteditable` and holds the coloured spans itself: what is
on screen is what is being edited. A repaint only re-colours text the browser has
already inserted, and skips when the result would be identical. Rewriting the DOM
discards the browser's undo history, so the editor keeps its own.

The colouring comes from the **engine's** lexer and live operator table
(`Shumway.TopLevel.SyntaxHighlighter`), so it cannot drift from how the reader
reads a file — including operators the consulted program declared itself.

### 6. Shared top-level logic

`src/Shumway.TopLevel/` holds what a top level is, independent of I/O:
`TopLevelSession`, `QueryRun`, `SolutionFormatter` (bindings and residual
constraints), `QueryWrapper`, `PredicateCompletion`, `ErrorRendering`,
`SyntaxHighlighter`. Both the console REPL and the page drive it, so an answer is
formatted once and the two cannot diverge.

### 7. Workspaces, and libraries as collections

A **workspace** is a directory and the engine's working directory, so relative
paths in Prolog mean what they say. Switching starts a fresh engine: a workspace
is a separate program.

A **library collection** is a directory on the engine's library search path
(ADR-038), optionally tagged with a dialect (ADR-040). Nothing in the engine
changed to support importing one: `AddLibraryDirectory(path, dialect)` was
already public, idempotent, and inherited by subdirectories.

Two decisions inside this are worth recording:

- **A collection is not a library.** Scryer's `lib/` is one folder and sixty
  libraries; every `x.pl` in it is `library(x)`. Compilation is therefore per
  library, and a large collection is not compiled through unasked.
- **Compiled beats source by LAYOUT, not by permission.** The bundle sits at the
  collection's root, which the search path reaches before its `src/`. Editing a
  source does nothing until a rebuild — a rule that is visible in the structure
  rather than enforced by a read-only flag. The alternative considered (a
  per-directory "prefer compiled" flag in the engine, plus read-only sources)
  was rejected as more machinery for the same result.

Compiling runs **outside the engine gate**: it builds its own ephemeral engine
and never touches the session's, so holding the gate bought nothing and made
Consult wait minutes for a big library. Two engines at once is the threading
model working as designed. The bundle is written under a temporary name and moved
into place, since a consult may now look while one is being written.

### 8. Sharing without a server

A program travels in the URL **fragment**, which browsers never send anywhere —
the property that makes sharing acceptable at all. Deflate, not Brotli:
browser-wasm has no Brotli codec, which is the same limitation that makes
`BundleFormat.DisableCompression` necessary for anything the page writes.

### 9. Versioned settings

Everything the page remembers as a preference lives in one `localStorage`
envelope carrying a version. A stored envelope of another version is **discarded**
rather than half-read. That is the right policy while the shape is still moving,
and the point is that the decision lives in one place instead of being discovered
later as a page misreading yesterday's data.

## Engine changes this forced

The browser found real engine defects, none of them browser-specific:

- `Console.In` **throws** under browser-wasm rather than returning an exhausted
  reader, which killed the first query of every program. `StreamRegistry` falls
  back to an empty reader.
- `PrologEngine.In` did not exist. `Out` had no counterpart, so `read/1` always
  came from the host's stdin — and a host without one answered `end_of_file`
  having asked nobody. This was a gap in the embedding API, not in the page.
- `PrologEngine.Warnings` did not exist. A `use_module` that finds nothing warns
  and continues, to standard error — which a browser does not have, so a program
  loaded without part of itself and said nothing.
- `MetaBuiltins.CollectAttvars` recursed once per list element, so `copy_term/3`
  — which the top level runs on every answer — overflowed the stack on a
  thousand-element list in a browser (and two hundred thousand on the desktop).
- `use_module(library(X))` resolving to a **bundle** loaded the module but left
  its predicates unresolvable: the importer's table is built from "the module
  this consult declared", which a bundle never sets, and a module's exports are
  matched against the clauses it defines, which a bundle has none of.

## Consequences

- Shumway can be tried with nothing installed, and used offline once visited.
- The hosting requirement (COOP/COEP headers) is real and narrows where it can be
  put. It is the price of a page that does not freeze.
- The browser is now a **second real deployment target** for the engine, which
  means trim-safety, the absence of runtime codegen and the absence of a console
  are properties that have to keep holding.
- `#selftest` is the only automatic test that reaches this layer; a browser-wasm
  project cannot be referenced by a normal test project. Everything below it —
  `Shumway.TopLevel` included — is covered by the ordinary xUnit gate.
