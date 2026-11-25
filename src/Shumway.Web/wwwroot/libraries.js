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
// The notebook site, a directory of this one, shares them (ADR-070, O3): it
// keeps them in the same storage, through this module.

let engine = null;

export function init(exports) { engine = exports; }

const lines = (s) => (s.length === 0 ? [] : s.split('\n'));

export const names = async () => lines(await engine.LibraryNames());
export const dialect = (name) => engine.LibraryDialect(name);
export const create = (name, dial) => engine.LibraryCreate(name, dial ?? '');
// After the stored bundles are back: one restored later would bring the
// library back.
export const remove = async (name) => {
  await restored();
  inEngine.delete(name);
  return engine.LibraryRemove(name);
};
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
 * Reads every Prolog source under a folder the user picks, and each library
 * compiled ahead beside its source (`x.shum` next to `x.pl`), as an archive
 * brings them.
 *
 * Two ways in, because the good one is not everywhere: showDirectoryPicker
 * gives a real directory handle (Chromium), and an <input webkitdirectory>
 * gives the same files with their relative paths in every current browser.
 *
 * @returns {Promise<{name: string, files: ({path: string, text: string}|{path: string, bytes: Uint8Array})[]}|null>}
 */
export async function pickFolder() {
  if (canPickFolder()) {
    let handle;
    try { handle = await window.showDirectoryPicker({ mode: 'read' }); }
    catch { return null; }                       // the user cancelled
    const found = [];
    await collect(handle, '', found);
    return { name: handle.name, files: await folderFiles(found) };
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
      const found = chosen.map((file) =>
        ({ path: file.webkitRelativePath.split('/').slice(1).join('/'), file }));
      resolve({ name: folder, files: await folderFiles(found) });
    }, { once: true });
    input.click();
  });
}

/** What a folder's files give a collection: the text of each Prolog source,
 *  and the bytes of each library compiled ahead. The rest is left out.
 *  @param {{path: string, file: Blob}[]} found */
export async function folderFiles(found) {
  const files = [];
  for (const { path, file } of found) {
    if (isSource(path)) files.push({ path, text: await file.text() });
    else if (isBundle(path)) {
      const bytes = new Uint8Array(await file.arrayBuffer());
      files.push({ path, bytes, current: await engine.LibraryBundleIsCurrent(bytes.subarray(0, 16)) });
    }
  }
  return files;
}

/** Only Prolog sources: a library folder in a checkout also holds tests, READMEs
 *  and whatever else, and none of it is what use_module resolves. */
export const isSource = (path) => /\.(pl|pro|prolog)$/i.test(path);

/** A library compiled ahead, which a folder or an archive may bring beside its
 *  source. */
export const isBundle = (path) => /\.shum$/i.test(path);

/** Whether a collection's file is a library compiled ahead: one an archive
 *  brought (held by the engine, named by `bundle`) or a folder (its `bytes`).
 *  `current` says whether this engine reads it. */
export const compiledAhead = (file) => file.bundle !== undefined || file.bytes !== undefined;

/** The library a file of a collection is of: its path less the extension. */
const libraryOf = (path) => path.replace(/\.(pl|pro|prolog|shum)$/i, '');

/** The bundles among `files` that this engine cannot read and that come
 *  without their library's source: nothing of those libraries could load. */
export function unreadable(files) {
  const sources = new Set(files.filter((f) => !compiledAhead(f)).map((f) => libraryOf(f.path)));
  return files.filter((f) => compiledAhead(f) && f.current === false && !sources.has(libraryOf(f.path)))
    .map((f) => f.path);
}

/**
 * The files the user picks on this computer for a collection: a `.zip` or
 * `.tar.gz` archive of it, or its files themselves, sources and bundles, read
 * by collectionOfFiles. The picker opens before this returns, in the click
 * that asked for it: a browser that wants a user's gesture for it does not
 * see one after an await. A dismissal leaves the promise pending, as the
 * other pickers here do: a browser may send `cancel` before the files of a
 * pick, and the pick would be lost.
 *
 * @returns {Promise<File[]>}
 */
export function pickCollectionFiles() {
  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.multiple = true;
    input.accept = '.zip,.tar.gz,.tgz,.tar,.shum,.pl,.pro,.prolog';
    input.addEventListener('change', () => resolve([...(input.files ?? [])]), { once: true });
    input.click();
  });
}

/**
 * The collection files picked one by one make: one archive, read as
 * fetchCollection reads one from a URL, or the collection's own files, read
 * as a folder's are: sources, and bundles with or without their source.
 * Named after the first source, or the first bundle when there is none.
 * Throws with what is wrong with the pick.
 *
 * @param {File[]} chosen
 * @returns {Promise<{name: string, files: object[]}>}
 */
export async function collectionOfFiles(chosen) {
  if (chosen.some((file) => ARCHIVE.test(file.name))) {
    if (chosen.length > 1) throw new Error('pick one archive, or the files of a collection, not both');
    return { name: stem(chosen[0].name), files: await unpack(new Uint8Array(await chosen[0].arrayBuffer())) };
  }
  const files = await folderFiles(chosen.map((file) => ({ path: file.name, file })));
  if (files.length === 0)
    throw new Error('pick Prolog sources (.pl), libraries compiled ahead (.shum), or an archive');
  const first = files.find((file) => !compiledAhead(file)) ?? files[0];
  return { name: stem(first.path), files };
}

// --- importing from a URL -------------------------------------------------
// The point is not having to clone a whole Prolog system to get one library.
//
// Two hosts are involved: the files come from raw.githubusercontent.com and
// the listing from api.github.com. Both answer a CORS fetch, which a
// cross-origin-isolated page may read under `require-corp`.

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

const ARCHIVE = /\.(zip|tar\.gz|tgz|tar)$/i;

/** A collection's name from the file it came in: `lib-1.0.tar.gz` is `lib-1.0`. */
const stem = (file) => file.replace(/\.(tar\.gz|tgz|zip|tar|pl|pro|prolog|shum)$/i, '') || file;

/**
 * The name a URL's collection takes, known before anything is fetched: the
 * suggested one's, the last directory of a GitHub address (or the repository),
 * or the file's name without its extension.
 */
export function collectionName(url) {
  const known = SUGGESTED.find((s) => s.url === url);
  if (known) return known.name;
  const where = parseGitHubTree(url);
  if (where) return where.path.split('/').pop() || where.repo;
  return stem(decodeURIComponent(new URL(url).pathname.split('/').pop() ?? ''));
}

/**
 * Fetches what a URL holds, as a collection's files: a GitHub directory, one
 * Prolog file, or a .zip or .tar.gz archive of them. Any site but GitHub has
 * no way to list a directory, so from those it is a file.
 *
 * @returns {Promise<{files: {path: string, text: string}[], from: string}>}
 */
export async function fetchCollection(url, report = () => {}) {
  const where = parseGitHubTree(url);
  if (where) return { files: await fetchGitHubTree(where, report), from: `${where.owner}/${where.repo}` };

  const address = new URL(url);
  const file = decodeURIComponent(address.pathname.split('/').pop() ?? '');
  if (!ARCHIVE.test(file) && !isSource(file)) {
    throw new Error('that is neither a GitHub directory (https://github.com/owner/repo/tree/branch/path),'
      + ' a Prolog file, nor a .zip or .tar.gz archive');
  }
  let response;
  try { response = await fetch(url); }
  catch {
    // A refused CORS read and a host that is not there fail alike.
    throw new Error(`${address.host} did not answer, or does not let a page of another site`
      + ' read its files (CORS)');
  }
  if (!response.ok) throw new Error(`${address.host} answered ${response.status} for ${file}`);
  const files = isSource(file)
    ? [{ path: file, text: await response.text() }]
    : await unpack(new Uint8Array(await response.arrayBuffer()));
  return { files, from: address.host };
}

/** What an archive's bytes hold of a collection: `{path, text}` for each
 *  Prolog source, and `{path, bundle}` for each library the archive carries
 *  compiled (`x.shum` beside `x.pl`), which importFolder puts in place. */
export async function unpack(bytes) {
  const answer = JSON.parse(await engine.LibraryUnpack(bytes));
  if (!Array.isArray(answer)) throw new Error(answer.error);
  return answer;
}

async function collect(dir, prefix, into) {
  for await (const [name, handle] of dir.entries()) {
    const path = prefix + name;
    if (handle.kind === 'directory') await collect(handle, path + '/', into);
    else if (isSource(path) || isBundle(path)) into.push({ path, file: await handle.getFile() });
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
 * `state` is 'compiled', 'bundle' (compiled, and it came without its
 * source), 'source', 'failed' or 'native' (the engine has its
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
    return { name: entry, state, note: note ?? null,
             compiled: state === 'compiled' || state === 'bundle', source: state !== 'bundle' };
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
export async function importFolder(name, dial, picked, report = () => {}, source = '') {
  report('writing files');
  const err = await create(name, dial);
  if (err) return err;
  if (source) {
    const failed = await engine.LibrarySetSource(name, source);
    if (failed) return failed;
  }
  for (const file of picked) {
    if (compiledAhead(file)) continue;
    const failed = await write(name, file.path, file.text);
    if (failed) return failed;
  }
  // The libraries the archive or the folder carried compiled. One the engine
  // does not take, written with another format of compiled code, is left out:
  // its library compiles from its source the first time it is used.
  const sources = new Set(picked.filter((f) => !compiledAhead(f)).map((f) => libraryOf(f.path)));
  for (const file of picked) {
    const library = libraryOf(file.path);
    let failed = null;
    if (file.bundle !== undefined) failed = await engine.LibraryTakeBundle(name, file.path, file.bundle);
    else if (file.bytes !== undefined) failed = await engine.LibraryPutBundle(name, library, file.bytes);
    else continue;
    // A library that came as its bundle alone gets the source the bundle
    // carries of it, when it carries one, where it can be read.
    if (!failed && !sources.has(library)) await engine.LibraryWriteBundleSource(name, library);
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

// The page sets it from the workspace's probe of storage: the interface being
// there does not mean it answers.
let usable = typeof navigator !== 'undefined' && !!navigator.storage?.getDirectory;
export const persistent = () => usable;
export const setPersistent = (answers) => { usable = usable && answers; };

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
    const source = await engine.LibrarySource(name);
    if (source) await writeInto(libDir, '.source', source);
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
      await restoreCollection(name, handle);
      restoredNames.push(name);
    }
  } catch { /* no storage, or it refused: the session just has no libraries */ }
  return restoredNames;
}

async function restoreCollection(name, handle) {
  // Bundles an earlier version stored as base64. Not converted: a library
  // whose bundle is gone compiles again when it is used.
  try { await handle.removeEntry('built', { recursive: true }); } catch { /* none */ }
  const collected = [];
  await readInto(handle, '', collected, 'bundles/');
  const tagged = collected.find((f) => f.path === '.dialect');
  await create(name, tagged ? tagged.text.trim() : '');
  const source = collected.find((f) => f.path === '.source');
  if (source) await engine.LibrarySetSource(name, source.text);

  for (const file of collected) {
    if (!file.path.startsWith('diag/')) continue;
    await engine.LibraryPutDiagnostic(name, file.path.slice(5), file.text);
  }

  for (const file of collected) {
    if (file.path === '.dialect' || file.path === '.source' || file.path.startsWith('diag/')) continue;
    // Stored under src/; the library's own file names are relative to it.
    const relative = file.path.startsWith('src/') ? file.path.slice(4) : file.path;
    await write(name, relative, file.text);
  }
}

/**
 * Brings back the stored collections this page does not have: those another
 * tab imported since this one started. Resolves, with their names, once their
 * bundles are back too.
 */
export async function syncFromStorage() {
  if (!persistent()) return [];
  await restored();
  const stored = [];
  try {
    for await (const [name, handle] of (await root()).entries())
      if (handle.kind === 'directory') stored.push([name, handle]);
  } catch { return []; /* no storage: nothing to bring */ }
  // The engine is asked for its names only when the storage holds one this
  // page has not seen there: a notebook syncs before every cell, and every
  // call into the runtime is one more for WebKit's threads to stop taking.
  if (stored.every(([name]) => inEngine.has(name))) return [];
  const have = new Set(await names());
  for (const name of have) inEngine.add(name);
  const added = [];
  try {
    for (const [name, handle] of stored) {
      if (have.has(name)) continue;
      await restoreCollection(name, handle);
      inEngine.add(name);
      added.push(name);
    }
  } catch { /* no storage: nothing to bring */ }
  if (added.length > 0) await restoreBundles(added);
  return added;
}

// The collections of the storage that the engine has: what syncFromStorage
// does not ask the engine about again.
const inEngine = new Set();

/** Stores the bundles the engine compiled on demand since the last call, so
 *  another page, or the next visit, does not compile them again. */
export async function keepCompiledOnDemand(taken = null) {
  const compiled = taken === null ? await takeCompiledOnDemand()
    : lines(taken).map((line) => {
      const [name, library] = line.split('\t');
      return { name, library };
    });
  for (const { name, library } of compiled) await persistLibrary(name, library);
}

/**
 * Imports a collection for a notebook cell (jupyter:import_library/1). The
 * location is a URL, a file the engine sees, or empty for the library the
 * page suggests for the dialect. A collection here already under the same
 * name is left as it is when it came from the same place, or when where it
 * came from was not kept; from anywhere else it is refused.
 *
 * @returns {Promise<string>} what to tell the notebook
 */
export async function importFor({ dialect: dial, location }) {
  const suggested = location === ''
    ? SUGGESTED.find((s) => s.dialect === dial)
    : SUGGESTED.find((s) => s.url === location);
  const source = location === '' ? suggested.url : location;
  const remote = /^https?:\/\//i.test(source);
  const tag = dial || suggested?.dialect || '';
  const name = remote ? collectionName(source) : stem(source.split('/').pop());

  await syncFromStorage();
  if ((await names()).includes(name)) {
    const was = await engine.LibrarySource(name);
    if (was && was !== source) throw new Error(`a collection named ${name} is here already, from ${was}`);
    return `% ${name} is here already${was ? `, from ${was}` : ''}.`;
  }

  let files, from;
  if (remote) ({ files, from } = await fetchCollection(source));
  else {
    const file = source.split('/').pop();
    const bytes = fromBase64(await engine.LibraryReadLocal(source));
    files = isSource(file) ? [{ path: file, text: new TextDecoder().decode(bytes) }] : await unpack(bytes);
    from = file;
  }
  const failed = await importFolder(name, tag, files, () => {}, source);
  if (failed) throw new Error(failed);
  const all = await entries(name);
  const count = all.length;
  const compiled = all.filter((entry) => entry.compiled).length;
  const rest = compiled === 0 ? 'Each compiles the first time it is used.'
    : compiled === count ? (count === 1 ? 'It came compiled.' : 'They came compiled.')
      : `${compiled} came compiled; each of the others compiles the first time it is used.`;
  return `% ${name}: ${count} librar${count === 1 ? 'y' : 'ies'} from ${from}`
    + `${tag ? `, dialect ${tag}` : ''}. ${rest}`;
}

/** Puts the stored bundles of `names` back, which is what library(X) resolves
 *  to, without holding the caller up: restored() says when they are back.
 *  `say(collection, library)` is told of each one as its turn comes. */
export function restoreBundles(names, say = () => { }, lost = () => { }) {
  bundlesPending = (async () => {
    for (const name of names) {
      let stored;
      try {
        stored = await (await (await root()).getDirectoryHandle(name)).getDirectoryHandle('bundles');
      } catch { continue; }                  // none compiled
      const broken = [];
      for await (const [library, handle] of stored.entries()) {
        if (handle.kind !== 'file') continue;
        say(name, library);
        try {
          const bytes = new Uint8Array(await (await handle.getFile()).arrayBuffer());
          // A page closed while it stored a bundle leaves the file empty. Put
          // back, it is what library(X) finds, and the library never loads
          // nor compiles again.
          if (await engine.LibraryPutBundle(name, library, bytes)) broken.push(library);
        } catch { /* that library compiles again when it is used */ }
      }
      for (const library of broken) {
        await stored.removeEntry(library).catch(() => {});
        // One with its source compiles again when it is used; one that came
        // alone is gone, and has to be imported again.
        if ((await read(name, library + '.pl')) === null) lost(name, library);
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
