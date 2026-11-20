// Calls into a threaded .NET app from a browser driven over WebDriver, and
// says whether every call came back. It speaks the W3C WebDriver protocol
// over HTTP, which is all safaridriver needs: no package to install.
//
//   node probe.mjs <published wwwroot> <macos|ios> [calls] [driver url]
//
// Exits with 1 when a call did not come back in 30 s, or the page did not
// start.
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';

const [dir, target = 'macos', callsText = '30000', driver = 'http://localhost:4444'] = process.argv.slice(2);
const calls = Number(callsText);
const root = path.resolve(dir);
const ROUND = 1000;

// Threads need cross-origin isolation, which these headers give.
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript',
  '.wasm': 'application/wasm', '.json': 'application/json', '.dat': 'application/octet-stream' };
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
const page = `http://localhost:${server.address().port}/`;

async function webdriver(method, url, body) {
  const response = await fetch(driver + url, {
    method, headers: { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body)
  });
  const answer = await response.json();
  if (!response.ok || answer.value?.error) throw new Error(`${method} ${url}: ${JSON.stringify(answer.value)}`);
  return answer.value;
}

const capabilities = target === 'ios'
  ? { browserName: 'safari', platformName: 'iOS', 'safari:useSimulator': true }
  : { browserName: 'safari' };
let session = null;
let code = 0;
try {
  session = (await webdriver('POST', '/session', { capabilities: { alwaysMatch: capabilities } })).sessionId;
  const at = `/session/${session}`;
  await webdriver('POST', `${at}/timeouts`, { script: 600000, pageLoad: 300000 });
  await webdriver('POST', `${at}/url`, { url: page });
  const started = Date.now();
  while (!(await webdriver('POST', `${at}/execute/sync`, { script: 'return window.ready === true;', args: [] }))) {
    if (Date.now() - started > 180000) throw new Error('the page did not start in 180 s');
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  const about = await webdriver('POST', `${at}/execute/sync`, {
    script: 'return { browser: navigator.userAgent, isolated: crossOriginIsolated, waitAsync: typeof Atomics.waitAsync };',
    args: []
  });
  console.log(`[${target}] ${JSON.stringify(about)}, started in ${((Date.now() - started) / 1000).toFixed(1)} s`);

  // A round of calls, one after the other; a call that does not come back
  // in 30 s ends it.
  const round = `
    const [count, done] = arguments;
    (async () => {
      const t0 = performance.now();
      for (let i = 1; i <= count; i++) {
        const late = new Promise((_, no) => setTimeout(() => no(new Error('call ' + i + ' of the round did not come back in 30 s')), 30000));
        try { await Promise.race([window.ping(), late]); }
        catch (e) { done({ made: i - 1, ms: performance.now() - t0, stopped: String(e.message) }); return; }
      }
      done({ made: count, ms: performance.now() - t0 });
    })();`;
  let made = 0;
  while (made < calls) {
    const result = await webdriver('POST', `${at}/execute/async`, { script: round, args: [Math.min(ROUND, calls - made)] });
    made += result.made;
    const each = result.made > 0 ? (result.ms / result.made).toFixed(2) : '-';
    console.log(`[${target}] ${made} calls, ${each} ms each in the last round`);
    if (result.stopped) {
      console.log(`[${target}] STOPPED after ${made} calls: ${result.stopped}`);
      code = 1;
      break;
    }
  }
  if (code === 0) console.log(`[${target}] every one of ${made} calls came back`);
} catch (e) {
  console.log(`[${target}] did not get there: ${e.message}`);
  code = 1;
} finally {
  if (session) await webdriver('DELETE', `/session/${session}`).catch(() => {});
  server.close();
}
process.exit(code);
