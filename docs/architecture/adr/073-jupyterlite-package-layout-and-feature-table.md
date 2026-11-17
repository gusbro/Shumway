# ADR-073: What the notebook site adds to the build, protocol versioning and the feature table

## Status

Accepted (2026-11-16), as revised that day after measuring what a notebook
site costs to build: the first version (2026-11-14) asked for five packages,
two of them published to npm and one consumed by the demo. Records D16 and
D17. Nothing is implemented.

## Context

The Prolog variant comes first, and Hercutalk web must be able to reuse
everything that is not Prolog-specific (ADR-066 D2). The runtime is shared
by the demo and by notebooks. The server is chosen per session (ADR-068).

**What the repository needs today.** The .NET SDK and nothing else. No
project file calls Node or Python. The demo's front end is plain JavaScript
files under `src/Shumway.Web/wwwroot`, and `dotnet publish` builds the whole
site. `Shumway.Web` is not in `Shumway.slnx`: only who builds the demo needs
the `wasm-tools` workload. Two workflows run: the three .NET lanes, and the
demo's, which runs when `src/**` changes, takes about five minutes and
deploys the repository's Pages site.

**What a notebook site cannot do without.** A JupyterLite kernel is a
JupyterLab extension, and JupyterLab's builder compiles an extension with
Node. The site is assembled by `jupyter lite build`, a Python tool. Both
enter whatever the layout.

**Measured (JupyterLite 0.8.7, Windows, 2026-11-16).**

| What | Install | Build | Size |
|---|---|---|---|
| An empty site | 13 s | 8 s | 69 MB, 64 MB with the `lab` app alone |
| JupyterLab, which holds the extension builder | 124 s | not measured | a 209 MB environment |
| The demo as published | | | 63 MB, 62 MB of them `_framework` |

`jupyter lite build` takes an extension as a directory
(`federated_extensions`, `extra_labextensions_path`): it does not have to be
installed from a Python package.

**One Pages site per repository.** Pages deploys one artifact per
repository, so the demo and `/lite` are always published together, by one
workflow.

## Decision

### D16: One extension, one wasm app, and a build of its own

**The rule.** A clone builds and tests Shumway with the .NET SDK and nothing
else. Node and Python are needed only to build the notebook site, as
`wasm-tools` is needed only to build the demo.

- The notebook front end lives in a directory of its own, `jupyterlite/`,
  outside `Shumway.slnx`. `dotnet build`, `dotnet test`, the local gate and
  the three .NET lanes do not touch it.
- A test reads the solution's project files and fails if one of them refers
  to that directory or runs a Node or Python command. The runners have both
  installed, so the lanes passing would not show a dependency.

**The pieces.**

| # | Piece | Kind | Contents |
|---|---|---|---|
| 1 | Web host library | .NET, in the solution | sessions per instance (ADR-068), boot, cancellation, page input |
| 2 | Notebook server | .NET, in the solution | the JSON-RPC server and what hosts it in a session (ADR-067) |
| 3 | The wasm app | .NET browser-wasm, `Shumway.Web` | the demo's exports and the session API, in one app |
| 4 | The runtime's JavaScript | plain modules, `src/Shumway.Web/wwwroot` | `isolate.js`, the service worker, the loader, the MEMFS mirror with a pluggable target (ADR-070) |
| 5 | The kernel extension | JupyterLab extension, `jupyterlite/` | the kernel, the kernelspec (ADR-066 D11), logos, the CodeMirror mode |
| 6 | The site | `jupyter lite build`, `jupyterlite/` | 5, the configuration, example notebooks |

- **One wasm app.** The demo's app exports the session API as well, and the
  notebook site loads the same `_framework`. A second app would be a second
  publish and a second 62 MB download that shares no cache with the demo's.
  The server bundle stays a parameter of session creation, so one runtime
  serves Prolog and Logtalk.
- **No npm package for the runtime's JavaScript.** The files stay where the
  demo has them, and the extension's build takes them from there. A package
  the demo consumed would put Node in the demo's build.
- **One extension, with the split inside it.** The kernel core (JSON-RPC
  client, the mapping of responses to Jupyter messages, renderers,
  completion, inspection, input, interrupt) is a directory that names
  neither Prolog nor Logtalk. What is the variant's (kernelspec, server
  bundle, logos, CodeMirror mode) is another, and reaches the core as
  parameters. It becomes two packages when Hercutalk web starts and needs
  the core without the variant.
- **Nothing is published to npm or PyPI in this phase.** The site is built
  from the repository.
- **TypeScript for the extension.** The builder needs Node either way, and
  JupyterLite moves its API between versions (`BaseKernel` went from
  `@jupyterlite/kernel` to `@jupyterlite/services`): the type check is what
  finds it.

**Where it is built.**

- The demo's workflow builds the notebook site too and publishes both, since
  Pages takes one artifact. The site's build is cached by the contents of
  `jupyterlite/`, so a change to the engine alone does not rebuild it.
- A workflow of its own, run when `jupyterlite/` or the wasm app changes,
  builds the site and runs the acceptance notebook (D17) in headless
  browsers.

**Versions.**

- JupyterLite and JupyterLab are pinned (JupyterLite 0.8.7 to begin with)
  and moved deliberately.
- **The protocol has its own version, independent of everything else.**
  [`jupyterlite-protocol.md`](../../design/jupyterlite-protocol.md) is the
  contract with Hercutalk. Each side declares the protocol version it
  speaks, and the kernel checks it at session start.

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

- Who wants desktop Shumway installs the .NET SDK and builds. Nothing of the
  notebook site reaches them.
- The demo and the notebooks share one runtime download and one copy of the
  runtime's JavaScript, so a fix lands once.
- The demo and the notebook site are one deployment: a notebook site that
  does not build stops the demo's deployment too.
- Hercutalk web starts with a mechanical step, splitting the extension in
  two packages along the directory line that is already there.
- The feature table cannot silently drift from reality, because CI ties it
  to tests.

## Alternatives considered

- **Five packages, two on npm** (this ADR's first version). The demo would
  need Node to build, and the split of the kernel in two packages would be
  paid before its second consumer exists. Rejected.
- **A second wasm app for notebooks.** A second publish and a second
  download of the runtime for nothing the one app cannot do. Rejected.
- **A separate repository for the notebook site**, with its own Pages. The
  only layout in which this repository never sees Node. It needs the wasm
  app published as an artifact and versions coordinated across two
  repositories. Rejected while the rule above holds without it.
- **JavaScript without types for the extension.** One tool less inside
  `jupyterlite/`, and a version of JupyterLite that moved an API found at
  run time. Rejected.

## Related

ADR-066 to ADR-072.
