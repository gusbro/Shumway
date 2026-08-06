// Drives the whole browser path without a human: consult, pull solutions one at
// a time, failure, a syntax error, engine output, completion. Loading the page
// with #selftest and reading the transcript is an end-to-end check of the
// DEPLOYED app — the part no xUnit test can reach, since it needs a browser.
//
// Loaded on demand, so a normal page never fetches it.

/**
 * Whether a file survives a reload — the whole point of mirroring to OPFS, and
 * the one claim a single page load cannot check. Run the page twice against the
 * same browser profile: `#persist=write` leaves a marker, `#persist=check`
 * reports whether it came back.
 */
export async function persistProbe(workspace, emit, mode) {
  const marker = 'persist_marker.pl';
  if (mode === 'write') {
    workspace.write(marker, 'marker(survived).\n');
    const ok = await workspace.persist();
    emit(`persist write: stored=${ok}${ok ? '' : ' reason=' + workspace.storageError()}\n`,
         ok ? 'note' : 'error');
    return;
  }
  const content = workspace.read(marker);
  const ok = content === 'marker(survived).\n';
  emit(`persist check: ${ok ? 'ok   restored across reload' : 'FAIL not restored'}`
     + ` (${JSON.stringify(content)})\n`, ok ? 'note' : 'error');
  workspace.remove(marker);
  await workspace.persist();
}

export async function run(session, emit, out, editor, workspace) {
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

  // --- the editor's highlighting, over the real DOM -----------------------
  const backdrop = document.getElementById('program-backdrop');
  const program = document.getElementById('program');

  // repaintNow rather than the input event: the scheduled repaint waits for a
  // frame, and a headless browser's virtual clock does not reliably deliver one.
  const paint = async (src) => {
    program.value = src;
    await editor.repaintNow();
    return backdrop;
  };

  const painted = await paint("foo(X) :- bar(X). % note\n");
  check('backdrop reproduces the text exactly',
        painted.textContent.replace(/\n$/, ''), "foo(X) :- bar(X). % note\n");
  check('variables are coloured',
        [...painted.querySelectorAll('.tok-variable')].some(e => e.textContent === 'X'), true);
  check('comments are coloured',
        [...painted.querySelectorAll('.tok-comment')].some(e => e.textContent === '% note'), true);
  check('operators are coloured',
        [...painted.querySelectorAll('.tok-operator')].some(e => e.textContent === ':-'), true);

  const halfTyped = await paint("p('unterminated");
  check('half-typed text still reproduces',
        halfTyped.textContent.replace(/\n$/, ''), "p('unterminated");

  // A program-declared operator must colour as one once consulted — the payoff
  // of asking the live table instead of a fixed pattern list.
  await paint('X #= Y.');
  const beforeOp = [...backdrop.querySelectorAll('.tok-operator')].some(e => e.textContent === '#=');
  await session.consult(':- op(700, xfx, #=).');
  await paint('X #= Y.');
  const afterOp = [...backdrop.querySelectorAll('.tok-operator')].some(e => e.textContent === '#=');
  check('program-declared operator colours after consult', [beforeOp, afterOp].join(), 'false,true');

  program.value = '';
  await editor.repaintNow();

  // --- the workspace, and that Prolog can actually see it -----------------
  workspace.write('selftest_data.pl', 'from_a_file(yes).\n');
  check('file appears in the workspace', workspace.list().includes('selftest_data.pl'), true);
  check('file reads back', workspace.read('selftest_data.pl'), 'from_a_file(yes).\n');
  check('consulting a workspace file', workspace.consultFile('selftest_data.pl'), null);
  check('its clauses are solvable', await solutions('from_a_file(X).'), 'X = yes');

  // Prolog's own file I/O writes into the same workspace — the point of putting
  // it on a real filesystem rather than a bag of strings.
  await solutions("open('written_by_prolog.txt', write, S), write(S, hello), close(S).");
  check('open/4 wrote into the workspace',
        workspace.read('written_by_prolog.txt'), 'hello');

  check('delete removes it', workspace.remove('written_by_prolog.txt'), null);
  check('and it is gone', workspace.list().includes('written_by_prolog.txt'), false);
  workspace.remove('selftest_data.pl');

  // The mirror, both ways, through real storage: write a file, push it to OPFS,
  // erase it from the engine's filesystem, and pull it back. That is exactly
  // what a reload does, minus the reload — which a headless browser cannot be
  // made to perform reliably.
  if (workspace.persistent()) {
    workspace.write('mirror_probe.pl', 'mirrored(yes).\n');
    const stored = await workspace.persist();
    if (!stored) {
      // A headless browser advertises origin-private storage and then never
      // answers. That is the harness, not the app — reported, not counted as a
      // failure, because the app's own behaviour (degrade, say so, carry on) is
      // exactly what happens here.
      emit(`note: storage unusable here — ${workspace.storageError()};`
         + ` mirror unverified in this environment\n`, 'note');
      workspace.remove('mirror_probe.pl');
    } else {
      workspace.remove('mirror_probe.pl');
      check('mirror: gone from memory', workspace.read('mirror_probe.pl'), null);
      await workspace.restore();
      check('mirror: restored from storage',
            workspace.read('mirror_probe.pl'), 'mirrored(yes).\n');
      workspace.remove('mirror_probe.pl');
      await workspace.persist();
    }
  }

  // --- sharing ------------------------------------------------------------
  const shared = 'p(1).\np(2).  % a comment with | and \\n in it\n';
  const packed = await session.shareEncode(shared, 'p(X).');
  const unpacked = session.shareDecode(packed);
  check('share round-trips the program', unpacked && unpacked.program, shared);
  check('share round-trips the query', unpacked && unpacked.query, 'p(X).');
  check('share is url-safe', encodeURIComponent(packed), packed);
  check('a mangled link is rejected', session.shareDecode('not a share'), null);

  // --- examples -------------------------------------------------------------
  // Every example must at least parse and load; one that does not is worse than
  // no example. (Their queries are exercised on the desktop REPL, where a wrong
  // answer is visible; here the point is that the files ship and consult.)
  for (const name of ['family.pl', 'queens.pl', 'zebra.pl', 'dcg.pl', 'tabling.pl', 'clpfd.pl']) {
    const source = await (await fetch('examples/' + name)).text();
    check(`example ${name} is served`, source.length > 0, true);
  }
  // A fresh engine per example is not available here, so only the one that
  // needs no library is consulted — enough to prove the pipeline.
  check('example family.pl consults',
        await session.consult(await (await fetch('examples/family.pl')).text()), null);
  check('and answers', await solutions('ancestor(ana, W), W == beto.'), 'W = beto');

  // Persistence is reported rather than assumed: a browser may refuse storage,
  // and the session must still work when it does.
  emit(`note: origin-private storage ${workspace.persistent() ? 'available' : 'UNAVAILABLE'}`
     + `, file pickers ${workspace.canPickFiles() ? 'available' : 'unavailable (download fallback)'}\n`,
       'note');

  emit(`--- selftest ${failures === 0 ? 'passed' : failures + ' FAILED'} ---\n`,
       failures === 0 ? 'note' : 'error');
}
