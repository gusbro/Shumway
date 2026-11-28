// A check that hangs ends in minutes, with a diagnosis, and is not left to
// the job's own limit: what the page and the runtime's workers said last, and
// whether the page, its contents, its storage and a new kernel still answer.
// With NOTEBOOK_CHECK_OUTPUT set to a directory, the page's console and a
// screenshot go there too.
import fs from 'node:fs';
import path from 'node:path';

export const MINUTE = 60000;

/** A step that did not end in time. A step of the page says "did not end in"
 *  too, in the message of the error it throws. */
export class Hung extends Error {}

export const hung = (e) => e instanceof Hung || /did not end in/.test(String(e?.message));

/** The promise, or a Hung after ms. */
export function guarded(promise, ms, what) {
  let timer;
  const late = new Promise((_, no) => { timer = setTimeout(() => no(new Hung(`${what} did not end in ${ms / 1000} s`)), ms); });
  return Promise.race([promise, late]).finally(() => clearTimeout(timer));
}

/** Listens to the page from the start; diagnose() says what a hung page can
 *  still tell. The checks announce each step on the console. */
export function watch(page, engineName) {
  const said = [];
  const note = (line) => {
    said.push(line.slice(0, 300));
    if (said.length > 300) said.shift();
  };
  page.on('console', (m) => note(`${m.type()}: ${m.text()}`));
  // The runtime's threads are workers, which say things of their own; one
  // that closes has died.
  let workers = 0;
  page.on('worker', (w) => {
    const n = ++workers;
    w.on('console', (m) => note(`worker ${n} ${m.type()}: ${m.text()}`));
    w.on('close', () => note(`worker ${n} closed: ${path.posix.basename(new URL(w.url()).pathname)}`));
  });

  async function diagnose() {
    console.log(`[${engineName}] the page said last:\n  ${said.slice(-30).join('\n  ')}`);
    const probe = async (what, ms, fn) => {
      try {
        console.log(`[${engineName}] ${what}: ${JSON.stringify(await guarded(page.evaluate(fn), ms, what)).slice(0, 300)}`);
      } catch (e) {
        console.log(`[${engineName}] ${what}: ${String(e?.message).split('\n')[0]}`);
      }
    };
    await probe('the page answers', 10000, () => true);
    await probe('its contents answer', 15000,
      async () => (await window.jupyterapp.serviceManager.contents.get('', { content: true })).content.length);
    // Playwright's WebKit cannot use this storage at all: an error there is
    // no sign of a hang.
    await probe('its storage answers', 15000, async () => {
      const names = [];
      for await (const name of (await navigator.storage.getDirectory()).keys()) names.push(name);
      return names;
    });
    await probe('a new kernel answers', 2 * MINUTE, async () => {
      const shell = await window.jupyterapp.commands.execute('console:create', { kernelPreference: { name: 'shumway_prolog' } });
      await shell.sessionContext.ready;
      const texts = [];
      const future = shell.sessionContext.session.kernel.requestExecute({ code: 'X = alive.' });
      future.onIOPub = (msg) => { if (msg.header.msg_type === 'display_data') texts.push(msg.content.data['text/plain']); };
      await future.done;
      return texts;
    });
    // The runtime's memory and threads, which the page knows without asking it.
    await probe(`the runtime (${workers} workers started)`, 10000, async () => {
      const session = await import(new URL('../shumway/session.js', location.href).href);
      return { memoryMB: session.wasmMemoryBytes() >> 20, threadPool: session.threadPoolSize() };
    });
    const workerLines = said.filter((line) => /^worker \d+ /.test(line));
    console.log(`[${engineName}] the workers said:\n  ${workerLines.slice(-40).join('\n  ')}`);
    const out = process.env.NOTEBOOK_CHECK_OUTPUT;
    if (out) {
      fs.mkdirSync(out, { recursive: true });
      fs.writeFileSync(path.join(out, `${engineName}-console.txt`), said.join('\n'));
      await guarded(page.screenshot({ path: path.join(out, `${engineName}-hung.png`), fullPage: true }), 20000, 'the screenshot')
        .catch((e) => console.log(`[${engineName}] ${e.message}`));
    }
  }

  return { diagnose };
}
