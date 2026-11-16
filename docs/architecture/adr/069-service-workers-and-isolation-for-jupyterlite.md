# ADR-069: Service workers and cross-origin isolation for the notebook site

## Status

Proposed (2026-11-14). Records D12 and D13. Verification V1 of the plan has
results (Context), and they changed D13: WebShumway sends one value to every
engine, and the choice by engine is the lite site's alone. The WebShumway side
of D12 and D13 is implemented (ADR-042, amended); the lite site's part is
not.

## Context

**WebShumway** needs cross-origin isolation: threads need SharedArrayBuffer.
GitHub Pages cannot send headers, so `wwwroot/sw.js` adds them to every
response (ADR-042 §3):

- `Cross-Origin-Opener-Policy: same-origin`
- `Cross-Origin-Embedder-Policy: credentialless`
- `Cross-Origin-Resource-Policy: same-origin`

`isolate.js` runs before the module that boots the runtime and reloads once
on the first visit. `sw.js` also caches the fingerprinted `_framework`
assets, and on `activate` it deletes **every cache of the origin** whose name
is not its own.

**JupyterLite's service worker** (`packages/apputils/src/service-worker.ts`,
0.9.0-beta.1) does three things:

- `/api/drive`: syncs contents with kernels that run in Web Workers;
- `/api/stdin/`: stdin for those same worker kernels;
- an offline cache, disabled by default.

It does **not** add COOP/COEP; JupyterLite's docs expect real headers from
the host (they show Netlify and Vercel). On a version change,
`ServiceWorkerManager` **unregisters every service worker of the origin**. It
can be disabled with:

```json
"disabledExtensions": ["@jupyterlite/application-extension:service-worker-manager"]
```

**One origin, many projects.** `gusbro.github.io` is a single origin shared
by all of the owner's Pages projects.

**WebKit.** WebKit does not implement `COEP: credentialless`. caniuse lists
Safari on iOS as unsupported through 27.0. Every browser on iOS and iPadOS
uses WebKit. A COEP value the browser does not recognise leaves the page
non-isolated: no SharedArrayBuffer, so the threaded runtime cannot start.
ADR-042 states the Safari cost as "URL import is unavailable there", and
nothing in `isolate.js` or `sw.js` treats Safari differently. **If this
reading is right, WebShumway does not boot on iPhone, iPad or Mac Safari
today.** That is verification V1.

**V1, first result (2026-11-14).** In headless WebKit 27.2 (the Playwright
build, on Windows) the published demo does not boot. The service worker
controls the page and serves it with `COEP: credentialless`;
`crossOriginIsolated` stays false, there is no SharedArrayBuffer, and the boot
aborts with "the page is not cross-origin isolated". The control was the same
browser against a page served with real headers: with `credentialless` the page
is not isolated, with `require-corp` it is.

**V1, closed (2026-11-15).** With the fix published (the worker sends
`require-corp`), the demo boots and answers queries on a real iPhone. An iPad
and Mac Safari have not been tried; they use the same engine.

**WebShumway never needed `credentialless`.** ADR-042 chose it so that the
library importer could read GitHub's listing API, which sends CORS but no CORP.
A CORS fetch is allowed under `require-corp`. Measured in headless Chromium
159, Firefox 159 and WebKit 27.2, against the published site behind a server
that sends no headers and with the service worker sending `require-corp`: all
three isolate the page, boot the engine, answer a query and read the listing.

**What `credentialless` does buy is images from other hosts.** An `<img>` whose
host sends no CORP (measured with two public image hosts) loads under
`credentialless` in Chromium and Firefox, and under `require-corp` in no
engine. WebShumway embeds no such image. A notebook's markdown cells commonly
do.

## Decision

### D12: One service worker in the lite site: ours

- **Disable JupyterLite's service worker** with the `disabledExtensions`
  entry above. None of its jobs apply to us: our kernel is not in a worker
  (ADR-068), stdin goes through page input, and files are mirrored from the
  main thread through the Contents API (ADR-070). The plotting plan has no
  Pyodide, so no worker kernel needs `/api/drive` either (ADR-072).
- **The lite site registers its own service worker**, built from the same
  source as WebShumway's (headers, `isolate.js` boot; ADR-073). It is
  registered at the lite site's root, whose scope is narrower than the
  demo's and so wins for the notebook pages. The two differ in the COEP
  value they send (D13), which is why the demo's worker cannot serve both.
  The lite site loads the demo's `_framework` (ADR-073), and its worker
  reads and fills the same cache of those files, so the runtime is
  downloaded once for the two.
- **WebShumway's `sw.js` becomes a good neighbour:** on `activate` it deletes
  only caches carrying its own prefix. This fix stands on its own and goes
  into WebShumway first.

### D13: `require-corp` in WebShumway, COEP chosen by engine in the lite site

- **WebShumway sends `require-corp` to every browser.** All three engines
  implement it, and nothing the page loads needs the other value (Context).
  There is no detection of the browser engine. ADR-042 is amended.
- **The lite site chooses by engine**, because of the images in markdown
  cells:
  - `credentialless` where it is implemented (Chromium, Firefox);
  - `require-corp` in WebKit, where an image from a host without CORP does
    not show.

  Detection must match **WebKit that is not Chromium**, not "iOS": iPadOS
  presents itself as a Mac by default, and Mac Safari has the same limitation.
  The lite site's worker is built from WebShumway's source with this one
  difference.

## Consequences

- The "unregister everything" behaviour disappears, because the manager that
  performs it is disabled.
- The lite site loses JupyterLite's offline cache (disabled by default
  anyway) and contents sync for worker kernels (none here).
- If a future kernel needs to run in a worker, this decision has to be
  revisited.
- iPhone, iPad and Mac Safari users gain WebShumway itself, not only
  notebooks.
- In WebKit, a notebook's image from a host that sends no CORP does not show.

## Alternatives considered

- **Patch JupyterLite's service worker to add the headers.** Leaves a fork to
  re-apply on every JupyterLite version, and the unregister-everything
  behaviour stays. Rejected.
- **Host with real headers (Cloudflare Pages, Netlify).** Technically the
  cleanest option, but it means leaving GitHub Pages for this alone. Kept in
  reserve.
- **Rely on WebShumway's worker covering `/lite/`.** It sends one COEP value
  to every browser, and the notebook pages need another in two of the three
  engines (D13). Rejected.
- **`require-corp` in the lite site too.** One behaviour in every browser and
  no detection, at the cost of those images everywhere. Rejected while the
  detection stays this small.

## Related

ADR-042 §3, ADR-068, ADR-070, ADR-072, ADR-073.
