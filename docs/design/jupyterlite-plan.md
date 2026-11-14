# Jupyter notebooks on WebShumway: plan

Status: plan, nothing started (2026-11-14). The architecture is in
[`jupyterlite-architecture.md`](jupyterlite-architecture.md); the decisions
are in ADR-066 to ADR-073.

Each phase ends with its acceptance criteria met, and with
`jupyterlite-features.yaml` and the reference docs updated to say what is now
true, as the Documentation invariant requires.

## Phase 0: Verifications and independent fixes

| Id | Task | Done when |
|---|---|---|
| V1 | Open the current WebShumway demo on an iPhone (Safari). Check `crossOriginIsolated`, and whether the engine boots | result recorded in ADR-069; if it fails, F2 is scheduled and ADR-042 is amended |
| V2 | Measure the memory of one engine with a server-sized program loaded, on desktop Chrome and on the iPhone | default session limit written into ADR-068 (can wait until Phase 3 provides the real bundle; a proxy program is fine for a first figure) |
| F1 | `sw.js`: on `activate`, delete only caches carrying WebShumway's prefix | demo still works offline; another app's caches on the origin survive |
| F2 | (if V1 fails) `sw.js`: COEP per engine, `require-corp` on WebKit that is not Chromium, with detection that also matches iPadOS in desktop mode and Mac Safari | demo boots on the iPhone; ADR-042 amended |

V1 has a first result, recorded in ADR-069: in headless WebKit 27.2 the demo
does not boot, and `require-corp` isolates where `credentialless` does not. A
real device is still to be tried. F2 is therefore scheduled.

## Phase 1: Host sessions (ADR-068; must be accepted first)

- Extract the host library from `Shumway.Web`, turning the static engine,
  engine thread, cancellation and page input into a session per instance,
  keyed by id.
- Audit what else is static or global, and record it in ADR-068.
- Rewrite the WebShumway demo as a host with exactly one session.

**Acceptance:**
- The demo behaves exactly as before (existing tests, plus a manual pass
  over the guide's examples).
- A test creates two sessions in one runtime and shows that their databases,
  flags and operators are isolated.
- Cancelling one session leaves the other untouched.

## Phase 2: The Prolog server, headless (ADR-067)

- Translate `jupyter_term_handling`, `jupyter_request_handling` (with
  `loop/3`), `jupyter_query_handling`, `jupyter_variable_bindings`,
  `jupyter_preferences`, `jupyter` and the message layer of
  `jupyter_jsonrpc` into Prolog modules, keeping names and structure. Each
  file keeps its MIT notice and records the Hercutalk version.
- Write the transport (request and response streams, the `jupyter_requests`
  alias), output capture to memory, logging, boot, and the JSON bridge
  producing Hercutalk's term shape.
- Add the `protocol` method **[ext]**.
- Precompile the server into a bundle (D10).
- Apply the D8 exit condition after the first third of `term_handling`.

**Acceptance: differential tests against Hercutalk on the desktop.** Logtalk
already runs on Shumway (`shumwaylgt`), so the reference is Hercutalk 0.47.0
on desktop Shumway. Build a corpus of cells covering:
- queries;
- `retry` chains;
- `$Var`;
- errors;
- magics;
- `print_table`.

Feed the same JSON-RPC lines to the translated server through a desktop
harness, and compare the responses with Hercutalk's. Differences must be
either fixed or documented as intended, for example module-qualified names.

## Phase 3: MVP in the browser

- **.NET:** the Jupyter wasm app, exposing the session API.
- **`@shumway/web-runtime`:**
  - `isolate.js`;
  - the service worker (ADR-069);
  - the loader;
  - the mirror with the Contents API target (ADR-070).
- **`@shumway/jupyter-kernel-core`:**
  - the `BaseKernel` subclass, calling `SessionRequest`;
  - response mapping;
  - Phase-1 outputs (ADR-072): text, bindings, errors, tables, `display_data`
    **[ext]**, `residual_goals` **[ext]**;
  - completion from `predicate_atoms`;
  - inspection from `jupyter_predicate_docs`;
  - input;
  - interrupt as restart, with a notice;
  - session limit.
- **`jupyterlite-shumway-prolog`:** the kernelspec (ADR-066 D11), the
  bundle, logos and CodeMirror mode.
- **Site:**
  - `jupyter lite build`, with JupyterLite's service worker disabled and
    `shumway_prolog` as the default kernel;
  - example notebooks;
  - deployed under `/lite`.
- **Working directory:** each session starts in its notebook's directory,
  using ADR-071 if it is accepted and implemented by then, and otherwise the
  interim global gate (ADR-070).

**Acceptance:**
- **Acceptance notebook (ADR-073 D17).** It runs green in CI with Playwright
  against the built site, on Chromium and Firefox. WebKit joins once V1 and
  F2 are resolved.
- **Files.** A `.pl` edited in the JupyterLite editor is seen by `consult/1`
  in the next cell, and a file written by Prolog appears in the file browser.
- **Two notebooks** in different directories run correctly side by side.
- **A notebook from hhu-stups' kernel** opens with `shumway_prolog` without
  prompting.

## Phase 4: Graphs

Graphviz compiled to WASM, loaded on first use, for `print_sld_tree` and
`print_transition_graph`.

**Acceptance:** the rows in the feature table pass, and notebooks that draw
no graphs do not download Graphviz.

## Phase 5: Plots with Vega-Lite

- `show_data` → Vega-Lite spec, generated in the server.
- Mapping of the common options, with unknown options reported.
- Polar and hexbin stay `not_yet_supported`.
- A static SVG fallback for GitHub is optional.

**Acceptance:** one feature-table row per plot type, each with a test.

## Phase 6: Widgets (depends on Hercutalk)

Configurable callback in `jupyter_widgets`, `jupyter_forms` and
`jupyter_inputs`. These files are Apache-2.0 and keep that licence.

## Engine track, in parallel

- **ADR-071**, working directory per engine: do the analysis, then accept or
  reject it.
- **Future ADR:** a cancellation that Prolog can catch, so that an interrupt
  keeps the session's state.

## Open decisions

| Id | Question | Leaning |
|---|---|---|
| O1 | Prolog variant: accept a cell containing only clauses directly, as hhu-stups' kernel does, as an implicit `%%user+` **[ext]**? Hercutalk enters code through magics only | decide in Phase 2, after seeing how the translated `term_handling` handles it |
| O2 | `residual_goals` final shape | agree with Hercutalk |
| O3 | Where imported libraries live in the lite site (`/drive/lib` or the bundle) | decide in Phase 3 from WebShumway's import code |

## Coordination with Paulo Moura (Hercutalk, Logtalk)

To raise early, because several items change his code or depend on it:

1. Protocol document and versioning, and the `protocol` method.
2. Reading requests from a dedicated stream instead of `current_input`
   (ADR-067).
3. The outdated "clause definitions" comment in `jupyter_term_handling` (or
   the code that is missing, if the comment is the intent).
4. Vega-Lite generated in the server for `show_data` (ADR-072), useful on
   the desktop too.
5. A configurable callback for widgets (ADR-072).
6. The `jupyter:display/2` and `residual_goals` extensions.
7. The feature table schema (ADR-073).
8. How Hercutalk web is distributed, and where it lives (ADR-066 D3).
9. Courtesy note: the translated server files keep the MIT notices and pin
   the Hercutalk version.
