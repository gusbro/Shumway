// WebShumway's front end: a Prolog top level in the page.
//
// The interaction is the one every Prolog top level has, and the reason the
// engine hands back solutions one at a time: show a solution, then wait to be
// asked for the next. Here `;` (or space) asks, `.` (or Enter, or Escape)
// stops — the same keys the console REPL answers to, so the habit transfers.

import * as session from './session.js';
import * as workspace from './workspace.js';
import { attach } from './editor.js';

const out = document.getElementById('out');
const queryInput = document.getElementById('query');
const programInput = document.getElementById('program');
const statusEl = document.getElementById('status');

// Attached once the engine exists (its colouring comes from the engine's lexer),
// so the handlers registered below must tolerate it being absent for that moment.
let editor = null;

// --- the transcript ------------------------------------------------------

/** Appends text in a role: 'query' | 'answer' | 'error' | 'note' | '' (engine). */
function emit(text, role = '') {
  const atBottom = out.scrollHeight - out.scrollTop - out.clientHeight < 40;
  const span = document.createElement('span');
  if (role) span.className = role;
  span.textContent = text;
  out.appendChild(span);
  // Follow the tail only if the user was already there — scrolling back to read
  // something should not be undone by the next line of output.
  if (atBottom) out.scrollTop = out.scrollHeight;
}

const emitEngineOutput = (text) => emit(text);

// A page that dies silently looks like a page that is still loading. Anything
// that escapes lands in the transcript, where it can be read and reported.
const emitFailure = (what, detail) =>
  emit(`% ${what}: ${detail && detail.stack ? detail.stack : detail}\n`, 'error');
addEventListener('error', (e) => emitFailure('script error', e.error ?? e.message));
addEventListener('unhandledrejection', (e) => emitFailure('unhandled rejection', e.reason));

// --- pending-solution state ---------------------------------------------
// A query is "pending" between solutions: the engine has one more it could
// look for, and the UI is waiting to be told whether to.

let pending = false;

function setPending(on) {
  pending = on;
  document.body.classList.toggle('pending', on);
  statusEl.textContent = on ? 'more solutions?  ;  next    .  stop' : '';
}

/** Columns available for an answer, so long terms wrap where they are read. */
function answerWidth() {
  const probe = document.createElement('span');
  probe.textContent = '0'.repeat(10);
  probe.style.cssText = 'position:absolute;visibility:hidden;white-space:pre';
  out.appendChild(probe);
  const charWidth = probe.getBoundingClientRect().width / 10;
  probe.remove();
  const cols = Math.floor((out.clientWidth - 24) / (charWidth || 8));
  return Math.max(40, Math.min(200, cols));
}

async function step() {
  const { tag, text } = await session.next(answerWidth());
  if (tag === session.FAILED) { emit('false.\n\n', 'answer'); setPending(false); return; }
  if (tag === session.ERROR) { emit(text + '\n\n', 'error'); setPending(false); return; }
  if (tag === session.LAST) { emit(text + '.\n\n', 'answer'); setPending(false); return; }
  emit(text + ' ', 'answer');
  setPending(true);
}

async function run(queryText) {
  emit('?- ' + queryText + '\n', 'query');
  const err = await session.start(queryText);
  if (err) { emit(err + '\n\n', 'error'); return; }
  await step();
}

async function stop() {
  await session.cancel();
  emit(';\n% Execution aborted.\n\n', 'note');
  setPending(false);
}

// --- query entry ---------------------------------------------------------

const history = [];
let historyAt = 0;      // index into history; == length means "the live line"
let draft = '';         // what was typed before arrowing into history

document.getElementById('query-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  if (pending) return;          // Enter means "stop" while a query is pending
  const text = queryInput.value.trim();
  if (!text) return;
  queryInput.value = '';
  if (history[history.length - 1] !== text) history.push(text);
  historyAt = history.length;
  draft = '';
  await run(text.endsWith('.') ? text : text + '.');
});

queryInput.addEventListener('keydown', async (e) => {
  // While solutions are pending the keys mean what they mean in a top level.
  if (pending) {
    if (e.key === ';' || e.key === ' ') { e.preventDefault(); await step(); return; }
    if (e.key === '.' || e.key === 'Enter' || e.key === 'Escape') {
      e.preventDefault();
      emit('.\n\n', 'answer');
      await session.cancel();
      setPending(false);
      return;
    }
    return;
  }

  if (e.key === 'ArrowUp' && historyAt > 0) {
    e.preventDefault();
    if (historyAt === history.length) draft = queryInput.value;
    queryInput.value = history[--historyAt];
    queryInput.setSelectionRange(queryInput.value.length, queryInput.value.length);
  } else if (e.key === 'ArrowDown' && historyAt < history.length) {
    e.preventDefault();
    queryInput.value = ++historyAt === history.length ? draft : history[historyAt];
  }
});

document.getElementById('stop').addEventListener('click', () => stop());

// --- files ---------------------------------------------------------------
// The buffer being edited is itself a workspace file, so it persists like any
// other and a program that spans several files can be assembled here.

const filesEl = document.getElementById('files');
let currentFile = 'scratch.pl';

/** Writes the editor's buffer back to its file, then mirrors to storage. */
async function saveBuffer() {
  workspace.write(currentFile, programInput.value);
  await workspace.persist();
  refreshFiles();
}

function refreshFiles() {
  const names = workspace.list();
  filesEl.replaceChildren(...names.map((name) => {
    const item = document.createElement('button');
    item.type = 'button';
    item.className = 'file' + (name === currentFile ? ' current' : '');
    item.textContent = name;
    item.title = `open ${name}`;
    item.addEventListener('click', async () => {
      workspace.write(currentFile, programInput.value);   // don't lose the buffer
      currentFile = name;
      programInput.value = workspace.read(name) ?? '';
      await editor?.repaintNow();
      refreshFiles();
    });
    return item;
  }));
  document.getElementById('current-file').textContent = currentFile;
}

document.getElementById('open-file').addEventListener('click', async () => {
  const names = await workspace.openFiles();
  if (names.length === 0) return;
  workspace.write(currentFile, programInput.value);
  currentFile = names[0];
  programInput.value = workspace.read(currentFile) ?? '';
  await editor?.repaintNow();
  await workspace.persist();
  refreshFiles();
  emit(`% opened ${names.join(', ')}\n`, 'note');
});

document.getElementById('save-file').addEventListener('click', async () => {
  await saveBuffer();
  const saved = await workspace.saveFile(currentFile, programInput.value);
  emit(`% saved ${saved}\n`, 'note');
});

document.getElementById('new-file').addEventListener('click', async () => {
  const name = prompt('New file name', 'program.pl');
  if (!name) return;
  workspace.write(currentFile, programInput.value);
  workspace.write(name, '');
  currentFile = name;
  programInput.value = '';
  await editor?.repaintNow();
  await workspace.persist();
  refreshFiles();
});

// --- examples ------------------------------------------------------------
// Real programs, each with its queries in a comment at the top — the fastest
// way to find out what the engine can do is to run something that does it.

const EXAMPLES = [
  ['family.pl',  'Relations and recursion'],
  ['queens.pl',  'N queens, generate and test'],
  ['zebra.pl',   'The zebra puzzle'],
  ['clpfd.pl',   'Constraints over finite domains'],
  ['dcg.pl',     'Grammars (DCG)'],
  ['tabling.pl', 'Tabling: left recursion and memoisation'],
];

async function loadExample(name) {
  const source = await (await fetch('examples/' + name)).text();
  workspace.write(currentFile, programInput.value);   // don't lose the buffer
  currentFile = name;
  workspace.write(name, source);
  programInput.value = source;
  await editor?.repaintNow();
  await workspace.persist();
  refreshFiles();
  // The constraint example needs its library; loading it here means the
  // example runs as written instead of failing on the first #=.
  if (name === 'clpfd.pl') {
    const err = await session.useClpfd();
    emit(err ? err + '\n' : '% CLP(FD) loaded.\n', err ? 'error' : 'note');
  }
  emit(`% loaded ${name} — its queries are in the comment at the top\n`, 'note');
}

const examplesEl = document.getElementById('examples');
for (const [name, title] of EXAMPLES) {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = 'example';
  b.textContent = name.replace(/\.pl$/, '');
  b.title = title;
  b.addEventListener('click', () => loadExample(name));
  examplesEl.appendChild(b);
}

// --- sharing -------------------------------------------------------------
// No server to store anything on, so the program travels in the URL fragment —
// which browsers never send anywhere. Sharing a link does not hand the code to
// a third party.

document.getElementById('share').addEventListener('click', async () => {
  const encoded = await session.shareEncode(programInput.value, queryInput.value);
  const url = location.origin + location.pathname + '#p=' + encoded;
  try {
    await navigator.clipboard.writeText(url);
    emit(`% link copied (${url.length} characters)\n`, 'note');
  } catch {
    emit(`% link: ${url}\n`, 'note');
  }
  history.replaceState(null, '', '#p=' + encoded);
});

document.getElementById('consult').addEventListener('click', async () => {
  // The buffer IS a file; consulting saves it first, so what ran and what is
  // stored are the same text.
  await saveBuffer();
  const err = await session.consult(programInput.value);
  if (!err) {
    editor?.markError(null);
    // Operators the program declared are now in the table, so the buffer's
    // colouring can change on consult — repaint it.
    editor?.repaint();
    emit('% consulted.\n', 'note');
    return;
  }
  emit(err + '\n', 'error');
  // A parse error names its position; put it on the editor too, so the report
  // and the text the user has to fix are not in different places.
  const at = /(\d+):(\d+):/.exec(err);
  if (at) editor?.markError(Number(at[1]), Number(at[2]), err);
});

// --- boot ----------------------------------------------------------------

out.textContent = '';
emit(await session.boot(emitEngineOutput) + '\n\n', 'note');
setPending(false);

editor = attach(
  programInput, document.getElementById('program-backdrop'),
  session.highlight, session.highlightKinds(), session.complete);

// Files: bring back whatever the last session left, then show the buffer.
workspace.init(session.exports());
const restored = await workspace.restore();
if (workspace.read(currentFile) === null) workspace.write(currentFile, '');

// A shared link wins over the stored buffer: someone who followed a link came
// to see what is in it. It is not saved over the workspace file until they
// consult or save, so following a link cannot silently destroy their work.
const shared = /^#p=(.+)$/.exec(location.hash);
if (shared) {
  const unpacked = session.shareDecode(shared[1]);
  if (unpacked) {
    programInput.value = unpacked.program;
    queryInput.value = unpacked.query;
    emit('% loaded from a shared link\n', 'note');
  } else {
    emit('% that link could not be read\n', 'error');
  }
}
if (!shared) programInput.value = workspace.read(currentFile) ?? '';
await editor.repaintNow();
refreshFiles();
if (restored > 0) emit(`% ${restored} file(s) restored\n`, 'note');
else if (!workspace.persistent())
  emit('% this browser has no origin-private storage — files last for this session only\n', 'note');

// Offline. Registered after boot so it never competes with the runtime download
// on a first visit; the second visit is the one that benefits.
if ('serviceWorker' in navigator && location.protocol !== 'file:') {
  navigator.serviceWorker.register('sw.js').catch(() => {
    emit('% offline support unavailable in this browser\n', 'note');
  });
}

queryInput.focus();

const persistMode = /^#persist=(write|check)$/.exec(location.hash);
if (persistMode) {
  try {
    await (await import('./selftest.js')).persistProbe(workspace, emit, persistMode[1]);
  } catch (ex) { emitFailure('persist probe', ex); }
} else if (location.hash === '#selftest') {
  try {
    await (await import('./selftest.js')).run(session, emit, out, editor, workspace);
  } catch (ex) {
    // A selftest that dies silently reads as a selftest that passed.
    emit(`--- selftest CRASHED: ${ex && ex.stack ? ex.stack : ex} ---\n`, 'error');
  }
}
