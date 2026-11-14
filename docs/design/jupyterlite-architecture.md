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
 │                                       @shumway/jupyter-kernel-core    │
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
2. **Logical server.** Prolog code running on the engine: the Hercutalk
   JSON-RPC server, translated from Logtalk objects to Prolog modules
   (ADR-067). It owns cell semantics: what a clause, directive or query
   cell does, `retry`, variable bindings carried across cells, magics, and
   the `jupyter` helper module.
3. **Session host (.NET).** One session per kernel: an engine, its thread,
   in-memory request and response pipes, cancellation, and page input. It is
   the generalisation of `WebShumwayApp`'s static engine (ADR-068).
4. **Kernel (TypeScript).** A JupyterLite `BaseKernel` subclass that maps
   Jupyter messages to JSON-RPC requests and renders responses as standard
   MIME bundles (ADR-067, ADR-072). Little code: the logic is in layer 2.
5. **Site.** A `jupyter lite build` output with our service worker in place
   of JupyterLite's (ADR-069), deployed under `/lite` on Shumway's Pages.

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

## Packages

See ADR-073 for the reasoning behind this split.

| # | Package | Kind | Contents |
|---|---|---|---|
| 1 | Shumway web host library | .NET | sessions per instance, boot, cancellation, page input; extracted from `Shumway.Web` |
| 2 | Shumway Jupyter wasm app | .NET browser-wasm | `[JSExport]` session API; the server bundle is chosen per session |
| 3 | `@shumway/web-runtime` | npm | isolation check, service worker, runtime loader, MEMFS mirror with pluggable target |
| 4 | `@shumway/jupyter-kernel-core` | npm | generic `BaseKernel`, JSON-RPC client, renderers, completion, inspection, input, interrupt |
| 5 | `jupyterlite-shumway-prolog` | Python wrapping a labextension | kernelspec, Prolog server bundle, logos, CodeMirror mode |
| | `jupyterlite-logtalk` (future) | Moura's | depends on 3 and 4, adds the Hercutalk + embedded Logtalk bundle |

The WebShumway demo becomes a consumer of 1 and 3 with a single session.

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
| D8 | Hybrid server: translate Hercutalk's semantic modules **and its request loop**; write the transport and output capture | 067 |
| D9 | Helpers in a Prolog module `jupyter` | 067 |
| D10 | Server precompiled into the bundle | 067 |
| D11 | Identity: `shumway_prolog`, standard Prolog `language_info`, site default kernel | 066 |
| D12 | Disable JupyterLite's service worker; our own, derived from WebShumway's; WebShumway's stops deleting other caches | 069 |
| D13 | `require-corp` in WebShumway; in the lite site COEP chosen by engine (WebKit → `require-corp`), for the images of markdown cells | 069 |
| D14 | Files: mirror between the Contents API and MEMFS around each cell; no OPFS in the lite site | 070 |
| D15 | Rich outputs in standard MIME, in phases: tables → Graphviz → Vega-Lite → widgets | 072 |
| D16 | Package layout above, protocol versioned separately | 073 |
| D17 | Feature table as YAML, every row backed by an acceptance cell | 073 |
| | Per-engine working directory as an engine option (**Proposed**) | 071 |

## What changes outside the notebook work

These come out of the design but belong to WebShumway or the engine:

- `sw.js` deletes every cache of the origin on activation, not only its own
  (ADR-069).
- `sw.js` sends `credentialless` to WebKit, which does not implement it, so
  WebShumway does not boot there (ADR-069, verification V1). The fix and the
  amendment of ADR-042 are a pull request of their own.
- The working directory is process-global (ADR-071). The desktop
  `EnginePool` has the same latent problem.
- Cancellation surfaces as an OperationCanceledException, so it cannot be
  caught as a Prolog exception, and an interrupt costs the session its state.
  A catchable form is future work and needs its own ADR (ADR-067).
