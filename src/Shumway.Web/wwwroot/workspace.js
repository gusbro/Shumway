// Making the page's files outlive the page, grouped by project.
//
// A workspace is a directory the engine can see: Prolog's files live in the
// browser's in-memory filesystem, which is what makes consult/1 and open/4 work
// in a browser at all, and the active workspace is the current directory. In-
// memory means gone on reload, so each workspace is mirrored to OPFS: origin-
// private storage, no permission prompt, survives reloads, available in every
// current browser.
//
// The mirror is deliberately one-directional at each moment: OPFS -> memory when
// the page loads, memory -> OPFS after anything changes. Nothing merges, so
// there is no case where two versions of a file have to be reconciled.
//
// Real files on the user's disk are a separate, explicit act (open/save/export
// below), because reading and writing someone's filesystem should be something
// they asked for rather than something a page does while they are not looking.

// Only for the examples' bookkeeping: which version of each one this profile
// was given, so a shipped change can reach it without writing over an edit.
import * as settings from './settings.js';

let engine = null;

export function init(exports) { engine = exports; }

// --- the in-memory side (the engine's) -----------------------------------
// All of these are async because the engine's exports are: the runtime lives on
// its own thread, so every call to it is a message and a reply.

const lines = (s) => (s.length === 0 ? [] : s.split('\n'));

export const names = async () => lines(await engine.WorkspaceNames());
export const select = (name) => engine.WorkspaceSelect(name);
export const create = (name) => engine.WorkspaceCreate(name);
export const list = async () => lines(await engine.WorkspaceList());
export const read = (name) => engine.WorkspaceRead(name);
export const write = (name, content) => engine.WorkspaceWrite(name, content);
export const remove = (name) => engine.WorkspaceDelete(name);
export const consultFile = (name) => engine.ConsultWorkspaceFile(name);

/** Removes a workspace and its files, from memory and from storage. */
export async function removeWorkspace(name) {
  const err = await engine.WorkspaceRemove(name);
  if (err) return err;
  if (persistent()) {
    try { (await root()).removeEntry(name, { recursive: true }); } catch { /* never stored */ }
  }
  return null;
}

// --- the persistent side (OPFS) ------------------------------------------

/** True when this browser gives us origin-private storage. */
export const persistent = () =>
  typeof navigator !== 'undefined' && navigator.storage && !!navigator.storage.getDirectory;

const root = async () => (await navigator.storage.getDirectory())
  .getDirectoryHandle('workspaces', { create: true });

/** Storage must not be able to hang the page. A browser that never answers is
 *  treated as one that has no storage: the session works, it just forgets. */
function withTimeout(promise, ms, fallback) {
  return Promise.race([
    promise,
    new Promise((resolve) => setTimeout(() => {
      lastError = `storage did not respond within ${ms} ms`;
      resolve(fallback);
    }, ms)),
  ]);
}

/** Copies every stored workspace into the engine's filesystem. Returns how many
 *  files arrived, across all of them. */
export async function restoreAll() {
  if (!persistent()) return 0;
  return withTimeout(restoreCore(), 5000, 0);
}

async function restoreCore() {
  let count = 0;
  const active = await activeName();
  try {
    const dir = await root();
    for await (const [workspace, handle] of dir.entries()) {
      if (handle.kind !== 'directory') continue;
      await select(workspace);
      for await (const [name, file] of handle.entries()) {
        if (file.kind !== 'file') continue;
        await write(name, await (await file.getFile()).text());
        count++;
      }
    }
  } catch {
    // A browser that refuses storage is not a failure worth stopping for.
  }
  await select(active);
  return count;
}

/** Copies the ACTIVE workspace into OPFS, deleting what is no longer in it. */
export async function persist() {
  if (!persistent()) return false;
  return withTimeout(persistCore(), 3000, false);
}

async function persistCore() {
  try {
    const dir = await (await root()).getDirectoryHandle(await activeName(), { create: true });
    const present = new Set(await list());
    for (const name of present) {
      const handle = await dir.getFileHandle(name, { create: true });
      const writable = await handle.createWritable();
      await writable.write((await read(name)) ?? '');
      await writable.close();
    }
    for await (const [name, handle] of dir.entries()) {
      if (handle.kind === 'file' && !present.has(name)) await dir.removeEntry(name);
    }
    lastError = null;
    return true;
  } catch (ex) {
    lastError = ex && ex.message ? ex.message : String(ex);
    return false;
  }
}

// The active workspace is the engine's, not ours — asking it is what keeps the
// two from drifting.
let activeCache = 'scratch';
export async function setActive(name) {
  const err = await select(name);
  if (!err) activeCache = name;
  return err;
}
const activeName = async () => activeCache;
export const active = () => activeCache;

async function forget(names) {
  if (!persistent()) return;
  try {
    const opfs = await navigator.storage.getDirectory();
    for (const name of names) {
      try { await opfs.removeEntry(name, { recursive: true }); } catch { /* not there */ }
    }
  } catch { /* no storage; nothing to forget */ }
}

/** Erases every stored workspace. Used when settings of an older shape are
 *  discarded and the layout they were written against no longer applies. */
export const forgetStorage = () => forget(['workspaces', 'workspace']);

/** Erases the flat directory files lived in before workspaces existed. It is
 *  unreadable under this layout no matter what the settings say, so it goes on
 *  every load — the check costs nothing and it only ever finds something once. */
export const forgetLegacyStorage = () => forget(['workspace']);

/** Why the last persist failed, or null. A save that quietly does nothing is
 *  worse than one that says it could not. */
let lastError = null;
export const storageError = () => lastError;

// --- exporting -----------------------------------------------------------

/** Downloads `content` as `name`, through the file picker where there is one. */
export async function saveFile(suggestedName, content) {
  if (window.showSaveFilePicker) {
    const handle = await window.showSaveFilePicker({
      suggestedName,
      types: [{ description: 'Prolog', accept: { 'text/plain': ['.pl'] } }],
    });
    const writable = await handle.createWritable();
    await writable.write(content);
    await writable.close();
    return handle.name;
  }
  download(new Blob([content], { type: 'text/plain' }), suggestedName);
  return suggestedName;
}

/** Downloads the active workspace as a zip. The engine builds it (it owns the
 *  filesystem) and hands it over base64-encoded, since an array cannot cross
 *  inside a Task. */
export async function exportZip() {
  const name = await activeName();
  const bytes = Uint8Array.from(atob(await engine.WorkspaceZip()), (c) => c.charCodeAt(0));
  download(new Blob([bytes], { type: 'application/zip' }), `${name}.zip`);
  return `${name}.zip`;
}

function download(blob, name) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}

// --- the user's own files ------------------------------------------------

/** True when the browser can open real files (Chromium today). */
export const canPickFiles = () => typeof window !== 'undefined' && !!window.showOpenFilePicker;

/**
 * Opens files the user chooses and puts them in the active workspace.
 * @param {(name: string) => Promise<boolean>} confirmReplace asked about a
 *        file this workspace already holds with different text; the default
 *        replaces without asking, for callers that have nobody to ask.
 * @returns {Promise<Array<{name: string, kept: boolean}>>} see acceptFiles
 */
export async function openFiles(confirmReplace = async () => true) {
  return acceptFiles(await pickFiles(), confirmReplace);
}

/**
 * Reads the files the user picks. Falls back to an <input type=file> where the
 * picker does not exist, which every browser has — the difference is only that
 * saving cannot write back to the same file.
 * @returns {Promise<Array<{name: string, text: string}>>} empty if dismissed
 */
async function pickFiles() {
  const picked = [];
  if (canPickFiles()) {
    let handles;
    try {
      handles = await window.showOpenFilePicker({
        multiple: true,
        types: [{ description: 'Prolog', accept: { 'text/plain': ['.pl', '.pro', '.prolog'] } }],
      });
    } catch (ex) {
      if (ex && ex.name === 'AbortError') return picked;   // dismissed
      throw ex;
    }
    for (const handle of handles) {
      const file = await handle.getFile();
      picked.push({ name: file.name, text: await file.text() });
    }
    return picked;
  }

  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.multiple = true;
    input.accept = '.pl,.pro,.prolog,text/plain';
    input.addEventListener('change', async () => {
      for (const file of input.files ?? [])
        picked.push({ name: file.name, text: await file.text() });
      resolve(picked);
    }, { once: true });
    input.click();
  });
}

/**
 * Whether two versions of a file hold the same text. Line endings do not
 * count: the editor hands back \n whatever came in, so a file that arrived
 * with CRLF and was saved once here would otherwise look changed every time it
 * was reopened — and asking about a difference nobody made is worse than not
 * asking at all.
 */
const sameText = (a, b) => a.replace(/\r\n/g, '\n') === b.replace(/\r\n/g, '\n');

/**
 * Puts files that have been read into the active workspace. Reopening a file
 * edited elsewhere is what the picker is FOR, so a new file, and one whose
 * text is unchanged, go straight in. One that is here already and now holds
 * different text is the case with something to lose, and only that one is
 * asked about.
 *
 * @param {Array<{name: string, text: string}>} picked
 * @param {(name: string) => Promise<boolean>} confirmReplace
 * @returns {Promise<Array<{name: string, kept: boolean}>>} what arrived, in
 *          the order picked; `kept` means this workspace's own copy stayed.
 */
export async function acceptFiles(picked, confirmReplace) {
  const settled = [];
  for (const file of picked) {
    const here = await read(file.name);
    const present = here !== null && here !== undefined;
    const same = present && sameText(here, file.text);
    const take = same || !present || await confirmReplace(file.name);
    if (take && !same) await write(file.name, file.text);
    settled.push({ name: file.name, kept: !take });
  }
  return settled;
}

// --- the examples --------------------------------------------------------

/** The examples workspace's files, fetched from the site. */
export const EXAMPLE_FILES = [
  'family.pl', 'boards.pl', 'zebra.pl', 'clpfd.pl', 'clpr.pl',
  'coroutining.pl', 'dcg.pl', 'tabling.pl',
];

export const EXAMPLES_WORKSPACE = 'examples';

/**
 * Fills the examples workspace, leaving the active one where it was. Runs
 * whenever the workspace is ABSENT at boot: deleting it and reloading is how a
 * fresh, current copy is asked for. (It was once-ever, "respecting" deletion —
 * which also meant an updated example could never reach an existing profile,
 * and deleting it left you with no way back.)
 *
 * With onlyMissing, it adds the examples this profile has never seen AND
 * brings the untouched ones up to date (see refreshExample below), so a
 * corrected example reaches an existing profile without ever writing over a
 * file someone edited. Offline it changes nothing: an example that cannot be
 * fetched is one we have no newer version of, which is not an error.
 */
export async function seedExamples(onlyMissing = false) {
  const was = active();
  await setActive(EXAMPLES_WORKSPACE);
  const present = onlyMissing ? new Set(await list()) : new Set();
  const known = { ...(settings.get().exampleHashes ?? {}) };
  for (const name of EXAMPLE_FILES) {
    let source;
    try { source = await (await fetch('examples/' + name)).text(); }
    catch { continue; }   // offline: whatever is already here is what we use
    if (!present.has(name)) {
      await write(name, source);
      known[name] = await fingerprint(source);
      continue;
    }
    const refreshed = await refreshExample(name, source, known[name]);
    if (refreshed !== null) known[name] = refreshed;
  }
  settings.update({ exampleHashes: known });
  await persist();
  await setActive(was);
}

/**
 * Brings one already-present example up to date, when it is safe to.
 *
 * The rule is that a file you edited is YOURS. `baseline` is what this profile
 * was given last time; a local copy that still matches it has not been touched,
 * so a newer shipped version can replace it. A local copy that differs was
 * edited here and is left alone for good, whatever ships later.
 *
 * A profile that predates the bookkeeping has no baseline. It adopts what is
 * here rather than assuming it is pristine — an edited file must not be
 * clobbered by the first update that comes along.
 *
 * @returns the new baseline, or null to leave the recorded one as it is.
 */
async function refreshExample(name, shipped, baseline) {
  const local = await fingerprint((await read(name)) ?? '');
  if (local === null) return null;                 // no digest here: never touch
  if (baseline === undefined) return local;        // first sight: adopt, do not judge
  if (local !== baseline) return null;             // edited here
  const current = await fingerprint(shipped);
  if (current === baseline) return null;           // already up to date
  await write(name, shipped);
  return current;
}

/** Enough of a SHA-256 to tell two texts apart. Null where the browser has no
 *  subtle crypto, which turns the refresh off rather than guessing. Exported
 *  because the selftest has to plant a baseline to exercise the refresh. */
export async function fingerprint(text) {
  if (!globalThis.crypto?.subtle) return null;
  const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(hash).slice(0, 8)]
    .map((byte) => byte.toString(16).padStart(2, '0')).join('');
}
