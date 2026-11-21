// A runtime that takes long, one that stops answering, and one that does
// not start: a cell waits for a long query while the runtime answers a
// trivial call; once it does not, or when the runtime did not start in
// time, the cell says to reload the page, and so does every cell after it.
// The site's options shorten the waits: shumwayRuntimePatience, and a
// shumwayRuntimeTimeout or shumwayRuntimeStartTimeout of 0, which nothing
// ends in. As text: JupyterLab reads an option of 0 as one that is not
// there.
//
//   node stall.mjs <deployment dir> [chromium|firefox|webkit]
import { webkit, chromium, firefox } from 'playwright';

import { serve } from './serve.mjs';
import { guarded, hung, MINUTE, watch } from './watch.mjs';

const engineName = process.argv[3] ?? 'chromium';
const browser = await { webkit, chromium, firefox }[engineName].launch();

let failures = 0;
const check = (ok, what, detail) => {
  if (!ok) failures++;
  console.log(`${ok ? 'ok  ' : 'FAIL'} [${engineName}] ${what}${ok ? '' : ' ' + JSON.stringify(detail).slice(0, 900)}`);
};

/** Opens the site with these options and runs each list of cells in a
 *  notebook of its own; answers each cell's reply, outputs and time. */
async function cells(options, notebooks) {
  const server = await serve(process.argv[2], options);
  const page = await (await browser.newContext()).newPage();
  const { diagnose } = watch(page, engineName);
  try {
    await page.goto(server.url + 'jupyter/lab/index.html');
    await page.waitForFunction(() => window.crossOriginIsolated === true
        && !!window.jupyterapp?.serviceManager?.contents
        && !!document.querySelector('#jp-main-dock-panel, .jp-LabShell'),
      null, { timeout: 180000, polling: 500 });
    await page.evaluate(() => window.jupyterapp.serviceManager.ready);
    return await guarded(page.evaluate(async (notebooks) => {
      const app = window.jupyterapp;
      const strip = (text) => String(text).replace(/\x1b\[[0-9;]*m/g, '');
      const within = (promise, ms, what) => Promise.race([promise,
        new Promise((_, no) => setTimeout(() => no(new Error(`${what} did not end in ${ms / 1000} s`)), ms))]);
      const answers = [];
      for (const [index, codes] of notebooks.entries()) {
        const path = `stall-${index}.ipynb`;
        await app.serviceManager.contents.save(path, { type: 'notebook', format: 'json', content: {
          cells: [], nbformat: 4, nbformat_minor: 5,
          metadata: { kernelspec: { name: 'shumway_prolog', display_name: 'Prolog (Shumway)', language: 'prolog' } } } });
        const panel = await app.commands.execute('docmanager:open', { path });
        await panel.context.ready;
        console.log(`[stall.mjs] the kernel of ${path}`);
        await within(panel.sessionContext.ready, 180000, `the kernel of ${path}`);
        for (const code of codes) {
          console.log(`[stall.mjs] the cell ${JSON.stringify(code)}`);
          const texts = [];
          const started = performance.now();
          const future = panel.sessionContext.session.kernel.requestExecute({ code });
          future.onIOPub = (msg) => {
            if (msg.header.msg_type === 'display_data') texts.push(strip(msg.content.data['text/plain'] ?? ''));
          };
          const reply = (await within(future.done, 180000, `the cell ${JSON.stringify(code)}`)).content;
          answers.push({ code, status: reply.status, ename: reply.ename, texts,
            seconds: Math.round((performance.now() - started) / 100) / 10 });
        }
      }
      return answers;
    }, notebooks), 8 * MINUTE, 'the cells');
  } catch (e) {
    if (hung(e)) await diagnose();
    throw e;
  } finally {
    await guarded(page.context().close(), MINUTE, 'closing the page').catch(() => {});
    server.close();
  }
}

try {
  // A query longer than the patience: the runtime is asked, answers, and
  // the cell is left to end.
  const [slow] = await cells({ shumwayRuntimePatience: '1' }, [['sleep(3), X = slept.']]);
  check(slow.status === 'ok' && slow.texts[0] === 'X = slept.',
    'a query that takes long ends, while the runtime answers', slow);

  // No call answers in time: the runtime is taken for stopped.
  const [first, again, other] = await cells({ shumwayRuntimePatience: '0.2', shumwayRuntimeTimeout: '0' },
    [['sleep(1), X = 1.', 'X = 2.'], ['X = 3.']]);
  const said = (answer) => answer.status === 'error' && answer.ename === 'runtime stopped'
    && /runtime stopped answering/.test(answer.texts.join('\n')) && /reloading the page/.test(answer.texts.join('\n'));
  check(said(first), 'a runtime that does not answer: the cell says to reload the page', first);
  check(said(again) && said(other) && other.seconds < 5,
    'and so does every cell after it, in every notebook of the page, at once', [again, other]);

  // A runtime that does not start in time.
  const [unstarted] = await cells({ shumwayRuntimeStartTimeout: '0' }, [['X = 1.']]);
  const told = unstarted.texts.join(' ');
  check(unstarted.status === 'error' && /did not start in time/.test(told) && /Reload the page/.test(told),
    'a runtime that does not start in time: the cell says to reload the page', unstarted);
} catch (e) {
  failures++;
  console.log(`[${engineName}] did not get there: ${String(e?.message).split('\n')[0]}`);
}
console.log(`[${engineName}] ${failures} failed`);
await guarded(browser.close(), MINUTE, 'closing the browser').catch((e) => console.log(e.message));
process.exit(failures ? 1 : 0);
