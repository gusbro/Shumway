# ADR-069: Service workers and cross-origin isolation for the notebook site

## Status

Proposed (2026-11-14). Design only; nothing is implemented. Records D12 and
D13. D13's WebKit branch depends on verification V1 in the plan, which has a
first result (Context, below).

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
is not isolated, with `require-corp` it is. A real iPhone, iPad or Mac Safari
has not been tried yet.

## Decision

### D12: One service worker in the lite site: ours

- **Disable JupyterLite's service worker** with the `disabledExtensions`
  entry above. None of its jobs apply to us: our kernel is not in a worker
  (ADR-068), stdin goes through page input, and files are mirrored from the
  main thread through the Contents API (ADR-070). The plotting plan has no
  Pyodide, so no worker kernel needs `/api/drive` either (ADR-072).
- **The lite site registers its own service worker**, built from the same
  source as WebShumway's (headers, `isolate.js` boot) and shipped in
  `@shumway/web-runtime` (ADR-073). It is registered at the lite site's
  root. It does not rely on WebShumway's worker happening to cover `/lite/`
  by scope, so the two sites deploy independently.
- **WebShumway's `sw.js` becomes a good neighbour:** on `activate` it deletes
  only caches carrying its own prefix. This fix stands on its own and goes
  into WebShumway first.

### D13: COEP chosen by engine

Pending V1, the service worker selects the COEP value per browser engine:

- `credentialless` where it is supported (Chromium, Firefox);
- `require-corp` in WebKit.

Detection must match **WebKit that is not Chromium**, not "iOS": iPadOS
presents itself as a Mac by default, and Mac Safari has the same limitation.
The fix lands in WebShumway's service worker, and the lite site inherits it.
ADR-042's statement about Safari is amended once V1 has a result.

Under `require-corp`, WebKit loses what ADR-042 attributes to it (URL import
from GitHub's listing API). Booting matters more.

## Consequences

- The "unregister everything" behaviour disappears, because the manager that
  performs it is disabled.
- The lite site loses JupyterLite's offline cache (disabled by default
  anyway) and contents sync for worker kernels (none here).
- If a future kernel needs to run in a worker, this decision has to be
  revisited.
- If V1 confirms the problem, iPhone, iPad and Mac Safari users gain
  WebShumway itself, not only notebooks.

## Alternatives considered

- **Patch JupyterLite's service worker to add the headers.** Leaves a fork to
  re-apply on every JupyterLite version, and the unregister-everything
  behaviour stays. Rejected.
- **Host with real headers (Cloudflare Pages, Netlify).** Technically the
  cleanest option, but it means leaving GitHub Pages for this alone. Kept in
  reserve.
- **Rely on WebShumway's worker covering `/lite/`.** Couples the deployment
  of the two sites. Rejected.

## Related

ADR-042 §3, ADR-068, ADR-070, ADR-072, ADR-073.
