// The limit of kernels in a page (ADR-068 D7), with the site set to hold
// one: the second notebook's kernel has no session, and its first cell says
// why and what to do about it.
//
//   node limit.mjs <deployment dir> [chromium|firefox|webkit]
import { webkit, chromium, firefox } from 'playwright';

import { serve } from './serve.mjs';
import { guarded, hung, MINUTE, watch } from './watch.mjs';

const engineName = process.argv[3] ?? 'chromium';
const server = await serve(process.argv[2], { shumwaySessionLimit: 1 });
const browser = await { webkit, chromium, firefox }[engineName].launch();
const page = await (await browser.newContext()).newPage();
const { diagnose } = watch(page, engineName);

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

  const report = await guarded(page.evaluate(async () => {
    const app = window.jupyterapp;
    const contents = app.serviceManager.contents;
    const strip = (text) => String(text).replace(/\x1b\[[0-9;]*m/g, '');
    const within = (promise, ms, what) => Promise.race([promise,
      new Promise((_, no) => setTimeout(() => no(new Error(`${what} did not end in ${ms / 1000} s`)), ms))]);
    const step = async (what, ms, promise) => {
      console.log(`[limit.mjs] ${what}`);
      return within(promise, ms, what);
    };
    async function open(path) {
      await contents.save(path, { type: 'notebook', format: 'json', content: {
        cells: [], nbformat: 4, nbformat_minor: 5,
        metadata: { kernelspec: { name: 'shumway_prolog', display_name: 'Prolog (Shumway)', language: 'prolog' } } } });
      const panel = await app.commands.execute('docmanager:open', { path });
      await panel.context.ready;
      await step(`the kernel of ${path}`, 180000, panel.sessionContext.ready);
      return panel.sessionContext.session.kernel;
    }
    async function run(kernel, code) {
      const texts = [];
      const future = kernel.requestExecute({ code });
      future.onIOPub = (msg) => {
        if (msg.header.msg_type === 'display_data') texts.push(strip(msg.content.data['text/plain'] ?? ''));
      };
      return { status: (await step(`the cell ${JSON.stringify(code)}`, 90000, future.done)).content.status, texts };
    }
    const first = await open('one.ipynb');
    const second = await open('two.ipynb');
    const answered = await run(first, 'X = 1.');
    const refused = await run(second, 'X = 2.');
    // Shutting the first one down frees its place for the second.
    await step('shutting the first kernel down', 60000, first.shutdown());
    await step('restarting the second', 180000, second.restart());
    const after = await run(second, 'X = 3.');
    return { answered, refused, after };
  }), 10 * MINUTE, 'the limit checks');

  check(report.answered.texts[0] === 'X = 1', 'the first kernel has its session', report.answered);
  check(report.refused.status === 'error' && /limit/.test(report.refused.texts[0] ?? '')
      && /Running Terminals and Kernels/.test(report.refused.texts[0] ?? ''),
    'the second is refused, and its cell says how to free a place', report.refused);
  check(report.after.texts[0] === 'X = 3', 'with the first shut down, the second restarts into a session', report.after);
} catch (e) {
  failures++;
  console.log(`[${engineName}] did not get there: ${e.message.split('\n')[0]}`);
  if (hung(e)) await diagnose();
}
console.log(`[${engineName}] ${failures} failed`);
await guarded(browser.close(), MINUTE, 'closing the browser').catch((e) => console.log(e.message));
server.close();
process.exit(failures ? 1 : 0);
