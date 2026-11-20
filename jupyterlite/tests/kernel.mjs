// The kernel in a real JupyterLab page of a built deployment: the demo at the
// root and the notebook site under jupyter/, served with no isolation headers,
// as GitHub Pages serves them.
//
//   node kernel.mjs <deployment dir> [chromium|firefox|webkit]
//
// The deployment is what `node jupyterlite/build.mjs --out <dir> --with-demo
// <publish wwwroot>` makes. Exits with 1 when a check fails.
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import zlib from 'node:zlib';
import { webkit, chromium, firefox } from 'playwright';

import { serve } from './serve.mjs';
import { guarded, hung, Hung, MINUTE, watch } from './watch.mjs';

const engineName = process.argv[3] ?? 'chromium';
const server = await serve(process.argv[2]);
const base = server.url;

/** A .tar.gz of [name, text] entries, as a library's release is packed. */
function tarGz(entries) {
  const blocks = [];
  for (const [name, text] of entries) {
    const data = Buffer.from(text, 'utf8');
    const header = Buffer.alloc(512);
    header.write(name, 0, 100);
    header.write('0000644\0', 100);
    header.write('0000000\0', 108);
    header.write('0000000\0', 116);
    header.write(data.length.toString(8).padStart(11, '0') + '\0', 124);
    header.write('00000000000\0', 136);
    header.write('        ', 148);
    header.write('0', 156);
    header.write('ustar\0', 257);
    header.write('00', 263);
    let sum = 0;
    for (const byte of header) sum += byte;
    header.write(sum.toString(8).padStart(6, '0') + '\0 ', 148);
    blocks.push(header, data, Buffer.alloc((512 - (data.length % 512)) % 512));
  }
  blocks.push(Buffer.alloc(1024));
  return zlib.gzipSync(Buffer.concat(blocks));
}

// Another site, for jupyter:import_library/1 from a URL: under /cors/ it lets
// other sites read its files, under /nocors/ it does not.
const remoteFiles = {
  '/cors/o3one.pl': ':- module(o3one, [one/1]).\none(1).\n',
  '/other/o3one.pl': ':- module(o3one, [one/1]).\none(2).\n',
  '/nocors/o3hidden.pl': ':- module(o3hidden, []).\n',
  '/cors/o3url.tar.gz': tarGz([['o3url-1.0/o3_url.pl', ':- module(o3_url, [url_ok/1]).\nurl_ok(yes).\n'],
    ['o3url-1.0/README', 'not a source']]),
};
const remote = http.createServer((request, response) => {
  const path = new URL(request.url, 'http://x').pathname;
  const body = remoteFiles[path];
  const headers = path.startsWith('/nocors/') ? {} : { 'Access-Control-Allow-Origin': '*' };
  if (body === undefined) { response.writeHead(404, headers); response.end(); return; }
  response.writeHead(200, { ...headers, 'Content-Type': 'application/octet-stream' });
  response.end(body);
});
await new Promise((resolve) => remote.listen(0, '127.0.0.1', resolve));
const remoteUrl = `http://localhost:${remote.address().port}/`;

const browser = await { webkit, chromium, firefox }[engineName].launch();
const page = await (await browser.newContext()).newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message.split('\n')[0].slice(0, 200)));
const missing = new Set();
page.on('response', (r) => { if (r.status() === 404) missing.add(new URL(r.url()).pathname); });
// The script with the graph drawer (graph.ts and dagre), which only a graph
// should bring: found by a string of dagre's own.
const drawerScripts = [];
(function find(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const file = path.join(dir, entry.name);
    if (entry.isDirectory()) find(file);
    else if (entry.name.endsWith('.js') && fs.readFileSync(file, 'utf8').includes('network-simplex'))
      drawerScripts.push(entry.name);
  }
})(path.join(process.argv[2], 'jupyter', 'extensions'));
const fetched = new Set();
page.on('request', (r) => fetched.add(path.posix.basename(new URL(r.url()).pathname)));
const { diagnose } = watch(page, engineName);

let failures = 0;
const check = (ok, what, detail) => {
  if (!ok) failures++;
  console.log(`${ok ? 'ok  ' : 'FAIL'} [${engineName}] ${what}${ok ? '' : ' ' + JSON.stringify(detail).slice(0, 900)}`);
};

/** Opens a notebook, runs its cells, and answers each cell's outputs as text. */
const runNotebook = (file) => guarded(page.evaluate(async (path) => {
  const within = (promise, ms, what) => Promise.race([promise,
    new Promise((_, no) => setTimeout(() => no(new Error(`${what} did not end in ${ms / 1000} s`)), ms))]);
  const app = window.jupyterapp;
  console.log(`[kernel.mjs] open ${path}`);
  const panel = await within(app.commands.execute('docmanager:open', { path }), 60000, `opening ${path}`);
  await within(panel.context.ready, 60000, `the document ${path}`);
  await within(panel.sessionContext.ready, 180000, `the kernel of ${path}`);
  console.log(`[kernel.mjs] run the cells of ${path}`);
  await within(app.commands.execute('notebook:run-all-cells'), 300000, `the cells of ${path}`);
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
}, file), 8 * MINUTE, `the notebook ${file}`);

/** Shuts down a notebook's kernel and closes it, so later ones fit under the page's limit. */
const closeNotebook = (file) => guarded(page.evaluate(async (path) => {
  const widget = [...window.jupyterapp.shell.widgets('main')].find((w) => w.context?.path === path);
  await widget?.sessionContext.shutdown();
  widget?.dispose();
}, file), MINUTE, `closing ${file}`);

try {
  await page.goto(base + 'jupyter/lab/index.html');
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
  const report = await guarded(page.evaluate(async () => {
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
      const cell = JSON.stringify(code).slice(0, 100);
      console.log(`[kernel.mjs] cell ${cell}`);
      const reply = await within(future.done, 90000, `the cell ${cell}`);
      return { reply: reply.content, outputs,
        texts: outputs.map((o) => strip(o['text/plain'] ?? o.stream ?? '')) };
    }
    const within = (promise, ms, what) => Promise.race([promise,
      new Promise((_, no) => setTimeout(() => no(new Error(`${what} did not end in ${ms / 1000} s`)), ms))]);
    /** A notebook's kernel, once it has one. */
    const opened = async (widget, what) => {
      await within(widget.sessionContext.ready, 180000, `the kernel of ${what}`);
      return widget;
    };
    let stuck = null;

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

      // The rest of the feature table's cells (docs/design/jupyterlite-features.yaml).
      const fileText = async (path) => (await contents.get(path, { content: true }).catch(() => ({ content: null }))).content;
      const shown = (answer) => answer.texts.join('\n');
      await run(kernel, 'X = 3.');
      r = await run(kernel, 'Y is $X + 1.');
      check(/^Y = 4,/.test(r.texts[0] ?? ''), '$X is the value X had in an earlier query', r);
      r = await run(kernel, 'p(X) :- q(X).');
      check(r.reply.status === 'error' && /%%user/.test(shown(r)), 'a clause in a query cell is an error that points to %%user', r);
      r = await run(kernel, 'jupyter:trace(member(X, [a])).');
      check(r.reply.status === 'ok' && /Call/.test(shown(r)) && /member/.test(shown(r)), 'jupyter:trace/1 shows the ports of its goal', r);
      r = await run(kernel, 'jupyter:help.');
      check(/print_table/.test(shown(r)), 'jupyter:help/0 lists the helpers', r);
      r = await run(kernel, 'jupyter:magic.');
      check(/%%user/.test(shown(r)) && /%%file/.test(shown(r)), 'jupyter:magic/0 lists the magics', r);
      r = await run(kernel, 'jupyter:versions.');
      const versioned = await run(kernel, 'jupyter:version(Ma, Mi, Pa, _).');
      check(/Shumway Prolog \d+\.\d+\.\d+/.test(shown(r)) && /Ma = \d+/.test(shown(versioned)),
        'jupyter:versions/0 and jupyter:version/4 say the version', [r, versioned]);
      r = await run(kernel, 'jupyter:pwd.');
      check(/Working directory: '?\/drive/.test(shown(r)), "jupyter:pwd/0 says the session's directory", r);
      r = await run(kernel, 'jupyter:print_queries.');
      check(/^X=3,$/m.test(shown(r)) && /^jupyter:help,$/m.test(shown(r)), 'jupyter:print_queries/0 shows the queries that ran', r);
      r = await run(kernel, 'jupyter:print_query_time.');
      check(/Runtime: [\d.e-]+ s/.test(shown(r)), 'jupyter:print_query_time/0 shows how long the last query took', r);
      r = await run(kernel, 'jupyter:print_variable_bindings.');
      check(/\$Y = 4/.test(shown(r)), 'jupyter:print_variable_bindings/0 shows the values $Var reads', r);
      await run(kernel, "jupyter:print_and_save_table(member(N-S, [1-1, 2-4]), csv, 'squares.csv').");
      let saved = await fileText('squares.csv');
      check(saved === 'N,S\n1,1\n2,4\n', 'jupyter:print_and_save_table/3 writes the table to a file', saved);

      await run(kernel, '%%file colours.pl\ncolour2(blue).');
      await run(kernel, '%%file+ colours.pl\ncolour2(green).');
      r = await run(kernel, 'findall(C, colour2(C), L).');
      const loaded = await run(kernel, '%%load colours.pl\ncolour2(grey).');
      const reloaded = await run(kernel, 'findall(C, colour2(C), L).');
      await run(kernel, '%%save notes.txt\nnot loaded (');
      saved = await fileText('notes.txt');
      check(r.texts[0] === 'L = [blue, green]' && loaded.reply.status === 'ok' && reloaded.texts[0] === 'L = [grey]'
          && saved === 'not loaded (\n',
        '%%file, %%file+ and %%load write and load a file; %%save only writes it', [r, reloaded, saved]);
      r = await run(kernel, '%%highlight\nfoo(X) :- bar(X).');
      check(r.reply.status === 'ok' && r.outputs.length === 0, '%%highlight runs nothing', r);
      r = await run(kernel, '%%table\nmember(X-Y, [a-1, b-2]).');
      await run(kernel, '%%csv letters.csv\nmember(X, [a, b]).');
      saved = await fileText('letters.csv');
      check(r.outputs[0]?.['text/markdown'] === 'X | Y | \n:- | :- | \na | 1 | \nb | 2 | ' && saved === 'X\na\nb\n',
        '%%table shows a table, and %%csv writes it too', [r, saved]);
      for (const [magic, what] of [['%bindings', /\$Y = 4/], ['%queries', /X=3/], ['%flags', /double_quotes/],
        ['%help', /print_table/], ['%magic', /%%user/], ['%pwd', /\/drive/], ['%versions', /Shumway Prolog/]]) {
        r = await run(kernel, magic);
        const all = shown(r) + JSON.stringify(r.outputs);
        check(r.reply.status === 'ok' && what.test(all), `the line magic ${magic}`, r);
      }

      // Files, both ways.
      await contents.save('facts.pl', { type: 'file', format: 'text', content: 'colour(red).\n' });
      r = await run(kernel, 'consult(facts), colour(C).');
      check(r.texts.at(-1) === 'C = red', 'a file saved in the editor is there for consult/1', r);
      await run(kernel, "open('out.txt', write, S), write(S, hello), close(S).");
      const out = await contents.get('out.txt', { content: true }).catch((e) => ({ error: String(e) }));
      const listed = (await contents.get('', { content: true })).content.map((c) => c.name).sort();
      check(out.content === 'hello' && listed.includes('first-steps.user.pl') && !listed.includes('user.pl'),
        "a file a cell wrote is in the file browser, and %%user writes the notebook's own", [out, listed]);

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
      await opened(second, 'sub/second.ipynb');
      const other = kernelOf(second);
      await run(other, '%%user\nonly_here(1).');
      r = await run(other, 'only_here(X), working_directory(D, D).');
      const elsewhere = await run(kernel, 'only_here(X).');
      check(/X = 1/.test(r.texts[0] ?? '') && /\/drive\/sub/.test(r.texts[0] ?? '') && elsewhere.reply.status === 'error'
          && other.id !== kernel.id,
        'two notebooks have a session each, each in its directory', [r, elsewhere]);
      const subFiles = (await contents.get('sub', { content: true })).content.map((c) => c.name).sort();
      check(subFiles.includes('second.user.pl'), 'and the files of each are in its directory', subFiles);
      r = await run(other, "jupyter:cd('..'), jupyter:pwd.");
      check(/Working directory: '?\/drive\/?'?$/m.test(r.texts[0] ?? '') && !/sub/.test(r.texts[0] ?? ''),
        "jupyter:cd/1 changes the session's directory", r);

      // A third notebook beside the first: each keeps its own %%user file,
      // and %%user+ loads only what its own cells wrote.
      const text = async (path) => (await contents.get(path, { content: true }).catch(() => ({ content: null }))).content;
      await contents.save('third.ipynb', { type: 'notebook', format: 'json', content: {
        cells: [], nbformat: 4, nbformat_minor: 5,
        metadata: { kernelspec: { name: 'shumway_prolog', display_name: 'Prolog (Shumway)', language: 'prolog' } } } });
      const third = await app.commands.execute('docmanager:open', { path: 'third.ipynb' });
      await third.context.ready;
      await opened(third, 'third.ipynb');
      const thirdKernel = kernelOf(third);
      await run(thirdKernel, '%%user\nonly_third(1).');
      await run(thirdKernel, '%%user+\nonly_third(2).');
      let own = await text('third.user.pl');
      const firsts = await text('first-steps.user.pl');
      check(own === 'only_third(1).\nonly_third(2).\n' && /grandparent/.test(firsts ?? '') && !/only_third/.test(firsts),
        'two notebooks in one directory keep their %%user files apart', [own, firsts]);

      // Renamed while its kernel runs, a notebook takes its file along.
      await contents.rename('third.ipynb', 'renamed.ipynb');
      const sessions = app.serviceManager.sessions;
      for (let tries = 0; tries < 40; tries++) {
        await sessions.refreshRunning();
        if ([...sessions.running()].some((m) => m.kernel?.id === thirdKernel.id && m.path.endsWith('renamed.ipynb'))) break;
        await new Promise((resolve) => setTimeout(resolve, 250));
      }
      await run(thirdKernel, '%%user+\nonly_third(3).');
      r = await run(thirdKernel, 'findall(X, only_third(X), L).');
      own = await text('renamed.user.pl');
      check(own === 'only_third(1).\nonly_third(2).\nonly_third(3).\n' && (await text('third.user.pl')) === null
          && r.texts[0] === 'L = [1, 2, 3]',
        'a renamed notebook takes its %%user file along', [own, r.texts]);

      // A console has no notebook: its file is user.pl.
      const shell = await app.commands.execute('console:create', { kernelPreference: { name: 'shumway_prolog' } });
      await opened(shell, 'the console');
      await run(shell.sessionContext.session.kernel, '%%user\nfrom_console(1).');
      own = await text('user.pl');
      check(own === 'from_console(1).\n', 'a console writes user.pl', own);
      // Rewritten at once, to the same size: the file browser has the second.
      await run(shell.sessionContext.session.kernel, '%%user\nfrom_console(2).');
      own = await text('user.pl');
      check(own === 'from_console(2).\n', 'a file rewritten within a second is copied as the second cell left it', own);

      // A notebook saved by hhu-stups' Prolog kernel opens with this one,
      // without a dialog that asks for a kernel (ADR-066).
      await contents.save('other-kernel.ipynb', { type: 'notebook', format: 'json', content: {
        cells: [], nbformat: 4, nbformat_minor: 5,
        metadata: { kernelspec: { name: 'prolog_kernel', display_name: 'Prolog', language: 'prolog' },
          language_info: { name: 'Prolog', file_extension: '.pl', mimetype: 'text/x-prolog', codemirror_mode: 'prolog' } } } });
      const foreign = await app.commands.execute('docmanager:open', { path: 'other-kernel.ipynb' });
      await foreign.context.ready;
      await Promise.race([foreign.sessionContext.ready, new Promise((resolve) => setTimeout(resolve, 20000))]);
      const dialog = document.querySelector('.jp-Dialog');
      r = foreign.sessionContext.session?.kernel ? await run(foreign.sessionContext.session.kernel, 'X = here.') : null;
      check(foreign.sessionContext.session?.kernel?.name === 'shumway_prolog' && !dialog && r?.texts[0] === 'X = here',
        "a notebook of hhu-stups' kernel opens with this one, and nothing asks which",
        [foreign.sessionContext.session?.kernel?.name, dialog?.textContent?.slice(0, 200), r?.texts]);

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
      if (/did not end in/.test(String(ex?.message))) stuck = ex.message;
      else check(false, 'the checks ran to their end', String(ex?.stack ?? ex));
    }
    return { lines, stuck };
  }), 15 * MINUTE, 'the kernel checks');
  for (const line of report.lines) check(line.ok, line.what, line.detail);
  if (report.stuck) throw new Hung(report.stuck);

  // Library collections (ADR-070, O3): the ones the browser keeps for the
  // demo, and jupyter:import_library/1. A notebook of its own.
  const archive = tarGz([['o3drive/o3_tar.pl', ':- module(o3_tar, [tar_ok/1]).\ntar_ok(yes).\n']]).toString('base64');
  const libraryReport = await guarded(page.evaluate(async ({ remote, archive }) => {
    const app = window.jupyterapp;
    const contents = app.serviceManager.contents;
    const lines = [];
    const check = (ok, what, detail) => lines.push({ ok: !!ok, what, detail: ok ? undefined : detail });
    const strip = (text) => String(text).replace(/\x1b\[[0-9;]*m/g, '');
    async function run(kernel, code) {
      const outputs = [];
      const future = kernel.requestExecute({ code, store_history: true });
      future.onIOPub = (msg) => {
        if (msg.header.msg_type === 'display_data' || msg.header.msg_type === 'execute_result')
          outputs.push(msg.content.data);
        else if (msg.header.msg_type === 'stream') outputs.push({ stream: msg.content.text });
      };
      const cell = JSON.stringify(code).slice(0, 100);
      console.log(`[kernel.mjs] cell ${cell}`);
      const reply = await within(future.done, 90000, `the cell ${cell}`);
      return { reply: reply.content, texts: outputs.map((o) => strip(o['text/plain'] ?? o.stream ?? '')) };
    }
    const within = (promise, ms, what) => Promise.race([promise,
      new Promise((_, no) => setTimeout(() => no(new Error(`${what} did not end in ${ms / 1000} s`)), ms))]);
    let stuck = null;
    const shown = (r) => r.texts.join('\n');
    // What the browser keeps, as the demo keeps it.
    const stored = async (path) => {
      let here = await (await navigator.storage.getDirectory()).getDirectoryHandle('libraries');
      const parts = path.split('/');
      try {
        for (const part of parts.slice(0, -1)) here = await here.getDirectoryHandle(part);
        return await (await (await here.getFileHandle(parts.at(-1))).getFile()).text();
      } catch { return null; }
    };
    const store = async (path, text) => {
      let here = await (await navigator.storage.getDirectory()).getDirectoryHandle('libraries', { create: true });
      const parts = path.split('/');
      for (const part of parts.slice(0, -1)) here = await here.getDirectoryHandle(part, { create: true });
      const writable = await (await here.getFileHandle(parts.at(-1), { create: true })).createWritable();
      await writable.write(text);
      await writable.close();
    };
    // A browser whose storage cannot be written (the Playwright build of
    // WebKit on Windows) still imports; what it keeps is not checked there.
    const writable = await (async () => {
      try {
        const file = await (await navigator.storage.getDirectory()).getFileHandle('o3-probe', { create: true });
        const writer = await file.createWritable();
        await writer.write('x');
        await writer.close();
        return true;
      } catch { return false; }
    })();
    const skipped = [];

    try {
      await contents.save('libraries.ipynb', { type: 'notebook', format: 'json', content: {
        cells: [], nbformat: 4, nbformat_minor: 5,
        metadata: { kernelspec: { name: 'shumway_prolog', display_name: 'Prolog', language: 'prolog' } } } });
      const panel = await app.commands.execute('docmanager:open', { path: 'libraries.ipynb' });
      await panel.context.ready;
      await within(panel.sessionContext.ready, 180000, 'the kernel of libraries.ipynb');
      const kernel = panel.sessionContext.session.kernel;
      let r = await run(kernel, 'X = ready.');
      check(r.texts[0] === 'X = ready', 'the libraries notebook has a kernel', r);

      // The demo imports a collection while this notebook is open.
      if (writable) {
        await store('o3stored/src/o3_stored.pl', ':- module(o3_stored, [stored/1]).\nstored(yes).\n');
        r = await run(kernel, 'use_module(library(o3_stored)), stored(X).');
        check(r.texts.includes('X = yes'), 'a collection the demo stored while the notebook is open is there for the next cell', r);
        check(!!(await stored('o3stored/bundles/o3_stored')),
          'and what the cell compiled to use it is kept for the demo and the next visit', r);
      } else skipped.push('a collection the demo stored is there for the next cell, and what it compiled is kept');

      // From an archive among the notebook's files.
      await contents.save('o3drive.tar.gz', { type: 'file', format: 'base64', content: archive });
      r = await run(kernel, "jupyter:import_library(swi:'o3drive.tar.gz').");
      check(r.reply.status === 'ok' && /o3drive: 1 library from o3drive\.tar\.gz, dialect swi/.test(shown(r)),
        'import_library brings in an archive of the notebook', r);
      r = await run(kernel, 'use_module(library(o3_tar)), tar_ok(X).');
      check(r.texts.includes('X = yes'), 'and its libraries load', r);
      if (writable) {
        check((await stored('o3drive/.dialect'))?.trim() === 'swi' && !!(await stored('o3drive/src/o3_tar.pl'))
            && /o3drive\.tar\.gz$/.test((await stored('o3drive/.source')) ?? ''),
          'the browser keeps it, with its dialect and where it came from',
          [await stored('o3drive/.dialect'), await stored('o3drive/.source')]);
      } else skipped.push('the browser keeps an imported collection, with its dialect and where it came from');

      // From another site that lets other sites read it.
      r = await run(kernel, `jupyter:import_library('${remote}cors/o3one.pl').`);
      check(r.reply.status === 'ok' && /o3one: 1 library from localhost/.test(shown(r)),
        'import_library brings in a Prolog file from a URL', r);
      r = await run(kernel, 'use_module(library(o3one)), one(X).');
      check(r.texts.includes('X = 1'), 'and it loads', r);
      r = await run(kernel, `jupyter:import_library(scryer:'${remote}cors/o3url.tar.gz').`);
      check(r.reply.status === 'ok' && /o3url: 1 library from localhost.*dialect scryer/.test(shown(r)),
        'and an archive from a URL, with a dialect', r);
      r = await run(kernel, 'use_module(library(o3_url)), url_ok(X).');
      check(r.texts.includes('X = yes'), 'whose libraries load', r);

      // Again from the same place: nothing to fetch.
      r = await run(kernel, `jupyter:import_library('${remote}cors/o3one.pl').`);
      check(r.reply.status === 'ok' && /o3one is here already, from http/.test(shown(r)),
        'importing again from the same place leaves the collection as it is', r);
      r = await run(kernel, `jupyter:import_library('${remote}other/o3one.pl').`);
      check(r.reply.status === 'error' && /a collection named o3one is here already, from http/.test(shown(r)),
        'the same name from somewhere else is refused, and says where the one here came from', r);
      r = await run(kernel, `jupyter:import_library('${remote}nocors/o3hidden.pl').`);
      check(r.reply.status === 'error' && /CORS/.test(shown(r)),
        'a site that does not let other sites read it is named as the reason', r);

      // A dialect's library, here already from where the page suggests it:
      // nothing is fetched from GitHub.
      const scryer = 'https://github.com/mthom/scryer-prolog/tree/master/src/lib';
      if (writable) {
        await store('scryer/.dialect', 'scryer');
        await store('scryer/.source', scryer);
        await store('scryer/src/o3_placeholder.pl', ':- module(o3_placeholder, []).\n');
        r = await run(kernel, 'jupyter:import_library(scryer).');
        check(r.reply.status === 'ok' && shown(r).includes(`scryer is here already, from ${scryer}`),
          'import_library(scryer) is the library the page suggests, and one here is not fetched again', r);
      } else skipped.push('import_library(scryer) is the library the page suggests, and one here is not fetched again');
    } catch (ex) {
      if (/did not end in/.test(String(ex?.message))) stuck = ex.message;
      else check(false, 'the library checks ran to their end', `${ex?.name}: ${ex?.message} ${ex?.stack ?? ''}`);
    }
    return { lines, skipped, stuck };
  }, { remote: remoteUrl, archive }), 10 * MINUTE, 'the library checks');
  for (const line of libraryReport.lines) check(line.ok, line.what, line.detail);
  if (libraryReport.stuck) throw new Hung(libraryReport.stuck);
  for (const what of libraryReport.skipped)
    console.log(`skip [${engineName}] ${what}: this browser's storage cannot be written`);

  check(drawerScripts.length === 1 && !fetched.has(drawerScripts[0]),
    'the graph drawer is not downloaded before a notebook draws a graph', drawerScripts);

  // Where a drawn graph writes a node's text, and the edges it draws.
  const placeOf = (svg, text) => {
    const m = new RegExp(`<tspan x="([\\d.]+)" y="([\\d.]+)">${text}</tspan>`).exec(svg);
    return m ? { x: +m[1], y: +m[2] } : { x: NaN, y: NaN };
  };
  const pathsOf = (svg) => [...svg.matchAll(/<path d="M([\d.]+),([\d.]+)C[^"]* ([\d.]+),([\d.]+)"/g)]
    .map((m) => ({ x1: +m[1], y1: +m[2], x2: +m[3], y2: +m[4] }));
  const drawingsOf = (cells) => cells.flat().map((o) => o?.['image/svg+xml']).filter((svg) => typeof svg === 'string');

  // The example notebooks run from top to bottom, and show what they say.
  for (const [file, expected] of [
    ['grammars.ipynb', ['Who = world', 'Who = prolog', 'V = 9', 'Text | Value', "'(1+2)*3' | 9 "]],
    ['constraints.ipynb', ['X in 4..5', 'X = 8', 'X = 9', 'L = [_A, _B, _C]', 'L = []', 'L = [_A]',
      'A = [_A, _B, _C, _D],\\nB = [ann, bob]',
      '[1,1] | 2 ', 'P = ([9, 5, 6, 7]+[1, 0, 8, 5]=[1, 0, 6, 5, 2])', 'Qs | ', '[2,4,6,1,3,5] | ', '[5,3,1,6,4,2] | ']],
    ['tables-and-pictures.ipynb', ['N | Square | Power', '6 | 36 | 64 ', 'two', '<svg', 'width=\\"256\\"']],
    ['graphs.ipynb', ['<svg', 'text/vnd.graphviz', '\\"person(ann,born(1990,may),[prolog,logic])\\"',
      'label=\\"born\\"', '\\"1+2*3-4\\" [shape=\\"rect\\", label=\\"-\\"]',
      '\\"idle\\" -> \\"ready\\" [label=\\"coin\\", color=\\"black\\", style=\\"solid\\"]',
      '\\"ready\\" [label=\\"Ready\\", shape=\\"box\\", style=\\"filled\\", fillcolor=\\"lightyellow\\"]',
      'stroke-dasharray']],
    ['plots.ipynb', ['application/vnd.vegalite.v5+json', 'Fruit sold', 'Histogram', 'Squares', 'Languages',
      '"type":"boxplot"', '"type":"arc"', '"interpolate":"step-after"']],
  ]) {
    const ran = await runNotebook(file);
    const flat = JSON.stringify(ran.cells);
    const absent = expected.filter((text) => text instanceof RegExp ? !text.test(flat) : !flat.includes(text));
    check(!flat.includes('ERROR') && absent.length === 0, `the example ${file} runs`, { absent, cells: ran.cells });
    if (file === 'graphs.ipynb') {
      const svgs = drawingsOf(ran.cells);
      // A tree draws no edge across another: the children of the list are
      // right of those of born(1990, may), the second element's too.
      const tree = svgs.find((svg) => svg.includes('>prolog<')) ?? '';
      const [may, prolog, logic] = ['may', 'prolog', 'logic'].map((text) => placeOf(tree, text).x);
      check(may < prolog && may < logic, 'a term is drawn as a tree whose edges do not cross', { may, prolog, logic });
      // An edge from a state to itself is a loop out of the right side of
      // its node and back into it, as Graphviz draws one.
      const states = svgs.find((svg) => svg.includes('>idle<') && svg.includes('<ellipse')) ?? '';
      const idle = placeOf(states, 'idle');
      const node = [...states.matchAll(/<ellipse cx="([\d.]+)" cy="([\d.]+)" rx="([\d.]+)" ry="([\d.]+)"/g)]
        .map((m) => m.slice(1).map(Number)).find(([cx, cy]) => Math.abs(cx - idle.x) < 0.1 && Math.abs(cy - idle.y) < 0.1);
      const loops = node ? pathsOf(states).filter((p) => Math.abs(p.x1 - p.x2) < 0.1
        && p.x1 > node[0] && p.x1 <= node[0] + node[2] && p.y1 < node[1] && p.y2 > node[1] && p.y2 - p.y1 < 2 * node[3]) : [];
      check(loops.length === 1, 'an edge from a state to itself is a loop beside its node', { node, paths: pathsOf(states) });
    }
    if (file !== 'plots.ipynb') await closeNotebook(file);
  }
  check(fetched.has(drawerScripts[0]), 'and the first graph downloads it', [...fetched].filter((f) => f.endsWith('.js')));

  // Each plot of the example is drawn, and an option a plot does not draw
  // is named below it.
  const plots = await guarded(page.evaluate(async () => {
    const app = window.jupyterapp;
    const widget = [...app.shell.widgets('main')].find((w) => w.context?.path === 'plots.ipynb');
    const drawn = () => widget.node.querySelectorAll('.vega-embed .marks').length;
    for (let i = 0; i < 60 && drawn() < 14; i++) await new Promise((r) => setTimeout(r, 500));
    const notes = [...widget.node.querySelectorAll('.jp-OutputArea-output')]
      .map((o) => o.textContent).filter((t) => /Not drawn here|could not be drawn/.test(t));
    const outputs = [];
    const future = widget.sessionContext.session.kernel.requestExecute(
      { code: "show_data(Data = [type-bar, x-[a, b], height-[1, 2], hatch-x])." });
    future.onIOPub = (msg) => { if (msg.header.msg_type === 'display_data') outputs.push(msg.content.data); };
    await future.done;
    return { drawn: drawn(), notes, outputs };
  }), 3 * MINUTE, 'the plots');
  check(plots.drawn === 14 && plots.notes.length === 0, 'each plot of plots.ipynb is drawn, with every option it gives',
    { drawn: plots.drawn, notes: plots.notes });
  check(plots.outputs.some((o) => 'application/vnd.vegalite.v5+json' in o)
      && plots.outputs.some((o) => /Not drawn here: hatch\./.test(o['text/plain'] ?? '')),
    'an option a plot does not draw is named below it', plots.outputs);
} catch (e) {
  failures++;
  console.log(`[${engineName}] did not get there: ${e.message.split('\n')[0]}`);
  if (hung(e)) await diagnose();
}
if (errors.length) console.log('page errors:', errors.slice(0, 6));
if (missing.size) console.log('not found:', [...missing].slice(0, 8));
console.log(`[${engineName}] ${failures} failed`);
await guarded(browser.close(), MINUTE, 'closing the browser').catch((e) => console.log(e.message));
server.close();
remote.close();
process.exit(failures ? 1 : 0);
