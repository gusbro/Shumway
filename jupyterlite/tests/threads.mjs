// Waking the runtime's threads (src/Shumway.Web/wwwroot/threads.js), against
// a runtime made of shared memory and stand-ins for its thread workers: a
// thread that still has its wait is notified and told nothing, one that says
// it waits with no wait left is told to look at its messages, and one that
// does not wait is left alone; a call still out wakes them, each time after
// twice as long, and a call that came back does not.
//
//   node threads.mjs
import assert from 'node:assert/strict';
import { wakeThreadsOf, watchCalls, watcher } from '../../src/Shumway.Web/wwwroot/threads.js';

// Each thread's pthread struct: its first word holds its own address, which
// is what it waits on, and waiting_async is 128 bytes in.
function runtime(threads) {
  const memory = { buffer: new SharedArrayBuffer(4096) };
  const heap = new Int32Array(memory.buffer);
  const workers = threads.map((thread, i) => {
    const self = 256 + i * 512;
    heap[self >> 2] = self;
    heap[(self + 128) >> 2] = thread.saysItWaits ? 1 : 0;
    const worker = { pthread_ptr: self, messages: [], postMessage(message) { this.messages.push(message); } };
    if (thread.hasItsWait) worker.wait = Atomics.waitAsync(heap, self >> 2, self, 1000).value;
    return worker;
  });
  return { module: { PThread: { runningWorkers: workers }, wasmMemory: memory }, workers };
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

let failures = 0;
async function check(name, body) {
  try { await body(); console.log(`ok   ${name}`); }
  catch (e) { failures++; console.log(`FAIL ${name}\n     ${e.message}`); }
}

await check('a thread that still has its wait is notified, and told nothing', async () => {
  const { module, workers: [thread] } = runtime([{ saysItWaits: true, hasItsWait: true }]);
  assert.equal(wakeThreadsOf(module), 0);
  assert.deepEqual(thread.messages, []);
  assert.equal(await thread.wait, 'ok', 'its wait was not notified');
});

await check('a thread that says it waits with no wait left is told to look at its messages', async () => {
  const { module, workers: [thread] } = runtime([{ saysItWaits: true, hasItsWait: false }]);
  assert.equal(wakeThreadsOf(module), 1);
  assert.deepEqual(thread.messages, [{ cmd: 'checkMailbox' }]);
});

await check('a thread that does not wait is told nothing', async () => {
  const { module, workers: [thread] } = runtime([{ saysItWaits: false, hasItsWait: false }]);
  assert.equal(wakeThreadsOf(module), 0);
  assert.deepEqual(thread.messages, []);
});

await check('of several threads, only the one that lost its wait is told', async () => {
  const { module, workers } = runtime([
    { saysItWaits: true, hasItsWait: true },
    { saysItWaits: true, hasItsWait: false },
    { saysItWaits: false, hasItsWait: false }
  ]);
  assert.equal(wakeThreadsOf(module), 1);
  assert.deepEqual(workers.map((w) => w.messages.length), [0, 1, 0]);
  assert.equal(await workers[0].wait, 'ok');
});

await check('a runtime without threads is left alone', async () => {
  assert.equal(wakeThreadsOf({}), 0);
  assert.equal(wakeThreadsOf(null), 0);
});

await check('a call still out wakes the threads, after twice as long each time', async () => {
  let wakes = 0;
  const exports = {
    quick: () => Promise.resolve('quick'),
    slow: () => new Promise((resolve) => setTimeout(() => resolve('slow'), 260)),
    plain: () => 'plain',
    count: 7
  };
  const watched = watchCalls(exports, watcher(() => { wakes++; }, { afterMs: 50, everyMs: 10 }));
  assert.equal(watched.plain(), 'plain');
  assert.equal(watched.count, 7);
  assert.equal(await watched.quick(), 'quick');
  await sleep(120);
  assert.equal(wakes, 0, 'a call that came back woke the threads');
  // Out from 0 to 260 ms: woken at about 50 ms, then 150 ms; the next would
  // be at about 350 ms, after it came back.
  assert.equal(await watched.slow(), 'slow');
  assert.equal(wakes, 2);
  await sleep(200);
  assert.equal(wakes, 2, 'the threads were woken after the call came back');
});

await check('a step of the boot still pending wakes the threads, and one that ended does not', async () => {
  let wakes = 0;
  const watch = watcher(() => { wakes++; }, { afterMs: 50, everyMs: 10 });
  const booted = await watch(new Promise((resolve) => setTimeout(() => resolve('booted'), 120)));
  assert.equal(booted, 'booted');
  assert.equal(wakes, 1);
  // One that fails is not watched after it failed.
  await assert.rejects(watch(Promise.reject(new Error('no runtime'))), /no runtime/);
  await sleep(150);
  assert.equal(wakes, 1);
});

console.log(failures === 0 ? 'threads: every check passed' : `threads: ${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
