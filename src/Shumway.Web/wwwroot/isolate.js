// Cross-origin isolation, before anything that needs it.
//
// The app uses threads; threads need SharedArrayBuffer; that needs the page to
// be cross-origin isolated, which normally means the server sends COOP/COEP.
// Where the host cannot — GitHub Pages — the service worker adds those headers
// to what the page receives (see sw.js). But a service worker does not control
// the page that installed it, so the FIRST load of a fresh visit is not
// isolated no matter what the worker does. One reload puts it in charge.
//
// This runs as a plain script in <head>, ahead of the module that boots the
// engine. It has to: a module's imports are evaluated before its own body, and
// main.js reaches the .NET runtime through session.js — so isolation code
// living in main.js ran only after the runtime had already failed the assert.
//
// Where the server DOES send the headers, this returns immediately and neither
// the worker nor the reload is involved.

(() => {
  if (window.crossOriginIsolated) return;
  if (!('serviceWorker' in navigator) || location.protocol === 'file:') return;

  const TRIED = 'shumway.isolating';

  // Already reloaded once and still not isolated: something is stopping the
  // worker from taking over, and reloading again would do it forever. Let the
  // app start un-isolated — it says so, and everything works except that a long
  // query blocks the tab.
  if (sessionStorage.getItem(TRIED)) {
    sessionStorage.removeItem(TRIED);
    window.shumwayIsolationFailed = true;
    return;
  }

  // Stop the page here: the module scripts below must not start, since the
  // runtime would assert and the reload would throw the work away anyway.
  document.documentElement.style.visibility = 'hidden';

  navigator.serviceWorker.register('sw.js')
    .then((registration) => navigator.serviceWorker.ready.then(() => registration))
    .then(() => {
      sessionStorage.setItem(TRIED, 'yes');
      location.reload();
    })
    .catch(() => {
      // No worker, so no isolation. Show the page and let the app explain.
      document.documentElement.style.visibility = '';
      window.shumwayIsolationFailed = true;
    });

  // The reload is what should happen; anything the rest of the page would do
  // meanwhile is wasted. Blocking further module execution is not possible from
  // here, so the page is merely hidden — the reload follows in milliseconds.
})();
