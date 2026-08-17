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
// Breakpoints are kept per WORKSPACE, then per file name. The engine binds a
// breakpoint by file base name, and debug mode consults the buffer BY ITS FILE
// (see main.js), so a dot in any file of the workspace matches however that
// file gets loaded — directly or through another file's directive. Deleting a
// workspace forgets its dots (forgetWorkspace); two workspaces may hold a file
// of the same name with different code and different breakpoints.

import * as session from './session.js';
import * as settings from './settings.js';

let emit, statusEl, consultBuffer, editorEl, getText, getFile, getWorkspace, openFile;

let debugMode = false;
let stopped = null;          // the current stop event while suspended
let selectedFrame = 0;
let currentLine = 0;         // 1-based stopped line in the CURRENT file, 0 = none
let frameLine = 0;           // 1-based SELECTED-frame line in the current file
let frameFile = '';          // ...and the file that line belongs to

/** workspace -> file name -> (1-based line ->
 *  {state:'bound'|'unbound', condition, log, enabled}).
 *  Kept across consults and across mode switches: a dot the user placed is a
 *  statement about the program, not about one engine's lifetime. A DISABLED
 *  one is removed from the engine but kept here, grey — the VS convention.
 *  Keyed by WORKSPACE first: two workspaces may hold a file of the same name
 *  with different code, and a workspace's dots vanish when it is deleted. */
const allBreakpoints = new Map();

/** The current workspace's file→line→bp map, made on demand. */
function breakpoints() {
  const ws = getWorkspace();
  let m = allBreakpoints.get(ws);
  if (!m) { m = new Map(); allBreakpoints.set(ws, m); }
  return m;
}

/** Run to Cursor's one-shot breakpoint: engine-side only, never drawn.
 *  Cleared at the next stop, or when the query ends without reaching it. */
let tempBp = null;

/** Watch goals, re-evaluated against the selected frame at every stop.
 *  Each: { goal, result }. */
const watches = [];

const $ = (id) => document.getElementById(id);
const queryInput = () => $('query');
const basename = (p) => p.slice(p.lastIndexOf('/') + 1);

export function init(deps) {
  ({ emit, statusEl, consultBuffer, editorEl, getText, getFile, getWorkspace, openFile } = deps);
  $('immediate-form').addEventListener('submit', (e) => {
    e.preventDefault();
    const goal = $('immediate-input').value.trim();
    if (!goal) return;
    $('immediate-input').value = '';
    if (immHistory[immHistory.length - 1] !== goal) immHistory.push(goal);
    immAt = immHistory.length;
    immDraft = '';
    evaluate(goal);
  });
  $('immediate-input').addEventListener('keydown', onImmediateKey);
  $('dbg-pause').addEventListener('click', () => session.debugBreakNow());
  $('debug-toggle').addEventListener('click', () => toggle());
  $('dbg-continue').addEventListener('click', () => resume('continue'));
  $('dbg-into').addEventListener('click', () => resume('into'));
  $('dbg-over').addEventListener('click', () => resume('over'));
  $('dbg-out').addEventListener('click', () => resume('out'));
  $('gutter').addEventListener('click', onGutterClick);
  $('gutter').addEventListener('contextmenu', onGutterMenu);
  // The same line menu on the SOURCE itself — where the right hand already
  // is. Only in debug mode: normal mode keeps the browser's own menu.
  editorEl.addEventListener('contextmenu', onEditorMenu);
  for (const b of document.querySelectorAll('.debug-tabs .tab-strip button'))
    b.addEventListener('click', () => selectTab(b.dataset.tab));
  for (const b of document.querySelectorAll('.debug-tabs .tab-move'))
    b.addEventListener('click', () => moveActiveTab(b.dataset.dock));
  renderDocks();
  // The usual debugger keys, only while the mode is on — F5 must stay the
  // browser's refresh in normal mode. All of these are preventDefault-able
  // in current browsers (F11's fullscreen and F5's reload included).
  addEventListener('keydown', onDebugKey);
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
    persist();
    if (stopped) evalWatches();
  });
  editorEl.addEventListener('scroll', syncGutterScroll);
  editorEl.addEventListener('input', scheduleGutter);
  addEventListener('resize', scheduleGutter);
  setToolbar(false);
}

export const active = () => debugMode;
export const isStopped = () => stopped !== null;

// --- what survives a reload ----------------------------------------------
// The debugger's memory rides the same envelope as the theme: mode,
// breakpoints (condition, log, enabled), watch goals. Saved on every change,
// reapplied by main.js at boot.

function persist() {
  const bps = {};   // workspace -> file -> line -> {c,l,e}
  for (const [ws, files] of allBreakpoints) {
    const perFile = {};
    for (const [file, lines] of files) {
      const perLine = {};
      for (const [line, bp] of lines)
        perLine[line] = { c: bp.condition, l: bp.log, e: bp.enabled };
      if (Object.keys(perLine).length) perFile[file] = perLine;
    }
    if (Object.keys(perFile).length) bps[ws] = perFile;
  }
  settings.update({
    debug: {
      on: debugMode, bps, watches: watches.map((w) => w.goal),
      // The dock split: only the views sent LEFT are worth remembering —
      // right is the default home.
      dleft: TAB_IDS.filter((t) => dockOf[t] === 'left'),
    },
  });
}

/** Restores the saved metadata into the page's model. Returns whether debug
 *  MODE was on — the caller decides when it is safe to re-enter it. */
export function restore() {
  const d = settings.get().debug;
  if (!d) return false;
  for (const [ws, files] of Object.entries(d.bps || {})) {
    const wsMap = new Map();
    for (const [file, lines] of Object.entries(files)) {
      const m = new Map();
      for (const [line, bp] of Object.entries(lines))
        m.set(Number(line), {
          state: 'unbound',
          condition: bp.c || '',
          log: bp.l || '',
          enabled: bp.e !== false,
        });
      if (m.size) wsMap.set(file, m);
    }
    if (wsMap.size) allBreakpoints.set(ws, wsMap);
  }
  for (const goal of d.watches || [])
    if (!watches.some((w) => w.goal === goal)) watches.push({ goal, result: '' });
  renderWatches();
  for (const t of d.dleft || [])
    if (t in dockOf) dockOf[t] = 'left';
  renderDocks();
  return !!d.on;
}

/** A workspace was deleted: its breakpoints go with it. */
export function forgetWorkspace(name) {
  if (allBreakpoints.delete(name)) persist();
  renderGutter();
}

/** The active workspace changed. In debug mode the engine openWorkspace just
 *  reset is a PLAIN one — it must be debug-compiled again for the new
 *  workspace, or a consult there binds nothing. The caller consults the
 *  buffer afterwards (afterConsult re-applies this workspace's dots). */
export async function onWorkspaceChanged() {
  clearStopped();
  if (!debugMode) return;
  const err = await session.debugEnable();
  if (err) emit(err + '\n', 'error');
}

// Whether a query is SEARCHING right now (main.js reports it around each
// pull of the next solution). Break is the one button that lives then.
let running = false;
export function setRunning(on) {
  running = on;
  updateButtons();
}

// --- the Immediate input's history, the query box's manners ---------------

const immHistory = [];
let immAt = 0;
let immDraft = '';

function onImmediateKey(e) {
  const input = $('immediate-input');
  if (e.key === 'ArrowUp') {
    if (immAt === 0) return;
    e.preventDefault();
    if (immAt === immHistory.length) immDraft = input.value;
    immAt--;
    input.value = immHistory[immAt];
  } else if (e.key === 'ArrowDown') {
    if (immAt >= immHistory.length) return;
    e.preventDefault();
    immAt++;
    input.value = immAt === immHistory.length ? immDraft : immHistory[immAt];
  }
}

// --- the two view docks ----------------------------------------------------
// Two dock zones, VS-style: the classic one under the transcript (right) and
// a second one under the editor (left), so two views — say Call stack +
// Locals — stay visible at once. dockOf is the single source of truth;
// renderDocks() MOVES the button and panel elements between docks (their
// listeners travel with the nodes), keeps one active view per dock, and
// collapses a dock with nothing in it. Default: everything right.
const TAB_IDS = ['tab-stack', 'tab-locals', 'tab-watches', 'tab-immediate'];
const dockOf = Object.fromEntries(TAB_IDS.map((t) => [t, 'right']));
const activeIn = { left: null, right: 'tab-stack' };
const dockEl = (d) => $(d === 'left' ? 'debug-tabs-left' : 'debug-tabs');
const tabButton = (t) => document.querySelector(`.debug-tabs [data-tab="${t}"]`);

function renderDocks() {
  for (const d of ['left', 'right']) {
    const el = dockEl(d);
    const strip = el.querySelector('.tab-strip');
    const tabs = TAB_IDS.filter((t) => dockOf[t] === d);
    if (!tabs.includes(activeIn[d])) activeIn[d] = tabs[0] ?? null;
    for (const t of tabs) {         // canonical order, nodes re-homed as needed
      strip.appendChild(tabButton(t));
      el.appendChild($(t));
    }
    el.classList.toggle('empty', tabs.length === 0);
    for (const t of tabs) {
      tabButton(t).classList.toggle('active', t === activeIn[d]);
      $(t).hidden = t !== activeIn[d];
    }
  }
}

/** Sends a dock's current view to the other dock (the ⇄ button). */
function moveActiveTab(from) {
  const id = activeIn[from];
  if (!id) return;
  const to = from === 'left' ? 'right' : 'left';
  dockOf[id] = to;
  activeIn[to] = id;
  renderDocks();
  persist();
}

function selectTab(id) {
  activeIn[dockOf[id]] = id;
  renderDocks();
}

function onDebugKey(e) {
  if (!debugMode) return;
  switch (e.key) {
    case 'F9':
      e.preventDefault();
      { const line = caretLine();
        if (line > 0) (e.ctrlKey ? toggleEnableAt : toggleBreakpointAt)(line); }
      return;
    case 'F5':
      // Swallowed even when nothing is stopped: mid-session a reflex F5 would
      // throw the whole debug session away. Ctrl+R still reloads.
      e.preventDefault();
      if (stopped) resume('continue');
      return;
    case 'F10':
      if (e.ctrlKey && e.shiftKey) {   // Set Next Statement, the VS key
        e.preventDefault();
        { const line = caretLine();
          if (canSetNext(line)) setNextStatement(line); }
        return;
      }
      if (e.ctrlKey) {         // Run to Cursor, the VS key
        e.preventDefault();
        { const line = caretLine(); if (line > 0) runToCursor(line); }
        return;
      }
      if (!stopped) return;
      e.preventDefault();
      resume('over');
      return;
    case 'F11':
      if (!stopped) return;
      e.preventDefault();
      resume(e.shiftKey ? 'out' : 'into');
      return;
  }
}

/** The 1-based editor line the caret is on, or 0 when the caret is elsewhere. */
function caretLine() {
  const sel = document.getSelection();
  if (!sel || sel.rangeCount === 0) return 0;
  const range = sel.getRangeAt(0);
  if (!editorEl.contains(range.startContainer)) return 0;
  const r = document.createRange();
  r.selectNodeContents(editorEl);
  r.setEnd(range.startContainer, range.startOffset);
  return r.toString().split('\n').length;
}

// --- entering and leaving ------------------------------------------------

export async function toggle() {
  if (debugMode) return exit();
  debugMode = true;
  document.body.classList.add('debug-mode');
  $('debug-toggle').classList.add('active');
  persist();                               // the INTENT survives an instant reload
  const err = await session.debugEnable();
  if (err) { emit(err + '\n', 'error'); return exit(); }
  emit('% debug mode: engine restarted debug-compiled\n', 'note');
  persist();
  $('immediate-log').replaceChildren();   // a fresh session, a fresh conversation
  // The program must be IN the debug engine before anything can stop in it.
  // consultBuffer calls back into afterConsult, which re-applies the dots.
  if (getText().trim()) await consultBuffer('% consulted (debuggable).\n');
  else renderGutter();
}

async function exit() {
  debugMode = false;
  persist();
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
  persist();
  if (getText().trim()) await consultBuffer('% consulted.\n');
  // The dots are KEPT (they come back with the mode) but must not be drawn
  // in normal mode — re-render the now-plain number column.
  renderGutter();
}

/** Called by consultBuffer after every successful consult: a reconsult
 *  replaces the compiled clauses, so the engine-side breakpoints are re-added
 *  against the new code — every file's, since a consult can reload the files
 *  it imports too. Unbound dots become bound here if the new code reaches
 *  them. */
export async function afterConsult() {
  if (!debugMode) return;
  for (const [file, lines] of breakpoints())
    for (const line of [...lines.keys()]) await applyBreakpoint(file, line);
  renderGutter();
}

/** The edited file changed (a chip, a new file, a share): the gutter now
 *  shows THAT file's numbers and dots. */
export function fileChanged() {
  renderGutter();
}

async function applyBreakpoint(file, line) {
  const bp = breakpoints().get(file)?.get(line);
  if (!bp) return;
  if (!bp.enabled) {
    await session.debugBreakpoint(file, line, false);
    return;
  }
  const err = await session.debugBreakpoint(file, line, true, bp.condition || '');
  bp.state = err ? 'unbound' : 'bound';
}

async function toggleEnableAt(line) {
  const file = getFile();
  const bp = file && breakpoints().get(file)?.get(line);
  if (!bp || !debugMode) return;
  bp.enabled = !bp.enabled;
  await applyBreakpoint(file, line);
  renderGutter();
  persist();
}

/** Whether Set Next Statement accepts this line on the selected frame: the
 *  engine says which lines are valid targets, per frame, in the stop event. */
function canSetNext(line) {
  const f = stopped?.frames[selectedFrame];
  return !!f && (f.setNextLines || []).includes(line)
    && basename(f.file) === getFile();
}

/** Set Next Statement: move where the query resumes to `line` on the selected
 *  frame — nothing runs; the arrow moves. The engine returns the re-captured
 *  stop, which we render in place. */
async function setNextStatement(line) {
  if (!stopped) return;
  const result = await session.debugSetNext(selectedFrame, line);
  if (result.error) { emit(`% set next statement: ${result.error}\n`, 'error'); return; }
  // Render the moved arrow like a fresh stop at the same place — the machine
  // did not run, but its position (and so the frames) changed.
  onStop(result);
}

/** Run to Cursor: a one-shot engine breakpoint at the line, then continue.
 *  Meaningful while stopped (resume to there) or running (arm ahead). */
async function runToCursor(line) {
  const file = getFile();
  if (!file || !debugMode || (!stopped && !running)) return;
  tempBp = { file, line };
  await session.debugBreakpoint(file, line, true, '');
  if (stopped) await resume('continue');
}

/** Retires the one-shot breakpoint: gone from the engine, or handed back to
 *  the REAL breakpoint at the same line (condition included). */
async function clearTempBp() {
  if (!tempBp) return;
  const { file, line } = tempBp;
  tempBp = null;
  const real = breakpoints().get(file)?.get(line);
  if (real && real.enabled) await applyBreakpoint(file, line);
  else await session.debugBreakpoint(file, line, false);
}

// --- the stop ------------------------------------------------------------

/** The stop event, from main.js. Runs on the UI thread while the search
 *  thread is parked on the engine side. */
export function onStop(stop) {
  // A LOGPOINT's hit: say its message, never its stop (the DAP convention).
  if (stop.reason === 'breakpoint' && stop.breakFile) {
    const bp = breakpoints().get(basename(stop.breakFile))?.get(stop.breakLine);
    if (bp?.log) {
      emit('% ' + interpolate(bp.log, stop.frames[0]) + '\n', 'note');
      session.debugResume('continue');
      return;
    }
  }

  // Step landings in the top level's own scaffolding are noise, not places: the
  // copy_term/3 the top level wraps EVERY query in (the residual-constraint
  // display), and the step-abandoned ports of a query already yielding its
  // answer. Stepping out of the last user frame runs to the answer — the VS
  // behaviour — instead of touring the wrapper's plumbing goal by goal.
  const topFrame = stop.frames[0];
  if (stop.reason !== 'breakpoint'
      && ((stop.reason === 'stepabandoned' && !stop.goal)
          || (stop.goal === 'copy_term/3' && (!topFrame || topFrame.arity === -1)))) {
    session.debugResume('continue');
    return;
  }

  clearTempBp();               // Run to Cursor's shot is spent, wherever we stopped
  stopped = stop;
  // A stop standing in library code selects the first USER frame — the one
  // whose variables the person actually wants — the VS behaviour.
  selectedFrame = Math.max(0, stop.frames.findIndex((f) => !isOpaqueFrame(f)));
  frameLine = 0;
  frameFile = '';
  const file = getFile();
  currentLine = file && basename(stop.file) === file ? stop.line : 0;
  const sel = stop.frames[selectedFrame];
  if (selectedFrame > 0 && sel && sel.line > 0 && basename(sel.file) === file) {
    frameFile = file;
    frameLine = sel.line;      // the hollow marker: where you are LOOKING
  }
  for (const el of document.querySelectorAll('.debug-tabs')) el.classList.remove('stale');
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

/** The search came BACK — a solution, an end, an abort (main.js calls this
 *  when the pending pull resolves). Run to Cursor's shot retires here: the
 *  run is over and it was not hit. NOT called on resume — the whole point of
 *  the one-shot breakpoint is to be armed while the search runs. */
export function clearStopped() {
  clearTempBp();
  clearStoppedUi();
}

/** The UI side alone — used by resume, where the temp breakpoint must live. */
function clearStoppedUi() {
  if (!stopped) return;
  stopped = null;
  currentLine = 0;
  frameLine = 0;
  frameFile = '';
  setToolbar(false);
  restorePrompt();
  for (const el of document.querySelectorAll('.debug-tabs')) el.classList.add('stale');
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
  // An evaluation still in flight runs ON the parked machine; resuming under
  // it is the race that left the engine catatonic. Drain first (the engine
  // bounds a runaway evaluation at its own 15s timeout).
  await drainEvals();
  if (!stopped) return;      // the drain can outlive the stop (cancel won)
  clearStoppedUi();
  statusEl.textContent = 'running…';
  await session.debugResume(mode);
}

/** Waits for any in-flight Immediate/watch evaluations, capped so an
 *  abandoning caller (Stop) is never held hostage by a slow goal. */
export function drainEvals(capMs = 4000) {
  return Promise.race([evalChain, new Promise((r) => setTimeout(r, capMs))]);
}

function setToolbar(enabled) {
  for (const id of ['dbg-continue', 'dbg-into', 'dbg-over', 'dbg-out'])
    $(id).disabled = !enabled;
  updateButtons();
}

function updateButtons() {
  // Break is the inverse of the steps: alive while the search runs free.
  $('dbg-pause').disabled = !(debugMode && running && !stopped);
}

// --- the Immediate window ------------------------------------------------

/** Appends a line to the Immediate tab's log, scrolled to the tail. */
function immLog(text, role = '') {
  const log = $('immediate-log');
  const span = document.createElement('span');
  if (role) span.className = role;
  span.textContent = text;
  log.appendChild(span);
  log.scrollTop = log.scrollHeight;
}

/** ONE evaluation at a time, page-wide: the engine refuses overlapping
 *  evaluations ("an evaluation is already running"), and a watch sweep
 *  colliding with a typed goal produced exactly that. Everything that
 *  evaluates goes through this chain. */
let evalChain = Promise.resolve();

function enqueueEval(fn) {
  const run = evalChain.then(fn, fn);
  evalChain = run.then(() => {}, () => {});
  return run;
}

/** A bare variable name — `X` or `X.` — is a QUESTION about the frame, not a
 *  goal (as a goal it is call(<value>), a nonsense the engine answers with an
 *  existence error). Answered from the frame directly, residuals included.
 *  Returns null when the text is not a bare variable. */
function frameVariableAnswer(text) {
  const m = /^([A-Z_][A-Za-z0-9_]*)\s*\.?$/.exec(text);
  if (!m) return null;
  const frame = stopped?.frames[selectedFrame];
  if (!frame) return null;
  const v = frame.vars.find((x) => x.name === m[1]);
  if (!v) return `unknown variable ${m[1]} in this frame`;
  const residual = frame.residuals.find((r) => r.var === m[1]);
  return `${v.name} = ${v.value}` + (residual ? `  ⟨${residual.goals}⟩` : '');
}

/** A goal from the Immediate tab or the ?- box while stopped: evaluated
 *  against the selected frame, with the engine's Immediate semantics — `!`
 *  on-frame, `;` for the parked evaluation's next solution. The conversation
 *  lives in the Immediate tab, which is switched in so the answer is seen
 *  whichever input it came from. */
export async function evaluate(goalText) {
  if (!stopped) return;
  selectTab('tab-immediate');
  immLog('?- ' + goalText + '\n', 'query');

  const direct = frameVariableAnswer(goalText.trim());
  if (direct !== null) { immLog(direct + '\n', 'answer'); return; }

  const result = await enqueueEval(
    () => session.debugEvaluate(selectedFrame, goalText));
  immLog(result + '\n', /error/i.test(result) ? 'error' : 'answer');
  // `!` runs on the REAL frame: what Locals and the residual rows show may
  // just have changed. Re-capture rather than guess.
  if (goalText.trim().startsWith('!') && stopped) {
    const now = await enqueueEval(() => session.debugFrames());
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
      // silently; that gesture belongs to the Immediate box, one shot at a time.
      w.result = 'on-frame goals (!) are for the Immediate box';
    } else {
      const direct = frameVariableAnswer(w.goal);
      w.result = direct !== null
        ? direct
        : await enqueueEval(() => session.debugEvaluate(selectedFrame, w.goal));
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
      persist();
    });
    row.append(text, remove);
    list.appendChild(row);
  });
}

// --- call stack and locals -----------------------------------------------

/** A real predicate frame standing in code that carries no source location:
 *  the prelude, a library — code the user did not write. Shown collapsed. */
const isOpaqueFrame = (f) => f.arity >= 0 && !f.file;

function renderStack() {
  const list = $('debug-stack');
  list.replaceChildren();
  if (!stopped) return;
  // Consecutive library frames collapse to one grey row — the VS [External
  // Code] convention. The run's predicates go in the tooltip.
  let opaqueRun = null;
  const flushOpaque = () => {
    if (!opaqueRun) return;
    const row = document.createElement('div');
    row.className = 'debug-row stack-row muted';
    const goal = document.createElement('span');
    goal.className = 'stack-goal';
    goal.textContent = '[library code]';
    row.appendChild(goal);
    row.title = opaqueRun.map((f) => `${f.name}/${f.arity}`).join(', ');
    list.appendChild(row);
    opaqueRun = null;
  };
  stopped.frames.forEach((f, i) => {
    if (isOpaqueFrame(f)) { (opaqueRun ??= []).push(f); return; }
    flushOpaque();
    const row = document.createElement('div');
    row.className = 'debug-row stack-row' + (i === selectedFrame ? ' selected' : '');
    const goal = document.createElement('span');
    goal.className = 'stack-goal';
    // total(...)!2 — the VS convention: WHICH clause of the predicate runs.
    goal.textContent = `${f.name}${f.headArgs || ''}`
      + (f.clause > 0 ? `!${f.clause}` : '');
    row.appendChild(goal);
    if (f.line > 0) {
      const where = document.createElement('span');
      where.className = 'stack-where';
      where.textContent = `${basename(f.file).replace(/\.pl$/, '')}:${f.line}`;
      row.appendChild(where);
    }
    row.title = `${f.name}/${f.arity}`;
    row.addEventListener('click', async () => {
      selectedFrame = i;
      renderStack();
      renderLocals();
      evalWatches();      // watches mean "in the frame I am looking at"
      // Navigate to the frame: same file scrolls; another WORKSPACE file opens
      // in the editor first (openFile declines files outside the workspace).
      const base = basename(f.file);
      if (f.line > 0
          && (base === getFile() || (openFile && await openFile(base)))) {
        frameFile = base;
        frameLine = f.line;
        scrollEditorToLine(f.line);
      }
      renderGutter();
    });
    list.appendChild(row);
  });
  flushOpaque();
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
// Always visible: it is the editor's line-number column. Debug mode adds the
// breakpoint dots and the position markers on top. The editor wraps long
// lines (pre-wrap), so rows sit at MEASURED line tops, not at line-index ×
// line-height; they live in an inner element translated to follow the
// editor's own scroll.

let gutterTimer = 0;

function scheduleGutter() {
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
  // Content-relative tops, measured against the GUTTER's own origin: the rows
  // are positioned inside it. Measuring against the editor's border-box put
  // every dot one border-width too low.
  const base = $('gutter').getBoundingClientRect().top - editorEl.scrollTop;
  const tops = [];
  const range = document.createRange();
  const walker = document.createTreeWalker(editorEl, NodeFilter.SHOW_TEXT);
  let node, seen = 0, want = 0;
  while (want < starts.length && (node = walker.nextNode())) {
    // STRICTLY inside this node: a start sitting exactly at its end (the
    // newline closed a coloured span) belongs to the NEXT node's first
    // character. Measuring it here collapsed the range at the node's end,
    // whose rect is the END of the previous line — the line then drew at
    // its neighbour's height, numberless.
    while (want < starts.length && starts[want] - seen < node.data.length) {
      const at = starts[want] - seen;
      // A one-character range gives the character's own box; a collapsed range
      // at a line start often reports the END of the previous line instead.
      range.setStart(node, at);
      range.setEnd(node, at + 1);
      const r = range.getClientRects()[0] || range.getBoundingClientRect();
      tops.push(r.height > 0 || r.top !== 0
        ? r.top - base
        : (tops.length ? tops[tops.length - 1] + lineHeight : 0));
      want++;
    }
    seen += node.data.length;
  }
  // Lines the walk did not reach (empty text) still get a row each.
  while (tops.length < starts.length)
    tops.push((tops.length ? tops[tops.length - 1] : 0) + lineHeight);
  return { tops, lineHeight };
}

function renderGutter() {
  const lines = $('gutter-lines');
  const file = getFile();
  const fileBps = debugMode && file ? breakpoints().get(file) : null;
  const { tops, lineHeight } = measureLineTops();
  const frag = document.createDocumentFragment();
  for (let i = 0; i < tops.length; i++) {
    const line = i + 1;
    const row = document.createElement('div');
    row.className = 'gutter-row';
    row.textContent = line;
    const bp = fileBps?.get(line);
    if (bp) {
      row.classList.add('bp', bp.state);
      if (!bp.enabled) row.classList.add('disabled');
      else if (bp.log) row.classList.add('log');
      else if (bp.condition) row.classList.add('conditional');
    }
    if (debugMode && line === currentLine) row.classList.add('current');
    else if (debugMode && line === frameLine && frameFile === file)
      row.classList.add('frame');   // the SELECTED frame's line, VS-green style
    row.dataset.line = line;
    row.style.top = tops[i] + 'px';
    row.style.height = lineHeight + 'px';
    if (debugMode)
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
  if (line && debugMode) await toggleBreakpointAt(line);
}

async function toggleBreakpointAt(line) {
  const file = getFile();
  if (!file || !debugMode) return;
  const fileBps = breakpoints().get(file);
  if (fileBps?.has(line)) {
    fileBps.delete(line);
    await session.debugBreakpoint(file, line, false);
  } else {
    if (!breakpoints().has(file)) breakpoints().set(file, new Map());
    breakpoints().get(file).set(line,
      { state: 'unbound', condition: '', log: '', enabled: true });
    await applyBreakpoint(file, line);
  }
  renderGutter();
  persist();
}

/** Right-click: the debugger's line menu — toggle, enable/disable, the
 *  condition/logpoint dialog, Run to Cursor. */
function onGutterMenu(e) {
  const line = Number(e.target?.dataset?.line);
  const file = getFile();
  if (!line || !file || !debugMode) return;
  e.preventDefault();
  openLineMenu(e.clientX, e.clientY, line);
}

/** Right-click on the SOURCE: the line comes from the click's height, against
 *  the same measured tops the gutter rows sit at — so wrapped lines resolve
 *  to their logical line, not a visual row. */
function onEditorMenu(e) {
  const file = getFile();
  if (!file || !debugMode) return;
  e.preventDefault();
  const { tops } = measureLineTops();
  const yContent = e.clientY - $('gutter').getBoundingClientRect().top
    + editorEl.scrollTop;
  let line = 1;
  for (let i = 0; i < tops.length; i++)
    if (tops[i] <= yContent) line = i + 1; else break;
  openLineMenu(e.clientX, e.clientY, line);
}

function openLineMenu(x, y, line) {
  const file = getFile();
  const bp = breakpoints().get(file)?.get(line);
  showMenu(x, y, [
    { label: bp ? 'Remove breakpoint' : 'Set breakpoint',
      key: 'F9', run: () => toggleBreakpointAt(line) },
    bp && { label: bp.enabled ? 'Disable breakpoint' : 'Enable breakpoint',
      key: 'Ctrl+F9', run: () => toggleEnableAt(line) },
    { label: 'Condition / logpoint…', run: () => openBpDialog(line) },
    { label: 'Run to cursor', key: 'Ctrl+F10',
      disabled: !stopped && !running, run: () => runToCursor(line) },
    { label: 'Set next statement', key: 'Ctrl+Shift+F10',
      disabled: !canSetNext(line), run: () => setNextStatement(line) },
  ].filter(Boolean));
}

/** A small positioned menu; any click or Escape takes it down. */
function showMenu(x, y, items) {
  document.querySelector('.gutter-menu')?.remove();
  const menu = document.createElement('div');
  menu.className = 'gutter-menu';
  for (const item of items) {
    const b = document.createElement('button');
    b.type = 'button';
    b.disabled = !!item.disabled;
    const label = document.createElement('span');
    label.textContent = item.label;
    b.appendChild(label);
    if (item.key) {
      const key = document.createElement('kbd');
      key.textContent = item.key;
      b.appendChild(key);
    }
    b.addEventListener('click', () => { close(); item.run(); });
    menu.appendChild(b);
  }
  const close = () => {
    menu.remove();
    removeEventListener('pointerdown', away, true);
    removeEventListener('keydown', esc, true);
  };
  const away = (ev) => { if (!menu.contains(ev.target)) close(); };
  const esc = (ev) => { if (ev.key === 'Escape') { ev.preventDefault(); close(); } };
  addEventListener('pointerdown', away, true);
  addEventListener('keydown', esc, true);
  document.body.appendChild(menu);
  // On screen, then clamped: a menu opened near an edge stays readable.
  const r = menu.getBoundingClientRect();
  menu.style.left = Math.min(x, innerWidth - r.width - 8) + 'px';
  menu.style.top = Math.min(y, innerHeight - r.height - 8) + 'px';
}

/** The breakpoint's extras — condition and log message. Creates the
 *  breakpoint if the line has none yet. */
async function openBpDialog(line) {
  const file = getFile();
  if (!file) return;
  if (!breakpoints().has(file)) breakpoints().set(file, new Map());
  const fileBps = breakpoints().get(file);
  const existed = fileBps.has(line);
  const bp = fileBps.get(line)
    ?? { state: 'unbound', condition: '', log: '', enabled: true };

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
  persist();
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
