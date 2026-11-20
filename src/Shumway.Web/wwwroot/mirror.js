// Copying files between two places, one direction at a time (ADR-070).
//
// The engine's files are in the runtime's in-memory file system, which is
// gone with the page. What keeps them is somewhere else: origin-private
// storage for the demo's workspaces, JupyterLite's contents for a notebook.
// A mirror copies from one to the other when it is told to, and nothing
// merges: there is no moment at which two versions of a file are reconciled.
//
// A side is where files are:
//   list()               -> [{ path, modified? }], the files, at any depth
//   read(path)           -> the content, or null when there is no such file
//   write(path, content) -> what `modified` will be for it at the next list
//   remove(path)
//   unchanged()?         -> true when nothing changed since the last list,
//                           for a side that is told and whose list is slow
// A push can be given the list of the side it copies from, taken by the
// caller with something else; list(given) decides whether it serves.
// `modified` is compared for equality only, so a side gives whatever tells
// one version from the next; a side that gives none has every file copied
// every time. The content is whatever the two sides of one mirror agree on.

/**
 * @param memory the engine's side
 * @param store what keeps the files
 * @param {{ oneAtATime?: boolean }} [options] oneAtATime makes a copy wait
 *        for the one before it. A page with several kernels wants it; where
 *        a store can hang, as the demo's can, a copy must not queue behind
 *        one that will never end.
 */
export function createMirror(memory, store, { oneAtATime = false } = {}) {
  // What each side said of a file when it was last copied, either way.
  const seen = new Map();

  async function copy(from, to, fromKey, toKey, given) {
    if (from.unchanged?.()) return { copied: 0, removed: 0 };
    const present = new Set();
    let copied = 0;
    for (const { path, modified } of await from.list(given)) {
      present.add(path);
      const known = seen.get(path);
      if (known && modified !== undefined && known[fromKey] === modified) continue;
      const content = await from.read(path);
      if (content === null || content === undefined) continue;   // gone since the list
      const written = await to.write(path, content);
      seen.set(path, { [fromKey]: modified, [toKey]: written });
      copied++;
    }
    // Only what this mirror copied: a file the other side made since, and
    // has not sent yet, is not one that was deleted here.
    let removed = 0;
    for (const path of [...seen.keys()]) {
      if (present.has(path)) continue;
      await to.remove(path);
      seen.delete(path);
      removed++;
    }
    return { copied, removed };
  }

  let turn = Promise.resolve();
  const run = (work) => {
    if (!oneAtATime) return work();
    const mine = turn.then(work, work);
    turn = mine.catch(() => { });
    return mine;
  };

  return {
    /** Store to memory: what changed in the store, and what was deleted there. */
    pull: () => run(() => copy(store, memory, 'store', 'memory')),
    /** Memory to store: what changed in memory, and what was deleted there. */
    push: (given) => run(() => copy(memory, store, 'memory', 'store', given)),
  };
}

// --- base64, the content a notebook's mirror carries ---------------------

/** @param {Uint8Array} bytes */
export function toBase64(bytes) {
  let binary = '';
  // In pieces: one call with every byte as an argument overflows the stack.
  for (let i = 0; i < bytes.length; i += 0x8000)
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

const textToBase64 = (text) => toBase64(new TextEncoder().encode(text));

/** The text base64 holds, or null when its bytes are not text. */
function textOf(base64) {
  const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
  if (bytes.includes(0)) return null;
  try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
  catch { return null; }
}

// --- the sides of a notebook's mirror ------------------------------------

/**
 * The files under `root` of the in-memory file system, through the app's
 * exports. Content is base64.
 */
export function memoryFiles(api, root) {
  const full = (path) => root + '/' + path;
  // Lists, writes and removals made here. A listing taken elsewhere serves
  // only when none was made since the call that took it began: an older
  // listing can lack a file a pull or another push put in `seen`, and the
  // push would delete it from the store.
  let touched = 0;
  return {
    since: () => touched,
    /** given: { since, files }, files as FilesList lists them. */
    list: async (given) => {
      const fresh = given !== undefined && given.since === touched;
      touched++;
      return fresh ? given.files : JSON.parse(await api.FilesList(root));
    },
    read: (path) => api.FileRead(full(path)),
    write: (path, base64) => { touched++; return api.FileWrite(full(path), base64); },
    remove: (path) => { touched++; return api.FileRemove(full(path)); },
  };
}

/** Notebooks are the reader's documents, not the program's files, and one is
 *  saved again every time a cell runs. */
const notAProgramFile = (path) => /\.ipynb$|(^|\/)\.ipynb_checkpoints(\/|$)/.test(path);

/**
 * JupyterLab's contents manager as a store. Content is base64.
 * @param contents a `Contents.IManager`: get, save, delete
 * @param {{ ignore?: (path: string) => boolean }} [options]
 */
export function contentsStore(contents, { ignore = notAProgramFile } = {}) {
  async function walk(directory, out) {
    const model = await contents.get(directory, { content: true });
    for (const child of model.content ?? []) {
      if (ignore(child.path)) continue;
      if (child.type === 'directory') await walk(child.path, out);
      else out.push({ path: child.path, modified: child.last_modified });
    }
    return out;
  }

  // A file's directory has to be there for the file browser to show it.
  const made = new Set(['']);
  async function ensureDirectory(directory) {
    if (made.has(directory)) return;
    const parent = directory.includes('/') ? directory.slice(0, directory.lastIndexOf('/')) : '';
    await ensureDirectory(parent);
    try {
      await contents.get(directory, { content: false });
    } catch {
      await mine(directory, () => contents.save(directory, {
        type: 'directory', name: directory.slice(parent.length ? parent.length + 1 : 0), path: directory,
      }));
    }
    made.add(directory);
  }

  // Listing the contents is slow in one engine (seconds for 150 files), and
  // a kernel lists before every cell. The manager says when a file is saved,
  // renamed or deleted through it, by the editor or the file browser, so the
  // list is taken only after it has spoken. What this store writes itself
  // is not news. A change made in another tab is not heard.
  let dirty = true;
  const writing = new Set();
  contents.fileChanged?.connect((_, change) => {
    const path = change?.newValue?.path ?? change?.oldValue?.path;
    if (writing.has(path)) return;
    dirty = true;
    made.clear();
    made.add('');
  });
  async function mine(path, work) {
    writing.add(path);
    try { return await work(); }
    finally { writing.delete(path); }
  }

  return {
    unchanged: () => !dirty && !!contents.fileChanged,
    async list() {
      dirty = false;
      try { return await walk('', []); }
      catch (ex) { dirty = true; throw ex; }
    },
    async read(path) {
      let model;
      try { model = await contents.get(path, { content: true }); }
      catch { return null; }
      if (model.format === 'base64') return model.content;
      return textToBase64(model.format === 'json' ? JSON.stringify(model.content) : model.content ?? '');
    },
    async write(path, base64) {
      if (ignore(path)) return undefined;
      const cut = path.lastIndexOf('/');
      if (cut > 0) await ensureDirectory(path.slice(0, cut));
      // As text when it is text: the contents keep the format they are
      // given, and the editor does not open a text file kept as base64.
      const text = textOf(base64);
      const saved = await mine(path, () => contents.save(path, {
        type: 'file', name: path.slice(cut + 1), path,
        format: text === null ? 'base64' : 'text', content: text ?? base64,
      }));
      return saved.last_modified;
    },
    async remove(path) {
      if (ignore(path)) return;
      try { await mine(path, () => contents.delete(path)); } catch { /* gone already */ }
    },
  };
}
