// Offline, and cross-origin isolation on a host that cannot send headers.
//
// OFFLINE. A precache list is not an option here: the runtime's files are
// fingerprinted at publish time, so their names are not known when this file is
// written. So the strategy is cache-as-you-go, which keeps this file correct
// across republishes without maintaining a manifest. What it cannot do by
// itself is catch the assets of the very load that installed it — those were
// fetched before this worker controlled anything — so main.js asks for them
// again once it does (warmOfflineCache). Between the two, one visit is enough
// to run with no network afterwards.
//
// ISOLATION. The app uses threads, threads need SharedArrayBuffer, and that
// needs the page to be cross-origin isolated — which normally means the SERVER
// sends COOP/COEP. A host like GitHub Pages cannot. But a service worker
// controls the responses the page receives, so it can add those headers itself:
// the browser sees an isolated document either way, because what matters is what
// arrives, not who wrote it.
//
// The one cost is the FIRST load of a fresh visit: no worker controls the page
// yet, so nothing adds the headers and the page is not isolated. main.js detects
// that and reloads once — after which the worker is in charge and the app runs
// isolated for good. Where the server DOES send the headers (see
// docs/guide/webshumway.md), the page is isolated from the start and neither the
// rewriting nor the reload ever happens.
//
// CACHE STORAGE IS BEST-EFFORT, NEVER LOAD-BEARING. Diagnosed live: a browser
// whose Cache Storage backend for this origin is damaged makes caches.open /
// match / keys HANG rather than fail — and this worker used to sit on the
// request path awaiting them, so every navigation and asset request hung with
// it (activation too, which parks navigations forever behind an `activating`
// worker). Every cache operation here is therefore bounded by a short timeout
// and falls through to the network, and writes are fire-and-forget: a broken
// cache costs speed and offline support, never the page.
//
// Versioned by cache name: bumping it discards the previous generation on
// activate, which is how a republished app stops serving yesterday's runtime.
// Only this app's generations: the origin is shared with other sites, and
// their caches are not ours to delete.

const CACHE_PREFIX = 'webshumway-';
const CACHE = CACHE_PREFIX + 'v10';

/** A promise, or a rejection after `ms` — cache ops must never park a request. */
const bounded = (promise, ms) => Promise.race([
  promise,
  new Promise((_, reject) => setTimeout(reject, ms, new Error('cache timeout'))),
]);

/** Best-effort cache write: never awaited, never fatal. */
function stash(request, response) {
  try {
    const copy = response.clone();   // clone NOW — the body streams away otherwise
    caches.open(CACHE).then((c) => c.put(request, copy)).catch(() => { });
  } catch { /* an already-consumed body is not worth a failure */ }
}

/** Best-effort cache read: a hit, or null — hangs and errors both read as miss. */
const cached = (request) =>
  bounded(caches.match(request), 1500).catch(() => null);

// ADR-069 D13. `require-corp` is the value every engine implements: WebKit
// ignores `credentialless`, and a page it ignores it on is not isolated and
// cannot boot. The demo sends it to every browser; its cross-origin reads are
// CORS fetches, which `require-corp` allows.
// The notebook site, a directory of this one, gets `credentialless` where the
// engine implements it: a markdown cell shows images from hosts that send no
// CORP header, and only that value lets them through. WebKit that is not
// Chromium keeps `require-corp`; "iOS" would be the wrong test, since iPadOS
// says it is a Mac and Safari on a Mac has the same limit.
// One worker for the two sites, not one each: the runtime's threads are
// workers whose scripts are in this site's _framework, a worker script needs
// these headers too, and a worker registered under the notebook site is not
// asked about what is outside it.

/** The directory this worker is served from: the root of its site. */
const ROOT = new URL('./', self.location.href).pathname;
const NOTEBOOKS = ROOT + 'lite/';
const WEBKIT = /AppleWebKit\//.test(navigator.userAgent)
  && !/Chrom(e|ium)\//.test(navigator.userAgent);
const coep = (url) =>
  (!WEBKIT && url.pathname.startsWith(NOTEBOOKS) ? 'credentialless' : 'require-corp');

/**
 * The response the page should get for `url`, isolated.
 * A response whose body cannot be re-read (an opaque cross-origin one) is
 * returned untouched — it is not ours to relabel.
 */
function isolated(response, url) {
  if (!response || response.type === 'opaque' || response.type === 'opaqueredirect')
    return response;
  const headers = new Headers(response.headers);
  headers.set('Cross-Origin-Opener-Policy', 'same-origin');
  headers.set('Cross-Origin-Embedder-Policy', coep(url));
  // Same-origin subresources need this to be embeddable in an isolated
  // document; the server sends it where it can, and this covers where it
  // cannot.
  headers.set('Cross-Origin-Resource-Policy', 'same-origin');
  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers,
  });
}

/**
 * Whether a URL names one exact version of its contents. The runtime's files
 * and this app's modules carry a fingerprint in their name, so a changed one
 * arrives under a NEW name and the cached copy can never be wrong.
 *
 * Everything else — the stylesheet, the manifest, the examples — keeps its name
 * across publishes, so a cached copy CAN be stale, and serving it was: a normal
 * reload gave the new HTML with the old CSS, and only a cache-bypassing reload
 * looked right.
 */
const immutable = (url) =>
  url.pathname.includes('/_framework/') || /\.[a-z0-9]{8,}\.[a-z]+$/.test(url.pathname);

self.addEventListener('install', (event) => {
  // The shell, whose names we DO know. Everything else arrives via fetch.
  // Bounded and tolerated: a hanging cache backend must not park this worker
  // in `installing` — a stuck installation is how the scope got wedged once.
  event.waitUntil((async () => {
    await bounded(
      caches.open(CACHE).then((c) =>
        c.addAll(['./', './index.html', './styles.css', './manifest.webmanifest'])),
      8000).catch(() => { /* a shell file missing must not block installation */ });
    self.skipWaiting();
  })());
});

self.addEventListener('activate', (event) => {
  // Navigations WAIT for an `activating` worker to finish, so nothing here may
  // hang: cleanup of older cache generations is bounded and best-effort.
  event.waitUntil((async () => {
    await bounded((async () => {
      for (const name of await caches.keys()) {
        if (name !== CACHE && name.startsWith(CACHE_PREFIX)) await caches.delete(name);
      }
    })(), 5000).catch(() => { });
    // Asked for, not waited for. In Firefox the claim does not settle while
    // the page's worker threads are blocked, and the runtime's threads block:
    // the worker stayed `activating` with every fetch of the page held behind
    // it, until the browser killed it a minute later and those fetches failed.
    // The page is controlled as soon as the claim is made.
    self.clients.claim().catch(() => { });
  })());
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;   // never cache third parties

  // A navigation goes to the network first so a republished app is picked up,
  // and falls back to the cache when there is none — which is what makes the
  // installed app start offline.
  // A page is kept under its own path, whatever its query: the scope holds
  // more than one page (a notebook site under the demo's, and that site's
  // own), and one kept under another's name is what an offline visit to the
  // other would get.
  if (request.mode === 'navigate') {
    const page = url.origin + url.pathname;
    event.respondWith((async () => {
      try {
        const fresh = await fetch(request);
        if (fresh.ok) stash(page, fresh);
        return isolated(fresh, url);
      } catch {
        let hit = await cached(page);
        if (!hit && url.pathname === ROOT + 'index.html') hit = await cached('./');
        if (!hit && url.pathname === ROOT) hit = await cached('./index.html');
        return hit ? isolated(hit, url) : Response.error();
      }
    })());
    return;
  }

  // A file whose name pins its contents is served from the cache: it cannot be
  // stale, and this is what makes a second visit fast and an offline one
  // possible.
  if (immutable(url)) {
    event.respondWith((async () => {
      const hit = await cached(request);
      if (hit) return isolated(hit, url);
      const fresh = await fetch(request);
      if (fresh.ok) stash(request, fresh);
      return isolated(fresh, url);
    })());
    return;
  }

  // Everything else keeps its name across publishes, so the network decides and
  // the cache is the fallback — which is still an offline app, just one that
  // takes the newer file when there is a network to ask.
  event.respondWith((async () => {
    try {
      const fresh = await fetch(request);
      if (fresh.ok) stash(request, fresh);
      return isolated(fresh, url);
    } catch {
      const hit = await cached(request);
      return hit ? isolated(hit, url) : Response.error();
    }
  })());
});
