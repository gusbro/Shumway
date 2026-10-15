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
// After the stored bundles are back: one restored later would bring the
// library back.
export const remove = async (name) => { await restored(); return engine.LibraryRemove(name); };
export const files = async (name) => lines(await engine.LibraryFiles(name));
export const read = (name, file) => engine.LibraryRead(name, file);
export const write = (name, file, content) => engine.LibraryWrite(name, file, content);

/** Which dialects a library can be tagged with. Plain means none. */
export const DIALECTS = ['', 'scryer', 'swi', 'trealla'];

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

// --- importing from a URL -------------------------------------------------
// The point is not having to clone a whole Prolog system to get one library.
//
// Two hosts are involved and they behave differently, which is the whole story
// of why this works: the FILES come from raw.githubusercontent.com, which sends
// `Cross-Origin-Resource-Policy: cross-origin` and so is readable even from a
// cross-origin-isolated page; the LISTING comes from api.github.com, which
// sends CORS but no CORP — readable only because this app asks for
// `credentialless` isolation rather than `require-corp`.

/** Somewhere to start from, for the systems whose libraries are worth having. */
export const SUGGESTED = [
  {
    label: 'Scryer Prolog — library (about 60 files)',
    url: 'https://github.com/mthom/scryer-prolog/tree/master/src/lib',
    dialect: 'scryer',
    name: 'scryer',
  },
  {
    label: 'SWI-Prolog — library (about 200 files)',
    url: 'https://github.com/SWI-Prolog/swipl-devel/tree/master/library',
    dialect: 'swi',
    name: 'swi',
  },
  {
    label: 'Trealla Prolog — library (about 40 files)',
    url: 'https://github.com/trealla-prolog/trealla/tree/main/library',
    dialect: 'trealla',
    name: 'trealla',
  },
];

/** Reads `https://github.com/owner/repo/tree/ref/path` — what you get by
 *  navigating to a directory on GitHub and copying the address. */
export function parseGitHubTree(url) {
  const m = /^https?:\/\/github\.com\/([^/]+)\/([^/]+)\/tree\/([^/]+)\/?(.*)$/.exec(url.trim());
  if (!m) return null;
  const [, owner, repo, ref, path] = m;
  return { owner, repo, ref, path: path.replace(/\/$/, '') };
}

/**
 * Fetches every Prolog source under a GitHub directory.
 *
 * ONE listing request, recursive: a directory at a time would be one request
 * per directory against an hourly budget of sixty.
 *
 * @param report called with (done, total) as the files arrive
 */
export async function fetchGitHubTree(where, report = () => {}) {
  const listing = await fetch(
    `https://api.github.com/repos/${where.owner}/${where.repo}`
    + `/git/trees/${encodeURIComponent(where.ref)}?recursive=1`);
  if (!listing.ok) {
    throw new Error(listing.status === 403
      ? 'GitHub is rate-limiting this address (sixty listings an hour, unauthenticated)'
      : `GitHub answered ${listing.status} for that repository`);
  }
  const tree = (await listing.json()).tree ?? [];

  const prefix = where.path ? where.path + '/' : '';
  const wanted = tree.filter((e) =>
    e.type === 'blob' && e.path.startsWith(prefix) && isSource(e.path));
  if (wanted.length === 0) throw new Error('no Prolog sources under that path');

  // A few at a time: sixty files fetched one after another is sixty round
  // trips end to end, and most of each one is waiting. Six keeps it brisk
  // without hammering a host that is doing us a favour.
  const files = [];
  const queue = [...wanted];
  const worker = async () => {
    while (queue.length > 0) {
      const entry = queue.shift();
      const raw = `https://raw.githubusercontent.com/${where.owner}/${where.repo}`
        + `/${where.ref}/${entry.path}`;
      try {
        const response = await fetch(raw);
        // One missing file is not a failed import.
        if (response.ok)
          files.push({ path: entry.path.slice(prefix.length), text: await response.text() });
      } catch { /* likewise for one that would not come */ }
      report(files.length, wanted.length);
    }
  };
  await Promise.all(Array.from({ length: Math.min(6, queue.length) }, worker));
  return files;
}

async function collect(dir, prefix, into) {
  for await (const [name, handle] of dir.entries()) {
    const path = prefix + name;
    if (handle.kind === 'directory') await collect(handle, path + '/', into);
    else if (isSource(path)) into.push({ path, text: await (await handle.getFile()).text() });
  }
}

// Without force, a library that has a bundle already is left as it is: the
// batch queued it before a consult compiled it on demand. After the stored
// bundles are back, so an older one does not land over what this builds.
export const compile = async (name, library, force = false) => {
  await restored();
  return engine.LibraryCompile(name, library, force);
};

/** The libraries a consult, a query or the compile of another library
 *  compiled because it needed them, since the last ask (the engine compiles
 *  an uncompiled one on first use): `[{name, library}]`. */
export async function takeCompiledOnDemand() {
  return lines(await engine.LibraryTakeCompiledOnDemand()).map((line) => {
    const [name, library] = line.split('\t');
    return { name, library };
  });
}

/** Everything the last compile of one library had to say. */
export const diagnostic = (name, library) => engine.LibraryDiagnostic(name, library);

/**
 * What a collection provides: `[{name, state, note}]`, one per importable
 * library. Scryer's lib/ is one folder and forty-six of these.
 *
 * `state` is 'compiled', 'source', 'failed' or 'native' (the engine has its
 * own version, so there is nothing to build), and `note` is the headline of
 * the last compile's diagnostic when there was one — a library that failed, or
 * one that compiled while every one of its directives raised, which comes to
 * the same thing for whoever tries to call it.
 */
export async function entries(name) {
  await restored();                  // which ones are compiled is in the bundles
  const text = await engine.LibraryEntries(name);
  return lines(text).map((line) => {
    const [entry, state, note] = line.split('\t');
    return { name: entry, state, note: note ?? null, compiled: state === 'compiled' };
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
  await persist(name);
  return null;
}

// --- persistence ----------------------------------------------------------
// Mirrored to OPFS like the workspaces, in a directory of their own — the two
// are separate things and deleting a workspace must not touch a library.
//
// Each step stores what it changed and nothing else. A collection's bundles run
// to megabytes, and copying all of them after every compile made the batch
// quadratic and leaving a library source the user had only looked at take
// seconds of a busy page.

export const persistent = () =>
  typeof navigator !== 'undefined' && navigator.storage && !!navigator.storage.getDirectory;

const root = async () => (await navigator.storage.getDirectory())
  .getDirectoryHandle('libraries', { create: true });

/** A collection's stored directory, made with its dialect the first time. */
async function storedCollection(name) {
  const dir = await root();
  try { return await dir.getDirectoryHandle(name); } catch { /* not stored yet */ }
  const made = await dir.getDirectoryHandle(name, { create: true });
  await writeInto(made, '.dialect', await dialect(name));
  return made;
}

/** Stores one library's compiled bundle and diagnostic as they are now, a
 *  stored one that is gone included: a library recompiled clean must stop
 *  reading as broken after a reload. The bundle is stored as bytes: every page
 *  load reads every stored bundle back. */
async function storeBuilt(libDir, name, library) {
  const said = await diagnostic(name, library);
  if (said.length > 0) await writeInto(libDir, `diag/${library}`, said);
  else await removeFrom(libDir, `diag/${library}`);
  const bundle = await engine.LibraryBundle(name, library);
  if (bundle.length > 0) await writeInto(libDir, `bundles/${library}`, fromBase64(bundle));
  else await removeFrom(libDir, `bundles/${library}`);
}

function fromBase64(text) {
  if (Uint8Array.fromBase64) return Uint8Array.fromBase64(text);
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/** Copies one whole collection into OPFS: what an import needs, once. */
export async function persist(name) {
  if (!persistent()) return false;
  try {
    const libDir = await storedCollection(name);
    await writeInto(libDir, '.dialect', await dialect(name));
    for (const entry of await entries(name)) await storeBuilt(libDir, name, entry.name);
    for (const file of await files(name))
      await writeInto(libDir, 'src/' + file, (await read(name, file)) ?? '');
    return true;
  } catch { return false; }
}

/** Stores what compiling one library changed: its bundle and its diagnostic. */
export async function persistLibrary(name, library) {
  if (!persistent()) return false;
  try {
    await storeBuilt(await storedCollection(name), name, library);
    return true;
  } catch { return false; }
}

/** Stores one source file of a collection, after an edit. */
export async function persistSource(name, file) {
  if (!persistent()) return false;
  try {
    await writeInto(await storedCollection(name), 'src/' + file, (await read(name, file)) ?? '');
    return true;
  } catch { return false; }
}

/** Drops a removed collection from storage. */
export async function forget(name) {
  if (!persistent()) return false;
  try {
    await (await root()).removeEntry(name, { recursive: true });
    return true;
  } catch { return false; }
}

// A page load restores the stored libraries in two steps. Their sources,
// dialects and diagnostics come back first, and the page waits for them: they
// are small, and a library file left open needs its source. Their bundles,
// tens of megabytes, come back while the page is already in use; a consult, a
// query or a compile waits for them through restored(), or the engine would
// find only the sources and compile them again.

let bundlesPending = null;

/** Whether the stored bundles are still being restored. */
export const restoring = () => bundlesPending !== null;

/** Resolves once the stored bundles are back. */
export const restored = () => bundlesPending ?? Promise.resolve();

/** Brings every stored library's sources, dialect and diagnostics back into
 *  the engine's filesystem, and onto its search path. Returns the names of
 *  the collections restored, for restoreBundles. */
export async function restoreSources() {
  if (!persistent()) return [];
  const restoredNames = [];
  try {
    const dir = await root();
    for await (const [name, handle] of dir.entries()) {
      if (handle.kind !== 'directory') continue;
      // Bundles an earlier version stored as base64. Not converted: a library
      // whose bundle is gone compiles again when it is used.
      try { await handle.removeEntry('built', { recursive: true }); } catch { /* none */ }
      const collected = [];
      await readInto(handle, '', collected, 'bundles/');
      const tagged = collected.find((f) => f.path === '.dialect');
      await create(name, tagged ? tagged.text.trim() : '');

      for (const file of collected) {
        if (!file.path.startsWith('diag/')) continue;
        await engine.LibraryPutDiagnostic(name, file.path.slice(5), file.text);
      }

      for (const file of collected) {
        if (file.path === '.dialect' || file.path.startsWith('diag/')) continue;
        // Stored under src/; the library's own file names are relative to it.
        const relative = file.path.startsWith('src/') ? file.path.slice(4) : file.path;
        await write(name, relative, file.text);
      }
      restoredNames.push(name);
    }
  } catch { /* no storage, or it refused: the session just has no libraries */ }
  return restoredNames;
}

/** Puts the stored bundles of `names` back, which is what library(X) resolves
 *  to, without holding the caller up: restored() says when they are back. */
export function restoreBundles(names) {
  bundlesPending = (async () => {
    for (const name of names) {
      let stored;
      try {
        stored = await (await (await root()).getDirectoryHandle(name)).getDirectoryHandle('bundles');
      } catch { continue; }                  // none compiled
      for await (const [library, handle] of stored.entries()) {
        if (handle.kind !== 'file') continue;
        try {
          const bytes = new Uint8Array(await (await handle.getFile()).arrayBuffer());
          await engine.LibraryPutBundle(name, library, bytes);
        } catch { /* that library compiles again when it is used */ }
      }
    }
  })().finally(() => { bundlesPending = null; });
  return bundlesPending;
}

/** Reads every file under `dir` as text, but those under `skip`. */
async function readInto(dir, prefix, into, skip) {
  for await (const [name, handle] of dir.entries()) {
    const path = prefix + name;
    if (handle.kind === 'directory') {
      if (path + '/' !== skip) await readInto(handle, path + '/', into, skip);
    } else into.push({ path, text: await (await handle.getFile()).text() });
  }
}

/** Removes `path` under an OPFS directory; nothing stored there is not an error. */
async function removeFrom(dir, path) {
  const parts = path.split('/');
  try {
    let here = dir;
    for (const part of parts.slice(0, -1)) here = await here.getDirectoryHandle(part);
    await here.removeEntry(parts[parts.length - 1]);
  } catch { /* none stored */ }
}

/** Writes `path` (which may name subdirectories) under an OPFS directory:
 *  text, or bytes. */
async function writeInto(dir, path, content) {
  const parts = path.split('/');
  let here = dir;
  for (const part of parts.slice(0, -1))
    here = await here.getDirectoryHandle(part, { create: true });
  const handle = await here.getFileHandle(parts[parts.length - 1], { create: true });
  const writable = await handle.createWritable();
  await writable.write(content);
  await writable.close();
}
