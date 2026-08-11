// Debug mode: the page-side debugger over the engine's DebugService.
//
// The page has TWO modes. Normal mode is the top level as it always was —
// nothing here shows. Debug mode restarts the engine debug-compiled and adds
// the debugger's furniture: a breakpoint gutter on the editor, a step toolbar
// on the answers pane, and a Call Stack / Locals pane. One body class
// (`debug-mode`) is the whole switch; everything this module draws hides
// without it.
//
// The stop model matches the engine side (WebDebug.cs): while a query is
// stopped, its own promise in main.js simply stays pending, and this module
// drives the engine with the UNGATED resume export. Everything shown at a stop
// — frames, variables, residual constraints — arrives IN the stop event, so
// no engine call is needed (or possible: the stopped search holds the engine
// gate) to render it.

import * as session from './session.js';

let emit, statusEl, consultBuffer, editorEl, getText;

let debugMode = false;
let stopped = null;          // the current stop event while suspended
let selectedFrame = 0;
let currentLine = 0;         // 1-based stopped line in <string>, 0 = none

// Breakpoints by 1-based editor line -> 'bound' | 'unbound'. Kept across
// consults and across mode switches: a dot the user placed is a statement
// about the program, not about one engine's lifetime.
const breakpoints = new Map();

const $ = (id) => document.getElementById(id);

export function init(deps) {
  ({ emit, statusEl, consultBuffer, editorEl, getText } = deps);
  $('debug-toggle').addEventListener('click', () => toggle());
  $('dbg-continue').addEventListener('click', () => resume('continue'));
  $('dbg-into').addEventListener('click', () => resume('into'));
  $('dbg-over').addEventListener('click', () => resume('over'));
  $('dbg-out').addEventListener('click', () => resume('out'));
  $('gutter').addEventListener('click', onGutterClick);
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
 *  against the new code. Unbound dots (typo lines, not-yet-consulted code)
 *  become bound here if the new consult reaches them. */
export async function afterConsult() {
  if (!debugMode) return;
  for (const line of [...breakpoints.keys()]) await applyBreakpoint(line);
  renderGutter();
}

async function applyBreakpoint(line) {
  const err = await session.debugBreakpoint('<string>', line, true);
  breakpoints.set(line, err ? 'unbound' : 'bound');
}

// --- the stop ------------------------------------------------------------

/** The stop event, from main.js. Runs on the UI thread while the search
 *  thread is parked on the engine side. */
export function onStop(stop) {
  stopped = stop;
  selectedFrame = 0;
  currentLine = stop.file === '<string>' ? stop.line : 0;
  $('debug-pane').classList.remove('stale');
  renderStack();
  renderLocals();
  renderGutter();
  if (currentLine) scrollEditorToLine(currentLine);
  setToolbar(true);
  statusEl.textContent =
    `stopped (${stop.reason}) at ${stop.file}:${stop.line} — ${stop.goal}`;
}

/** The search moved on — resumed into a solution, an end, or an abort. The
 *  last stack stays visible but dimmed: what it shows is a moment ago, not
 *  now. */
export function clearStopped() {
  if (!stopped) return;
  stopped = null;
  currentLine = 0;
  setToolbar(false);
  $('debug-pane').classList.add('stale');
  renderGutter();
  if (statusEl.textContent.startsWith('stopped')) statusEl.textContent = '';
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

// --- call stack and locals -----------------------------------------------

function renderStack() {
  const list = $('debug-stack');
  list.replaceChildren();
  if (!stopped) return;
  stopped.frames.forEach((f, i) => {
    const row = document.createElement('div');
    row.className = 'debug-row' + (i === selectedFrame ? ' selected' : '');
    const where = f.line > 0 ? `  ${f.file}:${f.line}` : '';
    row.textContent = `${f.name}${f.headArgs || ''}${where}`;
    row.title = `${f.name}/${f.arity}`;
    row.addEventListener('click', () => {
      selectedFrame = i;
      renderStack();
      renderLocals();
      if (f.file === '<string>' && f.line > 0) scrollEditorToLine(f.line);
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
  const { tops, lineHeight } = measureLineTops();
  const frag = document.createDocumentFragment();
  for (let i = 0; i < tops.length; i++) {
    const line = i + 1;
    const row = document.createElement('div');
    row.className = 'gutter-row';
    const state = breakpoints.get(line);
    if (state) row.classList.add('bp', state);
    if (line === currentLine) row.classList.add('current');
    row.dataset.line = line;
    row.style.top = tops[i] + 'px';
    row.style.height = lineHeight + 'px';
    row.title = state
      ? `breakpoint at line ${line}${state === 'unbound' ? ' (not bound to any code yet)' : ''} — click to remove`
      : `set a breakpoint at line ${line}`;
    frag.appendChild(row);
  }
  lines.replaceChildren(frag);
  syncGutterScroll();
}

async function onGutterClick(e) {
  const line = Number(e.target?.dataset?.line);
  if (!line) return;
  if (breakpoints.has(line)) {
    breakpoints.delete(line);
    await session.debugBreakpoint('<string>', line, false);
  } else {
    breakpoints.set(line, 'unbound');
    await applyBreakpoint(line);
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
