// WebShumway's front end: a Prolog top level in the page.
//
// The interaction is the one every Prolog top level has, and the reason the
// engine hands back solutions one at a time: show a solution, then wait to be
// asked for the next. Here `;` (or space) asks, `.` (or Enter, or Escape)
// stops — the same keys the console REPL answers to, so the habit transfers.

import * as session from './session.js';
import * as workspace from './workspace.js';
import * as settings from './settings.js';
import * as theme from './theme.js';
import { attach } from './editor.js';

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

async function step() {
  stepping = true;
  let tag, text;
  try { ({ tag, text } = await session.next(answerWidth())); }
  finally { stepping = false; }
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

// Cancel closes without submitting, so the only submit button is the one Enter
// should press. Escape closes with an empty returnValue, which reads as cancel
// too — nothing here treats anything but a named answer as yes.
//
// The confirmation dialog has no such button in the markup: its answers vary by
// question, so askChoice builds them — including the cancelling one.
for (const dialog of [promptDialog, shareDialog, referenceDialog, guideDialog]) {
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
const workspaceEl = document.getElementById('workspace');
let currentFile = 'scratch.pl';

/** Writes the editor's buffer back to its file, then mirrors to storage. */
async function saveBuffer() {
  await workspace.write(currentFile, program());
  await workspace.persist();
  await refreshFiles();
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

async function refreshFiles() {
  const names = await workspace.list();
  filesEl.replaceChildren(...names.map((name) => {
    const item = document.createElement('button');
    item.type = 'button';
    item.className = 'file' + (name === currentFile ? ' current' : '');
    item.textContent = name;
    item.title = `open ${name}`;
    item.addEventListener('click', async () => {
      await workspace.write(currentFile, program());   // don't lose the buffer
      currentFile = name;
      await editor.setText((await workspace.read(name)) ?? '');
      await refreshFiles();
    });
    return item;
  }));
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
  await session.resetEngine();
  consultedSomething = false;
  settings.update({ workspace: name });

  const files = await workspace.list();
  currentFile = files[0] ?? 'scratch.pl';
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
  await workspace.write(currentFile, program());
  currentFile = names[0];
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
  await workspace.write(currentFile, program());
  await workspace.write(name, '');
  currentFile = name;
  await editor.setText('');
  await workspace.persist();
  await refreshFiles();
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
    currentFile = file.name;
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
  currentFile = here[0] ?? 'scratch.pl';
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
  const err = await session.consult(program());
  if (!err) {
    editor?.markError(null);
    // Operators the program declared are now in the table, so the buffer's
    // colouring can change on consult — repaint it.
    editor?.repaint();
    consultedSomething = true;
    emit(note, 'note');
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

out.textContent = '';
emit(await session.boot(emitEngineOutput, askForInput) + '\n\n', 'note');
setPending(false);

editor = attach(
  programEl, session.highlight, await session.highlightKinds(), session.complete);

workspace.init(session.exports());

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
if (!config.seededExamples) {
  await workspace.seedExamples();
  settings.update({ seededExamples: true });
}
const known = await workspace.names();
await workspace.setActive(known.includes(config.workspace) ? config.workspace : 'scratch');
await refreshWorkspaces();

const inWorkspace = await workspace.list();
currentFile = inWorkspace[0] ?? 'scratch.pl';
if (inWorkspace.length === 0) await workspace.write(currentFile, '');

// A shared link brings its own files. They are ADDED — never written over what
// is already here — so following a link cannot destroy someone's work.
await editor.setText((await workspace.read(currentFile)) ?? '');
await openSharedFromHash();
await editor.repaintNow();
await refreshFiles();
if (restored > 0) emit(`% ${restored} file(s) restored\n`, 'note');
else if (!workspace.persistent())
  emit('% this browser has no origin-private storage — files last for this session only\n', 'note');
if (!settings.persistent())
  emit('% this browser will not store preferences — the theme lasts for this session only\n', 'note');

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
