# ADR-072: Rich outputs, and no Python in the kernel

## Status

Proposed (2026-11-14). Design only; nothing is implemented. Records D4
(revised) and D15.

Accepted in part (2026-11-16): the residual constraints in a query's answer,
below. The rest stays proposed.

## Context

Hercutalk's Python half (about 2,400 lines) does five things:

1. Jupyter plumbing (`ipykernel`, ZMQ). Replaced by our TypeScript kernel.
2. Starting the backend with `subprocess.Popen` and speaking JSON-RPC over
   its stdio. Replaced by the session (ADR-067, ADR-068).
3. Completion and inspection token splitting with IPython utilities. Small.
4. Rendering additional data from the server:
   - tables as markdown, plus CSV/TSV files;
   - graphs by running the **native `dot` binary**;
   - plots through **matplotlib**;
5. Widgets and forms: the server emits HTML whose JavaScript `fetch`es
   `http://IP:port`, an **HTTP server** the Python kernel runs in a thread.

**Pyodide** (CPython in WebAssembly) would not remove the porting work: it
provides no subprocess, no HTTP server, no `dot` and no ZMQ. All it would add
for free is matplotlib, at a cost of tens of MB (Pyodide, numpy, matplotlib)
and a second WebAssembly runtime in the page.

`show_data` covers 17 plot types: plot, scatter, bar, barh, hist, step,
stem, stackplot, errorbar, boxplot, pie, polar, hexbin, eventplot, ecdf,
semilogx/semilogy and loglog. Their options are passed almost straight
through as matplotlib keyword arguments, so full fidelity means matplotlib.

**JupyterLite already ships a Vega-Lite renderer.** Its `lab` and
`notebooks` apps include `@jupyterlab/vega5-extension`, which renders
`application/vnd.vegalite.v5+json` interactively.

## Decision

### D4: No Python

The kernel core is TypeScript. Pyodide is out of the plan and kept only as a
last resort, should real need for matplotlib fidelity appear.

### D15: Standard MIME, in phases

Every output is emitted as a **standard MIME bundle**: `text/markdown`,
`image/svg+xml`, `text/html` or Vega-Lite, always alongside `text/plain`, as
Hercutalk does. A saved notebook then reads correctly on GitHub and nbviewer
without our extension.

| Phase | Server key(s) | Rendering |
|---|---|---|
| 1 | output, bindings, errors, `print_table`, `print_and_save_table`, `predicate_atoms` | TS; markdown tables. **The server writes the saved file itself**, and ADR-070's mirror makes it appear |
| 2 | `print_sld_tree`, `print_transition_graph` (DOT) | Graphviz compiled to WASM (for example `@hpcc-js/wasm`), **loaded on first use**; emit SVG plus the DOT as `text/plain` |
| 3 | `show_data` | **Vega-Lite**: the spec is **produced by the Prolog/Logtalk server**, and the TS only emits it |
| 4 | `input_html` (widgets, forms, inputs) | needs Hercutalk to make the callback configurable (below) |

**Phase 3 details:**

- **Coverage.** Direct: plot, scatter, bar, barh, hist, step, stackplot,
  errorbar, boxplot, pie, ecdf (via transforms) and log scales. Some work:
  stem, eventplot. Not reasonable: polar, hexbin, which stay `Not yet
  supported`.
- **Options.** A common subset of matplotlib options is mapped (labels,
  colour, line style, marker, alpha, titles, axes, limits, legend). An option
  that is not recognised is reported in the cell output, never dropped
  silently.
- **Static fallback.** GitHub does not render Vega-Lite, so a static SVG
  generated with the Vega already loaded by JupyterLab is a later step.
- **Proposal for Hercutalk.** Desktop JupyterLab renders Vega-Lite natively,
  so the same server-side spec would let Hercutalk on the desktop drop
  matplotlib too.

**Phase 4, widgets.** Today the server writes `fetch('http://IP:port')`
into the HTML. The proposal to Hercutalk: `jupyter_widgets` emits a call to
a configurable callback, HTTP on the desktop and a JavaScript function
exposed by the kernel extension on the web. JupyterLab renders HTML outputs
in the page, and outputs produced in the session are trusted, so their
scripts run. Those files are Apache-2.0 (ADR-067).

**Extensions in Shumway, offered upstream:**

- **`jupyter:display(+Mime, +Data)`** emits a raw `display_data`: SVG, HTML
  or markdown built in Prolog, for example to draw CLP solutions. It is a new
  additional-data key, marked as an extension in the protocol.
- **Residual constraints in a query's answer (accepted 2026-11-16).**
  Hercutalk shows a `clpfd` variable as `X in Dom` (a `dom` entry in the
  binding) on the backends where it knows how to ask for the domain. On
  Shumway it shows nothing: `X in 1..5.` answers with no bindings. For any
  attributed variable (`clpfd`, `dif`, `freeze`), the Prolog server sends the
  residual goals, as Shumway's top level shows them, through `copy_term/3`.
  - They travel under a key of their own, `residual_goals`: a list of goals
    as text, written with the query's variable names (protocol, §4).
  - The key is proposed to Hercutalk. Until it takes it, the Logtalk variant
    shows no constraints on Shumway.

**Principle.** Whatever can move from the kernel into the server (formatting
tables, building SVG or specs, writing files) moves there. It then serves
the desktop and the web alike, and shrinks what each frontend has to
implement.

## Consequences

- There is no Python anywhere, and the first visit downloads nothing for
  plots.
- Plots look different from Hercutalk on the desktop unless Hercutalk adopts
  Vega-Lite.
- Graphviz costs a download only for notebooks that draw graphs.
- Widgets wait for an upstream change.

## Alternatives considered

- **Pyodide on demand for plots.** Full fidelity, but tens of MB and a
  second runtime. Rejected (D4).
- **Plotly.js.** Broader coverage (polar, 2D histograms) but a much larger
  bundle, and JupyterLite does not ship its renderer. Rejected for now.
- **Observable Plot, or another JS library emitting SVG.** Viable, but adds
  a dependency, whereas Vega-Lite is already in the page. Rejected.
- **Intercepting the widgets' `fetch` in the service worker.** Works without
  upstream changes, but is fragile. Rejected.

## Related

ADR-067, ADR-069, ADR-070, ADR-073.
