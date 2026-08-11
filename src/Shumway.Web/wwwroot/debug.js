// Debug mode: the page-side debugger over the engine's DebugService.
//
// The page has TWO modes. Normal mode is the top level as it always was —
// nothing here shows. Debug mode restarts the engine debug-compiled and adds
// the debugger's furniture: a breakpoint gutter on the editor, a step toolbar
// on the answers pane, and a Call Stack / Locals / Watches pane. One body
// class (`debug-mode`) is the whole switch; everything this module draws
// hides without it.
//
// The stop model matches the engine side (WebDebug.cs): while a query is
// stopped, its own promise in main.js simply stays pending, and this module
// drives the engine with the UNGATED resume/evaluate exports. Everything
// shown at a stop — frames, variables, residual constraints — arrives IN the
// stop event; an on-frame `!` evaluation is followed by a re-capture
// (debugFrames) because it can change what the frames hold.
//
// Breakpoints are kept PER FILE, by the workspace file's name. The engine
// binds a breakpoint by file base name, and debug mode consults the buffer BY
// ITS FILE (see main.js), so a dot in any file of the workspace matches
// however that file gets loaded — directly or through another file's
// directive.

import * as session from './session.js';

let emit, statusEl, consultBuffer, editorEl, getText, getFile;

let debugMode = false;
let stopped = null;          // the current stop event while suspended
let selectedFrame = 0;
let currentLine = 0;         // 1-based stopped line in the CURRENT file, 0 = none

/** file name -> (1-based line -> {state:'bound'|'unbound', condition, log}).
 *  Kept across consults and across mode switches: a dot the user placed is a
 *  statement about the program, not about one engine's lifetime. */
const breakpoints = new Map();

/** Watch goals, re-evaluated against the selected frame at every stop.
 *  Each: { goal, result }. */
const watches = [];

const $ = (id) => document.getElementById(id);
const queryInput = () => $('query');
const basename = (p) => p.slice(p.lastIndexOf('/') + 1);

export function init(deps) {
  ({ emit, statusEl, consultBuffer, editorEl, getText, getFile } = deps);
  $('debug-toggle').addEventListener('click', () => toggle());
  $('dbg-continue').addEventListener('click', () => resume('continue'));
  $('dbg-into').addEventListener('click', () => resume('into'));
  $('dbg-over').addEventListener('click', () => resume('over'));
  $('dbg-out').addEventListener('click', () => resume('out'));
  $('gutter').addEventListener('click', onGutterClick);
  $('gutter').addEventListener('contextmenu', onGutterMenu);
  $('bp-dialog').querySelector('[data-close]')
    .addEventListener('click', () => $('bp-dialog').close(''));
  $('bp-remove').addEventListener('click', () => $('bp-dialog').close('remove'));
  $('watch-form').addEventListener('submit', (e) => {
    e.preventDefault();
    const goal = $('watch-input').value.trim();
    if (!goal) return;
    $('watch-input').value = '';
    watches.push({ goal, result: '' });
    renderWatches();
    if (stopped) evalWatches();
  });
  editorEl.addEventListener('scroll', syncGutterScroll);
  editorEl.addEventListener('input', scheduleGutter);
  addEventListener('resize', scheduleGutter);
  setToolbar(false);
}

export const active = () => debugMode;
export const isStopped = () => stopped !== null;

// --- entering and leaving ------------------------------------------------

export async function toggle() {
  if (debugMode) return exit();
  debugMode = true;
  document.body.classList.add('debug-mode');
  $('debug-toggle').classList.add('active');
  const err = await session.debugEnable();
  if (err) { emit(err + '\n', 'error'); return exit(); }
  emit('% debug mode: engine restarted debug-compiled\n', 'note');
  // The program must be IN the debug engine before anything can stop in it.
  // consultBuffer calls back into afterConsult, which re-applies the dots.
  if (getText().trim()) await consultBuffer('% consulted (debuggable).\n');
  else renderGutter();
}

async function exit() {
  debugMode = false;
  stopped = null;
  currentLine = 0;
  document.body.classList.remove('debug-mode');
  $('debug-toggle').classList.remove('active');
  setToolbar(false);
  restorePrompt();
  // Wakes a stopped query too (cancel releases the stop engine-side), so the
  // reset below cannot wait on a gate the stopped search still holds.
  await session.cancel();
  const err = await session.resetEngine();
  if (err) emit(err + '\n', 'error');
  emit('% debug mode off: engine restarted\n', 'note');
  if (getText().trim()) await consultBuffer('% consulted.\n');
}

/** Called by consultBuffer after every successful consult: a reconsult
 *  replaces the compiled clauses, so the engine-side breakpoints are re-added
 *  against the new code — every file's, since a consult can reload the files
 *  it imports too. Unbound dots become bound here if the new code reaches
 *  them. */
export async function afterConsult() {
  if (!debugMode) return;
  for (const [file, lines] of breakpoints)
    for (const line of [...lines.keys()]) await applyBreakpoint(file, line);
  renderGutter();
}

/** The edited file changed (a chip, a new file, a share): the gutter now
 *  shows THAT file's dots. */
export function fileChanged() {
  if (debugMode) renderGutter();
}

async function applyBreakpoint(file, line) {
  const bp = breakpoints.get(file)?.get(line);
  if (!bp) return;
  const err = await session.debugBreakpoint(file, line, true, bp.condition || '');
  bp.state = err ? 'unbound' : 'bound';
}

// --- the stop ------------------------------------------------------------

/** The stop event, from main.js. Runs on the UI thread while the search
 *  thread is parked on the engine side. */
export function onStop(stop) {
  // A LOGPOINT's hit: say its message, never its stop (the DAP convention).
  if (stop.reason === 'breakpoint' && stop.breakFile) {
    const bp = breakpoints.get(basename(stop.breakFile))?.get(stop.breakLine);
    if (bp?.log) {
      emit('% ' + interpolate(bp.log, stop.frames[0]) + '\n', 'note');
      session.debugResume('continue');
      return;
    }
  }

  stopped = stop;
  selectedFrame = 0;
  const file = getFile();
  currentLine = file && basename(stop.file) === file ? stop.line : 0;
  $('debug-pane').classList.remove('stale');
  renderStack();
  renderLocals();
  renderGutter();
  if (currentLine) scrollEditorToLine(currentLine);
  setToolbar(true);
  statusEl.textContent =
    `stopped (${stop.reason}) at ${basename(stop.file)}:${stop.line} — ${stop.goal}`
    + (stop.conditionError ? `  [condition failed to run: ${stop.conditionError}]` : '');
  const q = queryInput();
  q.dataset.prevPlaceholder ??= q.placeholder;
  q.placeholder = 'goal on the frame — ! binds for real, ; next solution';
  evalWatches();
}

/** A log message's {Name} holes filled from the frame's variables; an unknown
 *  name stays as typed, which is also how a literal brace survives. */
function interpolate(message, frame) {
  return message.replace(/\{([^{}]*)\}/g, (whole, name) => {
    const v = frame?.vars.find((x) => x.name === name.trim());
    return v ? v.value : whole;
  });
}

/** The search moved on — resumed into a solution, an end, or an abort. The
 *  last stack stays visible but dimmed: what it shows is a moment ago, not
 *  now. */
export function clearStopped() {
  if (!stopped) return;
  stopped = null;
  currentLine = 0;
  setToolbar(false);
  restorePrompt();
  $('debug-pane').classList.add('stale');
  renderGutter();
  if (statusEl.textContent.startsWith('stopped')) statusEl.textContent = '';
}

function restorePrompt() {
  const q = queryInput();
  if (q.dataset.prevPlaceholder !== undefined) {
    q.placeholder = q.dataset.prevPlaceholder;
    delete q.dataset.prevPlaceholder;
  }
}

async function resume(mode) {
  if (!stopped) return;
  clearStopped();
  statusEl.textContent = 'running…';
  await session.debugResume(mode);
}

function setToolbar(enabled) {
  for (const id of ['dbg-continue', 'dbg-into', 'dbg-over', 'dbg-out'])
    $(id).disabled = !enabled;
}

// --- the Immediate window ------------------------------------------------

/** A goal typed into the ?- box while stopped: evaluated against the selected
 *  frame, with the engine's Immediate semantics — `!` on-frame, `;` for the
 *  parked evaluation's next solution. */
export async function evaluate(goalText) {
  if (!stopped) return;
  emit('?- ' + goalText + '\n', 'query');
  const result = await session.debugEvaluate(selectedFrame, goalText);
  emit(result + '\n', /error/i.test(result) ? 'error' : 'answer');
  // `!` runs on the REAL frame: what Locals and the residual rows show may
  // just have changed. Re-capture rather than guess.
  if (goalText.trim().startsWith('!') && stopped) {
    const now = await session.debugFrames();
    if (now && stopped) {
      stopped = { ...stopped, frames: now.frames };
      if (selectedFrame >= now.frames.length) selectedFrame = 0;
      renderStack();
      renderLocals();
    }
    evalWatches();
  }
}

// --- watches -------------------------------------------------------------

let watchRun = 0;

async function evalWatches() {
  const run = ++watchRun;
  const at = stopped;
  for (const w of watches) {
    if (stopped !== at || run !== watchRun) return;   // the world moved on
    if (w.goal.startsWith('!')) {
      // Re-running an on-frame goal at EVERY stop would repeat its effects
      // silently; that gesture belongs to the ?- box, one shot at a time.
      w.result = 'on-frame goals (!) are for the ?- box';
    } else {
      w.result = await session.debugEvaluate(selectedFrame, w.goal);
    }
    renderWatches();
  }
}

function renderWatches() {
  const list = $('debug-watches');
  list.replaceChildren();
  if (watches.length === 0) {
    const row = document.createElement('div');
    row.className = 'debug-row muted';
    row.textContent = '(no watches)';
    list.appendChild(row);
    return;
  }
  watches.forEach((w, i) => {
    const row = document.createElement('div');
    row.className = 'debug-row watch-row';
    const text = document.createElement('span');
    text.className = 'watch-text';
    const goal = document.createElement('span');
    goal.className = 'watch-goal';
    goal.textContent = w.goal;
    const result = document.createElement('span');
    result.className = 'watch-result';
    result.textContent = w.result ? '  →  ' + w.result : '';
    text.append(goal, result);
    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'watch-remove';
    remove.textContent = '✕';
    remove.title = 'remove this watch';
    remove.addEventListener('click', () => {
      watches.splice(i, 1);
      renderWatches();
    });
    row.append(text, remove);
    list.appendChild(row);
  });
}

// --- call stack and locals -----------------------------------------------

function renderStack() {
  const list = $('debug-stack');
  list.replaceChildren();
  if (!stopped) return;
  stopped.frames.forEach((f, i) => {
    const row = document.createElement('div');
    row.className = 'debug-row' + (i === selectedFrame ? ' selected' : '');
    const where = f.line > 0 ? `  ${basename(f.file)}:${f.line}` : '';
    row.textContent = `${f.name}${f.headArgs || ''}${where}`;
    row.title = `${f.name}/${f.arity}`;
    row.addEventListener('click', () => {
      selectedFrame = i;
      renderStack();
      renderLocals();
      if (basename(f.file) === getFile() && f.line > 0) scrollEditorToLine(f.line);
      evalWatches();      // watches mean "in the frame I am looking at"
    });
    list.appendChild(row);
  });
}

function renderLocals() {
  const list = $('debug-locals');
  list.replaceChildren();
  const frame = stopped?.frames[selectedFrame];
  if (!frame) return;
  for (const v of frame.vars) {
    const row = document.createElement('div');
    row.className = 'debug-row';
    row.textContent = `${v.name} = ${v.value}`;
    list.appendChild(row);
  }
  // Residual constraints, one row per owner variable — the browser's version
  // of the ⟨constraints⟩ rows the desktop debuggers show.
  for (const r of frame.residuals) {
    const row = document.createElement('div');
    row.className = 'debug-row residual';
    row.textContent = `${r.var} ⟨${r.goals}⟩`;
    list.appendChild(row);
  }
  if (frame.vars.length === 0 && frame.residuals.length === 0) {
    const row = document.createElement('div');
    row.className = 'debug-row muted';
    row.textContent = '(no variables here)';
    list.appendChild(row);
  }
}

// --- the gutter ----------------------------------------------------------
// The editor wraps long lines (pre-wrap), so gutter rows sit at MEASURED line
// tops, not at line-index × line-height. Rows live in an inner element that is
// translated to follow the editor's own scroll.

let gutterTimer = 0;

function scheduleGutter() {
  if (!debugMode) return;
  clearTimeout(gutterTimer);
  gutterTimer = setTimeout(renderGutter, 150);
}

function syncGutterScroll() {
  $('gutter-lines').style.transform = `translateY(${-editorEl.scrollTop}px)`;
}

const MaxGutterLines = 2000;

/** Top (px, content-relative) of each logical line, by measuring a range at
 *  each line's first character in one pass over the editor's text nodes. */
function measureLineTops() {
  const text = getText();
  const starts = [0];
  for (let i = 0; i < text.length && starts.length < MaxGutterLines; i++)
    if (text[i] === '\n') starts.push(i + 1);

  const lineHeight = parseFloat(getComputedStyle(editorEl).lineHeight) || 19.5;
  const base = editorEl.getBoundingClientRect().top - editorEl.scrollTop;
  const tops = [];
  const range = document.createRange();
  const walker = document.createTreeWalker(editorEl, NodeFilter.SHOW_TEXT);
  let node, seen = 0, want = 0;
  while (want < starts.length && (node = walker.nextNode())) {
    while (want < starts.length && starts[want] - seen <= node.data.length) {
      const at = starts[want] - seen;
      // A one-character range gives the character's own box; a collapsed range
      // at a line start often reports the END of the previous line instead.
      range.setStart(node, at);
      range.setEnd(node, Math.min(at + 1, node.data.length));
      const r = range.getClientRects()[0] || range.getBoundingClientRect();
      tops.push(r.height > 0 || r.top !== 0
        ? r.top - base
        : (tops.length ? tops[tops.length - 1] + lineHeight : 0));
      want++;
      if (at >= node.data.length) break;   // next start is in a later node
    }
    seen += node.data.length;
  }
  // Lines the walk did not reach (empty text) still get a row each.
  while (tops.length < starts.length)
    tops.push((tops.length ? tops[tops.length - 1] : 0) + lineHeight);
  return { tops, lineHeight };
}

function renderGutter() {
  if (!debugMode) return;
  const lines = $('gutter-lines');
  const file = getFile();
  const fileBps = file ? breakpoints.get(file) : null;
  const { tops, lineHeight } = measureLineTops();
  const frag = document.createDocumentFragment();
  for (let i = 0; i < tops.length; i++) {
    const line = i + 1;
    const row = document.createElement('div');
    row.className = 'gutter-row';
    const bp = fileBps?.get(line);
    if (bp) {
      row.classList.add('bp', bp.state);
      if (bp.log) row.classList.add('log');
      else if (bp.condition) row.classList.add('conditional');
    }
    if (line === currentLine) row.classList.add('current');
    row.dataset.line = line;
    row.style.top = tops[i] + 'px';
    row.style.height = lineHeight + 'px';
    row.title = bp
      ? (bp.log ? `logpoint at line ${line}` : `breakpoint at line ${line}`)
        + (bp.condition ? ` when ${bp.condition}` : '')
        + (bp.state === 'unbound' ? ' (not bound to any code yet)' : '')
        + ' — click removes, right-click edits'
      : `set a breakpoint at line ${line} (right-click: condition / logpoint)`;
    frag.appendChild(row);
  }
  lines.replaceChildren(frag);
  syncGutterScroll();
}

async function onGutterClick(e) {
  const line = Number(e.target?.dataset?.line);
  const file = getFile();
  if (!line || !file) return;
  const fileBps = breakpoints.get(file);
  if (fileBps?.has(line)) {
    fileBps.delete(line);
    await session.debugBreakpoint(file, line, false);
  } else {
    if (!breakpoints.has(file)) breakpoints.set(file, new Map());
    breakpoints.get(file).set(line, { state: 'unbound', condition: '', log: '' });
    await applyBreakpoint(file, line);
  }
  renderGutter();
}

/** Right-click: the breakpoint's extras — condition and log message. Creates
 *  the breakpoint if the line has none yet. */
async function onGutterMenu(e) {
  const line = Number(e.target?.dataset?.line);
  const file = getFile();
  if (!line || !file) return;
  e.preventDefault();
  if (!breakpoints.has(file)) breakpoints.set(file, new Map());
  const fileBps = breakpoints.get(file);
  const existed = fileBps.has(line);
  const bp = fileBps.get(line) ?? { state: 'unbound', condition: '', log: '' };

  const dialog = $('bp-dialog');
  $('bp-title').textContent = `Breakpoint — ${file}:${line}`;
  $('bp-condition').value = bp.condition;
  $('bp-log').value = bp.log;
  $('bp-remove').hidden = !existed;
  dialog.returnValue = '';
  dialog.showModal();
  await new Promise((r) => dialog.addEventListener('close', r, { once: true }));

  if (dialog.returnValue === 'remove') {
    fileBps.delete(line);
    await session.debugBreakpoint(file, line, false);
  } else if (dialog.returnValue === 'ok') {
    bp.condition = $('bp-condition').value.trim();
    bp.log = $('bp-log').value.trim();
    fileBps.set(line, bp);
    await applyBreakpoint(file, line);
  } else if (!existed) {
    fileBps.delete(line);       // cancelled the creation
  }
  renderGutter();
}

function scrollEditorToLine(line) {
  const { tops } = measureLineTops();
  const top = tops[line - 1];
  if (top === undefined) return;
  const view = editorEl.clientHeight;
  if (top < editorEl.scrollTop || top > editorEl.scrollTop + view - 30)
    editorEl.scrollTop = Math.max(0, top - view / 3);
  renderGutter();
}
