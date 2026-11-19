// The limit of kernels in a page (ADR-068 D7), with the site set to hold
// one: the second notebook's kernel has no session, and its first cell says
// why and what to do about it.
//
//   node limit.mjs <deployment dir> [chromium|firefox|webkit]
import { webkit, chromium, firefox } from 'playwright';

import { serve } from './serve.mjs';

const engineName = process.argv[3] ?? 'chromium';
const server = await serve(process.argv[2], { shumwaySessionLimit: 1 });
const browser = await { webkit, chromium, firefox }[engineName].launch();
const page = await (await browser.newContext()).newPage();

let failures = 0;
const check = (ok, what, detail) => {
  if (!ok) failures++;
  console.log(`${ok ? 'ok  ' : 'FAIL'} [${engineName}] ${what}${ok ? '' : ' ' + JSON.stringify(detail).slice(0, 900)}`);
};

try {
  await page.goto(server.url + 'jupyter/lab/index.html');
  await page.waitForFunction(() => window.crossOriginIsolated === true
      && !!window.jupyterapp?.serviceManager?.contents
      && !!document.querySelector('#jp-main-dock-panel, .jp-LabShell'),
    null, { timeout: 180000, polling: 500 });
  await page.evaluate(() => window.jupyterapp.serviceManager.ready);

  const report = await page.evaluate(async () => {
    const app = window.jupyterapp;
    const contents = app.serviceManager.contents;
    const strip = (text) => String(text).replace(/\x1b\[[0-9;]*m/g, '');
    async function open(path) {
      await contents.save(path, { type: 'notebook', format: 'json', content: {
        cells: [], nbformat: 4, nbformat_minor: 5,
        metadata: { kernelspec: { name: 'shumway_prolog', display_name: 'Prolog (Shumway)', language: 'prolog' } } } });
      const panel = await app.commands.execute('docmanager:open', { path });
      await panel.context.ready;
      await panel.sessionContext.ready;
      return panel.sessionContext.session.kernel;
    }
    async function run(kernel, code) {
      const texts = [];
      const future = kernel.requestExecute({ code });
      future.onIOPub = (msg) => {
        if (msg.header.msg_type === 'display_data') texts.push(strip(msg.content.data['text/plain'] ?? ''));
      };
      return { status: (await future.done).content.status, texts };
    }
    const first = await open('one.ipynb');
    const second = await open('two.ipynb');
    const answered = await run(first, 'X = 1.');
    const refused = await run(second, 'X = 2.');
    // Shutting the first one down frees its place for the second.
    await first.shutdown();
    await second.restart();
    const after = await run(second, 'X = 3.');
    return { answered, refused, after };
  });

  check(report.answered.texts[0] === 'X = 1', 'the first kernel has its session', report.answered);
  check(report.refused.status === 'error' && /limit/.test(report.refused.texts[0] ?? '')
      && /Running Terminals and Kernels/.test(report.refused.texts[0] ?? ''),
    'the second is refused, and its cell says how to free a place', report.refused);
  check(report.after.texts[0] === 'X = 3', 'with the first shut down, the second restarts into a session', report.after);
} catch (e) {
  failures++;
  console.log(`[${engineName}] did not get there: ${e.message.split('\n')[0]}`);
}
console.log(`[${engineName}] ${failures} failed`);
await browser.close();
server.close();
process.exit(failures ? 1 : 0);
