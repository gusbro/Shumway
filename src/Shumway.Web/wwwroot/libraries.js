// Bringing a Prolog library into the page.
//
// A library is a folder of sources on the engine's library search path, so
// `:- use_module(library(clpz)).` finds it the same way it would on a desktop
// with -L. It may carry a DIALECT, which is what lets Scryer's and SWI's
// versions of the same library coexist.
//
// Libraries are GLOBAL. They are not part of any workspace, they survive
// switching between them, and they do not travel in a workspace's zip or share
// link — they are not the user's program, they are what the program builds on.

let engine = null;

export function init(exports) { engine = exports; }

const lines = (s) => (s.length === 0 ? [] : s.split('\n'));

export const names = async () => lines(await engine.LibraryNames());
export const dialect = (name) => engine.LibraryDialect(name);
export const create = (name, dial) => engine.LibraryCreate(name, dial ?? '');
export const remove = (name) => engine.LibraryRemove(name);
export const files = async (name) => lines(await engine.LibraryFiles(name));
export const read = (name, file) => engine.LibraryRead(name, file);
export const write = (name, file, content) => engine.LibraryWrite(name, file, content);

/** Which dialects a library can be tagged with. Plain means none. */
export const DIALECTS = ['', 'scryer', 'swi'];

// --- importing a folder ---------------------------------------------------

/** True when the browser can show a folder picker (Chromium today). */
export const canPickFolder = () =>
  typeof window !== 'undefined' && !!window.showDirectoryPicker;

/**
 * Reads every Prolog source under a folder the user picks.
 *
 * Two ways in, because the good one is not everywhere: showDirectoryPicker
 * gives a real directory handle (Chromium), and an <input webkitdirectory>
 * gives the same files with their relative paths in every current browser.
 *
 * @returns {Promise<{name: string, files: {path: string, text: string}[]}|null>}
 */
export async function pickFolder() {
  if (canPickFolder()) {
    let handle;
    try { handle = await window.showDirectoryPicker({ mode: 'read' }); }
    catch { return null; }                       // the user cancelled
    const collected = [];
    await collect(handle, '', collected);
    return { name: handle.name, files: collected };
  }

  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.webkitdirectory = true;
    input.addEventListener('change', async () => {
      const chosen = [...(input.files ?? [])];
      if (chosen.length === 0) { resolve(null); return; }
      // webkitRelativePath is "<folder>/a/b.pl": the first segment names the
      // folder, the rest is the path inside it.
      const folder = chosen[0].webkitRelativePath.split('/')[0];
      const collected = [];
      for (const file of chosen) {
        const path = file.webkitRelativePath.split('/').slice(1).join('/');
        if (!isSource(path)) continue;
        collected.push({ path, text: await file.text() });
      }
      resolve({ name: folder, files: collected });
    }, { once: true });
    input.click();
  });
}

/** Only Prolog sources: a library folder in a checkout also holds tests, READMEs
 *  and whatever else, and none of it is what use_module resolves. */
const isSource = (path) => /\.(pl|pro|prolog)$/i.test(path);

async function collect(dir, prefix, into) {
  for await (const [name, handle] of dir.entries()) {
    const path = prefix + name;
    if (handle.kind === 'directory') await collect(handle, path + '/', into);
    else if (isSource(path)) into.push({ path, text: await (await handle.getFile()).text() });
  }
}

export const compile = (name, library) => engine.LibraryCompile(name, library);

/** What a collection provides: `[{name, compiled}]`, one per importable
 *  library. Scryer's lib/ is one folder and forty-six of these. */
export async function entries(name) {
  const text = await engine.LibraryEntries(name);
  return lines(text).map((line) => {
    const [entry, state] = line.split('\t');
    return { name: entry, compiled: state === 'compiled' };
  });
}

/**
 * Writes a picked folder in as a collection.
 *
 * Compiling is NOT done here. A collection may hold dozens of libraries and
 * compiling one takes a while, so it happens per library, when someone wants
 * that one to be fast — nobody wants to wait for forty-six to get clpz.
 *
 * @param report called with each phase, so the wait has something to show
 * @returns {Promise<string|null>} the error text, or null
 */
export async function importFolder(name, dial, picked, report = () => {}) {
  report('writing files');
  const err = await create(name, dial);
  if (err) return err;
  for (const file of picked) {
    const failed = await write(name, file.path, file.text);
    if (failed) return failed;
  }
  report('storing');
  await persist();
  return null;
}

// --- persistence ----------------------------------------------------------
// Mirrored to OPFS like the workspaces, in a directory of their own — the two
// are separate things and deleting a workspace must not touch a library.

export const persistent = () =>
  typeof navigator !== 'undefined' && navigator.storage && !!navigator.storage.getDirectory;

const root = async () => (await navigator.storage.getDirectory())
  .getDirectoryHandle('libraries', { create: true });

/** Copies every library into OPFS. */
export async function persist() {
  if (!persistent()) return false;
  try {
    const dir = await root();
    const present = new Set(await names());
    for (const name of present) {
      const libDir = await dir.getDirectoryHandle(name, { create: true });
      const tag = await dialect(name);
      await writeInto(libDir, '.dialect', tag);
      // Every compiled bundle, so a reload does not mean compiling again.
      // Base64 because it is the only shape bytes cross the boundary in.
      for (const entry of await entries(name)) {
        if (!entry.compiled) continue;
        const bundle = await engine.LibraryBundle(name, entry.name);
        if (bundle.length > 0) await writeInto(libDir, `built/${entry.name}`, bundle);
      }
      for (const file of await files(name))
        await writeInto(libDir, 'src/' + file, (await read(name, file)) ?? '');
    }
    for await (const [name, handle] of dir.entries()) {
      if (handle.kind === 'directory' && !present.has(name))
        await dir.removeEntry(name, { recursive: true });
    }
    return true;
  } catch { return false; }
}

/** Brings every stored library back into the engine's filesystem, and onto its
 *  search path. Returns how many libraries arrived. */
export async function restoreAll() {
  if (!persistent()) return 0;
  let count = 0;
  try {
    const dir = await root();
    for await (const [name, handle] of dir.entries()) {
      if (handle.kind !== 'directory') continue;
      const collected = [];
      await readInto(handle, '', collected);
      const tagged = collected.find((f) => f.path === '.dialect');
      await create(name, tagged ? tagged.text.trim() : '');

      // The bundles first: they are what library(X) resolves to, and a program
      // whose first act is to import one must not find only the sources.
      for (const file of collected) {
        if (!file.path.startsWith('built/')) continue;
        await engine.LibraryPutBundle(name, file.path.slice(6), file.text);
      }

      for (const file of collected) {
        if (file.path === '.dialect' || file.path.startsWith('built/')) continue;
        // Stored under src/; the library's own file names are relative to it.
        const relative = file.path.startsWith('src/') ? file.path.slice(4) : file.path;
        await write(name, relative, file.text);
      }
      count++;
    }
  } catch { /* no storage, or it refused: the session just has no libraries */ }
  return count;
}

async function readInto(dir, prefix, into) {
  for await (const [name, handle] of dir.entries()) {
    const path = prefix + name;
    if (handle.kind === 'directory') await readInto(handle, path + '/', into);
    else into.push({ path, text: await (await handle.getFile()).text() });
  }
}

/** Writes `path` (which may name subdirectories) under an OPFS directory. */
async function writeInto(dir, path, text) {
  const parts = path.split('/');
  let here = dir;
  for (const part of parts.slice(0, -1))
    here = await here.getDirectoryHandle(part, { create: true });
  const handle = await here.getFileHandle(parts[parts.length - 1], { create: true });
  const writable = await handle.createWritable();
  await writable.write(text);
  await writable.close();
}
