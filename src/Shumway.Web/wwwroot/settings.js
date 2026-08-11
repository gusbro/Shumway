// What the page remembers between visits.
//
// One envelope, under one key, carrying a VERSION. Everything the page stores
// as a preference goes in it, so there is exactly one place that decides what
// happens when the shape changes. Today that decision is to discard and start
// clean — this is a prototype and the stored shape is still moving — but the
// decision is written here rather than discovered later as a page that misreads
// yesterday's data.
//
// Preferences live in localStorage, not in OPFS, on purpose: they are the
// page's, not the user's. Deleting a workspace must not take the theme with it.

const KEY = 'shumway.settings';

/** Bump when the stored shape changes. See migrate(). */
export const SETTINGS_VERSION = 1;

const DEFAULTS = {
  v: SETTINGS_VERSION,
  theme: 'dark',            // what a first visit gets; the toggle overrides it
  workspace: 'scratch',     // the one to open on load
  openFile: null,           // the file that was on screen, reopened on load
  // The debugger's own memory: mode on/off, breakpoints per file (condition,
  // log message, enabled), watch goals. Written by debug.js; a reload
  // reapplies the lot. Additive over v1 — the merge fills it in.
  debug: null,
};

/**
 * Brings a stored envelope up to the current version, or gives up on it.
 *
 * @returns the settings to use, and whether anything was discarded — the caller
 *          says so rather than letting preferences vanish silently.
 */
function migrate(stored) {
  if (!stored || typeof stored !== 'object') return { settings: { ...DEFAULTS }, discarded: false };
  if (stored.v === SETTINGS_VERSION) {
    const settings = { ...DEFAULTS, ...stored };
    // No theme stored is no CHOICE made, so it takes the default rather than
    // meaning "follow the system" — a null that predates there being a default.
    settings.theme ??= DEFAULTS.theme;
    return { settings, discarded: false };
  }
  // No migration path yet, by choice. When one is worth writing it goes here,
  // version by version, and this line becomes the last resort.
  return { settings: { ...DEFAULTS }, discarded: true };
}

let current = { ...DEFAULTS };
let discarded = false;
let usable = true;          // false when the browser refuses storage

/** Reads the stored settings. Call once, early. */
export function load() {
  try {
    const raw = localStorage.getItem(KEY);
    const result = migrate(raw ? JSON.parse(raw) : null);
    current = result.settings;
    discarded = result.discarded;
    if (discarded) save();      // do not re-read the discarded shape next time
  } catch {
    // A browser that refuses storage is not a failure worth stopping for: the
    // session works, it just will not remember.
    usable = false;
    current = { ...DEFAULTS };
  }
  return current;
}

/** True when a stored envelope of another version was thrown away by load(). */
export const wasDiscarded = () => discarded;

/** True when this browser lets us remember anything at all. */
export const persistent = () => usable;

export const get = () => current;

/** Merges `patch` into the settings and stores them. */
export function update(patch) {
  current = { ...current, ...patch, v: SETTINGS_VERSION };
  save();
  return current;
}

function save() {
  if (!usable) return;
  try {
    localStorage.setItem(KEY, JSON.stringify(current));
  } catch {
    usable = false;
  }
}
