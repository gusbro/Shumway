// The notebook mirror's push, given the listing a cell's call took: it
// serves when nothing touched the engine's files since, and otherwise the
// mirror asks for a listing of its own, so that an older listing never
// deletes from the store a file a later copy put there.
//
//   node mirror.mjs
import assert from 'node:assert/strict';
import { createMirror, memoryFiles } from '../../src/Shumway.Web/wwwroot/mirror.js';

// The engine's side through a stand-in for the app's exports.
function engine() {
  const files = new Map();
  const calls = [];
  const version = (content) => `${content.length}:${content}`;
  const api = {
    async FilesList() {
      calls.push('FilesList');
      return JSON.stringify([...files].map(([path, content]) => ({ path, modified: version(content) })));
    },
    async FileRead(path) { return files.get(path.slice('/notebooks/'.length)) ?? null; },
    async FileWrite(path, content) { files.set(path.slice('/notebooks/'.length), content); return version(content); },
    async FileRemove(path) { return files.delete(path.slice('/notebooks/'.length)); }
  };
  const listing = () => [...files].map(([path, content]) => ({ path, modified: version(content) }));
  return { files, calls, api, listing };
}

function store() {
  const files = new Map();
  let n = 0;
  return {
    files,
    async list() { return [...files].map(([path, { modified }]) => ({ path, modified })); },
    async read(path) { return files.get(path)?.content ?? null; },
    async write(path, content) { const modified = `s${++n}`; files.set(path, { content, modified }); return modified; },
    async remove(path) { files.delete(path); }
  };
}

let failures = 0;
async function check(name, body) {
  try { await body(); console.log(`ok   ${name}`); }
  catch (e) { failures++; console.log(`FAIL ${name}\n     ${e.message}`); }
}

await check('a listing taken with the cell serves its push, and FilesList is not called', async () => {
  const e = engine();
  const s = store();
  const memory = memoryFiles(e.api, '/notebooks');
  const mirror = createMirror(memory, s, { oneAtATime: true });
  await mirror.pull();
  const since = memory.since();
  e.files.set('out.txt', 'hi');                       // what the cell wrote
  const result = await mirror.push({ since, files: e.listing() });
  assert.deepEqual(result, { copied: 1, removed: 0 });
  assert.equal(s.files.get('out.txt')?.content, 'hi');
  assert.deepEqual(e.calls, []);
});

await check('a pull between the call and its push makes the push list for itself', async () => {
  const e = engine();
  const s = store();
  const memory = memoryFiles(e.api, '/notebooks');
  const mirror = createMirror(memory, s, { oneAtATime: true });
  const since = memory.since();
  const taken = e.listing();                          // the cell's listing: empty
  await s.write('upload.txt', 'from the file browser');
  await mirror.pull();                                // another kernel's cell pulls it in
  assert.equal(e.files.get('upload.txt'), 'from the file browser');
  const result = await mirror.push({ since, files: taken });
  assert.equal(s.files.get('upload.txt')?.content, 'from the file browser', 'the older listing deleted it');
  assert.deepEqual(result, { copied: 0, removed: 0 });
  assert.deepEqual(e.calls, ['FilesList']);
});

await check('another push between the call and its push makes it list for itself', async () => {
  const e = engine();
  const s = store();
  const memory = memoryFiles(e.api, '/notebooks');
  const mirror = createMirror(memory, s, { oneAtATime: true });
  const since = memory.since();
  const taken = e.listing();                          // this cell's listing: empty
  e.files.set('other.txt', 'another kernel wrote it');
  await mirror.push();                                // that kernel's push copies it
  const result = await mirror.push({ since, files: taken });
  assert.equal(s.files.get('other.txt')?.content, 'another kernel wrote it', 'the older listing deleted it');
  assert.deepEqual(result, { copied: 0, removed: 0 });
  assert.deepEqual(e.calls, ['FilesList', 'FilesList']);
});

await check('a file the cell deleted goes from the store, with the listing it took', async () => {
  const e = engine();
  const s = store();
  const memory = memoryFiles(e.api, '/notebooks');
  const mirror = createMirror(memory, s, { oneAtATime: true });
  e.files.set('old.txt', 'x');
  await mirror.push();
  assert.ok(s.files.has('old.txt'));
  const since = memory.since();
  e.files.delete('old.txt');                          // the cell deletes it
  const result = await mirror.push({ since, files: e.listing() });
  assert.deepEqual(result, { copied: 0, removed: 1 });
  assert.ok(!s.files.has('old.txt'));
});

console.log(failures === 0 ? 'mirror: every check passed' : `mirror: ${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
