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
// Versioned by cache name: bumping it discards the previous generation on
// activate, which is how a republished app stops serving yesterday's runtime.

const CACHE = 'webshumway-v9';

/**
 * The response the page should get, isolated.
 *
 * `credentialless` rather than `require-corp` so the library importer can read
 * GitHub's listing API, which sends CORS but no Cross-Origin-Resource-Policy.
 * A response whose body cannot be re-read (an opaque cross-origin one) is
 * returned untouched — it is not ours to relabel.
 */
function isolated(response) {
  if (!response || response.type === 'opaque' || response.type === 'opaqueredirect')
    return response;
  const headers = new Headers(response.headers);
  headers.set('Cross-Origin-Opener-Policy', 'same-origin');
  headers.set('Cross-Origin-Embedder-Policy', 'credentialless');
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
  event.waitUntil((async () => {
    const cache = await caches.open(CACHE);
    await cache.addAll(['./', './index.html', './styles.css', './manifest.webmanifest'])
      .catch(() => { /* a shell file missing must not block installation */ });
    self.skipWaiting();
  })());
});

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    for (const name of await caches.keys()) {
      if (name !== CACHE) await caches.delete(name);
    }
    await self.clients.claim();
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
  if (request.mode === 'navigate') {
    event.respondWith((async () => {
      try {
        const fresh = await fetch(request);
        (await caches.open(CACHE)).put('./', fresh.clone());
        return isolated(fresh);
      } catch {
        const cached = (await caches.match('./')) ?? (await caches.match('./index.html'));
        return cached ? isolated(cached) : Response.error();
      }
    })());
    return;
  }

  // A file whose name pins its contents is served from the cache: it cannot be
  // stale, and this is what makes a second visit fast and an offline one
  // possible.
  if (immutable(url)) {
    event.respondWith((async () => {
      const hit = await caches.match(request);
      if (hit) return isolated(hit);
      const fresh = await fetch(request);
      if (fresh.ok) (await caches.open(CACHE)).put(request, fresh.clone());
      return isolated(fresh);
    })());
    return;
  }

  // Everything else keeps its name across publishes, so the network decides and
  // the cache is the fallback — which is still an offline app, just one that
  // takes the newer file when there is a network to ask.
  event.respondWith((async () => {
    try {
      const fresh = await fetch(request);
      if (fresh.ok) (await caches.open(CACHE)).put(request, fresh.clone());
      return isolated(fresh);
    } catch {
      const cached = await caches.match(request);
      return cached ? isolated(cached) : Response.error();
    }
  })());
});
