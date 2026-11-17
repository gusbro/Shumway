// The kernel in a real JupyterLab page of a built deployment: the demo at the
// root and the notebook site under lite/, served with no isolation headers,
// as GitHub Pages serves them.
//
//   node kernel.mjs <deployment dir> [chromium|firefox|webkit]
//
// The deployment is what `node jupyterlite/build.mjs --out <dir> --with-demo
// <publish wwwroot>` makes. Exits with 1 when a check fails.
import { webkit, chromium, firefox } from 'playwright';

import { serve } from './serve.mjs';

const engineName = process.argv[3] ?? 'chromium';
const server = await serve(process.argv[2]);
const base = server.url;

const browser = await { webkit, chromium, firefox }[engineName].launch();
const page = await (await browser.newContext()).newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message.split('\n')[0].slice(0, 200)));
const missing = new Set();
page.on('response', (r) => { if (r.status() === 404) missing.add(new URL(r.url()).pathname); });

let failures = 0;
const check = (ok, what, detail) => {
  if (!ok) failures++;
  console.log(`${ok ? 'ok  ' : 'FAIL'} [${engineName}] ${what}${ok ? '' : ' ' + JSON.stringify(detail).slice(0, 900)}`);
};

/** Opens a notebook, runs its cells, and answers each cell's outputs as text. */
const runNotebook = (file) => page.evaluate(async (path) => {
  const app = window.jupyterapp;
  const panel = await app.commands.execute('docmanager:open', { path });
  await panel.context.ready;
  await panel.sessionContext.ready;
  await app.commands.execute('notebook:run-all-cells');
  const strip = (text) => String(text).replace(/\x1b\[[0-9;]*m/g, '');
  const kernel = panel.sessionContext.session?.kernel;
  return {
    kernel: kernel?.name,
    info: await kernel?.info,
    cells: panel.content.model.toJSON().cells.filter((c) => c.cell_type === 'code').map((c) =>
      (c.outputs ?? []).map((o) => o.output_type === 'stream' ? strip([].concat(o.text).join(''))
        : o.output_type === 'error' ? 'ERROR ' + o.ename
        : Object.fromEntries(Object.entries(o.data ?? {}).map(([mime, d]) =>
            [mime, typeof d === 'string' || Array.isArray(d) ? strip([].concat(d).join('')) : d])))),
  };
}, file);

try {
  await page.goto(base + 'lite/lab/index.html');
  await page.waitForFunction(() => window.crossOriginIsolated === true
      && !!window.jupyterapp?.serviceManager?.contents
      && !!document.querySelector('#jp-main-dock-panel, .jp-LabShell'),
    null, { timeout: 180000, polling: 500 });
  await page.evaluate(() => window.jupyterapp.serviceManager.ready);
  const specs = await page.evaluate(async () => {
    const s = window.jupyterapp.serviceManager.kernelspecs;
    await s.ready;
    return { default: s.specs?.default, names: Object.keys(s.specs?.kernelspecs ?? {}) };
  });
  check(specs.names.includes('shumway_prolog') && specs.default === 'shumway_prolog',
    'the kernel is registered, and is the default', specs);

  const first = await runNotebook('first-steps.ipynb');
  check(first.kernel === 'shumway_prolog', 'the notebook starts it without asking', first.kernel);
  check(first.cells[1]?.[0]?.['text/plain'] === 'Who = tom', 'a %%user cell and a query on it', first.cells);
  check(first.info?.implementation === 'shumway' && first.info?.language_info?.name === 'Prolog'
      && /^\d+\.\d+\.\d+/.test(first.info?.language_info?.version ?? '') && /WebAssembly/.test(first.info?.banner ?? ''),
    'it says what it is: Prolog, on Shumway, with its version and tier', first.info);

  // Everything else straight to the kernel of that notebook, a cell at a time.
  const report = await page.evaluate(async () => {
    const app = window.jupyterapp;
    const contents = app.serviceManager.contents;
    const lines = [];
    const check = (ok, what, detail) => lines.push({ ok: !!ok, what, detail: ok ? undefined : detail });
    const strip = (text) => String(text).replace(/\x1b\[[0-9;]*m/g, '');
    const kernelOf = (panel) => panel.sessionContext.session.kernel;
    const panel = app.shell.currentWidget;

    /** Runs code as a cell would; answers the outputs and the reply. */
    async function run(kernel, code, answerInput) {
      const outputs = [];
      const future = kernel.requestExecute({ code, allow_stdin: true, store_history: true });
      future.onIOPub = (msg) => {
        if (msg.header.msg_type === 'display_data' || msg.header.msg_type === 'execute_result')
          outputs.push(msg.content.data);
        else if (msg.header.msg_type === 'stream') outputs.push({ stream: msg.content.text });
      };
      future.onStdin = (msg) => {
        if (msg.header.msg_type === 'input_request')
          future.sendInputReply({ status: 'ok', value: answerInput ?? '' }, msg.header);
      };
      const reply = await future.done;
      return { reply: reply.content, outputs,
        texts: outputs.map((o) => strip(o['text/plain'] ?? o.stream ?? '')) };
    }
    const within = (promise, ms, what) => Promise.race([promise,
      new Promise((_, no) => setTimeout(() => no(new Error(what + ' did not end in ' + ms + ' ms')), ms))]);

    try {
      const kernel = kernelOf(panel);
      let r = await run(kernel, 'X = 1, Y = f(X).');
      check(r.reply.status === 'ok' && r.texts[0] === 'X = 1,\nY = f(1)', 'bindings, one to a line', r);

      r = await run(kernel, 'member(X, [a, b]).');
      const again = await run(kernel, 'retry.');
      check(r.texts[0] === 'X = a' && again.texts[0] === 'X = b', 'retry gives the next solution', [r, again]);

      r = await run(kernel, 'write(hello), nl, X = 1.\nwrite(second).');
      check(JSON.stringify(r.texts) === '["hello","X = 1","second","true"]',
        'a cell with two queries: output, then the answer, of each', r.texts);

      r = await run(kernel, 'fail.');
      check(r.reply.status === 'error' && r.reply.ename === 'failure' && r.texts[0] === 'false',
        'a failure is said and the cell is an error', r);

      r = await run(kernel, 'X is foo + 1.');
      check(r.reply.status === 'error' && r.reply.ename === 'exception' && /foo/.test(r.texts[0] ?? ''),
        "an exception shows the engine's own message", r);

      r = await run(kernel, 'X = .');
      check(r.reply.status === 'error' && r.texts.length === 1 && r.texts[0].length > 0,
        'a cell that does not parse is an error, with its message', r);

      r = await run(kernel, 'jupyter:print_table(member(X, [a, b])).');
      check(r.outputs[0]?.['text/markdown'] === 'X | \n:- | \na | \nb | ' && r.texts[1] === 'true',
        'a table is markdown, and text beside it', r);

      r = await run(kernel, "jupyter:display('text/html', '<b>hi</b>'), "
        + "jupyter:display('application/json', '{\"a\": 1}').");
      check(r.outputs[0]?.['text/html'] === '<b>hi</b>' && r.outputs[0]?.['text/plain'] === '<b>hi</b>'
          && r.outputs[1]?.['application/json']?.a === 1,
        'jupyter:display/2 shows each thing under its MIME type, JSON as an object', r);

      await run(kernel, 'use_module(library(clpfd)).');
      r = await run(kernel, 'X in 1..5, X #> 2.');
      check(r.texts[0] === 'X in 3..5', 'residual goals are shown where bindings would be', r);

      r = await run(kernel, 'read(X).', 'foo(bar).');
      check(r.reply.status === 'ok' && r.texts[0] === 'X = foo(bar)', 'read/1 asks the notebook for a line', r);

      const completed = (await kernel.requestComplete({ code: 'X = 1, grandp', cursor_pos: 13 })).content;
      check(completed.matches?.[0] === 'grandparent(A,B)' && completed.cursor_start === 7,
        'a predicate the notebook defined completes', completed);
      const inspected = (await kernel.requestInspect({ code: 'jupyter:print_table(', cursor_pos: 12, detail_level: 0 })).content;
      check(inspected.found && /Shows a table/.test(inspected.data?.['text/plain'] ?? ''),
        'a helper is documented where it is written', inspected);

      // Files, both ways.
      await contents.save('facts.pl', { type: 'file', format: 'text', content: 'colour(red).\n' });
      r = await run(kernel, 'consult(facts), colour(C).');
      check(r.texts.at(-1) === 'C = red', 'a file saved in the editor is there for consult/1', r);
      await run(kernel, "open('out.txt', write, S), write(S, hello), close(S).");
      const out = await contents.get('out.txt', { content: true }).catch((e) => ({ error: String(e) }));
      const listed = (await contents.get('', { content: true })).content.map((c) => c.name).sort();
      check(out.content === 'hello' && listed.includes('user.pl'),
        'a file a cell wrote is in the file browser', [out, listed]);

      // A query that never ends, interrupted.
      const endless = run(kernel, 'repeat, fail.');
      await new Promise((resolve) => setTimeout(resolve, 1500));
      await kernel.interrupt();
      r = await within(endless, 15000, 'the interrupted cell');
      check(r.reply.status === 'error' && /Interrupted/.test(r.texts.at(-1) ?? ''),
        'an endless query is interrupted, and the notebook says the session ended', r);
      r = await run(kernel, 'grandparent(X, ann).');
      const fresh = await run(kernel, 'X = again.');
      check(r.reply.status === 'error' && fresh.texts[0] === 'X = again',
        'the next cell has a new session, with nothing loaded', [r, fresh]);

      r = await run(kernel, 'halt.');
      const afterHalt = await run(kernel, 'X = after.');
      check(/Halted/.test(r.texts[0] ?? '') && afterHalt.texts[0] === 'X = after',
        'halt ends the session and the next cell starts another', [r, afterHalt]);

      // A second notebook, in a directory of its own, beside the first.
      const folder = await contents.newUntitled({ type: 'directory' });
      await contents.rename(folder.path, 'sub');
      await contents.save('sub/second.ipynb', { type: 'notebook', format: 'json', content: {
        cells: [], nbformat: 4, nbformat_minor: 5,
        metadata: { kernelspec: { name: 'shumway_prolog', display_name: 'Prolog (Shumway)', language: 'prolog' } } } });
      const second = await app.commands.execute('docmanager:open', { path: 'sub/second.ipynb' });
      await second.context.ready;
      await second.sessionContext.ready;
      const other = kernelOf(second);
      await run(other, '%%user\nonly_here(1).');
      r = await run(other, 'only_here(X), working_directory(D, D).');
      const elsewhere = await run(kernel, 'only_here(X).');
      check(/X = 1/.test(r.texts[0] ?? '') && /\/drive\/sub/.test(r.texts[0] ?? '') && elsewhere.reply.status === 'error'
          && other.id !== kernel.id,
        'two notebooks have a session each, each in its directory', [r, elsewhere]);
      const subFiles = (await contents.get('sub', { content: true })).content.map((c) => c.name).sort();
      check(subFiles.includes('user.pl'), 'and the files of each are in its directory', subFiles);

      await kernel.restart();
      r = await run(kernelOf(panel), 'X = restarted.');
      check(r.texts[0] === 'X = restarted', 'a restarted kernel answers', r);

      // The editor knows the language: a comment is what `%` starts. The
      // language is loaded when an editor first asks for it.
      const comment = (editor) => editor.editor.state.languageDataAt('commentTokens', 0)[0]?.line;
      const settled = async (read) => {
        for (let tries = 0; tries < 40 && read() !== '%'; tries++)
          await new Promise((resolve) => setTimeout(resolve, 250));
        return read();
      };
      const cell = panel.content.widgets.find((widget) => widget.model.type === 'code');
      check(await settled(() => comment(cell.editor)) === '%' && cell.model.mimeType === 'text/x-prolog',
        'a cell is edited as Prolog', [cell.model.mimeType, comment(cell.editor)]);
      const file = await app.commands.execute('docmanager:open', { path: 'facts.pl' });
      await file.context.ready;
      check(await settled(() => comment(file.content.editor)) === '%',
        'and so is a .pl file, which JupyterLab takes for Perl', [file.content.model.mimeType]);
    } catch (ex) {
      check(false, 'the checks ran to their end', String(ex?.stack ?? ex));
    }
    return lines;
  });
  for (const line of report) check(line.ok, line.what, line.detail);
} catch (e) {
  failures++;
  console.log(`[${engineName}] did not get there: ${e.message.split('\n')[0]}`);
}
if (errors.length) console.log('page errors:', errors.slice(0, 6));
if (missing.size) console.log('not found:', [...missing].slice(0, 8));
console.log(`[${engineName}] ${failures} failed`);
await browser.close();
server.close();
process.exit(failures ? 1 : 0);
