import { dotnet } from './_framework/dotnet.js'

// The async seam. The engine's exports are synchronous today because it runs on
// this thread; the UI never calls them directly, it calls THIS facade, which is
// async. When the engine moves to a Web Worker only this file changes — the
// calls become postMessage round-trips and the UI code does not notice.
//
// Tagged replies from queryNext: 's' solution, 'l' last solution, 'f' failed,
// 'e' error.

const out = document.getElementById('out');
const write = (text) => { out.textContent += text; out.scrollTop = out.scrollHeight; };

const { setModuleImports, getConfig, getAssemblyExports, runMain } = await dotnet.create();
setModuleImports('main.js', { ui: { write } });

const exports = (await getAssemblyExports(getConfig().mainAssemblyName)).Shumway.Web.WebShumwayApp;
await runMain();

export const shumway = {
  boot:     async ()        => exports.Boot(),
  consult:  async (src)     => exports.Consult(src),
  start:    async (q)       => exports.QueryStart(q),
  next:     async (width)   => exports.QueryNext(width ?? 80),
  cancel:   async ()        => exports.QueryCancel(),
  complete: async (prefix)  => {
    const s = exports.Complete(prefix);
    return s.length === 0 ? [] : s.split('\n');
  },
};

// --- a minimal driver, enough to exercise the session end to end ---------
// The editor and answer panes arrive with the UI chunks; this proves the
// contract: consult, query, pull solutions one at a time, cancel, complete.

write(await shumway.boot() + '\n\n');

const form = document.getElementById('query-form');
const input = document.getElementById('query');
const moreBtn = document.getElementById('more');
const stopBtn = document.getElementById('stop');

let running = false;

function setRunning(on) {
  running = on;
  moreBtn.disabled = !on;
  stopBtn.disabled = !on;
}
setRunning(false);

async function step() {
  const reply = await shumway.next(80);
  const tag = reply[0], text = reply.slice(1);
  if (tag === 'f') { write('false.\n\n'); setRunning(false); return; }
  if (tag === 'e') { write('% ' + text + '\n\n'); setRunning(false); return; }
  write(text + (tag === 'l' ? '.\n\n' : ' ;\n'));
  setRunning(tag !== 'l');
}

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  const q = input.value.trim();
  if (!q) return;
  write('?- ' + q + '\n');
  const err = await shumway.start(q);
  if (err) { write('% ' + err + '\n\n'); return; }
  setRunning(true);
  await step();
});

moreBtn.addEventListener('click', () => step());
stopBtn.addEventListener('click', async () => {
  await shumway.cancel();
  write('% Execution aborted.\n\n');
  setRunning(false);
});

document.getElementById('consult').addEventListener('click', async () => {
  const src = document.getElementById('program').value;
  const err = await shumway.consult(src);
  write(err ? '% ' + err + '\n' : '% consulted.\n');
});

// --- #selftest -----------------------------------------------------------
// Drives the whole browser path without a human: consult, pull solutions one
// at a time, a syntax error, engine output, completion. Loading the page with
// #selftest and reading the answers pane is an end-to-end check of the
// deployed app — the part no xUnit test can reach, since it needs a browser.
if (location.hash === '#selftest') {
  const check = (name, got, want) =>
    write(`${got === want ? 'ok  ' : 'FAIL'} ${name}: ${JSON.stringify(got)}` +
          (got === want ? '\n' : ` (wanted ${JSON.stringify(want)})\n`));

  const solutions = async (q) => {
    const acc = [];
    let err = await shumway.start(q);
    if (err) return 'error: ' + err;
    for (;;) {
      const r = await shumway.next(80);
      if (r[0] === 'f') return acc.join(' | ');
      if (r[0] === 'e') return 'error: ' + r.slice(1);
      acc.push(r.slice(1));
      if (r[0] === 'l') return acc.join(' | ');
    }
  };

  write('--- selftest ---\n');
  check('consult', await shumway.consult(
    'anc(X,Y) :- par(X,Y).  anc(X,Z) :- par(X,Y), anc(Y,Z).  par(a,b).  par(b,c).'), null);
  check('arithmetic', await solutions('X is 6*7.'), 'X = 42');
  check('backtracking', await solutions('member(X,[a,b,c]).'), 'X = a | X = b | X = c');
  check('consulted rules', await solutions('anc(a,X).'), 'X = b | X = c');
  check('failure', await solutions('fail.'), '');
  check('no variables', await solutions('atom(foo).'), 'true');
  const bad = await shumway.start('this is not( prolog.');
  check('syntax error reported', typeof bad === 'string' && bad.length > 0, true);
  check('completion', (await shumway.complete('appen')).includes('append'), true);
  const before = out.textContent.length;
  await solutions('write(engine_output), nl.');
  check('engine output reaches the page',
        out.textContent.slice(before).includes('engine_output'), true);
  await shumway.cancel();
  write('--- selftest done ---\n');
}
