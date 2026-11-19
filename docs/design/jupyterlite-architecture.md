# Jupyter notebooks on WebShumway: architecture

Status: design, nothing implemented (2026-11-14). The decisions are recorded
in ADR-066 to ADR-073; this document is the tour that ties them together.

## Goal

Jupyter notebooks for Prolog that run **entirely in the browser**, as a
static site with no backend, in the same spirit as WebShumway (ADR-042).
Opening a URL on a desktop or a tablet is enough. There is no Jupyter server
anywhere; "the server" is static file hosting.

Two variants share one engine:

| Variant | Language | Kernelspec | Owner | When |
|---|---|---|---|---|
| **Shumway Prolog** | Prolog | `shumway_prolog` | Shumway | first, built independently |
| **Hercutalk web** | Logtalk | `logtalk_kernel` | Paulo Moura (Logtalk) | later, the "cousin" |

They are two languages on one engine, not a light and a full edition of the
same thing. Logtalk compiles to Prolog, but it is a different language, and a
Prolog notebook should not have Logtalk in the middle (ADR-066).

**Out of scope:** desktop notebooks. Hercutalk on the desktop already
supports Shumway as a backend (the `shumwaylgt` integration script in Logtalk).

## The shape in one picture

```
 Browser page (JupyterLite, cross-origin isolated by our service worker)
 ┌───────────────────────────────────────────────────────────────────────┐
 │ JupyterLab UI ── kernel messages ──► ShumwayKernel (TS, BaseKernel)   │
 │                                       the kernel extension, its core  │
 │                                         │ handle_request(json) : Task │
 │                                         ▼                             │
 │  .NET runtime (browser-wasm, threads), one per page                   │
 │  ┌─────────────────────────────────────────────────────────────────┐  │
 │  │ Session per kernel (ADR-068)                                    │  │
 │  │  engine thread ─ PrologEngine ─ jupyter server (precompiled)    │  │
 │  │       ▲ request pipe        │ response pipe        user_input ◄─┼──┼─ input_request
 │  │       └──── loop/3 blocks here, goals keep their choice points  │  │
 │  └─────────────────────────────────────────────────────────────────┘  │
 │  MEMFS  /drive  ◄── mirror (before/after each cell) ──► Contents API  │
 └───────────────────────────────────────────────────────────────────────┘
```

## Layers

1. **Engine.** WebShumway's existing browser-wasm runtime with threads
   (ADR-042 §3). Nothing in the engine knows it is in a notebook.
2. **Logical server.** Prolog code running on the engine: a JSON-RPC server
   of our own that speaks Hercutalk's protocol and is tested against
   Hercutalk's responses (ADR-067). It owns cell semantics: what a clause, directive or query
   cell does, `retry`, variable bindings carried across cells, magics, and
   the `jupyter` helper module.
3. **Session host (.NET).** One session per kernel: an engine, its thread,
   in-memory request and response pipes, cancellation, and page input. It is
   the generalisation of `WebShumwayApp`'s static engine (ADR-068).
4. **Kernel (TypeScript).** A JupyterLite `BaseKernel` subclass that maps
   Jupyter messages to JSON-RPC requests and renders responses as standard
   MIME bundles (ADR-067, ADR-072). Little code: the logic is in layer 2.
5. **Site.** A `jupyter lite build` output with JupyterLite's service worker
   disabled and the demo's in charge of it (ADR-069), deployed under `/jupyter`
   on Shumway's Pages.

## The decisive design facts

- **Retry needs a live goal.** Hercutalk implements `retry` by calling its
  request loop *inside* the goal that just succeeded, so the goal's choice
  points stay on the stack. A request that returns cannot keep them. The
  server therefore runs its blocking loop on the session's thread, and the
  JavaScript side still sees one `Task` per request (ADR-067, D5).
- **The runtime lives in the page, not in a Web Worker.** ADR-042 records
  that a JS worker owning the runtime does not boot. The .NET threads
  already keep the UI responsive.
- **JupyterLite's service worker is not needed and is harmful here.** Its
  two jobs serve kernels that run in Web Workers. On a version change it
  unregisters every service worker of the origin, and `gusbro.github.io` is
  shared by all of the owner's Pages projects (ADR-069).
- **The engine's working directory is process-global.**
  `working_directory/2` calls `Directory.SetCurrentDirectory`. Several
  kernels in one runtime would trample each other's directory (ADR-070,
  ADR-071).

## Pieces

See ADR-073 for the reasoning, and for the rule it keeps: a clone builds and
tests Shumway with the .NET SDK alone, and Node and Python are needed only to
build the notebook site.

| # | Piece | Kind | Contents |
|---|---|---|---|
| 1 | Web host library | .NET, in the solution | sessions per instance, boot, cancellation, page input |
| 2 | Notebook server | .NET, in the solution | the JSON-RPC server and what hosts it in a session |
| 3 | The wasm app | .NET browser-wasm, `Shumway.Web` | the demo's exports and the session API, in one app; the server bundle is chosen per session |
| 4 | The runtime's JavaScript | plain modules in the demo's `wwwroot` | isolation check, service worker, runtime loader, MEMFS mirror with pluggable target |
| 5 | The kernel extension | JupyterLab extension, `jupyterlite/` | a server-agnostic core (`BaseKernel`, JSON-RPC client, renderers, completion, inspection, input, interrupt) and the Prolog variant (kernelspec, server bundle, logos, CodeMirror mode) |
| 6 | The site | `jupyter lite build`, `jupyterlite/` | 5, the configuration, example notebooks |
| | Hercutalk web (future) | Moura's | the core of 5, split into a package of its own then, with the Hercutalk and embedded Logtalk bundle |

The demo and the notebook site share 3 and 4: one runtime download, one copy
of the runtime's JavaScript.

## Decision index

| D | Decision | ADR |
|---|---|---|
| D1 | Web only (JupyterLite); desktop is Hercutalk's | 066 |
| D2 | Two variants, two languages, one engine; Prolog first | 066 |
| D3 | Logtalk integration lives outside Shumway; dependencies point one way | 066 |
| D4 | No Python in the core; Pyodide dropped (last resort only) | 072 |
| D5 | Hercutalk's JSON-RPC as the shared contract; **blocking loop inside the session, `Task` per request outside** | 067 |
| D6 | Runtime hosted in the JupyterLite page; reuse `QueryCancel` and `PageInput` | 068 |
| D7 | One engine per kernel, living for the session; configurable limit | 068 |
| D8 | A server of our own, written against the protocol, with Hercutalk as the reference of a differential test | 067 |
| D9 | Helpers in a Prolog module `jupyter` | 067 |
| D10 | Server precompiled into the bundle | 067 |
| D11 | Identity: `shumway_prolog`, standard Prolog `language_info`, site default kernel | 066 |
| D12 | Disable JupyterLite's service worker; WebShumway's serves the two sites, and stops deleting other caches | 069 |
| D13 | `require-corp` in WebShumway; in the notebook site COEP chosen by engine (WebKit → `require-corp`), for the images of markdown cells | 069 |
| D14 | Files: mirror between the Contents API and MEMFS around each cell; no OPFS in the notebook site | 070 |
| D15 | Rich outputs in standard MIME, in phases: tables → Graphviz → Vega-Lite → widgets | 072 |
| D16 | One extension and one wasm app, built apart from the solution; protocol versioned separately | 073 |
| D17 | Feature table as YAML, every row backed by an acceptance cell | 073 |
| | Per-engine working directory as an option of the host | 071 |

## What changes outside the notebook work

These come out of the design but belong to WebShumway or the engine:

- `sw.js` deleted every cache of the origin on activation, not only its own
  (ADR-069). Fixed.
- `sw.js` sent `credentialless` to WebKit, which does not implement it, so
  WebShumway did not boot there (ADR-069, verification V1). Fixed, with
  ADR-042 amended: the worker sends `require-corp` to every browser.
- The working directory is process-global (ADR-071). The desktop
  `EnginePool` has the same latent problem.
- Cancellation surfaces as an OperationCanceledException, so it cannot be
  caught as a Prolog exception, and an interrupt costs the session its state.
  A catchable form is future work and needs its own ADR (ADR-067).
