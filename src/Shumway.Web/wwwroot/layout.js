// The page's resizable seams: the vertical splitter between the two panes,
// and the horizontal handle atop each debug dock. Dragging is plain pointer
// capture; a size is stored as a PERCENTAGE of its container, so a window
// resize keeps the proportion; a double-click gives a seam its default back.
// Only what differs from the default is stored (settings.layout).

import * as settings from './settings.js';

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const $ = (id) => document.getElementById(id);
const dockEl = (side) => $(side === 'left' ? 'debug-tabs-left' : 'debug-tabs');

let layout = {};

function persist(patch) {
  layout = { ...layout, ...patch };
  for (const k of Object.keys(layout)) if (layout[k] == null) delete layout[k];
  settings.update({ layout: Object.keys(layout).length ? layout : null });
}

function apply() {
  const panes = document.querySelector('.panes');
  if (layout.split != null) panes.style.setProperty('--split', layout.split + '%');
  else panes.style.removeProperty('--split');
  for (const [side, key] of [['left', 'ldock'], ['right', 'rdock']]) {
    if (layout[key] != null) dockEl(side).style.flexBasis = layout[key] + '%';
    else dockEl(side).style.removeProperty('flex-basis');
  }
}

/** Pointer-capture drag on `handle`. Synthetic events (the selftest) have no
 *  active pointer to capture — without capture they still land on the handle
 *  they were dispatched at, which is all the wiring check needs. */
function draggable(handle, { begin, move, done }) {
  handle.addEventListener('pointerdown', (e) => {
    e.preventDefault();
    try { handle.setPointerCapture(e.pointerId); } catch { /* synthetic */ }
    begin(e);
    const onMove = (ev) => move(ev, e);
    const onUp = () => {
      handle.removeEventListener('pointermove', onMove);
      handle.removeEventListener('pointerup', onUp);
      handle.removeEventListener('pointercancel', onUp);
      done();
    };
    handle.addEventListener('pointermove', onMove);
    handle.addEventListener('pointerup', onUp);
    handle.addEventListener('pointercancel', onUp);
  });
}

export function init() {
  layout = { ...(settings.get().layout || {}) };
  apply();

  // The pane splitter: the left column's share of the grid.
  const panes = document.querySelector('.panes');
  const splitter = $('split-panes');
  let split = null;
  draggable(splitter, {
    begin: () => { split = null; },
    move: (e) => {
      const r = panes.getBoundingClientRect();
      split = clamp((e.clientX - r.left) / r.width * 100, 25, 75);
      panes.style.setProperty('--split', split + '%');
    },
    done: () => { if (split != null) persist({ split }); },
  });
  splitter.addEventListener('dblclick', () => { persist({ split: null }); apply(); });

  // The dock handles: a dock's height as a share of its pane. Dragging the
  // handle UP grows the dock — its bottom edge is pinned to the pane's.
  for (const [side, key] of [['left', 'ldock'], ['right', 'rdock']]) {
    const dock = dockEl(side);
    const handle = dock.querySelector('.dock-resize');
    let startH = 0, paneH = 0, pct = null;
    draggable(handle, {
      begin: () => {
        startH = dock.getBoundingClientRect().height;
        paneH = dock.parentElement.clientHeight;
        pct = null;
      },
      move: (e, start) => {
        if (paneH <= 0) return;
        pct = clamp((startH + (start.clientY - e.clientY)) / paneH * 100, 12, 85);
        dock.style.flexBasis = pct + '%';
      },
      done: () => { if (pct != null) persist({ [key]: pct }); },
    });
    handle.addEventListener('dblclick', () => { persist({ [key]: null }); apply(); });
  }
}
