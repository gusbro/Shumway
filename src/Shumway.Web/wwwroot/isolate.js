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
    // doing the one reload that fixes it. Same for the zombie-purge marker.
    sessionStorage.removeItem(TRIED);
    sessionStorage.removeItem('shumway.zombie-purged');
    return;
  }
  if (!('serviceWorker' in navigator) || location.protocol === 'file:') {
    // No worker possible here, so no isolation ever. Say so instead of leaving
    // main.js waiting for a reload that nobody is going to perform.
    window.shumwayIsolationFailed = true;
    return;
  }

  // Show the page and tell main.js (which already printed its waiting note and
  // listens for this event) that isolation has not arrived. NOT final: the
  // ready→reload path stays armed wherever a registration is still installing.
  const showFailed = () => {
    document.documentElement.style.visibility = '';
    window.shumwayIsolationFailed = true;
    window.dispatchEvent(new Event('shumway-isolation-failed'));
  };

  // Already reloaded once and still not isolated: the worker went through a
  // whole dance, took control, and its response STILL did not isolate the
  // page — that worker is broken, not slow. Drop the registration so the next
  // attempt installs a fresh one instead of re-inheriting it, and let main.js
  // explain — the threaded runtime cannot start without isolation.
  if (sessionStorage.getItem(TRIED)) {
    sessionStorage.removeItem(TRIED);
    navigator.serviceWorker.getRegistration()
      .then((r) => r && r.unregister())
      .catch(() => { });
    showFailed();
    return;
  }

  // Stop the page here: the module scripts below must not start, since the
  // runtime would assert and the reload would throw the work away anyway.
  document.documentElement.style.visibility = 'hidden';

  let reloading = false;
  const PURGED = 'shumway.zombie-purged';
  navigator.serviceWorker.register('sw.js')
    .then((reg) => {
      // A CORRUPTED registration record: it has our scope but no worker in
      // any state, and update() rejects with InvalidStateError, script
      // 'Unknown'. Seen in the wild (Chrome, after deploy churn): register()
      // happily resolves with it and `ready` then waits forever for a worker
      // that cannot exist. It cannot be revived — drop it and reload so the
      // next load registers from scratch. Once per tab session, so a zombie
      // that survives its own funeral cannot loop us.
      if (!reg.installing && !reg.waiting && !reg.active) {
        if (sessionStorage.getItem(PURGED)) {
          sessionStorage.removeItem(PURGED);
          showFailed();
          return;
        }
        sessionStorage.setItem(PURGED, 'yes');
        reloading = true;
        reg.unregister().then(
          () => location.reload(),
          () => { reloading = false; showFailed(); });
        return;
      }
      return navigator.serviceWorker.ready.then(() => {
        reloading = true;
        sessionStorage.setItem(TRIED, 'yes');
        location.reload();
      });
    })
    .catch(showFailed);   // no registration at all — nothing will ever isolate

  // Stop hiding after a bounded wait, but KEEP the registration and keep the
  // reload armed. `ready` resolves only when the registration gains an ACTIVE
  // worker, and on a slow connection the worker's install competes with the
  // runtime download this page is doing at the same time — it can take far
  // longer than any reasonable hide. Unregistering here was self-sabotage:
  // every F5 killed the worker mid-install and started over, a loop that
  // never converged. Left installing, the worker eventually activates and the
  // reload above brings the page back working, F5 or no F5.
  setTimeout(() => { if (!reloading) showFailed(); }, 5000);
})();
