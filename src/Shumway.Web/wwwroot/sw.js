// Offline, by remembering what the page actually fetched.
//
// A precache list is not an option here: the runtime's files are fingerprinted
// at publish time, so their names are not known when this file is written. So
// the strategy is cache-as-you-go — the first visit fills the cache from the
// network, and every visit after that can run with no network at all. That also
// keeps this file correct across republishes without maintaining a manifest.
//
// Versioned by cache name: bumping it discards the previous generation on
// activate, which is how a republished app stops serving yesterday's runtime.

const CACHE = 'webshumway-v2';

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
        return fresh;
      } catch {
        return (await caches.match('./')) ?? (await caches.match('./index.html'))
            ?? Response.error();
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
      if (hit) return hit;
      const fresh = await fetch(request);
      if (fresh.ok) (await caches.open(CACHE)).put(request, fresh.clone());
      return fresh;
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
      return fresh;
    } catch {
      return (await caches.match(request)) ?? Response.error();
    }
  })());
});
