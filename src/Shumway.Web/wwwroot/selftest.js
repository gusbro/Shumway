// Drives the whole browser path without a human: consult, pull solutions one at
// a time, failure, a syntax error, engine output, completion. Loading the page
// with #selftest and reading the transcript is an end-to-end check of the
// DEPLOYED app — the part no xUnit test can reach, since it needs a browser.
//
// Loaded on demand, so a normal page never fetches it.

export async function run(session, emit, out) {
  let failures = 0;

  const check = (name, got, want) => {
    const ok = got === want;
    if (!ok) failures++;
    emit(`${ok ? 'ok  ' : 'FAIL'} ${name}: ${JSON.stringify(got)}`
       + (ok ? '\n' : ` (wanted ${JSON.stringify(want)})\n`), ok ? 'note' : 'error');
  };

  /** Every solution of a query, joined — or "error: …". */
  const solutions = async (q) => {
    const acc = [];
    const err = await session.start(q);
    if (err) return 'error: ' + err;
    for (;;) {
      const { tag, text } = await session.next(80);
      if (tag === session.FAILED) return acc.join(' | ');
      if (tag === session.ERROR) return 'error: ' + text;
      acc.push(text);
      if (tag === session.LAST) return acc.join(' | ');
    }
  };

  emit('--- selftest ---\n', 'note');

  check('consult', await session.consult(
    'anc(X,Y) :- par(X,Y).  anc(X,Z) :- par(X,Y), anc(Y,Z).  par(a,b).  par(b,c).'), null);
  check('arithmetic', await solutions('X is 6*7.'), 'X = 42');
  check('backtracking', await solutions('member(X,[a,b,c]).'), 'X = a | X = b | X = c');
  check('consulted rules', await solutions('anc(a,X).'), 'X = b | X = c');
  check('failure', await solutions('fail.'), '');
  check('no variables', await solutions('atom(foo).'), 'true');
  check('value chaining', await solutions('X = Y, Y = shared.'), 'X = Y,\nY = shared');

  const syntax = await session.start('this is not( prolog.');
  check('syntax error is reported', typeof syntax === 'string' && syntax.includes('1:20'), true);

  // An undefined predicate must come back as a real ISO error, with the engine's
  // stack — this is the diagnostic the console REPL prints, shared verbatim.
  const undef = await solutions('no_such_predicate_xyz(1).');
  check('existence error', undef.includes('existence_error'), true);

  check('completion', (await session.complete('appen')).includes('append'), true);

  const before = out.textContent.length;
  await solutions('write(engine_output), nl.');
  check('engine output reaches the page',
        out.textContent.slice(before).includes('engine_output'), true);

  // Cancelling a search must stop it rather than run to completion.
  await session.start('between(1, 100000000, X), X > 99999999.');
  await session.cancel();
  check('cancel leaves no run', (await session.next(80)).tag, session.FAILED);

  emit(`--- selftest ${failures === 0 ? 'passed' : failures + ' FAILED'} ---\n`,
       failures === 0 ? 'note' : 'error');
}
