// #notebook: notebook sessions in this page's runtime, driven as a kernel
// drives them (ADR-068): a request line goes in, a response line comes out.
// What the desktop tests of the server cannot show is here: the server on the
// wasm tier, what a session with the server loaded costs, and the tracer.

import * as session from './session.js';

const call = (id, code) =>
  JSON.stringify({ jsonrpc: '2.0', id, method: 'call', params: { code } });

/** The entry of the first query of a cell's answer. */
const first = (line) => JSON.parse(line).result['1'];

const mb = (bytes) => (bytes / 1048576).toFixed(1);

/**
 * @param {number} count how many sessions to open
 * @param {(text: string) => void} say receives the report, a line at a time
 * @returns {Promise<string>} the closing line of the report
 */
export async function run(count, say) {
  const api = session.exports();
  let failures = 0;
  const check = (ok, what, detail = '') => {
    if (!ok) failures++;
    say((ok ? 'ok   ' : 'FAIL ') + what + (ok || !detail ? '' : ` [${detail}]`) + '\n');
  };

  const limit = await api.SessionLimit(count);
  const heapStart = await api.ManagedHeapBytes();
  const memoryStart = session.wasmMemoryBytes();
  say(`note limit ${limit}; before any session: managed heap ${mb(heapStart)} MB, `
    + `runtime memory ${mb(memoryStart)} MB\n`);

  // A session that asks for a line gets one: the kernel answers an
  // input_request the same way.
  const asked = [];
  session.onSessionAskForInput((kernelId) => {
    asked.push(kernelId);
    api.SessionSupplyInput(kernelId, 'typed(line).');
  });

  const ids = [];
  let heapBefore = heapStart;
  for (let i = 0; i < count; i++) {
    const id = 'kernel-' + (i + 1);
    let t0 = performance.now();
    const refused = await api.SessionCreate(id, 'prolog', '/notebooks/' + id);
    const createMs = performance.now() - t0;
    if (refused) { check(false, `session ${i + 1} starts`, refused); break; }
    ids.push(id);

    t0 = performance.now();
    const firstAnswer = await api.SessionRequest(id, call(1, 'X is 2 + 2.'));
    const firstMs = performance.now() - t0;
    const heapAfter = await api.ManagedHeapBytes();
    check(first(firstAnswer).bindings?.X === '4',
      `session ${i + 1}: starts in ${createMs.toFixed(0)} ms (${await api.SessionStartCost()}), first cell ${firstMs.toFixed(0)} ms, `
      + `+${mb(heapAfter - heapBefore)} MB managed, runtime memory ${mb(session.wasmMemoryBytes())} MB`,
      firstAnswer);
    heapBefore = heapAfter;
  }

  if (ids.length > 0) {
    const id = ids[0];
    const ask = async (n, code) => first(await api.SessionRequest(id, call(n, code)));

    let entry = await ask(2, '%%user\napp([], L, L).\napp([H|T], L, [H|R]) :- app(T, L, R).');
    check(entry.status === 'success', 'a %%user cell loads its clauses', JSON.stringify(entry));

    entry = await ask(3, 'app(X, Y, [1]).');
    const again = await ask(4, 'retry.');
    check(entry.bindings?.X === '[]' && again.bindings?.X === '[1]',
      'retry gives the next solution', JSON.stringify([entry, again]));

    entry = await ask(5, 'jupyter:print_table(member(X, [a, b])).');
    check(JSON.stringify(entry.print_table?.ValuesLists) === '[["a"],["b"]]',
      'a table comes with the answer', JSON.stringify(entry));

    // Hot first, so that what the tracer has to keep off is compiled code.
    await ask(6, 'between(1, 3000, _), app([1, 2], [3], _), fail ; true.');
    entry = await ask(7, 'jupyter:trace(app([1], [2], L)).');
    check(entry.bindings?.L === '[1,2]'
        && entry.output?.includes('Call: (1) app([1], [2], ')
        && entry.output?.includes('Exit: (1) app([], [2], [2])'),
      'jupyter:trace/1 prints the ports of a compiled predicate', JSON.stringify(entry));
    entry = await ask(8, 'app([1], [2], L).');
    check(entry.bindings?.L === '[1,2]' && entry.output === '',
      'the next query is not traced', JSON.stringify(entry));

    await ask(9, 'use_module(library(clpfd)).');
    entry = await ask(10, 'X in 1..5, X #> 2.');
    check(JSON.stringify(entry.residual_goals) === '["X in 3..5"]',
      'residual goals come with the answer', JSON.stringify(entry));

    entry = await ask(11, 'jupyter:update_completion_data.');
    const names = entry.predicate_atoms ?? [];
    check(names.includes('app(A,B,C)') && names.includes('append(A,B,C)'),
      `the names to complete with: ${names.length}`, JSON.stringify(names.slice(0, 8)));

    entry = await ask(12, 'read(X).');
    check(entry.bindings?.X === 'typed(line)' && asked.includes(id),
      'read/1 asks the page for a line', JSON.stringify([entry, asked]));

    entry = await ask(13, 'X is foo + 1.');
    check(entry.status === 'error' && entry.error?.code === -4712,
      'an error is an answer', JSON.stringify(entry));
  }

  // Every session still answers with all of them open, and each has its own
  // program.
  let answering = 0;
  for (const id of ids) {
    const entry = first(await api.SessionRequest(id, call(20, 'catch(app([], [], L), _, L = none).')));
    if (entry.bindings?.L === (id === ids[0] ? '[]' : 'none')) answering++;
  }
  check(answering === ids.length, `${answering} of ${ids.length} sessions answer, each with its own program`);

  // One over the limit is refused, and says why.
  if (ids.length === limit) {
    const refused = await api.SessionCreate('one-too-many', 'prolog', '');
    check(refused.includes('limit'), 'a session over the limit is refused', refused);
  }

  // halt ends the server; a session that is closed frees its place.
  if (ids.length > 0) {
    const last = ids[ids.length - 1];
    const halted = JSON.parse(await api.SessionRequest(last, call(30, 'halt.')));
    const after = await api.SessionRequest(last, call(31, 'true.'));
    check(halted.result?.['1']?.status === 'halt' && after === '',
      'halt stops the server', JSON.stringify([halted, after]));
    await api.SessionDispose(last);
    const reopened = await api.SessionCreate(last, 'prolog', '');
    check(reopened === '', 'a closed session frees its place', reopened);
  }

  const heapEnd = await api.ManagedHeapBytes();
  say(`note with ${ids.length} sessions: managed heap ${mb(heapEnd)} MB `
    + `(${mb((heapEnd - heapStart) / Math.max(1, ids.length))} MB a session), `
    + `runtime memory ${mb(session.wasmMemoryBytes())} MB, tier ${await session.tierName()}\n`);
  return `--- notebook probe: done, ${failures} failed ---\n`;
}
