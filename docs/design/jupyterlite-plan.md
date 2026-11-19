# Jupyter notebooks on WebShumway: plan

Status: Phase 0 done; Phase 1 done (2026-11-15); Phase 2 done (2026-11-16);
Phase 3 started, with the server running in a page. The architecture is in
[`jupyterlite-architecture.md`](jupyterlite-architecture.md); the decisions
are in ADR-066 to ADR-073.

Each phase ends with its acceptance criteria met, and with
`jupyterlite-features.yaml` and the reference docs updated to say what is now
true, as the Documentation invariant requires.

## Phase 0: Verifications and independent fixes

| Id | Task | Done when |
|---|---|---|
| V1 | Open the current WebShumway demo on an iPhone (Safari). Check `crossOriginIsolated`, and whether the engine boots | result recorded in ADR-069; if it fails, F2 is scheduled and ADR-042 is amended |
| V2 | Measure the memory of one engine with a server-sized program loaded, on desktop browsers and on a tablet (a phone's screen is too small for a notebook, so it is not a target) | default session limit written into ADR-068 (can wait until Phase 3 provides the real bundle; a proxy program is fine for a first figure) |
| F1 | `sw.js`: on `activate`, delete only caches carrying WebShumway's prefix | demo still works offline; another app's caches on the origin survive |
| F2 | `sw.js`: `require-corp` for every browser (WebShumway needs nothing `credentialless` gives) | demo boots in Chromium, Firefox and WebKit; ADR-042 amended |

V1, F1 and F2 are done. In headless WebKit 27.2 the demo did not boot, and
`require-corp` isolates where `credentialless` does not (ADR-069). F1 and F2
went into WebShumway as a fix of their own, and with it published the demo
boots on a real iPhone. V2 is measured in three desktop browsers and on a
tablet (ADR-068): with `library(clpfd)` loaded, a session took about 38 MB of
the runtime's memory before the sessions of a page shared their wasm modules,
and on the tablet six sessions take the runtime from 138 MB to 286 MB. The
default limit was set in Phase 3, with the server loaded in a page: ten
(ADR-068 D7).

## Phase 1: Host sessions (ADR-068, accepted)

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

**State: done (2026-11-15).** See ADR-068 "What was built". The page's own
test passes as on `main`, and the two-session test passes on the desktop and,
in the browser, in Chromium, Firefox and WebKit (Firefox once the service
worker stopped waiting for its claim, a fix of WebShumway's own). The
sessions of a page share their wasm modules (ADR-068, "What a session is made
of"). The manual pass over the guide's examples found two things:
- `read/1` answered `end_of_file` without asking, from before the sessions:
  the page's boot ran a query before the session gave the engine its input.
  Fixed in the engine (PR #247): the host's input takes effect whenever it is
  set.
- Once, in Chrome, after the first compile of Scryer's `clpz` and before a
  reload, `X #> 3, X #< 6.` answered `true` without its residual constraint;
  after a reload it showed. Not reproduced in headless Chromium; open.

## Phase 2: The Prolog server, headless (ADR-067)

- Build the differential harness first: the same JSON-RPC lines to Hercutalk
  on desktop Shumway and to our server, and the responses compared. The
  corpus grows with the server.
- Write the server as Prolog modules of our own (ADR-067 D8): cell semantics,
  the request loop with `retry`, a query run with its output captured,
  bindings carried across cells, preferences, the helper module `jupyter` and
  the JSON-RPC messages. Hercutalk's code is read as the specification of
  each; none of it is translated or copied.
- Write the transport (request and response streams, the `jupyter_requests`
  alias), output capture to memory, logging, boot, and JSON reading and
  writing.
- Add the `protocol` method **[ext]**.
- Apply the D8 exit condition once the first third of the corpus passes.

**State: done (2026-11-16).** The server is `src/Shumway.Jupyter` (PR #248);
the engine changes it needed went in pull requests of their own (#249 to
#252).
- Done: the harness and a corpus of 21 sessions; the request loop with
  `retry`, `$Var` and `halt`; output capture; the file, table and line
  magics; the helpers behind them; the predicate docs; the `protocol`
  method; residual goals (O2); `trace/1`; the names for completion; the
  message for a clause in an ordinary cell (O1).
- The D8 exit condition did not apply: the server answers the whole corpus,
  and what differs is listed in the protocol document, §9.
- Moved to Phase 3: the precompiled bundle (D10). What it saves is the
  compilation of the server when a notebook opens, which is measured in a
  browser. So are the default session limit of Phase 0, which needs the
  server loaded in a browser, and `jupyter:trace/1` on the wasm tier, which
  was not run.
- The corpus was recorded again with Hercutalk 0.47.1, which fixed three of
  the differences found with 0.47.0 after they were reported.

**Acceptance: differential tests against Hercutalk on the desktop.** Logtalk
already runs on Shumway (`shumwaylgt`), so the reference is Hercutalk 0.47.0
on desktop Shumway. Build a corpus of cells covering:
- queries;
- `retry` chains;
- `$Var`;
- errors;
- magics;
- `print_table`.

Feed the same JSON-RPC lines to our server through the desktop harness, and
compare the responses with Hercutalk's. Differences must be
either fixed or documented as intended, for example module-qualified names.

## Phase 3: MVP in the browser

The layout is ADR-073's: one wasm app, one extension, and a build apart
from the solution.

- **.NET:** the demo's wasm app exports the session API, with the notebook
  server in it. **Done (2026-11-16):** the page's `#notebook` hook drives
  sessions as a kernel will, and passes in headless Chromium, Firefox and
  WebKit, `jupyter:trace/1` over the wasm tier among its checks.
- The default session limit that Phase 0 left open is ten (ADR-068 D7).
- The precompiled bundle of the server (ADR-067 D10) is not built: loading
  the server from source costs about 0.3 s once for a page.
- **The runtime's JavaScript**, shared with the demo from where it is.
  **Done (2026-11-17)**, each part checked in the three browser engines
  with the two sites served as Pages serves them:
  - `isolate.js`, told where the worker is;
  - the service worker, one for the two sites, with the COEP value by
    engine for the notebook site (ADR-069, amended);
  - the loader, which starts the runtime with no engine of the page's own
    and finds it through `runtime.json`;
  - the mirror with the Contents API target (ADR-070), run against
    JupyterLite's own contents manager.
- **The kernel extension, its core. Done (2026-11-17)**, checked in a
  JupyterLab page of the built site in the three browser engines
  (`jupyterlite/tests/kernel.mjs`):
  - the `BaseKernel` subclass, calling `SessionRequest`;
  - response mapping;
  - Phase-1 outputs (ADR-072): text, bindings, errors, tables, `display_data`
    **[ext]**, `residual_goals` **[ext]**;
  - completion from `predicate_atoms`;
  - inspection from `jupyter_predicate_docs`;
  - input;
  - interrupt as restart, with a notice;
  - session limit, which the site may set; a refused kernel says how to
    free a place (`jupyterlite/tests/limit.mjs`).
- **The kernel extension, the Prolog variant. Done:** the kernelspec
  (ADR-066 D11), the bundle, a logo and a CodeMirror mode of our own, which
  a `.pl` file gets too.
- **The rule's test. Done:** no build file of the repository refers to
  `jupyterlite/` or runs a Node or Python command
  (`BuildNeedsOnlyDotNetTests`).
- **Site:**
  - `jupyter lite build`, with JupyterLite's service worker disabled and
    `shumway_prolog` as the default kernel: **done**, by
    `jupyterlite/build.mjs`, which also puts the runtime's JavaScript and
    the isolation script in the site;
  - example notebooks;
  - deployed under `/jupyter`.
- **Working directory:** each session starts in its notebook's directory,
  through ADR-071, which must be implemented by then.

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

The servers write the DOT the reference writes; the kernel draws it with
dagre, loaded on first use (ADR-072, amendment): `show_term/1`, `%%tree`,
`print_transition_graph/3,4` and `show_graph/2`.

`print_sld_tree/1` is left out for now (decided 2026-11-19). The reference
does not export it, and what it drew was a tree of the calls its tracer saw.
A tree of resolvents, clauses and substitutions, as textbooks draw one,
would need a meta-interpreter in the server, and one that is not simple: it
would have to show coroutining (`freeze/2`, `dif/2`, constraints), whose
goals run when a variable is bound, not where they are written.

**Acceptance:** the rows in the feature table pass, and notebooks that draw
no graphs do not download the drawer.

## Phase 5: Plots with Vega-Lite

- `show_data` sends the reference's description; the kernel builds the
  Vega-Lite spec (ADR-072, second amendment), and JupyterLab's renderer
  draws it.
- Mapping of the common options, with unknown options reported.
- Polar and hexbin stay `not_yet_supported`.
- A static SVG fallback for GitHub is optional.

**Acceptance:** one feature-table row per plot type, each with a test.

## Phase 6: Widgets (depends on Hercutalk)

Configurable callback in `jupyter_widgets`, `jupyter_forms` and
`jupyter_inputs`. These files are Apache-2.0 and keep that licence.

## Engine track, in parallel

- **ADR-071**, working directory per engine: accepted and implemented
  (PR #241), a prerequisite of Phase 3.
- **Future ADR:** a cancellation that Prolog can catch, so that an interrupt
  keeps the session's state.

## Open decisions

| Id | Question | Leaning |
|---|---|---|
| O1 | Prolog variant: accept a cell containing only clauses directly, as hhu-stups' kernel does, as an implicit `%%user+` **[ext]**? Hercutalk enters code through magics only | **Decided (2026-11-16): no.** A fact cannot be told from a query, so a mixed cell would load its rules and run its facts. Code enters through magics, and a term that can only be a clause is an error that points to `%%user` |
| O2 | `residual_goals` final shape | **Decided (2026-11-16):** a list of goals as text, as the top level shows them (protocol, §4). Proposed to Hercutalk; if it does not take it, the Logtalk variant shows no constraints |
| O3 | Where imported libraries live in the notebook site (`/drive/lib` or the bundle) | **Decided (2026-11-19):** neither. In the demo's OPFS, shared with it: the page restores them, brings in those another tab stored before each cell, and `jupyter:import_library/1` imports one from a cell (ADR-070, amendment) |

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
9. Courtesy note: the Prolog variant's server is written against the
   protocol and tested against Hercutalk's responses, and carries none of
   its code.
10. What the differential test found in Hercutalk's answers (protocol, §9):
    a variable that is another one shown as a quoted atom, the empty list
    sent as a JSON array, and a line that is not JSON ending the server.
