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
`mirror.js`) stays in `src/Shumway.Web/wwwroot`, and the build copies it into
the site.

## Building

Needs Node 20 or later and Python 3.10 or later.

```bash
python -m pip install -r jupyterlite/requirements.txt

# The demo, whose runtime the notebooks run on.
dotnet publish src/Shumway.Web -c Release

# The demo at the root and the notebook site under lite/, as they are served.
node jupyterlite/build.mjs --out <dir> \
  --with-demo src/Shumway.Web/bin/Release/net10.0/publish/wwwroot
```

Without `--with-demo` the output is the notebook site alone, to be put in a
directory `lite/` of a demo's publish. The site does not work without the
demo beside it: its runtime and its service worker are the demo's.

`PYTHON` names the Python to use when it is not `python`.

## Trying it

Any static server will do. The pages need no headers from it: the demo's
service worker isolates them, and the first visit reloads once for that.

```bash
npx http-server <dir> -p 8080
# then open http://localhost:8080/lite/lab/index.html
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
`read/1`, an interrupted query, files both ways, two notebooks at once.
`limit.mjs` serves the site set to hold one kernel and checks that a second
is refused with a message that says how to free a place.

## Configuration

`site/jupyter-lite.json` takes two options of this site's own:

| Option | Meaning | Default |
|---|---|---|
| `shumwayRuntimeUrl` | where the demo is, from the site's root | `../` |
| `shumwaySessionLimit` | how many kernels a page may hold | the app's, 10 |
