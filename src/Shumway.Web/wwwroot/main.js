// WebShumway's front end: a Prolog top level in the page.
//
// The interaction is the one every Prolog top level has, and the reason the
// engine hands back solutions one at a time: show a solution, then wait to be
// asked for the next. Here `;` (or space) asks, `.` (or Enter, or Escape)
// stops — the same keys the console REPL answers to, so the habit transfers.

import * as session from './session.js';
import * as workspace from './workspace.js';
import * as libraries from './libraries.js';
import * as settings from './settings.js';
import * as theme from './theme.js';
import * as layout from './layout.js';
import { attach } from './editor.js';
import * as debugUi from './debug.js';

const out = document.getElementById('out');
const queryInput = document.getElementById('query');
const programEl = document.getElementById('program');

/// The program text. There is one copy of it, in the editor.
const program = () => (editor ? editor.getText() : '');
const statusEl = document.getElementById('status');

// Attached once the engine exists (its colouring comes from the engine's lexer),
// so the handlers registered below must tolerate it being absent for that moment.
let editor = null;

// --- the transcript ------------------------------------------------------

// Whether the transcript is at the start of a line. A program that writes
// without a newline would otherwise have the next answer run onto its output.
let atLineStart = true;

/** Appends text in a role: 'query' | 'answer' | 'error' | 'note' | '' (engine). */
function emit(text, role = '') {
  if (!text) return;
  const atBottom = out.scrollHeight - out.scrollTop - out.clientHeight < 40;
  const span = document.createElement('span');
  if (role) span.className = role;
  span.textContent = text;
  out.appendChild(span);
  atLineStart = text.endsWith('\n');
  // Follow the tail only if the user was already there — scrolling back to read
  // something should not be undone by the next line of output.
  if (atBottom) out.scrollTop = out.scrollHeight;
}

/** Starts a line, unless one is already started. */
function freshLine() {
  if (!atLineStart) emit('\n');
}

const emitEngineOutput = (text) => emit(text);
// Standard error, which in a browser has nowhere else to go.
const emitDiagnostic = (text) => emit(text, 'error');

// A page that dies silently looks like a page that is still loading. Anything
// that escapes lands in the transcript, where it can be read and reported.
// EXCEPT the deliberate boot abort while isolation is pending: that path has
// already said what is happening, and "% script error" on top of it read as a
// breakage to report (it was reported).
const ISOLATION_ABORT = 'boot aborted: the page is not cross-origin isolated';
const emitFailure = (what, detail) => {
  if (detail && detail.message === ISOLATION_ABORT) return;
  emit(`% ${what}: ${detail && detail.stack ? detail.stack : detail}\n`, 'error');
};
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

let aborted = false;
let stepping = false;      // a solution is being searched for right now

// --- input for the running program ---------------------------------------
// A goal may READ. The engine's read blocks the thread it is on — a pool
// thread, so the page stays live — and asks here for a line. The query box does
// double duty: while a program is reading, what is typed into it goes to the
// program instead of being a new query.

let awaitingInput = false;

/** Called by the engine when a read has no characters left to give. */
function askForInput() {
  awaitingInput = true;
  freshLine();
  emit('|: ', 'note');
  statusEl.textContent = 'the program is reading — type a term and press Enter  (Esc: end of file)';
  queryInput.placeholder = 'input for the program';
  queryInput.focus();
}

// --- work that takes a while ---------------------------------------------
// The page stays responsive while the engine works (that is what the pool
// thread is for), which is exactly why silence is wrong: nothing is frozen, so
// nothing tells you anything is happening. Loading a library can take a good
// many seconds.

let busyTimer = 0;

async function withBusy(label, work) {
  const started = performance.now();
  // The label may be a function, for work that goes through phases: reading
  // files is not compiling, and a wait that says which is which is a wait
  // somebody can judge.
  const text = typeof label === 'function' ? label : () => label;
  const tick = () => {
    const seconds = Math.round((performance.now() - started) / 1000);
    statusEl.textContent = seconds < 1 ? `${text()}…` : `${text()}… ${seconds}s`;
  };
  tick();
  busyTimer = setInterval(tick, 500);
  document.body.classList.add('busy');
  try {
    return await work();
  } finally {
    clearInterval(busyTimer);
    busyTimer = 0;
    document.body.classList.remove('busy');
    statusEl.textContent = '';
  }
}

function inputDone() {
  awaitingInput = false;
  statusEl.textContent = '';
  queryInput.placeholder = '';
}

async function supplyInput(text) {
  emit(text + '\n', 'query');
  inputDone();
  await session.supplyInput(text);
}

async function supplyEndOfFile() {
  emit('end_of_file\n', 'note');
  inputDone();
  await session.supplyEndOfFile();
}

/**
 * Says the engine is searching — but only once it has gone on long enough to
 * wonder. Under a couple of seconds an indicator is just a flash, and a line
 * that appears and vanishes on every query is worse than none.
 *
 * Returns the function that takes it down again.
 */
function runningIndicator() {
  const started = performance.now();
  let ticking = 0;
  const tick = () => {
    // The status line belongs to whoever needs it more: a program asking for
    // input has something for the user to DO, and a query STOPPED at a
    // breakpoint is not running — the debug UI owns the line then.
    if (awaitingInput || debugUi.isStopped()) return;
    statusEl.textContent =
      `running goal… ${((performance.now() - started) / 1000).toFixed(0)}s   (Stop to abandon)`;
  };
  const waiting = setTimeout(() => { tick(); ticking = setInterval(tick, 500); }, 2000);
  return () => {
    clearTimeout(waiting);
    clearInterval(ticking);
    if (!awaitingInput) statusEl.textContent = '';
  };
}

async function step() {
  stepping = true;
  debugUi.setRunning(true);        // Break's moment, if the mode is on
  const searching = runningIndicator();
  let tag, text;
  try { ({ tag, text } = await session.next(answerWidth())); }
  finally { stepping = false; debugUi.setRunning(false); searching(); }
  // The promise resolving means the search is no longer suspended at a stop.
  debugUi.clearStopped();
  // The goal may have written as it ran; an answer starts its own line.
  freshLine();
  if (aborted) { aborted = false; emit('% Execution aborted.\n\n', 'note'); setPending(false); return; }
  if (tag === session.FAILED) { emit('false.\n\n', 'answer'); setPending(false); return; }
  if (tag === session.ERROR) { emit(text + '\n\n', 'error'); setPending(false); return; }
  if (tag === session.LAST) { emit(text + '.\n\n', 'answer'); setPending(false); return; }
  // No newline: the answer waits on its line for the `;` or `.` that follows
  // it, exactly as a console top level leaves it.
  emit(text + ' ', 'answer');
  setPending(true);
}

async function run(queryText) {
  freshLine();
  emit('?- ' + queryText + '\n', 'query');
  const err = await session.start(queryText);
  if (err) { emit(err + '\n\n', 'error'); return; }
  await step();
}

/**
 * Ends whatever query is open, in either of its two states, and says so.
 * Returns whether there was one.
 *
 * A query SEARCHING is on a pool thread: cancelling only asks it to stop, and
 * the pending step() prints the abort when the engine reaches its next safe
 * point — which is why the message is not printed here. A query WAITING for `;`
 * has nobody to print it, so this does.
 */
async function abandonQuery() {
  if (stepping) {
    aborted = true;
    // A query stopped at a breakpoint may have an Immediate/watch evaluation
    // running ON the parked machine; cancelling under it races the engine.
    // Drain (capped) so Stop stays a stop, not a coin toss.
    if (debugUi.isStopped()) await debugUi.drainEvals();
    await session.cancel();
    // A goal blocked on input is not at a safe point and will never see the
    // cancellation. Closing the stream lets the read return so it can.
    if (awaitingInput) { inputDone(); await session.supplyEndOfFile(); }
    return true;
  }
  if (pending) {
    emit('.\n\n', 'answer');
    setPending(false);
    await session.cancel();
    return true;
  }
  return false;
}

const stop = abandonQuery;

document.getElementById('clear').addEventListener('click', () => {
  out.replaceChildren();
  atLineStart = true;
  queryInput.focus();
});

// --- query entry ---------------------------------------------------------

// The query history. NOT `history`: that name is the browser's own, and
// shadowing it turned Share into a TypeError on history.replaceState.
const queryHistory = [];
let historyAt = 0;      // index into history; == length means "the live line"
let draft = '';         // what was typed before arrowing into history

document.getElementById('query-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  if (awaitingInput) {          // the program is reading; this line is for it
    const line = queryInput.value;
    queryInput.value = '';
    await supplyInput(line);
    return;
  }
  if (pending) return;          // Enter means "stop" while a query is pending
  const text = queryInput.value.trim();
  if (!text) return;
  queryInput.value = '';
  if (queryHistory[queryHistory.length - 1] !== text) queryHistory.push(text);
  historyAt = queryHistory.length;
  draft = '';
  // While a query is STOPPED at a breakpoint the box is the Immediate window:
  // the goal evaluates against the selected frame (a real QueryStart would
  // queue behind the engine gate the suspended search holds).
  if (debugUi.isStopped()) { await debugUi.evaluate(text); return; }
  await run(text.endsWith('.') ? text : text + '.');
});

queryInput.addEventListener('keydown', async (e) => {
  // While a program is reading, the box is its input: only Escape is ours, and
  // it means what Ctrl-D means at a terminal.
  if (awaitingInput) {
    if (e.key === 'Escape') { e.preventDefault(); await supplyEndOfFile(); }
    return;
  }

  // While solutions are pending the keys mean what they mean in a top level.
  if (pending) {
    if (e.key === ';' || e.key === ' ') {
      e.preventDefault();
      // Echo the request and close the line, so the next solution starts on
      // its own — `X = 1 ;` then the next answer, as a top level reads.
      emit(';\n', 'answer');
      await step();
      return;
    }
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
    if (historyAt === queryHistory.length) draft = queryInput.value;
    queryInput.value = queryHistory[--historyAt];
    queryInput.setSelectionRange(queryInput.value.length, queryInput.value.length);
  } else if (e.key === 'ArrowDown' && historyAt < queryHistory.length) {
    e.preventDefault();
    queryInput.value = ++historyAt === queryHistory.length ? draft : queryHistory[historyAt];
  }
});

document.getElementById('stop').addEventListener('click', () => stop());

// --- dialogs -------------------------------------------------------------
// Native <dialog>: modal, focus-trapping and Escape-closing without help, and
// unlike window.confirm it does not block the whole page while it is open —
// which matters here, because a search may be running behind it.

const confirmDialog = document.getElementById('confirm');
const promptDialog = document.getElementById('prompt-dialog');
const shareDialog = document.getElementById('share-dialog');
// Declared with the others rather than beside the code that uses them: the loop
// below wires every dialog's closing button, and a const is unreachable before
// its declaration runs.
const referenceDialog = document.getElementById('reference-dialog');
const guideDialog = document.getElementById('guide-dialog');
const librariesDialog = document.getElementById('libraries-dialog');
const importDialog = document.getElementById('import-dialog');
const urlDialog = document.getElementById('url-dialog');

// Cancel closes without submitting, so the only submit button is the one Enter
// should press. Escape closes with an empty returnValue, which reads as cancel
// too — nothing here treats anything but a named answer as yes.
//
// The confirmation dialog has no such button in the markup: its answers vary by
// question, so askChoice builds them — including the cancelling one.
for (const dialog of [promptDialog, shareDialog, referenceDialog, guideDialog,
                       librariesDialog, importDialog, urlDialog]) {
  dialog.querySelector('[data-close]')
    .addEventListener('click', () => dialog.close('cancel'));
}

/**
 * Asks a question with named answers. `choices` are `{value, label, primary}`;
 * a null value is the one that cancels — it does not submit, so Enter presses
 * the first real answer. Escape closes with '', which every caller reads as the
 * cautious answer.
 */
function askChoice(title, detail, choices) {
  document.getElementById('confirm-title').textContent = title;
  document.getElementById('confirm-detail').textContent = detail;
  document.getElementById('confirm-actions').replaceChildren(...choices.map((choice) => {
    const button = document.createElement('button');
    button.textContent = choice.label;
    if (choice.value === null) {
      button.type = 'button';
      button.addEventListener('click', () => confirmDialog.close(''));
    } else {
      button.value = choice.value;
      if (choice.primary) button.className = 'primary';
    }
    return button;
  }));
  confirmDialog.showModal();
  return new Promise((resolve) =>
    confirmDialog.addEventListener('close',
      () => resolve(confirmDialog.returnValue), { once: true }));
}

const ask = async (title, detail, okLabel = 'OK') =>
  (await askChoice(title, detail, [
    { value: null, label: 'Cancel' },
    { value: 'ok', label: okLabel, primary: true },
  ])) === 'ok';

function askFor(title, value) {
  const input = document.getElementById('prompt-input');
  document.getElementById('prompt-title').textContent = title;
  input.value = value;
  promptDialog.showModal();
  input.select();
  return new Promise((resolve) =>
    promptDialog.addEventListener('close',
      () => resolve(promptDialog.returnValue === 'ok' ? input.value.trim() : null),
      { once: true }));
}

// --- libraries -----------------------------------------------------------
// Global, not part of any workspace: what a program builds ON rather than what
// it is. Editable, because reading the library you are calling is half of
// learning it — a library file opened here saves back into the library.

// --- building a collection in the background ------------------------------
// Importing a collection makes forty-odd libraries available; compiling them is
// what makes each one load fast, and there is no reason to make anyone ask for
// that one at a time. So it runs by itself, one library after another, and each
// becomes fast the moment its own build lands — the whole thing does not have
// to finish to be worth having.
//
// Each compile takes the engine gate for its own duration and no longer, so a
// query or a consult waits for ONE library rather than for the batch.

const buildStatusEl = document.getElementById('build-status');
let buildRun = null;

/** Above this many libraries, a collection is not compiled through unasked. */
const BIG_COLLECTION = 80;

// A consult PAUSES the batch. Not for the engine's sake — each compile holds
// the engine gate for its own duration, so the two never run at once — but so
// that what a consult resolves against is a settled set of bundles, and so that
// pressing Consult does not mean waiting behind a library nobody asked for.
// The batch stops starting new ones and picks up where it left off after.
let consultsInFlight = 0;

// Bumped whenever the answer to "what do the programs here import" may have
// changed: a consult, and a switch to a different workspace's files.
let consultEpoch = 0;

async function whileConsulting(work) {
  consultsInFlight++;
  try { return await work(); } finally { consultsInFlight--; consultEpoch++; }
}

/**
 * The libraries this workspace's programs ask for.
 *
 * Read out of the sources rather than from the engine, because the point is to
 * compile them BEFORE anything imports them. A regular expression is enough
 * for that: it decides what to build FIRST, so a false positive costs a
 * library compiled early and a miss costs nothing but the original order.
 */
async function librariesInUse() {
  const wanted = new Set();
  const seen = [program(), ...await Promise.all(
    (await workspace.list()).map((f) => workspace.read(f)))];
  for (const text of seen) {
    for (const [, name] of (text ?? '').matchAll(/use_module\s*\(\s*library\s*\(\s*([\w.]+)/g))
      wanted.add(name);
  }
  return wanted;
}

const idle = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function showBuildStatus(text, onStop) {
  buildStatusEl.replaceChildren(document.createTextNode(text));
  if (onStop) {
    const stopIt = document.createElement('button');
    stopIt.type = 'button';
    stopIt.className = 'heading-action';
    stopIt.textContent = 'stop';
    stopIt.addEventListener('click', onStop);
    buildStatusEl.appendChild(stopIt);
  }
  buildStatusEl.hidden = false;
}

async function buildCollection(name) {
  if (buildRun) { emit(`% already building ${buildRun.name}\n`, 'note'); return; }
  const run = { name, cancel: false };
  buildRun = run;

  const queue = (await libraries.entries(name)).filter((e) => !e.compiled);
  const total = queue.length;
  let built = 0, stoppedEarly = false;
  const failed = [];
  let wanted = await librariesInUse();
  let knownAt = consultEpoch;
  const started = performance.now();
  try {
    while (queue.length > 0) {
      if (run.cancel) break;

      // Between libraries, not inside one: a compile that has started finishes,
      // and the next one waits for the user to be done.
      if (consultsInFlight > 0) {
        showBuildStatus(`paused while consulting — ${name}: ${queue.length} to go`);
        while (consultsInFlight > 0 && !run.cancel) await idle(200);
        if (run.cancel) break;
      }

      // What the programs here import goes FIRST — those are the ones whose
      // being slow is felt. Re-read after each consult, because a consult is
      // when a program's imports change.
      if (consultEpoch !== knownAt) { wanted = await librariesInUse(); knownAt = consultEpoch; }
      let next = queue.findIndex((e) => wanted.has(e.name));

      // A big collection is not compiled through unasked. SWI's library is two
      // hundred files; grinding for an hour on libraries nobody here calls is
      // rude, and the ones that ARE called have just been done. The rest stay a
      // button away.
      if (next < 0 && total > BIG_COLLECTION) { stoppedEarly = true; break; }
      if (next < 0) next = 0;
      const [entry] = queue.splice(next, 1);

      showBuildStatus(
        `compiling ${name}: ${entry.name} (${total - queue.length}/${total})`,
        () => { run.cancel = true; showBuildStatus('finishing the current library…'); });
      // The error is NOT emitted: what a library from another system says
      // while it compiles is not the output of whatever the user is doing.
      // It is filed against the library and the summary names it, so the
      // reason is a click away in the library list.
      const err = await libraries.compile(name, entry.name);
      if (err) failed.push(entry.name); else built++;
      // Stored as each one lands: a reload keeps what is already built.
      await libraries.persist();
      if (librariesDialog.open) await refreshLibraries();
    }
  } finally {
    buildRun = null;
    buildStatusEl.hidden = true;
    buildStatusEl.replaceChildren();
  }

  const seconds = ((performance.now() - started) / 1000).toFixed(0);
  emit(`% ${name}: ${built} librar${built === 1 ? 'y' : 'ies'} compiled in ${seconds}s`
     + (run.cancel ? ' (stopped)' : '')
     + (stoppedEarly
          ? '. That is what your programs import; “compile the rest” for the others,'
            + ' which still load from source.\n'
          : '. The rest still load from source.\n'), 'note');

  // Named, because "11 could not be" leaves you having to find out which. The
  // list marks them and holds the reason.
  if (failed.length > 0) {
    emit(`% ${name}: ${nameList(failed)} would not compile`
       + ' — Libraries marks them, with the reason\n', 'note');
  }
}

/** A few names, then a count: forty of them is not a sentence. */
function nameList(names, shown = 6) {
  if (names.length <= shown) return names.join(', ');
  return `${names.slice(0, shown).join(', ')} and ${names.length - shown} more`;
}

async function refreshLibraries() {
  const body = document.getElementById('libraries-body');
  const known = await libraries.names();
  if (known.length === 0) {
    body.replaceChildren(Object.assign(document.createElement('p'),
      { className: 'doc-empty', textContent: 'no libraries imported yet' }));
    return;
  }

  const parts = [];
  for (const name of known) {
    const tag = await libraries.dialect(name);
    const provided = await libraries.entries(name);
    const head = document.createElement('h4');
    head.textContent = (tag ? `${name} — ${tag}` : name)
      + ` — ${provided.length} librar${provided.length === 1 ? 'y' : 'ies'}`;

    // Compiling what is left, for a collection imported before this existed or
    // one whose batch was stopped.
    if (provided.some((e) => !e.compiled)) {
      const buildRest = document.createElement('button');
      buildRest.type = 'button';
      buildRest.className = 'heading-action';
      buildRest.textContent = 'compile the rest';
      buildRest.addEventListener('click', () => { librariesDialog.close(''); buildCollection(name); });
      head.appendChild(buildRest);
    }

    const drop = document.createElement('button');
    drop.type = 'button';
    drop.className = 'heading-action';
    drop.textContent = 'remove';
    drop.addEventListener('click', async () => {
      if (!await ask(`Remove library “${name}”?`,
                     'Its files are deleted from this browser. Programs that '
                     + 'import it will stop finding it.', 'Remove')) return;
      await libraries.remove(name);
      await libraries.persist();
      await refreshLibraries();
      emit(`% removed library ${name}\n`, 'note');
    });
    head.appendChild(drop);
    parts.push(head);

    // One row per LIBRARY the collection provides — the names use_module can
    // ask for. Compiling is per library: nobody wants to wait for forty-six of
    // them to get the one they came for.
    for (const entry of provided) {
      const row = document.createElement('div');
      row.className = 'doc-entry';

      const open = document.createElement('button');
      open.type = 'button';
      open.className = 'file';
      open.textContent = `library(${entry.name})`;
      open.title = `open ${entry.name}.pl`;
      open.addEventListener('click', async () => {
        await openLibraryFile(name, entry.name + '.pl');
        librariesDialog.close('');
      });

      const state = document.createElement('span');
      state.textContent = entry.state === 'failed' ? 'will not compile'
        : entry.note ? 'compiled, with warnings'
        : entry.compiled ? 'compiled'
        : 'source only';
      if (entry.note) {
        state.className = entry.state === 'failed' ? 'library-broken' : 'library-note';
        state.title = entry.note;
      }

      // What went wrong, on request. Out of the LIST rather than out of the
      // terminal: a collection compiles forty libraries nobody asked about one
      // at a time, and the answer to "why does this one not work" is wanted
      // when you go looking for the library, not while you are typing.
      let why = null;
      if (entry.note) {
        why = document.createElement('button');
        why.type = 'button';
        why.className = 'heading-action';
        why.textContent = 'details';
        why.addEventListener('click', async () => {
          const said = await libraries.diagnostic(name, entry.name);
          librariesDialog.close('');          // modal: its backdrop hides output
          emit(`% ${entry.name}:\n${said.split('\n').slice(1).join('\n')}\n`,
               entry.state === 'failed' ? 'error' : 'note');
        });
      }

      // Editing a source does nothing until the library is built again: what
      // library(X) resolves to is the bundle beside the sources.
      const build = document.createElement('button');
      build.type = 'button';
      build.className = 'heading-action';
      build.textContent = entry.compiled ? 'rebuild' : 'compile';
      build.addEventListener('click', async () => {
        const failed = await withBusy(`compiling ${entry.name}`,
                                      () => libraries.compile(name, entry.name));
        await libraries.persist();
        emit(failed ? `% ${entry.name}: ${failed}\n` : `% ${entry.name} compiled\n`,
             failed ? 'error' : 'note');
        await refreshLibraries();
      });

      row.append(open, state, build);
      if (why) row.append(why);
      parts.push(row);
    }
  }
  body.replaceChildren(...parts);
}

document.getElementById('libraries').addEventListener('click', async () => {
  await refreshLibraries();
  librariesDialog.showModal();
});

/** Names a set of fetched sources, writes them in, and starts building. Shared
 *  by both ways in — where the files came from stops mattering here. */
async function adoptCollection(files, { name: suggestedName, dialect = '', from }) {
  document.getElementById('import-detail').textContent =
    `${files.length} source file(s) from “${from}”.`;
  document.getElementById('import-name').value = suggestedName;
  document.getElementById('import-dialect').value = dialect;
  importDialog.showModal();
  const answer = await new Promise((resolve) =>
    importDialog.addEventListener('close', () => resolve(importDialog.returnValue), { once: true }));
  if (answer !== 'ok') return;

  const name = document.getElementById('import-name').value.trim();
  const dial = document.getElementById('import-dialect').value;
  if (!name) return;

  let phase = 'importing';
  const err = await withBusy(
    () => `${phase} ${name}`,
    () => libraries.importFolder(name, dial, files, (p) => { phase = p; }));

  if (err) { emit(`% ${name}: ${err}\n`, 'error'); return; }
  const provided = await libraries.entries(name);
  emit(`% ${name}: ${provided.length} librar${provided.length === 1 ? 'y' : 'ies'} available`
     + ` — compiling them in the background; each gets faster as it lands\n`, 'note');
  await refreshLibraries();
  buildCollection(name);        // not awaited: it runs while the page is used
}

document.getElementById('import-library').addEventListener('click', async () => {
  // Out of the way first: this dialog is modal, so anything reported while it
  // is open is drawn behind its backdrop — which reads as nothing happening.
  librariesDialog.close('');
  const picked = await libraries.pickFolder();
  if (!picked) return;
  if (picked.files.length === 0) {
    emit('% that folder holds no Prolog sources\n', 'error');
    return;
  }
  await adoptCollection(picked.files, { name: picked.name, from: picked.name });
});

// --- from a URL -----------------------------------------------------------

const urlSuggested = document.getElementById('url-suggested');
const urlInput = document.getElementById('url-input');

urlSuggested.replaceChildren(
  Object.assign(document.createElement('option'), { value: '', textContent: 'another address…' }),
  ...libraries.SUGGESTED.map((s, i) =>
    Object.assign(document.createElement('option'), { value: String(i), textContent: s.label })));
urlSuggested.addEventListener('change', () => {
  const chosen = libraries.SUGGESTED[Number(urlSuggested.value)];
  if (chosen) urlInput.value = chosen.url;
});

document.getElementById('import-url').addEventListener('click', async () => {
  librariesDialog.close('');          // see import-library: modal hides progress
  urlSuggested.value = '0';
  urlInput.value = libraries.SUGGESTED[0].url;
  urlDialog.showModal();
  const answer = await new Promise((resolve) =>
    urlDialog.addEventListener('close', () => resolve(urlDialog.returnValue), { once: true }));
  if (answer !== 'ok') return;

  const url = urlInput.value.trim();
  const where = libraries.parseGitHubTree(url);
  if (!where) {
    emit('% that is not a GitHub directory address'
       + ' (https://github.com/owner/repo/tree/branch/path)\n', 'error');
    return;
  }

  const known = libraries.SUGGESTED.find((s) => s.url === url);
  emit(`% fetching ${where.owner}/${where.repo}`
     + (where.path ? `/${where.path}` : '') + `…\n`, 'note');

  let fetched = 0, expected = 0;
  let files;
  try {
    files = await withBusy(
      () => expected === 0
        ? `asking GitHub what is in ${where.repo}`
        : `fetching ${where.repo}: ${fetched} of ${expected} files`,
      () => libraries.fetchGitHubTree(where, (done, total) => { fetched = done; expected = total; }));
  } catch (ex) {
    emit(`% ${ex.message ?? ex}\n`, 'error');
    return;
  }
  emit(`% fetched ${files.length} file(s)\n`, 'note');

  await adoptCollection(files, {
    name: known?.name ?? (where.path.split('/').pop() || where.repo),
    dialect: known?.dialect ?? '',
    from: `${where.owner}/${where.repo}`,
  });
});

// --- the reference and the guide -----------------------------------------
// The reference is built from the ENGINE's own predicate metadata — the same
// metadata that generates docs/guide/predicates.md — so what the page shows
// cannot drift from what the engine actually provides.

let reference = null;                 // fetched once, on first opening

function renderReference(filter) {
  const body = document.getElementById('reference-body');
  const wanted = filter.trim().toLowerCase();
  const shown = wanted.length === 0 ? reference : reference.filter((entry) =>
    entry.template.toLowerCase().includes(wanted)
    || entry.summary.toLowerCase().includes(wanted)
    || entry.category.toLowerCase().includes(wanted));

  if (shown.length === 0) {
    body.replaceChildren(Object.assign(document.createElement('p'),
      { className: 'doc-empty', textContent: `nothing matches “${filter.trim()}”` }));
    return;
  }

  const parts = [];
  let category = null;
  for (const entry of shown) {
    if (entry.category !== category) {
      category = entry.category;
      parts.push(Object.assign(document.createElement('h4'), { textContent: category }));
    }
    const row = document.createElement('div');
    row.className = 'doc-entry';
    row.append(
      Object.assign(document.createElement('code'), { textContent: entry.template }),
      Object.assign(document.createElement('span'), { textContent: entry.summary }));
    parts.push(row);
  }
  body.replaceChildren(...parts);
}

document.getElementById('reference').addEventListener('click', async () => {
  if (reference === null) {
    try { reference = JSON.parse(await session.predicateReference()); }
    catch (ex) { emit(`% could not read the reference: ${ex}\n`, 'error'); return; }
  }
  const filter = document.getElementById('reference-filter');
  renderReference(filter.value);
  referenceDialog.showModal();
  filter.focus();
});

document.getElementById('reference-filter')
  .addEventListener('input', (e) => renderReference(e.target.value));

document.getElementById('guide').addEventListener('click', () => guideDialog.showModal());

// --- workspaces ----------------------------------------------------------
// A workspace is a project: its own directory, its own files, and — because
// switching starts a fresh engine — its own program. The buffer being edited is
// itself a file in it, so it persists like any other.

const filesEl = document.getElementById('files');
const filesLeft = document.getElementById('files-left');
const filesRight = document.getElementById('files-right');
const workspaceEl = document.getElementById('workspace');

// The strip is one row and scrolls sideways. The arrows show up only when
// something is actually off the edge, so the common case — a handful of files
// that fit — looks like a plain row of chips and nothing has been added to it.
function updateFileNav() {
  const hidden = filesEl.scrollWidth <= filesEl.clientWidth + 1;
  filesLeft.hidden = hidden;
  filesRight.hidden = hidden;
  if (hidden) return;
  filesLeft.disabled = filesEl.scrollLeft <= 0;
  filesRight.disabled =
    filesEl.scrollLeft + filesEl.clientWidth >= filesEl.scrollWidth - 1;
}

const scrollFiles = (direction) =>
  filesEl.scrollBy({ left: direction * Math.max(120, filesEl.clientWidth * 0.8) });

filesLeft.addEventListener('click', () => scrollFiles(-1));
filesRight.addEventListener('click', () => scrollFiles(1));
filesEl.addEventListener('scroll', updateFileNav);
window.addEventListener('resize', updateFileNav);
let currentFile = 'scratch.pl';
// The library the open file belongs to, or null for the workspace's own. One
// editor, two places a file can live — and saving has to reach the right one.
let currentLib = null;
// And which system's Prolog it is. A library's source means what ITS system
// says it means — Scryer's double_quotes is not SWI's is not ISO's — so
// consulting it has to read it that way. Empty for a workspace file, which is
// the user's own program.
let currentDialect = '';

/** Opens a WORKSPACE file — the user's own program, in ISO Prolog. Going
 *  through one function is what keeps `currentLib` from being left pointing at
 *  a library after moving off one: saveBuffer would then write the workspace's
 *  text into the library's file. */
function editingWorkspaceFile(name) {
  currentLib = null;
  currentDialect = '';
  currentFile = name;
  // Remembered like the theme: a reload reopens the file you were looking at.
  settings.update({ openFile: name });
}

/** Writes the editor's buffer back to its file, then mirrors to storage. */
async function saveBuffer() {
  if (currentLib !== null) {
    await libraries.write(currentLib, currentFile, program());
    await libraries.persist();
    return;
  }
  await workspace.write(currentFile, program());
  await workspace.persist();
  await refreshFiles();
}

/** Opens a library's file in the editor. It saves back into the library. */
async function openLibraryFile(name, file) {
  await saveBuffer();
  currentLib = name;
  currentDialect = await libraries.dialect(name);
  currentFile = file;
  await editor.setText((await libraries.read(name, file)) ?? '');
  await refreshFiles();
  emit(`% editing library ${name}/${file}`
     + (currentDialect ? ` — consulting it reads ${currentDialect} Prolog\n` : '\n'), 'note');
}

async function refreshWorkspaces() {
  const all = await workspace.names();
  const active = workspace.active();
  workspaceEl.replaceChildren(...all.map((name) => {
    const option = document.createElement('option');
    option.value = name;
    option.textContent = name;
    option.selected = name === active;
    return option;
  }));
  // Deleting the one you are in would leave the engine's current directory
  // pointing at nothing, so it is switch-away-then-delete by construction.
  document.getElementById('delete-workspace').disabled = all.length < 2;
}

/** Opens a workspace file in the editor, saving whatever was there first.
 *  Returns false when there is no such file — which is also how the debugger
 *  declines to navigate to a frame that lives outside the workspace. */
async function openWorkspaceFile(name) {
  const text = await workspace.read(name);
  if (text === null) return false;
  await saveBuffer();               // whichever place the open file lives in
  editingWorkspaceFile(name);
  await editor.setText(text);
  await refreshFiles();
  return true;
}

async function refreshFiles() {
  // Runs after every change of which file is on screen, so it is also the
  // one place that tells the debug gutter to show THAT file's dots.
  debugUi.fileChanged();
  const names = await workspace.list();
  const chips = names.map((name) => {
    const item = document.createElement('button');
    item.type = 'button';
    item.className = 'file' + (currentLib === null && name === currentFile ? ' current' : '');
    item.textContent = name;
    item.title = `open ${name}`;
    item.addEventListener('click', () => openWorkspaceFile(name));
    return item;
  });

  // A library file is not one of the workspace's, so it gets a chip of its own
  // saying where it is from — otherwise the editor would be showing a file that
  // no chip claims.
  if (currentLib !== null) {
    const chip = document.createElement('span');
    chip.className = 'file current lib';
    chip.textContent = `library(${currentLib}) ${currentFile}`;
    chips.unshift(chip);
  }
  filesEl.replaceChildren(...chips);
  // The open file has to be the one you can see, whatever the strip was
  // scrolled to before: `nearest` so it moves the strip and not the page.
  filesEl.querySelector('.current')?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  updateFileNav();
}

/** Opens a workspace. Its files are a different program, so the engine starts
 *  over — with a word first if there was anything to lose. */
async function openWorkspace(name, { confirm = true } = {}) {
  if (name === workspace.active()) return;
  if (confirm && (consultedSomething || pending || stepping)) {
    const ok = await ask(
      `Open workspace “${name}”?`,
      'A workspace is a separate program, so the engine starts fresh: what is '
      + 'loaded now, and any query in progress, are discarded.',
      'Open');
    if (!ok) { workspaceEl.value = workspace.active(); return; }
  }
  await abandonQuery();
  await saveBuffer();

  const err = await workspace.setActive(name);
  if (err) { emit(`% ${err}\n`, 'error'); await refreshWorkspaces(); return; }
  // In debug mode the fresh engine must be debug-compiled again for the new
  // workspace (onWorkspaceChanged does that); otherwise a plain reset.
  if (debugUi.active()) await debugUi.onWorkspaceChanged();
  else await session.resetEngine();
  consultedSomething = false;
  // Different files, so possibly different imports: a background build should
  // re-aim at what THIS workspace uses.
  consultEpoch++;
  settings.update({ workspace: name });

  const files = await workspace.list();
  editingWorkspaceFile(files[0] ?? 'scratch.pl');
  if (files.length === 0) await workspace.write(currentFile, '');
  await editor.setText((await workspace.read(currentFile)) ?? '');
  await refreshWorkspaces();
  await refreshFiles();
  emit(`% workspace ${name} — the engine is fresh\n`, 'note');
}

workspaceEl.addEventListener('change', () => openWorkspace(workspaceEl.value));

document.getElementById('new-workspace').addEventListener('click', async () => {
  const name = await askFor('New workspace', '');
  if (!name) return;
  const err = await workspace.create(name);
  if (err) { emit(`% ${err}\n`, 'error'); return; }
  await refreshWorkspaces();
  await openWorkspace(name);
});

document.getElementById('delete-workspace').addEventListener('click', async () => {
  const doomed = workspace.active();
  const count = (await workspace.list()).length;
  const ok = await ask(
    `Delete workspace “${doomed}”?`,
    `Its ${count} file(s) are deleted from this browser's storage. This cannot `
    + 'be undone — export the workspace first if you want to keep it.',
    'Delete');
  if (!ok) return;

  // Move out of it before removing it: the active workspace is the engine's
  // current directory.
  const others = (await workspace.names()).filter((n) => n !== doomed);
  await openWorkspace(others[0], { confirm: false });
  const err = await workspace.removeWorkspace(doomed);
  emit(err ? `% ${err}\n` : `% deleted workspace ${doomed}\n`, err ? 'error' : 'note');
  // The workspace is gone; its remembered breakpoints go with it.
  if (!err) debugUi.forgetWorkspace(doomed);
  await refreshWorkspaces();
});

document.getElementById('export-workspace').addEventListener('click', async () => {
  await saveBuffer();
  try {
    emit(`% exported ${await workspace.exportZip()}\n`, 'note');
  } catch (ex) {
    emit(`% export failed: ${ex && ex.message ? ex.message : ex}\n`, 'error');
  }
});

// --- files ---------------------------------------------------------------

document.getElementById('open-file').addEventListener('click', async () => {
  const names = await workspace.openFiles();
  if (names.length === 0) return;
  await saveBuffer();               // may be a library file: it saves where it belongs
  editingWorkspaceFile(names[0]);
  await editor.setText((await workspace.read(currentFile)) ?? '');
  await workspace.persist();
  await refreshFiles();
  emit(`% opened ${names.join(', ')}\n`, 'note');
});

document.getElementById('download-file').addEventListener('click', async () => {
  await saveBuffer();
  const saved = await workspace.saveFile(currentFile, program());
  emit(`% saved ${saved}\n`, 'note');
});

// Keys that mean something here rather than to the browser.
//
// Ctrl-Enter loads the buffer. Not a Ctrl-letter: there is no letter free
// across browsers — B, I and M open a sidebar, page info or mute the tab in
// Firefox; E, K and L go to the address bar; J, D, P, R, U, W are taken
// everywhere. Ctrl-Enter is claimed by none of them, and it is already what
// "run this" means in SWISH, Jupyter and every playground of this shape.
//
// Ctrl-S saves the FILE. The browser's own meaning here — download this HTML —
// is never what someone editing a program wants.
addEventListener('keydown', async (e) => {
  if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
  if (e.key === 'Enter') {
    e.preventDefault();
    await consultBuffer('% consulted.\n');
    return;
  }
  if (e.key.toLowerCase() === 's') {
    e.preventDefault();
    await saveBuffer();
    emit(`% saved ${currentFile}\n`, 'note');
  }
});

document.getElementById('new-file').addEventListener('click', async () => {
  const name = await askFor('New file', 'program.pl');
  if (!name) return;
  await saveBuffer();               // may be a library file: it saves where it belongs
  await workspace.write(name, '');
  editingWorkspaceFile(name);
  await editor.setText('');
  await workspace.persist();
  await refreshFiles();
});

document.getElementById('delete-file').addEventListener('click', async () => {
  // A library file is not the workspace's to delete: it belongs to the
  // collection, and removing one there is what Libraries… is for.
  if (currentLib !== null) {
    emit(`% ${currentFile} belongs to library ${currentLib} — remove it from Libraries…\n`,
         'note');
    return;
  }
  const doomed = currentFile;
  const ok = await ask(
    `Delete “${doomed}”?`,
    'Its text is deleted from this browser. Whatever you already consulted '
    + 'stays in the engine until you consult again.',
    'Delete');
  if (!ok) return;

  // Deliberately NOT saving the buffer first: that would write the file back
  // out on the way to deleting it.
  await workspace.remove(doomed);
  const left = await workspace.list();
  const next = left[0] ?? 'scratch.pl';
  if (left.length === 0) await workspace.write(next, '');
  editingWorkspaceFile(next);
  await editor.setText((await workspace.read(next)) ?? '');
  await workspace.persist();
  await refreshFiles();
  emit(`% deleted ${doomed}\n`, 'note');
});

// --- sharing -------------------------------------------------------------
// No server to store anything on, so the program travels in the URL fragment —
// which browsers never send anywhere. Sharing a link does not hand the code to
// a third party.

// The fragment reads `#<label>~<payload>`: a name a person can recognise before
// the encoded part. The label is decoration — the payload says what it is — so a
// hand-edited one cannot make the loader do the wrong thing.
const SHARE_MARK = '~';
const labelFor = (name) => name.replace(/[^A-Za-z0-9._-]/g, '_').slice(0, 40);

document.getElementById('share').addEventListener('click', async () => {
  shareDialog.showModal();
  const what = await new Promise((resolve) =>
    shareDialog.addEventListener('close', () => resolve(shareDialog.returnValue), { once: true }));
  if (what !== 'file' && what !== 'workspace') return;

  let label, payload;
  if (what === 'file') {
    await saveBuffer();
    label = labelFor(currentFile);
    payload = await session.shareFile(currentFile, program(), queryInput.value);
  } else {
    await saveBuffer();
    label = labelFor(workspace.active());
    payload = await session.shareWorkspace(queryInput.value);
  }

  // The link goes to the clipboard and nowhere else. Putting it in the address
  // bar would leave YOUR page sitting on a share link, so reloading would
  // re-open what you just shared.
  const url = location.origin + location.pathname + '#' + label + SHARE_MARK + payload;
  try {
    await navigator.clipboard.writeText(url);
    emit(`% link copied — ${what}, ${url.length} characters\n`, 'note');
  } catch {
    emit(`% link: ${url}\n`, 'note');
  }
});

/** Same name, same text — then there is nothing to do. */
const sameText = (a, b) => (a ?? null) === (b ?? null);

/**
 * Writes one file a link brought, asking before replacing anything.
 *
 * A file that is not here yet, or that is here and identical, needs no
 * question. Only a DIFFERENT file of the same name is a decision, and it is the
 * user's. `state.policy` carries an Always/Never through the rest of THIS
 * link's files, so a workspace of twenty does not ask twenty times.
 *
 * @returns 'written' | 'kept' | 'stopped'
 */
async function mergeSharedFile(file, state) {
  const existing = await workspace.read(file.name);
  if (existing === null) { await workspace.write(file.name, file.text); return 'written'; }
  if (sameText(existing, file.text)) return 'kept';

  if (state.policy === 'never') return 'kept';
  if (state.policy !== 'always') {
    const answers = [
      { value: 'yes', label: 'Yes', primary: true },
      { value: 'no', label: 'No' },
    ];
    // Always / Never only mean something while files remain to ask about.
    if (state.remaining > 1) {
      answers.push({ value: 'always', label: 'Always' }, { value: 'never', label: 'Never' });
    }
    const answer = await askChoice(
      `Replace ${file.name}?`,
      `You already have a ${file.name}, and the link's is different. Replacing `
      + 'it overwrites what is in this workspace.',
      answers);
    if (answer === 'always' || answer === 'never') state.policy = answer;
    if (answer === 'no' || answer === '') return 'kept';
    if (answer === 'never') return 'kept';
  }
  await workspace.write(file.name, file.text);
  return 'written';
}

/** Merges every file of a share into the active workspace, reporting what
 *  happened once rather than file by file. */
async function mergeSharedFiles(files) {
  const state = { policy: null, remaining: files.length };
  let written = 0, kept = 0;
  for (const file of files) {
    const outcome = await mergeSharedFile(file, state);
    state.remaining--;
    if (outcome === 'written') written++; else kept++;
  }
  await workspace.persist();
  return { written, kept };
}

/**
 * Opens what a link carried.
 *
 * Nothing is ever overwritten, and nothing is duplicated either: what arrives is
 * compared against what is already here, and only a DIFFERENT thing of the same
 * name gets a name of its own. Following the same link twice therefore leaves
 * one copy, not a trail of them.
 */
async function openShared(shared) {
  if (shared.kind === 'file') {
    const [file] = shared.files;
    if (!file) return;
    const { written } = await mergeSharedFiles([file]);
    editingWorkspaceFile(file.name);
    await editor.setText((await workspace.read(file.name)) ?? '');
    await refreshFiles();
    emit(written
      ? `% loaded ${file.name} from a shared link\n`
      : `% opened your own ${file.name} — the link's is the same or you kept yours\n`,
      'note');
    return;
  }

  // A workspace: the link names one, and that is the one it goes into. A new
  // name is created; an existing one is merged into, file by file, asking
  // before anything of yours is replaced.
  const target = shared.label || 'shared';
  const existing = await workspace.names();
  if (!existing.includes(target)) {
    const err = await workspace.create(target);
    if (err) { emit(`% ${err}\n`, 'error'); return; }
    // FILLED before it is opened: opening an empty workspace seeds it with an
    // empty scratch.pl, which would then sit next to the files that arrived.
    const was = workspace.active();
    await workspace.setActive(target);
    for (const file of shared.files) await workspace.write(file.name, file.text);
    await workspace.persist();
    await workspace.setActive(was);
    await openWorkspace(target, { confirm: false });
    emit(`% loaded workspace ${target} from a shared link\n`, 'note');
  } else {
    await openWorkspace(target, { confirm: false });
    const { written, kept } = await mergeSharedFiles(shared.files);
    emit(`% workspace ${target}: ${written} file(s) from the link, ${kept} of yours kept\n`,
         'note');
  }

  const here = await workspace.list();
  editingWorkspaceFile(here[0] ?? 'scratch.pl');
  await editor.setText((await workspace.read(currentFile)) ?? '');
  await refreshWorkspaces();
  await refreshFiles();
}

/** Reads a share out of the URL fragment, if there is one there. */
async function openSharedFromHash() {
  const found = /^#[^~]*~(.+)$/.exec(location.hash);
  if (!found) return false;
  const unpacked = await session.shareDecode(found[1]);
  if (!unpacked) { emit('% that link could not be read\n', 'error'); return false; }
  await openShared(unpacked);
  queryInput.value = unpacked.query;
  // The link has been applied, so the page stops standing on it: the address
  // goes back to the plain site. That is also what makes the SAME link work a
  // second time — a browser already sitting on a URL does not navigate to it
  // again, and a fragment that never changes fires no event.
  history.replaceState(null, '', location.pathname + location.search);
  return true;
}

// Pasting a link into the address bar of a page that is ALREADY open changes
// only the fragment, and a fragment change does not reload anything — the
// browser fires this instead. Without it a link appeared to do nothing until
// the page was refreshed by hand. (Our own Share button uses replaceState,
// which deliberately does not fire it: sharing must not re-open what you are
// already looking at.)
addEventListener('hashchange', () => openSharedFromHash());

/**
 * Loads the buffer into the engine. The buffer IS a file, so it is saved first:
 * what ran and what is stored are the same text. Reports either way, and
 * returns whether it loaded.
 */
async function consultBuffer(note) {
  // Loading a program ends any query still open over the old one — running, or
  // waiting for the next solution. Otherwise `;` would go on asking a search
  // whose clauses have been replaced underneath it.
  await abandonQuery();
  await saveBuffer();
  const started = performance.now();
  // In debug mode a workspace file is consulted BY ITS FILE (it was just
  // saved), so its debug sites — and everyone's breakpoints — key by the
  // file's name instead of the anonymous buffer. A library file keeps the
  // buffer path either way.
  const err = await whileConsulting(
    () => withBusy('consulting', () =>
      debugUi.active() && currentLib === null
        ? workspace.consultFile(currentFile)
        : session.consult(program(), currentDialect)));
  const took = Math.round(performance.now() - started);
  if (!err) {
    editor?.markError(null);
    // Operators the program declared are now in the table, so the buffer's
    // colouring can change on consult — repaint it.
    editor?.repaint();
    // A reconsult replaced the compiled clauses: in debug mode the engine-side
    // breakpoints are re-applied against the new code.
    await debugUi.afterConsult();
    consultedSomething = true;
    // How long it took, when it took long enough to have been noticed: loading
    // a library is seconds of work and saying so is the difference between
    // "slow" and "broken".
    emit(took >= 1000 ? note.replace(/\n$/, ` (${(took / 1000).toFixed(1)}s)\n`) : note, 'note');
    return true;
  }
  emit(err + '\n', 'error');
  // A parse error names its position; put it on the editor too, so the report
  // and the text the user has to fix are not in different places.
  const at = /(\d+):(\d+):/.exec(err);
  if (at) editor?.markError(Number(at[1]), Number(at[2]), err);
  return false;
}

// Whether this engine has been given a program — what makes switching workspace
// worth a word first.
let consultedSomething = false;

document.getElementById('consult').addEventListener('click',
  () => consultBuffer('% consulted.\n'));

// --- boot ----------------------------------------------------------------

const config = settings.load();
theme.attach(document.getElementById('theme'), config.theme,
             (choice) => settings.update({ theme: choice }));
layout.init();

out.textContent = '';

// isolate.js (a plain script, ahead of this module) has already decided what
// to do about cross-origin isolation. Aborting boot is CONTROL FLOW here, not
// a failure — the marked throw below is skipped by the script-error reporter,
// whose "% script error: reloading to isolate the page" used to be the only
// thing a user saw when the wait outlived the hide.
function explainIsolationFailure() {
  // Booting anyway is not an option: the published runtime is the THREADED
  // one, and it asserts on SharedArrayBuffer before answering anything. The
  // old fallback message promised a shared-thread engine that no longer
  // exists in this build — what actually happened was that assert.
  freshLine();
  emit('% the engine needs cross-origin isolation (SharedArrayBuffer) and this'
     + ' page has not managed it yet — the worker that provides it did not'
     + ' take control of this load.\n', 'error');
  emit('% if the worker is still installing, this page will reload itself'
     + ' when it is ready. Otherwise F5 almost always fixes it; if it'
     + ' persists, serve the site with COOP/COEP headers'
     + ' (docs/guide/webshumway.md) — file:// and some private windows cannot'
     + ' isolate at all.\n', 'note');
  setPending(false);
}
if (!crossOriginIsolated) {
  if (window.shumwayIsolationFailed) {
    explainIsolationFailure();
  } else {
    // The dance window: isolate.js is installing the worker and will reload.
    // Normally invisible (the page is hidden and the reload is milliseconds
    // away); on a slow connection the hide times out and THIS text is what
    // the visitor reads while the install keeps going underneath.
    emit('% first visit setup: installing the worker that lets this page run'
       + ' the engine. The page reloads itself when it is ready.\n', 'note');
    window.addEventListener('shumway-isolation-failed',
      explainIsolationFailure, { once: true });
  }
  throw new Error(ISOLATION_ABORT);
}

// --- debug (spike) -------------------------------------------------------
// Console-driven, no UI yet: `shumwayDebug.*` in the devtools console drives
// the loop breakpoint → stop → frames → resume. The stop event lands here —
// while stopped, the query's own promise simply stays pending.
function onDebugStop(stop) {
  // An installed hook takes the stop first (the selftest awaits it this way);
  // then the debug UI, when its mode is on; the console fallback serves
  // whoever drove the engine directly through window.shumwayDebug.
  if (window.shumwayDebug.onStop) { window.shumwayDebug.onStop(stop); return; }
  if (debugUi.active()) { debugUi.onStop(stop); return; }
  emit(`% stopped (${stop.reason}) at ${stop.file}:${stop.line} — ${stop.goal}\n`, 'note');
  emit(`%   frames + vars in the devtools console; `
     + `resume: shumwayDebug.resume('continue'|'into'|'over'|'out')\n`, 'note');
  console.log('[shumway debug] stopped', stop);
  console.table(stop.frames.map((f) => ({
    frame: `${f.name}/${f.arity}`,
    at: `${f.file}:${f.line}`,
    vars: f.vars.map((v) => `${v.name} = ${v.value}`).join(', '),
    residuals: f.residuals.map((r) => r.goals).join(', '),
  })));
}
window.shumwayDebug = {
  enable: () => session.debugEnable(),
  bp: (line, file = '<string>') => session.debugBreakpoint(file, line, true),
  bpOff: (line, file = '<string>') => session.debugBreakpoint(file, line, false),
  resume: (mode = 'continue') => session.debugResume(mode),
  toggle: () => debugUi.toggle(),
};

emit(await session.boot(emitEngineOutput, askForInput, emitDiagnostic, onDebugStop) + '\n\n', 'note');
setPending(false);

editor = attach(
  programEl, session.highlight, await session.highlightKinds(), session.complete);

workspace.init(session.exports());
libraries.init(session.exports());
debugUi.init({
  emit, statusEl, consultBuffer,
  editorEl: programEl,
  getText: program,
  // The file whose lines the gutter's dots belong to — null for a library
  // file, where breakpoints are not offered.
  getFile: () => (currentLib === null ? currentFile : null),
  // Which workspace the breakpoints belong to — deleting it forgets them.
  getWorkspace: () => workspace.active(),
  // Clicking a stack frame in another workspace file navigates to it.
  openFile: openWorkspaceFile,
});

// Settings of another version are discarded rather than guessed at (see
// settings.js), and the stored files were written against the layout they
// described — so they go too. Prototype policy, stated where it happens.
if (settings.wasDiscarded()) {
  await workspace.forgetStorage();
  emit('% stored settings were from another version and have been reset\n', 'note');
} else {
  await workspace.forgetLegacyStorage();
}

// Files: bring back whatever the last session left, then open the workspace it
// was left in.
const restored = await workspace.restoreAll();
// Libraries come back before anything is consulted: a program whose first act
// is use_module(library(…)) must find it.
const restoredLibs = await libraries.restoreAll();
if (restoredLibs > 0) emit(`% ${restoredLibs} library(ies) restored\n`, 'note');
// The examples workspace reappears whenever it is ABSENT: deleting it and
// reloading is how you ask for a fresh, current copy of the examples. (The
// old once-ever flag respected deletion forever — which also meant an updated
// example could never reach an existing profile.)
if (!(await workspace.names()).includes('examples')) await workspace.seedExamples();
// An example added since this profile was created still arrives: seeding only
// the files it lacks leaves anything edited here untouched.
else await workspace.seedExamples(true);
const known = await workspace.names();
await workspace.setActive(known.includes(config.workspace) ? config.workspace : 'scratch');
await refreshWorkspaces();

const inWorkspace = await workspace.list();
// The file that was on screen, if it is still here; the first one otherwise.
editingWorkspaceFile(
  inWorkspace.includes(config.openFile) ? config.openFile
    : inWorkspace[0] ?? 'scratch.pl');
if (inWorkspace.length === 0) await workspace.write(currentFile, '');

// A shared link brings its own files. They are ADDED — never written over what
// is already here — so following a link cannot destroy someone's work.
await editor.setText((await workspace.read(currentFile)) ?? '');
await openSharedFromHash();
await editor.repaintNow();
await refreshFiles();
// The debugger's memory: breakpoints, watches, and the mode itself come back
// the way the theme does. Re-entering the mode restarts the engine
// debug-compiled and reconsults, which also re-binds the dots.
if (debugUi.restore() && location.hash !== '#selftest') await debugUi.toggle();
if (restored > 0) emit(`% ${restored} file(s) restored\n`, 'note');
else if (!workspace.persistent())
  emit('% this browser has no origin-private storage — files last for this session only\n', 'note');
if (!settings.persistent())
  emit('% this browser will not store preferences — the theme lasts for this session only\n', 'note');

// Offline. Registered after boot so it never competes with the runtime download
// on a first visit; the second visit is the one that benefits. (On a host that
// needs the worker for isolation, ensureIsolated already registered it — this
// is the case where the server sends the headers itself and the worker is only
// wanted for offline.)
if ('serviceWorker' in navigator && location.protocol !== 'file:') {
  navigator.serviceWorker.register('sw.js')
    .then(warmOfflineCache)
    .catch(() => { emit('% offline support unavailable in this browser\n', 'note'); });
}

/**
 * Puts this page's own assets in the offline cache.
 *
 * The worker caches what passes THROUGH it, and where the server sends the
 * isolation headers itself there is no first-visit reload — so everything the
 * page loaded was fetched before the worker was controlling anything, and the
 * cache ended up holding the four shell files and nothing else. Going offline
 * after that first visit did not boot: measured, 4 entries against 56.
 *
 * Asking for them a second time, once the worker IS in charge, is what files
 * them. It costs requests rather than bytes — the browser's HTTP cache answers
 * them, and on a later visit the worker answers from its own cache without
 * touching the network at all.
 */
async function warmOfflineCache() {
  try {
    await navigator.serviceWorker.ready;
    if (!navigator.serviceWorker.controller) {
      await new Promise((resolve) => navigator.serviceWorker
        .addEventListener('controllerchange', resolve, { once: true }));
    }
    const queue = [...new Set(performance.getEntriesByType('resource')
      .map((entry) => entry.name)
      .filter((url) => url.startsWith(location.origin)))];
    // A few at a time, behind whatever the user is already doing.
    const worker = async () => {
      while (queue.length > 0) {
        try { await fetch(queue.shift(), { cache: 'force-cache' }); }
        catch { /* no network: there is nothing to warm and nothing to report */ }
      }
    };
    await Promise.all(Array.from({ length: Math.min(4, queue.length) }, worker));
  } catch { /* no worker, or it never took control: the session is unaffected */ }
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
