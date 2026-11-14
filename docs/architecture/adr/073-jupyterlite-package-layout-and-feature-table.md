# ADR-073: Package layout, protocol versioning and the feature table

## Status

Proposed (2026-11-14). Design only; nothing is implemented. Records D16 and
D17.

## Context

The Prolog variant comes first, and Hercutalk web must be able to reuse
everything that is not Prolog-specific (ADR-066 D2). The runtime is shared
by the demo and by notebooks. The server is chosen per session (ADR-068).

## Decision

### D16: Layers and packages

| # | Package | Kind | Contents | Consumers |
|---|---|---|---|---|
| 1 | Web host library | .NET | sessions per instance (ADR-068), boot, cancellation, page input; extracted from `Shumway.Web` | demo, 2 |
| 2 | Jupyter wasm app | .NET browser-wasm | `[JSExport]` session API; **the server bundle is a parameter of session creation**, so one runtime serves Prolog and Logtalk | 5, Hercutalk web |
| 3 | `@shumway/web-runtime` | npm | `isolate.js`, the service worker with COEP per engine (ADR-069), runtime loader, MEMFS mirror with pluggable target (ADR-070) | demo, 4 |
| 4 | `@shumway/jupyter-kernel-core` | npm | server-agnostic `BaseKernel` subclass: JSON-RPC client, response → Jupyter message mapping, renderers (tables, Graphviz, Vega-Lite, `display`), completion, inspection, input, interrupt | 5, Hercutalk web |
| 5 | `jupyterlite-shumway-prolog` | Python package wrapping a prebuilt labextension | kernelspec (ADR-066 D11), Prolog server bundle, logos, CodeMirror mode | the site |

- **Where it lives.** Pieces 1–5 are in Shumway's monorepo. Pieces 3 and 4
  are published to npm.
- **The site.** It is a `jupyter lite build` of 5, with
  `disabledExtensions` set (ADR-069), `shumway_prolog` as the default kernel,
  and example notebooks. It deploys under `/lite` on Shumway's Pages.
- **Package 4 must not know whether the server is Prolog or Logtalk.**
  Anything variant-specific is a parameter of the variant package.
- **The protocol has its own version, independent of package versions.**
  [`jupyterlite-protocol.md`](../../design/jupyterlite-protocol.md) is the
  contract with Hercutalk. Each side declares the protocol version it speaks,
  and the kernel checks it at session start.
- **JupyterLite and JupyterLab versions** are pinned in package 5 and moved
  deliberately. `BaseKernel` moved from `@jupyterlite/kernel` to
  `@jupyterlite/services` across versions, which is an example of what
  breaks.

### D17: Feature table

- **Source of truth:**
  [`jupyterlite-features.yaml`](../../design/jupyterlite-features.yaml). A
  markdown table is generated from it, never edited by hand. Moura can reuse
  the schema.
- **Columns:**
  - `hercutalk_desktop`, the reference, with its version;
  - `shumway_prolog_web`;
  - `hercutalk_web`, added when it exists.
- **Statuses:** `supported`, `partial` (note required), `not_yet_supported`,
  `not_applicable`, `wont_support` (reason required).
- **Every row has an example cell that is also a test.** The examples form an
  acceptance notebook that CI runs against the built site in a headless
  browser (Playwright). A row moves to `supported` only when its test
  passes. The notebook doubles as living documentation.
- **Everything starts as `not_yet_supported`.** `set_prolog_backend` starts
  as `not_applicable`.

## Consequences

- Hercutalk web is a variant package plus a bundle, with no fork of the
  kernel core.
- The demo and notebooks share the runtime code, so a fix lands once.
- The feature table cannot silently drift from reality, because CI ties it
  to tests.

## Alternatives considered

- **One kernel package per variant, with duplicated core.** A few hundred
  lines duplicated in exchange for no shared ownership. Rejected; package 4
  is small, and Shumway owning it is acceptable.
- **A separate repository for the notebook site.** Adds release coordination
  with no gain while the owner is the same. Rejected for now.

## Related

ADR-066 to ADR-072.
