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
  const TRIED = 'shumway.isolating';

  if (window.crossOriginIsolated) {
    // Clear the one-reload guard on every ISOLATED load. Left set, it outlives
    // this successful dance for the whole tab session — and the next
    // un-isolated load (a deploy racing the worker's skipWaiting takeover, a
    // cache-bypassing reload) found it and gave up on its FIRST try instead of
    // doing the one reload that fixes it.
    sessionStorage.removeItem(TRIED);
    return;
  }
  if (!('serviceWorker' in navigator) || location.protocol === 'file:') {
    // No worker possible here, so no isolation ever. Say so instead of leaving
    // main.js waiting for a reload that nobody is going to perform.
    window.shumwayIsolationFailed = true;
    return;
  }

  // A registration that went through the dance and still could not isolate the
  // page is not one to keep: drop it, so the NEXT attempt installs a fresh
  // worker instead of inheriting whatever state got this one stuck.
  const giveUp = () => {
    sessionStorage.removeItem(TRIED);
    navigator.serviceWorker.getRegistration()
      .then((r) => r && r.unregister())
      .catch(() => { });
    document.documentElement.style.visibility = '';
    window.shumwayIsolationFailed = true;
  };

  // Already reloaded once and still not isolated: something is stopping the
  // worker from taking over, and reloading again would do it forever. Give up
  // and let main.js explain — the threaded runtime cannot start without
  // isolation, so there is nothing to run.
  if (sessionStorage.getItem(TRIED)) {
    giveUp();
    return;
  }

  // Stop the page here: the module scripts below must not start, since the
  // runtime would assert and the reload would throw the work away anyway.
  document.documentElement.style.visibility = 'hidden';

  // The timeout is load-bearing: `ready` resolves only when the registration
  // gains an ACTIVE worker, and a worker stuck installing never provides one —
  // which left this page hidden forever, throwing "reloading to isolate" on
  // every F5. Bounded wait, then give up VISIBLY with the registration dropped.
  const readyOrStuck = Promise.race([
    navigator.serviceWorker.register('sw.js')
      .then(() => navigator.serviceWorker.ready),
    new Promise((_, reject) => setTimeout(() => reject(new Error('stuck')), 5000)),
  ]);
  readyOrStuck
    .then(() => {
      sessionStorage.setItem(TRIED, 'yes');
      location.reload();
    })
    .catch(giveUp);

  // The reload is what should happen; anything the rest of the page would do
  // meanwhile is wasted. Blocking further module execution is not possible from
  // here, so the page is merely hidden — the reload follows in milliseconds.
})();
