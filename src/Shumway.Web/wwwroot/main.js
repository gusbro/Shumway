// WebShumway's front end: a Prolog top level in the page.
//
// The interaction is the one every Prolog top level has, and the reason the
// engine hands back solutions one at a time: show a solution, then wait to be
// asked for the next. Here `;` (or space) asks, `.` (or Enter, or Escape)
// stops — the same keys the console REPL answers to, so the habit transfers.

import * as session from './session.js';

const out = document.getElementById('out');
const queryInput = document.getElementById('query');
const programInput = document.getElementById('program');
const statusEl = document.getElementById('status');

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

document.getElementById('consult').addEventListener('click', async () => {
  const err = await session.consult(programInput.value);
  emit(err ? err + '\n' : '% consulted.\n', err ? 'error' : 'note');
});

// --- boot ----------------------------------------------------------------

out.textContent = '';
emit(await session.boot(emitEngineOutput) + '\n\n', 'note');
setPending(false);
queryInput.focus();

if (location.hash === '#selftest') (await import('./selftest.js')).run(session, emit, out);
