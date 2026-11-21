// Waking the runtime's threads when a call into it stays out.
//
// A thread of the runtime that has nothing to do waits for a message with
// Atomics.waitAsync on the first word of its pthread struct, and marks
// waiting_async, 128 bytes in (dotnet.native.js: `pthread_ptr + 128`); a
// thread that posts it work notifies that word. In WebKit, Safari included, a
// thread now and then loses that wait: it says it waits, nothing is there to
// notify, and the call it was to answer never comes back. The runtime's own
// fallback, for a thread it cannot notify, is the message `checkMailbox`,
// which makes the thread look at its messages and wait again.

/** The offset of waiting_async in the runtime's pthread struct. */
const WAITING_ASYNC = 128;

/**
 * Wakes the threads of `module` (the runtime's emscripten module) that wait
 * for a message: a notify on the word each waits on, which does nothing to a
 * thread that is not waiting there; and `checkMailbox` to a thread that says
 * it waits with no wait left to notify. Not the message to every thread: each
 * one it reaches waits once more, for good, on top of the wait it had.
 * Answers how many threads were told by message.
 */
export function wakeThreadsOf(module) {
  const threads = module?.PThread?.runningWorkers;
  const memory = module?.wasmMemory;
  if (!threads || !memory) return 0;
  // A view of the memory as it is now: one taken before it grew stops short.
  const heap = new Int32Array(memory.buffer);
  let told = 0;
  for (const thread of threads) {
    const self = thread.pthread_ptr;
    if (!self) continue;
    if (Atomics.notify(heap, self >> 2) === 0 && Atomics.load(heap, (self + WAITING_ASYNC) >> 2) === 1) {
      thread.postMessage({ cmd: 'checkMailbox' });
      told++;
    }
  }
  return told;
}

/**
 * A watch over promises: one still pending after `afterMs` calls `wake`, and
 * again after twice as long each time, so that a long query is woken a few
 * times, not every second. The pending ones are looked at every `everyMs`
 * while there is one. Answers `watch(answer)`, which watches `answer` when it
 * is a promise and answers it.
 */
export function watcher(wake, { afterMs = 2000, everyMs = 500 } = {}) {
  const out = new Set();
  let ticking = 0;
  const tick = () => {
    const now = performance.now();
    let due = false;
    for (const call of out) {
      if (now < call.at) continue;
      due = true;
      call.wait *= 2;
      call.at = now + call.wait;
    }
    if (due) wake();
    if (out.size === 0) { clearInterval(ticking); ticking = 0; }
  };
  return (answer) => {
    if (!(answer instanceof Promise)) return answer;
    const call = { wait: afterMs, at: performance.now() + afterMs };
    out.add(call);
    if (!ticking) ticking = setInterval(tick, everyMs);
    const done = () => { out.delete(call); };
    answer.then(done, done);
    return answer;
  };
}

/** The functions of `exports`, each call's answer given to `watch`. */
export function watchCalls(exports, watch) {
  const watched = {};
  for (const [name, value] of Object.entries(exports)) {
    watched[name] = typeof value === 'function' ? (...args) => watch(value.apply(exports, args)) : value;
  }
  return watched;
}
