# Notebooks

Jupyter notebooks for Prolog that run in the browser: a JupyterLite site
whose kernel runs on the WebShumway runtime. The design is in
[`docs/design/jupyterlite-architecture.md`](../docs/design/jupyterlite-architecture.md).

This directory is built apart from the solution. `dotnet build`, `dotnet
test` and the local gate do not touch it, and a clone that wants the engine
needs none of what is below (ADR-073). A test of the solution fails if a
project comes to depend on this directory.

## What is here

| Directory | Contents |
|---|---|
| `extension/` | The JupyterLab extension. `src/core` is the kernel and names no language; `src/prolog` is what makes it the Prolog one. |
| `site/` | What `jupyter lite build` is given: its configuration and the example notebooks. |
| `tests/` | Checks of the built site in headless browsers. |
| `build.mjs` | Builds the extension and the site. |

The kernel does not hold the engine. It asks the demo's wasm app for a
session and sends it request lines; the server that answers them is
`src/Shumway.Jupyter`. The runtime's JavaScript (`isolate.js`, `session.js`,
`mirror.js`, `libraries.js`) stays in `src/Shumway.Web/wwwroot`, and the build
copies it into the site.

## Building

Needs Node 20 or later and Python 3.10 or later.

```bash
python -m pip install -r jupyterlite/requirements.txt

# The demo, whose runtime the notebooks run on.
dotnet publish src/Shumway.Web -c Release

# The demo at the root and the notebook site under jupyter/, as they are served.
node jupyterlite/build.mjs --out <dir> \
  --with-demo src/Shumway.Web/bin/Release/net10.0/publish/wwwroot
```

Without `--with-demo` the output is the notebook site alone, to be put in a
directory `jupyter/` of a demo's publish. The site does not work without the
demo beside it: its runtime and its service worker are the demo's.

`PYTHON` names the Python to use when it is not `python`.

## Trying it

Any static server will do. The pages need no headers from it: the demo's
service worker isolates them, and the first visit reloads once for that.

```bash
npx http-server <dir> -p 8080
# then open http://localhost:8080/jupyter/lab/index.html
```

## Checking it

```bash
cd jupyterlite/tests
npm ci
npx playwright install chromium firefox webkit
node kernel.mjs <dir> chromium      # or firefox, or webkit
```

`kernel.mjs` opens a notebook in a JupyterLab page of the deployment and
runs cells through the kernel: answers, errors, tables, `jupyter:display/2`,
`read/1`, an interrupted query, files both ways, two notebooks at once, and
the cells of the feature table
([`jupyterlite-features.yaml`](../docs/design/jupyterlite-features.yaml)).
It opens a notebook saved by another Prolog kernel, uses a collection of
libraries stored while the notebook is open, imports others with
`jupyter:import_library/1` (from the notebook's files, and from a second
local server that does or does not allow CORS), and runs each example
notebook from top to bottom: `graphs.ipynb` draws graphs, and the drawer's
script is fetched only then; `plots.ipynb` draws each kind of plot, and an
option a plot does not draw is named below it.
`limit.mjs` serves the site set to hold one kernel and checks that a second
is refused with a message that says how to free a place. `stall.mjs` serves
it with short waits: a long query ends while the runtime answers, and with
a timeout of 0, which nothing ends in, the cells say to reload the page,
for a runtime that stops answering and for one that does not start.
`mirror.mjs` and `threads.mjs` need neither. `mirror.mjs` checks that the
push after a cell uses the listing of the engine's files that the cell's
call took, and lists them again when a pull or another push touched them
since, so that an older listing deletes nothing from the contents.
`threads.mjs`, on shared memory and stand-ins for the runtime's thread
workers, checks that a call still out wakes the threads that lost their
wait, and no other.

A step of `kernel.mjs` that does not end in time (a cell has 90 s) ends the
run, which says what the page did last and whether the page, its contents,
its storage and a new kernel still answer. With `NOTEBOOK_CHECK_OUTPUT` set
to a directory, a screenshot and the page's console go there too; CI keeps
them when the job fails.

CI runs `mirror.mjs` and `threads.mjs` once, and `kernel.mjs`, `limit.mjs`
and `stall.mjs` in Chromium, Firefox and WebKit
(`.github/workflows/notebooks.yml`). It runs them on a pull request or a
push to `main` that changes `jupyterlite/` or the engine: the answers a
notebook shows come from the engine.

## Libraries

The collections of libraries the demo imports (its **Libraries…** dialog) are
the notebooks' too (ADR-070, O3): the browser keeps them in one place, the
page restores them when it starts, and before each cell it brings in those
another tab stored since. A library a cell compiles because it uses it is
kept there as well.

A notebook imports one itself with `jupyter:import_library(Spec)`:

| Spec | What comes in |
|---|---|
| `scryer`, `swi`, `trealla` | the library of that system, from the GitHub directory the demo suggests |
| `Dialect:Location` | what Location holds, loaded under Dialect |
| `Location` | what Location holds, with no dialect (or the suggested one's, for its address) |

A location is the URL of a GitHub directory, of a Prolog file or of a `.zip`
or `.tar.gz` archive, or such a file of the notebook's. The collection is
named after it, and keeps where it came from: importing it again from there
does nothing, so a notebook's first cell can do it, and the same name from
elsewhere is refused.

## Graphs

`show_term/1`, `%%tree`, `jupyter:print_transition_graph/3,4` and
`show_graph/2` answer with a graph in DOT, the text the reference's server
writes. The kernel draws it (`extension/src/core/graph.ts`): it reads the
part of DOT the servers write, lays it out with dagre and writes SVG, black
on white as Graphviz draws. A tree keeps the order of each node's edges,
with no edge across another, and an edge from a node to itself is a loop
beside the node. The output keeps the DOT beside the SVG
(`text/vnd.graphviz`), so another drawer can take over from it (ADR-072).
The drawer is a script of its own, fetched the first time a notebook draws.

## Plots

`show_data/1` and `%%data` answer with the `Key-Value` pairs of `Data`
(or `_Data`) as a JSON object, as the reference's server does: `type` names
a matplotlib function and the other keys are its options. The kernel turns
them into a Vega-Lite specification (`extension/src/core/data.ts`), and
JupyterLab's own renderer draws it (ADR-072, second amendment). It draws
plot, scatter, step, stem, ecdf, bar, barh, hist, pie, stackplot,
errorbar, boxplot, eventplot, loglog, semilogx and semilogy; an option it
does not draw is named below the plot. `plots.ipynb` has one of each.

## Where it is published

The demo's workflow (`.github/workflows/webshumway.yml`) builds this site
without `--with-demo` and publishes it in the demo's `jupyter/`:
[gusbro.github.io/Shumway/jupyter/](https://gusbro.github.io/Shumway/jupyter/).
The build is cached by the contents of this directory and of the runtime
scripts it copies, so a push that changes only the engine
republishes the demo with the cached site.

## Configuration

`site/jupyter-lite.json` takes these options of this site's own:

| Option | Meaning | Default |
|---|---|---|
| `shumwayRuntimeUrl` | where the demo is, from the site's root | `../` |
| `shumwaySessionLimit` | how many kernels a page may hold | the app's, 10 |
| `shumwayRuntimePatience` | seconds a step waits for the runtime before asking it a trivial call | 10 |
| `shumwayRuntimeTimeout` | seconds that call has to answer, or the runtime is taken for stopped | 30 |
| `shumwayRuntimeStartTimeout` | seconds the runtime has to download and boot | 180 |

A runtime that stops answering, or does not start in time, is one only a
reload starts again: the cell that was waiting, and every cell after it in
the page, says so. A cell's query may take as long as it needs, since the
trivial call answers while it runs.

The runtime stops answering in WebKit and in Safari, on macOS and iOS,
because of .NET's threads: one of them now and then loses the wait on
which it is told of a message, and the call it was to answer does not come
back. A threaded .NET app with an empty export, and nothing of Shumway in
it, does it too. So a call into the runtime still out after 2 s wakes its
threads (`src/Shumway.Web/wwwroot/threads.js`), and again after 4 s, 8 s
and so on: a thread that lost its wait is told to look at its messages,
and the call comes back. The runtime's boot is watched the same way. In Safari on macOS, with it, every run of 5,000
cells in a row came back (5 of 5). Without it, 4 runs of 10 did, and the
others stopped after 52 to 1,474 cells. A cell makes one call into the
runtime, and every call answers a turn later, which makes a thread lose
its wait less often: about 3 times in such a run, against about 11 with
the four calls a cell made before.

`site/overrides.json` holds the JupyterLab settings the site starts with.
It turns on `autoStartDefaultKernel`, so that a notebook saved by another
Prolog kernel opens with this one and nothing asks which. JupyterLab
matches a kernel by name, then by language, and it compares the
notebook's `language_info.name`, `Prolog`, with the kernel's language,
`prolog`.
