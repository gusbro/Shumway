// Calls into a threaded .NET runtime from a browser driven over WebDriver,
// and says whether every call came back. It speaks the W3C WebDriver
// protocol over HTTP, which is all safaridriver needs: no package to install.
//
//   node probe.mjs <dir> <macos|ios> ping [calls] [driver url]
//   node probe.mjs <dir> <macos|ios> cells [cells] [driver url]
//   node probe.mjs <dir> <macos|ios> demo [keystrokes] [driver url]
//
// `ping` serves the published ThreadedPing app from <dir> and calls its
// empty export; `cells` serves a built notebook site from <dir> (what
// `node jupyterlite/build.mjs --out <dir> --with-demo ...` makes) and runs
// cells in a notebook, each a call or more into the runtime; `demo` serves
// the same site and makes the calls the demo's page makes, a highlight for
// every keystroke and a query every 50. Exits with 1
// when a call or a cell did not come back, or the page did not start.
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';

const [dir, target = 'macos', mode = 'ping', countText = '', driver = 'http://localhost:4444'] = process.argv.slice(2);
const count = Number(countText || ({ cells: 5000, demo: 20000 }[mode] ?? 30000));
const root = path.resolve(dir);

// Threads need cross-origin isolation, which these headers give.
const types = {
  '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css',
  '.wasm': 'application/wasm', '.json': 'application/json', '.ipynb': 'application/json',
  '.dat': 'application/octet-stream', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon',
  '.woff': 'font/woff', '.woff2': 'font/woff2', '.pl': 'text/plain', '.webmanifest': 'application/manifest+json'
};
const server = http.createServer((request, response) => {
  let name = decodeURIComponent(new URL(request.url, 'http://x').pathname);
  if (name.endsWith('/')) name += 'index.html';
  const file = path.join(root, name);
  if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
    response.writeHead(404);
    response.end();
    return;
  }
  response.writeHead(200, {
    'Content-Type': types[path.extname(file)] ?? 'application/octet-stream',
    'Cross-Origin-Opener-Policy': 'same-origin',
    'Cross-Origin-Embedder-Policy': 'require-corp',
    'Cache-Control': 'no-store'
  });
  fs.createReadStream(file).pipe(response);
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const base = `http://localhost:${server.address().port}/`;

async function webdriver(method, url, body) {
  const response = await fetch(driver + url, {
    method, headers: { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body)
  });
  const answer = await response.json();
  if (!response.ok || answer.value?.error) throw new Error(`${method} ${url}: ${JSON.stringify(answer.value)}`);
  return answer.value;
}

// Each mode: the page, when it is ready, the size of a round, and a round:
// work done one step after another, which a step that does not come back
// ends.
const modes = {
  ping: {
    page: base,
    ready: 'return window.ready === true;',
    round: 1000,
    script: `
      const [count, done] = arguments;
      (async () => {
        const t0 = performance.now();
        for (let i = 1; i <= count; i++) {
          const late = new Promise((_, no) => setTimeout(() => no(new Error('call ' + i + ' of the round did not come back in 30 s')), 30000));
          try { await Promise.race([window.ping(), late]); }
          catch (e) { done({ made: i - 1, ms: performance.now() - t0, stopped: String(e.message) }); return; }
        }
        done({ made: count, ms: performance.now() - t0 });
      })();`
  },
  cells: {
    page: base + 'jupyter/lab/index.html',
    ready: `return window.crossOriginIsolated === true && !!window.jupyterapp?.serviceManager?.contents
      && !!document.querySelector('.jp-LabShell');`,
    round: 250,
    script: `
      const [count, done] = arguments;
      (async () => {
        const app = window.jupyterapp;
        if (!window.probeKernel) {
          await app.serviceManager.contents.save('probe.ipynb', { type: 'notebook', format: 'json', content: {
            cells: [], nbformat: 4, nbformat_minor: 5,
            metadata: { kernelspec: { name: 'shumway_prolog', display_name: 'Prolog (Shumway)', language: 'prolog' } } } });
          const panel = await app.commands.execute('docmanager:open', { path: 'probe.ipynb' });
          await panel.sessionContext.ready;
          window.probeKernel = panel.sessionContext.session.kernel;
          window.probeCells = 0;
          await window.probeKernel.requestExecute({ code: 'true.' }).done;
        }
        const t0 = performance.now();
        for (let i = 1; i <= count; i++) {
          const n = ++window.probeCells;
          let text = '';
          const future = window.probeKernel.requestExecute({ code: 'X = ' + n + '.' });
          future.onIOPub = (msg) => { if (msg.header.msg_type === 'display_data') text = String(msg.content.data['text/plain']); };
          const late = new Promise((ok) => setTimeout(() => ok('late'), 60000));
          const stop = (why) => done({ made: i - 1, ms: performance.now() - t0, stopped: why });
          if ((await Promise.race([future.done, late])) === 'late') return stop('cell ' + n + ' did not come back in 60 s');
          if (/stopped answering|did not start/.test(text)) return stop('cell ' + n + ': ' + text.replace(/\\x1b\\[[0-9;]*m/g, ''));
        }
        const session = await import(new URL('../shumway/session.js', location.href).href);
        done({ made: count, ms: performance.now() - t0, wakes: session.threadWakes?.() });
      })();`
  },
  // The calls a session of the demo makes: the editor's highlighting, one a
  // keystroke as the program grows, and every 50 a query with the calls the
  // page makes after an answer.
  demo: {
    page: base + 'index.html',
    ready: `const q = document.querySelector('#query');
      return window.crossOriginIsolated === true && !!q && !q.disabled
        && !/loading the engine/.test(document.querySelector('#out')?.textContent ?? 'loading the engine');`,
    round: 1000,
    script: `
      const [count, done] = arguments;
      (async () => {
        const session = await import(new URL('./session.js', location.href).href);
        for (const until = performance.now() + 120000; !session.exports() && performance.now() < until;)
          await new Promise((resolve) => setTimeout(resolve, 250));
        const engine = session.exports();
        if (!engine) { done({ made: 0, ms: 0, stopped: 'the engine was not there in 120 s' }); return; }
        const program = 'app([], L, L).\\napp([X|Xs], L, [X|R]) :- app(Xs, L, R).\\n% a comment\\nrev(L, R) :- rev(L, [], R).\\n';
        window.probeKeys ??= 0;
        const t0 = performance.now();
        let highlightMs = 0;
        const timed = async (what, call) => {
          const late = new Promise((_, no) => setTimeout(() => no(new Error(what + ' did not come back in 30 s')), 30000));
          return Promise.race([call(), late]);
        };
        for (let i = 1; i <= count; i++) {
          const n = ++window.probeKeys;
          const src = program.repeat(Math.ceil(n / program.length) + 1).slice(0, n % 4000 + 1);
          try {
            const h0 = performance.now();
            await timed('the highlight of keystroke ' + n, () => engine.Highlight(src));
            highlightMs += performance.now() - h0;
            if (n % 50 === 0) {
              const err = await timed('QueryStart after keystroke ' + n, () => engine.QueryStart('X = ' + n + '.'));
              if (err) throw new Error('QueryStart said ' + err);
              await timed('QueryNext after keystroke ' + n, () => engine.QueryNext(80));
              await timed('LibraryTakeCompiledOnDemand after keystroke ' + n, () => engine.LibraryTakeCompiledOnDemand());
              await timed('JitCompileAllTick after keystroke ' + n, () => engine.JitCompileAllTick());
            }
          } catch (e) {
            done({ made: i - 1, ms: performance.now() - t0, stopped: String(e.message) });
            return;
          }
        }
        done({ made: count, ms: performance.now() - t0, highlight: highlightMs / count, wakes: session.threadWakes?.() });
      })();`
  }
};
const chosen = modes[mode];
if (!chosen) throw new Error(`no mode ${mode}: ping or cells`);
const unit = { cells: 'cells', demo: 'keystrokes' }[mode] ?? 'calls';

const capabilities = target === 'ios'
  ? { browserName: 'safari', platformName: 'iOS', 'safari:useSimulator': true }
  : { browserName: 'safari' };
let session = null;
let code = 0;
try {
  session = (await webdriver('POST', '/session', { capabilities: { alwaysMatch: capabilities } })).sessionId;
  const at = `/session/${session}`;
  await webdriver('POST', `${at}/timeouts`, { script: 900000, pageLoad: 300000 });
  await webdriver('POST', `${at}/url`, { url: chosen.page });
  const started = Date.now();
  while (!(await webdriver('POST', `${at}/execute/sync`, { script: chosen.ready, args: [] }))) {
    if (Date.now() - started > 300000) throw new Error('the page did not start in 300 s');
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  const about = await webdriver('POST', `${at}/execute/sync`, {
    script: 'return { browser: navigator.userAgent, isolated: crossOriginIsolated, waitAsync: typeof Atomics.waitAsync };',
    args: []
  });
  console.log(`[${target} ${mode}] ${JSON.stringify(about)}, started in ${((Date.now() - started) / 1000).toFixed(1)} s`);

  let made = 0;
  while (made < count) {
    const result = await webdriver('POST', `${at}/execute/async`, { script: chosen.script, args: [Math.min(chosen.round, count - made)] });
    made += result.made;
    const each = result.made > 0 ? (result.ms / result.made).toFixed(2) : '-';
    const highlight = (result.highlight === undefined ? '' : `, a highlight ${result.highlight.toFixed(2)} ms`)
      + (result.wakes ? `; the threads woken ${result.wakes.times} times so far, ${result.wakes.told} told by message` : '');
    console.log(`[${target} ${mode}] ${made} ${unit}, ${each} ms each in the last round${highlight}`);
    if (result.stopped) {
      console.log(`[${target} ${mode}] STOPPED after ${made} ${unit}: ${result.stopped}`);
      code = 1;
      break;
    }
  }
  if (code === 0) console.log(`[${target} ${mode}] every one of ${made} ${unit} came back`);
} catch (e) {
  // fetch says only "fetch failed"; what failed is its cause.
  console.log(`[${target} ${mode}] did not get there: ${e.message}${e.cause ? ` (${e.cause.code ?? ''} ${e.cause.message ?? e.cause})` : ''}`);
  code = 1;
} finally {
  if (session) await webdriver('DELETE', `/session/${session}`).catch(() => {});
  server.close();
}
process.exit(code);
