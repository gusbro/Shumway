// Making the page's files outlive the page.
//
// Prolog's files live in the engine's in-memory filesystem — that is what makes
// consult/1 and open/4 work in a browser at all. In-memory means gone on
// reload, so this mirrors that directory to OPFS: origin-private storage, no
// permission prompt, survives reloads, and available in every current browser.
//
// The mirror is deliberately one-directional at each moment: OPFS -> memory when
// the page loads, memory -> OPFS after anything changes. Nothing merges, so
// there is no case where two versions of a file have to be reconciled.
//
// Real files on the user's disk are a separate, explicit act (open/save below),
// because reading and writing someone's filesystem should be something they
// asked for rather than something a page does while they are not looking.

let engine = null;

export function init(exports) { engine = exports; }

// --- the in-memory side (the engine's) -----------------------------------

export const list = () => {
  const s = engine.WorkspaceList();
  return s.length === 0 ? [] : s.split('\n');
};
export const read = (name) => engine.WorkspaceRead(name);
export const write = (name, content) => engine.WorkspaceWrite(name, content);
export const remove = (name) => engine.WorkspaceDelete(name);
export const consultFile = (name) => engine.ConsultWorkspaceFile(name);

// --- the persistent side (OPFS) ------------------------------------------

/** True when this browser gives us origin-private storage. */
export const persistent = () =>
  typeof navigator !== 'undefined' && navigator.storage && !!navigator.storage.getDirectory;

const opfsRoot = async () => (await navigator.storage.getDirectory())
  .getDirectoryHandle('workspace', { create: true });

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

/** Copies OPFS into the engine's filesystem. Returns how many files arrived. */
export async function restore() {
  if (!persistent()) return 0;
  return withTimeout(restoreCore(), 3000, 0);
}

async function restoreCore() {
  let count = 0;
  try {
    const dir = await opfsRoot();
    for await (const [name, handle] of dir.entries()) {
      if (handle.kind !== 'file') continue;
      write(name, await (await handle.getFile()).text());
      count++;
    }
  } catch {
    // A browser that refuses storage is not a failure worth stopping for: the
    // session still works, it just will not remember.
    return count;
  }
  return count;
}

/** Copies the engine's filesystem into OPFS, deleting what is no longer there. */
export async function persist() {
  if (!persistent()) return false;
  return withTimeout(persistCore(), 3000, false);
}

async function persistCore() {
  try {
    const dir = await opfsRoot();
    const names = new Set(list());
    for (const name of names) {
      const handle = await dir.getFileHandle(name, { create: true });
      const writable = await handle.createWritable();
      await writable.write(read(name) ?? '');
      await writable.close();
    }
    for await (const [name, handle] of dir.entries()) {
      if (handle.kind === 'file' && !names.has(name)) await dir.removeEntry(name);
    }
    lastError = null;
    return true;
  } catch (ex) {
    lastError = ex && ex.message ? ex.message : String(ex);
    return false;
  }
}

/** Why the last persist failed, or null. A save that quietly does nothing is
 *  worse than one that says it could not. */
let lastError = null;
export const storageError = () => lastError;

// --- the user's own files ------------------------------------------------

/** True when the browser can open and save real files (Chromium today). */
export const canPickFiles = () => typeof window !== 'undefined' && !!window.showOpenFilePicker;

/**
 * Opens files the user chooses and puts them in the workspace.
 * Falls back to an <input type=file> where the picker does not exist, which
 * every browser has — the difference is only that saving cannot write back to
 * the same file.
 * @returns {Promise<string[]>} the names that arrived
 */
export async function openFiles() {
  if (canPickFiles()) {
    const handles = await window.showOpenFilePicker({
      multiple: true,
      types: [{ description: 'Prolog', accept: { 'text/plain': ['.pl', '.pro', '.prolog'] } }],
    });
    const names = [];
    for (const handle of handles) {
      const file = await handle.getFile();
      write(file.name, await file.text());
      names.push(file.name);
    }
    return names;
  }

  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.multiple = true;
    input.accept = '.pl,.pro,.prolog,text/plain';
    input.addEventListener('change', async () => {
      const names = [];
      for (const file of input.files ?? []) {
        write(file.name, await file.text());
        names.push(file.name);
      }
      resolve(names);
    }, { once: true });
    input.click();
  });
}

/** Saves text to a file the user chooses, or downloads it where there is no picker. */
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

  const blob = new Blob([content], { type: 'text/plain' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = suggestedName;
  a.click();
  URL.revokeObjectURL(url);
  return suggestedName;
}
